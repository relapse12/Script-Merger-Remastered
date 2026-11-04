#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WitcherScriptMerger.FileIndex;
using WitcherScriptMerger.Inventory;
using WitcherScriptMerger.Merging;

namespace WitcherScriptMerger.BlobMerging;

// A blob merge can build on an existing ordinary merge. Keep that merge's files and records so
// removing the blob merge can restore it. Preparation returns a new value so cancellation leaves earlier state intact.
internal sealed record BlobMergeAdoption(
	IReadOnlyList<Merge> Merges,
	IReadOnlyDictionary<string, string> OutputHashes,
	IReadOnlyList<MergeInputFile> Guards )
{
	internal static BlobMergeAdoption None { get; } = new(
		Array.Empty<Merge>(),
		new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase ),
		Array.Empty<MergeInputFile>() );

	// An unrecorded merged file has no saved ownership information. Refuse to replace it to avoid losing its edits.
	internal static BlobMergeAdoption Prepare( LoadedBlobMerge loaded, MergePlan plan, BlobMergeAdoption earlier )
	{
		string[] paths = ( plan.SourceFiles ?? [] )
			.Where( file => file.Mod.Equals( loaded.LooseModName, StringComparison.OrdinalIgnoreCase ) )
			.Select( file => file.Path ).ToArray();

		MergeInventory inventory;
		List<Merge> merges;
		Dictionary<string, string> hashes;
		List<MergeInputFile> guards;

		if ( paths.Length == 0 )
			return earlier;

		inventory = MergeInventory.Deserialize( loaded.InventoryPath is not null && File.Exists( loaded.InventoryPath )
			? File.ReadAllBytes( loaded.InventoryPath ) : null );

		merges = earlier.Merges.ToList();
		hashes = new Dictionary<string, string>( earlier.OutputHashes, StringComparer.OrdinalIgnoreCase );
		guards = earlier.Guards.ToList();

		foreach ( string path in paths )
		{
			string relative;
			string physical;
			string hash;
			Merge? entry;

			if ( loaded.RetainedAdoptions.Any( merge => IsMergeOf( merge, path ) ) || merges.Any( merge => IsMergeOf( merge, path ) ) )
				continue;

			entry = inventory.Merges.SingleOrDefault( merge => !merge.IsBlobMerge && merge.Category == Categories.Script
				&& merge.MergedModName.Equals( loaded.LooseModName, StringComparison.OrdinalIgnoreCase ) && IsMergeOf( merge, path ) );

			if ( entry is null )
				throw new IOException( UiText.Format( "BlobMerge", "An existing merge already owns {0}. Remove that merge before rebuilding.", path ) );

			relative = Path.Combine( loaded.LooseModName, "content", "scripts", path.Replace( '/', Path.DirectorySeparatorChar ) );
			physical = MergeBuilder.SafePath( loaded.ModsDirectory, relative );
			hash = MergeBuilder.Hash( physical );

			merges.Add( entry );
			hashes[relative] = hash;
			guards.Add( new MergeInputFile( physical, hash ) );
		}

		return new BlobMergeAdoption( merges, hashes, guards );
	}

	private static bool IsMergeOf( Merge merge, string path )
	{
		return merge.RelativePath.Replace( '\\', '/' ).Equals( path.Replace( '\\', '/' ), StringComparison.OrdinalIgnoreCase );
	}

	// Record the original contributing mods as sources. The merged folder is only where their combined file was read.
	internal MergePlan OutputPlan( LoadedBlobMerge loaded, MergePlan plan )
	{
		return plan with
		{
			IncludedMods = plan.IncludedMods
				.Where( mod => !mod.Equals( loaded.LooseModName, StringComparison.OrdinalIgnoreCase ) )
				.ToArray(),
			Files = plan.Files.Select( file => file with
			{
				Owners = file.Owners.SelectMany( owner => owner.Equals( loaded.LooseModName, StringComparison.OrdinalIgnoreCase )
					? ForFile( loaded, file ).SelectMany( merge => merge.Mods.Select( source => source.Name ) )
					: new[] { owner } ).Distinct( StringComparer.OrdinalIgnoreCase ).ToArray()
			} ).ToArray()
		};
	}

	internal IReadOnlyList<Merge> ForFile( LoadedBlobMerge loaded, PlannedFile file )
	{
		HashSet<string> keys = file.Items.Select( MergePlanner.Key ).ToHashSet( StringComparer.Ordinal );

		HashSet<string> paths = loaded.Modules.Where( module => module.Name.Equals( loaded.LooseModName, StringComparison.OrdinalIgnoreCase ) )
			.SelectMany( module => module.Files )
			.Where( source => source.Path.Replace( '\\', '/' ).Equals( file.Path.Replace( '\\', '/' ), StringComparison.OrdinalIgnoreCase )
				|| source.Items.Any( item => keys.Contains( MergePlanner.Key( item ) ) ) )
			.Select( source => source.Path.Replace( '\\', '/' ) ).ToHashSet( StringComparer.OrdinalIgnoreCase );

		return Merges.Concat( loaded.RetainedAdoptions )
			.Where( merge => paths.Contains( merge.RelativePath.Replace( '\\', '/' ) ) )
			.ToArray();
	}

	// Check these hashes before replacing any output, including ordinary merges brought into this blob merge.
	internal IReadOnlyDictionary<string, string> ReplaceableOutputs( LoadedBlobMerge loaded )
	{
		var outputs = new Dictionary<string, string>( loaded.PreviousOutputs, StringComparer.OrdinalIgnoreCase );

		foreach ( KeyValuePair<string, string> output in OutputHashes )
			outputs[output.Key] = output.Value;

		return outputs;
	}
}
