#nullable enable

using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using WitcherScriptMerger.FileIndex;

namespace WitcherScriptMerger.Tools;

// Game XML uses the UTF8-BOM encoding name and can repeat stackable attributes, both rejected by standard XML parsing.
internal static class GameXmlReader
{
	private static readonly Regex Utf8BomDeclaration = new(
		@"\A<\?xml\s+[^?]*\bencoding\s*=\s*(['""])UTF8-BOM\1[^?]*\?>",
		RegexOptions.IgnoreCase | RegexOptions.CultureInvariant );

	internal static bool IsUtf8Alias( string name ) => name.Equals( "UTF8-BOM", StringComparison.OrdinalIgnoreCase );

	// For repeated stackable attributes in gameplay items, preserve the first value as the game does.
	internal static XDocument Load( Stream stream, XmlReaderSettings settings, LoadOptions options )
	{
		long start = stream.CanSeek ? stream.Position : 0;
		var owned = settings.Clone();

		owned.CloseInput = false;

		try
		{
			using var reader = Create( stream, owned );

			return XDocument.Load( reader, options );
		}
		catch ( XmlException )
		{
			const long recoveryLimit = 256L * 1024L * 1024L;
			long maximum = settings.MaxCharactersInDocument > 0 ? Math.Min( settings.MaxCharactersInDocument, recoveryLimit ) : recoveryLimit;

			if ( !stream.CanSeek || stream.Length - start > Math.Min( recoveryLimit, maximum * 4 + 1024 ) )
				throw;

			stream.Position = start;

			using var bytes = new MemoryStream();

			stream.CopyTo( bytes );

			string? tolerated = GameplayItemXml.RetainFirstStackable( bytes.ToArray() );

			if ( tolerated is null )
				throw;

			using var text = new StringReader( tolerated );
			using var reader = XmlReader.Create( text, owned );

			return XDocument.Load( reader, options );
		}
	}

	// The framework rejects the UTF8-BOM encoding name, so decode those files as UTF-8 first.
	internal static XmlReader Create( Stream stream, XmlReaderSettings settings )
	{
		ArgumentNullException.ThrowIfNull( stream );
		ArgumentNullException.ThrowIfNull( settings );

		if ( !stream.CanSeek )
			return XmlReader.Create( stream, settings );

		long start = stream.Position;
		byte[] prefix = new byte[1024];
		int length = stream.Read( prefix, 0, prefix.Length );

		stream.Position = start;

		int bom = length >= 3 && prefix[0] == 0xef && prefix[1] == 0xbb && prefix[2] == 0xbf ? 3 : 0;

		if ( !Utf8BomDeclaration.IsMatch( Encoding.ASCII.GetString( prefix, bom, length - bom ) ) )
			return XmlReader.Create( stream, settings );

		// Keep strict decoding and XML validation after accepting the encoding alias.
		stream.Position = start + bom;

		StreamReader text = new( stream, new UTF8Encoding( false, true ), detectEncodingFromByteOrderMarks: false,
			bufferSize: 1024, leaveOpen: true );
		XmlReaderSettings ownedSettings = settings.Clone();

		ownedSettings.CloseInput = true;

		try
		{
			return XmlReader.Create( text, ownedSettings );
		}
		catch
		{
			text.Dispose();

			throw;
		}
	}
}
