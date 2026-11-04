#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using WitcherScriptMerger.Controls;
using WitcherScriptMerger.FileIndex;
using WitcherScriptMerger.Inventory;
using WitcherScriptMerger.LoadOrder;
using WitcherScriptMerger.Tools;
using WitcherScriptMerger.XmlCoverage;

namespace WitcherScriptMerger.Forms;

internal partial class MainForm
{
	private bool conflictRefreshPending;

	// Keep the attempted record so a failure can be checked against what was actually saved.
	private Merge? pendingAdoption;

	internal void AdoptVerifiedExistingBundledXmlMerge( TreeNode fileNode )
	{
		_ = AdoptVerifiedExistingBundledXmlMergeAsync( fileNode );
	}

	// An existing bundled XML merge can be recorded only after its sources have been verified.
	// If a later step fails, check the saved inventory before telling the user whether it was added.
	private async Task AdoptVerifiedExistingBundledXmlMergeAsync( TreeNode fileNode )
	{
		pendingAdoption = null;

		try
		{
			ModFile conflict = RequireAdoptableConflict( fileNode );
			string? candidateModName = AdoptionCandidate( conflict );
			BundledXmlVerificationRequest request;
			BundledXmlMergeVerificationResult verification;
			BundledXmlMergeAdoptionService.AdoptionResult adoption;

			if ( candidateModName == null )
				return;

			request = AdoptionRequest( conflict, candidateModName );

			if ( !ConfirmAdoption( conflict, request ) )
				return;

			verification = VerifyAdoption( request );

			if ( !verification.IsVerified )
			{
				await ShowBundledXmlVerificationFailureAsync( verification, new ProcessRunner() ).ConfigureAwait( true );

				return;
			}

			// Keep the verified files locked until the record is saved.
			using ( LeaseVerifiedInputs( verification ) )
			{
				adoption = RecordAdoption( conflict, request, verification );
				CompleteBundledXmlAdoptionWithoutConflictScan( fileNode );
				ShowAdoptionComplete( conflict, adoption );
			}
		}
		catch ( OperationCanceledException )
		{
		}
		catch ( Exception exception )
		{
			var (title, outcome) = AdoptionOutcome();

			ShowError( exception.Message + "\n\n" + outcome, title );
		}
	}

	private (string Title, string Text) AdoptionOutcome()
	{
		string notAdopted = UiText.T( "MainForm.BundledXmlAdoption", "Existing Merge Not Adopted" );
		string notKept = UiText.T( "MainForm.BundledXmlAdoption", "No bundle was modified. No unverified inventory record was retained." );

		if ( pendingAdoption is not { } adopted )
			return (notAdopted, notKept);

		return BundledXmlMergeAdoptionService.ReadSavedState( session.LoadInventory, adopted ) switch
		{
			BundledXmlMergeAdoptionService.SavedState.Saved => (UiText.T( "MainForm.BundledXmlAdoption", "Existing Merge Adopted" ),
				UiText.T( "MainForm.BundledXmlAdoption", "The adoption was saved to MergeInventory.xml, but the steps after it did not finish. Press F5 or click Refresh." )),
			BundledXmlMergeAdoptionService.SavedState.NotSaved => (notAdopted, notKept),
			_ => (UiText.T( "MainForm.BundledXmlAdoption", "Existing Merge Not Confirmed" ),
				UiText.T( "MainForm.BundledXmlAdoption", "No bundle was modified, but MergeInventory.xml could not be read back, so whether the adoption was saved is not confirmed. Press F5 or click Refresh to check." ))
		};
	}

	private static ModFile RequireAdoptableConflict( TreeNode fileNode )
	{
		if ( fileNode == null
			|| !ReferenceEquals( fileNode.Parent?.Tag, Categories.BundleText )
			|| !ModFile.IsXml( fileNode.Text )
			|| fileNode.Tag is not SMTree.NodeMetadata metadata
			|| metadata.ModFile == null )
		{
			throw new ArgumentException( UiText.T( "MainForm.BundledXmlAdoption", "Select a Bundled Text XML conflict before adopting an existing merge." ) );
		}

		return metadata.ModFile;
	}

	private void ShowAdoptionRefused( string message )
	{
		ShowError( message, UiText.T( "MainForm.BundledXmlAdoption", "Existing Merge Not Adopted" ) );
	}

	// Only record a generated mod when the current load order confirms that its content wins.
	private string? AdoptionCandidate( ModFile conflict )
	{
		string[] conflictModNames;
		string? configuredMergedMod;
		string? candidateModName;

		session.LoadOrder!.Refresh();

		if ( !session.LoadOrder.IsValid )
		{
			ShowAdoptionRefused( UiText.T( "MainForm.BundledXmlAdoption", "The current mods.settings file is invalid, so the actual winning mod cannot be proven." ) );

			return null;
		}

		conflictModNames = conflict.Mods
			.Select( mod => mod.Name )
			.Where( name => !string.IsNullOrWhiteSpace( name ) )
			.Distinct( StringComparer.OrdinalIgnoreCase )
			.ToArray();

		if ( conflictModNames.Length != conflict.Mods.Count || conflictModNames.Length < 2 )
			throw new InvalidDataException( UiText.T( "MainForm.BundledXmlAdoption", "The conflict has missing or duplicate mod identities." ) );

		if ( HasDuplicateEnabledPriority( conflictModNames, out int duplicatePriority, out string[] tiedMods ) )
		{
			ShowAdoptionRefused( UiText.Format( "MainForm.BundledXmlAdoption", "More than one enabled conflicting mod has priority {0}:\n\n{1}\n\nGive them distinct priorities, refresh conflicts, and try again.", duplicatePriority, string.Join( "\n", tiedMods.Select( name => "- " + name ) ) ) );

			return null;
		}

		configuredMergedMod = Paths.RetrieveMergedModName();

		if ( configuredMergedMod == null )
			return null;

		candidateModName = session.LoadOrder.GetTopPriorityEnabledMod( conflict );

		if ( string.IsNullOrWhiteSpace( candidateModName ) )
		{
			ShowAdoptionRefused( UiText.T( "MainForm.BundledXmlAdoption", "No enabled winning mod could be determined for this conflict." ) );

			return null;
		}

		if ( !candidateModName.Equals( configuredMergedMod, StringComparison.OrdinalIgnoreCase ) )
		{
			ShowAdoptionRefused( UiText.Format( "MainForm.BundledXmlAdoption", "The top-priority file currently comes from '{0}', not the configured generated mod '{1}'.\n\nThis action only adopts files already packed from Merged Bundle Content, because recording an arbitrary source mod as generated output would make later unmerge operations unsafe.", candidateModName, configuredMergedMod ) );

			return null;
		}

		return candidateModName;
	}

	private BundledXmlVerificationRequest AdoptionRequest( ModFile conflict, string candidateModName )
	{
		return new BundledXmlVerificationRequest()
		{
			GameDirectory = Paths.GameDirectory,
			ResourcePath = conflict.RelativePath,
			CandidateModName = candidateModName,
			CandidateBundleName = conflict.GetBundleName( candidateModName ),
			CandidateBundlePath = conflict.GetModFile( candidateModName, session.GamePaths ),
			StagedResourcePath = Path.Combine( Paths.MergedBundleContentAbsolute, conflict.RelativePath ),
			Sources = AdoptionSources( conflict, candidateModName ),
			JobsRoot = Path.Combine( Environment.CurrentDirectory, "Bundled XML Adoption Jobs" )
		};
	}

	// Rebuild this resource's source list before saving. Changes to unrelated resources should not invalidate it.
	private List<BundledXmlVerificationSource> AdoptionSources( ModFile conflict, string candidateModName )
	{
		return BundledXmlMergeAdoptionService.VerifiedSources( conflict, candidateModName )
			.OrderBy( mod => mod.Name, StringComparer.OrdinalIgnoreCase )
			.Select( mod => new BundledXmlVerificationSource(
				mod.Name,
				conflict.GetBundleName( mod.Name ),
				conflict.GetModFile( mod.Name, session.GamePaths ) ) )
			.ToList();
	}

	// Excluded sources stay out of the record; any edits they already contributed remain in the file.
	private static string LeftOutSources( ModFile conflict )
	{
		string[] names = conflict.Mods.Except( conflict.ParticipatingSources ).Select( source => source.Name )
			.Distinct( StringComparer.OrdinalIgnoreCase ).ToArray();

		return names.Length == 0
			? ""
			: UiText.Format(
				"MainForm.BundledXmlAdoption",
				"Left out because disabled: {0}. Any of their edits already in the merged file stay there.\n\n",
				string.Join( ", ", names ) );
	}

	private bool ConfirmAdoption( ModFile conflict, BundledXmlVerificationRequest request )
	{
		DialogResult confirmation = ShowMessage(
			UiText.Format(
				"MainForm.BundledXmlAdoption",
				"{0}{1}{2}Contributing mods to prove:\n{3}\n\nScript Merger Remastered will only read and compare files. If every source change is proven present and the packed winner exactly matches Merged Bundle Content, it will add a normal MergeInventory.xml record. It will not rebuild or modify any bundle.",
				UiText.T( "MainForm.BundledXmlAdoption", "Verify and adopt this existing bundled XML merge?\n\n" ),
				UiText.F( "MainForm.BundledXmlAdoption", $"Resource:\n{conflict.RelativePath}\n\n" ),
				UiText.F( "MainForm.BundledXmlAdoption", $"Installed winner:\n{request.CandidateModName}\n\n" ) + LeftOutSources( conflict ),
				string.Join( "\n", request.Sources.Select( source => "- " + source.ModName ) ) ),
			UiText.T( "MainForm.BundledXmlAdoption", "Adopt Verified Existing Merge" ),
			MessageBoxButtons.YesNo,
			MessageBoxIcon.Question );

		return confirmation == DialogResult.Yes;
	}

	private BundledXmlMergeVerificationResult VerifyAdoption( BundledXmlVerificationRequest request )
	{
		RemasteredBundleResourceTool bundleTool = new RemasteredBundleResourceTool();
		VanillaBundleIndex vanillaIndex = new VanillaBundleIndex(
			bundleTool,
			Path.Combine( AppContext.BaseDirectory, "Data", "Bundle Index Cache", "vanilla-xml-index.json" ) );
		BundledXmlMergeVerificationService verifier = new BundledXmlMergeVerificationService( bundleTool, vanillaIndex, new XmlCoverageAnalyzer() );

		using OperationProgressForm progressForm = new OperationProgressForm( UiText.T( "MainForm.BundledXmlAdoption", "Verifying Existing Bundled XML Merge" ) );

		return progressForm.Run( this, ( progress, cancellationToken ) => verifier.VerifyAsync( request, progress, cancellationToken ) );
	}

	private BundledXmlInputLease LeaseVerifiedInputs( BundledXmlMergeVerificationResult verification )
	{
		using OperationProgressForm progressForm = new OperationProgressForm( UiText.T( "MainForm.BundledXmlAdoption", "Finalizing Existing Merge Adoption" ) );

		return progressForm.Run(
			this,
			async ( progress, cancellationToken ) =>
			{
				progress.Report( UiText.T( "MainForm.BundledXmlAdoption", "Confirming and locking the verified inputs..." ) );

				return await verification.AcquireVerifiedInputLeaseAsync( cancellationToken ).ConfigureAwait( false );
			} );
	}

	// Reload the saved inventory on failure so an unsaved record cannot remain active in memory.
	private BundledXmlMergeAdoptionService.AdoptionResult RecordAdoption(
		ModFile conflict,
		BundledXmlVerificationRequest request,
		BundledXmlMergeVerificationResult verification )
	{
		string? currentWinner;
		BundledXmlMergeAdoptionService.AdoptionResult adoption;

		session.LoadOrder!.Refresh();

		if ( !session.LoadOrder.IsValid )
			throw new InvalidOperationException( UiText.T( "MainForm.BundledXmlAdoption", "mods.settings could not be read after verification, so the winning mod and its sources could not be confirmed." ) );

		currentWinner = session.LoadOrder.GetTopPriorityEnabledMod( conflict );

		if ( !request.CandidateModName.Equals( currentWinner, StringComparison.OrdinalIgnoreCase ) )
			throw new InvalidOperationException( UiText.T( "MainForm.BundledXmlAdoption", "The conflict winner changed during verification. No inventory record was added." ) );

		if ( !request.Sources.SequenceEqual( AdoptionSources( conflict, request.CandidateModName ) ) )
			throw new InvalidOperationException( UiText.T( "MainForm.BundledXmlAdoption", "The sources of this file changed during verification." ) );

		session.Inventory ??= session.InventoryStore.LoadAndBackfillHashes();

		try
		{
			adoption = BundledXmlMergeAdoptionService.AdoptVerified(
				session.Inventory,
				conflict,
				request.CandidateModName,
				request.Sources.Select( source => source.ModName ),
				session.GamePaths,
				Paths.MergedBundleContentAbsolute );
			pendingAdoption = adoption.Merge;

			if ( !session.Inventory.HasResolvedConflict( conflict, session.GamePaths ) )
				throw new InvalidOperationException( UiText.T( "MainForm.BundledXmlAdoption", "The new inventory record did not pass Script Merger Remastered's stale-source and load-order checks." ) );

			session.SaveInventory();
			verification.DeleteSuccessfulJob();
		}
		catch
		{
			session.LoadInventory();

			throw;
		}

		return adoption;
	}

	private void ShowAdoptionComplete( ModFile conflict, BundledXmlMergeAdoptionService.AdoptionResult adoption )
	{
		ShowMessage(
			UiText.Format(
				"MainForm.BundledXmlAdoption",
				"{0}{1}If any source bundle changes, Script Merger Remastered will automatically show the conflict again.\n\nThe full conflict scan was deferred so you can adopt more existing merges. Press F5 or click Refresh when you are finished.",
				UiText.F( "MainForm.BundledXmlAdoption", $"Verified and adopted {conflict.RelativePath}.\n\n" ),
				UiText.F(
					"MainForm.BundledXmlAdoption",
					$"Recorded contributing mods in MergeInventory.xml: {adoption.Merge.Mods.Count}. " ) + LeftOutSources( conflict ) ),
			UiText.T( "MainForm.BundledXmlAdoption", "Existing Merge Adopted" ),
			MessageBoxButtons.OK,
			MessageBoxIcon.Information );
	}

	// Remove the adopted row immediately. The rest of the list needs a full refresh.
	private void CompleteBundledXmlAdoptionWithoutConflictScan( TreeNode adoptedFileNode )
	{
		TreeNode? categoryNode = adoptedFileNode.Parent;

		adoptedFileNode.Checked = false;
		adoptedFileNode.RemoveFromTree();

		if ( categoryNode != null && categoryNode.Nodes.Count == 0 )
		{
			categoryNode.RemoveFromTree();
		}

		session.LoadOrder!.Refresh();
		RefreshMergeTree();
		ShowDefaultConflictView();
		UpdateConflictSearchSource();
		UpdateConflictSearchSuggestions();

		if ( IsConflictSearchActive )
		{
			UpdateConflictSearchView();
		}

		SetMergeButtonTextIfValidSelection();
		SetConflictRefreshPending( true );
	}

	private void SetConflictRefreshPending( bool pending )
	{
		conflictRefreshPending = pending;
		LiveText.BindText(
			btnRefreshConflicts,
			() => pending ? UiText.T( "MainForm.BundledXmlAdoption", "&Refresh *" ) : UiText.T( "MainForm.BundledXmlAdoption", "&Refresh" ) );
		LiveText.BindToolTip( toolTip1, btnRefreshConflicts, () => pending
				? !session.IsScanCurrent ? UiText.T( "MainForm", "Refresh conflicts to rebuild the cleared caches before merging." )
					: UiText.T(
						"MainForm.BundledXmlAdoption",
						"Conflict list has pending adopted merges. Press F5 when you are ready to run one full conflict scan." )
				: UiText.T( "MainForm.BundledXmlAdoption", "Refresh Conflicts (Keyboard: F5)" ) );

		if ( pending )
		{
			lblConflicts.Text = IsConflictSearchActive
				? UiText.F(
					"MainForm.BundledXmlAdoption",
					$"{( IsFileSearchActive ? UiText.T( "MainForm", "File Search Results" ) : UiText.T( "MainForm", "Mod Search Results" ) )} (Refresh Pending)" )
				: UiText.T( "MainForm.BundledXmlAdoption", "Conflicts (Refresh Pending)" );
			LiveText.BindText( lblStatusLeft1, () => !session.IsScanCurrent
				? UiText.T( "MainForm", "Scan invalidated - refresh required" ) : UiText.T( "MainForm.BundledXmlAdoption", "Adoption saved - full conflict scan deferred" ) );
			lblStatusLeft2.Visible = false;
		}
	}

	// Tied priorities leave the winner uncertain, so the existing merge cannot be accepted.
	private bool HasDuplicateEnabledPriority(
		IReadOnlyCollection<string> conflictModNames,
		out int duplicatePriority,
		out string[] tiedMods )
	{
		IGrouping<int, ModLoadSetting>? duplicate = session.LoadOrder!.Mods
			.Where( setting => setting.IsEnabled == true
				&& setting.Priority.HasValue
				&& conflictModNames.Contains( setting.ModName, StringComparer.OrdinalIgnoreCase ) )
			.GroupBy( setting => setting.Priority!.Value )
			.FirstOrDefault( group => group.Count() > 1 );

		if ( duplicate == null )
		{
			duplicatePriority = -1;
			tiedMods = Array.Empty<string>();

			return false;
		}

		duplicatePriority = duplicate.Key;
		tiedMods = duplicate.Select( setting => setting.ModName )
			.OrderBy( name => name, StringComparer.OrdinalIgnoreCase )
			.ToArray();

		return true;
	}

	private async Task ShowBundledXmlVerificationFailureAsync(
		BundledXmlMergeVerificationResult verification,
		IProcessRunner processRunner )
	{
		BundledXmlVerifiedSource? unresolved = verification.Sources
			.FirstOrDefault( source => source.Coverage.Status != XmlCoverageStatus.Covered );
		StringBuilder message = new StringBuilder();

		message.AppendLine( UiText.T( "MainForm.BundledXmlAdoption", "Script Merger Remastered could not prove that the existing winner contains every source change." ) )
			.AppendLine()
			.AppendLine( UiText.T( "MainForm.BundledXmlAdoption", "No inventory record was added." ) )
			.AppendLine()
			.AppendLine( UiText.T( "MainForm.BundledXmlAdoption", "Sources needing attention:" ) );

		foreach ( BundledXmlVerifiedSource source in verification.Sources.Where( source => source.Coverage.Status != XmlCoverageStatus.Covered ) )
		{
			XmlCoverageFinding? first = source.Coverage.Findings.FirstOrDefault( finding => finding.Status != XmlCoverageStatus.Covered );

			message.Append( "- " ).Append( source.Source.ModName ).Append( ": " ).Append( source.Coverage.Status );

			if ( first != null )
			{
				message.Append( UiText.T( "MainForm.BundledXmlAdoption", " at " ) ).Append( first.Location ).Append( " — " ).Append( first.Message );
			}

			message.AppendLine();
		}

		message.AppendLine()
			.Append( UiText.T( "MainForm.BundledXmlAdoption", "Full report: " ) ).AppendLine( verification.DiagnosticReportPath );

		string kdiff3Path = ResolveExecutablePath( KDiff3.ExePath );
		bool canCompare = unresolved != null && File.Exists( kdiff3Path );

		if ( !canCompare )
		{
			ShowMessage(
				message.ToString(),
				UiText.T( "MainForm.BundledXmlAdoption", "Existing Merge Needs Review" ),
				MessageBoxButtons.OK,
				MessageBoxIcon.Warning );

			return;
		}

		message.AppendLine().Append( UiText.T( "MainForm.BundledXmlAdoption", "Open a read-only Vanilla / generated winner / source comparison in KDiff3?" ) );

		if ( ShowMessage(
			message.ToString(),
			UiText.T( "MainForm.BundledXmlAdoption", "Existing Merge Needs Review" ),
			MessageBoxButtons.YesNo,
			MessageBoxIcon.Warning ) != DialogResult.Yes )
		{
			return;
		}

		using OperationProgressForm progressForm = new OperationProgressForm( UiText.T( "MainForm.BundledXmlAdoption", "Comparing Bundled XML in KDiff3" ) );

		progressForm.Run(
			this,
			async ( progress, cancellationToken ) =>
			{
				progress.Report( UiText.T( "MainForm.BundledXmlAdoption", "Waiting for the KDiff3 comparison to close..." ) );

				ProcessRunResult result = await processRunner.RunAsync( new ProcessRunRequest
				{
					FileName = kdiff3Path,
					WorkingDirectory = verification.JobDirectory,
					Arguments = new[]
					{
						verification.VanillaResourcePath,
						verification.CandidateResourcePath,
						unresolved!.ExtractedResourcePath,
						"--L1", "Vanilla",
						"--L2", verification.Request.CandidateModName,
						"--L3", unresolved.Source.ModName,
						"--cs", "ShowWhiteSpace=0",
						"--cs", "ShowWhiteSpaceCharacters=0",
						"--cs", "EscapeKeyQuits=1"
					},
					ShowWindow = true
				}, cancellationToken ).ConfigureAwait( false );

				if ( !result.Succeeded )
				{
					throw new InvalidOperationException( UiText.F( "MainForm.BundledXmlAdoption", $"KDiff3 closed with exit code {result.ExitCode}." ) );
				}

				return true;
			} );
		await Task.CompletedTask;
	}
}
