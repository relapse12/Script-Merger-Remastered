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

internal sealed record ScriptSemanticSymbol( string Key, ScriptSymbolKind Kind, ScopedScriptMember? Member = null );

internal sealed record RsBlobSymbolCacheResult(
	IReadOnlyList<ScriptSemanticSymbol> Symbols,
	bool CacheHit,
	string SourceSha256,
	RsBlobDependencyMetadata? Dependencies = null );

// Reuse scanned blob symbols until the blob or parser changes.
internal sealed class RsBlobSymbolCache
{
	internal const int CurrentSchemaVersion = 5;

	private const string CacheFolderName = "RsBlob Symbol Cache";
	private const string BlobParserFingerprint = "SCRIPT-MERGER-REMASTERED-RSBLOB-SYMBOLS-V8-SCOPED-MEMBERS";

	private readonly string cacheDirectory;
	private readonly ConcurrentDictionary<string, object> cacheKeyLocks = new( StringComparer.OrdinalIgnoreCase );

	public RsBlobSymbolCache( string cacheDirectory )
	{
		if ( string.IsNullOrWhiteSpace( cacheDirectory ) )
		{
			throw new ArgumentException(
				UiText.T( "ScriptSymbolCache", "A script-symbol cache directory is required." ),
				nameof( cacheDirectory ) );
		}

		this.cacheDirectory = Path.GetFullPath( cacheDirectory );
	}

	public static string DefaultCacheDirectory => Path.Combine(
		AppContext.BaseDirectory,
		"Data",
		CacheFolderName );

	public RsBlobSymbolCacheResult GetOrCreate(
		string sourcePath,
		Func<IReadOnlyList<ScriptSemanticSymbol>> symbolFactory )
	{
		ArgumentNullException.ThrowIfNull( symbolFactory );

		return GetOrCreateCore( sourcePath, () => new RsBlobSymbolCacheResult( symbolFactory(), false, string.Empty ), false );
	}

	public RsBlobSymbolCacheResult GetOrCreateIndex( string sourcePath, Func<RsBlobSymbolIndex> indexFactory )
	{
		ArgumentNullException.ThrowIfNull( indexFactory );

		return GetOrCreateCore( sourcePath, () =>
		{
			RsBlobSymbolIndex index = indexFactory();

			return new RsBlobSymbolCacheResult(
				index.Symbols.Select( value => new ScriptSemanticSymbol( value.Key, value.Kind, value.Member ) ).ToArray(),
				false,
				string.Empty,
				index.Dependencies ?? RsBlobDependencyMetadata.Empty );
		}, true );
	}

	// A file can change during parsing. Verify its hash afterward and retry once before giving up.
	private RsBlobSymbolCacheResult GetOrCreateCore( string sourcePath,
		Func<RsBlobSymbolCacheResult> symbolFactory, bool includeDependencies )
	{
		ArgumentNullException.ThrowIfNull( symbolFactory );

		string fullSourcePath = Path.GetFullPath( sourcePath );
		string parserFingerprint = BlobParserFingerprint + ( includeDependencies ? "-FULL" : "-SYMBOLS" );

		for ( int attempt = 0; attempt < 2; attempt++ )
		{
			SourceIdentity identity = ComputeIdentity( fullSourcePath );
			string cacheKey = ComputeCompositeHash(
				CurrentSchemaVersion.ToString(),
				identity.Length.ToString(),
				identity.Sha256,
				parserFingerprint );
			string cachePath = Path.Combine( cacheDirectory, cacheKey + ".json" );
			object keyLock = cacheKeyLocks.GetOrAdd( cacheKey, static _ => new object() );

			lock ( keyLock )
			{
				if ( TryRead( cachePath, identity, parserFingerprint, out ScriptSemanticSymbol[] cached, out var cachedDependencies ) )
				{
					return new RsBlobSymbolCacheResult( cached, CacheHit: true, identity.Sha256, cachedDependencies );
				}

				var parsed = symbolFactory();
				ScriptSemanticSymbol[] symbols = Normalize( parsed.Symbols );
				RsBlobDependencyMetadata dependencies = NormalizeDependencies( parsed.Dependencies );
				SourceIdentity afterParse = ComputeIdentity( fullSourcePath );

				if ( afterParse != identity )
				{
					if ( attempt == 0 )
					{
						continue;
					}

					throw new IOException( UiText.T( "ScriptSymbolCache", "The script source changed while its symbols were being indexed." ) );
				}

				ScriptSymbolCacheEntry entry = new()
				{
					SchemaVersion = CurrentSchemaVersion,
					SourceLength = identity.Length,
					SourceSha256 = identity.Sha256,
					ParserFingerprint = parserFingerprint,
					SymbolsSha256 = ComputeSymbolsSha256( symbols ),
					Symbols = symbols,
					Dependencies = dependencies,
					DependenciesSha256 = ComputeDependenciesSha256( dependencies )
				};

				TryWriteAtomically( cachePath, entry );

				return new RsBlobSymbolCacheResult( symbols, CacheHit: false, identity.Sha256, dependencies );
			}
		}

		throw new IOException( UiText.T( "ScriptSymbolCache", "The script source could not be indexed from a stable snapshot." ) );
	}

	private static SourceIdentity ComputeIdentity( string path )
	{
		using FileStream stream = new(
			path,
			FileMode.Open,
			FileAccess.Read,
			FileShare.Read,
			bufferSize: 1024 * 1024,
			FileOptions.SequentialScan );

		return new SourceIdentity( stream.Length, Convert.ToHexString( SHA256.HashData( stream ) ) );
	}

	// Sort and combine duplicate keys before hashing so enumeration order does not change the result.
	private static ScriptSemanticSymbol[] Normalize( IEnumerable<ScriptSemanticSymbol>? symbols )
	{
		if ( symbols is null )
		{
			throw new InvalidDataException( UiText.T( "ScriptSymbolCache", "Script symbol parsing returned no result." ) );
		}

		var values = symbols.ToArray();

		foreach ( var symbol in values )
		{
			if ( symbol is null || string.IsNullOrWhiteSpace( symbol.Key ) )
				throw new InvalidDataException( "Invalid cached script symbol." );

			if ( symbol.Member is { } member )
			{
				if ( string.IsNullOrWhiteSpace( member.Owner ) || string.IsNullOrWhiteSpace( member.Name )
					|| member.Type is null || member.Binding is null || member.Value is null
					|| !member.Key.Equals( symbol.Key, StringComparison.OrdinalIgnoreCase ) || member.Kind != symbol.Kind )
					throw new InvalidDataException( "Invalid cached scoped member." );

				ScopedScriptMember.NormalizeType( member.Type );
			}
		}

		return values
			.GroupBy( symbol => symbol.Key, StringComparer.OrdinalIgnoreCase )
			.Select( group => new ScriptSemanticSymbol(
				group.Key,
				group.Aggregate(
					ScriptSymbolKind.None,
					( kinds, symbol ) => kinds | symbol.Kind ), group.Last().Member ) )
			.OrderBy( symbol => symbol.Key, StringComparer.OrdinalIgnoreCase )
			.ToArray();
	}

	private static bool TryRead(
		string cachePath,
		SourceIdentity expectedIdentity,
		string expectedParserFingerprint,
		out ScriptSemanticSymbol[] symbols,
		out RsBlobDependencyMetadata dependencies )
	{
		symbols = Array.Empty<ScriptSemanticSymbol>();
		dependencies = RsBlobDependencyMetadata.Empty;

		if ( !File.Exists( cachePath ) )
		{
			return false;
		}

		try
		{
			ScriptSymbolCacheEntry? entry = JsonSerializer.Deserialize<ScriptSymbolCacheEntry>(
				File.ReadAllText( cachePath, Encoding.UTF8 ) );

			if ( entry is null
				|| entry.SchemaVersion != CurrentSchemaVersion
				|| entry.SourceLength != expectedIdentity.Length
				|| !string.Equals( entry.SourceSha256, expectedIdentity.Sha256, StringComparison.Ordinal )
				|| !string.Equals( entry.ParserFingerprint, expectedParserFingerprint, StringComparison.Ordinal )
				|| entry.Symbols is null || entry.Dependencies is null )
			{
				DeleteInvalidEntry( cachePath );

				return false;
			}

			ScriptSemanticSymbol[] normalized = Normalize( entry.Symbols );
			var normalizedDependencies = NormalizeDependencies( entry.Dependencies );

			if ( !string.Equals( entry.SymbolsSha256, ComputeSymbolsSha256( normalized ), StringComparison.Ordinal )
				|| !string.Equals(
					entry.DependenciesSha256,
					ComputeDependenciesSha256( normalizedDependencies ),
					StringComparison.Ordinal ) )
			{
				DeleteInvalidEntry( cachePath );

				return false;
			}

			symbols = normalized;
			dependencies = normalizedDependencies;

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

	// A failed cache write only means the next scan must parse the blob again.
	private static void TryWriteAtomically( string cachePath, ScriptSymbolCacheEntry entry )
	{
		string? tempPath = null;

		try
		{
			Directory.CreateDirectory( Path.GetDirectoryName( cachePath )! );
			tempPath = cachePath + "." + Guid.NewGuid().ToString( "N" ) + ".tmp";
			File.WriteAllText(
				tempPath,
				JsonSerializer.Serialize( entry ),
				new UTF8Encoding( encoderShouldEmitUTF8Identifier: false ) );
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

	// Include each key's length so different sequences of names cannot produce the same byte sequence.
	private static string ComputeSymbolsSha256( IEnumerable<ScriptSemanticSymbol> symbols )
	{
		using IncrementalHash hash = IncrementalHash.CreateHash( HashAlgorithmName.SHA256 );

		foreach ( ScriptSemanticSymbol symbol in symbols )
		{
			if ( string.IsNullOrWhiteSpace( symbol.Key ) )
			{
				throw new InvalidDataException( UiText.T( "ScriptSymbolCache", "The script-symbol cache contains an empty key." ) );
			}

			byte[] keyBytes = Encoding.UTF8.GetBytes( symbol.Key );

			hash.AppendData( BitConverter.GetBytes( keyBytes.Length ) );
			hash.AppendData( keyBytes );
			hash.AppendData( BitConverter.GetBytes( (int)symbol.Kind ) );
			hash.AppendData( Encoding.UTF8.GetBytes( JsonSerializer.Serialize( symbol.Member ) ) );
		}

		return Convert.ToHexString( hash.GetHashAndReset() );
	}

	private static string ComputeCompositeHash( params string[] values )
	{
		return Convert.ToHexString( SHA256.HashData( Encoding.UTF8.GetBytes( string.Join( "\n", values ) ) ) );
	}

	private static RsBlobDependencyMetadata NormalizeDependencies( RsBlobDependencyMetadata? value )
	{
		// Use the same empty arrays when writing and reading metadata. Leaving TypeParents null on write
		// would produce a different hash when the reader restores an empty list.
		value ??= RsBlobDependencyMetadata.Empty;

		if ( value.Types is null || value.Requirements is null
			|| value.Types.Any( string.IsNullOrWhiteSpace )
			|| value.Requirements.Any( item => item is null || string.IsNullOrWhiteSpace( item.Key ) || !Enum.IsDefined( item.Kind ) ) )
			throw new InvalidDataException( UiText.T( "ScriptSymbolCache", "The script-symbol cache contains invalid dependency metadata." ) );

		var enums = value.Enums ?? Array.Empty<RsBlobEnumDefinition>();

		if ( enums.Any( item => item is null || string.IsNullOrWhiteSpace( item.Name ) || item.Members is null
			|| item.Members.Any( member => member is null || string.IsNullOrWhiteSpace( member.Name ) ) ) )
			throw new InvalidDataException( UiText.T( "ScriptSymbolCache", "The script-symbol cache contains invalid enum metadata." ) );

		if ( ( value.TypeParents ?? Array.Empty<RsBlobTypeParent>() ).Any( parent => parent is null
			|| string.IsNullOrWhiteSpace( parent.Name ) || string.IsNullOrWhiteSpace( parent.Parent ) ) )
			throw new InvalidDataException( "Invalid cached class inheritance." );

		return new RsBlobDependencyMetadata( value.Types.Distinct( StringComparer.OrdinalIgnoreCase )
			.OrderBy( item => item, StringComparer.OrdinalIgnoreCase ).ToArray(),
			value.Requirements.Distinct().OrderBy( item => item.Key, StringComparer.OrdinalIgnoreCase ).ThenBy( item => item.Kind ).ToArray(),
			enums.OrderBy( item => item.Name, StringComparer.Ordinal ).ToArray(),
			( value.TypeParents ?? Array.Empty<RsBlobTypeParent>() ).Distinct().OrderBy( item => item.Name, StringComparer.Ordinal ).ToArray() );
	}

	private static string ComputeDependenciesSha256( RsBlobDependencyMetadata value ) =>
		Convert.ToHexString( SHA256.HashData( Encoding.UTF8.GetBytes( JsonSerializer.Serialize( value ) ) ) );

	private sealed class ScriptSymbolCacheEntry
	{
		public int SchemaVersion { get; set; }

		public long SourceLength { get; set; }

		public string SourceSha256 { get; set; } = string.Empty;

		public string ParserFingerprint { get; set; } = string.Empty;

		public string SymbolsSha256 { get; set; } = string.Empty;

		public ScriptSemanticSymbol[] Symbols { get; set; } = Array.Empty<ScriptSemanticSymbol>();

		public RsBlobDependencyMetadata? Dependencies { get; set; }

		public string DependenciesSha256 { get; set; } = string.Empty;
	}

	private sealed record SourceIdentity( long Length, string Sha256 );
}
