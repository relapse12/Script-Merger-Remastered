using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using WitcherScriptMerger.GameContent;
using WitcherScriptMerger.Tools;

namespace WitcherScriptMerger.FileIndex;

internal sealed record XmlItemCatalog( IReadOnlySet<string> Keys, IReadOnlyDictionary<string, string> Categories );

// Game XML can repeat attributes, so read item tags without the standard XML parser.
internal static class VanillaXmlItemDefinitions
{
	private static readonly Regex Tokens = new(
		"<!--[\\s\\S]*?-->|<!\\[CDATA\\[[\\s\\S]*?\\]\\]>|<\\?[\\s\\S]*?\\?>|<(?<closing>/)?(?<name>[A-Za-z_][A-Za-z0-9_.:-]*)(?:[^<>\"']|\"[^\"]*\"|'[^']*')*?>",
		RegexOptions.CultureInvariant );
	private static readonly Regex Attributes = new(
		"(?<attribute>[A-Za-z_][A-Za-z0-9_.:-]*)\\s*=\\s*(?<quoted>\"[^\"]*\"|'[^']*')",
		RegexOptions.CultureInvariant );

	internal static IReadOnlySet<string> Read( string gameDirectory )
		=> ReadCatalog( gameDirectory )?.Keys;

	internal static XmlItemCatalog ReadCatalog( string gameDirectory )
	{
		var keys = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		var categories = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );

		foreach ( string bundle in OfficialGameContent.DiscoverBundleGroups( gameDirectory ).SelectMany( group => group ) )
		{
			string[] paths = RemasteredBundleFile.ListPaths( bundle ).Where( IsItemXml ).ToArray();

			if ( paths.Length == 0 )
				continue;

			foreach ( var resource in RemasteredBundleFile.ReadResources( bundle, paths ) )
			{
				var catalog = ReadCatalog( resource.Content, resource.Path );

				keys.UnionWith( catalog.Keys );

				foreach ( var item in catalog.Categories )
					categories[item.Key] = categories.TryGetValue( item.Key, out string value ) && value != item.Value ? string.Empty : item.Value;
			}
		}

		return keys.Count > 0 ? new XmlItemCatalog( keys, categories ) : null;
	}

	internal static IReadOnlySet<string> ReadNames( byte[] bytes, string path )
		=> ReadCatalog( bytes, path ).Keys;

	// Keep a category only when the item declares one consistently. The equip_slot restriction allows the head slot for masks.
	// DTDs and unbalanced elements make the item catalog unusable.
	internal static XmlItemCatalog ReadCatalog( byte[] bytes, string path )
	{
		if ( bytes.Length > 64 * 1024 * 1024 )
			throw new InvalidDataException( "Vanilla item XML exceeds the read limit." );

		Encoding encoding = new UTF8Encoding( false, true );
		int offset = 0;

		if ( bytes.AsSpan().StartsWith( new byte[] { 0xef, 0xbb, 0xbf } ) )
			offset = 3;
		else if ( bytes.AsSpan().StartsWith( new byte[] { 0xff, 0xfe, 0, 0 } ) )
		{
			offset = 4;
			encoding = new UTF32Encoding( false, false, true );
		}
		else if ( bytes.AsSpan().StartsWith( new byte[] { 0, 0, 0xfe, 0xff } ) )
		{
			offset = 4;
			encoding = new UTF32Encoding( true, false, true );
		}
		else if ( bytes.AsSpan().StartsWith( new byte[] { 0xff, 0xfe } ) )
		{
			offset = 2;
			encoding = new UnicodeEncoding( false, false, true );
		}
		else if ( bytes.AsSpan().StartsWith( new byte[] { 0xfe, 0xff } ) )
		{
			offset = 2;
			encoding = new UnicodeEncoding( true, false, true );
		}
		else if ( bytes.AsSpan().StartsWith( new byte[] { 0x3c, 0, 0, 0 } ) )
			encoding = new UTF32Encoding( false, false, true );
		else if ( bytes.AsSpan().StartsWith( new byte[] { 0, 0, 0, 0x3c } ) )
			encoding = new UTF32Encoding( true, false, true );
		else if ( bytes.Length >= 2 && bytes[0] == 0x3c && bytes[1] == 0 )
			encoding = new UnicodeEncoding( false, false, true );
		else if ( bytes.Length >= 2 && bytes[0] == 0 && bytes[1] == 0x3c )
			encoding = new UnicodeEncoding( true, false, true );

		string text = encoding.GetString( bytes, offset, bytes.Length - offset );

		if ( text.Contains( "<!DOCTYPE", StringComparison.OrdinalIgnoreCase ) )
			throw new InvalidDataException( "Vanilla item XML contains a DTD." );

		string context = path.Replace( '/', '\\' ).Split( '\\' ).Any( segment => segment.EndsWith( "_plus", StringComparison.OrdinalIgnoreCase ) ) ? "New Game+" : "Normal";
		var keys = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		var categories = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
		var stack = new List<string>();

		// Loot XML may repeat unrelated attributes; only item identities are needed.
		foreach ( Match token in Tokens.Matches( text ) )
		{
			if ( !token.Groups["name"].Success )
				continue;

			string name = token.Groups["name"].Value.Split( ':' ).Last().ToLowerInvariant();

			if ( token.Groups["closing"].Success )
			{
				if ( stack.Count == 0 || stack[^1] != name )
					throw new InvalidDataException( "Vanilla item XML has mismatched elements: " + path );

				stack.RemoveAt( stack.Count - 1 );

				continue;
			}

			if ( name == "item" && stack.SequenceEqual( new[] { "redxml", "definitions", "items" } ) )
			{
				var attributes = Attributes.Matches( token.Value ).Cast<Match>()
					.Where( attribute => attribute.Groups["attribute"].Value.Split( ':' ).Last().Equals( "name", StringComparison.OrdinalIgnoreCase ) ).ToArray();

				if ( attributes.Length != 1 )
					throw new InvalidDataException( "Vanilla item identity is missing or duplicated: " + path );

				string identity = XElement.Parse( "<item name=" + attributes[0].Groups["quoted"].Value + "/>" )
					.Attribute( "name" ).Value.Trim( ' ', '\t', '\r', '\n' );
				string key = new XmlOverrideDefinitionKey( context, "items", "item", identity ).ComparisonKey;

				keys.Add( key );

				var allAttributes = Attributes.Matches( token.Value ).Cast<Match>().ToArray();
				var category = allAttributes.Where( attribute => attribute.Groups["attribute"].Value == "category" ).ToArray();
				string value = category.Length == 1
					? XElement.Parse( "<item category=" + category[0].Groups["quoted"].Value + "/>" ).Attribute( "category" ).Value : string.Empty;
				var slots = allAttributes.Where( attribute => attribute.Groups["attribute"].Value == "equip_slot" ).ToArray();

				if ( slots.Length > 0 && !( slots.Length == 1 && value == "mask"
					&& XElement.Parse( "<item equip_slot=" + slots[0].Groups["quoted"].Value + "/>" ).Attribute( "equip_slot" ).Value == "head" ) )
					value = string.Empty;

				categories[key] = categories.TryGetValue( key, out string existing ) && existing != value ? string.Empty : value;
			}

			if ( !token.Value.EndsWith( "/>", StringComparison.Ordinal ) )
				stack.Add( name );
		}

		if ( stack.Count != 0 )
			throw new InvalidDataException( "Vanilla item XML has unclosed elements: " + path );

		return new XmlItemCatalog( keys, categories );
	}

	private static bool IsItemXml( string path )
	{
		if ( !path.EndsWith( ".xml", StringComparison.OrdinalIgnoreCase ) )
			return false;

		string normalized = path.Replace( '/', '\\' );

		return normalized.StartsWith( "gameplay\\items\\", StringComparison.OrdinalIgnoreCase )
			|| normalized.StartsWith( "gameplay\\items_plus\\", StringComparison.OrdinalIgnoreCase )
			|| normalized.Contains( "\\gameplay\\items\\", StringComparison.OrdinalIgnoreCase )
			|| normalized.Contains( "\\gameplay\\items_plus\\", StringComparison.OrdinalIgnoreCase );
	}
}
