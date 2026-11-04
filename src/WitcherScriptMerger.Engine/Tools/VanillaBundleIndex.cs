#nullable enable

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WitcherScriptMerger.GameContent;
namespace WitcherScriptMerger.Tools;

// Reuse bundle listings while their paths, sizes, timestamps and sampled content remain unchanged.
internal sealed class VanillaBundleIndex : IVanillaBundleResourceLocator
{
	private const int CacheFormatVersion = 2;
	private const int SampleSize = 64 * 1024;
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		WriteIndented = true,
		PropertyNameCaseInsensitive = true
	};

	private readonly IBundleResourceTool bundleTool;
	private readonly string cachePath;
	private readonly SemaphoreSlim gate = new( 1, 1 );

	public VanillaBundleIndex( IBundleResourceTool bundleTool, string cachePath )
	{
		this.bundleTool = bundleTool ?? throw new ArgumentNullException( nameof( bundleTool ) );

		if ( string.IsNullOrWhiteSpace( cachePath ) )
		{
			throw new ArgumentException(
				UiText.T( "VanillaBundleIndex", "A vanilla bundle-index cache path is required." ),
				nameof( cachePath ) );
		}

		this.cachePath = Path.GetFullPath( cachePath );
	}

	public async Task<string?> FindBundleAsync(
		string gameDirectory,
		string resourcePath,
		ToolJobContext context,
		CancellationToken cancellationToken = default )
	{
		string normalizedResource;
		string normalizedGameDirectory;

		ArgumentNullException.ThrowIfNull( context );

		if ( string.IsNullOrWhiteSpace( gameDirectory ) )
			throw new ArgumentException( UiText.T( "VanillaBundleIndex", "A game directory is required." ), nameof( gameDirectory ) );

		normalizedResource = RemasteredBundleFile.NormalizeResourcePath( resourcePath );

		if ( !IsIndexableResource( normalizedResource ) )
		{
			throw new ArgumentException(
				UiText.T( "VanillaBundleIndex", "The vanilla bundle locator only accepts mergeable text resources." ),
				nameof( resourcePath ) );
		}

		normalizedGameDirectory = NormalizeDirectory( gameDirectory );

		if ( !Directory.Exists( normalizedGameDirectory ) )
			throw new DirectoryNotFoundException( UiText.F( "VanillaBundleIndex", $"The Witcher 3 game directory does not exist: {normalizedGameDirectory}" ) );

		await gate.WaitAsync( cancellationToken ).ConfigureAwait( false );

		try
		{
			return await SearchBundlesAsync( normalizedGameDirectory, normalizedResource, context, cancellationToken ).ConfigureAwait( false );
		}
		finally
		{
			gate.Release();
		}
	}

	// Search highest-priority bundles first so the first match is the version the game uses.
	private async Task<string?> SearchBundlesAsync(
		string normalizedGameDirectory,
		string normalizedResource,
		ToolJobContext context,
		CancellationToken cancellationToken )
	{
		VanillaBundleCache cache = await LoadCurrentCacheAsync( normalizedGameDirectory, cancellationToken ).ConfigureAwait( false );
		IReadOnlyList<IReadOnlyList<string>> bundleGroups = DiscoverBundleGroups( normalizedGameDirectory );
		HashSet<string> discoveredBundles = new(
			bundleGroups.SelectMany( group => group ).Select( Path.GetFullPath ),
			StringComparer.OrdinalIgnoreCase );
		bool cacheChanged = cache.Bundles.RemoveAll( entry => !discoveredBundles.Contains( entry.BundlePath ) ) > 0;
		string? found = null;

		foreach ( string bundlePath in bundleGroups.Reverse().SelectMany( group => group ) )
		{
			BundleFingerprint fingerprint;
			VanillaBundleCacheEntry? entry;

			cancellationToken.ThrowIfCancellationRequested();
			fingerprint = await ComputeFingerprintAsync( bundlePath, cancellationToken ).ConfigureAwait( false );
			entry = cache.Bundles.FirstOrDefault( item => item.BundlePath.Equals( bundlePath, StringComparison.OrdinalIgnoreCase ) );

			if ( entry is null || !entry.Matches( fingerprint ) )
			{
				entry = await RefreshEntryAsync( cache, entry, bundlePath, fingerprint, context, cancellationToken ).ConfigureAwait( false );
				cacheChanged = false;
			}

			if ( entry.ResourcePaths.Contains( normalizedResource, StringComparer.OrdinalIgnoreCase ) )
			{
				found = bundlePath;

				break;
			}
		}

		if ( cacheChanged )
			await SaveCacheAsync( cache, cancellationToken ).ConfigureAwait( false );

		return found;
	}

	private async Task<VanillaBundleCache> LoadCurrentCacheAsync( string normalizedGameDirectory, CancellationToken cancellationToken )
	{
		VanillaBundleCache cache = await LoadCacheAsync( cancellationToken ).ConfigureAwait( false );

		cache.Normalize();

		if ( cache.FormatVersion == CacheFormatVersion
			&& string.Equals( cache.GameDirectory, normalizedGameDirectory, StringComparison.OrdinalIgnoreCase ) )
		{
			return cache;
		}

		return new VanillaBundleCache
		{
			FormatVersion = CacheFormatVersion,
			GameDirectory = normalizedGameDirectory
		};
	}

	private async Task<VanillaBundleCacheEntry> RefreshEntryAsync(
		VanillaBundleCache cache,
		VanillaBundleCacheEntry? entry,
		string bundlePath,
		BundleFingerprint fingerprint,
		ToolJobContext context,
		CancellationToken cancellationToken )
	{
		IReadOnlyList<string> listing = await bundleTool.ListPathsAsync( bundlePath, context, cancellationToken ).ConfigureAwait( false );
		IReadOnlyList<string> resourcePaths = listing
			.Where( IsIndexableResource )
			.Select( RemasteredBundleFile.NormalizeResourcePath )
			.Distinct( StringComparer.OrdinalIgnoreCase )
			.OrderBy( path => path, StringComparer.OrdinalIgnoreCase )
			.ToArray();

		if ( entry is null )
		{
			entry = new VanillaBundleCacheEntry();
			cache.Bundles.Add( entry );
		}

		entry.Update( fingerprint, resourcePaths );
		await SaveCacheAsync( cache, cancellationToken ).ConfigureAwait( false );

		return entry;
	}

	internal static IReadOnlyList<IReadOnlyList<string>> DiscoverBundleGroups( string gameDirectory )
	{
		return OfficialGameContent.DiscoverBundleGroups( NormalizeDirectory( gameDirectory ) );
	}

	private static bool IsIndexableResource( string path )
	{
		return path.EndsWith( ".xml", StringComparison.OrdinalIgnoreCase )
			|| path.EndsWith( ".ws", StringComparison.OrdinalIgnoreCase )
			|| path.EndsWith( ".txt", StringComparison.OrdinalIgnoreCase )
			|| path.EndsWith( ".csv", StringComparison.OrdinalIgnoreCase );
	}

	// Sample blocks to detect content changes without rereading each entire bundle.
	private static async Task<BundleFingerprint> ComputeFingerprintAsync(
		string bundlePath,
		CancellationToken cancellationToken )
	{
		return await Task.Run( () =>
		{
			cancellationToken.ThrowIfCancellationRequested();

			FileInfo file = new( bundlePath );

			if ( !file.Exists )
			{
				throw new FileNotFoundException(
					UiText.T( "VanillaBundleIndex", "The vanilla bundle disappeared while it was being indexed." ),
					bundlePath );
			}

			using FileStream stream = new( bundlePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete );
			using IncrementalHash hash = IncrementalHash.CreateHash( HashAlgorithmName.SHA256 );

			hash.AppendData( BitConverter.GetBytes( stream.Length ) );

			long[] offsets = GetSampleOffsets( stream.Length ).Distinct().ToArray();
			byte[] buffer = ArrayPool<byte>.Shared.Rent( SampleSize );

			try
			{
				foreach ( long offset in offsets )
				{
					cancellationToken.ThrowIfCancellationRequested();
					hash.AppendData( BitConverter.GetBytes( offset ) );
					stream.Position = offset;

					int requested = (int)Math.Min( SampleSize, stream.Length - offset );
					int readTotal = 0;

					while ( readTotal < requested )
					{
						int read = stream.Read( buffer, readTotal, requested - readTotal );

						if ( read == 0 )
						{
							break;
						}

						readTotal += read;
					}

					hash.AppendData( buffer, 0, readTotal );
				}
			}
			finally
			{
				ArrayPool<byte>.Shared.Return( buffer );
			}

			return new BundleFingerprint(
				Path.GetFullPath( bundlePath ),
				file.Length,
				file.LastWriteTimeUtc.Ticks,
				Convert.ToHexString( hash.GetHashAndReset() ) );
		}, cancellationToken ).ConfigureAwait( false );
	}

	private static IEnumerable<long> GetSampleOffsets( long length )
	{
		if ( length <= SampleSize )
		{
			yield return 0;

			yield break;
		}

		yield return 0;

		yield return Math.Max( 0, length / 3 - SampleSize / 2 );

		yield return Math.Max( 0, length * 2 / 3 - SampleSize / 2 );

		yield return Math.Max( 0, length - SampleSize );
	}

	private async Task<VanillaBundleCache> LoadCacheAsync( CancellationToken cancellationToken )
	{
		if ( !File.Exists( cachePath ) )
		{
			return new VanillaBundleCache();
		}

		try
		{
			await using FileStream stream = new( cachePath, FileMode.Open, FileAccess.Read, FileShare.Read );

			return await JsonSerializer.DeserializeAsync<VanillaBundleCache>( stream, JsonOptions, cancellationToken ).ConfigureAwait( false )
				?? new VanillaBundleCache();
		}
		catch ( JsonException )
		{
			return new VanillaBundleCache();
		}
		catch ( IOException )
		{
			return new VanillaBundleCache();
		}
	}

	// Sort entries before saving so cache contents are independent of scan order.
	private async Task SaveCacheAsync( VanillaBundleCache cache, CancellationToken cancellationToken )
	{
		string? directory = Path.GetDirectoryName( cachePath );

		if ( directory is not null )
		{
			Directory.CreateDirectory( directory );
		}

		cache.Bundles.Sort( ( left, right ) => StringComparer.OrdinalIgnoreCase.Compare( left.BundlePath, right.BundlePath ) );

		string tempPath = cachePath + ".tmp." + Guid.NewGuid().ToString( "N" );
		string backupPath = cachePath + ".bak";

		try
		{
			await using ( FileStream stream = new(
				tempPath,
				FileMode.CreateNew,
				FileAccess.Write,
				FileShare.None,
				bufferSize: 64 * 1024,
				FileOptions.Asynchronous | FileOptions.WriteThrough ) )
			{
				await JsonSerializer.SerializeAsync( stream, cache, JsonOptions, cancellationToken ).ConfigureAwait( false );
				await stream.FlushAsync( cancellationToken ).ConfigureAwait( false );
				stream.Flush( flushToDisk: true );
			}

			if ( File.Exists( cachePath ) )
			{
				TryDeleteFile( backupPath );
				File.Replace( tempPath, cachePath, backupPath, ignoreMetadataErrors: true );
				TryDeleteFile( backupPath );
			}
			else
			{
				File.Move( tempPath, cachePath );
			}
		}
		finally
		{
			if ( File.Exists( tempPath ) )
			{
				File.Delete( tempPath );
			}
		}
	}

	private static string NormalizeDirectory( string path )
	{
		string fullPath = Path.GetFullPath( path );
		string root = Path.GetPathRoot( fullPath ) ?? string.Empty;

		return fullPath.Equals( root, StringComparison.OrdinalIgnoreCase )
			? fullPath
			: fullPath.TrimEnd( Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar );
	}

	private static void TryDeleteFile( string path )
	{
		try
		{
			if ( File.Exists( path ) )
			{
				File.Delete( path );
			}
		}
		catch ( IOException )
		{
		}
		catch ( UnauthorizedAccessException )
		{
		}
	}

	private sealed class VanillaBundleCache
	{
		public int FormatVersion { get; set; } = CacheFormatVersion;

		public string GameDirectory { get; set; } = string.Empty;

		public List<VanillaBundleCacheEntry> Bundles { get; set; } = new();

		public void Normalize()
		{
			GameDirectory ??= string.Empty;
			Bundles ??= new List<VanillaBundleCacheEntry>();
			Bundles.RemoveAll( entry => entry is null );

			foreach ( VanillaBundleCacheEntry entry in Bundles )
			{
				entry.Normalize();
			}
		}
	}

	private sealed class VanillaBundleCacheEntry
	{
		public string BundlePath { get; set; } = string.Empty;

		public long Length { get; set; }

		public long LastWriteUtcTicks { get; set; }

		public string SparseHash { get; set; } = string.Empty;

		public List<string> ResourcePaths { get; set; } = new();

		public bool Matches( BundleFingerprint fingerprint )
		{
			return string.Equals( BundlePath, fingerprint.BundlePath, StringComparison.OrdinalIgnoreCase )
				&& Length == fingerprint.Length
				&& LastWriteUtcTicks == fingerprint.LastWriteUtcTicks
				&& SparseHash.Equals( fingerprint.SparseHash, StringComparison.OrdinalIgnoreCase );
		}

		public void Update( BundleFingerprint fingerprint, IReadOnlyList<string> resourcePaths )
		{
			BundlePath = fingerprint.BundlePath;
			Length = fingerprint.Length;
			LastWriteUtcTicks = fingerprint.LastWriteUtcTicks;
			SparseHash = fingerprint.SparseHash;
			ResourcePaths = resourcePaths.ToList();
		}

		public void Normalize()
		{
			BundlePath ??= string.Empty;
			SparseHash ??= string.Empty;
			ResourcePaths ??= new List<string>();
			ResourcePaths.RemoveAll( string.IsNullOrWhiteSpace );
		}
	}

	private sealed record BundleFingerprint(
		string BundlePath,
		long Length,
		long LastWriteUtcTicks,
		string SparseHash );
}
