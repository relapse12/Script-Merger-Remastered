#nullable enable annotations
using System;
using System.IO;
using System.Windows.Forms;
using WitcherScriptMerger.Inventory;
using WitcherScriptMerger.Tools;

namespace WitcherScriptMerger;

// The engine uses this host to reach the application's settings, dialogs and tools.
// Program is read when needed because the engine can be created before the main window.
internal sealed class ApplicationEngineHost : IEngineHost
{
	public IEngineSettings Settings => Program.Settings;

	public string MergedBundleContentDirectory => Paths.MergedBundleContentAbsolute;

	public void ShowMessage( string message )
	{
		Program.MainFrm.ShowMessage( message );
	}

	public void ShowWarning( string message, string title )
	{
		Program.MainFrm.ShowMessage( message, title, MessageBoxButtons.OK, MessageBoxIcon.Exclamation );
	}

	public string? RetrieveMergedModName()
	{
		return Paths.RetrieveMergedModName();
	}

	public void LoadOrderFileChanged()
	{
		Program.MainFrm.treConflicts.BeginInvoke( (Action)delegate
		{
			Program.MainFrm.treConflicts.SetStylesForCustomLoadOrder();
			Program.MainFrm.RefreshConflictSearchResultsIfActive();
		} );
	}

	public int RunTextMerge(
		FileMerger.MergeSource first,
		FileMerger.MergeSource second,
		FileInfo? vanillaFile,
		string outputPath,
		bool followFileLinks )
	{
		return KDiff3.Run( first, second, vanillaFile, outputPath, followFileLinks );
	}

	public int PackBundle( string sourceDirectory, string outputDirectory )
	{
		return WccLite.PackBundle( sourceDirectory, outputDirectory );
	}

	public int GenerateMetadata( string bundleDirectory )
	{
		return WccLite.GenerateMetadata( bundleDirectory );
	}
}
