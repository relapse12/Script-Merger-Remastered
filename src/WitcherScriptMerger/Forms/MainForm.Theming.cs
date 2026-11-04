using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using WitcherScriptMerger.Controls;
using WitcherScriptMerger.FileIndex;
using WitcherScriptMerger.Inventory;
using WitcherScriptMerger.Theming;

namespace WitcherScriptMerger.Forms;

internal partial class MainForm
{
	internal void RefreshThemeStyles()
	{
		// Retained search trees also need theme updates.
		foreach ( var tree in new StyledTreeView[] { treConflicts, treConflictSearchResults, treMerges } )
			Program.ThemeMngr.ApplyTreeTheme( tree, this );

		foreach ( var fader in _treConflictsColorFaders.Concat( _treMergesColorFaders ) )
			fader.Stop();

		_treConflictsPrevNode = _treMergesPrevNode = null;
		RefreshConflictColors( treConflicts );
		RefreshConflictColors( treConflictSearchResults );

		RestyleMergeOutputs();
		ResetConflictToolTip();
		treConflicts.Invalidate();
		treConflictSearchResults.Invalidate();
		treMerges.Invalidate();
	}

	internal void RestyleMergeOutputs()
	{
		foreach ( TreeNode category in treMerges.Nodes.Cast<TreeNode>().Concat( mergeSearchSource.Select( group => group.Category ) ).Distinct() )
		{
			category.ForeColor = ThemeManager.CurrentTheme.ForeColor;

			var stored = mergeSearchSource.FirstOrDefault( group => ReferenceEquals( group.Category, category ) ).Files;

			foreach ( TreeNode file in category.Nodes.Cast<TreeNode>().Concat( stored ?? System.Array.Empty<TreeNode>() ).Distinct() )
			{
				treMerges.ApplyOutputStyles( file );
			}
		}
	}

	private void RefreshConflictColors( TreeView tree )
	{
		foreach ( TreeNode category in tree.Nodes )
		{
			category.ForeColor = category.Tag is ModFileCategory ? ThemeManager.CurrentTheme.ForeColor : ThemeManager.CurrentTheme.ForeDimmedColor;

			foreach ( TreeNode group in category.Nodes )
				if ( group.GetMetadata()?.MergeGroup != null )
				{
					SetNodeColor( group, ThemeManager.CurrentTheme.ForeBrightColor );

					foreach ( var section in SMTree.Descendants( group ).Where( node => node.GetMetadata()?.IsLinkedFile == true ) )
						SetNodeColor( section, ThemeManager.CurrentTheme.ForeBrightColor );
				}
		}

		foreach ( TreeNode file in SMTree.SymbolRows( tree ) )
		{
			file.ForeColor = ThemeManager.CurrentTheme.ForeBrightColor;

			ModFile conflict = file.Tag as ModFile ?? file.GetMetadata()?.ModFile;
			string winner = conflict != null && session.LoadOrder?.HasResolvedConflict( conflict ) == true
				? session.LoadOrder.GetTopPriorityEnabledMod( conflict ) : null;

			foreach ( TreeNode source in file.Nodes )
			{
				FileHash hash = source.Tag as FileHash ?? source.GetMetadata()?.FileHash;
				var setting = session.LoadOrder?.GetModLoadSettingByName( source.Text );
				bool isTopPriority = source.Text.EqualsIgnoreCase( winner );
				ConflictSourceRole role = ConflictTree.GetConflictSourceRole( conflict, hash, setting, isTopPriority );

				SetNodeColor( source, ConflictTree.ConflictSourceColor( conflict, hash, setting, isTopPriority ) );
				StyledTreeView.SetActiveTag( source, role == ConflictSourceRole.TopPriority );
			}
		}
	}

	private static void SetNodeColor( TreeNode node, Color color )
	{
		node.ForeColor = color;

		if ( node.GetMetadata() is SMTree.NodeMetadata metadata )
			metadata.ForeColor = color;
	}
}
