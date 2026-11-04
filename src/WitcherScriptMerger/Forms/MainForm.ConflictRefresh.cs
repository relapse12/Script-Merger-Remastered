using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using WitcherScriptMerger.FileIndex;
using WitcherScriptMerger.Inventory;

namespace WitcherScriptMerger.Forms;

internal partial class MainForm
{
	private readonly ConflictPreparation conflictPreparation = new ConflictPreparation();

	// Reuse the scan for priority and enabled-state changes, keeping existing selections where possible.
	// Changed files and renamed folders need a full refresh. Wait for any running merge before updating rows;
	// it may still be changing the inventory.
	internal void RefreshConflictNodes()
	{
		if ( session.Scan?.Files == null || session.Inventory == null || pnlProgress.Visible )
			return;

		session.Scan.MergePreflight?.Refresh(
			session.LoadOrder?.Mods.ToArray() ?? Array.Empty<WitcherScriptMerger.LoadOrder.ModLoadSetting>(),
			session.LoadOrder?.IsValid == true );

		var wanted = PrepareConflictNodes().Where( row => !ignoredConflictKeys.Contains( IgnoredConflictStore.KeyFor( row.Conflict ) )
			&& ignoredConflictStore?.IsPermanent( row.Conflict ) != true ).ToList();
		var rows = wanted.GroupBy( row => RowKey( row.Conflict ) ).ToDictionary( group => group.Key, group => group.First() );
		var existing = treConflicts.SymbolNodes.Where( node => node.GetMetadata()?.ModFile != null ).ToArray();
		var shown = existing.Select( node => node.GetMetadata().ModFile ).ToHashSet();

		bool IsCurrent( TreeNode node )
		{
			ModFile file = node.GetMetadata().ModFile;

			return rows.TryGetValue( RowKey( file ), out var row ) && ReferenceEquals( row.Conflict, file )
				&& ListsSources( node, ConflictRowSources( file, row.Merge, session.GamePaths ) );
		}

		if ( existing.All( IsCurrent ) && rows.Values.All( row => shown.Contains( row.Conflict ) ) )
		{
			ApplyLinkedMergeGroups();

			return;
		}

		treConflicts.BeginUpdate();

		try
		{
			ResetConflictToolTip();

			foreach ( TreeNode node in existing )
			{
				if ( !rows.TryGetValue( RowKey( node.GetMetadata().ModFile ), out var row ) )
				{
					node.RemoveFromTree();

					continue;
				}

				node.GetMetadata().ModFile = row.Conflict;

				var names = ConflictRowSources( row.Conflict, row.Merge, session.GamePaths ).Select( source => source.Name )
					.ToHashSet( StringComparer.OrdinalIgnoreCase );

				foreach ( TreeNode child in node.Nodes.Cast<TreeNode>().ToArray() )
					if ( !names.Contains( child.Text ) )
						child.RemoveFromTree();
			}

			foreach ( TreeNode category in treConflicts.CategoryNodes )
				if ( category.Nodes.Count == 0 )
				{
					category.NodeFont?.Dispose();
					category.RemoveFromTree();
				}

			PopulatePreparedConflictNodes( wanted );
			UpdateConflictSearchSource();
			UpdateConflictSearchSuggestions();
		}
		finally { treConflicts.EndUpdate(); }
	}

	// A path can appear in several categories. Use both values to find the same row after a rescan.
	private static (ModFileCategory Category, string Path) RowKey( ModFile file )
	{
		return (file.Category, file.RelativePath.ToUpperInvariant());
	}

	private static bool ListsSources( TreeNode node, IEnumerable<FileHash> sources )
	{
		return node.Nodes.Cast<TreeNode>().Select( child => child.Text ).ToHashSet( StringComparer.OrdinalIgnoreCase )
			.SetEquals( sources.Select( source => source.Name ) );
	}
}
