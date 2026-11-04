using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using WitcherScriptMerger.Forms;
using WitcherScriptMerger.Theming;

namespace WitcherScriptMerger;

internal static class Program
{
	public enum DWMWINDOWATTRIBUTE : uint
	{
		DWMWA_NCRENDERING_ENABLED = 0u,
		DWMWA_NCRENDERING_POLICY = 1u,
		DWMWA_TRANSITIONS_FORCEDISABLED = 2u,
		DWMWA_ALLOW_NCPAINT = 3u,
		DWMWA_CAPTION_BUTTON_BOUNDS = 4u,
		DWMWA_NONCLIENT_RTL_LAYOUT = 5u,
		DWMWA_FORCE_ICONIC_REPRESENTATION = 6u,
		DWMWA_FLIP3D_POLICY = 7u,
		DWMWA_EXTENDED_FRAME_BOUNDS = 8u,
		DWMWA_HAS_ICONIC_BITMAP = 9u,
		DWMWA_DISALLOW_PEEK = 10u,
		DWMWA_EXCLUDED_FROM_PEEK = 11u,
		DWMWA_CLOAK = 12u,
		DWMWA_CLOAKED = 13u,
		DWMWA_FREEZE_REPRESENTATION = 14u,
		DWMWA_PASSIVE_UPDATE_MODE = 15u,
		DWMWA_USE_HOSTBACKDROPBRUSH = 16u,
		DWMWA_USE_IMMERSIVE_DARK_MODE = 20u,
		DWMWA_WINDOW_CORNER_PREFERENCE = 33u,
		DWMWA_BORDER_COLOR = 34u,
		DWMWA_CAPTION_COLOR = 35u,
		DWMWA_TEXT_COLOR = 36u,
		DWMWA_VISIBLE_FRAME_BORDER_THICKNESS = 37u,
		DWMWA_SYSTEMBACKDROP_TYPE = 38u,
		DWMWA_LAST = 39u
	}

	private static readonly Mutex mutex = new Mutex( initiallyOwned: true, "{B2B9C9EA-E9D0-47EB-875E-854FCC925F19}" );

	private static readonly string pipeName = "ScriptMergerRemastered_Pipe";

	private static readonly TimeSpan PipeTimeout = TimeSpan.FromSeconds( 5.0 );

	private static NamedPipeServerStream server;

	public static AppSettings Settings = LoadSettings();

	internal static readonly IEngineHost EngineHost = new ApplicationEngineHost();

	// Unreadable settings stop the application, so they are not replaced with defaults.
	private static AppSettings LoadSettings()
	{
		try
		{
			return new AppSettings();
		}
		catch ( Exception ex )
		{
			MessageBox.Show(
				UiText.Format(
					"Program",
					"Could not load portable settings beside the application.\n\nExisting settings have not been reset or overwritten.\n\n{0}",
					ex.Message ),
				UiText.T( "Program", "Script Merger Remastered Error" ),
				MessageBoxButtons.OK,
				MessageBoxIcon.Error );
			Environment.Exit( 1 );

			throw;
		}
	}

	public static MainForm MainFrm;

	public static OptionsForm OptionsFrm;

	public static MessageForm MessageFrm;

	public static ThemeManager ThemeMngr;

	[DllImport( "user32.dll" )]
	private static extern bool SetForegroundWindow( nint hWnd );

	// A second launch uses this pipe to bring the existing window forward.
	private static void ListenForSignal()
	{
		try
		{
			server = new NamedPipeServerStream( pipeName );

			while ( true )
			{
				server.WaitForConnection();

				if ( server.IsConnected )
				{
					nint windowHandle = Process.GetCurrentProcess().MainWindowHandle;

					if ( windowHandle != IntPtr.Zero )
					{
						SetForegroundWindow( windowHandle );
					}

					server.Disconnect();
				}
			}
		}
		catch ( IOException )
		{
		}
	}

	// If the existing window cannot be reached, continue startup in this process.
	private static void SendSignal()
	{
		MessageBox.Show(
			UiText.T(
				"Program",
				"Another instance of Script Merger Remastered is already running. Trying to switch to it now, but this won't work if it's minimized. If so, switch to it manually, or check Task Manager if an old WitcherScriptMerger.exe process is still running.\n\nIf you see this popup while no other instance is active, please report this as a bug!" ),
			UiText.T( "Program", "Another Script Merger Remastered instance is running" ),
			MessageBoxButtons.OK,
			MessageBoxIcon.Exclamation );

		using NamedPipeClientStream client = new NamedPipeClientStream( pipeName );

		try
		{
			client.Connect( (int)PipeTimeout.TotalMilliseconds );
			client.Close();
		}
		catch ( Exception )
		{
			RunApplication();
		}
	}

	internal static void StopListening()
	{
		server?.Close();
		server?.Dispose();
	}

	[DllImport( "dwmapi.dll", CharSet = CharSet.Unicode, PreserveSig = false )]
	public static extern void DwmSetWindowAttribute( nint hwnd, DWMWINDOWATTRIBUTE attribute, ref int pvAttribute, uint cbAttribute );

	[STAThread]
	private static void Main()
	{
		Application.EnableVisualStyles();
		Application.SetHighDpiMode( HighDpiMode.PerMonitorV2 );
		Application.SetCompatibleTextRenderingDefault( defaultValue: false );

		if ( mutex.WaitOne( TimeSpan.Zero, exitContext: true ) )
		{
			Thread thread = new Thread( ListenForSignal );

			thread.IsBackground = true;
			thread.Start();
			RunApplication();
			mutex.ReleaseMutex();
		}
		else
		{
			SendSignal();
		}
	}

	private static void RunApplication()
	{
		string languageError = UiText.Initialize( Settings.Get( "UiLanguage" ) );

		if ( languageError != null )
			MessageBox.Show(
				UiText.Format(
					"Program",
					"The selected language pack could not be loaded. English will be used; your language setting has not been reset.\n\n{0}",
					languageError ),
				UiText.T( "Program", "Language pack error" ),
				MessageBoxButtons.OK,
				MessageBoxIcon.Warning );

		ThemeMngr = new ThemeManager();

		using var themeLifetime = ThemeMngr;

		// Options is created below. The session reads its game folder only when an operation needs it.
		MainFrm = new MainForm( MergeSession.ForGame( Paths.ForSelectedGame() ) );
		ThemeMngr.WatchWindowsTheme( MainFrm );
		OptionsFrm = new OptionsForm();
		MessageFrm = new MessageForm();

		if ( !Paths.ValidateDependencyPaths() )
		{
			using DependencyForm dependencyForm = new DependencyForm();

			if ( dependencyForm.ShowDialog() != DialogResult.OK )
			{
				ShowLaunchFailure( UiText.T( "Program", "A dependency is missing." ) );

				return;
			}
		}

		Application.Run( MainFrm );
	}

	public static void UpdateWindowSizeFromWorkingArea( Form form )
	{
		Rectangle workingArea = Screen.FromControl( form ).WorkingArea;

		form.MinimumSize = new Size(
			Math.Min( form.MinimumSize.Width, workingArea.Width ),
			Math.Min( form.MinimumSize.Height, workingArea.Height ) );
		// Zero means unrestricted; do not turn it into a fixed-size window.
		form.MaximumSize = new Size( form.MaximumSize.Width == 0 ? 0 : Math.Max( form.MaximumSize.Width, form.MinimumSize.Width ),
			form.MaximumSize.Height == 0 ? 0 : Math.Max( form.MaximumSize.Height, form.MinimumSize.Height ) );
		form.Size = new Size( Math.Min( form.Width, workingArea.Width ), Math.Min( form.Height, workingArea.Height ) );

		if ( form.Visible && form.WindowState == FormWindowState.Normal )
			form.Location = new Point( Math.Clamp( form.Left, workingArea.Left, workingArea.Right - form.Width ),
				Math.Clamp( form.Top, workingArea.Top, workingArea.Bottom - form.Height ) );
	}

	private static void ShowLaunchFailure( string message )
	{
		MessageBox.Show(
			UiText.Format( "Program", "Launch failure: {0}", message ),
			UiText.T( "Program", "Script Merger Remastered Error" ),
			MessageBoxButtons.OK,
			MessageBoxIcon.Hand );
	}

	public static bool TryOpenFile( string path )
	{
		if ( !File.Exists( path ) )
		{
			MainFrm.ShowMessage( UiText.Format( "Program", "Can't find file: {0}", path ) );

			return false;
		}

		if ( path.EndsWithIgnoreCase( ".exe" ) )
		{
			Process.Start( new ProcessStartInfo
			{
				FileName = path,
				WorkingDirectory = Path.GetDirectoryName( path )
			} );
		}
		else
		{
			TryOpenFileLocation( path );
		}

		return true;
	}

	public static bool TryOpenFileLocation( string filePath )
	{
		if ( File.Exists( filePath ) )
		{
			Process.Start( "explorer.exe", "/select,\"" + filePath + "\"" );

			return true;
		}

		MainFrm.ShowMessage( UiText.Format( "Program", "Can't find file: {0}", filePath ) );

		return false;
	}

	public static bool TryOpenDirectory( string dirPath )
	{
		if ( !Directory.Exists( dirPath ) )
		{
			MainFrm.ShowMessage( UiText.Format( "Program", "Can't find directory: {0}", dirPath ) );

			return false;
		}

		Process.Start( "explorer.exe", dirPath );

		return true;
	}

}
