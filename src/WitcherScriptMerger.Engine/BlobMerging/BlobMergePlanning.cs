#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using WitcherScriptMerger.FileIndex;
using WitcherScriptMerger.Inventory;
using WitcherScriptMerger.LoadOrder;
using WitcherScriptMerger.Merging;

namespace WitcherScriptMerger.BlobMerging;

// Build the merge plan and reuse earlier plans or method comparisons when their inputs still match.
internal static class BlobMergePlanning
{
	internal static IReadOnlySet<string> CompiledFunctionPaths( MergePlan plan )
	{
		var paths = new HashSet<string>( StringComparer.Ordinal );

		foreach ( string symbol in plan.CompiledDefinitions.Where( symbol => symbol.StartsWith( "function:", StringComparison.Ordinal ) ) )
		{
			string name = symbol["function:".Length..];
			int dot = name.LastIndexOf( '.' );

			paths.Add( ScriptSymbolKey.Function( dot < 0 ? "" : name[..dot], name[( dot + 1 )..] ) );
		}

		return paths;
	}

	// Adding a required compiled mod can reveal more dependencies. Plan again until the selection stops growing.
	internal static (LoadedBlobMerge Loaded, MergePlan Plan) PrepareMinimalSelection(
		LoadedBlobMerge loaded,
		IEnumerable<ModFile> conflicts,
		Action<string, string>? progress = null,
		IReadOnlyDictionary<string, MergeChoice>? choices = null,
		IReadOnlyDictionary<string, PlannedFile>? fileOverrides = null )
	{
		ModFile[] compiledConflicts = conflicts.Where( file => file.Category == Categories.PrecompiledScript ).ToArray();
		HashSet<string> selected = loaded.Selected.ToHashSet( StringComparer.OrdinalIgnoreCase );

		while ( true )
		{
			MergePlan plan = CachedPlan( loaded, selected, choices, fileOverrides, progress );
			FileHash[] needed;

			if ( !plan.NeedsCompilation )
				return (loaded, plan);

			needed = WrappersAtOrAfterOutput( loaded, plan, compiledConflicts, selected );

			if ( needed.Length == 0 )
				return (loaded, plan);

			foreach ( FileHash source in needed )
			{
				if ( !BlobConflictSelection.IsConventionalSource( source ) )
					throw new InvalidOperationException( UiText.T( "BlobMerge", "Mod.io sources are read-only and cannot be merged." ) );

				progress?.Invoke( UiText.T( "BlobMerge", "Recovering compiled scripts" ), source.Name );

				MergeModule recovered = BlobMergeWorkspace.RecoverAdditional( loaded, source.Name );

				selected.Add( source.Name );
				loaded = loaded with
				{
					Selected = selected.ToHashSet( StringComparer.OrdinalIgnoreCase ),
					Modules = loaded.Modules
						.Select( module => module.Name.Equals( source.Name, StringComparison.OrdinalIgnoreCase ) ? recovered : module )
						.ToArray()
				};
			}
		}
	}

	private static FileHash[] WrappersAtOrAfterOutput(
		LoadedBlobMerge loaded,
		MergePlan plan,
		ModFile[] compiledConflicts,
		HashSet<string> selected )
	{
		IReadOnlySet<string> compiledFunctions = CompiledFunctionPaths( plan );
		(string Name, int? Priority) placement = CompiledOutputPlacement.ForMerge( loaded, plan );
		var outputSetting = new ModLoadSetting( placement.Name )
		{
			Priority = placement.Priority,
			IsEnabled = true
		};

		bool LoadsAtOrAfterOutput( FileHash source )
		{
			ModLoadSetting? sourceSetting = loaded.Settings.FirstOrDefault(
				setting => setting.ModName.Equals( source.Name, StringComparison.OrdinalIgnoreCase ) );

			return LoadOrderComparer.CompareRuntimeOrder( source.Name, sourceSetting, placement.Name, outputSetting ) >= 0;
		}

		return compiledConflicts
			.Where( file => compiledFunctions.Contains( file.RelativePath ) )
			.SelectMany( file => new PrecompiledConflictAnalysis( file.Mods, loaded.Settings ).EnabledSources )
			.Where( source => source.HasPrecompiledScriptDefinition
				&& ( source.PrecompiledScriptKinds & ScriptSymbolKind.WrappedFunction ) != 0
				&& !selected.Contains( source.Name )
				&& LoadsAtOrAfterOutput( source ) )
			.DistinctBy( source => source.Name, StringComparer.OrdinalIgnoreCase )
			.ToArray();
	}

	internal static MergePlan Plan( LoadedBlobMerge loaded, IReadOnlySet<string> selected,
		IReadOnlyDictionary<string, MergeChoice>? choices = null, IReadOnlyList<string>? wrapperOrder = null,
		IReadOnlyDictionary<string, PlannedFile>? fileOverrides = null, Action<string, string>? progress = null )
	{
		try
		{
			return CachedPlan( loaded, selected, choices, fileOverrides, progress, wrapperOrder );
		}
		catch ( EnumRenumberingException error )
		{
			throw new InvalidOperationException( InspectionFailureMessage( error ), error );
		}
	}

	// A custom wrapper order bypasses the saved-plan cache.
	private static MergePlan CachedPlan( LoadedBlobMerge loaded, IReadOnlySet<string> selected,
		IReadOnlyDictionary<string, MergeChoice>? choices, IReadOnlyDictionary<string, PlannedFile>? overrides,
		Action<string, string>? progress, IReadOnlyList<string>? wrapperOrder = null )
	{
		// Copies of the loaded inputs share this cache. Lock it while planning so parallel scan checks cannot change it together.
		lock ( loaded.PlanningCache )
		{
			bool created = false;

			if ( wrapperOrder is not null )
				return Create();

			var result = BlobMergePlanCache.Get( loaded, selected, choices, overrides, Create );

			if ( !created )
				progress?.Invoke( UiText.T( "BlobMerge", "Reusing prepared script plan" ), "" );

			return result;

			MergePlan Create()
			{
				created = true;
				progress?.Invoke( UiText.T( "BlobMerge", "Planning required script changes" ), "" );

				MergePlan plan = CreatePlan();

				if ( !BlobBodyComparisons.Prepare(
					loaded, plan, key => progress?.Invoke( UiText.T( "BlobMerge", "Verifying script comparisons" ), key ) ) )
					return plan;

				return CreatePlan();
			}

			MergePlan CreatePlan()
			{
				return MergePlanner.Create(
					loaded.Modules, selected, loaded.Vanilla, choices, wrapperOrder, loaded.PlanningCache.Comparisons, overrides );
			}
		}
	}

	internal static string InspectionFailureMessage( Exception error )
	{
		if ( error is EnumRenumberingException enumeration )
		{
			return UiText.Format(
				"EnumConflicts",
				"Combining these mods would change the numbers they use for {0}. Script Merger cannot safely make that change. Use compatible versions of these mods.",
				enumeration.EnumName );
		}

		return error.Message;
	}
}
