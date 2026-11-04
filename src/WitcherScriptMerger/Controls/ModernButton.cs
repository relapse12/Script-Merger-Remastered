using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using WitcherScriptMerger.Theming;

namespace WitcherScriptMerger.Controls;

internal enum ButtonGlyph
{
	None,
	Menu,
	Refresh
}

internal sealed class ModernButton : Button
{
	private bool isHovered;
	private bool isPressed;
	private int cornerRadius = 8;
	private ButtonGlyph glyph;

	[DefaultValue( 8 )]
	public int CornerRadius
	{
		get => cornerRadius;
		set
		{
			cornerRadius = Math.Max( 0, value );
			Invalidate();
		}
	}

	// Replaces the text color while the button is hovered or pressed. Empty keeps ForeColor.
	[DefaultValue( typeof( Color ), "" )]
	internal Color ActiveForeColor { get; set; } = Color.Empty;

	[DefaultValue( ButtonGlyph.None )]
	internal ButtonGlyph Glyph
	{
		get => glyph;
		set
		{
			glyph = value;
			Invalidate();
		}
	}

	private int GlyphWidth => glyph == ButtonGlyph.None ? 0 : LogicalToDeviceUnits( 15 );

	private int GlyphSpace => glyph == ButtonGlyph.None ? 0 : GlyphWidth + LogicalToDeviceUnits( 7 );

	public ModernButton()
	{
		SetStyle( ControlStyles.AllPaintingInWmPaint
			| ControlStyles.OptimizedDoubleBuffer
			| ControlStyles.ResizeRedraw
			| ControlStyles.UserPaint, true );
		AutoSize = false;
		UseVisualStyleBackColor = false;
	}

	public override Size GetPreferredSize( Size proposedSize )
	{
		string displayText = Text.Replace( "&", string.Empty, StringComparison.Ordinal );
		Size textSize = TextRenderer.MeasureText( displayText, Font, Size.Empty,
			TextFormatFlags.NoPadding | TextFormatFlags.SingleLine );
		int horizontalPadding = Math.Max( 20, (int)Math.Round( 24d * DeviceDpi / 96d ) );
		int verticalPadding = Math.Max( 10, (int)Math.Round( 12d * DeviceDpi / 96d ) );

		return new Size(
			Math.Max( MinimumSize.Width, textSize.Width + GlyphSpace + horizontalPadding ),
			Math.Max( MinimumSize.Height, textSize.Height + verticalPadding ) );
	}

	protected override void OnMouseEnter( EventArgs e )
	{
		isHovered = true;
		Invalidate();
		base.OnMouseEnter( e );
	}

	protected override void OnMouseLeave( EventArgs e )
	{
		isHovered = false;
		isPressed = false;
		Invalidate();
		base.OnMouseLeave( e );
	}

	protected override void OnMouseDown( MouseEventArgs mevent )
	{
		if ( mevent.Button == MouseButtons.Left )
		{
			isPressed = true;
			Invalidate();
		}

		base.OnMouseDown( mevent );
	}

	protected override void OnMouseUp( MouseEventArgs mevent )
	{
		isPressed = false;
		Invalidate();
		base.OnMouseUp( mevent );
	}

	protected override void OnKeyDown( KeyEventArgs kevent )
	{
		if ( kevent.KeyCode is Keys.Space or Keys.Enter )
		{
			isPressed = true;
			Invalidate();
		}

		base.OnKeyDown( kevent );
	}

	protected override void OnKeyUp( KeyEventArgs kevent )
	{
		isPressed = false;
		Invalidate();
		base.OnKeyUp( kevent );
	}

	protected override void OnGotFocus( EventArgs e )
	{
		Invalidate();
		base.OnGotFocus( e );
	}

	protected override void OnLostFocus( EventArgs e )
	{
		isPressed = false;
		Invalidate();
		base.OnLostFocus( e );
	}

	protected override void OnEnabledChanged( EventArgs e )
	{
		Invalidate();
		base.OnEnabledChanged( e );
	}

	protected override void OnPaint( PaintEventArgs pevent )
	{
		if ( ClientSize.Width <= 1 || ClientSize.Height <= 1 )
		{
			return;
		}

		Color surfaceColor = isPressed
			? FlatAppearance.MouseDownBackColor
			: isHovered
				? FlatAppearance.MouseOverBackColor
				: BackColor;
		Color parentColor = Parent?.BackColor ?? SystemColors.Control;
		Color textColor = ( isPressed || isHovered ) && !ActiveForeColor.IsEmpty ? ActiveForeColor : ForeColor;
		Color borderColor = FlatAppearance.BorderColor;

		if ( !Enabled )
		{
			surfaceColor = Blend( surfaceColor, parentColor, 0.58f );
			textColor = Blend( textColor, parentColor, 0.55f );
			borderColor = Blend( borderColor, parentColor, 0.55f );
		}

		Rectangle bounds = new Rectangle( 0, 0, ClientSize.Width - 1, ClientSize.Height - 1 );

		pevent.Graphics.Clear( parentColor );
		pevent.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

		using GraphicsPath path = ThemeUtils.RoundedPath( bounds, cornerRadius );
		using SolidBrush surfaceBrush = new SolidBrush( surfaceColor );

		pevent.Graphics.FillPath( surfaceBrush, path );

		if ( FlatAppearance.BorderSize > 0 )
		{
			using Pen borderPen = new Pen( borderColor, FlatAppearance.BorderSize );

			pevent.Graphics.DrawPath( borderPen, path );
		}

		if ( glyph == ButtonGlyph.None )
		{
			TextRenderer.DrawText( pevent.Graphics, Text, Font, bounds, textColor,
				TextFormatFlags.HorizontalCenter
				| TextFormatFlags.VerticalCenter
				| TextFormatFlags.SingleLine
				| TextFormatFlags.NoPadding );
		}
		else
		{
			int textWidth = TextRenderer.MeasureText( Text.Replace( "&", string.Empty, StringComparison.Ordinal ), Font, Size.Empty,
				TextFormatFlags.NoPadding | TextFormatFlags.SingleLine ).Width;
			int left = bounds.Left + Math.Max( 0, ( bounds.Width - GlyphSpace - textWidth ) / 2 );

			Rectangle glyphBounds = new Rectangle( left, bounds.Top + ( bounds.Height - GlyphWidth ) / 2, GlyphWidth, GlyphWidth );
			Rectangle textBounds = Rectangle.FromLTRB( left + GlyphSpace, bounds.Top, bounds.Right, bounds.Bottom );

			DrawGlyph( pevent.Graphics, glyphBounds, textColor );
			TextRenderer.DrawText( pevent.Graphics, Text, Font, textBounds, textColor,
				TextFormatFlags.Left
				| TextFormatFlags.VerticalCenter
				| TextFormatFlags.SingleLine
				| TextFormatFlags.NoPadding );
		}

		if ( Focused && ShowFocusCues )
		{
			Rectangle focusBounds = Rectangle.Inflate( bounds, -4, -4 );

			ControlPaint.DrawFocusRectangle( pevent.Graphics, focusBounds, textColor, surfaceColor );
		}
	}

	private void DrawGlyph( Graphics graphics, Rectangle area, Color color )
	{
		float scale = DeviceDpi / 96f;
		using Pen pen = new Pen( color, 1.6f * scale ) { StartCap = LineCap.Round, EndCap = LineCap.Round };

		if ( glyph == ButtonGlyph.Menu )
		{
			foreach ( float height in new[] { 0.2f, 0.5f, 0.8f } )
			{
				float y = area.Top + area.Height * height;

				graphics.DrawLine( pen, area.Left + scale, y, area.Right - scale, y );
			}

			return;
		}

		using AdjustableArrowCap arrow = new AdjustableArrowCap( 3.2f, 3.2f, isFilled: false );
		int inset = (int)Math.Ceiling( 1.5f * scale );

		pen.CustomEndCap = arrow;
		graphics.DrawArc( pen, Rectangle.Inflate( area, -inset, -inset ), 50f, 270f );
	}

	private static Color Blend( Color foreground, Color background, float backgroundAmount )
	{
		float foregroundAmount = 1f - backgroundAmount;

		return Color.FromArgb(
			(int)( foreground.R * foregroundAmount + background.R * backgroundAmount ),
			(int)( foreground.G * foregroundAmount + background.G * backgroundAmount ),
			(int)( foreground.B * foregroundAmount + background.B * backgroundAmount ) );
	}
}
