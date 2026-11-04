using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using WitcherScriptMerger.Theming;

namespace WitcherScriptMerger.Controls;

// Adds a themed, rounded border around a standard TextBox.
internal sealed class ModernTextBoxHost : Panel
{
	private readonly TextBox textBox;

	private bool hovered;

	public ModernTextBoxHost( TextBox textBox )
	{
		this.textBox = textBox ?? throw new ArgumentNullException( nameof( textBox ) );
		SetStyle( ControlStyles.AllPaintingInWmPaint
			| ControlStyles.OptimizedDoubleBuffer
			| ControlStyles.ResizeRedraw
			| ControlStyles.UserPaint, true );
		Dock = DockStyle.Fill;
		Cursor = Cursors.IBeam;
		MinimumSize = new Size( 80, 0 );
		Padding = new Padding( 12, 6, 12, 6 );
		Size = new Size( 200, 36 );
		textBox.BorderStyle = BorderStyle.None;
		textBox.Dock = DockStyle.None;
		textBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
		textBox.Margin = new Padding( 0 );
		textBox.Enter += ( _, _ ) => Invalidate();
		textBox.Leave += ( _, _ ) => Invalidate();
		textBox.HandleCreated += ( _, _ ) => ApplyScrollBarTheme();
		textBox.MouseEnter += ( _, _ ) => SetHovered( value: true );
		textBox.MouseLeave += ( _, _ ) => SetHovered( ClientRectangle.Contains( PointToClient( System.Windows.Forms.Cursor.Position ) ) );
		Click += ( _, _ ) => textBox.Focus();
		Controls.Add( textBox );
		ApplyTheme( ThemeManager.CurrentTheme );
	}

	[DefaultValue( false )]
	internal bool ShowSearchGlyph { get; init; }

	public override Size GetPreferredSize( Size proposedSize ) =>
		new( Math.Max( MinimumSize.Width, proposedSize.Width > 0 ? proposedSize.Width : 200 ),
			Math.Max( MinimumSize.Height, textBox.PreferredHeight + Padding.Vertical ) );

	protected override void OnMouseEnter( EventArgs e )
	{
		base.OnMouseEnter( e );
		SetHovered( value: true );
	}

	protected override void OnMouseLeave( EventArgs e )
	{
		base.OnMouseLeave( e );
		SetHovered( ClientRectangle.Contains( PointToClient( System.Windows.Forms.Cursor.Position ) ) );
	}

	protected override void OnLayout( LayoutEventArgs levent )
	{
		base.OnLayout( levent );
		LayoutTextBox();
	}

	protected override void OnResize( EventArgs eventargs )
	{
		base.OnResize( eventargs );
		LayoutTextBox();
	}

	protected override void OnPaddingChanged( EventArgs e )
	{
		base.OnPaddingChanged( e );
		LayoutTextBox();
	}

	private void LayoutTextBox()
	{
		if ( textBox == null || ClientSize.Width <= 0 || ClientSize.Height <= 0 )
		{
			return;
		}

		int width = Math.Max( 1, ClientSize.Width - Padding.Horizontal );
		int availableHeight = Math.Max( 1, ClientSize.Height - Padding.Vertical );
		int preferredHeight = textBox.Multiline ? availableHeight : Math.Min( textBox.PreferredHeight, availableHeight );
		int top = Padding.Top + Math.Max( 0, ( availableHeight - preferredHeight ) / 2 );

		textBox.SetBounds( Padding.Left, top, width, preferredHeight );
	}

	internal void ApplyTheme( Theme theme )
	{
		if ( theme == null )
		{
			return;
		}

		BackColor = theme.InputBackColor;
		ForeColor = theme.ForeColor;
		textBox.BackColor = theme.InputBackColor;
		textBox.ForeColor = theme.ForeColor;
		textBox.BorderStyle = BorderStyle.None;
		ApplyScrollBarTheme();
		Invalidate();
	}

	private void ApplyScrollBarTheme()
	{
		if ( textBox.Multiline && textBox.IsHandleCreated )
			ThemeUtils.ApplyExplorerTheme( textBox, textBox.BackColor.GetBrightness() < 0.5f );
	}

	protected override void OnPaintBackground( PaintEventArgs e )
	{
		Color outsideColor = Parent?.BackColor ?? ThemeManager.CurrentTheme.BackColor;

		e.Graphics.Clear( outsideColor );

		if ( ClientSize.Width <= 1 || ClientSize.Height <= 1 )
		{
			return;
		}

		e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

		Color borderColor = textBox.Focused
			? ThemeManager.CurrentTheme.AcceptButtonBackColor
			: ( hovered
				? ControlPaint.Light( ThemeManager.CurrentTheme.InputBorderColor, 0.12f )
				: ShowSearchGlyph ? ThemeManager.CurrentTheme.CardBorderColor : ThemeManager.CurrentTheme.InputBorderColor );
		float dpiScale = Math.Max( 1f, DeviceDpi / 96f );
		float cornerRadius = 9f * dpiScale;
		float borderThickness = ( textBox.Focused ? 2f : 1.25f ) * dpiScale;
		RectangleF outerBounds = new RectangleF( 0f, 0f, ClientSize.Width, ClientSize.Height );
		using GraphicsPath outerPath = CreateRoundedPath( outerBounds, cornerRadius );
		using SolidBrush borderBrush = new SolidBrush( borderColor );

		e.Graphics.FillPath( borderBrush, outerPath );

		RectangleF innerBounds = new RectangleF(
			borderThickness,
			borderThickness,
			Math.Max( 1f, ClientSize.Width - borderThickness * 2f ),
			Math.Max( 1f, ClientSize.Height - borderThickness * 2f ) );
		using GraphicsPath innerPath = CreateRoundedPath( innerBounds, Math.Max( 2f, cornerRadius - borderThickness ) );
		using SolidBrush surfaceBrush = new SolidBrush( BackColor );

		e.Graphics.FillPath( surfaceBrush, innerPath );

		if ( ShowSearchGlyph )
			DrawSearchGlyph( e.Graphics, dpiScale );
	}

	private void DrawSearchGlyph( Graphics graphics, float dpiScale )
	{
		float radius = 4.75f * dpiScale;
		float centerX = Padding.Left * 0.5f;
		float centerY = ClientSize.Height * 0.5f - 1f * dpiScale;
		float handleStart = radius * 0.72f;
		float handleEnd = handleStart + 3.6f * dpiScale;
		using Pen pen = new Pen( ThemeManager.CurrentTheme.ForeDimmedColor, 1.5f * dpiScale ) { EndCap = LineCap.Round };

		graphics.DrawEllipse( pen, centerX - radius, centerY - radius, radius * 2f, radius * 2f );
		graphics.DrawLine( pen, centerX + handleStart, centerY + handleStart, centerX + handleEnd, centerY + handleEnd );
	}

	protected override void OnPaint( PaintEventArgs e )
	{
		base.OnPaint( e );
	}

	private void SetHovered( bool value )
	{
		if ( hovered == value )
		{
			return;
		}

		hovered = value;
		Invalidate();
	}

	private static GraphicsPath CreateRoundedPath( RectangleF bounds, float radius )
	{
		float diameter = Math.Max( 2f, Math.Min( radius * 2f, Math.Min( bounds.Width, bounds.Height ) ) );
		GraphicsPath path = new GraphicsPath();

		path.AddArc( bounds.Left, bounds.Top, diameter, diameter, 180f, 90f );
		path.AddArc( bounds.Right - diameter, bounds.Top, diameter, diameter, 270f, 90f );
		path.AddArc( bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0f, 90f );
		path.AddArc( bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90f, 90f );
		path.CloseFigure();

		return path;
	}
}
