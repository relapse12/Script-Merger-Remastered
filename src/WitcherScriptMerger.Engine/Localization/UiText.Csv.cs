#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace WitcherScriptMerger;

internal static partial class UiText
{
	internal static readonly string[] CsvColumns = { "key", "context", "english", "translation", "developer_comment" };

	internal static void ExportCsvTemplate( string path )
	{
		var text = new StringBuilder();

		AppendCsvRow( text, CsvColumns );

		foreach ( var item in Catalog.OrderBy( item => item.Key, StringComparer.Ordinal ) )
			AppendCsvRow( text, new[] { item.Key, item.Value.Context, item.Value.English, "", TranslatorComment( item.Value ) } );

		// A UTF-8 BOM lets spreadsheet editors recognize non-Latin translations.
		WriteAtomically( path, "\uFEFF" + text );
	}

	internal static LanguagePack InspectCsv( string path, string code, string name )
	{
		(code, name) = ValidateLanguageInfo( code, name );

		if ( new FileInfo( path ).Length > MaximumPackBytes )
			throw new InvalidDataException( "The translation CSV exceeds the 8 MiB size limit." );

		List<string[]> rows = ReadCsv( StrictUtf8.GetString( File.ReadAllBytes( path ) ).TrimStart( '\uFEFF' ) );

		if ( rows.Count == 0 )
			throw new InvalidDataException( "The translation CSV is empty." );

		string[] header = rows[0];

		if ( header.Length != CsvColumns.Length || header.Distinct( StringComparer.OrdinalIgnoreCase ).Count() != header.Length ||
			CsvColumns.Any( column => !header.Contains( column, StringComparer.Ordinal ) ) )
			throw new InvalidDataException( "The CSV must contain these columns exactly once: " + string.Join( ", ", CsvColumns ) );

		int KeyColumn = Array.IndexOf( header, "key" ), ContextColumn = Array.IndexOf( header, "context" ),
			EnglishColumn = Array.IndexOf( header, "english" ), TranslationColumn = Array.IndexOf( header, "translation" );
		var seen = new HashSet<string>( StringComparer.Ordinal );
		var translations = new Dictionary<string, string>( StringComparer.Ordinal );

		for ( int index = 1; index < rows.Count; index++ )
		{
			string[] row = rows[index];

			if ( row.Length != header.Length )
				throw new InvalidDataException( $"CSV row {index + 1} has the wrong number of fields." );

			string key = row[KeyColumn];

			if ( !seen.Add( key ) )
				throw new InvalidDataException( "Duplicate CSV key: " + key );

			if ( !Catalog.TryGetValue( key, out TextEntry? entry ) )
				throw new InvalidDataException( "Unknown CSV key: " + key );

			if ( row[ContextColumn] != entry.Context || NormalizeNewlines( row[EnglishColumn] ) != NormalizeNewlines( entry.English ) )
				throw new InvalidDataException( "The context or English text has changed. Export a fresh template: " + key );

			string translated = row[TranslationColumn];

			if ( translated.Length > 32768 || translated.Contains( '\0' ) )
				throw new InvalidDataException( "Invalid translation text: " + key );

			if ( translated.Length == 0 )
				continue;

			if ( entry.Formatted )
				ValidateFormat( entry.English, translated, key );

			translations.Add( key, translated );
		}

		if ( seen.Count != Catalog.Count )
			throw new InvalidDataException( "The CSV is missing catalog keys. Export a fresh template and edit only the translation column." );

		return new( code, name, translations );
	}

	internal static LanguagePack ImportCsv( string path, string code, string name, bool overwrite ) =>
		Install( InspectCsv( path, code, name ), overwrite );

	private static string NormalizeNewlines( string value ) => value.Replace( "\r\n", "\n" ).Replace( '\r', '\n' );

	internal static void AppendCsvRow( StringBuilder text, IEnumerable<string> fields ) =>
		text.AppendJoin( ',', fields.Select( field => "\"" + field.Replace( "\"", "\"\"" ) + "\"" ) ).Append( "\r\n" );

	private enum CsvState
	{
		Start, Text, Quoted, Closed
	}

	internal static List<string[]> ReadCsv( string text )
	{
		var rows = new List<string[]>();
		var fields = new List<string>();
		var field = new StringBuilder();
		CsvState state = CsvState.Start;

		void EndField()
		{
			if ( fields.Count >= CsvColumns.Length )
				throw new InvalidDataException( "The CSV has too many fields in a row." );

			if ( field.Length > 32768 )
				throw new InvalidDataException( "A CSV field exceeds the 32768-character limit." );

			fields.Add( field.ToString() );
			field.Clear();
			state = CsvState.Start;
		}

		void EndRow()
		{
			EndField();

			if ( fields.Any( value => value.Length != 0 ) )
			{
				if ( rows.Count > Catalog.Count )
					throw new InvalidDataException( "The CSV has more rows than the current translation catalog." );

				rows.Add( fields.ToArray() );
			}

			fields.Clear();
		}

		for ( int index = 0; index < text.Length; index++ )
		{
			if ( field.Length > 32768 )
				throw new InvalidDataException( "A CSV field exceeds the 32768-character limit." );

			char value = text[index];

			if ( state == CsvState.Quoted )
			{
				if ( value != '"' )
				{
					field.Append( value );

					continue;
				}

				if ( index + 1 < text.Length && text[index + 1] == '"' )
				{
					field.Append( '"' );
					index++;
				}
				else
					state = CsvState.Closed;
			}
			else if ( value == ',' )
				EndField();
			else if ( value is '\r' or '\n' )
			{
				EndRow();

				if ( value == '\r' && index + 1 < text.Length && text[index + 1] == '\n' )
					index++;
			}
			else if ( value == '"' && state == CsvState.Start )
				state = CsvState.Quoted;
			else
			{
				if ( state == CsvState.Closed || value == '"' )
					throw new InvalidDataException( "Invalid CSV quoting. Save the file as a comma-separated UTF-8 CSV." );

				field.Append( value );
				state = CsvState.Text;
			}
		}

		if ( state == CsvState.Quoted )
			throw new InvalidDataException( "The CSV ends inside a quoted field." );

		if ( state != CsvState.Start || fields.Count != 0 )
			EndRow();

		return rows;
	}
}
