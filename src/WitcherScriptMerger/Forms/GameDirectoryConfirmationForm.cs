using System.Drawing;
using System.Windows.Forms;
using WitcherScriptMerger.Controls;

namespace WitcherScriptMerger.Forms;

internal sealed class GameDirectoryConfirmationForm : BaseForm
{
	internal GameDirectoryConfirmationForm( string gameDirectory )
	{
		SuspendLayout();
		LiveText.BindText( this, () => UiText.T( "GameDirectoryConfirmationForm", "Confirm Witcher 3 Remastered Installation" ) );
		AutoScaleDimensions = new SizeF( 96, 96 );
		AutoScaleMode = AutoScaleMode.Dpi;
		ClientSize = new Size( 560, 200 );
		MinimumSize = Size.Empty;
		FormBorderStyle = FormBorderStyle.Sizable;
		MaximizeBox = false;
		MinimizeBox = false;
		ShowInTaskbar = false;
		ShowIcon = false;
		StartPosition = FormStartPosition.CenterParent;

		Label question = LiveText.Live( new Label()
		{
			AutoSize = true,
			Dock = DockStyle.Fill,
			Font = new Font( "Segoe UI", 11f, FontStyle.Regular, GraphicsUnit.Point ),
			Margin = new Padding( 0, 0, 0, 8 ),
			Text = UiText.T( "GameDirectoryConfirmationForm", "Use this Witcher 3 Remastered installation?" )
		}, control => { LiveText.BindText(
			control,
			() => UiText.T( "GameDirectoryConfirmationForm", "Use this Witcher 3 Remastered installation?" ) ); } );
		TextBox path = new TextBox()
		{
			Font = new Font( "Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Point ),
			Margin = Padding.Empty,
			ReadOnly = true,
			TabStop = false,
			Text = gameDirectory
		};
		ModernTextBoxHost pathHost = new ModernTextBoxHost( path )
		{
			Margin = new Padding( 0, 0, 0, 8 ),
			MinimumSize = new Size( 0, 40 ),
			Padding = new Padding( 12, 7, 12, 7 )
		};
		Label help = LiveText.Live( new Label()
		{
			AutoSize = true,
			Dock = DockStyle.Fill,
			Font = new Font( "Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point ),
			Margin = Padding.Empty,
			Text = UiText.T(
				"GameDirectoryConfirmationForm",
				"Choose No to browse for another folder. You can also change it later in Options." )
		}, control => { LiveText.BindText(
			control,
			() => UiText.T(
				"GameDirectoryConfirmationForm",
				"Choose No to browse for another folder. You can also change it later in Options." ) ); } );
		ModernButton yes = LiveText.Live( new ModernButton()
		{
			AutoSize = false,
			DialogResult = DialogResult.Yes,
			MinimumSize = new Size( 90, 34 ),
			Size = new Size( 90, 34 ),
			Margin = Padding.Empty,
			Name = "btnConfirmGameDirectory",
			Text = UiText.T( "GameDirectoryConfirmationForm", "Yes" )
		}, control => { LiveText.BindText( control, () => UiText.T( "GameDirectoryConfirmationForm", "Yes" ) ); } );
		ModernButton no = LiveText.Live( new ModernButton()
		{
			AutoSize = false,
			DialogResult = DialogResult.No,
			Margin = new Padding( 14, 0, 0, 0 ),
			MinimumSize = new Size( 90, 34 ),
			Size = new Size( 90, 34 ),
			Name = "btnRejectGameDirectory",
			Text = UiText.T( "GameDirectoryConfirmationForm", "No" )
		}, control => { LiveText.BindText( control, () => UiText.T( "GameDirectoryConfirmationForm", "No" ) ); } );
		FlowLayoutPanel actions = new FlowLayoutPanel()
		{
			AutoSize = true,
			Dock = DockStyle.Fill,
			FlowDirection = FlowDirection.RightToLeft,
			Margin = Padding.Empty,
			WrapContents = false
		};

		actions.Controls.Add( no );
		actions.Controls.Add( yes );

		TableLayoutPanel layout = new TableLayoutPanel()
		{
			ColumnCount = 1,
			Dock = DockStyle.Top,
			AutoSize = true,
			Padding = new Padding( 14 ),
			RowCount = 5
		};

		layout.ColumnStyles.Add( new ColumnStyle( SizeType.Percent, 100f ) );
		layout.RowStyles.Add( new RowStyle( SizeType.AutoSize ) );
		layout.RowStyles.Add( new RowStyle( SizeType.AutoSize ) );
		layout.RowStyles.Add( new RowStyle( SizeType.AutoSize ) );
		layout.RowStyles.Add( new RowStyle( SizeType.Absolute, 12f ) );
		layout.RowStyles.Add( new RowStyle( SizeType.AutoSize ) );
		layout.Controls.Add( question, 0, 0 );
		layout.Controls.Add( pathHost, 0, 1 );
		layout.Controls.Add( help, 0, 2 );
		layout.Controls.Add( actions, 0, 4 );
		AutoScroll = true;
		Controls.Add( layout );
		AcceptButton = yes;
		CancelButton = no;
		Load += ( _, _ ) => ClientSize = new Size( ClientSize.Width,
			layout.GetPreferredSize( new Size( ClientSize.Width, 0 ) ).Height );
		Shown += ( _, _ ) => yes.Select();
		AutoScaleDimensions = new SizeF( 96, 96 );
		ResumeLayout( true );
	}
}
