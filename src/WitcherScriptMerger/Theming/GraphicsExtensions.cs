using System.Drawing;
using System.Drawing.Drawing2D;

namespace WitcherScriptMerger.Theming;

public static class GraphicsExtensions
{
	public static void DrawRoundedRectangle( this Graphics graphics, Pen pen, Rectangle rectangle, int cornerRadius )
	{
		using GraphicsPath path = new GraphicsPath();

		path.AddArc( rectangle.X, rectangle.Y, cornerRadius, cornerRadius, 180f, 90f );
		path.AddArc( rectangle.X + rectangle.Width - cornerRadius, rectangle.Y, cornerRadius, cornerRadius, 270f, 90f );
		path.AddArc(
			rectangle.X + rectangle.Width - cornerRadius,
			rectangle.Y + rectangle.Height - cornerRadius,
			cornerRadius,
			cornerRadius,
			0f,
			90f );
		path.AddArc( rectangle.X, rectangle.Y + rectangle.Height - cornerRadius, cornerRadius, cornerRadius, 90f, 90f );
		path.CloseFigure();
		graphics.DrawPath( pen, path );
	}
}
