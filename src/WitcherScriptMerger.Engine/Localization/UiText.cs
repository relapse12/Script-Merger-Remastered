#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace WitcherScriptMerger;

internal sealed record LanguageChoice( string Code, string Name )
{
	public override string ToString() => Name;
}

internal sealed record TextEntry( string Context, string English, bool Formatted );
internal sealed record LanguagePack( string Code, string Name, IReadOnlyDictionary<string, string> Strings );

// Keep the English text and arguments until display, so cached messages follow later language changes.
internal sealed record DeferredText( string Text, string? Context = null, DeferredText[]? Arguments = null )
{
	internal string Render() => Context is null ? Text : Arguments is null ? UiText.T( Context, Text )
		: UiText.Format( Context, Text, Arguments.Select( value => (object)value.Render() ).ToArray() );

	public static implicit operator DeferredText?( string? text ) => text is null ? null : UiText.CapturedText( text );
}

// Loads translations from Languages and falls back to English for missing entries.
// The catalog combines ui_text_catalog.json with entries declared in code.
internal static partial class UiText
{
	internal const string ApplicationId = "script-merger-remastered";
	internal const int FormatVersion = 1;
	internal const string CatalogResource = "ScriptMergerRemastered.UiCatalog";
	private const int MaximumPackBytes = 8 * 1024 * 1024;
	private static readonly UTF8Encoding StrictUtf8 = new( false, true );
	private static readonly Regex CodePattern = new( "^[a-zA-Z]{2,3}(?:-[a-zA-Z0-9]{2,8}){0,2}$", RegexOptions.CultureInvariant );
	private static readonly Lazy<IReadOnlyDictionary<string, TextEntry>> Entries = new( ReadCatalog );
	private static LanguagePack active = new( "en", "English", new Dictionary<string, string>() );
	private static readonly AsyncLocal<MessageCapture?> capturedMessages = new();

	// Remember how messages were constructed so saved scan results can be translated again in another language.
	internal sealed class MessageCapture : IDisposable
	{
		private readonly MessageCapture? parent = capturedMessages.Value;
		internal readonly System.Collections.Concurrent.ConcurrentDictionary<string, DeferredText> Messages = new( StringComparer.Ordinal );

		internal MessageCapture() => capturedMessages.Value = this;

		public void Dispose() => capturedMessages.Value = parent;
	}

	internal static MessageCapture CaptureMessages() => new();

	internal static DeferredText CapturedText( string text ) => capturedMessages.Value?.Messages.GetValueOrDefault( text ) ?? new( text );

	internal static string LanguageCode => Volatile.Read( ref active ).Code;

	internal static LanguagePack PrepareLanguage( string code )
	{
		if ( string.IsNullOrWhiteSpace( code ) || code.Equals( "en", StringComparison.OrdinalIgnoreCase ) )
			return new( "en", "English", new Dictionary<string, string>() );

		string normalized = NormalizeCode( code );
		LanguagePack pack = Inspect( System.IO.Path.Combine( LanguageDirectory, normalized + ".json" ) );

		if ( pack.Code != normalized )
			throw new System.IO.InvalidDataException( "The language code does not match the pack filename." );

		return pack;
	}

	internal static void SetLanguage( LanguagePack pack )
	{
		Volatile.Write( ref active, pack );
	}

	internal static string LanguageDirectory => Path.Combine( AppContext.BaseDirectory, "Languages" );
	internal static IReadOnlyDictionary<string, TextEntry> Catalog => Entries.Value;

	internal static string Key( string context, string english ) => context + "." +
		Convert.ToHexString( SHA256.HashData( Encoding.UTF8.GetBytes( context + "\0" + english ) ) )[..16].ToLowerInvariant();

	public static string T( string context, string english )
	{
		LanguagePack pack = Volatile.Read( ref active );
		string text = pack.Strings.TryGetValue( Key( context, english ), out string? translation ) ? translation : english;

		if ( capturedMessages.Value is { } capture )
			capture.Messages[text] = new( english, context );

		return text;
	}

	public static string F( string context, FormattableString text ) => Format( context, text.Format, text.GetArguments() );

	// A malformed translation must still display a usable message; fall back to the English format string.
	public static string Format( string context, string english, params object?[] values )
	{
		string translated = T( context, english );
		string text;

		try
		{
			text = string.Format( CultureInfo.CurrentCulture, translated, values );
		}
		catch ( FormatException ) { text = string.Format( CultureInfo.CurrentCulture, english, values ); }

		if ( capturedMessages.Value is { } capture )
			capture.Messages[text] = new(
				english,
				context,
				values.Select( value => CapturedText( Convert.ToString( value, CultureInfo.InvariantCulture ) ?? "" ) ).ToArray() );

		return text;
	}

	internal static string? Initialize( string code )
	{
		try
		{
			Activate( code );

			return null;
		}
		catch ( Exception error ) when ( error is IOException or JsonException or ArgumentException or InvalidDataException or UnauthorizedAccessException )
		{
			Volatile.Write( ref active, new( "en", "English", new Dictionary<string, string>() ) );

			return error.Message;
		}
	}

	// Validate the complete pack before replacing the active one.
	internal static void Activate( string code )
	{
		if ( string.IsNullOrWhiteSpace( code ) || code.Equals( "en", StringComparison.OrdinalIgnoreCase ) )
		{
			Volatile.Write( ref active, new( "en", "English", new Dictionary<string, string>() ) );

			return;
		}

		string normalized = NormalizeCode( code );
		LanguagePack candidate = Inspect( Path.Combine( LanguageDirectory, normalized + ".json" ) );

		if ( candidate.Code != normalized )
			throw new InvalidDataException( "The language code does not match the pack filename." );

		Volatile.Write( ref active, candidate );
	}

	// Skip unreadable packs individually so the other languages remain available.
	internal static IReadOnlyList<LanguageChoice> AvailableLanguages()
	{
		var choices = new Dictionary<string, LanguageChoice>( StringComparer.Ordinal );

		try
		{
			foreach ( string path in Directory.Exists( LanguageDirectory )
				? Directory.GetFiles( LanguageDirectory, "*.json" ).Order( StringComparer.OrdinalIgnoreCase )
				: Enumerable.Empty<string>() )
			{
				try
				{
					LanguagePack pack = Inspect( path );

					if ( Path.GetFileNameWithoutExtension( path ).Equals( pack.Code, StringComparison.OrdinalIgnoreCase ) )
						choices[pack.Code] = new( pack.Code, pack.Name );
				}
				catch ( Exception error ) when ( error is IOException or JsonException or ArgumentException or InvalidDataException or UnauthorizedAccessException ) { }
			}
		}
		catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException ) { }

		return new[] { new LanguageChoice( "en", "English" ) }.Concat( choices.Values.OrderBy( choice => choice.Code, StringComparer.Ordinal ) ).ToArray();
	}

	internal static LanguagePack Inspect( string path )
	{
		var info = new FileInfo( path );

		if ( info.Length > MaximumPackBytes )
			throw new InvalidDataException( "The language pack exceeds the 8 MiB size limit." );

		string text = StrictUtf8.GetString( File.ReadAllBytes( path ) ).TrimStart( '\uFEFF' );

		return InspectText( text );
	}

	// Translations must preserve the English format placeholders.
	private static LanguagePack InspectText( string text )
	{
		using JsonDocument document = JsonDocument.Parse( text, new JsonDocumentOptions { MaxDepth = 12 } );
		JsonElement root = document.RootElement;

		CheckObject( root, "formatVersion", "application", "languageCode", "languageName", "entries" );

		JsonElement version = root.GetProperty( "formatVersion" );

		if ( version.ValueKind != JsonValueKind.Number || !version.TryGetInt32( out int number ) || number != FormatVersion || ReadString( root, "application" ) != ApplicationId )
			throw new InvalidDataException( "This is not a supported Script Merger Remastered language pack." );

		(string code, string name) = ValidateLanguageInfo( ReadString( root, "languageCode" ), ReadString( root, "languageName" ) );

		var translations = new Dictionary<string, string>( StringComparer.Ordinal );
		JsonElement strings = root.GetProperty( "entries" );

		CheckObject( strings, allowAny: true );

		foreach ( JsonProperty property in strings.EnumerateObject() )
		{
			CheckObject( property.Value, "context", "english", "translation", "comment" );

			string context = ReadString( property.Value, "context" ), english = ReadString( property.Value, "english" );

			if ( property.Name != Key( context, english ) )
				throw new InvalidDataException( "The English text or context does not match its key: " + property.Name );

			string translated = ReadString( property.Value, "translation" );

			ReadString( property.Value, "comment" );

			if ( translated.Length > 32768 || translated.Contains( '\0' ) )
				throw new InvalidDataException( "Invalid translation text: " + property.Name );

			// Keep older language packs usable when the application retires some of their keys.
			if ( !Catalog.TryGetValue( property.Name, out TextEntry? entry ) )
				continue;

			if ( translated.Length == 0 )
				continue;

			if ( entry.Formatted )
				ValidateFormat( entry.English, translated, property.Name );

			translations.Add( property.Name, translated );
		}

		return new( code, name, translations );
	}

	internal static void ValidateFormat( string english, string translated, string key )
	{
		try
		{
			CompositeFormat.Parse( english );
			CompositeFormat.Parse( translated );

			if ( !FormatTokens( english ).SequenceEqual( FormatTokens( translated ) ) )
				throw new FormatException();
		}
		catch ( FormatException ) { throw new InvalidDataException( "Keep every numbered placeholder and its format unchanged: " + key ); }
	}

	private static string[] FormatTokens( string value )
	{
		var tokens = new List<string>();

		for ( int position = 0; position < value.Length; position++ )
		{
			if ( value[position] != '{' )
				continue;

			if ( position + 1 < value.Length && value[position + 1] == '{' )
			{
				position++;

				continue;
			}

			int start = ++position;

			while ( char.IsAsciiDigit( value[position] ) )
				position++;

			string index = int.Parse( value[start..position], CultureInfo.InvariantCulture ).ToString( CultureInfo.InvariantCulture );

			while ( value[position] != ':' && value[position] != '}' )
				position++;

			string format = "";

			if ( value[position] == ':' )
			{
				start = ++position;

				while ( value[position] != '}' )
					position++;

				format = value[start..position];
			}

			tokens.Add( index + ":" + format );
		}

		return tokens.Order( StringComparer.Ordinal ).ToArray();
	}

	internal static void ExportTemplate( string path )
	{
		var entries = Catalog.ToDictionary( item => item.Key, item => new
		{
			context = item.Value.Context,
			english = item.Value.English,
			translation = "",
			comment = TranslatorComment( item.Value )
		} );
		var template = new
		{
			formatVersion = FormatVersion,
			application = ApplicationId,
			languageCode = "xx",
			languageName = "Your language",
			entries
		};

		WriteAtomically( path, JsonSerializer.Serialize( template, new JsonSerializerOptions { WriteIndented = true } ) );
	}

	internal static LanguagePack Import( string sourcePath, bool overwrite )
	{
		LanguagePack pack = Inspect( sourcePath );

		return Install( pack, overwrite );
	}

	internal static LanguagePack Install( LanguagePack pack, bool overwrite )
	{
		ValidateLanguageInfo( pack.Code, pack.Name );
		Directory.CreateDirectory( LanguageDirectory );

		string target = Path.Combine( LanguageDirectory, pack.Code + ".json" );

		if ( File.Exists( target ) && !overwrite )
			throw new IOException( "A language pack with this code is already installed." );

		var entries = pack.Strings.ToDictionary( item => item.Key, item => new
		{
			context = Catalog[item.Key].Context,
			english = Catalog[item.Key].English,
			translation = item.Value,
			comment = ""
		} );

		WriteAtomically( target, JsonSerializer.Serialize( new
		{
			formatVersion = FormatVersion,
			application = ApplicationId,
			languageCode = pack.Code,
			languageName = pack.Name,
			entries
		}, new JsonSerializerOptions { WriteIndented = true } ) );

		return pack;
	}

	private static string TranslatorComment( TextEntry entry ) => entry.Formatted ?
		"Keep numbered placeholders (including format specifiers); reorder them if needed. Leave blank for English." :
		"Keep filenames, XML tags, annotation names and & keyboard-access markers. Leave blank for English.";

	internal static (string Code, string Name) ValidateLanguageInfo( string code, string name )
	{
		code = NormalizeCode( code );

		if ( code == "en" )
			throw new InvalidDataException( "Use a non-English language code for a translation pack." );

		name = name.Trim();

		if ( name.Length is < 1 or > 80 || name.Any( char.IsControl ) )
			throw new InvalidDataException( "The language name is invalid." );

		return (code, name);
	}

	private static void WriteAtomically( string path, string text )
	{
		string fullPath = Path.GetFullPath( path );
		string temporary = fullPath + "." + Guid.NewGuid().ToString( "N" ) + ".tmp";

		try
		{
			using ( var stream = new FileStream( temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None ) )
			{
				byte[] bytes = StrictUtf8.GetBytes( text );

				stream.Write( bytes );
				stream.Flush( true );
			}

			if ( File.Exists( fullPath ) )
				File.Replace( temporary, fullPath, null );
			else
				File.Move( temporary, fullPath );
		}
		finally { if ( File.Exists( temporary ) ) File.Delete( temporary ); }
	}

	private static string NormalizeCode( string code )
	{
		if ( !CodePattern.IsMatch( code ) )
			throw new InvalidDataException( "Use a language code such as fr, de, pt-BR or zh-Hant." );

		return code.ToLowerInvariant();
	}

	private static string ReadString( JsonElement element, string name )
	{
		if ( !element.TryGetProperty( name, out JsonElement value ) || value.ValueKind != JsonValueKind.String )
			throw new InvalidDataException( "A language pack field must contain text: " + name );

		return value.GetString()!;
	}

	private static void CheckObject( JsonElement element, params string[] fields ) => CheckObject( element, false, fields );

	private static void CheckObject( JsonElement element, bool allowAny, params string[] fields )
	{
		if ( element.ValueKind != JsonValueKind.Object )
			throw new InvalidDataException( "A language pack field must be a JSON object." );

		var keys = new HashSet<string>( StringComparer.Ordinal );

		foreach ( JsonProperty property in element.EnumerateObject() )
		{
			if ( !keys.Add( property.Name ) )
				throw new InvalidDataException( "Duplicate JSON key: " + property.Name );

			if ( !allowAny && !fields.Contains( property.Name ) )
				throw new InvalidDataException( "Unsupported language pack field: " + property.Name );
		}

		if ( !allowAny && fields.Any( field => !keys.Contains( field ) ) )
			throw new InvalidDataException( "A required language pack field is missing." );
	}

	private static IReadOnlyDictionary<string, TextEntry> ReadCatalog()
	{
		using Stream stream = typeof( UiText ).Assembly.GetManifestResourceStream( CatalogResource )
			?? throw new InvalidDataException( "The embedded English UI catalog is missing." );
		using JsonDocument document = JsonDocument.Parse( stream );

		return document.RootElement.EnumerateObject().Select( item =>
			new TextEntry( item.Value.GetProperty( "context" ).GetString()!, item.Value.GetProperty( "english" ).GetString()!,
				item.Value.GetProperty( "formatted" ).GetBoolean() ) ).Concat( BlobMergeEntries ).Concat( ReviewEntries )
			.GroupBy( entry => Key( entry.Context, entry.English ), StringComparer.Ordinal )
			.ToDictionary( group => group.Key, group => group.First(), StringComparer.Ordinal );
	}

}
