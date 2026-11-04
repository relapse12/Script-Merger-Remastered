#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using WitcherScriptMerger.FileIndex;
using WitcherScriptMerger.LoadOrder;
using WSCompiler.Core;
using WSCompiler.Core.Scripts.Standalone;
using WSCompiler;
using WitcherScriptMerger.Merging;
using WSCompiler.Recovery;
using WitcherScriptMerger.Inventory;

namespace WitcherScriptMerger.BlobMerging;

// Read the current sources together with the previous merge's saved originals.
// Keep their hashes so changes can be detected before the next merge is applied.
internal static class BlobMergeWorkspace
{
	// Mod.io sources need both a known enabled state and a priority before they can participate.
	internal static IReadOnlyList<BlobMergeSource> Discover( string modsDirectory, IReadOnlyList<ModLoadSetting> settings,
		IEnumerable<ModIoInstalledMod> modIo, string? existingLooseMerge = null )
	{
		var result = new List<BlobMergeSource>();

		foreach ( string directory in Directory.GetDirectories( modsDirectory, "mod*" ) )
		{
			string name = Path.GetFileName( directory );

			if ( settings.Any( setting => setting.ModName.Equals( name, StringComparison.OrdinalIgnoreCase ) && setting.IsEnabled == false ) )
				continue;

			string content = Path.Combine( directory, "content" );

			if ( !Directory.Exists( content ) )
				continue;

			result.Add( new( name, directory, false, Directory.GetFiles( content, "*", SearchOption.AllDirectories ).Where( ModFileIndex.IsContentPrecompiledBlob ).ToArray(),
				name.Equals( existingLooseMerge, StringComparison.OrdinalIgnoreCase ) ) );
		}

		foreach ( var source in modIo.Where( source => source.IsEnabled != false ) )
		{
			string content = Path.Combine( source.SourceDirectory, "content" );

			result.Add( new( source.DisplayName, source.SourceDirectory, true, Directory.Exists( content )
				? Directory.GetFiles( content, "*", SearchOption.AllDirectories ).Where( ModFileIndex.IsContentPrecompiledBlob ).ToArray() : Array.Empty<string>(),
				StateKnown: source.IsEnabled == true && source.Priority.HasValue ) );
		}

		return result;
	}

	private sealed class ModInputs
	{
		// Map each original path to the saved copy made when an earlier merge disabled it.
		private readonly IReadOnlyDictionary<string, string> reads;

		internal readonly List<MergeInputFile> Snapshot;
		internal readonly IReadOnlyList<SourceFile> Vanilla;
		internal readonly Dictionary<string, Dictionary<long, string>> VanillaEnums;

		// By blob path.
		internal readonly Dictionary<string, byte[]> BlobData = new( StringComparer.OrdinalIgnoreCase );
		internal readonly Dictionary<string, SourceFile> ReferenceFiles = new( StringComparer.OrdinalIgnoreCase );

		// By blob path, and by mod name for a mod's loose scripts.
		internal readonly Dictionary<string, Dictionary<string, Dictionary<long, string>>> EnumReferences = new( StringComparer.OrdinalIgnoreCase );

		// By mod name.
		internal readonly Dictionary<string, List<SourceFile>> LooseFiles = new( StringComparer.OrdinalIgnoreCase );
		internal readonly Dictionary<string, List<string>> UnparsedFiles = new( StringComparer.OrdinalIgnoreCase );
		internal readonly Dictionary<string, List<string>> UnparsedPaths = new( StringComparer.OrdinalIgnoreCase );
		internal readonly Dictionary<string, List<string>> InspectedPaths = new( StringComparer.OrdinalIgnoreCase );
		internal readonly Dictionary<string, IReadOnlyList<string>?> UnparsedIdentifiers = new( StringComparer.OrdinalIgnoreCase );

		internal ModInputs(
			IReadOnlyDictionary<string, string> reads,
			List<MergeInputFile> snapshot,
			IReadOnlyList<SourceFile> vanilla,
			Dictionary<string, Dictionary<long, string>> vanillaEnums )
		{
			this.reads = reads;
			Snapshot = snapshot;
			Vanilla = vanilla;
			VanillaEnums = vanillaEnums;
		}

		internal byte[] ReadBytes( string path )
		{
			return File.ReadAllBytes( reads.GetValueOrDefault( path, path ) );
		}

		internal void AddToSnapshot( string path, byte[] bytes )
		{
			Snapshot.Add( new( path, MergeBuilder.Hash( bytes ), ReadPath: reads.GetValueOrDefault( path ) ) );
		}
	}

	// Recovery runs in parallel. Keep failures with their blobs so they can be handled later in load order.
	private sealed record RecoveredBlob( string BlobPath, IReadOnlyList<SourceFile>? Files, Exception? Error );

	// With recoveryUnavailable supplied, a failed blob can still provide reference declarations.
	// Otherwise recovery failure stops the load.
	internal static LoadedBlobMerge Load(
		string modsDirectory,
		string vanillaDirectory,
		string settingsPath,
		IReadOnlyList<BlobMergeSource> sources,
		IReadOnlyList<ModLoadSetting> settings,
		IReadOnlySet<string> selected,
		string? inventoryPath = null,
		string? looseModName = null,
		string? stateFile = null,
		IReadOnlySet<string>? excludedMods = null,
		IReadOnlySet<string>? removedMods = null,
		Action<string, string>? recoveryUnavailable = null,
		Action<string, string>? progress = null,
		IReadOnlySet<string>? excludedSourceFiles = null,
		bool selectExistingBlobs = true )
	{
		var reads = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
		var guards = new List<MergeInputFile>();
		var previousOutputs = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
		var snapshot = new List<MergeInputFile>();
		var modules = new List<MergeModule>();
		var referenceModules = new List<MergeModule>();
		var retainedAdoptions = new List<Merge>();
		var retainedSources = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
		AppliedBlobMerge? previous;
		string? stateHash;
		string? settingsHash;
		string[] conventionalFolders;
		string[] vanillaPaths;
		Dictionary<string, string[]> inventories;
		List<SourceFile> vanilla;
		BlobMergeSource[] ordered;
		ModInputs inputs;
		Dictionary<string, RecoveredBlob>? recovered;
		string modIoConfiguration;
		string looseMod;

		stateFile ??= inventoryPath is null ? BlobMergeStore.ForApplication().InventoryPath : Path.GetFullPath( inventoryPath );

		if ( new BlobMergeStore( stateFile ).HasInterruptedUpdate )
			throw new IOException( UiText.T( "BlobMerge", "An update was interrupted. Close The Witcher 3 and refresh to recover the merge." ) );

		previous = BlobMergeTransaction.HasState( stateFile ) ? BlobMergeTransaction.ReadApplied( stateFile, modsDirectory, inventoryPath ) : null;
		stateHash = File.Exists( stateFile ) ? MergeBuilder.Hash( stateFile ) : null;

		if ( previous is not null )
		{
			var locations = MergeSourceLocations.Find( previous.Manifest );
			bool ownsCompiled = previous.Manifest.Outputs.Any( output => output.Path.EndsWith( ".rsblob", StringComparison.OrdinalIgnoreCase ) );

			guards.Add( new( stateFile, stateHash! ) );
			RecordPreviousOutputs( previous, modsDirectory, guards, previousOutputs );
			FindMovedSources( previous, modsDirectory, locations, reads );
			sources = SourcesAfterPreviousMerge( sources, previous, ownsCompiled, reads.Keys );
			selected = SelectionAfterPreviousMerge( selected, sources, previous, ownsCompiled, selectExistingBlobs, excludedMods );
		}

		if ( sources.Any( source => source.IsModIo && !source.StateKnown ) )
			throw new InvalidOperationException( UiText.T( "BlobMerge", "Some mod.io states or priorities are unavailable. Check the selected mod.io and Game Documents folders before merging." ) );

		settingsHash = File.Exists( settingsPath ) ? MergeBuilder.Hash( settingsPath ) : null;
		conventionalFolders = Directory.GetDirectories( modsDirectory, "mod*" ).Order( StringComparer.OrdinalIgnoreCase ).ToArray();
		vanillaPaths = Directory.GetFiles( vanillaDirectory, "*.ws", SearchOption.AllDirectories ).Order( StringComparer.OrdinalIgnoreCase ).ToArray();
		inventories = sources.ToDictionary(
			source => source.Name,
			source => TrackedSourcePaths( source, previous, reads.Keys, excludedSourceFiles ),
			StringComparer.OrdinalIgnoreCase );
		vanilla = ReadVanillaSources( vanillaDirectory, vanillaPaths, snapshot, progress );
		ordered = InLoadOrder( sources, settings );
		inputs = new ModInputs(
			reads,
			snapshot,
			vanilla,
			WSCompiler.Core.Scripts.EnumResolver.ReadEnums( vanilla.SelectMany( file => file.Items ).OfType<EnumItem>().Select( item => item.Decl ) ) );

		foreach ( BlobMergeSource source in ordered )
		{
			progress?.Invoke( UiText.T( "BlobMerge", "Reading mod scripts and dependencies" ), source.Name );
			ReadBlobs( source, inputs );
			ReadLooseScripts( source, inventories[source.Name], inputs );
		}

		recovered = recoveryUnavailable is null ? null : RecoverSelectedBlobs( ordered, selected, inputs );

		for ( int order = 0; order < ordered.Length; order++ )
		{
			BlobMergeSource source = ordered[order];
			bool isSelected = selected.Contains( source.Name );

			if ( isSelected )
				progress?.Invoke( UiText.T( "BlobMerge", "Recovering compiled scripts" ), source.Name );

			AddModule(
				source,
				order,
				isSelected && CanRecover( source, recoveryUnavailable ),
				inputs,
				recovered,
				recoveryUnavailable,
				modules,
				referenceModules );
		}

		SnapshotUnparsedScripts( sources, inventories, inputs );
		snapshot.Add( new( settingsPath, settingsHash ?? "" ) );
		modIoConfiguration = Path.Combine( Path.GetDirectoryName( settingsPath )!, "modioModsConfig.json" );
		snapshot.Add( new( modIoConfiguration, HashIfPresent( modIoConfiguration ) ) );

		if ( inventoryPath is not null )
		{
			inventoryPath = Path.GetFullPath( inventoryPath );
			snapshot.Add( new( inventoryPath, HashIfPresent( inventoryPath ) ) );
		}

		progress?.Invoke( UiText.T( "BlobMerge", "Checking merge inputs" ), "" );
		MergeBuilder.VerifyInputs( snapshot );
		looseMod = looseModName ?? MergeBuilder.LooseName;

		foreach ( RetainedAdoption retained in ReadRetainedAdoptions( previous, looseMod, modsDirectory, stateFile ) )
		{
			guards.Add( retained.Guard );
			AddOriginal( modules, looseMod, retained.OriginalFile );
			AddOriginal( referenceModules, looseMod, retained.OriginalFile );
			retainedAdoptions.Add( retained.Merge );
			retainedSources[retained.OutputPath] = retained.SavedCopy;
		}

		return new LoadedBlobMerge(
			modules,
			vanilla,
			snapshot,
			sources,
			settings,
			vanillaDirectory,
			modsDirectory,
			settingsPath,
			settingsHash,
			inventories,
			conventionalFolders,
			vanillaPaths,
			new BlobPlanningCache() )
		{
			InventoryPath = inventoryPath,
			LooseModName = looseMod,
			Previous = previous,
			StateFile = stateFile,
			StateHash = stateHash,
			Guards = guards,
			PreviousOutputs = previousOutputs,
			Selected = selected,
			SourceReads = reads,
			RemovedMods = removedMods,
			ReferenceModules = referenceModules,
			ExcludedSourceFiles = excludedSourceFiles ?? new HashSet<string>(),
			EnumReferences = inputs.EnumReferences,
			RetainedAdoptions = retainedAdoptions,
			RetainedMergeSources = retainedSources
		};
	}

	private static string HashIfPresent( string path )
	{
		return File.Exists( path ) ? MergeBuilder.Hash( path ) : "";
	}

	private static bool IsContentBlob( BlobMergeSource source, string blob )
	{
		return Path.GetFullPath( blob ).Equals(
			Path.Combine( source.Directory, "content", "precompiled.rsblob" ),
			StringComparison.OrdinalIgnoreCase );
	}

	private static void RecordPreviousOutputs(
		AppliedBlobMerge previous,
		string modsDirectory,
		List<MergeInputFile> guards,
		Dictionary<string, string> previousOutputs )
	{
		foreach ( var output in previous.Manifest.Outputs )
		{
			string path = MergeBuilder.SafePath( modsDirectory, output.Path );
			string hash = HashIfPresent( path );

			previousOutputs.Add( output.Path, hash );
			guards.Add( new( path, hash ) );
		}
	}

	// Read disabled sources from their saved locations. A newly compiled mod no longer uses its old loose scripts.
	private static void FindMovedSources(
		AppliedBlobMerge previous,
		string modsDirectory,
		IReadOnlyDictionary<string, string> locations,
		Dictionary<string, string> reads )
	{
		string Locate( string path )
		{
			return MergeSourceLocations.PathFor( modsDirectory, path, locations );
		}

		foreach ( var input in previous.Manifest.Inputs.Where( input => input.SuppressedPath is not null
			&& !File.Exists( Locate( input.Path ) )
			&& File.Exists( Locate( input.SuppressedPath! ) ) ) )
		{
			string current = Locate( input.Path );
			string mod = Path.GetRelativePath( modsDirectory, current ).Split( Path.DirectorySeparatorChar )[0];
			bool isBlob = Path.GetFileName( input.Path ).Equals( "precompiled.rsblob", StringComparison.OrdinalIgnoreCase );

			if ( !isBlob && File.Exists( Path.Combine( modsDirectory, mod, "content", "precompiled.rsblob" ) ) )
				continue;

			reads[current] = Locate( input.SuppressedPath! );
		}
	}

	// Rebuild from original mod inputs, including disabled copies. Exclude the previous merged output from those inputs.
	private static BlobMergeSource[] SourcesAfterPreviousMerge(
		IReadOnlyList<BlobMergeSource> sources,
		AppliedBlobMerge previous,
		bool ownsCompiled,
		IEnumerable<string> movedPaths )
	{
		return sources
			.Where( source => !ownsCompiled || !source.Name.Equals( previous.Manifest.CompiledMod, StringComparison.OrdinalIgnoreCase ) )
			.Select( source => source with
			{
				Blobs = source.Blobs
					.Concat( movedPaths.Where( path => Path.GetFileName( path ).Equals( "precompiled.rsblob", StringComparison.OrdinalIgnoreCase )
						&& IsInside( path, source.Directory ) ) )
					.Distinct( StringComparer.OrdinalIgnoreCase )
					.ToArray()
			} )
			.ToArray();
	}

	private static HashSet<string> SelectionAfterPreviousMerge(
		IReadOnlySet<string> selected,
		IReadOnlyList<BlobMergeSource> sources,
		AppliedBlobMerge previous,
		bool ownsCompiled,
		bool selectExistingBlobs,
		IReadOnlySet<string>? excludedMods )
	{
		HashSet<string> names = selected
			.Where( name => ( !ownsCompiled || !name.Equals( previous.Manifest.CompiledMod, StringComparison.OrdinalIgnoreCase ) )
				&& !name.Equals( previous.Manifest.LooseMod, StringComparison.OrdinalIgnoreCase ) )
			.ToHashSet( StringComparer.OrdinalIgnoreCase );

		if ( selectExistingBlobs )
		{
			names.UnionWith( sources
				.Where( source => source.Blobs.Count > 0 && previous.Manifest.Mods.Contains( source.Name, StringComparer.OrdinalIgnoreCase ) )
				.Select( source => source.Name ) );
		}

		if ( excludedMods is not null )
			names.ExceptWith( excludedMods );

		return names;
	}

	// A missing part of vanilla would give the merge an incomplete base. Stop if any vanilla script cannot be parsed.
	private static List<SourceFile> ReadVanillaSources(
		string vanillaDirectory,
		IEnumerable<string> vanillaPaths,
		List<MergeInputFile> snapshot,
		Action<string, string>? progress )
	{
		var vanilla = new List<SourceFile>();

		foreach ( string path in vanillaPaths )
		{
			string relative = Path.GetRelativePath( vanillaDirectory, path );
			byte[] data;
			string text;

			progress?.Invoke( UiText.T( "BlobMerge", "Reading original game scripts" ), relative );
			data = File.ReadAllBytes( path );
			text = TextDecoding.Decode( data );

			try
			{
				vanilla.Add( new SourceFile( relative, Parser.ParseSource( text, false ) ) );
			}
			catch ( ScriptSyntaxException exception )
			{
				throw new InvalidDataException(
					UiText.Format( "BlobMerge", "Cannot preserve the vanilla source {0}: {1}", path, exception.Message ),
					exception );
			}

			snapshot.Add( new( path, MergeBuilder.Hash( data ) ) );
		}

		if ( vanilla.Count == 0 )
			throw new InvalidDataException( UiText.T( "BlobMerge", "Vanilla script sources are required to recover and merge compiled edits." ) );

		return vanilla;
	}

	private static BlobMergeSource[] InLoadOrder( IEnumerable<BlobMergeSource> sources, IReadOnlyList<ModLoadSetting> settings )
	{
		ModLoadSetting? SettingFor( string name )
		{
			return settings.FirstOrDefault( setting => setting.ModName.Equals( name, StringComparison.OrdinalIgnoreCase ) );
		}

		return sources
			.OrderBy( source => source.Name, Comparer<string>.Create( ( left, right ) =>
				LoadOrderComparer.CompareRuntimeOrder( left, SettingFor( left ), right, SettingFor( right ) ) ) )
			.ToArray();
	}

	private static Dictionary<string, Dictionary<long, string>> NumberedEnums( IEnumerable<EnumItem> items )
	{
		return WSCompiler.Core.Scripts.EnumResolver.ReadEnums( items
			.Where( item => item.Decl.Members.All( member => member.Value.HasValue ) )
			.Select( item => item.Decl ) );
	}

	private static void ReadBlobs( BlobMergeSource source, ModInputs inputs )
	{
		if ( source.Blobs.Any( blob => !IsContentBlob( source, blob ) ) )
			throw new InvalidOperationException( UiText.Format( "BlobMerge", "{0} must contain one blob at content/precompiled.rsblob to be merged.", source.Name ) );

		foreach ( string blob in source.Blobs )
		{
			byte[] data = inputs.ReadBytes( blob );

			inputs.BlobData[blob] = data;
			inputs.AddToSnapshot( blob, data );
			inputs.ReferenceFiles[blob] = BlobReferences.Read( data, blob );
			inputs.EnumReferences[blob] = NumberedEnums( inputs.ReferenceFiles[blob].Items.OfType<EnumItem>() );
		}
	}

	private static void ReadLooseScripts( BlobMergeSource source, IEnumerable<string> trackedPaths, ModInputs inputs )
	{
		var files = new List<SourceFile>();
		var errors = new List<string>();
		var errorPaths = new List<string>();
		var inspected = new List<string>();
		var identifiers = new HashSet<string>( StringComparer.Ordinal );
		bool identifiersKnown = true;
		string scripts = Path.Combine( source.Directory, "content", "scripts" );

		inputs.LooseFiles[source.Name] = files;
		inputs.UnparsedFiles[source.Name] = errors;
		inputs.UnparsedPaths[source.Name] = errorPaths;
		inputs.InspectedPaths[source.Name] = inspected;

		if ( source.Blobs.Count == 0 )
		{
			foreach ( string path in trackedPaths.Where( path => path.StartsWith( scripts + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase )
				&& path.EndsWith( ".ws", StringComparison.OrdinalIgnoreCase ) ) )
			{
				byte[] data = inputs.ReadBytes( path );
				string text = TextDecoding.Decode( data );
				string relative = Path.GetRelativePath( scripts, path );

				try
				{
					files.Add( new SourceFile( relative, Parser.ParseSource( text, false ) ) );
				}
				catch ( ScriptSyntaxException error )
				{
					errors.Add( relative + "(" + error.Line + "): " + error.Message );
					errorPaths.Add( relative );

					if ( !KeepWhatParses( text, relative, files, inspected, identifiers ) )
						identifiersKnown = false;
				}

				inputs.AddToSnapshot( path, data );
			}
		}

		inputs.UnparsedIdentifiers[source.Name] = identifiersKnown ? identifiers.ToArray() : null;
		inputs.EnumReferences[source.Name] = NumberedEnums( files.SelectMany( file => file.Items ).OfType<EnumItem>() );
	}

	// Even an unparseable script may mention names the merge depends on. Keep whatever declarations and references
	// can be read so the dependency check can account for it.
	private static bool KeepWhatParses(
		string text,
		string relative,
		List<SourceFile> files,
		List<string> inspected,
		HashSet<string> identifiers )
	{
		try
		{
			files.Add( new( relative, Parser.InspectDependencies( text ) ) );
			inspected.Add( relative );

			return true;
		}
		catch ( ScriptSyntaxException )
		{
		}

		try
		{
			files.Add( new( relative, Parser.ParseSource( text, true ) ) );
		}
		catch ( ScriptSyntaxException )
		{
		}

		try
		{
			identifiers.UnionWith( Lexer.Tokenize( text, lenient: true ).Where( token => token.Kind == TokKind.Ident ).Select( token => token.Text ) );

			return true;
		}
		catch ( Exception exception ) when ( exception is ScriptSyntaxException or FormatException or OverflowException )
		{
			return false;
		}
	}

	private static IReadOnlyList<SourceFile> RecoverBlob( BlobMergeSource source, string blob, ModInputs inputs )
	{
		return RecoverAndSave( inputs.BlobData[blob], source.Name, blob, inputs.VanillaEnums, inputs.EnumReferences, inputs.Vanilla );
	}

	// Recover blobs in parallel, then consume the results in load order.
	private static Dictionary<string, RecoveredBlob> RecoverSelectedBlobs(
		IEnumerable<BlobMergeSource> ordered,
		IReadOnlySet<string> selected,
		ModInputs inputs )
	{
		return ordered
			.Where( source => selected.Contains( source.Name )
				&& !source.IsModIo
				&& source.Blobs.Count == 1
				&& IsContentBlob( source, source.Blobs[0] ) )
			.AsParallel()
			.WithDegreeOfParallelism( Math.Min( 4, Environment.ProcessorCount ) )
			.Select( source =>
			{
				try
				{
					return new RecoveredBlob( source.Blobs[0], RecoverBlob( source, source.Blobs[0], inputs ), null );
				}
				catch ( Exception error ) when ( IsInspectionFailure( error ) )
				{
					return new RecoveredBlob( source.Blobs[0], null, error );
				}
			} )
			.ToDictionary( result => result.BlobPath, StringComparer.OrdinalIgnoreCase );
	}

	private static bool CanRecover( BlobMergeSource source, Action<string, string>? recoveryUnavailable )
	{
		string? problem = null;

		if ( source.IsModIo )
			problem = UiText.T( "BlobMerge", "Mod.io sources are read-only and cannot be merged." );
		else if ( source.Blobs.Count != 1 || !IsContentBlob( source, source.Blobs[0] ) )
			problem = UiText.Format( "BlobMerge", "{0} must contain one blob at content/precompiled.rsblob to be merged.", source.Name );

		if ( problem is null )
			return true;

		if ( recoveryUnavailable is null )
			throw new InvalidOperationException( problem );

		recoveryUnavailable( source.Name, problem );

		return false;
	}

	// Keep reference declarations separately from recovered code; unselected mods need only their references.
	private static void AddModule(
		BlobMergeSource source,
		int order,
		bool recover,
		ModInputs inputs,
		IReadOnlyDictionary<string, RecoveredBlob>? recovered,
		Action<string, string>? recoveryUnavailable,
		List<MergeModule> modules,
		List<MergeModule> referenceModules )
	{
		var files = new List<SourceFile>();
		var links = new HashSet<string>( StringComparer.Ordinal );
		MergeModule module;

		foreach ( string blob in source.Blobs.Order( StringComparer.OrdinalIgnoreCase ) )
		{
			if ( recover )
				AddRecoveredFiles( source, blob, inputs, recovered, recoveryUnavailable, files );
			else
				files.Add( inputs.ReferenceFiles[blob] );

			links.UnionWith( BlobReferences.LinkedSymbols( inputs.BlobData[blob], blob ) );
		}

		files.AddRange( inputs.LooseFiles[source.Name] );

		if ( files.Count == 0 && inputs.UnparsedFiles[source.Name].Count == 0 )
			return;

		module = new MergeModule(
			source.Name,
			files,
			source.Blobs.Count > 0,
			source.IsModIo,
			order,
			links.ToArray(),
			inputs.UnparsedFiles[source.Name],
			inputs.UnparsedIdentifiers[source.Name],
			inputs.UnparsedPaths[source.Name],
			inputs.InspectedPaths[source.Name] );
		modules.Add( module );
		referenceModules.Add( module with
		{
			Files = source.Blobs.Select( blob => inputs.ReferenceFiles[blob] ).Concat( inputs.LooseFiles[source.Name] ).ToArray()
		} );
	}

	private static void AddRecoveredFiles(
		BlobMergeSource source,
		string blob,
		ModInputs inputs,
		IReadOnlyDictionary<string, RecoveredBlob>? recovered,
		Action<string, string>? recoveryUnavailable,
		List<SourceFile> files )
	{
		try
		{
			if ( recovered is not null && recovered.TryGetValue( blob, out RecoveredBlob? result ) )
			{
				if ( result.Error is not null )
					throw result.Error;

				files.AddRange( result.Files! );
			}
			else
			{
				files.AddRange( RecoverBlob( source, blob, inputs ) );
			}
		}
		catch ( Exception error ) when ( recoveryUnavailable is not null && IsInspectionFailure( error ) )
		{
			recoveryUnavailable( source.Name, error.Message );
			files.Add( inputs.ReferenceFiles[blob] );
		}
	}

	// Track loose scripts beside a blob even though they are not parsed, so later file changes are still detected.
	private static void SnapshotUnparsedScripts(
		IEnumerable<BlobMergeSource> sources,
		IReadOnlyDictionary<string, string[]> inventories,
		ModInputs inputs )
	{
		IEnumerable<string> scripts = sources
			.Where( source => source.Blobs.Count > 0 )
			.SelectMany( source => inventories[source.Name] )
			.Where( path => path.EndsWith( ".ws", StringComparison.OrdinalIgnoreCase ) );

		foreach ( string path in scripts )
		{
			if ( !inputs.Snapshot.Any( input => input.Path.Equals( path, StringComparison.OrdinalIgnoreCase ) ) )
				inputs.AddToSnapshot( path, inputs.ReadBytes( path ) );
		}
	}

	private static void AddOriginal( List<MergeModule> targets, string looseMod, SourceFile file )
	{
		int index = targets.FindIndex( module => module.Name.Equals( looseMod, StringComparison.OrdinalIgnoreCase ) );

		if ( index < 0 )
			targets.Add( new( looseMod, [file] ) );
		else
			targets[index] = targets[index] with
			{
				Files = targets[index].Files.Concat( [file] ).ToArray()
			};
	}

	private sealed record RetainedAdoption(
		Merge Merge,
		SourceFile OriginalFile,
		string OutputPath,
		string SavedCopy,
		MergeInputFile Guard );

	// The live files now contain the blob merge. Read the saved copies to recover the earlier ordinary merges,
	// and check their hashes before trusting them.
	private static List<RetainedAdoption> ReadRetainedAdoptions(
		AppliedBlobMerge? previous,
		string looseMod,
		string modsDirectory,
		string stateFile )
	{
		var adoptions = new List<RetainedAdoption>();
		string prefix = looseMod + "/content/scripts/";
		MergeInventory originals;

		if ( previous?.Inventory?.Previous is not { } originalInventory )
			return adoptions;

		originals = MergeInventory.Deserialize( originalInventory );

		foreach ( var original in previous.RestoredOutputs ?? [] )
		{
			string relative = original.Path.Replace( '\\', '/' );
			string savedCopy;
			byte[] data;
			Merge? entry;

			if ( !relative.StartsWith( prefix, StringComparison.OrdinalIgnoreCase ) )
				continue;

			relative = relative[prefix.Length..];
			entry = originals.Merges.SingleOrDefault( merge => !merge.IsBlobMerge
				&& merge.MergedModName.Equals( looseMod, StringComparison.OrdinalIgnoreCase )
				&& merge.RelativePath.Replace( '\\', '/' ).Equals( relative, StringComparison.OrdinalIgnoreCase ) );

			if ( entry is null )
				throw new IOException( UiText.T( "BlobMerge", "Saved merge ownership changed or is missing." ) );

			savedCopy = MergeBuilder.SafePath( BlobMergeTransaction.StorageDirectory( stateFile ), original.StoragePath );
			data = File.ReadAllBytes( savedCopy );

			if ( MergeBuilder.Hash( data ) != original.Sha256 )
				throw new IOException( UiText.T( "BlobMerge", "Saved merge ownership changed or is missing." ) );

			adoptions.Add( new RetainedAdoption(
				entry,
				new SourceFile( relative, Parser.ParseSource( TextDecoding.Decode( data ), false ) ),
				MergeBuilder.SafePath( modsDirectory, original.Path ),
				savedCopy,
				new MergeInputFile( savedCopy, original.Sha256 ) ) );
		}

		return adoptions;
	}

	// Readable exports in Data are separate from the copies needed to undo an applied merge.
	private static IReadOnlyList<SourceFile> RecoverAndSave(
		byte[] data,
		string name,
		string path,
		Dictionary<string, Dictionary<long, string>> vanillaEnums,
		IReadOnlyDictionary<string, Dictionary<string, Dictionary<long, string>>> references,
		IReadOnlyList<SourceFile> vanilla )
	{
		var sources = Recover( data, name, path, vanillaEnums, references );

		RecoveredScriptStore.Save( name, data, sources, vanilla );

		return sources;
	}

	// Enum references affect recovered names, so include them in the cache key.
	// Require complete recovery before allowing a blob to be merged.
	private static IReadOnlyList<SourceFile> Recover( byte[] data, string name, string path,
		Dictionary<string, Dictionary<long, string>> vanillaEnums,
		IReadOnlyDictionary<string, Dictionary<string, Dictionary<long, string>>> references )
	{
		var context = new Dictionary<string, Dictionary<long, string>>( vanillaEnums, StringComparer.Ordinal );

		foreach ( var reference in references.Where( pair => !pair.Key.Equals( path, StringComparison.OrdinalIgnoreCase ) ).SelectMany( pair => pair.Value ) )
			context.TryAdd( reference.Key, reference.Value );

		string identity = BlobMergeArtifacts.Identity( new
		{
			name,
			Blob = MergeBuilder.Hash( data ),
			Enums = context.OrderBy( pair => pair.Key, StringComparer.Ordinal ).Select( pair => new { pair.Key, Values = pair.Value.OrderBy( value => value.Key ).ToArray() } ).ToArray()
		} );

		return BlobMergeArtifacts.Get( "recovered-source", identity, () =>
		{
			var result = BlobDecompiler.Recover( data, name, context );

			if ( !result.CanMerge )
				throw new InvalidDataException( UiText.Format( "BlobMerge", "{0} cannot be fully recovered:\n{1}", name,
				string.Join(
					Environment.NewLine,
					result.Issues.Where( issue => issue.Requirement == RecoveryRequirement.Unsupported ).Select( issue => issue.Symbol + ": " + issue.Message ) ) ) );

			return result.Sources.ToArray();
		} );
	}

	internal static MergeModule RecoverAdditional( LoadedBlobMerge loaded, string name )
	{
		var source = loaded.Sources.FirstOrDefault( source => source.Name.Equals( name, StringComparison.OrdinalIgnoreCase ) );
		string conventionalBlob;
		string path;
		byte[] data;
		Dictionary<string, Dictionary<long, string>> vanillaEnums;

		if ( source is null )
			throw new IOException( UiText.T( "BlobMerge", "The selected source was not checked. Refresh conflicts before merging." ) );

		if ( source.IsModIo )
			throw new InvalidOperationException( UiText.T( "BlobMerge", "Mod.io sources are read-only and cannot be merged." ) );

		conventionalBlob = Path.Combine( source.Directory, "content", "precompiled.rsblob" );

		if ( source.Blobs.Count != 1
			|| !Path.GetFullPath( source.Blobs[0] ).Equals( conventionalBlob, StringComparison.OrdinalIgnoreCase ) )
			throw new InvalidOperationException( UiText.Format(
				"BlobMerge", "{0} must contain one blob at content/precompiled.rsblob to be merged.", name ) );

		path = source.Blobs[0];
		data = File.ReadAllBytes( loaded.ReadPath( path ) );

		if ( loaded.Snapshot.FirstOrDefault( input => input.Path.Equals( path, StringComparison.OrdinalIgnoreCase ) )?.Sha256
			!= MergeBuilder.Hash( data ) )
			throw new IOException( UiText.T( "BlobMerge", "The installed script set changed. Refresh conflicts and try again." ) );

		vanillaEnums = WSCompiler.Core.Scripts.EnumResolver.ReadEnums(
			loaded.Vanilla.SelectMany( file => file.Items ).OfType<EnumItem>().Select( item => item.Decl ) );

		return loaded.Modules.Single( module => module.Name.Equals( name, StringComparison.OrdinalIgnoreCase ) )
			with
		{
			Files = RecoverAndSave( data, name, path, vanillaEnums, loaded.EnumReferences, loaded.Vanilla )
		};
	}

	internal static bool IsInspectionFailure( Exception error ) => error is IOException or InvalidDataException or UnauthorizedAccessException
		or InvalidOperationException or ArgumentException or System.Text.Json.JsonException or ScriptSyntaxException
		or NotSupportedException or OverflowException or IndexOutOfRangeException;

	// The trailing separator prevents sibling names such as modA2 from matching modA.
	internal static bool IsInside( string path, string folder )
	{
		return path.StartsWith(
			Path.TrimEndingDirectorySeparator( folder ) + Path.DirectorySeparatorChar,
			StringComparison.OrdinalIgnoreCase );
	}

	// Inputs may have changed while the user was making decisions. Recheck them before applying the result.
	internal static void VerifySnapshot(
		LoadedBlobMerge loaded,
		bool ignoreLoadOrder = false,
		IEnumerable<MergeInputFile>? runGuards = null )
	{
		string modIoConfiguration = Path.Combine( Path.GetDirectoryName( loaded.SettingsPath )!, "modioModsConfig.json" );
		IEnumerable<MergeInputFile> inputs = loaded.Snapshot;

		if ( ignoreLoadOrder )
		{
			inputs = loaded.Snapshot
				.Where( input => !input.Path.Equals( loaded.SettingsPath, StringComparison.OrdinalIgnoreCase )
					&& !input.Path.Equals( modIoConfiguration, StringComparison.OrdinalIgnoreCase ) )
				.ToArray();
		}

		MergeBuilder.VerifyInputs( inputs );
		MergeBuilder.VerifyInputs( loaded.Guards );

		if ( runGuards is not null )
			MergeBuilder.VerifyInputs( runGuards );

		VerifySourceSet( loaded );
	}

	internal static void VerifySourceSet( LoadedBlobMerge loaded )
	{
		bool sameFiles = loaded.ConventionalFolders.SequenceEqual(
				Directory.GetDirectories( loaded.ModsDirectory, "mod*" ).Order( StringComparer.OrdinalIgnoreCase ),
				StringComparer.OrdinalIgnoreCase )
			&& loaded.VanillaPaths.SequenceEqual(
				Directory.GetFiles( loaded.VanillaDirectory, "*.ws", SearchOption.AllDirectories ).Order( StringComparer.OrdinalIgnoreCase ),
				StringComparer.OrdinalIgnoreCase );

		if ( !sameFiles )
			throw new IOException( UiText.T( "BlobMerge", "The installed script set changed. Refresh conflicts and try again." ) );

		foreach ( var source in loaded.Sources )
		{
			string[] current = TrackedSourcePaths( source, loaded.Previous, loaded.SourceReads.Keys, loaded.ExcludedSourceFiles );

			if ( !loaded.SourcePaths[source.Name].SequenceEqual( current, StringComparer.OrdinalIgnoreCase ) )
				throw new IOException( UiText.Format(
					"BlobMerge", "Script inputs were added or removed in {0}. Refresh conflicts and try again.", source.Name ) );
		}
	}

	// Include the saved copies of disabled sources; a normal directory scan cannot find them at their original paths.
	private static string[] TrackedSourcePaths(
		BlobMergeSource source,
		AppliedBlobMerge? previous,
		IEnumerable<string> disabledPaths,
		IReadOnlySet<string>? excludedFiles )
	{
		return SourcePaths( source, previous )
			.Concat( disabledPaths.Where( path => IsInside( path, source.Directory ) ) )
			.Where( path => excludedFiles?.Contains( path ) != true )
			.Distinct( StringComparer.OrdinalIgnoreCase )
			.Order( StringComparer.OrdinalIgnoreCase )
			.ToArray();
	}

	private static string[] SourcePaths( BlobMergeSource source, AppliedBlobMerge? previous = null )
	{
		string content = Path.Combine( source.Directory, "content" );
		var paths = Directory.Exists( content ) ? Directory.GetFiles( content, "*", SearchOption.AllDirectories )
			.Where( path => path.EndsWith( ".ws", StringComparison.OrdinalIgnoreCase ) || Path.GetFileName( path ).Equals( "precompiled.rsblob", StringComparison.OrdinalIgnoreCase ) ) : Enumerable.Empty<string>();

		if ( previous is not null )
		{
			paths = paths.Where( path => !previous.Manifest.Outputs.Any( output => Path.GetFullPath( Path.Combine( previous.Manifest.ModsRoot, output.Path ) ).Equals( path, StringComparison.OrdinalIgnoreCase ) ) )
				.Concat( previous.Manifest.Inputs.Where( input => input.SuppressedPath is not null && input.Path.StartsWith( content + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase )
					&& !File.Exists( input.Path ) && File.Exists( input.SuppressedPath )
					&& ( Path.GetFileName( input.Path ).Equals( "precompiled.rsblob", StringComparison.OrdinalIgnoreCase )
						|| !File.Exists( Path.Combine( source.Directory, "content", "precompiled.rsblob" ) ) ) ).Select( input => input.Path ) );
		}

		return paths.Distinct( StringComparer.OrdinalIgnoreCase ).Order( StringComparer.OrdinalIgnoreCase ).ToArray();
	}

	internal static IReadOnlyList<string> Diagnostics( string modsDirectory )
	{
		BlobMergeStore store;

		if ( string.IsNullOrWhiteSpace( modsDirectory ) || !Directory.Exists( modsDirectory ) )
			return Array.Empty<string>();

		store = BlobMergeStore.ForApplication();

		if ( store.HasUpdateJournal )
			return new[] { UiText.T( "BlobMerge", "An update was interrupted. Close The Witcher 3 and refresh to recover the merge." ) };

		if ( store.HasPendingJournal )
			return new[] { UiText.T( "BlobMerge", "A script merge was interrupted. Close The Witcher 3 and refresh to recover it." ) };

		try
		{
			AppliedBlobMerge applied;

			if ( !BlobMergeTransaction.HasState( store.InventoryPath ) )
				return Array.Empty<string>();

			applied = BlobMergeTransaction.ReadApplied( store.InventoryPath, modsDirectory, store.InventoryPath );

			if ( BlobMergeInventory.IsRegistered( applied ) )
				return Array.Empty<string>();

			return new[] { UiText.T(
				"BlobMerge",
				"The merge inventory is missing managed entries. Restore the complete MergeInventory.xml backup before rebuilding these merges." ) };
		}
		catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException
			or ArgumentException or InvalidOperationException )
		{
			return new[] { UiText.Format( "BlobMerge", "The managed blob merge could not be checked: {0}", error.Message ) };
		}
	}
}
