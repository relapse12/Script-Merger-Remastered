#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using WSCompiler.Core.Scripts.Standalone;
using WitcherScriptMerger.Merging;

namespace WitcherScriptMerger.BlobMerging;

// Gives KDiff3 a script path and symbol name for each conflict.
internal sealed class BlobMergeComparisonContext
{
	private readonly Dictionary<string, string> paths = new( StringComparer.Ordinal );

	internal BlobMergeComparisonContext( IEnumerable<SourceFile> sources )
	{
		// Read originals first so a replacement method is labeled with the vanilla file it changes.
		foreach ( var file in sources )
			foreach ( var item in file.Items )
			{
				paths.TryAdd( MergePlanner.Key( item ), file.Path );

				if ( item is ClassItem type )
				{
					string owner = type.Decl.StateMachineOf is { Length: > 0 } machine
						? machine + "State" + type.Decl.Name : type.Decl.Name;

					foreach ( var method in type.Decl.Methods )
						paths.TryAdd( "function:" + owner + "." + method.Name, file.Path );
				}
			}
	}

	// Annotation keys refer to the target function; file keys already contain the path.
	internal string Describe( string key )
	{
		if ( key.StartsWith( "file:", StringComparison.Ordinal ) )
			return key[5..].Replace( '/', '\\' );

		string[] parts = key.Split( '/' );
		string head = parts[0];
		int colon = head.IndexOf( ':' );
		string symbol = head[( colon + 1 )..];
		string lookup = colon >= 0 && head[..colon] is "wrap" or "replace" or "add" ? "function:" + symbol : head;

		if ( parts.Length > 1 && parts[1].IndexOf( ':' ) is int memberColon && memberColon >= 0 )
			symbol += "." + parts[1][( memberColon + 1 )..];

		paths.TryGetValue( lookup, out string? path );

		return path is null ? symbol : path.Replace( '/', '\\' ) + " — " + symbol;
	}

	internal string Label(
		string name,
		string key ) => name + " — " + Describe( key ).Replace( '"', '\'' ).Replace( '\r', ' ' ).Replace( '\n', ' ' );
}

// KDiff3 has a path-length limit. Use short filenames and fall back to the system temp folder
// when the workspace path is too long. Dispose removes these comparison files.
internal sealed class BlobMergeComparisonFiles : IDisposable
{
	internal const int MaximumPathLength = 240;
	internal string DirectoryPath { get; }
	internal string First => Path.Combine( DirectoryPath, "a.ws" );
	internal string Second => Path.Combine( DirectoryPath, "b.ws" );
	internal string Original => Path.Combine( DirectoryPath, "v.ws" );
	internal string Output => Path.Combine( DirectoryPath, "o.ws" );

	internal BlobMergeComparisonFiles( string work, int number, string? temporaryRoot = null )
	{
		string directory = Path.GetFullPath( Path.Combine( work, "d", number.ToString( "D6", CultureInfo.InvariantCulture ) ) );

		if ( Path.Combine( directory, "o.ws" ).Length >= MaximumPathLength )
			directory = Path.GetFullPath( Path.Combine( temporaryRoot ?? Path.GetTempPath(), "smr-diff-" + Guid.NewGuid().ToString( "N" )[..12] ) );

		if ( Path.Combine( directory, "o.ws" ).Length >= MaximumPathLength )
			throw new IOException( UiText.T( "BlobMerge", "The temporary folder path is too long for KDiff3. Choose a shorter Windows temporary folder." ) );

		if ( Directory.Exists( directory ) )
			throw new IOException( "The temporary comparison directory already exists." );

		Directory.CreateDirectory( directory );
		DirectoryPath = directory;
	}

	public void Dispose()
	{
		try
		{
			Directory.Delete(
				MergeBuilder.SafePath( Path.GetDirectoryName( DirectoryPath )!, Path.GetFileName( DirectoryPath ) ),
				recursive: true );
		}
		catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException ) { }
	}
}
