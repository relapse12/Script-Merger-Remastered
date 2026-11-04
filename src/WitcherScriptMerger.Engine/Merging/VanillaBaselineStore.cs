#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;


namespace WitcherScriptMerger.Merging;

public sealed record VanillaBaseline( string Root, string Fingerprint, int Count, int Schema = 1 );

// Store vanilla file hashes in Data to keep MergeInventory.xml small. The list can be regenerated if lost.
// These records are only for comparison; they never authorize changes to vanilla files.
internal static class VanillaBaselineStore
{
	private sealed record Entry( string Path, string Sha256 );

	internal static bool Within( string path, string root ) =>
		Path.GetFullPath( path ).Equals( Path.TrimEndingDirectorySeparator( Path.GetFullPath( root ) ), StringComparison.OrdinalIgnoreCase )
		|| Path.GetFullPath( path ).StartsWith( Path.TrimEndingDirectorySeparator( Path.GetFullPath( root ) ) + Path.DirectorySeparatorChar,
			StringComparison.OrdinalIgnoreCase );

	// Use consistent path casing and ordering so equivalent baselines produce the same hash.
	private static Entry[] Entries( string root, IEnumerable<MergeInputFile> inputs ) => inputs
		.Where( input => Within( input.Path, root ) && input.Path.EndsWith( ".ws", StringComparison.OrdinalIgnoreCase )
			&& input.SuppressedPath is null && input.ReadPath is null )
		.Select( input => new Entry( Path.GetRelativePath( root, input.Path ).Replace( '\\', '/' ).ToUpperInvariant(), input.Sha256 ) )
		.OrderBy( input => input.Path, StringComparer.Ordinal ).ToArray();

	private static string Fingerprint( Entry[] entries ) => MergeBuilder.Hash( JsonSerializer.SerializeToUtf8Bytes( entries ) );

	internal static VanillaBaseline? Describe(
		string root,
		IReadOnlyList<MergeInputFile> inputs,
		string mods )
	{
		if ( string.IsNullOrWhiteSpace( root ) || !Path.IsPathFullyQualified( root ) )
			return null;

		root = Path.TrimEndingDirectorySeparator( Path.GetFullPath( root ) );

		if ( Within( root, mods ) || Within( mods, root ) )
			throw new InvalidDataException( "Vanilla and mod script folders overlap." );

		var entries = Entries( root, inputs );

		return entries.Length == 0 ? null : new( root, Fingerprint( entries ), entries.Length );
	}

	// A failed cache save is recoverable: Matches can regenerate the file list.
	internal static AppliedBlobMerge Compact(
		AppliedBlobMerge applied )
	{
		if ( applied.Manifest.VanillaBaseline is not { } baseline )
			return applied;

		var entries = Entries( baseline.Root, applied.Manifest.Inputs );

		if ( entries.Length == 0 )
			return applied;

		if ( entries.Length != baseline.Count || Fingerprint( entries ) != baseline.Fingerprint )
			throw new InvalidDataException( "Vanilla baseline records changed before saving." );

		Save( baseline, entries, applied.Inventory?.Path );

		return applied with
		{
			Manifest = applied.Manifest with
			{
				Inputs = applied.Manifest.Inputs
			.Where( input => !Within( input.Path, baseline.Root ) || !input.Path.EndsWith( ".ws", StringComparison.OrdinalIgnoreCase )
				|| input.SuppressedPath is not null || input.ReadPath is not null ).ToArray()
			}
		};
	}

	private static string CachePath( string inventory, string fingerprint ) =>
		MergeBuilder.SafePath( Path.GetDirectoryName( Path.GetFullPath( inventory ) )!, "Data/Vanilla Baselines/" + fingerprint + ".json" );

	private static Entry[]? Read(
		VanillaBaseline baseline,
		string? inventory )
	{
		if ( inventory is null )
			return null;

		try
		{
			string path = CachePath( inventory, baseline.Fingerprint );

			if ( !File.Exists( path ) || new FileInfo( path ).Length > 16 * 1024 * 1024 )
				return null;

			var entries = JsonSerializer.Deserialize<Entry[]>( File.ReadAllBytes( path ) );

			return entries is not null && entries.Length == baseline.Count && Fingerprint( entries ) == baseline.Fingerprint ? entries : null;
		}
		catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException or JsonException )
		{
			return null;
		}
	}

	private static void Save(
		VanillaBaseline baseline,
		Entry[] entries,
		string? inventory )
	{
		if ( inventory is null || Read( baseline, inventory ) is not null )
			return;

		string? temporary = null;

		try
		{
			string path = CachePath( inventory, baseline.Fingerprint );
			Directory.CreateDirectory( Path.GetDirectoryName( path )! );
			temporary = path + "." + Guid.NewGuid().ToString( "N" ) + ".tmp";
			File.WriteAllBytes( temporary, JsonSerializer.SerializeToUtf8Bytes( entries ) );
			File.Move( temporary, path, overwrite: true );
		}
		catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException )
		{
		}
		finally
		{
			try
			{
				if ( temporary is not null && File.Exists( temporary ) )
					File.Delete( temporary );
			}
			catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException )
			{
			}
		}
	}

	// Rehash vanilla files to check the baseline. Rebuild a missing cached list only when the baseline still matches.
	internal static bool Matches(
		VanillaBaseline baseline,
		string? inventory )
	{
		try
		{
			var paths = Directory.GetFiles( baseline.Root, "*.ws", SearchOption.AllDirectories )
				.Select( path => new { Physical = path, Relative = Path.GetRelativePath( baseline.Root, path ).Replace( '\\', '/' ).ToUpperInvariant() } )
				.OrderBy( path => path.Relative, StringComparer.Ordinal ).ToArray();

			if ( paths.Length != baseline.Count )
				return false;

			if ( Read( baseline, inventory ) is { } saved )
				return saved.Zip( paths ).All( pair => pair.First.Path == pair.Second.Relative
					&& MergeBuilder.Hash( pair.Second.Physical ) == pair.First.Sha256 );

			var rebuilt = paths.Select( path => new Entry( path.Relative, MergeBuilder.Hash( path.Physical ) ) ).ToArray();

			if ( Fingerprint( rebuilt ) != baseline.Fingerprint )
				return false;

			Save( baseline, rebuilt, inventory );

			return true;
		}
		catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException )
		{
			return false;
		}
	}
}
