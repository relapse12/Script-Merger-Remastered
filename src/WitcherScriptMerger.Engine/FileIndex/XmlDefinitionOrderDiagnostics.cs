using System;
using System.Collections.Generic;
using System.Linq;
using WitcherScriptMerger.Inventory;
using WitcherScriptMerger.LoadOrder;

namespace WitcherScriptMerger.FileIndex;

// Finds XML items where one mod defines an item and another extends it, which only works in the right order.
internal static class XmlDefinitionOrderDiagnostics
{
	// Only the one-definition, one-extension case has a clear ordering requirement here.
	internal static IReadOnlyList<ModFile> CreateInputs( IEnumerable<XmlOverrideDefinition> definitions )
	{
		var result = new List<ModFile>();

		foreach ( var group in definitions.Where( value => value.Key.DefinitionType == "item" )
			.GroupBy( value => value.Key.ComparisonKey, StringComparer.OrdinalIgnoreCase ) )
		{
			var sources = group.GroupBy( value => value.Source.ModName, StringComparer.OrdinalIgnoreCase ).ToArray();

			if ( sources.Length != 2 || sources.Any( source => source.Count() != 1 ) )
				continue;

			var pair = sources.Select( source => source.Single() ).ToArray();

			string Mode( XmlOverrideDefinition value ) => string.IsNullOrWhiteSpace( value.ConflictMode ) ? "default" : value.ConflictMode;

			if ( !pair.Any( value => Mode( value ) == "default" ) || !pair.Any( value => Mode( value ) == "extend" ) )
				continue;

			var input = new ModFile( group.Key ) { IndexedCategoryOverride = Categories.XmlDefinitionOverride };

			foreach ( var declaration in pair )
				input.Mods.Add( new FileHash
				{
					Name = declaration.Source.ModName,
					RuntimeOrderName = declaration.Source.DlcName,
					SourceDirectory = declaration.Source.SourceDirectory,
					Origin = declaration.Source.Origin,
					ModIoId = declaration.Source.ModIoId,
					XmlOverrideInfo = new( group.First().Key, "@definition_order", "", false, new[] { Mode( declaration ) } )
				} );

			result.Add( input );
		}

		return result;
	}

	// Group matching warnings for the same two mods and list each affected item once.
	internal static IReadOnlyList<string> Format( IEnumerable<ModFile> files, IEnumerable<ModLoadSetting> settings )
	{
		ModLoadSetting[] currentSettings = settings?.ToArray() ?? Array.Empty<ModLoadSetting>();
		var issues = files.Where( file => ReferenceEquals( file.Category, Categories.XmlDefinitionOverride ) )
			.Select( file => (File: file, Resolution: CustomLoadOrder.ResolveXmlOverridePolicyForSettings( currentSettings, file )) )
			.Where( value => value.Resolution.Outcome is XmlOverrideOutcome.ExtensionBeforeDefault
				or XmlOverrideOutcome.DisabledDefaultDefinition );
		var groups = issues.GroupBy( issue => (
			issue.Resolution.Outcome,
			issue.Resolution.ExtensionSource,
			issue.Resolution.DefaultSource ) )
			.OrderBy( group => group.Key.ExtensionSource, StringComparer.OrdinalIgnoreCase )
			.ThenBy( group => group.Key.DefaultSource, StringComparer.OrdinalIgnoreCase )
			.ThenBy( group => group.Key.Outcome );
		var result = new List<string>();

		foreach ( var group in groups )
		{
			string explanation = group.Key.Outcome == XmlOverrideOutcome.DisabledDefaultDefinition
				? UiText.Format(
					"XmlDefinitionDiagnostics",
					"The default definitions from {0} are disabled. {1} extends the following items.\nEnable {0} before using the extension.",
					group.Key.DefaultSource,
					group.Key.ExtensionSource )
				: UiText.Format(
					"XmlDefinitionDiagnostics",
					"{0} extends the following items before {1} defines them. Some item properties may be missing.",
					group.Key.ExtensionSource,
					group.Key.DefaultSource );
			var lines = new List<string>
			{
				UiText.T( "XmlDefinitionDiagnostics", "XML definition order" ),
				explanation.ReplaceLineEndings( Environment.NewLine )
			};
			var items = group.Select( issue => issue.File.Mods[0].XmlOverrideInfo.Key.Identity )
				.Distinct( StringComparer.OrdinalIgnoreCase )
				.OrderBy( name => name, StringComparer.OrdinalIgnoreCase );

			lines.Add( "" );
			lines.AddRange( items.Select( name => "  - " + name ) );

			result.Add( string.Join( Environment.NewLine, lines ) );
		}

		return result;
	}
}
