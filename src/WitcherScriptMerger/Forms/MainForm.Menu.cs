using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using WitcherScriptMerger.Inventory;
using WitcherScriptMerger.Theming;
using WitcherScriptMerger.Tools;

namespace WitcherScriptMerger.Forms;

internal partial class MainForm
{
	private static ToolStripMenuItem CreateMenuItem( string name, Func<string> text, Func<string> toolTip = null )
	{
		return LiveText.Live( new ToolStripMenuItem( text() ) { Name = name, ToolTipText = toolTip?.Invoke() }, control =>
		{
			if ( toolTip != null )
				LiveText.BindToolTipText( control, toolTip );

			LiveText.BindText( control, text );
		} );
	}

	private static void AddMenuItem(
		ContextMenuStrip menu,
		string name,
		Func<string> text,
		EventHandler click,
		Func<string> toolTip = null )
	{
		ToolStripMenuItem item = CreateMenuItem( name, text, toolTip );

		item.Click += click;
		menu.Items.Add( item );
	}

	public void GenerateConflictsCsv( TreeView treeView )
	{
		SaveFileDialog saveFileDialog = new SaveFileDialog
		{
			FileName = "Conflicts.csv",
			InitialDirectory = Environment.GetFolderPath( Environment.SpecialFolder.Personal ),
			Filter = UiText.T( "MainForm", "CSV files" ) + " (*.csv)|*.csv|" + UiText.T( "MainForm", "All files" ) + " (*.*)|*.*",
			DefaultExt = "csv",
			AddExtension = true
		};

		if ( saveFileDialog.ShowDialog() != DialogResult.OK )
		{
			return;
		}

		string filePath = saveFileDialog.FileName;
		StringBuilder csvContent = new StringBuilder();
		string sep = CultureInfo.CurrentCulture.TextInfo.ListSeparator;
		StringBuilder stringBuilder = csvContent;
		StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler( 38, 3, stringBuilder );

		handler.AppendLiteral( "\"Category\"" );
		handler.AppendFormatted( sep );
		handler.AppendLiteral( "\"FilePath\"" );
		handler.AppendFormatted( sep );
		handler.AppendLiteral( "\"ModName\"" );
		handler.AppendFormatted( sep );
		handler.AppendLiteral( "\"ModPrio\"" );
		stringBuilder.AppendLine( ref handler );

		foreach ( TreeNode rootNode in treeView.Nodes )
		{
			foreach ( TreeNode scriptNode in rootNode.Nodes )
			{
				foreach ( TreeNode modNode in scriptNode.Nodes )
				{
					string line = $"\"{rootNode.Text}\"{sep}\"{scriptNode.Text}\"{sep}\"{modNode.Text}\"{sep}\"{session.LoadOrder.GetPriorityByName( modNode.Text )}\"";

					csvContent.AppendLine( line );
				}
			}
		}

		File.WriteAllText( filePath, csvContent.ToString() );
	}

	public ContextMenuStrip CreateMainMenu()
	{
		ContextMenuStrip menu = new ContextMenuStrip
		{
			ShowImageMargin = false,
			Renderer = new MenuRenderer( ThemeManager.CurrentTheme, 12 )
		};

		menu.Opening += MainMenu_Opening;
		menu.Closing += MainMenu_Closing;
		menu.Items.Add( CreateOpenMenu() );
		AddMenuItem( menu, "mnInspectAll", () => UiText.T( "InspectorLaunch", "Inspect All" ),
			async ( _, _ ) => await InspectAsync() );
		AddMenuItem( menu, "mnOptions", () => UiText.T( "MainForm", "Options..." ), MnOptions_Click );
		AddMenuItem( menu, "mnDependencies", () => UiText.T( "MainForm", "Dependency Locations..." ), MnDependencies_Click );
		AddMenuItem( menu, "mnExportConflicts", () => UiText.T( "MainForm", "Export conflicts to CSV file" ), MnExportConflicts_Click );
		AddMenuItem(
			menu,
			"mnPermanentIgnoreRules",
			() => UiText.T( "MainForm", "Permanent Ignore Rules..." ),
			( _, _ ) => OpenPermanentIgnoreRules() );
		AddMenuItem(
			menu,
			"mnRestoreIgnoredConflicts",
			() => UiText.T( "MainForm", "Restore All Temporarily Ignored Conflicts" ),
			async ( _, _ ) => await RestoreTemporarilyIgnoredConflictsAsync() );
		AddMenuItem( menu, "mnClearMemoryCaches", () => UiText.T( "MainForm", "Clear memory caches..." ), MnClearMemoryCaches_Click );
		AddMenuItem( menu, "mnKeyboardShortcuts", () => UiText.T( "MainForm", "Keyboard Shortcuts..." ), MnKeyboardShortcuts_Click );
		menu.Items.Add( CreateApplicationUpdateMenuItem() );
		menu.Items.Add( new ToolStripSeparator() );
		AddMenuItem( menu, "mnExitAndPlayDX12", () => UiText.T( "MainForm", "Exit and launch Remastered" ), MnExitAndPlayDX12_Click );
		AddMenuItem( menu, "mnExit", () => UiText.T( "MainForm", "Exit" ), MnExit_Click );
		KeyboardShortcuts.UpdateMenu( menu.Items, Program.Settings );

		return menu;
	}

	private ToolStripMenuItem CreateOpenMenu()
	{
		ContextMenuStrip openMenu = new ContextMenuStrip
		{
			ShowImageMargin = false,
			Renderer = new MenuRenderer( ThemeManager.CurrentTheme, 12 )
		};
		ToolStripMenuItem mnOpen = CreateMenuItem( "mnOpen", () => UiText.T( "MainForm", "Open..." ) );
		ToolStripMenuItem compiledFiles = new ToolStripMenuItem { Name = "mnOpenCompiledMerge" };

		mnOpen.DropDownOpening += MnOpen_DropDownOpening;
		AddMenuItem(
			openMenu,
			"mnOpenLoadOrderFile",
			() => UiText.T( "MainForm", "Open Load Order File Directory" ),
			MnOpenLoadOrderFile_Click );
		AddMenuItem( openMenu, "mnOpenMergedModDir", () => UiText.T( "MainForm", "Open Merged Mod Directory" ), MnOpenMergedModDir_Click );
		AddMenuItem(
			openMenu,
			"mnOpenBundleContentDir",
			() => UiText.T( "MainForm", "Open Merged Bundle Content Directory" ),
			MnOpenBundleContentDir_Click );
		LiveText.BindText( compiledFiles, () => UiText.T( "MainForm", "Open Merged Compiled Scripts" ) );
		compiledFiles.Click += ( _, _ ) =>
		{
			foreach ( string path in ExistingCompiledMergeFiles( session.Inventory.Merges, session.GamePaths ) )
				Program.TryOpenFileLocation( path );
		};
		openMenu.Items.Add( compiledFiles );
		mnOpen.DropDown = openMenu;

		return mnOpen;
	}

	private void MnExportConflicts_Click( object sender, EventArgs e )
	{
		GenerateConflictsCsv( treConflicts );
	}

	private async void MnClearMemoryCaches_Click( object sender, EventArgs e )
	{
		if ( pnlProgress.Visible )
			return;

		DialogResult result = ShowMessage(
			UiText.T(
				"MainForm",
				"Clear saved bundle, script-symbol and merge-check caches? They will be rebuilt on the next scan. Your settings, merges and recovery files will be kept." ),
			UiText.T( "MainForm", "Clear memory caches" ),
			MessageBoxButtons.YesNo,
			MessageBoxIcon.Question );

		if ( result != DialogResult.Yes )
		{
			return;
		}

		try
		{
			InitializeProgressScreen( UiText.T( "MainForm", "Clearing caches" ) );
			await ClearScanCachesAsync( AppContext.BaseDirectory );
			ShowMessage(
				UiText.T( "MainForm", "Caches cleared. Refresh conflicts to rebuild them." ),
				UiText.T( "MainForm", "Clear memory caches" ),
				MessageBoxButtons.OK,
				MessageBoxIcon.Information );
		}
		catch ( Exception exception )
		{
			ShowError( UiText.Format( "MainForm", "Caches could not be cleared.\n\n{0}", exception.GetBaseException().Message ) );
		}
		finally { HideProgressScreen(); }
	}

	private void MainMenu_Closing( object sender, ToolStripDropDownClosingEventArgs e )
	{
		Point mousePosition = Control.MousePosition;

		if ( btnMainMenu.Bounds.Contains( btnMainMenu.PointToClient( mousePosition ) ) && e.CloseReason != ToolStripDropDownCloseReason.CloseCalled )
		{
			e.Cancel = true;
		}
	}

	private void BtnMainMenu_Click( object sender, EventArgs e )
	{
		Button btn = (Button)sender;

		if ( MainMenu.Visible )
		{
			MainMenu.Close( ToolStripDropDownCloseReason.CloseCalled );
		}
		else
		{
			MainMenu.Show( btn, new Point( 0, btn.Height + 2 ) );
		}
	}

	private void MainMenu_Opening( object sender, CancelEventArgs e )
	{
		MainMenu.Items["mnInspectAll"].Enabled = InspectorUnavailableReason.Length == 0;
		MainMenu.Items["mnInspectAll"].ToolTipText = InspectorUnavailableReason;
		MainMenu.Items["mnRestoreIgnoredConflicts"].Enabled = ignoredConflictStore?.Conflicts.Count > 0;
	}

	private void MnOpen_DropDownOpening( object sender, EventArgs e )
	{
		ToolStripMenuItem obj = (ToolStripMenuItem)MainMenu.Items["mnOpen"];

		( (ToolStripMenuItem)obj.DropDownItems["mnOpenLoadOrderFile"] ).Enabled = File.Exists( session.LoadOrder?.FilePath ?? "" );
		PopulateMergedDirectoryMenu( (ToolStripMenuItem)obj.DropDownItems["mnOpenMergedModDir"],
			ExistingMergedModDirectories( Paths.ModsDirectory, Paths.RetrieveMergedModName(), session.Inventory.Merges ), path => Program.TryOpenDirectory( path ) );
		( (ToolStripMenuItem)obj.DropDownItems["mnOpenBundleContentDir"] ).Enabled = Directory.Exists( Paths.MergedBundleContent );
		PopulateCompiledMergeMenu(
			(ToolStripMenuItem)obj.DropDownItems["mnOpenCompiledMerge"],
			ExistingCompiledMergeFiles( session.Inventory.Merges, session.GamePaths ),
			path => Program.TryOpenFileLocation( path ) );
	}

	internal static string[] ExistingCompiledMergeFiles(
		IEnumerable<Merge> merges,
		GamePaths game ) => merges.Where( merge => merge.IsBlobMerge
		&& merge.RelativePath.EndsWith( ".rsblob", StringComparison.OrdinalIgnoreCase ) ).Select( merge => merge.GetMergedFile( game ) )
		.Where( File.Exists ).Distinct( StringComparer.OrdinalIgnoreCase ).ToArray();

	internal static void PopulateCompiledMergeMenu( ToolStripMenuItem item, IReadOnlyList<string> paths, Action<string> open )
	{
		while ( item.DropDownItems.Count > 0 )
		{
			var child = item.DropDownItems[0];

			item.DropDownItems.RemoveAt( 0 );
			child.Dispose();
		}

		item.Enabled = paths.Count > 0;

		if ( paths.Count < 2 )
			return;

		foreach ( string path in paths )
		{
			var child = new ToolStripMenuItem( Path.GetFileName( Path.GetDirectoryName( Path.GetDirectoryName( path ) ) ) + " / " + Path.GetFileName( path ) );

			child.Click += ( _, _ ) => open( path );
			item.DropDownItems.Add( child );
		}
	}

	private void MnOpenLoadOrderFile_Click( object sender, EventArgs e )
	{
		Program.TryOpenFile( session.LoadOrder.FilePath );
	}

	private void MnOpenMergedModDir_Click( object sender, EventArgs e )
	{
		var directories = ExistingMergedModDirectories( Paths.ModsDirectory, Paths.RetrieveMergedModName(), session.Inventory.Merges );

		if ( directories.Length == 0 )
		{
			MessageBox.Show( UiText.Format( "MainForm", "The Merged Mods directory ({0}) does not exist. Did you create any merges yet?", Paths.RetrieveMergedModDir() ) );
		}
		else
		{
			foreach ( string directory in directories )
				Program.TryOpenDirectory( directory );
		}
	}

	internal static string[] ExistingMergedModDirectories( string modsDirectory, string configuredName, IEnumerable<Merge> merges ) =>
		new[] { configuredName }.Concat( merges.Select( merge => merge.MergedModName ) )
			.Where( name => !string.IsNullOrWhiteSpace( name ) && name.StartsWith( "mod", StringComparison.OrdinalIgnoreCase )
				&& name.IndexOfAny( Path.GetInvalidFileNameChars() ) < 0 && Path.GetFileName( name ) == name )
			.Distinct( StringComparer.OrdinalIgnoreCase ).Select( name => Path.Combine( modsDirectory, name ) ).Where( Directory.Exists ).ToArray();

	internal static void PopulateMergedDirectoryMenu( ToolStripMenuItem item, IReadOnlyList<string> directories, Action<string> open )
	{
		while ( item.DropDownItems.Count > 0 )
		{
			var child = item.DropDownItems[0];

			item.DropDownItems.RemoveAt( 0 );
			child.Dispose();
		}

		item.Enabled = directories.Count > 0;

		if ( directories.Count <= 1 )
			return;

		if ( item.DropDown is ToolStripDropDownMenu submenu )
			submenu.ShowImageMargin = false;

		if ( item.Owner != null )
			item.DropDown.Renderer = item.Owner.Renderer;

		foreach ( string directory in directories )
		{
			var child = new ToolStripMenuItem( Path.GetFileName( directory ) ) { Tag = directory };

			child.Click += ( _, _ ) => open( directory );
			item.DropDownItems.Add( child );
		}
	}

	private void MnOpenBundleContentDir_Click( object sender, EventArgs e )
	{
		Program.TryOpenDirectory( Paths.MergedBundleContent );
	}

	private void MnOptions_Click( object sender, EventArgs e )
	{
		// Transfer focus after the menu closes.
		BeginInvoke( new Action( () => ShowModal( Program.OptionsFrm ) ) );
	}

	private void MnDependencies_Click( object sender, EventArgs e )
	{
		using DependencyForm dependencyForm = new DependencyForm();

		ShowModal( dependencyForm );
	}

	private void MnKeyboardShortcuts_Click( object sender, EventArgs e )
	{
		BeginInvoke( new Action( () =>
		{
			using var dialog = new KeyboardShortcutsForm( Program.Settings );

			if ( ShowModal( dialog ) == DialogResult.OK )
				RefreshShortcutHints();
		} ) );
	}

	private void MnExitAndPlayDX12_Click( object sender, EventArgs e )
	{
		try
		{
			GameLauncher.Launch( Paths.GameExeDX12 );
			AutoExit();
		}
		catch ( Exception exception )
		{
			ShowError( UiText.Format( "MainForm", "Remastered could not be launched.\n\n{0}", exception.GetBaseException().Message ) );
		}
	}

	private void MnExit_Click( object sender, EventArgs e )
	{
		Close();
	}
}
