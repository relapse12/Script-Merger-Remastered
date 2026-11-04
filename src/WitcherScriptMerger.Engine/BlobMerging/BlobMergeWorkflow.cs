#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WitcherScriptMerger.Inventory;
using WitcherScriptMerger.FileIndex;
using WitcherScriptMerger.Merging;
using WSCompiler.Core.Scripts.Standalone;
using WSCompiler.Recovery;

namespace WitcherScriptMerger.BlobMerging;

// Runs a blob merge from conflict decisions through validation and installation of the result.
internal static class BlobMergeWorkflow
{
	// Cancellation returns null. The work directory must be new; cleanup is attempted on exit.
	// Validation errors are reported through BlobMergeValidationException.
	internal static AppliedBlobMerge? Run(
		LoadedBlobMerge loaded,
		IReadOnlySet<string> selected,
		string work,
		string state,
		Func<FileMerger.MergeSource, FileMerger.MergeSource, FileInfo?, string, bool, int> merge,
		Action<string>? progress = null,
		Func<bool>? gameRunning = null,
		Action<string, string>? stages = null,
		IReadOnlyList<ModFile>? conflicts = null,
		MergePlan? preparedPlan = null )
	{
		gameRunning ??= () => System.Diagnostics.Process.GetProcessesByName( "witcher3" ).Length > 0;

		if ( gameRunning() )
			throw new IOException( UiText.T( "BlobMerge", "Close The Witcher 3 before applying or restoring a merge." ) );

		if ( Directory.Exists( work ) )
			throw new IOException( "The temporary merge directory already exists." );

		Directory.CreateDirectory( work );

		try
		{
			return new MergeRun( loaded, selected, work, state, merge, progress, gameRunning, stages, conflicts ).Execute( preparedPlan );
		}
		catch ( ScriptSyntaxException error )
		{
			throw new BlobMergeValidationException( new[] { new WSCompiler.Diagnostic( "error", "C006", UiText.T( "BlobMerge", "Merged source" ), error.Line, error.Message ) } );
		}
		finally
		{
			try
			{
				if ( Directory.Exists( work ) )
					Directory.Delete( MergeBuilder.SafePath( Path.GetDirectoryName( work )!, Path.GetFileName( work ) ), recursive: true );
			}
			catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException )
			{
			}
		}
	}

	// A user's decision can change the dependencies. Keep the plan and loaded inputs together as the selection changes.
	private sealed class MergeRun
	{
		private readonly string work;
		private readonly string state;
		private readonly Func<FileMerger.MergeSource, FileMerger.MergeSource, FileInfo?, string, bool, int> merge;
		private readonly Action<string>? progress;
		private readonly Func<bool> gameRunning;
		private readonly Action<string, string>? stages;
		private readonly IReadOnlyList<ModFile>? conflicts;
		private readonly Dictionary<string, MergeChoice> choices = new( StringComparer.Ordinal );
		private readonly Dictionary<string, PlannedFile> overrides = new( StringComparer.OrdinalIgnoreCase );
		private readonly Dictionary<string, PlannedFile> baselines = new( StringComparer.OrdinalIgnoreCase );
		private LoadedBlobMerge loaded;
		private IReadOnlySet<string> selected;
		private MergePlan plan = null!;
		private BlobMergeComparisonContext? comparisonContext;
		private int comparisonNumber;

		internal MergeRun(
			LoadedBlobMerge loaded,
			IReadOnlySet<string> selected,
			string work,
			string state,
			Func<FileMerger.MergeSource, FileMerger.MergeSource, FileInfo?, string, bool, int> merge,
			Action<string>? progress,
			Func<bool> gameRunning,
			Action<string, string>? stages,
			IReadOnlyList<ModFile>? conflicts )
		{
			this.loaded = loaded;
			this.selected = selected;
			this.work = work;
			this.state = state;
			this.merge = merge;
			this.progress = progress;
			this.gameRunning = gameRunning;
			this.stages = stages;
			this.conflicts = conflicts;
		}

		// Saved choices can change the plan. Apply them before asking about the remaining conflicts.
		internal AppliedBlobMerge? Execute( MergePlan? preparedPlan )
		{
			BlobMergeAdoption adoption;
			BlobMergeHistory history;

			selected = loaded.Selected.Count > 0 ? loaded.Selected : selected;
			plan = preparedPlan ?? BlobMergePlanning.Plan( loaded, selected, progress: stages );
			adoption = BlobMergeAdoption.Prepare( loaded, plan, BlobMergeAdoption.None );
			stages?.Invoke( UiText.T( "BlobMerge", "Reading saved merge choices" ), "" );
			history = BlobMergeHistory.Load( loaded, adoption, plan );

			if ( !CarryOverSavedDecisions( history ) )
				return null;

			if ( choices.Count > 0 )
				plan = BlobMergePlanning.Plan( loaded, selected, choices, progress: stages );

			if ( !ResolveConflicts() )
				return null;

			if ( loaded.Previous is not null && !KeepPreviousEdits( history ) )
				return null;

			progress?.Invoke( loaded.LooseModName );
			adoption = BlobMergeAdoption.Prepare( loaded, plan, adoption );
			plan = adoption.OutputPlan( loaded, plan );

			return BuildAndApply( adoption, history );
		}

		// Preserve earlier decisions when their inputs are unchanged. Merge changed inputs into the saved result
		// so a rebuild keeps the user's previous work.
		private bool CarryOverSavedDecisions( BlobMergeHistory history )
		{
			foreach ( MergeConflict decision in plan.Decisions ?? [] )
			{
				SavedMergeDecision? saved = history.Decisions.FirstOrDefault( entry => entry.Decision.Key == decision.Key );
				IReadOnlyList<MergeOption> previousInputs;
				MergeOption[] changed;
				bool manual;
				string? updated;

				if ( saved is null )
					continue;

				previousInputs = saved.Decision.Inputs ?? saved.Decision.Options;
				changed = ( decision.Inputs ?? decision.Options )
					.Where( option => !previousInputs.Any( previous => previous.Mod == option.Mod && previous.Source == option.Source ) )
					.ToArray();
				manual = saved.Decision.Options.Count > 1 || !saved.Decision.Options.Any( option => option.Source == saved.Source );

				if ( changed.Length == 0 && !manual )
					continue;

				updated = CompareChangedInputs( decision, saved.Source, changed, previousInputs );

				if ( updated is null )
					return false;

				choices[decision.Key] = new( decision.Options[0].Id, updated );
			}

			return true;
		}

		private string? CompareChangedInputs(
			MergeConflict decision,
			string current,
			IEnumerable<MergeOption> changed,
			IReadOnlyList<MergeOption> previousInputs )
		{
			foreach ( MergeOption option in changed )
			{
				string? baseline;
				string? updated;

				if ( option.Source == current )
					continue;

				baseline = previousInputs.FirstOrDefault( previous => previous.Mod == option.Mod )?.Source ?? decision.Original;
				updated = Compare( current, UiText.T( "BlobMerge", "Current merge" ), option, baseline, decision.Key );

				if ( updated is null )
					return null;

				current = updated;
			}

			return current;
		}

		// Each answer can change the plan or reveal required inputs. Recheck conflicts after those changes.
		private bool ResolveConflicts()
		{
			while ( true )
			{
				while ( plan.Conflicts.FirstOrDefault( conflict => !choices.ContainsKey( conflict.Key ) ) is { } conflict )
				{
					if ( !ResolveConflict( conflict ) )
						return false;
				}

				if ( conflicts is null || !plan.NeedsCompilation )
					return true;

				var prepared = BlobMergePlanning.PrepareMinimalSelection( loaded with
				{
					Selected = selected
				}, conflicts, stages, choices, overrides );

				plan = prepared.Plan;

				if ( prepared.Loaded.Selected.SetEquals( selected ) )
					return true;

				loaded = prepared.Loaded;
				selected = loaded.Selected;
			}
		}

		private bool ResolveConflict( MergeConflict conflict )
		{
			string current = conflict.Options[0].Source;
			string name = conflict.Options[0].Mod;

			progress?.Invoke( conflict.Key );

			foreach ( MergeOption option in conflict.Options.Skip( 1 ) )
			{
				string? edited = Compare( current, name, option, conflict.Original, conflict.Key );

				if ( edited is null )
					return false;

				current = edited;
				name = UiText.T( "BlobMerge", "Current merge" );
			}

			choices[conflict.Key] = new( conflict.Options[0].Id, current );
			plan = BlobMergePlanning.Plan( loaded, selected, choices, fileOverrides: overrides, progress: stages );

			return true;
		}

		// Loose-file edits can be merged into the new result. An externally edited blob cannot be handled that way,
		// so stop to preserve it.
		private bool KeepPreviousEdits( BlobMergeHistory history )
		{
			var manifest = loaded.Previous!.Manifest;
			string previousWorkspace = BlobMergeTransaction.WorkspacePath( state, manifest.Id );
			string prefix = manifest.LooseMod + "/content/scripts/";
			Dictionary<string, SavedMergeFile> editedFiles;

			foreach ( var output in manifest.Outputs.Where( output => output.Path.EndsWith( ".rsblob", StringComparison.OrdinalIgnoreCase ) ) )
			{
				if ( IsEditedOutput( output.Path, output.Sha256 ) )
					throw new IOException( UiText.T( "BlobMerge", "The compiled merge was edited outside Script Merger. Preserve those changes before rebuilding it." ) );
			}

			editedFiles = history.Files.Where( file => file.Manual ).ToDictionary( file => file.Path, StringComparer.OrdinalIgnoreCase );

			foreach ( var output in manifest.Outputs.Where( output => output.Path.EndsWith( ".ws", StringComparison.OrdinalIgnoreCase )
				&& IsEditedOutput( output.Path, output.Sha256 ) ) )
			{
				string relative = output.Path.Replace( '\\', '/' )[prefix.Length..];

				editedFiles.TryAdd( relative, new( relative, false, output.Mods ?? manifest.Mods, [] ) );
			}

			foreach ( SavedMergeFile file in editedFiles.Values )
			{
				if ( !KeepEditedFile( file, previousWorkspace, prefix ) )
					return false;
			}

			if ( overrides.Count == 0 )
				return true;

			plan = BlobMergePlanning.Plan( loaded, selected, choices, fileOverrides: overrides, progress: stages );

			return ResolveConflicts();
		}

		private bool IsEditedOutput( string path, string recordedHash )
		{
			return loaded.PreviousOutputs[path].Length > 0 && loaded.PreviousOutputs[path] != recordedHash;
		}

		private bool KeepEditedFile( SavedMergeFile file, string previousWorkspace, string prefix )
		{
			string publicFile = MergeBuilder.SafePath( loaded.ModsDirectory, prefix + file.Path );
			string stored = MergeBuilder.SafePath( previousWorkspace, ( file.Compile ? "compiled/" : "loose/" ) + file.Path );
			string actual = File.Exists( publicFile ) ? File.ReadAllText( publicFile ) : File.ReadAllText( stored );
			PlannedFile next = plan.Files.FirstOrDefault( planned => planned.Path.Equals( file.Path, StringComparison.OrdinalIgnoreCase ) )
				?? new( file.Path, [], false, file.Owners );
			string baseline = MergeBuilder.SafePath( previousWorkspace, ( file.Manual ? "base/" : "loose/" ) + file.Path );
			string? original = OriginalText( baseline, file.Path );
			string? edited = original == next.Source
				? actual
				: Compare(
					actual,
					UiText.T( "BlobMerge", "Current merge" ),
					new( "file:" + file.Path, UiText.T( "BlobMerge", "Updated scripts" ), next.Source ),
					original,
					"file:" + file.Path );

			if ( edited is null )
				return false;

			overrides[file.Path] = next with
			{
				Items = Parser.ParseSource( edited, false )
			};
			baselines[file.Path] = next;

			return true;
		}

		private string? OriginalText( string baseline, string relativePath )
		{
			SourceFile? vanilla;

			if ( File.Exists( baseline ) )
				return File.ReadAllText( baseline );

			vanilla = loaded.Vanilla.FirstOrDefault( source => source.Path.Equals( relativePath, StringComparison.OrdinalIgnoreCase ) );

			return vanilla is null ? null : SourceWriter.Write( vanilla.Items );
		}

		// Save recovery data before applying the build. Check that the game is closed again before writing to Mods.
		private AppliedBlobMerge BuildAndApply( BlobMergeAdoption adoption, BlobMergeHistory history )
		{
			string workspace = Path.Combine( work, "sources" );
			string buildFolder = Path.Combine( work, "output" );
			WitcherScriptMerger.Merging.MergeBuildResult outputMerge;
			AppliedBlobMerge applied;

			stages?.Invoke( UiText.T( "BlobMerge", "Saving merge recovery information" ), "" );
			BlobMergeHistory.Save( workspace, loaded, adoption, plan, choices, baselines );

			if ( plan.NeedsCompilation )
				File.WriteAllText( Path.Combine( workspace, "compilation.key" ), BlobMergeBuilder.CompilationKey( loaded, plan ) );

			try
			{
				outputMerge = BlobMergeBuilder.Build( loaded, adoption, plan, choices, buildFolder, stages );
			}
			catch ( InvalidDataException ) when ( File.Exists( Path.Combine( buildFolder, "compile-errors.json" ) ) )
			{
				var diagnostics = System.Text.Json.JsonSerializer.Deserialize<WSCompiler.Diagnostic[]>( File.ReadAllText( Path.Combine( buildFolder, "compile-errors.json" ) ) )!;

				throw new BlobMergeValidationException( diagnostics );
			}

			BlobMergeValidation.Validate( loaded, outputMerge, stages );

			if ( gameRunning() )
				throw new IOException( UiText.T( "BlobMerge", "Close The Witcher 3 before applying or restoring a merge." ) );

			stages?.Invoke( UiText.T( "BlobMerge", "Applying merged scripts" ), "" );
			applied = BlobMergeInventory.Apply( loaded, adoption, outputMerge, state, workspace, history.WorkspaceGuards );
			SaveRecoveredSources( applied );
			RecoveredScriptStore.Prune( state );
			stages?.Invoke( UiText.T( "BlobMerge", "Finishing merge" ), "" );

			return applied;
		}

		private void SaveRecoveredSources( AppliedBlobMerge applied )
		{
			var included = loaded.Sources.Where( source => source.Blobs.Count == 1 && plan.IncludedMods.Contains( source.Name, StringComparer.OrdinalIgnoreCase ) );

			foreach ( BlobMergeSource source in included )
			{
				var input = applied.Manifest.Inputs.SingleOrDefault( entry => entry.Path.Equals( source.Blobs[0], StringComparison.OrdinalIgnoreCase ) );
				string path;

				if ( input is null )
					continue;

				path = input.SuppressedPath ?? input.Path;

				if ( File.Exists( path ) )
				{
					RecoveredScriptStore.Save(
						source.Name,
						File.ReadAllBytes( path ),
						loaded.Modules.Single( module => module.Name.Equals( source.Name, StringComparison.OrdinalIgnoreCase ) ).Files,
						loaded.Vanilla );
				}
			}
		}

		private string? Compare( string current, string name, MergeOption option, string? original, string key )
		{
			FileInfo? vanilla;
			FileMerger.MergeSource firstSource;

			progress?.Invoke( option.Mod );
			stages?.Invoke( UiText.T( "BlobMerge", "Merging script changes—waiting for KDiff3" ), option.Mod );
			comparisonContext ??= new( loaded.Vanilla.Concat( loaded.Modules.SelectMany( module => module.Files ) ) );

			using var comparison = new BlobMergeComparisonFiles( work, comparisonNumber++ );

			File.WriteAllText( comparison.First, current );
			File.WriteAllText( comparison.Second, option.Source );
			vanilla = original is null ? null : new( comparison.Original );

			if ( vanilla is not null )
				File.WriteAllText( vanilla.FullName, original! );

			firstSource = FileMerger.MergeSource.FromFlatFile( new( comparison.First ), new()
			{
				Name = name
			} );
			firstSource.Name = comparisonContext.Label( name, key );

			if ( merge( firstSource, FileMerger.MergeSource.FromFlatFile( new( comparison.Second ), new()
			{
				Name = option.Mod
			} ), vanilla, comparison.Output, false ) != 0
				|| !File.Exists( comparison.Output ) )
			{
				return null;
			}

			return File.ReadAllText( comparison.Output );
		}
	}
}
