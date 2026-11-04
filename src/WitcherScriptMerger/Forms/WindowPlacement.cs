using System;
using System.Drawing;

namespace WitcherScriptMerger.Forms;

internal static class WindowPlacement
{
	internal static Rectangle FirstLaunchBounds( Rectangle workingArea )
	{
		double fraction = Math.Sqrt( 0.5 );
		int width = (int)Math.Round( workingArea.Width * fraction );
		int height = (int)Math.Round( workingArea.Height * fraction );

		return new Rectangle( workingArea.Left + ( workingArea.Width - width ) / 2,
			workingArea.Top + ( workingArea.Height - height ) / 2, width, height );
	}

	internal static Rectangle Constrain( Rectangle bounds, Rectangle workingArea )
	{
		int width = Math.Clamp( bounds.Width, 1, workingArea.Width );
		int height = Math.Clamp( bounds.Height, 1, workingArea.Height );

		return new Rectangle( Math.Clamp( bounds.Left, workingArea.Left, workingArea.Right - width ),
			Math.Clamp( bounds.Top, workingArea.Top, workingArea.Bottom - height ), width, height );
	}
}
