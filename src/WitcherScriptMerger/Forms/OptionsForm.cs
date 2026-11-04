using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Win32;
using WinRT;
using WinRT.WitcherScriptMergerVtableClasses;
using WitcherScriptMerger.Controls;
using WitcherScriptMerger.FileIndex;
using WitcherScriptMerger.Theming;

namespace WitcherScriptMerger.Forms;

// Other parts of the application read the selected game folder from this form.
// Language-pack controls are in OptionsForm.Localization.cs.
[WinRTRuntimeClassName( "Windows.Foundation.IClosable" )]
[WinRTExposedType( typeof( WitcherScriptMerger_Forms_MergeReportFormWinRTTypeDetails ) )]
public partial class OptionsForm : BaseForm
{
	private string gameDirectory = string.Empty;
	private readonly HashSet<Control> constructionLayouts = new HashSet<Control>();
	private bool checkboxFadersAttached;

	private List<ColorFader> colorFaders;

	private IContainer components = new Container();


	private CheckBox chkPackReport;

	private CheckBox chkMergeReport;

	private CheckBox chkReviewEachMerge;

	private CheckBox chkCollapseCustomLoadOrder;

	private CheckBox chkCollapseNotMergeable;

	private Button btnCancel;

	private Button btnOK;

	private Button btnApply;

	private CheckBox chkAutoDeleteOldMerges;

	private CheckBox chkAutoExit;
	private CheckBox chkCheckForApplicationUpdates;

	private CheckBox chkAutoCreateScriptMerges;

	private CheckBox chkAutoOverwriteOldMerges;

	private CheckBox chkAutoSkipKDiff3InfoDialogs;

	private TextBox txtGameDir;

	private TextBox txtModIoDirectory;

	private Button btnDefaultOptions;

	private CheckBox chkAccentColor;

	private TextBox txtGameSettingsDirectory;
	private OptionsColumnsPanel optionsColumns;
	private OptionsStackPanel optionsLeft;
	private OptionsStackPanel optionsRight;

	private AccentColorSwatch swatchAccentColor;

	private Color customAccentColor = ThemeManager.DefaultAccentColor;

	private bool initialUseWindowsAccent;

	private Color initialCustomAccentColor = ThemeManager.DefaultAccentColor;

	private bool suppressAccentPreview;

	private ComboBox cbColorTheme;
	private NumericUpDown numConflictListFontSize;

	private ComboBox cbPrioTooltips;

	private CheckBox chkCollapseIdenticalConflicts;

	private Panel pnlOptionsMain;

	[DesignerSerializationVisibility( DesignerSerializationVisibility.Hidden )]
	public string GameDirectory
	{
		get
		{
			return gameDirectory;
		}
		set
		{
			SetGameDirectory( value );
		}
	}

	private void SetGameDirectory( string path )
	{
		gameDirectory = path;
		txtGameDir.Text = path;
		Program.Settings.Set( "GameDirectory", path );
		Program.Settings.Save();
	}

	public OptionsForm()
	{
		InitializeComponent();
		ConfigureFolderLayout();
		FinishConstruction( this );
		Program.UpdateWindowSizeFromWorkingArea( this );
		Program.ThemeMngr.ApplyThemeOnForm( this );
	}

	private T HoldLayout<T>( T control ) where T : Control
	{
		control.SuspendLayout();
		constructionLayouts.Add( control );

		return control;
	}

	private void FinishConstruction( Control control )
	{
		foreach ( Control child in control.Controls )
			FinishConstruction( child );

		if ( constructionLayouts.Remove( control ) )
			control.ResumeLayout( true );
	}

	private void ConfigureFolderLayout()
	{
		pnlOptionsMain.SizeChanged += ( _, _ ) => ArrangeOptions();
		ArrangeOptions();
	}

	private void ArrangeOptions()
	{
		if ( optionsColumns == null )
			return;

		bool stacked = pnlOptionsMain.ClientSize.Width < 780 * DeviceDpi / 96;

		if ( optionsColumns.Stacked == stacked )
			return;

		optionsColumns.Stacked = stacked;
		optionsColumns.Parent?.PerformLayout();
		optionsColumns.PerformLayout();
	}

	private OptionsStackPanel Stack( string name ) => HoldLayout( new OptionsStackPanel { Name = name } );

	private static void AddRow( OptionsStackPanel panel, Control control )
	{
		control.Dock = DockStyle.None;
		control.Anchor = AnchorStyles.Top | AnchorStyles.Left;
		control.Margin = new Padding( 0, 4, 0, 4 );

		if ( control is Label || control is CheckBox )
			control.AutoSize = false;

		panel.Controls.Add( control );
	}

	private OptionsStackPanel Section( Func<string> title, params Control[] controls )
	{
		var section = Stack( title().Replace( " ", "" ) + "Section" );

		section.Padding = new Padding( 0, 8, 16, 12 );
		AddRow( section, Heading( title ) );

		foreach ( Control control in controls )
			AddRow( section, control );

		return section;
	}

	private Label Heading( Func<string> text ) => LiveText.Live(
		new Label { Font = new Font( Font, FontStyle.Bold ) },
		control => LiveText.BindText( control, text ) );

	private void AddLocation( OptionsStackPanel panel, Func<string> label, TextBox path, EventHandler browse )
	{
		var row = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Dock = DockStyle.Top, Margin = Padding.Empty };

		HoldLayout( row );

		var caption = LiveText.Live(
			new Label { AutoSize = true, Anchor = AnchorStyles.Left, Margin = Padding.Empty },
			control => LiveText.BindText( control, label ) );
		var host = new ModernTextBoxHost( path )
		{
			Name = path.Name + "Host",
			AutoSize = true,
			MinimumSize = new Size( 0, 38 ),
			Margin = new Padding( 0, 0, 10, 0 )
		};
		var button = ActionButton( () => UiText.T( "OptionsForm", "Browse…" ), browse );

		button.Name = path.Name + "Browse";
		button.Margin = Padding.Empty;
		button.Dock = DockStyle.Fill;

		bool? wasStacked = null;

		void Arrange()
		{
			bool stacked = row.Width < 620 * DeviceDpi / 96;

			if ( wasStacked == stacked )
				return;

			wasStacked = stacked;
			row.SuspendLayout();
			row.Controls.Clear();
			row.ColumnStyles.Clear();
			row.RowStyles.Clear();
			row.ColumnCount = stacked ? 2 : 3;
			row.RowCount = stacked ? 2 : 1;

			if ( !stacked )
				row.ColumnStyles.Add( new ColumnStyle( SizeType.Absolute, 180 * DeviceDpi / 96 ) );

			row.ColumnStyles.Add( new ColumnStyle( SizeType.Percent, 100 ) );
			row.ColumnStyles.Add( new ColumnStyle( SizeType.AutoSize ) );
			row.RowStyles.Add( new RowStyle( SizeType.AutoSize ) );

			if ( stacked )
				row.RowStyles.Add( new RowStyle( SizeType.AutoSize ) );

			caption.Margin = stacked ? new Padding( 0, 0, 0, 6 ) : Padding.Empty;
			row.Controls.Add( caption, 0, 0 );
			row.SetColumnSpan( caption, stacked ? 2 : 1 );
			row.Controls.Add( host, stacked ? 0 : 1, stacked ? 1 : 0 );
			row.Controls.Add( button, stacked ? 1 : 2, stacked ? 1 : 0 );
			row.ResumeLayout( true );
		}

		row.SizeChanged += ( _, _ ) => Arrange();
		row.DpiChangedAfterParent += ( _, _ ) => { wasStacked = null; Arrange(); };
		Arrange();
		AddRow( panel, row );
	}

	private void BrowseFolder( TextBox field, string description, string defaultPath )
	{
		using var picker = new FolderBrowserDialog
		{
			Description = description,
			UseDescriptionForTitle = true,
			SelectedPath = string.IsNullOrWhiteSpace( field.Text ) ? defaultPath : field.Text
		};

		if ( picker.ShowDialog( this ) == DialogResult.OK )
			field.Text = picker.SelectedPath;
	}

	private static ModernButton ActionButton( Func<string> text, EventHandler handler )
	{
		var button = new ModernButton
		{
			AutoSize = true,
			MinimumSize = new Size( 100, 38 ),
			Margin = new Padding( 6, 0, 0, 0 ),
			Padding = new Padding( 12, 5, 12, 5 )
		};

		button.Click += handler;
		LiveText.BindText( button, text );

		return button;
	}

	private void Options_Load( object sender, EventArgs e )
	{
		suppressAccentPreview = true;

		if ( !checkboxFadersAttached )
		{
			Program.ThemeMngr.AttachColorFaderToCheckBoxes( this, out colorFaders, 300, 700 );
			checkboxFadersAttached = true;
		}

		gameDirectory = Program.Settings.Get( "GameDirectory" );
		txtGameDir.Text = gameDirectory;
		txtModIoDirectory.Text = Program.Settings.Get( "ModIoDirectory" );
		txtGameSettingsDirectory.Text = Program.Settings.Get( "GameSettingsDirectory" );
		chkCollapseIdenticalConflicts.Checked = Program.Settings.Get<bool>( "CollapseIdenticalConflicts" );
		chkCollapseNotMergeable.Checked = Program.Settings.Get<bool>( "CollapseNotMergeable" );
		chkCollapseCustomLoadOrder.Checked = Program.Settings.Get<bool>( "CollapseCustomLoadOrder" );
		chkReviewEachMerge.Checked = Program.Settings.Get<bool>( "ReviewEachMerge" );
		chkMergeReport.Checked = Program.Settings.Get<bool>( "ReportAfterMerge" );
		chkPackReport.Checked = Program.Settings.Get<bool>( "ReportAfterPack" );
		chkAutoCreateScriptMerges.Checked = Program.Settings.Get<bool>( "AutoCreateScriptMerges" );
		chkAutoDeleteOldMerges.Checked = Program.Settings.Get<bool>( "AutoDeleteOldMerges" );
		chkAutoOverwriteOldMerges.Checked = Program.Settings.Get<bool>( "AutoOverwriteOldMerges" );
		chkAutoSkipKDiff3InfoDialogs.Checked = Program.Settings.Get<bool>( "AutoSkipKDiff3InfoDialogs" );
		chkAutoExit.Checked = Program.Settings.Get<bool>( "AutoExit" );
		chkCheckForApplicationUpdates.Checked = Program.Settings.Get<bool>( "CheckForApplicationUpdates" );
		cbColorTheme.SelectedIndex = Program.Settings.Get<int>( "ColorTheme" );
		numConflictListFontSize.Value = StyledTreeView.ConfiguredListFontSize();
		chkAccentColor.Checked = Program.Settings.Get<bool>( "AccentColor" );
		customAccentColor = ThemeManager.GetConfiguredCustomAccentColor();
		RefreshAccentColorSwatch();
		cbPrioTooltips.SelectedIndex = Program.Settings.Get<int>( AppSettings.PriorityToolTipsKey );
		SelectConfiguredLanguage();
		CaptureAppliedAccentState();
		suppressAccentPreview = false;
		btnOK.Select();
	}

	private bool Save()
	{
		LanguagePack language;

		try
		{
			language = UiText.PrepareLanguage( ( cbLanguage.SelectedItem as LanguageChoice )?.Code ?? "en" );
		}
		catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException )
		{
			MessageBox.Show(
				this,
				error.Message,
				UiText.T( "OptionsForm.Localization", "Could not apply language pack" ),
				MessageBoxButtons.OK,
				MessageBoxIcon.Warning );

			return false;
		}

		string settingsDirectory = txtGameSettingsDirectory.Text.Trim();

		if ( !string.IsNullOrWhiteSpace( settingsDirectory ) && !Directory.Exists( settingsDirectory ) )
		{
			MessageBox.Show(
				this,
				UiText.T( "OptionsForm", "Select an existing game settings folder, or leave the field blank for automatic discovery." ),
				UiText.T( "OptionsForm", "Game settings folder unavailable" ),
				MessageBoxButtons.OK,
				MessageBoxIcon.Warning );

			return false;
		}

		bool locationsChanged = settingsDirectory != Program.Settings.Get( "GameSettingsDirectory" )
			|| txtModIoDirectory.Text.Trim() != Program.Settings.Get( "ModIoDirectory" );
		bool updatePreferenceChanged = chkCheckForApplicationUpdates.Checked != Program.Settings.Get<bool>( "CheckForApplicationUpdates" );

		Program.Settings.Set( "GameSettingsDirectory", settingsDirectory );
		Program.Settings.Set( "GameDirectory", GameDirectory );
		Program.Settings.Set( "ModIoDirectory", txtModIoDirectory.Text.Trim() );
		Program.Settings.Set( "CollapseIdenticalConflicts", chkCollapseIdenticalConflicts.Checked );
		Program.Settings.Set( "CollapseNotMergeable", chkCollapseNotMergeable.Checked );
		Program.Settings.Set( "CollapseCustomLoadOrder", chkCollapseCustomLoadOrder.Checked );
		Program.Settings.Set( "ReviewEachMerge", chkReviewEachMerge.Checked );
		Program.Settings.Set( "ReportAfterMerge", chkMergeReport.Checked );
		Program.Settings.Set( "ReportAfterPack", chkPackReport.Checked );
		Program.Settings.Set( "AutoCreateScriptMerges", chkAutoCreateScriptMerges.Checked );
		Program.Settings.Set( "AutoDeleteOldMerges", chkAutoDeleteOldMerges.Checked );
		Program.Settings.Set( "AutoOverwriteOldMerges", chkAutoOverwriteOldMerges.Checked );
		Program.Settings.Set( "AutoSkipKDiff3InfoDialogs", chkAutoSkipKDiff3InfoDialogs.Checked );
		Program.Settings.Set( "AutoExit", chkAutoExit.Checked );
		Program.Settings.Set( "CheckForApplicationUpdates", chkCheckForApplicationUpdates.Checked );
		Program.Settings.Set( "ColorTheme", cbColorTheme.SelectedIndex );
		Program.Settings.Set( "ConflictListFontSize", numConflictListFontSize.Value.ToString( CultureInfo.InvariantCulture ) );
		Program.Settings.Set( "AccentColor", chkAccentColor.Checked );
		Program.Settings.Set( "CustomAccentColor", Colors.GetHexByColor( customAccentColor ) );
		Program.Settings.Set( AppSettings.PriorityToolTipsKey, cbPrioTooltips.SelectedIndex );
		Program.Settings.Set( "UiLanguage", ( cbLanguage.SelectedItem as LanguageChoice )?.Code ?? "en" );
		Program.Settings.Save();
		Program.ThemeMngr.ApplyConfiguredTheme();
		LiveText.ApplyLanguage( language );

		if ( updatePreferenceChanged )
			Program.MainFrm?.ApplicationUpdatePreferenceChanged();

		ReloadLanguages( language.Code );
		CaptureAppliedAccentState();
		Program.MainFrm?.ApplyConflictListSize();
		Program.MainFrm?.ResetConflictToolTip();
		Program.MainFrm?.RefreshNoConflictsAppearance();

		if ( locationsChanged )
		{
			Program.MainFrm?.GameSettingsLocationChanged();
		}

		return true;
	}

	private void BtnOK_Click( object sender, EventArgs e )
	{
		if ( Save() )
			base.DialogResult = DialogResult.OK;
	}

	private void BtnCancel_Click( object sender, EventArgs e )
	{
		RestoreAppliedAccentState();
		base.DialogResult = DialogResult.Cancel;
	}

	private void RestoreAppliedAccentState()
	{
		suppressAccentPreview = true;
		chkAccentColor.Checked = initialUseWindowsAccent;
		customAccentColor = initialCustomAccentColor;
		RefreshAccentColorSwatch();
		suppressAccentPreview = false;
		PreviewAccentColor();
	}

	private void BtnApply_Click( object sender, EventArgs e )
	{
		Save();
		base.DialogResult = DialogResult.None;
	}

	private void BtnSelectGameDirectory_Click( object sender, EventArgs e )
	{
		SelectGameDirectory();
	}

	internal void CheckSetGameDir()
	{
		GameDirectory = Program.Settings.Get( "GameDirectory" );

		if ( IsRemasteredGameDirectory( GameDirectory ) )
		{
			return;
		}

		GameDirectory = string.Empty;

		string SteamPath = string.Empty;
		string GogPath = string.Empty;

		try
		{
			SteamPath = Registry.GetValue(
				"HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\Steam App 292030",
				"InstallLocation",
				null )?.ToString() ?? string.Empty;
			GogPath = Registry.GetValue( "HKEY_LOCAL_MACHINE\\SOFTWARE\\WOW6432Node\\GOG.com\\Games\\1207664643", "path", null )?.ToString() ?? string.Empty;
		}
		catch ( Exception )
		{
		}

		string detectedPath = new[] { SteamPath, GogPath }.FirstOrDefault( IsRemasteredGameDirectory );

		if ( !string.IsNullOrWhiteSpace( detectedPath ) && ConfirmAutoGameDir( detectedPath ) )
		{
			GameDirectory = detectedPath;

			return;
		}

		SelectGameDirectory();
	}

	private bool ConfirmAutoGameDir( string gameDir )
	{
		using GameDirectoryConfirmationForm prompt = new GameDirectoryConfirmationForm( gameDir );

		Program.ThemeMngr.ApplyThemeOnForm( prompt );

		return prompt.ShowDialog( Program.MainFrm ) == DialogResult.Yes;
	}

	// Accept the Mods folder as a game selection by moving up one level.
	private void SelectGameDirectory()
	{
		string dirChoice = GetUserDirectoryChoice();

		if ( IsRemasteredGameDirectory( dirChoice ) )
		{
			if ( dirChoice.EndsWithIgnoreCase( "The Witcher 3 Wild Hunt\\Mods" ) )
			{
				dirChoice = Path.GetDirectoryName( dirChoice );
			}

			GameDirectory = dirChoice;
		}
		else if ( !IsRemasteredGameDirectory( GameDirectory ) )
		{
			GameDirectory = string.Empty;

			if ( !string.IsNullOrWhiteSpace( dirChoice ) )
			{
				Program.MainFrm.ShowMessage(
					UiText.T(
						"OptionsForm",
						"That folder is not a Witcher 3 Remastered installation. It must contain the 5.x DX12 executable under bin\\x64_dx12." ),
					UiText.T( "OptionsForm", "Invalid Remastered game directory" ),
					MessageBoxButtons.OK,
					MessageBoxIcon.Hand );
			}
		}
	}

	internal static bool IsRemasteredGameDirectory( string path )
	{
		if ( string.IsNullOrWhiteSpace( path ) || !Directory.Exists( path ) )
		{
			return false;
		}

		string executable = Path.Combine( path, "bin", "x64_dx12", "witcher3.exe" );

		if ( !File.Exists( executable ) )
		{
			return false;
		}

		try
		{
			return FileVersionInfo.GetVersionInfo( executable ).FileMajorPart >= 5;
		}
		catch
		{
			return false;
		}
	}

	private string GetUserDirectoryChoice()
	{
		using FolderBrowserDialog dlgSelectRoot = new FolderBrowserDialog
		{
			Description = UiText.T( "OptionsForm", "Select the Witcher 3 Remastered game directory" ),
			UseDescriptionForTitle = true
		};

		if ( Directory.Exists( Paths.GameDirectory ) )
		{
			dlgSelectRoot.SelectedPath = Paths.GameDirectory;
		}

		IWin32Window owner = Program.MainFrm != null ? Program.MainFrm : this;

		if ( DialogResult.OK != dlgSelectRoot.ShowDialog( owner ) )
		{
			return null;
		}

		return dlgSelectRoot.SelectedPath;
	}

	private void BtnDefaultOptions_Click( object sender, EventArgs e )
	{
		chkCollapseIdenticalConflicts.Checked = true;
		chkCollapseNotMergeable.Checked = false;
		chkCollapseCustomLoadOrder.Checked = false;
		chkReviewEachMerge.Checked = false;
		chkMergeReport.Checked = false;
		chkPackReport.Checked = false;
		chkAutoCreateScriptMerges.Checked = false;
		chkAutoDeleteOldMerges.Checked = true;
		chkAutoOverwriteOldMerges.Checked = true;
		chkAutoSkipKDiff3InfoDialogs.Checked = true;
		chkAutoExit.Checked = false;
		chkCheckForApplicationUpdates.Checked = true;
		cbColorTheme.SelectedIndex = 0;
		numConflictListFontSize.Value = StyledTreeView.DefaultListFontSize;
		chkAccentColor.Checked = true;
		customAccentColor = ThemeManager.DefaultAccentColor;
		RefreshAccentColorSwatch();
		cbPrioTooltips.SelectedIndex = 3;
		SelectLanguage( "en" );
		Save();
	}

	// Switching off the Windows accent also fires a change event. Preview the chosen color once, after both values are set.
	private void AccentColorSwatch_Click( object sender, EventArgs e )
	{
		using ColorDialog dialog = new ColorDialog()
		{
			AllowFullOpen = true,
			AnyColor = true,
			Color = swatchAccentColor.SelectedColor,
			FullOpen = true
		};

		if ( dialog.ShowDialog( this ) != DialogResult.OK )
		{
			return;
		}

		customAccentColor = Color.FromArgb( 255, dialog.Color.R, dialog.Color.G, dialog.Color.B );
		suppressAccentPreview = true;
		chkAccentColor.Checked = false;
		suppressAccentPreview = false;
		RefreshAccentColorSwatch();
		PreviewAccentColor();
	}

	private void AccentColorSource_CheckedChanged( object sender, EventArgs e )
	{
		RefreshAccentColorSwatch();

		if ( !suppressAccentPreview )
		{
			PreviewAccentColor();
		}
	}

	private void CaptureAppliedAccentState()
	{
		initialUseWindowsAccent = chkAccentColor.Checked;
		initialCustomAccentColor = customAccentColor;
	}

	private void PreviewAccentColor()
	{
		Color accentColor = chkAccentColor.Checked
			? Colors.GetAccentColorFromRegistry()
			: customAccentColor;

		Program.ThemeMngr.ApplyAccentColor( accentColor );
		Program.ThemeMngr.RefreshForms();
	}

	internal void StopColorFades()
	{
		if ( colorFaders == null )
			return;

		foreach ( ColorFader fader in colorFaders )
			fader.Stop();
	}

	private void RefreshAccentColorSwatch()
	{
		if ( swatchAccentColor == null )
		{
			return;
		}

		swatchAccentColor.SelectedColor = chkAccentColor.Checked
			? Colors.GetAccentColorFromRegistry()
			: customAccentColor;
		LiveText.BindAccessibleDescription( swatchAccentColor, () => chkAccentColor.Checked
			? UiText.T( "OptionsForm", "Current Windows accent color. Activate to choose a custom color." )
			: UiText.T( "OptionsForm", "Current custom accent color. Activate to choose another color." ) );
	}

	protected override void OnFormClosing( FormClosingEventArgs e )
	{
		if ( base.DialogResult != DialogResult.OK )
		{
			RestoreAppliedAccentState();
		}

		base.OnFormClosing( e );
	}

	protected override void Dispose( bool disposing )
	{
		if ( disposing && components != null )
		{
			components.Dispose();
		}

		base.Dispose( disposing );
	}

	private static void StyleChoice( ComboBox choice )
	{
		choice.DrawMode = DrawMode.OwnerDrawFixed;

		void SizeItem() => choice.ItemHeight = TextRenderer.MeasureText( "Ag", choice.Font ).Height + 6 * choice.DeviceDpi / 96;

		choice.FontChanged += ( _, _ ) => SizeItem();
		choice.DpiChangedAfterParent += ( _, _ ) => SizeItem();
		choice.DrawItem += ( _, e ) =>
		{
			var theme = ThemeManager.CurrentTheme;
			bool selected = ( e.State & DrawItemState.Selected ) != 0;
			using var background = new SolidBrush( selected ? theme.AcceptButtonBackColor : theme.InputBackColor );

			e.Graphics.FillRectangle( background, e.Bounds );

			string text = e.Index >= 0 ? choice.GetItemText( choice.Items[e.Index] ) : choice.Text;

			TextRenderer.DrawText( e.Graphics, text, choice.Font, e.Bounds, theme.ForeColor,
				TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix );
			e.DrawFocusRectangle();
		};
		SizeItem();
	}

	private void InitializeComponent()
	{
		HoldLayout(this);
		Font = new Font("Segoe UI", 10f);
		AutoScaleDimensions = new SizeF(96, 96);
		AutoScaleMode = AutoScaleMode.Dpi;
		ClientSize = new Size(1000, 800);
		MinimumSize = Size.Empty;
		FormBorderStyle = FormBorderStyle.Sizable;
		ShowInTaskbar = false;
		MinimizeBox = false;
		StartPosition = FormStartPosition.CenterParent;
		LiveText.BindText(this, () => UiText.T("OptionsForm", "Options — Script Merger Remastered"));
		Name = "OptionsForm";
		DoubleBuffered = true;
		Icon = Program.MainFrm?.Icon;

		CheckBox Check(string name, Func<string> text) => LiveText.Live(new CheckBox { Name = name, AutoSize = true }, control => LiveText.BindText(control, text));
		chkCollapseNotMergeable = Check("chkCollapseNotMergeable", () => UiText.T("OptionsForm", "Nonmergeable conflicts"));
		chkCollapseCustomLoadOrder = Check("chkCollapseCustomLoadOrder", () => UiText.T("OptionsForm", "Conflicts resolved by priority"));
		chkCollapseIdenticalConflicts = Check("chkCollapseIdenticalConflicts", () => UiText.T("OptionsForm", "Identical bundled conflicts"));
		chkReviewEachMerge = Check("chkReviewEachMerge", () => UiText.T("OptionsForm", "Review successful merges in KDiff3"));
		chkMergeReport = Check("chkMergeReport", () => UiText.T("OptionsForm", "Show merge report"));
		chkPackReport = Check("chkPackReport", () => UiText.T("OptionsForm", "Show bundle packing report"));
		chkAutoCreateScriptMerges = Check("chkAutoCreateScriptMerges", () => UiText.T("OptionsForm", "Create script merges automatically"));
		chkAutoDeleteOldMerges = Check("chkAutoDeleteOldMerges", () => UiText.T("OptionsForm", "Delete outdated merges automatically"));
		chkAutoOverwriteOldMerges = Check("chkAutoOverwriteOldMerges", () => UiText.T("OptionsForm", "Overwrite old merges automatically"));
		chkAutoSkipKDiff3InfoDialogs = Check("chkAutoSkipKDiff3InfoDialogs", () => UiText.T("OptionsForm", "Skip KDiff3 information dialogs"));
		chkAutoExit = Check("chkAutoExit", () => UiText.T("OptionsForm", "Exit after merging"));
		chkCheckForApplicationUpdates = Check("chkCheckForApplicationUpdates", () => UiText.T("OptionsForm", "Automatically check for app updates (weekly)"));
		chkAccentColor = Check("chkAccentColor", () => UiText.T("OptionsForm", "Use Windows accent"));
		chkAccentColor.CheckedChanged += AccentColorSource_CheckedChanged;
		cbPrioTooltips = new ComboBox { Name = "cbPrioTooltips", DropDownStyle = ComboBoxStyle.DropDownList };
		LiveText.BindItems(cbPrioTooltips, () => new object[] { UiText.T("OptionsForm", "Never"), UiText.T("OptionsForm", "Bundled conflicts"), UiText.T("OptionsForm", "Script conflicts"), UiText.T("OptionsForm", "Bundled and script conflicts") });
		cbColorTheme = new ComboBox { Name = "cbColorTheme", DropDownStyle = ComboBoxStyle.DropDownList };
		LiveText.BindItems(cbColorTheme, () => new object[] { UiText.T("OptionsForm", "Follow Windows"), UiText.T("OptionsForm", "Light"), UiText.T("OptionsForm", "Dark") });
		numConflictListFontSize = LiveText.Live(new NumericUpDown {
			Name = "numConflictListFontSize", AccessibleName = UiText.T("OptionsForm", "Conflict/merge list font size in points"),
			Minimum = StyledTreeView.MinimumListFontSize, Maximum = StyledTreeView.MaximumListFontSize,
			DecimalPlaces = 1, Increment = 0.5m, Value = StyledTreeView.DefaultListFontSize
		}, control => { LiveText.BindAccessibleName(control, () => UiText.T("OptionsForm", "Conflict/merge list font size in points")); });
		StyleChoice(cbPrioTooltips);
		StyleChoice(cbColorTheme);
		swatchAccentColor = new AccentColorSwatch { Name = "swatchAccentColor", Size = new Size(38, 34) };
		swatchAccentColor.Click += AccentColorSwatch_Click;
		var accent = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, Margin = Padding.Empty };
		HoldLayout(accent);
		accent.Controls.Add(swatchAccentColor);
		accent.Controls.Add(ActionButton(() => UiText.T("OptionsForm", "Choose…"), AccentColorSwatch_Click));

		txtGameDir = new TextBox { Name = "txtGameDir", ReadOnly = true };
		txtGameSettingsDirectory = LiveText.Live(new TextBox { Name = "txtGameSettingsDirectory",
			PlaceholderText = UiText.T("OptionsForm", "Automatic — Documents\\The Witcher 3") }, control => { LiveText.BindPlaceholderText(control, () => UiText.T("OptionsForm", "Automatic — Documents\\The Witcher 3")); });
		txtModIoDirectory = LiveText.Live(new TextBox { Name = "modIoDirectoryPath", PlaceholderText = UiText.T("OptionsForm", "Automatic — public Mod.io storage") }, control => { LiveText.BindPlaceholderText(control, () => UiText.T("OptionsForm", "Automatic — public Mod.io storage")); });
		var locations = Section(() => UiText.T("OptionsForm", "Locations"));
		AddLocation(locations, () => UiText.T("OptionsForm", "Game installation"), txtGameDir, BtnSelectGameDirectory_Click);
		AddLocation(locations, () => UiText.T("OptionsForm", "Game settings folder"), txtGameSettingsDirectory, (_, _) =>
			BrowseFolder(txtGameSettingsDirectory, UiText.T("OptionsForm", "Select the folder containing mods.settings and modioModsConfig.json"), GameSettingsLocation.DirectoryPath( Program.Settings )));
		AddLocation(locations, () => UiText.T("OptionsForm", "Mod.io storage"), txtModIoDirectory, (_, _) =>
			BrowseFolder(txtModIoDirectory, UiText.T("OptionsForm", "Select the shared mod.io folder or its Witcher 3 (8254) folder"),
				ModIoDiscovery.ResolveDefaultStorageRoot(Environment.GetEnvironmentVariable("PUBLIC"))));

		optionsLeft = Stack("optionsLeft");
		AddRow(optionsLeft, Section(() => UiText.T("OptionsForm", "Scanning and display"),
			Heading(() => UiText.T("OptionsForm", "Automatically Collapse")), chkCollapseNotMergeable,
			chkCollapseCustomLoadOrder, chkCollapseIdenticalConflicts, Heading(() => UiText.T("OptionsForm", "Priority Tooltips")), cbPrioTooltips));
		AddRow(optionsLeft, Section(() => UiText.T("OptionsForm", "Appearance"), LiveText.Live(new Label { Text = UiText.T("OptionsForm", "Theme") }, control => { LiveText.BindText(control, () => UiText.T("OptionsForm", "Theme")); }), cbColorTheme,
			chkAccentColor, LiveText.Live(new Label { Text = UiText.T("OptionsForm", "Accent color") }, control => { LiveText.BindText(control, () => UiText.T("OptionsForm", "Accent color")); }), accent,
			LiveText.Live(new Label { Text = UiText.T("OptionsForm", "Conflict/merge list font size (pt)") }, control => { LiveText.BindText(control, () => UiText.T("OptionsForm", "Conflict/merge list font size (pt)")); }), numConflictListFontSize));
		optionsRight = Stack("optionsRight");
		AddRow(optionsRight, Section(() => UiText.T("OptionsForm", "Merging"), chkReviewEachMerge,
			chkMergeReport, chkPackReport));
		AddRow(optionsRight, Section(() => UiText.T("OptionsForm", "Automation"), chkAutoCreateScriptMerges, chkAutoDeleteOldMerges,
			chkAutoOverwriteOldMerges, chkAutoSkipKDiff3InfoDialogs, chkAutoExit));
		AddRow(optionsRight, CreateLanguageSection());
		AddRow(optionsRight, Section(() => UiText.T("OptionsForm", "Updates"), chkCheckForApplicationUpdates));
		optionsColumns = new OptionsColumnsPanel { Name = "optionsGroups", Margin = Padding.Empty };
		optionsLeft.Dock = optionsRight.Dock = DockStyle.None;
		optionsColumns.Controls.AddRange(new Control[] { optionsLeft, optionsRight });
		HoldLayout(optionsColumns);
		var content = Stack("optionsContent");
		AddRow(content, locations);
		AddRow(content, optionsColumns);
		pnlOptionsMain = new Panel { Name = "pnlOptionsMain", Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(18, 8, 6, 0) };
		HoldLayout(pnlOptionsMain);
		pnlOptionsMain.Controls.Add(content);

		btnOK = ActionButton(() => UiText.T("OptionsForm", "OK"), BtnOK_Click);
		btnCancel = ActionButton(() => UiText.T("OptionsForm", "Cancel"), BtnCancel_Click);
		btnApply = ActionButton(() => UiText.T("OptionsForm", "Apply"), BtnApply_Click);
		btnDefaultOptions = ActionButton(() => UiText.T("OptionsForm", "Defaults"), BtnDefaultOptions_Click);
		var actions = new FlowLayoutPanel { Name = "optionsActions", AutoSize = true, Dock = DockStyle.Fill,
			FlowDirection = FlowDirection.RightToLeft, Margin = Padding.Empty };
		HoldLayout(actions);
		actions.Controls.AddRange(new Control[] { btnOK, btnCancel, btnApply });
		var footer = new TableLayoutPanel { Name = "optionsFooter", Dock = DockStyle.Bottom, AutoSize = true,
			ColumnCount = 2, Padding = new Padding(12), Margin = Padding.Empty };
		HoldLayout(footer);
		footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
		footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
		footer.Controls.Add(btnDefaultOptions, 0, 0);
		footer.Controls.Add(actions, 1, 0);
		Controls.Add(pnlOptionsMain);
		Controls.Add(footer);
		AcceptButton = btnOK;
		CancelButton = btnCancel;
		Load += Options_Load;
		AutoScaleDimensions = new SizeF(96, 96);
	}
}
