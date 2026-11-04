using System.Threading.Tasks;
using System.Windows.Forms;
using WitcherScriptMerger.FileIndex;

namespace WitcherScriptMerger.Forms;

// Clearing caches makes the displayed scan unusable for merging until it is refreshed.
internal partial class MainForm
{
	internal async Task ClearScanCachesAsync( string applicationDirectory )
	{
		// Clear the checks before marking the scan stale, while the normal selection handling still applies.
		treConflicts.ClearMergeSelection();
		session.InvalidateScan();
		treConflicts.RefreshMergeCheckboxes();
		session.Scan?.ForgetMergeChecks();

		ApplyLinkedMergeGroups();
		RefreshConflictSearchResultsIfActive();
		RefreshSearchGroupCheckboxes();

		SetConflictRefreshPending( true );
		SetMergeButtonTextIfValidSelection();
		UpdateStatusText();

		await Task.Run( () => ScanCaches.Clear( applicationDirectory ) );
	}

	private bool RequireCurrentConflictScan()
	{
		if ( session.IsScanCurrent )
			return true;

		ShowMessage( UiText.T( "MainForm", "Refresh conflicts after clearing caches before creating merges." ),
			UiText.T( "MainForm", "Refresh required" ), MessageBoxButtons.OK, MessageBoxIcon.Information );

		return false;
	}
}
