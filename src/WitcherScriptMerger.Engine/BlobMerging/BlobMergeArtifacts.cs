#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using WSCompiler.Core.Scripts.Standalone;
using WitcherScriptMerger.Merging;

namespace WitcherScriptMerger.BlobMerging;

// Keep recovered source and merge plans between runs to avoid repeating expensive work.
// Content hashes and a build fingerprint prevent reuse after inputs or merge code change.
internal static class BlobMergeArtifacts
{
	private const int MaximumBytes = 128 * 1024 * 1024;
	internal const int MaximumDiskFiles = 64;
	internal const long MaximumDiskBytes = 32 * 1024 * 1024;
	private static readonly object diskLock = new();
	private static readonly ConcurrentDictionary<string, byte[]> memory = new( StringComparer.Ordinal );
	private static readonly ConcurrentDictionary<string, long> hits = new( StringComparer.Ordinal );
	private static readonly ConcurrentDictionary<string, long> builds = new( StringComparer.Ordinal );
	private static long memoryBytes;
	internal static readonly JsonSerializerOptions Json = CreateJson();
	internal static string DirectoryPath => Path.Combine( BlobMergeCheckCache.DefaultDirectory, "Artifacts" );

	internal static long Hits( string kind ) => hits.GetValueOrDefault( kind );

	internal static long Builds( string kind ) => builds.GetValueOrDefault( kind );

	internal static void RecordBuild( string kind ) => builds.AddOrUpdate( kind, 1, ( _, count ) => count + 1 );

	internal static void ClearMemory()
	{
		memory.Clear();
		Interlocked.Exchange( ref memoryBytes, 0 );
		BlobMergeValidation.ClearMemory();
	}

	internal static string Identity<T>( T value ) => MergeBuilder.Hash( JsonSerializer.SerializeToUtf8Bytes( value, Json ) );

	internal static T Get<T>( string kind, string identity, Func<T> create ) where T : class
	{
		if ( Read<T>( kind, identity ) is { } cached )
			return cached;

		RecordBuild( kind );

		T value = create();

		Write( kind, identity, value );

		return value;
	}

	// A bad cache entry can be rebuilt, so treat missing, oversized or unreadable data as a cache miss.
	internal static T? Read<T>( string kind, string identity ) where T : class
	{
		string path = FilePath( kind, identity );

		try
		{
			if ( !memory.TryGetValue( path, out byte[]? bytes ) )
			{
				if ( !File.Exists( path ) )
					return null;

				using var input = File.OpenRead( path );
				byte[] expected = new byte[32];

				input.ReadExactly( expected );

				using var compressed = new GZipStream( input, CompressionMode.Decompress );
				using var output = new MemoryStream();
				byte[] buffer = new byte[65536];
				int count;

				while ( ( count = compressed.Read( buffer ) ) > 0 )
				{
					if ( output.Length + count > MaximumBytes )
						return null;

					output.Write( buffer, 0, count );
				}

				bytes = output.ToArray();

				if ( !SHA256.HashData( bytes ).SequenceEqual( expected ) )
					return null;

				Remember( path, bytes );
			}

			T? value = JsonSerializer.Deserialize<T>( bytes, Json );

			if ( value is not null )
				hits.AddOrUpdate( kind, 1, ( _, count ) => count + 1 );

			return value;
		}
		catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException )
		{
			return null;
		}
	}

	// Failure to save the cache must not fail the merge.
	internal static void Write<T>( string kind, string identity, T value )
	{
		string path = FilePath( kind, identity ), temporary = path + "." + Guid.NewGuid().ToString( "N" ) + ".tmp";

		try
		{
			byte[] bytes = JsonSerializer.SerializeToUtf8Bytes( value, Json );

			if ( bytes.Length > MaximumBytes )
				return;

			Directory.CreateDirectory( DirectoryPath );

			using ( var output = File.Create( temporary ) )
			{
				output.Write( SHA256.HashData( bytes ) );

				using var compressed = new GZipStream( output, CompressionLevel.Fastest );

				compressed.Write( bytes );
			}

			lock ( diskLock )
			{
				File.Move( temporary, path, overwrite: true );
				PruneDisk();
			}

			Remember( path, bytes );
		}
		catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException ) { }
		finally { if ( File.Exists( temporary ) ) try { File.Delete( temporary ); } catch ( IOException ) { } catch ( UnauthorizedAccessException ) { } }
	}

	private static string FilePath( string kind, string identity ) => Path.Combine( DirectoryPath,
		BlobMergeCheckCache.Engine + "-" + MergeBuilder.Hash( Encoding.UTF8.GetBytes( kind + "\n" + identity ) ) + ".cache.gz" );

	private static void PruneDisk()
	{
		var files = new DirectoryInfo( DirectoryPath ).GetFiles( "*.cache.gz" )
			.Where( file => System.Text.RegularExpressions.Regex.IsMatch( file.Name, @"\A[A-F0-9]{64}-[A-F0-9]{64}\.cache\.gz\z" ) ).ToArray();

		foreach ( var obsolete in files.Where( file => !file.Name.StartsWith( BlobMergeCheckCache.Engine + "-", StringComparison.Ordinal ) ) )
			File.Delete( MergeBuilder.SafePath( DirectoryPath, obsolete.Name ) );

		var current = files.Where( file => file.Name.StartsWith( BlobMergeCheckCache.Engine + "-", StringComparison.Ordinal ) )
			.OrderByDescending( file => file.LastWriteTimeUtc ).ToArray();
		long bytes = 0;

		for ( int index = 0; index < current.Length; index++ )
		{
			bytes += current[index].Length;

			if ( index >= MaximumDiskFiles || bytes > MaximumDiskBytes )
			{
				File.Delete( MergeBuilder.SafePath( DirectoryPath, current[index].Name ) );
				memory.TryRemove( current[index].FullName, out _ );
			}
		}
	}

	private static void Remember( string path, byte[] bytes )
	{
		if ( Interlocked.Read( ref memoryBytes ) > MaximumBytes )
			ClearMemory();

		if ( memory.TryAdd( path, bytes ) )
			Interlocked.Add( ref memoryBytes, bytes.Length );
		else
			memory[path] = bytes;
	}

	// Store the concrete declaration and expression types so JSON can restore them.
	// Computed properties are rebuilt from that data.
	private static JsonSerializerOptions CreateJson()
	{
		var resolver = new DefaultJsonTypeInfoResolver();

		resolver.Modifiers.Add( info =>
		{
			string[] computed = info.Type == typeof( MergePlan ) ? new[] { "NeedsCompilation", "CompiledDefinitions" }
				: info.Type == typeof( PlannedFile ) ? new[] { "Source" } : Array.Empty<string>();

			foreach ( var property in info.Properties.Where( property => computed.Contains( property.Name ) ).ToArray() )
				info.Properties.Remove( property );

			if ( info.Type != typeof( Item ) && info.Type != typeof( StmtKind ) && info.Type != typeof( ExprKind ) && info.Type != typeof( SwitchItem ) )
				return;

			info.PolymorphismOptions = new JsonPolymorphismOptions();

			foreach ( var type in typeof( Item ).Assembly.GetTypes().Where( type => type.BaseType == info.Type && type.IsSealed ) )
				info.PolymorphismOptions.DerivedTypes.Add( new JsonDerivedType( type, type.Name ) );
		} );

		var options = new JsonSerializerOptions
		{
			IncludeFields = true,
			MaxDepth = 512,
			TypeInfoResolver = resolver,
			NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
		};

		options.Converters.Add( new TypeRefConverter() );

		return options;
	}

	private sealed class TypeRefConverter : JsonConverter<TypeRef>
	{
		public override TypeRef Read( ref Utf8JsonReader reader, Type type, JsonSerializerOptions options )
		{
			using var json = JsonDocument.ParseValue( ref reader );

			return new( json.RootElement.GetProperty( "Name" ).GetString()!,
				json.RootElement.GetProperty( "Args" ).Deserialize<System.Collections.Generic.List<TypeRef>>( options )! );
		}

		public override void Write( Utf8JsonWriter writer, TypeRef value, JsonSerializerOptions options )
		{
			writer.WriteStartObject();
			writer.WriteString( "Name", value.Name );
			writer.WritePropertyName( "Args" );
			JsonSerializer.Serialize( writer, value.Args, options );
			writer.WriteEndObject();
		}
	}
}
