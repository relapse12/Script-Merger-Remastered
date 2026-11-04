using System;
using System.Linq;
using System.Windows.Forms;
using WitcherScriptMerger.FileIndex;

namespace WitcherScriptMerger.Forms;

// Applies language changes to the open workspace without rescanning.
internal partial class MainForm
{
	internal void RefreshLocalizedWorkspace()
	{
		if ( IsDisposed )
			return;

		ResetConflictToolTip();
		RefreshConflictToolTipText();

		foreach ( TreeNode node in treConflicts.CategoryNodes.Concat( treMerges.CategoryNodes )
			.Concat( treConflictSearchResults.Nodes.Cast<TreeNode>() ).Concat( mergeSearchSource.Select( group => group.Category ) ).Distinct() )
		{
			if ( node.Tag is not ModFileCategory category )
				continue;

			node.Text = category.DisplayName;
			node.ToolTipText = category.ToolTipText;
		}

		lblConflicts.Text = IsConflictSearchActive
			? IsFileSearchActive ? UiText.T( "MainForm", "File Search Results" ) : UiText.T( "MainForm", "Mod Search Results" )
			: UiText.T( "MainForm", "Conflicts" );

		if ( conflictRefreshPending )
			lblConflicts.Text += UiText.T( "MainForm", " (Refresh Pending)" );

		SetMergeButtonTextIfValidSelection();
		SetUnmergeButtonTextIfValidSelection();
		UpdateStatusText();

		if ( session.Scan == null )
			scanSummary?.SetSummary( new[] { UiText.T( "ScanSummaryBar", "Not scanned" ) }, Array.Empty<string>(), "" );

		foreach ( var (button, width) in new[] { (btnMainMenu, 78), (btnRefreshConflicts, 88), (btnRefreshMerged, 88) } )
		{
			button.MinimumSize = System.Drawing.Size.Empty;
			ConfigureHeaderButton( button, LogicalToDeviceUnits( width ), LogicalToDeviceUnits( 36 ) );
		}

		responsiveLayoutPending = true;
		FlushResponsiveWorkspaceLayout();
		Invalidate( true );
	}
}
