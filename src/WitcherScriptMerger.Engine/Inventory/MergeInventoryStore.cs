#nullable enable
using System;
using System.IO;
using WitcherScriptMerger.Tools;
using WitcherScriptMerger.Merging;

namespace WitcherScriptMerger.Inventory;

// Older inventories lack source hashes. LoadAndBackfillHashes adds and saves them; Read leaves the file unchanged.
internal sealed class MergeInventoryStore
{
	private readonly Func<string> filePath;
	private readonly GamePaths game;

	internal MergeInventoryStore( string filePath, GamePaths game )
		: this( () => filePath, game )
	{
	}

	private MergeInventoryStore( Func<string> filePath, GamePaths game )
	{
		this.filePath = filePath;
		this.game = game;
	}

	internal string FilePath => filePath();

	// The inventory path follows the working directory on each operation.
	internal static MergeInventoryStore ForApplication( GamePaths game )
	{
		return new MergeInventoryStore( () => Path.GetFullPath( GamePaths.InventoryFile ), game );
	}

	// Preserve read errors so callers can refuse to overwrite an unreadable inventory.
	internal MergeInventory Read()
	{
		string path = FilePath;
		MergeInventory inventory;

		try
		{
			inventory = MergeInventory.Deserialize( File.ReadAllBytes( path ) );
		}
		catch ( Exception error )
		{
			inventory = new MergeInventory { LoadError = File.Exists( path ) ? error.Message : null };
		}

		inventory.ClearChangeTracking();

		return inventory;
	}

	internal MergeInventory LoadAndBackfillHashes()
	{
		MergeInventory inventory = Read();

		if ( inventory.LoadError is not null )
			return inventory;

		try
		{
			if ( BackfillMissingHashes( inventory ) )
				ForApplication( game ).Save( inventory );
		}
		catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException )
		{
			// Keep the loaded merge records even if saving the added hashes fails.
		}

		return inventory;
	}

	// Write through a temporary file to preserve the previous inventory if saving is interrupted.
	internal void Save( MergeInventory inventory )
	{
		string path = FilePath;
		string? directory = Path.GetDirectoryName( path );
		string temporaryPath = path + ".tmp";
		string backupPath = path + ".bak";
		byte[] content;
		string? vanilla;

		if ( !string.IsNullOrWhiteSpace( directory ) )
			Directory.CreateDirectory( directory );

		content = inventory.Serialize();
		vanilla = game.ScriptsDirectoryIfSelected;

		// Store the vanilla file list separately under Data; merge records keep the hash identifying that baseline.
		if ( inventory.BlobMergeState is not null && !string.IsNullOrWhiteSpace( vanilla ) )
			content = BlobMergeTransaction.CompactVanillaBaseline( content, vanilla, path );

		using ( var stream = new FileStream( temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None ) )
		{
			stream.Write( content );
			stream.Flush( flushToDisk: true );
		}

		if ( File.Exists( path ) )
		{
			File.Replace( temporaryPath, path, backupPath, ignoreMetadataErrors: true );

			if ( File.Exists( backupPath ) )
				File.Delete( backupPath );
		}
		else
		{
			File.Move( temporaryPath, path );
		}

		// Use the saved, compacted state in memory too.
		if ( inventory.BlobMergeState is not null )
			inventory.BlobMergeState = MergeInventory.Deserialize( content ).BlobMergeState;
	}

	private bool BackfillMissingHashes( MergeInventory inventory )
	{
		bool added = false;

		foreach ( Merge merge in inventory.Merges )
		{
			foreach ( FileHash source in merge.Mods )
			{
				string sourcePath;

				if ( source.Hash is not null )
					continue;

				sourcePath = source.SourceFilePath ?? merge.GetModFile( source.Name, game );

				if ( !File.Exists( sourcePath ) )
					continue;

				source.Hash = Hasher.ComputeHash( sourcePath );
				added = true;
			}
		}

		return added;
	}
}
