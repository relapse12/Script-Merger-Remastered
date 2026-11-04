using System;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using WinRT;
using WinRT.WitcherScriptMergerVtableClasses;
using WitcherScriptMerger.Controls;
using WitcherScriptMerger.Theming;
using WitcherScriptMerger.Tools;

namespace WitcherScriptMerger.Forms;

// Asks where KDiff3 and the bundle packer are. Shown at startup when a required tool is missing.
[WinRTRuntimeClassName( "Windows.Foundation.IClosable" )]
[WinRTExposedType( typeof( WitcherScriptMerger_Forms_MergeReportFormWinRTTypeDetails ) )]
internal class DependencyForm : BaseForm
{
	private readonly GroupBox grpKDiff3 = LiveText.Live(
		new GroupBox() { Name = "grpKDiff3", Text = UiText.T( "DependencyForm", "KDiff3.exe" ) },
		control => { LiveText.BindText( control, () => UiText.T( "DependencyForm", "KDiff3.exe" ) ); } );
	private readonly GroupBox grpWccLite = LiveText.Live(
		new GroupBox() { Name = "grpWccLite", Text = UiText.T( "DependencyForm", "Bundle Packer" ) },
		control => { LiveText.BindText( control, () => UiText.T( "DependencyForm", "Bundle Packer" ) ); } );
	private readonly TextBox txtKDiff3Path = new TextBox() { Name = "txtKDiff3Path" };
	private readonly TextBox txtWccLitePath = new TextBox() { Name = "txtWccLitePath" };
	private readonly Label lblKDiff3Status = new Label() { Name = "lblKDiff3Status" };
	private readonly Label lblWccLiteStatus = new Label() { Name = "lblWccLiteStatus" };
	private readonly ModernButton btnKDiff3Path;
	private readonly ModernButton btnWccLitePath;
	private readonly ModernButton btnBundledW3Bundle;
	private readonly ModernButton btnOK;
	private readonly ModernButton btnCancel;

	private bool AreAnyPathsChanged =>
		!txtKDiff3Path.Text.EqualsIgnoreCase( KDiff3.ExePath )
		|| !txtWccLitePath.Text.EqualsIgnoreCase( WccLite.ExePath );

	public DependencyForm()
	{
		btnKDiff3Path = CreateModernButton( "btnKDiff3Path", "Browse…", BtnKDiff3Path_Click );
		btnWccLitePath = CreateModernButton( "btnWccLitePath", "Browse…", BtnWccLitePath_Click );
		btnBundledW3Bundle = new ModernButton()
		{
			Name = "btnBundledW3Bundle",
			AutoSize = true,
			UseVisualStyleBackColor = false,
			MinimumSize = new Size( 160, 40 )
		};
		LiveText.BindText( btnBundledW3Bundle, () => UiText.T( "DependencyForm", "Use Bundled W3Bundle" ) );
		btnBundledW3Bundle.Enabled = File.Exists( WccLite.BundledExePath );
		btnBundledW3Bundle.Click += ( _, _ ) => txtWccLitePath.Text = WccLite.BundledExePath;
		btnOK = CreateModernButton( "btnOK", "OK", BtnOK_Click );
		btnCancel = CreateModernButton( "btnCancel", "Cancel", BtnCancel_Click );
		txtKDiff3Path.TextChanged += Exe_TextChanged;
		txtWccLitePath.TextChanged += Exe_TextChanged;
		ConfigureLayout();
		Program.ThemeMngr.ApplyThemeOnForm( this );
		Program.UpdateWindowSizeFromWorkingArea( this );
		Load += DependencyForm_Load;
	}

	private void ConfigureLayout()
	{
		SuspendLayout();
		LiveText.BindText( this, () => UiText.T( "DependencyForm", "Dependency Locations" ) );
		TopMost = false;
		AutoScroll = true;
		AutoScaleMode = AutoScaleMode.Dpi;
		FormBorderStyle = FormBorderStyle.Sizable;
		MaximizeBox = false;
		MinimizeBox = false;
		MinimumSize = Size.Empty;
		MaximumSize = Size.Empty;
		ClientSize = new Size( 780, 520 );
		StartPosition = FormStartPosition.CenterParent;

		ComponentResourceManager resources = new ComponentResourceManager( typeof( DependencyForm ) );

		if ( resources.GetObject( "$this.Icon" ) is Icon icon )
		{
			Icon = icon;
		}

		ConfigureDependencyGroup(
			grpKDiff3,
			txtKDiff3Path,
			btnKDiff3Path,
			lblKDiff3Status,
			UiText.T( "DependencyForm", "Required for creating and reviewing text merges." ) );
		ConfigureDependencyGroup(
			grpWccLite,
			txtWccLitePath,
			btnWccLitePath,
			lblWccLiteStatus,
			UiText.T(
				"DependencyForm",
				"W3Bundle is included for packing merged XML/CSV and metadata. Select wcc_lite.exe to use official CDPR tooling instead." ) );

		var packerContent = (TableLayoutPanel)grpWccLite.Controls[0];

		packerContent.RowCount = 4;
		packerContent.RowStyles.Add( new RowStyle( SizeType.AutoSize ) );
		btnBundledW3Bundle.Margin = new Padding( 0, 8, 0, 0 );
		packerContent.Controls.Add( btnBundledW3Bundle, 0, 3 );
		packerContent.SetColumnSpan( btnBundledW3Bundle, 3 );

		ConfigureActionButton( btnOK, DialogResult.None );
		ConfigureActionButton( btnCancel, DialogResult.Cancel );
		AcceptButton = btnOK;
		CancelButton = btnCancel;

		FlowLayoutPanel actions = new FlowLayoutPanel()
		{
			AutoSize = true,
			AutoSizeMode = AutoSizeMode.GrowAndShrink,
			Dock = DockStyle.Fill,
			FlowDirection = FlowDirection.RightToLeft,
			Margin = Padding.Empty,
			Name = "flpDependencyActions",
			Padding = new Padding( 0, 8, 0, 0 ),
			WrapContents = false
		};

		btnCancel.Margin = new Padding( 10, 0, 0, 0 );
		btnOK.Margin = Padding.Empty;
		actions.Controls.Add( btnCancel );
		actions.Controls.Add( btnOK );

		TableLayoutPanel root = new TableLayoutPanel()
		{
			ColumnCount = 1,
			Dock = DockStyle.Fill,
			Margin = Padding.Empty,
			Name = "tlpDependenciesRoot",
			AutoSize = true,
			Padding = new Padding( 18 ),
			RowCount = 4
		};

		root.ColumnStyles.Add( new ColumnStyle( SizeType.Percent, 100f ) );
		root.RowStyles.Add( new RowStyle( SizeType.AutoSize ) );
		root.RowStyles.Add( new RowStyle( SizeType.AutoSize ) );
		root.RowStyles.Add( new RowStyle( SizeType.Absolute, 20f ) );
		root.RowStyles.Add( new RowStyle( SizeType.AutoSize ) );
		grpKDiff3.Margin = new Padding( 0, 0, 0, 12 );
		grpWccLite.Margin = Padding.Empty;
		root.Controls.Add( grpKDiff3, 0, 0 );
		root.Controls.Add( grpWccLite, 0, 1 );
		root.Controls.Add( actions, 0, 3 );
		root.Dock = DockStyle.Top;
		Controls.Add( root );
		AutoScaleDimensions = new SizeF( 96, 96 );
		ResumeLayout( performLayout: true );
	}

	private static ModernButton CreateModernButton( string name, string text, EventHandler click )
	{
		ModernButton button = new ModernButton()
		{
			AutoSize = false,
			Name = name,
			Text = text,
			UseVisualStyleBackColor = false
		};

		button.Click += click;

		return button;
	}

	private static void ConfigureActionButton( Button button, DialogResult dialogResult )
	{
		button.AutoSize = false;
		button.DialogResult = dialogResult;
		button.MinimumSize = new Size( 120, 40 );
		button.Size = button.MinimumSize;
	}

	private static void ConfigureDependencyGroup(
		GroupBox group,
		TextBox path,
		Button browse,
		Label status,
		string description )
	{
		group.SuspendLayout();
		group.AutoSize = true;
		group.AutoSizeMode = AutoSizeMode.GrowAndShrink;
		group.Dock = DockStyle.Top;
		group.Font = new Font( "Segoe UI", 10f, FontStyle.Bold, GraphicsUnit.Point );
		group.Padding = new Padding( 12, 8, 12, 12 );

		Label pathLabel = LiveText.Live( new Label()
		{
			Anchor = AnchorStyles.Left,
			AutoSize = true,
			Font = new Font( "Segoe UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point ),
			Margin = new Padding( 0, 7, 10, 7 ),
			Text = UiText.T( "DependencyForm", "Path:" )
		}, control => { LiveText.BindText( control, () => UiText.T( "DependencyForm", "Path:" ) ); } );

		path.Anchor = AnchorStyles.Left | AnchorStyles.Right;
		path.Font = new Font( "Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Point );
		path.Margin = Padding.Empty;

		ModernTextBoxHost pathHost = new ModernTextBoxHost( path )
		{
			Margin = new Padding( 0, 4, 10, 4 ),
			MinimumSize = new Size( 0, 40 ),
			Name = path.Name + "Host",
			Padding = new Padding( 12, 7, 12, 7 )
		};

		browse.AutoSize = false;
		browse.Dock = DockStyle.Fill;
		browse.Margin = new Padding( 0, 4, 0, 4 );
		browse.MinimumSize = new Size( 104, 40 );
		browse.Size = browse.MinimumSize;
		browse.UseVisualStyleBackColor = false;
		status.AutoSize = true;
		status.Dock = DockStyle.Fill;
		status.Font = new Font( "Segoe UI", 9f, FontStyle.Bold, GraphicsUnit.Point );
		status.Margin = new Padding( 0, 5, 0, 2 );

		Label descriptionLabel = new Label()
		{
			AutoSize = true,
			Dock = DockStyle.Fill,
			Font = new Font( "Segoe UI", 8.75f, FontStyle.Regular, GraphicsUnit.Point ),
			Margin = new Padding( 0, 2, 0, 2 ),
			Name = group.Name + "Description",
			Text = description
		};
		TableLayoutPanel content = new TableLayoutPanel()
		{
			AutoSize = true,
			AutoSizeMode = AutoSizeMode.GrowAndShrink,
			ColumnCount = 3,
			Dock = DockStyle.Top,
			Margin = Padding.Empty,
			Padding = new Padding( 0, 4, 0, 0 ),
			RowCount = 3
		};

		content.ColumnStyles.Add( new ColumnStyle( SizeType.AutoSize ) );
		content.ColumnStyles.Add( new ColumnStyle( SizeType.Percent, 100f ) );
		content.ColumnStyles.Add( new ColumnStyle( SizeType.AutoSize ) );
		content.RowStyles.Add( new RowStyle( SizeType.AutoSize ) );
		content.RowStyles.Add( new RowStyle( SizeType.AutoSize ) );
		content.RowStyles.Add( new RowStyle( SizeType.AutoSize ) );
		content.Controls.Add( pathLabel, 0, 0 );
		content.Controls.Add( pathHost, 1, 0 );
		content.Controls.Add( browse, 2, 0 );
		content.Controls.Add( status, 0, 1 );
		content.SetColumnSpan( status, 3 );
		content.Controls.Add( descriptionLabel, 0, 2 );
		content.SetColumnSpan( descriptionLabel, 3 );

		void UpdateWrapWidths()
		{
			int width = Math.Max( 120, content.ClientSize.Width - content.Padding.Horizontal );
			Size wrapLimit = new Size( width, 0 );

			status.MaximumSize = wrapLimit;
			descriptionLabel.MaximumSize = wrapLimit;
		}

		content.SizeChanged += ( _, _ ) => UpdateWrapWidths();
		group.Controls.Add( content );
		group.ResumeLayout( performLayout: true );
		UpdateWrapWidths();
	}

	private void DependencyForm_Load( object sender, EventArgs e )
	{
		txtKDiff3Path.Text = KDiff3.ExePath;
		txtWccLitePath.Text = WccLite.ExePath;
		btnOK.Select();
	}

	private void BtnOK_Click( object sender, EventArgs e )
	{
		bool wccIsEmpty = string.IsNullOrWhiteSpace( txtWccLitePath.Text );
		bool allValid = IsValidFile( txtKDiff3Path, ".exe" )
			&& ( wccIsEmpty || WccLite.TryValidateInstallation( txtWccLitePath.Text, out _ ) );
		bool saveAnywayAccepted = allValid
			|| DialogResult.Yes == MessageBox.Show(
				this,
				UiText.T( "DependencyForm", "Not all the files are located and valid. Save settings anyway?" ),
				UiText.T( "DependencyForm", "Missing Dependency" ),
				MessageBoxButtons.YesNo,
				MessageBoxIcon.Exclamation );
		DialogResult acceptedResult = ResolveSaveDialogResult( allValid, saveAnywayAccepted );

		if ( acceptedResult != DialogResult.OK )
		{
			DialogResult = DialogResult.None;

			return;
		}

		if ( AreAnyPathsChanged )
		{
			KDiff3.ExePath = UpdatePathSetting( KDiff3.ExePath, txtKDiff3Path.Text, "KDiff3Path" );

			string packerPath = WccLite.SettingPath( txtWccLitePath.Text, AppContext.BaseDirectory );

			Program.Settings.Set( "WccLitePath", packerPath );
			WccLite.ExePath = WccLite.ResolveExecutable( packerPath, AppContext.BaseDirectory );
			Program.Settings.Save();
		}

		DialogResult = acceptedResult;
	}

	internal static DialogResult ResolveSaveDialogResult( bool allDependenciesValid, bool saveAnywayAccepted )
	{
		return allDependenciesValid || saveAnywayAccepted
			? DialogResult.OK
			: DialogResult.None;
	}

	private static string UpdatePathSetting( string oldPath, string newPath, string settingName )
	{
		if ( oldPath.EqualsIgnoreCase( newPath ) )
		{
			return oldPath;
		}

		Program.Settings.Set( settingName, newPath );

		return newPath;
	}

	private void BtnCancel_Click( object sender, EventArgs e )
	{
		DialogResult = DialogResult.Cancel;
	}

	private void BtnKDiff3Path_Click( object sender, EventArgs e )
	{
		GetUserFileChoice( txtKDiff3Path );
	}

	private void BtnWccLitePath_Click( object sender, EventArgs e )
	{
		GetUserFileChoice( txtWccLitePath );
	}

	// Keep paths inside the application folder relative so moving the installation does not break them.
	private void GetUserFileChoice( TextBox textBox )
	{
		using OpenFileDialog dialog = new OpenFileDialog()
		{
			Filter = "Executables|*.exe",
			RestoreDirectory = true
		};

		if ( !string.IsNullOrWhiteSpace( textBox.Text ) && File.Exists( textBox.Text ) )
		{
			dialog.FileName = textBox.Text;
		}

		if ( dialog.ShowDialog( this ) == DialogResult.OK )
		{
			textBox.Text = dialog.FileName.Replace( Environment.CurrentDirectory + "\\", "" );
		}
	}

	// A blank packer path selects the bundled tool.
	private void Exe_TextChanged( object sender, EventArgs e )
	{
		TextBox textBox = sender as TextBox;

		if ( ReferenceEquals( textBox, txtWccLitePath ) )
		{
			if ( string.IsNullOrWhiteSpace( textBox.Text ) )
			{
				textBox.BackColor = ThemeManager.CurrentTheme.InputBackColor;
				textBox.ForeColor = ThemeManager.CurrentTheme.ForeColor;
				LiveText.BindText(
					lblWccLiteStatus,
					() => UiText.T(
						"DependencyForm",
						"Uses bundled W3Bundle when available. Required only for packing bundled-text merges." ) );
				lblWccLiteStatus.ForeColor = ThemeManager.CurrentTheme.ForeColor;

				return;
			}

			bool valid = WccLite.TryValidateInstallation( textBox.Text, out string reason );

			SetPathStatus( textBox, lblWccLiteStatus, valid,
				valid ? WccLite.IsW3Bundle( textBox.Text )
					? UiText.T( "DependencyForm", "Ready — W3Bundle executable found." )
					: UiText.T( "DependencyForm", "Ready — complete WCC installation detected." ) : reason );

			return;
		}

		bool executableValid = IsValidFile( textBox, ".exe" );

		SetPathStatus( textBox, lblKDiff3Status, executableValid,
			executableValid ? UiText.T( "DependencyForm", "Ready — KDiff3 executable found." ) : UiText.T( "DependencyForm", "Select a valid KDiff3 executable." ) );
	}

	private static bool IsValidFile( TextBox textBox, string validExtension )
	{
		string path = textBox?.Text ?? string.Empty;

		return path.EndsWithIgnoreCase( validExtension ) && File.Exists( path );
	}

	private static void SetPathStatus( TextBox textBox, Label statusLabel, bool valid, string message )
	{
		textBox.BackColor = ThemeManager.CurrentTheme.InputBackColor;
		textBox.ForeColor = ThemeManager.CurrentTheme.ForeColor;
		statusLabel.Text = message;
		statusLabel.ForeColor = valid
			? ( ThemeManager.CurrentTheme.ThemeName == "Dark" ? Colors.GetColorByHex( "73D99B" ) : Colors.GetColorByHex( "1A7F37" ) )
			: ( ThemeManager.CurrentTheme.ThemeName == "Dark" ? Colors.GetColorByHex( "FF8C86" ) : Colors.GetColorByHex( "CF222E" ) );
	}
}
