#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WitcherScriptMerger.LoadOrder;

namespace WitcherScriptMerger.Inventory;

// Users can disable outputs in mods.settings or by prefixing their folder names.
// Keep their merge records and recovery data until the user removes the merge.
internal static class MergeOutputFolder
{
	// Locating a disabled output must leave it disabled.
	internal static string ResolveOutput( string modsDirectory, string relativeOutput )
	{
		string[] parts = relativeOutput.Replace( '\\', '/' ).Split( '/', 2 );
		string? renamed = parts.Length == 2 ? FindRenamed( modsDirectory, parts[0], parts[1] ) : null;

		return Path.GetFullPath( Path.Combine(
			modsDirectory, renamed is null ? relativeOutput : Path.Combine( renamed, parts[1] ) ) );
	}

	internal static bool IsRenamed( Merge merge, GamePaths game )
	{
		return FindRenamed( game.ModsDirectory, merge.MergedModName, RelativeOutput( merge, game ) ) is not null;
	}

	internal static bool IsRenamed( string modsDirectory, string outputPath )
	{
		string[] parts = outputPath.Replace( '\\', '/' ).Split( '/', 2 );

		return parts.Length == 2 && FindRenamed( modsDirectory, parts[0], parts[1] ) is not null;
	}

	// An unreadable load order cannot confirm that an output is disabled. A renamed folder can still establish that.
	internal static bool IsDisabled( Merge merge, GamePaths game, CustomLoadOrder? order )
	{
		try
		{
			if ( IsRenamed( merge, game ) )
				return true;

			return order?.IsValid == true && order.IsModDisabledByName( merge.MergedModName )
				&& Directory.Exists( Path.Combine( game.ModsDirectory, merge.MergedModName ) );
		}
		catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException )
		{
			// Leave the row unmarked when its state is unknown; operations that change files handle the read error.
			return false;
		}
	}

	// Check settings even before a target exists, since creating a disabled output would leave the conflicts unresolved.
	internal static string? FindDisabledTarget( IEnumerable<string> folders, IEnumerable<Merge> merges, GamePaths game, CustomLoadOrder? order )
	{
		Merge[] recorded = merges.ToArray();

		foreach ( string folder in folders.Distinct( StringComparer.OrdinalIgnoreCase ) )
		{
			if ( order?.IsValid == true && order.IsModDisabledByName( folder ) )
				return folder;

			string? renamed = recorded.Where( merge => folder.Equals( merge.MergedModName, StringComparison.OrdinalIgnoreCase ) )
				.Select( merge => FindRenamed( game.ModsDirectory, folder, RelativeOutput( merge, game ) ) )
				.FirstOrDefault( name => name is not null );

			if ( renamed is not null )
				return renamed;
		}

		return null;
	}

	// Let read failures reach the caller. A failed lookup must not authorize deletion or creation of another output folder.
	private static string? FindRenamed( string? modsDirectory, string? folder, string relativeOutput )
	{
		if ( string.IsNullOrWhiteSpace( modsDirectory ) || string.IsNullOrWhiteSpace( folder ) || Path.GetFileName( folder ) != folder )
			return null;

		if ( PathAbsence.IsConfirmed( modsDirectory ) || !PathAbsence.IsConfirmed( Path.Combine( modsDirectory, folder ) ) )
			return null;

		foreach ( string candidate in Directory.EnumerateDirectories( modsDirectory ).Order( StringComparer.OrdinalIgnoreCase ) )
		{
			string name = Path.GetFileName( candidate );

			if ( !IsPrefixed( name, folder ) )
				continue;

			string output = Path.Combine( candidate, relativeOutput );

			if ( !PathAbsence.IsConfirmed( output ) && !Directory.Exists( output ) )
				return name;
		}

		return null;
	}

	private static bool IsPrefixed( string name, string folder )
	{
		return name.Length > folder.Length && name.EndsWith( folder, StringComparison.OrdinalIgnoreCase )
			&& !name.StartsWith( "mod", StringComparison.OrdinalIgnoreCase );
	}

	private static string RelativeOutput( Merge merge, GamePaths game )
	{
		return string.IsNullOrWhiteSpace( game.ModsDirectory ) || string.IsNullOrWhiteSpace( merge.MergedModName )
			? ""
			: Path.GetRelativePath( Path.Combine( game.ModsDirectory, merge.MergedModName ), merge.GetOutputFile( game ) );
	}
}
