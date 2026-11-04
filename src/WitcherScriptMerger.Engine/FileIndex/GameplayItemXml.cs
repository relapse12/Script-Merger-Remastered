#nullable enable
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace WitcherScriptMerger.FileIndex;

internal static class GameplayItemXml
{
	// Remove repeated stackable attributes from item tags before XML parsing, keeping line positions unchanged.
	// Return null if no change is needed or the XML cannot be read.
	internal static string? RetainFirstStackable( byte[] content )
	{
		string? text = Decode( content );

		return text is null ? null : new StackableScan( text ).Run();
	}

	private static string? Decode( byte[] content )
	{
		Encoding encoding = new UTF8Encoding( false, true );
		int offset = 0;
		string text;
		Match declaration;

		if ( content.AsSpan().StartsWith( new byte[] { 0xef, 0xbb, 0xbf } ) )
		{
			offset = 3;
		}
		else if ( content.AsSpan().StartsWith( new byte[] { 0xff, 0xfe } ) )
		{
			encoding = new UnicodeEncoding( false, true, true );
			offset = 2;
		}
		else if ( content.AsSpan().StartsWith( new byte[] { 0xfe, 0xff } ) )
		{
			encoding = new UnicodeEncoding( true, true, true );
			offset = 2;
		}

		try
		{
			text = encoding.GetString( content, offset, content.Length - offset );
		}
		catch ( DecoderFallbackException )
		{
			return null;
		}

		declaration = Regex.Match( text, @"\A\s*<\?xml\s[^?]*\bencoding\s*=\s*['""](?<name>[^'""]+)['""]", RegexOptions.IgnoreCase );

		if ( !declaration.Success || IsSameEncoding( declaration.Groups["name"].Value, encoding, offset ) )
			return text;

		return null;
	}

	private static bool IsSameEncoding( string declared, Encoding encoding, int markLength )
	{
		return declared.Equals( encoding.WebName, StringComparison.OrdinalIgnoreCase )
			|| encoding.CodePage == Encoding.UTF8.CodePage && WitcherScriptMerger.Tools.GameXmlReader.IsUtf8Alias( declared )
			|| markLength == 2 && declared.Equals( "utf-16", StringComparison.OrdinalIgnoreCase );
	}

	// The game's XML can repeat attributes, which a standard XML parser rejects. Read these tags directly.
	private sealed class StackableScan
	{
		private readonly string text;
		private readonly char[] result;
		private readonly List<string> path = new();
		private int position;
		private bool changed;

		internal StackableScan( string text )
		{
			this.text = text;
			result = text.ToCharArray();
		}

		internal string? Run()
		{
			while ( position < text.Length )
			{
				if ( text[position++] == '<' && !ReadMarkup( position - 1 ) )
					return null;
			}

			return changed && path.Count == 0 ? new string( result ) : null;
		}

		private bool ReadMarkup( int start )
		{
			if ( text.AsSpan( start ).StartsWith( "<!--" ) )
				return SkipTo( "-->", start + 4 );

			if ( text.AsSpan( start ).StartsWith( "<![CDATA[" ) )
				return SkipTo( "]]>", start + 9 );

			if ( position < text.Length && text[position] == '?' )
				return SkipTo( "?>", position + 1 );

			if ( position >= text.Length || text[position] == '!' )
				return false;

			return text[position] == '/' ? ReadClosingTag() : ReadOpeningTag();
		}

		private bool SkipTo( string marker, int from )
		{
			int end = text.IndexOf( marker, from, StringComparison.Ordinal );

			if ( end < 0 )
				return false;

			position = end + marker.Length;

			return true;
		}

		private void SkipWhiteSpace()
		{
			while ( position < text.Length && char.IsWhiteSpace( text[position] ) )
				position++;
		}

		private string ReadName()
		{
			int from = position;

			while ( position < text.Length && ( char.IsLetterOrDigit( text[position] ) || text[position] is '_' or ':' or '-' or '.' ) )
				position++;

			return text[from..position];
		}

		private bool ReadClosingTag()
		{
			string name;

			position++;
			name = ReadName();

			if ( name.Length == 0 )
				return false;

			SkipWhiteSpace();

			if ( position >= text.Length || text[position++] != '>' || path.Count == 0 || path[^1] != name )
				return false;

			path.RemoveAt( path.Count - 1 );

			return true;
		}

		private bool ReadOpeningTag()
		{
			string name = ReadName();
			bool isItem = name == "item" && path.Count == 3
				&& path[0] == "redxml" && path[1] == "definitions" && path[2] == "items";
			bool seenStackable = false;

			if ( name.Length == 0 )
				return false;

			while ( position < text.Length )
			{
				int attributeStart = position;
				string attribute;

				SkipWhiteSpace();

				if ( text.AsSpan( position ).StartsWith( "/>" ) )
				{
					position += 2;

					return true;
				}

				if ( position < text.Length && text[position] == '>' )
				{
					position++;
					path.Add( name );

					return true;
				}

				if ( attributeStart == position || !ReadAttribute( out attribute ) )
					return false;

				if ( isItem && attribute == "stackable" )
				{
					if ( seenStackable )
						BlankFrom( attributeStart );

					seenStackable = true;
				}
			}

			return false;
		}

		private bool ReadAttribute( out string attribute )
		{
			char quote;
			int end;

			attribute = ReadName();
			SkipWhiteSpace();

			if ( attribute.Length == 0 || position >= text.Length || text[position++] != '=' )
				return false;

			SkipWhiteSpace();

			if ( position >= text.Length || text[position] is not ( '\'' or '"' ) )
				return false;

			quote = text[position++];
			end = text.IndexOf( quote, position );

			if ( end < 0 )
				return false;

			position = end + 1;

			return true;
		}

		private void BlankFrom( int start )
		{
			for ( int index = start; index < position; index++ )
			{
				if ( result[index] is not ( '\r' or '\n' ) )
					result[index] = ' ';
			}

			changed = true;
		}
	}
}
