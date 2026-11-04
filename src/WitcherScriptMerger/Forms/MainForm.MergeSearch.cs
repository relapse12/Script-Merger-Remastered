using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using WitcherScriptMerger.Controls;

namespace WitcherScriptMerger.Forms;

// Keep the original nodes while filtering so clearing the search restores their selection.
internal partial class MainForm
{
	private TextBox txtMergeModSearch;
	private TextBox txtMergeFileSearch;
	private ModernTextBoxHost pnlMergeModSearch;
	private ModernTextBoxHost pnlMergeFileSearch;
	private TableLayoutPanel tlpMergeSearch;
	private Label lblNoMergeMatches;
	private System.Windows.Forms.Timer mergeSearchTimer;
	private bool changingMergeSearchText;
	private readonly List<(TreeNode Category, TreeNode[] Files)> mergeSearchSource = new List<(TreeNode Category, TreeNode[] Files)>();
	private bool IsMergeSearchActive => !string.IsNullOrWhiteSpace( txtMergeModSearch.Text )
		|| !string.IsNullOrWhiteSpace( txtMergeFileSearch.Text );

	private void InitializeMergeSearch()
	{
		TextBox CreateSearchBox(
			string name,
			System.Func<string> accessibleName,
			TextBox template,
			int tabIndex ) => LiveText.Live(
			new TextBox
		{
			Name = name,
			Dock = DockStyle.Fill,
			Font = new Font( template.Font, template.Font.Style ),
			PlaceholderText = template.PlaceholderText,
			AutoCompleteMode = AutoCompleteMode.SuggestAppend,
			AutoCompleteSource = AutoCompleteSource.CustomSource,
			TabIndex = tabIndex
		},
			control => LiveText.BindAccessibleName( control, accessibleName ) );

		txtMergeModSearch = CreateSearchBox(
			"txtMergeModSearch",
			() => UiText.T( "MainForm.MergeSearch", "Search merges by mod name" ),
			txtConflictModSearch,
			20 );
		txtMergeFileSearch = CreateSearchBox(
			"txtMergeFileSearch",
			() => UiText.T( "MainForm.MergeSearch", "Search merges by file name, path, or wildcard" ),
			txtConflictFileSearch,
			21 );
		pnlMergeModSearch = new ModernTextBoxHost( txtMergeModSearch ) { Name = "pnlMergeModSearch", ShowSearchGlyph = true };
		pnlMergeFileSearch = new ModernTextBoxHost( txtMergeFileSearch ) { Name = "pnlMergeFileSearch", ShowSearchGlyph = true };
		tlpMergeSearch = new TableLayoutPanel
		{
			Name = "tlpMergeSearch",
			Dock = DockStyle.Fill,
			Margin = new Padding( 0 ),
			Padding = tlpConflictSearch.Padding,
			ColumnCount = 2,
			RowCount = 1
		};
		tlpMergeSearch.Controls.Add( pnlMergeModSearch, 0, 0 );
		tlpMergeSearch.Controls.Add( pnlMergeFileSearch, 1, 0 );
		ConfigureResponsiveSearchLayout( tlpMergeSearch, pnlMergeModSearch, pnlMergeFileSearch, false, value => value );
		tlpMergesHeader.SetColumnSpan( tlpMergeSearch, 3 );
		tlpMergesHeader.Controls.Add( tlpMergeSearch, 0, 1 );
		toolTip1.SetToolTip( txtMergeModSearch, toolTip1.GetToolTip( txtConflictModSearch ) );
		toolTip1.SetToolTip( txtMergeFileSearch, toolTip1.GetToolTip( txtConflictFileSearch ) );
		mergeSearchTimer = new System.Windows.Forms.Timer( components ) { Interval = 250 };
		mergeSearchTimer.Tick += ( _, _ ) => UpdateMergeSearchView();
		txtMergeModSearch.TextChanged += ( _, _ ) => MergeSearchTextChanged( txtMergeModSearch, txtMergeFileSearch );
		txtMergeFileSearch.TextChanged += ( _, _ ) => MergeSearchTextChanged( txtMergeFileSearch, txtMergeModSearch );
		lblNoMergeMatches = LiveText.Live( new Label
		{
			Name = "lblNoMergeMatches",
			Text = UiText.T( "MainForm.MergeSearch", "No matching merges" ),
			Dock = DockStyle.Fill,
			TextAlign = ContentAlignment.MiddleCenter,
			Visible = false
		}, control => { LiveText.BindText( control, () => UiText.T( "MainForm.MergeSearch", "No matching merges" ) ); } );
		treMerges.Controls.Add( lblNoMergeMatches );
	}

	private void MergeSearchTextChanged( TextBox active, TextBox other )
	{
		if ( changingMergeSearchText || !active.Enabled )
			return;

		changingMergeSearchText = true;

		if ( !string.IsNullOrWhiteSpace( active.Text ) )
			other.Clear();

		changingMergeSearchText = false;
		mergeSearchTimer.Stop();

		if ( !IsMergeSearchActive )
			UpdateMergeSearchView();
		else
			mergeSearchTimer.Start();
	}

	private void ClearMergeSearch()
	{
		changingMergeSearchText = true;
		txtMergeModSearch.Clear();
		txtMergeFileSearch.Clear();
		changingMergeSearchText = false;
		UpdateMergeSearchView();
	}

	// Called only after a full tree refresh, never from the filtered view.
	private void UpdateMergeSearchSource()
	{
		mergeSearchSource.Clear();
		mergeSearchSource.AddRange( treMerges.CategoryNodes.Select( category =>
			(category, category.Nodes.Cast<TreeNode>().ToArray()) ) );
		UpdateMergeSearchSuggestions();
	}

	private void UpdateMergeSearchSuggestions()
	{
		var files = mergeSearchSource.SelectMany( group => group.Files ).SelectMany( MergeTree.OutputNodes ).ToArray();
		var mods = new AutoCompleteStringCollection();

		mods.AddRange( files.SelectMany( file => file.Nodes.Cast<TreeNode>() ).Select( node => node.Text )
			.Distinct( StringComparer.OrdinalIgnoreCase ).OrderBy( name => name, StringComparer.OrdinalIgnoreCase ).ToArray() );

		var paths = new AutoCompleteStringCollection();

		paths.AddRange( files.SelectMany( file => new[] { file.Text, Path.GetFileName( file.Text ) } )
			.Distinct( StringComparer.OrdinalIgnoreCase ).OrderBy( path => path, StringComparer.OrdinalIgnoreCase ).ToArray() );
		txtMergeModSearch.AutoCompleteCustomSource = mods;
		txtMergeFileSearch.AutoCompleteCustomSource = paths;
	}

	private void UpdateMergeSearchView()
	{
		mergeSearchTimer.Stop();

		if ( !txtMergeModSearch.Enabled )
			return;

		string modQuery = txtMergeModSearch.Text.Trim();
		string fileQuery = txtMergeFileSearch.Text.Trim();
		TreeNode selected = treMerges.SelectedNode;
		var sorter = treMerges.TreeViewNodeSorter;

		treMerges.BeginUpdate();
		treMerges.TreeViewNodeSorter = null;
		treMerges.Sorted = false;

		try
		{
			treMerges.Nodes.Clear();

			foreach ( var (category, files) in mergeSearchSource )
			{
				category.Nodes.Clear();

				var matches = files.Where( group => MergeTree.OutputNodes( group ).Any( file =>
					( fileQuery.Length == 0 || ConflictSearch.IsFileMatch( file.Text, fileQuery ) )
					&& ( modQuery.Length == 0 || file.Nodes.Cast<TreeNode>()
						.Any( mod => ConflictSearch.IsModNameMatch( mod.Text, modQuery ) ) ) ) ).ToArray();

				if ( matches.Length == 0 )
					continue;

				category.Nodes.AddRange( matches );
				treMerges.SynchronizeCategory( category );
				treMerges.Nodes.Add( category );
			}

			treMerges.TreeViewNodeSorter = sorter;
			treMerges.Sort();
			treMerges.ExpandAll();

			if ( selected?.TreeView == treMerges )
				treMerges.SelectedNode = selected;

			treMerges.RefreshCheckboxes();
		}
		finally
		{
			treMerges.TreeViewNodeSorter = sorter;
			treMerges.EndUpdateAtTop();
		}

		lblNoMergeMatches.Visible = IsMergeSearchActive && treMerges.Nodes.Count == 0;
		LiveText.BindText(
			btnDeleteAllMerges,
			() => IsMergeSearchActive ? UiText.T( "MainForm.MergeSearch", "Delete Matching Merges" ) : UiText.T( "MainForm.MergeSearch", "Delete &All Merges" ) );
		SetUnmergeButtonTextIfValidSelection();
		UpdateStatusText();
	}

	// Updating the search text after a rename must not start a new search.
	private void UpdateRenamedMergeSearchNodes( string oldName, string newName )
	{
		foreach ( TreeNode node in mergeSearchSource.SelectMany( group => group.Files )
			.SelectMany( MergeTree.OutputNodes )
			.SelectMany( file => file.Nodes.Cast<TreeNode>() ).Where( node => node.Text.EqualsIgnoreCase( oldName ) ) )
		{
			node.Text = newName;

			var metadata = node.GetMetadata();

			if ( metadata?.FileHash != null )
				metadata.FileHash.Name = newName;

			if ( metadata?.ModFile != null )
				metadata.FilePath = metadata.ModFile.GetModFile( newName, session.GamePaths );
		}

		if ( txtMergeModSearch.Text.Trim().EqualsIgnoreCase( oldName ) )
		{
			changingMergeSearchText = true;
			txtMergeModSearch.Text = newName;
			changingMergeSearchText = false;
		}

		UpdateMergeSearchSuggestions();

		if ( IsMergeSearchActive )
			UpdateMergeSearchView();
	}
}
