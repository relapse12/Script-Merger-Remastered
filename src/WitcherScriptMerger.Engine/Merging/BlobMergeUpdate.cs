#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using WSCompiler.Core;


namespace WitcherScriptMerger.Merging;

public static partial class BlobMergeTransaction
{
	private sealed record UpdateFile( string Path, string? Before, string? After, string Backup );
	// Recovery needs both merge records and every file changed between them, including renamed source folders.
	private sealed record UpdateJournal(
		string Id,
		AppliedBlobMerge Previous,
		AppliedBlobMerge Next,
		IReadOnlyList<UpdateFile> Files,
		IReadOnlyDictionary<string, string> SourceFolders );

	// Back up affected files and save the update journal before changing the installed merge.
	// Keep that journal until the replacement or its rollback finishes.
	// An initial ordinary merge and adopted outputs need saved copies so removing the blob merge can restore them.
	public static AppliedBlobMerge Replace(
		string candidate,
		string modsRoot,
		string stateFile,
		string? settingsPath,
		string? updatedSettings,
		MergeInventoryChange inventory,
		string expectedStateHash,
		IReadOnlyDictionary<string, string> previousOutputs,
		Func<byte[]?, string, byte[]?> reconcileInventory,
		string? workspaceDirectory = null,
		IReadOnlySet<string>? removedMods = null,
		Action<string>? checkpoint = null,
		AppliedBlobMerge? initial = null,
		IReadOnlyList<MergeOutputFile>? adoptedOutputs = null )
	{
		if ( UsesInventory( stateFile ) && !Path.GetFullPath( stateFile ).Equals( Path.GetFullPath( inventory.Path ), StringComparison.OrdinalIgnoreCase ) )
			throw new InvalidDataException( "Embedded recovery state must use the same merge inventory." );

		if ( CurrentHash( stateFile ) != ( expectedStateHash.Length == 0 ? null : expectedStateHash ) )
			throw new IOException( "The saved merge changed; refresh before merging." );

		if ( File.Exists( stateFile + ".pending" ) || File.Exists( stateFile + ".update" ) )
			throw new IOException( "An interrupted merge needs recovery." );

		var previous = initial is null ? Read( stateFile ) : !HasState( stateFile ) ? initial : throw new IOException( "The saved merge already exists." );
		Validate( previous.Manifest );
		var sourceFolders = MergeSourceLocations.Find( previous.Manifest );
		string Locate( string path ) => MergeSourceLocations.PathFor( modsRoot, path, sourceFolders );
		CheckRoot( previous.Manifest, modsRoot );
		CheckInventory( previous, inventory.Path );

		if ( !string.Equals( previous.SettingsPath, settingsPath is null ? null : Path.GetFullPath( settingsPath ), StringComparison.OrdinalIgnoreCase ) )
			throw new IOException( "The saved merge belongs to a different game Documents folder." );

		var manifest = JsonSerializer.Deserialize<BlobMergeManifest>( File.ReadAllText( Path.Combine( candidate, "merge.json" ) ) )
			?? throw new InvalidDataException( "Missing merge manifest." );
		Validate( manifest );
		CheckRoot( manifest, modsRoot );

		if ( adoptedOutputs is not null )
		{
			if ( adoptedOutputs.Any( output => !manifest.Outputs.Any( next => next.Path.Equals( output.Path, StringComparison.OrdinalIgnoreCase ) )
				|| previous.Manifest.Outputs.Any( old => old.Path.Equals( output.Path, StringComparison.OrdinalIgnoreCase ) ) ) )
				throw new InvalidDataException( "Invalid adopted output ownership." );

			previous = previous with
			{
				Manifest = previous.Manifest with
				{
					Outputs = previous.Manifest.Outputs.Concat( adoptedOutputs ).ToArray(),
					Mods = previous.Manifest.Mods.Concat( adoptedOutputs.SelectMany( output => output.Mods ?? [] ) ).Distinct( StringComparer.OrdinalIgnoreCase ).ToArray()
				}
			};
			Validate( previous.Manifest );
		}

		if ( manifest.Id == previous.Manifest.Id )
			throw new InvalidDataException( "A replacement needs a new merge identity." );

		MergeBuilder.VerifyInputs( manifest.Inputs );
		byte[]? settings = settingsPath is not null && File.Exists( settingsPath ) ? File.ReadAllBytes( settingsPath ) : null;

		if ( ( settings is null ? null : Hash( settings ) ) != manifest.SettingsSha256 )
			throw new IOException( "Load order changed; refresh before merging." );

		byte[]? currentInventory = File.Exists( inventory.Path ) ? File.ReadAllBytes( inventory.Path ) : null;

		if ( ( currentInventory is null ? null : Hash( currentInventory ) ) != inventory.ExpectedHash )
			throw new IOException( "The merge inventory changed." );

		var inventoryInput = manifest.Inputs.SingleOrDefault( input => input.Path.Equals( inventory.Path, StringComparison.OrdinalIgnoreCase ) );

		if ( inventoryInput is null || inventoryInput.Sha256 != ( inventory.ExpectedHash ?? "" ) )
			throw new InvalidDataException( "The merge inventory is not a planned input." );

		foreach ( var input in manifest.Inputs.Where( input => input.ReadPath is not null ) )
			if ( !Suppressed( previous.Manifest ).Any( old => Locate( old.Path ).Equals( input.Path, StringComparison.OrdinalIgnoreCase )
				&& Locate( old.SuppressedPath! ).Equals( input.ReadPath, StringComparison.OrdinalIgnoreCase ) && old.Sha256 == input.Sha256 ) )
				throw new InvalidDataException( "Only a previously managed source can use its suppressed copy." );

		var oldOutputs = previous.Manifest.Outputs.ToDictionary( output => output.Path, StringComparer.OrdinalIgnoreCase );

		if ( manifest.Outputs.Any( output => output.Path.EndsWith( ".rsblob", StringComparison.OrdinalIgnoreCase ) )
			&& ( Directory.Exists( MergeBuilder.SafePath( modsRoot, manifest.CompiledMod ) ) || File.Exists( MergeBuilder.SafePath( modsRoot, manifest.CompiledMod ) ) )
			&& !previous.Manifest.Outputs.Any( output => output.Path.Replace( '\\', '/' ).Equals( manifest.CompiledMod + "/content/precompiled.rsblob", StringComparison.OrdinalIgnoreCase ) ) )
			throw new IOException( "The compiled output folder belongs to another mod." );

		if ( previousOutputs.Count != oldOutputs.Count || previousOutputs.Keys.Any( path => !oldOutputs.ContainsKey( path ) ) )
			throw new InvalidDataException( "The replacement does not identify every previous output." );

		foreach ( var output in oldOutputs.Values )
		{
			string path = MergeBuilder.SafePath( modsRoot, output.Path );

			if ( ( File.Exists( path ) ? MergeBuilder.Hash( path ) : "" ) != previousOutputs[output.Path] )
				throw new IOException( "Merged output changed during the merge: " + path );
		}

		var desired = new Dictionary<string, byte[]?>( StringComparer.OrdinalIgnoreCase );
		// An updated mod must not receive its previous version's backup.
		var updatedMods = Suppressed( manifest ).Where( input => input.ReadPath is null && Suppressed( previous.Manifest ).Any( old => old.Path.Equals( input.Path, StringComparison.OrdinalIgnoreCase ) ) )
			.Select( input => Path.GetRelativePath( modsRoot, input.Path ).Split( Path.DirectorySeparatorChar )[0] ).ToHashSet( StringComparer.OrdinalIgnoreCase );
		updatedMods.UnionWith( Suppressed( previous.Manifest ).Where( input => File.Exists( Locate( input.Path ) ) )
			.Select( input => Path.GetRelativePath( modsRoot, input.Path ).Split( Path.DirectorySeparatorChar )[0] ) );
		var restored = ( previous.RestoredOutputs ?? [] ).ToList();

		// Keep the ordinary merge's file so removing this blob merge can restore it.
		foreach ( var output in ( initial is not null ? previous.Manifest.Outputs : adoptedOutputs ?? [] ) )
		{
			string source = MergeBuilder.SafePath( modsRoot, output.Path );

			if ( !Matches( source, output.Sha256 ) )
				throw new IOException( "An existing merge changed before adoption: " + source );

			string relative = "original-outputs-" + manifest.Id + "/" + output.Path.Replace( '\\', '/' );
			string storage = MergeBuilder.SafePath( StorageDirectory( stateFile ), relative );
			Directory.CreateDirectory( Path.GetDirectoryName( storage )! );
			File.Copy( source, storage, overwrite: false );
			restored.Add( new( output.Path, relative, output.Sha256 ) );
		}

		foreach ( var output in previous.Manifest.Outputs )
		{
			var original = restored.SingleOrDefault( original => original.Path.Equals( output.Path, StringComparison.OrdinalIgnoreCase ) );
			desired[MergeBuilder.SafePath( modsRoot, output.Path )] = original is null ? null
				: ReadExpected( MergeBuilder.SafePath( StorageDirectory( stateFile ), original.StoragePath ), original.Sha256 );
		}

		foreach ( var output in manifest.Outputs )
		{
			string path = MergeBuilder.SafePath( modsRoot, output.Path ), source = MergeBuilder.SafePath( candidate, output.Path );

			if ( !oldOutputs.ContainsKey( output.Path ) && ( File.Exists( path ) || Directory.Exists( path ) ) )
				throw new IOException( "An unrelated merge owns " + path );

			desired[path] = ReadExpected( source, output.Sha256 );
		}

		// Restore sources no longer needed by the new merge, unless that would reintroduce an old or removed mod.
		foreach ( var old in Suppressed( previous.Manifest ) )
		{
			if ( Suppressed( manifest ).Any( input => input.Path.Equals( old.Path, StringComparison.OrdinalIgnoreCase ) ) )
				continue;

			string source = Locate( old.SuppressedPath! ), target = Locate( old.Path );

			if ( !File.Exists( source ) )
				continue;

			if ( !Matches( source, old.Sha256 ) )
				throw new IOException( "A source recovery copy changed: " + source );

			string mod = Path.GetRelativePath( modsRoot, old.Path ).Split( Path.DirectorySeparatorChar )[0];

			if ( !File.Exists( target ) && Directory.Exists( Path.GetDirectoryName( target ) ) && removedMods?.Contains( mod ) != true && !updatedMods.Contains( mod ) )
				desired[target] = File.ReadAllBytes( source );

			desired[source] = null;
		}

		foreach ( var input in Suppressed( manifest ) )
		{
			if ( File.Exists( input.SuppressedPath ) && !Suppressed( previous.Manifest ).Any( old => old.Path.Equals( input.Path, StringComparison.OrdinalIgnoreCase ) ) )
				throw new IOException( "An unrelated source backup already exists: " + input.SuppressedPath );

			byte[] bytes = File.ReadAllBytes( input.ReadPath ?? input.Path );
			desired[input.SuppressedPath!] = bytes;
			desired[input.Path] = null;
		}

		byte[]? nextSettings = updatedSettings is null ? settings : new UTF8Encoding( false ).GetBytes( updatedSettings );

		if ( settingsPath is not null )
			desired[Path.GetFullPath( settingsPath )] = nextSettings;

		desired[Path.GetFullPath( inventory.Path )] = inventory.Content;
		var storedManifest = manifest with
		{
			Inputs = manifest.Inputs.Select( input => input.SuppressedPath is null
			? input with
			{
				ReadPath = null
			} : input ).ToArray()
		};
		var next = new AppliedBlobMerge( storedManifest, SettingsBaseline( previous, settings ), nextSettings is null ? null : Hash( nextSettings ),
			previous.SettingsPath, new( Path.GetFullPath( inventory.Path ),
				( currentInventory is null ? null : UsesInventory( stateFile ) ? InventoryDigest( currentInventory ) : Hash( currentInventory ) ) == previous.Inventory?.AppliedHash
					? previous.Inventory?.Previous : reconcileInventory( currentInventory, previous.Manifest.Id ),
				UsesInventory( stateFile ) ? InventoryDigest( inventory.Content ) : Hash( inventory.Content ) ),
			( previous.CreatedDirectories ?? [] ).Concat( MissingOutputDirectories( manifest ) ).Distinct( StringComparer.OrdinalIgnoreCase )
				.Where( relative => manifest.Outputs.Any( output => output.Path.Replace( '\\', '/' ).StartsWith( relative.Replace( '\\', '/' ).TrimEnd( '/' ) + "/", StringComparison.OrdinalIgnoreCase ) ) ).ToArray(),
			PreserveWorkspace( workspaceDirectory, stateFile, manifest.Id ),
			restored.Where( original => manifest.Outputs.Any( output => output.Path.Equals( original.Path, StringComparison.OrdinalIgnoreCase ) ) ).ToArray(), UsesInventory( stateFile ) );
		desired[Path.GetFullPath( stateFile )] = UsesInventory( stateFile ) ? SetInventoryState( inventory.Content, next )
			: JsonSerializer.SerializeToUtf8Bytes( next, new JsonSerializerOptions { WriteIndented = true } );
		// Save the update backups and journal before changing installed files.
		string id = Guid.NewGuid().ToString( "N" ), backupRoot = MergeBuilder.SafePath( StorageDirectory( stateFile ), "update-" + id );
		Directory.CreateDirectory( backupRoot );
		var files = new List<UpdateFile>();

		try
		{
			foreach ( var pair in desired )
			{
				byte[]? before = File.Exists( pair.Key ) ? File.ReadAllBytes( pair.Key ) : null;
				string backup = files.Count.ToString( "D6" );

				if ( before is not null )
					WriteNew( Path.Combine( backupRoot, backup ), before );

				files.Add( new( pair.Key, before is null ? null : Hash( before ), pair.Value is null ? null : Hash( pair.Value ), backup ) );
			}

			MergeBuilder.VerifyInputs( manifest.Inputs );

			if ( CurrentHash( stateFile ) != ( expectedStateHash.Length == 0 ? null : expectedStateHash ) )
				throw new IOException( "The saved merge changed during preparation." );

			foreach ( var output in oldOutputs.Values )
				if ( ( CurrentHash( MergeBuilder.SafePath( modsRoot, output.Path ) ) ?? "" ) != previousOutputs[output.Path] )
					throw new IOException( "Merged output changed during preparation: " + output.Path );

			WriteAtomic( stateFile + ".update", JsonSerializer.SerializeToUtf8Bytes( new UpdateJournal( id, previous, next, files, sourceFolders ) ), overwrite: false );
		}
		catch
		{
			// Without a journal, this attempt has only created temporary files.
			if ( !File.Exists( stateFile + ".update" ) )
			{
				CleanupUpdateBackups( stateFile, id, files );
				CleanupWorkspace( next, stateFile );
				CleanupAdoptedOutputs( next, stateFile, previous.RestoredOutputs ?? [] );
			}

			throw;
		}

		try
		{
			// Check for changes made after preparation before modifying each file.
			checkpoint?.Invoke( "prepared" );

			foreach ( var file in files )
			{
				if ( CurrentHash( file.Path ) != file.Before )
					throw new IOException( "A merge input changed during replacement: " + file.Path );

				if ( file.Before == file.After )
					continue;

				if ( desired[file.Path] is { } bytes )
				{
					Directory.CreateDirectory( Path.GetDirectoryName( file.Path )! );
					WriteAtomic( file.Path, bytes );
				}
				else if ( File.Exists( file.Path ) )
					File.Delete( file.Path );

				checkpoint?.Invoke( file.Path );
			}
		}
		catch
		{
			RecoverUpdate( stateFile, settingsPath, modsRoot, inventory.Path, forceRollback: true );
			throw;
		}

		// The replacement is complete. Recovery can finish cleanup if the journal cannot be removed now.
		try
		{
			File.Delete( stateFile + ".update" );
		}
		catch ( IOException )
		{
		}
		catch ( UnauthorizedAccessException )
		{
		}

		if ( !File.Exists( stateFile + ".update" ) )
		{
			CleanupUpdateBackups( stateFile, id, files );
			CleanupWorkspace( previous, stateFile );
			CleanupAdoptedOutputs( previous, stateFile, next.RestoredOutputs ?? [] );
		}

		try
		{
			RemoveEmptyOutputs( previous.Manifest, previous.CreatedDirectories );
		}
		catch ( IOException )
		{
		}
		catch ( UnauthorizedAccessException )
		{
		}

		return next;
	}

	// All new hashes matching means the replacement finished. Otherwise roll back only files that still match
	// the recorded before or after contents; unrelated edits must survive recovery.
	public static void RecoverUpdate(
		string stateFile,
		string? settingsPath,
		string? expectedModsRoot,
		string? inventoryPath,
		bool forceRollback = false )
	{
		string journalPath = stateFile + ".update";

		if ( new FileInfo( journalPath ).Length > 32 * 1024 * 1024 )
			throw new InvalidDataException( "The merge replacement journal is too large." );

		var journal = JsonSerializer.Deserialize<UpdateJournal>( File.ReadAllText( journalPath ) ) ?? throw new InvalidDataException( "Invalid merge replacement journal." );

		if ( journal.Previous is null || journal.Next is null || journal.Files is null )
			throw new InvalidDataException( "Incomplete merge replacement journal." );

		if ( journal.SourceFolders is null )
			throw new InvalidDataException( "Missing moved source folders." );

		MergeSourceLocations.Validate( journal.Previous.Manifest, journal.SourceFolders );
		ValidateApplied( journal.Previous, stateFile );
		ValidateApplied( journal.Next, stateFile );
		CheckRoot( journal.Previous.Manifest, expectedModsRoot );
		CheckRoot( journal.Next.Manifest, expectedModsRoot );
		CheckInventory( journal.Previous, inventoryPath );
		CheckInventory( journal.Next, inventoryPath );

		if ( !Guid.TryParseExact( journal.Id, "N", out _ )
			|| journal.Previous.SettingsPath != journal.Next.SettingsPath
			|| !string.Equals( journal.Next.SettingsPath, settingsPath is null ? null : Path.GetFullPath( settingsPath ), StringComparison.OrdinalIgnoreCase ) )
			throw new InvalidDataException( "Invalid merge replacement identity." );

		string backupRoot = MergeBuilder.SafePath( StorageDirectory( stateFile ), "update-" + journal.Id );
		var allowed = journal.Previous.Manifest.Outputs.Concat( journal.Next.Manifest.Outputs ).Select( output => MergeBuilder.SafePath( journal.Next.Manifest.ModsRoot, output.Path ) )
			.Concat( Suppressed( journal.Previous.Manifest ).Concat( Suppressed( journal.Next.Manifest ) ).SelectMany( input => new[] { input.Path, input.SuppressedPath! } ) )
			.Concat( Suppressed( journal.Previous.Manifest ).SelectMany( input => new[] {
				MergeSourceLocations.PathFor(journal.Previous.Manifest.ModsRoot, input.Path, journal.SourceFolders),
				MergeSourceLocations.PathFor(journal.Previous.Manifest.ModsRoot, input.SuppressedPath!, journal.SourceFolders) } ) )
			.Append( Path.GetFullPath( stateFile ) ).Concat( settingsPath is null ? [] : new[] { Path.GetFullPath( settingsPath ) } )
			.Concat( inventoryPath is null ? [] : new[] { Path.GetFullPath( inventoryPath ) } ).ToHashSet( StringComparer.OrdinalIgnoreCase );

		if ( journal.Files.Select( file => file.Path ).Distinct( StringComparer.OrdinalIgnoreCase ).Count() != journal.Files.Count
			|| journal.Files.Any( file => !allowed.Contains( file.Path ) || file.Backup.Length != 6 || !file.Backup.All( char.IsAsciiDigit ) ) )
			throw new InvalidDataException( "Invalid merge replacement file." );

		bool committed = !forceRollback && journal.Files.All( file => CurrentHash( file.Path ) == file.After );

		if ( !committed )
		{
			foreach ( var file in journal.Files )
			{
				string? current = CurrentHash( file.Path );

				if ( current != file.Before && current != file.After )
					throw new IOException( "A changed file prevents automatic replacement recovery: " + file.Path );

				if ( file.Before is not null && !Matches( MergeBuilder.SafePath( backupRoot, file.Backup ), file.Before ) )
					throw new IOException( "A replacement recovery copy is missing or changed." );
			}

			foreach ( var file in journal.Files.AsEnumerable().Reverse() )
			{
				if ( CurrentHash( file.Path ) == file.Before )
					continue;

				if ( file.Before is null )
				{
					if ( File.Exists( file.Path ) )
						File.Delete( file.Path );
				}
				else
				{
					Directory.CreateDirectory( Path.GetDirectoryName( file.Path )! );
					WriteAtomic( file.Path, File.ReadAllBytes( MergeBuilder.SafePath( backupRoot, file.Backup ) ) );
				}
			}

			RemoveEmptyOutputs( journal.Next.Manifest, journal.Next.CreatedDirectories );
		}

		File.Delete( journalPath );
		CleanupUpdateBackups( stateFile, journal.Id, journal.Files );
		CleanupWorkspace( committed ? journal.Previous : journal.Next, stateFile );
		CleanupAdoptedOutputs( committed ? journal.Previous : journal.Next, stateFile,
			( committed ? journal.Next : journal.Previous ).RestoredOutputs ?? [] );
	}

	public static string WorkspacePath( string stateFile, string id ) => MergeBuilder.SafePath( StorageDirectory( stateFile ), "workspace-" + id );

	// Save hashes with the working copies so later cleanup can preserve any edits.
	private static IReadOnlyList<MergeOutputFile>? PreserveWorkspace(
		string? source,
		string stateFile,
		string id )
	{
		if ( source is null )
			return null;

		string target = WorkspacePath( stateFile, id );

		if ( Directory.Exists( target ) )
			throw new IOException( "The merge source workspace already exists." );

		var files = new List<MergeOutputFile>();

		foreach ( string path in FilesWithoutLinks( source ) )
		{
			string relative = Path.GetRelativePath( source, path ), destination = MergeBuilder.SafePath( target, relative );
			Directory.CreateDirectory( Path.GetDirectoryName( destination )! );
			File.Copy( path, destination );
			files.Add( new( relative, MergeBuilder.Hash( destination ) ) );
		}

		return files;
	}

	private static string? CurrentHash( string path ) => File.Exists( path ) ? MergeBuilder.Hash( path ) : null;

	private static byte[] ReadExpected(
		string path,
		string expected )
	{
		byte[] bytes = File.ReadAllBytes( path );

		if ( Hash( bytes ) != expected )
			throw new IOException( "A merge file changed: " + path );

		return bytes;
	}

	// Carry forward settings from before the merge while preserving later user edits.
	private static byte[]? SettingsBaseline(
		AppliedBlobMerge previous,
		byte[]? current )
	{
		if ( ( current is null ? null : Hash( current ) ) == previous.AppliedSettingsHash )
			return previous.PreviousSettings;

		if ( current is null || previous.AppliedSettingsHash == ( previous.PreviousSettings is null ? null : Hash( previous.PreviousSettings ) ) )
			return current;

		string text = TextDecoding.Decode( current );

		foreach ( string mod in OwnedSettings( previous ) )
			text = System.Text.RegularExpressions.Regex.Replace( text,
				@"(?ms)^[ \t]*\[" + System.Text.RegularExpressions.Regex.Escape( mod ) + @"\][^\r\n]*(?:\r?\n|$).*?(?=^[ \t]*\[|\z)", "" );

		return new UTF8Encoding( false ).GetBytes( text );
	}
}
