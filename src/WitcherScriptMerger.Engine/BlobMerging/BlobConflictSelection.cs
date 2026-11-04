#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WitcherScriptMerger.FileIndex;
using WitcherScriptMerger.Inventory;

namespace WitcherScriptMerger.BlobMerging;

// The conflict list uses these rules to decide which sources can be selected.
// BlobMergePreflight checks whether the selected sources can actually be merged.
internal static class BlobConflictSelection
{
	internal static bool CanMerge( ModFile? file )
		=> IsConventionalConflict( file ) && Readiness( file )?.Available == true;

	internal static BlobMergeReadiness? Readiness( ModFile? file ) => file?.MergePreflight is { } scan
		? scan.GroupFor( file )?.Readiness ?? scan.Inspect( file ) : null;

	internal static bool CanSelect( ModFile? file, string? source = null )
		=> IsConventionalConflict( file ) && ( file!.MergePreflight?.GroupFor( file ) is { } group
			? group.Readiness.Includes( source ) && ( source is null || ActiveSources( file ).Any( item => item.Name.Equals( source, StringComparison.OrdinalIgnoreCase ) ) )
			: file.MergePreflight?.CanSelect( file, source ) == true );

	internal static bool IsUnchangedSource( ModFile? file, string? source )
		=> source is not null && IsConventionalConflict( file )
			&& ActiveSources( file! ).Any( item => item.Name.Equals( source, StringComparison.OrdinalIgnoreCase ) )
			&& Readiness( file ) is { Available: true, MergeInputs: not null } result
			&& !result.Includes( source );

	// Only ordinary Mods folders with content/precompiled.rsblob are supported by this merge path.
	internal static bool IsConventionalConflict( ModFile? file )
	{
#if RSBLOB_SEMANTIC_ANALYSIS
		if ( file is null || file.Category != Categories.PrecompiledScript || !file.HasConflict )
			return false;

		var sources = ActiveSources( file );

		return sources.Count >= 2 && sources.All( IsConventionalSource )
			&& sources.Any( source => source.HasPrecompiledScriptDefinition && source.SourceFilePath is { } path
				&& Path.GetFileName( path ).Equals( "precompiled.rsblob", StringComparison.OrdinalIgnoreCase )
				&& string.Equals( Path.GetFileName( Path.GetDirectoryName( path ) ), "content", StringComparison.OrdinalIgnoreCase ) );
#else
        return false;
#endif
	}

	internal static bool IsConventionalSource( FileHash source ) => !source.IsExternal && string.IsNullOrEmpty( source.Origin ) && source.ModIoId == 0;

	internal static IReadOnlyList<FileHash> ActiveSources( ModFile file )
	{
		var enabled = file.GetPrecompiledAnalysis().EnabledSources.Select( source => source.Name ).ToHashSet( StringComparer.OrdinalIgnoreCase );

		return file.GetConflictDisplaySources().Where( source => enabled.Contains( source.Name ) ).ToArray();
	}

	internal static HashSet<string> Sources( IEnumerable<ModFile> conflicts ) => conflicts.Where( IsConventionalConflict )
		.SelectMany( file => RequiredBlobSources( ActiveSources( file ) ) ).ToHashSet( StringComparer.OrdinalIgnoreCase );

	// If no source was marked as the definition, include all blobs so a required input is not missed.
	internal static IEnumerable<string> RequiredBlobSources( IEnumerable<FileHash> sources )
	{
		var blobs = sources.Where( source => source.HasPrecompiledScriptDefinition ).ToArray();
		var definitions = blobs.Where( source => source.HasPrecompiledPriorityConflictDefinition ).ToArray();

		return ( definitions.Length > 0 ? definitions : blobs ).Select( source => source.Name ).Distinct( StringComparer.OrdinalIgnoreCase );
	}

	// Selections must come from the same scan. Linked conflicts are checked together.
	internal static BlobMergeReadiness CheckSelection( IEnumerable<ModFile> conflicts )
	{
		var files = conflicts.Where( file => file?.Category == Categories.PrecompiledScript ).ToArray();

		if ( files.Length == 0 )
			return new( true );

		if ( files.Any( file => !IsConventionalConflict( file ) || file.MergePreflight is null )
			|| files.Any( file => !ReferenceEquals( file.MergePreflight, files[0].MergePreflight ) ) )
			return new( false, UiText.T( "BlobMerge", "The selected source was not checked. Refresh conflicts before merging." ) );

		var expanded = files.SelectMany( file => file.MergePreflight!.GroupFor( file )?.Conflicts ?? [file] ).Distinct().ToArray();

		return files[0].MergePreflight!.Inspect( Sources( expanded ) );
	}

	internal static ModFile Subset( ModFile file, IEnumerable<string> names )
	{
		var selected = names.ToHashSet( StringComparer.OrdinalIgnoreCase );
		var result = new ModFile( file.RelativePath, file.BundleName ) { IndexedCategoryOverride = file.Category, MergePreflight = file.MergePreflight, LoadOrderSource = file.LoadOrderSource };

		result.Mods.AddRange( file.Mods.Where( source => selected.Contains( source.Name ) || IsUnchangedSource( file, source.Name ) ) );

		return result;
	}
}
