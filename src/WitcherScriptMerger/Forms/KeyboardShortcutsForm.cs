using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using WitcherScriptMerger.Controls;
using WitcherScriptMerger.Theming;

namespace WitcherScriptMerger.Forms;

internal sealed class KeyboardShortcutsForm : BaseForm
{
	private readonly DataGridView grid;
	private readonly Dictionary<string, Keys> values;
	private readonly TextBox filter;
	private readonly AppSettings settings;

	internal KeyboardShortcutsForm( AppSettings settings )
	{
		this.settings = settings;
		values = KeyboardShortcuts.Operations.ToDictionary( operation => operation.Id, operation => KeyboardShortcuts.Read( settings, operation ) );
		Name = "KeyboardShortcutsForm";
		LiveText.BindText( this, () => UiText.T( "KeyboardShortcuts", "Keyboard shortcuts" ) );
		Font = new Font( "Segoe UI", 10 );
		AutoScaleMode = AutoScaleMode.Dpi;
		ClientSize = new Size( 820, 620 );
		MinimumSize = new Size( 620, 400 );
		StartPosition = FormStartPosition.CenterParent;
		ShowInTaskbar = false;
		MinimizeBox = false;

		var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding( 12 ), ColumnCount = 1, RowCount = 4 };

		layout.ColumnStyles.Add( new ColumnStyle( SizeType.Percent, 100 ) );
		layout.RowStyles.Add( new RowStyle( SizeType.AutoSize ) );
		layout.RowStyles.Add( new RowStyle( SizeType.AutoSize ) );
		layout.RowStyles.Add( new RowStyle( SizeType.Percent, 100 ) );
		layout.RowStyles.Add( new RowStyle( SizeType.AutoSize ) );

		var help = new Label { AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding( 0, 0, 0, 10 ) };

		layout.SizeChanged += (
			_,
			_ ) => help.MaximumSize = new Size( System.Math.Max( 1, layout.ClientSize.Width - layout.Padding.Horizontal ), 0 );
		LiveText.BindText(
			help,
			() => UiText.T(
				"KeyboardShortcuts",
				"Select an operation and press its shortcut. Use Clear to leave it unassigned. Changes apply when you save; typing in search fields keeps normal text-editing behavior." ) );
		filter = new TextBox { Dock = DockStyle.Fill, Margin = new Padding( 0, 0, 0, 8 ) };
		LiveText.BindPlaceholderText( filter, () => UiText.T( "KeyboardShortcuts", "Filter operations…" ) );
		grid = new ShortcutGrid
		{
			Dock = DockStyle.Fill,
			AllowUserToAddRows = false,
			AllowUserToDeleteRows = false,
			RowHeadersVisible = false,
			ReadOnly = true,
			MultiSelect = false,
			SelectionMode = DataGridViewSelectionMode.FullRowSelect,
			AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
			AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells,
			ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
			BackgroundColor = BackColor,
			BorderStyle = BorderStyle.FixedSingle
		};
		grid.Columns.Add( "Operation", UiText.T( "KeyboardShortcuts", "Operation" ) );
		grid.Columns.Add( "Shortcut", UiText.T( "KeyboardShortcuts", "Shortcut" ) );
		grid.Columns.Add( "Default", UiText.T( "KeyboardShortcuts", "Default" ) );
		grid.Columns[0].FillWeight = 60;
		grid.Columns[1].FillWeight = grid.Columns[2].FillWeight = 20;

		foreach ( var operation in KeyboardShortcuts.Operations )
		{
			int row = grid.Rows.Add(
				KeyboardShortcuts.Caption( operation ),
				KeyboardShortcuts.Display( values[operation.Id] ),
				KeyboardShortcuts.Display( operation.Default ) );

			grid.Rows[row].Tag = operation;
		}

		grid.KeyDown += CaptureShortcut;
		( (ShortcutGrid)grid ).CaptureShortcut = keys => CaptureShortcut( grid, new KeyEventArgs( keys ) );
		filter.TextChanged += ( _, _ ) =>
		{
			grid.CurrentCell = null;

			foreach ( DataGridViewRow row in grid.Rows )
				row.Visible = KeyboardShortcuts.Caption( (ShortcutOperation)row.Tag )
				.Contains( filter.Text.Trim(), StringComparison.CurrentCultureIgnoreCase );
		};

		var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding( 0, 8, 0, 0 ) };
		var cancel = Button(
			() => UiText.T( "KeyboardShortcuts", "Cancel" ),
			( _, _ ) => { DialogResult = DialogResult.Cancel; Close(); } );
		var save = Button( () => UiText.T( "KeyboardShortcuts", "Save" ), ( _, _ ) => Save() );
		var clear = Button( () => UiText.T( "KeyboardShortcuts", "Clear" ), ( _, _ ) =>
		{
			if ( grid.CurrentRow?.Tag is ShortcutOperation operation )
				Assign( operation, Keys.None );
		} );
		var defaults = Button( () => UiText.T( "KeyboardShortcuts", "Restore defaults" ), ( _, _ ) =>
		{
			foreach ( var operation in KeyboardShortcuts.Operations )
				Assign( operation, operation.Default );
		} );

		buttons.Controls.AddRange( [cancel, save, clear, defaults] );
		layout.Controls.Add( help );
		layout.Controls.Add( filter );
		layout.Controls.Add( grid );
		layout.Controls.Add( buttons );
		Controls.Add( layout );
		CancelButton = cancel;
		Program.ThemeMngr?.ApplyThemeOnForm( this );
		ApplyGridTheme();
	}

	protected override void OnBackColorChanged( EventArgs e )
	{
		base.OnBackColorChanged( e );
		ApplyGridTheme();
	}

	protected override void OnForeColorChanged( EventArgs e )
	{
		base.OnForeColorChanged( e );
		ApplyGridTheme();
	}

	private void ApplyGridTheme()
	{
		if ( grid == null || ThemeManager.CurrentTheme is not { } theme )
			return;

		grid.EnableHeadersVisualStyles = false;
		grid.BackgroundColor = theme.InputBackColor;
		grid.GridColor = theme.BorderColor;
		grid.DefaultCellStyle.BackColor = theme.InputBackColor;
		grid.DefaultCellStyle.ForeColor = theme.ForeColor;
		grid.DefaultCellStyle.SelectionBackColor = theme.AcceptButtonBackColor;
		grid.DefaultCellStyle.SelectionForeColor = theme.AcceptButtonForeColor;
		grid.ColumnHeadersDefaultCellStyle.BackColor = theme.StandardButtonBackColor;
		grid.ColumnHeadersDefaultCellStyle.ForeColor = theme.ForeColor;
	}

	private static ModernButton Button( Func<string> text, EventHandler click )
	{
		var button = new ModernButton { AutoSize = true, MinimumSize = new Size( 95, 36 ), Padding = new Padding( 10, 4, 10, 4 ) };

		LiveText.BindText( button, text );
		button.Click += click;

		return button;
	}

	private void CaptureShortcut( object sender, KeyEventArgs e )
	{
		e.Handled = e.SuppressKeyPress = true;

		if ( grid.CurrentRow?.Tag is not ShortcutOperation operation || !KeyboardShortcuts.Valid( e.KeyData ) )
			return;

		Assign( operation, e.KeyData );
	}

	internal void Assign( ShortcutOperation operation, Keys keys )
	{
		values[operation.Id] = keys;

		foreach ( DataGridViewRow row in grid.Rows )
			if ( ( (ShortcutOperation)row.Tag ).Id == operation.Id )
				row.Cells[1].Value = KeyboardShortcuts.Display( keys );
	}

	private void Save()
	{
		try
		{
			KeyboardShortcuts.Save( settings, values );
		}
		catch ( Exception error ) when ( error is ArgumentException or System.IO.IOException or UnauthorizedAccessException )
		{
			MessageBox.Show(
				this,
				error.Message,
				UiText.T( "KeyboardShortcuts", "Could not save shortcuts" ),
				MessageBoxButtons.OK,
				MessageBoxIcon.Warning );

			return;
		}

		DialogResult = DialogResult.OK;
		Close();
	}

	// Capture the key for assignment before the grid treats it as a navigation command.
	private sealed class ShortcutGrid : DataGridView
	{
		internal Action<Keys> CaptureShortcut;

		protected override bool ProcessCmdKey( ref Message message, Keys keys )
		{
			if ( CurrentRow != null && KeyboardShortcuts.Valid( keys ) )
			{
				CaptureShortcut?.Invoke( keys );

				return true;
			}

			return base.ProcessCmdKey( ref message, keys );
		}
	}
}
