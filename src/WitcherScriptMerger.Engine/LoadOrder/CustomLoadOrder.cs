using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Timers;
using WitcherScriptMerger.FileIndex;
using WitcherScriptMerger.Inventory;

namespace WitcherScriptMerger.LoadOrder;

// Combines conventional and Mod.io load settings. Methods ending in ForSettings let callers evaluate a captured order.
internal class CustomLoadOrder
{
	public const int TopPriority = 0;

	public const int BottomPriority = 9999;

	private readonly FileSystemWatcher LoadOrderWatcher = new FileSystemWatcher();

	private System.Timers.Timer LoadOrderTimer;

	private List<ModLoadSetting> _mods = new List<ModLoadSetting>();

	private ModIoDiscovery modIoDiscovery;

	private readonly bool usesConfiguredLocation;

	private string LoadOrderDirectory;

	private readonly IEngineHost host;

	private static readonly string LoadOrderFileName = "mods.settings";

	public string FilePath => string.IsNullOrWhiteSpace( LoadOrderDirectory ) ? string.Empty : Path.Combine( LoadOrderDirectory, LoadOrderFileName );

	public IReadOnlyList<ModLoadSetting> Mods
	{
		get
		{
			return _mods;
		}
		private set
		{
			_mods = value.OrderBy( setting => setting, new LoadOrderComparer() ).ToList();
		}
	}

	public bool IsValid { get; private set; }

	internal CustomLoadOrder( IEngineHost host )
		: this( host, ModIoDiscovery.ForSettings( host.Settings ), true )
	{
	}

	internal CustomLoadOrder(
		IEngineHost host,
		ModIoDiscovery modIoDiscovery,
		bool usesConfiguredLocation = false )
	{
		this.host = host;
		LoadOrderDirectory = GameSettingsLocation.DirectoryPath( host.Settings );
		this.modIoDiscovery = modIoDiscovery;
		this.usesConfiguredLocation = usesConfiguredLocation;

		Refresh();
		SetupLoadOrderWatcher();
	}

	private void SetupLoadOrderWatcher()
	{
		if ( Directory.Exists( LoadOrderDirectory ) )
			LoadOrderWatcher.Path = LoadOrderDirectory;

		LoadOrderWatcher.Filter = LoadOrderFileName;
		LoadOrderWatcher.Changed += LoadOrderWatcher_Changed;
		LoadOrderWatcher.EnableRaisingEvents = Directory.Exists( LoadOrderDirectory );
		LoadOrderTimer = new System.Timers.Timer( 1000.0 )
		{
			AutoReset = false
		};
		LoadOrderTimer.Elapsed += LoadOrderTimer_Elapsed;
	}

	internal void ReloadConfiguredLocation()
	{
		if ( !usesConfiguredLocation )
			return;

		LoadOrderTimer?.Stop();
		LoadOrderWatcher.EnableRaisingEvents = false;
		LoadOrderDirectory = GameSettingsLocation.DirectoryPath( host.Settings );
		modIoDiscovery = ModIoDiscovery.ForSettings( host.Settings );

		if ( Directory.Exists( LoadOrderDirectory ) )
		{
			LoadOrderWatcher.Path = LoadOrderDirectory;
			LoadOrderWatcher.EnableRaisingEvents = Directory.Exists( LoadOrderDirectory );
		}

		Refresh();
	}

	private void LoadOrderTimer_Elapsed( object sender, ElapsedEventArgs e )
	{
		Refresh();
		host.LoadOrderFileChanged();
	}

	private void LoadOrderWatcher_Changed( object sender, FileSystemEventArgs e )
	{
		LoadOrderTimer.Stop();
		LoadOrderTimer.Start();
	}

	// A missing file means no overrides. A failed read keeps the last good list for display, but marks the order invalid.
	internal void Refresh( bool keepVortexKey = false )
	{
		IsValid = false;

		try
		{
			// Read directly so access failures remain distinguishable from a missing file.
			if ( string.IsNullOrWhiteSpace( FilePath ) )
			{
				Mods = Array.Empty<ModLoadSetting>();
				IsValid = true;
			}
			else
				IsValid = ParseFileToMods( FilePath, keepVortexKey );
		}
		catch ( Exception error ) when ( error is FileNotFoundException or DirectoryNotFoundException )
		{
			Mods = Array.Empty<ModLoadSetting>();
			IsValid = true;
		}
		catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException )
		{
			host.ShowWarning(
				UiText.Format( "CustomLoadOrder", "Could not read mods.settings.\n\n{0}", error.Message ),
				UiText.T( "CustomLoadOrder", "Invalid Load Order File" ) );
		}

		if ( IsValid )
			AppendExternalSettings();
	}

	// The displayed list may come from an earlier read. Reread before writing to avoid replacing unreadable settings.
	private bool RefreshBeforeChange( bool keepVortexKey = false )
	{
		Refresh( keepVortexKey );

		return IsValid;
	}

	private void AppendExternalSettings()
	{
		IEnumerable<ModLoadSetting> external = modIoDiscovery.Discover().Select( mod => new ModLoadSetting
		{
			ModName = mod.DisplayName,
			IsEnabled = mod.IsEnabled,
			Priority = mod.Priority,
			IsExternal = true,
			StateKnown = mod.IsEnabled.HasValue,
			Origin = ModIoDiscovery.OriginName,
			ModIoId = mod.ModId
		} );

		Mods = Mods.Where( setting => !setting.IsExternal ).Concat( external ).ToList();
	}

	public bool ParseFileToMods( string filePath, bool keepVortexKey )
	{
		var mods = ParseSettings( File.ReadAllLines( filePath ), keepVortexKey, ShowWarningForMalformedFile );

		if ( mods is null )
			return false;

		Mods = mods;

		return true;
	}

	// Parse the same bytes included in the Inspector launch hash.
	internal static IReadOnlyList<ModLoadSetting> ParseSettings(
		IEnumerable<string> lines, bool keepVortexKey, Action<string> warn )
	{
		List<ModLoadSetting> mods = new List<ModLoadSetting>();
		ModLoadSetting currentMod = null;

		foreach ( string line in lines )
		{
			string trimmedLine = line.Trim();

			if ( string.IsNullOrEmpty( trimmedLine ) )
			{
				continue;
			}

			if ( trimmedLine.StartsWith( "[" ) && trimmedLine.EndsWith( "]" ) )
			{
				if ( currentMod != null )
				{
					if ( !currentMod.IsEnabled.HasValue )
					{
						warn( UiText.Format( "CustomLoadOrder", "{0} settings are incomplete. 'Enabled' is required.", currentMod.ModName ) );

						return null;
					}

					mods.Add( currentMod );
				}

				currentMod = new ModLoadSetting( trimmedLine.TrimStart( '[' ).TrimEnd( ']' ) );

				continue;
			}

			string[] parts = trimmedLine.Split( '=' );

			if ( parts.Length != 2 || currentMod is null )
			{
				warn( UiText.Format( "CustomLoadOrder", "Unrecognized setting or value for mod {0},on line:\n\n{1}", currentMod?.ModName, line ) );

				return null;
			}

			string key = parts[0];
			string value = parts[1];

			if ( key.Equals( "Priority", StringComparison.OrdinalIgnoreCase ) )
			{
				if ( string.IsNullOrWhiteSpace( value ) )
				{
					currentMod.Priority = null;

					continue;
				}

				if ( !int.TryParse( value, out var priority ) || 0 > priority || priority > 9999 )
				{
					warn( UiText.F( "CustomLoadOrder", $"The priority for mod {currentMod.ModName} isn't within the valid range of {0} to {9999}:\n\n{line}" ) );

					return null;
				}

				currentMod.Priority = priority;
			}
			else if ( key.Equals( "Enabled", StringComparison.OrdinalIgnoreCase ) )
			{
				if ( !value.Equals( "0" ) && !value.Equals( "1" ) )
				{
					warn( UiText.Format( "CustomLoadOrder", "The 'Enabled' setting for mod {0} isn't within the valid range of 0 or 1:\n\n{1}", currentMod?.ModName, line ) );

					return null;
				}

				currentMod.IsEnabled = value.Equals( "1" );
			}
			else
			{
				if ( !key.Equals( "VK", StringComparison.OrdinalIgnoreCase ) )
				{
					warn( UiText.Format( "CustomLoadOrder", "Unrecognized setting for mod {0},on line:\n\n{1}", currentMod?.ModName, line ) );

					return null;
				}

				if ( keepVortexKey )
				{
					currentMod.VK = value;
				}
			}
		}

		if ( currentMod != null )
		{
			if ( !currentMod.IsEnabled.HasValue )
			{
				warn( UiText.Format( "CustomLoadOrder", "{0} settings are incomplete. 'Enabled' is required.", currentMod.ModName ) );

				return null;
			}

			mods.Add( currentMod );
		}

		return mods;
	}

	private void ShowWarningForMalformedFile( string reason )
	{
		host.ShowWarning(
			UiText.Format( "CustomLoadOrder", "Your mods.settings file is invalid.\n\n{0}", reason ),
			UiText.T( "CustomLoadOrder", "Invalid Load Order File" ) );
	}

	// Keep Vortex's key when saving. Mod.io settings belong in their own file.
	public void Save()
	{
		StringBuilder builder = new StringBuilder();
		List<ModLoadSetting> writableSettings = Mods.Where( setting => !setting.IsExternal ).ToList();

		foreach ( ModLoadSetting modSetting in writableSettings )
		{
			builder.Append( '[' ).Append( modSetting.ModName ).AppendLine( "]" )
				.Append( "Enabled=" )
				.AppendLine( Convert.ToInt32( modSetting.IsEnabled ).ToString() );

			if ( modSetting.Priority.HasValue )
				builder.Append( "Priority=" ).AppendLine( modSetting.Priority.Value.ToString() );

			if ( !string.IsNullOrEmpty( modSetting.VK ) )
			{
				builder.Append( "VK=" ).AppendLine( modSetting.VK );
			}

			if ( modSetting != writableSettings.Last() )
			{
				builder.AppendLine();
			}
		}

		LoadOrderWatcher.EnableRaisingEvents = false;

		string tempPath = FilePath + ".tmp";
		string backupPath = FilePath + ".bak";

		try
		{
			if ( !Directory.Exists( LoadOrderDirectory ) )
				throw new DirectoryNotFoundException( UiText.T( "CustomLoadOrder", "Game settings folder is unavailable. Select it in Menu > Options." ) );

			File.WriteAllText( tempPath, builder.ToString() );

			if ( File.Exists( FilePath ) )
			{
				File.Replace( tempPath, FilePath, backupPath, ignoreMetadataErrors: true );

				if ( File.Exists( backupPath ) )
				{
					File.Delete( backupPath );
				}
			}
			else
			{
				File.Move( tempPath, FilePath );
			}
		}
		finally
		{
			try
			{
				if ( File.Exists( tempPath ) )
				{
					File.Delete( tempPath );
				}
			}
			finally
			{
				LoadOrderWatcher.EnableRaisingEvents = true;
			}
		}
	}

	public void AddMergedModIfMissingAndSave()
	{
		string mergedModName = host.RetrieveMergedModName();

		if ( Mods.All( ( ModLoadSetting setting ) => !setting.ModName.EqualsIgnoreCase( mergedModName ) ) )
		{
			AddMod( new ModLoadSetting
			{
				ModName = mergedModName,
				IsEnabled = true,
				Priority = 0
			} );
		}

		Save();
	}

	public bool HasResolvedConflict( IEnumerable<string> modNames )
	{
		return HasResolvedConflictForSettings( Mods, modNames, modIoDominatesConventional: false );
	}

	public bool HasResolvedConflict( ModFile conflict )
	{
		if ( conflict == null )
			return false;

		if ( Categories.UsesDlcRuntimeOrder( conflict.Category ) )
		{
			return ResolveXmlOverrideConflictForSettings( Mods, conflict ).Resolved;
		}

		if ( ReferenceEquals( conflict.Category, Categories.PrecompiledScript )
			&& conflict.Mods.Any( source => source.HasPrecompiledScriptDefinition || source.HasLooseScriptDefinition ) )
		{
			return ResolvePrecompiledConflictForSettings( Mods, conflict ).Resolved;
		}

		return HasResolvedConflictForSettings(
			Mods,
			conflict.Mods.Select( mod => mod.Name ),
			UsesVerifiedModIoBundlePrecedence( conflict ) );
	}

	public bool ContainsMod( string modName )
	{
		return Mods.Any( ( ModLoadSetting setting ) => setting.ModName.EqualsIgnoreCase( modName ) );
	}

	public ModLoadSetting GetTopPriorityEnabledMod()
	{
		return Mods.Where( setting => !setting.IsExternal )
			.OrderBy( ( ModLoadSetting setting ) => setting, new LoadOrderComparer() ).FirstOrDefault();
	}

	public string GetTopPriorityEnabledMod( IEnumerable<string> conflictMods )
	{
		return GetTopPriorityEnabledModForSettings( Mods, conflictMods, modIoDominatesConventional: false );
	}

	public string GetTopPriorityEnabledMod( ModFile conflict )
	{
		if ( conflict == null )
			return null;

		if ( Categories.UsesDlcRuntimeOrder( conflict.Category ) )
		{
			return ResolveXmlOverrideConflictForSettings( Mods, conflict ).Winner;
		}

		if ( ReferenceEquals( conflict.Category, Categories.PrecompiledScript )
			&& conflict.Mods.Any( source => source.HasPrecompiledScriptDefinition || source.HasLooseScriptDefinition ) )
		{
			return ResolvePrecompiledConflictForSettings( Mods, conflict ).Winner;
		}

		return GetTopPriorityEnabledModForSettings(
			Mods,
			conflict.Mods.Select( mod => mod.Name ),
			UsesVerifiedModIoBundlePrecedence( conflict ) );
	}

	internal static bool UsesVerifiedModIoBundlePrecedence( ModFile conflict )
	{
		return conflict?.IsBundleContent == true
			&& !Path.GetFileName( conflict.RelativePath ).Equals( "precompiled.rsblob", StringComparison.OrdinalIgnoreCase );
	}

	internal static (bool Resolved, string Winner) ResolveXmlOverrideConflictForSettings(
		IEnumerable<ModLoadSetting> allSettings,
		ModFile conflict )
	{
		XmlOverrideResolution resolution = ResolveXmlOverridePolicyForSettings( allSettings, conflict );

		return (resolution.Resolved, resolution.Winner);
	}

	internal static XmlOverrideResolution ResolveXmlOverridePolicyForSettings(
		IEnumerable<ModLoadSetting> allSettings,
		ModFile conflict )
	{
		ModLoadSetting[] settings = allSettings?.ToArray() ?? Array.Empty<ModLoadSetting>();
		bool isDefinition;
		FileHash[] priorityOrder;

		if ( conflict == null || !TryGetEnabledSources( settings, conflict.Mods, out FileHash[] enabledSources ) )
			return UnresolvedXml();

		if ( enabledSources.Length == 0 )
			return new( true, null, XmlOverrideOutcome.NoEnabledSource );

		if ( enabledSources.Length == 1 )
			return ResolveSingleXmlSource( conflict, enabledSources[0] );

		isDefinition = ReferenceEquals( conflict.Category, Categories.XmlDefinitionOverride );

		if ( isDefinition && enabledSources[0].XmlOverrideInfo?.Parameter == "variants" )
			return ResolveXmlVariantWinner( settings, conflict );

		priorityOrder = enabledSources
			.OrderBy(
				source => source,
				Comparer<FileHash>.Create( ( left, right ) => CompareXmlOverrideSourcesForSettings( settings, left, right ) ) )
			.ToArray();

		// External sources leave this tie uncertain.
		if ( HasExternalXmlSource( settings, enabledSources )
			&& priorityOrder.Skip( 1 ).Any( source => CompareXmlOverrideSourcesForSettings( settings, priorityOrder[0], source ) == 0 ) )
		{
			return UnresolvedXml();
		}

		if ( !isDefinition )
			return new( true, priorityOrder[0].Name, XmlOverrideOutcome.RuntimeOrder );

		return ResolveItemParameterPolicies( settings, enabledSources, priorityOrder );
	}

	private static XmlOverrideResolution UnresolvedXml()
	{
		return new( false, null, XmlOverrideOutcome.Unresolved );
	}

	private static bool HasExternalXmlSource( IReadOnlyList<ModLoadSetting> settings, IEnumerable<FileHash> sources )
	{
		return sources.Any( source => source.IsExternal
			|| settings.Any( setting => setting.IsExternal && setting.ModName.EqualsIgnoreCase( source.Name ) ) );
	}

	// Even a single enabled extension needs an existing definition to extend.
	private static XmlOverrideResolution ResolveSingleXmlSource( ModFile conflict, FileHash active )
	{
		XmlOverrideTooltipInfo info = active.XmlOverrideInfo;

		if ( ReferenceEquals( conflict.Category, Categories.XmlDefinitionOverride ) && info?.Key.DefinitionType == "item"
			&& info.Parameter.StartsWith( "@", StringComparison.Ordinal ) && !info.IsWholeDefinitionConflict
			&& info.Policies.Count == 1 && info.Policies[0] is "default" or "extend" )
		{
			if ( info.HasVanillaDefinition == null || info.DefinitionSourceCount > conflict.Mods.Count )
				return UnresolvedXml();

			if ( info.HasVanillaDefinition == true )
			{
				return info.Policies[0] == "default"
					? new( true, null, XmlOverrideOutcome.VanillaDefaultRetained )
					: new( true, active.Name, XmlOverrideOutcome.ExistingDefinitionExtended, ExtensionSource: active.Name );
			}

			if ( info.Policies[0] == "extend" && conflict.Mods.Count == 2 )
			{
				FileHash disabledDefault = conflict.Mods.FirstOrDefault( source => source != active
					&& source.XmlOverrideInfo?.Policies.SequenceEqual( new[] { "default" } ) == true );

				if ( disabledDefault != null )
					return new( false, null, XmlOverrideOutcome.DisabledDefaultDefinition, disabledDefault.Name, active.Name );
			}
		}

		return new( true, active.Name, XmlOverrideOutcome.SingleSource );
	}

	// A winner must beat every other participant; some sets of competing rules have no single winner.
	private static XmlOverrideResolution ResolveXmlVariantWinner( IEnumerable<ModLoadSetting> settings, ModFile conflict )
	{
		IReadOnlyList<XmlVariantResolution> pairs = ResolveXmlVariantCompetitionsForSettings( settings, conflict );
		FileHash[] participants;
		FileHash winner;

		if ( pairs == null || pairs.Any( pair => pair.Winner == null ) )
			return UnresolvedXml();

		participants = pairs.SelectMany( pair => new[] { pair.Left, pair.Right } ).Distinct().ToArray();
		winner = participants.SingleOrDefault( candidate => participants.All( other => other == candidate
			|| pairs.Any( pair => pair.Winner == candidate && ( pair.Left == other || pair.Right == other ) ) ) );

		return new( true, winner?.Name, XmlOverrideOutcome.EquipmentVariants );
	}

	private static bool AreComparableItemParameters( IEnumerable<FileHash> enabledSources, XmlOverrideTooltipInfo first )
	{
		if ( first == null || first.IsWholeDefinitionConflict || first.Key.DefinitionType != "item"
			|| !first.Parameter.StartsWith( "@", StringComparison.Ordinal ) )
		{
			return false;
		}

		return !enabledSources.Any( source => source.XmlOverrideInfo == null
			|| source.XmlOverrideInfo.Key != first.Key || source.XmlOverrideInfo.Parameter != first.Parameter
			|| source.XmlOverrideInfo.IsWholeDefinitionConflict || source.XmlOverrideInfo.DeclarationCount != 1
			|| source.XmlOverrideInfo.Policies.Count != 1
			|| source.XmlOverrideInfo.Policies[0] is not ( "default" or "extend" ) );
	}

	// Extensions apply after the definition they extend. External sources leave that order uncertain unless all sources extend it.
	private static XmlOverrideResolution ResolveItemParameterPolicies(
		IReadOnlyList<ModLoadSetting> settings,
		FileHash[] enabledSources,
		FileHash[] priorityOrder )
	{
		bool allExtend;
		bool hasVanillaDefinition;
		FileHash[] registrationOrder;
		FileHash defaultSource;
		FileHash extensionSource;

		if ( !AreComparableItemParameters( enabledSources, enabledSources[0].XmlOverrideInfo ) )
			return UnresolvedXml();

		allExtend = enabledSources.All( source => source.XmlOverrideInfo.Policies[0] == "extend" );

		if ( HasExternalXmlSource( settings, enabledSources ) )
			return allExtend ? new( true, priorityOrder[0].Name, XmlOverrideOutcome.RuntimeOrder ) : UnresolvedXml();

		if ( enabledSources.Select( source => source.RuntimeOrderName ?? source.Name ).Distinct( StringComparer.OrdinalIgnoreCase ).Count() != enabledSources.Length )
			return UnresolvedXml();

		// Conventional XML registration follows internal DLC names in ascending order.
		registrationOrder = Enumerable.Reverse( priorityOrder ).ToArray();

		if ( allExtend )
			return new( true, priorityOrder[0].Name, XmlOverrideOutcome.Extended );

		if ( enabledSources.Any( source => source.XmlOverrideInfo.HasVanillaDefinition == null
			|| source.XmlOverrideInfo.DefinitionSourceCount > 0 && source.XmlOverrideInfo.DefinitionSourceCount != enabledSources.Length ) )
		{
			return UnresolvedXml();
		}

		hasVanillaDefinition = enabledSources.Any( source => source.XmlOverrideInfo.HasVanillaDefinition == true );

		if ( enabledSources.All( source => source.XmlOverrideInfo.Policies[0] == "default" ) )
		{
			return hasVanillaDefinition
				? new( true, null, XmlOverrideOutcome.VanillaDefaultRetained )
				: new( true, registrationOrder[0].Name, XmlOverrideOutcome.DefaultRetained, DefaultSource: registrationOrder[0].Name );
		}

		if ( registrationOrder.Length != 2 )
			return UnresolvedXml();

		defaultSource = registrationOrder.Single( source => source.XmlOverrideInfo.Policies[0] == "default" );
		extensionSource = registrationOrder.Single( source => source.XmlOverrideInfo.Policies[0] == "extend" );

		if ( hasVanillaDefinition )
			return new( true, extensionSource.Name, XmlOverrideOutcome.ExistingDefinitionExtended, ExtensionSource: extensionSource.Name );

		return registrationOrder[0] == defaultSource
			? new( true, extensionSource.Name, XmlOverrideOutcome.Extended, defaultSource.Name, extensionSource.Name )
			: new( false, null, XmlOverrideOutcome.ExtensionBeforeDefault, defaultSource.Name, extensionSource.Name );
	}

	// Each pair of variant rules can have a different outcome.
	internal static IReadOnlyList<XmlVariantResolution> ResolveXmlVariantCompetitionsForSettings(
		IEnumerable<ModLoadSetting> allSettings, ModFile conflict )
	{
		var competitions = conflict?.Mods.FirstOrDefault()?.XmlOverrideInfo?.VariantCompetitions;

		if ( competitions == null )
			return null;

		ModLoadSetting[] settings = allSettings?.ToArray() ?? Array.Empty<ModLoadSetting>();

		ModLoadSetting Setting( FileHash source ) => settings.FirstOrDefault( value => value.ModName.EqualsIgnoreCase( source.Name ) );

		bool External( FileHash source ) => source.IsExternal || Setting( source )?.IsExternal == true;

		bool Known( FileHash source ) => Setting( source ) is { } setting ? HasKnownState( setting ) : !External( source );

		var results = new List<XmlVariantResolution>();

		foreach ( var pair in competitions )
		{
			FileHash left = conflict.Mods.FirstOrDefault( value => value.Name.EqualsIgnoreCase( pair.LeftMod ) );
			FileHash right = conflict.Mods.FirstOrDefault( value => value.Name.EqualsIgnoreCase( pair.RightMod ) );

			if ( left == null || right == null )
				return null;

			if ( Setting( left ) is { StateKnown: true, IsEnabled: false }
				|| Setting( right ) is { StateKnown: true, IsEnabled: false } )
				continue;

			bool fixedOrder = !External( left ) && !External( right );
			bool knownOrder = Known( left ) && Known( right ) && ( !fixedOrder
				|| !string.Equals(
					left.RuntimeOrderName ?? left.Name,
					right.RuntimeOrderName ?? right.Name,
					StringComparison.OrdinalIgnoreCase ) );
			int comparison = knownOrder ? CompareXmlOverrideSourcesForSettings( settings, left, right ) : 0;

			results.Add( new( left, right, comparison == 0 ? null : comparison < 0 ? left : right, fixedOrder ) );
		}

		return results;
	}

	internal static int CompareXmlOverrideSourcesForSettings(
		IEnumerable<ModLoadSetting> allSettings,
		FileHash x,
		FileHash y )
	{
		ModLoadSetting[] settings = allSettings?.ToArray() ?? Array.Empty<ModLoadSetting>();
		ModLoadSetting xSetting = settings.FirstOrDefault( setting => setting.ModName.EqualsIgnoreCase( x?.Name ) );
		ModLoadSetting ySetting = settings.FirstOrDefault( setting => setting.ModName.EqualsIgnoreCase( y?.Name ) );
		bool xDisabled = xSetting?.StateKnown == true && xSetting.IsEnabled == false;
		bool yDisabled = ySetting?.StateKnown == true && ySetting.IsEnabled == false;

		if ( xDisabled != yDisabled )
		{
			return xDisabled ? 1 : -1;
		}

		bool xExternal = x?.IsExternal == true || xSetting?.IsExternal == true;
		bool yExternal = y?.IsExternal == true || ySetting?.IsExternal == true;

		if ( xExternal != yExternal )
		{
			return xExternal ? -1 : 1;
		}

		if ( xExternal )
		{
			return xSetting?.Priority.HasValue == true && ySetting?.Priority.HasValue == true
				? xSetting.Priority.Value.CompareTo( ySetting.Priority.Value ) : 0;
		}

		string xOrderName = x?.RuntimeOrderName ?? x?.Name;
		string yOrderName = y?.RuntimeOrderName ?? y?.Name;
		int fallback = LoadOrderComparer.CompareAlphabetically( yOrderName, xOrderName );

		return fallback != 0 ? fallback : LoadOrderComparer.CompareAlphabetically( x?.Name, y?.Name );
	}

	internal static int CompareConflictSourcesForSettings(
		IEnumerable<ModLoadSetting> allSettings,
		ModFile conflict,
		FileHash x,
		FileHash y )
	{
		ModLoadSetting[] settings = allSettings?.ToArray() ?? Array.Empty<ModLoadSetting>();

		if ( Categories.UsesDlcRuntimeOrder( conflict?.Category ) )
		{
			if ( ReferenceEquals( conflict.Category, Categories.XmlDefinitionOverride ) )
			{
				string winner = ResolveXmlOverridePolicyForSettings( settings, conflict ).Winner;
				bool xWins = winner != null && x?.Name.EqualsIgnoreCase( winner ) == true;
				bool yWins = winner != null && y?.Name.EqualsIgnoreCase( winner ) == true;

				if ( xWins != yWins )
					return xWins ? -1 : 1;
			}

			return CompareXmlOverrideSourcesForSettings( settings, x, y );
		}

		if ( ReferenceEquals( conflict?.Category, Categories.PrecompiledScript ) )
		{
			bool xCompiled = x?.HasPrecompiledScriptDefinition == true;
			bool yCompiled = y?.HasPrecompiledScriptDefinition == true;

			if ( xCompiled != yCompiled )
				return xCompiled ? -1 : 1;
		}

		ModLoadSetting xSetting = settings.FirstOrDefault( setting => setting.ModName.EqualsIgnoreCase( x?.Name ) );
		ModLoadSetting ySetting = settings.FirstOrDefault( setting => setting.ModName.EqualsIgnoreCase( y?.Name ) );

		return LoadOrderComparer.CompareRuntimeOrder( x?.Name, xSetting, y?.Name, ySetting );
	}

	// Compiled implementations take precedence over loose ones. Compatible annotations can coexist, so a resolved conflict may have no single winner.
	internal static (bool Resolved, string Winner) ResolvePrecompiledConflictForSettings(
		IEnumerable<ModLoadSetting> allSettings,
		ModFile conflict )
	{
		ModLoadSetting[] settings = allSettings?.ToArray() ?? Array.Empty<ModLoadSetting>();
		IReadOnlyList<FileHash> sources = conflict.GetPrecompiledAnalysis( settings ).Sources;
		FileHash[] blobSources = sources
			.Where( source => source.HasPrecompiledScriptDefinition )
			.ToArray();

		if ( !TryGetEnabledSources( settings, blobSources, out FileHash[] enabledBlobs ) )
		{
			return (false, null);
		}

		if ( enabledBlobs.Length > 0 )
		{
			string[] priorityDefinitions = enabledBlobs
				.Where( source => source.HasPrecompiledPriorityConflictDefinition )
				.Select( source => source.Name )
				.Distinct( StringComparer.OrdinalIgnoreCase )
				.ToArray();

			if ( priorityDefinitions.Length == 0 )
			{
				return (true, null);
			}

			if ( priorityDefinitions.Length == 1 )
			{
				return (true, priorityDefinitions[0]);
			}

			bool resolved = HasResolvedConflictForSettings(
				settings,
				priorityDefinitions,
				modIoDominatesConventional: true );

			return (resolved, resolved
				? GetTopPriorityEnabledModForSettings(
					settings,
					priorityDefinitions,
					modIoDominatesConventional: true )
				: null);
		}

		FileHash[] looseSources = sources
			.Where( source => source.HasLooseScriptDefinition )
			.ToArray();

		if ( !TryGetEnabledSources( settings, looseSources, out FileHash[] enabledLoose ) )
		{
			return (false, null);
		}

		if ( enabledLoose.Length <= 1 )
		{
			return (true, enabledLoose.FirstOrDefault()?.Name);
		}

		string[] looseNames = enabledLoose.Select( source => source.Name )
			.Distinct( StringComparer.OrdinalIgnoreCase )
			.ToArray();
		bool looseResolved = HasResolvedConflictForSettings(
			settings,
			looseNames,
			modIoDominatesConventional: true );

		return (looseResolved, looseResolved
			? GetTopPriorityEnabledModForSettings(
				settings,
				looseNames,
				modIoDominatesConventional: true )
			: null);
	}

	private static bool TryGetEnabledSources(
		IReadOnlyCollection<ModLoadSetting> settings,
		IEnumerable<FileHash> sources,
		out FileHash[] enabled )
	{
		List<FileHash> enabledSources = new();

		foreach ( FileHash source in sources )
		{
			ModLoadSetting setting = settings.FirstOrDefault( candidate =>
				candidate.ModName.EqualsIgnoreCase( source.Name ) );

			if ( setting == null )
			{
				if ( source.IsExternal )
				{
					enabled = Array.Empty<FileHash>();

					return false;
				}

				enabledSources.Add( source );

				continue;
			}

			if ( !HasKnownState( setting ) )
			{
				enabled = Array.Empty<FileHash>();

				return false;
			}

			if ( setting.IsEnabled == true )
			{
				enabledSources.Add( source );
			}
		}

		enabled = enabledSources.ToArray();

		return true;
	}

	internal static bool HasResolvedConflictForSettings(
		IEnumerable<ModLoadSetting> allSettings,
		IEnumerable<string> modNames,
		bool modIoDominatesConventional )
	{
		string[] names = modNames
			.Where( name => !string.IsNullOrWhiteSpace( name ) )
			.Distinct( StringComparer.OrdinalIgnoreCase )
			.ToArray();
		ModLoadSetting[] loadSettings = GetConflictSettings( allSettings, names );

		if ( !loadSettings.Any() )
			return false;

		bool hasExternal = loadSettings.Any( setting => setting.IsExternal );
		bool hasConventional = names.Any( name => !loadSettings.Any( setting =>
			setting.IsExternal && setting.ModName.EqualsIgnoreCase( name ) ) );

		if ( hasExternal )
		{
			if ( hasConventional && !modIoDominatesConventional )
				return false;

			if ( loadSettings.Any( setting => setting.IsExternal && !HasKnownState( setting ) ) )
				return false;

			var active = loadSettings.Where( setting => setting.IsExternal && setting.IsEnabled == true ).ToArray();

			if ( active.Length > 0 )
				return ModIoWinner( active ) != null;
		}

		if ( loadSettings.Any( setting => HasKnownState( setting ) && setting.IsEnabled == true && setting.Priority.HasValue ) )
			return true;

		return loadSettings.Count( setting => HasKnownState( setting ) && setting.IsEnabled == false ) >= names.Length - 1;
	}

	internal static string GetTopPriorityEnabledModForSettings(
		IEnumerable<ModLoadSetting> allSettings,
		IEnumerable<string> conflictMods,
		bool modIoDominatesConventional )
	{
		string[] names = conflictMods
			.Where( name => !string.IsNullOrWhiteSpace( name ) )
			.Distinct( StringComparer.OrdinalIgnoreCase )
			.ToArray();
		ModLoadSetting[] conflictModSettings = GetConflictSettings( allSettings, names );
		bool hasExternal = conflictModSettings.Any( setting => setting.IsExternal );
		bool hasConventional = names.Any( name => !conflictModSettings.Any( setting =>
			setting.IsExternal && setting.ModName.EqualsIgnoreCase( name ) ) );

		if ( hasExternal )
		{
			if ( hasConventional && !modIoDominatesConventional
				|| conflictModSettings.Any( setting => setting.IsExternal && !HasKnownState( setting ) ) )
				return null;

			var active = conflictModSettings.Where( setting => setting.IsExternal && setting.IsEnabled == true ).ToArray();

			if ( active.Length > 0 )
				return ModIoWinner( active )?.ModName;
		}

		ModLoadSetting[] knownSettings = conflictModSettings.Where( HasKnownState ).ToArray();
		ModLoadSetting[] enabledModSettings = knownSettings.Where( setting => setting.IsEnabled == true ).ToArray();

		if ( !enabledModSettings.Any() && conflictModSettings.Any( setting => !HasKnownState( setting ) ) )
			return null;

		var candidates = enabledModSettings.Select( setting => setting.ModName )
			.Concat( names.Except( conflictModSettings.Select( setting => setting.ModName ), StringComparer.OrdinalIgnoreCase ) )
			.Distinct( StringComparer.OrdinalIgnoreCase );

		return candidates.OrderBy( name => name, Comparer<string>.Create( ( left, right ) => LoadOrderComparer.CompareRuntimeOrder(
			left, conflictModSettings.FirstOrDefault( setting => setting.ModName.EqualsIgnoreCase( left ) ),
			right, conflictModSettings.FirstOrDefault( setting => setting.ModName.EqualsIgnoreCase( right ) ) ) ) ).FirstOrDefault();
	}

	private static ModLoadSetting ModIoWinner( ModLoadSetting[] active )
	{
		if ( active.Length == 1 )
			return active[0];

		if ( active.Any( setting => !setting.Priority.HasValue ) )
			return null;

		int priority = active.Min( setting => setting.Priority.Value );
		var candidates = active.Where( setting => setting.Priority == priority ).ToArray();

		return candidates.Length == 1 ? candidates[0] : null;
	}

	private static ModLoadSetting[] GetConflictSettings(
		IEnumerable<ModLoadSetting> allSettings,
		IReadOnlyCollection<string> names )
	{
		HashSet<string> requested = names.ToHashSet( StringComparer.OrdinalIgnoreCase );

		return allSettings.Where( setting => requested.Contains( setting.ModName ) ).ToArray();
	}

	private static bool HasKnownState( ModLoadSetting setting )
	{
		return setting.StateKnown && setting.IsEnabled.HasValue;
	}

	public ModLoadSetting GetModLoadSettingByName( string modName )
	{
		return Mods.FirstOrDefault( ( ModLoadSetting setting ) => setting.ModName.EqualsIgnoreCase( modName ) );
	}

	public bool IsModDisabledByName( string modName )
	{
		ModLoadSetting mod = GetModLoadSettingByName( modName );

		return mod?.StateKnown == true && mod.IsEnabled == false;
	}

	public void ToggleModByName( string modName )
	{
		if ( !RefreshBeforeChange() )
			return;

		ModLoadSetting mod = GetModLoadSettingByName( modName );

		if ( mod?.IsExternal == true )
			return;

		if ( mod != null )
		{
			mod.IsEnabled = !mod.IsEnabled;
		}
		else
		{
			AddMod( new ModLoadSetting
			{
				ModName = modName,
				IsEnabled = false,
				Priority = 9999
			} );
		}

		AddMergedModIfMissingAndSave();
	}

	public int GetPriorityByName( string modName )
	{
		return GetModLoadSettingByName( modName )?.Priority ?? ( -1 );
	}

	public int GetModIoPriorityMaximum()
	{
		return Math.Max( 0, Mods.Count( setting => setting.IsExternal && setting.ModIoId > 0 ) - 1 );
	}

	public bool SetModIoPriorityByName( string modName, int priority )
	{
		ModLoadSetting setting = GetModLoadSettingByName( modName );

		if ( setting?.IsExternal != true || setting.ModIoId <= 0 )
			throw new InvalidOperationException( UiText.T( "CustomLoadOrder", "The selected source is not a configured Mod.io mod." ) );

		bool changed = modIoDiscovery.SetPriority( setting.ModIoId, priority );

		if ( changed )
			Refresh();

		return changed;
	}

	public void SetPriorityByName( string modName, int priority )
	{
		if ( !RefreshBeforeChange() )
			return;

		if ( GetModLoadSettingByName( modName )?.IsExternal == true )
			return;

		if ( GetModLoadSettingByName( modName ) != null )
		{
			List<ModLoadSetting> updatedMods = Mods.Select( ( ModLoadSetting m ) => ( !m.ModName.EqualsIgnoreCase( modName ) ) ? m : new ModLoadSetting
			{
				ModName = m.ModName,
				IsEnabled = m.IsEnabled,
				VK = m.VK,
				Priority = priority
			} ).ToList();

			Mods = updatedMods;

			ModLoadSetting newMod = Mods.First( ( ModLoadSetting m ) => m.ModName.EqualsIgnoreCase( modName ) );

			IncrementLeadingContiguousPriorities( newMod );
		}
		else
		{
			ModLoadSetting newMod2 = new ModLoadSetting
			{
				ModName = modName,
				IsEnabled = true,
				Priority = priority
			};

			AddMod( newMod2 );
		}

		AddMergedModIfMissingAndSave();
	}

	// Propagate settings failures so the caller can undo the folder rename.
	internal bool RenameModByName( string oldName, string newName )
	{
		if ( !RefreshBeforeChange( keepVortexKey: true ) )
			throw new InvalidOperationException( UiText.T( "CustomLoadOrder", "mods.settings could not be read, so it was not changed." ) );

		ModLoadSetting existing = GetModLoadSettingByName( oldName );

		if ( existing == null || existing.IsExternal )
		{
			return false;
		}

		if ( !oldName.EqualsIgnoreCase( newName ) && ContainsMod( newName ) )
		{
			throw new InvalidOperationException( UiText.Format( "CustomLoadOrder", "mods.settings already contains an entry named '{0}'.", newName ) );
		}

		Mods = Mods.Select( setting => !setting.ModName.EqualsIgnoreCase( oldName )
			? setting
			: new ModLoadSetting
			{
				ModName = newName,
				IsEnabled = setting.IsEnabled,
				Priority = setting.Priority,
				VK = setting.VK
			} ).ToList();
		Save();

		return true;
	}

	public void AddMod( ModLoadSetting modLoadSetting, bool keepVortexKey = false )
	{
		if ( keepVortexKey && !RefreshBeforeChange( keepVortexKey: true ) )
			return;

		IncrementLeadingContiguousPriorities( modLoadSetting );
		Mods = Mods.Concat( new ModLoadSetting[1] { modLoadSetting } ).ToList();
		AddMergedModIfMissingAndSave();

		if ( keepVortexKey )
		{
			Refresh();
		}
	}

	public void RemoveMod( string modName )
	{
		if ( !RefreshBeforeChange() )
			return;

		ModLoadSetting modToRemove = Mods.FirstOrDefault( ( ModLoadSetting setting ) => setting.ModName.EqualsIgnoreCase( modName ) );

		if ( modToRemove != null && !modToRemove.IsExternal )
		{
			Mods = Mods.Where( ( ModLoadSetting mod ) => mod != modToRemove ).ToList();
			Save();
		}
	}

	private void IncrementLeadingContiguousPriorities( ModLoadSetting incomingMod )
	{
		IncrementLeadingContiguousPrioritiesInternal( incomingMod );
		Mods = Mods;
	}

	// Shift consecutive priorities to make room without giving two mods the same number.
	private void IncrementLeadingContiguousPrioritiesInternal( ModLoadSetting incomingMod )
	{
		int? startingPriority = incomingMod.Priority;

		if ( !startingPriority.HasValue )
		{
			return;
		}

		int? nextPriority = startingPriority + 1;
		ModLoadSetting[] modsToIncrement = Mods.Where( ( ModLoadSetting mod ) => !mod.IsExternal && mod.Priority == startingPriority && mod.ModName != incomingMod.ModName ).ToArray();
		ModLoadSetting[] displacedMods = Mods.Where( ( ModLoadSetting mod ) => !mod.IsExternal && mod.Priority == nextPriority && mod.ModName != incomingMod.ModName ).ToArray();

		if ( modsToIncrement.Any() )
		{
			if ( displacedMods.Any() && nextPriority < 9999 )
			{
				IncrementLeadingContiguousPrioritiesInternal( new ModLoadSetting
				{
					ModName = incomingMod.ModName,
					Priority = nextPriority
				} );
			}

			ModLoadSetting[] array = modsToIncrement;

			foreach ( ModLoadSetting obj in array )
			{
				int? priority = obj.Priority + 1;

				obj.Priority = priority;
			}
		}
	}
}
