using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using WitcherScriptMerger.FileIndex;
using WitcherScriptMerger.Inventory;
using WitcherScriptMerger.Theming;
using WitcherScriptMerger.BlobMerging;

namespace WitcherScriptMerger.Controls;

internal enum ScanDiagnosticKind
{
	Scripts, Xml
}
internal sealed record ScanDiagnosticTag( ScanDiagnosticKind Kind );

// Both panes use category, file and source rows. Tag stores the data behind a row or marks a diagnostic.
internal abstract class SMTree : ConflictSelectionTree
{
	// The form supplies the current game folders for actions such as opening a merged file.
	[DesignerSerializationVisibility( DesignerSerializationVisibility.Hidden )]
	internal GamePaths GamePaths { get; set; } = Paths.ForSelectedGame();

	public enum LevelType
	{
		Categories,
		Files,
		Mods
	}

	// MergeGroup identifies a linked group; IsLinkedSymbol and IsLinkedFile identify its children.
	// MergedOutputs identifies a compiled-output group in the merge pane.
	public class NodeMetadata
	{
		public string FilePath;

		public FileHash FileHash;

		public ModFile ModFile;
		internal LinkedBlobMergeGroup MergeGroup;
		internal bool IsLinkedSymbol;
		internal bool IsLinkedFile;
		internal IReadOnlyList<Merge> MergedOutputs;

		public Color ForeColor = ThemeManager.CurrentTheme.ForeDimmedColor;

		public string ModName = string.Empty;
	}

	public static readonly Color FileNodeForeColor = Color.Black;

	protected TreeNode ClickedNode;

	protected bool IsUpdating;

	private readonly Theme currentTheme;

	private const int TVM_SETEXTENDEDSTYLE = 4396;

	private const int TVS_EX_DOUBLEBUFFER = 4;

	protected TreeNode RightClickedNode;

	private ContextMenuStrip _contextMenu;

	protected ToolStripRegion ContextOpenRegion;

	private readonly ToolStripMenuItem _contextOpenModFile = new ToolStripMenuItem();

	private readonly ToolStripMenuItem _contextOpenModFileDir = new ToolStripMenuItem();

	private readonly ToolStripMenuItem _contextOpenModBundleDir = new ToolStripMenuItem();

	private readonly ToolStripMenuItem _contextOpenVanillaFile = new ToolStripMenuItem();

	private readonly ToolStripMenuItem _contextOpenVanillaFileDir = new ToolStripMenuItem();

	private readonly ToolStripMenuItem _contextCopyPath = new ToolStripMenuItem();

	protected ToolStripRegion ContextNodeRegion;

	protected ToolStripRegion ContextAllRegion;

	private readonly ToolStripSeparator _contextAllSeparator = new ToolStripSeparator();

	private readonly ToolStripMenuItem _contextExpandAll = new ToolStripMenuItem();

	private readonly ToolStripMenuItem _contextCollapseAll = new ToolStripMenuItem();

	protected ToolStripMenuItem ContextSelectAll = new ToolStripMenuItem();

	protected ToolStripMenuItem ContextDeselectAll = new ToolStripMenuItem();

	public List<TreeNode> CategoryNodes => GetNodesAtLevel( LevelType.Categories );

	public List<TreeNode> FileNodes => GetNodesAtLevel( LevelType.Files ).Where( node => node.Tag is not ScanDiagnosticTag ).ToList();

	public List<TreeNode> SymbolNodes => SymbolRows( this ).ToList();

	internal static IEnumerable<TreeNode> SymbolRows( TreeView tree ) => tree.Nodes.Cast<TreeNode>().SelectMany( category => category.GetTreeNodes() )
		.SelectMany( node => node.GetMetadata()?.MergedOutputs != null ? node.GetTreeNodes()
			: node.GetMetadata()?.MergeGroup != null ? Descendants( node ).Where( child => child.GetMetadata()?.IsLinkedSymbol == true ) : new[] { node } )
		.Where( node => node.Tag is not ScanDiagnosticTag );

	public List<TreeNode> ModNodes => SymbolNodes.SelectMany( node => node.GetTreeNodes() ).ToList();

	internal static IEnumerable<TreeNode> Descendants( TreeNode node ) => node.GetTreeNodes().SelectMany( child => new[] { child }.Concat( Descendants( child ) ) );

	[DllImport( "user32.dll" )]
	private static extern nint SendMessage( nint hWnd, int msg, nint wp, nint lp );

	protected override void OnHandleCreated( EventArgs e )
	{
		SendMessage( base.Handle, 4396, 4, 4 );
		base.OnHandleCreated( e );
	}

	public SMTree( Theme theme )
	{
		currentTheme = theme;
		InitializeContextMenu();
		base.TreeViewNodeSorter = new SMTreeSorter( () => CurrentLoadOrder() );
	}

	protected List<TreeNode> GetNodesAtLevel( LevelType level )
	{
		IEnumerable<TreeNode> nodes = base.Nodes.Cast<TreeNode>();

		for ( int i = 0; i < (int)level; i++ )
		{
			nodes = nodes.SelectMany( ( TreeNode node ) => node.GetTreeNodes() );
		}

		return nodes.ToList();
	}

	public TreeNode GetCategoryNode( ModFileCategory category )
	{
		return CategoryNodes.FirstOrDefault( ( TreeNode node ) => category == (ModFileCategory)node.Tag );
	}

	protected override void OnKeyDown( KeyEventArgs e )
	{
		base.OnKeyDown( e );

		if ( e.Handled )
			return;

		if ( e.Control )
		{
			if ( e.KeyCode == Keys.A )
			{
				ContextSelectAll_Click( null, null );
			}
			else if ( e.KeyCode == Keys.D )
			{
				ContextDeselectAll_Click( null, null );
			}
		}
	}

	protected override void OnAfterSelect( TreeViewEventArgs e )
	{
		base.OnAfterSelect( e );

		if ( !FileSelectionEnabled )
			base.SelectedNode = null;
	}

	protected override void OnMouseDown( MouseEventArgs e )
	{
		base.OnMouseDown( e );
		ClickedNode = GetNodeAt( e.Location );

		if ( ClickedNode != null )
		{
			if ( !IsContextRowHit( ClickedNode, e.Location ) )
			{
				ClickedNode = null;

				return;
			}

			_ = e.Button;
			_ = 1048576;
		}
	}

	protected override void OnMouseMove( MouseEventArgs e )
	{
		base.OnMouseMove( e );

		if ( ClickedNode != null && RightClickedNode == null && e.Button != MouseButtons.Right )
		{
			ClickedNode.Bounds.Contains( e.Location );
		}
	}

	// A drag that ends on another row must not count as a click on that row.
	protected override void OnMouseUp( MouseEventArgs e )
	{
		base.OnMouseUp( e );

		TreeNode lastClicked = ClickedNode;

		ClickedNode = GetNodeAt( e.Location );

		if ( ClickedNode != null && ( lastClicked != ClickedNode || !IsContextRowHit( ClickedNode, e.Location ) ) )
		{
			ClickedNode = null;
		}

		if ( e.Button == MouseButtons.Left )
		{
			OnLeftMouseUp( e );

			if ( lastClicked != null )
			{
				_ = ClickedNode;
			}

			ClickedNode = null;
		}
		else if ( e.Button == MouseButtons.Right )
		{
			OnRightMouseUp( e );
		}

		IsUpdating = false;
	}

	protected virtual void OnLeftMouseUp( MouseEventArgs e )
	{
		if ( ClickedNode != null && ClickedNode.SetCheckedIfVisible( !ClickedNode.Checked ) )
		{
			HandleCheckedChange();
		}
	}

	private bool IsContextRowHit( TreeNode node, Point point ) => node.Bounds.Contains( point )
		|| ( FileSelectionEnabled && IsConflictFileNode( node ) && point.X >= node.Bounds.Left
			&& point.Y >= node.Bounds.Top && point.Y < node.Bounds.Bottom );

	protected virtual void OnRightMouseUp( MouseEventArgs e )
	{
		ResetContextItemAvailability();
		SetContextItemAvailability();

		if ( _contextMenu.Items.OfType<ToolStripMenuItem>().Any( ( ToolStripMenuItem item ) => item.Available ) )
		{
			RightClickedNode = ClickedNode;

			if ( ClickedNode != null )
			{
				SetContextMenuSize();
			}

			_contextMenu.Show( this, e.X, e.Y );
		}
	}

	protected override void OnAfterCheck( TreeViewEventArgs e )
	{
		base.OnAfterCheck( e );

		if ( e.Action != TreeViewAction.Unknown )
		{
			ClickedNode = e.Node;
			HandleCheckedChange();
		}
	}

	protected abstract void HandleCheckedChange();

	protected override void OnMouseLeave( EventArgs e )
	{
		base.OnMouseLeave( e );
	}

	protected static bool IsCategoryNode( TreeNode node )
	{
		return node.Level == 0;
	}

	internal static bool IsFileNode( TreeNode node )
	{
		if ( node.GetMetadata()?.ModFile is Merge )
			return node.GetMetadata().FileHash == null;

		return node.Level == 1 || node.GetMetadata()?.IsLinkedSymbol == true || node.GetMetadata()?.IsLinkedFile == true;
	}

	internal static bool IsModNode( TreeNode node )
	{
		if ( node.GetMetadata()?.ModFile is Merge )
			return node.GetMetadata().FileHash != null;

		return node.GetMetadata()?.IsLinkedSymbol != true && node.GetMetadata()?.IsLinkedFile != true
			&& ( node.Level == 2 || node.Parent?.GetMetadata()?.IsLinkedSymbol == true );
	}

	private void InitializeContextMenu()
	{
		_contextMenu = new ContextMenuStrip
		{
			ShowImageMargin = false,
			Renderer = new MenuRenderer( currentTheme )
		};
		ContextOpenRegion = new ToolStripRegion(
			_contextMenu,
			new ToolStripItem[5] { _contextCopyPath, _contextOpenModFileDir, _contextOpenModBundleDir, _contextOpenVanillaFile, _contextOpenVanillaFileDir } );
		ContextNodeRegion = new ToolStripRegion( _contextMenu, Array.Empty<ToolStripItem>() );
		ContextAllRegion = new ToolStripRegion(
			_contextMenu,
			new ToolStripItem[5] { _contextAllSeparator, ContextSelectAll, ContextDeselectAll, _contextExpandAll, _contextCollapseAll } );
		_contextMenu.Name = "treeContextMenu";
		_contextMenu.Closing += ContextMenu_Closing;
		_contextOpenModFileDir.Name = "contextOpenModFileDir";
		_contextOpenModFileDir.Size = new Size( 225, 22 );
		LiveText.BindText( _contextOpenModFileDir, () => UiText.T( "SMTree", "Open Mod File Directory" ) );
		LiveText.BindToolTipText(
			_contextOpenModFileDir,
			() => UiText.T( "SMTree", "Opens the location of this mod's version of the file" ) );
		_contextOpenModFileDir.Click += ContextOpenDirectory_Click;
		_contextOpenModBundleDir.Name = "contextOpenModBundleDir";
		_contextOpenModBundleDir.Size = new Size( 225, 22 );
		LiveText.BindText( _contextOpenModBundleDir, () => UiText.T( "SMTree", "Open Mod Bundle Directory" ) );
		LiveText.BindToolTipText( _contextOpenModBundleDir, () => UiText.T( "SMTree", "Opens the location of this mod's bundle file" ) );
		_contextOpenModBundleDir.Click += ContextOpenDirectory_Click;
		_contextOpenVanillaFile.Name = "contextOpenVanillaFile";
		LiveText.BindText( _contextOpenVanillaFile, () => UiText.T( "SMTree", "Open Vanilla File" ) );
		LiveText.BindToolTipText( _contextOpenVanillaFile, () => UiText.T( "SMTree", "Opens the unmodded version of the file" ) );
		_contextOpenVanillaFile.Click += ( _, _ ) =>
		{
			if ( RightClickedNode != null )
				Program.TryOpenFile( RightClickedNode.GetMetadata().ModFile.GetVanillaFile( GamePaths ) );

			RightClickedNode = null;
		};
		_contextOpenVanillaFileDir.Name = "contextOpenVanillaFileDir";
		_contextOpenVanillaFileDir.Size = new Size( 225, 22 );
		LiveText.BindText( _contextOpenVanillaFileDir, () => UiText.T( "SMTree", "Open Vanilla File Directory" ) );
		LiveText.BindToolTipText(
			_contextOpenVanillaFileDir,
			() => UiText.T( "SMTree", "Opens the location of the unmodded version of the file" ) );
		_contextOpenVanillaFileDir.Click += ContextOpenVanillaDirectory_Click;
		_contextCopyPath.Name = "contextCopyPath";
		_contextCopyPath.Size = new Size( 225, 22 );
		LiveText.BindText( _contextCopyPath, () => UiText.T( "SMTree", "Copy Path" ) );
		_contextCopyPath.Click += ContextCopyPath_Click;
		_contextAllSeparator.Name = "contextAllSeparator";
		_contextAllSeparator.Size = new Size( 235, 6 );
		ContextSelectAll.Name = "contextSelectAll";
		ContextSelectAll.Size = new Size( 225, 22 );
		LiveText.BindText( ContextSelectAll, () => UiText.T( "SMTree", "Select All" ) );
		ContextSelectAll.Click += ContextSelectAll_Click;
		ContextDeselectAll.Name = "contextDeselectAll";
		ContextDeselectAll.Size = new Size( 225, 22 );
		LiveText.BindText( ContextDeselectAll, () => UiText.T( "SMTree", "Deselect All" ) );
		ContextDeselectAll.Click += ContextDeselectAll_Click;
		_contextExpandAll.Name = "contextExpandAll";
		_contextExpandAll.Size = new Size( 225, 22 );
		LiveText.BindText( _contextExpandAll, () => UiText.T( "SMTree", "Expand All" ) );
		_contextExpandAll.Click += ContextExpandAll_Click;
		_contextCollapseAll.Name = "contextCollapseAll";
		_contextCollapseAll.Size = new Size( 225, 22 );
		LiveText.BindText( _contextCollapseAll, () => UiText.T( "SMTree", "Collapse All" ) );
		_contextCollapseAll.Click += ContextCollapseAll_Click;
	}

	protected void BuildContextMenu()
	{
		_contextMenu.Items.Clear();

		ToolStripItemCollection items = _contextMenu.Items;
		ToolStripItem[] toolStripItems = new ToolStripMenuItem[4] { _contextCopyPath, _contextOpenModFileDir, _contextOpenModBundleDir, _contextOpenVanillaFileDir };

		items.AddRange( toolStripItems );
		items.AddRange( ContextOpenRegion.Items.Cast<ToolStripItem>().ToArray() );

		foreach ( ToolStripItem item in ContextNodeRegion.Items )
		{
			_contextMenu.Items.Add( item );
		}

		_contextMenu.Items.Add( _contextAllSeparator );

		ToolStripItemCollection items2 = _contextMenu.Items;

		toolStripItems = new ToolStripMenuItem[4] { ContextSelectAll, ContextDeselectAll, _contextExpandAll, _contextCollapseAll };
		items2.AddRange( toolStripItems );
	}

	private void ResetContextItemAvailability()
	{
		foreach ( ToolStripItem item in _contextMenu.Items.OfType<ToolStripItem>() )
		{
			item.Available = false;
		}
	}

	protected virtual void SetContextItemAvailability()
	{
		foreach ( ToolStripItem item in _contextMenu.Items.OfType<ToolStripItem>() )
		{
			item.Available = false;
		}

		if ( ClickedNode != null && ClickedNode.Tag is NodeMetadata )
		{
			_contextCopyPath.Available = !string.IsNullOrWhiteSpace( ClickedNode.GetMetadata().FilePath );

			if ( ClickedNode.GetMetadata().ModFile is { } vanillaModel && ( IsFileNode( ClickedNode ) || IsModNode( ClickedNode ) )
				&& ( vanillaModel.Category == Categories.Script || vanillaModel.Category == Categories.Xml || vanillaModel.Category == Categories.PrecompiledScript )
				&& File.Exists( vanillaModel.GetVanillaFile( GamePaths ) ) )
			{
				_contextOpenVanillaFile.Available = true;
				_contextOpenVanillaFileDir.Available = true;
			}

			if ( IsModNode( ClickedNode ) )
			{
				if ( ClickedNode.GetMetadata().ModFile.IsBundleContent )
				{
					_contextOpenModBundleDir.Available = true;
				}
				else
				{
					ToolStripMenuItem contextOpenModFile = _contextOpenModFile;
					bool available = ( _contextOpenModFileDir.Available = true );

					contextOpenModFile.Available = available;
				}
			}
		}

		if ( ClickedNode == null && !this.IsEmpty() )
		{
			_contextExpandAll.Available = CategoryNodes.Any( ( TreeNode catNode ) => !catNode.IsExpanded )
				|| SymbolNodes.Concat( FileNodes ).Concat( FileNodes.SelectMany( Descendants ).Where( node => node.GetMetadata()?.IsLinkedFile == true ) )
					.Any( node => node.Nodes.Count > 0 && !node.IsExpanded );
			_contextCollapseAll.Available = CategoryNodes.Any( ( TreeNode node ) => node.IsExpanded );
			_contextAllSeparator.Visible = ( _contextExpandAll.Available || _contextCollapseAll.Available ) && ( ContextOpenRegion.Available || ContextNodeRegion.Available );
		}
	}

	private void SetContextMenuSize()
	{
		if ( _contextMenu.Items.OfType<ToolStripItem>().Any( ( ToolStripItem item ) => item.Available ) )
		{
			int width = ( from item in _contextMenu.Items.OfType<ToolStripMenuItem>()
						  where item.Available
						  select item ).Max( ( ToolStripMenuItem item ) => TextRenderer.MeasureText( item.Text, item.Font ).Width );
			int height = _contextMenu.GetAvailableItems().Sum( ( ToolStripItem item ) => item.Height );

			_contextMenu.Width = width + 45;
			_contextMenu.Height = height + 5;
		}
	}

	protected void ContextOpenFile_Click( object sender, EventArgs e )
	{
		if ( RightClickedNode != null )
		{
			Program.TryOpenFile( RightClickedNode.GetMetadata().FilePath );
			RightClickedNode = null;
		}
	}

	protected void ContextOpenDirectory_Click( object sender, EventArgs e )
	{
		if ( RightClickedNode != null )
		{
			Program.TryOpenFileLocation( RightClickedNode.GetMetadata().FilePath );
			RightClickedNode = null;
		}
	}

	protected void ContextOpenVanillaDirectory_Click( object sender, EventArgs e )
	{
		if ( RightClickedNode != null )
		{
			Program.TryOpenFileLocation( RightClickedNode.GetMetadata().ModFile.GetVanillaFile( GamePaths ) );
			RightClickedNode = null;
		}
	}

	private void ContextCopyPath_Click( object sender, EventArgs e )
	{
		if ( RightClickedNode != null )
		{
			Clipboard.SetText( RightClickedNode.GetMetadata().FilePath );
			RightClickedNode = null;
		}
	}

	protected void ContextSelectAll_Click( object sender, EventArgs e )
	{
		SetAllChecked( isChecked: true );
	}

	protected void ContextDeselectAll_Click( object sender, EventArgs e )
	{
		SetAllChecked( isChecked: false );
	}

	protected abstract void SetAllChecked( bool isChecked );

	private void ContextExpandAll_Click( object sender, EventArgs e )
	{
		ExpandAll();
	}

	private void ContextCollapseAll_Click( object sender, EventArgs e )
	{
		CollapseAll();
	}

	private void ContextMenu_Closing( object sender, ToolStripDropDownClosingEventArgs e )
	{
		ClickedNode = null;
	}
}
