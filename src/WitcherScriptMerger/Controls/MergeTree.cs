using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using WitcherScriptMerger.Theming;
using WitcherScriptMerger.FileIndex;
using WitcherScriptMerger.Inventory;

namespace WitcherScriptMerger.Controls;

// Compiled outputs that must be deleted together share one row in the merge pane.
internal class MergeTree : SMTree
{
	internal static readonly ModFileCategory PrecompiledMerges = new( 4,
		() => UiText.T( "MergeTree", "Precompiled Script Merges" ),
		() => UiText.T( "MergeTree", "Merged files created from precompiled scripts. Each group is deleted together." ), true, false );
	private bool synchronizingChecks;

	internal static IEnumerable<Merge> MergesForNode( TreeNode node )
	{
		for ( var current = node; current != null; current = current.Parent )
			if ( current.GetMetadata()?.MergedOutputs is { } outputs )
				return outputs;

		return ( node.GetMetadata()?.ModFile ?? node.Parent?.GetMetadata()?.ModFile ) is Merge merge ? new[] { merge } : Array.Empty<Merge>();
	}

	internal static IEnumerable<TreeNode> OutputNodes( TreeNode node ) => node.GetMetadata()?.MergedOutputs != null
		? node.GetTreeNodes() : new[] { node };

	internal static Color OutdatedColor => ThemeManager.CurrentTheme.ThemeName == "Light" ? Color.Firebrick : Color.FromArgb( 255, 110, 110 );

	// Disabling a merged output leaves its input mods unchanged. Strike through the output rows only.
	internal void ApplyOutputStyles( TreeNode root )
	{
		string reason = string.Join( Environment.NewLine, MergesForNode( root ).Select( merge => merge.OutdatedReason )
			.Where( text => text.Length > 0 ).Distinct() );
		TreeNode[] outputs = OutputNodes( root ).ToArray();
		TreeNode[] disabled = outputs.Where( IsInDisabledFolder ).ToArray();

		if ( root.GetMetadata()?.MergedOutputs != null )
		{
			root.ToolTipText = reason.Length > 0 ? reason : UiText.T(
				"MergeTree",
				"These files are deleted together. Deleting this group restores their original mod scripts." );
			root.ForeColor = reason.Length > 0 ? OutdatedColor : ThemeManager.CurrentTheme.ForeBrightColor;
			root.GetMetadata().ForeColor = root.ForeColor;
			root.NodeFont = NodeStyleFont( disabled.Length == outputs.Length ? FontStyle.Strikeout : FontStyle.Regular );
		}

		foreach ( var file in outputs )
		{
			var merge = file.GetMetadata()?.ModFile as Merge;
			string outdated = reason.Length > 0 ? reason : merge?.OutdatedReason ?? "";
			bool isDisabled = disabled.Contains( file );

			file.ForeColor = outdated.Length > 0 ? OutdatedColor : ThemeManager.CurrentTheme.ForeBrightColor;
			file.ToolTipText = outdated.Length > 0 ? outdated : isDisabled ? UiText.T( "ConflictTree", "Disabled." ) : "";
			file.NodeFont = NodeStyleFont( isDisabled ? FontStyle.Strikeout : FontStyle.Regular );

			if ( file.GetMetadata() is { } metadata )
				metadata.ForeColor = file.ForeColor;

			foreach ( TreeNode source in file.Nodes )
			{
				source.ForeColor = source.GetMetadata()?.FileHash?.IsOutdated == true ? OutdatedColor
					: source.GetMetadata()?.FileHash?.IsExternal == true ? ThemeManager.ModIoColor : ThemeManager.CurrentTheme.ForeDimmedColor;

				if ( source.GetMetadata() is { } data )
					data.ForeColor = source.ForeColor;
			}
		}
	}

	private bool IsInDisabledFolder( TreeNode output )
	{
		return output.GetMetadata()?.ModFile is Merge merge && MergeOutputFolder.IsDisabled( merge, GamePaths, CurrentLoadOrder?.Invoke() );
	}

	internal void GroupPrecompiledOutputs( IReadOnlyList<IReadOnlyList<Merge>> groups )
	{
		var files = FileNodes.Where( node => node.GetMetadata()?.ModFile is Merge { IsBlobMerge: true } )
			.ToDictionary( node => (Merge)node.GetMetadata().ModFile );

		foreach ( var outputs in groups )
		{
			var members = outputs.Where( files.ContainsKey ).ToArray();

			if ( members.Length == 0 )
				continue;

			var category = GetCategoryNode( PrecompiledMerges );

			if ( category == null )
			{
				category = new TreeNode( PrecompiledMerges.DisplayName ) { Tag = PrecompiledMerges, ToolTipText = PrecompiledMerges.ToolTipText };
				Nodes.Add( category );
			}

			if ( members.Length == 1 )
			{
				var file = files[members[0]];

				file.Remove();
				category.Nodes.Add( file );

				continue;
			}

			string owners = string.Join( ", ", members.SelectMany( merge => merge.Mods ).Select( source => source.Name )
				.Distinct( StringComparer.OrdinalIgnoreCase ).OrderBy( name => name, StringComparer.OrdinalIgnoreCase ) );
			var group = new TreeNode( UiText.Format( "MergeTree", "Scripts from {0}", owners ) )
			{
				Tag = new NodeMetadata { MergedOutputs = members },
				ForeColor = ThemeManager.CurrentTheme.ForeBrightColor,
				ToolTipText = UiText.T(
					"MergeTree",
					"These files are deleted together. Deleting this group restores their original mod scripts." )
			};

			category.Nodes.Add( group );

			foreach ( var merge in members )
			{
				var file = files[merge];

				file.Remove();
				group.Nodes.Add( file );
				file.ToolTipText = group.ToolTipText;
			}
		}

		foreach ( var category in CategoryNodes.Where( category => category.Nodes.Count == 0 ).ToArray() )
			category.Remove();

		foreach ( var root in FileNodes )
			ApplyOutputStyles( root );

		RefreshCheckboxes();
	}

	// The user deletes whole merge outputs. Their source rows have no separate checkbox.
	internal void RefreshCheckboxes()
	{
		if ( !IsHandleCreated )
			return;

		foreach ( TreeNode category in Nodes )
			foreach ( var node in new[] { category }.Concat( Descendants( category ) ) )
				node.SetIsCheckBoxVisible( node.Level <= 1 );
	}

	internal void SynchronizeCategory( TreeNode category )
	{
		bool previous = synchronizingChecks;

		synchronizingChecks = true;

		try
		{
			category.Checked = category.GetTreeNodes().All( node => node.Checked );
		}
		finally { synchronizingChecks = previous; }
	}

	protected override void OnAfterCheck( TreeViewEventArgs e )
	{
		base.OnAfterCheck( e );

		if ( e.Action != TreeViewAction.Unknown || synchronizingChecks || e.Node.TreeView != this )
			return;

		var previous = ClickedNode;

		ClickedNode = e.Node;
		HandleCheckedChange();
		ClickedNode = previous;
	}

	public new static readonly Color FileNodeForeColor = Color.Blue;

	private readonly ToolStripMenuItem _contextOpenMergedFile = new ToolStripMenuItem();

	private readonly ToolStripMenuItem _contextOpenMergedFileDir = new ToolStripMenuItem();

	private readonly ToolStripMenuItem _contextDeleteAssociatedMerges = new ToolStripMenuItem();

	private readonly ToolStripMenuItem _contextDeleteMerge = new ToolStripMenuItem();

	private readonly ToolStripSeparator _contextDeleteSeparator = new ToolStripSeparator();
	private nint tooltipFont;

	[DllImport( "user32.dll", EntryPoint = "SendMessageW" )]
	private static extern nint SendMessage( nint window, int message, nint parameter, nint value );

	[DllImport( "gdi32.dll" )]
	private static extern bool DeleteObject( nint value );

	protected override void OnHandleCreated( EventArgs e )
	{
		base.OnHandleCreated( e );
		RefreshTooltipFont();
	}

	protected override void OnFontChanged( EventArgs e )
	{
		base.OnFontChanged( e );
		RefreshTooltipFont();
	}

	protected override void OnDpiChangedAfterParent( EventArgs e )
	{
		base.OnDpiChangedAfterParent( e );
		RefreshTooltipFont();
	}

	protected override void OnHandleDestroyed( EventArgs e )
	{
		base.OnHandleDestroyed( e );

		if ( tooltipFont != 0 )
			DeleteObject( tooltipFont );

		tooltipFont = 0;
	}

	private void RefreshTooltipFont()
	{
		if ( !IsHandleCreated )
			return;

		nint tooltip = SendMessage( Handle, 0x1119, 0, 0 ); // TVM_GETTOOLTIPS

		if ( tooltip == 0 )
			return;

		nint previous = tooltipFont;

		tooltipFont = SystemFonts.StatusFont.ToHfont();
		SendMessage( tooltip, 0x30, tooltipFont, 1 ); // WM_SETFONT

		if ( previous != 0 )
			DeleteObject( previous );
	}

	public MergeTree( Theme theme )
		: base( theme )
	{
		NativeItemsReady += RefreshCheckboxes;
		ContextOpenRegion.Items.AddRange( new ToolStripItem[2] { _contextOpenMergedFile, _contextOpenMergedFileDir } );
		ContextNodeRegion.Items.AddRange( new ToolStripItem[3] { _contextDeleteSeparator, _contextDeleteMerge, _contextDeleteAssociatedMerges } );
		BuildContextMenu();
		_contextOpenMergedFile.Name = "contextOpenMergedFile";
		_contextOpenMergedFile.Size = new Size( 225, 22 );
		LiveText.BindText( _contextOpenMergedFile, () => UiText.T( "MergeTree", "Open Merged File" ) );
		LiveText.BindToolTipText( _contextOpenMergedFile, () => UiText.T( "MergeTree", "Opens the merged version of the file" ) );
		_contextOpenMergedFile.Click += ( _, _ ) => OpenMergedLocation( directory: false );
		_contextOpenMergedFileDir.Name = "contextOpenMergedFileDir";
		_contextOpenMergedFileDir.Size = new Size( 225, 22 );
		LiveText.BindText( _contextOpenMergedFileDir, () => UiText.T( "MergeTree", "Open Merged File Directory" ) );
		LiveText.BindToolTipText(
			_contextOpenMergedFileDir,
			() => UiText.T( "MergeTree", "Opens the location of the merged version of the file" ) );
		_contextOpenMergedFileDir.Click += ( _, _ ) => OpenMergedLocation( directory: true );
		_contextDeleteSeparator.Name = "contextDeleteSeparator";
		_contextDeleteSeparator.Size = new Size( 235, 6 );
		_contextDeleteMerge.Name = "contextDeleteMerge";
		_contextDeleteMerge.Size = new Size( 225, 22 );
		LiveText.BindText( _contextDeleteMerge, () => UiText.T( "MergeTree", "Delete This Merge" ) );
		LiveText.BindToolTipText( _contextDeleteMerge, () => UiText.T( "MergeTree", "Deletes the merged version of the file" ) );
		_contextDeleteMerge.Click += ContextDeleteMerge_Click;
		_contextDeleteAssociatedMerges.Name = "contextDeleteAssociatedMerges";
		_contextDeleteAssociatedMerges.Size = new Size( 225, 22 );
		LiveText.BindText( _contextDeleteAssociatedMerges, () => UiText.T( "MergeTree", "Delete All {0} Merges" ) );
		LiveText.BindToolTipText(
			_contextDeleteAssociatedMerges,
			() => UiText.T( "MergeTree", "Deletes all merges that contain this mod's files" ) );
		_contextDeleteAssociatedMerges.Click += ContextDeleteAssociatedMerges_Click;
	}

	// Changing a parent checkbox also changes its children. Suppress the resulting nested events.
	protected override void HandleCheckedChange()
	{
		if ( synchronizingChecks || ClickedNode == null )
			return;

		synchronizingChecks = true;

		try
		{
			if ( SMTree.IsCategoryNode( ClickedNode ) )
			{
				foreach ( TreeNode treeNode in ClickedNode.GetTreeNodes() )
				{
					treeNode.Checked = ClickedNode.Checked;
				}
			}
			else if ( ClickedNode.Level == 1 )
			{
				TreeNode parent = ClickedNode.Parent;

				parent.Checked = parent.GetTreeNodes().All( ( TreeNode node ) => node.Checked );
			}
			else
				ClickedNode.Checked = false;
		}
		finally { synchronizingChecks = false; }

		Program.MainFrm?.SetUnmergeButtonTextIfValidSelection();
	}

	protected override void OnLeftMouseUp( MouseEventArgs e )
	{
		for ( var owner = ClickedNode; owner != null; owner = owner.Parent )
			if ( owner.GetMetadata()?.MergedOutputs != null )
			{
				ClickedNode = owner;

				break;
			}

		if ( ClickedNode != null && SMTree.IsModNode( ClickedNode ) )
		{
			ClickedNode = ClickedNode.Parent;
		}

		base.OnLeftMouseUp( e );
	}

	protected override void SetAllChecked( bool isChecked )
	{
		foreach ( TreeNode categoryNode in base.CategoryNodes )
		{
			categoryNode.Checked = isChecked;

			foreach ( TreeNode treeNode in categoryNode.GetTreeNodes() )
			{
				treeNode.Checked = isChecked;
			}
		}

		Program.MainFrm?.SetUnmergeButtonTextIfValidSelection();
	}

	private void ContextDeleteMerge_Click( object sender, EventArgs e )
	{
		if ( RightClickedNode != null )
		{
			if ( SMTree.IsModNode( RightClickedNode ) )
			{
				Program.MainFrm.DeleteMerges( new TreeNode[1] { RightClickedNode.Parent } );
			}
			else
			{
				Program.MainFrm.DeleteMerges( new TreeNode[1] { RightClickedNode } );
			}
		}
	}

	private void ContextDeleteAssociatedMerges_Click( object sender, EventArgs e )
	{
		if ( RightClickedNode == null || !SMTree.IsModNode( RightClickedNode ) )
		{
			return;
		}

		IEnumerable<TreeNode> fileNodes = base.FileNodes.SelectMany( OutputNodes ).Where( ( TreeNode node ) => node.GetTreeNodes().Any( ( TreeNode modNode ) => modNode.Text == RightClickedNode.Text ) );

		Program.MainFrm.DeleteMerges( fileNodes );
	}

	protected override void SetContextItemAvailability()
	{
		base.SetContextItemAvailability();

		if ( ClickedNode != null )
		{
			_contextDeleteMerge.Text = MergesForNode( ClickedNode ).Any( merge => merge.IsBlobMerge )
				? UiText.T( "MergeTree", "Delete This Merge Group" ) : UiText.T( "MergeTree", "Delete This Merge" );

			if ( MergedLocation( ClickedNode, GamePaths ) is { } mergedPath )
			{
				_contextOpenMergedFile.Available = File.Exists( mergedPath );
				_contextOpenMergedFileDir.Available = Directory.Exists( Path.GetDirectoryName( mergedPath ) );
			}

			if ( !SMTree.IsCategoryNode( ClickedNode ) )
			{
				ToolStripMenuItem contextDeleteMerge = _contextDeleteMerge;
				bool available = ( _contextDeleteSeparator.Available = true );

				contextDeleteMerge.Available = available;

				if ( SMTree.IsModNode( ClickedNode ) )
				{
					_contextDeleteAssociatedMerges.Available = true;
					_contextDeleteAssociatedMerges.Text = UiText.Format( "MergeTree", "Delete All {0} Merges", ClickedNode.Text );
				}
			}
		}
		else if ( !this.IsEmpty() )
		{
			ContextSelectAll.Available = base.CategoryNodes.Any( ( TreeNode catNode ) => !catNode.Checked );
			ContextDeselectAll.Available = base.FileNodes.Any( ( TreeNode fileNode ) => fileNode.Checked );
		}
	}

	internal static string MergedLocation(
		TreeNode node,
		GamePaths game ) => node?.GetMetadata()?.ModFile is WitcherScriptMerger.Inventory.Merge merge
		? merge.GetMergedFile( game ) : null;

	private void OpenMergedLocation( bool directory )
	{
		string path = MergedLocation( RightClickedNode, GamePaths );

		RightClickedNode = null;

		if ( path == null )
			return;

		if ( directory )
			Program.TryOpenFileLocation( path );
		else
			Program.TryOpenFile( path );
	}
}
