#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WitcherScriptMerger.BlobMerging;
using WitcherScriptMerger.FileIndex;
using WitcherScriptMerger.LoadOrder;

namespace WitcherScriptMerger.Inventory;

internal sealed record MergeSelectionSource( string Name, bool IsSelected, string? FilePath, FileHash? Hash, ModFile? Conflict );

// Sources includes both selected and unselected rows, so linked-group decisions have the complete selection.
internal sealed record MergeSelection(
	string RelativePath,
	ModFile? Conflict,
	ModFileCategory? Category,
	bool HasOrdinaryMerges,
	string? VanillaPath,
	bool IsChecked,
	IReadOnlyList<ModFile>? LinkedConflicts,
	bool LinkedGroupReady,
	IReadOnlyList<MergeSelectionSource> Sources )
{
	internal IEnumerable<MergeSelectionSource> SelectedSources => Sources.Where( source => source.IsSelected );

	internal bool IsMergeable
	{
		get
		{
			if ( LinkedConflicts is not null )
				return IsChecked && LinkedGroupReady;

			if ( HasOrdinaryMerges )
				return SelectedSources.Count() > 1;

			return SelectedSources.Any( source => BlobConflictSelection.CanSelect( Conflict, source.Name ) )
				&& BlobConflictSelection.CanSelect( SelectedBlobConflict );
		}
	}

	// Recheck ordinary sources before merging. Blob merging performs its own check when loading inputs.
	internal string? FindUnavailableSource( CustomLoadOrder? order )
	{
		if ( !HasOrdinaryMerges )
			return null;

		return SelectedSources.FirstOrDefault( source => ( order?.IsValid == true && source.Hash?.IsDisabledBy( order.GetModLoadSettingByName( source.Name ) ) == true )
			|| ( source.FilePath is { Length: > 0 } path && !File.Exists( path ) ) )?.Name;
	}

	internal ModFile? SelectedBlobConflict => Conflict is { } file
		? BlobConflictSelection.Subset( file, SelectedSources.Select( source => source.Name ) )
		: null;

	internal IEnumerable<ModFile> SelectedBlobConflicts
	{
		get
		{
			if ( LinkedConflicts is not null )
				return IsChecked ? LinkedConflicts : [];

			return SelectedBlobConflict is { } file ? [file] : [];
		}
	}

	internal bool IsScriptCandidate( IEnumerable<Merge> merges )
	{
		return Conflict is { } file && file.Category == Categories.Script && !IsManagedScript( file, merges );
	}

	internal static bool IsManagedScript( ModFile? file, IEnumerable<Merge> merges )
	{
		string? script = file?.RelativePath.Replace( '\\', '/' );

		return file?.Category == Categories.Script
			&& merges.Any( merge => merge.IsBlobMerge
				&& merge.Category == Categories.Script
				&& merge.RelativePath.Replace( '\\', '/' ).Equals( script, StringComparison.OrdinalIgnoreCase ) );
	}

	internal static HashSet<string> ManagedScriptPaths( IEnumerable<Merge> merges )
	{
		return merges.Where( merge => merge.IsBlobMerge && merge.Category == Categories.Script )
			.Select( merge => merge.RelativePath.Replace( '\\', '/' ) ).ToHashSet( StringComparer.OrdinalIgnoreCase );
	}

	// Blob merging may have consumed these sources or taken over their output. Recheck before an ordinary merge runs.
	internal bool RemainsOrdinary( ISet<string> managedScripts, ISet<string> consumedInputs )
	{
		bool isManaged = ( Conflict?.Category ?? Category ) == Categories.Script
			&& managedScripts.Contains( ( Conflict?.RelativePath ?? RelativePath ).Replace( '\\', '/' ) );

		return LinkedConflicts is null
			&& Conflict?.Category != Categories.PrecompiledScript
			&& !isManaged
			&& !Sources.Any( source => consumedInputs.Contains( source.FilePath ?? "" ) );
	}
}
