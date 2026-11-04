#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WitcherScriptMerger.FileIndex;

namespace WitcherScriptMerger.Tools;

internal sealed record RemasteredBundleEntry(
	string Path,
	long Offset,
	int Length,
	int PackedLength,
	uint Crc,
	uint Codec,
	uint Flags );

internal sealed record RemasteredBundleResource( string Path, byte[] Content );

// Reads and extracts POTATO70 bundle resources directly. Validate resource paths before writing them.
internal static class RemasteredBundleFile
{
	private const int MaximumResourceSize = 512 * 1024 * 1024;

	public static IReadOnlyList<string> ListPaths( string bundlePath )
	{
		return ReadIndex( bundlePath ).Select( entry => entry.Path ).ToArray();
	}

	public static IReadOnlyList<RemasteredBundleEntry> ReadIndex( string bundlePath )
	{
		if ( string.IsNullOrWhiteSpace( bundlePath ) )
		{
			throw new ArgumentException( UiText.T( "RemasteredBundleFile", "A bundle path is required." ), nameof( bundlePath ) );
		}

		string fullPath = Path.GetFullPath( bundlePath );
		using FileStream stream = new( fullPath, FileMode.Open, FileAccess.Read, FileShare.Read );

		return ReadIndex( stream, fullPath );
	}

	public static string Extract( string bundlePath, string resourcePath, string outputDirectory )
	{
		string normalizedPath = NormalizeResourcePath( resourcePath );
		string fullBundlePath = Path.GetFullPath( bundlePath );
		RemasteredBundleEntry entry = ReadIndex( fullBundlePath )
			.FirstOrDefault( candidate => candidate.Path.Equals( normalizedPath, StringComparison.OrdinalIgnoreCase ) )
			?? throw new FileNotFoundException(
				UiText.F( "RemasteredBundleFile", $"The resource '{normalizedPath}' is not present in the bundle." ),
				fullBundlePath );

		byte[] unpacked;

		using ( FileStream stream = new( fullBundlePath, FileMode.Open, FileAccess.Read, FileShare.Read ) )
		{
			unpacked = ReadEntryPayload( stream, entry, normalizedPath );
		}

		string outputPath = GetOutputPath( outputDirectory, normalizedPath );

		Directory.CreateDirectory( Path.GetDirectoryName( outputPath )! );
		File.WriteAllBytes( outputPath, unpacked );

		return outputPath;
	}

	public static IReadOnlyList<RemasteredBundleResource> ReadResources(
		string bundlePath,
		IEnumerable<string> resourcePaths )
	{
		ArgumentNullException.ThrowIfNull( resourcePaths );

		string fullBundlePath = Path.GetFullPath( bundlePath );
		HashSet<string> requested = resourcePaths
			.Select( NormalizeResourcePath )
			.ToHashSet( StringComparer.OrdinalIgnoreCase );

		if ( requested.Count == 0 )
		{
			return Array.Empty<RemasteredBundleResource>();
		}

		using FileStream stream = new( fullBundlePath, FileMode.Open, FileAccess.Read, FileShare.Read );
		IReadOnlyList<RemasteredBundleEntry> entries = ReadIndex( stream, fullBundlePath );
		List<RemasteredBundleResource> resources = new();

		foreach ( RemasteredBundleEntry entry in entries )
		{
			if ( !requested.Contains( entry.Path ) )
			{
				continue;
			}

			resources.Add( new RemasteredBundleResource(
				entry.Path,
				ReadEntryPayload( stream, entry, entry.Path ) ) );
		}

		return resources;
	}

	internal static IReadOnlyList<RemasteredBundleEntry> ReadIndex( Stream stream, string description )
	{
		return PotatoBundleIndexReader.ReadIndex( stream, description )
			.Select( entry => new RemasteredBundleEntry(
				entry.Path,
				entry.Offset,
				entry.Length,
				entry.PackedLength,
				entry.Crc,
				entry.Codec,
				entry.Flags ) )
			.ToArray();
	}

	internal static string NormalizeResourcePath( string resourcePath )
	{
		return PotatoBundleIndexReader.NormalizeResourcePath( resourcePath );
	}

	// Check payload lengths before allocating or decoding data from a bundle.
	private static byte[] ReadEntryPayload(
		FileStream stream,
		RemasteredBundleEntry entry,
		string resourcePath )
	{
		if ( entry.Length > MaximumResourceSize || ( entry.Codec != 0 && entry.PackedLength > MaximumResourceSize ) )
		{
			throw new InvalidDataException( UiText.F( "RemasteredBundleFile",
				$"Bundle resource '{resourcePath}' exceeds the {MaximumResourceSize:N0}-byte extraction limit." ) );
		}

		byte[] packed = new byte[entry.Codec == 0 ? entry.Length : entry.PackedLength];

		stream.Position = entry.Offset;
		stream.ReadExactly( packed );

		byte[] unpacked = entry.Codec switch
		{
			0 => packed,
			1 => InflateZlib( packed, entry.Length ),
			4 or 5 => DecodeLz4Block( packed, entry.Length ),
			_ => throw new InvalidDataException(
				UiText.F( "RemasteredBundleFile", $"Bundle resource '{resourcePath}' uses unsupported compression codec {entry.Codec}." ) )
		};

		if ( unpacked.Length != entry.Length )
		{
			throw new InvalidDataException(
				UiText.F(
					"RemasteredBundleFile",
					$"Bundle resource '{resourcePath}' decoded to {unpacked.Length:N0} bytes; expected {entry.Length:N0}." ) );
		}

		return unpacked;
	}

	private static byte[] InflateZlib( byte[] packed, int expectedLength )
	{
		using MemoryStream input = new( packed, writable: false );
		using ZLibStream zlib = new( input, CompressionMode.Decompress );
		using MemoryStream output = new( expectedLength );

		zlib.CopyTo( output );

		return output.ToArray();
	}

	private static byte[] DecodeLz4Block( byte[] source, int expectedLength )
	{
		byte[] destination = new byte[expectedLength];
		int sourceOffset = 0;
		int destinationOffset = 0;

		while ( sourceOffset < source.Length )
		{
			byte token = source[sourceOffset++];
			int literalLength = ReadExtendedLength( source, ref sourceOffset, token >> 4 );

			EnsureAvailable( source.Length, sourceOffset, literalLength, "LZ4 literal" );
			EnsureAvailable( destination.Length, destinationOffset, literalLength, "LZ4 output" );
			Buffer.BlockCopy( source, sourceOffset, destination, destinationOffset, literalLength );
			sourceOffset += literalLength;
			destinationOffset += literalLength;

			if ( sourceOffset == source.Length )
			{
				break;
			}

			EnsureAvailable( source.Length, sourceOffset, 2, "LZ4 match offset" );

			int matchOffset = source[sourceOffset] | source[sourceOffset + 1] << 8;

			sourceOffset += 2;

			if ( matchOffset <= 0 || matchOffset > destinationOffset )
			{
				throw new InvalidDataException( UiText.T( "RemasteredBundleFile", "The bundle contains an invalid LZ4 match offset." ) );
			}

			int matchLength = ReadExtendedLength( source, ref sourceOffset, token & 0x0f ) + 4;

			EnsureAvailable( destination.Length, destinationOffset, matchLength, "LZ4 output" );

			for ( int index = 0; index < matchLength; index++ )
			{
				destination[destinationOffset] = destination[destinationOffset - matchOffset];
				destinationOffset++;
			}
		}

		if ( destinationOffset != destination.Length )
		{
			throw new InvalidDataException( UiText.T( "RemasteredBundleFile", "The LZ4 payload ended before the declared resource length." ) );
		}

		return destination;
	}

	// An LZ4 length of 15 continues in following bytes; 255 means another length byte follows.
	private static int ReadExtendedLength( byte[] source, ref int offset, int length )
	{
		if ( length != 15 )
		{
			return length;
		}

		byte extension;

		do
		{
			EnsureAvailable( source.Length, offset, 1, "LZ4 length" );
			extension = source[offset++];
			length = checked(length + extension);
		}
		while ( extension == byte.MaxValue );

		return length;
	}

	private static void EnsureAvailable( int totalLength, int offset, int count, string description )
	{
		if ( offset < 0 || count < 0 || offset > totalLength - count )
		{
			throw new InvalidDataException( UiText.F( "RemasteredBundleFile", $"The bundle contains a truncated or invalid {description} sequence." ) );
		}
	}

	private static string GetOutputPath( string outputDirectory, string resourcePath )
	{
		string root = Path.GetFullPath( outputDirectory );
		string path = Path.GetFullPath( Path.Combine( root, resourcePath ) );
		string prefix = root.TrimEnd( Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar )
			+ Path.DirectorySeparatorChar;

		if ( !path.StartsWith( prefix, StringComparison.OrdinalIgnoreCase ) )
		{
			throw new InvalidOperationException( UiText.T( "RemasteredBundleFile", "The extracted resource would escape its output directory." ) );
		}

		return path;
	}
}

internal sealed class RemasteredBundleResourceTool : IBundleResourceTool
{
	public Task<IReadOnlyList<string>> ListPathsAsync(
		string bundlePath,
		ToolJobContext context,
		CancellationToken cancellationToken = default )
	{
		ArgumentNullException.ThrowIfNull( context );
		cancellationToken.ThrowIfCancellationRequested();

		return Task.FromResult( RemasteredBundleFile.ListPaths( bundlePath ) );
	}

	public Task<string> ExtractAsync(
		string bundlePath,
		string resourcePath,
		string outputDirectory,
		ToolJobContext context,
		CancellationToken cancellationToken = default )
	{
		ArgumentNullException.ThrowIfNull( context );
		cancellationToken.ThrowIfCancellationRequested();

		string output = context.EnsurePathInsideJob( outputDirectory );

		return Task.FromResult( RemasteredBundleFile.Extract( bundlePath, resourcePath, output ) );
	}
}
