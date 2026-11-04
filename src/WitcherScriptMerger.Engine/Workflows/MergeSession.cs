#nullable enable
using WitcherScriptMerger.BlobMerging;
using WitcherScriptMerger.FileIndex;
using WitcherScriptMerger.Inventory;
using WitcherScriptMerger.LoadOrder;

namespace WitcherScriptMerger;

// Holds the main window's current inventory, load order and scan state.
internal sealed class MergeSession
{
	internal MergeSession( GamePaths gamePaths, MergeInventoryStore inventoryStore )
	{
		GamePaths = gamePaths;
		InventoryStore = inventoryStore;
	}

	internal static MergeSession ForGame( GamePaths gamePaths )
	{
		return new MergeSession( gamePaths, MergeInventoryStore.ForApplication( gamePaths ) );
	}

	internal GamePaths GamePaths { get; }

	internal MergeInventoryStore InventoryStore { get; }

	// Reloading replaces Merge objects too; references kept across a reload become stale.
	internal MergeInventory? Inventory { get; set; }

	// Consumers read this through a callback because refresh replaces the load-order instance.
	internal CustomLoadOrder? LoadOrder { get; set; }

	internal ModFileIndex? Scan { get; set; }

	// Clearing scan caches invalidates existing selections until a fresh scan succeeds.
	internal bool IsScanCurrent { get; private set; } = true;

	internal bool HasManagedBlobState()
	{
		if ( string.IsNullOrWhiteSpace( GamePaths.ModsDirectory ) )
			return false;

		return Inventory?.BlobMergeState is not null || BlobMergeStore.ForApplication().HasInterruptedUpdate;
	}

	internal MergeInventory LoadInventory()
	{
		Inventory = InventoryStore.LoadAndBackfillHashes();

		return Inventory;
	}

	internal void SaveInventory()
	{
		InventoryStore.Save( Inventory! );
	}

	// Hide stale checkboxes before dropping their prepared merge checks.
	internal void InvalidateScan()
	{
		IsScanCurrent = false;
	}

	internal void MarkScanCurrent()
	{
		IsScanCurrent = true;
	}
}
