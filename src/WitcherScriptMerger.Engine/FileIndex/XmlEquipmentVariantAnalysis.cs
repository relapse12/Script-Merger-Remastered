#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace WitcherScriptMerger.FileIndex;

// Only one equipment variant can apply to an item set. Check whether the mods compete for that choice.
internal static class XmlEquipmentVariantAnalysis
{
	private static readonly HashSet<string> SingleItemCategories = new( StringComparer.Ordinal )
		{ "armor", "gloves", "pants", "boots", "head", "hair", "mask" };

	internal static string ItemKey(
		string context,
		string item ) => new XmlOverrideDefinitionKey( context, "items", "item", item ).ComparisonKey;

	// An empty category means the item's slot is uncertain, so it cannot establish whether two rules compete.
	internal static IReadOnlyDictionary<string, string> ItemCategories( IEnumerable<XmlOverrideDefinition> definitions,
		IReadOnlyDictionary<string, string>? vanillaCategories )
	{
		var categories = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );

		if ( vanillaCategories != null )
			foreach ( var item in vanillaCategories )
				categories[item.Key] = item.Value;

		foreach ( var definition in definitions.Where( value => value.Key.Container == "items" && value.Key.DefinitionType == "item" ) )
		{
			string key = definition.Key.ComparisonKey;

			definition.Parameters.TryGetValue( "@category", out string? category );

			if ( definition.Parameters.TryGetValue( "@equip_slot", out string? slot ) && !( category == "mask" && slot == "head" )
				|| definition.ConflictMode == "replace" && !definition.Parameters.ContainsKey( "@category" ) )
			{
				categories[key] = string.Empty;

				continue;
			}

			if ( category == null )
				continue;

			if ( categories.TryGetValue( key, out string? existing ) && existing != category )
				categories[key] = string.Empty;
			else
				categories[key] = category;
		}

		return categories;
	}

	// Return null when the rules cannot be compared reliably. Two items occupying the same slot cannot match together.
	internal static bool? Compete( XmlEquipmentVariants left, XmlEquipmentVariants right, string context,
		IReadOnlyDictionary<string, string> categories )
	{
		bool unknown = false;
		XmlEquipmentRule[] leftRules = EffectiveRules( left ).ToArray();
		XmlEquipmentRule[] rightRules = EffectiveRules( right ).ToArray();

		foreach ( var a in leftRules )
			foreach ( var b in rightRules )
			{
				if ( a.EquipTemplate == b.EquipTemplate )
					continue;

				string[] items = a.Items.Concat( b.Items ).Distinct( StringComparer.OrdinalIgnoreCase ).ToArray();

				string? Category( string item ) => categories.TryGetValue( ItemKey( context, item ), out string? value )
					&& SingleItemCategories.Contains( value ) ? value : null;

				if ( items.Where( item => Category( item ) != null ).GroupBy( Category ).Any( group => group.Count() > 1 ) )
					continue;

				if ( a.Items.OrderBy( item => item, StringComparer.OrdinalIgnoreCase ).SequenceEqual(
					b.Items.OrderBy( item => item, StringComparer.OrdinalIgnoreCase ),
					StringComparer.OrdinalIgnoreCase )
					|| items.All( item => Category( item ) is "armor" or "mask" ) )
					return true;

				unknown = true;
			}

		return unknown ? null : false;
	}

	// A later rule can cover an earlier one completely, making the earlier rule irrelevant to this comparison.
	private static IEnumerable<XmlEquipmentRule> EffectiveRules( XmlEquipmentVariants variants ) => variants.Rules
		.Where( ( rule, index ) => !variants.Rules.Skip( index + 1 )
				.Any( later => later.Items.All( item => rule.Items.Contains( item, StringComparer.OrdinalIgnoreCase ) ) ) );
}
