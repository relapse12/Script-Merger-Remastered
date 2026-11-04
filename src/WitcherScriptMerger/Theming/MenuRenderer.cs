using System;
using System.Drawing;
using System.Windows.Forms;

namespace WitcherScriptMerger.Theming;

public class MenuRenderer : ToolStripProfessionalRenderer
{
	private readonly Theme initialTheme;
	private Theme theme => ThemeManager.CurrentTheme ?? initialTheme;

	private readonly Font baseFont;

	private readonly int fontSize;

	public MenuRenderer( Theme theme, int fontSize = 11 )
	{
		initialTheme = theme;
		this.fontSize = fontSize;
		baseFont = new Font( "Segoe UI", fontSize );
	}

	protected override void InitializeItem( ToolStripItem item )
	{
		base.InitializeItem( item );

		if ( item is ToolStripMenuItem )
		{
			item.Padding = new Padding( fontSize, fontSize / 2, fontSize, fontSize / 2 );
			item.Font = baseFont;
		}
	}

	protected override void OnRenderMenuItemBackground( ToolStripItemRenderEventArgs e )
	{
		Rectangle rect = new Rectangle( Point.Empty, e.Item.Size );

		if ( e.Item.Selected )
		{
			e.Item.ForeColor = theme.MenuSelectedForeColor;

			using SolidBrush brush = new SolidBrush( theme.MenuSelectedBackColor );

			e.Graphics.FillRectangle( brush, rect );

			return;
		}

		e.Item.ForeColor = theme.MenuForeColor;
	}

	protected override void OnRenderItemText( ToolStripItemTextRenderEventArgs e )
	{
		Rectangle bounds = e.TextRectangle;
		bounds.Y = 0;
		bounds.Height = e.Item.Height;
		e.TextRectangle = bounds;
		e.TextFormat = ( e.TextFormat & ~TextFormatFlags.Bottom )
			| TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine;

		base.OnRenderItemText( e );
	}

	protected override void OnRenderArrow( ToolStripArrowRenderEventArgs e )
	{
		e.ArrowColor = !e.Item.Enabled
			? SystemColors.GrayText
			: theme.ThemeName == "Dark" ? Color.White : theme.MenuForeColor;

		base.OnRenderArrow( e );
	}

	protected override void OnRenderToolStripBackground( ToolStripRenderEventArgs e )
	{
		e.ToolStrip.BackColor = theme.MenuBackColor;
	}

	protected override void OnRenderToolStripBorder( ToolStripRenderEventArgs e )
	{
		using Pen pen = new Pen( theme.BorderColor, 1f );
		Rectangle border = e.AffectedBounds;

		border.Width = Math.Max( 0, border.Width - 1 );
		border.Height = Math.Max( 0, border.Height - 1 );
		e.Graphics.DrawRectangle( pen, border );
	}

	protected override void OnRenderSeparator( ToolStripSeparatorRenderEventArgs e )
	{
		if ( e.Item is ToolStripSeparator )
		{
			Rectangle bounds = new Rectangle( Point.Empty, e.Item.Size );
			using Pen pen = new Pen( theme.BorderColor, 1f );
			int y = bounds.Height / 2;

			e.Graphics.DrawLine( pen, bounds.Left + fontSize, y, bounds.Right - fontSize, y );
		}
	}
}
