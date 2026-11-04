using System;
using System.IO;
using System.Windows.Forms;
using WitcherScriptMerger.Tools;

namespace WitcherScriptMerger;

internal static class Paths
{
	public const string TempBundleContent = "tempbundlecontent";

	public static string MergedBundleContent = GamePaths.MergedBundleContent;

	public static string MergedBundleContentAbsolute = Path.Combine( Environment.CurrentDirectory, MergedBundleContent );

	public const string Inventory = GamePaths.InventoryFile;

	public static string ModScriptBase = GamePaths.ModScriptBase;

	public static string VanillaScriptBase = GamePaths.VanillaScriptBase;

	public static string BundleBase = GamePaths.BundleBase;

	private static readonly string _modsDirSetting = Program.Settings.Get( "ModsDirectory" );

	private static readonly GamePaths selectedGame = new( () => GameDirectory, _modsDirSetting );

	// Options may not exist yet during startup, so read the game path only when it is needed.
	internal static GamePaths ForSelectedGame()
	{
		return new GamePaths( () => Program.OptionsFrm?.GameDirectory, _modsDirSetting );
	}

	public static string GameDirectory => Program.OptionsFrm.GameDirectory;

	public static string GameExeDX12 => Path.Combine( GameDirectory, "bin", "x64_dx12", "witcher3.exe" );

	public static string BundlesDirectory => selectedGame.BundlesDirectory;

	public static string DlcDirectory => selectedGame.DlcDirectory;

	public static string ScriptsDirectory => selectedGame.ScriptsDirectory;

	public static string ModsDirectory => selectedGame.ModsDirectory;

	public static bool IsScriptsDirectoryDerived => true;

	public static bool IsModsDirectoryDerived => selectedGame.IsModsDirectoryDerived;

	public static string GetRelativePath( string fullPath, string basePath )
	{
		int num = fullPath.IndexOfIgnoreCase( basePath ) + basePath.Length + 1;

		return fullPath.Substring( num, fullPath.Length - num );
	}

	public static bool ValidateDependencyPaths()
	{
		return File.Exists( KDiff3.ExePath );
	}

	public static bool ValidateModsDirectory()
	{
		if ( !Directory.Exists( ModsDirectory ) )
		{
			Directory.CreateDirectory( ModsDirectory );

			return ValidateModsDirectory();
		}

		return true;
	}

	public static bool ValidateScriptsDirectory()
	{
		if ( !Directory.Exists( ScriptsDirectory ) )
		{
			string message = !IsScriptsDirectoryDerived
				? UiText.T(
					"Paths",
					"Can't find the Scripts directory specified in the config file.\n\nIt was added in patch 1.08.1 and should contain the game's vanilla scripts." )
				: UiText.T(
					"Paths",
					"Can't find \\content\\content0\\scripts directory in the specified game directory.\n\nIt was added in patch 1.08.1 and should contain the game's vanilla scripts." );

			Program.MainFrm.ShowMessage( message );

			return false;
		}

		return true;
	}

	public static bool ValidateBundlesDirectory()
	{
		if ( !Directory.Exists( BundlesDirectory ) )
		{
			Program.MainFrm.ShowMessage( UiText.T( "Paths", "Can't find 'content' directory in the specified game directory." ) );

			return false;
		}

		return true;
	}

	public static string RetrieveMergedBundlePath()
	{
		return RetrieveMergedBundlePath( selectedGame );
	}

	// Resolving the mod name can prompt the user. Do that before constructing the bundle path.
	internal static string RetrieveMergedBundlePath( GamePaths game )
	{
		string mergedModName = RetrieveMergedModName();

		if ( mergedModName == null )
		{
			return null;
		}

		return Path.Combine( game.ModsDirectory, mergedModName, BundleBase, "blob0.bundle" );
	}

	public static string RetrieveMergedModName()
	{
		string mergedModName = Program.Settings.Get( "MergedModName" );

		if ( string.IsNullOrWhiteSpace( mergedModName ) )
		{
			Program.MainFrm.ShowMessage( UiText.T( "Paths", "The MergedModName setting isn't configured in the .config file." ) );

			return null;
		}

		if ( mergedModName.Length > 64 )
		{
			mergedModName = mergedModName.Substring( 0, 64 );
		}

		if ( ( !mergedModName.IsAlphaNumeric() || !mergedModName.StartsWith( "mod" ) ) && !ConfirmInvalidModName( mergedModName ) )
		{
			return null;
		}

		return mergedModName;
	}

	public static string RetrieveMergedModDir()
	{
		string modName = RetrieveMergedModName();

		if ( modName == null )
		{
			return null;
		}

		return Path.Combine( ModsDirectory, modName );
	}

	private static bool ConfirmInvalidModName( string mergedModName )
	{
		return DialogResult.Yes == Program.MainFrm.ShowMessage(
			UiText.Format(
				"Paths",
				"The Witcher 3 won't load the merged file if the mod name isn't \"mod\" followed by numbers, letters, or underscores.\n\nUse this name anyway?\n{0}\n\nTo change the name: Click No, then edit \"MergedModName\" in the .config file.",
				mergedModName ),
			UiText.T( "Paths", "Warning" ),
			MessageBoxButtons.YesNo,
			MessageBoxIcon.Exclamation );
	}
}
