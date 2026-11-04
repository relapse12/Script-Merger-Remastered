using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using WitcherScriptMerger.Theming;

namespace WitcherScriptMerger.Controls;

internal enum ScanSummaryState
{
	None,
	Complete,
	Failed,
	NotScanned
}

// Keep the two sets of counts aligned with the conflict and merge panes.
internal sealed class ScanSummaryBar : Control
{
	// Translations can put the count anywhere in the label and use different digit separators.
	private static readonly Regex NumberPattern = new( @"\p{Nd}(?:[.,'   ]?\p{Nd})*", RegexOptions.CultureInvariant );

	private string[] left = { UiText.T( "ScanSummaryBar", "Not scanned" ) };
	private string[] right = Array.Empty<string>();
	private ScanSummaryState state;
	private Font emphasisFont;
	private Rectangle leftPane;
	private Rectangle rightPane;
	private readonly List<(string Text, Rectangle Bounds, bool IsState)> items = new();

	internal ScanSummaryBar()
	{
		Name = "scanSummary";
		Dock = DockStyle.Fill;
		AutoSize = true;
		Margin = Padding.Empty;
		Font = new Font( "Segoe UI", 9f );
		SetStyle( ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
			ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true );
	}

	// When scanState is set, the first scannedCounts entry supplies the status text and colored dot.
	internal void SetSummary(
		string[] conflictCounts,
		string[] scannedCounts,
		string details,
		ScanSummaryState scanState = ScanSummaryState.None )
	{
		left = conflictCounts;
		right = scannedCounts;
		state = scanState;
		AccessibleDescription = details;
		AccessibleName = string.Join( "; ", left.Concat( right ) );
		Reflow();
	}

	internal void SetPaneBounds( Rectangle leftBounds, Rectangle rightBounds )
	{
		if ( leftPane == leftBounds && rightPane == rightBounds )
			return;

		leftPane = leftBounds;
		rightPane = rightBounds;
		Reflow();
	}

	public override Size GetPreferredSize( Size proposedSize )
	{
		return new Size( proposedSize.Width, LayoutItems( Math.Max( 1, Width ) ) );
	}

	protected override void OnSizeChanged( EventArgs e )
	{
		base.OnSizeChanged( e );
		Reflow();
	}

	protected override void OnFontChanged( EventArgs e )
	{
		base.OnFontChanged( e );
		emphasisFont?.Dispose();
		emphasisFont = null;
		Reflow();
	}

	protected override void OnDpiChangedAfterParent( EventArgs e )
	{
		base.OnDpiChangedAfterParent( e );
		Reflow();
	}

	protected override void Dispose( bool disposing )
	{
		if ( disposing )
			emphasisFont?.Dispose();

		base.Dispose( disposing );
	}

	private Font EmphasisFont => emphasisFont ??= new Font( Font, FontStyle.Bold );

	private int Scale( int logicalPixels ) => Math.Max( 1, (int)Math.Round( logicalPixels * DeviceDpi / 96d ) );

	private static int TextWidth( string text, Font font )
	{
		if ( string.IsNullOrEmpty( text ) )
			return 0;

		return TextRenderer.MeasureText( text, font, Size.Empty,
			TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine ).Width;
	}

	private static (string Before, string Number, string After) SplitNumber( string text )
	{
		Match match = NumberPattern.Matches( text ).LastOrDefault();

		return match == null ? (text, string.Empty, string.Empty)
			: (text[..match.Index], match.Value, text[( match.Index + match.Length )..]);
	}

	private int CountWidth( string text )
	{
		var parts = SplitNumber( text );

		return TextWidth( parts.Before, Font ) + TextWidth( parts.Number, EmphasisFont ) + TextWidth( parts.After, Font );
	}

	private int StateWidth( string text ) => Scale( 14 ) + TextWidth( text, EmphasisFont );

	// Uses equal widths until the panes supply their bounds.
	private (Rectangle Left, Rectangle Right) Cards( int width )
	{
		if ( leftPane.Width > 0 && rightPane.Width > 0 )
			return (leftPane, rightPane);

		int gap = Scale( 3 );
		int middle = width / 2;

		return (Rectangle.FromLTRB( gap, 0, middle - gap, 1 ), Rectangle.FromLTRB( middle + gap, 0, width - gap, 1 ));
	}

	// Use the taller card's height for both sides so their bottom edges stay aligned.
	private int LayoutItems( int width )
	{
		items.Clear();

		int pad = Math.Max( 4, (int)Math.Round( 8 * DeviceDpi / 96d ) );
		int gap = pad * 2;
		int lineHeight = TextRenderer.MeasureText( "Ag", Font ).Height + pad;
		int top = pad / 2 + Scale( 2 );
		var cards = Cards( width );

		int AddGroup( IEnumerable<string> group, Rectangle card, int reservedWidth )
		{
			int leftEdge = card.Left + gap;
			int rightEdge = card.Right - gap;
			int x = leftEdge;
			int y = top;

			foreach ( string text in group )
			{
				int itemWidth = CountWidth( text );
				int limit = y == top ? rightEdge - reservedWidth : rightEdge;

				if ( x + itemWidth > limit && ( x > leftEdge || y == top && reservedWidth > 0 ) )
				{
					x = leftEdge;
					y += lineHeight;
				}

				int visibleWidth = Math.Max( 1, Math.Min( itemWidth, rightEdge - x ) );

				items.Add( (text, new Rectangle( x, y, visibleWidth, lineHeight ), false) );
				x += itemWidth + gap;
			}

			return y + lineHeight;
		}

		int leftBottom = AddGroup( left, cards.Left, 0 );
		bool hasState = state != ScanSummaryState.None && right.Length > 0;
		int stateWidth = hasState ? StateWidth( right[0] ) : 0;
		int rightBottom = AddGroup( hasState ? right.Skip( 1 ) : right, cards.Right, hasState ? stateWidth + gap : 0 );

		if ( hasState )
		{
			int stateRight = cards.Right.Right - gap;
			int stateLeft = Math.Max( cards.Right.Left + gap, stateRight - stateWidth );
			int visibleWidth = Math.Max( 1, Math.Min( stateWidth, stateRight - stateLeft ) );

			items.Add( (right[0], new Rectangle( stateLeft, top, visibleWidth, lineHeight ), true) );
		}

		return Math.Max( leftBottom, rightBottom ) + pad / 2 + Scale( 6 );
	}

	private void Reflow()
	{
		if ( left == null || items == null )
			return;

		int height = LayoutItems( Math.Max( 1, Width ) );

		if ( Height != height )
		{
			Height = height;
			Parent?.PerformLayout();
		}

		Invalidate();
	}

	protected override void OnPaint( PaintEventArgs e )
	{
		var theme = ThemeManager.CurrentTheme;
		var cards = Cards( Width );

		e.Graphics.Clear( theme.BackColor );
		DrawCard( e.Graphics, cards.Left, theme );
		DrawCard( e.Graphics, cards.Right, theme );

		foreach ( var item in items )
		{
			if ( item.IsState )
				DrawState( e.Graphics, item.Text, item.Bounds, theme );
			else
				DrawCount( e.Graphics, item.Text, item.Bounds, theme );
		}
	}

	private void DrawCard( Graphics graphics, Rectangle card, Theme theme )
	{
		Rectangle bounds = Rectangle.FromLTRB( card.Left, Scale( 1 ), card.Right, Height - Scale( 5 ) );

		if ( bounds.Width <= 1 || bounds.Height <= 1 )
			return;

		graphics.SmoothingMode = SmoothingMode.AntiAlias;

		using GraphicsPath path = ThemeUtils.RoundedPath( bounds, Scale( 12 ) );
		using SolidBrush surface = new SolidBrush( theme.BackgroundPanelBackColor );
		using Pen border = new Pen( theme.CardBorderColor, Math.Max( 1f, DeviceDpi / 96f ) );

		graphics.FillPath( surface, path );
		graphics.DrawPath( border, path );
	}

	private void DrawCount( Graphics graphics, string text, Rectangle bounds, Theme theme )
	{
		var parts = SplitNumber( text );
		int x = bounds.Left;

		x += DrawRun( graphics, parts.Before, Font, x, bounds, theme.ForeDimmedColor, theme );
		x += DrawRun( graphics, parts.Number, EmphasisFont, x, bounds, theme.ForeColor, theme );
		DrawRun( graphics, parts.After, Font, x, bounds, theme.ForeDimmedColor, theme );
	}

	private void DrawState( Graphics graphics, string text, Rectangle bounds, Theme theme )
	{
		int diameter = Scale( 8 );
		Rectangle dot = new Rectangle( bounds.Left, bounds.Top + ( bounds.Height - diameter ) / 2, diameter, diameter );
		Color color = state switch
		{
			ScanSummaryState.Complete => theme.SuccessColor,
			ScanSummaryState.Failed => theme.DangerOutlineColor,
			_ => theme.ForeDimmedColor
		};

		using ( var brush = new SolidBrush( color ) )
			graphics.FillEllipse( brush, dot );

		DrawRun( graphics, text, EmphasisFont, bounds.Left + Scale( 14 ), bounds, theme.ForeColor, theme );
	}

	private int DrawRun( Graphics graphics, string text, Font font, int x, Rectangle bounds, Color color, Theme theme )
	{
		int width = TextWidth( text, font );

		if ( width > 0 && x < bounds.Right )
		{
			Rectangle run = new Rectangle( x, bounds.Top, Math.Min( width, bounds.Right - x ), bounds.Height );

			TextRenderer.DrawText( graphics, text, font, run, color, theme.BackgroundPanelBackColor,
				TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter );
		}

		return width;
	}
}
