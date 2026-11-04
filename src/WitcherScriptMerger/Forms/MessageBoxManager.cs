using System;
using System.Runtime.InteropServices;
using System.Text;

namespace WitcherScriptMerger.Forms;

// Translates the buttons of standard message boxes, through a Windows hook on the dialog's creation.
internal class MessageBoxManager
{
	private delegate nint HookProc( int nCode, nint wParam, nint lParam );

	private delegate bool EnumChildProc( nint hWnd, nint lParam );

	public struct CWPRETSTRUCT
	{
		public nint lResult;

		public nint lParam;

		public nint wParam;

		public uint message;

		public nint hwnd;
	}

	private const int WH_CALLWNDPROCRET = 12;

	private const int WM_INITDIALOG = 272;

	private const int MBOK = 1;

	private const int MBCancel = 2;

	private const int MBAbort = 3;

	private const int MBRetry = 4;

	private const int MBIgnore = 5;

	private const int MBYes = 6;

	private const int MBNo = 7;

	private static readonly HookProc hookProc;

	private static readonly EnumChildProc enumProc;

	[ThreadStatic]
	private static nint hHook;

	[ThreadStatic]
	private static int nButton;

	public static string OK;

	public static string Cancel;

	public static string Abort;

	public static string Retry;

	public static string Ignore;

	public static string Yes;

	public static string No;

	[DllImport( "user32.dll" )]
	private static extern nint SendMessage( nint hWnd, int Msg, nint wParam, nint lParam );

	[DllImport( "user32.dll" )]
	private static extern nint SetWindowsHookEx( int idHook, HookProc lpfn, nint hInstance, int threadId );

	[DllImport( "user32.dll" )]
	private static extern int UnhookWindowsHookEx( nint idHook );

	[DllImport( "user32.dll" )]
	private static extern nint CallNextHookEx( nint idHook, int nCode, nint wParam, nint lParam );

	[DllImport( "user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextLengthW" )]
	private static extern int GetWindowTextLength( nint hWnd );

	[DllImport( "user32.dll" )]
	private static extern bool EnumChildWindows( nint hWndParent, EnumChildProc lpEnumFunc, nint lParam );

	[DllImport( "user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW" )]
	private static extern int GetClassName( nint hWnd, StringBuilder lpClassName, int nMaxCount );

	[DllImport( "user32.dll" )]
	private static extern int GetDlgCtrlID( nint hwndCtl );

	[DllImport( "user32.dll" )]
	private static extern nint GetDlgItem( nint hDlg, int nIDDlgItem );

	[DllImport( "user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SetWindowTextW" )]
	private static extern bool SetWindowText( nint hWnd, string lpString );

	static MessageBoxManager()
	{
		RefreshLanguage();
		hookProc = MessageBoxHookProc;
		enumProc = MessageBoxEnumProc;
		hHook = IntPtr.Zero;
	}

	internal static void RefreshLanguage()
	{
		OK = UiText.T( "MessageBoxManager", "&OK" );
		Cancel = UiText.T( "MessageBoxManager", "&Cancel" );
		Abort = UiText.T( "MessageBoxManager", "&Abort" );
		Retry = UiText.T( "MessageBoxManager", "&Retry" );
		Ignore = UiText.T( "MessageBoxManager", "&Ignore" );
		Yes = UiText.T( "MessageBoxManager", "&Yes" );
		No = UiText.T( "MessageBoxManager", "&No" );
	}

	public static void Register()
	{
		if ( hHook != IntPtr.Zero )
		{
			throw new NotSupportedException( UiText.T( "MessageBoxManager", "One hook per thread allowed." ) );
		}

		hHook = SetWindowsHookEx( 12, hookProc, IntPtr.Zero, AppDomain.GetCurrentThreadId() );
	}

	public static void Unregister()
	{
		if ( hHook != IntPtr.Zero )
		{
			UnhookWindowsHookEx( hHook );
			hHook = IntPtr.Zero;
		}
	}

	// WM_INITDIALOG (272) is the first chance to replace the standard button labels.
	private static nint MessageBoxHookProc( int nCode, nint wParam, nint lParam )
	{
		if ( nCode < 0 )
		{
			return CallNextHookEx( hHook, nCode, wParam, lParam );
		}

		CWPRETSTRUCT msg = (CWPRETSTRUCT)Marshal.PtrToStructure( lParam, typeof( CWPRETSTRUCT ) );
		nint hook = hHook;

		if ( msg.message == 272 )
		{
			GetWindowTextLength( msg.hwnd );

			StringBuilder className = new StringBuilder( 10 );

			GetClassName( msg.hwnd, className, className.Capacity );

			if ( className.ToString() == "#32770" )
			{
				nButton = 0;
				EnumChildWindows( msg.hwnd, enumProc, IntPtr.Zero );

				if ( nButton == 1 )
				{
					nint hButton = GetDlgItem( msg.hwnd, 2 );

					if ( hButton != IntPtr.Zero )
					{
						SetWindowText( hButton, OK );
					}
				}
			}
		}

		return CallNextHookEx( hook, nCode, wParam, lParam );
	}

	private static bool MessageBoxEnumProc( nint hWnd, nint lParam )
	{
		StringBuilder className = new StringBuilder( 10 );

		GetClassName( hWnd, className, className.Capacity );

		if ( className.ToString() == "Button" )
		{
			switch ( GetDlgCtrlID( hWnd ) )
			{
				case 1:
					SetWindowText( hWnd, OK );
					break;

				case 2:
					SetWindowText( hWnd, Cancel );
					break;

				case 3:
					SetWindowText( hWnd, Abort );
					break;

				case 4:
					SetWindowText( hWnd, Retry );
					break;

				case 5:
					SetWindowText( hWnd, Ignore );
					break;

				case 6:
					SetWindowText( hWnd, Yes );
					break;

				case 7:
					SetWindowText( hWnd, No );
					break;
			}

			nButton++;
		}

		return true;
	}
}
