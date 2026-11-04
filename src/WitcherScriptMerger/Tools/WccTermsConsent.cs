using System;
using System.Drawing;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

namespace WitcherScriptMerger.Tools;

// Consent belongs to a particular copy of WCC and its terms. Ask again if either file changes.
internal static class WccTermsConsent
{
	private const int DocumentVersion = 1;

	private sealed class ConsentDocument
	{
		public int Version { get; set; }
		public string WccSha256 { get; set; }
		public string TermsSha256 { get; set; }
		public DateTimeOffset AcceptedUtc { get; set; }
	}

	internal static string ConsentPath => Path.Combine(
		AppContext.BaseDirectory,
		"Data",
		"wcc-terms-consent.json" );

	public static bool EnsureAccepted( string wccPath )
	{
		string executable = Path.GetFullPath( wccPath );
		string termsPath = Path.Combine( Path.GetDirectoryName( executable ), "wcc_eula.rtf" );

		if ( !File.Exists( termsPath ) )
		{
			Program.MainFrm.ShowError(
				UiText.Format(
					"WccTermsConsent",
					"The CD Projekt RED WCC terms file is missing:\n\n{0}\n\nSelect a complete Remastered-compatible WCC installation before packing merged XML files.",
					termsPath ),
				UiText.T( "WccTermsConsent", "Missing WCC Terms" ) );

			return false;
		}

		string wccHash;
		string termsHash;

		try
		{
			wccHash = HashFile( executable );
			termsHash = HashFile( termsPath );

			if ( HasMatchingConsent( ConsentPath, wccHash, termsHash ) )
				return true;
		}
		catch ( Exception exception ) when ( exception is IOException || exception is UnauthorizedAccessException || exception is JsonException )
		{
			Program.MainFrm.ShowError(
				UiText.Format(
					"WccTermsConsent",
					"Script Merger Remastered could not verify the WCC terms state.\n\n{0}",
					exception.Message ),
				UiText.T( "WccTermsConsent", "WCC Terms Error" ) );

			return false;
		}

		using WccTermsForm form = new( termsPath );

		if ( form.ShowDialog( Program.MainFrm ) != DialogResult.OK )
			return false;

		try
		{
			SaveConsent( ConsentPath, wccHash, termsHash );

			return true;
		}
		catch ( Exception exception ) when ( exception is IOException || exception is UnauthorizedAccessException )
		{
			Program.MainFrm.ShowError(
				UiText.Format(
					"WccTermsConsent",
					"Your acceptance could not be saved to Script Merger Remastered's portable data folder. No WCC operation was started.\n\n{0}",
					exception.Message ),
				UiText.T( "WccTermsConsent", "WCC Terms Error" ) );

			return false;
		}
	}

	internal static bool HasMatchingConsent( string path, string wccHash, string termsHash )
	{
		if ( !File.Exists( path ) )
			return false;

		ConsentDocument document = JsonSerializer.Deserialize<ConsentDocument>( File.ReadAllText( path ) );

		return document != null
			&& document.Version == DocumentVersion
			&& string.Equals( document.WccSha256, wccHash, StringComparison.OrdinalIgnoreCase )
			&& string.Equals( document.TermsSha256, termsHash, StringComparison.OrdinalIgnoreCase );
	}

	// Write the complete consent file before replacing the saved copy.
	internal static void SaveConsent( string path, string wccHash, string termsHash )
	{
		string fullPath = Path.GetFullPath( path );

		Directory.CreateDirectory( Path.GetDirectoryName( fullPath ) );

		ConsentDocument document = new()
		{
			Version = DocumentVersion,
			WccSha256 = wccHash,
			TermsSha256 = termsHash,
			AcceptedUtc = DateTimeOffset.UtcNow
		};
		string tempPath = fullPath + "." + Guid.NewGuid().ToString( "N" ) + ".tmp";

		try
		{
			File.WriteAllText(
				tempPath,
				JsonSerializer.Serialize( document, new JsonSerializerOptions { WriteIndented = true } ),
				new UTF8Encoding( encoderShouldEmitUTF8Identifier: false ) );
			File.Move( tempPath, fullPath, overwrite: true );
		}
		finally
		{
			if ( File.Exists( tempPath ) )
				File.Delete( tempPath );
		}
	}

	private static string HashFile( string path )
	{
		using FileStream stream = new( path, FileMode.Open, FileAccess.Read, FileShare.Read );

		return Convert.ToHexString( SHA256.HashData( stream ) );
	}
}

internal sealed class WccTermsForm : Form
{
	public WccTermsForm( string termsPath )
	{
		Text = UiText.T( "WccTermsConsent", "CD Projekt RED WCC Terms" );
		StartPosition = FormStartPosition.CenterParent;
		MinimumSize = new Size( 700, 520 );
		Size = new Size( 820, 680 );
		ShowIcon = false;
		ShowInTaskbar = false;

		Label introduction = new()
		{
			AutoSize = false,
			Dock = DockStyle.Top,
			Height = 62,
			Padding = new Padding( 12, 10, 12, 6 ),
			Text = UiText.Format(
				"WccTermsConsent",
				"Script Merger Remastered uses wcc_lite from The Witcher 3 REDkit to pack merged XML files. Accept the CD Projekt RED terms below once for this exact WCC and terms version." )
		};

		RichTextBox terms = new()
		{
			Dock = DockStyle.Fill,
			ReadOnly = true,
			DetectUrls = true,
			BackColor = SystemColors.Window,
			BorderStyle = BorderStyle.FixedSingle
		};

		terms.LoadFile( termsPath, RichTextBoxStreamType.RichText );

		CheckBox confirmation = new()
		{
			AutoSize = true,
			Text = UiText.T( "WccTermsConsent", "I have read and accept these terms." ),
			Margin = new Padding( 12, 12, 12, 8 )
		};
		Button accept = new()
		{
			Text = UiText.T( "WccTermsConsent", "Accept" ),
			DialogResult = DialogResult.OK,
			Enabled = false,
			AutoSize = true,
			Margin = new Padding( 6, 8, 12, 8 )
		};
		Button cancel = new()
		{
			Text = UiText.T( "WccTermsConsent", "Cancel" ),
			DialogResult = DialogResult.Cancel,
			AutoSize = true,
			Margin = new Padding( 6, 8, 6, 8 )
		};

		confirmation.CheckedChanged += ( _, _ ) => accept.Enabled = confirmation.Checked;

		FlowLayoutPanel actions = new()
		{
			Dock = DockStyle.Bottom,
			Height = 54,
			FlowDirection = FlowDirection.RightToLeft,
			WrapContents = false
		};

		actions.Controls.Add( accept );
		actions.Controls.Add( cancel );
		actions.Controls.Add( confirmation );

		Controls.Add( terms );
		Controls.Add( actions );
		Controls.Add( introduction );
		AcceptButton = accept;
		CancelButton = cancel;
	}
}
