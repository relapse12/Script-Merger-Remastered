#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;


namespace WitcherScriptMerger.Merging;

public sealed class AmbiguousMergeSourceException( string mod ) : IOException( "More than one folder matches the moved merge source: " + mod )
{
	public string Mod { get; } = mod;
}

// Find renamed source folders so restoring a merge reaches the files in their current location.
public static class MergeSourceLocations
{
	// Prefer a match for every saved script hash. Only accept a name-only match when one prefixed folder is possible.
	public static IReadOnlyDictionary<string, string> Find(
		BlobMergeManifest manifest )
	{
		var result = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
		string[]? folders = null;

		foreach ( string mod in manifest.Mods )
		{
			string root = MergeBuilder.SafePath( manifest.ModsRoot, mod );

			if ( Directory.Exists( root ) )
				continue;

			folders ??= Directory.GetDirectories( manifest.ModsRoot )
				.Where( folder => ( File.GetAttributes( folder ) & FileAttributes.ReparsePoint ) == 0 ).ToArray();
			var prefixed = folders.Where( folder => !Path.GetFileName( folder ).StartsWith( "mod", StringComparison.OrdinalIgnoreCase )
				&& Path.GetFileName( folder ).EndsWith( mod, StringComparison.OrdinalIgnoreCase ) ).ToArray();
			var inputs = manifest.Inputs.Where( input => input.Path.StartsWith( root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase )
				&& IsScript( input.Path ) ).ToArray();
			bool Matches( string folder ) => inputs.Length > 0 && inputs.All( input =>
{
	string path = MergeBuilder.SafePath( folder, Path.GetRelativePath( root, input.Path ) );
	string read = input.SuppressedPath is not null && File.Exists( path + ".smr-disabled" ) ? path + ".smr-disabled" : path;

	return File.Exists( read ) && MergeBuilder.Hash( read ) == input.Sha256;
} );
			var matching = folders.Where( Matches ).ToArray();

			if ( matching.Length > 1 || prefixed.Length > 1 && matching.Length == 0 )
				throw new AmbiguousMergeSourceException( mod );

			string? current = matching.SingleOrDefault() ?? prefixed.SingleOrDefault();

			if ( current is not null )
				result.Add( mod, Path.GetFileName( current ) );
		}

		return result;
	}

	public static string PathFor(
		string modsRoot,
		string path,
		IReadOnlyDictionary<string, string> folders )
	{
		string relative = Path.GetRelativePath( modsRoot, path );
		string mod = relative.Split( Path.DirectorySeparatorChar )[0];

		return folders.TryGetValue( mod, out string? folder )
			? MergeBuilder.SafePath( modsRoot, folder + relative[mod.Length..] ) : path;
	}

	// Validate journal folder names before they can redirect recovery.
	public static void Validate(
		BlobMergeManifest manifest,
		IReadOnlyDictionary<string, string> folders )
	{
		if ( folders.Any( pair => !manifest.Mods.Contains( pair.Key, StringComparer.OrdinalIgnoreCase )
			|| string.IsNullOrEmpty( pair.Value ) || Path.GetFileName( pair.Value ) != pair.Value
			|| pair.Value is "." or ".." ) )
			throw new InvalidDataException( "Invalid moved source folders." );

		foreach ( string folder in folders.Values )
			_ = MergeBuilder.SafePath( manifest.ModsRoot, folder );
	}

	private static bool IsScript( string path ) => path.EndsWith( ".ws", StringComparison.OrdinalIgnoreCase )
		|| path.EndsWith( ".rsblob", StringComparison.OrdinalIgnoreCase );
}
