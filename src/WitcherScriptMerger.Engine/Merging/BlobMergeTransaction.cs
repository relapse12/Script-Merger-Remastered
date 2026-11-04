#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;


namespace WitcherScriptMerger.Merging;

// Restoration relies on these saved paths and hashes to distinguish the merge's files from later edits.
// Keep the previous settings and inventory so removing a merge can undo its changes.
public sealed record AppliedBlobMerge(
	BlobMergeManifest Manifest,
	byte[]? PreviousSettings,
	string? AppliedSettingsHash,
	string? SettingsPath = null,
	MergeSidecarState? Inventory = null,
	IReadOnlyList<string>? CreatedDirectories = null,
	IReadOnlyList<MergeOutputFile>? Workspace = null,
	IReadOnlyList<MergeRestoredOutput>? RestoredOutputs = null,
	bool InventoryEmbedded = false );

// Keep a copy of an ordinary merge's output so removing the blob merge can restore it.
public sealed record MergeRestoredOutput( string Path, string StoragePath, string Sha256 );

// A null ExpectedHash requires the inventory file to be absent.
public sealed record MergeInventoryChange( string Path, string? ExpectedHash, byte[] Content );

// For embedded state, AppliedHash excludes the state element because saving that element changes the inventory.
public sealed record MergeSidecarState( string Path, byte[]? Previous, string AppliedHash );

public sealed class EditedMergeOutputsException : IOException
{
	public IReadOnlyDictionary<string, string> Outputs
	{
		get;
	}

	public EditedMergeOutputsException(
		IReadOnlyDictionary<string, string> outputs )
		: base( "Merged output has edits; preserve them before restoring: "
			+ string.Join( Environment.NewLine, outputs.Keys ) )
	{
		Outputs = outputs;
	}
}

// Applies and restores the file changes chosen by the merge planner.
// A journal is saved before changing installed files, allowing interrupted operations to be recovered.
// The changes span several files; recovery can also fail if those files were edited or become unreadable.
public static partial class BlobMergeTransaction
{
	public static AppliedBlobMerge ReadApplied(
		string stateFile,
		string? expectedModsRoot = null,
		string? expectedInventoryPath = null )
	{
		var applied = Read( stateFile );
		CheckRoot( applied.Manifest, expectedModsRoot );
		CheckInventory( applied, expectedInventoryPath );

		return applied;
	}
	// Check that the inputs, outputs and settings still match the plan before applying it.
	// Keep the journal until the applied state is saved, so an interruption can be rolled back.
	public static AppliedBlobMerge Apply(
		string candidate,
		string modsRoot,
		string stateFile,
		string? settingsPath,
		string? updatedSettings,
		MergeInventoryChange? inventory = null,
		string? workspaceDirectory = null )
	{
		if ( UsesInventory( stateFile ) && ( inventory is null || !Path.GetFullPath( stateFile ).Equals( Path.GetFullPath( inventory.Path ), StringComparison.OrdinalIgnoreCase ) ) )
			throw new InvalidDataException( "Embedded recovery state must use the same merge inventory." );

		if ( HasState( stateFile ) )
			throw new IOException( "Restore the existing managed merge before applying another." );

		string journal = stateFile + ".pending";

		if ( File.Exists( journal ) )
			throw new IOException( "An interrupted merge needs recovery: " + journal );

		var manifest = JsonSerializer.Deserialize<BlobMergeManifest>( File.ReadAllText( Path.Combine( candidate, "merge.json" ) ) )
			?? throw new InvalidDataException( "Missing merge manifest." );
		Validate( manifest );

		if ( !Path.GetFullPath( modsRoot ).Equals( manifest.ModsRoot, StringComparison.OrdinalIgnoreCase ) )
			throw new InvalidDataException( "The candidate belongs to a different Mods folder." );

		MergeBuilder.VerifyInputs( manifest.Inputs );

		foreach ( string mod in manifest.Mods )
		{
			string content = MergeBuilder.SafePath( modsRoot, mod + "/content" );
			var expected = ScriptInputs( manifest, content );

			if ( expected.Values.Any( input => Path.GetFileName( input.Path ).Equals( "precompiled.rsblob", StringComparison.OrdinalIgnoreCase ) )
				&& Directory.Exists( content ) && FilesWithoutLinks( content ).Where( IsScript ).Any( path => !expected.ContainsKey( path ) ) )
				throw new IOException( "New source was installed; rescan before applying: " + mod );
		}

		foreach ( var input in Suppressed( manifest ) )
			if ( File.Exists( input.SuppressedPath ) || Directory.Exists( input.SuppressedPath ) )
				throw new IOException( "A disabled-source backup already exists: " + input.SuppressedPath );

		foreach ( var output in manifest.Outputs )
		{
			string source = MergeBuilder.SafePath( candidate, output.Path );

			if ( !File.Exists( source ) || MergeBuilder.Hash( source ) != output.Sha256 )
				throw new IOException( "Candidate output changed." );
		}

		foreach ( string name in new[] { manifest.LooseMod, manifest.CompiledMod } )
		{
			if ( manifest.SharedLooseFolder && name == manifest.LooseMod )
				continue;

			if ( Directory.Exists( MergeBuilder.SafePath( modsRoot, name ) ) || File.Exists( MergeBuilder.SafePath( modsRoot, name ) ) )
				throw new IOException( "An output mod folder already exists: " + name );
		}

		foreach ( var output in manifest.Outputs )
			if ( File.Exists( MergeBuilder.SafePath( modsRoot, output.Path ) ) || Directory.Exists( MergeBuilder.SafePath( modsRoot, output.Path ) ) )
				throw new IOException( "A merged output already exists; preserve or remove its existing merge first: " + output.Path );

		if ( manifest.SharedLooseFolder && inventory is null )
			throw new InvalidOperationException( "Shared output requires its normal merge inventory update." );

		MergeSidecarState? sidecar = null;

		if ( inventory is not null )
		{
			string path = Path.GetFullPath( inventory.Path );
			var input = manifest.Inputs.SingleOrDefault( input => input.Path.Equals( path, StringComparison.OrdinalIgnoreCase ) && input.SuppressedPath is null );

			if ( input is null || input.Sha256 != ( inventory.ExpectedHash ?? "" ) )
				throw new IOException( "The merge inventory was not part of the reviewed inputs." );

			byte[]? original = File.Exists( path ) ? File.ReadAllBytes( path ) : null;

			if ( ( original is null ? null : Hash( original ) ) != inventory.ExpectedHash )
				throw new IOException( "The merge inventory changed; rebuild the candidate." );

			sidecar = new( path, original, UsesInventory( stateFile ) ? InventoryDigest( inventory.Content ) : Hash( inventory.Content ) );
		}

		byte[]? previous = settingsPath is not null && File.Exists( settingsPath ) ? File.ReadAllBytes( settingsPath ) : null;
		string? previousHash = previous is null ? null : Hash( previous );

		if ( manifest.SettingsSha256 != previousHash )
			throw new IOException( "Load order changed; rebuild the candidate." );

		if ( manifest.CompiledPriority is not null && ( settingsPath is null || updatedSettings is null ) )
			throw new InvalidOperationException( "Compiled output requires the reviewed load-order update." );

		byte[]? updated = updatedSettings is not null ? new UTF8Encoding( false ).GetBytes( updatedSettings ) : null;
		var applied = new AppliedBlobMerge( manifest, previous, settingsPath is not null ? updated is not null ? Hash( updated ) : previousHash : null,
			settingsPath is null ? null : Path.GetFullPath( settingsPath ), sidecar, MissingOutputDirectories( manifest ),
			PreserveWorkspace( workspaceDirectory, stateFile, manifest.Id ), InventoryEmbedded: UsesInventory( stateFile ) );
		Directory.CreateDirectory( Path.GetDirectoryName( stateFile )! );
		// Save the journal before changing installed files so an interrupted apply can be recovered.
		WriteAtomic( journal, JsonSerializer.SerializeToUtf8Bytes( applied ), overwrite: false );
		var moved = new List<MergeInputFile>();
		var copied = new List<string>();
		bool settingsChanged = false;
		bool inventoryChanged = false;

		try
		{
			foreach ( var input in Suppressed( manifest ) )
			{
				if ( MergeBuilder.Hash( input.Path ) != input.Sha256 )
					throw new IOException( "Source changed during apply: " + input.Path );

				File.Move( input.Path, input.SuppressedPath! );
				moved.Add( input );
			}

			foreach ( var output in manifest.Outputs )
			{
				string target = MergeBuilder.SafePath( modsRoot, output.Path );
				Directory.CreateDirectory( Path.GetDirectoryName( target )! );
				string temporary = target + ".smr-writing-" + manifest.Id;
				using ( var source = File.OpenRead( MergeBuilder.SafePath( candidate, output.Path ) ) )
				using ( var destination = new FileStream( temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None ) )
				{
					source.CopyTo( destination );
					destination.Flush( true );
				}

				if ( MergeBuilder.Hash( temporary ) != output.Sha256 )
					throw new IOException( "Candidate output changed during apply." );

				File.Move( temporary, target );
				copied.Add( target );
			}

			if ( settingsPath is not null && updated is not null )
			{
				if ( ( File.Exists( settingsPath ) ? MergeBuilder.Hash( settingsPath ) : null ) != previousHash )
					throw new IOException( "Load order changed during apply." );

				Directory.CreateDirectory( Path.GetDirectoryName( settingsPath )! );
				WriteAtomic( settingsPath, updated );
				settingsChanged = true;
			}

			if ( sidecar is not null )
			{
				if ( ( File.Exists( sidecar.Path ) ? MergeBuilder.Hash( sidecar.Path ) : null ) != inventory!.ExpectedHash )
					throw new IOException( "The merge inventory changed during apply." );

				Directory.CreateDirectory( Path.GetDirectoryName( sidecar.Path )! );
				WriteAtomic( sidecar.Path, UsesInventory( stateFile ) ? SetInventoryState( inventory.Content, applied ) : inventory.Content );
				inventoryChanged = true;
			}

			if ( !UsesInventory( stateFile ) )
				WriteAtomic( stateFile, JsonSerializer.SerializeToUtf8Bytes( applied, new JsonSerializerOptions { WriteIndented = true } ), overwrite: false );
		}
		catch
		{
			// Undo only the changes made by this attempt, in reverse order.
			foreach ( string path in copied.AsEnumerable().Reverse() )
				File.Delete( path );

			foreach ( var output in manifest.Outputs )
			{
				string temporary = MergeBuilder.SafePath( modsRoot, output.Path ) + ".smr-writing-" + manifest.Id;

				if ( File.Exists( temporary ) )
					File.Delete( temporary );
			}

			foreach ( var input in moved.AsEnumerable().Reverse() )
				File.Move( input.SuppressedPath!, input.Path );

			if ( settingsChanged )
				RestoreSettings( applied, settingsPath );

			if ( inventoryChanged )
				RestoreInventory( applied, null );

			RemoveEmptyOutputs( manifest, applied.CreatedDirectories );
			File.Delete( journal );
			CleanupWorkspace( applied, stateFile );
			throw;
		}

		// The saved state proves apply finished. Recovery can remove a leftover journal later.
		try
		{
			File.Delete( journal );
		}
		catch ( IOException )
		{
		}
		catch ( UnauthorizedAccessException )
		{
		}

		return applied;
	}

	// Checking vanilla may regenerate its cached file hashes. Installed sources and outputs are only read.
	public static IReadOnlyList<string> Inspect(
		AppliedBlobMerge applied,
		string? expectedModsRoot = null )
	{
		var manifest = applied.Manifest;
		Validate( manifest );
		CheckRoot( manifest, expectedModsRoot );
		var issues = new List<string>();

		if ( manifest.VanillaBaseline is { } baseline && !VanillaBaselineStore.Matches( baseline, applied.Inventory?.Path ) )
			issues.Add( "Vanilla scripts changed or are missing; rescan before rebuilding the merge." );

		foreach ( var input in Suppressed( manifest ) )
		{
			if ( File.Exists( input.Path ) )
				issues.Add( "Updated source is active: " + input.Path );

			if ( !Matches( input.SuppressedPath!, input.Sha256 ) )
				issues.Add( "Managed source backup changed or is missing: " + input.SuppressedPath );
		}

		foreach ( string mod in manifest.Mods )
		{
			string content = MergeBuilder.SafePath( manifest.ModsRoot, mod + "/content" );

			if ( !Directory.Exists( content ) )
				continue;

			var expected = ScriptInputs( manifest, content );

			if ( !expected.Values.Any( input => Path.GetFileName( input.Path ).Equals( "precompiled.rsblob", StringComparison.OrdinalIgnoreCase ) ) )
				continue;

			foreach ( string path in FilesWithoutLinks( content ).Where( IsScript ) )
				if ( !expected.ContainsKey( path ) )
					issues.Add( "New source needs remerging: " + path );
		}

		foreach ( var output in manifest.Outputs )
		{
			string path = MergeBuilder.SafePath( manifest.ModsRoot, output.Path );

			if ( !Matches( path, output.Sha256 ) )
				issues.Add( "Merged output changed or is missing: " + path );
		}

		foreach ( var input in manifest.Inputs.Where( input => input.SuppressedPath is null
			&& !input.Path.Equals( applied.SettingsPath, StringComparison.OrdinalIgnoreCase )
			&& !input.Path.Equals( applied.Inventory?.Path, StringComparison.OrdinalIgnoreCase ) ) )
			if ( input.Sha256.Length == 0 ? File.Exists( input.Path ) : !Matches( input.Path, input.Sha256 ) )
				issues.Add( "Reference input changed or is missing: " + input.Path );

		if ( applied.SettingsPath is not null && applied.AppliedSettingsHash is not null && !Matches( applied.SettingsPath, applied.AppliedSettingsHash ) )
			issues.Add( "Load order changed; review the managed merge." );

		return issues;
	}

	// A pending journal with matching saved state means apply finished. Otherwise recover its unfinished changes.
	// Conflicting saved state requires intervention; it cannot establish which operation owns the files.
	public static void RecoverInterrupted(
		string stateFile,
		string? settingsPath,
		string? expectedModsRoot = null,
		Func<byte[]?, string, byte[]?>? reconcileInventory = null,
		string? expectedInventoryPath = null )
	{
		if ( File.Exists( stateFile + ".update" ) )
		{
			RecoverUpdate( stateFile, settingsPath, expectedModsRoot, expectedInventoryPath );

			if ( !File.Exists( stateFile + ".pending" ) )
				return;
		}

		string journal = stateFile + ".pending";
		var pending = Read( journal );
		CheckRoot( pending.Manifest, expectedModsRoot );
		CheckInventory( pending, expectedInventoryPath );

		if ( HasState( stateFile ) )
		{
			if ( Read( stateFile ).Manifest.Id != pending.Manifest.Id )
				throw new IOException( "Interrupted and active merges do not match." );

			File.Delete( journal );

			return;
		}

		RestoreState( pending, journal, settingsPath, interrupted: true, reconcileInventory );
	}

	// Backups from removedMods must also be removed, or restoration would reinstall those mods.
	public static void Restore(
		string stateFile,
		string? settingsPath,
		string? expectedModsRoot = null,
		Func<byte[]?, string, byte[]?>? reconcileInventory = null,
		string? expectedInventoryPath = null,
		IReadOnlySet<string>? removedMods = null,
		IReadOnlyDictionary<string, string>? approvedEditedOutputs = null )
	{
		var applied = Read( stateFile );
		CheckRoot( applied.Manifest, expectedModsRoot );
		CheckInventory( applied, expectedInventoryPath );
		RestoreState( applied, stateFile, settingsPath, interrupted: false, reconcileInventory, removedMods, approvedEditedOutputs );
	}

	// Do not restore old backups over updated mods. Validate backups and the inventory before changing files;
	// edited outputs require approval for their current contents.
	private static void RestoreState(
		AppliedBlobMerge applied,
		string stateFile,
		string? settingsPath,
		bool interrupted,
		Func<byte[]?, string, byte[]?>? reconcileInventory,
		IReadOnlySet<string>? removedMods = null,
		IReadOnlyDictionary<string, string>? approvedEditedOutputs = null )
	{
		var manifest = applied.Manifest;
		Validate( manifest );
		// Source folders may have been renamed since the merge, usually by disabling them with a prefix.
		var sourceFolders = MergeSourceLocations.Find( manifest );
		string Locate( string path ) => MergeSourceLocations.PathFor( manifest.ModsRoot, path, sourceFolders );

		if ( applied.SettingsPath is not null && !string.Equals( applied.SettingsPath, settingsPath is null ? null : Path.GetFullPath( settingsPath ), StringComparison.OrdinalIgnoreCase ) )
			throw new IOException( "The saved merge belongs to a different game Documents folder." );

		var updatedMods = new HashSet<string>( removedMods ?? new HashSet<string>(), StringComparer.OrdinalIgnoreCase );

		foreach ( string mod in manifest.Mods )
		{
			string content = MergeBuilder.SafePath( manifest.ModsRoot, mod + "/content" );
			string currentContent = Locate( content );

			if ( !Directory.Exists( currentContent ) )
			{
				updatedMods.Add( mod );
				continue;
			}

			var expected = ScriptInputs( manifest, content );
			bool wholeBlob = expected.Values.Any( input => Path.GetFileName( input.Path ).Equals( "precompiled.rsblob", StringComparison.OrdinalIgnoreCase ) );

			foreach ( string path in FilesWithoutLinks( currentContent ).Where( IsScript ) )
			{
				string originalPath = Path.Combine( content, Path.GetRelativePath( currentContent, path ) );

				if ( !expected.TryGetValue( originalPath, out var original ) )
				{
					if ( wholeBlob )
						updatedMods.Add( mod );

					continue;
				}

				if ( !Matches( path, original.Sha256 )
					|| original.SuppressedPath is not null && File.Exists( Locate( original.SuppressedPath ) ) )
					updatedMods.Add( mod );
			}

			if ( expected.Values.Any( input => input.SuppressedPath is null && !Matches( Locate( input.Path ), input.Sha256 ) ) )
				updatedMods.Add( mod );

			if ( expected.Values.Any( input => input.SuppressedPath is not null && !File.Exists( Locate( input.Path ) )
				&& !File.Exists( Locate( input.SuppressedPath ) ) ) )
				updatedMods.Add( mod );
		}

		foreach ( var input in Suppressed( manifest ) )
		{
			string current = Locate( input.Path ), suppressed = Locate( input.SuppressedPath! );

			if ( File.Exists( suppressed ) && !Matches( suppressed, input.Sha256 ) )
				throw new IOException( "Source backup changed; automatic restore is unsafe: " + suppressed );
		}

		var editedOutputs = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );

		foreach ( var output in manifest.Outputs )
		{
			string path = MergeBuilder.SafePath( manifest.ModsRoot, output.Path );

			if ( !File.Exists( path ) )
				continue;

			string hash = MergeBuilder.Hash( path );

			if ( hash == output.Sha256 )
				continue;

			if ( approvedEditedOutputs is not null
				&& approvedEditedOutputs.TryGetValue( path, out var approvedHash ) && hash == approvedHash )
				continue;

			editedOutputs.Add( path, hash );
		}

		if ( editedOutputs.Count > 0 )
			throw new EditedMergeOutputsException( editedOutputs );

		foreach ( var original in applied.RestoredOutputs ?? [] )
			if ( !Matches( MergeBuilder.SafePath( StorageDirectory( stateFile ), original.StoragePath ), original.Sha256 ) )
				throw new IOException( "An adopted merge recovery copy is missing or changed." );

		// Resolve inventory conflicts before making any file changes.
		var inventoryRestore = PrepareInventoryRestore( applied, reconcileInventory );

		foreach ( var input in Suppressed( manifest ) )
		{
			string target = Locate( input.Path ), suppressed = Locate( input.SuppressedPath! );

			if ( !File.Exists( suppressed ) )
				continue;

			string mod = Path.GetRelativePath( manifest.ModsRoot, input.Path ).Split( Path.DirectorySeparatorChar )[0];

			if ( File.Exists( target ) || updatedMods.Contains( mod ) )
			{
				File.Delete( suppressed );
			}
			else
			{
				Directory.CreateDirectory( Path.GetDirectoryName( target )! );
				File.Move( suppressed, target );
			}
		}

		foreach ( var output in manifest.Outputs )
		{
			string path = MergeBuilder.SafePath( manifest.ModsRoot, output.Path );
			var original = applied.RestoredOutputs?.SingleOrDefault( original => original.Path.Equals( output.Path, StringComparison.OrdinalIgnoreCase ) );

			if ( original is not null )
			{
				string source = MergeBuilder.SafePath( StorageDirectory( stateFile ), original.StoragePath );

				if ( !Matches( source, original.Sha256 ) )
					throw new IOException( "An adopted merge recovery copy is missing or changed." );

				Directory.CreateDirectory( Path.GetDirectoryName( path )! );
				WriteAtomic( path, File.ReadAllBytes( source ) );
			}
			else if ( File.Exists( path ) )
				File.Delete( path );

			string temporary = path + ".smr-writing-" + manifest.Id;

			if ( File.Exists( temporary ) )
				File.Delete( temporary );
		}

		RemoveEmptyOutputs( manifest, applied.CreatedDirectories );
		RestoreSettings( applied, settingsPath );
		WriteInventoryRestore( applied, inventoryRestore );

		if ( !UsesInventory( stateFile ) && File.Exists( stateFile ) )
			File.Delete( stateFile );

		CleanupWorkspace( applied, stateFile );
		CleanupAdoptedOutputs( applied, stateFile, [] );
	}

	// If the user has since edited mods.settings, remove only this merge's sections and preserve their other changes.
	private static void RestoreSettings(
		AppliedBlobMerge applied,
		string? settingsPath )
	{
		if ( settingsPath is null || applied.AppliedSettingsHash is null )
			return;

		if ( File.Exists( settingsPath ) && MergeBuilder.Hash( settingsPath ) == applied.AppliedSettingsHash )
		{
			if ( applied.PreviousSettings is null )
				File.Delete( settingsPath );
			else if ( MergeBuilder.Hash( settingsPath ) != Hash( applied.PreviousSettings ) )
				WriteAtomic( settingsPath, applied.PreviousSettings );
		}
		else if ( File.Exists( settingsPath ) )
		{
			string current = File.ReadAllText( settingsPath );
			string changed = current;

			foreach ( string mod in OwnedSettings( applied ) )
				changed = System.Text.RegularExpressions.Regex.Replace( changed,
					@"(?ms)^[ \t]*\[" + System.Text.RegularExpressions.Regex.Escape( mod ) + @"\][^\r\n]*(?:\r?\n|$).*?(?=^[ \t]*\[|\z)", "" );

			if ( changed != current )
				WriteAtomic( settingsPath, new UTF8Encoding( false ).GetBytes( changed ) );
		}
	}

	private static void RestoreInventory(
		AppliedBlobMerge applied,
		Func<byte[]?, string, byte[]?>? reconcile )
		=> WriteInventoryRestore( applied, PrepareInventoryRestore( applied, reconcile ) );

	private sealed record InventoryRestore( string? ExpectedHash, byte[]? Bytes );

	// Use the saved inventory only if it is still unchanged. Otherwise remove this merge from the current records
	// through the caller's reconciliation step, preserving other merges.
	private static InventoryRestore? PrepareInventoryRestore(
		AppliedBlobMerge applied,
		Func<byte[]?, string, byte[]?>? reconcile )
	{
		if ( applied.Inventory is not { } sidecar )
			return null;

		byte[]? current = File.Exists( sidecar.Path ) ? File.ReadAllBytes( sidecar.Path ) : null;
		string? hash = current is null ? null : Hash( current );
		bool embedded = applied.InventoryEmbedded && current is not null && InventoryDocument( current ).Root!.Element( InventoryStateElement ) is not null;
		byte[]? restored = ( hash == sidecar.AppliedHash || embedded && InventoryDigest( current! ) == sidecar.AppliedHash ) ? sidecar.Previous
			: hash == ( sidecar.Previous is null ? null : Hash( sidecar.Previous ) ) ? current
			: reconcile is not null ? reconcile( current, applied.Manifest.Id )
			: throw new IOException( "The normal merge inventory changed during rollback." );

		return new( hash, restored );
	}

	private static void WriteInventoryRestore(
		AppliedBlobMerge applied,
		InventoryRestore? restore )
	{
		if ( applied.Inventory is not { } sidecar || restore is null )
			return;

		string? hash = File.Exists( sidecar.Path ) ? MergeBuilder.Hash( sidecar.Path ) : null;

		if ( hash != restore.ExpectedHash )
			throw new IOException( "The normal merge inventory changed during restore." );

		if ( restore.Bytes is null )
		{
			if ( File.Exists( sidecar.Path ) )
				File.Delete( sidecar.Path );
		}
		else if ( hash != Hash( restore.Bytes ) )
			WriteAtomic( sidecar.Path, restore.Bytes );
	}

	private static AppliedBlobMerge Read(
		string path )
	{
		if ( new FileInfo( path ).Length > 16 * 1024 * 1024 )
			throw new InvalidDataException( "The managed merge state is too large." );

		var applied = UsesInventory( path ) ? RebindInventory( ReadInventoryState( File.ReadAllBytes( path ) ), path )
			: JsonSerializer.Deserialize<AppliedBlobMerge>( File.ReadAllText( path ) ) ?? throw new InvalidDataException( "Invalid managed merge state." );
		ValidateApplied( applied, path );

		return applied;
	}

	// Saved state must only refer to folders and recovery copies belonging to this merge.
	private static void ValidateApplied(
		AppliedBlobMerge applied,
		string path )
	{
		Validate( applied.Manifest );

		if ( applied.Manifest.SharedLooseFolder && ( applied.Inventory is null || applied.CreatedDirectories is null ) )
			throw new InvalidDataException( "Shared-folder recovery metadata is missing." );

		foreach ( string relative in applied.CreatedDirectories ?? [] )
		{
			MergeBuilder.SafePath( applied.Manifest.ModsRoot, relative );
			string prefix = relative.Replace( '\\', '/' ).TrimEnd( '/' ) + "/";

			if ( !applied.Manifest.Outputs.Any( output => output.Path.Replace( '\\', '/' ).StartsWith( prefix, StringComparison.OrdinalIgnoreCase ) ) )
				throw new InvalidDataException( "Unknown managed output directory." );
		}

		foreach ( var original in applied.RestoredOutputs ?? [] )
		{
			MergeBuilder.SafePath( StorageDirectory( path ), original.StoragePath );

			if ( !original.StoragePath.Replace( '\\', '/' ).StartsWith( "original-outputs-", StringComparison.Ordinal )
				|| !applied.Manifest.Outputs.Any( output => output.Path.Equals( original.Path, StringComparison.OrdinalIgnoreCase ) ) )
				throw new InvalidDataException( "Invalid adopted merge recovery metadata." );
		}
	}

	private static void CheckInventory(
		AppliedBlobMerge applied,
		string? expected )
	{
		if ( applied.Inventory is { } sidecar && ( expected is null
			|| !Path.GetFullPath( expected ).Equals( sidecar.Path, StringComparison.OrdinalIgnoreCase )
			|| !applied.Manifest.Inputs.Any( input => input.SuppressedPath is null && input.Path.Equals( sidecar.Path, StringComparison.OrdinalIgnoreCase ) ) ) )
			throw new InvalidDataException( "The saved merge belongs to a different merge inventory." );
	}

	private static void CheckRoot(
		BlobMergeManifest manifest,
		string? expected )
	{
		if ( expected is not null && !Path.GetFullPath( manifest.ModsRoot ).Equals( Path.GetFullPath( expected ), StringComparison.OrdinalIgnoreCase ) )
			throw new InvalidDataException( "The managed merge belongs to a different Mods folder." );
	}

	private static bool Matches( string path, string hash ) => File.Exists( path ) && MergeBuilder.Hash( path ) == hash;
	// A shared loose folder can belong to other merges too; leave its settings entry in place.
	private static IEnumerable<string> OwnedSettings( AppliedBlobMerge applied ) => ( applied.Manifest.SharedLooseFolder ? new[] { applied.Manifest.CompiledMod }
		: new[] { applied.Manifest.LooseMod, applied.Manifest.CompiledMod } ).Where( mod => applied.Manifest.Outputs.Any( output => output.Path.Replace( '\\', '/' ).StartsWith( mod + "/", StringComparison.OrdinalIgnoreCase ) ) );

	private static string Hash( byte[] bytes ) => Convert.ToHexString( System.Security.Cryptography.SHA256.HashData( bytes ) );

	private static IEnumerable<MergeInputFile> Suppressed( BlobMergeManifest manifest ) => manifest.Inputs.Where( input => input.SuppressedPath is not null );

	private static bool IsScript( string path ) => path.EndsWith( ".ws", StringComparison.OrdinalIgnoreCase ) || Path.GetFileName( path ).Equals( "precompiled.rsblob", StringComparison.OrdinalIgnoreCase );

	private static Dictionary<string, MergeInputFile> ScriptInputs(
		BlobMergeManifest manifest,
		string content )
	{
		string root = Path.TrimEndingDirectorySeparator( Path.GetFullPath( content ) ) + Path.DirectorySeparatorChar;

		return manifest.Inputs.Where( input => IsScript( input.Path ) && Path.GetFullPath( input.Path ).StartsWith( root, StringComparison.OrdinalIgnoreCase ) )
			.ToDictionary( input => Path.GetFullPath( input.Path ), StringComparer.OrdinalIgnoreCase );
	}

	// Validate saved paths before using them for file operations. A manifest must stay within the merge's folders.
	private static void Validate(
		BlobMergeManifest manifest )
	{
		bool ModName( string name ) => name is not null && name.StartsWith( "mod", StringComparison.OrdinalIgnoreCase ) && name.Length is > 3 and < 128
			&& name.IndexOfAny( Path.GetInvalidFileNameChars() ) < 0 && !name.Contains( "[Mod.io", StringComparison.OrdinalIgnoreCase );

		if ( manifest is null || manifest.Schema != MergeBuilder.ManifestSchema
			|| manifest.Inputs is null || manifest.Outputs is null || manifest.Mods is null
			|| manifest.Outputs.Count == 0 || manifest.Mods.Count == 0
			|| string.IsNullOrEmpty( manifest.ModsRoot ) || !Path.IsPathFullyQualified( manifest.ModsRoot ) || !Guid.TryParseExact( manifest.Id, "N", out _ )
			|| !ModName( manifest.LooseMod ) || !ModName( manifest.CompiledMod )
			|| manifest.LooseMod.Equals( manifest.CompiledMod, StringComparison.OrdinalIgnoreCase ) || manifest.Mods.Any( name => !ModName( name ) ) )
			throw new InvalidDataException( "Invalid managed mod identity." );

		bool Digest( string? hash ) => hash is not null && System.Text.RegularExpressions.Regex.IsMatch( hash, @"\A[0-9A-F]{64}\z" );

		if ( manifest.VanillaBaseline is { } baseline
			&& ( baseline.Schema != 1 || baseline.Root is null || !Path.IsPathFullyQualified( baseline.Root ) || !Digest( baseline.Fingerprint ) || baseline.Count <= 0
				|| VanillaBaselineStore.Within( baseline.Root, manifest.ModsRoot ) || VanillaBaselineStore.Within( manifest.ModsRoot, baseline.Root ) ) )
			throw new InvalidDataException( "Invalid vanilla baseline identity." );

		if ( manifest.Inputs.Any( input => input is null || !Path.IsPathFullyQualified( input.Path ?? "" )
				|| !Digest( input.Sha256 ) && ( input.Sha256 != "" || input.SuppressedPath is not null ) )
			|| manifest.Outputs.Any( output => output is null || string.IsNullOrWhiteSpace( output.Path ) || !Digest( output.Sha256 ) ) )
			throw new InvalidDataException( "Invalid managed file identity." );

		if ( manifest.Inputs.GroupBy( input => Path.GetFullPath( input.Path ), StringComparer.OrdinalIgnoreCase ).Any( group => group.Count() > 1 )
			|| manifest.Outputs.GroupBy( output => output.Path, StringComparer.OrdinalIgnoreCase ).Any( group => group.Count() > 1 ) )
			throw new InvalidDataException( "Duplicate managed file identity." );

		foreach ( var input in Suppressed( manifest ) )
		{
			string relative = Path.GetRelativePath( manifest.ModsRoot, input.Path );
			MergeBuilder.SafePath( manifest.ModsRoot, relative );

			if ( !manifest.Mods.Contains( relative.Split( Path.DirectorySeparatorChar )[0], StringComparer.OrdinalIgnoreCase )
				|| !IsScript( input.Path ) || !string.Equals( input.Path + ".smr-disabled", input.SuppressedPath, StringComparison.OrdinalIgnoreCase ) )
				throw new InvalidDataException( "A managed source is outside the participating script folders." );

			MergeBuilder.SafePath( manifest.ModsRoot, Path.GetRelativePath( manifest.ModsRoot, input.SuppressedPath! ) );
		}

		if ( manifest.Inputs.Any( input => input.ReadPath is not null
			&& !input.ReadPath.Equals( input.Path + ".smr-disabled", StringComparison.OrdinalIgnoreCase ) ) )
			throw new InvalidDataException( "Invalid managed source read path." );

		foreach ( var input in manifest.Inputs.Where( input => input.ReadPath is not null ) )
			MergeBuilder.SafePath( manifest.ModsRoot, Path.GetRelativePath( manifest.ModsRoot, input.ReadPath! ) );

		foreach ( string mod in manifest.Mods )
		{
			var sources = ScriptInputs( manifest, MergeBuilder.SafePath( manifest.ModsRoot, mod + "/content" ) ).Values;
			bool blob = sources.Any( input => Path.GetFileName( input.Path ).Equals( "precompiled.rsblob", StringComparison.OrdinalIgnoreCase ) );

			if ( blob && sources.Where( input => input.SuppressedPath is null && input.Path.EndsWith( ".ws", StringComparison.OrdinalIgnoreCase ) )
				.Any( input => !manifest.Outputs.Any( output => output.Path.Replace( '\\', '/' ).Equals(
					manifest.LooseMod + "/content/scripts/" + Path.GetRelativePath( MergeBuilder.SafePath( manifest.ModsRoot, mod + "/content/scripts" ), input.Path ).Replace( '\\', '/' ),
					StringComparison.OrdinalIgnoreCase ) ) ) )
				throw new InvalidDataException( "An adjacent loose copy must be suppressed or overridden by a merged file." );
		}

		foreach ( var output in manifest.Outputs )
		{
			string folder = output.Path.Replace( '\\', '/' ).Split( '/' )[0];

			if ( folder != manifest.LooseMod && folder != manifest.CompiledMod || !IsScript( output.Path ) )
				throw new InvalidDataException( "Unknown output mod folder or file type." );

			string relative = output.Path.Replace( '\\', '/' );

			if ( relative.EndsWith( ".rsblob", StringComparison.OrdinalIgnoreCase ) ? !relative.Equals( manifest.CompiledMod + "/content/precompiled.rsblob", StringComparison.OrdinalIgnoreCase )
				: !relative.StartsWith( manifest.LooseMod + "/content/scripts/", StringComparison.OrdinalIgnoreCase ) )
				throw new InvalidDataException( "Compiled and loose merged scripts must use separate mod folders." );

			if ( output.Mods?.Any( mod => !manifest.Mods.Contains( mod, StringComparer.OrdinalIgnoreCase ) ) == true )
				throw new InvalidDataException( "Unknown merged source owner." );

			string canonical = Path.GetRelativePath( manifest.ModsRoot, MergeBuilder.SafePath( manifest.ModsRoot, output.Path ) ).Replace( '\\', '/' );

			if ( !canonical.Equals( relative, StringComparison.OrdinalIgnoreCase ) )
				throw new InvalidDataException( "A managed output path is not canonical." );
		}
	}

	private static void WriteNew(
		string path,
		byte[] bytes )
	{
		using var stream = new FileStream( path, FileMode.CreateNew, FileAccess.Write, FileShare.None );
		stream.Write( bytes );
		stream.Flush( true );
	}
	// Write beside the destination before renaming into place. This keeps a failed write from damaging the previous file.
	private static void WriteAtomic(
		string path,
		byte[] bytes,
		bool overwrite = true )
	{
		string temporary = path + ".smr-writing-" + Guid.NewGuid().ToString( "N" );

		try
		{
			WriteNew( temporary, bytes );
			File.Move( temporary, path, overwrite );
		}
		finally
		{
			if ( File.Exists( temporary ) )
				File.Delete( temporary );
		}
	}

	// Refuse links so traversal stays inside the managed folder.
	private static IEnumerable<string> FilesWithoutLinks(
		string directory )
	{
		foreach ( string entry in Directory.EnumerateFileSystemEntries( directory ) )
		{
			var attributes = File.GetAttributes( entry );

			if ( ( attributes & FileAttributes.ReparsePoint ) != 0 )
				throw new IOException( "Managed paths cannot traverse links: " + entry );

			if ( ( attributes & FileAttributes.Directory ) == 0 )
				yield return entry;
			else
				foreach ( string file in FilesWithoutLinks( entry ) )
					yield return file;
		}
	}

	private static IReadOnlyList<string> MissingOutputDirectories(
		BlobMergeManifest manifest )
	{
		var result = new HashSet<string>( StringComparer.OrdinalIgnoreCase );

		foreach ( var output in manifest.Outputs )
			for ( string? path = Path.GetDirectoryName( MergeBuilder.SafePath( manifest.ModsRoot, output.Path ) );
				path is not null && path.Length > manifest.ModsRoot.Length && !Directory.Exists( path ); path = Path.GetDirectoryName( path ) )
				result.Add( Path.GetRelativePath( manifest.ModsRoot, path ) );

		return result.OrderByDescending( path => path.Length ).ToArray();
	}
	// A shared loose folder may contain other merges. Only prune folders this merge created.
	private static void RemoveEmptyOutputs(
		BlobMergeManifest manifest,
		IReadOnlyList<string>? created = null )
	{
		if ( manifest.SharedLooseFolder )
		{
			foreach ( string relative in ( created ?? [] ).OrderByDescending( path => path.Length ) )
			{
				string path = MergeBuilder.SafePath( manifest.ModsRoot, relative );

				if ( Directory.Exists( path ) && !Directory.EnumerateFileSystemEntries( path ).Any() )
					Directory.Delete( path );
			}

			return;
		}

		foreach ( string name in new[] { manifest.LooseMod, manifest.CompiledMod } )
			Prune( MergeBuilder.SafePath( manifest.ModsRoot, name ) );

		static void Prune(
			string directory )
		{
			if ( !Directory.Exists( directory ) )
				return;

			if ( ( File.GetAttributes( directory ) & FileAttributes.ReparsePoint ) != 0 )
				return;

			foreach ( string child in Directory.EnumerateDirectories( directory ) )
				Prune( child );

			if ( !Directory.EnumerateFileSystemEntries( directory ).Any() )
				Directory.Delete( directory );
		}
	}
}
