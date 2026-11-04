#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WitcherScriptMerger.Merging;

namespace WitcherScriptMerger.Inventory;

internal sealed record UnregisteredMergeOutput( string Path, string ModRoot );

// Interrupted merges can leave output files with no inventory record. Locate them only in folders SMR writes to.
internal static class UnregisteredMergeOutputs
{
	// An unreadable inventory cannot establish which outputs are unregistered.
	internal static IReadOnlyList<UnregisteredMergeOutput> Find( string mods, string configuredName, MergeInventory inventory,
		IEnumerable<string>? managedOutputs = null )
	{
		if ( inventory.LoadError is not null )
			throw new InvalidDataException( inventory.LoadError );

		if ( !Directory.Exists( mods ) )
			return Array.Empty<UnregisteredMergeOutput>();

		var owned = new HashSet<string>( managedOutputs ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase );
		var names = new HashSet<string>( StringComparer.OrdinalIgnoreCase ) { configuredName, MergeBuilder.LooseName, MergeBuilder.CompiledName };

		foreach ( var merge in inventory.Merges )
		{
			names.Add( merge.MergedModName );

			string root = MergeBuilder.SafePath( mods, merge.MergedModName );

			if ( merge.IsBundleContent )
			{
				string bundle = MergeBuilder.SafePath( root, Path.Combine( "content", merge.BundleName ) );

				owned.Add( bundle );
				owned.Add( Path.Combine( Path.GetDirectoryName( bundle )!, "metadata.store" ) );
			}
			else
				owned.Add( MergeBuilder.SafePath( root, ModFilePath( merge ) ) );
		}

		foreach ( string directory in Directory.EnumerateDirectories( mods, "mod*", SearchOption.TopDirectoryOnly ) )
			if ( Path.GetFileName( directory ).EndsWith( "SMR_MergedCompiled", StringComparison.OrdinalIgnoreCase ) )
				names.Add( Path.GetFileName( directory ) );

		var result = new List<UnregisteredMergeOutput>();
		var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false };

		foreach ( string name in names.Where( name => !string.IsNullOrWhiteSpace( name ) && name.StartsWith( "mod", StringComparison.OrdinalIgnoreCase )
			&& Path.GetFileName( name ) == name && name.IndexOfAny( Path.GetInvalidFileNameChars() ) < 0 ) )
		{
			string root = MergeBuilder.SafePath( mods, name );

			if ( !Directory.Exists( root ) )
				continue;

			foreach ( string path in Directory.EnumerateFiles( root, "*", options ) )
				if ( IsOutput( path ) && !owned.Contains( path ) )
					result.Add( new( path, root ) );
		}

		return result.OrderBy( output => output.Path, StringComparer.OrdinalIgnoreCase ).ToArray();
	}

	private static string ModFilePath( Merge merge ) => merge.IsBlobMerge && merge.RelativePath.EndsWith( ".rsblob", StringComparison.OrdinalIgnoreCase )
		? Path.Combine( "content", merge.RelativePath )
		: merge.RelativePath.EndsWith( ".ws", StringComparison.OrdinalIgnoreCase ) ? Path.Combine( "content", "scripts", merge.RelativePath ) : merge.RelativePath;

	private static bool IsOutput( string path ) => Path.GetExtension( path ).ToLowerInvariant() is ".ws" or ".xml" or ".bundle" or ".rsblob"
		|| Path.GetFileName( path ).Equals( "metadata.store", StringComparison.OrdinalIgnoreCase );

	// Keep removed files under temporary names until metadata and disabled originals are updated, so failures can be rolled back.
	internal static bool Remove(
		string mods,
		IReadOnlyList<UnregisteredMergeOutput> outputs,
		Func<string, bool> rebuildMetadata,
		Func<bool> gameRunning,
		IReadOnlyList<string>? disabledOriginals = null )
	{
		disabledOriginals ??= Array.Empty<string>();

		if ( outputs.Count == 0 && disabledOriginals.Count == 0 )
			return true;

		if ( gameRunning() )
			throw new IOException( UiText.T( "BlobMerge", "Close The Witcher 3 before applying or restoring a merge." ) );

		var moved = new List<(string Path, string Temporary)>();
		var restored = new List<(string Original, string Disabled)>();
		var metadata = new Dictionary<string, byte[]?>( StringComparer.OrdinalIgnoreCase );
		var rebuilt = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		bool committed = false;

		try
		{
			foreach ( string directory in outputs.Where( output => Path.GetExtension( output.Path ).Equals( ".bundle", StringComparison.OrdinalIgnoreCase ) )
				.Select( output => Path.GetDirectoryName( output.Path )! ).Distinct( StringComparer.OrdinalIgnoreCase ) )
			{
				string path = MergeBuilder.SafePath( mods, Path.GetRelativePath( mods, Path.Combine( directory, "metadata.store" ) ) );

				metadata[path] = File.Exists( path ) ? File.ReadAllBytes( path ) : null;
			}

			foreach ( var output in outputs )
			{
				string path = MergeBuilder.SafePath( mods, Path.GetRelativePath( mods, output.Path ) );

				if ( !path.StartsWith( output.ModRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase ) )
					throw new InvalidDataException( "Merged output is outside its mod directory." );

				if ( !File.Exists( path ) )
					continue;

				string temporary = path + "." + Guid.NewGuid().ToString( "N" ) + ".tmp";

				File.Move( path, temporary );
				moved.Add( (path, temporary) );
			}

			foreach ( string path in metadata.Keys )
			{
				string directory = Path.GetDirectoryName( path )!;
				string[] remaining = Directory.GetFiles( directory, "*.bundle", SearchOption.TopDirectoryOnly );

				foreach ( string bundle in remaining )
					MergeBuilder.SafePath( mods, Path.GetRelativePath( mods, bundle ) );

				if ( remaining.Length > 0 )
				{
					rebuilt.Add( path );

					if ( !rebuildMetadata( directory ) )
						return false;
				}
			}

			foreach ( string disabled in disabledOriginals )
			{
				string original = Path.ChangeExtension( disabled, null );

				if ( File.Exists( original ) || Directory.Exists( original ) )
					throw new IOException( UiText.Format(
						"BlobMerge",
						"An active script already exists. The unclaimed disabled copy was kept: {0}",
						disabled ) );

				File.Move( disabled, original );
				restored.Add( (original, disabled) );
			}

			if ( gameRunning() )
				throw new IOException( UiText.T( "BlobMerge", "Close The Witcher 3 before applying or restoring a merge." ) );

			committed = true;
		}
		finally
		{
			if ( !committed )
			{
				foreach ( var file in restored.AsEnumerable().Reverse() )
					File.Move( file.Original, file.Disabled );

				foreach ( var file in moved.AsEnumerable().Reverse() )
					File.Move( file.Temporary, file.Path );

				foreach ( string path in rebuilt )
					if ( metadata[path] is { } bytes )
						File.WriteAllBytes( path, bytes );
					else if ( File.Exists( path ) )
						File.Delete( path );
			}
		}

		foreach ( var file in moved )
			File.Delete( file.Temporary );

		foreach ( var output in outputs )
		{
			string? directory = Path.GetDirectoryName( output.Path );

			while ( directory is not null && ( directory.Equals( output.ModRoot, StringComparison.OrdinalIgnoreCase )
				|| directory.StartsWith( output.ModRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase ) ) )
			{
				if ( !Directory.Exists( directory ) || Directory.EnumerateFileSystemEntries( directory ).Any() )
					break;

				Directory.Delete( directory );
				directory = Path.GetDirectoryName( directory );
			}
		}

		return true;
	}
}
