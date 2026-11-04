#nullable enable
using System;
using System.Collections.Generic;
using WitcherScriptMerger.BlobMerging;
using WitcherScriptMerger.Inventory;

namespace WitcherScriptMerger;

// Calls originate on a worker thread; UI implementations must dispatch prompts and control updates.
internal interface IMergeReconcileHost
{
	bool AutoDeleteOldMerges { get; }

	string MergedModName { get; }

	bool ConfirmPruneMissingOutput( Merge merge );

	bool ConfirmDeleteForMissingSource( Merge merge, string modName );

	bool ConfirmDeleteForDisabledSource( Merge merge, string modName );

	bool ConfirmDeleteForChangedSource( Merge merge, string modFilePath, string modName );

	bool ConfirmDeleteForRenamedSource( string oldName, string newName );

	bool ConfirmRemoveUnregistered( IReadOnlyList<UnregisteredMergeOutput> files, IReadOnlyList<string> disabledOriginals );

	bool ConfirmRemoveOutdatedBlobMerges( IReadOnlyList<BlobMergeChange> changes );

	void ShowError( string message );

	void ShowUnregisteredCleanupError( string message );

	Action? PrepareBlobRemoval( IReadOnlyList<Merge> merges, BlobRemovalScope? scope );

	// Return zero on success and report tool failures before returning.
	int GenerateMetadata( string bundleDirectory );

	void ReloadAfterBlobChange();

	void KeepWindowOpen();

	void MergeChecksStarting();

	void MergeChecksRestarting();
}
