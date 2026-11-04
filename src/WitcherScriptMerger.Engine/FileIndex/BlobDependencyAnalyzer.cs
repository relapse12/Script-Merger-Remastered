#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using WitcherScriptMerger.LoadOrder;

namespace WitcherScriptMerger.FileIndex;

internal enum RsBlobRequirementKind
{
	BaseClass, Type, Function
}
internal sealed record RsBlobRequirement( string Key, RsBlobRequirementKind Kind );
internal sealed record RsBlobTypeParent( string Name, string Parent );
// Keep the types a blob defines alongside the names it needs from other scripts.
internal sealed record RsBlobDependencyMetadata( IReadOnlyList<string> Types, IReadOnlyList<RsBlobRequirement> Requirements,
	IReadOnlyList<RsBlobEnumDefinition>? Enums = null, IReadOnlyList<RsBlobTypeParent>? TypeParents = null )
{
	public static RsBlobDependencyMetadata Empty { get; } = new( Array.Empty<string>(), Array.Empty<RsBlobRequirement>(), Array.Empty<RsBlobEnumDefinition>() );
}
internal sealed record BlobDependencySource( string Name, bool IsExternal, RsBlobDependencyMetadata Metadata,
	IReadOnlyList<ScriptSemanticSymbol> Symbols );

internal enum BlobDependencySymbolKind
{
	Class, Function, Type
}

// A compiled mod needs Symbol from Provider, which is disabled or loads too late.
internal sealed record BlobDependencyIssue( string Consumer, string Provider, string Symbol, bool ProviderDisabled,
	BlobDependencySymbolKind SymbolKind = BlobDependencySymbolKind.Class, bool ConsumerIsModIo = false, bool ProviderIsModIo = false );

internal static class BlobDependencyReport
{
	internal static IReadOnlyList<string> Format( IEnumerable<BlobDependencyIssue> issues ) => issues
		.GroupBy( issue => issue.Provider, StringComparer.OrdinalIgnoreCase )
		.OrderBy( group => group.Key, StringComparer.OrdinalIgnoreCase )
		.Select( FormatProvider ).ToArray();

	// Mod.io content always loads before conventional mods. Changing a numeric priority cannot reverse that order.
	private static string FormatProvider( IEnumerable<BlobDependencyIssue> issues )
	{
		var group = issues.ToArray();
		string provider = group[0].Provider;
		bool disabled = group.All( issue => issue.ProviderDisabled );
		var symbols = group.GroupBy( issue => issue.Symbol, StringComparer.OrdinalIgnoreCase )
			.Select( values => values.First() ).OrderBy( issue => issue.Symbol, StringComparer.OrdinalIgnoreCase ).ToArray();
		string symbolList = HyphenList( symbols.Select( issue => issue.Symbol ) );
		string consumerList = HyphenList( group.Select( issue => issue.Consumer ), modNames: true );
		string heading = disabled
			? UiText.Format( "BlobDependencies", "Blob dependency disabled — {0}", provider )
			: UiText.Format( "BlobDependencies", "Blob dependency order — {0}", provider );
		string introduction = DescribeSymbols( symbols, provider );
		string guidance;

		if ( disabled )
		{
			guidance = UiText.Format( "BlobDependencies", "Enable {0}, or remove the following mods that require it:", provider );
		}
		else
		{
			string availability = symbols.Length == 1
				? UiText.T( "BlobDependencies", "The current load order may leave it unavailable." )
				: UiText.T( "BlobDependencies", "The current load order may leave them unavailable." );

			if ( group.Any( issue => issue.ProviderIsModIo ) )
			{
				var parts = new List<string>();

				if ( group.Any( issue => issue.ConsumerIsModIo ) )
					parts.Add( UiText.Format( "BlobDependencies", "Move {0} below these mods in the mod.io load order (a larger priority number):", provider )
						+ Environment.NewLine + HyphenList( group.Where( issue => issue.ConsumerIsModIo ).Select( issue => issue.Consumer ), true ) );

				if ( group.Any( issue => !issue.ConsumerIsModIo ) )
					parts.Add( UiText.Format( "BlobDependencies", "Use a conventional installation of {0} so it can load after these mods:", provider )
						+ Environment.NewLine + HyphenList( group.Where( issue => !issue.ConsumerIsModIo ).Select( issue => issue.Consumer ), true ) );

				return string.Join( Environment.NewLine + Environment.NewLine, heading,
					introduction + Environment.NewLine + symbolList, availability, string.Join( Environment.NewLine + Environment.NewLine, parts ) );
			}

			guidance = availability + " " + UiText.Format( "BlobDependencies",
				"Make sure {0} trails the following mods in the load order (a larger priority number, or a folder name that comes later alphabetically when none have a priority assigned):", provider );
		}

		return string.Join( Environment.NewLine + Environment.NewLine,
			heading, introduction + Environment.NewLine + symbolList, guidance + Environment.NewLine + consumerList );
	}

	private static string HyphenList( IEnumerable<string> values, bool modNames = false )
	{
		IEnumerable<string> unique = values.Distinct( StringComparer.OrdinalIgnoreCase );
		IEnumerable<string> ordered = modNames
			? unique.OrderBy(
				value => value.StartsWith( "mod", StringComparison.OrdinalIgnoreCase ) ? value[3..] : value,
				LoadOrderComparer.GameAlphabeticalComparer )
				.ThenBy( value => value, StringComparer.Ordinal )
			: unique.OrderBy( value => value, StringComparer.OrdinalIgnoreCase );

		return string.Join( Environment.NewLine, ordered.Select( value => "    - " + value ) );
	}

	private static string DescribeSymbols( IReadOnlyList<BlobDependencyIssue> symbols, string provider )
	{
		var kinds = symbols.Select( issue => issue.SymbolKind ).Distinct().ToArray();

		if ( kinds.Length == 1 )
		{
			return kinds[0] switch
			{
				BlobDependencySymbolKind.Function => symbols.Count == 1
					? UiText.Format( "BlobDependencies", "The following function from {0} is required by other mods:", provider )
					: UiText.Format( "BlobDependencies", "The following functions from {0} are required by other mods:", provider ),
				BlobDependencySymbolKind.Type => symbols.Count == 1
					? UiText.Format( "BlobDependencies", "The following type from {0} is required by other mods:", provider )
					: UiText.Format( "BlobDependencies", "The following types from {0} are required by other mods:", provider ),
				_ => symbols.Count == 1
					? UiText.Format( "BlobDependencies", "The following class from {0} is required by other mods:", provider )
					: UiText.Format( "BlobDependencies", "The following classes from {0} are required by other mods:", provider )
			};
		}

		string description = !kinds.Contains( BlobDependencySymbolKind.Type )
			? UiText.T( "BlobDependencies", "classes and functions" )
			: !kinds.Contains( BlobDependencySymbolKind.Class )
				? UiText.T( "BlobDependencies", "functions and types" )
				: !kinds.Contains( BlobDependencySymbolKind.Function )
					? UiText.T( "BlobDependencies", "classes and types" )
					: UiText.T( "BlobDependencies", "classes, functions and types" );

		return UiText.Format( "BlobDependencies", "The following {0} from {1} are required by other mods:", description, provider );
	}
}

// Find compiled mods whose required definitions are disabled or load too late.
internal static class BlobDependencyAnalyzer
{
	private const ScriptSymbolKind FunctionProviders = ScriptSymbolKind.ClassFunction | ScriptSymbolKind.GlobalFunction
		| ScriptSymbolKind.AddedFunction | ScriptSymbolKind.ExternalFunction;

	// Report a dependency only when one provider can be identified. Duplicate definitions leave that uncertain.
	// Definitions available from the game or loose scripts already satisfy the requirement.
	internal static IReadOnlyList<BlobDependencyIssue> Analyze( IEnumerable<BlobDependencySource> sources,
		IEnumerable<ModLoadSetting> settings, ISet<string> availableTypes, ISet<string> availableFunctions,
		IEnumerable<BlobDependencySource>? looseSources = null, Action<string>? reportIssue = null )
	{
		var blobs = sources.ToArray();
		var loadSettings = ReadLoadSettings( settings, reportIssue );

		availableTypes = new HashSet<string>( availableTypes, StringComparer.OrdinalIgnoreCase );
		availableFunctions = new HashSet<string>( availableFunctions, StringComparer.OrdinalIgnoreCase );

		foreach ( var loose in looseSources ?? Array.Empty<BlobDependencySource>() )
		{
			loadSettings.TryGetValue( loose.Name, out var setting );

			if ( !HasKnownEnabledState( setting ) || loose.IsExternal && setting == null )
				continue;

			availableTypes.UnionWith( loose.Metadata.Types );
			availableFunctions.UnionWith( loose.Symbols.Where( value => ( value.Kind & FunctionProviders ) != 0 ).Select( value => value.Key ) );
		}

		var typeProviders = new Dictionary<string, HashSet<string>>( StringComparer.OrdinalIgnoreCase );
		var functionProviders = new Dictionary<string, HashSet<string>>( StringComparer.OrdinalIgnoreCase );

		foreach ( var blob in blobs )
		{
			foreach ( string type in blob.Metadata.Types )
				AddProvider( typeProviders, type, blob.Name );

			foreach ( var symbol in blob.Symbols.Where( value => ( value.Kind & FunctionProviders ) != 0 ) )
				AddProvider( functionProviders, symbol.Key, blob.Name );
		}

		var issues = new List<BlobDependencyIssue>();
		// An inherited vanilla method can be referenced through a more-derived scope.
		var availableFunctionNames = availableFunctions.Select( value => value.Split( '\\' ).Last() ).ToHashSet( StringComparer.OrdinalIgnoreCase );

		foreach ( var blob in blobs )
		{
			loadSettings.TryGetValue( blob.Name, out var consumerSetting );

			bool consumerModIo = blob.IsExternal || LoadOrderComparer.IsModIo( blob.Name, consumerSetting );

			if ( !HasKnownEnabledState( consumerSetting ) || consumerModIo && consumerSetting?.IsExternal != true
				|| !consumerModIo && !blob.Name.StartsWith( "mod", StringComparison.OrdinalIgnoreCase ) )
				continue;

			foreach ( var required in blob.Metadata.Requirements )
			{
				bool function = required.Kind == RsBlobRequirementKind.Function;

				if ( function ? availableFunctions.Contains( required.Key ) || availableFunctionNames.Contains( required.Key.Split( '\\' ).Last() )
					: availableTypes.Contains( required.Key ) )
					continue;

				var providers = function ? functionProviders : typeProviders;

				if ( !providers.TryGetValue( required.Key, out var names ) || names.Contains( blob.Name ) )
					continue;

				if ( names.Count != 1 )
					continue;

				string providerName = names.Single();

				loadSettings.TryGetValue( providerName, out var providerSetting );

				bool providerModIo = LoadOrderComparer.IsModIo( providerName, providerSetting )
					|| blobs.Any( value => value.Name.Equals( providerName, StringComparison.OrdinalIgnoreCase ) && value.IsExternal );

				if ( providerModIo && providerSetting?.IsExternal != true
					|| !providerModIo && !providerName.StartsWith( "mod", StringComparison.OrdinalIgnoreCase ) )
					continue;

				if ( providerSetting != null && ( !providerSetting.StateKnown || !providerSetting.IsEnabled.HasValue ) )
					continue;

				bool disabled = providerSetting?.IsEnabled == false;
				int order = LoadOrderComparer.CompareRuntimeOrder( providerName, providerSetting, blob.Name, consumerSetting );

				if ( !disabled && ( order > 0 || providerModIo && consumerModIo && order == 0 ) )
					continue;

				bool otherType = !function && blobs.Where( value => value.Name.Equals( providerName, StringComparison.OrdinalIgnoreCase ) )
					.SelectMany( value => value.Symbols ).Any( symbol =>
						( symbol.Kind & ( ScriptSymbolKind.EnumDefinition | ScriptSymbolKind.StructDefinition ) ) != 0
						&& symbol.Key.Split( '\\' ).Last().Equals( required.Key, StringComparison.OrdinalIgnoreCase ) );

				issues.Add( new BlobDependencyIssue( blob.Name, providerName, DisplaySymbol( required.Key ), disabled,
					function ? BlobDependencySymbolKind.Function : otherType ? BlobDependencySymbolKind.Type : BlobDependencySymbolKind.Class,
					consumerModIo, providerModIo ) );
			}
		}

		return issues.Distinct().OrderBy( value => value.Consumer, StringComparer.OrdinalIgnoreCase )
			.ThenBy( value => value.Provider, StringComparer.OrdinalIgnoreCase ).ThenBy( value => value.Symbol, StringComparer.OrdinalIgnoreCase ).ToArray();
	}

	private static Dictionary<string, ModLoadSetting> ReadLoadSettings( IEnumerable<ModLoadSetting> settings, Action<string>? reportIssue )
	{
		var result = new Dictionary<string, ModLoadSetting>( StringComparer.OrdinalIgnoreCase );
		var duplicates = new HashSet<string>( StringComparer.OrdinalIgnoreCase );

		foreach ( var setting in settings )
		{
			if ( string.IsNullOrWhiteSpace( setting.ModName ) )
				continue;

			if ( !result.TryAdd( setting.ModName, setting ) && duplicates.Add( setting.ModName ) )
				reportIssue?.Invoke( UiText.Format( "BlobDependencies",
					"Duplicate load-order settings — {0}\nDependency checks use the first matching load-order entry.", result[setting.ModName].ModName ) );
		}

		return result;
	}

	internal static bool HasKnownEnabledState( ModLoadSetting? value ) => value == null || value.StateKnown && value.IsEnabled == true;

	private static string DisplaySymbol( string key ) => key.StartsWith( "Functions\\", StringComparison.OrdinalIgnoreCase )
		? key[10..].Replace( '\\', '.' ) : key;

	private static void AddProvider( Dictionary<string, HashSet<string>> providers, string key, string name )
	{
		if ( !providers.TryGetValue( key, out var names ) )
			providers.Add( key, names = new( StringComparer.OrdinalIgnoreCase ) );

		names.Add( name );
	}
}
