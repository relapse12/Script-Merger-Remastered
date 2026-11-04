#nullable enable

namespace WitcherScriptMerger.Inventory;

// FileMerger calls these from its worker thread. UI implementations must dispatch dialogs to the UI thread.
internal interface IMergePrompts
{
	string? RetrieveMergedBundlePath();

	bool ConfirmOverwrite( string outputPath );

	bool ConfirmContinueAfterCanceledMerge( string message, bool hasRemainingMerges );

	void ShowUnpackError( string message );

	void ShowCleanupWarning( string message );

	void ShowMergeReport( int mergeNum, int mergeTotal, string file1, string file2, string outputFile, string modName1, string modName2 );

	void ShowPackReport( string bundlePath );
}
