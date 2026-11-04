using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using WitcherScriptMerger.FileIndex;
using WitcherScriptMerger.Tools;

namespace WitcherScriptMerger.Inventory;

// Merges ordinary text files through KDiff3 and repacks bundled outputs after the batch.
// Compiled-script merges run through BlobMergeWorkflow.
public class FileMerger
{
	public struct MergeSource
	{
		public FileInfo TextFile;

		public FileInfo Bundle;

		public FileHash Hash;

		public string Name;

		// Without a source record, derive the mod name from its location inside the game's Mods folder.
		public static MergeSource FromFlatFile( FileInfo file, FileHash hash, GamePaths game = null )
		{
			return Create( file, hash, isBundle: false, game );
		}

		public static MergeSource FromBundle( FileInfo file, FileHash hash, GamePaths game = null )
		{
			return Create( file, hash, isBundle: true, game );
		}

		private static MergeSource Create( FileInfo file, FileHash hash, bool isBundle, GamePaths game )
		{
			return new MergeSource
			{
				TextFile = ( isBundle ? null : file ),
				Bundle = ( isBundle ? file : null ),
				Hash = hash,
				Name = hash?.Name ?? ModFile.GetModNameFromPath( file.FullName,
					game ?? throw new InvalidOperationException( "A merge source without a record needs the game folders to find its mod." ) )
			};
		}
	}

	private readonly MergeInventory _inventory;

	private FileInfo _vanillaFile;

	private string _mergedModName;

	private string _outputPath;

	private bool _bundleChanged;

	private readonly List<Merge> _pendingBundleMerges = new List<Merge>();
	private readonly MergeInventory _pendingBundleInventory = new MergeInventory();
	private readonly Func<MergeSource, MergeSource, FileInfo, string, bool, int> _runTextMerge;
	private readonly Func<string, string, string, string> _unpackFile;
	private readonly Func<string, bool, string> _packBundle;

	private readonly GamePaths _game;

	private readonly IEngineHost _host;

	private readonly IMergePrompts _prompts;

	private readonly BackgroundWorker _bgWorker;

	public MergeProgressInfo ProgressInfo { get; private set; }

	internal FileMerger(
		MergeInventory inventory,
		GamePaths game,
		IEngineHost host,
		IMergePrompts prompts,
		ProgressChangedEventHandler progressHandler,
		RunWorkerCompletedEventHandler completedHandler )
		: this( inventory, game, host, prompts, progressHandler, completedHandler, null, null, null )
	{
	}

	internal FileMerger(
		MergeInventory inventory,
		GamePaths game,
		IEngineHost host,
		IMergePrompts prompts,
		ProgressChangedEventHandler progressHandler,
		RunWorkerCompletedEventHandler completedHandler,
		Func<MergeSource, MergeSource, FileInfo, string, bool, int> runTextMerge,
		Func<string, string, string, string> unpackFile,
		Func<string, bool, string> packBundle )
	{
		_inventory = inventory;
		_game = game;
		_host = host;
		_prompts = prompts;

		_runTextMerge = runTextMerge ?? host.RunTextMerge;
		_unpackFile = unpackFile ?? UnpackFile;
		_packBundle = packBundle ?? PackNewBundle;

		_bgWorker = new BackgroundWorker
		{
			WorkerReportsProgress = true
		};
		_bgWorker.ProgressChanged += progressHandler;

		ProgressInfo = new MergeProgressInfo();
		ProgressInfo.PropertyChanged += delegate
		{
			_bgWorker.ReportProgress( 0, ProgressInfo );
		};

		_bgWorker.RunWorkerCompleted += completedHandler;
	}

	~FileMerger()
	{
		_bgWorker?.Dispose();
	}

	// Capture selections before starting the worker; it must not read UI controls.
	internal void MergeAsync( IReadOnlyList<MergeSelection> selections, string mergedModName )
	{
		_bgWorker.DoWork += delegate
		{
			MergeSelections( selections, mergedModName );
		};

		_bgWorker.RunWorkerAsync();
	}

	private void MergeSelections( IReadOnlyList<MergeSelection> selections, string mergedModName )
	{
		MergeSelectionSource[][] selectedSources;

		_mergedModName = mergedModName;
		RefuseIncompleteBundle( selections );
		selectedSources = selections.Select( selection => selection.SelectedSources.ToArray() ).ToArray();

		ProgressInfo.TotalMergeCount = selectedSources.Sum( sources => sources.Length - 1 );
		ProgressInfo.TotalFileCount = selections.Count;

		for ( int num = 0; num < selections.Count; num++ )
			MergeOneSelection( selections[num], selectedSources[num], num + 1 );

		if ( _bundleChanged )
			PackBundleMerges();

		CleanUpTempFiles();
		CleanUpEmptyDirectories();
	}

	// Repacking replaces the whole bundle. Check for missing merge text before starting any work.
	private void RefuseIncompleteBundle( IReadOnlyList<MergeSelection> selections )
	{
		if ( !selections.Any( selection => selection.Category == Categories.BundleText ) )
			return;

		Merge[] replaced = _inventory.Merges
			.Where( merge => selections.Any( selection => selection.RelativePath.EqualsIgnoreCase( merge.RelativePath ) ) )
			.ToArray();

		RefuseIncompleteBundle( _prompts.RetrieveMergedBundlePath(), replaced );
	}

	private void RefuseIncompleteBundle( string bundlePath, IReadOnlyCollection<Merge> leaving )
	{
		IReadOnlyList<Merge> unstaged = bundlePath == null
			? Array.Empty<Merge>()
			: _inventory.FindUnstagedMerges( bundlePath, _game, leaving );

		if ( unstaged.Count > 0 )
			throw new InvalidOperationException( DescribeLostMerges( unstaged ) );
	}

	internal static string DescribeLostMerges( IReadOnlyList<Merge> unstaged )
	{
		return UiText.Format(
			"FileMerger",
			"A merged bundle was left as it is, because packing it again would lose these merges, which have no staged text:\n\n{0}\n\nDelete these merges and create them again, or delete every merge in the bundle together.",
			string.Join( Environment.NewLine, unstaged.Select( merge => merge.RelativePath ) ) );
	}

	// Every pack replaces the installed bundle, so all callers need the same completeness check.
	private string PackBundle( string bundlePath, bool isRepack )
	{
		RefuseIncompleteBundle( bundlePath, Array.Empty<Merge>() );

		return _packBundle( bundlePath, isRepack );
	}

	private void MergeOneSelection( MergeSelection selection, MergeSelectionSource[] sources, int fileNumber )
	{
		bool isNew = false;
		Merge merge;

		ProgressInfo.CurrentFileName = Path.GetFileName( selection.RelativePath );
		ProgressInfo.CurrentFileNum = fileNumber;
		ProgressInfo.CurrentAction = UiText.T( "FileMerger", "Starting merge" );

		merge = _inventory.Merges.FirstOrDefault( ( Merge m ) => m.RelativePath.EqualsIgnoreCase( selection.RelativePath ) );

		if ( merge?.IsBlobMerge == true )
			throw new InvalidOperationException( UiText.T( "BlobMerge", "This script belongs to a compiled merge. Refresh conflicts and use Create Selected Merges to update it." ) );

		if ( merge == null )
		{
			isNew = true;
			merge = new Merge
			{
				RelativePath = selection.RelativePath,
				MergedModName = _mergedModName
			};
		}

		if ( selection.Category == Categories.BundleText )
		{
			Merge pendingMerge = new Merge { RelativePath = merge.RelativePath, MergedModName = merge.MergedModName };

			pendingMerge.Mods.AddRange( merge.Mods.Select( mod => mod.Copy() ) );
			merge = pendingMerge;
			merge.BundleName = Path.GetFileName( _prompts.RetrieveMergedBundlePath() );
			MergeBundleSelection( selection, sources, merge );
		}
		else
		{
			MergeFlatSelection( selection, sources, merge, isNew );
		}
	}

	// Record bundled merges only after packing succeeds.
	private void PackBundleMerges()
	{
		string text = PackBundle( _prompts.RetrieveMergedBundlePath(), false );

		if ( text == null )
			return;

		ProgressInfo.CurrentAction = UiText.T( "FileMerger", "Adding bundle merge to inventory" );

		foreach ( Merge current in _pendingBundleMerges )
		{
			Merge existing = _inventory.Merges.FirstOrDefault( merge => merge.RelativePath.EqualsIgnoreCase( current.RelativePath ) );

			if ( existing == null )
				_inventory.Merges.Add( current );
			else
				_inventory.Merges[_inventory.Merges.IndexOf( existing )] = current;
		}

		if ( _host.Settings.Get<bool>( "ReportAfterPack" ) )
		{
			ProgressInfo.CurrentAction = UiText.T( "FileMerger", "Showing pack report" );
			_prompts.ShowPackReport( text );
		}
	}

	private void MergeFlatSelection( MergeSelection selection, MergeSelectionSource[] sources, Merge merge, bool isNew )
	{
		MergeSource source1 = MergeSource.FromFlatFile( new FileInfo( sources[0].FilePath ), sources[0].Hash, _game );

		string relPath = sources[0].Conflict.Category == Categories.Script
			? Path.Combine( GamePaths.ModScriptBase, selection.RelativePath )
			: selection.RelativePath;

		_outputPath = Path.Combine( _game.ModsDirectory, _mergedModName, relPath );

		if ( File.Exists( _outputPath ) && !ConfirmOutputOverwrite( _outputPath ) )
		{
			return;
		}

		_vanillaFile = new FileInfo( selection.VanillaPath );

		for ( int i = 1; i < sources.Length; i++ )
		{
			MergeProgressInfo progressInfo = ProgressInfo;
			int currentMergeNum = progressInfo.CurrentMergeNum + 1;

			progressInfo.CurrentMergeNum = currentMergeNum;

			MergeSource source2 = MergeSource.FromFlatFile( new FileInfo( sources[i].FilePath ), sources[i].Hash, _game );
			FileInfo mergedFile = MergeText( merge, source1, source2, isBundled: false );

			if ( mergedFile != null )
			{
				source1 = MergeSource.FromFlatFile( mergedFile, null, _game );
			}
			else if ( !ContinueAfterCanceledMerge( sources.Length - i - 1, merge ) )
			{
				break;
			}
		}

		if ( isNew && merge.Mods.Count > 1 )
		{
			ProgressInfo.CurrentAction = UiText.T( "FileMerger", "Adding script merge to inventory" );
			_inventory.Merges.Add( merge );
		}
	}

	private void MergeBundleSelection( MergeSelection selection, MergeSelectionSource[] sources, Merge merge )
	{
		_outputPath = Path.Combine( GamePaths.MergedBundleContent, selection.RelativePath );

		if ( File.Exists( _outputPath ) && !ConfirmOutputOverwrite( _outputPath ) )
		{
			return;
		}

		_vanillaFile = null;

		MergeSource source1 = MergeSource.FromBundle( new FileInfo( sources[0].FilePath ), sources[0].Hash, _game );

		for ( int i = 1; i < sources.Length; i++ )
		{
			MergeProgressInfo progressInfo = ProgressInfo;
			int currentMergeNum = progressInfo.CurrentMergeNum + 1;

			progressInfo.CurrentMergeNum = currentMergeNum;

			MergeSource source2 = MergeSource.FromBundle( new FileInfo( sources[i].FilePath ), sources[i].Hash, _game );

			if ( !GetUnpackedFiles( selection.RelativePath, ref source1, ref source2 ) )
			{
				if ( !ContinueAfterCanceledMerge( sources.Length - i - 1, merge ) )
				{
					break;
				}

				continue;
			}

			FileInfo mergedFile = MergeText( merge, source1, source2, isBundled: true );

			if ( mergedFile != null )
			{
				source1 = MergeSource.FromFlatFile( mergedFile, null, _game );
				_bundleChanged = true;

				if ( !_pendingBundleMerges.Contains( merge ) )
					_pendingBundleMerges.Add( merge );
			}
			else if ( !ContinueAfterCanceledMerge( sources.Length - i - 1, merge ) )
			{
				break;
			}
		}
	}

	private FileInfo MergeText( Merge merge, MergeSource source1, MergeSource source2, bool isBundled )
	{
		ProgressInfo.CurrentAction = UiText.F( "FileMerger", $"Merging {source1.Name} && {source2.Name} — waiting for KDiff3 to close" );

		if ( _runTextMerge( source1, source2, _vanillaFile, _outputPath, !isBundled ) == 0 )
		{
			MergeInventory inventory = isBundled ? _pendingBundleInventory : _inventory;

			if ( !source1.TextFile.FullName.EqualsIgnoreCase( _outputPath ) && !source1.TextFile.FullName.StartsWithIgnoreCase( _host.MergedBundleContentDirectory ) )
			{
				inventory.AddModToMerge( source1, merge );
			}

			if ( !source2.TextFile.FullName.EqualsIgnoreCase( _outputPath ) && !source2.TextFile.FullName.StartsWithIgnoreCase( _host.MergedBundleContentDirectory ) )
			{
				inventory.AddModToMerge( source2, merge );
			}

			if ( _host.Settings.Get<bool>( "ReportAfterMerge" ) )
			{
				ProgressInfo.CurrentAction = UiText.T( "FileMerger", "Showing merge report" );
				_prompts.ShowMergeReport(
					ProgressInfo.CurrentMergeNum,
					ProgressInfo.TotalMergeCount,
					source1.TextFile.FullName,
					source2.TextFile.FullName,
					_outputPath,
					source1.Name,
					source2.Name );
			}

			return new FileInfo( _outputPath );
		}

		return null;
	}

	private bool ConfirmOutputOverwrite( string outputPath )
	{
		return _host.Settings.Get<bool>( "AutoOverwriteOldMerges" ) || _prompts.ConfirmOverwrite( outputPath );
	}

	private bool ContinueAfterCanceledMerge( int remainingMergesForFile, Merge merge )
	{
		string msg = UiText.F( "FileMerger", $"Merge {ProgressInfo.CurrentMergeNum} of {ProgressInfo.TotalMergeCount} was canceled." );

		if ( remainingMergesForFile > 0 )
		{
			string fileName = Path.GetFileName( merge.RelativePath );

			msg += remainingMergesForFile == 1
				? UiText.F( "FileMerger", $"\n\nContinue with {remainingMergesForFile} remaining merge for file {fileName}?" )
				: UiText.F( "FileMerger", $"\n\nContinue with {remainingMergesForFile} remaining merges for file {fileName}?" );
		}

		if ( !_prompts.ConfirmContinueAfterCanceledMerge( msg, remainingMergesForFile > 0 ) )
		{
			ProgressInfo.CurrentMergeNum += remainingMergesForFile;

			return false;
		}

		return true;
	}

	private bool GetUnpackedFiles( string contentRelativePath, ref MergeSource source1, ref MergeSource source2 )
	{
		if ( _vanillaFile == null )
		{
			ProgressInfo.CurrentAction = UiText.T( "FileMerger", "Searching for corresponding vanilla bundle" );

			var groups = WitcherScriptMerger.GameContent.OfficialGameContent.DiscoverBundleGroups(
				_game.GameDirectory );

			for ( int i = groups.Count - 1; i >= 0; i-- )
			{
				var files = groups[i];

				foreach ( string bundle in files )
				{
					if ( RemasteredBundleFile.ListPaths( bundle ).Any( ( string path ) => path.EqualsIgnoreCase( contentRelativePath ) ) )
					{
						_vanillaFile = new FileInfo( bundle );

						break;
					}
				}

				if ( _vanillaFile != null )
				{
					break;
				}
			}

			if ( _vanillaFile != null )
			{
				ProgressInfo.CurrentAction = UiText.T( "FileMerger", "Unpacking vanilla bundle content file" );

				string vanillaContentPath = _unpackFile( _vanillaFile.FullName, contentRelativePath, "Vanilla" );

				_vanillaFile = new FileInfo( vanillaContentPath );
			}
		}

		if ( source1.TextFile == null )
		{
			ProgressInfo.CurrentAction = UiText.Format( "FileMerger", "Unpacking bundle content file for {0}", source1.Name );

			string modContentFile1 = _unpackFile( source1.Bundle.FullName, contentRelativePath, source1.Name );

			if ( modContentFile1 == null )
			{
				return false;
			}

			source1.TextFile = new FileInfo( modContentFile1 );
		}

		ProgressInfo.CurrentAction = UiText.Format( "FileMerger", "Unpacking bundle content file for {0}", source2.Name );

		string modContentFile2 = _unpackFile( source2.Bundle.FullName, contentRelativePath, source2.Name );

		if ( modContentFile2 == null )
		{
			return false;
		}

		source2.TextFile = new FileInfo( modContentFile2 );

		return true;
	}

	private string UnpackFile( string bundlePath, string contentRelativePath, string outputDirName )
	{
		string outputDir = Path.Combine( "tempbundlecontent", outputDirName );

		try
		{
			return RemasteredBundleFile.Extract( bundlePath, contentRelativePath, outputDir );
		}
		catch ( Exception exception )
		{
			_prompts.ShowUnpackError(
				UiText.Format( "FileMerger", "Error unpacking Remastered bundle content.\n\n{0}", exception.GetBaseException().Message ) );

			return null;
		}
	}

	public void RepackBundleAsync( string bundlePath )
	{
		if ( _bgWorker.IsBusy )
		{
			throw new Exception( UiText.T( "FileMerger", "BackgroundWorker can't run 2 tasks concurrently." ) );
		}

		_bgWorker.DoWork += delegate
		{
			if ( PackBundle( bundlePath, true ) != null )
			{
				if ( _host.Settings.Get<bool>( "ReportAfterPack" ) )
				{
					_prompts.ShowPackReport( bundlePath );
				}
			}
		};
		_bgWorker.RunWorkerAsync();
	}

	private string PackNewBundle( string bundlePath, bool isRepack = false )
	{
		ProgressInfo.CurrentPhase = ( ( !isRepack ) ? UiText.T( "FileMerger", "Packing Bundle" ) : UiText.T( "FileMerger", "Repacking Bundle" ) );
		ProgressInfo.CurrentAction = UiText.T( "FileMerger", "Packing merged bundle content into new blob0.bundle" );

		string outputDir = Path.GetDirectoryName( bundlePath );

		if ( _host.PackBundle( _host.MergedBundleContentDirectory, outputDir ) != 0 )
		{
			return null;
		}

		ProgressInfo.CurrentAction = UiText.T( "FileMerger", "Generating metadata.store for new blob0.bundle" );

		if ( _host.GenerateMetadata( outputDir ) == 0 )
		{
			return bundlePath;
		}

		return null;
	}

	// The merge has finished, so a cleanup failure should warn without reporting that the merge failed.
	private void CleanUpTempFiles()
	{
		if ( !Directory.Exists( "tempbundlecontent" ) )
		{
			return;
		}

		try
		{
			ProgressInfo.CurrentAction = UiText.T( "FileMerger", "Deleting temporary unpacked bundle content" );
			DeleteDirectory( "tempbundlecontent" );
		}
		catch ( Exception ex )
		{
			_prompts.ShowCleanupWarning( UiText.Format( "FileMerger", "Non-critical error: Failed to delete temporary unpacked bundle content.\n\n{0}", ex.Message ) );
		}
	}

	private void CleanUpEmptyDirectories()
	{
		if ( !Directory.Exists( GamePaths.MergedBundleContent ) )
		{
			return;
		}

		try
		{
			ProgressInfo.CurrentAction = UiText.T( "FileMerger", "Deleting empty Merged Bundle Content directories" );
			DeleteEmptyDirectories( GamePaths.MergedBundleContent );
		}
		catch ( Exception ex )
		{
			_prompts.ShowCleanupWarning( UiText.Format( "FileMerger", "Non-critical error: Failed to delete empty Merged Bundle Content directories.\n\n{0}", ex.Message ) );
		}
	}

	// Retries folder deletion while recently exited tools may still hold files open.
	private void DeleteDirectory( string path )
	{
		string[] directories = Directory.GetDirectories( path );

		foreach ( string subdirPath in directories )
		{
			Thread.Sleep( 1 );
			DeleteDirectory( subdirPath );
		}

		try
		{
			Thread.Sleep( 1 );
			Directory.Delete( path, recursive: true );
		}
		catch ( IOException )
		{
			Thread.Sleep( 1 );
			Directory.Delete( path, recursive: true );
		}
		catch ( UnauthorizedAccessException )
		{
			Thread.Sleep( 1 );
			Directory.Delete( path, recursive: true );
		}
		catch ( Exception )
		{
			throw;
		}
	}

	// Remove children first so their parents can become empty too.
	private void DeleteEmptyDirectories( string rootPath )
	{
		string[] directories = Directory.GetDirectories( rootPath );

		foreach ( string directory in directories )
		{
			Thread.Sleep( 1 );
			DeleteEmptyDirectories( directory );
		}

		if ( Directory.GetFiles( rootPath ).Any() || Directory.GetDirectories( rootPath ).Any() )
		{
			return;
		}

		try
		{
			Thread.Sleep( 1 );
			DeleteDirectory( rootPath );
		}
		catch ( Exception )
		{
			throw;
		}
	}
}
