using System;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using WitcherScriptMerger.Controls;
using WitcherScriptMerger.FileIndex;
using WitcherScriptMerger.Inventory;
using WitcherScriptMerger.BlobMerging;

namespace WitcherScriptMerger.Forms;

internal partial class MainForm
{
	internal const int ConflictToolTipDelay = 600;
	private System.Windows.Forms.Timer conflictToolTipTimer;
	private TreeView conflictToolTipTree;
	private TreeNode pendingToolTipNode;
	private Point conflictToolTipLocation;

	// Hide the tooltip whenever its row moves or the pointer leaves the tree.
	private void InitializeConflictToolTips()
	{
		conflictToolTipTimer = new System.Windows.Forms.Timer( components ) { Interval = ConflictToolTipDelay };
		conflictToolTipTimer.Tick += ConflictToolTipTimer_Tick;

		foreach ( TreeView tree in new TreeView[] { treConflicts, treConflictSearchResults } )
		{
			tree.MouseLeave += ( _, _ ) => ResetConflictToolTip();
			tree.MouseDown += ( _, _ ) => ResetConflictToolTip();
			tree.MouseWheel += ( _, _ ) => ResetConflictToolTip();
			tree.KeyDown += ( _, _ ) => ResetConflictToolTip();
			tree.PreviewKeyDown += ( _, _ ) => ResetConflictToolTip();
			tree.BeforeSelect += ( _, _ ) => ResetConflictToolTip();
			tree.BeforeCollapse += ( _, _ ) => ResetConflictToolTip();
			tree.BeforeExpand += ( _, _ ) => ResetConflictToolTip();
			tree.Resize += ( _, _ ) => ResetConflictToolTip();
		}

		treConflictSearchResults.MouseMove += ( sender, args ) => ShowConflictsToolTip( (TreeView)sender, args );
		Deactivate += ( _, _ ) => ResetConflictToolTip();
	}

	private void QueueConflictToolTip( TreeView tree, MouseEventArgs args )
	{
		TreeNode node = ConflictToolTipNodeAt( tree, args.Location );

		if ( node == null || string.IsNullOrEmpty( node.ToolTipText )
			|| !AllowsConflictToolTip( node, Program.Settings.Get<int>( AppSettings.PriorityToolTipsKey ) ) )
		{
			ResetConflictToolTip();

			return;
		}

		conflictToolTipLocation = args.Location;

		if ( tree == conflictToolTipTree && ( node == pendingToolTipNode || node == _prevToolTipNode ) )
			return;

		ResetConflictToolTip();
		conflictToolTipTree = tree;
		pendingToolTipNode = node;
		conflictToolTipLocation = args.Location;
		conflictToolTipTimer.Start();
	}

	// The pointer may have moved during the delay. Check the row again before showing its tooltip.
	private void ConflictToolTipTimer_Tick( object sender, EventArgs args )
	{
		conflictToolTipTimer.Stop();

		TreeView tree = conflictToolTipTree;
		TreeNode node = pendingToolTipNode;

		if ( tree == null || tree.IsDisposed || !tree.Visible || !tree.Enabled || node?.TreeView != tree
			|| ConflictToolTipNodeAt( tree, conflictToolTipLocation ) != node || string.IsNullOrEmpty( node.ToolTipText )
			|| !AllowsConflictToolTip( node, Program.Settings.Get<int>( AppSettings.PriorityToolTipsKey ) ) )
		{
			ResetConflictToolTip();

			return;
		}

		int gap = tree.LogicalToDeviceUnits( 12 );
		Point screenPointer = tree.PointToScreen( conflictToolTipLocation );
		Rectangle workArea = Screen.FromPoint( screenPointer ).WorkingArea;
		int textWidth = Math.Max( 1, Math.Min( tree.LogicalToDeviceUnits( 620 ), workArea.Width / 2 - gap * 3 ) );
		string text = WrapConflictToolTipText( node.ToolTipText, SystemFonts.StatusFont, textWidth );
		Size size = TextRenderer.MeasureText( text, SystemFonts.StatusFont, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding )
			+ new Size( gap * 2, gap * 2 );
		Rectangle screenRow = tree.RectangleToScreen( node.Bounds );
		Point position = tree.PointToClient( ConflictToolTipPosition( screenRow, screenPointer, gap, size, workArea ) );

		treConflictsToolTip.Show( text, tree, position, 10000 );
		_prevToolTipNode = node;
		pendingToolTipNode = null;
	}

	internal static TreeNode ConflictToolTipNodeAt( TreeView tree, Point point )
	{
		TreeNode node = tree.GetNodeAt( point );

		return node != null && ( tree is StyledTreeView styled ? styled.IsPointOverText( node, point ) : node.Bounds.Contains( point ) ) ? node : null;
	}

	// Try below the row first, then above it, then beside the pointer to keep the tooltip on screen.
	internal static Point ConflictToolTipPosition( Rectangle row, Point pointer, int gap, Size size, Rectangle workArea )
	{
		int x = Math.Clamp( pointer.X + gap, workArea.Left, Math.Max( workArea.Left, workArea.Right - size.Width ) );
		int below = Math.Max( row.Bottom, pointer.Y ) + gap;

		if ( below + size.Height <= workArea.Bottom )
			return new Point( x, below );

		int above = Math.Min( row.Top, pointer.Y ) - gap - size.Height;

		if ( above >= workArea.Top )
			return new Point( x, above );

		int y = Math.Clamp( pointer.Y - size.Height / 2, workArea.Top, Math.Max( workArea.Top, workArea.Bottom - size.Height ) );

		if ( pointer.X + gap + size.Width <= workArea.Right )
			return new Point( pointer.X + gap, y );

		return new Point( pointer.X - gap - size.Width, y );
	}

	internal static string WrapConflictToolTipText( string text, Font font, int width )
	{
		var lines = new System.Collections.Generic.List<string>();

		int Measure( string value ) => TextRenderer.MeasureText( value, font, Size.Empty,
			TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine ).Width;

		foreach ( string original in text.ReplaceLineEndings( "\n" ).Split( '\n' ) )
		{
			string remaining = original;

			while ( remaining.Length > 0 && Measure( remaining ) > width )
			{
				int[] elements = StringInfo.ParseCombiningCharacters( remaining ).Append( remaining.Length ).ToArray();
				int low = 1, high = elements.Length - 1;

				while ( low < high )
				{
					int middle = ( low + high + 1 ) / 2;

					if ( Measure( remaining[..elements[middle]] ) <= width )
						low = middle;
					else
						high = middle - 1;
				}

				int split = elements[low];
				int space = remaining.LastIndexOf( ' ', Math.Max( 0, split - 1 ), split );

				if ( space > 0 )
				{
					lines.Add( remaining[..space] );
					remaining = remaining[( space + 1 )..];
				}
				else
				{
					lines.Add( remaining[..split] );
					remaining = remaining[split..];
				}
			}

			lines.Add( remaining );
		}

		return string.Join( "\n", lines );
	}

	internal void ResetConflictToolTip()
	{
		conflictToolTipTimer?.Stop();

		if ( conflictToolTipTree?.IsDisposed == false )
			treConflictsToolTip?.Hide( conflictToolTipTree );

		conflictToolTipTree = null;
		pendingToolTipNode = null;
		_prevToolTipNode = null;
	}

	// Row tooltips hold rendered text, so a language change must rebuild them.
	private void RefreshConflictToolTipText()
	{
		foreach ( TreeView tree in new TreeView[] { treConflicts, treConflictSearchResults } )
			foreach ( TreeNode file in SMTree.SymbolRows( tree ) )
			{
				ModFile conflict = file.GetMetadata()?.ModFile ?? file.Tag as ModFile;

				if ( conflict == null )
					continue;

				file.ToolTipText = conflict.GetSemanticConflictDescription();

				if ( session.LoadOrder == null )
					continue;

				string active = session.LoadOrder.HasResolvedConflict( conflict )
					? session.LoadOrder.GetTopPriorityEnabledMod( conflict ) : null;

				foreach ( TreeNode mod in file.Nodes )
				{
					FileHash source = mod.GetMetadata()?.FileHash ?? mod.Tag as FileHash;

					mod.ToolTipText = ConflictTree.DescribeConflictSource( conflict, source,
						session.LoadOrder.GetModLoadSettingByName( mod.Text ), mod.Text.EqualsIgnoreCase( active ),
						session.LoadOrder.IsModDisabledByName( mod.Text ) );
				}
			}

		foreach ( TreeView tree in new TreeView[] { treConflicts, treConflictSearchResults } )
			foreach ( TreeNode category in tree.Nodes )
				foreach ( TreeNode node in category.Nodes )
					if ( node.GetMetadata()?.MergeGroup is { } group )
					{
						node.Text = group.Label;
						node.ToolTipText = group.Description;

						foreach ( var section in SMTree.Descendants( node ).Where( child => child.GetMetadata()?.IsLinkedFile == true ) )
							section.ToolTipText = LinkedBlobMergeGroup.FileDescription;
					}

		RefreshLinkedLooseToolTips();
	}
}
