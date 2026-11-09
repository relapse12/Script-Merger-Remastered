#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using WitcherScriptMerger.Inventory;
using WitcherScriptMerger.LoadOrder;
using WitcherScriptMerger.Tools;

namespace WitcherScriptMerger.FileIndex;

internal sealed record XmlOverrideSource(
	string ModName,
	string BundlePath,
	string ResourcePath,
	string DlcName,
	string? SourceDirectory,
	string? Origin,
	long ModIoId );

// The DLC context is part of a definition's identity.
internal sealed record XmlOverrideDefinitionKey(
	string Context,
	string Container,
	string DefinitionType,
	string Identity )
{
	public string ComparisonKey => string.Join( "\u001f", Context, Container, DefinitionType, Identity );
}

internal sealed record XmlOverrideDefinition(
	XmlOverrideDefinitionKey Key,
	string ConflictMode,
	IReadOnlyDictionary<string, string> Parameters,
	XmlOverrideSource Source,
	XmlEquipmentVariants? EquipmentVariants = null,
	int? DeclarationOrder = null );

internal sealed record XmlEquipmentRule( string EquipTemplate, IReadOnlyList<string> Items );

internal sealed record XmlEquipmentVariants( IReadOnlyList<XmlEquipmentRule> Rules )
{
	internal IEnumerable<string> EquipTemplates => Rules.Select( rule => rule.EquipTemplate );
	internal IEnumerable<string> Items => Rules.SelectMany( rule => rule.Items );
}

internal enum XmlVariantInteraction
{
	EquipmentRules,
	ItemLists,
	ExtensionOverItem
}

internal sealed record XmlVariantCompetition( string LeftMod, string RightMod, bool ConditionsKnown,
	XmlVariantInteraction Interaction = XmlVariantInteraction.EquipmentRules );

internal sealed record XmlOverrideConflictSource(
	XmlOverrideSource Source,
	string ConflictMode,
	string Value,
	IReadOnlyList<string> ResourcePaths,
	IReadOnlyList<string>? ConflictModes = null,
	XmlEquipmentVariants? EquipmentVariants = null,
	int DeclarationCount = 1, string? VariantDefinitionType = null );

internal sealed record XmlOverrideTooltipInfo( XmlOverrideDefinitionKey Key, string Parameter, string Value,
	bool IsWholeDefinitionConflict, IReadOnlyList<string> Policies, XmlEquipmentVariants? EquipmentVariants = null,
	int DeclarationCount = 1, bool? HasVanillaDefinition = false, int DefinitionSourceCount = 0,
	IReadOnlyList<XmlVariantCompetition>? VariantCompetitions = null, string? VariantDefinitionType = null );

internal static class XmlOverrideToolTips
{
	internal static string DescribeSource( ModFile conflict, FileHash source )
	{
		if ( source?.XmlOverrideInfo == null )
			return UiText.T( "XmlOverrideToolTips", "XML definition override: this source contributes to the conflicting definition." );

		return Describe( conflict, source );
	}

	internal static string DescribeConflict( ModFile conflict ) => Describe( conflict, null );

	private static string Describe( ModFile conflict, FileHash? hoveredSource )
	{
		FileHash[] sources = conflict.ParticipatingSources.Where( value => value.XmlOverrideInfo != null )
			.OrderByDescending( value => value == hoveredSource ).ToArray();

		if ( sources.Length == 0 )
			return UiText.T( "XmlOverrideToolTips", "XML definition conflict: multiple sources declare the same definition." );

		XmlOverrideTooltipInfo first = sources[0].XmlOverrideInfo!;

		if ( first.Parameter == "variants" && !first.IsWholeDefinitionConflict )
		{
			var summary = DescribeVariantPriority( conflict, hoveredSource ).ToList();

			summary.Add( string.Empty );
			summary.AddRange( DescribeVariantPriority( conflict, hoveredSource, technicalDetails: true ).Distinct() );

			foreach ( FileHash source in sources )
			{
				XmlOverrideTooltipInfo info = source.XmlOverrideInfo!;
				string templates = info.EquipmentVariants != null ? DescribeEquipment( info )
					: UiText.T( "XmlOverrideToolTips", "variant details unavailable" );

				summary.Add( UiText.Format( "XmlOverrideToolTips", "{0}: on_conflict={1}  {2}",
					source.Name, string.Join( " / ", info.Policies ), templates ) );
			}

			return string.Join( "\n", summary );
		}

		var lines = new List<string> { first.IsWholeDefinitionConflict
			? UiText.Format("XmlOverrideToolTips", "{0} \"{1}\" — whole definition.", first.Key.DefinitionType, first.Key.Identity)
			: UiText.Format(
				"XmlOverrideToolTips",
				"{0} \"{1}\" — {2}.",
				first.Key.DefinitionType,
				first.Key.Identity,
				first.Parameter.TrimStart('@')) };

		foreach ( FileHash source in sources )
		{
			XmlOverrideTooltipInfo info = source.XmlOverrideInfo!;
			string value = info.IsWholeDefinitionConflict
				? info.Policies.Contains( "replace" )
					? UiText.T( "XmlOverrideToolTips", "replaces the whole definition" )
					: UiText.T( "XmlOverrideToolTips", "supplies this definition" )
				: DisplayValue( info.Value );

			lines.Add( UiText.Format( "XmlOverrideToolTips", "{0} (on_conflict={1}): {2}.", source.Name, string.Join( " / ", info.Policies ), value ) );
		}

		lines.AddRange( DescribePriority( conflict, hoveredSource ) );

		return string.Join( "\n", lines );
	}

	private static string DescribeEquipment( XmlOverrideTooltipInfo info )
	{
		XmlEquipmentVariants variants = info.EquipmentVariants!;

		if ( variants.Rules.Count == 0 )
			return info.VariantDefinitionType == "item"
				? UiText.T( "XmlOverrideToolTips", "empty item appearance list (clears item-owned rules)" )
				: UiText.T( "XmlOverrideToolTips", "empty appearance list (keeps earlier rules)" );

		string[] templates = variants.EquipTemplates.Distinct( StringComparer.Ordinal ).ToArray();

		return "equip_template=" + string.Join( ", ", templates.Select( template => "\"" + template + "\"" ) );
	}

	// Equipment variant priority depends on which pair of rules competes.
	internal static IEnumerable<string> DescribeVariantPriority( ModFile conflict, FileHash? hoveredSource = null,
		IEnumerable<ModLoadSetting>? overrideSettings = null, bool technicalDetails = false )
	{
		ModLoadSetting[] settings = ( overrideSettings ?? conflict.CurrentLoadOrder?.Mods ?? Array.Empty<ModLoadSetting>() ).ToArray();
		var resolutions = CustomLoadOrder.ResolveXmlVariantCompetitionsForSettings( settings, conflict );

		if ( resolutions == null )
		{
			if ( !technicalDetails )
				yield return UiText.T(
					"XmlOverrideToolTips",
					"These appearance rules differ; their equipment conditions could not be compared." );

			yield break;
		}

		ModLoadSetting? Setting( FileHash source ) => settings.FirstOrDefault( value => value.ModName.EqualsIgnoreCase( source.Name ) );

		bool External( FileHash source ) => source.IsExternal || Setting( source )?.IsExternal == true;

		var active = resolutions.Where( pair => hoveredSource == null || pair.Left.Name.EqualsIgnoreCase( hoveredSource.Name )
			|| pair.Right.Name.EqualsIgnoreCase( hoveredSource.Name ) ).ToArray();
		bool validOrder = overrideSettings != null || conflict.CurrentLoadOrder?.IsValid != false;
		bool allFixed = validOrder && active.Length > 0 && active.All( pair => pair.FixedOrder && pair.Winner != null
			&& pair.Interaction != XmlVariantInteraction.ExtensionOverItem );

		if ( active.Length == 0 )
		{
			if ( !technicalDetails )
				yield return UiText.T( "XmlOverrideToolTips", "No competing pair is enabled." );

			yield break;
		}

		foreach ( var pair in active )
		{
			if ( !validOrder || pair.Winner == null )
			{
				if ( technicalDetails )
					yield return pair.Interaction == XmlVariantInteraction.ExtensionOverItem
						? UiText.T( "XmlOverrideToolTips", "The active item list or item_extension source is uncertain." )
						: UiText.Format( "XmlOverrideToolTips", "XML order: {0} / {1} — unavailable", pair.Left.Name, pair.Right.Name );
				else if ( pair.Interaction == XmlVariantInteraction.ItemLists )
					yield return UiText.Format( "XmlOverrideToolTips", "The item appearance list used between {0} and {1} could not be determined.",
						pair.Left.Name, pair.Right.Name );
				else
					yield return UiText.Format(
						"XmlOverrideToolTips",
						"When both equipment rules match, the appearance winner between {0} and {1} is unknown.",
						pair.Left.Name,
						pair.Right.Name );

				continue;
			}

			FileHash winner = pair.Winner;
			FileHash other = winner == pair.Left ? pair.Right : pair.Left;

			if ( pair.Interaction == XmlVariantInteraction.ExtensionOverItem )
			{
				yield return technicalDetails
					? UiText.T( "XmlOverrideToolTips", "item_extension rules take precedence over item-owned rules regardless of DLC registration order." )
					: UiText.Format( "XmlOverrideToolTips",
						"When both equipment rules match, {0}'s item_extension appearance wins over {1}'s item appearance.",
						winner.Name, other.Name );

				continue;
			}

			if ( !technicalDetails )
			{
				string summary = pair.Interaction == XmlVariantInteraction.ItemLists
					? UiText.Format( "XmlOverrideToolTips", "{0}'s item appearance list is used. The list from {1} does not apply.", winner.Name, other.Name )
					: UiText.Format(
					"XmlOverrideToolTips",
					"When both equipment rules match, {0}'s appearance wins over {1}.",
					winner.Name,
					other.Name );

				if ( pair.FixedOrder && !allFixed )
					summary += " " + UiText.T( "XmlOverrideToolTips", "This order uses internal DLC names; Priority cannot resolve it." );

				yield return summary;

				continue;
			}

			if ( External( winner ) != External( other ) )
				yield return UiText.Format(
					"XmlOverrideToolTips",
					"XML order: {0} (conventional DLC) → {1} (Mod.io)",
					other.Name,
					winner.Name );
			else if ( External( winner ) )
				yield return UiText.Format( "XmlOverrideToolTips", "Mod.io Priority: {0}={2} · {1}={3} (lower wins)",
					winner.Name, other.Name, Setting( winner )!.Priority, Setting( other )!.Priority );
			else
				yield return UiText.Format(
					"XmlOverrideToolTips",
					"XML order: {0} → {1}",
					other.RuntimeOrderName ?? other.Name,
					winner.RuntimeOrderName ?? winner.Name );
		}

		if ( !technicalDetails && allFixed )
			yield return UiText.T( "XmlOverrideToolTips", "This order uses internal DLC names; Priority cannot resolve it." );
	}

	internal static IEnumerable<string> DescribePriority( ModFile conflict, FileHash? source = null,
		IEnumerable<ModLoadSetting>? overrideSettings = null )
	{
		if ( overrideSettings == null && conflict.CurrentLoadOrder?.IsValid == false )
			yield break;

		ModLoadSetting[] settings = ( overrideSettings ?? conflict.CurrentLoadOrder?.Mods ?? Array.Empty<ModLoadSetting>() ).ToArray();
		var resolution = CustomLoadOrder.ResolveXmlOverridePolicyForSettings( settings, conflict );

		if ( resolution.Outcome == XmlOverrideOutcome.ExtensionBeforeDefault )
		{
			yield return UiText.Format( "XmlOverrideToolTips", "{0}'s extend is registered before {1}'s default definition.",
				resolution.ExtensionSource, resolution.DefaultSource );

			yield break;
		}

		if ( !resolution.Resolved )
			yield break;

		if ( resolution.Outcome == XmlOverrideOutcome.VanillaDefaultRetained )
		{
			yield return UiText.T( "XmlOverrideToolTips", "The game's existing default definition is retained." );

			yield break;
		}

		if ( string.IsNullOrEmpty( resolution.Winner ) )
			yield break;

		if ( resolution.Outcome == XmlOverrideOutcome.ExistingDefinitionExtended )
		{
			yield return UiText.Format( "XmlOverrideToolTips", "{0} extends the game's existing item definition.", resolution.Winner );

			yield break;
		}

		if ( resolution.Outcome == XmlOverrideOutcome.DefaultRetained )
		{
			yield return UiText.Format( "XmlOverrideToolTips", "The first default definition from {0} is retained.", resolution.Winner );

			yield break;
		}

		if ( resolution.Outcome == XmlOverrideOutcome.Extended && resolution.DefaultSource != null )
		{
			yield return UiText.Format( "XmlOverrideToolTips", "{0} extends the default definition from {1}.",
				resolution.Winner, resolution.DefaultSource );

			yield break;
		}

		FileHash winner = conflict.Mods.First( value => value.Name.EqualsIgnoreCase( resolution.Winner ) );

		ModLoadSetting? Setting( FileHash value ) => settings.FirstOrDefault( setting => setting.ModName.EqualsIgnoreCase( value.Name ) );

		bool External( FileHash value ) => value.IsExternal || Setting( value )?.IsExternal == true;

		var competitors = conflict.Mods.Where( value => !value.Name.EqualsIgnoreCase( winner.Name )
			&& Setting( value )?.IsEnabled != false ).ToArray();

		if ( competitors.Length == 0 )
		{
			yield return UiText.Format( "XmlOverrideToolTips", "{0} is the only enabled source.", winner.Name );

			yield break;
		}

		foreach ( FileHash competitor in competitors.Where( value => source == null || source.Name.EqualsIgnoreCase( winner.Name )
			|| source.Name.EqualsIgnoreCase( value.Name ) ) )
		{
			if ( External( winner ) != External( competitor ) )
				yield return UiText.Format(
					"XmlOverrideToolTips",
					"{0} has priority over {1} (Mod.io over conventional DLC).",
					winner.Name,
					competitor.Name );
			else if ( External( winner ) )
			{
				int winnerPriority = Setting( winner )?.Priority ?? CustomLoadOrder.BottomPriority;
				int otherPriority = Setting( competitor )?.Priority ?? CustomLoadOrder.BottomPriority;

				yield return UiText.Format(
					"XmlOverrideToolTips",
					"{0} has priority over {1}: Mod.io Priority {2} vs {3} (lower wins).",
					winner.Name,
					competitor.Name,
					winnerPriority,
					otherPriority );
			}
			else
			{
				string winnerFolder = winner.RuntimeOrderName ?? winner.Name;
				string otherFolder = competitor.RuntimeOrderName ?? competitor.Name;

				yield return LoadOrderComparer.CompareAlphabetically( winnerFolder, otherFolder ) != 0
					? UiText.Format(
						"XmlOverrideToolTips",
						"{0} has priority over {1}: later extend; XML DLC name \"{2}\" sorts after \"{3}\".",
						winner.Name,
						competitor.Name,
						winnerFolder,
						otherFolder )
					: UiText.Format(
						"XmlOverrideToolTips",
						"{0} has priority over {1}: matching XML DLC names; source-folder order.",
						winner.Name,
						competitor.Name );
			}
		}
	}

	private static string DisplayValue( string value ) => value.ReplaceLineEndings( " " );
}

internal sealed record XmlOverrideConflict(
	XmlOverrideDefinitionKey Key,
	string Parameter,
	bool IsWholeDefinitionReplacement,
	IReadOnlyList<XmlOverrideConflictSource> Sources,
	IReadOnlyList<XmlVariantCompetition>? VariantCompetitions = null )
{
	public string DisplayPath
	{
		get
		{
			string parameter = IsWholeDefinitionReplacement ? "[replace]" : Parameter.TrimStart( '@' );

			return $"{Key.Context}\\{Key.Container}\\{Key.DefinitionType} \"{Key.Identity}\"\\{parameter}";
		}
	}
}

// Compare the definitions inside XML resources across mods, including definitions stored in different files.
internal static class XmlOverrideConflictAnalyzer
{
	private static readonly string[] IdentityAttributes = { "name", "id", "index" };

	private static readonly HashSet<string> SupportedDefinitionTypes = new(
		new[]
		{
			"attitude",
			"ability",
			"item",
			"item_extension",
			"set",
			"usable_item",
			"res",
			"reward",
			"card"
		},
		StringComparer.OrdinalIgnoreCase );

	public static IReadOnlyList<XmlOverrideDefinition> ReadBundle(
		string bundlePath,
		IEnumerable<string> resourcePaths,
		XmlOverrideSource sourceTemplate,
		Action<string>? reportIssue = null )
	{
		string[] xmlPaths = resourcePaths
			.Where( path => path.EndsWith( ".xml", StringComparison.OrdinalIgnoreCase ) )
			.Distinct( StringComparer.OrdinalIgnoreCase )
			.ToArray();

		if ( xmlPaths.Length == 0 )
		{
			return Array.Empty<XmlOverrideDefinition>();
		}

		return ReadResources( RemasteredBundleFile.ReadResources( bundlePath, xmlPaths ), sourceTemplate, reportIssue );
	}

	internal static IReadOnlyList<XmlOverrideDefinition> ReadResources(
		IEnumerable<RemasteredBundleResource> resources,
		XmlOverrideSource sourceTemplate,
		Action<string>? reportIssue = null )
	{
		List<XmlOverrideDefinition> definitions = new();

		foreach ( RemasteredBundleResource resource in resources )
		{
			XmlOverrideSource source = sourceTemplate with
			{
				ResourcePath = resource.Path,
				DlcName = GetMountedDlcName( resource.Path ) ?? sourceTemplate.DlcName
			};

			try
			{
				definitions.AddRange( ReadXml( resource.Content, source ) );
			}
			catch ( InvalidDataException exception )
			{
				if ( reportIssue is null )
					throw;

				reportIssue( FormatResourceIssue( source, exception ) );
			}
		}

		return definitions;
	}

	private static string FormatResourceIssue( XmlOverrideSource source, InvalidDataException exception )
	{
		if ( exception.InnerException is not XmlException xml )
			return UiText.F(
				"XmlOverrideConflictAnalyzer",
				$"Invalid XML — {source.ModName}{Environment.NewLine}{source.ResourcePath}{Environment.NewLine}{exception.Message}" );

		Match mismatchedTag = Regex.Match( xml.Message,
			@"^The '(?<open>[^']+)' start tag on line (?<openLine>\d+) position \d+ does not match the end tag of '(?<close>[^']+)'\.",
			RegexOptions.CultureInvariant );
		string explanation = mismatchedTag.Success
			? UiText.F(
				"XmlOverrideConflictAnalyzer",
				$"Found </{mismatchedTag.Groups["close"].Value}>, but the open tag is <{mismatchedTag.Groups["open"].Value}> (line {mismatchedTag.Groups["openLine"].Value})." )
			: Regex.Replace( xml.Message, @"\s*Line \d+, position \d+\.\s*$", string.Empty, RegexOptions.CultureInvariant ).Trim();

		return UiText.F(
			"XmlOverrideConflictAnalyzer",
			$"Invalid XML — {source.ModName}{Environment.NewLine}{source.ResourcePath}{Environment.NewLine}line {xml.LineNumber}, column {xml.LinePosition}{Environment.NewLine}{explanation}" );
	}

	internal static string? GetMountedDlcName( string resourcePath )
	{
		if ( string.IsNullOrWhiteSpace( resourcePath ) )
		{
			return null;
		}

		string[] segments = resourcePath
			.Replace( '/', '\\' )
			.Split( '\\', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries );

		return segments.Length >= 2
			&& segments[0].Equals( "dlc", StringComparison.OrdinalIgnoreCase )
			? segments[1]
			: null;
	}

	internal static IReadOnlyList<XmlOverrideDefinition> ReadXml(
		byte[] content,
		XmlOverrideSource source )
	{
		ArgumentNullException.ThrowIfNull( content );
		ArgumentNullException.ThrowIfNull( source );

		try
		{
			using MemoryStream stream = new( content, writable: false );
			XDocument document = GameXmlReader.Load( stream, new XmlReaderSettings
			{
				DtdProcessing = DtdProcessing.Prohibit,
				IgnoreComments = true,
				IgnoreProcessingInstructions = true,
				MaxCharactersFromEntities = 0,
				MaxCharactersInDocument = 64L * 1024L * 1024L,
				XmlResolver = null
			}, LoadOptions.None );
			XElement? definitions = document.Root?.Elements()
				.FirstOrDefault( element => element.Name.LocalName.Equals( "definitions", StringComparison.OrdinalIgnoreCase ) );

			if ( definitions is null )
			{
				return Array.Empty<XmlOverrideDefinition>();
			}

			List<XmlOverrideDefinition> result = new();

			foreach ( XElement container in definitions.Elements() )
			{
				foreach ( XElement definition in container.Elements() )
				{
					if ( !SupportedDefinitionTypes.Contains( definition.Name.LocalName ) )
					{
						continue;
					}

					XAttribute? identityAttribute = IdentityAttributes
						.Select( name => FindAttribute( definition, name ) )
						.FirstOrDefault( attribute => attribute is not null );

					if ( identityAttribute is null )
					{
						continue;
					}

					// Keep distinct Unicode names separate when identifying game definitions.
					string identity = NormalizeIdentity( identityAttribute.Value );

					if ( identity.Length == 0 )
					{
						continue;
					}

					string context = GetContext( source.ResourcePath );
					string mode = NormalizeValue( FindAttribute( definition, "on_conflict" )?.Value ?? string.Empty )
						.ToLowerInvariant();
					Dictionary<string, string> parameters = ReadParameters( definition, identityAttribute.Name.LocalName );

					result.Add( new XmlOverrideDefinition(
						new XmlOverrideDefinitionKey(
							context,
							container.Name.LocalName.ToLowerInvariant(),
							definition.Name.LocalName.ToLowerInvariant(),
							identity ),
						mode,
						parameters,
						source,
						ReadEquipmentVariants( definition ), result.Count ) );
				}
			}

			return result;
		}
		catch ( XmlException exception )
		{
			throw new InvalidDataException(
				UiText.F(
					"XmlOverrideConflictAnalyzer",
					$"{source.ResourcePath}: invalid XML at line {exception.LineNumber}, column {exception.LinePosition} ({exception.Message})" ),
				exception );
		}
		catch ( DecoderFallbackException exception )
		{
			throw new InvalidDataException(
				UiText.F( "XmlOverrideConflictAnalyzer", $"{source.ResourcePath}: invalid UTF-8 byte sequence." ),
				exception );
		}
	}

	// Cards and replace-mode definitions conflict as a whole. Other definitions can be compared parameter by parameter.
	public static IReadOnlyList<XmlOverrideConflict> Analyze(
		IEnumerable<XmlOverrideDefinition> definitions,
		IReadOnlyDictionary<string, string>? vanillaItemCategories = null )
	{
		ArgumentNullException.ThrowIfNull( definitions );

		XmlOverrideDefinition[] declarations = definitions.ToArray();
		var itemCategories = XmlEquipmentVariantAnalysis.ItemCategories( declarations, vanillaItemCategories );
		List<XmlOverrideConflict> conflicts = new();

		foreach ( IGrouping<string, XmlOverrideDefinition> definitionGroup in declarations
			.GroupBy( definition => definition.Key.ComparisonKey, StringComparer.OrdinalIgnoreCase ) )
		{
			XmlOverrideDefinitionKey key = definitionGroup.First().Key;
			AggregatedDefinition[] sources = definitionGroup
				.GroupBy( definition => definition.Source.ModName, StringComparer.OrdinalIgnoreCase )
				.Select( AggregateSource )
				.ToArray();

			if ( sources.Length < 2 )
			{
				continue;
			}

			if ( key.DefinitionType.Equals( "card", StringComparison.OrdinalIgnoreCase )
				|| sources.Any( source => source.IsReplace ) )
			{
				conflicts.Add( new XmlOverrideConflict(
					key,
					"[replace]",
					IsWholeDefinitionReplacement: true,
					sources.Select( source => source.ToConflictSource( "whole definition" ) ).ToArray() ) );

				continue;
			}

			foreach ( string parameter in sources.SelectMany( source => source.Parameters.Keys )
				.Distinct( StringComparer.OrdinalIgnoreCase )
				.OrderBy( value => value, StringComparer.OrdinalIgnoreCase ) )
			{
				// AnalyzeVariants compares appearance lists, so item and item_extension are checked together.
				if ( parameter == "variants" && key.DefinitionType is "item" or "item_extension" )
					continue;

				AggregatedDefinition[] writers = sources
					.Where( source => source.Parameters.ContainsKey( parameter ) )
					.ToArray();

				if ( writers.Length < 2 )
				{
					continue;
				}

				if ( writers.Select( source => source.Parameters[parameter] )
					.Distinct( StringComparer.Ordinal ).Count() < 2 )
				{
					continue;
				}

				conflicts.Add( new XmlOverrideConflict(
					key,
					parameter,
					IsWholeDefinitionReplacement: false,
					writers.Select( source => source.ToConflictSource( source.Parameters[parameter] ) ).ToArray() ) );
			}
		}

		conflicts.AddRange( AnalyzeVariants( declarations, itemCategories ) );

		return conflicts
			.OrderBy( conflict => conflict.DisplayPath, StringComparer.OrdinalIgnoreCase )
			.ToArray();
	}

	// Find mods that change the same item's appearance through item or item_extension.
	// Compare complete item lists and check extension rules for overlapping equipment conditions.
	private static IEnumerable<XmlOverrideConflict> AnalyzeVariants( XmlOverrideDefinition[] declarations,
		IReadOnlyDictionary<string, string> itemCategories )
	{
		var targets = declarations.Where( definition => definition.Key.DefinitionType is "item" or "item_extension"
			&& definition.Parameters.ContainsKey( "variants" ) )
			.GroupBy( definition => new XmlOverrideDefinitionKey( definition.Key.Context, "items", "item", definition.Key.Identity )
				.ComparisonKey, StringComparer.OrdinalIgnoreCase );

		foreach ( var target in targets )
		{
			XmlOverrideDefinition[] sections = target.ToArray();
			bool sameType = sections.Select( definition => definition.Key.DefinitionType ).Distinct().Count() == 1;

			if ( sameType && sections.Any( definition => definition.ConflictMode == "replace" ) )
				continue;

			AggregatedDefinition[] writers = sections.GroupBy( definition => definition.Source.ModName, StringComparer.OrdinalIgnoreCase )
				.Select( AggregateSource ).ToArray();

			if ( writers.Length < 2 )
				continue;

			XmlOverrideDefinitionKey key = sections[0].Key;

			// Show one conflict row for the item. Each mod keeps its item or item_extension type.
			if ( !sameType )
				key = key with { Container = "items", DefinitionType = "item" };

			IReadOnlyList<XmlVariantCompetition>? competitions = null;
			bool comparable = writers.All( source => source.ConflictModes.All( mode => mode is "default" or "extend" )
				&& source.EquipmentVariants != null && source.VariantDefinitionType != null );

			if ( comparable )
			{
				var pairs = new List<XmlVariantCompetition>();

				for ( int i = 0; i < writers.Length; i++ )
					for ( int j = i + 1; j < writers.Length; j++ )
					{
						AggregatedDefinition left = writers[i];
						AggregatedDefinition right = writers[j];
						bool itemLists = left.VariantDefinitionType == "item" && right.VariantDefinitionType == "item";

						// The game uses one complete item list, even when the lists cover different equipment.
						if ( itemLists )
						{
							if ( left.Parameters["variants"] != right.Parameters["variants"] )
								pairs.Add( new( left.Source.ModName, right.Source.ModName, true, XmlVariantInteraction.ItemLists ) );

							continue;
						}

						bool? overlap = XmlEquipmentVariantAnalysis.Compete( left.EquipmentVariants!, right.EquipmentVariants!,
							key.Context, itemCategories );

						if ( overlap != false )
							pairs.Add( new( left.Source.ModName, right.Source.ModName, overlap == true,
								left.VariantDefinitionType == right.VariantDefinitionType
									? XmlVariantInteraction.EquipmentRules : XmlVariantInteraction.ExtensionOverItem ) );
					}

				if ( pairs.Count == 0 )
					continue;

				competitions = pairs;

				if ( sameType && key.DefinitionType == "item_extension" )
					writers = writers.Where( source => pairs.Any( pair => pair.LeftMod == source.Source.ModName
						|| pair.RightMod == source.Source.ModName ) ).ToArray();
			}
			else if ( writers.Select( source => source.Parameters["variants"] ).Distinct( StringComparer.Ordinal ).Count() < 2 )
				continue;

			yield return new( key, "variants", false,
				writers.Select( source => source.ToConflictSource( source.Parameters["variants"] ) ).ToArray(), competitions );
		}
	}

	// Later sections override earlier parameters. Variant sections in an item extension accumulate;
	// combine their rules only when their order is known.
	private static AggregatedDefinition AggregateSource(
		IGrouping<string, XmlOverrideDefinition> group )
	{
		XmlOverrideDefinition[] definitions = group.ToArray();
		Dictionary<string, string> parameters = new( StringComparer.OrdinalIgnoreCase );

		foreach ( XmlOverrideDefinition definition in definitions )
		{
			foreach ( (string key, string value) in definition.Parameters )
			{
				parameters[key] = value;
			}
		}

		XmlOverrideDefinition[] variantSections = definitions.Where( definition => definition.Parameters.ContainsKey( "variants" ) )
			.OrderBy( definition => definition.DeclarationOrder ).ToArray();
		string? variantDefinitionType = definitions.Select( definition => definition.Key.DefinitionType ).Distinct().Count() == 1
			? definitions[0].Key.DefinitionType : null;
		bool additiveSections = variantDefinitionType == "item_extension"
			&& definitions.All( definition => definition.ConflictMode is "" or "default" or "extend" );

		// Keep every list when the sections add up, or when they mix item and item_extension.
		if ( ( additiveSections || variantDefinitionType == null ) && variantSections.Length > 1 )
			parameters["variants"] = string.Join( "|", variantSections.Select( definition => definition.Parameters["variants"] ) );

		bool orderedSections = variantSections.Length <= 1 || variantSections.All( definition => definition.DeclarationOrder.HasValue )
			&& variantSections.Select( definition => definition.DeclarationOrder ).Distinct().Count() == variantSections.Length;
		// Use declaration order only when all sections come from the same XML file.
		bool knownSections = orderedSections && variantSections.Length > 0
			&& definitions.All( definition => SameXmlResource( definition.Source, definitions[0].Source ) )
			&& variantSections.All( definition => definition.EquipmentVariants != null );
		XmlEquipmentVariants? equipmentVariants = null;

		// Each item_extension section adds its rules. An empty section leaves earlier rules in place.
		if ( knownSections && additiveSections )
			equipmentVariants = new( variantSections.SelectMany( definition => definition.EquipmentVariants!.Rules ).ToArray() );
		else if ( knownSections && variantDefinitionType == "item"
			&& definitions.OrderBy( definition => definition.DeclarationOrder ).Skip( 1 )
				.All( definition => definition.ConflictMode == "extend" ) )
		{
			// Use the last explicit variants list, including an empty list that clears earlier rules.
			// A section without variants leaves the previous list in place.
			equipmentVariants = variantSections[^1].EquipmentVariants;
			parameters["variants"] = variantSections[^1].Parameters["variants"];
		}

		return new AggregatedDefinition(
			definitions[0].Source,
			definitions.Any( definition => definition.ConflictMode.Equals( "replace", StringComparison.OrdinalIgnoreCase ) ),
			definitions.Select( definition => definition.ConflictMode )
				.FirstOrDefault( mode => !string.IsNullOrWhiteSpace( mode ) ) ?? "default",
			parameters,
			definitions.Select( definition => definition.Source.ResourcePath )
				.Distinct( StringComparer.OrdinalIgnoreCase )
				.ToArray(),
			definitions.Select( definition => string.IsNullOrWhiteSpace( definition.ConflictMode ) ? "default" : definition.ConflictMode )
				.Distinct( StringComparer.OrdinalIgnoreCase ).ToArray(), equipmentVariants, definitions.Length, variantDefinitionType );
	}

	private static bool SameXmlResource( XmlOverrideSource left, XmlOverrideSource right ) =>
		string.Equals( left.BundlePath?.Replace( '/', '\\' ), right.BundlePath?.Replace( '/', '\\' ), StringComparison.OrdinalIgnoreCase )
		&& string.Equals(
			left.ResourcePath?.Replace( '/', '\\' ),
			right.ResourcePath?.Replace( '/', '\\' ),
			StringComparison.OrdinalIgnoreCase )
		&& string.Equals( left.DlcName, right.DlcName, StringComparison.OrdinalIgnoreCase );

	// An unsupported rule leaves the comparison uncertain.
	private static XmlEquipmentVariants? ReadEquipmentVariants( XElement definition )
	{
		XElement[] lists = definition.Elements().Where( element => element.Name.LocalName.Equals( "variants", StringComparison.OrdinalIgnoreCase ) ).ToArray();
		// Treat <variants/> as an empty list. Return null when the list is missing or cannot be read.
		if ( lists.Length == 0 || lists.Any( list => list.HasAttributes
			|| !list.HasElements && !string.IsNullOrWhiteSpace( list.Value ) ) )
			return null;

		var rules = new List<XmlEquipmentRule>();

		foreach ( XElement rule in lists.SelectMany( list => list.Elements() ) )
		{
			XElement[] conditions = rule.Elements().ToArray();
			string? currentTemplate = FindAttribute( rule, "equip_template" )?.Value;

			if ( !rule.Name.LocalName.Equals( "variant", StringComparison.OrdinalIgnoreCase )
				|| rule.Attributes().Count() != 2 || FindAttribute( rule, "all" )?.Value != "true"
				|| string.IsNullOrWhiteSpace( currentTemplate ) || conditions.Length == 0
				|| conditions.Any( condition => !condition.Name.LocalName.Equals( "item", StringComparison.OrdinalIgnoreCase )
					|| condition.HasAttributes || condition.HasElements || string.IsNullOrWhiteSpace( condition.Value ) ) )
				return null;

			rules.Add( new( NormalizeIdentity( currentTemplate ), conditions.Select( condition => NormalizeIdentity( condition.Value ) )
				.Distinct( StringComparer.Ordinal ).ToArray() ) );
		}

		return new XmlEquipmentVariants( rules.ToArray() );
	}

	// The definition's identity and on_conflict setting control how it is applied; compare its remaining values.
	private static Dictionary<string, string> ReadParameters(
		XElement definition,
		string identityAttributeName )
	{
		Dictionary<string, string> parameters = new( StringComparer.OrdinalIgnoreCase );

		foreach ( XAttribute attribute in definition.Attributes() )
		{
			if ( attribute.Name.LocalName.Equals( identityAttributeName, StringComparison.OrdinalIgnoreCase )
				|| attribute.Name.LocalName.Equals( "on_conflict", StringComparison.OrdinalIgnoreCase ) )
			{
				continue;
			}

			parameters["@" + attribute.Name.LocalName.ToLowerInvariant()] = NormalizeValue( attribute.Value );
		}

		foreach ( IGrouping<string, XElement> childGroup in definition.Elements()
			.GroupBy( child => child.Name.LocalName, StringComparer.OrdinalIgnoreCase ) )
		{
			if ( childGroup.Key.Equals( "tags", StringComparison.OrdinalIgnoreCase ) )
			{
				ReadTags( childGroup, parameters );

				continue;
			}

			string canonical = string.Join( "|", childGroup.Select( CanonicalizeElement ) );

			parameters[childGroup.Key.ToLowerInvariant()] = canonical;
		}

		return parameters;
	}

	// Tags can be child elements or a comma-separated list. A leading minus removes a tag.
	private static void ReadTags(
		IEnumerable<XElement> tagElements,
		IDictionary<string, string> parameters )
	{
		foreach ( XElement tags in tagElements )
		{
			IEnumerable<string> tokens = tags.HasElements
				? tags.DescendantsAndSelf()
					.SelectMany( element => element.Attributes( "name" ).Select( attribute => attribute.Value ) )
				: tags.Value.Split( ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries );

			foreach ( string rawToken in tokens )
			{
				string token = NormalizeValue( rawToken.Trim( '"', '\'', ' ' ) );

				if ( token.Length == 0 )
				{
					continue;
				}

				bool remove = token.StartsWith( "-", StringComparison.Ordinal );
				string tagName = NormalizeValue( remove ? token[1..] : token );

				if ( tagName.Length > 0 )
				{
					parameters["tags:" + tagName.ToLowerInvariant()] = remove ? "remove" : "add";
				}
			}
		}
	}

	// Ignore attribute order and spacing when comparing XML content.
	private static string CanonicalizeElement( XElement element )
	{
		StringBuilder builder = new();

		builder.Append( '<' ).Append( element.Name.LocalName.ToLowerInvariant() );

		foreach ( XAttribute attribute in element.Attributes()
			.OrderBy( attribute => attribute.Name.LocalName, StringComparer.OrdinalIgnoreCase ) )
		{
			builder.Append( ' ' ).Append( attribute.Name.LocalName.ToLowerInvariant() )
				.Append( '=' ).Append( NormalizeValue( attribute.Value ) );
		}

		builder.Append( '>' );

		if ( element.HasElements )
		{
			foreach ( XElement child in element.Elements() )
			{
				builder.Append( CanonicalizeElement( child ) );
			}
		}
		else
		{
			builder.Append( NormalizeValue( element.Value ) );
		}

		builder.Append( "</" ).Append( element.Name.LocalName.ToLowerInvariant() ).Append( '>' );

		return builder.ToString();
	}

	private static XAttribute? FindAttribute( XElement element, string localName )
	{
		return element.Attributes().FirstOrDefault( attribute =>
			attribute.Name.LocalName.Equals( localName, StringComparison.OrdinalIgnoreCase ) );
	}

	private static string GetContext( string resourcePath )
	{
		string[] segments = resourcePath.Replace( '/', '\\' )
			.Split( '\\', StringSplitOptions.RemoveEmptyEntries );

		return segments.Any( segment => segment.EndsWith( "_plus", StringComparison.OrdinalIgnoreCase ) )
			? "New Game+"
			: "Normal";
	}

	private static string NormalizeValue( string value )
	{
		if ( string.IsNullOrWhiteSpace( value ) )
		{
			return string.Empty;
		}

		StringBuilder builder = new( value.Length );
		bool pendingSpace = false;

		foreach ( char character in value.Normalize( NormalizationForm.FormKC ) )
		{
			if ( char.IsWhiteSpace( character ) || CharUnicodeInfo.GetUnicodeCategory( character ) == UnicodeCategory.SpaceSeparator )
			{
				pendingSpace = builder.Length > 0;

				continue;
			}

			if ( pendingSpace )
			{
				builder.Append( ' ' );
				pendingSpace = false;
			}

			builder.Append( character );
		}

		return builder.ToString();
	}

	private static string NormalizeIdentity( string value )
	{
		return string.IsNullOrEmpty( value )
			? string.Empty
			: value.Trim( ' ', '\t', '\r', '\n' );
	}

	private sealed record AggregatedDefinition(
		XmlOverrideSource Source,
		bool IsReplace,
		string ConflictMode,
		IReadOnlyDictionary<string, string> Parameters,
		IReadOnlyList<string> ResourcePaths,
		IReadOnlyList<string> ConflictModes,
		XmlEquipmentVariants? EquipmentVariants,
		int DeclarationCount,
		string? VariantDefinitionType )
	{
		public XmlOverrideConflictSource ToConflictSource( string value )
			=> new( Source, ConflictMode, value, ResourcePaths, ConflictModes, EquipmentVariants, DeclarationCount, VariantDefinitionType );
	}
}
