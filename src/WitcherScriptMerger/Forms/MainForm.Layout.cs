using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using WitcherScriptMerger.Controls;
using WitcherScriptMerger.FileIndex;
using WitcherScriptMerger.Theming;

namespace WitcherScriptMerger.Forms;

internal partial class MainForm
{
	private void InitializeCustomComponent()
	{
		CreateConflictTree();
		ConfigureMainLayout();
		CreateConflictSearchBoxes();
		CreateConflictSearchTimerAndFonts();
		CreateConflictSearchResults();
		CreateConflictSearchMenu();
		AddSearchVanillaActions();
		CreateMergeTree();
		InitializeMergeSearch();
		CreateNoConflictsLabel();
		InitializePolishedWorkspaceChrome();
	}

	private void CreateConflictTree()
	{
		treConflicts = new ConflictTree( ThemeManager.CurrentTheme )
		{
			Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
			BorderStyle = BorderStyle.None,
			CheckBoxes = true,
			Font = new Font( "Segoe UI", 11f, FontStyle.Regular, GraphicsUnit.Point ),
			FullRowSelect = true,
			HideSelection = false,
			ItemHeight = 28,
			Location = new Point( 4, 74 ),
			Margin = new Padding( 4, 0, 4, 0 ),
			Name = "treConflicts",
			ShowLines = false,
			Size = new Size( 470, 809 ),
			Sorted = true,
			TabIndex = 1,
			TabStop = false
		};
		treConflicts.ModFolderRenameRequested += ModFolderRenameRequested;
		treConflicts.ModPriorityRequested += SetPriorityForMod;
		tlpMain.SetColumnSpan( treConflicts, 2 );
		tlpMain.Controls.Add( treConflicts, 1, 1 );
	}

	private void ConfigureMainLayout()
	{
		tlpMain.ColumnStyles[0].Width = 6f;
		tlpMain.ColumnStyles[3].Width = 10f;
		tlpMain.ColumnStyles[6].Width = 6f;
		tlpMain.RowStyles[0].Height = 96f;
		tlpMain.RowStyles[2].Height = 70f;

		foreach ( TableLayoutPanel header in new[] { tlpConflictsHeader, tlpMergesHeader } )
		{
			header.RowCount = 2;
			header.RowStyles.Clear();
			header.RowStyles.Add( new RowStyle( SizeType.Absolute, 52f ) );
			header.RowStyles.Add( new RowStyle( SizeType.Percent, 100f ) );
		}
	}

	private void CreateMergeTree()
	{
		treMerges = new MergeTree( ThemeManager.CurrentTheme )
		{
			Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
			BorderStyle = BorderStyle.None,
			CheckBoxes = true,
			Font = new Font( "Segoe UI", 11f, FontStyle.Regular, GraphicsUnit.Point ),
			FullRowSelect = true,
			HideSelection = false,
			ItemHeight = 28,
			Location = new Point( 482, 74 ),
			Margin = new Padding( 4, 0, 4, 0 ),
			Name = "treMerges",
			ShowLines = false,
			ShowNodeToolTips = true,
			Size = new Size( 470, 809 ),
			Sorted = true,
			TabIndex = 1,
			TabStop = false
		};
		tlpMain.SetColumnSpan( treMerges, 2 );
		tlpMain.Controls.Add( treMerges, 4, 1 );
	}

	private void CreateNoConflictsLabel()
	{
		lblNoConflicts = LiveText.Live( new Label
		{
			AutoSize = false,
			Dock = DockStyle.Fill,
			Font = new Font( "Segoe UI", 13.75f, FontStyle.Regular, GraphicsUnit.Point ),
			Margin = new Padding( 0 ),
			Name = "lblNoConflicts",
			Text = UiText.T( "MainForm", "No conflicts detected" ),
			TextAlign = ContentAlignment.MiddleCenter
		}, control => LiveText.BindText( control, () => UiText.T( "MainForm", "No conflicts detected" ) ) );
	}

	// Adds the resizable panes and action bars to the designer-created controls.
	private void InitializePolishedWorkspaceChrome()
	{
		tlpConflictsHeader.ColumnStyles.Clear();
		tlpConflictsHeader.ColumnStyles.Add( new ColumnStyle( SizeType.Absolute, 104f ) );
		tlpConflictsHeader.ColumnStyles.Add( new ColumnStyle( SizeType.Percent, 100f ) );
		tlpConflictsHeader.ColumnStyles.Add( new ColumnStyle( SizeType.Absolute, 112f ) );
		tlpConflictsHeader.Margin = new Padding( 4, 4, 4, 0 );

		tlpMergesHeader.ColumnStyles.Clear();
		tlpMergesHeader.ColumnStyles.Add( new ColumnStyle( SizeType.Percent, 50f ) );
		tlpMergesHeader.ColumnStyles.Add( new ColumnStyle( SizeType.Percent, 50f ) );
		tlpMergesHeader.ColumnStyles.Add( new ColumnStyle( SizeType.Absolute, 112f ) );
		tlpMergesHeader.Controls.Remove( lblMerges );
		tlpMergesHeader.Controls.Add( lblMerges, 0, 0 );
		tlpMergesHeader.SetColumnSpan( lblMerges, 2 );
		tlpMergesHeader.Margin = new Padding( 4, 4, 4, 0 );

		ConfigureSectionHeading( lblConflicts );
		ConfigureSectionHeading( lblMerges );

		paneCountFont = new Font( "Segoe UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point );
		lblConflicts.Paint += PaintPaneCount;
		lblMerges.Paint += PaintPaneCount;

		( (ModernButton)btnMainMenu ).Glyph = ButtonGlyph.Menu;
		( (ModernButton)btnRefreshConflicts ).Glyph = ButtonGlyph.Refresh;
		( (ModernButton)btnRefreshMerged ).Glyph = ButtonGlyph.Refresh;

		InitializeScanDiagnosticsButton();
		treConflicts.DiagnosticRequested += ShowDiagnosticIssues;

		btnMainMenu.Margin = new Padding( 10, 0, 4, 0 );
		btnRefreshConflicts.Margin = new Padding( 4, 0, 10, 0 );
		btnRefreshMerged.Margin = new Padding( 4, 0, 10, 0 );

		tlpConflictActions = CreateActionBar( "tlpConflictActions", btnCreateMerges, btnCreateAllScriptMerges );
		tlpMergeActions = CreateActionBar( "tlpMergeActions", btnDeleteMerges, btnDeleteAllMerges );
		tlpMain.Controls.Add( tlpConflictActions, 1, 2 );
		tlpMain.SetColumnSpan( tlpConflictActions, 2 );
		tlpMain.Controls.Add( tlpMergeActions, 4, 2 );
		tlpMain.SetColumnSpan( tlpMergeActions, 2 );
		InitializeWorkspaceSplitter();
		InitializeRootLayout();
		RefreshModernLayoutMetrics();

		tlpMain.Paint += PaintWorkspaceCards;
		pnlProgress.Paint += PaintProgressSurface;
		pnlProgress.Resize += ( _, _ ) => LayoutProgressPanel();
		lblProgressCurrentPhase.TextChanged += ( _, _ ) => ScheduleProgressLayout();
		lblProgressCurrentAction.TextChanged += ( _, _ ) => ScheduleProgressLayout();
		lblProgressCurrentPhase.FontChanged += ( _, _ ) => LayoutProgressPanel();
		lblProgressCurrentAction.FontChanged += ( _, _ ) => LayoutProgressPanel();
		DpiChanged += ( _, _ ) => BeginInvoke( new Action( () =>
		{
			RefreshModernLayoutMetrics();
			ApplyThemeToConflictSearchResults();
			LayoutProgressPanel();
		} ) );
		lblProgressCurrentPhase.UseMnemonic = false;
		lblProgressCurrentAction.UseMnemonic = false;
		Resize += ( _, _ ) =>
		{
			if ( WindowState != FormWindowState.Minimized )
				lastRestorableWindowState = WindowState;

			if ( pnlProgress.Visible )
				LayoutFullScreenProgressPanel();
		};
		responsiveLayoutTimer = new System.Windows.Forms.Timer( components )
		{
			Interval = 33
		};
		responsiveLayoutTimer.Tick += ( _, _ ) => FlushResponsiveWorkspaceLayout();
		workspaceSplitterTimer = new System.Windows.Forms.Timer( components )
		{
			Interval = 16
		};
		workspaceSplitterTimer.Tick += ( _, _ ) => FlushWorkspaceSplit();
		tlpMain.SizeChanged += ( _, _ ) => ScheduleResponsiveWorkspaceLayout();
		progressBar.Height = 14;
		progressBar.Style = ProgressBarStyle.Continuous;
		lblProgressCurrentPhase.Font = new Font( "Segoe UI", 18f, FontStyle.Regular, GraphicsUnit.Point );
		lblProgressCurrentAction.Font = new Font( "Segoe UI", 11.5f, FontStyle.Regular, GraphicsUnit.Point );
		lblBundleScanInfo.Text = string.Empty;
		lblBundleScanInfo.Visible = false;
		SetDoubleBuffered( tlpMain );
		SetDoubleBuffered( tlpRoot );
		SetDoubleBuffered( pnlProgress );
		LayoutProgressPanel();
	}

	private void InitializeScanDiagnosticsButton()
	{
		btnScanDiagnostics = LiveText.Live( new ModernButton
		{
			Anchor = AnchorStyles.Right,
			Font = new Font( "Segoe UI", 10f ),
			Name = "btnScanDiagnostics",
			TabStop = true,
			Text = UiText.T( "MainForm", "Diagnostics" ),
			Visible = false
		}, control => { LiveText.BindText( control, () => UiText.T( "MainForm", "Diagnostics" ) ); } );
		btnScanDiagnostics.Click += ( _, _ ) => ShowScanIssuesReport();
		tlpConflictsHeader.Controls.Remove( lblConflicts );
		tlpConflictTitle = new TableLayoutPanel
		{
			ColumnCount = 2,
			Dock = DockStyle.Fill,
			Margin = Padding.Empty,
			Name = "tlpConflictTitle",
			RowCount = 1
		};
		tlpConflictTitle.ColumnStyles.Add( new ColumnStyle( SizeType.Percent, 100f ) );
		tlpConflictTitle.ColumnStyles.Add( new ColumnStyle( SizeType.AutoSize ) );
		tlpConflictTitle.RowStyles.Add( new RowStyle( SizeType.Percent, 100f ) );
		tlpConflictTitle.Controls.Add( lblConflicts, 0, 0 );
		tlpConflictTitle.Controls.Add( btnScanDiagnostics, 1, 0 );
		tlpConflictsHeader.Controls.Add( tlpConflictTitle, 1, 0 );
	}

	private void UpdateScanDiagnosticsButton()
	{
		if ( btnScanDiagnostics == null )
			return;

		int count = !scanFailed && !conflictRefreshPending && session.Scan?.ScanCompleted == true
			? GetDiagnosticIssues().Count : 0;
		bool visible = count > 0;
		string label = UiText.F( "MainForm", $"Diagnostics ({count:N0})" );
		bool layoutChanged = btnScanDiagnostics.Visible != visible || btnScanDiagnostics.Text != label;

		btnScanDiagnostics.Text = label;

		if ( layoutChanged )
		{
			btnScanDiagnostics.MinimumSize = Size.Empty;

			int textWidth = TextRenderer.MeasureText( label, btnScanDiagnostics.Font ).Width;

			ConfigureHeaderButton( btnScanDiagnostics,
				Math.Max( LogicalToDeviceUnits( 106 ), textWidth + LogicalToDeviceUnits( 24 ) ), LogicalToDeviceUnits( 36 ) );
		}

		btnScanDiagnostics.Visible = visible;

		if ( layoutChanged )
			ScheduleResponsiveWorkspaceLayout();
	}

	// Combine resize events into one layout update.
	private void ScheduleResponsiveWorkspaceLayout()
	{
		responsiveLayoutPending = true;

		if ( !IsHandleCreated || !Visible || responsiveLayoutTimer == null )
		{
			FlushResponsiveWorkspaceLayout();

			return;
		}

		if ( !responsiveLayoutTimer.Enabled )
			responsiveLayoutTimer.Start();
	}

	private void FlushResponsiveWorkspaceLayout()
	{
		responsiveLayoutTimer?.Stop();

		if ( !responsiveLayoutPending )
			return;

		responsiveLayoutPending = false;
		UpdateResponsiveWorkspaceLayout();
	}

	private void InitializeWorkspaceSplitter()
	{
		pnlWorkspaceSplitter = LiveText.Live( new WorkspaceSplitterGrip
		{
			AccessibleDescription = UiText.T( "MainForm", "Drag to resize the Conflicts and Merges panels." ),
			AccessibleName = UiText.T( "MainForm", "Resize Conflicts and Merges panels" ),
			AccessibleRole = AccessibleRole.Grip,
			Cursor = Cursors.VSplit,
			Dock = DockStyle.Fill,
			Margin = Padding.Empty,
			Name = "pnlWorkspaceSplitter",
			TabStop = false
		}, control => { LiveText.BindAccessibleDescription( control, () => UiText.T( "MainForm", "Drag to resize the Conflicts and Merges panels." ) ); LiveText.BindAccessibleName( control, () => UiText.T( "MainForm", "Resize Conflicts and Merges panels" ) ); } );
		pnlWorkspaceSplitter.MouseDown += WorkspaceSplitter_MouseDown;
		pnlWorkspaceSplitter.MouseMove += WorkspaceSplitter_MouseMove;
		pnlWorkspaceSplitter.MouseUp += WorkspaceSplitter_MouseUp;
		pnlWorkspaceSplitter.MouseCaptureChanged += WorkspaceSplitter_CaptureChanged;
		tlpMain.Controls.Add( pnlWorkspaceSplitter, 3, 0 );
		tlpMain.SetRowSpan( pnlWorkspaceSplitter, 3 );
	}

	private void WorkspaceSplitter_MouseDown( object sender, MouseEventArgs e )
	{
		if ( e.Button != MouseButtons.Left )
			return;

		workspaceSplitterDragging = true;
		lastWorkspaceSplitLeftWidth = -1;
		pnlWorkspaceSplitter.IsDragging = true;
		pnlWorkspaceSplitter.Capture = true;
		UpdateWorkspaceSplit( tlpMain.PointToClient( pnlWorkspaceSplitter.PointToScreen( e.Location ) ).X );
	}

	private void WorkspaceSplitter_MouseMove( object sender, MouseEventArgs e )
	{
		if ( !workspaceSplitterDragging || ( Control.MouseButtons & MouseButtons.Left ) == 0 )
			return;

		QueueWorkspaceSplit( tlpMain.PointToClient( pnlWorkspaceSplitter.PointToScreen( e.Location ) ).X );
	}

	private void WorkspaceSplitter_MouseUp( object sender, MouseEventArgs e )
	{
		if ( e.Button == MouseButtons.Left )
			EndWorkspaceSplitterDrag();
	}

	private void WorkspaceSplitter_CaptureChanged( object sender, EventArgs e )
	{
		if ( !pnlWorkspaceSplitter.Capture )
			EndWorkspaceSplitterDrag();
	}

	// Mouse-up and lost-capture can both end the same drag. Apply its final position only once.
	private void EndWorkspaceSplitterDrag()
	{
		bool wasDragging = workspaceSplitterDragging;

		workspaceSplitterDragging = false;
		pnlWorkspaceSplitter.IsDragging = false;

		if ( pnlWorkspaceSplitter.Capture )
			pnlWorkspaceSplitter.Capture = false;

		if ( wasDragging )
		{
			FlushWorkspaceSplit();
			lastWorkspaceSplitLeftWidth = -1;
			responsiveLayoutPending = true;
			FlushResponsiveWorkspaceLayout();
			tlpRoot?.Invalidate( invalidateChildren: true );
		}
	}

	// Each split repaints all three trees. Timer messages arrive after painting, so each repaint finishes before the next split.
	private void QueueWorkspaceSplit( int dividerCenterX )
	{
		pendingWorkspaceSplitX = dividerCenterX;
		workspaceSplitPending = true;

		if ( !workspaceSplitterTimer.Enabled )
			workspaceSplitterTimer.Start();
	}

	private void FlushWorkspaceSplit()
	{
		workspaceSplitterTimer?.Stop();

		if ( !workspaceSplitPending )
			return;

		workspaceSplitPending = false;
		UpdateWorkspaceSplit( pendingWorkspaceSplitX );
	}

	private void UpdateWorkspaceSplit( int dividerCenterX )
	{
		int[] columns = tlpMain.GetColumnWidths();

		if ( columns.Length < 7 )
			return;

		int Scale( int logicalPixels ) => Math.Max( 1, (int)Math.Round( logicalPixels * DeviceDpi / 96d ) );

		int availableWidth = tlpMain.ClientSize.Width - columns[0] - columns[3] - columns[6];

		if ( availableWidth <= 0 )
			return;

		int minimumPaneWidth = Math.Min( Scale( 360 ), Math.Max( Scale( 220 ), availableWidth / 4 ) );

		minimumPaneWidth = Math.Min( minimumPaneWidth, availableWidth / 2 );

		int requestedLeftWidth = dividerCenterX - columns[0] - columns[3] / 2;
		int leftWidth = Math.Clamp( requestedLeftWidth, minimumPaneWidth, availableWidth - minimumPaneWidth );

		if ( lastWorkspaceSplitLeftWidth >= 0 && Math.Abs( leftWidth - lastWorkspaceSplitLeftWidth ) < Scale( 2 ) )
			return;

		lastWorkspaceSplitLeftWidth = leftWidth;

		float leftPercent = 100f * leftWidth / availableWidth;
		float rightPercent = 100f - leftPercent;

		tlpMain.SuspendLayout();
		tlpMain.ColumnStyles[1].Width = leftPercent / 2f;
		tlpMain.ColumnStyles[2].Width = leftPercent / 2f;
		tlpMain.ColumnStyles[4].Width = rightPercent / 2f;
		tlpMain.ColumnStyles[5].Width = rightPercent / 2f;
		tlpMain.ResumeLayout( performLayout: true );
		AlignSummaryWithPanes();

		if ( !workspaceSplitterDragging )
			ScheduleResponsiveWorkspaceLayout();

		// Wrapped rows require a full repaint after divider movement.
		treConflicts?.Invalidate();
		treMerges?.Invalidate();
		treConflictSearchResults?.Invalidate();
		tlpMain.Invalidate();
	}

	internal void RefreshModernLayoutMetrics()
	{
		int Scale( int logicalPixels ) => Math.Max( 1, (int)Math.Round( logicalPixels * DeviceDpi / 96d ) );

		ConfigureHeaderButton( btnMainMenu, Scale( 78 ), Scale( 36 ) );
		ConfigureHeaderButton( btnRefreshConflicts, Scale( 88 ), Scale( 36 ) );
		ConfigureHeaderButton( btnRefreshMerged, Scale( 88 ), Scale( 36 ) );
		ConfigureHeaderButton( btnScanDiagnostics, Scale( 106 ), Scale( 36 ) );

		btnMainMenu.Margin = new Padding( Scale( 10 ), 0, Scale( 4 ), 0 );
		btnRefreshConflicts.Margin = new Padding( Scale( 4 ), 0, Scale( 10 ), 0 );
		btnRefreshMerged.Margin = new Padding( Scale( 4 ), 0, Scale( 10 ), 0 );
		btnScanDiagnostics.Margin = new Padding( Scale( 4 ), 0, Scale( 4 ), 0 );

		int headerRowHeight = Math.Max( Scale( 52 ), Math.Max( btnMainMenu.Height, btnRefreshConflicts.Height ) + Scale( 10 ) );
		int searchRowHeight = Scale( 56 );

		tlpConflictsHeader.RowStyles[0].Height = headerRowHeight;
		tlpMergesHeader.RowStyles[0].Height = headerRowHeight;
		tlpMain.RowStyles[0].Height = headerRowHeight + searchRowHeight;
		tlpMain.RowStyles[2].Height = Math.Max( Scale( 70 ), btnCreateAllScriptMerges.MinimumSize.Height + Scale( 22 ) );

		tlpMain.ColumnStyles[0].Width = Scale( 6 );
		tlpMain.ColumnStyles[3].Width = Scale( 10 );
		tlpMain.ColumnStyles[6].Width = Scale( 6 );
		tlpConflictsHeader.ColumnStyles[0].Width = btnMainMenu.Width + btnMainMenu.Margin.Horizontal + Scale( 4 );

		int refreshColumnWidth = Math.Max( btnRefreshConflicts.Width + btnRefreshConflicts.Margin.Horizontal,
			btnRefreshMerged.Width + btnRefreshMerged.Margin.Horizontal ) + Scale( 4 );

		tlpConflictsHeader.ColumnStyles[2].Width = refreshColumnWidth;
		tlpMergesHeader.ColumnStyles[2].Width = refreshColumnWidth;

		tlpConflictsHeader.Margin = new Padding( Scale( 4 ), Scale( 4 ), Scale( 4 ), 0 );
		tlpMergesHeader.Margin = new Padding( Scale( 4 ), Scale( 4 ), Scale( 4 ), 0 );
		tlpConflictSearch.Padding = new Padding( Scale( 12 ), Scale( 5 ), Scale( 12 ), Scale( 8 ) );
		pnlConflictModSearch.MinimumSize = new Size( Scale( 80 ), 0 );
		pnlConflictFileSearch.MinimumSize = new Size( Scale( 80 ), 0 );
		pnlConflictModSearch.Padding = new Padding( Scale( 34 ), Scale( 6 ), Scale( 12 ), Scale( 6 ) );
		pnlConflictFileSearch.Padding = new Padding( Scale( 34 ), Scale( 6 ), Scale( 12 ), Scale( 6 ) );
		pnlConflictModSearch.Margin = searchLayoutStacked
			? new Padding( 0, Scale( 1 ), 0, Scale( 4 ) )
			: new Padding( 0, Scale( 1 ), Scale( 5 ), Scale( 1 ) );
		pnlConflictFileSearch.Margin = searchLayoutStacked
			? new Padding( 0, Scale( 4 ), 0, Scale( 1 ) )
			: new Padding( Scale( 5 ), Scale( 1 ), 0, Scale( 1 ) );
		tlpMergeSearch.Padding = tlpConflictSearch.Padding;
		pnlMergeModSearch.MinimumSize = pnlConflictModSearch.MinimumSize;
		pnlMergeFileSearch.MinimumSize = pnlConflictFileSearch.MinimumSize;
		pnlMergeModSearch.Padding = pnlConflictModSearch.Padding;
		pnlMergeFileSearch.Padding = pnlConflictFileSearch.Padding;
		pnlMergeModSearch.Margin = pnlConflictModSearch.Margin;
		pnlMergeFileSearch.Margin = pnlConflictFileSearch.Margin;
		lblConflicts.Margin = new Padding( Scale( 12 ), 0, Scale( 4 ), 0 );
		lblMerges.Margin = new Padding( Scale( 12 ), 0, Scale( 4 ), 0 );

		tlpConflictActions.Margin = new Padding( Scale( 4 ), 0, Scale( 4 ), Scale( 5 ) );
		tlpMergeActions.Margin = new Padding( Scale( 4 ), 0, Scale( 4 ), Scale( 5 ) );
		tlpConflictActions.Padding = new Padding( Scale( 10 ), Scale( 9 ), Scale( 10 ), Scale( 9 ) );
		tlpMergeActions.Padding = new Padding( Scale( 10 ), Scale( 9 ), Scale( 10 ), Scale( 9 ) );

		foreach ( Button action in new[] { btnCreateMerges, btnCreateAllScriptMerges, btnDeleteMerges, btnDeleteAllMerges } )
		{
			action.Margin = actionLayoutStacked
				? new Padding( 0, Scale( 3 ), 0, Scale( 3 ) )
				: new Padding( Scale( 6 ), 0, Scale( 6 ), 0 );
		}

		UpdateResponsiveWorkspaceLayout();
		PerformLayout();
	}

	// Narrow panes stack their controls. Moving those controls can trigger layout again, so guard against nested calls.
	private void UpdateResponsiveWorkspaceLayout()
	{
		// Minimized windows report zero client space. Keep the visible pane measurements for restore.
		if ( WindowState == FormWindowState.Minimized || applyingResponsiveLayout
			|| tlpConflictActions == null || tlpMain.ClientSize.Width <= 0 )
		{
			return;
		}

		applyingResponsiveLayout = true;

		try
		{
			int Scale( int logicalPixels ) => Math.Max( 1, (int)Math.Round( logicalPixels * DeviceDpi / 96d ) );

			int[] columns = tlpMain.GetColumnWidths();
			int panelWidth = columns.Length >= 6
				? Math.Min( columns[1] + columns[2], columns[4] + columns[5] )
				: Math.Max( 1, ( tlpMain.ClientSize.Width - Scale( 22 ) ) / 2 );
			int requiredButtonCellWidth = new[]
			{
				btnCreateMerges.MinimumSize.Width + Scale(12),
				btnCreateAllScriptMerges.MinimumSize.Width + Scale(12),
				btnDeleteMerges.MinimumSize.Width + Scale(12),
				btnDeleteAllMerges.MinimumSize.Width + Scale(12)
			}.Max();
			int requiredWidePanelWidth = (int)Math.Ceiling( requiredButtonCellWidth / 0.4d ) + Scale( 20 );
			bool shouldStackActions = panelWidth < requiredWidePanelWidth;

			if ( actionLayoutStacked != shouldStackActions || tlpConflictActions.RowCount != ( shouldStackActions ? 2 : 1 ) )
			{
				actionLayoutStacked = shouldStackActions;
				ConfigureResponsiveActionBar( tlpConflictActions, btnCreateMerges, btnCreateAllScriptMerges, shouldStackActions, Scale );
				ConfigureResponsiveActionBar( tlpMergeActions, btnDeleteMerges, btnDeleteAllMerges, shouldStackActions, Scale );
			}

			tlpMain.RowStyles[2].Height = shouldStackActions
				? Scale( 116 )
				: Math.Max( Scale( 70 ), btnCreateAllScriptMerges.MinimumSize.Height + Scale( 22 ) );

			string modSearchText = UiText.T( "MainForm", "Search mod names..." );
			string fileSearchText = UiText.T( "MainForm", "Search files (e.g. *.swf)..." );
			Size modSearchTextSize = TextRenderer.MeasureText( modSearchText, txtConflictModSearch.Font, Size.Empty,
				TextFormatFlags.NoPadding | TextFormatFlags.SingleLine );
			Size fileSearchTextSize = TextRenderer.MeasureText( fileSearchText, txtConflictFileSearch.Font, Size.Empty,
				TextFormatFlags.NoPadding | TextFormatFlags.SingleLine );
			int requiredSideBySideSearchWidth = modSearchTextSize.Width + fileSearchTextSize.Width
				+ pnlConflictModSearch.Padding.Horizontal + pnlConflictFileSearch.Padding.Horizontal
				+ tlpConflictSearch.Padding.Horizontal + Scale( 18 );
			bool shouldStackSearch = panelWidth < requiredSideBySideSearchWidth;

			if ( searchLayoutStacked != shouldStackSearch || tlpConflictSearch.RowCount != ( shouldStackSearch ? 2 : 1 ) )
			{
				searchLayoutStacked = shouldStackSearch;
				ConfigureResponsiveSearchLayout( shouldStackSearch, Scale );
			}

			int stackedSearchWidth = panelWidth - tlpConflictSearch.Padding.Horizontal
				- Math.Max( pnlConflictModSearch.Margin.Horizontal, pnlConflictFileSearch.Margin.Horizontal )
				- pnlConflictFileSearch.Padding.Horizontal;
			bool compactSearchText = shouldStackSearch && stackedSearchWidth < fileSearchTextSize.Width + Scale( 8 );

			LiveText.BindPlaceholderText(
				txtConflictModSearch,
				() => compactSearchText ? UiText.T( "MainForm", "Search mods..." ) : modSearchText );
			LiveText.BindPlaceholderText(
				txtConflictFileSearch,
				() => compactSearchText ? UiText.T( "MainForm", "Search files..." ) : fileSearchText );
			txtMergeModSearch.PlaceholderText = txtConflictModSearch.PlaceholderText;
			txtMergeFileSearch.PlaceholderText = txtConflictFileSearch.PlaceholderText;

			int searchRowHeight = shouldStackSearch ? Scale( 100 ) : Scale( 56 );
			int headerRowHeight = LayoutSectionHeaders( panelWidth, searchRowHeight );

			tlpConflictSearch.Height = searchRowHeight;
			tlpMergeSearch.Height = searchRowHeight;
			tlpMain.RowStyles[0].Height = headerRowHeight + searchRowHeight + tlpConflictsHeader.Margin.Vertical;
			ApplyResponsiveStatusText();
			AlignSummaryWithPanes();
		}
		finally
		{
			applyingResponsiveLayout = false;
		}
	}

	private int LayoutSectionHeaders( int panelWidth, int searchHeight )
	{
		int scale( int value ) => LogicalToDeviceUnits( value );

		int titleWidth = TextRenderer.MeasureText( lblConflicts.Text, lblConflicts.Font ).Width + PaneCountWidth( lblConflicts );
		int diagnosticsWidth = btnScanDiagnostics.Visible ? btnScanDiagnostics.Width + btnScanDiagnostics.Margin.Horizontal : 0;
		bool compact = panelWidth < btnMainMenu.Width + btnRefreshConflicts.Width + titleWidth + diagnosticsWidth + scale( 70 );
		int buttonHeight = Math.Max( scale( 52 ), Math.Max( btnMainMenu.Height, btnRefreshConflicts.Height ) + scale( 10 ) );
		bool stackDiagnostics = compact && btnScanDiagnostics.Visible
			&& titleWidth + diagnosticsWidth + lblConflicts.Margin.Horizontal > panelWidth - scale( 30 );
		int titleTextHeight = TextRenderer.MeasureText( lblConflicts.Text, lblConflicts.Font,
			new Size( Math.Max( 1, panelWidth - scale( 40 ) ), int.MaxValue ), TextFormatFlags.WordBreak ).Height + scale( 8 );
		int titleHeight = compact ? stackDiagnostics
			? titleTextHeight + btnScanDiagnostics.Height + scale( 4 )
			: Math.Max( titleTextHeight, btnScanDiagnostics.Visible ? btnScanDiagnostics.Height + scale( 8 ) : 0 ) : 0;

		if ( tlpConflictTitle.RowCount != ( stackDiagnostics ? 2 : 1 ) )
		{
			tlpConflictTitle.SuspendLayout();
			tlpConflictTitle.ColumnCount = stackDiagnostics ? 1 : 2;
			tlpConflictTitle.RowCount = stackDiagnostics ? 2 : 1;
			tlpConflictTitle.ColumnStyles.Clear();
			tlpConflictTitle.RowStyles.Clear();
			tlpConflictTitle.ColumnStyles.Add( new ColumnStyle( SizeType.Percent, 100f ) );

			if ( !stackDiagnostics )
				tlpConflictTitle.ColumnStyles.Add( new ColumnStyle( SizeType.AutoSize ) );

			tlpConflictTitle.RowStyles.Add( new RowStyle( stackDiagnostics ? SizeType.Absolute : SizeType.Percent,
				stackDiagnostics ? titleTextHeight : 100f ) );

			if ( stackDiagnostics )
				tlpConflictTitle.RowStyles.Add( new RowStyle( SizeType.Percent, 100f ) );

			tlpConflictTitle.SetCellPosition( lblConflicts, new TableLayoutPanelCellPosition( 0, 0 ) );
			tlpConflictTitle.SetCellPosition( btnScanDiagnostics,
				new TableLayoutPanelCellPosition( stackDiagnostics ? 0 : 1, stackDiagnostics ? 1 : 0 ) );
			tlpConflictTitle.ResumeLayout( true );
		}

		void Arrange( TableLayoutPanel header, Control title, Button refresh, Control search, bool conflicts )
		{
			int rows = compact ? 3 : 2;

			if ( header.RowCount == rows && header.RowStyles.Count == rows
				&& header.RowStyles[0].Height == ( compact ? titleHeight : buttonHeight )
				&& header.RowStyles[rows - 1].Height == searchHeight
				&& header.GetRow( search ) == rows - 1 )
				return;

			header.SuspendLayout();
			header.RowCount = compact ? 3 : 2;
			header.RowStyles.Clear();

			if ( compact )
				header.RowStyles.Add( new RowStyle( SizeType.Absolute, titleHeight ) );

			header.RowStyles.Add( new RowStyle( SizeType.Absolute, buttonHeight ) );
			header.RowStyles.Add( new RowStyle( SizeType.Absolute, searchHeight ) );
			header.SetCellPosition( title, new TableLayoutPanelCellPosition( compact || !conflicts ? 0 : 1, 0 ) );
			header.SetColumnSpan( title, compact ? 3 : conflicts ? 1 : 2 );
			header.SetCellPosition( refresh, new TableLayoutPanelCellPosition( 2, compact ? 1 : 0 ) );

			if ( conflicts )
				header.SetCellPosition( btnMainMenu, new TableLayoutPanelCellPosition( 0, compact ? 1 : 0 ) );

			header.SetCellPosition( search, new TableLayoutPanelCellPosition( 0, compact ? 2 : 1 ) );
			header.ResumeLayout( true );
		}

		Arrange( tlpConflictsHeader, tlpConflictTitle, btnRefreshConflicts, tlpConflictSearch, true );
		Arrange( tlpMergesHeader, lblMerges, btnRefreshMerged, tlpMergeSearch, false );

		return buttonHeight + titleHeight;
	}

	private void ConfigureResponsiveSearchLayout( bool stacked, Func<int, int> scale )
	{
		ConfigureResponsiveSearchLayout( tlpConflictSearch, pnlConflictModSearch, pnlConflictFileSearch, stacked, scale );
		ConfigureResponsiveSearchLayout( tlpMergeSearch, pnlMergeModSearch, pnlMergeFileSearch, stacked, scale );
	}

	private static void ConfigureResponsiveSearchLayout( TableLayoutPanel tlpConflictSearch,
		ModernTextBoxHost pnlConflictModSearch, ModernTextBoxHost pnlConflictFileSearch,
		bool stacked, Func<int, int> scale )
	{
		tlpConflictSearch.SuspendLayout();
		tlpConflictSearch.ColumnStyles.Clear();
		tlpConflictSearch.RowStyles.Clear();

		if ( stacked )
		{
			tlpConflictSearch.ColumnCount = 1;
			tlpConflictSearch.RowCount = 2;
			tlpConflictSearch.ColumnStyles.Add( new ColumnStyle( SizeType.Percent, 100f ) );
			tlpConflictSearch.RowStyles.Add( new RowStyle( SizeType.Percent, 50f ) );
			tlpConflictSearch.RowStyles.Add( new RowStyle( SizeType.Percent, 50f ) );
			pnlConflictModSearch.Margin = new Padding( 0, scale( 1 ), 0, scale( 4 ) );
			pnlConflictFileSearch.Margin = new Padding( 0, scale( 4 ), 0, scale( 1 ) );
			tlpConflictSearch.SetCellPosition( pnlConflictModSearch, new TableLayoutPanelCellPosition( 0, 0 ) );
			tlpConflictSearch.SetCellPosition( pnlConflictFileSearch, new TableLayoutPanelCellPosition( 0, 1 ) );
		}
		else
		{
			tlpConflictSearch.ColumnCount = 2;
			tlpConflictSearch.RowCount = 1;
			tlpConflictSearch.ColumnStyles.Add( new ColumnStyle( SizeType.Percent, 50f ) );
			tlpConflictSearch.ColumnStyles.Add( new ColumnStyle( SizeType.Percent, 50f ) );
			tlpConflictSearch.RowStyles.Add( new RowStyle( SizeType.Percent, 100f ) );
			pnlConflictModSearch.Margin = new Padding( 0, scale( 1 ), scale( 5 ), scale( 1 ) );
			pnlConflictFileSearch.Margin = new Padding( scale( 5 ), scale( 1 ), 0, scale( 1 ) );
			tlpConflictSearch.SetCellPosition( pnlConflictModSearch, new TableLayoutPanelCellPosition( 0, 0 ) );
			tlpConflictSearch.SetCellPosition( pnlConflictFileSearch, new TableLayoutPanelCellPosition( 1, 0 ) );
		}

		tlpConflictSearch.ResumeLayout( performLayout: true );
	}

	private static void ConfigureResponsiveActionBar(
		TableLayoutPanel actionBar,
		Button secondaryAction,
		Button primaryAction,
		bool stacked,
		Func<int, int> scale )
	{
		actionBar.SuspendLayout();
		actionBar.ColumnStyles.Clear();
		actionBar.RowStyles.Clear();

		if ( stacked )
		{
			actionBar.ColumnCount = 3;
			actionBar.RowCount = 2;
			actionBar.ColumnStyles.Add( new ColumnStyle( SizeType.Percent, 10f ) );
			actionBar.ColumnStyles.Add( new ColumnStyle( SizeType.Percent, 80f ) );
			actionBar.ColumnStyles.Add( new ColumnStyle( SizeType.Percent, 10f ) );
			actionBar.RowStyles.Add( new RowStyle( SizeType.Percent, 50f ) );
			actionBar.RowStyles.Add( new RowStyle( SizeType.Percent, 50f ) );
			secondaryAction.Margin = new Padding( 0, scale( 3 ), 0, scale( 3 ) );
			primaryAction.Margin = new Padding( 0, scale( 3 ), 0, scale( 3 ) );
			actionBar.SetCellPosition( secondaryAction, new TableLayoutPanelCellPosition( 1, 0 ) );
			actionBar.SetCellPosition( primaryAction, new TableLayoutPanelCellPosition( 1, 1 ) );
		}
		else
		{
			actionBar.ColumnCount = 4;
			actionBar.RowCount = 1;
			actionBar.ColumnStyles.Add( new ColumnStyle( SizeType.Percent, 10f ) );
			actionBar.ColumnStyles.Add( new ColumnStyle( SizeType.Percent, 40f ) );
			actionBar.ColumnStyles.Add( new ColumnStyle( SizeType.Percent, 40f ) );
			actionBar.ColumnStyles.Add( new ColumnStyle( SizeType.Percent, 10f ) );
			actionBar.RowStyles.Add( new RowStyle( SizeType.Percent, 100f ) );
			secondaryAction.Margin = new Padding( scale( 6 ), 0, scale( 6 ), 0 );
			primaryAction.Margin = new Padding( scale( 6 ), 0, scale( 6 ), 0 );
			actionBar.SetCellPosition( secondaryAction, new TableLayoutPanelCellPosition( 1, 0 ) );
			actionBar.SetCellPosition( primaryAction, new TableLayoutPanelCellPosition( 2, 0 ) );
		}

		actionBar.ResumeLayout( performLayout: true );
	}

	private static void ConfigureHeaderButton( Button button, int minimumWidth, int minimumHeight )
	{
		button.AutoSize = false;
		button.Padding = Padding.Empty;

		Size preferredSize = button.GetPreferredSize( Size.Empty );

		button.MinimumSize = new Size( Math.Max( minimumWidth, preferredSize.Width ), Math.Max( minimumHeight, preferredSize.Height ) );
		button.Size = button.MinimumSize;
	}

	private static void ConfigureSectionHeading( Label heading )
	{
		heading.Dock = DockStyle.Fill;
		heading.AutoEllipsis = false;
		heading.AutoSize = false;
		heading.Font = new Font( "Segoe UI", 13f, FontStyle.Bold, GraphicsUnit.Point );
		heading.Margin = new Padding( 12, 0, 4, 0 );
		heading.Size = new Size( 190, 34 );
		heading.TextAlign = ContentAlignment.MiddleLeft;
	}

	// Count the displayed file and group rows so the header follows the current search.
	private int DisplayedRowCount( Label heading )
	{
		TreeView tree = treConflicts;

		if ( ReferenceEquals( heading, lblMerges ) )
			tree = treMerges;
		else if ( IsConflictSearchActive )
			tree = treConflictSearchResults;

		return tree.Nodes.Cast<TreeNode>()
			.Where( category => category.Tag is ModFileCategory )
			.Sum( category => category.Nodes.Count );
	}

	private string PaneCountText( Label heading )
	{
		int count = DisplayedRowCount( heading );

		return count > 0 ? count.ToString( "N0" ) : string.Empty;
	}

	private int PaneCountWidth( Label heading )
	{
		string text = PaneCountText( heading );

		return text.Length == 0 ? 0
			: TextRenderer.MeasureText( text, paneCountFont, Size.Empty, TextFormatFlags.NoPadding ).Width + LogicalToDeviceUnits( 22 );
	}

	private void PaintPaneCount( object sender, PaintEventArgs e )
	{
		Label heading = (Label)sender;
		string text = PaneCountText( heading );

		if ( text.Length == 0 )
			return;

		Size textSize = TextRenderer.MeasureText( text, paneCountFont, Size.Empty, TextFormatFlags.NoPadding );
		int titleWidth = TextRenderer.MeasureText( heading.Text, heading.Font ).Width;
		int height = textSize.Height + LogicalToDeviceUnits( 4 );
		Rectangle pill = new Rectangle( titleWidth + LogicalToDeviceUnits( 6 ), ( heading.Height - height ) / 2,
			textSize.Width + LogicalToDeviceUnits( 14 ), height );

		if ( pill.Right >= heading.Width || pill.Top < 0 )
			return;

		e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

		using GraphicsPath path = ThemeUtils.RoundedPath( pill, height / 2 );
		Theme theme = ThemeManager.CurrentTheme;
		using SolidBrush surface = new SolidBrush( theme.StandardButtonHoverColor );

		e.Graphics.FillPath( surface, path );
		TextRenderer.DrawText( e.Graphics, text, paneCountFont, pill, theme.ForeDimmedColor, theme.StandardButtonHoverColor,
			TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding );
	}

	private void RefreshPaneCounts()
	{
		lblConflicts.Invalidate();
		lblMerges.Invalidate();
	}

	private void InitializeRootLayout()
	{
		tlpRoot = new CompositedWorkspacePanel
		{
			BackColor = ThemeManager.CurrentTheme.BackColor,
			ColumnCount = 1,
			Dock = DockStyle.Fill,
			Margin = new Padding( 0 ),
			Name = "tlpRoot",
			Padding = new Padding( 0 ),
			RowCount = 1
		};
		tlpRoot.ColumnStyles.Add( new ColumnStyle( SizeType.Percent, 100f ) );
		tlpRoot.RowStyles.Add( new RowStyle( SizeType.Percent, 100f ) );
		Controls.Remove( tlpMain );
		Controls.Remove( pnlProgress );
		Controls.Remove( statusStrip );
		statusStrip.Visible = false;
		tlpRoot.Controls.Add( tlpMain, 0, 0 );
		scanSummary = new ScanSummaryBar();
		tlpRoot.RowCount = 2;
		tlpRoot.RowStyles.Add( new RowStyle( SizeType.AutoSize ) );
		tlpRoot.Controls.Add( scanSummary, 0, 1 );
		Controls.Add( tlpRoot );
		Controls.Add( pnlProgress );
		pnlProgress.Dock = DockStyle.None;
		pnlProgress.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
		pnlProgress.Margin = Padding.Empty;
		tlpMain.BringToFront();
		pnlProgress.BringToFront();
		LayoutFullScreenProgressPanel();
	}

	// Paint a view change in one pass to reduce flicker when swapping trees.
	private sealed class CompositedWorkspacePanel : TableLayoutPanel
	{
		private bool presenting;

		internal void Present( Action reveal )
		{
			presenting = true;
			UpdateStyles();

			try
			{
				reveal();
				Refresh();
			}
			finally
			{
				presenting = false;
				UpdateStyles();
			}
		}

		protected override CreateParams CreateParams
		{
			get
			{
				var parameters = base.CreateParams;

				if ( presenting )
					parameters.ExStyle |= 0x02000000; // WS_EX_COMPOSITED

				return parameters;
			}
		}
	}

	private static TableLayoutPanel CreateActionBar( string name, Button secondaryAction, Button primaryAction )
	{
		TableLayoutPanel actionBar = new TableLayoutPanel()
		{
			ColumnCount = 4,
			Dock = DockStyle.Fill,
			Margin = new Padding( 4, 0, 4, 5 ),
			Name = name,
			Padding = new Padding( 10, 9, 10, 9 ),
			RowCount = 1
		};

		actionBar.ColumnStyles.Add( new ColumnStyle( SizeType.Percent, 10f ) );
		actionBar.ColumnStyles.Add( new ColumnStyle( SizeType.Percent, 40f ) );
		actionBar.ColumnStyles.Add( new ColumnStyle( SizeType.Percent, 40f ) );
		actionBar.ColumnStyles.Add( new ColumnStyle( SizeType.Percent, 10f ) );
		actionBar.RowStyles.Add( new RowStyle( SizeType.Percent, 100f ) );
		secondaryAction.Anchor = AnchorStyles.None;
		secondaryAction.AutoSize = false;
		secondaryAction.Dock = DockStyle.Fill;
		secondaryAction.Margin = new Padding( 6, 0, 6, 0 );
		primaryAction.Anchor = AnchorStyles.None;
		primaryAction.AutoSize = false;
		primaryAction.Dock = DockStyle.Fill;
		primaryAction.Margin = new Padding( 6, 0, 6, 0 );
		actionBar.Controls.Add( secondaryAction, 1, 0 );
		actionBar.Controls.Add( primaryAction, 2, 0 );

		return actionBar;
	}

	private static void SetDoubleBuffered( Control control )
	{
		typeof( Control ).GetProperty(
			"DoubleBuffered",
			System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic )
			?.SetValue( control, true );
	}

	private bool TryGetPaneCards( out Rectangle leftCard, out Rectangle rightCard )
	{
		int[] columns = tlpMain.GetColumnWidths();

		leftCard = rightCard = Rectangle.Empty;

		if ( columns.Length < 7 )
		{
			return false;
		}

		int leftX = columns[0];
		int leftWidth = columns[1] + columns[2];
		int rightX = leftX + leftWidth + columns[3];
		int rightWidth = columns[4] + columns[5];
		int centerInset = Math.Max( 1, columns[3] / 3 );
		int leftOuterX = Math.Max( 1, leftX - Math.Max( 1, columns[0] / 2 ) );
		int leftInnerX = leftX + leftWidth + centerInset;
		int rightInnerX = rightX - centerInset;
		int rightOuterX = Math.Min( tlpMain.ClientSize.Width - 1,
			rightX + rightWidth + Math.Max( 1, columns[6] / 2 ) );
		int topY = Math.Max( 1, Math.Min( tlpConflictsHeader.Top, tlpMergesHeader.Top ) / 2 );
		int actionBottom = Math.Max( tlpConflictActions.Bottom, tlpMergeActions.Bottom );
		int availableBottomSpace = Math.Max( 1, tlpMain.ClientSize.Height - actionBottom );
		int bottomY = Math.Min( tlpMain.ClientSize.Height - 1,
			actionBottom + Math.Max( 1, availableBottomSpace / 2 ) );

		leftCard = Rectangle.FromLTRB( leftOuterX, topY, leftInnerX, bottomY );
		rightCard = Rectangle.FromLTRB( rightInnerX, topY, rightOuterX, bottomY );

		return true;
	}

	// The summary and workspace share a left edge, so the pane positions can be reused.
	private void AlignSummaryWithPanes()
	{
		if ( TryGetPaneCards( out Rectangle leftCard, out Rectangle rightCard ) )
			scanSummary?.SetPaneBounds( leftCard, rightCard );
	}

	private void PaintWorkspaceCards( object sender, PaintEventArgs e )
	{
		if ( !TryGetPaneCards( out Rectangle leftCard, out Rectangle rightCard ) )
		{
			return;
		}

		float dpiScale = Math.Max( 1f, DeviceDpi / 96f );
		int cornerRadius = Math.Max( 1, (int)Math.Round( 14f * dpiScale ) );
		using SolidBrush surface = new SolidBrush( ThemeManager.CurrentTheme.BackgroundPanelBackColor );
		using Pen border = new Pen( ThemeManager.CurrentTheme.CardBorderColor, dpiScale );

		e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

		using GraphicsPath leftPath = ThemeUtils.RoundedPath( leftCard, cornerRadius );
		using GraphicsPath rightPath = ThemeUtils.RoundedPath( rightCard, cornerRadius );

		e.Graphics.FillPath( surface, leftPath );
		e.Graphics.FillPath( surface, rightPath );
		e.Graphics.DrawPath( border, leftPath );
		e.Graphics.DrawPath( border, rightPath );
	}

	private void PaintProgressSurface( object sender, PaintEventArgs e )
	{
		Rectangle frame = new Rectangle(
			12,
			12,
			Math.Max( 1, pnlProgress.ClientSize.Width - 25 ),
			Math.Max( 1, pnlProgress.ClientSize.Height - 25 ) );
		using Pen border = new Pen( ThemeManager.CurrentTheme.BorderColor, 1f );

		e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
		e.Graphics.DrawRoundedRectangle( border, frame, 14 );
	}

	// Queue one layout update for repeated requests. Before the window handle exists, update directly.
	private void ScheduleProgressLayout()
	{
		if ( !IsHandleCreated )
		{
			LayoutProgressPanel();

			return;
		}

		if ( progressLayoutPending )
			return;

		progressLayoutPending = true;
		BeginInvoke( new Action( () =>
		{
			progressLayoutPending = false;
			LayoutProgressPanel();
		} ) );
	}

	private void LayoutProgressPanel()
	{
		if ( pnlProgress == null || progressBar == null )
		{
			return;
		}

		int gap = LogicalToDeviceUnits( 18 );
		int contentWidth = Math.Max(
			1,
			Math.Min( pnlProgress.ClientSize.Width - LogicalToDeviceUnits( 64 ), LogicalToDeviceUnits( 1200 ) ) );
		int left = Math.Max( 0, ( pnlProgress.ClientSize.Width - contentWidth ) / 2 );

		int TextHeight( Label label ) => Math.Max( label.Font.Height,
			TextRenderer.MeasureText( label.Text, label.Font, new Size( contentWidth, int.MaxValue ),
				TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl ).Height ) + LogicalToDeviceUnits( 8 );

		int phaseHeight = TextHeight( lblProgressCurrentPhase );
		int actionHeight = TextHeight( lblProgressCurrentAction );
		int barHeight = LogicalToDeviceUnits( 14 );
		int buttonHeight = Math.Max( LogicalToDeviceUnits( 52 ), Math.Max( btnDontExit.Font.Height, btnExitNow.Font.Height ) + gap );
		int totalHeight = phaseHeight + actionHeight + barHeight + buttonHeight + gap * 3;
		int phaseTop = Math.Max( gap, ( pnlProgress.ClientSize.Height - totalHeight ) / 2 );

		lblProgressCurrentPhase.SetBounds( left, phaseTop, contentWidth, phaseHeight );
		lblProgressCurrentPhase.TextAlign = ContentAlignment.MiddleCenter;
		progressBar.SetBounds( left, lblProgressCurrentPhase.Bottom + gap, contentWidth, barHeight );
		lblProgressCurrentAction.SetBounds( left, progressBar.Bottom + gap, contentWidth, actionHeight );
		lblProgressCurrentAction.TextAlign = ContentAlignment.TopCenter;

		int buttonWidth = Math.Min( LogicalToDeviceUnits( 190 ), Math.Max( 1, ( contentWidth - gap ) / 2 ) );
		int buttonsLeft = ( pnlProgress.ClientSize.Width - ( buttonWidth * 2 + gap ) ) / 2;
		int buttonTop = lblProgressCurrentAction.Bottom + gap;

		btnDontExit.SetBounds( buttonsLeft, buttonTop, buttonWidth, buttonHeight );
		btnExitNow.SetBounds( buttonsLeft + buttonWidth + gap, buttonTop, buttonWidth, buttonHeight );
	}

	private void LayoutFullScreenProgressPanel()
	{
		if ( pnlProgress?.Parent != this )
		{
			return;
		}

		pnlProgress.Bounds = ClientRectangle;
		LayoutProgressPanel();
	}
}
