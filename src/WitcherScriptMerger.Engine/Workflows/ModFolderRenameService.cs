#nullable enable
using System;
using System.Collections.Generic;
using System.IO;

namespace WitcherScriptMerger.FileIndex;

internal sealed record ModFolderRenameResult( string? NewName, string? Error = null, bool Unavailable = false );

// Keep folder names, settings and inventory records consistent, rolling back completed steps on failure.
internal static class ModFolderRenameService
{
	internal static ModFolderRenameResult Rename(
		MergeSession session,
		string mergedModName,
		string oldName,
		string requestedName,
		bool operationInProgress )
	{
		if ( session.LoadOrder?.GetModLoadSettingByName( oldName )?.IsExternal == true )
			return new( null, UiText.T(
				"MainForm", "Mod.io content is managed by the game and is read-only in Script Merger Remastered." ), true );

		if ( string.IsNullOrWhiteSpace( requestedName ) )
			return new( null );

		string newName;

		try
		{
			newName = ValidateRequestedName( session.GamePaths.ModsDirectory, oldName, requestedName );
		}
		catch ( Exception error ) when ( error is ArgumentException or IOException or UnauthorizedAccessException )
		{
			return new( null, error.Message );
		}

		if ( oldName.Equals( newName, StringComparison.Ordinal ) )
			return new( null );

		if ( operationInProgress )
			return new( null, UiText.T(
				"MainForm", "Wait for the current Script Merger Remastered operation to finish before renaming a mod folder." ), true );

		if ( oldName.EqualsIgnoreCase( mergedModName ) )
			return new( null, UiText.T(
				"MainForm", "The generated merged-mod folder cannot be renamed here because Script Merger Remastered manages that folder." ), true );

		if ( !oldName.EqualsIgnoreCase( newName )
			&& ( ( session.LoadOrder?.ContainsMod( newName ) ?? false )
				|| ( session.Inventory?.ContainsModReference( newName ) ?? false ) ) )
			return new( null, UiText.T( "MainForm", "The new name is already referenced by mods.settings or MergeInventory.xml." ) );

		bool folderRenamed = false;
		bool loadOrderRenameAttempted = false;
		bool inventoryRenamed = false;

		try
		{
			RenameDirectory( session.GamePaths.ModsDirectory, oldName, newName );
			folderRenamed = true;

			if ( session.LoadOrder?.ContainsMod( oldName ) == true )
			{
				loadOrderRenameAttempted = true;
				session.LoadOrder.RenameModByName( oldName, newName );
			}

			if ( session.Inventory is not null )
			{
				inventoryRenamed = session.Inventory.RenameModReferences( oldName, newName );

				if ( inventoryRenamed )
					session.SaveInventory();
			}

			return new( newName );
		}
		catch ( Exception error ) when ( error is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException )
		{
			List<string> rollbackErrors = RollBack( session, oldName, newName,
				inventoryRenamed, loadOrderRenameAttempted, folderRenamed );
			string rollbackMessage = rollbackErrors.Count == 0
				? string.Empty
				: UiText.Format( "MainForm", "\n\nSome rollback steps also failed:\n{0}", string.Join( "\n", rollbackErrors ) );

			return new( null, error.Message + rollbackMessage );
		}
	}

	// Continue attempting rollback after an error so the caller receives all failures.
	private static List<string> RollBack(
		MergeSession session,
		string oldName,
		string newName,
		bool inventoryRenamed,
		bool loadOrderRenameAttempted,
		bool folderRenamed )
	{
		var errors = new List<string>();

		void Attempt( Action step, Func<string, string> describe )
		{
			try
			{
				step();
			}
			catch ( Exception error )
			{
				errors.Add( describe( error.Message ) );
			}
		}

		if ( inventoryRenamed )
		{
			Attempt( () =>
			{
				session.Inventory!.RenameModReferences( newName, oldName );
				session.SaveInventory();
			}, message => UiText.Format( "MainForm", "Merge inventory: {0}", message ) );
		}

		if ( loadOrderRenameAttempted )
			Attempt(
				() => session.LoadOrder!.RenameModByName( newName, oldName ),
				message => UiText.Format( "MainForm", "mods.settings: {0}", message ) );

		if ( folderRenamed )
			Attempt(
				() => RenameDirectory( session.GamePaths.ModsDirectory, newName, oldName ),
				message => UiText.Format( "MainForm", "Mod folder: {0}", message ) );

		return errors;
	}

	internal static string ValidateRequestedName( string modsDirectory, string oldName, string requestedName )
	{
		if ( string.IsNullOrWhiteSpace( modsDirectory ) )
		{
			throw new ArgumentException(
				UiText.T( "ModFolderRenameService", "The Mods directory is not configured." ),
				nameof( modsDirectory ) );
		}

		if ( string.IsNullOrWhiteSpace( oldName ) )
		{
			throw new ArgumentException(
				UiText.T( "ModFolderRenameService", "The existing mod folder name is missing." ),
				nameof( oldName ) );
		}

		string newName = requestedName?.Trim() ?? string.Empty;

		if ( newName.Length == 0 )
		{
			throw new ArgumentException( UiText.T( "ModFolderRenameService", "A mod folder name is required." ), nameof( requestedName ) );
		}

		if ( !newName.StartsWith( "mod", StringComparison.OrdinalIgnoreCase ) )
		{
			throw new ArgumentException(
				UiText.T( "ModFolderRenameService", "Mod folder names must begin with 'mod'." ),
				nameof( requestedName ) );
		}

		if ( newName.IndexOfAny( Path.GetInvalidFileNameChars() ) >= 0
			|| newName.Contains( Path.DirectorySeparatorChar )
			|| newName.Contains( Path.AltDirectorySeparatorChar )
			|| newName is "." or ".." )
		{
			throw new ArgumentException(
				UiText.T(
					"ModFolderRenameService",
					"The new mod folder name contains characters that Windows cannot use in a folder name." ),
				nameof( requestedName ) );
		}

		string modsPath = Path.GetFullPath( modsDirectory );
		string sourcePath = GetDirectChildPath( modsPath, oldName );
		string destinationPath = GetDirectChildPath( modsPath, newName );

		if ( !Directory.Exists( sourcePath ) )
		{
			throw new DirectoryNotFoundException( UiText.Format( "ModFolderRenameService", "The mod folder no longer exists: {0}", sourcePath ) );
		}

		if ( !sourcePath.Equals( destinationPath, StringComparison.OrdinalIgnoreCase )
			&& ( Directory.Exists( destinationPath ) || File.Exists( destinationPath ) ) )
		{
			throw new IOException( UiText.Format( "ModFolderRenameService", "A file or folder named '{0}' already exists in the Mods directory.", newName ) );
		}

		return newName;
	}

	// Use a temporary name for case-only renames on Windows, and move back if the second step fails.
	internal static void RenameDirectory( string modsDirectory, string oldName, string requestedName )
	{
		string newName = ValidateRequestedName( modsDirectory, oldName, requestedName );

		if ( oldName.Equals( newName, StringComparison.Ordinal ) )
		{
			return;
		}

		string modsPath = Path.GetFullPath( modsDirectory );
		string sourcePath = GetDirectChildPath( modsPath, oldName );
		string destinationPath = GetDirectChildPath( modsPath, newName );

		if ( !sourcePath.Equals( destinationPath, StringComparison.OrdinalIgnoreCase ) )
		{
			Directory.Move( sourcePath, destinationPath );

			return;
		}

		string temporaryName = oldName + ".script-merger-remastered-rename-" + Guid.NewGuid().ToString( "N" );
		string temporaryPath = GetDirectChildPath( modsPath, temporaryName );

		Directory.Move( sourcePath, temporaryPath );

		try
		{
			Directory.Move( temporaryPath, destinationPath );
		}
		catch
		{
			if ( Directory.Exists( temporaryPath ) && !Directory.Exists( sourcePath ) )
			{
				Directory.Move( temporaryPath, sourcePath );
			}

			throw;
		}
	}

	private static string GetDirectChildPath( string modsPath, string folderName )
	{
		if ( !string.Equals( Path.GetFileName( folderName ), folderName, StringComparison.Ordinal ) )
		{
			throw new ArgumentException(
				UiText.T( "ModFolderRenameService", "Only top-level folders inside the Mods directory can be renamed." ),
				nameof( folderName ) );
		}

		string childPath = Path.GetFullPath( Path.Combine( modsPath, folderName ) );

		if ( !string.Equals( Path.GetDirectoryName( childPath ), modsPath, StringComparison.OrdinalIgnoreCase ) )
		{
			throw new ArgumentException(
				UiText.T( "ModFolderRenameService", "Only top-level folders inside the Mods directory can be renamed." ),
				nameof( folderName ) );
		}

		return childPath;
	}
}
