#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using WitcherScriptMerger.Merging;

namespace WitcherScriptMerger.BlobMerging;

internal sealed record SavedBodyComparison( string Key, string Mod, MergeBodyComparison Value );
// Keep observations of unselected sources too: a later edit could make one relevant to this plan.
internal sealed record SavedExecutionPlan( MergePlan Plan, SavedBlobMergeScope Scope,
	Dictionary<string, SavedBlobMergeSource> ObservedSources, MergeInputFile[] GlobalInputs,
	string InventoryHash, SavedBodyComparison[] Comparisons, string[] ComparisonAttempts );

// Reuse a saved plan only if the selection, decisions and relevant inputs still match.
internal static class BlobMergePlanCache
{
	private static readonly ConditionalWeakTable<MergePlan, MergeDependencyScope> scopes = new();

	internal static MergeDependencyScope Dependencies( LoadedBlobMerge loaded, MergePlan plan, IReadOnlySet<string> selected )
		=> scopes.TryGetValue( plan, out var scope ) ? scope : MergeDependencyScope.Create( loaded.Modules, loaded.Vanilla, selected, plan );

	// Several saved plans can be combined only when they share no files or symbols and have no dependencies
	// on each other. Otherwise plan the selection from scratch. Restore cached comparison results along with the plan.
	internal static MergePlan Get(
		LoadedBlobMerge loaded,
		IReadOnlySet<string> selected,
		IReadOnlyDictionary<string, MergeChoice>? choices,
		IReadOnlyDictionary<string, PlannedFile>? overrides,
		Func<MergePlan> create )
	{
		string Key( IEnumerable<string> names ) => BlobMergeArtifacts.Identity( new
		{
			loaded.ModsDirectory,
			loaded.VanillaDirectory,
			loaded.InventoryPath,
			loaded.LooseModName,
			Selected = names.Order( StringComparer.OrdinalIgnoreCase ).ToArray(),
			Choices = ( choices ?? new Dictionary<string, MergeChoice>() ).OrderBy( pair => pair.Key, StringComparer.Ordinal ).ToArray(),
			Overrides = ( overrides ?? new Dictionary<string, PlannedFile>() ).OrderBy( pair => pair.Key, StringComparer.Ordinal ).ToArray()
		} );

		string key = Key( selected );
		var cached = BlobMergeArtifacts.Read<SavedExecutionPlan>( "execution-plan", key );

		if ( cached is not null && Matches( cached, loaded ) )
		{
			loaded.PlanningCache.Restore( cached.Comparisons, cached.ComparisonAttempts );
			scopes.Add( cached.Plan, cached.Scope.Dependencies );

			return cached.Plan;
		}

		SavedExecutionPlan[] components = [];

		if ( selected.Count > 1 && ( choices?.Count ?? 0 ) == 0 && ( overrides?.Count ?? 0 ) == 0 )
		{
			components = selected
				.Select( name => BlobMergeArtifacts.Read<SavedExecutionPlan>( "execution-plan", Key( [name] ) ) )
				.Where( value => value is not null )
				.Cast<SavedExecutionPlan>()
				.ToArray();
		}

		bool independent = components.Length == selected.Count
			&& components.All( component => Matches( component, loaded )
				&& component.Scope.Dependencies is { DefinedSymbols: not null, UsedSymbols: not null } );

		// Shared files or symbols can change the merge result, so these plans cannot simply be joined.
		for ( int i = 0; independent && i < components.Length; i++ )
		{
			for ( int j = 0; independent && j < i; j++ )
			{
				var left = components[i];
				var right = components[j];

				bool sharedOutput = left.Plan.Files
					.Select( file => file.Path )
					.Intersect( right.Plan.Files.Select( file => file.Path ), StringComparer.OrdinalIgnoreCase )
					.Any();

				if ( sharedOutput )
				{
					independent = false;
					break;
				}

				bool sharedSource = ( left.Plan.SourceFiles ?? [] ).Any( leftSource =>
					( right.Plan.SourceFiles ?? [] ).Any( rightSource =>
						leftSource.Mod.Equals( rightSource.Mod, StringComparison.OrdinalIgnoreCase )
						&& leftSource.Path.Equals( rightSource.Path, StringComparison.OrdinalIgnoreCase ) ) );

				if ( sharedSource )
				{
					independent = false;
					break;
				}

				bool sharedSymbols = left.Scope.Dependencies.DefinedSymbols!
					.Intersect( right.Scope.Dependencies.DefinedSymbols!, StringComparer.Ordinal ).Any()
					|| left.Scope.Dependencies.DefinedSymbols!
						.Intersect( right.Scope.Dependencies.UsedSymbols!, StringComparer.Ordinal ).Any()
					|| right.Scope.Dependencies.DefinedSymbols!
						.Intersect( left.Scope.Dependencies.UsedSymbols!, StringComparer.Ordinal ).Any();

				independent = !sharedSymbols;
			}
		}

		MergePlan plan;
		MergeDependencyScope scope;

		if ( independent )
		{
			BlobMergeArtifacts.RecordBuild( "plan-composition" );
			plan = new(
				components.SelectMany( value => value.Plan.Files ).ToArray(),
				components.SelectMany( value => value.Plan.Conflicts ).ToArray(),
				components.SelectMany( value => value.Plan.Notices ).ToArray(),
				components.SelectMany( value => value.Plan.IncludedMods ).Distinct( StringComparer.OrdinalIgnoreCase ).ToArray(),
				components.SelectMany( value => value.Plan.ExternalEdges ).Distinct().ToArray(),
				components.SelectMany( value => value.Plan.Wrappers ?? [] ).ToArray(),
				components.SelectMany( value => value.Plan.SourceFiles ?? [] ).Distinct().ToArray(),
				components.SelectMany( value => value.Plan.Decisions ?? [] ).ToArray() );

			var parts = components.Select( value => value.Scope.Dependencies ).ToArray();

			scope = new(
				Mods: parts.SelectMany( value => value.Mods ).Distinct( StringComparer.OrdinalIgnoreCase ).ToArray(),
				ChangedNames: parts.SelectMany( value => value.ChangedNames ).Distinct().ToArray(),
				OverlapNames: parts.SelectMany( value => value.OverlapNames ).Distinct().ToArray(),
				UsedNames: parts.SelectMany( value => value.UsedNames ).Distinct().ToArray(),
				Paths: parts.SelectMany( value => value.Paths ).Distinct( StringComparer.OrdinalIgnoreCase ).ToArray(),
				Files: parts.SelectMany( value => value.Files ).Distinct().ToArray(),
				DefinedSymbols: parts.SelectMany( value => value.DefinedSymbols! ).Distinct().ToArray(),
				UsedSymbols: parts.SelectMany( value => value.UsedSymbols! ).Distinct().ToArray() );

			foreach ( var component in components )
				loaded.PlanningCache.Restore( component.Comparisons, component.ComparisonAttempts );
		}
		else
		{
			BlobMergeArtifacts.RecordBuild( "plan-build" );
			plan = create();
			scope = MergeDependencyScope.Create( loaded.Modules, loaded.Vanilla, selected, plan );
		}

		scopes.Add( plan, scope );

		var inventory = BlobMergeCheckCache.ReadInventory( loaded.InventoryPath ?? loaded.StateFile! );
		var observed = loaded.Sources.ToDictionary( source => source.Name, source =>
		{
			string prefix = Path.TrimEndingDirectorySeparator( source.Directory ) + Path.DirectorySeparatorChar;
			var inputs = loaded.Snapshot
				.Where( input => input.Path.StartsWith( prefix, StringComparison.OrdinalIgnoreCase )
					&& ( source.Blobs.Count > 0
						? input.Path.EndsWith( ".rsblob", StringComparison.OrdinalIgnoreCase )
						: input.Path.EndsWith( ".ws", StringComparison.OrdinalIgnoreCase ) ) )
				.ToArray();

			return new SavedBlobMergeSource(
				BlobMergeCheckCache.SourceIdentity( source ),
				inputs,
				inputs.Select( input => input.Path ).ToArray() );
		}, StringComparer.OrdinalIgnoreCase );

		var outputs = plan.Files
			.Select( file => Path.Combine( loaded.ModsDirectory, loaded.LooseModName, "content", "scripts", file.Path ) )
			.ToArray();
		var global = GlobalInputs( loaded );
		var savedScope = new SavedBlobMergeScope(
			scope,
			scope.Mods,
			BlobMergeCheckCache.ScopeFingerprint( loaded.Settings, scope.Mods, true ),
			new( StringComparer.OrdinalIgnoreCase ),
			null,
			BlobMergeCheckCache.ScopeSources( loaded.Sources, loaded.Snapshot, scope ),
			BlobMergeCheckCache.InventoryFingerprint( inventory, scope, outputs, loaded.ModsDirectory ) );

		BlobMergeArtifacts.Write( "execution-plan", key, new SavedExecutionPlan(
			plan, savedScope, observed, global,
			loaded.StateHash ?? "",
			loaded.PlanningCache.SavedComparisons(), loaded.PlanningCache.SavedAttempts() ) );

		return plan;
	}

	// Inventory and load-order files contain unrelated entries. Matches checks only the entries used by this plan.
	private static MergeInputFile[] GlobalInputs( LoadedBlobMerge loaded )
	{
		string configuration = Path.Combine( Path.GetDirectoryName( loaded.SettingsPath )!, "modioModsConfig.json" );

		return loaded.Snapshot.Where( input => !loaded.Sources.Any( source => input.Path.StartsWith(
				Path.TrimEndingDirectorySeparator( source.Directory ) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase ) )
			&& !input.Path.Equals( loaded.InventoryPath, StringComparison.OrdinalIgnoreCase )
			&& !input.Path.Equals( loaded.SettingsPath, StringComparison.OrdinalIgnoreCase )
			&& !input.Path.Equals( configuration, StringComparison.OrdinalIgnoreCase ) ).ToArray();
	}

	// Changes outside the plan are allowed only after checking that they cannot affect its files or symbols.
	private static bool Matches( SavedExecutionPlan saved, LoadedBlobMerge loaded )
	{
		var inputs = loaded.Snapshot.Concat( loaded.Guards ).GroupBy( input => input.Path, StringComparer.OrdinalIgnoreCase )
			.ToDictionary( group => group.Key, group => group.First().Sha256, StringComparer.OrdinalIgnoreCase );

		bool Same( MergeInputFile input ) => inputs.TryGetValue( input.Path, out string? hash ) && hash == input.Sha256;

		if ( saved.GlobalInputs.Length != GlobalInputs( loaded ).Length || saved.GlobalInputs.Any( input => !Same( input ) )
			|| saved.Scope.Fingerprint != BlobMergeCheckCache.ScopeFingerprint( loaded.Settings, saved.Scope.SettingsMods, true ) )
			return false;

		var sources = loaded.Sources.ToDictionary( source => source.Name, StringComparer.OrdinalIgnoreCase );

		foreach ( var pair in saved.Scope.Sources )
			if ( !sources.TryGetValue( pair.Key, out var source ) || pair.Value.Identity != BlobMergeCheckCache.SourceIdentity( source )
				|| pair.Value.Inputs.Any( input => !Same( input ) ) )
				return false;

		if ( saved.InventoryHash != ( loaded.StateHash ?? "" ) )
		{
			var inventory = BlobMergeCheckCache.ReadInventory( loaded.InventoryPath ?? loaded.StateFile! );

			if ( saved.Scope.Inventory != BlobMergeCheckCache.InventoryFingerprint( inventory, saved.Scope.Dependencies,
					saved.Plan.Files.Select( file => Path.Combine( loaded.ModsDirectory, loaded.LooseModName, "content", "scripts", file.Path ) ), loaded.ModsDirectory ) )
				return false;
		}

		foreach ( var source in loaded.Sources )
		{
			saved.ObservedSources.TryGetValue( source.Name, out var before );

			var paths = source.Blobs.Count > 0 ? source.Blobs : loaded.SourcePaths[source.Name].Where( path => path.EndsWith( ".ws", StringComparison.OrdinalIgnoreCase ) ).ToArray();

			if ( before?.Identity == BlobMergeCheckCache.SourceIdentity( source ) && before.Paths.SequenceEqual( paths, StringComparer.OrdinalIgnoreCase )
				&& before.Inputs.All( Same ) )
				continue;

			var changed = paths.Where( path => before?.Inputs.FirstOrDefault( input => input.Path.Equals( path, StringComparison.OrdinalIgnoreCase ) ) is not { } old || !Same( old ) ).ToArray();
			var facts = BlobMergeCheckCache.Inspect( source, changed );

			if ( facts is null || facts.Value.Mentioned.Intersect( saved.Scope.Dependencies.ChangedNames, StringComparer.Ordinal ).Any()
				|| facts.Value.Declared.Intersect( saved.Scope.Dependencies.UsedNames, StringComparer.Ordinal ).Any()
				|| facts.Value.Declared.Intersect( saved.Scope.Dependencies.OverlapNames, StringComparer.Ordinal ).Any()
				|| facts.Value.Paths.Intersect( saved.Scope.Dependencies.Paths, StringComparer.OrdinalIgnoreCase ).Any() )
				return false;
		}

		return true;
	}
}
