using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using WinRT;
using WinRT.WitcherScriptMergerVtableClasses;
using WitcherScriptMerger.Controls;
using WitcherScriptMerger.FileIndex;
using WitcherScriptMerger.Inventory;
using WitcherScriptMerger.LoadOrder;
using WitcherScriptMerger.Theming;

namespace WitcherScriptMerger.Forms;

// MainForm connects user actions to the engine and displays the results.
// Its partial files contain the individual UI features; MergeSession keeps the inventory and scan state.
[WinRTRuntimeClassName( "Windows.Foundation.IClosable" )]
[WinRTExposedType( typeof( WitcherScriptMerger_Forms_MergeReportFormWinRTTypeDetails ) )]
internal partial class MainForm : BaseForm
{	private FormWindowState lastRestorableWindowState = FormWindowState.Normal;

	private TreeNode _treConflictsPrevNode;

	private TreeNode _treMergesPrevNode;

	private List<TreeNodeColorFader> _treConflictsColorFaders = new List<TreeNodeColorFader>();

	private List<TreeNodeColorFader> _treMergesColorFaders = new List<TreeNodeColorFader>();

	private TreeNode _prevToolTipNode;

	private System.Threading.Timer _autoExitTimer;

	private bool _autoMergeStarted;

	private bool _manualRefreshRunning;
	private bool progressLayoutPending;

	private readonly MergeSession session;

	private readonly MergeReconciler reconciler;
	private readonly ScriptMergeService scriptMerges;
	private readonly MergeDeletionService mergeDeletions;

	internal ContextMenuStrip MainMenu;

	public ConflictTree treConflicts;

	private MergeTree treMerges;

	private Label lblNoConflicts;

	private ScanSummaryBar scanSummary;
	private bool scanFailed;
	private TableLayoutPanel tlpConflictTitle;
	private Button btnScanDiagnostics;

	private IContainer components;

	public Button btnCreateAllScriptMerges;

	public Button btnCreateMerges;

	public Button btnDeleteAllMerges;

	public Button btnDeleteMerges;

	private Button btnDontExit;

	private Button btnExitNow;

	private Button btnMainMenu;

	private Button btnRefreshConflicts;

	private Button btnRefreshMerged;

	private Button button1;

	private Label lblConflicts;

	private Label lblMerges;

	private Label lblProgressCurrentAction;

	private Label lblProgressCurrentPhase;

	private Panel pnlProgress;

	private ModernProgressBar progressBar;

	private StatusStrip statusStrip;

	private TableLayoutPanel tableLayoutPanel2;

	private TableLayoutPanel tlpMergesHeader;

	private TableLayoutPanel tlpConflictsHeader;

	private TableLayoutPanel tlpMain;

	private ToolStripStatusLabel lblStatusLeft1;

	private ToolStripStatusLabel lblStatusLeft2;

	private ToolStripStatusLabel lblStatusLeft3;

	private ToolStripStatusLabel lblStatusRight;

	private ToolStripStatusLabel lblStatusSpring;

	private ToolTip toolTip1;

	private ToolTip treConflictsToolTip;

	private Label lblBundleScanInfo;

	private ContextMenuStrip contextMenuStrip1;

	private TextBox txtConflictModSearch;

	private TextBox txtConflictFileSearch;

	private TableLayoutPanel tlpConflictSearch;

	private ModernTextBoxHost pnlConflictModSearch;

	private ModernTextBoxHost pnlConflictFileSearch;

	private TableLayoutPanel tlpConflictActions;

	private TableLayoutPanel tlpMergeActions;

	private CompositedWorkspacePanel tlpRoot;

	private WorkspaceSplitterGrip pnlWorkspaceSplitter;

	private bool workspaceSplitterDragging;

	private int lastWorkspaceSplitLeftWidth = -1;

	private System.Windows.Forms.Timer workspaceSplitterTimer;

	private bool workspaceSplitPending;

	private int pendingWorkspaceSplitX;

	private System.Windows.Forms.Timer responsiveLayoutTimer;

	private Font paneCountFont;

	private bool responsiveLayoutPending;

	private bool applyingResponsiveLayout;

	private bool actionLayoutStacked;

	private bool searchLayoutStacked;

	private string fullStatusDetails = string.Empty;

	private string compactStatusDetails = string.Empty;

	private ConflictSelectionTree treConflictSearchResults;

	private Font conflictSearchCategoryFont;

	private Font conflictSearchMatchFont;

	private List<ModFile> conflictSearchSource = new List<ModFile>();

	private System.Windows.Forms.Timer conflictSearchTimer;

	private bool conflictSearchActionStateCaptured;

	private bool createMergesEnabledBeforeSearch;

	private bool createAllMergesEnabledBeforeSearch;

	private bool changingConflictSearchText;

	private ContextMenuStrip conflictSearchModMenu;

	private string conflictSearchContextModName;

	private TreeNode conflictSearchInlineRenameNode;

	public MainForm()
		: this( MergeSession.ForGame( Paths.ForSelectedGame() ) )
	{
	}

	internal MainForm( MergeSession session )
	{
		this.session = session;
		reconciler = new MergeReconciler( session, this );
		scriptMerges = new ScriptMergeService( session, this );
		mergeDeletions = new MergeDeletionService( session, this );
		InitializeComponent();
		InitializeCustomComponent();
		treConflicts.ScanIsCurrent = () => this.session.IsScanCurrent;
		treConflicts.GamePaths = this.session.GamePaths;
		treMerges.GamePaths = this.session.GamePaths;
		treConflicts.CurrentLoadOrder = () => this.session.LoadOrder;
		treMerges.CurrentLoadOrder = () => this.session.LoadOrder;
		treConflictSearchResults.CurrentLoadOrder = () => this.session.LoadOrder;
		InitializeConflictToolTips();
		treConflicts.MergeInputsChanged += LinkedMergeInputsChanged;
		InitializeApplicationUpdates();
		ApplyConflictListSize();
	}

	internal MergeSession Session => session;

	internal void ApplyConflictListSize()
	{
		treConflicts.ApplyListSize();
		treMerges.ApplyListSize();
		treConflictSearchResults.ApplyListSize();
	}

	private void MainForm_Load( object sender, EventArgs e )
	{
		Text = UiText.Format( "MainForm", "{0} v{1}", Text, Application.ProductVersion );
		pnlProgress.BringToFront();
		treConflicts.MouseMove += OnTreeViewMouseMove;
		treMerges.MouseMove += OnTreeViewMouseMove;
		MainMenu = CreateMainMenu();
		RefreshShortcutHints();
		Program.ThemeMngr.ApplyThemeOnForm( this );
		LoadLastWindowConfiguration();
		ApplyThemeToConflictSearchResults();
	}

	private async void MainForm_Shown( object sender, EventArgs e )
	{
		Update();
		Program.OptionsFrm.CheckSetGameDir();

		bool repackingBundle = false;

		if ( !string.IsNullOrWhiteSpace( Paths.GameDirectory ) || !Paths.IsModsDirectoryDerived )
		{
			repackingBundle = await RefreshMergeInventory();
		}

		if ( repackingBundle )
		{
			return;
		}

		if ( !string.IsNullOrWhiteSpace( Paths.GameDirectory ) || ( !Paths.IsScriptsDirectoryDerived && !Paths.IsModsDirectoryDerived ) )
		{
			RefreshConflictsTree();
		}
		else
		{
			LiveText.BindText( lblStatusLeft1, () => UiText.T( "MainForm", "Please locate your Witcher 3 game directory." ) );
		}
	}

	private void MainForm_FormClosing( object sender, FormClosingEventArgs e )
	{
		if ( pnlProgress.Visible )
		{
			e.Cancel = true;

			return;
		}

		Program.Settings.Set( "GameDirectory", Paths.GameDirectory );
		RememberWindowPlacement();
		Program.Settings.Save();
		Program.StopListening();
	}

	internal void RememberWindowPlacement()
	{
		Rectangle bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;

		Program.Settings.Set( "StartMaximized", lastRestorableWindowState == FormWindowState.Maximized );
		Program.Settings.Set( "StartWidth", bounds.Width );
		Program.Settings.Set( "StartHeight", bounds.Height );
		Program.Settings.Set( "StartPosTop", bounds.Top );
		Program.Settings.Set( "StartPosLeft", bounds.Left );
	}

	internal void LoadLastWindowConfiguration()
	{
		int startWidth = Program.Settings.Get<int>( "StartWidth" );
		int startHeight = Program.Settings.Get<int>( "StartHeight" );

		StartPosition = FormStartPosition.Manual;

		if ( startWidth > 0 && startHeight > 0 )
		{
			Rectangle saved = new Rectangle( Program.Settings.Get<int>( "StartPosLeft" ),
				Program.Settings.Get<int>( "StartPosTop" ), startWidth, startHeight );

			Bounds = WindowPlacement.Constrain( saved, Screen.FromRectangle( saved ).WorkingArea );
		}
		else
			Bounds = WindowPlacement.FirstLaunchBounds( Screen.FromPoint( Cursor.Position ).WorkingArea );

		if ( Program.Settings.Get<bool>( "StartMaximized" ) )
		{
			base.WindowState = FormWindowState.Maximized;
		}
	}

	internal void AutoExit( object state = null )
	{
		Program.Settings.Save();
		Environment.Exit( 0 );
	}

	private void MainForm_KeyDown( object sender, KeyEventArgs e )
	{
		HandleConfiguredShortcut( e );
	}

	internal void SetPriorityForMod( string modName )
	{
		if ( string.IsNullOrWhiteSpace( modName ) || session.LoadOrder == null || !session.LoadOrder.IsValid )
			return;

		ModLoadSetting setting = session.LoadOrder.GetModLoadSettingByName( modName );
		bool isExternal = setting?.IsExternal == true;

		int? priority;

		using ( PriorityPromptForm prompt = new PriorityPromptForm() )
		{
			priority = isExternal
				? prompt.ShowModIoDialog( modName, setting.Priority ?? 0, session.LoadOrder.GetModIoPriorityMaximum() )
				: prompt.ShowDialog( modName, setting?.Priority ?? CustomLoadOrder.BottomPriority );
		}

		if ( !priority.HasValue )
			return;

		try
		{
			if ( isExternal )
				session.LoadOrder.SetModIoPriorityByName( modName, priority.Value );
			else
				session.LoadOrder.SetPriorityByName( modName, priority.Value );

			treConflicts.SetStylesForCustomLoadOrder();
			RefreshConflictSearchResultsIfActive();
		}
		catch ( Exception exception ) when ( exception is IOException
			or UnauthorizedAccessException
			or System.Text.Json.JsonException
			or InvalidDataException
			or InvalidOperationException )
		{
			ShowError( exception.GetBaseException().Message,
				isExternal ? UiText.T( "MainForm", "Unable to Set Mod.io Priority" ) : UiText.T( "MainForm", "Unable to Set Priority" ) );
		}
	}

	private void ModFolderRenameRequested( object sender, ModFolderRenameRequestedEventArgs e )
	{
		RequestModFolderRename( e.OldName, e.NewName );
	}

	private void RequestModFolderRename( string oldName, string requestedName )
	{
		var result = ModFolderRenameService.Rename(
			session, Program.Settings.Get( "MergedModName" ), oldName, requestedName, pnlProgress.Visible );

		if ( result.Error is not null )
		{
			if ( result.Unavailable )
				ShowMessage( result.Error, UiText.T( "MainForm", "Rename Unavailable" ), MessageBoxButtons.OK, MessageBoxIcon.Information );
			else
				ShowError( result.Error, UiText.T( "MainForm", "Unable to Rename Mod Folder" ) );

			return;
		}

		if ( result.NewName is not null )
			ApplyRenamedModToCurrentView( oldName, result.NewName );
	}

	// Update both the scan data and displayed rows so a folder rename does not require a full scan.
	private void ApplyRenamedModToCurrentView( string oldName, string newName )
	{
		IEnumerable<ModFile> indexedFiles = session.Scan?.Files ?? Enumerable.Empty<ModFile>();

		foreach ( ModFile modFile in indexedFiles.Concat( conflictSearchSource ).Distinct() )
		{
			foreach ( FileHash mod in modFile.Mods.Where( mod => mod.Name.EqualsIgnoreCase( oldName ) ) )
			{
				mod.Name = newName;
			}
		}

		UpdateRenamedTreeNodes( treConflicts, oldName, newName );
		UpdateRenamedTreeNodes( treMerges, oldName, newName );
		UpdateRenamedMergeSearchNodes( oldName, newName );
		treConflicts.Sort();
		treMerges.Sort();

		if ( txtConflictModSearch.Text.Trim().EqualsIgnoreCase( oldName ) )
		{
			changingConflictSearchText = true;
			txtConflictModSearch.Text = newName;
			changingConflictSearchText = false;
		}

		UpdateConflictSearchSource();
		UpdateConflictSearchSuggestions();
		treConflicts.SetStylesForCustomLoadOrder();
		RefreshConflictSearchResultsIfActive();
		UpdateStatusText();
	}

	private static void UpdateRenamedTreeNodes( SMTree tree, string oldName, string newName )
	{
		foreach ( TreeNode modNode in tree.ModNodes.Where( node => node.Text.EqualsIgnoreCase( oldName ) ) )
		{
			modNode.Text = newName;

			SMTree.NodeMetadata metadata = modNode.GetMetadata();

			if ( metadata?.FileHash != null )
			{
				metadata.FileHash.Name = newName;
			}

			if ( metadata?.ModFile != null )
			{
				metadata.FilePath = metadata.ModFile.GetModFile( newName, tree.GamePaths );
			}
		}
	}

	// The progress screen also prevents new commands while an operation is running.
	private void InitializeProgressScreen( string progressOf, ProgressBarStyle style = ProgressBarStyle.Marquee )
	{
		progressBar.Value = 0;
		lblProgressCurrentPhase.Text = progressOf;
		lblProgressCurrentAction.Text = string.Empty;
		lblBundleScanInfo.Text = string.Empty;
		lblBundleScanInfo.Visible = false;
		progressBar.Style = style;

		switch ( style )
		{
			case ProgressBarStyle.Continuous:
				TaskbarProgress.SetValue( base.Handle, 0.0, 100.0 );
				TaskbarProgress.SetState( base.Handle, TaskbarProgress.TaskbarStates.Normal );
				break;

			case ProgressBarStyle.Marquee:
				TaskbarProgress.SetState( base.Handle, TaskbarProgress.TaskbarStates.Indeterminate );
				break;
		}

		pnlProgress.Visible = true;
		LayoutFullScreenProgressPanel();
		pnlProgress.BringToFront();
		LayoutProgressPanel();
		pnlProgress.Invalidate();
		Update();
	}

	private void HideProgressScreen()
	{
		tlpRoot.Present( () =>
		{
			pnlProgress.Visible = false;
			treMerges.Select();
		} );
		// Windows draws placeholder text separately from the panel. Refresh the search boxes after uncovering them.
		txtConflictModSearch.Refresh();
		txtConflictFileSearch.Refresh();
		TaskbarProgress.SetState( base.Handle, TaskbarProgress.TaskbarStates.NoProgress );
	}

	// Outdated counts would be misleading. Show a refresh notice until the scan is current again.
	internal void UpdateStatusText()
	{
		if ( UpdateDiagnosticFlags() && !IsConflictSearchActive && !conflictRefreshPending )
			ShowDefaultConflictView();

		UpdateScanDiagnosticsButton();
		RefreshPaneCounts();

		if ( scanSummary == null || session.Scan == null )
			return;

		if ( conflictRefreshPending )
		{
			scanSummary.SetSummary(
				new[] { UiText.T( "MainForm", "Refresh required" ) },
				Array.Empty<string>(),
				UiText.T( "MainForm", "The scan is out of date. Refresh to update conflicts and counts." ) );

			return;
		}

		var allConflicts = AllDisplayedConflicts();
		var conflicts = allConflicts.Where( file => !ignoredConflictKeys.Contains( IgnoredConflictStore.KeyFor( file ) ) ).ToList();
		int ignored = allConflicts.Count - conflicts.Count;
		int mergeable = conflicts.Count( file => file.Category.IsSupported || WitcherScriptMerger.BlobMerging.BlobConflictSelection.CanSelect( file ) );
		int scripts = conflicts.Count( file => file.Category == Categories.Script || file.Category == Categories.PrecompiledScript );
		string scanState = scanFailed ? UiText.T( "MainForm", "Scan failed" ) : session.Scan.ScanCompleted ? UiText.T( "MainForm", "Scan complete" ) : UiText.T( "MainForm", "Not scanned" );
		int issueCount = GetDiagnosticIssues().Count + ( scanFailed ? 1 : 0 );
		string[] totals = { UiText.F( "MainForm", $"Mergeable: {mergeable:N0}" ), UiText.F( "MainForm", $"Nonmergeable: {conflicts.Count - mergeable:N0}" ), UiText.F( "MainForm", $"Script conflicts: {scripts:N0}" ), UiText.F( "MainForm", $"Ignored: {ignored:N0}" ) };
		string[] scanned = {
			UiText.F("MainForm", $"Bundles: {session.Scan.BundleCount:N0}"), UiText.F("MainForm", $"XMLs: {session.Scan.XmlCount:N0}"),
			session.Scan.ScriptsScanned ? UiText.F("MainForm", $"Loose scripts: {session.Scan.ScriptCount:N0}") : UiText.T("MainForm", "Loose scripts: off"),
			session.Scan.ScriptsScanned ? UiText.F("MainForm", $"Script blobs: {session.Scan.BlobCount:N0}") : UiText.T("MainForm", "Script blobs: off")
		};

		scanned = new[] { scanState }
			.Concat( issueCount > 0 ? new[] { UiText.F( "MainForm", $"Issues: {issueCount:N0}" ) } : Array.Empty<string>() )
			.Concat( scanned ).ToArray();
		scanSummary.SetSummary( totals, scanned,
			UiText.Format(
				"MainForm",
				"Conflict totals exclude ignored conflicts and do not change with search filters. Ignored shows conflicts hidden by temporary or permanent ignore choices. Script conflicts includes loose-script files and precompiled symbols, so it overlaps the mergeable/nonmergeable totals.\nScanned counts cover active mod/DLC content, excluding vanilla and the generated merged mod. XMLs are bundled resource occurrences; loose XML files are not counted.\n{0}",
				string.Empty ).TrimEnd(),
			scanFailed ? ScanSummaryState.Failed : session.Scan.ScanCompleted ? ScanSummaryState.Complete : ScanSummaryState.NotScanned );
	}

	private void ApplyResponsiveStatusText() => scanSummary?.Invalidate();

	private void ShowScanIssuesReport() => ShowDiagnosticReport( null );

	internal void GameSettingsLocationChanged()
	{
		session.LoadOrder?.ReloadConfiguredLocation();
		SetConflictRefreshPending( true );
		scanSummary?.SetSummary( new[] { UiText.T( "MainForm", "Settings changed — refresh required" ) }, Array.Empty<string>(),
			UiText.T( "MainForm", "Refresh to scan with the selected game settings and Mod.io locations." ) );
	}

	private void BtnDontExit_Click( object sender, EventArgs e )
	{
		_autoExitTimer.Dispose();
		progressBar.Show();
		btnDontExit.Hide();
		btnExitNow.Hide();
		btnCreateMerges.Focus();
		HideProgressScreen();
		SetMergeButtonTextIfValidSelection();
	}

	private void BtnExitNow_Click( object sender, EventArgs e )
	{
		AutoExit();
	}

	public DialogResult ShowMessage(
		string text,
		string title = "",
		MessageBoxButtons buttons = MessageBoxButtons.OK,
		MessageBoxIcon icon = MessageBoxIcon.None )
	{
		ActivateSafely();

		if ( !base.InvokeRequired )
		{
			return MessageBox.Show( this, text, title, buttons, icon );
		}

		return Invoke( () => MessageBox.Show( this, text, title, buttons, icon ) );
	}

	public DialogResult ShowError( string text, string title = "Error" )
	{
		return ShowMessage( text, title, MessageBoxButtons.OK, MessageBoxIcon.Hand );
	}

	public DialogResult ShowModal( Form form )
	{
		ActivateSafely();

		if ( base.InvokeRequired )
		{
			return Invoke( delegate
			{
				if ( ( form is MergeReportForm || form is PackReportForm ) ? true : false )
				{
					Program.ThemeMngr.ApplyThemeOnForm( form );
				}

				return form.ShowDialog( this );
			} );
		}

		if ( ( form is MergeReportForm || form is PackReportForm ) ? true : false )
		{
			Program.ThemeMngr.ApplyThemeOnForm( form );
		}

		return form.ShowDialog( this );
	}

	public void ActivateSafely()
	{
		if ( base.InvokeRequired )
		{
			Invoke( (MethodInvoker)delegate
			{
				Activate();
			} );
		}
		else
		{
			Activate();
		}
	}

	protected override void Dispose( bool disposing )
	{
		if ( disposing )
			ResetConflictToolTip();

		if ( disposing )
			DisposeApplicationUpdates();

		if ( disposing && components != null )
		{
			components.Dispose();
		}

		if ( disposing )
		{
			treConflictSearchResults?.Dispose();
			conflictSearchModMenu?.Dispose();
			conflictSearchCategoryFont?.Dispose();
			conflictSearchMatchFont?.Dispose();
			paneCountFont?.Dispose();
		}

		base.Dispose( disposing );
	}

	// Check saved merges before scanning conflicts, since restoring an old merge can change the input files.
	internal async void RefreshTrees()
	{
		if ( Paths.ValidateModsDirectory() && Paths.ValidateScriptsDirectory() && Paths.ValidateBundlesDirectory() )
		{
			if ( session.Inventory == null )
			{
				await RefreshMergeInventory();
			}
			else
			{
				InitializeProgressScreen( UiText.T( "MainForm", "Loading Merges" ) );
				session.LoadOrder.Refresh();
				await Task.Run( (Func<bool>)RefreshMergeTree );
			}

			RefreshConflictsTree();
		}
	}

	private async Task<bool> RefreshMergeInventory()
	{
		InitializeProgressScreen(
			UiText.T( "MainForm", "Checking existing Merges and Merged Mod Directory" ),
			ProgressBarStyle.Continuous );
		LiveText.BindText( lblProgressCurrentAction, () => UiText.T( "MainForm", "Checking existing Merges and Merged Mod Directory" ) );
		progressBar.Value = 10;
		LiveText.BindText( lblProgressCurrentAction, () => UiText.T( "MainForm", "Loading MergeInventory.xml file" ) );
		session.Inventory = await Task.Run( session.InventoryStore.LoadAndBackfillHashes );
		progressBar.Value = 25;
		LiveText.BindText( lblProgressCurrentAction, () => UiText.T( "MainForm", "Loading mods.settings file" ) );
		session.LoadOrder = await Task.Run( () => new CustomLoadOrder( Program.EngineHost ) );
		progressBar.Value = 40;
		progressBar.Value = 75;
		LiveText.BindText( lblProgressCurrentAction, () => UiText.T( "MainForm", "Refreshing merge tree" ) );

		return await Task.Run( (Func<bool>)RefreshMergeTree );
	}

	private void OnMergeProgressChanged( object sender, ProgressChangedEventArgs e )
	{
		MergeProgressInfo mergeProgress = (MergeProgressInfo)e.UserState;

		lblProgressCurrentPhase.Text = mergeProgress.CurrentPhase;
		lblProgressCurrentAction.Text = mergeProgress.CurrentAction;
	}

	// A failed merge must not trigger another automatic attempt during the following refresh.
	private void OnMergeComplete( object sender, RunWorkerCompletedEventArgs e )
	{
		CompleteMergeOperation( e, session.Inventory.HasChanged,
			() => session.SaveInventory(),
			failed =>
			{
				if ( failed )
				{
					_manualRefreshRunning = true;
					_autoMergeStarted = true;
				}

				RefreshTrees();
			},
			() =>
			{
				HideProgressScreen();
				SetMergeButtonTextIfValidSelection();
			},
			message => ShowError( message, UiText.T( "MainForm", "Merge interrupted" ) ) );
	}

	private bool RefreshMergeTree()
	{
		MergeReconcileResult result = reconciler.Run();

		Invoke( (MethodInvoker)( () => RenderMergeTree( result ) ) );

		if ( result.MergesToDelete.Any() && Invoke( () => DeleteMerges( result.MergesToDelete ) ) )
			return true;

		if ( result.InventoryChanged )
		{
			session.SaveInventory();

			if ( result.PrunedBundleMerges.Any() )
				return Invoke( () => DeleteMerges( result.PrunedBundleMerges ) );
		}

		Invoke( (MethodInvoker)delegate
		{
			treMerges.RefreshCheckboxes();
			UpdateStatusText();
			SetUnmergeButtonTextIfValidSelection();
			progressBar.Value = 100;
		} );

		return false;
	}

	private void RenderMergeTree( MergeReconcileResult result )
	{
		foreach ( Merge merge in result.KeptMerges )
			AddMergeNode( merge );

		treMerges.GroupPrecompiledOutputs( result.LinkedOutputs );
		ThemeManager.AttachColorFaderToTreeNodes( treMerges, out _treMergesColorFaders );
		treMerges.Sort();
		treMerges.ExpandAll();
		treMerges.EndUpdateAtTop();
		UpdateMergeSearchSource();
		txtMergeModSearch.Enabled = txtMergeFileSearch.Enabled = true;

		if ( IsMergeSearchActive )
			UpdateMergeSearchView();
	}

	private void AddMergeNode( Merge merge )
	{
		TreeNode mergeNode = new TreeNode
		{
			Text = merge.RelativePath,
			ForeColor = ThemeManager.CurrentTheme.ForeBrightColor,
			Tag = new SMTree.NodeMetadata
			{
				FilePath = merge.GetMergedFile( session.GamePaths ),
				ModFile = merge
			}
		};
		TreeNode categoryNode = treMerges.GetCategoryNode( merge.Category );

		if ( categoryNode == null )
		{
			categoryNode = new TreeNode
			{
				Text = merge.Category.DisplayName,
				ToolTipText = merge.Category.ToolTipText,
				Tag = merge.Category
			};
			treMerges.Nodes.Add( categoryNode );
		}

		categoryNode.Nodes.Add( mergeNode );

		foreach ( FileHash source in merge.Mods.DistinctBy( source => source.Name, StringComparer.OrdinalIgnoreCase ) )
		{
			string sourcePath = source.SourceFilePath ?? merge.GetModFile( source.Name, session.GamePaths );

			mergeNode.Nodes.Add( new TreeNode
			{
				Text = source.Name,
				ForeColor = ThemeManager.CurrentTheme.ForeDimmedColor,
				Tag = new SMTree.NodeMetadata
				{
					FilePath = merge.IsBlobMerge
						? WitcherScriptMerger.Merging.MergeSourceLocations.PathFor( Paths.ModsDirectory, sourcePath, reconciler.MovedMergeFolders )
						: sourcePath,
					FileHash = source,
					ModFile = merge
				}
			} );
		}
	}

	private bool ConfirmPruneMissingMergeFile( Merge merge )
	{
		string msg = UiText.Format(
			"MainForm",
			"Can't find the merged version of the following file.\n\n{0}\n        {1}\n\nExpected path:\n{2}\n\n",
			merge.RelativePath,
			string.Join( "\n        ", merge.Mods.Select( ( FileHash mod ) => mod.Name ) ),
			merge.GetOutputFile( session.GamePaths ) );

		msg += merge.IsBundleContent
			? UiText.T( "MainForm", "Remove every merge in this bundle from the Merges list?" )
			: UiText.T( "MainForm", "Remove from Merges list?" );
		msg = DescribeManagedRemoval( merge, msg );

		return DialogResult.Yes == ShowMessage(
			msg,
			UiText.T( "MainForm", "Missing Merge Inventory File" ),
			MessageBoxButtons.YesNo,
			MessageBoxIcon.Question );
	}

	private bool ConfirmDeleteMergeForMissingMod( Merge merge, string modName )
	{
		string msg = UiText.Format(
			"MainForm",
			"Can't find the '{0}' version of the following file, perhaps because the mod was uninstalled or updated.\n\n{1}\n        {2}\n\nExpected path:\n{3}\n\n",
			modName,
			merge.RelativePath,
			string.Join( "\n        ", merge.Mods.Select( ( FileHash mod ) => mod.Name ) ),
			merge.GetModFile( modName, session.GamePaths ) );

		msg += ( merge.IsBundleContent ? UiText.T( "MainForm", "Delete this affected merge & repack the merged bundle?" ) : UiText.T( "MainForm", "Delete this affected merge?" ) );
		msg = DescribeManagedRemoval( merge, msg );

		return DialogResult.Yes == ShowMessage(
			msg,
			UiText.T( "MainForm", "Missing Merge Inventory File" ),
			MessageBoxButtons.YesNo,
			MessageBoxIcon.Question );
	}

	private bool ConfirmDeleteMergeForDisabledMod( Merge merge, string modName )
	{
		string msg = UiText.Format(
			"MainForm",
			"In your custom load order, {0} is disabled.\nDelete the following merge that includes the disabled mod?\n\n{1}\n        {2}",
			modName,
			merge.RelativePath,
			string.Join( "\n        ", merge.Mods.Select( ( FileHash mod ) => mod.Name ) ) );

		msg = DescribeManagedRemoval( merge, msg );

		return DialogResult.Yes == ShowMessage(
			msg,
			UiText.T( "MainForm", "Disabled Mod in Merge" ),
			MessageBoxButtons.YesNo,
			MessageBoxIcon.Question );
	}

	private static DialogResult PromptToDeleteForChangedHash( Merge merge, string modFilePath, string modName )
	{
		string text = merge.IsBundleContent
			? UiText.Format(
				"MainForm",
				"The '{0}' bundle is different from when it was used in a merge, perhaps because the mod has been updated.\n\nThis file has changed:\n\n{1}\n\nThis merge is affected:\n\n{2}\n        {3}\n\nDelete this affected merge & repack the merged bundle?",
				modName,
				modFilePath,
				merge.RelativePath,
				string.Join( "\n        ", merge.Mods.Select( ( FileHash mod ) => mod.Name ) ) )
			: UiText.Format(
				"MainForm",
				"The '{0}' version of the following file is different from when it was used in a merge, perhaps because the mod has been updated.\n\nThis file has changed:\n\n{1}\n\nThis merge is affected:\n\n{2}\n        {3}\n\nDelete this affected merge?",
				modName,
				modFilePath,
				merge.RelativePath,
				string.Join( "\n        ", merge.Mods.Select( ( FileHash mod ) => mod.Name ) ) );

		text = DescribeManagedRemoval( merge, text );

		return MessageBox.Show(
			text,
			UiText.T( "MainForm", "Merged Mod File Changed" ),
			MessageBoxButtons.YesNo,
			MessageBoxIcon.Exclamation );
	}

	private void RefreshConflictsTree()
	{
		conflictSearchTimer.Stop();
		conflictPreparation.ClearSourceHashes();
		EnsureIgnoredConflictStore();
		RestoreHiddenConflictNodes();
		treConflicts.ClearFileSelection();
		treConflictSearchResults.ClearFileSelection();
		txtConflictModSearch.Enabled = false;
		txtConflictFileSearch.Enabled = false;
		InitializeProgressScreen( UiText.T( "MainForm", "Detecting Conflicts" ), ProgressBarStyle.Continuous );
		LiveText.BindText( lblStatusLeft1, () => UiText.T( "MainForm", "Refreshing..." ) );

		ToolStripStatusLabel toolStripStatusLabel = lblStatusLeft2;
		bool visible = ( lblStatusLeft3.Visible = false );

		toolStripStatusLabel.Visible = visible;
		treConflicts.BeginUpdate();

		if ( session.Inventory.ScriptsChanged && session.Inventory.BundleChanged )
		{
			treConflicts.Nodes.Clear();
		}
		else
		{
			List<TreeNode> nodesToUpdate = new List<TreeNode>();
			TreeNode scriptCatNode = treConflicts.GetCategoryNode( Categories.Script );

			if ( scriptCatNode != null )
			{
				nodesToUpdate.Add( scriptCatNode );
			}

			TreeNode precompiledScriptCatNode = treConflicts.GetCategoryNode( Categories.PrecompiledScript );

			if ( precompiledScriptCatNode != null )
			{
				nodesToUpdate.Add( precompiledScriptCatNode );
			}

			foreach ( var category in new[] { Categories.XmlDefinitionOverride, Categories.DlcBundleResource } )
			{
				TreeNode categoryNode = treConflicts.GetCategoryNode( category );

				if ( categoryNode != null )
					nodesToUpdate.Add( categoryNode );
			}

			TreeNode xmlCatNode = treConflicts.GetCategoryNode( Categories.Xml );

			if ( xmlCatNode != null )
			{
				nodesToUpdate.Add( xmlCatNode );
			}

			{
				TreeNode bundleTextCatNode = treConflicts.GetCategoryNode( Categories.BundleText );

				if ( bundleTextCatNode != null )
				{
					nodesToUpdate.Add( bundleTextCatNode );
				}

				TreeNode bundleNotMergeableCatNode = treConflicts.GetCategoryNode( Categories.BundleNotMergeable );

				if ( bundleNotMergeableCatNode != null )
				{
					nodesToUpdate.Add( bundleNotMergeableCatNode );
				}
			}

			IEnumerable<TreeNode> missingFileNodes = treConflicts.SymbolNodes.Where( ( TreeNode node2 ) => node2.GetTreeNodes().Any( ( TreeNode modNode ) => !File.Exists( modNode.GetMetadata().FilePath ) ) );

			nodesToUpdate.AddRange( missingFileNodes );

			foreach ( TreeNode node in nodesToUpdate )
			{
				node.RemoveFromTree();
			}

			foreach ( TreeNode catNode in treConflicts.CategoryNodes )
			{
				if ( catNode.Nodes.Count == 0 )
				{
					treConflicts.Nodes.Remove( catNode );
				}
			}
		}

		scanFailed = false;
		scanSummary?.SetSummary(
			new[] { UiText.T( "MainForm", "Scanning…" ) },
			Array.Empty<string>(),
			UiText.T( "MainForm", "Scanning active content" ) );
		session.Scan = new ModFileIndex(
			gamePaths: session.GamePaths,
			host: Program.EngineHost,
			bundleScanner: null,
			previousPreflight: session.Scan?.MergePreflight,
			loadOrder: () => session.LoadOrder );
		UpdateScanDiagnosticsButton();
		session.Scan.BuildAsync( checkScripts: true, checkBundles: true, OnRefreshConflictsProgressChanged, OnRefreshConflictsComplete );
	}

	private async void OnRefreshConflictsComplete( object sender, RunWorkerCompletedEventArgs e )
		=> await CompleteConflictScanAsync( e );

	internal async Task CompleteConflictScanAsync( RunWorkerCompletedEventArgs e )
	{
		lblBundleScanInfo.Visible = false;

		if ( e.Error != null )
		{
			scanFailed = true;
			UpdateStatusText();
			// A failed scan cannot confirm that the files behind temporary ignores are unchanged.
			ignoredConflictKeys = AllDisplayedConflicts()
				.Where( file => ignoredConflictStore?.IsPermanent( file ) == true )
				.Select( IgnoredConflictStore.KeyFor ).ToHashSet();
			HideIgnoredConflictNodes();
			treConflicts.EndUpdate();
			HideProgressScreen();
			ShowDefaultConflictView();
			txtConflictModSearch.Enabled = true;
			txtConflictFileSearch.Enabled = true;
			UpdateConflictSearchSource();
			UpdateConflictSearchSuggestions();
			ShowError(
				UiText.Format(
					"MainForm",
					"The detailed conflict scan failed, so partial bundle-index results were not accepted.\n\n{0}",
					e.Error.GetBaseException().Message ),
				UiText.T( "MainForm", "Conflict Scan Failed" ) );

			return;
		}

		SetConflictScanProgress( 90, new ConflictScanProgress( UiText.T( "MainForm", "Preparing conflict results" ) ) );

		try
		{
			var conflicts = await Task.Run( PrepareConflictNodes );

			SetConflictScanProgress( 94, new ConflictScanProgress( UiText.T( "MainForm", "Updating the conflict list" ) ) );
			PopulatePreparedConflictNodes( conflicts );
		}
		catch ( Exception error )
		{
			await CompleteConflictScanAsync( new RunWorkerCompletedEventArgs( null, error, false ) );

			return;
		}

		treConflicts.EndUpdate();
		SetConflictScanProgress( 96, new ConflictScanProgress( UiText.T( "MainForm", "Applying ignore rules" ) ) );
		await RefreshIgnoredConflictsAsync( refreshCheckboxes: false );
		SetConflictScanProgress( 98, new ConflictScanProgress( UiText.T( "MainForm", "Updating the conflict list" ) ) );
		session.MarkScanCurrent();
		treConflicts.RefreshMergeCheckboxes();
		SetConflictRefreshPending( false );
		btnCreateAllScriptMerges.Enabled = !IsConflictSearchActive;
		treConflicts.SetStylesForCustomLoadOrder();
		treConflicts.ApplyInitialExpansion(
			Program.Settings.Get<bool>( "CollapseNotMergeable" ),
			Program.Settings.Get<bool>( "CollapseCustomLoadOrder" ),
			Program.Settings.Get<bool>( "CollapseIdenticalConflicts" ) );
		ShowDefaultConflictView();
		treConflicts.ScrollToTop();
		UpdateStatusText();
		SetMergeButtonTextIfValidSelection();
		ThemeManager.AttachColorFaderToTreeNodes( treConflicts, out _treConflictsColorFaders );
		UpdateConflictSearchSource();
		UpdateConflictSearchSuggestions();

		if ( IsConflictSearchActive )
			UpdateConflictSearchView();

		txtConflictModSearch.Enabled = true;
		txtConflictFileSearch.Enabled = true;
		SetConflictScanProgress( 100, new ConflictScanProgress( UiText.T( "MainForm", "Scan complete" ) ) );
		HideProgressScreen();

		if ( Program.Settings.Get<bool>( "AutoCreateScriptMerges" ) )
			CreateAllScriptMerges();

		if ( CanAutoExitAfterScan )
			ExitIfNoMoreConflicts();
	}

	private List<(ModFile Conflict, Merge Merge)> PrepareConflictNodes()
	{
		return conflictPreparation.Prepare( session.Scan.Conflicts, session.Inventory, session.GamePaths );
	}

	// Sort once after adding all rows; sorting after every insertion slows large scans.
	private void PopulatePreparedConflictNodes( List<(ModFile Conflict, Merge Merge)> conflicts )
	{
		Dictionary<string, TreeNode> filesByPath;
		Dictionary<ModFileCategory, TreeNode> categories;
		System.Collections.IComparer sorter;

		ResetConflictToolTip();
		filesByPath = treConflicts.SymbolNodes.GroupBy( node => node.Text, StringComparer.InvariantCultureIgnoreCase )
			.ToDictionary( group => group.Key, group => group.First(), StringComparer.InvariantCultureIgnoreCase );
		categories = treConflicts.CategoryNodes.ToDictionary( node => (ModFileCategory)node.Tag );
		sorter = treConflicts.TreeViewNodeSorter;
		treConflicts.TreeViewNodeSorter = null;
		treConflicts.Sorted = false;

		try
		{
			foreach ( var (conflict, merge) in conflicts )
			{
				if ( ReferenceEquals( conflict.Category, Categories.XmlDefinitionOverride ) && !conflict.HasConflict )
					continue;

				if ( !filesByPath.TryGetValue( conflict.RelativePath, out TreeNode fileNode ) )
				{
					fileNode = AddConflictFileNode( conflict, categories );
					filesByPath.Add( conflict.RelativePath, fileNode );
				}

				AddConflictSourceNodes( conflict, merge, fileNode );
			}
		}
		finally
		{
			treConflicts.TreeViewNodeSorter = sorter;
		}

		ApplyLinkedMergeGroups();
	}

	private TreeNode AddConflictFileNode( ModFile conflict, Dictionary<ModFileCategory, TreeNode> categories )
	{
		TreeNode fileNode = new TreeNode
		{
			Text = conflict.RelativePath,
			ToolTipText = conflict.GetSemanticConflictDescription(),
			ForeColor = ThemeManager.CurrentTheme.ForeBrightColor,
			Tag = new SMTree.NodeMetadata
			{
				FilePath = ( conflict.Category == Categories.Script || conflict.Category == Categories.Xml ) ? conflict.GetVanillaFile( session.GamePaths ) : conflict.RelativePath,
				ModFile = conflict
			}
		};

		if ( !categories.TryGetValue( conflict.Category, out TreeNode categoryNode ) )
		{
			categoryNode = new TreeNode
			{
				Text = conflict.Category.DisplayName,
				ToolTipText = conflict.Category.ToolTipText,
				NodeFont = new Font( "Segoe UI", 14.75f, FontStyle.Bold ),
				ForeColor = ThemeManager.CurrentTheme.ForeColor,
				Tag = conflict.Category
			};
			treConflicts.Nodes.Add( categoryNode );
			categories.Add( conflict.Category, categoryNode );
		}

		categoryNode.Nodes.Add( fileNode );

		return fileNode;
	}

	private void AddConflictSourceNodes( ModFile conflict, Merge merge, TreeNode fileNode )
	{
		foreach ( FileHash mod in ConflictRowSources( conflict, merge, session.GamePaths ) )
		{
			FileHash mergeModHash = merge?.Mods.FirstOrDefault( ( FileHash m ) => m.Name.EqualsIgnoreCase( mod.Name ) );
			TreeNode modNode = fileNode.GetTreeNodes().FirstOrDefault( ( TreeNode node ) => node.Text.EqualsIgnoreCase( mod.Name ) );

			if ( modNode == null )
			{
				fileNode.Nodes.Add( new TreeNode
				{
					Text = mod.Name,
					Tag = new SMTree.NodeMetadata
					{
						FilePath = conflict.GetModFile( mod.Name, session.GamePaths ),
						FileHash = mergeModHash ?? mod,
						ModFile = conflict
					}
				} );
			}
			else if ( ReferenceEquals( conflict.Category, Categories.PrecompiledScript ) )
			{
				modNode.GetMetadata().ModFile = conflict;
				modNode.GetMetadata().FileHash = mod;
				modNode.GetMetadata().FilePath = conflict.GetModFile( mod.Name, session.GamePaths );
			}
		}
	}

	private void ExitIfNoMoreConflicts()
	{
		if ( treConflicts.GetCategoryNode( Categories.Script ) == null )
		{
			InitializeProgressScreen( UiText.T( "MainForm", "Merging complete, auto closing in 2 seconds..." ) );
			progressBar.Hide();
			btnDontExit.Show();
			btnExitNow.Show();
			btnExitNow.Focus();
			_autoExitTimer = new System.Threading.Timer( AutoExit, null, 2000, 0 );
		}
	}

	private void OnRefreshConflictsProgressChanged( object sender, ProgressChangedEventArgs e )
	{
		SetConflictScanProgress( e.ProgressPercentage, e.UserState as ConflictScanProgress
			?? new ConflictScanProgress( UiText.T( "MainForm", "Detecting Conflicts" ), e.UserState as string ?? string.Empty ) );
	}

	internal void SetConflictScanProgress( int percentage, ConflictScanProgress progress )
	{
		progressBar.Value = percentage;
		lblProgressCurrentPhase.Text = progress.Phase;
		lblProgressCurrentAction.Text = progress.CurrentAction;
		TaskbarProgress.SetState( base.Handle, TaskbarProgress.TaskbarStates.Normal );
		TaskbarProgress.SetValue( base.Handle, percentage, 100.0 );
		pnlProgress.Update();
	}

	private void OnTreeViewMouseMove( object sender, MouseEventArgs e )
	{
		TreeView tv = (TreeView)sender;

		if ( tv == treConflicts )
		{
			_treConflictsPrevNode = HandleTreeViewMouseMoves( tv, _treConflictsColorFaders, _treConflictsPrevNode, e );
			ShowConflictsToolTip( tv, e );

			return;
		}

		if ( tv != treMerges )
		{
			throw new NotImplementedException( UiText.Format( "MainForm", "Unknown Treeview: {0}", tv.Name ) );
		}

		_treMergesPrevNode = HandleTreeViewMouseMoves( tv, _treMergesColorFaders, _treMergesPrevNode, e );
	}

	private void ShowConflictsToolTip( TreeView tv, MouseEventArgs e )
	{
		QueueConflictToolTip( tv, e );
	}

	internal static bool AllowsConflictToolTip( TreeNode node, int priorityToolTips )
	{
		if ( node?.GetMetadata()?.MergeGroup != null || node?.GetMetadata()?.IsLinkedFile == true )
			return true;

		ModFile conflict = node?.GetMetadata()?.ModFile ?? node?.Tag as ModFile
			?? node?.Parent?.GetMetadata()?.ModFile ?? node?.Parent?.Tag as ModFile;
		ModFileCategory category = conflict?.Category ?? node?.Tag as ModFileCategory
			?? node?.Parent?.Tag as ModFileCategory ?? node?.Parent?.Parent?.Tag as ModFileCategory;

		return AllowsConflictToolTip( conflict, category?.IsBundled == true, priorityToolTips );
	}

	internal static bool AllowsConflictToolTip( ModFile conflict, bool isBundledNode, int priorityToolTips )
	{
		return conflict?.HasBlockedLooseWrapperConflict == true || priorityToolTips switch
		{
			1 => isBundledNode,
			2 => !isBundledNode,
			3 => true,
			_ => false
		};
	}

	private TreeNode HandleTreeViewMouseMoves( TreeView tv, List<TreeNodeColorFader> colorFaders, TreeNode previousNode, MouseEventArgs e )
	{
		TreeNode hoveredNode = tv.GetNodeAt( e.Location );
		TreeNodeColorFader previousHoveredColorFader = colorFaders.Find( ( TreeNodeColorFader f ) => f.OutputNode == previousNode );
		TreeNodeColorFader? treeNodeColorFader = colorFaders.Find( ( TreeNodeColorFader f ) => f.OutputNode == hoveredNode );

		if ( hoveredNode != previousNode )
		{
			Color targetForeColor = ThemeManager.CurrentTheme.ForeDimmedColor;

			if ( previousNode?.Tag is SMTree.NodeMetadata tag && tag?.ForeColor != Color.Empty )
			{
				targetForeColor = tag.ForeColor;
			}

			previousHoveredColorFader?.StartFadeOut( targetForeColor );
		}

		treeNodeColorFader?.StartFadeIn( ThemeManager.CurrentTheme.ForeBrightColor );

		return hoveredNode;
	}

	private async void CreateAllScriptMerges( bool manual = false )
	{
		if ( !RequireCurrentConflictScan() )
			return;

		if ( !_autoMergeStarted || manual )
		{
			_autoMergeStarted = true;

			var scriptFiles = GetAllScriptMergeCandidates( treConflicts.FileNodes, session.Inventory.Merges );

			foreach ( var file in scriptFiles )
			{
				file.SetCheckedIfVisible( true );

				foreach ( var source in file.GetTreeNodes() )
					source.SetCheckedIfVisible( true );
			}

			if ( scriptFiles.Any( IsMergeableConflictSelection ) )
			{
				SetMergeButtonTextIfValidSelection();
				await MergeFilesAsync( looseScriptsOnly: true, allowSearch: !manual );
			}
		}
	}

	private bool CheckSubNodesIfVisible( TreeNode baseNode )
	{
		if ( !CheckNodeIfVisible( baseNode ) )
		{
			return false;
		}

		foreach ( TreeNode node in baseNode.Nodes )
		{
			if ( !CheckSubNodesIfVisible( node ) )
			{
				return false;
			}
		}

		return true;
	}

	private static bool CheckNodeIfVisible( TreeNode node )
	{
		if ( node.IsCheckBoxVisible() )
		{
			node.Checked = true;

			return true;
		}

		return false;
	}


	public void SetMergeButtonTextIfValidSelection()
	{
		int validFileNodeCount = session.IsScanCurrent ? MergeSelectionNodes().Count( IsMergeableConflictSelection ) : 0;

		btnCreateMerges.Text = ( ( validFileNodeCount > 1 ) ? ( UiText.Format( "MainForm", "&Create {0} Merges", validFileNodeCount ) ) : UiText.T( "MainForm", "&Create Selected Merges" ) );
		btnCreateMerges.Enabled = session.IsScanCurrent && ( !IsConflictSearchActive || validFileNodeCount > 0 );

		if ( !session.IsScanCurrent )
			btnCreateAllScriptMerges.Enabled = false;

		LiveText.BindToolTip( toolTip1, btnCreateMerges, () => ShortcutHint( "CreateSelected" ) );
	}

	public void SetUnmergeButtonTextIfValidSelection()
	{
		int selectedCount = treMerges.FileNodes.Count( ( TreeNode node ) => node.Checked );

		btnDeleteMerges.Text = ( ( selectedCount > 1 ) ? ( UiText.Format( "MainForm", "&Delete {0} Merges", selectedCount ) ) : UiText.T( "MainForm", "&Delete Selected Merges" ) );
	}

	public bool IsValidMergeSelection()
	{
		if ( !RequireCurrentConflictScan() )
			return false;

		if ( MergeSelectionNodes().Any( IsMergeableConflictSelection ) )
		{
			return true;
		}

		ShowMessage(
			UiText.T( "MainForm", "You have not selected any files to merge!" ),
			UiText.T( "MainForm", "No files selected" ),
			MessageBoxButtons.OK,
			MessageBoxIcon.Exclamation );

		return false;
	}

	public bool IsValidUnMergeSelection()
	{
		if ( treMerges.FileNodes.Count( ( TreeNode node ) => node.Checked ) > 0 )
		{
			return true;
		}

		ShowMessage(
			UiText.T( "MainForm", "You have not selected any merges to delete!" ),
			UiText.T( "MainForm", "No files selected" ),
			MessageBoxButtons.OK,
			MessageBoxIcon.Exclamation );

		return false;
	}

	private async void BtnRefreshMerged_Click( object sender, EventArgs e )
	{
		if ( string.IsNullOrWhiteSpace( Paths.GameDirectory ) )
		{
			Program.MainFrm.ShowMessage( UiText.T( "MainForm", "Please locate your 'The Witcher 3 Wild Hunt' game directory." ) );

			return;
		}

		if ( Paths.ValidateModsDirectory() )
		{
			bool hadManagedMerge = session.HasManagedBlobState();

			reconciler.ManagedMergesChanged = false;
			await RefreshMergeInventory();

			if ( reconciler.ManagedMergesChanged || hadManagedMerge && !session.HasManagedBlobState() )
			{
				RefreshConflictsTree();

				return;
			}
		}

		HideProgressScreen();
	}

	private void BtnRefreshConflicts_Click( object sender, EventArgs e )
	{
		if ( string.IsNullOrWhiteSpace( Paths.GameDirectory ) )
		{
			Program.MainFrm.ShowMessage( UiText.T( "MainForm", "Please locate your 'The Witcher 3 Wild Hunt' game directory." ) );

			return;
		}

		_manualRefreshRunning = true;
		RefreshTrees();
	}

	private async void BtnMergeFiles_Click( object sender, EventArgs e )
	{
		await MergeFilesAsync();
	}

	// Blob merges may also resolve loose conflicts. Run them first, then remove those inputs from the ordinary queue.
	private async Task MergeFilesAsync( bool looseScriptsOnly = false, bool allowSearch = false )
	{
		string mergedModName;
		int cleanupRevision;
		IEnumerable<TreeNode> candidates;
		List<TreeNode> fileNodes;
		ScriptMergePlan plan;

		if ( !CanStartMerge( looseScriptsOnly, allowSearch ) )
			return;

		mergedModName = Paths.RetrieveMergedModName();

		if ( mergedModName == null )
			return;

		session.LoadInventory();
		cleanupRevision = reconciler.UnregisteredRevision;
		candidates = looseScriptsOnly
			? GetAllScriptMergeCandidates( treConflicts.FileNodes, session.Inventory.Merges )
			: MergeSelectionNodes();
		fileNodes = candidates.Where( IsMergeableConflictSelection ).ToList();
		plan = ScriptMergeService.Plan( fileNodes.Select( SelectionFor ), session.Inventory.Merges );

		// Check output availability before cleanup can delete unregistered files.
		if ( ( plan.HasWork || fileNodes.Count > 0 ) && !CanWriteMerges( plan, fileNodes, mergedModName ) )
			return;

		if ( !GetIsInventoryInOrder( inventory: session.Inventory ) )
			return;

		// Unregistered files were removed during that check, so the rows on screen may be out of date.
		if ( cleanupRevision != reconciler.UnregisteredRevision )
		{
			RefreshTrees();

			return;
		}

		if ( plan.HasWork )
		{
			var appliedInputs = await MergeBlobConflicts( plan );

			if ( appliedInputs is null )
			{
				RefreshTrees();

				return;
			}

			// The blob merge may have used these loose inputs. Remove them from the ordinary queue using the reloaded inventory.
			fileNodes = RemainingOrdinaryMergeCandidates( fileNodes, session.Inventory.Merges, appliedInputs ).ToList();
		}

		if ( fileNodes.Count == 0 )
		{
			RefreshTrees();

			return;
		}

		InitializeProgressScreen( UiText.T( "MainForm", "Merging" ) );

		FileMerger fileMerger = new FileMerger(
			session.Inventory,
			session.GamePaths,
			Program.EngineHost,
			this,
			OnMergeProgressChanged,
			OnMergeComplete );

		fileMerger.MergeAsync( fileNodes.Select( SelectionFor ).ToArray(), mergedModName );
	}

	private bool CanStartMerge( bool looseScriptsOnly, bool allowSearch )
	{
		bool hasCandidates;

		if ( !RequireCurrentConflictScan() )
			return false;

		if ( IsConflictSearchActive && !allowSearch && !MergeSelectionNodes().Any( IsMergeableConflictSelection ) )
		{
			FocusActiveConflictSearch();

			return false;
		}

		hasCandidates = looseScriptsOnly
			? GetAllScriptMergeCandidates( treConflicts.FileNodes, session.Inventory.Merges ).Any( IsMergeableConflictSelection )
			: IsValidMergeSelection();

		if ( !hasCandidates || !Paths.ValidateModsDirectory() )
			return false;

		if ( treConflicts.FileNodes.Any( node => ModFile.IsScript( node.Text ) ) && !Paths.ValidateScriptsDirectory() )
			return false;

		return !treConflicts.FileNodes.Any( node => ModFile.IsBundle( node.Text ) ) || Paths.ValidateBundlesDirectory();
	}

	private void BtnDeleteMerges_Click( object sender, EventArgs e )
	{
		if ( mergeSearchTimer.Enabled )
			UpdateMergeSearchView();

		if ( IsValidUnMergeSelection() )
		{
			IEnumerable<TreeNode> fileNodes = treMerges.FileNodes.Where( ( TreeNode node ) => node.Checked );

			DeleteMerges( fileNodes );
		}
	}

	private void BtnDeleteAllMerges_Click( object sender, EventArgs e )
	{
		if ( mergeSearchTimer.Enabled )
			UpdateMergeSearchView();

		List<TreeNode> fileNodes = treMerges.FileNodes;

		DeleteMerges( fileNodes, deleteAll: true );
	}

	private void BtnCreateAllMerges_Click( object sender, EventArgs e )
	{
		if ( IsConflictSearchActive )
		{
			FocusActiveConflictSearch();

			return;
		}

		CreateAllScriptMerges( manual: true );
	}

	public async void DeleteMerges( IEnumerable<TreeNode> fileNodes, bool deleteAll = false )
	{
		await mergeDeletions.DeleteAsync( fileNodes.SelectMany( MergeTree.MergesForNode ).Distinct().ToList(), deleteAll );
	}

	private bool DeleteMerges( List<Merge> merges )
	{
		return mergeDeletions.Delete( merges );
	}
}
