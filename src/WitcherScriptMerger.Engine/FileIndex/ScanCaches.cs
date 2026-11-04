using System.IO;
using System.Linq;

namespace WitcherScriptMerger.FileIndex;

internal static class ScanCaches
{
	internal static readonly string[] FolderNames = ["Bundle Index Cache", "RsBlob Symbol Cache", "Blob Merges/Cache", "Vanilla Baselines"];

	// Inspect all cache folders before deleting anything. A linked folder must stop the operation.
	internal static int Clear( string applicationDirectory )
	{
		BlobMerging.BlobMergeArtifacts.ClearMemory();

		string data = Path.Combine( Path.GetFullPath( applicationDirectory ), "Data" );

		if ( !Directory.Exists( data ) )
			return 0;

		if ( ( File.GetAttributes( data ) & FileAttributes.ReparsePoint ) != 0 )
			throw new IOException( UiText.T( "ScanCaches", "A cache folder is a linked directory. No caches were removed." ) );

		var directories = FolderNames.Select( name => Path.Combine( data, name ) ).Where( Directory.Exists ).ToArray();
		int count = 0;

		foreach ( string directory in directories )
			Inspect( directory );

		foreach ( string directory in directories )
			Directory.Delete( directory, recursive: true );

		return count;

		void Inspect( string directory )
		{
			if ( ( File.GetAttributes( directory ) & FileAttributes.ReparsePoint ) != 0 )
				throw new IOException( UiText.T( "ScanCaches", "A cache folder is a linked directory. No caches were removed." ) );

			foreach ( string entry in Directory.EnumerateFileSystemEntries( directory ) )
			{
				var attributes = File.GetAttributes( entry );

				if ( ( attributes & FileAttributes.ReparsePoint ) != 0 )
					throw new IOException( UiText.T( "ScanCaches", "A cache folder is a linked directory. No caches were removed." ) );

				if ( ( attributes & FileAttributes.Directory ) != 0 )
					Inspect( entry );
				else
					count++;
			}
		}
	}
}
