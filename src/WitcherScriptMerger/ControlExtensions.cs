using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using WitcherScriptMerger.Controls;

namespace WitcherScriptMerger;

internal static class ControlExtensions
{
	[StructLayout( LayoutKind.Sequential, CharSet = CharSet.Auto, Pack = 8 )]
	private struct TVITEM
	{
		public int mask;

		public nint hItem;

		public int state;

		public int stateMask;

		[MarshalAs( UnmanagedType.LPTStr )]
		public string lpszText;

		public int cchTextMax;

		public int iImage;

		public int iSelectedImage;

		public int cChildren;

		public nint lParam;
	}

	private const int TVIF_STATE = 8;

	private const int TVIS_STATEIMAGEMASK = 61440;

	private const int TVM_GETITEM = 4414;

	private const int TVM_SETITEM = 4415;

	private const int TVM_GETNEXTITEM = 4362;

	private const int TVM_GETITEMSTATE = 4391;

	private const int TVGN_DROPHILITE = 8;

	private const int TVGN_CARET = 9;

	private const int TVIS_NATIVE_HIGHLIGHTS = 10; // TVIS_SELECTED | TVIS_DROPHILITED

	public static IEnumerable<ToolStripItem> GetAvailableItems( this ContextMenuStrip menu )
	{
		return from ToolStripItem item in menu.Items
			   where item.Available
			   select item;
	}

	public static IEnumerable<TreeNode> GetTreeNodes( this TreeNode node )
	{
		return node.Nodes.Cast<TreeNode>();
	}

	// Sorting can leave a row's stored index out of date, so removal also checks its parent.
	public static void RemoveFromTree( this TreeNode node )
	{
		TreeView tree = node.TreeView;
		TreeNodeCollection siblings = node.Parent?.Nodes ?? tree?.Nodes;
		int index = node.Index;

		if ( siblings != null && ( index < 0 || index >= siblings.Count || siblings[index] != node ) )
		{
			bool sorted = tree?.Sorted == true;

			tree?.BeginUpdate();

			try
			{
				if ( tree != null )
					tree.Sorted = false;

				var marker = new TreeNode();

				siblings.Insert( 0, marker );
				marker.Remove();
			}
			finally
			{
				if ( tree != null )
					tree.Sorted = sorted;

				tree?.EndUpdate();
			}
		}

		node.Remove();
	}

	public static SMTree.NodeMetadata GetMetadata( this TreeNode node )
	{
		return node.Tag as SMTree.NodeMetadata;
	}

	public static bool IsEmpty( this TreeView tree )
	{
		return tree.Nodes.Count == 0;
	}

	public static void ScrollToTop( this TreeView treeView )
	{
		if ( treeView.Nodes.Count == 0 )
		{
			return;
		}

		TreeNode firstNode = treeView.Nodes[0];

		firstNode.EnsureVisible();
		treeView.TopNode = firstNode;
	}

	public static void EndUpdateAtTop( this TreeView treeView )
	{
		treeView.EndUpdate();
		treeView.ScrollToTop();
	}

	[DllImport( "user32.dll", CharSet = CharSet.Auto )]
	private static extern nint SendMessage( nint hWnd, int Msg, nint wParam, ref TVITEM lParam );

	[DllImport( "user32.dll" )]
	private static extern nint SendMessage( nint hWnd, int Msg, nint wParam, nint lParam );

	// Reads the native row's state image, which WinForms does not expose. Zero means the row has no checkbox.
	public static bool IsCheckBoxVisible( this TreeNode node )
	{
		if ( node == null )
		{
			throw new ArgumentNullException( "node" );
		}

		if ( node.TreeView == null )
		{
			throw new InvalidOperationException( UiText.T( "Extensions", "The node does not belong to a tree." ) );
		}

		TVITEM tvi = new TVITEM
		{
			hItem = node.Handle,
			mask = TVIF_STATE
		};

		if ( SendMessage( node.TreeView.Handle, TVM_GETITEM, node.Handle, ref tvi ) == IntPtr.Zero )
		{
			throw new ApplicationException( UiText.T( "Extensions", "Error getting TreeNode state." ) );
		}

		return ( tvi.state & TVIS_STATEIMAGEMASK ) >> 12 != 0;
	}

	// Clears native selection and pressed-row highlights while preserving SelectedNode and keyboard focus.
	public static void ClearNativeHighlights( this TreeView tree )
	{
		foreach ( int row in new[] { TVGN_CARET, TVGN_DROPHILITE } )
		{
			IntPtr item = SendMessage( tree.Handle, TVM_GETNEXTITEM, row, IntPtr.Zero );

			// Child nodes can be detached before Windows finishes removing their rows.
			if ( item == IntPtr.Zero
				|| TreeNode.FromHandle( tree, item ) == null
				|| ( SendMessage( tree.Handle, TVM_GETITEMSTATE, item, TVIS_NATIVE_HIGHLIGHTS ) & TVIS_NATIVE_HIGHLIGHTS ) == 0 )
			{
				continue;
			}

			TVITEM tvi = new TVITEM
			{
				hItem = item,
				mask = TVIF_STATE,
				stateMask = TVIS_NATIVE_HIGHLIGHTS
			};

			SendMessage( tree.Handle, TVM_SETITEM, IntPtr.Zero, ref tvi );
		}
	}

	// Sets native checkbox visibility for rows already attached to a tree.
	public static void SetIsCheckBoxVisible( this TreeNode node, bool isVisible, bool applyToSubtree = false )
	{
		if ( node.TreeView == null )
		{
			throw new InvalidOperationException( UiText.T( "Extensions", "The node does not belong to a tree." ) );
		}

		TVITEM tvi = new TVITEM
		{
			hItem = node.Handle,
			mask = TVIF_STATE,
			stateMask = TVIS_STATEIMAGEMASK,
			state = ( isVisible ? ( ( !node.Checked ) ? 1 : 2 ) : 0 ) << 12
		};

		if ( SendMessage( node.TreeView.Handle, TVM_SETITEM, IntPtr.Zero, ref tvi ) == IntPtr.Zero )
		{
			throw new ApplicationException( UiText.T( "Extensions", "Error setting TreeNode state." ) );
		}

		if ( !applyToSubtree )
		{
			return;
		}

		foreach ( TreeNode treeNode in node.GetTreeNodes() )
		{
			treeNode.SetIsCheckBoxVisible( isVisible, applyToSubtree );
		}
	}

	public static bool SetCheckedIfVisible( this TreeNode node, bool isChecked )
	{
		if ( node.IsCheckBoxVisible() )
		{
			node.Checked = isChecked;

			return true;
		}

		return false;
	}

	public static bool AreAllVisibleCheckboxesChecked( this TreeNode node )
	{
		TreeNode[] visible = ( from child in node.GetTreeNodes()
							   where child.IsCheckBoxVisible()
							   select child ).ToArray();

		return visible.Length > 0 && visible.All( child => child.Checked );
	}
}
