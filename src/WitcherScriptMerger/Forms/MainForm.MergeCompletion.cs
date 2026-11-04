using System;
using System.ComponentModel;

namespace WitcherScriptMerger.Forms;

internal partial class MainForm
{
	// Saves completed merges even when the run fails or is canceled. A save failure is included in the report.
	internal static void CompleteMergeOperation( RunWorkerCompletedEventArgs result, bool inventoryChanged,
		Action saveInventory, Action<bool> refreshTrees, Action finishProgress, Action<string> reportFailure )
	{
		string failure = result.Error != null
			? UiText.Format(
				"MainForm",
				"Merging stopped before all selected files were completed.\n\n{0}",
				result.Error.GetBaseException().Message )
			: result.Cancelled ? UiText.T( "MainForm", "Merging was canceled before all selected files were completed." ) : null;
		bool saveFailed = false;

		if ( inventoryChanged )
		{
			try
			{
				saveInventory();
			}
			catch ( Exception error )
			{
				saveFailed = true;

				string saveFailure = UiText.Format( "MainForm", "Could not save the merge records.\n\n{0}", error.GetBaseException().Message );

				failure = failure == null ? saveFailure : failure + "\n\n" + saveFailure;
			}
		}

		if ( failure != null )
		{
			finishProgress();
			reportFailure( failure );

			if ( inventoryChanged && !saveFailed )
				refreshTrees( true );
		}
		else if ( inventoryChanged )
		{
			refreshTrees( false );
		}
		else
		{
			finishProgress();
		}
	}
}
