#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WitcherScriptMerger.BlobMerging;
using WitcherScriptMerger.FileIndex;
using WitcherScriptMerger.Inventory;
using WitcherScriptMerger.LoadOrder;
using WitcherScriptMerger.Merging;

namespace WitcherScriptMerger;

// Selected loose scripts already owned by a blob merge require rebuilding that merge.
internal sealed record ScriptMergePlan( IReadOnlyList<ModFile> BlobConflicts, IReadOnlySet<string> Sources, bool RebuildExisting )
{
	internal bool HasWork => BlobConflicts.Count > 0 || RebuildExisting;

	internal bool HasInputs => Sources.Count > 0 || RebuildExisting;
}

internal enum MergeWriteRefusalKind
{
	UnreadableSettings,
	UnreadableOutput,
	DisabledOutput,
	UnavailableSource
}

internal sealed record MergeWriteRefusal( MergeWriteRefusalKind Kind, string? Name = null );

// Capture inputs on the UI thread before dispatching the merge.
internal sealed record ScriptMergeRequest(
	ScriptMergePlan Plan,
	IReadOnlyList<ModFile> RelatedConflicts,
	IReadOnlyList<ModLoadSetting> Settings,
	string? ConfiguredModIoDirectory,
	string ModIoConfigPath,
	string MergedModName,
	string ModsDirectory,
	string VanillaDirectory,
	string LoadOrderPath,
	string InventoryPath );

// Prepare on the UI thread, apply in the background, then finish on the UI thread.
internal sealed class ScriptMergeService
{
	private readonly MergeSession session;
	private readonly IMergeWorkflowHost host;

	internal ScriptMergeService( MergeSession session, IMergeWorkflowHost host )
	{
		this.session = session;
		this.host = host;
	}

	internal static ScriptMergePlan Plan( IEnumerable<MergeSelection> selections, IEnumerable<Merge> merges )
	{
		MergeSelection[] batch = selections.ToArray();
		ModFile[] blobConflicts = batch.SelectMany( selection => selection.SelectedBlobConflicts )
			.Where( file => BlobConflictSelection.CanSelect( file ) ).ToArray();

		return new ScriptMergePlan( blobConflicts, BlobConflictSelection.Sources( blobConflicts ),
			batch.Any( selection => MergeSelection.IsManagedScript( selection.Conflict, merges ) ) );
	}

	// Read the load order again before writing. Missing mods.settings is valid; unreadable settings and disabled outputs block merging.
	internal static MergeWriteRefusal? FindWriteRefusal(
		ScriptMergePlan plan,
		IEnumerable<MergeSelection> selections,
		string mergedModName,
		IEnumerable<Merge> merges,
		GamePaths game,
		CustomLoadOrder? order )
	{
		if ( order?.IsValid != true )
			return new( MergeWriteRefusalKind.UnreadableSettings );

		Merge[] recorded = merges.ToArray();
		IEnumerable<string> blobFolders = plan.HasWork
			? recorded.Where( merge => merge.IsBlobMerge ).Select( merge => merge.MergedModName )
			: [];
		string? disabled;

		try
		{
			disabled = MergeOutputFolder.FindDisabledTarget( new[] { mergedModName }.Concat( blobFolders ), recorded, game, order );
		}
		catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException )
		{
			return new( MergeWriteRefusalKind.UnreadableOutput, error.Message );
		}

		if ( disabled is not null )
			return new( MergeWriteRefusalKind.DisabledOutput, disabled );

		string? changed = selections.Select( selection => selection.FindUnavailableSource( order ) )
			.FirstOrDefault( name => name is not null );

		return changed is null ? null : new( MergeWriteRefusalKind.UnavailableSource, changed );
	}

	internal bool CanStart()
	{
		return Directory.Exists( session.GamePaths.ModsDirectory ) && host.ValidateScriptsDirectory() && session.LoadOrder?.IsValid == true;
	}

	// Call after CanStart succeeds. A null result means the user has already been told why preparation stopped.
	internal ScriptMergeRequest? Prepare( ScriptMergePlan plan )
	{
		CustomLoadOrder order = session.LoadOrder!;
		BlobMergeStore store = BlobMergeStore.ForApplication();
		string configured;
		ModLoadSetting[] settings;
		string? mergedName;

		if ( store.HasInterruptedUpdate )
		{
			host.ShowMessage( UiText.T( "BlobMerge", "An update was interrupted. Close The Witcher 3 and refresh to recover the merge." ) );

			return null;
		}

		configured = host.Settings.Get( "ModIoDirectory" );
		settings = order.Mods.ToArray();

		if ( MergeReconciler.IsGameRunning() )
		{
			host.ShowError( UiText.T( "BlobMerge", "Close The Witcher 3 before applying or restoring a merge." ) );

			return null;
		}

		mergedName = host.RetrieveMergedModName();

		if ( mergedName is null )
			return null;

		return new ScriptMergeRequest(
			plan,
			session.Scan?.Files.ToArray() ?? plan.BlobConflicts.ToArray(),
			settings,
			string.IsNullOrWhiteSpace( configured ) ? null : configured,
			ModIoDiscovery.ConfigPath( host.Settings ),
			mergedName,
			session.GamePaths.ModsDirectory,
			session.GamePaths.ScriptsDirectory,
			order.FilePath,
			store.InventoryPath );
	}

	// Run off the UI thread: this reads mod files and waits for the merge editor.
	internal static AppliedBlobMerge? Apply( ScriptMergeRequest request, Action<string, string> progress, Action<string> showCurrentPath,
		Func<FileMerger.MergeSource, FileMerger.MergeSource, FileInfo?, string, bool, int> textMerge )
	{
		IReadOnlyList<ModFile> conflicts = request.Plan.BlobConflicts;
		BlobMergeReadiness readiness = BlobConflictSelection.CheckSelection( conflicts );
		IReadOnlyList<BlobMergeSource> sources;
		BlobMergePreflight? preparedScan;
		LoadedBlobMerge? loaded;

		if ( !readiness.Available )
			throw new InvalidOperationException( readiness.Reason );

		sources = BlobMergeWorkspace.Discover(
			request.ModsDirectory,
			request.Settings,
			new ModIoDiscovery( request.ConfiguredModIoDirectory, request.ModIoConfigPath ).Discover(),
			request.MergedModName );
		preparedScan = conflicts.Select( file => file.MergePreflight ).FirstOrDefault( scan => scan is not null );
		loaded = preparedScan?.PrepareExecution( request.Plan.Sources, sources, request.Settings );

		if ( loaded is null || !WasLoadedFor( request, loaded ) )
		{
			loaded = BlobMergeWorkspace.Load(
				request.ModsDirectory,
				request.VanillaDirectory,
				request.LoadOrderPath,
				sources,
				request.Settings,
				request.Plan.Sources,
				request.InventoryPath,
				request.MergedModName,
				progress: progress );
		}

		var prepared = BlobMergePlanning.PrepareMinimalSelection( loaded, request.RelatedConflicts, progress );

		loaded = prepared.Loaded;

		return BlobMergeWorkflow.Run( loaded, loaded.Selected, BlobMergeStore.NewMergeWorkDirectory(), request.InventoryPath, textMerge,
			showCurrentPath, stages: progress, conflicts: request.RelatedConflicts, preparedPlan: prepared.Plan );
	}

	// Only reuse prepared inputs for the same files.
	private static bool WasLoadedFor( ScriptMergeRequest request, LoadedBlobMerge loaded )
	{
		return loaded.VanillaDirectory.Equals( request.VanillaDirectory, StringComparison.OrdinalIgnoreCase )
			&& loaded.SettingsPath.Equals( request.LoadOrderPath, StringComparison.OrdinalIgnoreCase )
			&& string.Equals( loaded.InventoryPath, request.InventoryPath, StringComparison.OrdinalIgnoreCase )
			&& loaded.LooseModName.Equals( request.MergedModName, StringComparison.OrdinalIgnoreCase );
	}

	// Return consumed sources so the batch can skip their ordinary merges.
	internal IReadOnlyList<string>? Finish( AppliedBlobMerge? applied )
	{
		if ( applied is null )
			return null;

		session.LoadOrder!.Refresh();
		session.LoadInventory();

		return applied.Manifest.Inputs.Where( input => input.SuppressedPath is not null ).Select( input => input.Path ).ToArray();
	}
}
