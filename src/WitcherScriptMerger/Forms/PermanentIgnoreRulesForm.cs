using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using WitcherScriptMerger.Controls;
using WitcherScriptMerger.Theming;

namespace WitcherScriptMerger.Forms;

internal sealed class PermanentIgnoreRulesForm : BaseForm
{
	private readonly IgnoredConflictStore store;
	private readonly ListBox rules = LiveText.Live( new ListBox()
	{
		Dock = DockStyle.Fill,
		SelectionMode = SelectionMode.MultiExtended,
		AccessibleName = UiText.T( "PermanentIgnoreRulesForm", "Permanent filename and path rules" )
	}, control => { LiveText.BindAccessibleName( control, () => UiText.T( "PermanentIgnoreRulesForm", "Permanent filename and path rules" ) ); } );
	private readonly TextBox rule = LiveText.Live( new TextBox()
	{
		Dock = DockStyle.Fill,
		PlaceholderText = UiText.T( "PermanentIgnoreRulesForm", "strings.list" ),
		AccessibleName = UiText.T( "PermanentIgnoreRulesForm", "New permanent filename or path rule" )
	}, control => { LiveText.BindPlaceholderText( control, () => UiText.T( "PermanentIgnoreRulesForm", "strings.list" ) ); LiveText.BindAccessibleName( control, () => UiText.T( "PermanentIgnoreRulesForm", "New permanent filename or path rule" ) ); } );
	private readonly Label status = new Label() { AutoSize = true, Dock = DockStyle.Fill };
	private readonly Button remove;
	public bool Changed { get; private set; }

	public PermanentIgnoreRulesForm( IgnoredConflictStore store )
	{
		this.store = store;
		LiveText.BindText( this, () => UiText.T( "PermanentIgnoreRulesForm", "Permanent Ignore Rules" ) );
		Name = "PermanentIgnoreRulesForm";
		StartPosition = FormStartPosition.CenterParent;
		MinimumSize = new Size( 620, 400 );
		ClientSize = new Size( 800, 480 );
		Font = new Font( "Segoe UI", 10f );

		var layout = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			RowCount = 6,
			ColumnCount = 1,
			Padding = new Padding( 16 )
		};

		layout.ColumnStyles.Add( new ColumnStyle( SizeType.Percent, 100 ) );

		for ( int i = 0; i < 6; i++ )
			layout.RowStyles.Add( new RowStyle( SizeType.AutoSize ) );

		layout.RowStyles[1] = new RowStyle( SizeType.Percent, 100 );
		layout.Controls.Add( LiveText.Live( new Label
		{
			Text = UiText.T(
				"PermanentIgnoreRulesForm",
				"A filename such as strings.list matches in any folder. A path matches the complete relative path.\nUse * and ? for wildcards. These rules remain active even when the files change." ),
			AutoSize = true,
			Dock = DockStyle.Fill,
			Padding = new Padding( 0, 0, 0, 10 )
		}, control => { LiveText.BindText(
			control,
			() => UiText.T(
				"PermanentIgnoreRulesForm",
				"A filename such as strings.list matches in any folder. A path matches the complete relative path.\nUse * and ? for wildcards. These rules remain active even when the files change." ) ); } ), 0, 0 );
		layout.Controls.Add( rules, 0, 1 );
		layout.Controls.Add(
			LiveText.Live(
				new Label { Text = UiText.T( "PermanentIgnoreRulesForm", "Filename or relative path:" ), AutoSize = true, Padding = new Padding( 0, 8, 0, 2 ) },
				control => { LiveText.BindText( control, () => UiText.T( "PermanentIgnoreRulesForm", "Filename or relative path:" ) ); } ),
			0,
			2 );
		layout.Controls.Add( rule, 0, 3 );

		var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
		Button add = MakeButton( UiText.T( "PermanentIgnoreRulesForm", "Add permanent rule" ), ( _, _ ) =>
		{
			if ( Change( () => store.AddPermanentRules( new[] { rule.Text } ) ) )
				rule.Clear();
		} );

		remove = MakeButton( UiText.T( "PermanentIgnoreRulesForm", "Remove selected rules" ), ( _, _ ) =>
		{
			string[] selected = rules.SelectedItems.Cast<string>().ToArray();

			if ( selected.Length > 0 )
				Change( () => store.RemovePermanentRules( selected ) );
		} );

		Button close = MakeButton( UiText.T( "PermanentIgnoreRulesForm", "Close" ), ( _, _ ) => Close() );

		close.DialogResult = DialogResult.Cancel;
		CancelButton = close;
		actions.Controls.AddRange( new Control[] { add, remove, close } );
		layout.Controls.Add( actions, 0, 4 );
		layout.Controls.Add( status, 0, 5 );
		Controls.Add( layout );
		rules.SelectedIndexChanged += ( _, _ ) => remove.Enabled = rules.SelectedItems.Count > 0;
		rule.KeyDown += ( _, e ) =>
		{
			if ( e.KeyCode == Keys.Enter )
			{
				add.PerformClick();
				e.SuppressKeyPress = true;
			}
		};
		RefreshRules();
		Program.ThemeMngr.ApplyThemeOnForm( this );
		rules.BackColor = ThemeManager.CurrentTheme.TreeViewBackColor;
		rules.ForeColor = ThemeManager.CurrentTheme.ForeColor;
	}

	private static Button MakeButton( string text, EventHandler action )
	{
		var button = new ModernButton { Text = text, AutoSize = true, Padding = new Padding( 12, 6, 12, 6 ), Margin = new Padding( 4 ) };

		button.Click += action;

		return button;
	}

	private void RefreshRules()
	{
		rules.Items.Clear();
		rules.Items.AddRange( store.PermanentRules.Cast<object>().ToArray() );
		remove.Enabled = false;
		status.Text = UiText.F(
			"PermanentIgnoreRulesForm",
			$"{store.PermanentRules.Count} permanent rules. Changes are saved immediately." );
	}

	private bool Change( Action action )
	{
		try
		{
			action();
			Changed = true;
			RefreshRules();

			return true;
		}
		catch ( Exception ex )
		{
			MessageBox.Show( this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error );

			return false;
		}
	}
}
