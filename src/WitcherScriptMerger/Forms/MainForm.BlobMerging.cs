#nullable enable annotations
using System.IO;
using System.Windows.Forms;
using System;
using System.Collections.Generic;
using WitcherScriptMerger.BlobMerging;
using WitcherScriptMerger.FileIndex;
using WitcherScriptMerger.Inventory;
using System.Threading.Tasks;
using WitcherScriptMerger.Tools;

namespace WitcherScriptMerger.Forms;

internal partial class MainForm
{
	internal static bool IsManagedScriptConflict(
		ModFile? file,
		IEnumerable<Merge> merges ) => MergeSelection.IsManagedScript( file, merges );

	private static string DescribeManagedRemoval( Merge merge, string message ) => merge.IsBlobMerge
		? message + Environment.NewLine + Environment.NewLine + UiText.T( "BlobMerge", "Associated merged files will be removed together; unrelated merges are kept." ) : message;

	// The returned paths have been included in the blob merge and must leave the ordinary merge queue.
	// An empty list means there was no blob work; null means the merge could not finish.
	private async Task<IReadOnlyList<string>?> MergeBlobConflicts( ScriptMergePlan plan )
	{
		if ( !scriptMerges.CanStart() )
			return null;

		if ( !plan.HasInputs )
			return Array.Empty<string>();

		ScriptMergeRequest? request = scriptMerges.Prepare( plan );

		if ( request is null )
			return null;

		InitializeProgressScreen( UiText.T( "BlobMerge", "Reading installed mod information" ) );

		var Report = CreateScriptProgressReporter();

		try
		{
			var applied = await Task.Run( () => ScriptMergeService.Apply( request, Report,
				path => Invoke( (MethodInvoker)( () => lblProgressCurrentAction.Text = path ) ), KDiff3.Run ) );

			return scriptMerges.Finish( applied );
		}
		catch ( BlobMergeValidationException error )
		{
			string log = BlobMergeValidationLog.Record( error );

			HideProgressScreen();

			using var warning = new BlobMergeFailureForm( error.Details + Environment.NewLine + Environment.NewLine + log );

			warning.ShowDialog( this );

			return null;
		}
		catch ( Exception error ) when ( error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException
			or ArgumentException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception
			or WSCompiler.Core.Scripts.Standalone.ScriptSyntaxException )
		{
			ShowError( UiText.Format( "BlobMerge", "The merge could not be completed:\n{0}", error.Message ) );

			return null;
		}
		finally
		{
			HideProgressScreen();
		}
	}

	private void FinishManagedBlobRemoval()
	{
		session.LoadInventory();
		session.LoadOrder.Refresh();
		reconciler.ManagedMergesChanged = true;
	}

	internal async Task<bool> RunManagedBlobChangeAsync( Action change )
	{
		InitializeProgressScreen( UiText.T( "BlobMerge", "Removing selected script merges" ) );

		try
		{
			await Task.Run( change );
			lblProgressCurrentPhase.Text = UiText.T( "BlobMerge", "Reloading merge inventory" );
			lblProgressCurrentAction.Text = "";
			FinishManagedBlobRemoval();

			return true;
		}
		catch ( OperationCanceledException )
		{
			return false;
		}
		catch ( Exception error ) when ( error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException
			or System.Text.Json.JsonException or ArgumentException or WSCompiler.Core.Scripts.Standalone.ScriptSyntaxException )
		{
			ShowError( error.Message );

			return false;
		}
		finally { HideProgressScreen(); }
	}
}
