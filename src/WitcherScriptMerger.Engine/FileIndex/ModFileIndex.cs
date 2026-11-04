using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using WitcherScriptMerger.GameContent;
using WitcherScriptMerger.Inventory;
using WitcherScriptMerger.Tools;
using WitcherScriptMerger.BlobMerging;
using WitcherScriptMerger.LoadOrder;

namespace WitcherScriptMerger.FileIndex;

// Builds the scan index used by the conflict list and merge checks.
// The partial files separate script-source selection and individual class-member comparisons.
internal partial class ModFileIndex
{
	private readonly Dictionary<string, ModFile> filesByRelativePath = new( StringComparer.OrdinalIgnoreCase );

	private readonly BundleScanCoordinator bundleScanner;

	private readonly GamePaths gamePaths;

	private readonly IEngineHost host;

	// Give each indexed file access to the current load order so later priority edits update conflict results.
	private readonly Func<CustomLoadOrder> loadOrder;

	private CustomLoadOrder CurrentLoadOrder() => loadOrder?.Invoke();

	private IReadOnlyList<ModFile> xmlDefinitionOrderInputs = Array.Empty<ModFile>();
	private IReadOnlySet<string> vanillaXmlItemKeys = new HashSet<string>();
	private bool vanillaXmlItemsKnown = true;
	internal BlobMergePreflight MergePreflight { get; private set; }
	private readonly string preflightCacheDirectory;
	private Dictionary<string, int> xmlDefinitionSourceCounts = new( StringComparer.OrdinalIgnoreCase );

#if RSBLOB_SEMANTIC_ANALYSIS
	private readonly RsBlobSymbolCache rsBlobSymbolCache;
	private readonly List<BlobDependencySource> dependencySources = new();
	private readonly List<BlobDependencySource> looseDependencySources = new();
	private readonly HashSet<string> dependencyTypes = new( StringComparer.OrdinalIgnoreCase );
	private readonly HashSet<string> dependencyFunctions = new( StringComparer.OrdinalIgnoreCase );
	private readonly Dictionary<string, string> vanillaFilesBySymbol = new( StringComparer.OrdinalIgnoreCase );
	private readonly Dictionary<string, IReadOnlyDictionary<string, int>> dependencyEnums = new( StringComparer.Ordinal );
	private bool dependencyBaselineReady;
#endif

	public List<ModFile> Files;

	public IEnumerable<ModFile> Conflicts => Files.Where( ( ModFile f ) => f.HasConflict );

	public bool HasConflict => Conflicts.Any();

	public int ModCount { get; private set; }

	public int ScriptCount { get; private set; }

	public int XmlCount { get; private set; }

	public bool ScriptsScanned { get; private set; }

	public bool ScanCompleted { get; private set; }

	private readonly HashSet<string> countedBundles = new( StringComparer.OrdinalIgnoreCase );

	public int BundleCount { get; private set; }

	public int BlobCount { get; private set; }

	public int BlobSymbolCount { get; private set; }

	public int LooseScriptSymbolCount { get; private set; }

	public IReadOnlyList<string> BlobWarnings => blobWarnings;

	public IReadOnlyList<string> ScriptSymbolWarnings => scriptSymbolWarnings;

	public IReadOnlyList<string> XmlOverrideWarnings => xmlOverrideWarnings;

	public IReadOnlyList<string> InvalidXmlWarnings => invalidXmlWarnings;

	public IReadOnlyList<string> DependencyWarnings
	{
		get
		{
#if RSBLOB_SEMANTIC_ANALYSIS
			CustomLoadOrder order = CurrentLoadOrder();

			if ( !ScanCompleted || !dependencyBaselineReady || order?.IsValid != true )
				return Array.Empty<string>();

			var diagnostics = new List<string>();
			var issues = BlobDependencyAnalyzer.Analyze( dependencySources, order.Mods,
				dependencyTypes, dependencyFunctions, looseDependencySources, diagnostics.Add );

			diagnostics.AddRange( BlobDependencyReport.Format( issues ) );

			return diagnostics;
#else
			return Array.Empty<string>();
#endif
		}
	}

	public IReadOnlyList<string> XmlDefinitionWarnings => ScanCompleted && CurrentLoadOrder()?.IsValid == true
		? XmlDefinitionOrderDiagnostics.Format( xmlDefinitionOrderInputs.Concat( Files ), CurrentLoadOrder().Mods ) : Array.Empty<string>();

	public IReadOnlyList<string> PrecompiledConflictWarnings => GetPrecompiledConflictWarnings( Files );

	private IReadOnlyList<string> GetPrecompiledConflictWarnings( IEnumerable<ModFile> conflicts ) =>
		ScanCompleted && ScriptsScanned && CurrentLoadOrder()?.IsValid == true
			? PrecompiledConflictReport.Format( conflicts ) : Array.Empty<string>();

	public IReadOnlyList<string> ScriptDiagnostics => GetScriptDiagnostics( Files );

	internal IReadOnlyList<string> GetScriptDiagnostics( IEnumerable<ModFile> conflicts ) =>
		blobWarnings.Concat( scriptSymbolWarnings ).Concat( DependencyWarnings )
			.Concat( GetPrecompiledConflictWarnings( conflicts ) ).Concat( EnumConflictAnalysis.Report( conflicts ) )
			.Concat( BlobMergePreflight.Report( conflicts ) )
			.Concat( WitcherScriptMerger.BlobMerging.BlobMergeWorkspace.Diagnostics( gamePaths.ModsDirectory ) ).ToArray();

	public IReadOnlyList<string> XmlDiagnostics => xmlOverrideWarnings.Concat( XmlDefinitionWarnings ).ToArray();

	public IReadOnlyList<string> ScanIssues => ScriptDiagnostics.Concat( XmlDiagnostics ).ToArray();

	public int BundleCacheHitCount { get; private set; }

	public int BundleCacheMissCount { get; private set; }

	public int RsBlobSymbolCacheHitCount { get; private set; }

	public int RsBlobSymbolCacheMissCount { get; private set; }

	private readonly List<string> blobWarnings = new();

	private readonly List<string> scriptSymbolWarnings = new();

	private readonly List<string> xmlOverrideWarnings = new();
	private readonly List<string> invalidXmlWarnings = new();

	public ModFileIndex( GamePaths gamePaths, IEngineHost host, Func<CustomLoadOrder> loadOrder = null )
		: this( gamePaths, host, bundleScanner: null, loadOrder: loadOrder )
	{
	}

	internal void ForgetMergeChecks()
	{
		MergePreflight?.Invalidate();
		MergePreflight = null;

		foreach ( var file in Files )
			file.MergePreflight = null;

		ScanCompleted = false;
	}

	internal ModFileIndex(
		GamePaths gamePaths,
		IEngineHost host,
		BundleScanCoordinator bundleScanner,
		BlobMergePreflight previousPreflight = null,
		string preflightCacheDirectory = null,
		Func<CustomLoadOrder> loadOrder = null )
	{
		this.gamePaths = gamePaths;
		this.host = host;
		this.loadOrder = loadOrder;
		this.bundleScanner = bundleScanner;
		MergePreflight = previousPreflight;
		this.preflightCacheDirectory = preflightCacheDirectory;
#if RSBLOB_SEMANTIC_ANALYSIS
		this.rsBlobSymbolCache = new RsBlobSymbolCache( RsBlobSymbolCache.DefaultCacheDirectory );
#endif
		Files = new List<ModFile>();
	}

	// Discover folders before starting the background worker. Completion returns through the caller's
	// synchronization context, which is the UI thread for a normal scan.
	public void BuildAsync(
		bool checkScripts,
		bool checkBundles,
		ProgressChangedEventHandler progressHandler,
		RunWorkerCompletedEventHandler completedHandler )
	{
		HashSet<string> ignoredModNames;
		List<ModSource> modSources;
		IReadOnlyList<ModIoInstalledMod> discoveredModIoMods;
		BackgroundWorker worker = new BackgroundWorker
		{
			WorkerReportsProgress = true
		};

		gamePaths.EnsureModsDirectory();
		ignoredModNames = GetIgnoredModNames().ToHashSet( StringComparer.OrdinalIgnoreCase );
		modSources = ConventionalModSources( ignoredModNames );
		discoveredModIoMods = ModIoDiscovery.ForSettings( host.Settings ).Discover();
		AddModIoSources( modSources, discoveredModIoMods, ignoredModNames );
		ModCount = modSources.Count;

		if ( ModCount == 0 )
			host.ShowMessage( UiText.T( "ModFileIndex", "There are no conventional or Mod.io mods to scan, nothing to do..." ) );

		worker.DoWork += delegate
		{
			RunScan( worker, modSources, discoveredModIoMods, checkScripts, checkBundles );
		};
		worker.RunWorkerCompleted += completedHandler;
		worker.ProgressChanged += progressHandler;
		worker.RunWorkerAsync();
	}

	private List<ModSource> ConventionalModSources( IReadOnlySet<string> ignoredModNames )
	{
		List<ModSource> sources = Directory.GetDirectories( gamePaths.ModsDirectory, "mod*", SearchOption.TopDirectoryOnly )
			.Where( path => !ignoredModNames.Contains( Path.GetFileName( path ) ) )
			.Select( path => new ModSource(
				Path.GetFileName( path ),
				path,
				Origin: null,
				ModIoId: 0,
				DlcDirectories: Array.Empty<string>() ) )
			.ToList();

		foreach ( string dlcDirectory in DiscoverConventionalDlcDirectories( gamePaths.DlcDirectory ) )
		{
			string dlcName = Path.GetFileName( dlcDirectory );

			if ( !ignoredModNames.Contains( dlcName ) )
			{
				sources.Add( new ModSource(
					dlcName,
					dlcDirectory,
					Origin: null,
					ModIoId: 0,
					DlcDirectories: new[] { dlcDirectory } ) );
			}
		}

		return sources;
	}

	// Ignore rules can use either a Mod.io folder name or its display name.
	private static void AddModIoSources(
		List<ModSource> sources,
		IReadOnlyList<ModIoInstalledMod> discoveredMods,
		IReadOnlySet<string> ignoredModNames )
	{
		foreach ( ModIoInstalledMod mod in SelectActiveModIoMods( discoveredMods ) )
		{
			if ( ignoredModNames.Contains( mod.FolderName ) || ignoredModNames.Contains( mod.DisplayName ) )
				continue;

			sources.Add( new ModSource(
				mod.DisplayName,
				mod.SourceDirectory,
				ModIoDiscovery.OriginName,
				mod.ModId,
				mod.DlcDirectories ) );
		}
	}

	// Reserve the final progress range for the UI work after scanning.
	private void RunScan(
		BackgroundWorker worker,
		IReadOnlyList<ModSource> modSources,
		IReadOnlyList<ModIoInstalledMod> discoveredModIoMods,
		bool checkScripts,
		bool checkBundles )
	{
		// ResetIndex clears this field; keep the previous results available for reuse.
		BlobMergePreflight previousPreflight = MergePreflight;
		var bundleRequests = new List<BundleScanRequest>();
		var content = new ScanContent();
		List<ModScanPlan> modPlans;
		Dictionary<int, BundleScanResult> bundleResults;

		ResetIndex();
		ScriptsScanned = checkScripts;

		modPlans = PlanModScans( worker, modSources, bundleRequests, checkScripts, checkBundles );
		bundleResults = ScanBundles( worker, bundleRequests );
		ReadModContent( worker, modPlans, bundleResults, content, bundleRequests.Count > 0 ? 40 : 10, checkScripts );

		worker.ReportProgress( 80, new ConflictScanProgress( UiText.T( "ModFileIndex", "Comparing script and XML definitions" ) ) );
#if RSBLOB_SEMANTIC_ANALYSIS
		ResolveScopedMemberTypes();
		AddEnumAdditions( content.EnumDefinitions );
#endif
		CompareXmlDefinitions( content.XmlDefinitions );
#if RSBLOB_SEMANTIC_ANALYSIS
		PrepareMergeChecks( worker, discoveredModIoMods, previousPreflight, checkScripts );
#endif
		ScanCompleted = true;
		worker.ReportProgress( 90, new ConflictScanProgress( UiText.T( "MainForm", "Preparing conflict results" ) ) );
	}

	// Collect bundle requests first so the following step can read them together.
	private List<ModScanPlan> PlanModScans(
		BackgroundWorker worker,
		IReadOnlyList<ModSource> modSources,
		List<BundleScanRequest> bundleRequests,
		bool checkScripts,
		bool checkBundles )
	{
		var plans = new List<ModScanPlan>();

		foreach ( ModSource source in modSources )
		{
			string[] allFiles = Directory.GetFiles( source.SourceDirectory, "*", SearchOption.AllDirectories );

			(IReadOnlyList<string> scripts, IReadOnlyList<string> blobs) = SelectLooseScriptInputs( allFiles );

			HashSet<string> dlcBundles = source.DlcDirectories
				.Where( Directory.Exists )
				.SelectMany( dlcDirectory => Directory.GetFiles(
					dlcDirectory,
					"*.bundle",
					SearchOption.AllDirectories ) )
				.ToHashSet( StringComparer.OrdinalIgnoreCase );
			IReadOnlyList<string> bundles = SelectBundledTextBundlePaths( allFiles, dlcBundles );
			var modBundleRequests = new List<BundleScanRequest>();
			var dlcBundleRequests = new List<BundleScanRequest>();
			int enumerationProgress;

			if ( CountsAsActiveSource( source.Name ) && checkScripts )
			{
				ScriptCount += scripts.Count;
				BlobCount += blobs.Count;
			}

			if ( checkBundles )
				AddBundleRequests( source.Name, bundles, dlcBundles, bundleRequests, modBundleRequests, dlcBundleRequests );

			plans.Add( new ModScanPlan( source, scripts, blobs, modBundleRequests, dlcBundleRequests ) );

			enumerationProgress = (int)( (float)plans.Count / Math.Max( modSources.Count, 1 ) * 10f );
			worker.ReportProgress(
				enumerationProgress,
				new ConflictScanProgress( UiText.T( "ModFileIndex", "Finding installed files" ), source.Name ) );
		}

		return plans;
	}

	// A DLC bundle may also appear in the text-bundle list. Reuse its request to avoid reading it twice.
	private static void AddBundleRequests(
		string modName,
		IEnumerable<string> bundles,
		IEnumerable<string> dlcBundles,
		List<BundleScanRequest> allRequests,
		List<BundleScanRequest> modRequests,
		List<BundleScanRequest> dlcRequests )
	{
		var requestsByPath = new Dictionary<string, BundleScanRequest>( StringComparer.OrdinalIgnoreCase );

		foreach ( string bundlePath in bundles )
		{
			var request = new BundleScanRequest( allRequests.Count, modName, bundlePath );

			allRequests.Add( request );
			requestsByPath.Add( bundlePath, request );
			modRequests.Add( request );
		}

		foreach ( string bundlePath in dlcBundles )
		{
			if ( !requestsByPath.TryGetValue( bundlePath, out BundleScanRequest request ) )
			{
				request = new BundleScanRequest( allRequests.Count, modName, bundlePath );
				allRequests.Add( request );
				requestsByPath.Add( bundlePath, request );
			}

			dlcRequests.Add( request );
		}
	}

	private Dictionary<int, BundleScanResult> ScanBundles( BackgroundWorker worker, List<BundleScanRequest> bundleRequests )
	{
		var resultsByOrder = new Dictionary<int, BundleScanResult>();
		object progressLock = new object();
		BundleScanCoordinator scanner;
		IReadOnlyList<BundleScanResult> results;

		if ( bundleRequests.Count == 0 )
			return resultsByOrder;

		scanner = bundleScanner ?? CreateDefaultBundleScanner();

		// Bundle workers can report progress together. Serialize updates sent to the UI worker.
		results = scanner.Scan( bundleRequests, delegate ( int completed, BundleScanRequest request )
		{
			lock ( progressLock )
			{
				int bundleProgress = 10 + (int)( (float)completed / bundleRequests.Count * 30f );

				worker.ReportProgress(
					Math.Min( bundleProgress, 40 ),
					new ConflictScanProgress( UiText.T( "ModFileIndex", "Reading bundle indexes" ), request.ModName ) );
			}
		} );

		BundleCacheHitCount = results.Count( result => result.CacheHit );
		BundleCacheMissCount = results.Count - BundleCacheHitCount;

		foreach ( BundleScanResult result in results )
		{
			resultsByOrder.Add( result.Request.Order, result );

			if ( CountsAsActiveSource( result.Request.ModName ) && countedBundles.Add( result.Request.BundlePath ) )
			{
				BundleCount++;
				XmlCount += CountXmlResources( result.Paths );
			}
		}

		return resultsByOrder;
	}

	private void ReadModContent(
		BackgroundWorker worker,
		IReadOnlyList<ModScanPlan> modPlans,
		IReadOnlyDictionary<int, BundleScanResult> bundleResults,
		ScanContent content,
		int startProgress,
		bool checkScripts )
	{
		string phase = UiText.T( "ModFileIndex", "Reading script and XML definitions" );
		int modNumber = 0;

		worker.ReportProgress( startProgress, new ConflictScanProgress( phase ) );
#if RSBLOB_SEMANTIC_ANALYSIS
		content.IncludeScopedMembers = modPlans.Any( plan => plan.Blobs.Count > 0 );

		if ( checkScripts && content.IncludeScopedMembers )
			ReadDependencyBaseline();

#endif

		foreach ( ModScanPlan plan in modPlans )
		{
			int progress = startProgress + (int)( (float)modNumber++ / Math.Max( modPlans.Count, 1 ) * ( 80 - startProgress ) );

			worker.ReportProgress( progress, new ConflictScanProgress( phase, plan.Source.Name ) );

			if ( checkScripts )
				ReadScriptFiles( plan, content );

			foreach ( BundleScanRequest request in plan.BundleRequests )
				AddModFilesFromPaths( bundleResults[request.Order].Paths, Categories.BundleText, plan.Source, request.BundlePath );

			foreach ( BundleScanRequest request in plan.DlcBundleRequests )
				ReadDlcBundle( request, bundleResults[request.Order], plan.Source, content.XmlDefinitions );
		}
	}

	private void ReadScriptFiles( ModScanPlan plan, ScanContent content )
	{
		AddModFilesFromPaths( plan.Scripts, Categories.Script, plan.Source );

#if RSBLOB_SEMANTIC_ANALYSIS

		foreach ( string scriptPath in plan.Scripts )
			ReadLooseScriptSymbols( scriptPath, plan.Source, content );

		foreach ( string blobPath in plan.Blobs )
			ReadBlobSymbols( blobPath, plan.Source, content );
#else
		AddOpaqueRsBlobs( plan.Blobs, plan.Source );
#endif
	}

#if RSBLOB_SEMANTIC_ANALYSIS
	private void ReadLooseScriptSymbols( string scriptPath, ModSource source, ScanContent content )
	{
		try
		{
			var declaredTypes = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
			var parents = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
			IReadOnlyList<WitcherScriptFunctionSymbol> symbols = WitcherScriptSymbolReader.Read(
				scriptPath,
				declaredTypes,
				includeMembers: content.IncludeScopedMembers,
				memberWarnings: scriptSymbolWarnings,
				typeParents: content.IncludeScopedMembers ? parents : null );
			IReadOnlyList<WitcherScriptFunctionSymbol> vanillaSymbols;

			looseDependencySources.Add( new BlobDependencySource(
				source.Name,
				source.Origin != null,
				new RsBlobDependencyMetadata(
					declaredTypes.ToArray(),
					Array.Empty<RsBlobRequirement>(),
					TypeParents: parents.Select( value => new RsBlobTypeParent( value.Key, value.Value ) ).ToArray() ),
				symbols.Select( symbol => new ScriptSemanticSymbol( symbol.Key, symbol.Kind, symbol.Member ) ).ToArray() ) );

			vanillaSymbols = ReadVanillaSymbols( scriptPath, source, content.VanillaSymbolsByPath, content.IncludeScopedMembers );
			symbols = WitcherScriptSymbolReader.MarkVanillaEquivalent( symbols, vanillaSymbols );
			LooseScriptSymbolCount += symbols.Count;
			AddLooseScriptSymbols( symbols, scriptPath, source );
		}
		catch ( Exception exception ) when ( exception is IOException
			or UnauthorizedAccessException
			or DecoderFallbackException )
		{
			scriptSymbolWarnings.Add( UiText.F( "ModFileIndex", $"{source.Name}: {Path.GetFileName( scriptPath )} was skipped ({exception.Message})" ) );
		}
	}

	private void ReadBlobSymbols( string blobPath, ModSource source, ScanContent content )
	{
		try
		{
			RsBlobSymbolCacheResult result = rsBlobSymbolCache.GetOrCreateIndex( blobPath, () => RsBlobSymbolReader.Read( blobPath ) );

			dependencySources.Add( new BlobDependencySource( source.Name, source.Origin != null, result.Dependencies, result.Symbols ) );
			RecordRsBlobSymbolCacheResult( result );
			BlobSymbolCount += result.Symbols.Count;
			AddRsBlobSymbols( result.Symbols, blobPath, source );

			foreach ( var definition in result.Dependencies?.Enums ?? Array.Empty<RsBlobEnumDefinition>() )
				content.EnumDefinitions.Add( (definition, source) );
		}
		catch ( Exception exception ) when ( exception is IOException
			or UnauthorizedAccessException
			or InvalidDataException
			or DecoderFallbackException
			or OverflowException )
		{
			blobWarnings.Add( UiText.F( "ModFileIndex", $"{source.Name}: {Path.GetFileName( blobPath )} was skipped ({exception.Message})" ) );
		}
	}

#endif

	// Keep the resource list when XML analysis fails; other bundle conflicts can still be found.
	private void ReadDlcBundle(
		BundleScanRequest request,
		BundleScanResult result,
		ModSource source,
		List<XmlOverrideDefinition> definitions )
	{
		AddDlcBundleResources( result.Paths, source, request.BundlePath );

		try
		{
			definitions.AddRange( XmlOverrideConflictAnalyzer.ReadBundle(
				request.BundlePath,
				result.Paths,
				new XmlOverrideSource(
					source.Name,
					request.BundlePath,
					ResourcePath: string.Empty,
					DlcName: string.Empty,
					source.Origin == null ? null : source.SourceDirectory,
					source.Origin,
					source.ModIoId ),
				issue =>
				{
					xmlOverrideWarnings.Add( issue );
					invalidXmlWarnings.Add( issue );
				} ) );
		}
		catch ( Exception exception ) when ( exception is IOException
			or UnauthorizedAccessException
			or InvalidDataException
			or DecoderFallbackException
			or OverflowException )
		{
			xmlOverrideWarnings.Add( UiText.F( "ModFileIndex", $"{source.Name}: {Path.GetFileName( request.BundlePath )} XML overrides were skipped ({exception.Message})" ) );
		}
	}

#if RSBLOB_SEMANTIC_ANALYSIS
	// When vanilla has no definition, infer the enum base only if the active mods agree on its numbered members.
	private void AddEnumAdditions( IEnumerable<(RsBlobEnumDefinition Definition, ModSource Source)> definitions )
	{
		foreach ( var group in definitions.GroupBy( value => value.Definition.Name, StringComparer.Ordinal ) )
		{
			if ( !dependencyEnums.TryGetValue( group.Key, out IReadOnlyDictionary<string, int> baseline ) )
			{
				var anchors = group
					.Where( value => CountsAsActiveSource( value.Source.Name ) )
					.SelectMany( value => value.Definition.Members )
					.Where( member => member.Value != int.MaxValue )
					.GroupBy( member => member.Name, StringComparer.Ordinal )
					.ToArray();

				if ( anchors.Length == 0 || anchors.Any( values => values.Select( member => member.Value ).Distinct().Count() > 1 ) )
					continue;

				baseline = anchors.ToDictionary( values => values.Key, values => values.First().Value, StringComparer.Ordinal );
			}

			foreach ( var value in group )
			{
				GetOrAddSemanticSource( ScriptSymbolKey.Create( "Enums", group.Key ), value.Source ).EnumAdditions
					= EnumConflictAnalysis.AddedMembers( value.Definition, baseline );
			}
		}
	}

#endif

	private void CompareXmlDefinitions( IReadOnlyCollection<XmlOverrideDefinition> definitions )
	{
		XmlItemCatalog vanillaItems = ReadVanillaItems( definitions );

		SetXmlDefinitionDeclarations( definitions, vanillaItems?.Keys, vanillaItems != null );
		AddXmlOverrideConflicts( XmlOverrideConflictAnalyzer.Analyze( definitions, vanillaItems?.Categories ) );
	}

	private XmlItemCatalog ReadVanillaItems( IEnumerable<XmlOverrideDefinition> definitions )
	{
		try
		{
			if ( definitions.Any( definition => definition.Key.DefinitionType == "item" || definition.EquipmentVariants != null ) )
				return VanillaXmlItemDefinitions.ReadCatalog( gamePaths.GameDirectory );
		}
		catch ( Exception exception ) when ( exception is IOException
			or UnauthorizedAccessException
			or InvalidDataException
			or OverflowException
			or DecoderFallbackException
			or System.Xml.XmlException )
		{
			xmlOverrideWarnings.Add( UiText.Format( "ModFileIndex", "XML item definition lookup was skipped: {0}", exception.Message ) );
		}

		return null;
	}

#if RSBLOB_SEMANTIC_ANALYSIS
	private void PrepareMergeChecks(
		BackgroundWorker worker,
		IReadOnlyList<ModIoInstalledMod> discoveredModIoMods,
		BlobMergePreflight previousPreflight,
		bool checkScripts )
	{
		bool hasCompiledConflict = checkScripts && Files.Any( file => file.Category == Categories.PrecompiledScript
			&& file.Mods.Count > 1
			&& file.Mods.Any( source => source.HasPrecompiledScriptDefinition ) );
		CustomLoadOrder order;
		ModLoadSetting[] settings;
		string merged;

		if ( !hasCompiledConflict )
		{
			RecoveredScriptStore.Prune( BlobMergeStore.ForApplication().InventoryPath );

			return;
		}

		order = CurrentLoadOrder();
		settings = order?.Mods.ToArray() ?? Array.Empty<ModLoadSetting>();
		merged = host.Settings.Get( "MergedModName" );
		MergePreflight = BlobMergePreflight.Scan(
			Files,
			gamePaths.ModsDirectory,
			gamePaths.ScriptsDirectory,
			order?.FilePath ?? string.Empty,
			BlobMergeWorkspace.Discover( gamePaths.ModsDirectory, settings, discoveredModIoMods, merged ),
			settings,
			order?.IsValid == true,
			BlobMergeStore.ForApplication().InventoryPath,
			merged,
			previousPreflight,
			preflightCacheDirectory,
			() => worker.ReportProgress(
				85,
				new ConflictScanProgress( UiText.T( "BlobMerge", "Planning precompiled script resolutions" ) ) ),
			( phase, action ) => worker.ReportProgress( 85, new ConflictScanProgress( phase, action ) ) );
	}

#endif

	internal static int CountXmlResources( IEnumerable<string> paths ) =>
		paths.Distinct( StringComparer.OrdinalIgnoreCase ).Count( path => path.EndsWith( ".xml", StringComparison.OrdinalIgnoreCase ) );

	private bool CountsAsActiveSource( string name ) =>
		!name.Equals( host.Settings.Get( "MergedModName" ), StringComparison.OrdinalIgnoreCase )
		&& CurrentLoadOrder()?.Mods.Any( setting => setting.ModName.Equals( name, StringComparison.OrdinalIgnoreCase )
			&& setting.IsEnabled == false ) != true;

	internal static IReadOnlyList<ModIoInstalledMod> SelectActiveModIoMods( IEnumerable<ModIoInstalledMod> mods )
	{
		return ( mods ?? Enumerable.Empty<ModIoInstalledMod>() )
			.Where( mod => mod.IsEnabled == true
				&& mod.Priority.HasValue
				&& Directory.Exists( mod.SourceDirectory ) )
			.ToArray();
	}

	private static BundleScanCoordinator CreateDefaultBundleScanner()
	{
		BundleListingCache cache = new BundleListingCache(
			BundleListingCache.DefaultCacheDirectory );

		return new BundleScanCoordinator( cache, RemasteredBundleFile.ListPaths, BundleScanCoordinator.DefaultWorkerCount );
	}

	private void ResetIndex()
	{
		Files.Clear();
		filesByRelativePath.Clear();
		ScriptCount = 0;
		XmlCount = 0;
		ScanCompleted = false;
		MergePreflight = null;
		countedBundles.Clear();
		BundleCount = 0;
		BlobCount = 0;
		BlobSymbolCount = 0;
		LooseScriptSymbolCount = 0;
		BundleCacheHitCount = 0;
		BundleCacheMissCount = 0;
		RsBlobSymbolCacheHitCount = 0;
		RsBlobSymbolCacheMissCount = 0;
		blobWarnings.Clear();
		scriptSymbolWarnings.Clear();
		xmlOverrideWarnings.Clear();
		invalidXmlWarnings.Clear();
		xmlDefinitionOrderInputs = Array.Empty<ModFile>();
		vanillaXmlItemKeys = new HashSet<string>();
		vanillaXmlItemsKnown = false;
		xmlDefinitionSourceCounts.Clear();
#if RSBLOB_SEMANTIC_ANALYSIS
		dependencySources.Clear();
		looseDependencySources.Clear();
		dependencyTypes.Clear();
		dependencyFunctions.Clear();
		vanillaFilesBySymbol.Clear();
		dependencyEnums.Clear();
		scopedFieldTypes.Clear();
		scopedTypeParents.Clear();
		dependencyBaselineReady = false;
#endif
	}

	internal static IReadOnlyList<string> DiscoverConventionalDlcDirectories( string dlcDirectory )
	{
		if ( string.IsNullOrWhiteSpace( dlcDirectory ) || !Directory.Exists( dlcDirectory ) )
		{
			return Array.Empty<string>();
		}

		return Directory.GetDirectories( dlcDirectory, "*", SearchOption.TopDirectoryOnly )
			.Where( path => !OfficialGameContent.IsOfficialDlcDirectoryName( Path.GetFileName( path ) ) )
			.OrderBy( path => Path.GetFileName( path ), StringComparer.OrdinalIgnoreCase )
			.ToArray();
	}

	internal static IReadOnlyList<string> SelectBundledTextBundlePaths(
		IEnumerable<string> allFiles,
		IEnumerable<string> dlcBundlePaths )
	{
		HashSet<string> dlcBundles = dlcBundlePaths.ToHashSet( StringComparer.OrdinalIgnoreCase );

		return allFiles
			.Where( ModFile.IsBundle )
			.Where( path => !dlcBundles.Contains( path ) )
			.ToArray();
	}


	internal static string GetDlcBundleResourceIndexKey( string resourcePath )
	{
		return "DLC Bundle Resource\0" + resourcePath;
	}

	// Two sources can share a display name. Include their origin when recording who supplies a resource.
	private void AddDlcBundleResources(
		IEnumerable<string> resourcePaths,
		ModSource source,
		string bundlePath )
	{
		foreach ( string resourcePath in resourcePaths )
		{
			string indexKey = GetDlcBundleResourceIndexKey( resourcePath );

			if ( !filesByRelativePath.TryGetValue( indexKey, out ModFile file ) )
			{
				file = new ModFile( resourcePath, bundlePath ) { LoadOrderSource = loadOrder };
				filesByRelativePath.Add( indexKey, file );
				Files.Add( file );
			}

			file.IndexedCategoryOverride = Categories.DlcBundleResource;

			bool alreadyRecorded = file.Mods.Any( indexed =>
				indexed.Name.EqualsIgnoreCase( source.Name )
				&& indexed.ModIoId == source.ModIoId
				&& string.Equals( indexed.Origin, source.Origin, StringComparison.OrdinalIgnoreCase ) );

			if ( alreadyRecorded )
			{
				continue;
			}

			file.Mods.Add( new FileHash
			{
				Name = source.Name,
				BundleName = Path.GetFileName( bundlePath ),
				SourceDirectory = source.Origin == null ? null : source.SourceDirectory,
				SourceFilePath = bundlePath,
				Origin = source.Origin,
				ModIoId = source.ModIoId,
				RuntimeOrderName = source.Name
			} );
		}
	}

	internal void SetXmlDefinitionDeclarations( IEnumerable<XmlOverrideDefinition> definitions,
		IReadOnlySet<string> vanillaKeys = null, bool vanillaKnown = true )
	{
		XmlOverrideDefinition[] declarations = definitions.ToArray();

		vanillaXmlItemKeys = vanillaKeys ?? new HashSet<string>();
		vanillaXmlItemsKnown = vanillaKnown;
		xmlDefinitionSourceCounts = declarations.GroupBy( value => value.Key.ComparisonKey, StringComparer.OrdinalIgnoreCase )
			.ToDictionary( group => group.Key, group => group.Select( value => value.Source.ModName )
				.Distinct( StringComparer.OrdinalIgnoreCase ).Count(), StringComparer.OrdinalIgnoreCase );
		xmlDefinitionOrderInputs = XmlDefinitionOrderDiagnostics.CreateInputs( declarations );

		foreach ( var input in xmlDefinitionOrderInputs )
			foreach ( var source in input.Mods )
				source.XmlOverrideInfo = AddXmlDefinitionContext( source.XmlOverrideInfo );
	}

	private XmlOverrideTooltipInfo AddXmlDefinitionContext( XmlOverrideTooltipInfo info ) => info with
	{
		HasVanillaDefinition = vanillaXmlItemsKnown ? vanillaXmlItemKeys.Contains( info.Key.ComparisonKey ) : null,
		DefinitionSourceCount = xmlDefinitionSourceCounts.TryGetValue( info.Key.ComparisonKey, out int count ) ? count : 0
	};

	internal void AddXmlOverrideConflicts( IEnumerable<XmlOverrideConflict> conflicts )
	{
		foreach ( XmlOverrideConflict conflict in conflicts )
		{
			ModFile file = new ModFile( conflict.DisplayPath, conflict.Sources[0].Source.BundlePath )
			{
				IndexedCategoryOverride = Categories.XmlDefinitionOverride,
				LoadOrderSource = loadOrder
			};

			foreach ( XmlOverrideConflictSource source in conflict.Sources )
			{
				string valueDescription = conflict.IsWholeDefinitionReplacement
					? UiText.F( "ModFileIndex", $"on_conflict={source.ConflictMode}; whole definition participates" )
					: $"{conflict.Parameter.TrimStart( '@' )} = {source.Value}";

				file.Mods.Add( new FileHash
				{
					Name = source.Source.ModName,
					BundleName = Path.GetFileName( source.Source.BundlePath ),
					SourceDirectory = source.Source.SourceDirectory,
					SourceFilePath = source.Source.BundlePath,
					Origin = source.Source.Origin,
					ModIoId = source.Source.ModIoId,
					RuntimeOrderName = source.Source.Origin == null
						&& !string.IsNullOrWhiteSpace( source.Source.DlcName )
						? source.Source.DlcName
						: source.Source.ModName,
					ConflictDetail = $"{valueDescription}\n{string.Join( "\n", source.ResourcePaths )}",
					XmlOverrideInfo = AddXmlDefinitionContext( new XmlOverrideTooltipInfo( conflict.Key, conflict.Parameter, source.Value,
						conflict.IsWholeDefinitionReplacement, source.ConflictModes ?? new[] { source.ConflictMode },
						source.EquipmentVariants, source.DeclarationCount, VariantCompetitions: conflict.VariantCompetitions ) )
				} );
			}

			filesByRelativePath[file.RelativePath] = file;
			Files.Add( file );
		}
	}

#if RSBLOB_SEMANTIC_ANALYSIS

	// Vanilla and official DLC definitions satisfy dependencies without another mod being installed.
	private void ReadDependencyBaseline()
	{
		try
		{
			List<string> scriptRoots = new() { gamePaths.ScriptsDirectory };
			string game = gamePaths.GameDirectory;

			foreach ( var (parent, official) in new (string, Func<string, bool>)[]
			{
				(Path.Combine(game, "content"), OfficialGameContent.IsOfficialContentDirectoryName),
				(Path.Combine(game, "DLC"), OfficialGameContent.IsOfficialDlcDirectoryName)
			} )
			{
				if ( Directory.Exists( parent ) )
					scriptRoots.AddRange( Directory.GetDirectories( parent )
					.Where( path => official( Path.GetFileName( path ) ) )
					.Select( path => Path.Combine( path, "scripts" ) ) );

				if ( Directory.Exists( parent ) )
					scriptRoots.AddRange( Directory.GetDirectories( parent )
					.Where( path => official( Path.GetFileName( path ) ) )
					.Select( path => Path.Combine( path, "content", "scripts" ) ) );
			}

			string[] paths = scriptRoots.Where( Directory.Exists ).Distinct( StringComparer.OrdinalIgnoreCase )
				.SelectMany( root => Directory.EnumerateFiles( root, "*.ws", SearchOption.AllDirectories ) )
				.Distinct( StringComparer.OrdinalIgnoreCase ).ToArray();

			if ( paths.Length == 0 )
				throw new IOException( UiText.T( "BlobDependencies", "Vanilla script declarations are unavailable." ) );

			foreach ( string path in paths )
			{
				var types = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
				var symbols = WitcherScriptSymbolReader.Read( path, types, typeParents: scopedTypeParents );

				dependencyTypes.UnionWith( types );
				dependencyFunctions.UnionWith( symbols.Select( value => value.Key ) );

				foreach ( var symbol in symbols )
				{
					vanillaFilesBySymbol.TryAdd( symbol.Key, path );

					if ( symbol.Member is { IsDefault: false } member )
						scopedFieldTypes.TryAdd( member.Key, member.Type );
				}

				foreach ( string type in types )
					vanillaFilesBySymbol.TryAdd( ScriptSymbolKey.Create( "Types", type ), path );

				try
				{
					foreach ( var item in WSCompiler.Core.Scripts.Standalone.Parser.ParseSource( File.ReadAllText( path ), true )
						.OfType<WSCompiler.Core.Scripts.Standalone.EnumItem>() )
					{
						vanillaFilesBySymbol.TryAdd( ScriptSymbolKey.Create( "Enums", item.Decl.Name ), path );

						int next = 0;
						var members = new Dictionary<string, int>( StringComparer.Ordinal );

						foreach ( var member in item.Decl.Members )
						{
							int number = member.Value is { } value ? unchecked((int)value) : next;

							members[member.Name] = number;
							next = unchecked(number + 1);
						}

						dependencyEnums[item.Decl.Name] = members;
					}
				}
				catch ( WSCompiler.Core.Scripts.Standalone.ScriptSyntaxException ) { }
			}

			foreach ( var definition in WSCompiler.Core.Scripts.Standalone.Rtti.LoadBuiltIn().Enums )
				dependencyEnums.TryAdd( definition.Name, definition.Options.GroupBy( member => member.Name, StringComparer.Ordinal )
					.ToDictionary( values => values.Key, values => values.First().Value, StringComparer.Ordinal ) );

			dependencyBaselineReady = true;
		}
		catch ( Exception exception ) when ( exception is IOException or UnauthorizedAccessException or DecoderFallbackException )
		{
			scriptSymbolWarnings.Add( UiText.Format( "BlobDependencies", "Blob dependency checks were skipped: {0}", exception.Message ) );
		}
	}

	private void RecordRsBlobSymbolCacheResult( RsBlobSymbolCacheResult result )
	{
		if ( result.CacheHit )
		{
			RsBlobSymbolCacheHitCount++;
		}
		else
		{
			RsBlobSymbolCacheMissCount++;
		}
	}

	private void AddRsBlobSymbols( IEnumerable<ScriptSemanticSymbol> symbols, string blobPath, ModSource source )
	{
		foreach ( ScriptSemanticSymbol symbol in symbols )
		{
			FileHash indexedSource = GetOrAddSemanticSource( symbol.Key, source );

			indexedSource.SourceFilePath = blobPath;
			indexedSource.PrecompiledScriptKinds |= symbol.Kind;
			indexedSource.ScopedMember = symbol.Member;
		}
	}

#else
	// Builds without symbol analysis can only report blob conflicts at file level.
	private void AddOpaqueRsBlobs(IEnumerable<string> blobPaths, ModSource source)
	{
		string blobPath = blobPaths.FirstOrDefault();
		if (string.IsNullOrWhiteSpace(blobPath))
		{
			return;
		}

		const string conflictKey = "precompiled.rsblob";
		if (!filesByRelativePath.TryGetValue(conflictKey, out ModFile conflict))
		{
			conflict = new ModFile(conflictKey)
			{
				IndexedCategoryOverride = Categories.PrecompiledScript,
				LoadOrderSource = loadOrder
			};
			filesByRelativePath.Add(conflictKey, conflict);
			Files.Add(conflict);
		}

		if (!conflict.ContainsMod(source.Name))
		{
			conflict.Mods.Add(new FileHash
			{
				Name = source.Name,
				SourceDirectory = source.Origin == null ? null : source.SourceDirectory,
				SourceFilePath = blobPath,
				Origin = source.Origin,
				ModIoId = source.ModIoId
			});
		}
	}
#endif

	// Treat a mod's symbol as unchanged only when every loose declaration of it matches vanilla.
	private void AddLooseScriptSymbols(
		IEnumerable<WitcherScriptFunctionSymbol> symbols,
		string scriptPath,
		ModSource source )
	{
		foreach ( WitcherScriptFunctionSymbol symbol in symbols )
		{
			FileHash indexedSource = GetOrAddSemanticSource( symbol.Key, source );
			bool alreadyHadLooseDefinition = indexedSource.HasLooseScriptDefinition;

			if ( !indexedSource.HasPrecompiledScriptDefinition
				&& string.IsNullOrWhiteSpace( indexedSource.SourceFilePath ) )
			{
				indexedSource.SourceFilePath = scriptPath;
			}

			indexedSource.LooseScriptKinds |= symbol.Kind;
			indexedSource.ScopedMember = symbol.Member;
			indexedSource.IsLooseScriptVanillaEquivalent = alreadyHadLooseDefinition
				? indexedSource.IsLooseScriptVanillaEquivalent && symbol.IsVanillaEquivalent
				: symbol.IsVanillaEquivalent;
		}
	}

	private IReadOnlyList<WitcherScriptFunctionSymbol> ReadVanillaSymbols(
		string scriptPath,
		ModSource source,
		IDictionary<string, IReadOnlyList<WitcherScriptFunctionSymbol>> cache, bool includeMembers )
	{
		string sourceScriptsRoot = Path.GetFullPath( Path.Combine( source.SourceDirectory, GamePaths.ModScriptBase ) );
		string relativePath = Path.GetRelativePath( sourceScriptsRoot, Path.GetFullPath( scriptPath ) );

		if ( Path.IsPathRooted( relativePath )
			|| relativePath.Equals( "..", StringComparison.Ordinal )
			|| relativePath.StartsWith( ".." + Path.DirectorySeparatorChar, StringComparison.Ordinal )
			|| relativePath.StartsWith( ".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal ) )
		{
			return Array.Empty<WitcherScriptFunctionSymbol>();
		}

		string vanillaPath = Path.GetFullPath( Path.Combine( gamePaths.ScriptsDirectory, relativePath ) );

		if ( !File.Exists( vanillaPath ) )
		{
			return Array.Empty<WitcherScriptFunctionSymbol>();
		}

		if ( !cache.TryGetValue( vanillaPath, out IReadOnlyList<WitcherScriptFunctionSymbol> symbols ) )
		{
			try
			{
				symbols = WitcherScriptSymbolReader.Read( vanillaPath, includeMembers: includeMembers );
			}
			catch ( Exception exception ) when ( exception is IOException
				or UnauthorizedAccessException
				or DecoderFallbackException )
			{
				return Array.Empty<WitcherScriptFunctionSymbol>();
			}

			cache.Add( vanillaPath, symbols );
		}

		return symbols;
	}

	private FileHash GetOrAddSemanticSource( string key, ModSource source )
	{
		if ( !filesByRelativePath.TryGetValue( key, out ModFile existingFile ) )
		{
			existingFile = new ModFile( key )
			{
				IndexedCategoryOverride = Categories.PrecompiledScript,
				LoadOrderSource = loadOrder
			};
			filesByRelativePath.Add( key, existingFile );
			Files.Add( existingFile );
		}

		existingFile.VanillaScriptFile = vanillaFilesBySymbol.GetValueOrDefault( key );

		FileHash indexedSource = existingFile.Mods.FirstOrDefault( mod => mod.Name.EqualsIgnoreCase( source.Name ) );

		if ( indexedSource != null )
		{
			return indexedSource;
		}

		indexedSource = new FileHash
		{
			Name = source.Name,
			SourceDirectory = source.Origin == null ? null : source.SourceDirectory,
			Origin = source.Origin,
			ModIoId = source.ModIoId
		};
		existingFile.Mods.Add( indexedSource );

		return indexedSource;
	}

	private void AddModFilesFromPaths( IEnumerable<string> filePaths, ModFileCategory category, ModSource source, string bundlePath = null )
	{
		foreach ( string filePath in filePaths )
		{
			string sourceBundleName = bundlePath == null ? null : Path.GetFileName( bundlePath );
			string relativePath;

			if ( category == Categories.Script )
			{
				relativePath = Path.GetRelativePath( Path.Combine( source.SourceDirectory, GamePaths.ModScriptBase ), filePath );
			}
			else if ( category == Categories.BundleText )
			{
				relativePath = filePath;
			}
			else
			{
				throw new NotImplementedException();
			}

			if ( !filesByRelativePath.TryGetValue( relativePath, out ModFile existingFile ) )
			{
				existingFile = bundlePath != null ? new ModFile( relativePath, bundlePath ) : new ModFile( relativePath );
				existingFile.LoadOrderSource = loadOrder;
				filesByRelativePath.Add( relativePath, existingFile );
				Files.Add( existingFile );
			}

			existingFile.Mods.Add( new FileHash
			{
				Name = source.Name,
				BundleName = sourceBundleName,
				SourceDirectory = source.Origin == null ? null : source.SourceDirectory,
				Origin = source.Origin,
				ModIoId = source.ModIoId
			} );
		}
	}

	private IEnumerable<string> GetIgnoredModNames()
	{
		return from name in host.Settings.Get( "IgnoreModNames" ).Split( ',' )
			   where !string.IsNullOrWhiteSpace( name )
			   select name.Trim();
	}

	private sealed class ScanContent
	{
		internal readonly List<XmlOverrideDefinition> XmlDefinitions = new();
#if RSBLOB_SEMANTIC_ANALYSIS
		internal readonly Dictionary<string, IReadOnlyList<WitcherScriptFunctionSymbol>> VanillaSymbolsByPath = new( StringComparer.OrdinalIgnoreCase );
		internal readonly List<(RsBlobEnumDefinition Definition, ModSource Source)> EnumDefinitions = new();

		// Loose class members need comparison only when compiled mods are also present.
		internal bool IncludeScopedMembers;
#endif
	}

	private sealed record ModScanPlan(
		ModSource Source,
		IReadOnlyList<string> Scripts,
		IReadOnlyList<string> Blobs,
		IReadOnlyList<BundleScanRequest> BundleRequests,
		IReadOnlyList<BundleScanRequest> DlcBundleRequests );

	private sealed record ModSource(
		string Name,
		string SourceDirectory,
		string Origin,
		long ModIoId,
		IReadOnlyList<string> DlcDirectories );
}
