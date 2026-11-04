using System;
using System.Drawing;
using Microsoft.Win32;

namespace WitcherScriptMerger.Theming;

internal static class Colors
{
	// The registry stores red in the low byte. Fall back to Windows blue when the value cannot be read.
	internal static Color GetAccentColorFromRegistry()
	{
		try
		{
			using RegistryKey key = Registry.CurrentUser.OpenSubKey( "Software\\Microsoft\\Windows\\DWM" );

			if ( key != null )
			{
				int accentColor = (int)key.GetValue( "AccentColor" );

				return Color.FromArgb( 255, accentColor & 0xFF, ( accentColor & 0xFF00 ) >> 8, ( accentColor & 0xFF0000 ) >> 16 );
			}
		}
		catch ( Exception )
		{
		}

		return Color.FromArgb( 0, 120, 215 );
	}

	internal static Color GetColorByHex( string hex )
	{
		if ( !hex.StartsWith( "#" ) )
		{
			hex = "#" + hex;
		}

		return (Color)new ColorConverter().ConvertFromString( hex );
	}

	internal static string GetHexByColor( Color color )
	{
		return color.R.ToString( "X2" ) + color.G.ToString( "X2" ) + color.B.ToString( "X2" );
	}

	public static bool IsLightColor( Color color )
	{
		return 0.299 * (double)(int)color.R + 0.587 * (double)(int)color.G + 0.114 * (double)(int)color.B > 128.0;
	}
}
