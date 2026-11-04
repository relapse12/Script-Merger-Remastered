using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using WitcherScriptMerger.Theming;

namespace WitcherScriptMerger.Controls;

internal sealed class AccentColorSwatch : Control
{
	private Color selectedColor = ThemeManager.DefaultAccentColor;
	private bool isHovered;

	[Browsable( false )]
	[DesignerSerializationVisibility( DesignerSerializationVisibility.Hidden )]
	public Color SelectedColor
	{
		get => selectedColor;
		set
		{
			if ( selectedColor == value )
			{
				return;
			}

			selectedColor = Color.FromArgb( 255, value.R, value.G, value.B );
			Invalidate();
		}
	}

	public AccentColorSwatch()
	{
		SetStyle( ControlStyles.AllPaintingInWmPaint
			| ControlStyles.OptimizedDoubleBuffer
			| ControlStyles.ResizeRedraw
			| ControlStyles.Selectable
			| ControlStyles.UserPaint, true );
		AccessibleName = UiText.T( "AccentColorSwatch", "Choose accent color" );
		AccessibleRole = AccessibleRole.PushButton;
		Cursor = Cursors.Hand;
		Size = new Size( 38, 34 );
		TabStop = true;
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
		Invalidate();
		base.OnMouseLeave( e );
	}

	protected override void OnGotFocus( EventArgs e )
	{
		Invalidate();
		base.OnGotFocus( e );
	}

	protected override void OnLostFocus( EventArgs e )
	{
		Invalidate();
		base.OnLostFocus( e );
	}

	protected override void OnKeyDown( KeyEventArgs e )
	{
		if ( e.KeyCode is Keys.Enter or Keys.Space )
		{
			e.Handled = true;
			OnClick( EventArgs.Empty );
		}

		base.OnKeyDown( e );
	}

	protected override void OnPaint( PaintEventArgs e )
	{
		base.OnPaint( e );

		Theme theme = ThemeManager.CurrentTheme;
		Color outside = Parent?.BackColor ?? theme?.BackColor ?? SystemColors.Control;
		Color border = Focused
			? selectedColor
			: isHovered
				? ControlPaint.Light( theme?.InputBorderColor ?? SystemColors.ControlDark, 0.14f )
				: theme?.InputBorderColor ?? SystemColors.ControlDark;
		Rectangle outerBounds = new Rectangle( 0, 0, Math.Max( 1, ClientSize.Width - 1 ), Math.Max( 1, ClientSize.Height - 1 ) );
		Rectangle colorBounds = Rectangle.Inflate( outerBounds, -4, -4 );

		e.Graphics.Clear( outside );
		e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

		using GraphicsPath outerPath = CreateRoundedPath( outerBounds, 7 );
		using GraphicsPath colorPath = CreateRoundedPath( colorBounds, 4 );
		using SolidBrush surfaceBrush = new SolidBrush( theme?.InputBackColor ?? SystemColors.Window );
		using SolidBrush colorBrush = new SolidBrush( selectedColor );
		using Pen borderPen = new Pen( border, Focused ? 2f : 1f );

		e.Graphics.FillPath( surfaceBrush, outerPath );
		e.Graphics.FillPath( colorBrush, colorPath );
		e.Graphics.DrawPath( borderPen, outerPath );
	}

	private static GraphicsPath CreateRoundedPath( Rectangle bounds, int radius )
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
}
