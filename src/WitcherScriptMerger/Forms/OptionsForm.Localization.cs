using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using WitcherScriptMerger.Controls;

namespace WitcherScriptMerger.Forms;

public partial class OptionsForm
{
	private ComboBox cbLanguage;

	private OptionsStackPanel CreateLanguageSection()
	{
		cbLanguage = new ComboBox { Name = "cbLanguage", DropDownStyle = ComboBoxStyle.DropDownList };
		StyleChoice( cbLanguage );
		ReloadLanguages();

		return Section( () => UiText.T( "OptionsForm.Localization", "Language" ),
			LiveText.Live(
				new Label { Text = UiText.T( "OptionsForm.Localization", "Interface language" ) },
				control => { LiveText.BindText( control, () => UiText.T( "OptionsForm.Localization", "Interface language" ) ); } ), cbLanguage,
			ActionButton( () => UiText.T( "OptionsForm.Localization", "Export translation template…" ), ExportLanguageTemplate ),
			ActionButton( () => UiText.T( "OptionsForm.Localization", "Import language pack…" ), ImportLanguagePack ),
			ActionButton( () => UiText.T( "OptionsForm.Localization", "Import translated CSV…" ), ImportTranslatedCsv ) );
	}

	private void ReloadLanguages( string selectedCode = null )
	{
		selectedCode ??= ( cbLanguage.SelectedItem as LanguageChoice )?.Code ?? Program.Settings.Get( "UiLanguage" );
		cbLanguage.Items.Clear();
		cbLanguage.Items.AddRange( UiText.AvailableLanguages().Cast<object>().ToArray() );
		SelectLanguage( selectedCode );
	}

	private void SelectConfiguredLanguage() => SelectLanguage( Program.Settings.Get( "UiLanguage" ) );

	private void SelectLanguage( string code )
	{
		if ( string.IsNullOrWhiteSpace( code ) )
			code = "en";

		LanguageChoice choice = cbLanguage.Items.Cast<LanguageChoice>().FirstOrDefault( item => item.Code.Equals( code, StringComparison.OrdinalIgnoreCase ) );

		if ( choice == null )
		{
			choice = new LanguageChoice( code, UiText.F( "OptionsForm.Localization", $"{code} (unavailable)" ) );
			cbLanguage.Items.Add( choice );
		}

		cbLanguage.SelectedItem = choice;
	}

	private void ExportLanguageTemplate( object sender, EventArgs e )
	{
		using var dialog = new SaveFileDialog
		{
			Title = UiText.T( "OptionsForm.Localization", "Export translation template" ),
			Filter = "CSV (*.csv)|*.csv|JSON (*.json)|*.json",
			DefaultExt = "csv",
			FileName = "SMR-language-template.csv",
			OverwritePrompt = true
		};

		if ( dialog.ShowDialog( this ) != DialogResult.OK )
			return;

		try
		{
			bool csv = !Path.GetExtension( dialog.FileName ).Equals( ".json", StringComparison.OrdinalIgnoreCase );

			if ( csv )
				UiText.ExportCsvTemplate( dialog.FileName );
			else
				UiText.ExportTemplate( dialog.FileName );

			MessageBox.Show( this, csv
				? UiText.T(
					"OptionsForm.Localization",
					"CSV template exported. Edit only the translation column, save as UTF-8 CSV, then choose Import translated CSV. Blank translations use English." )
				: UiText.T(
					"OptionsForm.Localization",
					"Template exported. Edit languageCode, languageName and the translation fields, then import the completed JSON file. Blank translations use English." ),
				UiText.T( "OptionsForm.Localization", "Translation template" ), MessageBoxButtons.OK, MessageBoxIcon.Information );
		}
		catch ( Exception error )
		{
			MessageBox.Show(
				this,
				error.Message,
				UiText.T( "OptionsForm.Localization", "Could not export template" ),
				MessageBoxButtons.OK,
				MessageBoxIcon.Warning );
		}
	}

	private void ImportLanguagePack( object sender, EventArgs e )
	{
		using var dialog = new OpenFileDialog
		{
			Title = UiText.T( "OptionsForm.Localization", "Import language pack" ),
			Filter = "JSON (*.json)|*.json",
			CheckFileExists = true
		};

		if ( dialog.ShowDialog( this ) != DialogResult.OK )
			return;

		try
		{
			InstallLanguagePack( UiText.Inspect( dialog.FileName ) );
		}
		catch ( Exception error )
		{
			MessageBox.Show(
				this,
				error.Message,
				UiText.T( "OptionsForm.Localization", "Could not import language pack" ),
				MessageBoxButtons.OK,
				MessageBoxIcon.Warning );
		}
	}

	private void ImportTranslatedCsv( object sender, EventArgs e )
	{
		using var picker = new OpenFileDialog
		{
			Title = UiText.T( "OptionsForm.Localization", "Import translated CSV" ),
			Filter = "CSV (*.csv)|*.csv",
			CheckFileExists = true
		};

		if ( picker.ShowDialog( this ) != DialogResult.OK )
			return;

		using var details = new LanguagePackDetailsForm();

		Program.ThemeMngr.ApplyThemeOnForm( details );

		if ( details.ShowDialog( this ) != DialogResult.OK )
			return;

		try
		{
			InstallLanguagePack( UiText.InspectCsv( picker.FileName, details.LanguageCode, details.LanguageName ) );
		}
		catch ( Exception error )
		{
			MessageBox.Show(
				this,
				error.Message,
				UiText.T( "OptionsForm.Localization", "Could not import language pack" ),
				MessageBoxButtons.OK,
				MessageBoxIcon.Warning );
		}
	}

	private void InstallLanguagePack( LanguagePack pack )
	{
		bool exists = File.Exists( Path.Combine( UiText.LanguageDirectory, pack.Code + ".json" ) );

		if ( exists && MessageBox.Show( this, UiText.F( "OptionsForm.Localization", $"Replace the installed {pack.Name} language pack?" ),
			UiText.T( "OptionsForm.Localization", "Replace language pack" ), MessageBoxButtons.YesNo, MessageBoxIcon.Question,
			MessageBoxDefaultButton.Button2 ) != DialogResult.Yes )
			return;

		pack = UiText.Install( pack, overwrite: exists );
		ReloadLanguages( pack.Code );
		MessageBox.Show( this, UiText.T( "OptionsForm.Localization", "Language pack imported. Choose Apply or OK to use it immediately." ),
			UiText.T( "OptionsForm.Localization", "Language pack imported" ), MessageBoxButtons.OK, MessageBoxIcon.Information );
	}
}
