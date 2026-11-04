using System;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using WitcherScriptMerger.Controls;
using WitcherScriptMerger.Updates;

namespace WitcherScriptMerger.Forms;

internal sealed class ApplicationUpdateForm : BaseForm
{
	internal ApplicationUpdateForm( string installedVersion, ApplicationUpdateResult result, Icon icon )
	{
		SuspendLayout();
		Name = "ApplicationUpdateForm";
		Text = UiText.T( "ApplicationUpdates", "Updates — Script Merger Remastered" );
		Font = new Font( "Segoe UI", 10f );
		ClientSize = new Size( 680, 440 );
		MinimumSize = new Size( 460, 320 );
		Icon = icon;
		ShowIcon = icon != null;
		ShowInTaskbar = false;
		StartPosition = FormStartPosition.CenterParent;
		MinimizeBox = false;
		MaximizeBox = false;

		var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding( 18 ) };

		layout.ColumnStyles.Add( new ColumnStyle( SizeType.Percent, 100 ) );
		layout.RowStyles.Add( new RowStyle( SizeType.AutoSize ) );
		layout.RowStyles.Add( new RowStyle( SizeType.AutoSize ) );
		layout.RowStyles.Add( new RowStyle( SizeType.Percent, 100 ) );
		layout.RowStyles.Add( new RowStyle( SizeType.AutoSize ) );

		string status = result.Error != null ? UiText.T( "ApplicationUpdates", "Could not check for updates" ) : ApplicationReleaseSelector.IsNewer( result.Release, installedVersion )
			? UiText.T( "ApplicationUpdates", "An application update is available" ) : UiText.T( "ApplicationUpdates", "You have the latest stable release" );

		layout.Controls.Add( new Label
		{
			Name = "lblUpdateStatus",
			Text = status,
			AutoSize = true,
			Dock = DockStyle.Fill,
			Font = new Font( Font, FontStyle.Bold ),
			Margin = new Padding( 0, 0, 0, 8 )
		}, 0, 0 );

		string versions = UiText.Format( "ApplicationUpdates", "Installed: {0}", installedVersion );

		if ( result.Release != null )
			versions += "    " + ( result.Error == null ? UiText.Format( "ApplicationUpdates", "Latest: {0}", result.Release.Version ) : UiText.Format( "ApplicationUpdates", "Last known release: {0}", result.Release.Version ) );

		layout.Controls.Add( new Label
		{
			Name = "lblUpdateVersions",
			Text = versions,
			Dock = DockStyle.Fill,
			AutoSize = true,
			Margin = new Padding( 0, 0, 0, 12 )
		}, 0, 1 );

		var details = new TextBox
		{
			Name = "txtApplicationChangelog",
			ReadOnly = true,
			Multiline = true,
			WordWrap = true,
			ScrollBars = ScrollBars.Vertical,
			Text = FormatDetails( result )
		};

		details.KeyDown += ( _, e ) => { if ( e.Control && e.KeyCode == Keys.A ) { details.SelectAll(); e.SuppressKeyPress = true; } };
		layout.Controls.Add( new ModernTextBoxHost( details )
		{
			Name = "applicationChangelogHost",
			Margin = new Padding( 0, 0, 0, 14 ),
			Padding = new Padding( 12, 10, 12, 10 )
		}, 0, 2 );

		var footer = new FlowLayoutPanel
		{
			Name = "updateActions",
			AutoSize = true,
			Dock = DockStyle.Fill,
			FlowDirection = FlowDirection.RightToLeft,
			WrapContents = true,
			Margin = Padding.Empty
		};
		var close = new ModernButton { Name = "btnClose", Text = UiText.T( "ApplicationUpdates", "Close" ), AutoSize = true, MinimumSize = new Size( 96, 36 ), DialogResult = DialogResult.Cancel };

		close.Click += ( _, _ ) => Close();

		var downloads = new ModernButton { Name = "btnNexusDownloads", Text = UiText.T( "ApplicationUpdates", "Nexus downloads" ), AutoSize = true, MinimumSize = new Size( 140, 36 ) };
		var changelog = new ModernButton { Name = "btnNexusChangelog", Text = UiText.T( "ApplicationUpdates", "View on Nexus" ), AutoSize = true, MinimumSize = new Size( 130, 36 ) };

		downloads.Click += ( _, _ ) => OpenNexus( ApplicationReleaseSelector.DownloadsUrl );
		changelog.Click += ( _, _ ) => OpenNexus( ApplicationReleaseSelector.ChangelogUrl );
		footer.Controls.AddRange( new Control[] { close, downloads, changelog } );
		layout.Controls.Add( footer, 0, 3 );
		Controls.Add( layout );
		AcceptButton = close;
		CancelButton = close;
		ResumeLayout( true );
	}

	internal static string FormatDetails( ApplicationUpdateResult result )
	{
		string text = "";

		if ( result.Error != null )
		{
			text = result.Error + Environment.NewLine + Environment.NewLine;

			if ( result.Release != null )
				text += UiText.T( "ApplicationUpdates", "The changelog below is cached; it does not confirm the current Nexus release." )
					+ ( result.LastSuccessUtc.HasValue ? " " + UiText.Format(
						"ApplicationUpdates",
						"Last successful check: {0}.",
						result.LastSuccessUtc.Value.ToLocalTime().ToString( "g" ) ) : "" )
					+ Environment.NewLine + Environment.NewLine;
		}

		if ( result.Warning != null )
			text += result.Warning + Environment.NewLine + Environment.NewLine;

		var changelog = result.Release?.Changelog ?? Array.Empty<ReleaseChangelog>();

		if ( changelog.Length == 0 || !changelog.Any( section => section.Notes?.Length > 0 ) )
			return text + UiText.T(
				"ApplicationUpdates",
				"No changelog was provided for this release. You can view the app's Nexus page for more information." );

		return text + UiText.T( "ApplicationUpdates", "Changelog" ) + Environment.NewLine + Environment.NewLine + string.Join(
			Environment.NewLine + Environment.NewLine,
			changelog.Select( section => UiText.Format( "ApplicationUpdates", "Version {0}", section.Version ) + Environment.NewLine
				+ ( section.Notes?.Length > 0 ? string.Join( Environment.NewLine, section.Notes.Select( note => "• " + note ) ) : UiText.T( "ApplicationUpdates", "No changelog provided." ) ) ) );
	}

	private void OpenNexus( string url )
	{
		try
		{
			Process.Start( new ProcessStartInfo( url ) { UseShellExecute = true } );
		}
		catch ( Exception error ) when ( error is System.ComponentModel.Win32Exception or InvalidOperationException )
		{
			MessageBox.Show(
				this,
				UiText.Format( "ApplicationUpdates", "The browser could not be opened.\n\n{0}", url ),
				UiText.T( "ApplicationUpdates", "Open Nexus Mods" ),
				MessageBoxButtons.OK,
				MessageBoxIcon.Warning );
		}
	}
}
