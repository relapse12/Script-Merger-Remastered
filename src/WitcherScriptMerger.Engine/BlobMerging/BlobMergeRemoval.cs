#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using WitcherScriptMerger.Inventory;
using WitcherScriptMerger.LoadOrder;
using WitcherScriptMerger.Merging;

namespace WitcherScriptMerger.BlobMerging;

// Removing part of a blob merge rebuilds it from the remaining sources. Removing all of it restores the originals.
// The saved recipe identifies sources that cannot be separated and ordinary merges that must be restored.
internal static class BlobMergeRemoval
{
	// Removal needs the saved mapping from outputs to sources. Refuse a missing or changed recipe.
	private static IReadOnlyList<SavedMergeFile> ReadFiles( string state, AppliedBlobMerge applied )
	{
		string recipe = Path.Combine( BlobMergeTransaction.WorkspacePath( state, applied.Manifest.Id ), "recipe.json" );
		var expected = applied.Workspace?.SingleOrDefault( file => file.Path == "recipe.json" );

		if ( expected is null || !File.Exists( recipe ) || MergeBuilder.Hash( recipe ) != expected.Sha256 )
			throw new IOException( UiText.T( "BlobMerge", "Saved merge ownership changed or is missing." ) );

		var files = ( JsonSerializer.Deserialize<BlobMergeHistory>( File.ReadAllText( recipe ) ) ?? throw new InvalidDataException() ).Files;

		if ( files.Any( file => file.Inputs is null || file.Inputs.Count == 0 ) )
			throw new IOException( UiText.T( "BlobMerge", "Saved merge ownership changed or is missing." ) );

		return files;
	}

	private static string Key( MergeSourceFile input ) => input.Mod + "|" + input.Path.Replace( '\\', '/' );

	// All compiled inputs share one blob, so they belong to the same removal group.
	private static MergeSourceFile[] OutputInputs( AppliedBlobMerge applied, IReadOnlyList<SavedMergeFile> files, string path )
	{
		if ( path.EndsWith( ".rsblob", StringComparison.OrdinalIgnoreCase ) )
			return files.Where( file => file.Compile ).SelectMany( file => file.Inputs ).ToArray();

		string prefix = applied.Manifest.LooseMod + "/content/scripts/";
		string relative = path.Replace( '\\', '/' );

		return files.Where( file => file.Path.Replace( '\\', '/' ).Equals( relative.StartsWith( prefix, StringComparison.OrdinalIgnoreCase )
			? relative[prefix.Length..] : relative, StringComparison.OrdinalIgnoreCase ) ).SelectMany( file => file.Inputs ).ToArray();
	}

	private static HashSet<string> Scope(
		AppliedBlobMerge applied,
		IReadOnlyList<SavedMergeFile> files,
		IEnumerable<string> mods,
		IEnumerable<string> outputs )
	{
		var affected = mods.ToHashSet( StringComparer.OrdinalIgnoreCase );

		return new OwnershipGraph( files ).Select( files.SelectMany( file => file.Inputs ).Where( input => affected.Contains( input.Mod ) )
			.Concat( outputs.SelectMany( path => OutputInputs( applied, files, path ) ) ) );
	}

	// Inputs that share an output must be rebuilt together. All compiled inputs share the same blob.
	private sealed class OwnershipGraph
	{
		private readonly Dictionary<string, string> parents = new( StringComparer.OrdinalIgnoreCase );
		private readonly Dictionary<string, HashSet<string>> components = new( StringComparer.OrdinalIgnoreCase );

		internal OwnershipGraph( IReadOnlyList<SavedMergeFile> files )
		{
			foreach ( string key in files.SelectMany( file => file.Inputs ).Select( Key ).Distinct( StringComparer.OrdinalIgnoreCase ) )
				parents[key] = key;

			foreach ( var boundary in files.Where( file => !file.Compile ).Select( file => file.Inputs )
				.Append( files.Where( file => file.Compile ).SelectMany( file => file.Inputs ).ToArray() ) )
			{
				var keys = boundary.Select( Key ).ToArray();

				foreach ( string key in keys.Skip( 1 ) )
					parents[Root( key )] = Root( keys[0] );
			}

			foreach ( string key in parents.Keys.ToArray() )
			{
				string root = Root( key );

				if ( !components.TryGetValue( root, out var values ) )
					components[root] = values = new( StringComparer.OrdinalIgnoreCase );

				values.Add( key );
			}
		}

		private string Root( string key )
		{
			string current = key;

			while ( !parents[current].Equals( current, StringComparison.OrdinalIgnoreCase ) )
				current = parents[current];

			while ( !key.Equals( current, StringComparison.OrdinalIgnoreCase ) )
			{
				string next = parents[key];

				parents[key] = current;
				key = next;
			}

			return current;
		}

		internal HashSet<string> Select( IEnumerable<MergeSourceFile> inputs ) => inputs.Select( Key ).Distinct( StringComparer.OrdinalIgnoreCase )
			.SelectMany( key => components[Root( key )] ).ToHashSet( StringComparer.OrdinalIgnoreCase );
	}

	internal static HashSet<string> Group( string state, AppliedBlobMerge applied, IEnumerable<string> mods, IEnumerable<string> outputs )
	{
		var files = ReadFiles( state, applied );
		var selected = Scope( applied, files, mods, outputs );

		return files.SelectMany( file => file.Inputs ).Where( input => selected.Contains( Key( input ) ) ).Select( input => input.Mod ).ToHashSet( StringComparer.OrdinalIgnoreCase );
	}

	internal static string OutputPath( Merge merge ) => merge.MergedModName
		+ ( merge.Category == FileIndex.Categories.Script ? "/content/scripts/" : "/content/" ) + merge.RelativePath.Replace( '\\', '/' );

	internal static IReadOnlyList<IReadOnlyList<Merge>> LinkedOutputs( string state, string modsDirectory, IEnumerable<Merge> entries )
	{
		var remaining = entries.Where( merge => merge.IsBlobMerge ).ToArray();

		if ( remaining.Length == 0 )
			return [];

		var applied = BlobMergeTransaction.ReadApplied( state, modsDirectory, state );
		var files = ReadFiles( state, applied );
		var graph = new OwnershipGraph( files );

		return remaining.GroupBy( merge =>
		{
			var scope = graph.Select( OutputInputs( applied, files, OutputPath( merge ) ) );

			if ( scope.Count == 0 )
				throw new InvalidDataException( UiText.T( "BlobMerge", "Saved merge ownership changed or is missing." ) );

			return scope.Order( StringComparer.OrdinalIgnoreCase ).First();
		}, StringComparer.OrdinalIgnoreCase ).Select( group => (IReadOnlyList<Merge>)group.ToArray() ).ToArray();
	}

	// If nothing remains to build, restore the original files. Otherwise rebuild the remaining sources.
	// Skip backups belonging to removed mods, and check that the game is closed before changing files.
	internal static void Run( string mods, string vanilla, string settingsPath, IReadOnlyList<ModLoadSetting> settings,
		IReadOnlyList<BlobMergeSource> sources, string inventory, string looseName, string state,
		IEnumerable<string> affectedMods, IEnumerable<string> affectedOutputs, IReadOnlySet<string>? removedMods = null, Func<bool>? gameRunning = null,
		Func<IReadOnlyList<BlobMergeSource>>? discoverSources = null, Action<string, string>? progress = null,
		IReadOnlyDictionary<string, string>? approvedEditedOutputs = null )
	{
		gameRunning ??= () => System.Diagnostics.Process.GetProcessesByName( "witcher3" ).Length > 0;

		if ( gameRunning() )
			throw new IOException( UiText.T( "BlobMerge", "Close The Witcher 3 before applying or restoring a merge." ) );

		progress?.Invoke( UiText.T( "BlobMerge", "Reading merge recovery information" ), "" );

		var applied = BlobMergeTransaction.ReadApplied( state, mods, inventory );
		var affected = affectedMods.Concat( removedMods ?? new HashSet<string>() ).ToHashSet( StringComparer.OrdinalIgnoreCase );
		var outputs = affectedOutputs.Select( path => path.Replace( '\\', '/' ) ).ToHashSet( StringComparer.OrdinalIgnoreCase );

		if ( applied.Manifest.Mods.All( affected.Contains ) || applied.Manifest.Outputs.All( output => outputs.Contains( output.Path.Replace( '\\', '/' ) ) ) )
		{
			Restore();

			return;
		}

		progress?.Invoke( UiText.T( "BlobMerge", "Finding linked merge outputs" ), "" );

		var files = ReadFiles( state, applied );
		var selected = Scope( applied, files, affected, outputs );
		var allInputs = files.SelectMany( file => file.Inputs ).Distinct().ToArray();

		if ( allInputs.All( input => selected.Contains( Key( input ) ) ) )
		{
			Restore();

			return;
		}

		var inputs = allInputs.Where( input => selected.Contains( Key( input ) ) ).ToArray();
		var excludedMods = inputs.Where( input => input.Path.Length == 0 ).Select( input => input.Mod ).ToHashSet( StringComparer.OrdinalIgnoreCase );

		excludedMods.UnionWith( affected );

		var excludedFiles = inputs.Where( input => input.Path.Length > 0 )
			.Select( input => MergeBuilder.SafePath( mods, input.Mod + "/content/scripts/" + input.Path ) ).ToHashSet( StringComparer.OrdinalIgnoreCase );
		var loaded = BlobMergeWorkspace.Load( mods, vanilla, settingsPath, discoverSources?.Invoke() ?? sources, settings,
			new HashSet<string>(), inventory, looseName, state, excludedMods, removedMods, progress: progress, excludedSourceFiles: excludedFiles );

		if ( gameRunning() )
			throw new IOException( UiText.T( "BlobMerge", "Close The Witcher 3 before applying or restoring a merge." ) );

		if ( loaded.Selected.Count == 0 )
		{
			Restore();

			return;
		}

		string work = new BlobMergeStore( state ).NewRemovalWorkDirectory();

		progress?.Invoke( UiText.T( "BlobMerge", "Rebuilding remaining script merges" ), "" );

		if ( BlobMergeWorkflow.Run(
			loaded,
			loaded.Selected,
			work,
			state,
			// Automatic removal cannot ask for KDiff3 decisions. Treat a required comparison as cancellation.
			( _, _, _, _, _ ) => 1,
			gameRunning: gameRunning,
			stages: progress ) is null )
			throw new IOException( UiText.T( "BlobMerge", "Other merge sources have changed. Refresh conflicts before removing this merge." ) );

		void Restore()
		{
			if ( gameRunning() )
				throw new IOException( UiText.T( "BlobMerge", "Close The Witcher 3 before applying or restoring a merge." ) );

			progress?.Invoke( UiText.T( "BlobMerge", "Restoring original mod files and previous merges" ), "" );
			BlobMergeTransaction.Restore( state, settingsPath, mods, BlobMergeInventory.Reconcile, inventory, removedMods, approvedEditedOutputs );
			progress?.Invoke( UiText.T( "BlobMerge", "Updating recovered-script records" ), "" );
			RecoveredScriptStore.Prune( inventory );
		}
	}
}
