using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using WitcherScriptMerger.Forms;

namespace WitcherScriptMerger.Theming;

// Restyle open windows when the Windows light/dark preference changes.
internal partial class ThemeManager : IDisposable
{
	private readonly Func<bool> readWindowsLightTheme;
	private readonly ConditionalWeakTable<Form, object> themedForms = new();
	private Form windowsThemeDispatcher;
	private int windowsRefreshQueued;
	private bool disposed;

	private static bool ReadWindowsLightTheme() => Registry.GetValue(
		"HKEY_CURRENT_USER\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize", "AppsUseLightTheme", 0 ) is int value && value == 1;

	internal void ApplyConfiguredTheme()
	{
		SetCurrentTheme();
		UpdateThemeWithAccentColor();
		RefreshForms();
	}

	internal void RefreshForms()
	{
		// Reusable dialogs may exist before their first display.
		var forms = themedForms.Select( pair => pair.Key ).Concat( Application.OpenForms.Cast<Form>() ).Distinct().ToArray();

		foreach ( Form form in forms )
		{
			if ( form.IsDisposed || form.Disposing )
				continue;

			ApplyThemeOnForm( form );

			if ( form is MainForm main )
				main.RefreshThemeStyles();

			form.Invalidate( invalidateChildren: true );
		}
	}

	internal void WatchWindowsTheme( Form dispatcher )
	{
		ArgumentNullException.ThrowIfNull( dispatcher );

		if ( disposed )
			throw new ObjectDisposedException( nameof( ThemeManager ) );

		if ( windowsThemeDispatcher != null )
			throw new InvalidOperationException( "Windows theme monitoring is already active." );

		windowsThemeDispatcher = dispatcher;
		dispatcher.HandleCreated += WindowsThemeHandleCreated;
		dispatcher.Disposed += WindowsThemeDispatcherDisposed;
		SystemEvents.UserPreferenceChanged += WindowsPreferenceChanged;
	}

	private void WindowsThemeHandleCreated( object sender, EventArgs e ) => ApplyConfiguredTheme();

	private void WindowsThemeDispatcherDisposed( object sender, EventArgs e ) => Dispose();

	// Windows can send several preference events together. Schedule one update on the UI thread.
	internal void WindowsPreferenceChanged( object sender, UserPreferenceChangedEventArgs e )
	{
		Form dispatcher = windowsThemeDispatcher;

		if ( disposed || dispatcher == null || dispatcher.IsDisposed || !dispatcher.IsHandleCreated )
			return;

		if ( Interlocked.Exchange( ref windowsRefreshQueued, 1 ) != 0 )
			return;

		try
		{
			dispatcher.BeginInvoke( new Action( () =>
			{
				Interlocked.Exchange( ref windowsRefreshQueued, 0 );

				if ( disposed || dispatcher.IsDisposed )
					return;

				Theme previous = CurrentTheme;
				var accent = previous.AcceptButtonBackColor;

				SetCurrentTheme();
				UpdateThemeWithAccentColor();

				if ( !ReferenceEquals( previous, CurrentTheme ) || accent != CurrentTheme.AcceptButtonBackColor )
					RefreshForms();
			} ) );
		}
		catch ( InvalidOperationException ) when ( dispatcher.IsDisposed || dispatcher.Disposing || !dispatcher.IsHandleCreated )
		{
			Interlocked.Exchange( ref windowsRefreshQueued, 0 );
		}
	}

	public void Dispose()
	{
		if ( disposed )
			return;

		disposed = true;

		if ( windowsThemeDispatcher == null )
			return;

		SystemEvents.UserPreferenceChanged -= WindowsPreferenceChanged;
		windowsThemeDispatcher.HandleCreated -= WindowsThemeHandleCreated;
		windowsThemeDispatcher.Disposed -= WindowsThemeDispatcherDisposed;
		windowsThemeDispatcher = null;
	}
}
