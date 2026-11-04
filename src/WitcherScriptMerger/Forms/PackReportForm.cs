using System;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using WinRT;
using WinRT.WitcherScriptMergerVtableClasses;
using WitcherScriptMerger.Controls;

namespace WitcherScriptMerger.Forms;

[WinRTRuntimeClassName( "Windows.Foundation.IClosable" )]
[WinRTExposedType( typeof( WitcherScriptMerger_Forms_MergeReportFormWinRTTypeDetails ) )]
internal class PackReportForm : BaseForm
{
	private readonly TextBox txtBundlePath;
	private readonly CheckBox chkShowAfterPack;

	public PackReportForm( string bundlePath )
		: this( bundlePath, bundlePath == null ? Array.Empty<string>()
			: Directory.GetFiles( Paths.MergedBundleContent, "*", SearchOption.AllDirectories ) )
	{
	}

	internal PackReportForm( string bundlePath, string[] contentPaths )
	{
		SuspendLayout();
		AutoScaleDimensions = new SizeF( 96, 96 );
		AutoScaleMode = AutoScaleMode.Dpi;
		Font = new Font( "Segoe UI", 10f );
		ClientSize = new Size( 680, 440 );
		MinimumSize = new Size( 480, 360 );
		LiveText.BindText( this, () => UiText.T( "PackReportForm", "Pack Finished" ) );
		Name = "PackReportForm";
		Icon = (Icon)new ComponentResourceManager( typeof( PackReportForm ) ).GetObject( "$this.Icon" );
		StartPosition = FormStartPosition.CenterParent;
		ShowInTaskbar = false;
		MaximizeBox = false;
		MinimizeBox = false;

		var layout = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			Padding = new Padding( 18 ),
			ColumnCount = 1,
			RowCount = 5
		};

		layout.ColumnStyles.Add( new ColumnStyle( SizeType.Percent, 100 ) );
		layout.RowStyles.Add( new RowStyle( SizeType.AutoSize ) );
		layout.RowStyles.Add( new RowStyle( SizeType.AutoSize ) );
		layout.RowStyles.Add( new RowStyle( SizeType.AutoSize ) );
		layout.RowStyles.Add( new RowStyle( SizeType.Percent, 100 ) );
		layout.RowStyles.Add( new RowStyle( SizeType.AutoSize ) );

		layout.Controls.Add( Heading( UiText.T( "PackReportForm", "Merged bundle" ), ( _, _ ) =>
			Program.TryOpenFileLocation( txtBundlePath.Text ) ), 0, 0 );
		txtBundlePath = ReadOnlyText( "txtBundlePath", bundlePath ?? "" );
		layout.Controls.Add( new ModernTextBoxHost( txtBundlePath )
		{
			Name = "bundlePathHost",
			Margin = new Padding( 0, 6, 0, 16 ),
			Height = 64,
			MinimumSize = new Size( 0, 64 ),
			Padding = new Padding( 12, 10, 12, 10 )
		}, 0, 1 );

		layout.Controls.Add( Heading( UiText.T( "PackReportForm", "Packed content" ), ( _, _ ) =>
			Program.TryOpenDirectory( Paths.MergedBundleContent ) ), 0, 2 );
		layout.Controls.Add( new ModernTextBoxHost( ReadOnlyText( "txtContent",
			string.Join( Environment.NewLine, contentPaths ) ) )
		{
			Name = "contentHost",
			Margin = new Padding( 0, 6, 0, 16 ),
			Padding = new Padding( 12, 10, 12, 10 )
		}, 0, 3 );

		var footer = new TableLayoutPanel
		{
			Name = "packFooter",
			Dock = DockStyle.Fill,
			AutoSize = true,
			ColumnCount = 2,
			RowCount = 1,
			Margin = Padding.Empty
		};

		footer.ColumnStyles.Add( new ColumnStyle( SizeType.Percent, 100 ) );
		footer.ColumnStyles.Add( new ColumnStyle( SizeType.AutoSize ) );
		chkShowAfterPack = LiveText.Live( new CheckBox
		{
			Name = "chkShowAfterPack",
			Text = UiText.T( "PackReportForm", "&Show this report after packing" ),
			AutoSize = true,
			Anchor = AnchorStyles.Left,
			Margin = new Padding( 0, 0, 12, 0 ),
			Checked = Program.Settings.Get<bool>( "ReportAfterPack" )
		}, control => { LiveText.BindText( control, () => UiText.T( "PackReportForm", "&Show this report after packing" ) ); } );

		var ok = LiveText.Live( new ModernButton
		{
			Name = "btnOK",
			Text = UiText.T( "PackReportForm", "OK" ),
			Size = new Size( 96, 36 ),
			Margin = Padding.Empty,
			DialogResult = DialogResult.OK
		}, control => { LiveText.BindText( control, () => UiText.T( "PackReportForm", "OK" ) ); } );

		footer.Controls.Add( chkShowAfterPack, 0, 0 );
		footer.Controls.Add( ok, 1, 0 );
		layout.Controls.Add( footer, 0, 4 );
		Controls.Add( layout );
		AcceptButton = ok;
		CancelButton = ok;
		Shown += ( _, _ ) => ok.Select();
		FormClosing += ( _, _ ) => Program.Settings.Set( "ReportAfterPack", chkShowAfterPack.Checked );
		ResumeLayout( true );
	}

	private TableLayoutPanel Heading( string text, EventHandler click )
	{
		var row = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			AutoSize = true,
			ColumnCount = 2,
			Margin = Padding.Empty
		};

		row.ColumnStyles.Add( new ColumnStyle( SizeType.Percent, 100 ) );
		row.ColumnStyles.Add( new ColumnStyle( SizeType.AutoSize ) );
		row.Controls.Add( new Label
		{
			Text = text,
			AutoSize = true,
			Anchor = AnchorStyles.Left,
			Font = new Font( Font, FontStyle.Bold ),
			Margin = Padding.Empty
		}, 0, 0 );

		var button = LiveText.Live( new ModernButton
		{
			Text = UiText.T( "PackReportForm", "Open folder" ),
			Size = new Size( 112, 34 ),
			Margin = Padding.Empty
		}, control => { LiveText.BindText( control, () => UiText.T( "PackReportForm", "Open folder" ) ); } );

		button.Click += click;
		row.Controls.Add( button, 1, 0 );

		return row;
	}

	private static TextBox ReadOnlyText( string name, string text )
	{
		var box = new TextBox
		{
			Name = name,
			Text = text,
			ReadOnly = true,
			Multiline = true,
			WordWrap = true,
			ScrollBars = ScrollBars.Vertical
		};

		box.KeyDown += ( _, e ) =>
		{
			if ( e.Control && e.KeyCode == Keys.A )
			{
				box.SelectAll();
				e.SuppressKeyPress = true;
			}
		};

		return box;
	}
}
