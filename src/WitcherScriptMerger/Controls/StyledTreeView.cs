using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using WitcherScriptMerger.FileIndex;
using WitcherScriptMerger.Inventory;
using WitcherScriptMerger.Theming;

namespace WitcherScriptMerger.Controls;

// Draw the labels here so font size and wrapping stay consistent across both panes.
// TextRenderer avoids creating a Windows font handle for every row.
internal class StyledTreeView : TreeView
{
	[DesignerSerializationVisibility( DesignerSerializationVisibility.Hidden )]
	internal Func<WitcherScriptMerger.LoadOrder.CustomLoadOrder> CurrentLoadOrder { get; set; } = () => null;

	private readonly Dictionary<(FontStyle Style, bool Heading), Font> displayFonts = new Dictionary<(FontStyle Style, bool Heading), Font>();
	private readonly Dictionary<FontStyle, Font> nodeStyleFonts = new Dictionary<FontStyle, Font>();
	private Font listFont;
	private Font pillFont;

	// This table must allow old rows to be collected after a tree rebuild.
	private static readonly ConditionalWeakTable<TreeNode, object> activeRows = new();

	internal const decimal DefaultListFontSize = 12m;
	internal const decimal MinimumListFontSize = 8m;
	internal const decimal MaximumListFontSize = 18m;

	internal static decimal ResolveListFontSize( string value, string legacyPreset )
	{
		if ( string.IsNullOrWhiteSpace( value ) )
			return legacyPreset switch
			{
				"0" => 9m,
				"1" => 10.5m,
				"2" => 13m,
				_ => DefaultListFontSize
			};

		return decimal.TryParse( value, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal points )
			? Math.Clamp( decimal.Round( points, 1 ), MinimumListFontSize, MaximumListFontSize )
			: DefaultListFontSize;
	}

	internal static decimal ConfiguredListFontSize() =>
		ResolveListFontSize( Program.Settings.Get( "ConflictListFontSize" ), Program.Settings.Get( "ConflictListSize" ) );

	internal void ApplyListSize()
	{
		float points = (float)ConfiguredListFontSize();

		if ( Font.Name != "Segoe UI" || Math.Abs( Font.SizeInPoints - points ) > .01f )
		{
			Font previous = listFont;

			listFont = new Font( "Segoe UI", points, FontStyle.Regular, GraphicsUnit.Point );
			Font = listFont;
			previous?.Dispose();
		}

		RefreshItemMetrics();
		Invalidate();
	}

	internal Font DisplayFont( TreeNode node )
	{
		bool heading = node.Level == 0;
		FontStyle style = node.NodeFont?.Style ?? FontStyle.Regular;

		if ( heading )
			style |= FontStyle.Bold;

		var key = (style, heading);

		if ( !displayFonts.TryGetValue( key, out Font font ) )
		{
			font = new Font( Font.FontFamily, Font.SizeInPoints + ( heading ? 1.5f : 0f ), style, GraphicsUnit.Point );
			displayFonts.Add( key, font );
		}

		return font;
	}

	internal Font NodeStyleFont( FontStyle style )
	{
		if ( style == FontStyle.Regular )
			return null;

		if ( !nodeStyleFonts.TryGetValue( style, out Font font ) )
		{
			// NodeFont carries the style; DisplayFont supplies the current list size.
			font = new Font( "Segoe UI", 11.75f, style, GraphicsUnit.Point );
			nodeStyleFonts.Add( style, font );
		}

		return font;
	}

	private void ClearDisplayFonts()
	{
		if ( displayFonts == null )
			return;

		foreach ( Font font in displayFonts.Values )
			font.Dispose();

		displayFonts.Clear();
		pillFont?.Dispose();
		pillFont = null;
	}

	protected override void Dispose( bool disposing )
	{
		base.Dispose( disposing );

		if ( !disposing )
			return;

		ClearDisplayFonts();

		foreach ( Font font in nodeStyleFonts.Values )
			font.Dispose();

		nodeStyleFonts.Clear();
		listFont?.Dispose();
	}

	private const int TvmSetExtendedStyle = 0x112C;

	private const int TvsExDoubleBuffer = 0x0004;

	protected override CreateParams CreateParams
	{
		get
		{
			var parameters = base.CreateParams;

			parameters.Style |= 0x8000; // TVS_NOHSCROLL: rows wrap within the viewport.

			return parameters;
		}
	}

	[DllImport( "user32.dll" )]
	private static extern IntPtr SendMessage( IntPtr window, int message, IntPtr parameter, IntPtr value );

	protected override void OnHandleCreated( EventArgs e )
	{
		base.OnHandleCreated( e );
		SendMessage( Handle, TvmSetExtendedStyle, (IntPtr)TvsExDoubleBuffer, (IntPtr)TvsExDoubleBuffer );
	}

	private const int ReflectedNotify = 0x204E; // WM_REFLECT + WM_NOTIFY
	private const int CustomDraw = -12; // NM_CUSTOMDRAW
	private const int PrePaint = 1; // CDDS_PREPAINT

	[StructLayout( LayoutKind.Sequential )]
	private struct NotifyHeader
	{
		public IntPtr Window;
		public IntPtr Id;
		public int Code;
	}

	[StructLayout( LayoutKind.Sequential )]
	private struct PaintNotice
	{
		public NotifyHeader Header;
		public int Stage;
	}

	// Clear native highlights before painting to avoid overlap around checkboxes.
	// Windows also paints a pressed-row highlight before selection changes.
	protected override void WndProc( ref Message m )
	{
		if ( m.Msg == ReflectedNotify && Marshal.PtrToStructure<NotifyHeader>( m.LParam ).Code == CustomDraw
			&& Marshal.PtrToStructure<PaintNotice>( m.LParam ).Stage == PrePaint )
		{
			this.ClearNativeHighlights();
		}

		base.WndProc( ref m );
	}

	protected override void OnDpiChangedAfterParent( System.EventArgs e )
	{
		base.OnDpiChangedAfterParent( e );
		RefreshItemMetrics();
		Invalidate();
	}

	protected override void OnFontChanged( System.EventArgs e )
	{
		base.OnFontChanged( e );
		ClearDisplayFonts();
		RefreshItemMetrics();
		Invalidate();
	}

	protected override void OnSizeChanged( EventArgs e )
	{
		base.OnSizeChanged( e );
		Invalidate();
	}

	public StyledTreeView()
	{
		DrawMode = TreeViewDrawMode.OwnerDrawText;
		DoubleBuffered = true;
		FullRowSelect = false;
		RefreshItemMetrics();
	}

	internal void RefreshItemMetrics()
	{
		if ( Font == null )
			return;

		// Native rows share one height, including space for wrapped labels.
		int height = Math.Max( LogicalToDeviceUnits( 28 ), Font.Height * 2 );

		if ( ItemHeight != height )
			ItemHeight = height;
	}

	protected virtual bool IsNodeSelected( DrawTreeNodeEventArgs e ) => e.Node == SelectedNode;


	// Painting and hit testing must agree on where the label is. FolderLength separates the dimmed path prefix.
	private readonly record struct RowLayout(
		string Text,
		Rectangle TextBounds,
		int FolderLength,
		Rectangle GlyphBounds,
		string Pill,
		Rectangle PillBounds,
		Color PillColor );

	private Font PillFont => pillFont ??=
		new Font( Font.FontFamily, Math.Max( 8f, Font.SizeInPoints - 2f ), FontStyle.Regular, GraphicsUnit.Point );

	private const TextFormatFlags LineFlags = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;

	internal static void SetActiveTag( TreeNode node, bool tagged )
	{
		activeRows.Remove( node );

		if ( tagged )
			activeRows.Add( node, null );
	}

	private static (string Text, Color Color) RowPill( TreeNode node )
	{
		if ( node.Level == 0 && node.Tag is ModFileCategory )
			return (node.Nodes.Count.ToString( "N0", CultureInfo.CurrentCulture ), ThemeManager.CurrentTheme.ForeDimmedColor);

		if ( node.Level == 0 )
			return (null, Color.Empty);

		if ( IsModIoNode( node ) )
			return ("Mod.io", ThemeManager.ModIoColor);

		return activeRows.TryGetValue( node, out _ )
			? (UiText.T( "StyledTreeView", "Active" ), ThemeManager.CurrentTheme.AcceptButtonBackColor)
			: (null, Color.Empty);
	}

	private static int RunWidth( string text, Font font ) =>
		text.Length == 0 ? 0 : TextRenderer.MeasureText( text, font, Size.Empty, LineFlags ).Width;

	private static int FolderCharacters( string line, int lineStart, int folderLength ) =>
		Math.Clamp( folderLength - lineStart, 0, line.Length );

	private static int LineWidth( string line, int lineStart, int folderLength, Font font )
	{
		int folder = FolderCharacters( line, lineStart, folderLength );

		return RunWidth( line[..folder], font ) + RunWidth( line[folder..], font );
	}

	private RowLayout LayoutRow( TreeNode node, Rectangle row, Font font )
	{
		int padding = LogicalToDeviceUnits( 8 );
		int left = row.X + LogicalToDeviceUnits( 4 );
		int textWidth = Math.Max( 1, ClientSize.Width - row.X - padding );
		Rectangle glyph = Rectangle.Empty;

		if ( node.Level == 0 && node.Tag is ModFileCategory )
		{
			int size = LogicalToDeviceUnits( 16 );
			int space = size + LogicalToDeviceUnits( 8 );

			glyph = new Rectangle( left, row.Y + ( row.Height - size ) / 2, size, size );
			left += space;
			textWidth = Math.Max( 1, textWidth - space );
		}

		// Hide the badge when space is tight so it cannot cover the label. Keep the Mod.io gear visible.
		var (pill, pillColor) = RowPill( node );
		Rectangle pillBounds = Rectangle.Empty;

		if ( pill != null )
		{
			Size size = TextRenderer.MeasureText( pill, PillFont, Size.Empty, LineFlags );
			int width = size.Width + LogicalToDeviceUnits( 16 );
			int height = size.Height + LogicalToDeviceUnits( 4 );
			int pillLeft = ClientSize.Width - padding - width;

			if ( left + RunWidth( node.Text, font ) + LogicalToDeviceUnits( 12 ) <= pillLeft )
				pillBounds = new Rectangle( pillLeft, row.Y + ( row.Height - height ) / 2, width, height );
			else
				pill = null;
		}

		string text = pill != null ? node.Text : RenderedNodeText( node );
		bool isPath = node.Level == 1 || node.GetMetadata()?.IsLinkedFile == true;
		int wrapWidth = Math.Max( 1, ClientSize.Width - Math.Max( 0, row.X ) - padding );
		string rendered = isPath ? WrapPathToTwoLines( text, font, wrapWidth ) : text;

		// State colors and hover fades apply to the whole label. Split the path color only for ordinary file rows.
		bool normalColor = RenderedNodeColor( node, ForeColor ).ToArgb() == ThemeManager.CurrentTheme.ForeBrightColor.ToArgb();
		int folderLength = isPath && normalColor ? text.LastIndexOfAny( new[] { '\\', '/' } ) + 1 : 0;

		return new RowLayout(
			rendered, new Rectangle( left, row.Y, textWidth, row.Height ), folderLength, glyph, pill, pillBounds, pillColor );
	}

	internal Rectangle[] RenderedTextBounds( TreeNode node )
	{
		if ( node?.TreeView != this || node.Bounds.IsEmpty )
			return Array.Empty<Rectangle>();

		Font font = DisplayFont( node );
		RowLayout layout = LayoutRow( node, node.Bounds, font );
		string[] lines = layout.Text.Split( '\n' );
		int height = Math.Min( layout.TextBounds.Height, font.Height * lines.Length );
		int top = layout.TextBounds.Top + Math.Max( 0, ( layout.TextBounds.Height - height ) / 2 );
		int advance = lines.Length == 1 ? 0 : Math.Max( 1, height - font.Height );
		var result = new Rectangle[lines.Length];
		int lineStart = 0;

		for ( int index = 0; index < lines.Length; index++ )
		{
			int width = LineWidth( lines[index], lineStart, layout.FolderLength, font );

			result[index] = Rectangle.Intersect( ClientRectangle, new Rectangle( layout.TextBounds.Left, top + index * advance,
				Math.Min( width, layout.TextBounds.Width ), font.Height ) );
			lineStart += lines[index].Length;
		}

		return result;
	}

	internal bool IsPointOverText(
		TreeNode node,
		Point point ) => Array.Exists( RenderedTextBounds( node ), bounds => bounds.Contains( point ) );

	protected override void OnDrawNode( DrawTreeNodeEventArgs e )
	{
		// Retained search trees can repaint while detached.
		if ( Parent == null || !Visible || e.Node == null || e.Bounds.Height == 0 )
			return;

		Font font = DisplayFont( e.Node );
		bool selected = IsNodeSelected( e ) && ( Focused || !HideSelection );
		Color accent = ThemeManager.CurrentTheme?.AcceptButtonBackColor ?? SystemColors.Highlight;
		Color background = selected ? Blend( BackColor, accent, 0.32f ) : BackColor;
		Color foreground = RenderedNodeColor( e.Node, ForeColor );

		background = Color.FromArgb( background.ToArgb() );
		foreground = Color.FromArgb( foreground.ToArgb() );

		// Explorer checkboxes end where the native label begins, and e.Bounds starts a pixel earlier, so the fill starts
		// exactly at the label.
		int rowLeft = System.Math.Max( 0, CheckBoxes ? e.Node.Bounds.X : e.Bounds.X );
		Rectangle bounds = new Rectangle( rowLeft, e.Bounds.Y,
			System.Math.Max( 1, ClientSize.Width - rowLeft ), e.Bounds.Height );
		RowLayout layout = LayoutRow( e.Node, e.Bounds, font );

		using ( var brush = new SolidBrush( background ) )
			e.Graphics.FillRectangle( brush, bounds );

		DrawRowText( e.Graphics, layout, font, foreground, background );

		if ( !layout.GlyphBounds.IsEmpty )
			DrawCategoryGlyph( e.Graphics, (ModFileCategory)e.Node.Tag, layout.GlyphBounds, accent );

		if ( !layout.PillBounds.IsEmpty )
			DrawPill( e.Graphics, layout, background );
	}

	private static void DrawRowText( Graphics graphics, RowLayout layout, Font font, Color foreground, Color background )
	{
		string text = layout.Text;
		Rectangle bounds = layout.TextBounds;
		int split = text.IndexOf( '\n' );

		if ( split < 0 )
		{
			DrawLine( graphics, text, 0, layout.FolderLength, font, bounds, foreground, background );

			return;
		}

		int lineHeight = font.Height;
		int combinedHeight = System.Math.Min( bounds.Height, lineHeight * 2 );
		int lineAdvance = System.Math.Max( 1, combinedHeight - lineHeight );
		int top = bounds.Top + System.Math.Max( 0, ( bounds.Height - combinedHeight ) / 2 );
		Rectangle firstLine = new Rectangle( bounds.Left, top, bounds.Width, lineHeight );
		Rectangle secondLine = new Rectangle( bounds.Left, top + lineAdvance, bounds.Width, lineHeight );

		DrawLine( graphics, text[..split], 0, layout.FolderLength, font, firstLine, foreground, background );
		DrawLine( graphics, text[( split + 1 )..], split, layout.FolderLength, font, secondLine, foreground, background );
	}

	private static void DrawLine(
		Graphics graphics,
		string line,
		int lineStart,
		int folderLength,
		Font font,
		Rectangle bounds,
		Color foreground,
		Color background )
	{
		const TextFormatFlags flags = LineFlags | TextFormatFlags.VerticalCenter;
		int folder = FolderCharacters( line, lineStart, folderLength );

		if ( folder > 0 )
		{
			TextRenderer.DrawText( graphics, line[..folder], font, bounds, ThemeManager.CurrentTheme.ForeDimmedColor, background, flags );
			bounds = Rectangle.FromLTRB( bounds.Left + RunWidth( line[..folder], font ), bounds.Top, bounds.Right, bounds.Bottom );
		}

		if ( folder < line.Length )
			TextRenderer.DrawText( graphics, line[folder..], font, bounds, foreground, background, flags );
	}

	private void DrawCategoryGlyph( Graphics graphics, ModFileCategory category, Rectangle area, Color color )
	{
		bool isScript = ReferenceEquals( category, Categories.Script )
			|| ReferenceEquals( category, Categories.PrecompiledScript )
			|| ReferenceEquals( category, MergeTree.PrecompiledMerges );
		float width = area.Width;
		PointF At( float x, float y ) => new PointF( area.Left + x * width, area.Top + y * width );

		SmoothingMode previous = graphics.SmoothingMode;

		graphics.SmoothingMode = SmoothingMode.AntiAlias;

		using Pen pen = new Pen( color, 1.5f * DeviceDpi / 96f )
		{
			LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round
		};

		if ( isScript )
		{
			graphics.DrawLines( pen, new[] { At( 0.28f, 0.22f ), At( 0.04f, 0.5f ), At( 0.28f, 0.78f ) } );
			graphics.DrawLines( pen, new[] { At( 0.72f, 0.22f ), At( 0.96f, 0.5f ), At( 0.72f, 0.78f ) } );
			graphics.DrawLine( pen, At( 0.58f, 0.12f ), At( 0.42f, 0.88f ) );
		}
		else if ( category.IsBundled )
		{
			PointF center = At( 0.5f, 0.5f );

			graphics.DrawPolygon( pen, new[]
			{
				At( 0.5f, 0.06f ), At( 0.92f, 0.28f ), At( 0.92f, 0.72f ), At( 0.5f, 0.94f ), At( 0.08f, 0.72f ), At( 0.08f, 0.28f )
			} );
			graphics.DrawLine( pen, At( 0.08f, 0.28f ), center );
			graphics.DrawLine( pen, At( 0.92f, 0.28f ), center );
			graphics.DrawLine( pen, At( 0.5f, 0.94f ), center );
		}
		else
		{
			graphics.DrawPolygon( pen, new[]
			{
				At( 0.18f, 0.06f ), At( 0.6f, 0.06f ), At( 0.84f, 0.3f ), At( 0.84f, 0.94f ), At( 0.18f, 0.94f )
			} );
			graphics.DrawLines( pen, new[] { At( 0.6f, 0.06f ), At( 0.6f, 0.3f ), At( 0.84f, 0.3f ) } );
		}

		graphics.SmoothingMode = previous;
	}

	private void DrawPill( Graphics graphics, RowLayout layout, Color rowBackground )
	{
		Color surface = Blend( rowBackground, layout.PillColor, 0.16f );
		SmoothingMode previous = graphics.SmoothingMode;

		graphics.SmoothingMode = SmoothingMode.AntiAlias;

		using ( GraphicsPath path = ThemeUtils.RoundedPath( layout.PillBounds, LogicalToDeviceUnits( 6 ) ) )
		using ( var brush = new SolidBrush( surface ) )
			graphics.FillPath( brush, path );

		graphics.SmoothingMode = previous;
		TextRenderer.DrawText( graphics, layout.Pill, PillFont, layout.PillBounds, layout.PillColor, surface,
			LineFlags | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter );
	}

	// Fits a path onto two lines by binary-searching the longest first line that fits.
	private static string WrapPathToTwoLines( string text, Font font, int maximumWidth )
	{
		if ( string.IsNullOrEmpty( text )
			|| TextRenderer.MeasureText( text, font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine ).Width <= maximumWidth )
			return text;

		int low = 1;
		int high = text.Length - 1;
		int bestSplit = 1;

		while ( low <= high )
		{
			int candidate = low + ( ( high - low ) / 2 );
			int width = TextRenderer.MeasureText( text[..candidate], font, Size.Empty,
				TextFormatFlags.NoPadding | TextFormatFlags.SingleLine ).Width;

			if ( width <= maximumWidth )
			{
				bestSplit = candidate;
				low = candidate + 1;
			}
			else
			{
				high = candidate - 1;
			}
		}

		return bestSplit > 0 ? text[..bestSplit] + "\n" + text[bestSplit..] : text;
	}

	internal static string RenderedNodeText( TreeNode node ) => IsModIoNode( node ) ? $"⚙  {node.Text}" : node.Text;

	internal static Color RenderedNodeColor( TreeNode node, Color fallback ) => IsModIoNode( node )
		? ThemeManager.ModIoColor : node.ForeColor.IsEmpty ? fallback : node.ForeColor;

	private static bool IsModIoNode( TreeNode node )
	{
		if ( node?.Tag is SMTree.NodeMetadata metadata && metadata.FileHash?.IsExternal == true )
			return true;

		if ( node?.Tag is FileHash fileHash && fileHash.IsExternal )
			return true;

		return ( node?.TreeView as StyledTreeView )?.CurrentLoadOrder()?.GetModLoadSettingByName( node?.Text )?.IsExternal == true;
	}

	private static Color Blend( Color background, Color foreground, float foregroundAmount )
	{
		foregroundAmount = System.Math.Clamp( foregroundAmount, 0f, 1f );

		float backgroundAmount = 1f - foregroundAmount;

		return Color.FromArgb(
			255,
			(int)System.Math.Round( background.R * backgroundAmount + foreground.R * foregroundAmount ),
			(int)System.Math.Round( background.G * backgroundAmount + foreground.G * foregroundAmount ),
			(int)System.Math.Round( background.B * backgroundAmount + foreground.B * foregroundAmount ) );
	}
}
