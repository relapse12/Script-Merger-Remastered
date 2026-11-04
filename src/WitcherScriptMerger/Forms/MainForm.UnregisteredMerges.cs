#nullable enable annotations
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using WitcherScriptMerger.Inventory;

namespace WitcherScriptMerger.Forms;

// Ask the user how to handle merged files that have no inventory record.
internal partial class MainForm
{
	internal bool CanAutoExitAfterScan => !_manualRefreshRunning && Program.Settings.Get<bool>( "AutoExit" )
		&& !reconciler.KeptOutdatedMerges && !reconciler.HasRetainedUnownedFiles;

	internal bool GetIsInventoryInOrder( Func<bool>? confirmRemoval = null, MergeInventory? inventory = null )
	{
		return reconciler.CheckUnregisteredOutputs( confirmRemoval, inventory );
	}

	private static string ListModFiles( IReadOnlyList<string> paths )
	{
		string listing = string.Join(
			Environment.NewLine,
			paths.Take( 12 ).Select( path => Path.GetRelativePath( Paths.ModsDirectory, path ) ) );

		if ( paths.Count > 12 )
			listing += Environment.NewLine + UiText.Format( "UnregisteredMerges", "...and {0} more files.", paths.Count - 12 );

		return listing;
	}

	private bool ConfirmRemoveUnregisteredMerges(
		IReadOnlyList<UnregisteredMergeOutput> files,
		IReadOnlyList<string> disabledOriginals )
	{
		string merged = ListModFiles( files.Select( file => file.Path ).ToArray() );
		string originals = ListModFiles( disabledOriginals );
		string message;

		if ( disabledOriginals.Count == 0 )
		{
			message = UiText.Format(
				"UnregisteredMerges",
				"These merged files are not listed in the current MergeInventory.xml:\n\n{0}\n\nDelete these unregistered files? Files still listed in the inventory will be kept. Choose No to keep these files. Remove them or restore their inventory before creating new merges.",
				merged );
		}
		else if ( files.Count == 0 )
		{
			message = UiText.Format(
				"UnregisteredMerges",
				"These original mod files are disabled, and no merge in the current MergeInventory.xml owns them:\n\n{0}\n\nRestore these files? Choose No to keep them disabled. Restore them or their inventory before creating new merges.",
				originals );
		}
		else
		{
			message = UiText.Format(
				"UnregisteredMerges",
				"These merged files are not listed in the current MergeInventory.xml:\n\n{0}\n\nThese original mod files are disabled, and no merge owns them:\n\n{1}\n\nDelete the merged files and restore the originals? Files still listed in the inventory will be kept. Choose No to leave everything as it is. Clear these up or restore their inventory before creating new merges.",
				merged,
				originals );
		}

		return ShowMessage(
			message,
			UiText.T( "UnregisteredMerges", "Remove unregistered merges?" ),
			MessageBoxButtons.YesNo,
			MessageBoxIcon.Question ) == DialogResult.Yes;
	}
}
