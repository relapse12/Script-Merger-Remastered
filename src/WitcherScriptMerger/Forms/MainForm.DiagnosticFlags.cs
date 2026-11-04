using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using WitcherScriptMerger.Controls;
using WitcherScriptMerger.FileIndex;
using WitcherScriptMerger.Inventory;
using WitcherScriptMerger.Theming;
using WitcherScriptMerger.BlobMerging;

namespace WitcherScriptMerger.Forms;

internal partial class MainForm
{
	private ScanIssuesForm activeDiagnosticReport;
	private ScanDiagnosticKind? activeDiagnosticKind;

	internal IReadOnlyList<string> GetDiagnosticIssues( ScanDiagnosticKind? kind = null ) => session.Scan?.ScanCompleted == true
		? kind switch
		{
			ScanDiagnosticKind.Scripts => session.Scan.GetScriptDiagnostics( session.Scan.Files.Where( file =>
				!ignoredConflictKeys.Contains( IgnoredConflictStore.KeyFor( file ) ) && ignoredConflictStore?.IsPermanent( file ) != true ) )
				.Concat( reconciler.Issues ).Concat( RecoveredScriptStore.Issues ).Distinct().ToArray(),
			ScanDiagnosticKind.Xml => session.Scan.XmlDiagnostics,
			_ => GetDiagnosticIssues( ScanDiagnosticKind.Scripts ).Concat( GetDiagnosticIssues( ScanDiagnosticKind.Xml ) ).ToArray()
		}
		: Array.Empty<string>();

	private void ShowDiagnosticIssues( ScanDiagnosticKind kind ) => ShowDiagnosticReport( kind );

	// Keep the report window so later scans can update it.
	private void ShowDiagnosticReport( ScanDiagnosticKind? kind )
	{
		var issues = GetDiagnosticIssues( kind );

		if ( issues.Count == 0 )
			return;

		using var report = new ScanIssuesForm( issues );

		Program.ThemeMngr.ApplyThemeOnForm( report );
		activeDiagnosticReport = report;
		activeDiagnosticKind = kind;

		try
		{
			ShowModal( report );
		}
		finally { activeDiagnosticReport = null; }
	}

	internal bool UpdateDiagnosticFlags()
	{
		bool changed = SynchronizeXmlDefinitionRows();

		foreach ( var (kind, category) in new[] {
			(ScanDiagnosticKind.Scripts, Categories.PrecompiledScript),
			(ScanDiagnosticKind.Xml, Categories.XmlDefinitionOverride) } )
		{
			TreeNode categoryNode = treConflicts.GetCategoryNode( category );
			TreeNode flag = categoryNode?.Nodes.Cast<TreeNode>().FirstOrDefault( node => node.Tag is ScanDiagnosticTag tag && tag.Kind == kind );

			if ( GetDiagnosticIssues( kind ).Count == 0 )
			{
				if ( flag == null )
					continue;

				flag.NodeFont?.Dispose();
				flag.RemoveFromTree();

				if ( categoryNode.Nodes.Count == 0 )
				{
					categoryNode.NodeFont?.Dispose();
					categoryNode.RemoveFromTree();
				}

				changed = true;

				continue;
			}

			if ( categoryNode == null )
			{
				categoryNode = new TreeNode( category.DisplayName )
				{
					Tag = category,
					ToolTipText = category.ToolTipText,
					ForeColor = ThemeManager.CurrentTheme.ForeColor,
					NodeFont = new Font( "Segoe UI", 14.75f, FontStyle.Bold )
				};
				treConflicts.Nodes.Add( categoryNode );
				categoryNode.Expand();
			}

			if ( flag == null )
			{
				flag = new TreeNode { Tag = new ScanDiagnosticTag( kind ), NodeFont = new Font( treConflicts.Font, FontStyle.Underline ) };
				categoryNode.Nodes.Add( flag );
				changed = true;
			}

			flag.Text = kind == ScanDiagnosticKind.Scripts
				? UiText.T( "DiagnosticFlags", "Script issues — see Diagnostics" )
				: UiText.T( "DiagnosticFlags", "XML issues — see Diagnostics" );
			flag.ForeColor = ThemeManager.CurrentTheme.ForeBrightColor;
		}

		if ( changed )
			treConflicts.Sort();

		// Sorting rebuilds native TreeView items and resets their checkbox state.
		treConflicts.RefreshMergeCheckboxes();

		if ( activeDiagnosticReport is { IsDisposed: false } )
			activeDiagnosticReport.UpdateIssues( GetDiagnosticIssues( activeDiagnosticKind ) );

		return changed;
	}

	// XML definition conflicts depend on load order, so their rows can come and go without a rescan.
	private bool SynchronizeXmlDefinitionRows()
	{
		if ( session.Scan?.ScanCompleted != true || session.LoadOrder?.IsValid != true )
			return false;

		var wanted = session.Scan.Conflicts.Where( file => ReferenceEquals( file.Category, Categories.XmlDefinitionOverride ) )
			.Where( file => !ignoredConflictKeys.Contains( IgnoredConflictStore.KeyFor( file ) ) ).ToArray();
		var existing = treConflicts.FileNodes.Where( node => ReferenceEquals( node.GetMetadata()?.ModFile?.Category,
			Categories.XmlDefinitionOverride ) ).ToArray();
		var obsolete = existing.Where( node => !node.GetMetadata().ModFile.HasConflict ).ToArray();
		var added = wanted.Where( file => !existing.Any( node => node.GetMetadata().ModFile == file ) ).ToArray();

		if ( obsolete.Length == 0 && added.Length == 0 )
			return false;

		treConflicts.BeginUpdate();

		try
		{
			ResetConflictToolTip();

			foreach ( TreeNode node in obsolete )
				node.RemoveFromTree();

			if ( added.Length > 0 )
				PopulatePreparedConflictNodes( added.Select( file => (file, (Merge)null) ).ToList() );

			TreeNode category = treConflicts.GetCategoryNode( Categories.XmlDefinitionOverride );

			if ( category?.Nodes.Count == 0 )
			{
				category.NodeFont?.Dispose();
				category.RemoveFromTree();
			}

			treConflicts.SetStylesForCustomLoadOrder( updateStatus: false );
			UpdateConflictSearchSource();

			if ( IsConflictSearchActive )
				UpdateConflictSearchView();
		}
		finally { treConflicts.EndUpdate(); }

		return true;
	}
}
