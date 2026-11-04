using System.Linq;
using System.Windows.Forms;

namespace WitcherScriptMerger.Forms;

internal partial class MainForm
{
	protected override bool ProcessCmdKey( ref Message message, Keys keys )
	{
		var input = new KeyEventArgs( keys );

		HandleConfiguredShortcut( input );

		return input.Handled || base.ProcessCmdKey( ref message, keys );
	}

	internal string ShortcutHint( string id )
	{
		var operation = KeyboardShortcuts.Operations.Single( operation => operation.Id == id );
		Keys keys = KeyboardShortcuts.Read( Program.Settings, operation );

		return keys == Keys.None ? KeyboardShortcuts.Caption( operation )
			: UiText.Format( "KeyboardShortcuts", "{0} ({1})", KeyboardShortcuts.Caption( operation ), KeyboardShortcuts.Display( keys ) );
	}

	internal void RefreshShortcutHints()
	{
		if ( MainMenu != null )
			KeyboardShortcuts.UpdateMenu( MainMenu.Items, Program.Settings );

		foreach ( var (button, id) in new[] { (btnCreateMerges, "CreateSelected"), (btnCreateAllScriptMerges, "CreateScripts"),
			(btnDeleteMerges, "DeleteSelected"), (btnDeleteAllMerges, "DeleteAll"), (btnRefreshConflicts, "RefreshConflicts"), (btnRefreshMerged, "RefreshMerges") } )
			LiveText.BindToolTip( toolTip1, button, () => ShortcutHint( id ) );

		SetMergeButtonTextIfValidSelection();
	}

	// Do not let a shortcut start work behind a progress screen or modal dialog.
	internal void HandleConfiguredShortcut( KeyEventArgs e )
	{
		if ( pnlProgress == null || pnlProgress.Visible || OwnedForms.Any( form => form.Visible && form.Modal ) )
			return;

		bool mergeFocus = treMerges.ContainsFocus || tlpMergesHeader.ContainsFocus || tlpMergeActions.ContainsFocus;
		bool editing = txtConflictModSearch.Focused || txtConflictFileSearch.Focused || txtMergeModSearch.Focused || txtMergeFileSearch.Focused;

		if ( e.KeyData == Keys.Escape )
		{
			if ( mergeFocus && ( IsMergeSearchActive || editing ) )
			{
				ClearMergeSearch();
				treMerges.Focus();
			}
			else if ( IsConflictSearchActive || editing )
			{
				ClearConflictSearch();
				treConflicts.Focus();
			}
			else
				goto dispatch;

			e.Handled = e.SuppressKeyPress = true;

			return;
		}

	dispatch:
		string id = KeyboardShortcuts.Match( Program.Settings, e.KeyData );

		if ( id == null || editing && id is not ( "SearchMods" or "SearchFiles" or "RefreshConflicts" or "RefreshMerges" ) )
			return;

		e.Handled = e.SuppressKeyPress = true;

		switch ( id )
		{
			case "SearchMods":
			case "SearchFiles":
				var box = mergeFocus ? ( id == "SearchFiles" ? txtMergeFileSearch : txtMergeModSearch )
					: ( id == "SearchFiles" ? txtConflictFileSearch : txtConflictModSearch );
				box.Focus();
				box.SelectAll();
				break;

			case "RefreshConflicts":
				btnRefreshConflicts.PerformClick();
				break;

			case "RefreshMerges":
				btnRefreshMerged.PerformClick();
				break;

			case "CreateSelected":
				btnCreateMerges.PerformClick();
				break;

			case "CreateScripts":
				btnCreateAllScriptMerges.PerformClick();
				break;

			case "DeleteSelected":
				btnDeleteMerges.PerformClick();
				break;

			case "DeleteAll":
				btnDeleteAllMerges.PerformClick();
				break;

			case "Diagnostics":
				if ( btnScanDiagnostics.Visible && btnScanDiagnostics.Enabled )
					btnScanDiagnostics.PerformClick();
				break;

			default:
				string menu = KeyboardShortcuts.Operations.Single( operation => operation.Id == id ).Menu;
				if ( menu is not null && MainMenu != null )
				{
					MnOpen_DropDownOpening( null, System.EventArgs.Empty );
					MainMenu_Opening( null, new System.ComponentModel.CancelEventArgs() );

					var item = MainMenu.Items.Find( menu, searchAllChildren: true ).OfType<ToolStripMenuItem>().FirstOrDefault();

					if ( item?.Enabled == true && item.Available )
						item.PerformClick();
				}
				break;
		}
	}
}
