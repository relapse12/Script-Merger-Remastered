#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WitcherScriptMerger.BlobMerging;
using WitcherScriptMerger.Inventory;
using WitcherScriptMerger.LoadOrder;
using WitcherScriptMerger.Tools;
using WitcherScriptMerger.Merging;

namespace WitcherScriptMerger;

// Checks saved merges against current files and applies the user's decisions. Restart when that changes the inventory.
internal sealed class MergeReconciler
{
	private const string NoLoadOrderFile = "no file";

	private static readonly IReadOnlySet<string> NoMods = new HashSet<string>();

	private readonly MergeSession session;
	private readonly IMergeReconcileHost host;

	// Avoid repeating the same cleanup prompt during this refresh, and keep the window open for unresolved cleanup.
	private string? retainedUnownedFiles;
	private string? unregisteredError;

	internal MergeReconciler( MergeSession session, IMergeReconcileHost host )
	{
		this.session = session;
		this.host = host;
	}

	internal bool KeptOutdatedMerges { get; private set; }

	internal IReadOnlyList<string> Issues { get; private set; } = Array.Empty<string>();

	internal IReadOnlyDictionary<string, string> MovedMergeFolders { get; private set; } = NoMovedFolders();

	// The caller clears this before an operation and checks it afterward to decide whether conflicts need rescanning.
	internal bool ManagedMergesChanged { get; set; }

	// A changed revision tells the caller to reread after unregistered outputs were removed.
	internal int UnregisteredRevision { get; private set; }

	internal bool HasRetainedUnownedFiles => retainedUnownedFiles is not null;

	// A removal can change the result of earlier checks. Restart from the beginning until a pass leaves the inventory unchanged.
	internal MergeReconcileResult Run()
	{
		while ( true )
		{
			MergeReconcileResult? result;

			if ( !PrepareInventory() )
				continue;

			host.MergeChecksStarting();
			result = CheckMerges();

			if ( result is not null )
				return result;

			host.MergeChecksRestarting();
		}
	}

	// A null result requests another pass after the inventory changed.
	private MergeReconcileResult? CheckMerges()
	{
		var sourceHashes = new Dictionary<string, string?>( StringComparer.OrdinalIgnoreCase );
		var keptMerges = new List<Merge>();
		var mergesToDelete = new List<Merge>();
		var prunedBundleMerges = new List<Merge>();
		var missingBundles = new Dictionary<string, bool>( StringComparer.OrdinalIgnoreCase );
		IReadOnlyList<IReadOnlyList<Merge>>? linkedOutputs;
		bool inventoryChanged = false;

		for ( int i = session.Inventory!.Merges.Count - 1; i >= 0; i-- )
		{
			Merge merge = session.Inventory.Merges[i];
			string mergedFile;
			bool delete;

			if ( ShouldPrune( merge, missingBundles ) )
			{
				session.Inventory.Merges.RemoveAt( i );
				inventoryChanged = true;

				if ( merge.IsBundleContent )
					prunedBundleMerges.Add( merge );

				continue;
			}

			delete = CheckSources( merge, sourceHashes );

			if ( delete && merge.IsBlobMerge )
			{
				if ( RemoveOutdatedBlobMerge( merge ) )
					return null;

				KeptOutdatedMerges = true;
				delete = false;
			}

			if ( delete )
			{
				mergesToDelete.Add( merge );

				continue;
			}

			mergedFile = merge.GetOutputFile( session.GamePaths );

			if ( merge.OutdatedReason.Length == 0 && IsOutputMissing( merge ) )
				merge.OutdatedReason = UiText.Format( "MergeTree", "Outdated: a merged output is missing: {0}", mergedFile );

			if ( merge.Mods.Any( source => source.IsOutdated ) || merge.OutdatedReason.Length > 0 )
				KeptOutdatedMerges = true;

			keptMerges.Add( merge );
		}

		KeepBlockedBundleRemovals( mergesToDelete, keptMerges );
		linkedOutputs = FindLinkedOutputs( mergesToDelete );

		if ( linkedOutputs is null )
			return null;

		return new MergeReconcileResult( keptMerges, linkedOutputs, mergesToDelete, prunedBundleMerges, inventoryChanged );
	}

	// Check the complete batch: deleting every merge in a bundle needs no repack. Keep blocked removals visible as outdated.
	private void KeepBlockedBundleRemovals( List<Merge> mergesToDelete, List<Merge> keptMerges )
	{
		BlockedBundleRemovals blocked = session.Inventory!.FindBlockedBundleRemovals( mergesToDelete, session.GamePaths );

		if ( blocked.Kept.Count == 0 )
			return;

		foreach ( Merge merge in blocked.Kept )
		{
			mergesToDelete.Remove( merge );
			keptMerges.Add( merge );
		}

		KeptOutdatedMerges = true;
		AddIssue( FileMerger.DescribeLostMerges( blocked.Unstaged ) );
	}

	// Disabled folders still contain their output. Preserve those records, and let saved blob state govern blob removals.
	private bool ShouldPrune( Merge merge, Dictionary<string, bool> missingBundles )
	{
		if ( merge.IsBlobMerge && session.HasManagedBlobState() )
			return false;

		if ( !IsOutputMissing( merge ) )
			return false;

		if ( host.AutoDeleteOldMerges )
			return true;

		if ( !merge.IsBundleContent )
			return host.ConfirmPruneMissingOutput( merge );

		string bundle = merge.GetMergedBundle( session.GamePaths );

		if ( !missingBundles.TryGetValue( bundle, out bool remove ) )
			missingBundles[bundle] = remove = host.ConfirmPruneMissingOutput( merge );

		return remove;
	}

	// Check the installed bundle for bundled merges. Missing staging text says nothing about whether the installed output exists.
	private bool IsOutputMissing( Merge merge )
	{
		string output = merge.GetOutputFile( session.GamePaths );

		if ( File.Exists( output ) )
			return false;

		try
		{
			return !MergeOutputFolder.IsRenamed( merge, session.GamePaths ) && !CannotConfirmMissing( output );
		}
		catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException )
		{
			ReportReadFailure( output, error.Message );

			return false;
		}
	}

	// File.Exists hides access failures. Confirm a file is missing before removing its record;
	// an inaccessible Mods folder must leave the records intact.
	private bool CannotConfirmMissing( string path )
	{
		string mods = session.GamePaths.ModsDirectory;

		if ( !Directory.Exists( mods ) )
		{
			ReportReadFailure( path, UiText.Format( "MergeTree", "The Mods folder could not be found: {0}", mods ) );

			return true;
		}

		try
		{
			return !PathAbsence.IsConfirmed( path ) && !Directory.Exists( path );
		}
		catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException )
		{
			ReportReadFailure( path, error.Message );

			return true;
		}
	}

	private void ReportReadFailure( string path, string detail )
	{
		AddIssue( UiText.Format( "MergeTree", "A merge was kept because {0} could not be read. {1}", path, detail ) );
		KeptOutdatedMerges = true;
	}

	// A read failure leaves the source unverified; it does not prove the source changed.
	private string? HashSource( string path )
	{
		try
		{
			return Hasher.ComputeHash( path );
		}
		catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException )
		{
			ReportReadFailure( path, error.Message );

			return null;
		}
	}

	// Stop at the first problem for a blob merge so later sources do not prompt for the same removal again.
	private bool CheckSources( Merge merge, Dictionary<string, string?> sourceHashes )
	{
		bool ShouldCheck( FileHash source )
		{
			return !merge.IsBlobMerge || merge.OutdatedReason.Length == 0 && !IsDisabledCopy( source );
		}

		foreach ( FileHash source in merge.Mods.Where( ShouldCheck ) )
		{
			string sourcePath = SourcePath( merge, source );

			foreach ( BlobMergeChangeKind kind in SourceProblems( source, sourcePath, sourceHashes ) )
			{
				BlobMergeChange change = new( kind, source.Name, sourcePath, NoMods );

				source.IsOutdated = true;
				merge.OutdatedReason = BlobMergeLifecycle.Describe( change );

				if ( ShouldRemove( change, merge ) )
					return true;
			}
		}

		return false;
	}

	private static bool IsDisabledCopy( FileHash source )
	{
		return source.SourceFilePath?.EndsWith( ".smr-disabled", StringComparison.OrdinalIgnoreCase ) == true;
	}

	private string SourcePath( Merge merge, FileHash source )
	{
		return source.SourceFilePath ?? merge.GetModFile( source.Name, session.GamePaths );
	}

	// Read failures preserve the merge and are reported separately from outdated sources.
	private IEnumerable<BlobMergeChangeKind> SourceProblems(
		FileHash source,
		string sourcePath,
		Dictionary<string, string?> sourceHashes )
	{
		if ( !File.Exists( sourcePath ) )
		{
			if ( !CannotConfirmMissing( sourcePath ) )
				yield return BlobMergeChangeKind.MissingSource;

			yield break;
		}

		// Only current, successfully read settings can establish that a source was disabled.
		if ( session.LoadOrder!.IsValid && session.LoadOrder.GetModLoadSettingByName( source.Name )?.IsEnabled == false )
			yield return BlobMergeChangeKind.DisabledSource;

		// Several merges can use one source; hash it once per pass.
		if ( !sourceHashes.TryGetValue( sourcePath, out string? latestHash ) )
			sourceHashes.Add( sourcePath, latestHash = HashSource( sourcePath ) );

		if ( latestHash != null && source.Hash != latestHash )
			yield return BlobMergeChangeKind.ChangedSource;
	}

	private bool RemoveOutdatedBlobMerge( Merge merge )
	{
		try
		{
			return RemoveBlobMerges( new[] { merge } );
		}
		catch ( Exception error ) when ( IsRecoveryFailure( error ) )
		{
			merge.OutdatedReason = RecoveryFailureReason( error );
			AddIssue( merge.OutdatedReason );

			return false;
		}
	}

	private static bool IsRecoveryFailure( Exception error )
	{
		return error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException
			or System.Text.Json.JsonException;
	}

	// Access failures leave recovery data unverified, so it must be preserved.
	private static bool IsReadFailure( Exception error )
	{
		const int SharingViolation = unchecked( (int)0x80070020 );
		const int LockViolation = unchecked( (int)0x80070021 );

		return error is UnauthorizedAccessException || error is IOException && error.HResult is SharingViolation or LockViolation;
	}

	private static string RecoveryFailureReason( Exception error )
	{
		if ( error is OperationCanceledException )
			return UiText.T( "MergeTree", "Outdated: merge removal was canceled." );

		return UiText.Format(
			"MergeTree",
			"Outdated: merge recovery could not be verified: {0}",
			BlobMergeLifecycle.RecoveryMessage( error ) );
	}

	private bool RemoveBlobMerges( IReadOnlyList<Merge> merges )
	{
		Action? removal = host.PrepareBlobRemoval( merges, null );

		if ( removal is null )
			return false;

		removal();
		host.ReloadAfterBlobChange();

		return true;
	}

	// Restart after automatic removal changes the inventory. Otherwise retain records whose recovery state is damaged.
	private IReadOnlyList<IReadOnlyList<Merge>>? FindLinkedOutputs( List<Merge> mergesToDelete )
	{
		try
		{
			return BlobMergeRemoval.LinkedOutputs(
				BlobMergeStore.ForApplication().InventoryPath,
				session.GamePaths.ModsDirectory,
				session.Inventory!.Merges.Except( mergesToDelete ) );
		}
		catch ( Exception error ) when ( IsRecoveryFailure( error ) )
		{
			Merge[] entries = session.Inventory!.Merges.Where( merge => merge.IsBlobMerge && !mergesToDelete.Contains( merge ) ).ToArray();
			string reason = UiText.Format( "MergeTree", "Merge recovery information is incomplete: {0}", error.Message );

			if ( entries.Length > 0 && host.AutoDeleteOldMerges && !IsReadFailure( error ) )
			{
				try
				{
					if ( RemoveBlobMerges( entries ) )
						return null;
				}
				catch ( Exception recovery ) when ( recovery is IOException or UnauthorizedAccessException or InvalidOperationException
					or System.Text.Json.JsonException )
				{
					reason = UiText.Format(
						"MergeTree",
						"The merge could not be removed safely: {0}",
						BlobMergeLifecycle.RecoveryMessage( recovery ) );
				}
			}

			foreach ( Merge entry in entries )
				entry.OutdatedReason = reason;

			AddIssue( reason );
			KeptOutdatedMerges = true;

			return entries.Length == 0 ? Array.Empty<IReadOnlyList<Merge>>() : new IReadOnlyList<Merge>[] { entries };
		}
	}

	private bool PrepareInventory()
	{
		int revision;

		ResetPass();
		RecoverInterruptedUpdate();
		RefreshManagedSources();
		revision = UnregisteredRevision;

		if ( !CheckUnregisteredOutputs( inventory: session.Inventory ) )
			KeptOutdatedMerges = true;

		return revision == UnregisteredRevision;
	}

	private void AddIssue( string issue )
	{
		Issues = Issues.Concat( new[] { issue } ).Distinct().ToArray();
	}

	private void ResetPass()
	{
		KeptOutdatedMerges = false;
		Issues = Array.Empty<string>();
		MovedMergeFolders = NoMovedFolders();

		foreach ( Merge merge in session.Inventory!.Merges )
		{
			merge.OutdatedReason = "";

			foreach ( FileHash source in merge.Mods )
				source.IsOutdated = false;
		}
	}

	private static Dictionary<string, string> NoMovedFolders()
	{
		return new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
	}

	internal static bool IsGameRunning()
	{
		return System.Diagnostics.Process.GetProcessesByName( "witcher3" ).Length > 0;
	}

	// Defer interrupted-update recovery while the game is running.
	private void RecoverInterruptedUpdate()
	{
		BlobMergeStore store;

		if ( session.LoadOrder is null || !Directory.Exists( session.GamePaths.ModsDirectory ) )
			return;

		store = BlobMergeStore.ForApplication();

		if ( !store.HasInterruptedUpdate )
			return;

		if ( IsGameRunning() )
			return;

		try
		{
			store.RecoverInterrupted( session.LoadOrder.FilePath, session.GamePaths.ModsDirectory );
			host.ReloadAfterBlobChange();
		}
		catch ( Exception error ) when ( error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException
			or InvalidOperationException or System.Text.Json.JsonException )
		{
			KeptOutdatedMerges = true;
			host.ShowError( error.Message );
		}
	}

	// Remove outdated inputs together so rebuilding the remaining merge cannot reuse another outdated source.
	private void RefreshManagedSources()
	{
		if ( session.LoadOrder is null || !Directory.Exists( session.GamePaths.ModsDirectory ) )
			return;

		try
		{
			string state = BlobMergeStore.ForApplication().InventoryPath;
			string mods = session.GamePaths.ModsDirectory;
			ModLoadSetting[] settings = session.LoadOrder.IsValid ? session.LoadOrder.Mods.ToArray() : Array.Empty<ModLoadSetting>();
			IReadOnlyList<BlobMergeChange> changes = FindBlobChanges( state, mods, settings );

			MovedMergeFolders = changes.Where( change => change.CurrentName != null )
				.ToDictionary( change => change.Mod, change => change.CurrentName!, StringComparer.OrdinalIgnoreCase );

			if ( changes.Count == 0 )
				return;

			MarkChangedMerges( changes, state, mods );
			OfferRemoval( changes, () => FindBlobChanges( state, mods, settings ) );
		}
		catch ( Exception error ) when ( IsRecoveryFailure( error ) )
		{
			string reason = RecoveryFailureReason( error );

			KeptOutdatedMerges = true;
			AddIssue( reason );

			foreach ( Merge entry in session.Inventory!.Merges.Where( merge => merge.IsBlobMerge ) )
				entry.OutdatedReason = reason;
		}
	}

	private IReadOnlyList<BlobMergeChange> FindBlobChanges( string state, string mods, ModLoadSetting[] settings )
	{
		List<BlobMergeChange> changes = BlobMergeLifecycle.FindChanges( state, mods, settings ).ToList();

		if ( !BlobMergeTransaction.HasState( state ) )
			return changes;

		var sourceHashes = new Dictionary<string, string?>( StringComparer.OrdinalIgnoreCase );
		Merge[] unaffected = session.Inventory!.Merges
			.Where( merge => merge.IsBlobMerge && !changes.Any( change => IsAffectedBy( merge, change ) ) ).ToArray();

		changes.AddRange( unaffected.Select( merge => FirstSourceChange( merge, sourceHashes ) ).OfType<BlobMergeChange>() );

		return changes;
	}

	// Limit this change to the affected output. The same mod can contribute to unrelated merges.
	private BlobMergeChange? FirstSourceChange( Merge merge, Dictionary<string, string?> sourceHashes )
	{
		foreach ( FileHash source in merge.Mods.Where( source => !IsDisabledCopy( source ) ) )
		{
			string sourcePath = SourcePath( merge, source );

			foreach ( BlobMergeChangeKind kind in SourceProblems( source, sourcePath, sourceHashes ) )
				return new BlobMergeChange( kind, source.Name, sourcePath, NoMods, Output: BlobMergeRemoval.OutputPath( merge ) );
		}

		return null;
	}

	private void MarkChangedMerges( IReadOnlyList<BlobMergeChange> changes, string state, string mods )
	{
		Merge[] blobMerges = session.Inventory!.Merges.Where( merge => merge.IsBlobMerge ).ToArray();
		IReadOnlyList<IReadOnlyList<Merge>> groups = BlobMergeRemoval.LinkedOutputs( state, mods, blobMerges );

		foreach ( BlobMergeChange change in changes )
		{
			Merge[] affected = blobMerges.Where( merge => IsAffectedBy( merge, change ) ).ToArray();

			foreach ( Merge merge in groups.Where( group => group.Any( affected.Contains ) ).SelectMany( group => group ) )
				merge.OutdatedReason = BlobMergeLifecycle.Describe( change );
		}
	}

	private static bool IsAffectedBy( Merge merge, BlobMergeChange change )
	{
		return change.Output is null ? UsesSource( merge, change ) : ProducedOutput( merge, change );
	}

	private static bool ProducedOutput( Merge merge, BlobMergeChange change )
	{
		return change.Output?.Replace( '\\', '/' )
			.Equals( BlobMergeRemoval.OutputPath( merge ), StringComparison.OrdinalIgnoreCase ) == true;
	}

	private static bool UsesSource( Merge merge, BlobMergeChange change )
	{
		return merge.Mods.Any( source => source.Name.Equals( change.Mod, StringComparison.OrdinalIgnoreCase ) );
	}

	// Use the complete removal selection to decide whether to restore everything or rebuild the remaining merge.
	private void OfferRemoval( IReadOnlyList<BlobMergeChange> changes, Func<IReadOnlyList<BlobMergeChange>> findChanges )
	{
		Merge? merge = FirstAffectedBlobMerge( changes[0] );
		BlobRemovalScope scope = BlobRemovalScope.Of( changes );
		string? loadOrder = host.AutoDeleteOldMerges ? null : LoadOrderContent();
		Action? removal = null;

		if ( merge is null )
			return;

		foreach ( BlobMergeChange change in changes )
			MarkSourceOutdated( change );

		if ( ShouldRemove( changes, merge ) && IsStillCurrent( scope, loadOrder, findChanges ) )
			removal = host.PrepareBlobRemoval( new[] { merge }, scope );

		if ( removal is null )
		{
			KeptOutdatedMerges = true;

			return;
		}

		removal();
		host.ReloadAfterBlobChange();
	}

	private Merge? FirstAffectedBlobMerge( BlobMergeChange change )
	{
		Merge[] blobMerges = session.Inventory!.Merges.Where( merge => merge.IsBlobMerge ).ToArray();

		return blobMerges.FirstOrDefault( merge => ProducedOutput( merge, change ) )
			?? blobMerges.FirstOrDefault( merge => UsesSource( merge, change ) )
			?? blobMerges.FirstOrDefault();
	}

	private void MarkSourceOutdated( BlobMergeChange change )
	{
		if ( change.Kind == BlobMergeChangeKind.MissingOutput )
			return;

		IEnumerable<FileHash> sources = session.Inventory!.Merges
			.Where( merge => merge.IsBlobMerge && ( change.Output is null || ProducedOutput( merge, change ) ) )
			.SelectMany( merge => merge.Mods )
			.Where( source => source.Name.Equals( change.Mod, StringComparison.OrdinalIgnoreCase ) );

		foreach ( FileHash source in sources )
			source.IsOutdated = true;
	}

	private bool ShouldRemove( IReadOnlyList<BlobMergeChange> changes, Merge merge )
	{
		if ( changes.Count == 1 )
			return ShouldRemove( changes[0], merge );

		return host.AutoDeleteOldMerges || host.ConfirmRemoveOutdatedBlobMerges( changes );
	}

	// Inputs may change while the prompt is open. Confirm they still match what the user approved.
	private bool IsStillCurrent( BlobRemovalScope asked, string? loadOrder, Func<IReadOnlyList<BlobMergeChange>> findChanges )
	{
		if ( host.AutoDeleteOldMerges )
			return true;

		if ( loadOrder is not null && loadOrder == LoadOrderContent() && asked.IsSameAs( BlobRemovalScope.Of( findChanges() ) ) )
			return true;

		AddIssue( UiText.T(
			"MergeTree",
			"The merges changed while the question was open, so nothing was removed. Refresh to check them again." ) );

		return false;
	}

	// A missing settings file is valid. A read failure leaves the current order unknown.
	private string? LoadOrderContent()
	{
		string path = session.LoadOrder!.FilePath;

		try
		{
			return path.Length == 0 || PathAbsence.IsConfirmed( path ) ? NoLoadOrderFile : Hasher.ComputeHash( path );
		}
		catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException )
		{
			return null;
		}
	}

	private bool ShouldRemove( BlobMergeChange change, Merge merge )
	{
		if ( host.AutoDeleteOldMerges )
			return true;

		return change.Kind switch
		{
			BlobMergeChangeKind.MissingSource => host.ConfirmDeleteForMissingSource( merge, change.Mod ),
			BlobMergeChangeKind.DisabledSource => host.ConfirmDeleteForDisabledSource( merge, change.Mod ),
			BlobMergeChangeKind.MissingOutput => host.ConfirmPruneMissingOutput( merge ),
			BlobMergeChangeKind.RenamedSource => host.ConfirmDeleteForRenamedSource( change.Mod, change.CurrentName! ),
			_ => host.ConfirmDeleteForChangedSource( merge, change.Path, change.Mod )
		};
	}

	// Unregistered outputs and their disabled originals must be handled together to avoid leaving both versions active.
	internal bool CheckUnregisteredOutputs( Func<bool>? confirmRemoval = null, MergeInventory? inventory = null )
	{
		try
		{
			BlobMergeStore store = BlobMergeStore.ForApplication();
			string state = store.InventoryPath;
			string mods = session.GamePaths.ModsDirectory;
			string name;
			string signature;
			bool automatic;
			byte[]? before;
			string[] managed;
			IReadOnlyList<UnregisteredMergeOutput> files;
			DisabledScriptRecovery recovery;

			RefuseInterruptedUpdate( store );

			before = File.Exists( state ) ? File.ReadAllBytes( state ) : null;
			inventory ??= MergeInventory.Deserialize( before );
			managed = ManagedOutputs( state, mods );
			name = host.MergedModName;
			files = UnregisteredMergeOutputs.Find( mods, name, inventory, managed );
			recovery = DisabledScriptRecoveryScan.Find( mods, state, inventory );

			foreach ( string issue in recovery.Issues )
				AddIssue( issue );

			if ( files.Count == 0 && recovery.Unclaimed.Count == 0 )
			{
				retainedUnownedFiles = null;
				unregisteredError = null;

				return true;
			}

			// Defer restoring unclaimed originals until the game closes.
			if ( files.Count == 0 && IsGameRunning() )
			{
				AddIssue( UiText.T( "BlobMerge", "Close The Witcher 3 before restoring unclaimed disabled scripts." ) );

				return true;
			}

			signature = string.Join(
				"\n",
				files.Select( file => file.Path ).Concat( recovery.Unclaimed ).Select( path => path.ToUpperInvariant() ) );
			automatic = host.AutoDeleteOldMerges;

			if ( !automatic && retainedUnownedFiles == signature )
				return false;

			if ( !automatic && !( confirmRemoval?.Invoke() ?? host.ConfirmRemoveUnregistered( files, recovery.Unclaimed ) ) )
			{
				retainedUnownedFiles = signature;
				host.KeepWindowOpen();

				return false;
			}

			retainedUnownedFiles = signature;

			return RemoveConfirmedOutputs( store, mods, name, before, inventory, managed, files, recovery.Unclaimed );
		}
		catch ( Exception error ) when ( error is IOException or InvalidDataException or UnauthorizedAccessException
			or InvalidOperationException or ArgumentException or System.Text.Json.JsonException )
		{
			string message = UiText.Format(
				"UnregisteredMerges",
				"Unregistered merged files could not be removed. The app will stay open.\n\n{0}",
				error.Message );

			retainedUnownedFiles = message;
			host.KeepWindowOpen();

			if ( unregisteredError != message )
			{
				unregisteredError = message;
				host.ShowUnregisteredCleanupError( message );
			}

			return false;
		}
	}

	// Reread and lock the inventory after approval so files registered while the prompt was open are preserved.
	private bool RemoveConfirmedOutputs(
		BlobMergeStore store,
		string mods,
		string mergedMod,
		byte[]? before,
		MergeInventory inventory,
		string[] managed,
		IReadOnlyList<UnregisteredMergeOutput> files,
		IReadOnlyList<string> disabledOriginals )
	{
		string state = store.InventoryPath;
		byte[]? after;
		MergeInventory latest;
		bool changedInventory;
		UnregisteredMergeOutput[] stillUnregistered;
		string[] stillUnclaimed;

		// Keep the inventory read-locked until removal finishes.
		using FileStream? readLock = File.Exists( state ) ? new FileStream( state, FileMode.Open, FileAccess.Read, FileShare.Read ) : null;

		RefuseInterruptedUpdate( store );

		after = File.Exists( state ) ? File.ReadAllBytes( state ) : null;
		latest = after is not null ? MergeInventory.Deserialize( after ) : inventory;
		changedInventory = !before.AsSpan().SequenceEqual( after );

		if ( changedInventory )
			managed = ManagedOutputs( state, mods );

		stillUnregistered = UnregisteredMergeOutputs.Find( mods, mergedMod, latest, managed )
			.Where( file => files.Any( original => original.Path.Equals( file.Path, StringComparison.OrdinalIgnoreCase ) ) )
			.ToArray();
		stillUnclaimed = DisabledScriptRecoveryScan.Find( mods, state, latest ).Unclaimed
			.Where( path => disabledOriginals.Contains( path, StringComparer.OrdinalIgnoreCase ) )
			.ToArray();

		if ( !UnregisteredMergeOutputs.Remove(
			mods,
			stillUnregistered,
			directory => host.GenerateMetadata( directory ) == 0,
			IsGameRunning,
			stillUnclaimed ) )
			return false;

		if ( changedInventory )
			session.LoadInventory();

		if ( stillUnregistered.Length > 0 || stillUnclaimed.Length > 0 || changedInventory )
		{
			UnregisteredRevision++;
			ManagedMergesChanged = true;
		}

		retainedUnownedFiles = null;
		unregisteredError = null;

		return true;
	}

	private static void RefuseInterruptedUpdate( BlobMergeStore store )
	{
		if ( store.HasInterruptedUpdate )
			throw new IOException( UiText.T( "UnregisteredMerges", "An interrupted merge must be recovered before unregistered merged files can be removed." ) );
	}

	private static string[] ManagedOutputs( string state, string mods )
	{
		if ( !BlobMergeTransaction.HasState( state ) )
			return Array.Empty<string>();

		return BlobMergeTransaction.ReadApplied( state, mods, state ).Manifest.Outputs
			.Select( output => MergeBuilder.SafePath( mods, output.Path ) )
			.ToArray();
	}
}
