using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using WitcherScriptMerger.Theming;

namespace WitcherScriptMerger.Controls;

internal sealed class ModernProgressBar : Control
{
	private readonly System.Windows.Forms.Timer marqueeTimer;
	private int minimum;
	private int maximum = 100;
	private int currentValue;
	private int marqueeOffset;
	private ProgressBarStyle style = ProgressBarStyle.Continuous;

	public ModernProgressBar()
	{
		SetStyle( ControlStyles.AllPaintingInWmPaint
			| ControlStyles.OptimizedDoubleBuffer
			| ControlStyles.ResizeRedraw
			| ControlStyles.UserPaint, true );
		Size = new Size( 100, 14 );
		TabStop = false;
		marqueeTimer = new System.Windows.Forms.Timer
		{
			Interval = 24
		};
		marqueeTimer.Tick += ( _, _ ) => AdvanceMarquee();
	}

	[DefaultValue( 0 )]
	public int Minimum
	{
		get => minimum;
		set
		{
			minimum = Math.Min( value, maximum - 1 );
			currentValue = Math.Clamp( currentValue, minimum, maximum );
			Invalidate();
		}
	}

	[DefaultValue( 100 )]
	public int Maximum
	{
		get => maximum;
		set
		{
			maximum = Math.Max( value, minimum + 1 );
			currentValue = Math.Clamp( currentValue, minimum, maximum );
			Invalidate();
		}
	}

	[DefaultValue( 0 )]
	public int Value
	{
		get => currentValue;
		set
		{
			int nextValue = Math.Clamp( value, minimum, maximum );

			if ( currentValue == nextValue )
			{
				return;
			}

			currentValue = nextValue;
			Invalidate();
		}
	}

	[DefaultValue( ProgressBarStyle.Continuous )]
	public ProgressBarStyle Style
	{
		get => style;
		set
		{
			if ( style == value )
			{
				return;
			}

			style = value;
			marqueeOffset = -GetMarqueeWidth();
			UpdateMarqueeTimer();
			Invalidate();
		}
	}

	protected override void OnVisibleChanged( EventArgs e )
	{
		base.OnVisibleChanged( e );
		UpdateMarqueeTimer();
	}

	protected override void OnPaint( PaintEventArgs e )
	{
		base.OnPaint( e );

		if ( ClientSize.Width < 2 || ClientSize.Height < 2 )
		{
			return;
		}

		Theme theme = ThemeManager.CurrentTheme;
		Color trackColor = theme?.InputBackColor ?? Color.FromArgb( 15, 24, 33 );
		Color borderColor = theme?.BorderColor ?? Color.FromArgb( 42, 57, 70 );
		Color progressColor = theme?.AcceptButtonBackColor ?? Color.FromArgb( 10, 132, 255 );
		int trackHeight = LogicalToDeviceUnits( 10 );
		Rectangle trackBounds = new Rectangle(
			0,
			Math.Max( 0, ( ClientSize.Height - trackHeight ) / 2 ),
			ClientSize.Width - 1,
			Math.Min( trackHeight, ClientSize.Height - 1 ) );

		if ( trackBounds.Width <= 0 || trackBounds.Height <= 0 )
		{
			return;
		}

		e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

		using GraphicsPath trackPath = CreateRoundedPath( trackBounds, LogicalToDeviceUnits( 5 ) );
		using SolidBrush trackBrush = new SolidBrush( trackColor );

		e.Graphics.FillPath( trackBrush, trackPath );

		Region oldClip = e.Graphics.Clip;

		e.Graphics.SetClip( trackPath );

		using SolidBrush progressBrush = new SolidBrush( progressColor );

		if ( style == ProgressBarStyle.Marquee )
		{
			int marqueeWidth = GetMarqueeWidth();

			e.Graphics.FillRectangle( progressBrush, marqueeOffset, trackBounds.Top, marqueeWidth, trackBounds.Height );
		}
		else
		{
			double range = maximum - minimum;
			int fillWidth = (int)Math.Round( trackBounds.Width * ( ( currentValue - minimum ) / range ) );

			if ( fillWidth > 0 )
			{
				e.Graphics.FillRectangle( progressBrush, trackBounds.Left, trackBounds.Top, fillWidth, trackBounds.Height );
			}
		}

		e.Graphics.Clip = oldClip;
		oldClip.Dispose();

		using Pen borderPen = new Pen( borderColor );

		e.Graphics.DrawPath( borderPen, trackPath );
	}

	protected override void Dispose( bool disposing )
	{
		if ( disposing )
		{
			marqueeTimer.Dispose();
		}

		base.Dispose( disposing );
	}

	private void UpdateMarqueeTimer()
	{
		marqueeTimer.Enabled = Visible && style == ProgressBarStyle.Marquee;
	}

	private void AdvanceMarquee()
	{
		marqueeOffset += Math.Max( 5, ClientSize.Width / 120 );

		if ( marqueeOffset > ClientSize.Width )
		{
			marqueeOffset = -GetMarqueeWidth();
		}

		Invalidate();
	}

	private int GetMarqueeWidth()
	{
		return Math.Max( 48, ClientSize.Width / 5 );
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
