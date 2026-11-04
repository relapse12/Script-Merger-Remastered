using System;
using System.Linq;
using WitcherScriptMerger.Controls;
using WitcherScriptMerger.FileIndex;
using WitcherScriptMerger.Inventory;
using WitcherScriptMerger.LoadOrder;

namespace WitcherScriptMerger.Forms;

internal partial class MainForm
{
	private PriorityTradeoffToolTips priorityTradeoffs = PriorityTradeoffToolTips.Empty;

	internal void RefreshPriorityTradeoffToolTips()
	{
		priorityTradeoffs = PriorityTradeoffToolTips.Empty;

		if ( session.Scan?.ScanCompleted != true || !session.Scan.ScriptsScanned || session.LoadOrder?.IsValid != true )
			return;

		var visible = treConflicts.SymbolNodes.Select( node => node.GetMetadata()?.ModFile ).ToHashSet();

		priorityTradeoffs = PriorityTradeoffToolTips.Build( session.Scan.Files
			.Where( file => ReferenceEquals( file.Category, Categories.PrecompiledScript ) || visible.Contains( file ) )
			.Where( file => !ignoredConflictKeys.Contains( IgnoredConflictStore.KeyFor( file ) ) && ignoredConflictStore?.IsPermanent( file ) != true ), session.LoadOrder.Mods );
	}

	internal string GetConflictSourceToolTip( ModFile conflict, FileHash source, ModLoadSetting setting, bool isActive, bool isDisabled )
	{
		string description = ConflictTree.DescribeConflictSource( conflict, source, setting, isActive, isDisabled );
		string tradeoff = isDisabled ? string.Empty : priorityTradeoffs.Describe( conflict, source );

		return string.IsNullOrEmpty( tradeoff ) ? description : description + Environment.NewLine + Environment.NewLine + tradeoff;
	}
}
