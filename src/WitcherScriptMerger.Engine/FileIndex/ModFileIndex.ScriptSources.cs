using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WitcherScriptMerger.FileIndex;

internal partial class ModFileIndex
{
	// The game ignores loose scripts beside a content-level blob. Exclude them from the active inputs.
	internal static (IReadOnlyList<string> Scripts, IReadOnlyList<string> Blobs) SelectLooseScriptInputs(
		IEnumerable<string> allFiles )
	{
		List<string> scripts = new();
		List<string> blobs = new();

		foreach ( string path in allFiles )
		{
			if ( ModFile.IsScript( path ) )
			{
				scripts.Add( path );
			}
			else if ( ModFile.IsRsBlob( path ) )
			{
				blobs.Add( path );
			}
		}

		HashSet<string> precompiledContentDirectories = blobs
			.Where( IsContentPrecompiledBlob )
			.Select( path => Path.GetDirectoryName( Path.GetFullPath( path ) ) )
			.ToHashSet( StringComparer.OrdinalIgnoreCase );

		return (scripts.Where( path => !precompiledContentDirectories.Contains( GetScriptContentDirectory( path ) ) ).ToArray(), blobs);
	}

	internal static bool IsContentPrecompiledBlob( string path ) =>
		string.Equals( Path.GetFileName( path ), "precompiled.rsblob", StringComparison.OrdinalIgnoreCase )
		&& string.Equals(
			Path.GetFileName( Path.GetDirectoryName( Path.GetFullPath( path ) ) ),
			"content",
			StringComparison.OrdinalIgnoreCase );

	private static string GetScriptContentDirectory( string scriptPath )
	{
		string nearestContent = null;

		for ( string directory = Path.GetDirectoryName( Path.GetFullPath( scriptPath ) ); directory != null;
			directory = Path.GetDirectoryName( directory ) )
		{
			string parent = Path.GetDirectoryName( directory );

			if ( string.Equals( Path.GetFileName( directory ), "scripts", StringComparison.OrdinalIgnoreCase )
				&& string.Equals( Path.GetFileName( parent ), "content", StringComparison.OrdinalIgnoreCase ) )
			{
				return parent;
			}

			if ( string.Equals( Path.GetFileName( directory ), "content", StringComparison.OrdinalIgnoreCase ) )
			{
				nearestContent ??= directory;
			}
		}

		return nearestContent;
	}
}
