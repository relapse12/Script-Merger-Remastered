using System;
using System.Drawing;
using System.Windows.Forms;
using WitcherScriptMerger.Controls;

namespace WitcherScriptMerger.Forms;

internal sealed class LanguagePackDetailsForm : BaseForm
{
	private readonly TextBox code;
	private readonly TextBox name;
	internal string LanguageCode => code.Text;
	internal string LanguageName => name.Text;

	internal LanguagePackDetailsForm()
	{
		SuspendLayout();
		AutoScaleDimensions = new SizeF( 96, 96 );
		AutoScaleMode = AutoScaleMode.Dpi;
		Font = new Font( "Segoe UI", 10f );
		LiveText.BindText( this, () => UiText.T( "LanguagePackDetailsForm", "Translation language" ) );
		ShowIcon = false;
		ShowInTaskbar = false;
		MinimizeBox = false;
		MaximizeBox = false;
		StartPosition = FormStartPosition.CenterParent;
		ClientSize = new Size( 480, 280 );
		MinimumSize = new Size( 400, 280 );

		var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding( 18 ), ColumnCount = 1, RowCount = 5 };

		layout.SuspendLayout();
		layout.ColumnStyles.Add( new ColumnStyle( SizeType.Percent, 100 ) );

		for ( int index = 0; index < 4; index++ )
			layout.RowStyles.Add( new RowStyle( SizeType.AutoSize ) );

		layout.RowStyles.Add( new RowStyle( SizeType.Percent, 100 ) );
		code = new TextBox { Name = "txtLanguageCode", MaxLength = 20 };
		name = new TextBox { Name = "txtLanguageName", MaxLength = 80 };
		layout.Controls.Add( LiveText.Live( new Label
		{
			AutoSize = true,
			Dock = DockStyle.Fill,
			Text = UiText.T( "LanguagePackDetailsForm", "Language code (for example: fr, de, pt-BR)" )
		}, control => { LiveText.BindText(
			control,
			() => UiText.T( "LanguagePackDetailsForm", "Language code (for example: fr, de, pt-BR)" ) ); } ), 0, 0 );
		layout.Controls.Add(
			new ModernTextBoxHost( code ) { Dock = DockStyle.Top, AutoSize = true, MinimumSize = new Size( 0, 40 ), Margin = new Padding( 0, 6, 0, 16 ) },
			0,
			1 );
		layout.Controls.Add( LiveText.Live( new Label
		{
			AutoSize = true,
			Dock = DockStyle.Fill,
			Text = UiText.T( "LanguagePackDetailsForm", "Language name (shown in Options)" )
		}, control => { LiveText.BindText( control, () => UiText.T( "LanguagePackDetailsForm", "Language name (shown in Options)" ) ); } ), 0, 2 );
		layout.Controls.Add(
			new ModernTextBoxHost( name ) { Dock = DockStyle.Top, AutoSize = true, MinimumSize = new Size( 0, 40 ), Margin = new Padding( 0, 6, 0, 16 ) },
			0,
			3 );

		var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
		var cancel = LiveText.Live( new ModernButton
		{
			Name = "btnCancel",
			Text = UiText.T( "LanguagePackDetailsForm", "Cancel" ),
			AutoSize = true,
			MinimumSize = new Size( 100, 36 ),
			DialogResult = DialogResult.Cancel
		}, control => { LiveText.BindText( control, () => UiText.T( "LanguagePackDetailsForm", "Cancel" ) ); } );
		var ok = LiveText.Live( new ModernButton
		{
			Name = "btnOK",
			Text = UiText.T( "LanguagePackDetailsForm", "OK" ),
			AutoSize = true,
			MinimumSize = new Size( 100, 36 ),
			DialogResult = DialogResult.OK
		}, control => { LiveText.BindText( control, () => UiText.T( "LanguagePackDetailsForm", "OK" ) ); } );

		actions.Controls.Add( cancel );
		actions.Controls.Add( ok );
		layout.Controls.Add( actions, 0, 4 );
		Controls.Add( layout );
		AcceptButton = ok;
		CancelButton = cancel;
		Shown += ( _, _ ) => code.Focus();
		layout.ResumeLayout( true );
		ResumeLayout( true );
	}

	protected override void OnFormClosing( FormClosingEventArgs e )
	{
		if ( DialogResult == DialogResult.OK )
		{
			try
			{
				UiText.ValidateLanguageInfo( code.Text, name.Text );
			}
			catch ( Exception error ) when ( error is System.IO.InvalidDataException or ArgumentException )
			{
				e.Cancel = true;
				MessageBox.Show(
					this,
					error.Message,
					UiText.T( "LanguagePackDetailsForm", "Invalid language details" ),
					MessageBoxButtons.OK,
					MessageBoxIcon.Warning );
			}
		}

		base.OnFormClosing( e );
	}
}
