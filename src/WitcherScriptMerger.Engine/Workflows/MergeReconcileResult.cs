#nullable enable
using System.Collections.Generic;
using WitcherScriptMerger.Inventory;

namespace WitcherScriptMerger;

// Linked outputs must be removed together. Pruned bundle merges require repacking.
internal sealed record MergeReconcileResult(
	IReadOnlyList<Merge> KeptMerges,
	IReadOnlyList<IReadOnlyList<Merge>> LinkedOutputs,
	List<Merge> MergesToDelete,
	List<Merge> PrunedBundleMerges,
	bool InventoryChanged );
