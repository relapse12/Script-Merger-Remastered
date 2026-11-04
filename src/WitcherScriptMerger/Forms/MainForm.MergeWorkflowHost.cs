#nullable enable annotations
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Forms;
using WitcherScriptMerger.Inventory;

namespace WitcherScriptMerger.Forms;

// Create and delete services call these UI operations on the window's thread.
internal partial class MainForm : IMergeWorkflowHost
{
	IEngineSettings IMergeWorkflowHost.Settings => Program.Settings;

	void IMergeWorkflowHost.ShowMessage( string message )
	{
		ShowMessage( message );
	}

	void IMergeWorkflowHost.ShowError( string message )
	{
		ShowError( message );
	}

	bool IMergeWorkflowHost.ValidateScriptsDirectory()
	{
		return Paths.ValidateScriptsDirectory();
	}

	string? IMergeWorkflowHost.RetrieveMergedModName()
	{
		return Paths.RetrieveMergedModName();
	}

	bool IMergeWorkflowHost.ConfirmRemoveSelectedMerges()
	{
		return ShowMessage(
			UiText.T( "BlobMerge", "Remove the selected merges and associated script files? Unrelated merges will be kept." ),
			UiText.T( "MainForm", "&Delete Selected Merges" ).Replace( "&", string.Empty, StringComparison.Ordinal ),
			MessageBoxButtons.YesNo,
			MessageBoxIcon.Question ) == DialogResult.Yes;
	}

	Action<string, string> IMergeWorkflowHost.CreateProgressReporter()
	{
		return CreateScriptProgressReporter();
	}

	EditedMergeOutputChoice IMergeWorkflowHost.ConfirmEditedMergeOutputs( IReadOnlyList<string> paths, string backupDirectory )
	{
		return Invoke( () =>
		{
			using var prompt = new EditedMergeOutputsForm( paths, backupDirectory );
			Program.ThemeMngr.ApplyThemeOnForm( prompt );

			return prompt.ShowDialog( this ) switch
			{
				DialogResult.Yes => EditedMergeOutputChoice.BackUpAndDelete,
				DialogResult.No => EditedMergeOutputChoice.Delete,
				_ => EditedMergeOutputChoice.Cancel
			};
		} );
	}

	Task<bool> IMergeWorkflowHost.RunBlobRemovalAsync( Action removal )
	{
		return RunManagedBlobChangeAsync( removal );
	}

	void IMergeWorkflowHost.RepackBundle( string bundlePath )
	{
		InitializeProgressScreen( UiText.T( "MainForm", "Repacking remaining bundled files" ) );

		new FileMerger(
			session.Inventory,
			session.GamePaths,
			Program.EngineHost,
			this,
			OnMergeProgressChanged,
			OnMergeComplete ).RepackBundleAsync( bundlePath );
	}

	void IMergeWorkflowHost.RefreshTrees()
	{
		RefreshTrees();
	}
}
