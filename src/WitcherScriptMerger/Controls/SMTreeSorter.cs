using System.Collections;
using System.Collections.Generic;
using System.Windows.Forms;
using WitcherScriptMerger.FileIndex;
using WitcherScriptMerger.Inventory;
using WitcherScriptMerger.LoadOrder;

namespace WitcherScriptMerger.Controls;

// Categories keep their configured order; files sort by name and their sources by load order.
internal class SMTreeSorter : IComparer
{
	// Priorities can change while the tree is open, so each comparison reads the current order.
	private readonly System.Func<CustomLoadOrder> currentLoadOrder;

	internal SMTreeSorter( System.Func<CustomLoadOrder> currentLoadOrder )
	{
		this.currentLoadOrder = currentLoadOrder;
	}

	public int Compare( object x, object y )
	{
		TreeNode xNode = x as TreeNode;
		TreeNode yNode = y as TreeNode;

		if ( SMTree.IsModNode( xNode ) && SMTree.IsModNode( yNode ) && xNode.GetMetadata()?.FileHash is { } first && yNode.GetMetadata()?.FileHash is { } second )
			return CompareSourcesForSettings( currentLoadOrder()?.Mods, xNode.GetMetadata().ModFile, first, second );

		if ( xNode.GetMetadata()?.IsLinkedSymbol == true || xNode.GetMetadata()?.IsLinkedFile == true )
			return string.Compare( xNode.Text, yNode.Text, System.StringComparison.OrdinalIgnoreCase );

		if ( xNode.Level == 1 && ( xNode.Tag is ScanDiagnosticTag || yNode.Tag is ScanDiagnosticTag ) )
			return xNode.Tag is ScanDiagnosticTag ? yNode.Tag is ScanDiagnosticTag ? 0 : -1 : 1;

		switch ( xNode.Level )
		{
			case 0:
				{
					ModFileCategory obj = (ModFileCategory)xNode.Tag;
					ModFileCategory yCat = (ModFileCategory)yNode.Tag;

					return obj.OrderIndex.CompareTo( yCat.OrderIndex );
				}

			case 2:
				ModFile conflict = xNode.GetMetadata()?.ModFile ?? yNode.GetMetadata()?.ModFile;
				FileHash xSource = xNode.GetMetadata()?.FileHash;
				FileHash ySource = yNode.GetMetadata()?.FileHash;
				if ( conflict != null && xSource != null && ySource != null )
				{
					return CompareSourcesForSettings(
						currentLoadOrder()?.Mods,
						conflict,
						xSource,
						ySource );
				}
				return new LoadOrderComparer( currentLoadOrder() ).Compare( xNode.Text, yNode.Text );

			default:
				return xNode.Text.CompareTo( yNode.Text );
		}
	}

	internal static int CompareSourcesForSettings(
		IEnumerable<ModLoadSetting> settings,
		ModFile conflict,
		FileHash x,
		FileHash y )
	{
		return CustomLoadOrder.CompareConflictSourcesForSettings( settings, conflict, x, y );
	}
}
