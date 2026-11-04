#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using WitcherScriptMerger.Controls;
using WitcherScriptMerger.FileIndex;

namespace WitcherScriptMerger.Forms;

// A selected compiled merge may need loose-script inputs too. Keep their checkboxes linked to the group
// using the dependencies already found by the engine.
internal partial class MainForm
{
	private readonly Dictionary<TreeNode, TreeNode[]> linkedLooseInputs = new();
	// Remember the user's choice before a group checks a required input on their behalf.
	private readonly Dictionary<TreeNode, bool> previousLooseChecks = new();
	// Search result rows and the original rows they stand for.
	private readonly Dictionary<TreeNode, TreeNode> searchLooseInputs = new();
	private bool synchronizingLinkedInputs;

	private static string LooseInputKey( string mod, string path ) => mod + "\0" + path.Replace( '\\', '/' );

	private void RefreshLinkedLooseInputs()
	{
		linkedLooseInputs.Clear();

		var inputs = treConflicts.FileNodes.Where( node => ConflictSelectionTree.Model( node )?.Category == Categories.Script )
			.SelectMany( file => file.GetTreeNodes().Where( source => session.LoadOrder?.IsModDisabledByName( source.Text ) != true )
				.Select( source => (Key: LooseInputKey( source.Text, ConflictSelectionTree.Model( file )!.RelativePath ), Node: source) ) )
			.ToLookup( value => value.Key, value => value.Node, StringComparer.OrdinalIgnoreCase );

		foreach ( var group in treConflicts.FileNodes.Where( node => node.GetMetadata()?.MergeGroup?.Readiness.Available == true ) )
			linkedLooseInputs[group] = ( group.GetMetadata()!.MergeGroup.Readiness.SourceFiles ?? [] )
				.SelectMany( file => inputs[LooseInputKey( file.Mod, file.Path )] ).Distinct().ToArray();

		SynchronizeLinkedLooseInputs( null, false );
		RefreshLinkedLooseToolTips();
	}

	private void LinkedMergeInputsChanged( TreeNode? changed ) => SynchronizeLinkedLooseInputs( changed, true );

	// Required inputs stay checked while their group is selected. Clearing one also clears the group;
	// releasing the group restores the user's earlier choices.
	private void SynchronizeLinkedLooseInputs( TreeNode? changed, bool userAction )
	{
		if ( synchronizingLinkedInputs )
			return;

		synchronizingLinkedInputs = true;

		try
		{
			foreach ( var input in previousLooseChecks.Keys.Where( node => node.TreeView != treConflicts ).ToArray() )
				previousLooseChecks.Remove( input );

			if ( userAction )
			{
				var affected = changed is null ? previousLooseChecks.Keys.ToArray()
					: new[] { changed }.Concat( SMTree.Descendants( changed ) ).Where( node => node.Level == 2
						&& ConflictSelectionTree.Model( node.Parent )?.Category == Categories.Script ).ToArray();

				foreach ( var input in affected.Where( previousLooseChecks.ContainsKey ) )
					previousLooseChecks[input] = input.Checked;

				var cleared = affected.Where( node => !node.Checked ).ToHashSet();

				foreach ( var group in linkedLooseInputs.Where( pair => pair.Key.Checked && pair.Value.Any( cleared.Contains ) ) )
					group.Key.Checked = false;
			}

			var required = linkedLooseInputs.Where( pair => pair.Key.Checked ).SelectMany( pair => pair.Value ).ToHashSet();
			var changes = new Dictionary<TreeNode, bool>();

			foreach ( var input in required )
			{
				previousLooseChecks.TryAdd( input, input.Checked );
				changes[input] = true;
			}

			foreach ( var input in previousLooseChecks.Keys.Where( node => !required.Contains( node ) ).ToArray() )
			{
				changes[input] = previousLooseChecks[input];
				previousLooseChecks.Remove( input );
			}

			treConflicts.SetSourceChecks( changes );
			SynchronizeLinkedSearchChecks();
		}
		finally { synchronizingLinkedInputs = false; }
	}

	// The main tree holds the real selection, so search copies are set from it.
	private void SynchronizeLinkedSearchChecks()
	{
		foreach ( var pair in searchLooseInputs.Where( pair => pair.Key.TreeView == treConflictSearchResults ) )
			pair.Key.Checked = pair.Value.Checked;

		var groups = treConflicts.FileNodes.Where( node => node.GetMetadata()?.MergeGroup != null )
			.ToDictionary( node => node.GetMetadata()!.MergeGroup.Key, StringComparer.OrdinalIgnoreCase );

		foreach ( TreeNode category in treConflictSearchResults.Nodes )
			foreach ( var copy in category.GetTreeNodes().Where( node => node.GetMetadata()?.MergeGroup != null ) )
				copy.Checked = groups.TryGetValue( copy.GetMetadata()!.MergeGroup.Key, out var original ) && original.Checked;
	}

	private bool CanCheckSearchMergeInput( TreeNode node ) => session.IsScanCurrent
		&& ( node.GetMetadata()?.MergeGroup?.Readiness.Available == true || searchLooseInputs.ContainsKey( node ) );

	// Includes linked loose files in search results so all required inputs remain visible.
	private void CompleteLinkedLooseSearchResults( List<ModFile> matches, string query, bool fileSearch )
	{
		var visible = treConflictSearchResults.Nodes.Cast<TreeNode>().SelectMany( category => category.GetTreeNodes() )
			.Select( node => node.GetMetadata()?.MergeGroup?.Key ).OfType<string>().ToHashSet( StringComparer.OrdinalIgnoreCase );
		var files = linkedLooseInputs.Where( pair => visible.Contains( pair.Key.GetMetadata()!.MergeGroup.Key ) )
			.SelectMany( pair => pair.Value ).Select( node => node.Parent ).Distinct().ToArray();

		if ( files.Length == 0 )
			return;

		var category = treConflictSearchResults.Nodes.Cast<TreeNode>().FirstOrDefault( node => node.Tag == Categories.Script );

		if ( category is null )
		{
			category = new TreeNode( Categories.Script.DisplayName )
			{
				Tag = Categories.Script,
				ForeColor = Theming.ThemeManager.CurrentTheme.ForeColor,
				NodeFont = conflictSearchCategoryFont
			};
			treConflictSearchResults.Nodes.Insert( 0, category );
		}

		foreach ( var original in files )
		{
			var model = ConflictSelectionTree.Model( original )!;

			foreach ( var existing in category.GetTreeNodes().Where( node => ReferenceEquals( ConflictSelectionTree.Model( node ), model ) ).ToArray() )
				existing.RemoveFromTree();

			var copy = (TreeNode)original.Clone();

			category.Nodes.Add( copy );
			searchLooseInputs[copy] = original;
			copy.NodeFont = fileSearch && ConflictSearch.IsFileMatch( model.RelativePath, query ) ? conflictSearchMatchFont : null;

			string active = session.LoadOrder!.HasResolvedConflict( model ) ? session.LoadOrder.GetTopPriorityEnabledMod( model ) : null!;

			foreach ( TreeNode source in copy.Nodes )
			{
				var input = original.GetTreeNodes().Single( node => node.Text.Equals( source.Text, StringComparison.OrdinalIgnoreCase ) );

				if ( session.LoadOrder.IsModDisabledByName( source.Text ) != true )
					searchLooseInputs[source] = input;

				ApplyConflictSearchPriorityStyle( source, active, !fileSearch && ConflictSearch.IsModNameMatch( source.Text, query ), model );
			}

			if ( !matches.Contains( model ) )
				matches.Add( model );
		}
	}

	internal void RefreshLinkedLooseToolTips()
	{
		var linkedFiles = linkedLooseInputs.Values.SelectMany( inputs => inputs ).Select( node => ConflictSelectionTree.Model( node.Parent ) ).ToHashSet();
		var linkedGroups = linkedLooseInputs.Where( pair => pair.Value.Length > 0 ).Select( pair => pair.Key.GetMetadata()!.MergeGroup.Key ).ToHashSet( StringComparer.OrdinalIgnoreCase );

		foreach ( TreeView tree in new TreeView[] { treConflicts, treConflictSearchResults } )
			foreach ( TreeNode category in tree.Nodes )
				foreach ( var node in category.GetTreeNodes() )
					if ( node.GetMetadata()?.MergeGroup is { } group && linkedGroups.Contains( group.Key ) )
						node.ToolTipText = UiText.T(
							"BlobMerge",
							"Selecting this group also selects its linked loose-script inputs under Scripts. They are merged together." );
					else if ( ConflictSelectionTree.Model( node ) is { } file && file.Category == Categories.Script )
						node.ToolTipText = linkedFiles.Contains( file )
							? UiText.T(
								"BlobMerge",
								"This loose conflict is included when its linked precompiled merge is selected. It can also be merged separately." )
							: file.GetSemanticConflictDescription();
	}
}
