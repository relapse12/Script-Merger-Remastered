#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using WSCompiler.Core.Scripts.Standalone;
using WSCompiler.Recovery;
using WitcherScriptMerger.Merging;

namespace WitcherScriptMerger.BlobMerging;

// These are readable exports for inspection. Keep edited exports and report save failures;
// merge recovery uses separate saved files.
internal static class RecoveredScriptStore
{
	private sealed record Saved( string Blob, string Engine, Dictionary<string, string> Files, string Name );

	private static readonly ConcurrentDictionary<string, string> issues = new( StringComparer.OrdinalIgnoreCase );
	internal static IReadOnlyList<string> Issues => issues.Values.ToArray();
	internal static string DirectoryPath => Path.Combine( AppContext.BaseDirectory, "Data", "Recovered Scripts" );

	// Skip unchanged exports and preserve any file the user edited.
	internal static void Save( string name, byte[] blob, IReadOnlyList<SourceFile> files, IReadOnlyList<SourceFile> vanilla,
		string? directory = null )
	{
		string root = directory ?? DirectoryPath;
		string target = MergeBuilder.SafePath( root, name );
		string metadata = Path.Combine( directory is null ? OwnershipDirectory : root + ".cache",
			"recovered-" + MergeBuilder.Hash( Encoding.UTF8.GetBytes( target.ToUpperInvariant() ) ) + ".json" );

		try
		{
			Saved? saved = File.Exists( metadata ) ? JsonSerializer.Deserialize<Saved>( File.ReadAllText( metadata ) ) : null;
			string hash = MergeBuilder.Hash( blob ), engine = BlobMergeCheckCache.Engine;
			var paths = vanilla.SelectMany( file => file.Items.Select( item => (Key: MergePlanner.Key( item ), file.Path) ) )
				.GroupBy( pair => pair.Key, StringComparer.Ordinal ).ToDictionary( group => group.Key, group => group.First().Path, StringComparer.Ordinal );
			var output = files.SelectMany( file => file.Items ).GroupBy( item =>
				item is FunctionItem { Decl.Annotation: not null } or FieldItem { Decl.Annotation: not null } ? "local/" + name + ".ws"
					: paths.GetValueOrDefault( MergePlanner.Key( item ), "local/" + name + ".ws" ), StringComparer.OrdinalIgnoreCase )
				.ToDictionary(
					group => "content/scripts/" + group.Key.Replace( '\\', '/' ),
					group => SourceWriter.Write( group.ToArray() ),
					StringComparer.OrdinalIgnoreCase );

			if ( saved?.Blob == hash && saved.Engine == engine && saved.Files.Count == output.Count
				&& output.All( pair => saved.Files.GetValueOrDefault( pair.Key ) == MergeBuilder.Hash( Encoding.UTF8.GetBytes( pair.Value ) ) )
				&& saved.Files.All( pair => File.Exists( MergeBuilder.SafePath( target, pair.Key ) ) && MergeBuilder.Hash( MergeBuilder.SafePath( target, pair.Key ) ) == pair.Value ) )
			{
				issues.TryRemove( target, out _ );

				return;
			}

			foreach ( string relative in output.Keys.Concat( saved?.Files.Keys ?? Enumerable.Empty<string>() ).Distinct( StringComparer.OrdinalIgnoreCase ) )
			{
				string path = MergeBuilder.SafePath( target, relative );

				if ( File.Exists( path ) && !( saved?.Files.TryGetValue( relative, out string? expected ) == true && MergeBuilder.Hash( path ) == expected )
					&& !( output.TryGetValue( relative, out string? current ) && MergeBuilder.Hash( path ) == MergeBuilder.Hash( Encoding.UTF8.GetBytes( current ) ) ) )
					throw new IOException( UiText.Format( "BlobMerge", "A recovered script was edited or is not owned by SM. The file was kept: {0}", path ) );
			}

			foreach ( var pair in output )
			{
				string path = MergeBuilder.SafePath( target, pair.Key );

				Directory.CreateDirectory( Path.GetDirectoryName( path )! );

				if ( !File.Exists( path ) || File.ReadAllText( path ) != pair.Value )
					File.WriteAllText( path, pair.Value, new UTF8Encoding( false ) );
			}

			foreach ( string relative in ( saved?.Files.Keys ?? Enumerable.Empty<string>() ).Except( output.Keys, StringComparer.OrdinalIgnoreCase ) )
				File.Delete( MergeBuilder.SafePath( target, relative ) );

			var fingerprint = new Saved(
				hash,
				engine,
				output.ToDictionary( pair => pair.Key, pair => MergeBuilder.Hash( Encoding.UTF8.GetBytes( pair.Value ) ) ),
				name );

			Directory.CreateDirectory( Path.GetDirectoryName( metadata )! );
			File.WriteAllText( metadata, JsonSerializer.Serialize( fingerprint ), new UTF8Encoding( false ) );
			issues.TryRemove( target, out _ );
		}
		catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException )
		{
			issues[target] = UiText.Format( "BlobMerge", "Recovered scripts could not be saved for {0}: {1}", name, error.Message );
		}
	}

	private static string OwnershipDirectory => Path.Combine( BlobMergeStore.ApplicationStorage, "Recovered Script Ownership" );

	// Keep the whole export if any of its files was edited.
	internal static void Prune( IReadOnlyDictionary<string, string> activeBlobs, string? directory = null )
	{
		string root = directory ?? DirectoryPath, ownership = directory is null ? OwnershipDirectory : root + ".cache";

		if ( !Directory.Exists( ownership ) )
			return;

		foreach ( string metadata in Directory.GetFiles( ownership, "recovered-*.json" ) )
		{
			try
			{
				var saved = JsonSerializer.Deserialize<Saved>( File.ReadAllText( metadata ) );

				if ( saved is null || activeBlobs.TryGetValue( saved.Name, out string? hash ) && hash == saved.Blob )
					continue;

				string target = MergeBuilder.SafePath( root, saved.Name );
				var changed = saved.Files.FirstOrDefault( pair => File.Exists( MergeBuilder.SafePath( target, pair.Key ) )
					&& MergeBuilder.Hash( MergeBuilder.SafePath( target, pair.Key ) ) != pair.Value );

				if ( changed.Key is not null )
				{
					issues[target] = UiText.Format(
						"BlobMerge",
						"A recovered script was edited or is not owned by SM. The file was kept: {0}",
						MergeBuilder.SafePath( target, changed.Key ) );

					continue;
				}

				foreach ( string relative in saved.Files.Keys )
				{
					string path = MergeBuilder.SafePath( target, relative );

					if ( File.Exists( path ) )
						File.Delete( path );

					string? parent = Path.GetDirectoryName( path );

					while ( parent is not null && parent.StartsWith( target, StringComparison.OrdinalIgnoreCase )
						&& Directory.Exists( parent ) && !Directory.EnumerateFileSystemEntries( parent ).Any() )
					{
						Directory.Delete( parent );
						parent = Path.GetDirectoryName( parent );
					}
				}

				File.Delete( metadata );
				issues.TryRemove( target, out _ );
			}
			catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException ) { }
		}
	}

	// Wait for interrupted updates to recover before removing exports, because the active blobs are not yet known.
	internal static void Prune( string inventory, IReadOnlyDictionary<string, string>? plannedBlobs = null )
	{
		if ( new BlobMergeStore( inventory ).HasInterruptedUpdate )
			return;

		try
		{
			var applied = BlobMergeTransaction.HasState( inventory ) ? BlobMergeTransaction.ReadApplied( inventory ) : null;
			var active = ( applied?.Manifest.Inputs ?? [] ).Where( input => input.SuppressedPath is not null
				&& input.Path.EndsWith( ".rsblob", StringComparison.OrdinalIgnoreCase ) )
				.ToDictionary( input => Path.GetRelativePath( applied!.Manifest.ModsRoot, input.Path )
					.Split( Path.DirectorySeparatorChar )[0], input => input.Sha256, StringComparer.OrdinalIgnoreCase );

			foreach ( var pair in plannedBlobs ?? new Dictionary<string, string>() )
				active[pair.Key] = pair.Value;

			Prune( active );
		}
		catch ( Exception error ) when ( BlobMergeWorkspace.IsInspectionFailure( error ) ) { }
	}
}
