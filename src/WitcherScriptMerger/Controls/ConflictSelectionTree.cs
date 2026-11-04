using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using WitcherScriptMerger.FileIndex;

namespace WitcherScriptMerger.Controls;

// Row selection is used by actions such as Ignore. The checkboxes separately choose what to merge.
internal class ConflictSelectionTree : StyledTreeView
{
	private readonly HashSet<TreeNode> selectedFiles = new HashSet<TreeNode>();
	private TreeNode selectionAnchor;

	[DesignerSerializationVisibility( DesignerSerializationVisibility.Hidden )]
	public bool FileSelectionEnabled { get; set; }
	public event Action<TreeNode, Point> FileContextMenuRequested;
	internal event Action NativeItemsReady;

	// WinForms treats checkbox double-clicks differently from single clicks. Translate the message so both clicks toggle it.
	protected override void WndProc( ref Message message )
	{
		if ( message.Msg == 0x203 && CheckBoxes )
		{
			long coordinates = message.LParam.ToInt64();
			var point = new Point( unchecked((short)coordinates), unchecked((short)( coordinates >> 16 )) );

			if ( ( HitTest( point ).Location & TreeViewHitTestLocations.StateImage ) != 0 )
				message.Msg = 0x201;
		}

		base.WndProc( ref message );
	}

	// Recreating the handle rebuilds the native items, so owners must set per-row checkbox visibility again.
	protected override void CreateHandle()
	{
		base.CreateHandle();
		NativeItemsReady?.Invoke();
	}

	public IReadOnlyList<TreeNode> SelectedFileNodes
	{
		get
		{
			selectedFiles.RemoveWhere( node => node.TreeView != this || !IsConflictFileNode( node ) );

			return selectedFiles.ToList();
		}
	}

	public static bool IsConflictFileNode( TreeNode node ) => node != null && ( node.GetMetadata()?.IsLinkedFile == true
		|| ( node.Level == 1 || node.GetMetadata()?.IsLinkedSymbol == true )
		&& ( node.Tag is ModFile || node.GetMetadata()?.ModFile != null || node.GetMetadata()?.MergeGroup != null ) );

	internal static ModFile Model( TreeNode node ) => node?.Tag as ModFile ?? node?.GetMetadata()?.ModFile;

	internal static IEnumerable<ModFile> ConflictsFor( TreeNode node ) => node.GetMetadata()?.MergeGroup != null || node.GetMetadata()?.IsLinkedFile == true
		? SMTree.Descendants( node ).Where( child => child.GetMetadata()?.IsLinkedSymbol == true ).Select( Model ).Where( file => file != null ).Distinct()
		: Model( node ) is { } file ? new[] { file } : Array.Empty<ModFile>();

	public ModFile[] SelectedConflicts => SelectedFileNodes
		.SelectMany( ConflictsFor )
		.DistinctBy( IgnoredConflictStore.KeyFor ).ToArray();

	public void ClearFileSelection()
	{
		selectedFiles.Clear();
		selectionAnchor = null;
		Invalidate();
	}

	private List<TreeNode> VisibleFiles() => Nodes.Cast<TreeNode>()
		.Where( category => category.IsExpanded )
		.SelectMany( category => SMTree.Descendants( category ) )
		.Where( node => AncestorsExpanded( node ) )
		.Where( IsConflictFileNode ).ToList();

	private static bool AncestorsExpanded( TreeNode node ) => node.Parent == null || node.Parent.IsExpanded && AncestorsExpanded( node.Parent );

	// Right-clicking a selected row keeps the other selected rows, so the menu acts on the whole selection.
	internal void SelectFileNode( TreeNode node, Keys modifiers, bool rightClick = false )
	{
		if ( !FileSelectionEnabled )
			return;

		if ( !IsConflictFileNode( node ) || node.TreeView != this )
		{
			if ( ( modifiers & ( Keys.Control | Keys.Shift ) ) == 0 )
				ClearFileSelection();

			return;
		}

		if ( rightClick && selectedFiles.Contains( node ) )
			return;

		bool control = !rightClick && ( modifiers & Keys.Control ) != 0;
		bool shift = !rightClick && ( modifiers & Keys.Shift ) != 0;
		var visible = shift ? VisibleFiles() : null;
		int anchorIndex = shift ? visible.IndexOf( selectionAnchor ) : -1;
		int nodeIndex = shift ? visible.IndexOf( node ) : -1;

		if ( anchorIndex >= 0 && nodeIndex >= 0 )
		{
			if ( !control )
				selectedFiles.Clear();

			for ( int i = Math.Min( anchorIndex, nodeIndex ); i <= Math.Max( anchorIndex, nodeIndex ); i++ )
				selectedFiles.Add( visible[i] );
		}
		else
		{
			if ( !control )
				selectedFiles.Clear();

			if ( !control || !selectedFiles.Remove( node ) )
				selectedFiles.Add( node );

			selectionAnchor = node;
		}

		Invalidate();
	}

	protected override void OnMouseDown( MouseEventArgs e )
	{
		if ( FileSelectionEnabled && ( e.Button == MouseButtons.Left || e.Button == MouseButtons.Right ) )
		{
			TreeViewHitTestInfo hit = HitTest( e.Location );

			if ( ( hit.Location & ( TreeViewHitTestLocations.PlusMinus | TreeViewHitTestLocations.StateImage ) ) == 0 )
				SelectFileNode( hit.Node, ModifierKeys, e.Button == MouseButtons.Right );
		}

		base.OnMouseDown( e );
	}

	protected override void OnKeyDown( KeyEventArgs e )
	{
		if ( FileSelectionEnabled && !IsEditing() && HandleSelectionKey( e.KeyData ) )
		{
			e.Handled = e.SuppressKeyPress = true;

			return;
		}

		base.OnKeyDown( e );
	}

	private bool IsEditing() => SelectedNode?.IsEditing == true;

	internal bool HandleSelectionKey( Keys keyData )
	{
		Keys key = keyData & Keys.KeyCode;
		Keys modifiers = keyData & Keys.Modifiers;

		if ( key == Keys.A && modifiers == Keys.Control )
		{
			selectedFiles.Clear();
			selectedFiles.UnionWith( VisibleFiles() );
			selectionAnchor = IsConflictFileNode( SelectedNode ) ? SelectedNode : VisibleFiles().FirstOrDefault();
			Invalidate();

			return true;
		}

		if ( key == Keys.Space && modifiers == Keys.Control )
		{
			SelectFileNode( SelectedNode, Keys.Control );

			return true;
		}

		if ( ( key == Keys.Apps && modifiers == Keys.None ) || ( key == Keys.F10 && modifiers == Keys.Shift ) )
		{
			if ( IsConflictFileNode( SelectedNode ) )
			{
				SelectFileNode( SelectedNode, Keys.None, rightClick: true );
				FileContextMenuRequested?.Invoke( SelectedNode, new Point( SelectedNode.Bounds.Left, SelectedNode.Bounds.Bottom ) );
			}

			return true;
		}

		if ( key is not ( Keys.Up or Keys.Down or Keys.Home or Keys.End ) || ( modifiers & Keys.Alt ) != 0 )
			return false;

		List<TreeNode> visible = VisibleFiles();

		if ( visible.Count == 0 )
			return false;

		TreeNode current = IsConflictFileNode( SelectedNode ) ? SelectedNode : SelectedNode?.Parent;
		int index = visible.IndexOf( current );
		int next = key switch
		{
			Keys.Home => 0,
			Keys.End => visible.Count - 1,
			Keys.Up => Math.Max( 0, index - 1 ),
			_ => Math.Min( visible.Count - 1, index + 1 )
		};
		TreeNode target = visible[next];

		if ( ( modifiers & Keys.Control ) == 0 || ( modifiers & Keys.Shift ) != 0 )
			SelectFileNode( target, modifiers );

		SelectedNode = target;
		target.EnsureVisible();

		return true;
	}

	protected override bool IsNodeSelected( DrawTreeNodeEventArgs e ) => FileSelectionEnabled
		? selectedFiles.Contains( e.Node ) : base.IsNodeSelected( e );

	// Collapsed rows leave the selection, so an action never reaches rows the user cannot see.
	protected override void OnAfterCollapse( TreeViewEventArgs e )
	{
		base.OnAfterCollapse( e );

		if ( !FileSelectionEnabled )
			return;

		var descendants = SMTree.Descendants( e.Node ).ToHashSet();

		selectedFiles.RemoveWhere( descendants.Contains );

		if ( selectionAnchor != null && descendants.Contains( selectionAnchor ) )
			selectionAnchor = null;

		Invalidate();
	}
}
