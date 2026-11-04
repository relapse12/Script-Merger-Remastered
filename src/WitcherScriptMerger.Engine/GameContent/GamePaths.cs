#nullable enable
using System;
using System.IO;

namespace WitcherScriptMerger;

// Resolve folders when accessed so subsequent operations use the current game selection.
public sealed class GamePaths
{
	// Changes to these shared paths must invalidate cached merge plans.
	internal static readonly string VanillaScriptBase = Path.Combine( "content", "content0", "scripts" );
	internal const string BundleBase = "content";
	internal static readonly string ModScriptBase = Path.Combine( "content", "scripts" );

	// These paths are relative to the working directory.
	internal const string InventoryFile = "MergeInventory.xml";
	internal const string MergedBundleContent = "Merged Bundle Content";

	private readonly Func<string?> gameDirectory;
	private readonly string? configuredModsDirectory;
	private readonly string? explicitScriptsDirectory;

	internal GamePaths( Func<string?> gameDirectory, string? configuredModsDirectory )
		: this( gameDirectory, configuredModsDirectory, explicitScriptsDirectory: null )
	{
	}

	private GamePaths( Func<string?> gameDirectory, string? configuredModsDirectory, string? explicitScriptsDirectory )
	{
		this.gameDirectory = gameDirectory;
		this.configuredModsDirectory = configuredModsDirectory;
		this.explicitScriptsDirectory = explicitScriptsDirectory;
	}

	// Allows tests and tools to supply vanilla scripts outside a game installation.
	internal static GamePaths ForFolders( string? gameDirectory, string modsDirectory, string scriptsDirectory )
	{
		return new GamePaths( () => gameDirectory, modsDirectory, scriptsDirectory );
	}

	// This caller has only a Mods folder; the game directory remains unset.
	internal static GamePaths ForModsDirectory( string modsDirectory )
	{
		return new GamePaths( () => null, modsDirectory );
	}

	// The Options form is created after the main window, so the game directory may be unavailable during startup.
	internal string? GameDirectory => gameDirectory();

	internal string ModsDirectory => IsModsDirectoryDerived
		? Path.Combine( GameDirectory!, "Mods" )
		: configuredModsDirectory!;

	internal bool IsModsDirectoryDerived => string.IsNullOrWhiteSpace( configuredModsDirectory );

	internal string ScriptsDirectory => explicitScriptsDirectory ?? Path.Combine( GameDirectory!, VanillaScriptBase );

	internal string? ScriptsDirectoryIfSelected => explicitScriptsDirectory
		?? ( string.IsNullOrWhiteSpace( GameDirectory ) ? null : ScriptsDirectory );

	internal string DlcDirectory => Path.Combine( GameDirectory!, "DLC" );

	internal string BundlesDirectory => Path.Combine( GameDirectory!, BundleBase );

	internal void EnsureModsDirectory()
	{
		string mods = ModsDirectory;

		if ( !Directory.Exists( mods ) )
			Directory.CreateDirectory( mods );
	}
}
