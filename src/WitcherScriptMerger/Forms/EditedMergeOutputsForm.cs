using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using WitcherScriptMerger.Controls;

namespace WitcherScriptMerger.Forms;

// Ask before deleting a merged file that was edited after the merge. The user can back it up first.
internal sealed class EditedMergeOutputsForm : BaseForm
{
	internal EditedMergeOutputsForm( IReadOnlyList<string> paths, string backupDirectory )
	{
		Text = UiText.T( "BlobMerge", "Edited merged files" );
		ShowIcon = false;
		ShowInTaskbar = false;
		MinimizeBox = false;
		MaximizeBox = false;
		StartPosition = FormStartPosition.CenterParent;
		Font = new Font( "Segoe UI", 10f );
		ClientSize = new Size( 760, 360 );
		MinimumSize = new Size( 680, 300 );

		var layout = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			Padding = new Padding( 16 ),
			ColumnCount = 1,
			RowCount = 4
		};
		layout.ColumnStyles.Add( new ColumnStyle( SizeType.Percent, 100 ) );
		layout.RowStyles.Add( new RowStyle( SizeType.AutoSize ) );
		layout.RowStyles.Add( new RowStyle( SizeType.Percent, 100 ) );
		layout.RowStyles.Add( new RowStyle( SizeType.Absolute, 64 ) );
		layout.RowStyles.Add( new RowStyle( SizeType.AutoSize ) );

		var summary = new Label
		{
			AutoSize = true,
			Dock = DockStyle.Fill,
			MaximumSize = new Size( 720, 0 ),
			Margin = new Padding( 0, 0, 0, 12 ),
			Text = UiText.T( "BlobMerge", "These merged files changed after they were created. Removing the merge will discard those changes." )
		};

		var files = new ListBox
		{
			Name = "lstEditedOutputs",
			Dock = DockStyle.Fill,
			HorizontalScrollbar = true
		};

		foreach ( string path in paths )
			files.Items.Add( path );

		var location = new TextBox
		{
			Dock = DockStyle.Fill,
			Multiline = true,
			ReadOnly = true,
			BorderStyle = BorderStyle.None,
			Margin = new Padding( 0, 10, 0, 8 ),
			Text = UiText.T( "BlobMerge", "Backups will be saved to:" ) + Environment.NewLine + backupDirectory
		};

		var actions = new FlowLayoutPanel
		{
			Dock = DockStyle.Fill,
			AutoSize = true,
			FlowDirection = FlowDirection.RightToLeft,
			WrapContents = false
		};
		var cancel = new ModernButton
		{
			Name = "btnCancel",
			AutoSize = true,
			MinimumSize = new Size( 100, 38 ),
			DialogResult = DialogResult.Cancel,
			Text = UiText.T( "BlobMerge", "Cancel" )
		};
		var delete = new ModernButton
		{
			Name = "btnDeleteAnyway",
			AutoSize = true,
			MinimumSize = new Size( 140, 38 ),
			DialogResult = DialogResult.No,
			Text = UiText.T( "BlobMerge", "Delete anyway" )
		};
		var backup = new ModernButton
		{
			Name = "btnBackUpAndDelete",
			AutoSize = true,
			MinimumSize = new Size( 180, 38 ),
			DialogResult = DialogResult.Yes,
			Text = UiText.T( "BlobMerge", "Back up and delete" )
		};

		actions.Controls.AddRange( new Control[] { cancel, delete, backup } );
		layout.Controls.Add( summary, 0, 0 );
		layout.Controls.Add( files, 0, 1 );
		layout.Controls.Add( location, 0, 2 );
		layout.Controls.Add( actions, 0, 3 );
		Controls.Add( layout );
		AcceptButton = backup;
		CancelButton = cancel;
		SizeChanged += ( _, _ ) => summary.MaximumSize = new Size(
			Math.Max( 200, layout.ClientSize.Width - layout.Padding.Horizontal ), 0 );
	}
}
