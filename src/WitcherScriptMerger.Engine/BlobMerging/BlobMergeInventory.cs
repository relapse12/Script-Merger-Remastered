#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using WitcherScriptMerger.Inventory;
using WitcherScriptMerger.Tools;
using WitcherScriptMerger.Merging;

namespace WitcherScriptMerger.BlobMerging;

// Update merge records together with the files applied or restored by BlobMergeTransaction.
internal static class BlobMergeInventory
{
	// Deleting all merges must also remove ordinary outputs that blob removal would otherwise restore.
	internal static HashSet<string> DeletionPaths( MergeInventory inventory, IEnumerable<Merge> selected, bool deleteAll )
	{
		var paths = selected.Where( merge => deleteAll || !merge.IsBlobMerge )
			.Select( BlobMergeRemoval.OutputPath ).ToHashSet( StringComparer.OrdinalIgnoreCase );

		if ( !deleteAll )
			return paths;

		var retained = inventory.Merges.Where( merge => !merge.IsBlobMerge ).Select( BlobMergeRemoval.OutputPath )
			.Except( paths, StringComparer.OrdinalIgnoreCase ).ToHashSet( StringComparer.OrdinalIgnoreCase );
		var restored = BlobMergeStore.RestoredOutputPaths( inventory.BlobMergeState );

		paths.UnionWith( restored.Where( path => !retained.Contains( path ) ) );

		return paths;
	}

	// Replacing a blob merge can release ordinary merges it previously used. Restore those records
	// and replace the records still covered by the new plan.
	internal static AppliedBlobMerge Apply(
		LoadedBlobMerge loaded,
		BlobMergeAdoption adoption,
		MergeBuildResult candidate,
		string state,
		string? workspace = null,
		IReadOnlyList<MergeInputFile>? workspaceGuards = null )
	{
		BlobMergeWorkspace.VerifySnapshot( loaded, runGuards: adoption.Guards.Concat( workspaceGuards ?? [] ) );

		if ( loaded.InventoryPath is null )
			throw new InvalidOperationException( "The normal merge inventory is required." );

		var inventory = MergeInventory.Deserialize( File.Exists( loaded.InventoryPath ) ? File.ReadAllBytes( loaded.InventoryPath ) : null );

		if ( loaded.Previous is not null )
			foreach ( var entry in inventory.Merges.Where( entry => entry.BlobMergeId == loaded.Previous.Manifest.Id ).ToArray() )
				inventory.Merges.Remove( entry );

		if ( loaded.Previous?.Inventory?.Previous is { } priorInventory )
		{
			var originalInventory = MergeInventory.Deserialize( priorInventory );

			foreach ( var released in loaded.Previous.RestoredOutputs ?? [] )
			{
				if ( candidate.Manifest.Outputs.Any( output => output.Path.Equals( released.Path, StringComparison.OrdinalIgnoreCase ) ) )
					continue;

				var original = originalInventory.Merges.FirstOrDefault( merge => ( merge.MergedModName + "/content/scripts/" + merge.RelativePath.Replace( '\\', '/' ) )
					.Equals( released.Path.Replace( '\\', '/' ), StringComparison.OrdinalIgnoreCase ) );

				if ( original is not null && !inventory.Merges.Any( merge => merge.MergedModName == original.MergedModName && merge.RelativePath == original.RelativePath ) )
					inventory.Merges.Add( original );
			}
		}

		foreach ( var adopted in adoption.Merges )
			foreach ( var entry in inventory.Merges.Where( entry => entry.MergedModName == adopted.MergedModName && entry.RelativePath == adopted.RelativePath ).ToArray() )
				inventory.Merges.Remove( entry );

		foreach ( var output in candidate.Manifest.Outputs )
		{
			string relative = output.Path.Replace( '\\', '/' );
			int separator = relative.IndexOf( '/' );
			string mod = relative[..separator], file = relative[( separator + 1 )..];

			file = file.StartsWith( "content/scripts/", StringComparison.OrdinalIgnoreCase ) ? file["content/scripts/".Length..]
				: file.StartsWith( "content/", StringComparison.OrdinalIgnoreCase ) ? file["content/".Length..] : file;
			file = file.Replace( '/', Path.DirectorySeparatorChar );

			if ( inventory.Merges.Any( merge => merge.RelativePath.Replace( '/', Path.DirectorySeparatorChar ).Equals( file, StringComparison.OrdinalIgnoreCase ) ) )
				throw new IOException( UiText.Format( "BlobMerge", "An existing merge already owns {0}. Remove that merge before rebuilding.", file ) );

			var entry = new Merge { RelativePath = file.Replace( '/', Path.DirectorySeparatorChar ), MergedModName = mod, BlobMergeId = candidate.Manifest.Id };

			foreach ( string name in output.Mods ?? candidate.Manifest.Mods )
			{
				string root = MergeBuilder.SafePath( loaded.ModsDirectory, name ) + Path.DirectorySeparatorChar;
				var source = loaded.Sources.SingleOrDefault( source => source.Name.Equals( name, StringComparison.OrdinalIgnoreCase ) );
				var owned = candidate.Manifest.Inputs.Where( input => input.Path.StartsWith( root, StringComparison.OrdinalIgnoreCase )
					&& ( input.Path.EndsWith( ".ws", StringComparison.OrdinalIgnoreCase ) || input.Path.EndsWith( ".rsblob", StringComparison.OrdinalIgnoreCase ) ) ).ToArray();
				string normal = Path.Combine( root, "content", "scripts", entry.RelativePath );
				var sources = source?.Blobs.Count > 0 ? owned.Where( input => input.Path.EndsWith( ".rsblob", StringComparison.OrdinalIgnoreCase ) )
					: owned.Any( input => input.Path.Equals( normal, StringComparison.OrdinalIgnoreCase ) )
						? owned.Where( input => input.Path.Equals( normal, StringComparison.OrdinalIgnoreCase ) ) : owned;

				foreach ( var input in sources )
					entry.Mods.Add( new FileHash
					{
						Name = name,
						Hash = Hasher.ComputeHash( loaded.ReadPath( input.Path ) ),
						SourceFilePath = input.SuppressedPath ?? input.Path
					} );
			}

			inventory.Merges.Add( entry );
		}

		string expected = loaded.Snapshot.Single( input => input.Path.Equals( loaded.InventoryPath, StringComparison.OrdinalIgnoreCase ) ).Sha256;
		var change = new MergeInventoryChange( loaded.InventoryPath, expected.Length == 0 ? null : expected, inventory.Serialize() );
		string previous = File.Exists( loaded.SettingsPath ) ? File.ReadAllText( loaded.SettingsPath ) : "";
		string baseline = previous;

		if ( loaded.Previous?.Manifest.Outputs.Any( output => output.Path.EndsWith( ".rsblob", StringComparison.OrdinalIgnoreCase ) ) == true )
			baseline = System.Text.RegularExpressions.Regex.Replace( baseline,
				@"(?ms)^[ \t]*\[" + System.Text.RegularExpressions.Regex.Escape( loaded.Previous.Manifest.CompiledMod ) + @"\][^\r\n]*(?:\r?\n|$).*?(?=^[ \t]*\[|\z)", "" );

		string next = CompiledOutputPlacement.SettingsFor( candidate.Manifest, baseline );

		if ( loaded.Previous is null && adoption.Merges.Count == 0 )
			return BlobMergeTransaction.Apply(
				candidate.Directory,
				loaded.ModsDirectory,
				state,
				loaded.SettingsPath,
				next == previous ? null : next,
				change,
				workspace );

		var replaceableOutputs = adoption.ReplaceableOutputs( loaded );
		var adoptions = adoption.Merges.Select( merge =>
		{
			string relative = Path.Combine(
				loaded.LooseModName,
				"content",
				"scripts",
				merge.RelativePath.Replace( '/', Path.DirectorySeparatorChar ) );

			return new MergeOutputFile( relative, replaceableOutputs[relative], merge.Mods.Select( source => source.Name ).ToArray() );
		} ).ToArray();
		// Treat the adopted ordinary outputs as the previous merge so Replace saves their contents for restoration.
		AppliedBlobMerge? initial = loaded.Previous is not null ? null : new( candidate.Manifest with
		{
			Id = Guid.NewGuid().ToString( "N" ),
			Outputs = adoptions,
			Mods = candidate.Manifest.Mods.Where( name => adoption.Merges.Any( merge =>
				merge.Mods.Any( source => source.Name.Equals( name, StringComparison.OrdinalIgnoreCase ) ) ) ).ToArray(),
			Inputs = candidate.Manifest.Inputs.Select( input => input with { SuppressedPath = null, ReadPath = null } ).ToArray()
		}, File.Exists( loaded.SettingsPath ) ? File.ReadAllBytes( loaded.SettingsPath ) : null, loaded.SettingsHash, loaded.SettingsPath,
			new( loaded.InventoryPath, File.ReadAllBytes( loaded.InventoryPath ), expected ), Array.Empty<string>() );

		return BlobMergeTransaction.Replace(
			candidate.Directory,
			loaded.ModsDirectory,
			state,
			loaded.SettingsPath,
			next == previous ? null : next,
				change,
			loaded.StateHash ?? "",
			replaceableOutputs,
			Reconcile,
			workspace,
			removedMods: loaded.RemovedMods,
				initial: initial,
			adoptedOutputs: loaded.Previous is null ? null : adoptions );
	}

	// When a blob merge is restored or rolled back, put its adopted ordinary merge records back too.
	internal static byte[]? Reconcile( byte[]? bytes, string id )
	{
		MergeInventory inventory;

		if ( bytes is null )
			return null;

		inventory = MergeInventory.Deserialize( bytes );

		foreach ( var merge in inventory.Merges.Where( merge => merge.BlobMergeId == id ).ToArray() )
			inventory.Merges.Remove( merge );

		if ( inventory.BlobMergeState is { } state && BlobMergeStore.ManifestId( state ) == id )
		{
			if ( BlobMergeStore.PreviousInventory( state ) is { Length: > 0 } previous )
			{
				var originals = MergeInventory.Deserialize( Convert.FromBase64String( previous ) );
				var paths = BlobMergeStore.RestoredOutputPaths( state ).ToHashSet( StringComparer.OrdinalIgnoreCase );

				foreach ( var original in originals.Merges.Where( merge => !merge.IsBlobMerge && paths.Contains( BlobMergeRemoval.OutputPath( merge ) ) ) )
				{
					if ( !inventory.Merges.Any( merge => BlobMergeRemoval.OutputPath( merge ).Equals( BlobMergeRemoval.OutputPath( original ), StringComparison.OrdinalIgnoreCase ) ) )
						inventory.Merges.Add( original );
				}
			}

			inventory.BlobMergeState = null;
		}

		return inventory.Serialize();
	}

	internal static bool IsRegistered( AppliedBlobMerge applied )
	{
		if ( applied.Inventory is null )
			return true;

		var inventory = MergeInventory.Deserialize( File.Exists( applied.Inventory.Path ) ? File.ReadAllBytes( applied.Inventory.Path ) : null );
		var entries = inventory.Merges.Where( merge => merge.BlobMergeId == applied.Manifest.Id ).ToArray();

		return entries.Length == applied.Manifest.Outputs.Count && applied.Manifest.Outputs.All( output => entries.Any( merge =>
			( merge.MergedModName + ( merge.Category == FileIndex.Categories.Script ? "/content/scripts/" : "/content/" )
				+ merge.RelativePath.Replace( '\\', '/' ) ).Equals( output.Path.Replace( '\\', '/' ), StringComparison.OrdinalIgnoreCase ) ) );
	}

	internal static void Restore(
		string modsDirectory,
		string settingsPath,
		string inventoryPath,
		IReadOnlySet<string>? removedMods = null )
	{
		var store = new BlobMergeStore( Path.GetFullPath( inventoryPath ) );

		if ( store.HasInterruptedUpdate )
			store.RecoverInterrupted( settingsPath, modsDirectory );

		if ( BlobMergeTransaction.HasState( store.InventoryPath ) )
			BlobMergeTransaction.Restore( store.InventoryPath, settingsPath, modsDirectory, Reconcile, inventoryPath, removedMods );

		RecoveredScriptStore.Prune( inventoryPath );
	}
}
