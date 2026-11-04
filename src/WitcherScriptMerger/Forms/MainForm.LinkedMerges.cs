#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using WitcherScriptMerger.BlobMerging;
using WitcherScriptMerger.Controls;
using WitcherScriptMerger.FileIndex;
using WitcherScriptMerger.Theming;

namespace WitcherScriptMerger.Forms;

internal partial class MainForm
{
	private TreeNode? conflictSearchContextNode;

	private void AddSearchVanillaActions()
	{
		var open = new ToolStripMenuItem( UiText.T( "SMTree", "Open Vanilla File" ) ) { Name = "openSearchVanillaFile" };
		var directory = new ToolStripMenuItem( UiText.T( "SMTree", "Open Vanilla File Directory" ) ) { Name = "openSearchVanillaDir" };

		LiveText.BindText( open, () => UiText.T( "SMTree", "Open Vanilla File" ) );
		LiveText.BindText( directory, () => UiText.T( "SMTree", "Open Vanilla File Directory" ) );
		open.Click += ( _, _ ) => { if ( SearchVanillaFile() is { Length: > 0 } path ) Program.TryOpenFile( path ); };
		directory.Click += ( _, _ ) => { if ( SearchVanillaFile() is { Length: > 0 } path ) Program.TryOpenFileLocation( path ); };
		conflictSearchModMenu.Items.AddRange( [open, directory] );
	}

	private string? SearchVanillaFile()
	{
		var file = ConflictSelectionTree.Model( conflictSearchContextNode! )
			?? ConflictSelectionTree.Model( conflictSearchContextNode?.Parent! );

		return file is not null && ( file.Category == Categories.Script || file.Category == Categories.Xml
			|| file.Category == Categories.PrecompiledScript ) ? file.GetVanillaFile( session.GamePaths ) : null;
	}

	// Keep the selection only if the rebuilt group still represents the same merge.
	private void ApplyLinkedMergeGroups()
	{
		var category = treConflicts.GetCategoryNode( Categories.PrecompiledScript );

		if ( category is null )
		{
			RefreshLinkedLooseInputs();

			return;
		}

		var allSymbols = treConflicts.SymbolNodes.Where( node => ConflictSelectionTree.Model( node )?.Category == Categories.PrecompiledScript ).ToArray();
		var symbols = allSymbols.GroupBy( ConflictSelectionTree.Model ).Select( group => group.First() ).ToArray();
		var originals = symbols.ToHashSet();
		var desired = symbols.Select( node => (Node: node, Group: ConflictSelectionTree.Model( node )!.MergePreflight?.GroupFor( ConflictSelectionTree.Model( node )! )) )
			.Where( pair => pair.Group is not null ).GroupBy( pair => pair.Group!.Key ).ToDictionary( group => group.Key, group => group.ToArray() );
		var saved = new Dictionary<string, (bool Checked, bool Expanded)>();

		foreach ( var node in category.GetTreeNodes().Where( node => node.GetMetadata()?.MergeGroup is not null ).ToArray() )
		{
			var old = node.GetMetadata()!.MergeGroup;

			saved[old.SelectionKey] = (node.Checked, node.IsExpanded);

			var previousSymbols = SMTree.Descendants( node ).Where( child => child.GetMetadata()?.IsLinkedSymbol == true ).ToArray();

			if ( desired.TryGetValue( old.Key, out var wanted ) && old.LayoutKey == wanted[0].Group!.LayoutKey
				&& wanted.Select( pair => ConflictSelectionTree.Model( pair.Node ) ).ToHashSet().SetEquals( previousSymbols.Select( ConflictSelectionTree.Model ) ) )
			{
				var current = wanted[0].Group!;

				// Clear the selection when these rows now represent a different merge.
				if ( old.SelectionKey != current.SelectionKey )
					node.Checked = false;

				node.GetMetadata()!.MergeGroup = current;
				node.Text = current.Label;
				node.ToolTipText = current.Description;
				desired.Remove( old.Key );

				continue;
			}

			foreach ( var symbol in previousSymbols )
			{
				symbol.RemoveFromTree();

				if ( !originals.Contains( symbol ) )
					continue;

				symbol.GetMetadata()!.IsLinkedSymbol = false;
				category.Nodes.Add( symbol );
			}

			node.RemoveFromTree();
		}

		foreach ( var entries in desired.Values )
		{
			var group = entries[0].Group!;
			var parent = new TreeNode( group.Label )
			{
				Tag = new SMTree.NodeMetadata { MergeGroup = group, FilePath = string.Join( "\n", group.DisplayPaths ) },
				ToolTipText = group.Description,
				ForeColor = ThemeManager.CurrentTheme.ForeBrightColor
			};

			category.Nodes.Add( parent );

			var sections = new Dictionary<string, TreeNode>( StringComparer.OrdinalIgnoreCase );

			if ( group.HasFileSections )
				foreach ( string path in group.DisplayPaths )
				{
					var section = new TreeNode( path.Replace( '/', '\\' ) )
					{
						Tag = new SMTree.NodeMetadata { FilePath = path, IsLinkedFile = true },
						ToolTipText = LinkedBlobMergeGroup.FileDescription,
						ForeColor = ThemeManager.CurrentTheme.ForeBrightColor
					};

					parent.Nodes.Add( section );
					sections.Add( path, section );
				}

			foreach ( var (node, _) in entries )
			{
				node.Checked = false;

				foreach ( TreeNode child in node.Nodes )
					child.Checked = false;

				node.RemoveFromTree();
				node.GetMetadata()!.IsLinkedSymbol = true;

				var locations = group.HasFileSections ? group.PathsFor( ConflictSelectionTree.Model( node )! )
					.Where( sections.ContainsKey ).Select( path => sections[path] ).ToArray() : [];

				if ( locations.Length == 0 )
					parent.Nodes.Add( node );
				else
				for ( int i = 0; i < locations.Length; i++ )
					locations[i].Nodes.Add( i == 0 ? node : (TreeNode)node.Clone() );
			}

			ConflictTree.SetLinkedGroupExpanded(
				parent,
				!Program.Settings.Get<bool>( "CollapseCustomLoadOrder" ),
				Program.Settings.Get<bool>( "CollapseIdenticalConflicts" ) );

			if ( saved.TryGetValue( group.SelectionKey, out var state ) )
			{
				parent.Checked = state.Checked;

				if ( state.Expanded )
					parent.Expand();
				else
					parent.Collapse();
			}
		}

		foreach ( var node in allSymbols.Where( node => node.Parent == category ) )
			if ( originals.Contains( node ) )
				node.GetMetadata()!.IsLinkedSymbol = false;
			else
				node.RemoveFromTree();

		if ( category.Nodes.Count == 0 )
			category.RemoveFromTree();
		else
			treConflicts.SynchronizeCategory( category );

		RefreshLinkedLooseInputs();
	}

	private IEnumerable<TreeNode> MergeSelectionNodes() => IsConflictSearchActive
		? treConflictSearchResults.Nodes.Cast<TreeNode>().SelectMany( category => category.GetTreeNodes() )
			.Where( node => node.GetMetadata()?.MergeGroup is not null )
			.Concat( searchLooseInputs.Where( pair => pair.Key.Level == 1 && pair.Key.TreeView == treConflictSearchResults )
				.Select( pair => pair.Value ) ).Distinct()
		: treConflicts.FileNodes;


	private void RefreshSearchGroupCheckboxes()
	{
		if ( !treConflictSearchResults.IsHandleCreated )
			return;

		foreach ( TreeNode category in treConflictSearchResults.Nodes )
			foreach ( var node in new[] { category }.Concat( SMTree.Descendants( category ) ) )
				node.SetIsCheckBoxVisible( CanCheckSearchMergeInput( node ) );
	}

	// Search checkboxes must update the original rows because those are what the merge reads.
	private void SearchGroupChecked( TreeViewEventArgs e )
	{
		if ( synchronizingLinkedInputs )
			return;

		if ( searchLooseInputs.TryGetValue( e.Node!, out var input ) )
		{
			treConflicts.SetInputChecked( input, e.Node!.Checked );

			return;
		}

		if ( e.Node?.GetMetadata()?.MergeGroup is not { } group )
			return;

		var original = treConflicts.FileNodes.FirstOrDefault( node => node.GetMetadata()?.MergeGroup?.Key == group.Key );

		if ( original is not null )
		{
			original.Checked = e.Node!.Checked;
		}

		SetMergeButtonTextIfValidSelection();
	}

	// Show the whole linked group in search results because its members must merge together.
	private void CompleteLinkedSearchGroups( List<ModFile> matches, string query, bool fileSearch )
	{
		foreach ( var original in treConflicts.FileNodes.Where( node => node.GetMetadata()?.MergeGroup is not null ) )
		{
			var members = ConflictSelectionTree.ConflictsFor( original ).ToArray();

			if ( !members.Any( matches.Contains ) && !( fileSearch && original.GetMetadata()!.MergeGroup.MatchesFile( query ) ) )
				continue;

			var category = treConflictSearchResults.Nodes.Cast<TreeNode>().FirstOrDefault( node => node.Tag == Categories.PrecompiledScript );

			if ( category is null )
			{
				category = new TreeNode( Categories.PrecompiledScript.DisplayName )
				{
					Tag = Categories.PrecompiledScript,
					ForeColor = ThemeManager.CurrentTheme.ForeColor,
					NodeFont = conflictSearchCategoryFont
				};
				treConflictSearchResults.Nodes.Add( category );
			}

			foreach ( var node in category.GetTreeNodes().Where( node => members.Contains( ConflictSelectionTree.Model( node ) ) ).ToArray() )
				node.RemoveFromTree();

			var copy = (TreeNode)original.Clone();

			category.Nodes.Add( copy );

			if ( !members.Any( matches.Contains ) )
				matches.AddRange( members );

			foreach ( var section in SMTree.Descendants( copy ).Where( node => node.GetMetadata()?.IsLinkedFile == true ) )
			{
				section.NodeFont = fileSearch && ConflictSearch.IsFileMatch( section.Text, query ) ? conflictSearchMatchFont : null;
			}

			foreach ( var symbol in SMTree.Descendants( copy ).Where( node => node.GetMetadata()?.IsLinkedSymbol == true ) )
			{
				var model = ConflictSelectionTree.Model( symbol )!;

				symbol.NodeFont = fileSearch && ConflictSearch.IsFileMatch( model.RelativePath, query ) ? conflictSearchMatchFont : null;

				foreach ( TreeNode source in symbol.Nodes )
				{
					var hash = source.GetMetadata()!.FileHash;

					source.NodeFont = treConflictSearchResults.NodeStyleFont( ConflictTree.ConflictSourceFontStyle( model, hash,
						session.LoadOrder!.IsModDisabledByName( source.Text ), !fileSearch && ConflictSearch.IsModNameMatch( source.Text, query ) ) );
				}
			}
		}

		CompleteLinkedLooseSearchResults( matches, query, fileSearch );
	}
}
