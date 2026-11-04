#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WitcherScriptMerger.FileIndex;
using WitcherScriptMerger.Inventory;
using WitcherScriptMerger.LoadOrder;
using WitcherScriptMerger.Merging;

namespace WitcherScriptMerger.BlobMerging;

// A ready selection also records the files and symbols it would write. The UI uses this to group linked conflicts.
internal sealed record BlobMergeReadiness( bool Available, DeferredText? Failure = null, string? BlockingEnum = null,
	string[]? MergeInputs = null, string[]? BlobInputs = null, MergeSourceFile[]? SourceFiles = null, string[]? OutputPaths = null, bool NeedsCompilation = false,
	Dictionary<string, string[]>? SymbolOutputs = null )
{
	[System.Text.Json.Serialization.JsonIgnore]
	internal string? Reason => Failure?.Render();

	internal bool Includes( string? source ) => Available && ( source is null
		|| MergeInputs?.Contains( source, StringComparer.OrdinalIgnoreCase ) == true );
}

// Checks whether scan selections can merge before the user starts a merge.
// One instance serves the scan, shares previous results and recovers additional blobs only when needed.
internal sealed class BlobMergePreflight
{
	internal string VanillaDirectory { get; private init; } = "";
	private ModFile[] files;
	private readonly ConcurrentDictionary<string, DeferredText> recoveryFailures = new( StringComparer.OrdinalIgnoreCase );
	private readonly ConcurrentDictionary<string, Lazy<BlobMergeReadiness>> results = new( StringComparer.OrdinalIgnoreCase );
	private readonly ConcurrentDictionary<string, SavedBlobMergeScope> resultScopes = new( StringComparer.OrdinalIgnoreCase );
	private readonly Dictionary<string, string[]> groups = new( StringComparer.OrdinalIgnoreCase );
	private readonly Dictionary<ModFile, LinkedBlobMergeGroup> linkedGroups = new( System.Collections.Generic.ReferenceEqualityComparer.Instance );

	internal LinkedBlobMergeGroup? GroupFor( ModFile file ) => linkedGroups.GetValueOrDefault( file );

	private readonly ConcurrentDictionary<string, bool> outputChecks = new( StringComparer.OrdinalIgnoreCase );
	private LoadedBlobMerge? loaded;
	private bool invalidated;

	internal void Invalidate()
	{
		lock ( preparationLock )
		{
			invalidated = true;
			loaded = null;
			results.Clear();
			linkedGroups.Clear();
		}
	}

	private Func<IReadOnlySet<string>, LoadedBlobMerge>? load;
	private readonly HashSet<string> recovered = new( StringComparer.OrdinalIgnoreCase );
	private IReadOnlyList<ModLoadSetting> settings = Array.Empty<ModLoadSetting>();
	private string fingerprint = "";
	private string? failure;
	private bool orderKnown;
	private MergeInventory inventory = new();
	private GamePaths modsFolders = GamePaths.ForModsDirectory( "" );
	private int planCount;
	private long evaluationTicks;
	private string inputIdentity = "";
	private string inventoryHash = "";
	private readonly object preparationLock = new();
	private BlobMergeCheckCache? cache;
	private SavedBlobMergeChecks? saved;
	private IReadOnlyList<BlobMergeSource> sourceInputs = [];
	private int pendingSave;
	private bool preparing;
	private Action? planningStarted;
	private Action<string, string>? reportProgress;
	private int planningReported;

	private void ReportPlanning()
	{
		if ( Interlocked.Exchange( ref planningReported, 1 ) == 0 )
			planningStarted?.Invoke();
	}

	internal bool LoadedFromDisk { get; private set; }
	internal int RecoveryLoadCount { get; private set; }
	internal string? CachePath => cache?.FilePath;
	internal int PlanCount => planCount;
	internal int RecoveryCandidateCount { get; private set; }
	internal int ConflictCandidateCount { get; private set; }
	internal double PreparationSeconds { get; private set; }
	internal double PlanningSeconds { get; private set; }
	internal double PlanEvaluationSeconds => TimeSpan.FromTicks( evaluationTicks ).TotalSeconds;
	internal bool Reused { get; private set; }

	private BlobMergePreflight( ModFile[] files ) => this.files = files;

	// Attach the same check state to every conflict in the scan. Reuse previous results only after checking their inputs.
	internal static BlobMergePreflight Scan( IEnumerable<ModFile> files, string mods, string vanilla, string settingsPath,
		IReadOnlyList<BlobMergeSource> sources, IReadOnlyList<ModLoadSetting> settings, bool orderKnown,
		string inventoryPath, string looseMod, BlobMergePreflight? previous = null, string? cacheDirectory = null, Action? planningStarted = null,
		Action<string, string>? progress = null )
	{
		using var messages = UiText.CaptureMessages();
		var currentFiles = files.Where( file => file.Category == Categories.PrecompiledScript && file.Mods.Any( source => source.HasPrecompiledScriptDefinition ) ).ToArray();
		string identity = System.Text.Json.JsonSerializer.Serialize( new
		{
			Mods = mods,
			Vanilla = vanilla,
			Settings = settingsPath,
			Inventory = inventoryPath,
			looseMod
		} );

		if ( previous?.CanReuse( identity, sources, settings, orderKnown ) == true )
		{
			previous.files = currentFiles;
			previous.planningStarted = planningStarted;
			previous.reportProgress = progress;
			previous.planningReported = 0;

			foreach ( var file in currentFiles )
				file.MergePreflight = previous;

			previous.Reused = true;
			previous.PreparationSeconds = 0;

			var refresh = System.Diagnostics.Stopwatch.StartNew();

			previous.Refresh( settings, orderKnown, refreshFiles: true );
			previous.PlanningSeconds = refresh.Elapsed.TotalSeconds;
			previous.planningStarted = null;
			previous.reportProgress = null;
			previous.PruneInspectionSources( inventoryPath );

			return previous;
		}

		var scan = new BlobMergePreflight( currentFiles )
		{
			inputIdentity = identity,
			planningStarted = planningStarted,
			reportProgress = progress,
			VanillaDirectory = vanilla,
			modsFolders = GamePaths.ForModsDirectory( mods )
		};

		scan.cache = new BlobMergeCheckCache( cacheDirectory ?? BlobMergeCheckCache.DefaultDirectory, mods, inventoryPath );
		scan.sourceInputs = sources;

		foreach ( var file in scan.files )
			file.MergePreflight = scan;

		try
		{
			byte[]? data = File.Exists( inventoryPath ) ? File.ReadAllBytes( inventoryPath ) : null;

			scan.inventory = MergeInventory.Deserialize( data );
			scan.inventoryHash = data is null ? "" : MergeBuilder.Hash( data );
		}
		catch ( Exception error ) when ( BlobMergeWorkspace.IsInspectionFailure( error ) ) { scan.failure = error.Message; }

		var selected = BlobConflictSelection.Sources( scan.files.Where( file => !scan.inventory.HasResolvedConflict( file, scan.modsFolders ) ) );

		scan.RecoveryCandidateCount = selected.Count;
		scan.ConflictCandidateCount = selected.Count;
		scan.load = names =>
		{
			scan.ReportPlanning();
			scan.RecoveryLoadCount++;

			return BlobMergeWorkspace.Load( mods, vanilla, settingsPath, sources,
				scan.fingerprint.Length == 0 ? settings : scan.settings, names, inventoryPath, looseMod,
				recoveryUnavailable: (
					name,
					reason ) => scan.recoveryFailures[name] = UiText.CapturedText( reason ), selectExistingBlobs: false, progress: scan.reportProgress );
		};

		var timer = System.Diagnostics.Stopwatch.StartNew();

		if ( scan.failure is null && orderKnown )
			scan.saved = scan.cache.Read( identity, sources, settings, orderKnown );

		if ( scan.saved is { } saved )
		{
			scan.fingerprint = SettingsFingerprint( settings, orderKnown );
			scan.recovered.UnionWith( saved.Recovered );

			foreach ( var pair in saved.RecoveryFailures )
				scan.recoveryFailures[pair.Key] = pair.Value;

			foreach ( var pair in saved.Results )
				scan.results[pair.Key] = new Lazy<BlobMergeReadiness>( () => pair.Value );

			foreach ( var pair in saved.Scopes )
				scan.resultScopes[pair.Key] = pair.Value;

			foreach ( var pair in saved.OutputChecks )
				scan.outputChecks[pair.Key] = pair.Value;

			scan.RecoveryCandidateCount = saved.Recovered.Length;
			scan.LoadedFromDisk = scan.Reused = true;
		}
		else if ( selected.Count > 0 )
		{
			try
			{
				scan.loaded = scan.load( selected );
				scan.recovered.UnionWith( scan.loaded.Selected );
			}
			catch ( Exception error ) when ( BlobMergeWorkspace.IsInspectionFailure( error ) ) { scan.failure = error.Message; }
		}

		scan.PreparationSeconds = timer.Elapsed.TotalSeconds;
		timer.Restart();
		scan.Refresh( settings, orderKnown, refreshFiles: true );
		scan.PlanningSeconds = timer.Elapsed.TotalSeconds;
		scan.planningStarted = null;
		scan.reportProgress = null;
		scan.PruneInspectionSources( inventoryPath );

		return scan;
	}

	private void PruneInspectionSources( string inventoryPath )
	{
		try
		{
			var planned = sourceInputs.Where( source => recovered.Contains( source.Name ) && !recoveryFailures.ContainsKey( source.Name )
				&& source.Blobs.Count == 1 && File.Exists( source.Blobs[0] ) )
				.ToDictionary( source => source.Name, source => MergeBuilder.Hash( source.Blobs[0] ), StringComparer.OrdinalIgnoreCase );

			RecoveredScriptStore.Prune( inventoryPath, planned );
		}
		catch ( Exception error ) when ( BlobMergeWorkspace.IsInspectionFailure( error ) ) { }
	}

	private bool CanReuse( string identity, IReadOnlyList<BlobMergeSource> sources, IReadOnlyList<ModLoadSetting> current, bool valid )
	{
		if ( invalidated || failure is not null || inputIdentity != identity )
			return false;

		if ( !sourceInputs.Select( BlobMergeCheckCache.SourceIdentity ).Order( StringComparer.OrdinalIgnoreCase )
			.SequenceEqual( sources.Select( BlobMergeCheckCache.SourceIdentity ).Order( StringComparer.OrdinalIgnoreCase ) ) )
			return false;

		try
		{
			if ( cache is null || inventoryHash != ( File.Exists( saved?.InventoryPath ?? loaded?.InventoryPath )
				? MergeBuilder.Hash( ( saved?.InventoryPath ?? loaded?.InventoryPath )! ) : "" ) )
				return false;

			if ( loaded is null )
				return saved is not null && BlobMergeCheckCache.Filter( saved, sources, current, valid )?.Results.Count == results.Count;

			if ( new BlobMergeStore( loaded.StateFile! ).HasInterruptedUpdate )
				return false;

			if ( outputChecks.Any( pair => pair.Value != ( File.Exists( pair.Key ) || Directory.Exists( pair.Key ) ) ) )
				return false;

			BlobMergeWorkspace.VerifySnapshot( loaded, ignoreLoadOrder: true );

			return true;
		}
		catch ( Exception error ) when ( BlobMergeWorkspace.IsInspectionFailure( error ) ) { return false; }
	}

	// A priority change can affect readiness and group membership. Recheck affected selections before rebuilding the groups.
	internal void Refresh( IReadOnlyList<ModLoadSetting> current, bool valid, bool refreshFiles = false )
	{
		if ( invalidated )
			return;

		string next = SettingsFingerprint( current, valid );

		if ( next == fingerprint && !refreshFiles )
			return;

		bool changed = next != fingerprint;

		if ( changed )
		{
			foreach ( string key in results.Keys )
			{
				if ( !resultScopes.TryGetValue( key, out var scope )
					|| scope.Fingerprint != BlobMergeCheckCache.ScopeFingerprint( current, scope.SettingsMods, valid )
					|| scope.PlacementContext is not null
						&& scope.PlacementContext != BlobMergeCheckCache.PlacementFingerprint( sourceInputs, current ) )
				{
					results.TryRemove( key, out _ );
					resultScopes.TryRemove( key, out _ );
				}
			}
		}

		fingerprint = next;
		settings = current.Select( setting => new ModLoadSetting( setting.ModName )
		{
			Priority = setting.Priority,
			IsEnabled = setting.IsEnabled,
			IsExternal = setting.IsExternal,
			StateKnown = setting.StateKnown
		} ).ToArray();

		orderKnown = valid;
		groups.Clear();

		var conflicts = files.Where( file => file.HasConflict && !inventory.HasResolvedConflict( file, modsFolders ) ).ToArray();

		foreach ( var file in conflicts )
		{
			var names = BlobNames( BlobConflictSelection.ActiveSources( file ) );

			foreach ( string name in names )
			{
				groups[name] = groups.GetValueOrDefault( name, [] )
					.Concat( names )
					.Distinct( StringComparer.OrdinalIgnoreCase )
					.ToArray();
			}
		}

		preparing = true;

		try
		{
			Parallel.ForEach(
				conflicts,
				new ParallelOptions { MaxDegreeOfParallelism = Math.Min( 4, Environment.ProcessorCount ) },
				file => CanSelect( file ) );

			linkedGroups.Clear();

			foreach ( var group in LinkedBlobMergeGroup.Create( conflicts, Inspect, VanillaDirectory ) )
			{
				foreach ( var file in group.Conflicts )
					linkedGroups[file] = group;
			}
		}
		finally
		{
			preparing = false;
		}

		Persist();
	}

	internal static string SettingsFingerprint( IReadOnlyList<ModLoadSetting> current, bool valid ) =>
		System.Text.Json.JsonSerializer.Serialize( current.OrderBy( setting => setting.ModName, StringComparer.OrdinalIgnoreCase )
			.Select( setting => new { setting.ModName, setting.Priority, setting.IsEnabled, setting.IsExternal, setting.StateKnown } ) ) + valid;

	// This can replace loaded. Callers must read it again after preparing additional sources.
	private void PrepareSources( IEnumerable<string> names )
	{
		lock ( preparationLock )
		{
			if ( failure is not null )
				return;

			var needed = names.ToHashSet( StringComparer.OrdinalIgnoreCase );

			if ( loaded is null )
			{
				if ( saved is null )
					needed.UnionWith( BlobConflictSelection.Sources( files.Where( file => !inventory.HasResolvedConflict( file, modsFolders ) ) ) );

				try
				{
					loaded = load!( needed );
					recovered.Clear();
					recovered.UnionWith( loaded.Selected );
				}
				catch ( Exception error ) when ( BlobMergeWorkspace.IsInspectionFailure( error ) ) { failure = error.Message; return; }
			}

			foreach ( string name in needed.Where( name => !recovered.Contains( name ) ) )
			{
				try
				{
					var module = BlobMergeWorkspace.RecoverAdditional( loaded, name );

					loaded = loaded with
					{
						Modules = loaded.Modules.Select( original => original.Name.Equals( name, StringComparison.OrdinalIgnoreCase ) ? module : original ).ToArray()
					};
				}
				catch ( Exception error ) when ( BlobMergeWorkspace.IsInspectionFailure( error ) ) { recoveryFailures[name] = UiText.CapturedText( error.Message ); }

				recovered.Add( name );
			}

			RecoveryCandidateCount = recovered.Count;
		}
	}

	// Recheck sources and settings before reusing scan data for a merge. Return null to force a fresh load if they changed.
	// Include the previous merge's blobs because the replacement must preserve their contributions.
	internal LoadedBlobMerge? PrepareExecution( IReadOnlySet<string> names, IReadOnlyList<BlobMergeSource> sources,
		IReadOnlyList<ModLoadSetting> current )
	{
		lock ( preparationLock )
		{
			if ( invalidated )
				throw new InvalidOperationException( UiText.T( "BlobMerge", "Caches were cleared. Refresh conflicts before merging." ) );

			if ( failure is not null || !sourceInputs.Select( BlobMergeCheckCache.SourceIdentity ).Order( StringComparer.OrdinalIgnoreCase )
				.SequenceEqual( sources.Select( BlobMergeCheckCache.SourceIdentity ).Order( StringComparer.OrdinalIgnoreCase ) ) )
				return null;

			PrepareSources( names );

			if ( loaded is null || failure is not null || SettingsFingerprint( loaded.Settings, true ) != SettingsFingerprint( current, true ) )
				return null;

			try
			{
				BlobMergeWorkspace.VerifySnapshot( loaded );
			}
			catch ( Exception error ) when ( BlobMergeWorkspace.IsInspectionFailure( error ) ) { return null; }

			var selected = names.ToHashSet( StringComparer.OrdinalIgnoreCase );

			if ( loaded.Previous is { } prior )
				selected.UnionWith( loaded.Sources.Where( source => source.Blobs.Count > 0
					&& prior.Manifest.Mods.Contains( source.Name, StringComparer.OrdinalIgnoreCase ) ).Select( source => source.Name ) );

			PrepareSources( selected );

			if ( selected.Any( recoveryFailures.ContainsKey ) )
				return null;

			var references = loaded.ReferenceModules.ToDictionary( module => module.Name, StringComparer.OrdinalIgnoreCase );
			var modules = loaded.Modules.OrderBy(
				module => module.Name,
				Comparer<string>.Create( ( a, b ) => LoadOrderComparer.CompareRuntimeOrder( a,
					current.FirstOrDefault( setting => setting.ModName.Equals( a, StringComparison.OrdinalIgnoreCase ) ), b,
					current.FirstOrDefault( setting => setting.ModName.Equals( b, StringComparison.OrdinalIgnoreCase ) ) ) ) )
				.Select( ( module, order ) => ( selected.Contains( module.Name ) ? module : references[module.Name] ) with { Order = order } ).ToArray();

			return loaded with
			{
				Modules = modules,
				Selected = selected,
				Settings = current
			};
		}
	}

	// Observe all sources so the next scan can decide which changes affect the saved results.
	private void Persist()
	{
		if ( cache is null || loaded is null || failure is not null || Volatile.Read( ref pendingSave ) == 0 )
			return;

		try
		{
			BlobMergeWorkspace.VerifySnapshot( loaded, ignoreLoadOrder: true );

			string configuration = Path.Combine( Path.GetDirectoryName( loaded.SettingsPath )!, "modioModsConfig.json" );
			var observations = sourceInputs.ToDictionary(
				source => source.Name,
				source => new SavedBlobMergeSource( BlobMergeCheckCache.SourceIdentity( source ),
				loaded.Snapshot.Where( input => input.Path.StartsWith( Path.TrimEndingDirectorySeparator( source.Directory ) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase )
					&& ( loaded.Modules.Any( module => module.Name.Equals( source.Name, StringComparison.OrdinalIgnoreCase ) && module.IsBlob )
						? input.Path.EndsWith( ".rsblob", StringComparison.OrdinalIgnoreCase )
						: input.Path.StartsWith(
							Path.Combine( source.Directory, "content", "scripts" ) + Path.DirectorySeparatorChar,
							StringComparison.OrdinalIgnoreCase )
							&& input.Path.EndsWith( ".ws", StringComparison.OrdinalIgnoreCase ) ) ).ToArray(), BlobMergeCheckCache.ActivePaths( source ) ),
				StringComparer.OrdinalIgnoreCase );

			saved = new(
				inputIdentity,
				fingerprint,
				loaded.Snapshot.Where( input => !input.Path.Equals( loaded.SettingsPath, StringComparison.OrdinalIgnoreCase )
				&& !input.Path.Equals( configuration, StringComparison.OrdinalIgnoreCase ) && !sourceInputs.Any( source => input.Path.StartsWith(
					Path.TrimEndingDirectorySeparator( source.Directory ) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase ) ) ).Concat( loaded.Guards )
					.Where( input => !input.Path.Equals( loaded.InventoryPath, StringComparison.OrdinalIgnoreCase ) && !input.Path.Equals( loaded.StateFile, StringComparison.OrdinalIgnoreCase ) ).ToArray(),
				loaded.ModsDirectory,
				loaded.VanillaDirectory,
				loaded.InventoryPath ?? loaded.StateFile!,
				loaded.VanillaPaths,
				observations,
				recovered.ToArray(),
				recoveryFailures.ToDictionary( pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase ),
				results.Where( pair => pair.Value.IsValueCreated ).ToDictionary( pair => pair.Key, pair => pair.Value.Value, StringComparer.OrdinalIgnoreCase ),
				resultScopes.ToDictionary( pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase ),
				outputChecks.ToDictionary( pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase ),
				BlobMergeCheckCache.ManagedInventoryFingerprint( inventory ) );
			cache.Write( saved, snapshotVerified: true );
			Interlocked.Exchange( ref pendingSave, 0 );
		}
		catch ( Exception error ) when ( BlobMergeWorkspace.IsInspectionFailure( error ) ) { }
	}

	// Parallel callers can ask about the same selection. Lazy ensures they share one planning attempt.
	internal BlobMergeReadiness Inspect( IEnumerable<string> names )
	{
		if ( invalidated )
			return new( false, UiText.T( "BlobMerge", "Caches were cleared. Refresh conflicts before merging." ) );

		var selected = names.ToHashSet( StringComparer.OrdinalIgnoreCase );
		string key = string.Join( "\n", selected.Order( StringComparer.OrdinalIgnoreCase ) );
		var entry = results.GetOrAdd( key, _ => new Lazy<BlobMergeReadiness>( () => InspectUncached( selected ) ) );
		bool created = entry.IsValueCreated;
		var result = entry.Value;

		if ( !created )
		{
			Interlocked.Exchange( ref pendingSave, 1 );

			if ( !preparing )
				Persist();
		}

		return result;
	}

	// Keep track of how far the check reached, including failures, so its cached result covers the inputs it actually used.
	private sealed class InspectionTrace
	{
		internal Dictionary<string, bool> CheckedOutputs { get; } = new( StringComparer.OrdinalIgnoreCase );
		internal bool CheckingPlacement { get; set; }
		internal MergePlan? Plan { get; set; }
		internal IReadOnlyList<MergeModule>? Modules { get; set; }
	}

	// PrepareSources can replace loaded, so read the field again on each use.
	private LoadedBlobMerge LoadedState => loaded!;

	private BlobMergeReadiness InspectUncached( HashSet<string> selected )
	{
		using var messages = UiText.CaptureMessages();
		string key = string.Join( "\n", selected.Order( StringComparer.OrdinalIgnoreCase ) );
		InspectionTrace trace = new();
		BlobMergeReadiness result;

		if ( selected.Count > 0 && orderKnown && failure is null && !selected.Any( recoveryFailures.ContainsKey ) )
			PrepareSources( selected );

		result = SelectionFailure( selected ) ?? PlanSelection( selected, trace );

		if ( loaded is not null )
			SaveResultScope( key, selected, trace );

		return result;
	}

	private BlobMergeReadiness? SelectionFailure( HashSet<string> selected )
	{
		if ( !orderKnown )
			return new(
				false,
				UiText.T( "BlobMerge", "Load order is unavailable. Check the Game Documents and mod.io folders, then refresh." ) );

		if ( failure is not null )
			return new( false, failure );

		if ( selected.FirstOrDefault( recoveryFailures.ContainsKey ) is { } failed )
			return new( false, recoveryFailures[failed] );

		if ( loaded is null || selected.Count == 0 || selected.Any( name => !IsConventionalBlob( name ) ) )
			return new( false, UiText.T( "BlobMerge", "The selected source was not checked. Refresh conflicts before merging." ) );

		return null;
	}

	private bool IsConventionalBlob( string name )
	{
		return LoadedState.Modules.Any( module => module.IsBlob && !module.IsModIo && module.Name.Equals( name, StringComparison.OrdinalIgnoreCase ) );
	}

	private BlobMergeReadiness PlanSelection( HashSet<string> selected, InspectionTrace trace )
	{
		System.Diagnostics.Stopwatch planTimer;

		ReportPlanning();
		planTimer = System.Diagnostics.Stopwatch.StartNew();

		try
		{
			var references = LoadedState.ReferenceModules.ToDictionary( module => module.Name, StringComparer.OrdinalIgnoreCase );
			LoadedBlobMerge context;
			MergePlan plan;
			string placementName;

			Interlocked.Increment( ref planCount );
			trace.CheckingPlacement = true;
			(context, plan) = PlanWithConnectedSources( selected, references );
			trace.Modules = context.Modules;
			trace.Plan = plan;
			placementName = CompiledOutputPlacement.ForMerge( context, plan ).Name;
			trace.CheckingPlacement = false;
			CheckOutputPaths( plan, placementName, trace.CheckedOutputs );

			if ( HasEditedCompiledOutput() )
				throw new IOException( UiText.T( "BlobMerge", "The compiled merge was edited outside Script Merger. Preserve those changes before rebuilding it." ) );

			return ReadyResult( context, plan );
		}
		catch ( Exception error ) when ( BlobMergeWorkspace.IsInspectionFailure( error ) )
		{
			return new( false, BlobMergePlanning.InspectionFailureMessage( error ), ( error as EnumRenumberingException )?.EnumName );
		}
		finally
		{
			Interlocked.Add( ref evaluationTicks, planTimer.Elapsed.Ticks );
		}
	}

	// Earlier merges can connect more sources to this selection. Add them and repeat until the set stops growing.
	private (LoadedBlobMerge Context, MergePlan Plan) PlanWithConnectedSources(
		HashSet<string> selected,
		Dictionary<string, MergeModule> references )
	{
		while ( true )
		{
			var prepared = BlobMergePlanning.PrepareMinimalSelection( SelectionContext( selected, references ), files, reportProgress );
			string[] connected = ConnectedSources( prepared.Plan, selected );

			if ( connected.Length == 0 )
				return prepared;

			selected.UnionWith( connected );
			PrepareSources( connected );
		}
	}

	// Use full recovered code for selected mods and reference declarations for the remaining enabled mods.
	private LoadedBlobMerge SelectionContext( HashSet<string> selected, Dictionary<string, MergeModule> references )
	{
		MergeModule[] modules = LoadedState.Modules
			.Where( module => !settings.Any( setting => setting.ModName.Equals( module.Name, StringComparison.OrdinalIgnoreCase )
				&& ( !setting.StateKnown || setting.IsEnabled != true ) ) )
			.OrderBy(
				module => module.Name,
				Comparer<string>.Create( ( a, b ) => LoadOrderComparer.CompareRuntimeOrder( a, SettingFor( a ), b, SettingFor( b ) ) ) )
			.Select( ( module, order ) => ( selected.Contains( module.Name ) ? module : references[module.Name] ) with { Order = order } )
			.ToArray();

		return LoadedState with
		{
			Modules = modules,
			Settings = settings,
			Selected = selected
		};
	}

	private ModLoadSetting? SettingFor( string name )
	{
		return settings.FirstOrDefault( setting => setting.ModName.Equals( name, StringComparison.OrdinalIgnoreCase ) );
	}

	private IEnumerable<string> LooseOutputPaths( MergePlan plan )
	{
		return plan.Files
			.Where( file => !file.Compile )
			.Select( file => LoadedState.LooseModName + "/content/scripts/" + file.Path.Replace( '\\', '/' ) );
	}

	private string[] ConnectedSources( MergePlan plan, HashSet<string> selected )
	{
		HashSet<string> paths;
		string[] touched;
		HashSet<string> owners;

		if ( LoadedState.Previous is not { } existing )
			return [];

		paths = LooseOutputPaths( plan )
			.Concat( plan.NeedsCompilation
				? existing.Manifest.Outputs.Where( output => output.Path.EndsWith( ".rsblob", StringComparison.OrdinalIgnoreCase ) )
					.Select( output => output.Path.Replace( '\\', '/' ) )
				: Array.Empty<string>() )
			.ToHashSet( StringComparer.OrdinalIgnoreCase );
		touched = existing.Manifest.Outputs.Where( output => paths.Contains( output.Path.Replace( '\\', '/' ) ) ).Select( output => output.Path ).ToArray();

		if ( touched.Length == 0 )
			return [];

		owners = BlobMergeRemoval.Group( LoadedState.StateFile!, existing, [], touched );

		return LoadedState.Sources
			.Where( source => source.Blobs.Count > 0 && owners.Contains( source.Name ) && !selected.Contains( source.Name ) )
			.Select( source => source.Name )
			.ToArray();
	}

	// Do not overwrite a loose file unless an existing merge record accounts for it.
	private void CheckOutputPaths( MergePlan plan, string placementName, Dictionary<string, bool> checkedOutputs )
	{
		if ( plan.NeedsCompilation )
		{
			string folder = Path.Combine( LoadedState.ModsDirectory, placementName );

			outputChecks[folder] = checkedOutputs[folder] = File.Exists( folder ) || Directory.Exists( folder );
		}

		foreach ( string path in LooseOutputPaths( plan ) )
		{
			string physical = MergeBuilder.SafePath( LoadedState.ModsDirectory, path );
			bool occupied;

			if ( LoadedState.PreviousOutputs.ContainsKey( path.Replace( '/', Path.DirectorySeparatorChar ) ) )
				continue;

			occupied = File.Exists( physical ) || Directory.Exists( physical );
			outputChecks[physical] = checkedOutputs[physical] = occupied;

			if ( occupied && !IsLooseMergeOutput( path ) )
				throw new IOException( UiText.Format( "BlobMerge", "An existing merge already owns {0}. Remove that merge before rebuilding.", path ) );
		}
	}

	private bool IsLooseMergeOutput( string path )
	{
		string relativePath = path[( LoadedState.LooseModName.Length + "/content/scripts/".Length )..];

		return inventory.Merges.Any( merge => !merge.IsBlobMerge
			&& string.Equals( merge.MergedModName, LoadedState.LooseModName, StringComparison.OrdinalIgnoreCase )
			&& merge.RelativePath.Replace( '\\', '/' ).Equals( relativePath, StringComparison.OrdinalIgnoreCase ) );
	}

	private bool HasEditedCompiledOutput()
	{
		return LoadedState.Previous is { } prior && prior.Manifest.Outputs.Any( output =>
			output.Path.EndsWith( ".rsblob", StringComparison.OrdinalIgnoreCase )
			&& LoadedState.PreviousOutputs[output.Path].Length > 0
			&& LoadedState.PreviousOutputs[output.Path] != output.Sha256 );
	}

	private static BlobMergeReadiness ReadyResult( LoadedBlobMerge context, MergePlan plan )
	{
		return new(
			true,
			MergeInputs: plan.IncludedMods.ToArray(),
			BlobInputs: context.Modules.Where( module => module.IsBlob && plan.IncludedMods.Contains( module.Name, StringComparer.OrdinalIgnoreCase ) )
				.Select( module => module.Name ).ToArray(),
			SourceFiles: plan.SourceFiles?.ToArray(),
			OutputPaths: plan.Files.Select( file => file.Path ).Distinct( StringComparer.OrdinalIgnoreCase ).ToArray(),
			NeedsCompilation: plan.NeedsCompilation,
			SymbolOutputs: BlobMergeSymbolOutputs.Create( plan ) );
	}

	// A failed check depends only on the inputs it reached. Record those so unrelated changes can leave the result cached.
	private void SaveResultScope( string key, HashSet<string> selected, InspectionTrace trace )
	{
		try
		{
			IReadOnlyList<MergeModule> modules = trace.Modules ?? LoadedState.Modules
				.Where( module => !settings.Any( setting => setting.ModName.Equals( module.Name, StringComparison.OrdinalIgnoreCase )
					&& ( !setting.StateKnown || setting.IsEnabled == false ) ) )
				.ToArray();
			MergeDependencyScope dependencies = trace.Plan is not null
				? BlobMergePlanCache.Dependencies( LoadedState with
				{
					Modules = modules
				}, trace.Plan, selected )
				: MergeDependencyScope.Create( modules, LoadedState.Vanilla, selected, trace.Plan );
			string[] settingNames = SettingNames( dependencies, trace );

			resultScopes[key] = new(
				dependencies,
				settingNames,
				BlobMergeCheckCache.ScopeFingerprint( settings, settingNames, orderKnown ),
				trace.CheckedOutputs,
				trace.CheckingPlacement ? BlobMergeCheckCache.PlacementFingerprint( sourceInputs, settings ) : null,
				BlobMergeCheckCache.ScopeSources( sourceInputs, LoadedState.Snapshot, dependencies ),
				BlobMergeCheckCache.InventoryFingerprint( inventory, dependencies, trace.CheckedOutputs.Keys, LoadedState.ModsDirectory ) );
		}
		catch ( Exception error ) when ( BlobMergeWorkspace.IsInspectionFailure( error ) )
		{
		}
	}

	// Wrapper priorities and existing output mods can affect placement as well as direct dependencies.
	private string[] SettingNames( MergeDependencyScope dependencies, InspectionTrace trace )
	{
		IReadOnlySet<string> compiledFunctions = trace.Plan is null ? new HashSet<string>() : BlobMergePlanning.CompiledFunctionPaths( trace.Plan );
		IEnumerable<string> wrapperSettings = files
			.Where( file => compiledFunctions.Contains( file.RelativePath ) )
			.SelectMany( file => file.Mods )
			.Where( source => source.HasPrecompiledScriptDefinition && ( source.PrecompiledScriptKinds & ScriptSymbolKind.WrappedFunction ) != 0 )
			.Select( source => source.Name );
		IEnumerable<string> outputMods = trace.CheckedOutputs.Keys.Select( path => Path.GetRelativePath( LoadedState.ModsDirectory, path )
			.Split( Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar )[0] );

		return dependencies.Mods.Concat( wrapperSettings ).Concat( outputMods ).Distinct( StringComparer.OrdinalIgnoreCase ).ToArray();
	}

	internal BlobMergeReadiness Inspect( ModFile file )
	{
		var sources = BlobConflictSelection.ActiveSources( file );

		if ( sources.Any( source => !BlobConflictSelection.IsConventionalSource( source ) ) )
			return new( false, UiText.T( "BlobMerge", "Mod.io sources are read-only and cannot be merged." ) );

		return Inspect( BlobConflictSelection.RequiredBlobSources( sources ) );
	}

	// A full selection may fail while a smaller pair works. Try pairs before expanding to connected conflicts.
	internal bool CanSelect( ModFile file, string? source = null )
	{
		if ( !BlobConflictSelection.IsConventionalConflict( file ) )
			return false;

		var display = BlobConflictSelection.ActiveSources( file );

		if ( source is not null && !display.Any( item => item.Name.Equals( source, StringComparison.OrdinalIgnoreCase ) ) )
			return false;

		if ( source is not null && display.Any( item => item.Name.Equals( source, StringComparison.OrdinalIgnoreCase )
			&& recoveryFailures.ContainsKey( item.Name ) ) )
			return false;

		var full = Inspect( file );

		if ( full.Available )
			return full.Includes( source );

		for ( int a = 0; a < display.Count; a++ )
			for ( int b = a + 1; b < display.Count; b++ )
			{
				var pair = new[] { display[a], display[b] };

				if ( source is not null && !pair.Any( item => item.Name.Equals( source, StringComparison.OrdinalIgnoreCase ) ) )
					continue;

				if ( !BlobNames( pair ).Any() )
					continue;

				if ( Inspect( BlobConflictSelection.RequiredBlobSources( pair ) ).Includes( source ) )
					return true;
			}

		return !display.Any( item => recoveryFailures.ContainsKey( item.Name ) ) && Inspect( Connected( file ) ).Includes( source );
	}

	private HashSet<string> Connected( ModFile file )
	{
		var connected = BlobNames( BlobConflictSelection.ActiveSources( file ) ).ToHashSet( StringComparer.OrdinalIgnoreCase );
		var pending = new Queue<string>( connected );

		while ( pending.TryDequeue( out string? name ) )
			foreach ( string other in groups.GetValueOrDefault( name, [] ) )
				if ( !recoveryFailures.ContainsKey( other ) && connected.Add( other ) )
					pending.Enqueue( other );

		return connected;
	}

	internal static IEnumerable<string> BlobNames( IEnumerable<FileHash> sources ) => sources.Where( source => source.HasPrecompiledScriptDefinition )
		.Select( source => source.Name ).Distinct( StringComparer.OrdinalIgnoreCase );

	internal static string Guidance( ModFile file ) => BlobConflictSelection.CanSelect( file )
		? UiText.T( "BlobMerge", "To combine these changes, select the conflicts and use Create Selected Merges." )
		: UiText.Format( "BlobMerge", "Merge unavailable: {0}", BlobConflictSelection.Readiness( file )?.Reason
			?? UiText.T( "BlobMerge", "Merge availability has not been checked. Refresh conflicts." ) );

	private string AdditionalSelection( ModFile file )
	{
		var names = Connected( file );

		if ( !CanSelect( file ) || !Inspect( names ).Available )
			return string.Empty;

		return Environment.NewLine + Environment.NewLine + UiText.Format( "BlobMerge", "Select conflicts for these mods together:\n{0}",
			string.Join(
				Environment.NewLine,
				names.OrderBy( name => name.StartsWith( "mod", StringComparison.OrdinalIgnoreCase ) ? name[3..] : name,
				LoadOrderComparer.GameAlphabeticalComparer ).Select( name => "    - " + name ) ) );
	}

	// Leave out conflicts that already have a more specific report.
	internal static IReadOnlyList<string> Report( IEnumerable<ModFile> files ) => files.Where( file => file.Category == Categories.PrecompiledScript && file.HasConflict )
		.Where( file => !PrecompiledConflictReport.HasReport( file ) && !EnumConflictAnalysis.HasReport( file ) )
		.Where( file => file.MergePreflight is not null && !file.MergePreflight.inventory.HasResolvedConflict( file, file.MergePreflight.modsFolders )
			&& BlobConflictSelection.Readiness( file )?.Available != true )
		.GroupBy( file => BlobConflictSelection.Readiness( file )?.Reason )
		.Select( group => UiText.T( "BlobMerge", "Script merge requirements" ) + Environment.NewLine + Environment.NewLine + group.Key
			+ group.First().MergePreflight!.AdditionalSelection( group.First() )
			+ Environment.NewLine + Environment.NewLine + string.Join( Environment.NewLine,
				group.Select( file => file.RelativePath ).Distinct().Order( StringComparer.OrdinalIgnoreCase ).Select( path => "    - " + path ) ) ).ToArray();
}
