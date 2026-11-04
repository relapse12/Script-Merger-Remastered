using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using WitcherScriptMerger.Controls;
using WitcherScriptMerger.FileIndex;
using WitcherScriptMerger.Theming;

namespace WitcherScriptMerger.Forms;

// IgnoredConflictStore saves the rules. This partial applies them to the displayed rows.
internal partial class MainForm
{
	private IgnoredConflictStore ignoredConflictStore;
	private string ignoredConflictStorePath;
	private readonly List<(TreeNode Category, TreeNode File)> hiddenConflictNodes = new List<(TreeNode Category, TreeNode File)>();
	private HashSet<string> ignoredConflictKeys = new HashSet<string>();

	// Ignore rules belong to one Mods folder. Keep conflicts visible if its saved rules cannot be read.
	private void EnsureIgnoredConflictStore()
	{
		string path = IgnoredConflictStore.GetStorePath( Paths.ModsDirectory );

		if ( path == ignoredConflictStorePath )
			return;

		hiddenConflictNodes.Clear();
		ignoredConflictKeys.Clear();
		ignoredConflictStorePath = path;
		ignoredConflictStore = new IgnoredConflictStore( path, session.GamePaths );

		if ( ignoredConflictStore.LoadError != null )
			ShowError(
				UiText.Format( "MainForm.IgnoredConflicts", "{0}\nConflicts will remain visible.", ignoredConflictStore.LoadError ),
				UiText.T( "MainForm.IgnoredConflicts", "Ignore Settings Could Not Be Read" ) );
	}

	private List<ModFile> AllDisplayedConflicts() => treConflicts.SymbolNodes
		.Concat( hiddenConflictNodes.Select( pair => pair.File ) )
		.Select( node => node.GetMetadata()?.ModFile ).Where( file => file != null && file.HasConflict )
		.GroupBy( IgnoredConflictStore.KeyFor ).Select( group => group.First() ).ToList();

	private void RestoreHiddenConflictNodes()
	{
		if ( hiddenConflictNodes.Count == 0 )
			return;

		foreach ( var pair in hiddenConflictNodes )
		{
			var category = treConflicts.GetCategoryNode( (ModFileCategory)pair.Category.Tag );

			if ( category == null )
			{
				category = pair.Category;
				treConflicts.Nodes.Add( category );
			}

			category.Nodes.Add( pair.File );
		}

		hiddenConflictNodes.Clear();
		ApplyLinkedMergeGroups();
	}

	// A group with a hidden member no longer represents the full selection. Clear its check.
	private void HideIgnoredConflictNodes( bool refreshCheckboxes = true )
	{
		foreach ( TreeNode node in treConflicts.SymbolNodes )
		{
			ModFile file = node.GetMetadata()?.ModFile;

			if ( file == null || !ignoredConflictKeys.Contains( IgnoredConflictStore.KeyFor( file ) ) )
				continue;

			for ( TreeNode ancestor = node.Parent; ancestor != null; ancestor = ancestor.Parent )
				if ( ancestor.GetMetadata()?.MergeGroup != null )
				{
					ancestor.Checked = false;

					break;
				}

			TreeNode category = node.Parent;

			while ( category.Parent != null )
				category = category.Parent;

			if ( !hiddenConflictNodes.Any( pair => ReferenceEquals( pair.File.GetMetadata()?.ModFile, file ) ) )
				hiddenConflictNodes.Add( (category, node) );

			node.Checked = false;

			foreach ( TreeNode child in node.Nodes )
				child.Checked = false;

			node.RemoveFromTree();
		}

		foreach ( TreeNode category in treConflicts.CategoryNodes )
		{
			if ( category.Nodes.Count == 0 )
				category.RemoveFromTree();
			else if ( ( (ModFileCategory)category.Tag ).IsSupported )
				treConflicts.SynchronizeCategory( category );
		}

		ApplyLinkedMergeGroups();

		if ( refreshCheckboxes )
			treConflicts.RefreshMergeCheckboxes();
	}

	// Checking temporary ignores hashes files, so do that work off the UI thread.
	private async Task RefreshIgnoredConflictsAsync( bool refreshCheckboxes = true )
	{
		if ( ignoredConflictStore == null )
			return;

		List<ModFile> files = null;

		UpdateIgnoreControls( () =>
		{
			RestoreHiddenConflictNodes();
			files = AllDisplayedConflicts();
		} );

		HashSet<string> keys = new HashSet<string>();
		Exception failure = null;

		try
		{
			keys = await Task.Run( () => ignoredConflictStore.Evaluate( files, checkScripts: true, checkXml: false, checkBundles: true ) );
		}
		catch ( Exception ex )
		{
			failure = ex;
		}

		UpdateIgnoreControls( () =>
		{
			ignoredConflictKeys = keys;

			if ( failure != null )
				ShowError(
					UiText.Format(
						"MainForm.IgnoredConflicts",
						"Ignore choices could not be checked. All conflicts will remain visible.\n\n{0}",
						failure.Message ),
					UiText.T( "MainForm.IgnoredConflicts", "Ignore Check Failed" ) );

			HideIgnoredConflictNodes( refreshCheckboxes );
		} );
	}

	private void UpdateIgnoreControls( Action update )
	{
		// This can resume on a worker thread after awaiting file operations.
		if ( InvokeRequired )
			Invoke( update );
		else
			update();
	}

	internal async Task IgnoreSelectedConflictsAsync( ConflictSelectionTree tree, bool permanently = false )
	{
		if ( !txtConflictModSearch.Enabled || !txtConflictFileSearch.Enabled )
			return;

		if ( tree != ( IsConflictSearchActive ? treConflictSearchResults : treConflicts ) )
			return;

		ModFile[] selected = tree.SelectedConflicts;

		if ( selected.Length == 0 )
			return;

		EnsureIgnoredConflictStore();

		if ( permanently )
			await ChangeIgnoresAsync( () => ignoredConflictStore.AddPermanentRules( selected.Select( file => file.RelativePath ) ),
				UiText.F( "MainForm.IgnoredConflicts", $"Permanently ignoring {selected.Length} files" ) );
		else
			await ChangeIgnoresAsync( () => ignoredConflictStore.IgnoreUntilChanged( selected ),
				UiText.F( "MainForm.IgnoredConflicts", $"Ignoring {selected.Length} file conflicts until their files change" ) );
	}

	private async void OpenPermanentIgnoreRules()
	{
		if ( !txtConflictModSearch.Enabled || !txtConflictFileSearch.Enabled )
			return;

		conflictSearchTimer.Stop();
		EnsureIgnoredConflictStore();

		using var dialog = new PermanentIgnoreRulesForm( ignoredConflictStore );

		dialog.ShowDialog( this );

		if ( !dialog.Changed )
		{
			RefreshConflictSearchResultsIfActive();

			return;
		}

		await ChangeIgnoresAsync( null, UiText.T( "MainForm.IgnoredConflicts", "Applying permanent ignore rules" ) );
	}

	internal async Task RestoreTemporarilyIgnoredConflictsAsync()
	{
		if ( !txtConflictModSearch.Enabled || !txtConflictFileSearch.Enabled )
			return;

		EnsureIgnoredConflictStore();

		string[] keys = ignoredConflictStore.Conflicts.Select( snapshot => snapshot.Key ).ToArray();

		if ( keys.Length == 0 )
			return;

		await ChangeIgnoresAsync(
			() => ignoredConflictStore.Restore( keys ),
			UiText.T( "MainForm.IgnoredConflicts", "Restoring temporarily ignored conflicts" ) );
	}

	private async Task ChangeIgnoresAsync( Action change, string progress )
	{
		UpdateIgnoreControls( () =>
		{
			conflictSearchTimer.Stop();
			txtConflictModSearch.Enabled = txtConflictFileSearch.Enabled = false;
			InitializeProgressScreen( progress );
			treConflicts.BeginUpdate();
		} );

		try
		{
			if ( change != null )
				await Task.Run( change );

			UpdateIgnoreControls( () =>
			{
				treConflicts.ClearFileSelection();
				treConflictSearchResults.ClearFileSelection();
			} );
			await RefreshIgnoredConflictsAsync();
			UpdateIgnoreControls( () =>
			{
				treConflicts.Sort();
				treConflicts.SetStylesForCustomLoadOrder();
				ThemeManager.AttachColorFaderToTreeNodes( treConflicts, out _treConflictsColorFaders );
			} );
		}
		catch ( Exception ex )
		{
			ShowError( ex.Message, UiText.T( "MainForm.IgnoredConflicts", "Could Not Update Ignored Conflicts" ) );
		}
		finally
		{
			UpdateIgnoreControls( () =>
			{
				treConflicts.EndUpdate();
				HideProgressScreen();
				txtConflictModSearch.Enabled = txtConflictFileSearch.Enabled = true;
			} );
		}

		UpdateIgnoreControls( () =>
		{
			ShowDefaultConflictView();
			SetMergeButtonTextIfValidSelection();
			UpdateConflictSearchSource();
			UpdateConflictSearchSuggestions();
			UpdateStatusText();
			RefreshConflictSearchResultsIfActive();
		} );
	}
}
