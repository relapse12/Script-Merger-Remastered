#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;


namespace WitcherScriptMerger.Merging;

public static partial class BlobMergeTransaction
{
	// An edited generated file may contain work worth keeping. Preserve its entire workspace.
	private static void CleanupWorkspace(
		AppliedBlobMerge applied,
		string state )
	{
		string root = WorkspacePath( state, applied.Manifest.Id );

		try
		{
			if ( ( applied.Workspace ?? [] ).Any( file => File.Exists( MergeBuilder.SafePath( root, file.Path ) )
				&& !Matches( MergeBuilder.SafePath( root, file.Path ), file.Sha256 ) ) )
				return;
		}
		catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException )
		{
			return;
		}

		CleanupRecorded( root, ( applied.Workspace ?? [] ).Select( file => (file.Path, file.Sha256) ) );
	}

	// Keep backup copies still needed by the surviving merge.
	private static void CleanupAdoptedOutputs(
		AppliedBlobMerge applied,
		string state,
		IReadOnlyList<MergeRestoredOutput> keep )
	{
		foreach ( var output in ( applied.RestoredOutputs ?? [] ).Where( output => !keep.Any( next => next.StoragePath == output.StoragePath ) ) )
			CleanupRecorded( StorageDirectory( state ), [(output.StoragePath, output.Sha256)], removeRoot: false );
	}

	private static void CleanupUpdateBackups(
		string state,
		string id,
		IReadOnlyList<UpdateFile> files )
		=> CleanupRecorded( MergeBuilder.SafePath( StorageDirectory( state ), "update-" + id ),
			files.Where( file => file.Before is not null ).Select( file => (file.Backup, file.Before!) ) );

	// Delete only recorded files whose hashes still match. A cleanup failure must leave the completed merge usable.
	private static void CleanupRecorded(
		string root,
		IEnumerable<(string Path, string Hash)> files,
		bool removeRoot = true )
	{
		foreach ( var file in files )
		{
			try
			{
				string path = MergeBuilder.SafePath( root, file.Path );

				if ( !Matches( path, file.Hash ) )
					continue;

				File.Delete( path );
				string? directory = Path.GetDirectoryName( path );

				while ( directory is not null && !directory.Equals( root, StringComparison.OrdinalIgnoreCase )
					&& directory.StartsWith( Path.TrimEndingDirectorySeparator( root ) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase )
					&& Directory.Exists( directory ) && !Directory.EnumerateFileSystemEntries( directory ).Any() )
				{
					Directory.Delete( directory );
					directory = Path.GetDirectoryName( directory );
				}
			}
			catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException )
			{
			}
		}

		try
		{
			if ( removeRoot && Directory.Exists( root ) && !Directory.EnumerateFileSystemEntries( root ).Any() )
				Directory.Delete( root );
		}
		catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException )
		{
		}
	}
}
