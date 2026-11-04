using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using WitcherScriptMerger.Controls;
using WitcherScriptMerger.FileIndex;
using WitcherScriptMerger.Inventory;
using WitcherScriptMerger.LoadOrder;
using WitcherScriptMerger.Theming;

namespace WitcherScriptMerger.Forms;

internal partial class MainForm
{
	// Keep the text as a function so it can be translated again when the language changes.
	private static TextBox CreateConflictSearchBox(
		string name,
		Func<string> accessibleName,
		Func<string> placeholder,
		Padding margin,
		int tabIndex )
	{
		return LiveText.Live( new TextBox
		{
			AccessibleName = accessibleName(),
			AutoCompleteMode = AutoCompleteMode.SuggestAppend,
			AutoCompleteSource = AutoCompleteSource.CustomSource,
			Dock = DockStyle.Fill,
			Font = new Font( "Segoe UI", 10.5f, FontStyle.Regular, GraphicsUnit.Point ),
			Margin = margin,
			Name = name,
			PlaceholderText = placeholder(),
			TabIndex = tabIndex
		}, control =>
		{
			LiveText.BindAccessibleName( control, accessibleName );
			LiveText.BindPlaceholderText( control, placeholder );
		} );
	}

	private void CreateConflictSearchBoxes()
	{
		tlpConflictSearch = new TableLayoutPanel
		{
			AutoSize = false,
			ColumnCount = 2,
			Dock = DockStyle.Fill,
			Margin = new Padding( 0 ),
			Name = "tlpConflictSearch",
			Padding = new Padding( 12, 5, 12, 8 ),
			RowCount = 1,
			Size = new Size( 400, 56 )
		};
		tlpConflictSearch.ColumnStyles.Add( new ColumnStyle( SizeType.Percent, 50f ) );
		tlpConflictSearch.ColumnStyles.Add( new ColumnStyle( SizeType.Percent, 50f ) );
		txtConflictModSearch = CreateConflictSearchBox(
			"txtConflictModSearch",
			() => UiText.T( "MainForm", "Search conflicts by mod name" ),
			() => UiText.T( "MainForm", "Search mod names..." ),
			new Padding( 0, 1, 4, 0 ),
			18 );
		txtConflictFileSearch = CreateConflictSearchBox(
			"txtConflictFileSearch",
			() => UiText.T( "MainForm", "Search conflicts by file name, path, or wildcard" ),
			() => UiText.T( "MainForm", "Search files (e.g. *.swf)..." ),
			new Padding( 4, 1, 0, 0 ),
			19 );
		pnlConflictModSearch = new ModernTextBoxHost( txtConflictModSearch )
		{
			Margin = new Padding( 0, 1, 4, 1 ),
			Name = "pnlConflictModSearch",
			ShowSearchGlyph = true
		};
		pnlConflictFileSearch = new ModernTextBoxHost( txtConflictFileSearch )
		{
			Margin = new Padding( 4, 1, 0, 1 ),
			Name = "pnlConflictFileSearch",
			ShowSearchGlyph = true
		};
		tlpConflictSearch.Controls.Add( pnlConflictModSearch, 0, 0 );
		tlpConflictSearch.Controls.Add( pnlConflictFileSearch, 1, 0 );
		tlpConflictsHeader.SetColumnSpan( tlpConflictSearch, 3 );
		tlpConflictsHeader.Controls.Add( tlpConflictSearch, 0, 1 );
		LiveText.BindToolTip(
			toolTip1,
			txtConflictModSearch,
			() => UiText.T( "MainForm", "Type part of a mod name. Press Ctrl+F to focus or Esc to clear." ) );
		LiveText.BindToolTip(
			toolTip1,
			txtConflictFileSearch,
			() => UiText.T(
				"MainForm",
				"Type part of a file name or path, or use * and ? wildcards. Press Ctrl+Shift+F to focus or Esc to clear." ) );
		txtConflictModSearch.TextChanged += ConflictModSearch_TextChanged;
		txtConflictFileSearch.TextChanged += ConflictFileSearch_TextChanged;
	}

	private void CreateConflictSearchTimerAndFonts()
	{
		conflictSearchTimer = new System.Windows.Forms.Timer( components )
		{
			Interval = 250
		};
		conflictSearchTimer.Tick += ConflictSearchTimer_Tick;
		conflictSearchCategoryFont = new Font( "Segoe UI", 14.75f, FontStyle.Bold, GraphicsUnit.Point );
		conflictSearchMatchFont = new Font( "Segoe UI", 11.75f, FontStyle.Bold, GraphicsUnit.Point );
	}

	private void CreateConflictSearchResults()
	{
		treConflictSearchResults = new ConflictSelectionTree
		{
			FileSelectionEnabled = true,
			BorderStyle = BorderStyle.None,
			CheckBoxes = true,
			Dock = DockStyle.Fill,
			Font = new Font( "Segoe UI", 11f, FontStyle.Regular, GraphicsUnit.Point ),
			FullRowSelect = true,
			HideSelection = false,
			ItemHeight = 28,
			LabelEdit = true,
			Margin = new Padding( 4, 0, 4, 0 ),
			Name = "treConflictSearchResults",
			ShowLines = false,
			ShowNodeToolTips = false,
			Sorted = false,
			TabIndex = 1,
			TabStop = true,
			TreeViewNodeSorter = null
		};
		treConflictSearchResults.BeforeLabelEdit += ConflictSearchResults_BeforeLabelEdit;
		treConflictSearchResults.BeforeCheck += ( _, e ) =>
		{
			if ( !e.Node.Checked && !CanCheckSearchMergeInput( e.Node ) )
				e.Cancel = true;
		};
		treConflictSearchResults.AfterCheck += ( _, e ) => SearchGroupChecked( e );
		treConflictSearchResults.NativeItemsReady += RefreshSearchGroupCheckboxes;
		treConflictSearchResults.AfterLabelEdit += ConflictSearchResults_AfterLabelEdit;
		treConflictSearchResults.NodeMouseDoubleClick += ConflictSearchResults_NodeMouseDoubleClick;
		treConflictSearchResults.NodeMouseClick += ConflictSearchResults_NodeMouseClick;
		treConflictSearchResults.FileContextMenuRequested += ( node, point ) => ShowConflictSearchContextMenu( node, point );
	}

	private void CreateConflictSearchMenu()
	{
		conflictSearchModMenu = new ContextMenuStrip
		{
			Name = "conflictSearchModMenu",
			ShowImageMargin = false,
			Renderer = new MenuRenderer( ThemeManager.CurrentTheme, 11 )
		};
		AddMenuItem(
			conflictSearchModMenu, "inspectSearchConflict", () => UiText.T( "InspectorLaunch", "Inspect" ),
			async ( _, _ ) => await InspectAsync( conflictSearchContextNode ) );
		AddMenuItem(
			conflictSearchModMenu,
			"setSearchResultPriority",
			() => UiText.T( "MainForm", "Set Priority..." ),
			SetSearchResultPriority_Click,
			() => UiText.T( "MainForm", "Adds or updates this mod's priority in mods.settings" ) );
		AddMenuItem(
			conflictSearchModMenu,
			"ignoreSearchConflicts",
			() => UiText.T( "MainForm", "Ignore selected conflicts until files change" ),
			async ( _, _ ) => await IgnoreSelectedConflictsAsync( treConflictSearchResults ) );
		AddMenuItem(
			conflictSearchModMenu,
			"ignoreSearchPermanently",
			() => UiText.T( "MainForm", "Ignore selected files permanently" ),
			async ( _, _ ) => await IgnoreSelectedConflictsAsync( treConflictSearchResults, permanently: true ),
			() => UiText.T(
				"MainForm",
				"Adds the selected file paths to Menu > Permanent Ignore Rules, where they can be removed later." ) );
	}

	private void ApplyThemeToConflictSearchResults()
	{
		treConflictSearchResults.ForeColor = ThemeManager.CurrentTheme.ForeDimmedColor;
		treConflictSearchResults.BackColor = ThemeManager.CurrentTheme.TreeViewBackColor;
		treConflictSearchResults.FullRowSelect = false;
		treConflictSearchResults.HideSelection = false;
		treConflictSearchResults.ApplyListSize();
	}

	private bool IsConflictSearchActive => !string.IsNullOrWhiteSpace( txtConflictModSearch.Text )
		|| !string.IsNullOrWhiteSpace( txtConflictFileSearch.Text );

	private bool IsFileSearchActive => !string.IsNullOrWhiteSpace( txtConflictFileSearch.Text );

	private void ConflictModSearch_TextChanged( object sender, EventArgs e )
	{
		HandleConflictSearchTextChanged( txtConflictModSearch, txtConflictFileSearch );
	}

	private void ConflictFileSearch_TextChanged( object sender, EventArgs e )
	{
		HandleConflictSearchTextChanged( txtConflictFileSearch, txtConflictModSearch );
	}

	// Clearing the other box keeps the mod and file searches separate.
	private void HandleConflictSearchTextChanged( TextBox activeSearch, TextBox otherSearch )
	{
		if ( !activeSearch.Enabled || changingConflictSearchText )
		{
			return;
		}

		if ( !string.IsNullOrWhiteSpace( activeSearch.Text ) && !string.IsNullOrEmpty( otherSearch.Text ) )
		{
			changingConflictSearchText = true;
			otherSearch.Clear();
			changingConflictSearchText = false;
		}

		conflictSearchTimer.Stop();

		if ( !IsConflictSearchActive )
		{
			UpdateConflictSearchView();

			return;
		}

		conflictSearchTimer.Start();
	}

	private void ClearConflictSearch()
	{
		changingConflictSearchText = true;
		txtConflictModSearch.Clear();
		txtConflictFileSearch.Clear();
		changingConflictSearchText = false;
		UpdateConflictSearchView();
	}

	private void ConflictSearchTimer_Tick( object sender, EventArgs e )
	{
		conflictSearchTimer.Stop();
		UpdateConflictSearchView();
	}

	private void UpdateConflictSearchSuggestions()
	{
		AutoCompleteStringCollection modSuggestions = new AutoCompleteStringCollection();
		AutoCompleteStringCollection fileSuggestions = new AutoCompleteStringCollection();

		if ( conflictSearchSource.Count > 0 )
		{
			string[] modNames = conflictSearchSource
				.SelectMany( conflict => conflict.GetConflictDisplaySources() )
				.Select( mod => mod.Name )
				.Where( name => !string.IsNullOrWhiteSpace( name ) )
				.Distinct( StringComparer.InvariantCultureIgnoreCase )
				.OrderBy( value => value, StringComparer.InvariantCultureIgnoreCase )
				.ToArray();
			IEnumerable<string> filePaths = conflictSearchSource
				.Select( conflict => conflict.RelativePath )
				.Where( path => !string.IsNullOrWhiteSpace( path ) );
			IEnumerable<string> fileNames = filePaths.Select( Path.GetFileName );
			IEnumerable<string> extensionPatterns = filePaths
				.Select( Path.GetExtension )
				.Where( extension => !string.IsNullOrWhiteSpace( extension ) )
				.Select( extension => "*" + extension );
			string[] fileSuggestionValues = filePaths
				.Concat( fileNames )
				.Concat( extensionPatterns )
				.Distinct( StringComparer.InvariantCultureIgnoreCase )
				.OrderBy( value => value, StringComparer.InvariantCultureIgnoreCase )
				.ToArray();

			modSuggestions.AddRange( modNames );
			fileSuggestions.AddRange( fileSuggestionValues );
		}

		txtConflictModSearch.AutoCompleteCustomSource = modSuggestions;
		txtConflictFileSearch.AutoCompleteCustomSource = fileSuggestions;
	}

	private void UpdateConflictSearchSource()
	{
		conflictSearchSource = treConflicts.SymbolNodes
			.Select( fileNode => fileNode.GetMetadata()?.ModFile )
			.Where( conflict => conflict != null )
			.Distinct()
			.ToList();
	}

	// Search uses copied rows so filtering can leave the original tree and its selection intact.
	private void UpdateConflictSearchView()
	{
		bool isFileSearch;
		string query;
		List<ModFile> matches = new List<ModFile>();

		ResetConflictToolTip();

		if ( !IsConflictSearchActive )
		{
			ShowDefaultConflictView();
			UpdateStatusText();

			return;
		}

		isFileSearch = IsFileSearchActive;
		query = ( isFileSearch ? txtConflictFileSearch.Text : txtConflictModSearch.Text ).Trim();
		treConflictSearchResults.BeginUpdate();

		try
		{
			treConflictSearchResults.ClearFileSelection();
			treConflictSearchResults.Sorted = false;
			searchLooseInputs.Clear();
			treConflictSearchResults.Nodes.Clear();

			if ( conflictSearchSource.Count > 0 )
				matches = AddConflictSearchMatches( query, isFileSearch );

			CompleteLinkedSearchGroups( matches, query, isFileSearch );

			if ( matches.Count == 0 )
				ShowNoConflictSearchMatches( query );
			else
				ExpandConflictSearchMatches();
		}
		finally
		{
			treConflictSearchResults.EndUpdate();
		}

		ShowConflictSearchResults( matches.Count );
		RefreshSearchGroupCheckboxes();
		RefreshLinkedLooseToolTips();
		treConflictSearchResults.ScrollToTop();
		RefreshPaneCounts();
	}

	private List<ModFile> AddConflictSearchMatches( string query, bool isFileSearch )
	{
		Dictionary<ModFileCategory, TreeNode> categoryNodes = new Dictionary<ModFileCategory, TreeNode>();
		List<ModFile> matches = ( isFileSearch
			? ConflictSearch.FindFileMatches( conflictSearchSource, query )
			: ConflictSearch.FindModMatches( conflictSearchSource, query ) )
			.OrderBy( conflict => conflict.Category.OrderIndex )
			.ThenBy( conflict => conflict.RelativePath, StringComparer.InvariantCultureIgnoreCase )
			.ToList();

		foreach ( ModFile conflict in matches )
			AddConflictSearchRow( ConflictSearchCategoryNode( categoryNodes, conflict.Category ), conflict, query, isFileSearch );

		return matches;
	}

	private TreeNode ConflictSearchCategoryNode( Dictionary<ModFileCategory, TreeNode> categoryNodes, ModFileCategory category )
	{
		if ( !categoryNodes.TryGetValue( category, out TreeNode categoryNode ) )
		{
			categoryNode = new TreeNode
			{
				Text = category.DisplayName,
				ToolTipText = category.ToolTipText,
				NodeFont = conflictSearchCategoryFont,
				ForeColor = ThemeManager.CurrentTheme.ForeColor,
				Tag = category
			};
			categoryNodes.Add( category, categoryNode );
			treConflictSearchResults.Nodes.Add( categoryNode );
		}

		return categoryNode;
	}

	private void AddConflictSearchRow( TreeNode categoryNode, ModFile conflict, string query, bool isFileSearch )
	{
		IReadOnlyList<FileHash> displaySources = conflict.GetConflictDisplaySources();
		TreeNode fileNode = new TreeNode
		{
			Text = conflict.RelativePath,
			ForeColor = ThemeManager.CurrentTheme.ForeBrightColor,
			NodeFont = isFileSearch ? conflictSearchMatchFont : null,
			ToolTipText = conflict.GetSemanticConflictDescription(),
			Tag = conflict
		};
		string topPriorityMod;

		categoryNode.Nodes.Add( fileNode );
		topPriorityMod = session.LoadOrder.HasResolvedConflict( conflict )
			? session.LoadOrder.GetTopPriorityEnabledMod( conflict )
			: null;

		foreach ( FileHash mod in displaySources.OrderBy( mod => mod, Comparer<FileHash>.Create( ( left, right ) =>
			CustomLoadOrder.CompareConflictSourcesForSettings( session.LoadOrder.Mods, conflict, left, right ) ) ) )
		{
			bool isMatch = !isFileSearch && ConflictSearch.IsModNameMatch( mod.Name, query );
			TreeNode modNode = new TreeNode
			{
				Text = mod.Name,
				Tag = mod
			};

			ApplyConflictSearchPriorityStyle( modNode, topPriorityMod, isMatch, conflict );
			fileNode.Nodes.Add( modNode );
		}
	}

	private void ShowNoConflictSearchMatches( string query )
	{
		treConflictSearchResults.Sorted = false;
		treConflictSearchResults.Nodes.Add( LiveText.Live( new TreeNode
		{
			ForeColor = ThemeManager.CurrentTheme.ForeDimmedColor
		}, node => LiveText.BindText( node, () => UiText.F( "MainForm", $"No conflicts found for \"{query}\"." ) ) ) );
	}

	private void ExpandConflictSearchMatches()
	{
		ConflictTree.ExpandConflictSearchResults(
			treConflictSearchResults,
			Program.Settings.Get<bool>( "CollapseIdenticalConflicts" ),
			Program.Settings.Get<bool>( "CollapseCustomLoadOrder" ) );
	}

	// Use the same styling rules for search results and the original tree.
	private void ApplyConflictSearchPriorityStyle(
		TreeNode modNode,
		string topPriorityMod,
		bool isSearchMatch,
		ModFile conflict )
	{
		FileHash source = modNode.GetMetadata()?.FileHash ?? modNode.Tag as FileHash;
		ModLoadSetting loadSetting = session.LoadOrder.GetModLoadSettingByName( modNode.Text );
		bool isTopPriority = modNode.Text.EqualsIgnoreCase( topPriorityMod );
		bool isDisabled = session.LoadOrder.IsModDisabledByName( modNode.Text );

		ConflictSourceRole role = ConflictTree.GetConflictSourceRole( conflict, source, loadSetting, isTopPriority );

		modNode.ForeColor = ConflictTree.ConflictSourceColor( conflict, source, loadSetting, isTopPriority );
		StyledTreeView.SetActiveTag( modNode, role == ConflictSourceRole.TopPriority );
		modNode.NodeFont = treConflictSearchResults.NodeStyleFont(
			ConflictTree.ConflictSourceFontStyle( conflict, source, isDisabled, isSearchMatch ) );
		modNode.ToolTipText = GetConflictSourceToolTip( conflict, source, loadSetting, isTopPriority, isDisabled );
	}

	internal void RefreshConflictSearchResultsIfActive()
	{
		if ( IsConflictSearchActive )
		{
			UpdateConflictSearchView();
		}
	}

	private void ConflictSearchResults_NodeMouseDoubleClick( object sender, TreeNodeMouseClickEventArgs e )
	{
		if ( e.Button != MouseButtons.Left || e.Node == null || !SMTree.IsModNode( e.Node ) )
		{
			return;
		}

		if ( e.Node.Tag is FileHash { IsExternal: true }
			|| session.LoadOrder?.GetModLoadSettingByName( e.Node.Text )?.IsExternal == true )
		{
			SetPriorityForMod( e.Node.Text );
		}
		else if ( Categories.UsesDlcRuntimeOrder( e.Node.Parent?.Parent?.Tag as ModFileCategory ) )
		{
			return;
		}
		else
		{
			conflictSearchInlineRenameNode = e.Node;
			e.Node.BeginEdit();
		}
	}

	private void ConflictSearchResults_BeforeLabelEdit( object sender, NodeLabelEditEventArgs e )
	{
		bool allowEdit = ReferenceEquals( e.Node, conflictSearchInlineRenameNode );

		conflictSearchInlineRenameNode = null;
		e.CancelEdit = !allowEdit || e.Node == null || !SMTree.IsModNode( e.Node );
	}

	private void ConflictSearchResults_AfterLabelEdit( object sender, NodeLabelEditEventArgs e )
	{
		e.CancelEdit = true;

		if ( e.Node == null || !SMTree.IsModNode( e.Node ) || e.Label == null )
		{
			return;
		}

		RequestModFolderRename( e.Node.Text, e.Label );
	}

	private void ConflictSearchResults_NodeMouseClick( object sender, TreeNodeMouseClickEventArgs e )
	{
		if ( e.Button != MouseButtons.Right || e.Node == null || e.Node.Level == 0 )
		{
			return;
		}

		ShowConflictSearchContextMenu( e.Node, e.Location );
	}

	// Search rows are copies. Route their menu actions to the original mod or file.
	private void ShowConflictSearchContextMenu( TreeNode node, Point location )
	{
		if ( node == null || node.Level == 0 )
			return;

		conflictSearchContextNode = node;
		var inspect = conflictSearchModMenu.Items["inspectSearchConflict"];
		inspect.Visible = CanInspect( node );
		inspect.Enabled = InspectorUnavailableReason.Length == 0;
		inspect.ToolTipText = InspectorUnavailableReason;

		bool vanillaAvailable = File.Exists( SearchVanillaFile() );

		conflictSearchModMenu.Items["openSearchVanillaFile"].Visible = vanillaAvailable;
		conflictSearchModMenu.Items["openSearchVanillaDir"].Visible = vanillaAvailable;
		conflictSearchContextModName = node.Text;

		ToolStripItem priorityItem = conflictSearchModMenu.Items["setSearchResultPriority"];
		ModLoadSetting loadSetting = SMTree.IsModNode( node )
			? session.LoadOrder?.GetModLoadSettingByName( node.Text )
			: null;
		bool isExternal = node.Tag is FileHash { IsExternal: true } || loadSetting?.IsExternal == true;
		bool usesDlcRuntimeOrder = Categories.UsesDlcRuntimeOrder( node.Parent?.Parent?.Tag as ModFileCategory );

		priorityItem.Visible = SMTree.IsModNode( node ) && ( !usesDlcRuntimeOrder || isExternal );
		LiveText.BindText(
			priorityItem,
			() => isExternal ? UiText.T( "MainForm", "Set Mod.io Priority..." ) : UiText.T( "MainForm", "Set Priority..." ) );
		LiveText.BindToolTipText( priorityItem, () => isExternal
			? UiText.T( "MainForm", "Changes this mod's position in modioModsConfig.json" )
			: UiText.T( "MainForm", "Adds or updates this mod's priority in mods.settings" ) );
		conflictSearchModMenu.Items["ignoreSearchConflicts"].Visible = ConflictSelectionTree.IsConflictFileNode( node )
			&& treConflictSearchResults.SelectedConflicts.Length > 0;
		conflictSearchModMenu.Items["ignoreSearchPermanently"].Visible = conflictSearchModMenu.Items["ignoreSearchConflicts"].Available;

		if ( conflictSearchModMenu.Items.OfType<ToolStripMenuItem>().Any( item => item.Available ) )
			conflictSearchModMenu.Show( treConflictSearchResults, location );
	}

	private void SetSearchResultPriority_Click( object sender, EventArgs e )
	{
		string modName = conflictSearchContextModName;

		conflictSearchContextModName = null;
		SetPriorityForMod( modName );
	}

	private void FocusActiveConflictSearch()
	{
		( IsFileSearchActive ? txtConflictFileSearch : txtConflictModSearch ).Focus();
	}

	// Remember the button state once, so ending a search restores the previous view.
	private void ShowConflictSearchResults( int matchCount )
	{
		tlpMain.Controls.Remove( treConflicts );
		tlpMain.Controls.Remove( lblNoConflicts );
		tlpMain.SetColumnSpan( treConflictSearchResults, 2 );

		if ( !tlpMain.Controls.Contains( treConflictSearchResults ) )
		{
			tlpMain.Controls.Add( treConflictSearchResults, 1, 1 );
		}

		if ( !conflictSearchActionStateCaptured )
		{
			createMergesEnabledBeforeSearch = btnCreateMerges.Enabled;
			createAllMergesEnabledBeforeSearch = btnCreateAllScriptMerges.Enabled;
			conflictSearchActionStateCaptured = true;
		}

		btnCreateMerges.Enabled = false;
		btnCreateAllScriptMerges.Enabled = false;
		LiveText.BindText(
			lblConflicts,
			() => IsFileSearchActive ? UiText.T( "MainForm", "File Search Results" ) : UiText.T( "MainForm", "Mod Search Results" ) );

		if ( conflictRefreshPending )
		{
			lblConflicts.Text += UiText.T( "MainForm", " (Refresh Pending)" );
		}

		UpdateStatusText();
		LiveText.BindText(
			lblStatusLeft1,
			() => matchCount == 1 ? UiText.F( "MainForm", $"{matchCount} matching file" ) : UiText.F( "MainForm", $"{matchCount} matching files" ) );
		lblStatusLeft2.Visible = false;
		SetMergeButtonTextIfValidSelection();
	}

	internal void RefreshNoConflictsAppearance()
	{
		if ( tlpMain.Controls.Contains( lblNoConflicts ) )
		{
			ShowDefaultConflictView();
		}
	}

	private void ShowDefaultConflictView()
	{
		tlpMain.Controls.Remove( treConflictSearchResults );

		if ( conflictSearchActionStateCaptured )
		{
			btnCreateMerges.Enabled = session.IsScanCurrent && createMergesEnabledBeforeSearch;
			btnCreateAllScriptMerges.Enabled = session.IsScanCurrent && createAllMergesEnabledBeforeSearch;
			conflictSearchActionStateCaptured = false;
		}

		if ( treConflicts.IsEmpty() )
		{
			LiveText.BindText( lblNoConflicts, () => scanFailed ? UiText.T( "MainForm", "Scan failed" ) : GetDiagnosticIssues().Count > 0
				? UiText.T( "MainForm", "No conflicts found in inspected resources" ) : UiText.T( "MainForm", "No conflicts detected" ) );
			tlpMain.Controls.Remove( treConflicts );
			lblNoConflicts.BackColor = ThemeManager.CurrentTheme.TreeViewBackColor;
			lblNoConflicts.ForeColor = ThemeManager.CurrentTheme.ForeDimmedColor;
			tlpMain.SetColumnSpan( lblNoConflicts, 2 );

			if ( !tlpMain.Controls.Contains( lblNoConflicts ) )
				tlpMain.Controls.Add( lblNoConflicts, 1, 1 );

			LiveText.BindText( lblConflicts, () => UiText.T( "MainForm", "Conflicts" ) );
		}
		else
		{
			tlpMain.Controls.Remove( lblNoConflicts );
			tlpMain.SetColumnSpan( treConflicts, 2 );

			if ( !tlpMain.Controls.Contains( treConflicts ) )
			{
				tlpMain.Controls.Add( treConflicts, 1, 1 );
			}

			LiveText.BindText( lblConflicts, () => UiText.T( "MainForm", "Conflicts" ) );
		}

		if ( conflictRefreshPending )
		{
			lblConflicts.Text += UiText.T( "MainForm", " (Refresh Pending)" );
		}
	}
}
