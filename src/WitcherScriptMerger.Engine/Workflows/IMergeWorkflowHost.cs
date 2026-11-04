#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace WitcherScriptMerger;

internal interface IMergeWorkflowHost
{
	IEngineSettings Settings { get; }

	void ShowMessage( string message );

	void ShowError( string message );

	bool ValidateScriptsDirectory();

	string? RetrieveMergedModName();

	bool ConfirmRemoveSelectedMerges();

	// This prompt can come from a worker thread; dispatch it to the UI thread.
	EditedMergeOutputChoice ConfirmEditedMergeOutputs( IReadOnlyList<string> paths, string backupDirectory );

	Action<string, string> CreateProgressReporter();

	// The host displays progress and reports failures before returning.
	Task<bool> RunBlobRemovalAsync( Action removal );

	void RepackBundle( string bundlePath );

	void RefreshTrees();
}
