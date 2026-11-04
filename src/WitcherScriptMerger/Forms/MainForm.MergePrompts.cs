#nullable enable annotations
using System.Windows.Forms;
using WitcherScriptMerger.Inventory;

namespace WitcherScriptMerger.Forms;

// FileMerger prompts arrive on a background thread. ShowMessage and ShowModal dispatch to the UI thread.
internal partial class MainForm : IMergePrompts
{
	string? IMergePrompts.RetrieveMergedBundlePath()
	{
		return Paths.RetrieveMergedBundlePath( session.GamePaths );
	}

	bool IMergePrompts.ConfirmOverwrite( string outputPath )
	{
		string message = UiText.Format( "FileMerger", "The output file below already exists! Overwrite?\n\n{0}", outputPath );

		return ShowMessage(
			message,
			UiText.T( "FileMerger", "Overwrite?" ),
			MessageBoxButtons.YesNo,
			MessageBoxIcon.Exclamation ) == DialogResult.Yes;
	}

	bool IMergePrompts.ConfirmContinueAfterCanceledMerge( string message, bool hasRemainingMerges )
	{
		MessageBoxButtons buttons = hasRemainingMerges ? MessageBoxButtons.YesNo : MessageBoxButtons.OK;

		return ShowMessage( message, UiText.T( "FileMerger", "Skipped Merge" ), buttons, MessageBoxIcon.Asterisk ) != DialogResult.No;
	}

	void IMergePrompts.ShowUnpackError( string message )
	{
		ShowError( message );
	}

	void IMergePrompts.ShowCleanupWarning( string message )
	{
		ShowMessage( message, UiText.T( "FileMerger", "Error" ), MessageBoxButtons.OK, MessageBoxIcon.Exclamation );
	}

	void IMergePrompts.ShowMergeReport(
		int mergeNum,
		int mergeTotal,
		string file1,
		string file2,
		string outputFile,
		string modName1,
		string modName2 )
	{
		using MergeReportForm report = new MergeReportForm( mergeNum, mergeTotal, file1, file2, outputFile, modName1, modName2 );

		ShowModal( report );
	}

	void IMergePrompts.ShowPackReport( string bundlePath )
	{
		using PackReportForm report = new PackReportForm( bundlePath );

		ShowModal( report );
	}
}
