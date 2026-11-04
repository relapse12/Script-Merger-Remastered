#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using WitcherScriptMerger.Inventory;
using WitcherScriptMerger.BlobMerging;
using WitcherScriptMerger.Controls;
using WitcherScriptMerger.FileIndex;

namespace WitcherScriptMerger.Forms;

internal partial class MainForm
{
	internal static MergeSelection SelectionFor( TreeNode fileNode )
	{
		SMTree.NodeMetadata? metadata = fileNode.GetMetadata();
		LinkedBlobMergeGroup? group = metadata?.MergeGroup;
		MergeSelectionSource[] sources = fileNode.GetTreeNodes().Select( source =>
		{
			SMTree.NodeMetadata? sourceMetadata = source.GetMetadata();

			return new MergeSelectionSource(
				source.Text,
				source.Checked,
				sourceMetadata?.FilePath,
				sourceMetadata?.FileHash,
				sourceMetadata?.ModFile );
		} ).ToArray();

		ModFileCategory? category = fileNode.Parent?.Tag as ModFileCategory;

		return new MergeSelection(
			fileNode.Text,
			metadata?.ModFile,
			category,
			category?.IsSupported == true,
			metadata?.FilePath,
			fileNode.Checked,
			group?.Conflicts,
			group?.Readiness.Available == true,
			sources );
	}

	internal static TreeNode[] GetAllScriptMergeCandidates( IEnumerable<TreeNode> nodes, IEnumerable<Merge> merges )
	{
		return nodes.Where( node => SelectionFor( node ).IsScriptCandidate( merges ) ).ToArray();
	}

	internal static TreeNode[] RemainingOrdinaryMergeCandidates( IEnumerable<TreeNode> nodes,
		IEnumerable<Merge> currentMerges, IEnumerable<string> suppressedInputs )
	{
		var consumed = suppressedInputs.ToHashSet( StringComparer.OrdinalIgnoreCase );
		var managed = MergeSelection.ManagedScriptPaths( currentMerges );

		return nodes.Where( node => SelectionFor( node ).RemainsOrdinary( managed, consumed ) ).ToArray();
	}

	// When a merged output is disabled or loses priority, its input conflicts become visible again.
	// Keep the merge record so the user can still manage that output.
	internal static IEnumerable<FileHash> ConflictRowSources( ModFile conflict, Merge? merge, GamePaths game )
	{
		bool outputResolves = merge?.OutputResolves( conflict.CurrentLoadOrder, game ) == true;

		return conflict.GetConflictDisplaySources().Where( source => ShouldRepopulateConflictSource(
			merge?.Mods.FirstOrDefault( recorded => recorded.Name.EqualsIgnoreCase( source.Name ) ),
			outputResolves ) );
	}

	private static bool ShouldRepopulateConflictSource( FileHash? mergeModHash, bool outputResolves )
	{
		return mergeModHash == null || mergeModHash.IsOutdated || !outputResolves;
	}

	// An externally disabled output stays disabled. Ask the user to resolve it before writing a new merge.
	private bool CanWriteMerges( ScriptMergePlan plan, IEnumerable<TreeNode> fileNodes, string mergedModName )
	{
		session.LoadOrder?.Refresh();

		MergeWriteRefusal? refusal = ScriptMergeService.FindWriteRefusal( plan, fileNodes.Select( SelectionFor ), mergedModName,
			session.Inventory!.Merges, session.GamePaths, session.LoadOrder );

		if ( refusal is null )
			return true;

		var (text, title) = refusal.Kind switch
		{
			MergeWriteRefusalKind.UnreadableSettings => (
				UiText.T( "MainForm", "mods.settings could not be read, so nothing was merged and no files were changed.\n\nCorrect that file, then refresh conflicts." ),
				UiText.T( "MainForm", "Refresh required" )),
			MergeWriteRefusalKind.DisabledOutput => (
				UiText.Format( "MainForm", "The merged output folder {0} is disabled, so nothing was merged and no files were changed.\n\nEnable that folder, then create the merge again.", refusal.Name ),
				UiText.T( "MainForm", "Output Folder Disabled" )),
			MergeWriteRefusalKind.UnreadableOutput => (
				UiText.Format( "MainForm", "The merged output could not be checked, so nothing was merged and no files were changed.\n\n{0}\n\nRestore access, then refresh conflicts.", refusal.Name ),
				UiText.T( "MainForm", "Refresh required" )),
			_ => (
				UiText.Format( "MainForm", "{0} changed since the last scan, so nothing was merged and no files were changed.\n\nRefresh conflicts, then create the merge again.", refusal.Name ),
				UiText.T( "MainForm", "Refresh required" ))
		};

		ShowMessage( text, title, MessageBoxButtons.OK, MessageBoxIcon.Warning );

		return false;
	}

	private static bool IsMergeableConflictSelection( TreeNode fileNode )
	{
		return SelectionFor( fileNode ).IsMergeable;
	}

	private static ModFile? SelectedBlobConflict( TreeNode node ) => SelectionFor( node ).SelectedBlobConflict;

	private static string ResolveExecutablePath( string path )
	{
		return Path.IsPathRooted( path )
			? path
			: Path.GetFullPath( Path.Combine( Environment.CurrentDirectory, path ) );
	}
}
