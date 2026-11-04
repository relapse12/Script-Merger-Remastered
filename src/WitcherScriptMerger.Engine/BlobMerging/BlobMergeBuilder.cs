#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using WSCompiler.Core.Scripts.Standalone;
using WSCompiler;
using WitcherScriptMerger.Merging;
using WSCompiler.Recovery;

namespace WitcherScriptMerger.BlobMerging;

// Connects the loaded mods to MergeBuilder: which files to read, which originals to disable,
// and which scripts the compiler needs as references.
internal static class BlobMergeBuilder
{
	internal static MergeBuildResult Build(
		LoadedBlobMerge loaded,
		BlobMergeAdoption adoption,
		MergePlan plan,
		IReadOnlyDictionary<string, MergeChoice> choices,
		string output,
		Action<string, string>? progress = null )
	{
		List<MergeInputFile> inputs;
		HashSet<string> referenceMods;
		(IReadOnlyList<string> Paths, IReadOnlyList<SourceFile> Sources) references;
		(string Name, int? Priority) placement;
		MergeInputFile? cachedCompilation;

		progress?.Invoke( UiText.T( "BlobMerge", "Checking merge inputs" ), "" );
		BlobMergeWorkspace.VerifySourceSet( loaded );

		plan = adoption.OutputPlan( loaded, plan );
		inputs = SnapshotInputs( loaded, adoption );

		foreach ( BlobMergeSource source in loaded.Sources.Where( source => plan.IncludedMods.Contains( source.Name ) ) )
			AddSourceInputs( inputs, loaded, plan, source );

		referenceMods = MergePlanner.ReferenceMods( plan, loaded.Modules, loaded.Vanilla ).ToHashSet( StringComparer.OrdinalIgnoreCase );
		RemoveUnusedSourceInputs( inputs, loaded, plan, referenceMods );
		AddDisabledReferenceInputs( inputs, loaded, referenceMods );

		if ( plan.NeedsCompilation )
			progress?.Invoke( UiText.T( "BlobMerge", "Preparing required compilation" ), "" );

		references = CompilerReferences( loaded, loaded.Sources.Where( source => referenceMods.Contains( source.Name ) ) );
		placement = CompiledOutputPlacement.ForMerge( loaded, plan );
		RefuseUnownedOutputs( loaded, adoption, plan );
		cachedCompilation = ReusableCompilation( loaded, plan );

		return MergeBuilder.Build(
			plan,
			choices,
			loaded.VanillaDirectory,
			references.Paths,
			output,
			loaded.ModsDirectory,
			inputs.DistinctBy( input => input.Path, StringComparer.OrdinalIgnoreCase ).ToArray(),
			placement.Priority,
			loaded.SettingsHash,
			compiledModName: placement.Name,
			compiledAlphabetical: plan.NeedsCompilation && placement.Priority is null,
			looseModName: loaded.LooseModName,
			sharedLooseFolder: loaded.InventoryPath is not null,
			cachedCompilation: cachedCompilation,
			referenceSources: references.Sources,
			progress: stage => progress?.Invoke( StageText( stage ), "" ) );
	}

	private static List<MergeInputFile> SnapshotInputs( LoadedBlobMerge loaded, BlobMergeAdoption adoption )
	{
		HashSet<string> adoptedOutputs = adoption.Merges
			.Select( merge => Path.Combine( loaded.ModsDirectory, loaded.LooseModName, "content", "scripts", merge.RelativePath ) )
			.ToHashSet( StringComparer.OrdinalIgnoreCase );

		return loaded.Snapshot.Where( input => input.ReadPath is null && !adoptedOutputs.Contains( input.Path ) ).ToList();
	}

	private static void AddSourceInputs( List<MergeInputFile> inputs, LoadedBlobMerge loaded, MergePlan plan, BlobMergeSource source )
	{
		string scripts = Path.Combine( source.Directory, "content", "scripts" );
		string[] paths;

		if ( source.IsModIo )
			throw new InvalidOperationException( UiText.T( "BlobMerge", "Mod.io sources are read-only and cannot be merged." ) );

		paths = RecordedPaths( loaded, plan, source, scripts );

		if ( source.Blobs.Count == 0 )
		{
			inputs.RemoveAll( input => BlobMergeWorkspace.IsInside( input.Path, scripts )
				&& !paths.Contains( input.Path, StringComparer.OrdinalIgnoreCase ) );
		}

		foreach ( string path in paths )
		{
			string readPath = loaded.ReadPath( path );
			MergeInputFile? snapshotInput = inputs.FirstOrDefault( input => IsSamePath( input.Path, path ) );
			string hash = snapshotInput?.Sha256 ?? MergeBuilder.Hash( readPath );
			bool suppress;

			inputs.RemoveAll( input => IsSamePath( input.Path, path ) );
			suppress = MustSuppress( loaded, plan, source, path );

			inputs.Add( new MergeInputFile(
				path,
				hash,
				suppress ? path + ".smr-disabled" : null,
				IsSamePath( readPath, path ) ? null : readPath ) );
		}
	}

	// A blob represents the mod's whole compiled source. For loose mods, record only the files used by this plan.
	private static string[] RecordedPaths( LoadedBlobMerge loaded, MergePlan plan, BlobMergeSource source, string scripts )
	{
		if ( source.Blobs.Count > 0 || plan.SourceFiles is null )
		{
			IEnumerable<string> looseScripts = loaded.SourcePaths[source.Name]
				.Where( path => path.EndsWith( ".ws", StringComparison.OrdinalIgnoreCase ) );

			return source.Blobs.Concat( looseScripts ).ToArray();
		}

		return plan.SourceFiles
			.Where( file => file.Mod.Equals( source.Name, StringComparison.OrdinalIgnoreCase ) )
			.Select( file => MergeBuilder.SafePath( scripts, file.Path ) )
			.Distinct( StringComparer.OrdinalIgnoreCase )
			.ToArray();
	}

	private static void RemoveUnusedSourceInputs(
		List<MergeInputFile> inputs,
		LoadedBlobMerge loaded,
		MergePlan plan,
		HashSet<string> referenceMods )
	{
		BlobMergeSource[] unused = loaded.Sources
			.Where( source => !plan.IncludedMods.Contains( source.Name, StringComparer.OrdinalIgnoreCase )
				&& !referenceMods.Contains( source.Name ) )
			.ToArray();

		inputs.RemoveAll( input => unused.Any( source => BlobMergeWorkspace.IsInside( input.Path, source.Directory ) ) );
	}

	// Reference files disabled by an earlier merge still affect compilation and need to be tracked.
	private static void AddDisabledReferenceInputs( List<MergeInputFile> inputs, LoadedBlobMerge loaded, HashSet<string> referenceMods )
	{
		BlobMergeSource[] references = loaded.Sources.Where( source => referenceMods.Contains( source.Name ) ).ToArray();

		foreach ( MergeInputFile input in loaded.Snapshot.Where( input => input.ReadPath is not null ) )
		{
			bool isRecorded = inputs.Any( added => IsSamePath( added.Path, input.Path ) );
			bool isInReference = references.Any( source => BlobMergeWorkspace.IsInside( input.Path, source.Directory ) );

			if ( !isRecorded && isInReference )
				inputs.Add( input );
		}
	}

	// Only replace files recorded as outputs of this merge or an ordinary merge it includes.
	private static void RefuseUnownedOutputs( LoadedBlobMerge loaded, BlobMergeAdoption adoption, MergePlan plan )
	{
		IReadOnlyDictionary<string, string> replaceableOutputs = adoption.ReplaceableOutputs( loaded );

		foreach ( PlannedFile file in plan.Files.Where( file => !file.Compile ) )
		{
			string path = loaded.LooseModName + "/content/scripts/" + file.Path;
			string physicalPath;

			if ( replaceableOutputs.ContainsKey( path.Replace( '/', Path.DirectorySeparatorChar ) ) )
				continue;

			physicalPath = MergeBuilder.SafePath( loaded.ModsDirectory, path );

			if ( File.Exists( physicalPath ) || Directory.Exists( physicalPath ) )
				throw new IOException( UiText.Format( "BlobMerge", "An existing merge already owns {0}. Remove that merge before rebuilding.", path ) );
		}
	}

	// Reuse the previous blob only when its saved compile key still matches this plan.
	private static MergeInputFile? ReusableCompilation( LoadedBlobMerge loaded, MergePlan plan )
	{
		string keyPath;
		string blobPath;
		MergeOutputFile? blob;

		if ( !plan.NeedsCompilation || loaded.Previous is null )
			return null;

		keyPath = Path.Combine( BlobMergeTransaction.WorkspacePath( loaded.StateFile!, loaded.Previous.Manifest.Id ), "compilation.key" );
		blob = loaded.Previous.Manifest.Outputs
			.SingleOrDefault( output => output.Path.EndsWith( ".rsblob", StringComparison.OrdinalIgnoreCase ) );

		if ( blob is null )
			return null;

		blobPath = MergeBuilder.SafePath( loaded.ModsDirectory, blob.Path );

		if ( !File.Exists( blobPath ) || !File.Exists( keyPath ) || File.ReadAllText( keyPath ) != CompilationKey( loaded, plan ) )
			return null;

		return new MergeInputFile( blobPath, blob.Sha256 );
	}

	private static string StageText( MergeBuildStage stage )
	{
		return stage switch
		{
			MergeBuildStage.VerifyInputs => UiText.T( "BlobMerge", "Checking merge inputs" ),
			MergeBuildStage.WriteLooseScripts => UiText.T( "BlobMerge", "Writing merged loose scripts" ),
			MergeBuildStage.ReuseCompiledScripts => UiText.T( "BlobMerge", "Reusing unchanged compiled scripts" ),
			MergeBuildStage.CompileScripts => UiText.T( "BlobMerge", "Compiling required scripts" ),
			_ => UiText.T( "BlobMerge", "Preparing merge inventory" )
		};
	}

	// Disable the original blobs so their definitions do not run alongside the merged result.
	// Loose files can stay active when the new file overrides the same path; annotations and added files need explicit disabling.
	internal static bool MustSuppress( LoadedBlobMerge loaded, MergePlan plan, BlobMergeSource source, string path )
	{
		string relative;
		SourceFile? original;

		if ( path.EndsWith( ".rsblob", StringComparison.OrdinalIgnoreCase ) )
			return true;

		relative = NormalizedPath( Path.GetRelativePath( Path.Combine( source.Directory, "content", "scripts" ), path ) );

		if ( plan.Files.Any( file => !file.Compile && IsSameScript( file.Path, relative ) ) )
			return false;

		if ( source.Blobs.Count > 0 )
			return true;

		original = loaded.Modules
			.Single( module => module.Name.Equals( source.Name, StringComparison.OrdinalIgnoreCase ) ).Files
			.FirstOrDefault( file => IsSameScript( file.Path, relative ) );

		if ( original?.Items.Any( IsAnnotated ) == true )
			return true;

		return !loaded.Vanilla.Any( file => IsSameScript( file.Path, relative ) );
	}

	private static bool IsAnnotated( Item item )
	{
		if ( item is ClassItem type )
		{
			return type.Decl.Methods.Any( method => method.Annotation is not null )
				|| type.Decl.Fields.Any( field => field.Annotation is not null );
		}

		return item is FunctionItem { Decl.Annotation: not null } or FieldItem { Decl.Annotation: not null };
	}

	internal static string CompilationKey( LoadedBlobMerge loaded, MergePlan plan )
	{
		HashSet<string> references = MergePlanner.ReferenceMods( plan, loaded.Modules, loaded.Vanilla )
			.ToHashSet( StringComparer.OrdinalIgnoreCase );
		HashSet<string> requiredLoose = MergePlanner.LooseCompilationReferences( plan, loaded.Modules, loaded.Vanilla )
			.Select( file => file.Path )
			.ToHashSet( StringComparer.OrdinalIgnoreCase );

		var compiledFiles = plan.Files
			.Where( file => file.Compile || requiredLoose.Contains( file.Path ) )
			.Select( file => new
			{
				file.Path,
				file.Source,
				Types = file.Items.OfType<ClassItem>().Select( item => new
				{
					item.Decl.RequiresBinaryDefaults,
					item.Decl.BinaryDefaults,
					Functions = item.Decl.Methods.Select( method => new { method.Name, method.CompiledFlags } )
				} ),
				Functions = file.Items.OfType<FunctionItem>().Select( item => new { item.Decl.Name, item.Decl.CompiledFlags } )
			} );

		var compileInputs = loaded.Snapshot
			.Where( input => BlobMergeWorkspace.IsInside( input.Path, loaded.VanillaDirectory )
				|| loaded.Sources.Any( source => references.Contains( source.Name )
					&& BlobMergeWorkspace.IsInside( input.Path, source.Directory ) ) )
			.Select( input => new { input.Path, input.Sha256 } );

		var source = new
		{
			Compiler = MergeBuilder.Hash( typeof( WSCompiler.Compiler ).Assembly.Location ),
			Files = compiledFiles,
			Inputs = compileInputs
		};

		return MergeBuilder.Hash( JsonSerializer.SerializeToUtf8Bytes( source ) );
	}

	// Disabled files and saved ordinary merges may have no active path to read. Pass their parsed source directly.
	internal static (IReadOnlyList<string> Paths, IReadOnlyList<SourceFile> Sources) CompilerReferences(
		LoadedBlobMerge loaded,
		IEnumerable<BlobMergeSource> sources )
	{
		var paths = new List<string>();
		var declarations = new List<SourceFile>();

		foreach ( BlobMergeSource source in sources )
		{
			string scripts = Path.Combine( source.Directory, "content", "scripts" );

			if ( source.Blobs.Count > 0 )
			{
				foreach ( string blob in source.Blobs )
				{
					string readPath = loaded.ReadPath( blob );

					if ( readPath == blob )
						paths.Add( blob );
					else
						declarations.Add( BlobReferences.Read( File.ReadAllBytes( readPath ), blob ) );
				}
			}
			else if ( source.ExistingLooseMerge || HasDisabledFiles( loaded, source ) )
			{
				MergeModule? module = loaded.Modules.FirstOrDefault( module => module.Name == source.Name );

				declarations.AddRange( ( module?.Files ?? [] ).Select( file =>
					new SourceFile( file.Path, Parser.ParseSource( SourceWriter.Write( file.Items ), skipBodies: true ) ) ) );
			}
			else if ( Directory.Exists( scripts ) )
			{
				paths.Add( scripts );
			}
		}

		return (paths, declarations);
	}

	private static bool HasDisabledFiles( LoadedBlobMerge loaded, BlobMergeSource source )
	{
		return loaded.SourceReads.Keys
			.Any( path => path.StartsWith( source.Directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase ) );
	}

	private static bool IsSamePath( string left, string right )
	{
		return left.Equals( right, StringComparison.OrdinalIgnoreCase );
	}

	private static bool IsSameScript( string left, string right )
	{
		return IsSamePath( NormalizedPath( left ), NormalizedPath( right ) );
	}

	private static string NormalizedPath( string path )
	{
		return path.Replace( '\\', '/' );
	}
}
