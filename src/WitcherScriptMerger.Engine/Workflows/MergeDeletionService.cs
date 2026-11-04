#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using WitcherScriptMerger.BlobMerging;
using WitcherScriptMerger.FileIndex;
using WitcherScriptMerger.Inventory;
using WitcherScriptMerger.LoadOrder;
using WitcherScriptMerger.Merging;

namespace WitcherScriptMerger;

// Coordinates deletion, restoration and repacking. Entry points run on the UI thread; the host runs background work.
internal sealed class MergeDeletionService
{
	private readonly MergeSession session;
	private readonly IMergeWorkflowHost host;

	internal MergeDeletionService( MergeSession session, IMergeWorkflowHost host )
	{
		this.session = session;
		this.host = host;
	}

	// Remove blob merges first because they can restore earlier ordinary merges. Delete All must include those restored merges.
	internal async Task DeleteAsync( IReadOnlyList<Merge> selected, bool deleteAll )
	{
		List<Merge> merges = selected.ToList();
		bool managed = merges.Any( merge => merge.IsBlobMerge );
		string mergedModDirectory;

		if ( managed )
		{
			HashSet<string> paths = BlobMergeInventory.DeletionPaths( session.Inventory!, merges, deleteAll );
			Action? removal = PrepareBlobRemoval( merges.Where( merge => merge.IsBlobMerge ) );

			if ( removal is null || !await host.RunBlobRemovalAsync( removal ) )
				return;

			merges = session.Inventory!.Merges
				.Where( merge => !merge.IsBlobMerge && paths.Contains( BlobMergeRemoval.OutputPath( merge ) ) )
				.ToList();
		}

		Delete( merges );

		if ( managed && merges.Count == 0 )
			host.RefreshTrees();

		if ( session.Inventory!.Merges.Count != 0 )
			return;

		mergedModDirectory = Path.Combine( host.Settings.Get( "GameDirectory" ), "Mods", host.RetrieveMergedModName()! );

		if ( Directory.Exists( mergedModDirectory ) && !Directory.EnumerateFileSystemEntries( mergedModDirectory ).Any() )
			Directory.Delete( mergedModDirectory );
	}

	internal Action? PrepareBlobRemoval(
		IEnumerable<Merge> entries,
		BlobRemovalScope? scope = null,
		bool confirm = true,
		Func<bool>? gameRunning = null )
	{
		string configured;
		ModLoadSetting[] settings;
		string? mergedName;

		if ( !session.HasManagedBlobState() )
		{
			host.ShowError( UiText.T( "BlobMerge", "The compiled merge recovery information is missing from MergeInventory.xml. Restore the complete inventory before removing this merge." ) );

			return null;
		}

		if ( confirm && !host.ConfirmRemoveSelectedMerges() )
			return null;

		configured = host.Settings.Get( "ModIoDirectory" );
		settings = session.LoadOrder!.Mods.ToArray();
		mergedName = host.RetrieveMergedModName();

		if ( mergedName is null )
			return null;

		string mods = session.GamePaths.ModsDirectory;
		string vanilla = session.GamePaths.ScriptsDirectory;
		string order = session.LoadOrder.FilePath;
		string? modIoDirectory = string.IsNullOrWhiteSpace( configured ) ? null : configured;

		string inventory = BlobMergeStore.ForApplication().InventoryPath;
		string[] affectedMods = scope?.Mods.ToArray() ?? Array.Empty<string>();
		string[] affectedOutputs = scope?.Outputs.ToArray() ?? entries.Select( BlobMergeRemoval.OutputPath ).ToArray();
		Action<string, string> report = host.CreateProgressReporter();

		// Capture these values before dispatching work so the worker does not read UI controls or mutable session state.
		return () =>
		{
			try
			{
				Remove( null );
			}
			catch ( EditedMergeOutputsException error )
			{
				ApproveEditedOutputs( error.Outputs, mods, inventory );
				Remove( error.Outputs );
			}
		};

		void Remove( IReadOnlyDictionary<string, string>? approvedEditedOutputs ) => BlobMergeRemoval.Run(
			mods,
			vanilla,
			order,
			settings,
			Array.Empty<BlobMergeSource>(),
			inventory,
			mergedName,
			inventory,
			affectedMods,
			affectedOutputs,
			scope?.RemovedMods,
			gameRunning: gameRunning,
			discoverSources: () => BlobMergeWorkspace.Discover(
				mods,
				settings,
				new ModIoDiscovery( modIoDirectory, ModIoDiscovery.ConfigPath( host.Settings ) ).Discover(),
				mergedName ),
			progress: report,
			approvedEditedOutputs: approvedEditedOutputs );
	}

	// Approval applies only to the file contents shown to the user. The transaction checks them again before restoring.
	private void ApproveEditedOutputs(
		IReadOnlyDictionary<string, string> outputs, string mods, string inventory )
	{
		string backupDirectory = Path.Combine(
			Path.GetDirectoryName( inventory )!, "Data", "Edited Merge Backups",
			DateTime.Now.ToString( "yyyyMMdd-HHmmss-fff" ) );
		var paths = outputs.Keys.Select( path => Path.GetRelativePath( mods, path ) ).ToArray();
		EditedMergeOutputChoice choice = host.ConfirmEditedMergeOutputs( paths, backupDirectory );

		if ( choice == EditedMergeOutputChoice.Cancel )
			throw new OperationCanceledException( UiText.T( "BlobMerge", "Merge removal was canceled." ) );

		if ( choice != EditedMergeOutputChoice.BackUpAndDelete )
			return;

		foreach ( var output in outputs )
		{
			byte[] content = File.ReadAllBytes( output.Key );

			if ( Convert.ToHexString( SHA256.HashData( content ) ) != output.Value )
				throw new IOException( UiText.Format( "BlobMerge", "The edited file changed after confirmation. Retry removing the merge: {0}", output.Key ) );

			string target = MergeBuilder.SafePath( backupDirectory, Path.GetRelativePath( mods, output.Key ) );
			Directory.CreateDirectory( Path.GetDirectoryName( target )! );

			using ( var backup = new FileStream( target, FileMode.CreateNew, FileAccess.Write, FileShare.None ) )
				backup.Write( content );

			if ( MergeBuilder.Hash( target ) != output.Value )
				throw new IOException( UiText.Format( "BlobMerge", "The backup could not be verified. The merge was kept: {0}", target ) );
		}
	}

	// A partial bundle deletion requires repacking the remaining merges. Keep the records if their text is unavailable.
	internal bool Delete( List<Merge> merges )
	{
		var bundleMerges = new List<Merge>();
		BlockedBundleRemovals blocked = session.Inventory!.FindBlockedBundleRemovals( merges, session.GamePaths );
		string stopPath;

		if ( blocked.Kept.Count > 0 )
		{
			merges = merges.Except( blocked.Kept ).ToList();
			host.ShowError( FileMerger.DescribeLostMerges( blocked.Unstaged ) );
		}

		if ( merges.Count == 0 )
			return false;

		stopPath = Path.Combine( session.GamePaths.ScriptsDirectory.Split( Path.DirectorySeparatorChar )
			.Where( part => !part.EqualsIgnoreCase( "content0" ) ).ToArray() );

		foreach ( Merge merge in merges )
		{
			string mergePath = merge.GetMergedFile( session.GamePaths );

			if ( File.Exists( mergePath ) )
			{
				File.Delete( mergePath );
				DeleteEmptyDirectories( Path.GetDirectoryName( mergePath )!, stopPath );
			}

			if ( merge.IsBundleContent )
			{
				if ( IsLastMergeUsingBundle( merge, merges ) )
					DeleteBundle( merge, stopPath );
				else
					bundleMerges.Add( merge );
			}

			session.Inventory!.Merges.Remove( merge );
		}

		if ( !session.Inventory!.HasChanged )
			return false;

		session.SaveInventory();

		if ( bundleMerges.Count > 0 )
		{
			foreach ( string bundlePath in bundleMerges.Select( merge => merge.GetMergedBundle( session.GamePaths ) ).Distinct() )
				host.RepackBundle( bundlePath );

			return true;
		}

		if ( session.Scan != null )
			host.RefreshTrees();

		return false;
	}

	private bool IsLastMergeUsingBundle( Merge merge, List<Merge> deleting )
	{
		return session.Inventory!.Merges
			.Where( other => other.IsBundleContent
				&& other.MergedModName.EqualsIgnoreCase( merge.MergedModName )
				&& other.BundleName.EqualsIgnoreCase( merge.BundleName ) )
			.All( deleting.Contains );
	}

	private void DeleteBundle( Merge merge, string stopPath )
	{
		string bundlePath = merge.GetMergedBundle( session.GamePaths );
		string metadataPath = Path.Combine( Path.GetDirectoryName( bundlePath )!, "metadata.store" );

		if ( File.Exists( bundlePath ) )
			File.Delete( bundlePath );

		if ( File.Exists( metadataPath ) )
			File.Delete( metadataPath );

		DeleteEmptyDirectories( Path.GetDirectoryName( bundlePath )!, stopPath );
	}

	private static void DeleteEmptyDirectories( string directory, string stopPath )
	{
		DirectoryInfo info;

		if ( directory.EqualsIgnoreCase( stopPath ) )
			return;

		info = new DirectoryInfo( directory );

		if ( !info.Exists || info.GetFiles().Length != 0 || info.GetDirectories().Length != 0 )
			return;

		Directory.Delete( directory );
		DeleteEmptyDirectories( info.Parent!.FullName, stopPath );
	}
}
