using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using WitcherScriptMerger.FileIndex;
using WitcherScriptMerger.Inventory;
using WitcherScriptMerger.LoadOrder;
using WitcherScriptMerger.Theming;
using WitcherScriptMerger.BlobMerging;

namespace WitcherScriptMerger.Controls;

internal enum ConflictSourceRole
{
	External,
	Plain,
	Dimmed,
	TopPriority
}

internal sealed class ModFolderRenameRequestedEventArgs : EventArgs
{
	public string OldName { get; }

	public string NewName { get; }

	public ModFolderRenameRequestedEventArgs( string oldName, string newName )
	{
		OldName = oldName;
		NewName = newName;
	}
}

internal class ConflictTree : SMTree
{
	private bool synchronizingChecks;
	private readonly ToolStripMenuItem inspectItem = new ToolStripMenuItem();
	// The form supplies its session's scan state so old results cannot be used after the inputs change.
	[DesignerSerializationVisibility( DesignerSerializationVisibility.Hidden )]
	internal Func<bool> ScanIsCurrent { get; set; } = () => true;

	internal bool MergeInputsAvailable => ScanIsCurrent();

	// Unchecks every merge input. Call before the scan becomes stale, then RefreshMergeCheckboxes
	// after the change so stale checkboxes are hidden.
	internal void ClearMergeSelection()
	{
		SetAllChecked( false );
	}

	internal event Action<TreeNode> MergeInputsChanged;

	protected override void OnAfterCheck( TreeViewEventArgs e )
	{
		base.OnAfterCheck( e );

		if ( e.Action != TreeViewAction.Unknown || !IsHandleCreated || synchronizingChecks || !CanCheckNode( e.Node )
			|| ( e.Node.Parent is not null && e.Node.GetMetadata()?.MergeGroup is null ) )
			return;

		var previous = ClickedNode;

		ClickedNode = e.Node;
		HandleCheckedChange();
		ClickedNode = previous;
	}

	internal event Action<ScanDiagnosticKind> DiagnosticRequested;

	protected override void OnKeyDown( KeyEventArgs e )
	{
		if ( e.KeyCode == Keys.Enter && SelectedNode?.Tag is ScanDiagnosticTag diagnostic )
		{
			DiagnosticRequested?.Invoke( diagnostic.Kind );
			e.Handled = e.SuppressKeyPress = true;

			return;
		}

		base.OnKeyDown( e );
	}

	protected override void OnMouseMove( MouseEventArgs e )
	{
		base.OnMouseMove( e );
		Cursor = GetNodeAt( e.Location )?.Tag is ScanDiagnosticTag ? Cursors.Hand : Cursors.Default;
	}

	internal const int MaxAutomaticallyExpandedSearchFiles = 200;

	private readonly ToolStripSeparator _contextAdoptVerifiedMergeSeparator = new ToolStripSeparator();

	private readonly ToolStripMenuItem _contextAdoptVerifiedMerge = new ToolStripMenuItem();

	private readonly ToolStripSeparator _contextCustomLoadOrderSeparator = new ToolStripSeparator();

	private readonly ToolStripMenuItem _contextPrioritizeMod = new ToolStripMenuItem();

	private readonly ToolStripMenuItem _contextToggleMod = new ToolStripMenuItem();

	private readonly ToolStripMenuItem _contextRemoveFromCustomLoadOrder = new ToolStripMenuItem();

	private TreeNode _adoptionFileNode;

	private TreeNode _inlineRenameNode;

	private readonly ToolStripMenuItem _contextIgnoreConflicts = LiveText.Live(
		new ToolStripMenuItem( UiText.T( "ConflictTree", "Ignore selected conflicts until files change" ) ) { Name = "contextIgnoreConflicts" },
		control => { LiveText.BindText( control, () => UiText.T( "ConflictTree", "Ignore selected conflicts until files change" ) ); } );
	private readonly ToolStripMenuItem _contextIgnorePermanently = LiveText.Live(
		new ToolStripMenuItem( UiText.T( "ConflictTree", "Ignore selected files permanently" ) ) { Name = "contextIgnorePermanently" },
		control => { LiveText.BindText( control, () => UiText.T( "ConflictTree", "Ignore selected files permanently" ) ); } );

	internal event EventHandler<ModFolderRenameRequestedEventArgs> ModFolderRenameRequested;

	internal event Action<string> ModPriorityRequested;

	public ConflictTree( Theme theme )
		: base( theme )
	{
		LabelEdit = true;
		FileSelectionEnabled = true;
		LiveText.BindText( ContextSelectAll, () => UiText.T( "ConflictTree", "Check All Merge Inputs" ) );
		LiveText.BindText( ContextDeselectAll, () => UiText.T( "ConflictTree", "Uncheck All Merge Inputs" ) );
		FileContextMenuRequested += ( node, point ) =>
		{
			ClickedNode = node;
			OnRightMouseUp( new MouseEventArgs( MouseButtons.Right, 1, point.X, point.Y, 0 ) );
		};
		ContextNodeRegion.Items.Add( _contextIgnoreConflicts );
		ContextNodeRegion.Items.Add( inspectItem );
		LiveText.BindText( inspectItem, () => UiText.T( "InspectorLaunch", "Inspect" ) );
		inspectItem.Click += async ( _, _ ) => await Program.MainFrm.InspectAsync( ClickedNode );
		_contextIgnoreConflicts.Click += async ( _, _ ) => await Program.MainFrm.IgnoreSelectedConflictsAsync( this );
		ContextNodeRegion.Items.Add( _contextIgnorePermanently );
		LiveText.BindToolTipText(
			_contextIgnorePermanently,
			() => UiText.T(
				"ConflictTree",
				"Adds the selected file paths to Menu > Permanent Ignore Rules, where they can be removed later." ) );
		_contextIgnorePermanently.Click += async ( _, _ ) => await Program.MainFrm.IgnoreSelectedConflictsAsync( this, permanently: true );
		ContextNodeRegion.Items.AddRange( new ToolStripItem[6]
		{
			_contextAdoptVerifiedMergeSeparator,
			_contextAdoptVerifiedMerge,
			_contextCustomLoadOrderSeparator,
			_contextPrioritizeMod,
			_contextToggleMod,
			_contextRemoveFromCustomLoadOrder
		} );
		BuildContextMenu();
		_contextAdoptVerifiedMergeSeparator.Name = "contextAdoptVerifiedMergeSeparator";
		_contextAdoptVerifiedMergeSeparator.Size = new Size( 235, 6 );
		_contextAdoptVerifiedMerge.Name = "contextAdoptVerifiedMerge";
		_contextAdoptVerifiedMerge.Size = new Size( 225, 22 );
		LiveText.BindText( _contextAdoptVerifiedMerge, () => UiText.T( "ConflictTree", "Adopt Verified Existing Merge..." ) );
		LiveText.BindToolTipText(
			_contextAdoptVerifiedMerge,
			() => UiText.T(
				"ConflictTree",
				"Verifies that the top-priority bundled XML includes every conflicting mod's changes, then records those source mods in MergeInventory.xml." ) );
		_contextAdoptVerifiedMerge.Click += ContextAdoptVerifiedMerge;
		_contextCustomLoadOrderSeparator.Name = "contextCustomLoadOrderSeparator";
		_contextCustomLoadOrderSeparator.Size = new Size( 235, 6 );
		_contextPrioritizeMod.Name = "contextPrioritizeMod";
		_contextPrioritizeMod.Size = new Size( 225, 22 );
		LiveText.BindText( _contextPrioritizeMod, () => UiText.T( "ConflictTree", "Set Priority..." ) );
		LiveText.BindToolTipText(
			_contextPrioritizeMod,
			() => UiText.T( "ConflictTree", "Adds or updates this mod's priority in mods.settings" ) );
		_contextPrioritizeMod.Click += ContextPrioritizeMod;
		_contextToggleMod.Name = "contextToggleMod";
		_contextToggleMod.Size = new Size( 225, 22 );
		LiveText.BindToolTipText(
			_contextToggleMod,
			() => UiText.T( "ConflictTree", "Tells the game whether to load any of this mod's files" ) );
		_contextToggleMod.Click += ContextToggleMod;
		_contextRemoveFromCustomLoadOrder.Name = "contextRemoveFromCustomLoadOrder";
		_contextRemoveFromCustomLoadOrder.Size = new Size( 225, 22 );
		LiveText.BindToolTipText(
			_contextRemoveFromCustomLoadOrder,
			() => UiText.T( "ConflictTree", "Removes this mod's custom load order settings" ) );
		_contextRemoveFromCustomLoadOrder.Click += ContextRemoveFromCustomLoadOrder;
	}

	// Linked groups are selected as a unit; their member rows have no individual checkbox.
	private bool CanCheckNode( TreeNode node )
	{
		if ( node.Tag is ScanDiagnosticTag )
			return false;

		if ( node.Parent is null )
			return node.GetTreeNodes().Any( CanCheckNode );

		var metadata = node.GetMetadata();

		if ( metadata?.MergeGroup is { } group )
			return group.Readiness.Available;

		if ( metadata?.IsLinkedFile == true
			|| metadata?.IsLinkedSymbol == true
			|| node.Parent?.GetMetadata()?.IsLinkedSymbol == true )
			return false;

		var file = node.Level == 1 ? node : node.Parent;
		bool canSelect = file.Parent?.Tag is ModFileCategory { IsSupported: true }
			|| BlobConflictSelection.CanSelect( file.GetMetadata()?.ModFile, node.Level == 2 ? node.Text : null );

		return canSelect && ( node.Level != 2 || CurrentLoadOrder()?.IsModDisabledByName( node.Text ) != true );
	}

	protected override void OnBeforeCheck( TreeViewCancelEventArgs e )
	{
		base.OnBeforeCheck( e );

		if ( !e.Node.Checked && ( !MergeInputsAvailable || !CanCheckNode( e.Node ) ) )
			e.Cancel = true;
	}

	protected override void CreateHandle()
	{
		base.CreateHandle();
		RefreshMergeCheckboxes();
	}

	internal void RefreshMergeCheckboxes()
	{
		if ( !IsHandleCreated || !CheckBoxes )
			return;

		foreach ( TreeNode category in CategoryNodes )
		{
			foreach ( TreeNode node in new[] { category }.Concat( Descendants( category ) ) )
			{
				bool visible = MergeInputsAvailable && CanCheckNode( node );

				if ( !visible && node.Checked )
					node.Checked = false;

				node.SetIsCheckBoxVisible( visible );
			}

			SynchronizeCategory( category );
		}
	}

	internal void SynchronizeCategory( TreeNode category )
	{
		var inputs = category.GetTreeNodes().Where( CanCheckNode ).ToArray();
		bool previous = synchronizingChecks;

		synchronizingChecks = true;

		try
		{
			category.Checked = inputs.Length > 0 && inputs.All( node => node.Checked );
		}
		finally { synchronizingChecks = previous; }
	}

	private void SetCategoryInputs( TreeNode category, bool isChecked )
	{
		foreach ( TreeNode file in category.GetTreeNodes().Where( CanCheckNode ) )
		{
			file.Checked = isChecked;

			foreach ( TreeNode source in file.GetTreeNodes().Where( CanCheckNode ) )
				source.Checked = isChecked;
		}
	}

	protected override void HandleCheckedChange()
	{
		if ( synchronizingChecks || ClickedNode?.TreeView != this )
			return;

		synchronizingChecks = true;

		try
		{
			if ( SMTree.IsCategoryNode( ClickedNode ) )
			{
				SetCategoryInputs( ClickedNode, ClickedNode.Checked );
			}
			else if ( SMTree.IsFileNode( ClickedNode ) )
			{
				foreach ( TreeNode treeNode3 in ClickedNode.GetTreeNodes() )
				{
					if ( CanCheckNode( treeNode3 ) )
						treeNode3.Checked = ClickedNode.Checked;
				}

				SynchronizeCategory( ClickedNode.Parent );
			}
			else if ( SMTree.IsModNode( ClickedNode ) )
			{
				TreeNode parent = ClickedNode.Parent;

				parent.Checked = parent.AreAllVisibleCheckboxesChecked();
				SynchronizeCategory( parent.Parent );
			}
		}
		finally { synchronizingChecks = false; }

		MergeInputsChanged?.Invoke( ClickedNode );
		Program.MainFrm?.SetMergeButtonTextIfValidSelection();
	}

	// Use the same change handling as a user click so linked inputs follow this selection.
	internal void SetInputChecked( TreeNode node, bool value )
	{
		if ( node.TreeView != this || !CanCheckNode( node ) )
			return;

		node.Checked = value;

		var previous = ClickedNode;

		ClickedNode = node;

		try
		{
			HandleCheckedChange();
		}
		finally { ClickedNode = previous; }
	}

	// Update parent checkboxes after all source checks are set. Intermediate values must not trigger user actions.
	internal void SetSourceChecks( IReadOnlyDictionary<TreeNode, bool> values )
	{
		bool previous = synchronizingChecks;

		synchronizingChecks = true;

		try
		{
			foreach ( var pair in values.Where( pair => pair.Key.TreeView == this ) )
				pair.Key.Checked = pair.Value;

			var files = values.Keys.Where( node => node.TreeView == this ).Select( node => node.Parent ).Distinct().ToArray();

			foreach ( var file in files )
			{
				var inputs = file.GetTreeNodes().Where( CanCheckNode ).ToArray();

				file.Checked = inputs.Length > 0 && inputs.All( node => node.Checked );
			}

			foreach ( var category in files.Select( file => file.Parent ).Distinct() )
				SynchronizeCategory( category );
		}
		finally { synchronizingChecks = previous; }
	}

	// Clicking the text selects a row. Only a checkbox click changes the merge selection.
	protected override void OnLeftMouseUp( MouseEventArgs e )
	{
		if ( ClickedNode?.Tag is ScanDiagnosticTag diagnostic )
		{
			DiagnosticRequested?.Invoke( diagnostic.Kind );

			return;
		}

		if ( IsConflictFileNode( ClickedNode ) )
			return;

		if ( ClickedNode != null && CanCheckNode( ClickedNode ) )
		{
			base.OnLeftMouseUp( e );
		}
	}

	// Wait until every row is updated before handling the resulting selection change.
	protected override void SetAllChecked( bool isChecked )
	{
		if ( isChecked && !MergeInputsAvailable )
			return;

		synchronizingChecks = true;

		try
		{
			foreach ( TreeNode category in CategoryNodes.Where( CanCheckNode ) )
			{
				category.Checked = isChecked;
				SetCategoryInputs( category, isChecked );
			}
		}
		finally { synchronizingChecks = false; }

		MergeInputsChanged?.Invoke( null );
		Program.MainFrm.SetMergeButtonTextIfValidSelection();
	}

	protected override void SetContextItemAvailability()
	{
		base.SetContextItemAvailability();

		inspectItem.Available = Forms.MainForm.CanInspect( ClickedNode );
		inspectItem.Enabled = string.IsNullOrEmpty( Program.MainFrm?.InspectorUnavailableReason );
		inspectItem.ToolTipText = Program.MainFrm?.InspectorUnavailableReason;

		if ( ClickedNode?.Tag is ScanDiagnosticTag )
			return;

		_contextIgnoreConflicts.Available = IsConflictFileNode( ClickedNode ) && SelectedConflicts.Length > 0;
		_contextIgnoreConflicts.Enabled = true;
		_contextIgnorePermanently.Available = _contextIgnoreConflicts.Available;
		_contextIgnorePermanently.Enabled = true;
		_adoptionFileNode = null;

		if ( ClickedNode != null )
		{
			if ( SMTree.IsFileNode( ClickedNode )
				&& ReferenceEquals( ClickedNode.Parent?.Tag, Categories.BundleText )
				&& ModFile.IsXml( ClickedNode.Text ) )
			{
				_adoptionFileNode = ClickedNode;
				_contextAdoptVerifiedMergeSeparator.Available = true;
				_contextAdoptVerifiedMerge.Available = true;

				return;
			}

			if ( !SMTree.IsModNode( ClickedNode ) )
			{
				return;
			}

			if ( IsExternalModNode( ClickedNode ) )
			{
				ModLoadSetting externalSetting = CurrentLoadOrder()?.GetModLoadSettingByName( ClickedNode.Text );

				if ( externalSetting?.ModIoId > 0 )
				{
					_contextCustomLoadOrderSeparator.Available = true;
					_contextPrioritizeMod.Available = true;
					_contextPrioritizeMod.Enabled = CurrentLoadOrder().IsValid;
					LiveText.BindText( _contextPrioritizeMod, () => UiText.T( "ConflictTree", "Set Mod.io Priority..." ) );
					LiveText.BindToolTipText(
						_contextPrioritizeMod,
						() => UiText.T( "ConflictTree", "Changes this mod's position in modioModsConfig.json" ) );
				}

				return;
			}

			if ( Categories.UsesDlcRuntimeOrder( ClickedNode.Parent?.Parent?.Tag as ModFileCategory ) )
			{
				// Conventional DLC precedence is controlled by its folder name.
				return;
			}

			_contextCustomLoadOrderSeparator.Available = true;
			_contextPrioritizeMod.Available = true;
			LiveText.BindText( _contextPrioritizeMod, () => UiText.T( "ConflictTree", "Set Priority..." ) );
			LiveText.BindToolTipText(
				_contextPrioritizeMod,
				() => UiText.T( "ConflictTree", "Adds or updates this mod's priority in mods.settings" ) );

			foreach ( ToolStripItem item in ContextNodeRegion.Items.Cast<ToolStripItem>() )
			{
				item.Enabled = ReferenceEquals( item, _contextIgnoreConflicts ) || ReferenceEquals( item, _contextIgnorePermanently ) || CurrentLoadOrder().IsValid;
			}

			bool isDisabled = CurrentLoadOrder().IsModDisabledByName( ClickedNode.Text );

			_contextToggleMod.Available = true;
			LiveText.BindText(
				_contextToggleMod,
				() => ( isDisabled ? UiText.T( "ConflictTree", "Enable Mod in Load Order" ) : UiText.T( "ConflictTree", "Disable Mod in Load Order" ) ) );
			_contextRemoveFromCustomLoadOrder.Available = CurrentLoadOrder().ContainsMod( ClickedNode.Text );
			LiveText.BindText(
				_contextRemoveFromCustomLoadOrder,
				() => ( isDisabled ? UiText.T( "ConflictTree", "Clear Set Load Order && Disabled State" ) : UiText.T( "ConflictTree", "Clear Set Load Order" ) ) );
		}
		else if ( !this.IsEmpty() )
		{
			ContextSelectAll.Available = base.CategoryNodes.Any( catNode => !catNode.Checked && CanCheckNode( catNode ) );
			ContextDeselectAll.Available = base.FileNodes.Concat( base.ModNodes ).Any( node => node.Checked );
		}
	}

	private void ContextAdoptVerifiedMerge( object sender, EventArgs e )
	{
		TreeNode fileNode = _adoptionFileNode;

		_adoptionFileNode = null;

		if ( fileNode != null )
		{
			Program.MainFrm.AdoptVerifiedExistingBundledXmlMerge( fileNode );
		}
	}

	private void ContextPrioritizeMod( object sender, EventArgs e )
	{
		string modName = RightClickedNode?.Text;

		RightClickedNode = null;

		if ( !string.IsNullOrWhiteSpace( modName ) )
		{
			ModPriorityRequested?.Invoke( modName );
		}
	}

	protected override void OnDoubleClick( EventArgs e )
	{
		base.OnDoubleClick( e );

		if ( ClickedNode == null || !SMTree.IsModNode( ClickedNode ) )
		{
			return;
		}

		if ( IsExternalModNode( ClickedNode ) )
		{
			ModPriorityRequested?.Invoke( ClickedNode.Text );
		}
		else if ( Categories.UsesDlcRuntimeOrder( ClickedNode.Parent?.Parent?.Tag as ModFileCategory ) )
		{
			return;
		}
		else
		{
			_inlineRenameNode = ClickedNode;
			ClickedNode.BeginEdit();
		}
	}

	protected override void OnBeforeLabelEdit( NodeLabelEditEventArgs e )
	{
		base.OnBeforeLabelEdit( e );

		bool allowEdit = ReferenceEquals( e.Node, _inlineRenameNode );

		_inlineRenameNode = null;
		e.CancelEdit = !allowEdit || e.Node == null || !SMTree.IsModNode( e.Node );
	}

	// Cancel native label changes; ModFolderRenameRequested updates the row after a successful rename.
	protected override void OnAfterLabelEdit( NodeLabelEditEventArgs e )
	{
		base.OnAfterLabelEdit( e );
		e.CancelEdit = true;

		if ( e.Node == null || e.Label == null || !SMTree.IsModNode( e.Node ) )
		{
			return;
		}

		string requestedName = e.Label.Trim();

		if ( requestedName.Length == 0 || requestedName.Equals( e.Node.Text, StringComparison.Ordinal ) )
		{
			return;
		}

		ModFolderRenameRequested?.Invoke( this, new ModFolderRenameRequestedEventArgs( e.Node.Text, requestedName ) );
	}

	private void ContextToggleMod( object sender, EventArgs e )
	{
		string modName = RightClickedNode.Text;
		// Disabling a source removes its row, and the file's row with it when no conflict is left.
		TreeNode fileNode = RightClickedNode.Parent;

		CurrentLoadOrder().ToggleModByName( modName );
		SetStylesForCustomLoadOrder();

		if ( fileNode?.TreeView is not null && CanCheckNode( fileNode ) )
		{
			fileNode.Checked = fileNode.AreAllVisibleCheckboxesChecked();
		}

		Program.MainFrm.SetMergeButtonTextIfValidSelection();
	}

	private void ContextRemoveFromCustomLoadOrder( object sender, EventArgs e )
	{
		CurrentLoadOrder().RemoveMod( RightClickedNode.Text );
		SetStylesForCustomLoadOrder();
	}

	public void SetStylesForCustomLoadOrder()
		=> SetStylesForCustomLoadOrder( updateStatus: true );

	internal void SetStylesForCustomLoadOrder( bool updateStatus )
	{
		bool mainTree = ReferenceEquals( Program.MainFrm?.treConflicts, this );

		if ( mainTree )
		{
			Program.MainFrm.RefreshConflictNodes();
			Program.MainFrm.RestyleMergeOutputs();
			Program.MainFrm.RefreshPriorityTradeoffToolTips();
		}

		foreach ( TreeNode fileNode in base.SymbolNodes )
		{
			ModFile conflict = fileNode.GetMetadata()?.ModFile;

			if ( conflict != null )
			{
				string description = conflict.GetSemanticConflictDescription();

				if ( fileNode.ToolTipText != description )
					Program.MainFrm?.ResetConflictToolTip();

				fileNode.ToolTipText = description;
			}

			IEnumerable<string> modNames = from treeNode in fileNode.GetTreeNodes()
										   select treeNode.Text;
			string topPriorityMod = conflict != null
				? ( CurrentLoadOrder().HasResolvedConflict( conflict ) ? CurrentLoadOrder().GetTopPriorityEnabledMod( conflict ) : null )
				: ( CurrentLoadOrder().HasResolvedConflict( modNames ) ? CurrentLoadOrder().GetTopPriorityEnabledMod( modNames ) : null );

			foreach ( TreeNode modNode in fileNode.GetTreeNodes() )
			{
				ModLoadSetting loadSetting = CurrentLoadOrder().GetModLoadSettingByName( modNode.Text );
				bool isTopPriority = modNode.Text.EqualsIgnoreCase( topPriorityMod );
				Color rowColor = ConflictSourceColor( conflict, modNode.GetMetadata()?.FileHash, loadSetting, isTopPriority );
				ConflictSourceRole role = GetConflictSourceRole( conflict, modNode.GetMetadata()?.FileHash, loadSetting, isTopPriority );

				modNode.ForeColor = rowColor;
				StyledTreeView.SetActiveTag( modNode, role == ConflictSourceRole.TopPriority );

				if ( modNode.Tag is NodeMetadata metadata )
					metadata.ForeColor = rowColor;

				FileHash source = modNode.GetMetadata()?.FileHash;
				bool isDisabled = CurrentLoadOrder().IsModDisabledByName( modNode.Text );

				modNode.NodeFont = NodeStyleFont( ConflictSourceFontStyle( conflict, source, isDisabled ) );

				if ( isDisabled )
				{
					modNode.Checked = false;
				}

				string sourceDescription = mainTree
					? Program.MainFrm.GetConflictSourceToolTip( conflict, source, loadSetting, isTopPriority, isDisabled )
					: DescribeConflictSource( conflict, source, loadSetting, isTopPriority, isDisabled );

				if ( modNode.ToolTipText != sourceDescription )
					Program.MainFrm?.ResetConflictToolTip();

				modNode.ToolTipText = sourceDescription;
			}
		}

		foreach ( TreeNode node in FileNodes.Where( node => node.GetMetadata()?.MergeGroup != null ) )
		{
			node.Text = node.GetMetadata().MergeGroup.Label;
			node.ToolTipText = node.GetMetadata().MergeGroup.Description;
		}

		Sort();
		RefreshMergeCheckboxes();

		if ( mainTree )
			Program.MainFrm.RefreshLinkedLooseToolTips();

		if ( mainTree )
			Program.MainFrm.SetMergeButtonTextIfValidSelection();

		if ( updateStatus )
			Program.MainFrm?.UpdateStatusText();
	}

	internal static FontStyle ConflictSourceFontStyle( ModFile conflict, FileHash source, bool isDisabled, bool isSearchMatch = false )
	{
		FontStyle style = isDisabled ? FontStyle.Strikeout : FontStyle.Regular;

		if ( isSearchMatch )
			style |= FontStyle.Bold;

		if ( ReferenceEquals( conflict?.Category, Categories.PrecompiledScript )
			&& source?.PrecompiledScriptKinds.HasFunctionImplementation() == true )
			style |= FontStyle.Italic;

		return style;
	}

	// Compiled code can bypass loose changes even when the loose mod has higher priority.
	// Those sources are dimmed and excluded from the Active tag.
	internal static ConflictSourceRole GetConflictSourceRole(
		ModFile conflict,
		FileHash source,
		ModLoadSetting setting,
		bool isTopPriority )
	{
		if ( source?.IsExternal == true || setting?.IsExternal == true )
			return ConflictSourceRole.External;

		if ( ReferenceEquals( conflict?.Category, Categories.PrecompiledScript ) )
		{
			if ( conflict.RelativePath.StartsWith( "Enums\\", StringComparison.OrdinalIgnoreCase ) )
				return ConflictSourceRole.Plain;

			var analysis = conflict.GetPrecompiledAnalysis();

			if ( source?.HasPrecompiledScriptDefinition == true && analysis.HasMultipleCompiledSources )
				return ConflictSourceRole.Plain;

			if ( analysis.BypassedLooseSources.Contains( source ) )
				return ConflictSourceRole.Dimmed;
		}

		return isTopPriority ? ConflictSourceRole.TopPriority : ConflictSourceRole.Dimmed;
	}

	internal static Color ConflictSourceColor( ModFile conflict, FileHash source, ModLoadSetting setting, bool isTopPriority )
	{
		return GetConflictSourceRole( conflict, source, setting, isTopPriority ) switch
		{
			ConflictSourceRole.External => ThemeManager.ModIoColor,
			ConflictSourceRole.Plain => ThemeManager.CurrentTheme.ForeColor,
			ConflictSourceRole.TopPriority => ThemeManager.CurrentTheme.AcceptButtonBackColor,
			_ => ThemeManager.CurrentTheme.ForeDimmedColor
		};
	}

	internal static string DescribeRuntimePosition( ModLoadSetting loadSetting )
	{
		if ( loadSetting?.IsExternal == true )
			return loadSetting.Priority.HasValue
				? UiText.F( "ConflictTree", $"Mod.io priority {loadSetting.Priority.Value}." )
				: UiText.T( "ConflictTree", "Mod.io priority unavailable." );

		if ( loadSetting?.Priority.HasValue == true )
			return UiText.F( "ConflictTree", $"mods.settings priority {loadSetting.Priority.Value}." );

		return UiText.T( "ConflictTree", "Alphabetical load order." );
	}

	internal static string DescribeConflictSource(
		ModFile conflict,
		FileHash source,
		ModLoadSetting setting,
		bool isActive,
		bool isDisabled )
	{
		if ( isDisabled )
			return UiText.T( "ConflictTree", "Disabled." );

		if ( BlobConflictSelection.IsUnchangedSource( conflict, source?.Name ) )
			return UiText.T( "ConflictTree", "Shown for context. This mod's files are not changed by this merge." );

		if ( ReferenceEquals( conflict?.Category, Categories.XmlDefinitionOverride ) )
			return XmlOverrideToolTips.DescribeSource( conflict, source );

		if ( ReferenceEquals( conflict?.Category, Categories.PrecompiledScript ) && conflict.RelativePath.StartsWith( "Enums\\", StringComparison.OrdinalIgnoreCase ) )
			return conflict.GetSemanticConflictDescription();

		if ( conflict?.IsScopedMember == true )
			return UiText.T(
				"ScopedMembers",
				"This mod supplies a competing field definition or starting value. See Diagnostics for merge options." );

		List<string> lines = new();

		if ( ReferenceEquals( conflict?.Category, Categories.PrecompiledScript ) )
		{
			string wrapperDescription = conflict.GetPrecompiledAnalysis().DescribeSource( source );

			if ( wrapperDescription != null )
				return wrapperDescription;

			if ( source?.HasPrecompiledScriptDefinition == true )
			{
				if ( conflict.HasBlobLooseConflict && !conflict.HasPrecompiledPriorityConflict )
					return conflict.HasBlockedLooseWrapperConflict
						? UiText.T(
							"ConflictTree",
							"Compiled script (.rsblob): replaces the wrapper's target.\nThe loose-script wrapper may be skipped." )
						: UiText.T(
							"ConflictTree",
							"Compiled script (.rsblob): the game uses this version.\nIt overrides the overlapping loose-script changes, regardless of priority." );

				lines.Add( isActive ? UiText.T( "ConflictTree", "Compiled script (.rsblob): selected by load order." ) : UiText.T( "ConflictTree", "Compiled script (.rsblob)." ) );
			}
			else if ( conflict.HasBlockedLooseWrapperConflict && ( ( source?.LooseScriptKinds ?? ScriptSymbolKind.None ) & ScriptSymbolKind.WrappedFunction ) != 0 )
				return UiText.T(
					"ConflictTree",
					"Loose script (.ws): adds a wrapper around this function.\nA compiled replacement may bypass it; changing priority is not a fix." );
			else
				return UiText.T(
					"ConflictTree",
					"Loose script (.ws): these changes are overridden by a compiled script (.rsblob).\nPriority changes and loose-script merging cannot make them apply here." );
		}
		else
		{
			if ( !string.IsNullOrWhiteSpace( source?.ConflictDetail ) )
			{
				lines.Add( source.ConflictDetail.Trim() );
			}

			if ( isActive )
				lines.Add( UiText.T( "ConflictTree", "Active source." ) );
		}

		bool external = source?.IsExternal == true || setting?.IsExternal == true;

		lines.Add( Categories.UsesDlcRuntimeOrder( conflict?.Category ) && !external
			? UiText.F( "ConflictTree", $"DLC load order: {source?.RuntimeOrderName ?? source?.Name}." )
			: DescribeRuntimePosition( setting ) );

		return string.Join( "\n", lines );
	}

	private bool IsExternalModNode( TreeNode node )
	{
		return node?.GetMetadata()?.FileHash?.IsExternal == true
			|| CurrentLoadOrder()?.GetModLoadSettingByName( node?.Text )?.IsExternal == true;
	}

	internal void ApplyInitialExpansion( bool collapseNotMergeable, bool collapseCustomLoadOrder, bool collapseIdentical )
	{
		foreach ( TreeNode category in Nodes )
		{
			bool collapseCategory = collapseNotMergeable && category.Tag is ModFileCategory { IsSupported: false } && !CanCheckNode( category );
			bool compareRuns = collapseIdentical && !ReferenceEquals( category.Tag, Categories.Script )
				&& !ReferenceEquals( category.Tag, Categories.PrecompiledScript );
			string[] previousMods = null;

			foreach ( TreeNode file in category.Nodes )
			{
				if ( file.GetMetadata()?.MergeGroup != null )
				{
					SetLinkedGroupExpanded( file, !collapseCustomLoadOrder, collapseIdentical );

					continue;
				}

				string[] mods = compareRuns ? file.Nodes.Cast<TreeNode>().Select( node => node.Text )
					.OrderBy( name => name, StringComparer.InvariantCultureIgnoreCase ).ToArray() : null;
				bool repeat = compareRuns && previousMods != null
					&& previousMods.SequenceEqual( mods, StringComparer.InvariantCultureIgnoreCase );

				if ( collapseCustomLoadOrder || repeat )
					file.Collapse();
				else
					file.Expand();

				previousMods = mods;
			}

			if ( collapseCategory )
				category.Collapse();
			else
				category.Expand();
		}
	}

	// collapseIdentical keeps consecutive symbols with the same providers from repeating an expanded list.
	internal static void SetLinkedGroupExpanded( TreeNode group, bool expanded, bool collapseIdentical = false )
	{
		foreach ( TreeNode node in new[] { group }.Concat( Descendants( group ) ).Where( node => node.Nodes.Count > 0 ) )
			if ( expanded )
				node.Expand();
			else
				node.Collapse();

		if ( !expanded || !collapseIdentical )
			return;

		string[] previousMods = null;

		foreach ( TreeNode symbol in Descendants( group ).Where( node => node.GetMetadata()?.IsLinkedSymbol == true ) )
		{
			string[] mods = symbol.Nodes.Cast<TreeNode>().Select( source => source.Text )
				.Distinct( StringComparer.OrdinalIgnoreCase ).Order( StringComparer.OrdinalIgnoreCase ).ToArray();

			if ( mods is { Length: > 0 } && previousMods != null && previousMods.SequenceEqual( mods, StringComparer.OrdinalIgnoreCase ) )
				symbol.Collapse();

			previousMods = mods;
		}
	}

	// Linked groups expand as a unit. Outside script categories, the collapse setting also hides
	// repeated source lists so search results stay manageable.
	internal static int ExpandConflictSearchResults(
		TreeView treeView,
		bool collapseIdenticalConflicts,
		bool collapseCustomLoadOrder = false )
	{
		int expandedFileCount = 0;

		foreach ( TreeNode categoryNode in treeView.Nodes )
		{
			categoryNode.Expand();

			bool collapseIdenticalRuns = collapseIdenticalConflicts && categoryNode.Text != "Scripts"
				&& !ReferenceEquals( categoryNode.Tag, Categories.PrecompiledScript );
			List<string> previousModNames = null;

			foreach ( TreeNode fileNode in categoryNode.Nodes )
			{
				if ( fileNode.GetMetadata()?.MergeGroup != null )
				{
					bool expand = !collapseCustomLoadOrder && expandedFileCount < MaxAutomaticallyExpandedSearchFiles;

					SetLinkedGroupExpanded( fileNode, expand, collapseIdenticalConflicts );

					if ( expand )
						expandedFileCount++;

					continue;
				}

				List<string> currentModNames = fileNode.Nodes.Cast<TreeNode>()
					.Select( node => node.Text )
					.OrderBy( name => name, StringComparer.InvariantCultureIgnoreCase )
					.ToList();
				bool repeatsPreviousConflict = collapseIdenticalRuns
					&& previousModNames != null
					&& previousModNames.SequenceEqual( currentModNames, StringComparer.InvariantCultureIgnoreCase );

				if ( !repeatsPreviousConflict && expandedFileCount < MaxAutomaticallyExpandedSearchFiles )
				{
					fileNode.Expand();
					expandedFileCount++;
				}
				else if ( fileNode.IsExpanded )
				{
					fileNode.Collapse();
				}

				previousModNames = currentModNames;
			}
		}

		return expandedFileCount;
	}
}
