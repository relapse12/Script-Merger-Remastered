#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using WitcherScriptMerger.Controls;
using WitcherScriptMerger.FileIndex;
using WitcherScriptMerger.Inspection;

namespace WitcherScriptMerger.Forms;

// Inspect opens a separate viewer using the current scan. It leaves merges and settings unchanged.
internal partial class MainForm
{
	private bool inspectorLaunchPending;

	internal string InspectorUnavailableReason
	{
		get
		{
			if ( pnlProgress.Visible || inspectorLaunchPending )
				return UiText.T( "InspectorLaunch", "Wait for the current operation to finish." );

			if ( string.IsNullOrWhiteSpace( session.GamePaths.GameDirectory ) )
				return UiText.T( "InspectorLaunch", "Select the game folder first." );

			if ( !File.Exists( InspectorLauncher.Executable( AppContext.BaseDirectory ) ) )
				return UiText.T( "InspectorLaunch",
					"Annotation Inspector is missing. Copy Tools/WSAnnoInspector from the matching runtime package." );

			return "";
		}
	}

	internal static bool CanInspect( TreeNode? node )
	{
		if ( node is null || !ConflictSelectionTree.IsConflictFileNode( node ) )
			return false;

		var category = node.GetMetadata()?.ModFile?.Category ?? node.Parent?.Tag as ModFileCategory;

		return category == Categories.Script || category == Categories.PrecompiledScript;
	}

	internal async Task InspectAsync( TreeNode? node = null )
	{
		string unavailable = InspectorUnavailableReason;

		if ( unavailable.Length > 0 )
		{
			ShowMessage( unavailable );
			return;
		}

		if ( node is not null && !CanInspect( node ) )
			return;

		var game = session.GamePaths;
		string modIo = Program.Settings.Get( "ModIoDirectory" );

		if ( string.IsNullOrWhiteSpace( modIo ) )
			modIo = ModIoDiscovery.ResolveDefaultStorageRoot( Environment.GetEnvironmentVariable( "PUBLIC" ) );

		var folders = new InspectorFolders(
			Path.GetFullPath( game.GameDirectory! ), Path.GetFullPath( game.ModsDirectory ),
			Path.GetFullPath( game.ScriptsDirectory ), GameSettingsLocation.DirectoryPath( Program.Settings ),
			Path.GetFullPath( modIo ), Path.GetFullPath( session.InventoryStore.FilePath ),
			Path.Combine( AppContext.BaseDirectory, "Data", "Annotation Inspector" ), UiText.LanguageDirectory, null );
		InspectorSelection? selection = null;

		if ( node is not null )
		{
			var file = node.GetMetadata()?.ModFile;
			bool compiled = ( file?.Category ?? node.Parent?.Tag ) == Categories.PrecompiledScript;

			selection = new InspectorSelection(
				compiled ? "PrecompiledScript" : "Script", compiled ? null : file?.RelativePath ?? node.Text,
				compiled ? file?.RelativePath ?? node.Text : null,
				node.GetTreeNodes().Select( source => source.GetMetadata()?.FileHash?.Name ?? source.Text ).ToArray() );
		}

		string language = UiText.LanguageCode;
		bool scanCurrent = session.Scan is not null && session.IsScanCurrent;
		inspectorLaunchPending = true;

		try
		{
			var context = await Task.Run( () => InspectorLaunchSnapshot.Capture( folders, language, scanCurrent, selection ) );
			InspectorLauncher.Launch( AppContext.BaseDirectory, context );
		}
		catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException
			or System.ComponentModel.Win32Exception or ArgumentException or InvalidOperationException )
		{
			ShowMessage( UiText.F( "InspectorLaunch", $"Could not open Annotation Inspector.\n\n{error.Message}" ) );
		}
		finally
		{
			inspectorLaunchPending = false;
		}
	}
}
