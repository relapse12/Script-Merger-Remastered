#nullable enable annotations
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using WitcherScriptMerger.BlobMerging;
using WitcherScriptMerger.Inventory;

namespace WitcherScriptMerger.Forms;

// Merge checks run in the background. Their prompts and tree updates must return to the UI thread.
internal partial class MainForm : IMergeReconcileHost
{
	bool IMergeReconcileHost.AutoDeleteOldMerges => Program.Settings.Get<bool>( "AutoDeleteOldMerges" );

	string IMergeReconcileHost.MergedModName => Program.Settings.Get( "MergedModName" );

	int IMergeReconcileHost.GenerateMetadata( string bundleDirectory )
	{
		return Tools.WccLite.GenerateMetadata( bundleDirectory );
	}

	bool IMergeReconcileHost.ConfirmPruneMissingOutput( Merge merge )
	{
		return ConfirmPruneMissingMergeFile( merge );
	}

	bool IMergeReconcileHost.ConfirmDeleteForMissingSource( Merge merge, string modName )
	{
		return ConfirmDeleteMergeForMissingMod( merge, modName );
	}

	bool IMergeReconcileHost.ConfirmDeleteForDisabledSource( Merge merge, string modName )
	{
		return ConfirmDeleteMergeForDisabledMod( merge, modName );
	}

	bool IMergeReconcileHost.ConfirmDeleteForChangedSource( Merge merge, string modFilePath, string modName )
	{
		return Invoke( () => PromptToDeleteForChangedHash( merge, modFilePath, modName ) ) == DialogResult.Yes;
	}

	bool IMergeReconcileHost.ConfirmDeleteForRenamedSource( string oldName, string newName )
	{
		string message = UiText.Format(
			"MainForm",
			"The source folder was renamed from {0} to {1}. Delete its affected merge group?",
			oldName,
			newName );

		return ShowMessage(
			message,
			UiText.T( "MainForm", "Renamed Mod in Merge" ),
			MessageBoxButtons.YesNo,
			MessageBoxIcon.Question ) == DialogResult.Yes;
	}

	bool IMergeReconcileHost.ConfirmRemoveUnregistered(
		IReadOnlyList<UnregisteredMergeOutput> files,
		IReadOnlyList<string> disabledOriginals )
	{
		return ConfirmRemoveUnregisteredMerges( files, disabledOriginals );
	}

	bool IMergeReconcileHost.ConfirmRemoveOutdatedBlobMerges( IReadOnlyList<BlobMergeChange> changes )
	{
		string listing = string.Join( Environment.NewLine, changes.Take( 12 ).Select( BlobMergeLifecycle.Describe ) );

		if ( changes.Count > 12 )
			listing += Environment.NewLine + UiText.Format( "MainForm", "...and {0} more changes.", changes.Count - 12 );

		string message = UiText.Format(
			"MainForm",
			"These compiled script merges are out of date:\n\n{0}\n\nRemove everything affected? Any merges that are still valid will be rebuilt without it. If none are, the original files are restored. Choose No to keep the merges, marked as outdated.",
			listing );

		return ShowMessage(
			message,
			UiText.T( "MainForm", "Outdated Script Merges" ),
			MessageBoxButtons.YesNo,
			MessageBoxIcon.Question ) == DialogResult.Yes;
	}

	void IMergeReconcileHost.ShowError( string message )
	{
		Invoke( (MethodInvoker)( () => ShowError( message ) ) );
	}

	void IMergeReconcileHost.ShowUnregisteredCleanupError( string message )
	{
		ShowError( message, UiText.T( "UnregisteredMerges", "Merged file cleanup failed" ) );
	}

	Action? IMergeReconcileHost.PrepareBlobRemoval( IReadOnlyList<Merge> merges, BlobRemovalScope? scope )
	{
		return Invoke( () => mergeDeletions.PrepareBlobRemoval( merges, scope, confirm: false ) );
	}

	void IMergeReconcileHost.ReloadAfterBlobChange()
	{
		Invoke( (MethodInvoker)FinishManagedBlobRemoval );
	}

	void IMergeReconcileHost.KeepWindowOpen()
	{
		_manualRefreshRunning = true;
	}

	// The tree stays in BeginUpdate until RenderMergeTree finishes it.
	void IMergeReconcileHost.MergeChecksStarting()
	{
		Invoke( (MethodInvoker)delegate
		{
			mergeSearchTimer.Stop();
			txtMergeModSearch.Enabled = txtMergeFileSearch.Enabled = false;
			mergeSearchSource.Clear();
			treMerges.BeginUpdate();
			treMerges.Nodes.Clear();
		} );
	}

	void IMergeReconcileHost.MergeChecksRestarting()
	{
		Invoke( (MethodInvoker)( () => treMerges.EndUpdate() ) );
	}
}
