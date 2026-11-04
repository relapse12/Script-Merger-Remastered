#nullable enable

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace WitcherScriptMerger.FileIndex;

internal sealed record PotatoBundleEntry(
	string Path,
	long Offset,
	int Length,
	int PackedLength,
	uint Crc,
	uint Codec,
	uint Flags );

// POTATO70 has 304-byte and 320-byte records without a layout marker. Try both and accept one only after validating the table.
internal static class PotatoBundleIndexReader
{
	private const int HeaderSize = 32;
	private const int NameSize = 256;
	private static readonly UTF8Encoding StrictUtf8 = new( false, true );
	private static readonly BundleRecordLayout[] RecordLayouts =
	{
		new(304, OffsetOffset: 272, OffsetIs64Bit: true, LengthOffset: 280, PackedLengthOffset: 284,
			CrcOffset: 288, CodecOffset: 292, FlagsOffset: 296),
		new(320, OffsetOffset: 284, OffsetIs64Bit: false, LengthOffset: 276, PackedLengthOffset: 280,
			CrcOffset: 312, CodecOffset: 316, FlagsOffset: null)
	};

	public static IReadOnlyList<PotatoBundleEntry> ReadIndex( Stream stream, string description )
	{
		if ( !stream.CanRead || !stream.CanSeek )
		{
			throw new ArgumentException(
				UiText.T( "PotatoBundleIndexReader", "The bundle stream must be readable and seekable." ),
				nameof( stream ) );
		}

		if ( stream.Length < HeaderSize )
		{
			throw new InvalidDataException( UiText.F( "PotatoBundleIndexReader", $"'{description}' is too small to be a Remastered bundle." ) );
		}

		stream.Position = 0;

		byte[] header = new byte[HeaderSize];

		stream.ReadExactly( header );

		if ( !header.AsSpan( 0, 8 ).SequenceEqual( "POTATO70"u8 ) )
		{
			throw new InvalidDataException( UiText.F( "PotatoBundleIndexReader", $"'{description}' does not have a POTATO70 bundle signature." ) );
		}

		uint tableSize = BinaryPrimitives.ReadUInt32LittleEndian( header.AsSpan( 16, 4 ) );

		if ( tableSize == 0 || HeaderSize + (long)tableSize > stream.Length )
		{
			throw new InvalidDataException( UiText.F( "PotatoBundleIndexReader", $"'{description}' has an invalid Remastered bundle record table." ) );
		}

		List<IReadOnlyList<PotatoBundleEntry>> validIndexes = new();

		foreach ( BundleRecordLayout layout in RecordLayouts.Where( candidate => tableSize % candidate.RecordSize == 0 ) )
		{
			if ( TryReadIndex( stream, tableSize, layout, out IReadOnlyList<PotatoBundleEntry>? entries ) )
			{
				validIndexes.Add( entries! );
			}
		}

		if ( validIndexes.Count == 1 )
		{
			return validIndexes[0];
		}

		if ( validIndexes.Count > 1 )
		{
			throw new InvalidDataException( UiText.F( "PotatoBundleIndexReader", $"'{description}' has an ambiguous Remastered bundle record layout." ) );
		}

		throw new InvalidDataException( UiText.F( "PotatoBundleIndexReader", $"'{description}' does not use a supported 304-byte or 320-byte Remastered bundle record layout." ) );
	}

	public static string NormalizeResourcePath( string resourcePath )
	{
		if ( string.IsNullOrWhiteSpace( resourcePath ) )
		{
			throw new ArgumentException(
				UiText.T( "PotatoBundleIndexReader", "A bundle resource path is required." ),
				nameof( resourcePath ) );
		}

		string normalized = resourcePath.Trim().Replace( '/', '\\' );

		if ( Path.IsPathRooted( normalized )
			|| normalized.StartsWith( '\\' )
			|| normalized.Split( '\\' ).Any( segment => segment.Length == 0 || segment is "." or ".." ) )
		{
			throw new ArgumentException(
				UiText.T( "PotatoBundleIndexReader", "The bundle resource path must be a safe relative path." ),
				nameof( resourcePath ) );
		}

		return normalized;
	}

	private static bool TryReadIndex(
		Stream stream,
		uint tableSize,
		BundleRecordLayout layout,
		out IReadOnlyList<PotatoBundleEntry>? result )
	{
		result = null;

		try
		{
			stream.Position = HeaderSize;
			result = ReadIndex( stream, tableSize, layout );

			return true;
		}
		catch ( Exception exception ) when ( exception is InvalidDataException
			or ArgumentException
			or OverflowException
			or DecoderFallbackException
			or EndOfStreamException )
		{
			return false;
		}
	}

	// The wrong layout can look valid at first. Check every path and resource range before accepting the table.
	private static IReadOnlyList<PotatoBundleEntry> ReadIndex(
		Stream stream,
		uint tableSize,
		BundleRecordLayout layout )
	{
		int recordCount = checked((int)( tableSize / layout.RecordSize ));
		List<PotatoBundleEntry> entries = new( recordCount );
		HashSet<string> paths = new( StringComparer.OrdinalIgnoreCase );
		byte[] record = new byte[layout.RecordSize];

		for ( int index = 0; index < recordCount; index++ )
		{
			stream.ReadExactly( record );

			int terminator = Array.IndexOf( record, (byte)0, 0, NameSize );

			if ( terminator <= 0 )
			{
				throw new InvalidDataException( UiText.F( "PotatoBundleIndexReader", $"Bundle record {index} has an empty or unterminated path." ) );
			}

			string path = NormalizeResourcePath( StrictUtf8.GetString( record, 0, terminator ) );

			if ( !paths.Add( path ) )
			{
				throw new InvalidDataException( UiText.F( "PotatoBundleIndexReader", $"Bundle contains the resource path more than once: {path}" ) );
			}

			long offset = layout.OffsetIs64Bit
				? checked((long)BinaryPrimitives.ReadUInt64LittleEndian( record.AsSpan( layout.OffsetOffset, 8 ) ))
				: BinaryPrimitives.ReadUInt32LittleEndian( record.AsSpan( layout.OffsetOffset, 4 ) );
			int length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian( record.AsSpan( layout.LengthOffset, 4 ) ));
			int packedLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian( record.AsSpan( layout.PackedLengthOffset, 4 ) ));
			uint crc = BinaryPrimitives.ReadUInt32LittleEndian( record.AsSpan( layout.CrcOffset, 4 ) );
			uint codec = BinaryPrimitives.ReadUInt32LittleEndian( record.AsSpan( layout.CodecOffset, 4 ) );
			uint flags = layout.FlagsOffset.HasValue
				? BinaryPrimitives.ReadUInt32LittleEndian( record.AsSpan( layout.FlagsOffset.Value, 4 ) )
				: 0;
			int storedLength = codec == 0 ? length : packedLength;

			// Listing needs offsets and sizes only, so large resources do not require extraction buffers.
			if ( length < 0 || packedLength < 0
				|| offset < HeaderSize + tableSize || offset > stream.Length - storedLength )
			{
				throw new InvalidDataException( UiText.F( "PotatoBundleIndexReader", $"Bundle record {index} has invalid payload bounds." ) );
			}

			entries.Add( new PotatoBundleEntry( path, offset, length, packedLength, crc, codec, flags ) );
		}

		return entries;
	}

	private sealed record BundleRecordLayout(
		int RecordSize,
		int OffsetOffset,
		bool OffsetIs64Bit,
		int LengthOffset,
		int PackedLengthOffset,
		int CrcOffset,
		int CodecOffset,
		int? FlagsOffset );
}
