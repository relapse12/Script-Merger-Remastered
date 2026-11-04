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

	internal static IReadOnlyList<string> Format( IEnumerable<ModFile> files, IEnumerable<ModLoadSetting> settings )
	{
		ModLoadSetting[] currentSettings = settings?.ToArray() ?? Array.Empty<ModLoadSetting>();

		return files.Where( file => ReferenceEquals( file.Category, Categories.XmlDefinitionOverride ) )
			.Select( file => (File: file, Resolution: CustomLoadOrder.ResolveXmlOverridePolicyForSettings( currentSettings, file )) )
			.Where( value => value.Resolution.Outcome is XmlOverrideOutcome.ExtensionBeforeDefault or XmlOverrideOutcome.DisabledDefaultDefinition )
			.GroupBy( value => value.File.Mods[0].XmlOverrideInfo.Key.ComparisonKey, StringComparer.OrdinalIgnoreCase )
			.OrderBy( group => group.Key, StringComparer.OrdinalIgnoreCase )
			.Select( group =>
			{
				var issue = group.First();
				var key = issue.File.Mods[0].XmlOverrideInfo.Key;
				string explanation = issue.Resolution.Outcome == XmlOverrideOutcome.DisabledDefaultDefinition
					? UiText.Format(
						"XmlDefinitionDiagnostics",
						"The default definition from {0} is disabled. {1} extends this item.\nEnable the default definition before using the extension.",
						issue.Resolution.DefaultSource,
						issue.Resolution.ExtensionSource )
					: UiText.Format( "XmlDefinitionDiagnostics",
						"{0} extends this item before {1} registers its default definition. The item loads with incomplete definition data.",
						issue.Resolution.ExtensionSource, issue.Resolution.DefaultSource );

				return UiText.Format( "XmlDefinitionDiagnostics", "XML definition order — item \"{0}\" ({1})", key.Identity, key.Context )
					+ Environment.NewLine + explanation.ReplaceLineEndings( Environment.NewLine );
			} ).ToArray();
	}
}
