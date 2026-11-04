#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WitcherScriptMerger.FileIndex;

// Bundle listings are expensive to repeat. Reuse them while the bundle and listing tool are unchanged.
internal sealed class BundleListingCache
{
	internal const int CurrentSchemaVersion = 4;

	private const int PotatoHeaderSize = 32;

	private const string CacheFolderName = "Bundle Index Cache";

	private readonly string cacheDirectory;

	private const string NativeToolFingerprint = "SCRIPT-MERGER-REMASTERED-BUNDLE-304-320-V3";

	private readonly string toolFingerprint;

	private readonly ConcurrentDictionary<string, object> cacheKeyLocks = new( StringComparer.OrdinalIgnoreCase );

	public BundleListingCache( string cacheDirectory )
	{
		if ( string.IsNullOrWhiteSpace( cacheDirectory ) )
		{
			throw new ArgumentException(
				UiText.T( "BundleListingCache", "A bundle cache directory is required." ),
				nameof( cacheDirectory ) );
		}

		this.cacheDirectory = Path.GetFullPath( cacheDirectory );
		toolFingerprint = NativeToolFingerprint;
	}

	internal BundleListingCache( string cacheDirectory, string executablePath, string companionPath )
		: this( cacheDirectory )
	{
		toolFingerprint = ComputeCompositeHash(
			NativeToolFingerprint,
			ComputeFileSha256( Path.GetFullPath( executablePath ) ),
			ComputeFileSha256( Path.GetFullPath( companionPath ) ) );
	}

	public static string DefaultCacheDirectory => Path.Combine(
		AppContext.BaseDirectory,
		"Data",
		CacheFolderName );

	// Lock per bundle so parallel scans share one listing. Use the built-in POTATO70 reader when possible.
	public BundleListingCacheResult GetOrCreate(
		string bundlePath,
		Func<string, IReadOnlyList<string>> listingFactory )
	{
		ArgumentNullException.ThrowIfNull( listingFactory );

		string fullBundlePath = Path.GetFullPath( bundlePath );
		using FileStream bundleStream = new(
			fullBundlePath,
			FileMode.Open,
			FileAccess.Read,
			FileShare.Read,
			bufferSize: 1024 * 1024,
			FileOptions.SequentialScan );
		long bundleLength = bundleStream.Length;
		BundleListingIdentity identity = InspectListingIdentity( bundleStream );
		string bundleSha256 = identity.Fingerprint;
		string cacheKey = ComputeCompositeHash(
			CurrentSchemaVersion.ToString(),
			bundleLength.ToString(),
			bundleSha256,
			toolFingerprint );
		string cachePath = Path.Combine( cacheDirectory, cacheKey + ".json" );
		object keyLock = cacheKeyLocks.GetOrAdd( cacheKey, static _ => new object() );

		lock ( keyLock )
		{
			if ( TryRead( cachePath, bundleLength, bundleSha256, out string[] cachedPaths ) )
			{
				return new BundleListingCacheResult( cachedPaths, CacheHit: true, bundleSha256 );
			}

			IReadOnlyList<string> listedPaths = identity.NativePaths is not null
				? identity.NativePaths
				: listingFactory( fullBundlePath )
				?? throw new InvalidDataException( UiText.T( "BundleListingCache", "Bundle listing returned no result." ) );
			string[] paths = listedPaths.ToArray();

			ValidatePaths( paths );

			BundleListingCacheEntry entry = new()
			{
				SchemaVersion = CurrentSchemaVersion,
				BundleLength = bundleLength,
				BundleSha256 = bundleSha256,
				ToolFingerprint = toolFingerprint,
				PathsSha256 = ComputePathsSha256( paths ),
				Paths = paths
			};

			TryWriteAtomically( cachePath, entry );

			return new BundleListingCacheResult( paths, CacheHit: false, bundleSha256 );
		}
	}

	// A POTATO70 listing needs only the header and file table. Hashing those avoids reading every resource.
	private static BundleListingIdentity InspectListingIdentity( FileStream stream )
	{
		stream.Position = 0;

		if ( stream.Length >= PotatoHeaderSize )
		{
			byte[] header = new byte[PotatoHeaderSize];

			stream.ReadExactly( header );

			if ( header.AsSpan( 0, 8 ).SequenceEqual( "POTATO70"u8 ) )
			{
				uint tableSize = System.Buffers.Binary.BinaryPrimitives
					.ReadUInt32LittleEndian( header.AsSpan( 16, 4 ) );
				long tableEnd = PotatoHeaderSize + (long)tableSize;

				if ( tableSize > 0 && tableEnd <= stream.Length )
				{
					IReadOnlyList<PotatoBundleEntry> entries = PotatoBundleIndexReader.ReadIndex( stream, stream.Name );
					string[] paths = entries.Select( entry => entry.Path ).ToArray();
					using IncrementalHash hash = IncrementalHash.CreateHash( HashAlgorithmName.SHA256 );

					stream.Position = 0;
					hash.AppendData( header );
					stream.Position = PotatoHeaderSize;

					byte[] buffer = new byte[64 * 1024];
					long remaining = tableSize;

					while ( remaining > 0 )
					{
						int count = (int)Math.Min( buffer.Length, remaining );

						stream.ReadExactly( buffer.AsSpan( 0, count ) );
						hash.AppendData( buffer, 0, count );
						remaining -= count;
					}

					ValidatePaths( paths );

					return new BundleListingIdentity(
						Convert.ToHexString( hash.GetHashAndReset() ), paths );
				}
			}
		}

		stream.Position = 0;

		return new BundleListingIdentity(
			Convert.ToHexString( SHA256.HashData( stream ) ), null );
	}

	private bool TryRead(
		string cachePath,
		long expectedLength,
		string expectedBundleSha256,
		out string[] paths )
	{
		paths = Array.Empty<string>();

		if ( !File.Exists( cachePath ) )
		{
			return false;
		}

		try
		{
			BundleListingCacheEntry? entry = JsonSerializer.Deserialize<BundleListingCacheEntry>(
				File.ReadAllText( cachePath, Encoding.UTF8 ) );

			if ( entry is null
				|| entry.SchemaVersion != CurrentSchemaVersion
				|| entry.BundleLength != expectedLength
				|| !string.Equals( entry.BundleSha256, expectedBundleSha256, StringComparison.Ordinal )
				|| !string.Equals( entry.ToolFingerprint, toolFingerprint, StringComparison.Ordinal )
				|| entry.Paths is null )
			{
				DeleteInvalidEntry( cachePath );

				return false;
			}

			ValidatePaths( entry.Paths );

			if ( !string.Equals( entry.PathsSha256, ComputePathsSha256( entry.Paths ), StringComparison.Ordinal ) )
			{
				DeleteInvalidEntry( cachePath );

				return false;
			}

			paths = entry.Paths;

			return true;
		}
		catch ( Exception exception ) when ( exception is IOException
			or UnauthorizedAccessException
			or JsonException
			or InvalidDataException )
		{
			DeleteInvalidEntry( cachePath );

			return false;
		}
	}

	// A failed cache write only means the next scan must list the bundle again.
	private static void TryWriteAtomically( string cachePath, BundleListingCacheEntry entry )
	{
		string? tempPath = null;

		try
		{
			Directory.CreateDirectory( Path.GetDirectoryName( cachePath )! );
			tempPath = cachePath + "." + Guid.NewGuid().ToString( "N" ) + ".tmp";
			File.WriteAllText( tempPath, JsonSerializer.Serialize( entry ), new UTF8Encoding( encoderShouldEmitUTF8Identifier: false ) );
			File.Move( tempPath, cachePath, overwrite: true );
			tempPath = null;
		}
		catch ( Exception exception ) when ( exception is IOException or UnauthorizedAccessException )
		{
		}
		finally
		{
			if ( tempPath is not null )
			{
				try
				{
					File.Delete( tempPath );
				}
				catch
				{
				}
			}
		}
	}

	private static void DeleteInvalidEntry( string cachePath )
	{
		try
		{
			File.Delete( cachePath );
		}
		catch
		{
		}
	}

	private static void ValidatePaths( IEnumerable<string> paths )
	{
		if ( paths.Any( string.IsNullOrWhiteSpace ) )
		{
			throw new InvalidDataException( UiText.T( "BundleListingCache", "The cached bundle listing contains an empty resource path." ) );
		}
	}

	private static string ComputeFileSha256( string path )
	{
		using FileStream stream = new(
			path,
			FileMode.Open,
			FileAccess.Read,
			FileShare.Read,
			bufferSize: 1024 * 1024,
			FileOptions.SequentialScan );

		return Convert.ToHexString( SHA256.HashData( stream ) );
	}

	private static string ComputePathsSha256( IEnumerable<string> paths )
	{
		using IncrementalHash hash = IncrementalHash.CreateHash( HashAlgorithmName.SHA256 );

		foreach ( string path in paths )
		{
			byte[] pathBytes = Encoding.UTF8.GetBytes( path );

			hash.AppendData( BitConverter.GetBytes( pathBytes.Length ) );
			hash.AppendData( pathBytes );
		}

		return Convert.ToHexString( hash.GetHashAndReset() );
	}

	private static string ComputeCompositeHash( params string[] values )
	{
		return Convert.ToHexString( SHA256.HashData( Encoding.UTF8.GetBytes( string.Join( "\n", values ) ) ) );
	}

	private sealed class BundleListingCacheEntry
	{
		public int SchemaVersion { get; set; }

		public long BundleLength { get; set; }

		public string BundleSha256 { get; set; } = string.Empty;

		public string ToolFingerprint { get; set; } = string.Empty;

		public string PathsSha256 { get; set; } = string.Empty;

		public string[] Paths { get; set; } = Array.Empty<string>();
	}

	private sealed record BundleListingIdentity(
		string Fingerprint,
		string[]? NativePaths );
}

internal sealed record BundleListingCacheResult(
	IReadOnlyList<string> Paths,
	bool CacheHit,
	string BundleSha256 );
