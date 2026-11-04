using System;
using System.Collections.Generic;
using System.Linq;
using WitcherScriptMerger.Inventory;
using WitcherScriptMerger.LoadOrder;
using WitcherScriptMerger.BlobMerging;

namespace WitcherScriptMerger.FileIndex;

// Turn the results from PrecompiledConflictAnalysis into explanations and suggested actions.
internal static class PrecompiledConflictReport
{
	private static readonly string ParagraphBreak = Environment.NewLine + Environment.NewLine;

	private sealed record SymbolConflict( ModFile File, PrecompiledConflictAnalysis Analysis );

	private sealed record BlockedGroupKey( string Name, bool MergeAvailable, string Unavailable );

	private sealed record BlockedGroup( string Body, bool MergeAvailable, bool Mixed, ModFile FirstFile );

	internal static bool HasReport( ModFile file )
	{
		if ( !ReferenceEquals( file.Category, Categories.PrecompiledScript ) || file.Mods.Count < 2 )
			return false;

		if ( file.IsScopedMember )
			return file.ScopedMemberConflict;

		var analysis = file.GetPrecompiledAnalysis();

		return analysis.BypassedLooseSources.Length > 0 || analysis.BlockedBlobWrappers.Length > 0
			|| analysis.EnabledSources.Count( source => source.HasPrecompiledPriorityConflictDefinition ) > 1;
	}

	internal static IReadOnlyList<string> Format( IEnumerable<ModFile> files )
	{
		ModFile[] visible = files.ToArray();
		List<string> reports = new();
		SymbolConflict[] symbols;

		foreach ( ModFile file in visible.Where( file => file.IsScopedMember && file.HasConflict ) )
			reports.Add( ScopedMemberReport( file, visible ) );

		symbols = visible
			.Where( file => ReferenceEquals( file.Category, Categories.PrecompiledScript ) && file.Mods.Count > 1 && !file.IsScopedMember )
			.Select( file => new SymbolConflict( file, file.GetPrecompiledAnalysis() ) )
			.ToArray();

		foreach ( IGrouping<BlockedGroupKey, SymbolConflict> group in BlockedGroups( symbols, visible ) )
			reports.Add( BlockedGroupReport( group ) );

		foreach ( SymbolConflict item in symbols.OrderBy( item => item.File.RelativePath, StringComparer.OrdinalIgnoreCase ) )
		{
			FileHash[] definitions = item.Analysis.EnabledSources.Where( source => source.HasPrecompiledPriorityConflictDefinition ).ToArray();

			if ( definitions.Length >= 2 )
				reports.Add( CompetingVersionsReport( item.File, definitions, visible ) );
		}

		return reports;
	}

	// Point to an enum conflict already shown in the report so its cause does not need to be repeated.
	private static string UnavailableGuidance( ModFile file, ModFile[] visible )
	{
		string enumeration = BlobConflictSelection.Readiness( file )?.BlockingEnum;
		bool enumConflictShown = enumeration is not null && visible.Any( other => other.RelativePath.Equals( "Enums\\" + enumeration, StringComparison.Ordinal )
			&& EnumConflictAnalysis.HasReport( other ) );

		return enumConflictShown
			? UiText.Format( "BlobMerge", "Merge unavailable—see the {0} enum conflict.", enumeration )
			: UnavailableReason( file );
	}

	private static string NameList( IEnumerable<string> names )
	{
		return string.Join( Environment.NewLine, OrderedNames( names ).Select( name => "    - " + name ) );
	}

	private static string ScopedMemberReport( ModFile file, ModFile[] visible )
	{
		bool defaults = file.RelativePath.StartsWith( "Defaults\\", StringComparison.OrdinalIgnoreCase );
		string remedy = BlobConflictSelection.CanMerge( file ) ? BlobMergePreflight.Guidance( file ) : UnavailableGuidance( file, visible );
		string title = defaults
			? UiText.Format( "ScopedMembers", "Competing script starting values — {0}", file.RelativePath )
			: UiText.Format( "ScopedMembers", "Competing script fields — {0}", file.RelativePath );
		string explanation = defaults
			? UiText.T(
				"ScopedMembers",
				"These mods set different starting values for the same field. Both values cannot be used together." )
			: UiText.T(
				"ScopedMembers",
				"These mods define the same field differently, or add it more than once. Independent fields can coexist; these definitions need resolving." );

		return string.Join( ParagraphBreak,
			title,
			explanation + Environment.NewLine + NameList( file.ScopedMemberSources().Select( source => source.Name ) ),
			remedy );
	}

	private static IEnumerable<IGrouping<BlockedGroupKey, SymbolConflict>> BlockedGroups( SymbolConflict[] symbols, ModFile[] visible )
	{
		return symbols
			.Where( item => item.Analysis.BypassedLooseSources.Length > 0 || item.Analysis.BlockedBlobWrappers.Length > 0 )
			.GroupBy( item => new BlockedGroupKey(
				BodyName( item.Analysis ).ToUpperInvariant(),
				BlobConflictSelection.CanMerge( item.File ),
				UnavailableGuidance( item.File, visible ) ) )
			.OrderBy( group => group.Key.Name, LoadOrderComparer.GameAlphabeticalComparer );
	}

	// Show merge guidance once when a report has both compiled and loose sections.
	private static string BlockedGroupReport( IGrouping<BlockedGroupKey, SymbolConflict> group )
	{
		SymbolConflict[] compiled = group.Where( item => item.Analysis.BlockedBlobWrappers.Length > 0 ).ToArray();
		SymbolConflict[] loose = group.Where( item => item.Analysis.BypassedLooseSources.Length > 0 ).ToArray();
		BlockedGroup details = new(
			BodyName( group.First().Analysis ),
			group.Key.MergeAvailable,
			compiled.Length > 0 && loose.Length > 0,
			group.First().File );
		List<string> sections = new() { UiText.Format( "PrecompiledConflicts", "Compiled script conflict — {0}", details.Body ) };

		if ( details.MergeAvailable && details.Mixed )
			sections.Add( BlobMergePreflight.Guidance( details.FirstFile ) );
		else if ( !details.MergeAvailable && group.Key.Unavailable.Length > 0 )
			sections.Add( group.Key.Unavailable );

		if ( compiled.Length > 0 )
			AddCompiledSections( sections, compiled, details );

		if ( loose.Length > 0 )
			sections.Add( LooseSection( loose, details ) );

		return string.Join( ParagraphBreak, sections );
	}

	// Priority can restore a wrapper only when it and the implementation belong to the same load-order group.
	private static void AddCompiledSections( List<string> sections, SymbolConflict[] compiled, BlockedGroup details )
	{
		bool modIoBody = compiled.Any( item => PrecompiledConflictAnalysis.IsModIoSource( item.Analysis.WinningBody ) );
		(ModFile File, FileHash[] Sources)[] movable = BlockedWrappers( compiled, source => PrecompiledConflictAnalysis.IsModIoSource( source ) == modIoBody );
		(ModFile File, FileHash[] Sources)[] fixedGroup = BlockedWrappers( compiled, source => PrecompiledConflictAnalysis.IsModIoSource( source ) != modIoBody );

		if ( movable.Length > 0 )
		{
			sections.Add( UiText.Format( "PrecompiledConflicts", "{0} prevents the following mods' compiled additions from running:", details.Body )
				+ Environment.NewLine + FunctionList( movable ) + ParagraphBreak + MovableRemedy( movable, modIoBody, details ) );
		}

		if ( fixedGroup.Length > 0 )
		{
			sections.Add( UiText.Format( "PrecompiledConflicts", "The mod.io implementation from {0} skips these conventional mods' compiled additions:", details.Body )
				+ Environment.NewLine + FunctionList( fixedGroup ) + ParagraphBreak
				+ UiText.Format(
					"PrecompiledConflicts",
					"Use a conventional installation of {0} to put these additions ahead of it.",
					details.Body ) );
		}
	}

	private static (ModFile File, FileHash[] Sources)[] BlockedWrappers( SymbolConflict[] compiled, Func<FileHash, bool> include )
	{
		return compiled
			.Select( item => (item.File, Sources: item.Analysis.BlockedBlobWrappers.Where( include ).ToArray()) )
			.Where( item => item.Sources.Length > 0 )
			.ToArray();
	}

	private static string MovableRemedy( (ModFile File, FileHash[] Sources)[] movable, bool modIoBody, BlockedGroup details )
	{
		string remedy;

		if ( details.MergeAvailable )
		{
			remedy = UiText.Format(
				"PrecompiledConflicts",
				"Alternatively, restore only the compiled additions from {0} by giving those mods higher priority than {1}: smaller Priority numbers, or earlier alphabetical folder names when neither has an assigned priority.",
				string.Join(
					", ",
					OrderedNames( movable.SelectMany( item => item.Sources ).Select( source => source.Name ) ) ),
				details.Body );
		}
		else if ( modIoBody )
		{
			remedy = UiText.Format(
				"PrecompiledConflicts",
				"To include these additions, move the listed mods ahead of {0} in the mod.io load order (smaller priority numbers).",
				details.Body );
		}
		else
		{
			remedy = UiText.Format(
				"PrecompiledConflicts",
				"To include these additions, give the listed mods higher priority than {0}: smaller Priority numbers, or folder names that sort earlier alphabetically when neither has an assigned priority.",
				details.Body );
		}

		if ( details.Mixed )
			remedy += " " + UiText.T( "PrecompiledConflicts", "This priority change does not restore loose-script changes." );

		if ( details.MergeAvailable && !details.Mixed )
			remedy = BlobMergePreflight.Guidance( details.FirstFile ) + " " + remedy;

		return remedy;
	}

	private static string LooseSection( SymbolConflict[] loose, BlockedGroup details )
	{
		string remedy = UiText.T( "PrecompiledConflicts", "Changing priority cannot restore these loose-script changes." );

		if ( !details.MergeAvailable && loose.Any( item => item.Analysis.BodySources.Any( source => PrecompiledConflictAnalysis.IsModIoSource( source ) ) ) )
			remedy += " " + UiText.T(
				"PrecompiledConflicts",
				"Use a compatible mod.io version, or switch to conventional copies before combining these changes." );

		if ( details.MergeAvailable && !details.Mixed )
			remedy = BlobMergePreflight.Guidance( details.FirstFile ) + " " + remedy;

		return UiText.Format(
			"PrecompiledConflicts",
			"{0}'s compiled version prevents these loose-script changes from running:",
			details.Body )
			+ Environment.NewLine + FunctionList( loose.Select( item => (item.File, item.Analysis.BypassedLooseSources) ) )
			+ ParagraphBreak + remedy;
	}

	// An enabled Mod.io version beats a conventional mod regardless of its numeric priority.
	private static string CompetingVersionsReport( ModFile file, FileHash[] definitions, ModFile[] visible )
	{
		bool hasModIo = definitions.Any( source => PrecompiledConflictAnalysis.IsModIoSource( source ) );
		bool hasConventional = definitions.Any( source => !PrecompiledConflictAnalysis.IsModIoSource( source ) );
		string guidance;

		if ( hasModIo && hasConventional )
			guidance = UiText.T(
				"PrecompiledConflicts",
				"An enabled mod.io version has priority over conventional versions. To select a conventional version, disable the competing mod.io versions. Combining the versions requires a compatibility patch." );
		else if ( hasModIo )
			guidance = UiText.T(
				"PrecompiledConflicts",
				"Priority selects one version. Move your preferred mod ahead of the others in the mod.io load order (a smaller priority number). Combining the versions requires a compatibility patch." );
		else
			guidance = UiText.T(
				"PrecompiledConflicts",
				"Priority selects one version. Give your preferred mod higher priority: a smaller Priority number, or an earlier alphabetical folder name when neither has an assigned priority." );

		if ( BlobConflictSelection.CanMerge( file ) )
		{
			guidance = BlobMergePreflight.Guidance( file ) + " "
				+ UiText.T(
					"PrecompiledConflicts",
					"Alternatively, choose one version by giving its mod higher priority: a smaller Priority number, or an earlier alphabetical folder name when neither has an assigned priority." );
		}
		else if ( UnavailableGuidance( file, visible ) is { Length: > 0 } reason )
		{
			guidance = reason + " " + guidance;
		}

		return string.Join( ParagraphBreak,
			UiText.Format( "PrecompiledConflicts", "Competing compiled versions — {0}", file.RelativePath ),
			UiText.T( "PrecompiledConflicts", "These mods supply competing compiled definitions:" )
				+ Environment.NewLine + NameList( definitions.Select( source => source.Name ) ),
			guidance );
	}

	private static string UnavailableReason( ModFile file ) => file.MergePreflight is not null && !BlobConflictSelection.CanMerge( file )
		? BlobMergePreflight.Guidance( file ) : string.Empty;

	private static string BodyName( PrecompiledConflictAnalysis analysis ) => string.Join(
		", ",
		OrderedNames( ( analysis.WinningBody == null
		? analysis.EnabledSources.Where( source => source.HasPrecompiledPriorityConflictDefinition )
		: new[] { analysis.WinningBody } ).Select( source => source.Name ) ) );

	private static string FunctionList( IEnumerable<(ModFile File, FileHash[] Sources)> items ) =>
		string.Join( Environment.NewLine, items.OrderBy( item => item.File.RelativePath, StringComparer.OrdinalIgnoreCase )
			.Select( item => "    - " + item.File.RelativePath + Environment.NewLine
				+ string.Join(
					Environment.NewLine,
					OrderedNames( item.Sources.Select( source => source.Name ) ).Select( name => "        - " + name ) ) ) );

	private static IEnumerable<string> OrderedNames( IEnumerable<string> names ) => names.Distinct( StringComparer.OrdinalIgnoreCase )
		.OrderBy(
			name => name.StartsWith( "mod", StringComparison.OrdinalIgnoreCase ) ? name[3..] : name,
			LoadOrderComparer.GameAlphabeticalComparer )
		.ThenBy( name => name, StringComparer.Ordinal );
}
