using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WitcherScriptMerger.Theming;

public static class ThemeUtils
{
	[DllImport( "User32.dll" )]
	private static extern int GetDpiForWindow( nint hWnd );

	[DllImport( "uxtheme.dll", CharSet = CharSet.Unicode )]
	private static extern int SetWindowTheme( nint hWnd, string pszSubAppName, string pszSubIdList );

	public static float GetDpiScalingFactor( nint handle )
	{
		int dpiForWindow = GetDpiForWindow( handle );

		return dpiForWindow > 0 ? (float)dpiForWindow / 96f : 1f;
	}

	public static int GetDpiAwarePixels( int pixels, Form form )
	{
		return form.LogicalToDeviceUnits( pixels );
	}

	// The caller must dispose the returned path.
	internal static GraphicsPath RoundedPath( Rectangle bounds, int radius )
	{
		int diameter = Math.Max( 2, Math.Min( radius * 2, Math.Min( bounds.Width, bounds.Height ) ) );
		GraphicsPath path = new GraphicsPath();

		path.AddArc( bounds.Left, bounds.Top, diameter, diameter, 180f, 90f );
		path.AddArc( bounds.Right - diameter, bounds.Top, diameter, diameter, 270f, 90f );
		path.AddArc( bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0f, 90f );
		path.AddArc( bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90f, 90f );
		path.CloseFigure();

		return path;
	}

	public static void ApplyExplorerTheme( Control control, bool dark )
	{
		if ( control == null )
		{
			return;
		}

		SetWindowTheme( control.Handle, dark ? "DarkMode_Explorer" : "Explorer", null );
		control.Invalidate();
	}

	internal static void ApplyTitleBarTheme( Form form, bool dark )
	{
		if ( !form.IsHandleCreated )
			return;

		int preference = dark ? 1 : 0;

		Program.DwmSetWindowAttribute( form.Handle, Program.DWMWINDOWATTRIBUTE.DWMWA_USE_IMMERSIVE_DARK_MODE, ref preference, 4u );
	}
}
