using System;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using WitcherScriptMerger.Updates;

namespace WitcherScriptMerger.Forms;

internal partial class MainForm
{
	private readonly CancellationTokenSource applicationUpdateCancellation = new();
	private NexusApplicationClient applicationUpdateClient;
	private ApplicationUpdateChecker applicationUpdateChecker;
	private System.Windows.Forms.Timer applicationUpdateTimer;
	private ToolStripMenuItem applicationUpdateMenuItem;
	private ApplicationUpdateResult pendingApplicationUpdate;
	private ApplicationUpdateForm applicationUpdateReport;
	private bool applicationUpdateStartupPending;
	private bool applicationUpdateRunning;
	private bool applicationUpdateManualRequested;
	private bool applicationUpdateDisposed;

	private void InitializeApplicationUpdates()
	{
		applicationUpdateTimer = new System.Windows.Forms.Timer { Interval = 3000 };
		applicationUpdateTimer.Tick += ApplicationUpdateTimer_Tick;
		Shown += ( _, _ ) => ApplicationUpdatePreferenceChanged();
	}

	internal void ApplicationUpdatePreferenceChanged()
	{
		if ( applicationUpdateDisposed || !Visible )
			return;

		applicationUpdateStartupPending = Program.Settings.Get<bool>( "CheckForApplicationUpdates" );

		if ( !applicationUpdateStartupPending )
			pendingApplicationUpdate = null;

		applicationUpdateTimer.Interval = 3000;
		applicationUpdateTimer.Enabled = applicationUpdateStartupPending;
	}

	private ToolStripMenuItem CreateApplicationUpdateMenuItem()
	{
		applicationUpdateMenuItem = new ToolStripMenuItem() { Name = "mnCheckForUpdates" };
		RefreshApplicationUpdateCaption();
		applicationUpdateMenuItem.Click += ( _, _ ) =>
		{
			MainMenu?.Close( ToolStripDropDownCloseReason.CloseCalled );
			BeginInvoke( new Action( () => CheckApplicationUpdatesAsync( true ) ) );
		};

		return applicationUpdateMenuItem;
	}

	private void RefreshApplicationUpdateCaption()
	{
		if ( applicationUpdateMenuItem != null )
			LiveText.BindText( applicationUpdateMenuItem, () => applicationUpdateRunning
			? UiText.T( "ApplicationUpdates", "Checking for updates…" ) : UiText.T( "ApplicationUpdates", "Check for updates…" ) );
	}

	// Delay the startup notification until it can be shown without interrupting another dialog.
	private void ApplicationUpdateTimer_Tick( object sender, EventArgs e )
	{
		if ( applicationUpdateStartupPending )
		{
			applicationUpdateStartupPending = false;
			applicationUpdateTimer.Stop();
			CheckApplicationUpdatesAsync( false );
		}

		if ( pendingApplicationUpdate == null )
			return;

		if ( !Program.Settings.Get<bool>( "CheckForApplicationUpdates" ) )
		{
			pendingApplicationUpdate = null;
			applicationUpdateTimer.Stop();

			return;
		}

		if ( !Visible || ActiveForm != this || WindowState == FormWindowState.Minimized || pnlProgress.Visible
			|| OwnedForms.Any( form => form.Visible ) || MainMenu?.Visible == true )
			return;

		var result = pendingApplicationUpdate;

		pendingApplicationUpdate = null;
		applicationUpdateTimer.Stop();

		if ( applicationUpdateChecker.ShouldNotify( result ) )
		{
			ShowApplicationUpdateReport( result );
			applicationUpdateChecker.MarkNotified( result.Release );
		}
	}

	// A manual request can arrive during the startup check. Remember it in case that check decides it is too soon to run.
	private async void CheckApplicationUpdatesAsync( bool manual )
	{
		if ( applicationUpdateDisposed || !manual && !Program.Settings.Get<bool>( "CheckForApplicationUpdates" ) )
			return;

		if ( manual )
		{
			applicationUpdateStartupPending = false;
			pendingApplicationUpdate = null;
			applicationUpdateTimer.Stop();
		}

		applicationUpdateManualRequested |= manual;

		if ( applicationUpdateRunning )
			return;

		applicationUpdateRunning = true;
		RefreshApplicationUpdateCaption();

		try
		{
			applicationUpdateClient ??= new NexusApplicationClient( Application.ProductVersion );
			applicationUpdateChecker ??= new ApplicationUpdateChecker(
				applicationUpdateClient,
				new ApplicationUpdateStore( AppContext.BaseDirectory ),
				Application.ProductVersion );

			var result = await applicationUpdateChecker.CheckAsync( manual, applicationUpdateCancellation.Token );

			if ( result.Skipped && applicationUpdateManualRequested && !applicationUpdateDisposed )
				result = await applicationUpdateChecker.CheckAsync( true, applicationUpdateCancellation.Token );

			if ( applicationUpdateDisposed || IsDisposed || Disposing || result.Skipped )
				return;

			if ( applicationUpdateManualRequested )
			{
				pendingApplicationUpdate = null;
				ShowApplicationUpdateReport( result );

				if ( result.Error == null )
					applicationUpdateChecker.MarkNotified( result.Release );
			}
			else if ( Program.Settings.Get<bool>( "CheckForApplicationUpdates" ) && applicationUpdateChecker.ShouldNotify( result ) )
			{
				pendingApplicationUpdate = result;
				applicationUpdateTimer.Interval = 1000;
				applicationUpdateTimer.Start();
			}
		}
		catch ( OperationCanceledException ) { }
		catch ( Exception )
		{
			if ( !applicationUpdateDisposed && !IsDisposed && applicationUpdateManualRequested )
				ShowApplicationUpdateReport( new( null, UiText.T( "ApplicationUpdates", "The application update check could not be completed. Please try again later." ), null, null ) );
		}
		finally
		{
			applicationUpdateRunning = false;
			applicationUpdateManualRequested = false;

			if ( !applicationUpdateDisposed )
				RefreshApplicationUpdateCaption();
		}
	}

	private void ShowApplicationUpdateReport( ApplicationUpdateResult result )
	{
		applicationUpdateReport?.Close();
		applicationUpdateReport = new ApplicationUpdateForm( Application.ProductVersion, result, Icon );
		Program.ThemeMngr.ApplyThemeOnForm( applicationUpdateReport );
		applicationUpdateReport.Show( this );
	}

	private void DisposeApplicationUpdates()
	{
		if ( applicationUpdateDisposed )
			return;

		applicationUpdateDisposed = true;
		applicationUpdateCancellation.Cancel();
		applicationUpdateTimer?.Dispose();
		applicationUpdateClient?.Dispose();
		applicationUpdateCancellation.Dispose();
		applicationUpdateReport?.Dispose();
	}
}
