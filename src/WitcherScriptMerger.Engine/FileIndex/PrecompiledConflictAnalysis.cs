using System;
using System.Collections.Generic;
using System.Linq;
using WitcherScriptMerger.Inventory;
using WitcherScriptMerger.LoadOrder;
using WSCompiler;

namespace WitcherScriptMerger.FileIndex;

// Find the implementation and wrappers the game can use, along with loose changes bypassed by compiled code.
// Evaluate this against the current load order.
internal sealed class PrecompiledConflictAnalysis
{
	internal FileHash[] Sources { get; }
	// Unknown sources may still be active. Keep them in conflict checks until their state is known.
	internal FileHash[] ConflictSources { get; }
	internal FileHash[] EnabledSources { get; }
	// Loose wrappers that do nothing, because a compiled implementation of the function exists.
	internal FileHash[] BlockedLooseWrappers { get; }
	// Compiled wrappers cut off because the winning implementation loads ahead of them.
	internal FileHash[] BlockedBlobWrappers { get; }
	// Compiled wrappers that load ahead of the winning implementation, so they still apply.
	internal FileHash[] CompatibleBlobWrappers { get; }
	// Loose sources whose change to this function is overridden by compiled code, whatever their priority.
	internal FileHash[] BypassedLooseSources { get; }
	internal FileHash[] BodySources { get; }
	// Null means the load order cannot identify a winner.
	internal FileHash WinningBody { get; }
	internal bool HasWrapperConflict => BlockedLooseWrappers.Length > 0 || BlockedBlobWrappers.Length > 0;
	internal bool HasMultipleCompiledSources => ConflictSources.Count( source => !CompatibleBlobWrappers.Contains( source )
		&& ( source.HasPrecompiledPriorityConflictDefinition || ( source.PrecompiledScriptKinds & ScriptSymbolKind.WrappedFunction ) != 0 ) ) > 1;

	// Prefer a mod's compiled definition when it also has loose source. Unknown settings must not select a winner or mark a mod disabled.
	internal PrecompiledConflictAnalysis( IEnumerable<FileHash> sources, IEnumerable<ModLoadSetting> settings, bool orderKnown = true )
	{
		ModLoadSetting[] order = settings?.Where( setting => setting != null ).ToArray() ?? Array.Empty<ModLoadSetting>();

		Sources = sources.GroupBy( source => source.Name, StringComparer.OrdinalIgnoreCase )
			.Select( group => group.FirstOrDefault( source => source.HasPrecompiledScriptDefinition )
				?? group.FirstOrDefault( source => source.HasLooseScriptDefinition ) )
			.Where( source => source != null ).ToArray();

		ModLoadSetting Setting( FileHash source ) => order.FirstOrDefault( setting => setting.ModName.EqualsIgnoreCase( source.Name ) );

		bool Enabled( FileHash source )
		{
			ModLoadSetting setting = Setting( source );

			if ( source.IsExternal && setting?.IsExternal != true )
				return false;

			return setting == null ? !source.IsExternal : setting.StateKnown && setting.IsEnabled == true;
		}

		bool Disabled( FileHash source ) => orderKnown && source.IsDisabledBy( Setting( source ) );

		EnabledSources = Sources.Where( Enabled ).ToArray();
		ConflictSources = Sources.Where( source => !Disabled( source ) ).ToArray();

		BodySources = Sources.Where( source => orderKnown && Enabled( source )
			&& source.PrecompiledScriptKinds.HasFunctionImplementation() ).ToArray();
		WinningBody = BodySources.FirstOrDefault( candidate => BodySources.All( other => ReferenceEquals( candidate, other )
			|| CompareKnownOrder( candidate, Setting( candidate ), other, Setting( other ) ) < 0 ) );
		BlockedLooseWrappers = BodySources.Length == 0 ? Array.Empty<FileHash>() : Sources.Where( source => Enabled( source )
			&& !source.HasPrecompiledScriptDefinition
			&& ( source.LooseScriptKinds & ScriptSymbolKind.WrappedFunction ) != 0 ).ToArray();
		BlockedBlobWrappers = WinningBody == null ? Array.Empty<FileHash>() : Sources.Where( source => Enabled( source )
			&& ( source.PrecompiledScriptKinds & ScriptSymbolKind.WrappedFunction ) != 0
			&& !ReferenceEquals( source, WinningBody )
			&& CompareKnownOrder( WinningBody, Setting( WinningBody ), source, Setting( source ) ) < 0 ).ToArray();
		CompatibleBlobWrappers = WinningBody == null ? Array.Empty<FileHash>() : Sources.Where( source => Enabled( source )
			&& !source.HasPrecompiledPriorityConflictDefinition
			&& ( source.PrecompiledScriptKinds & ScriptSymbolKind.WrappedFunction ) != 0
			&& CompareKnownOrder( source, Setting( source ), WinningBody, Setting( WinningBody ) ) < 0 ).ToArray();
		BypassedLooseSources = Sources.Where( source => Enabled( source ) && !source.HasPrecompiledScriptDefinition
			&& ( BlockedLooseWrappers.Contains( source ) || source.HasLoosePriorityConflictDefinition
				&& !source.IsLooseScriptVanillaEquivalent && EnabledSources.Any( blob => blob.HasPrecompiledPriorityConflictDefinition ) ) ).ToArray();
	}

	// Use null for an uncertain order. Only a negative comparison proves that the first source wins.
	internal static int? CompareKnownOrder( FileHash x, ModLoadSetting xSetting, FileHash y, ModLoadSetting ySetting )
	{
		return ModOrderFacts.ExecutionOrder( OrderSource( x, xSetting ), OrderSource( y, ySetting ) ).Relation switch
		{
			ModOrderRelation.FirstLoadsAhead => -1,
			ModOrderRelation.SecondLoadsAhead => 1,
			ModOrderRelation.SameMod => 0,
			_ => null
		};
	}

	private static ModOrderSource OrderSource( FileHash source, ModLoadSetting setting ) =>
		new( source.Name, IsModIoSource( source, setting ), setting?.Priority );

	internal static bool IsModIoSource( FileHash source, ModLoadSetting setting = null ) =>
		source?.IsExternal == true || LoadOrderComparer.IsModIo( source?.Name, setting );

	internal string Describe()
	{
		return HasWrapperConflict
			? UiText.T(
				"PrecompiledConflicts",
				"Some changes to this function do not run.\nSee Diagnostics for the affected mods and available solutions." )
			: string.Empty;
	}

	internal string DescribeSource( FileHash source )
	{
		if ( BypassedLooseSources.Contains( source ) )
			return UiText.T(
				"PrecompiledConflicts",
				"Loose script (.ws): changes to this function are bypassed.\nSee Diagnostics for details." );

		if ( source?.HasPrecompiledScriptDefinition == true )
		{
			ScriptSymbolKind kinds = source.PrecompiledScriptKinds;
			bool implementation = kinds.HasFunctionImplementation();
			bool replacement = ( kinds & ( ScriptSymbolKind.ClassReplacement | ScriptSymbolKind.GlobalReplacement ) ) != 0;
			bool wrapper = ( kinds & ScriptSymbolKind.WrappedFunction ) != 0;
			string role = (replacement, implementation, wrapper) switch
			{
				(true, _, true ) => UiText.T( "PrecompiledConflicts", "Compiled script (.rsblob): replaces and wraps this function." ),
				(true, _, false ) => UiText.T( "PrecompiledConflicts", "Compiled script (.rsblob): replaces this function." ),
				(_, true, true ) => UiText.T(
					"PrecompiledConflicts",
					"Compiled script (.rsblob): supplies an implementation and wraps this function." ),
				(_, true, false ) => UiText.T( "PrecompiledConflicts", "Compiled script (.rsblob): supplies this function's implementation." ),
				(_, _, true ) => UiText.T( "PrecompiledConflicts", "Compiled script (.rsblob): wraps this function." ),
				_ => null
			};

			if ( !CompatibleBlobWrappers.Contains( source ) && ( HasWrapperConflict || HasMultipleCompiledSources ) )
				return role == null ? UiText.T(
					"PrecompiledConflicts",
					"Compiled script (.rsblob).\nSee Diagnostics for conflicts involving this function." )
					: UiText.Format( "PrecompiledConflicts", "{0}\nSee Diagnostics for conflicts involving this function.", role );

			if ( role != null )
				return implementation && BypassedLooseSources.Length > 0
					? UiText.Format(
						"PrecompiledConflicts",
						"{0}\nIt overrides the overlapping loose-script changes, regardless of priority.",
						role ) : role;
		}

		return null;
	}
}
