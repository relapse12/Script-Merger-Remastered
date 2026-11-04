#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using WitcherScriptMerger.Merging;

namespace WitcherScriptMerger.BlobMerging;

// Blob state lives in MergeInventory.xml. Recovery journals sit beside it; working files and caches live under Data.
internal sealed class BlobMergeStore
{
	// Journal filenames use this exact path, so keep the caller's spelling.
	internal BlobMergeStore( string inventoryPath )
	{
		InventoryPath = inventoryPath;
	}

	internal string InventoryPath { get; }

	internal bool HasUpdateJournal => File.Exists( InventoryPath + ".update" );

	internal bool HasPendingJournal => File.Exists( InventoryPath + ".pending" );

	internal bool HasInterruptedUpdate => HasPendingJournal || HasUpdateJournal;

	internal static string ApplicationStorage => Path.Combine( AppContext.BaseDirectory, "Data", "Blob Merges" );

	// The inventory follows the working directory; Data follows the executable's directory.
	internal static BlobMergeStore ForApplication()
	{
		return new BlobMergeStore( Path.GetFullPath( GamePaths.InventoryFile ) );
	}

	// Finish cleanup if the update already committed; otherwise use the journals to undo the interrupted changes.
	internal void RecoverInterrupted( string? settingsPath, string modsDirectory )
	{
		BlobMergeTransaction.RecoverInterrupted( InventoryPath, settingsPath, modsDirectory, BlobMergeInventory.Reconcile, InventoryPath );
	}

	internal static string NewMergeWorkDirectory()
	{
		return Path.Combine( ApplicationStorage, "work", Guid.NewGuid().ToString( "N" ) );
	}

	internal string NewRemovalWorkDirectory()
	{
		return Path.Combine( Path.GetDirectoryName( InventoryPath )!, "work", Guid.NewGuid().ToString( "N" ) );
	}


	// These outputs belonged to ordinary merges before the blob merge took them over and must be restored on removal.
	internal static IEnumerable<string> RestoredOutputPaths( XmlElement? state )
	{
		return state?["RestoredOutputs"]?.ChildNodes.OfType<XmlElement>()
			.Select( node => node["Path"]?.InnerText.Replace( '\\', '/' ) )
			.OfType<string>() ?? [];
	}

	internal static string? ManifestId( XmlElement state )
	{
		return state["Manifest"]?["Id"]?.InnerText;
	}

	internal static string? PreviousInventory( XmlElement state )
	{
		return state["Inventory"]?["Previous"]?.InnerText;
	}
}
