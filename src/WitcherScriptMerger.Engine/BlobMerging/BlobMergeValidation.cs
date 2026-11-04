#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using WSCompiler.Core;
using WSCompiler.Core.Scripts.Standalone;
using WSCompiler;
using WitcherScriptMerger.Merging;

namespace WitcherScriptMerger.BlobMerging;

internal sealed class BlobMergeValidationException( IReadOnlyList<Diagnostic> diagnostics ) : IOException( "Merged scripts failed validation." )
{
	internal IReadOnlyList<Diagnostic> Diagnostics { get; } = diagnostics;
	internal string Details => string.Join( Environment.NewLine, Diagnostics.Select( value =>
		$"{value.Severity} {value.Code} {value.File}{( value.Line > 0 ? "(" + value.Line + ")" : "" )}: {value.Message}" ) );
}

internal sealed record SuccessfulBlobValidation( Diagnostic[] Diagnostics );

// Validate the generated files using declarations already read by the scan.
internal static class BlobMergeValidation
{
	private sealed record ReferenceFile( MergeModule Module, SourceFile Source, string Physical );

	private sealed record OutputBytes( MergeOutputFile File, byte[] Bytes );

	// Repeated checks of the same loaded inputs can share prepared compiler references.
	private static ConditionalWeakTable<LoadedBlobMerge, Dictionary<string, CompilationReferences>> environments = new();
	private static ConditionalWeakTable<LoadedBlobMerge, Dictionary<string, Diagnostic[]>> validated = new();

	internal static void ClearMemory()
	{
		environments = new();
		validated = new();
	}

	internal static IReadOnlyList<Diagnostic> Validate(
		LoadedBlobMerge loaded,
		MergeBuildResult build,
		Action<string, string>? progress = null )
	{
		OutputBytes[] outputs;

		progress?.Invoke( UiText.T( "BlobMerge", "Validating merged scripts" ), "" );
		outputs = ReadOutputs( build );

		return new OutputCheck( loaded, build, outputs ).Run();
	}

	private static OutputBytes[] ReadOutputs( MergeBuildResult build )
	{
		return build.Manifest.Outputs
			.OrderBy( file => file.Path, StringComparer.OrdinalIgnoreCase )
			.Select( file =>
			{
				byte[] bytes = File.ReadAllBytes( MergeBuilder.SafePath( build.Directory, file.Path ) );

				if ( MergeBuilder.Hash( bytes ) != file.Sha256 )
					throw new IOException( "Merged output changed before validation: " + file.Path );

				return new OutputBytes( file, bytes );
			} )
			.ToArray();
	}

	private sealed class OutputCheck
	{
		private const string OutputName = "<merged-output>";

		private readonly LoadedBlobMerge loaded;
		private readonly MergeBuildResult build;
		private readonly OutputBytes[] outputs;
		private readonly string outputKey;
		private readonly Dictionary<string, Diagnostic[]> completed;
		private readonly List<Diagnostic> diagnostics = new();
		private readonly List<SourceFile> project = new();
		private ReferenceFile[] active = [];

		internal OutputCheck( LoadedBlobMerge loaded, MergeBuildResult build, OutputBytes[] outputs )
		{
			this.loaded = loaded;
			this.build = build;
			this.outputs = outputs;
			outputKey = BlobMergeArtifacts.Identity( new
			{
				Outputs = outputs.Select( output => new { output.File.Path, output.File.Sha256 } ),
				Suppressed = build.Manifest.Inputs.Where( input => input.SuppressedPath is not null )
					.Select( input => input.Path ).Order( StringComparer.OrdinalIgnoreCase )
			} );
			completed = validated.GetOrCreateValue( loaded );
		}

		// Stop after a stage reports errors so later checks do not add misleading follow-on errors.
		internal IReadOnlyList<Diagnostic> Run()
		{
			lock ( completed )
			{
				if ( completed.TryGetValue( outputKey, out Diagnostic[]? unchanged ) )
					return unchanged;
			}

			ParseScripts();
			ThrowErrors( diagnostics );

			if ( project.Count == 0 )
				return build.Diagnostics;

			SelectActiveReferences();
			FindLooseDuplicates();
			FindVanillaEnumCopies();
			ThrowErrors( diagnostics );

			return CompileProject();
		}

		private IEnumerable<OutputBytes> OutputsEndingWith( string extension )
		{
			return outputs.Where( output => output.File.Path.EndsWith( extension, StringComparison.OrdinalIgnoreCase ) );
		}

		// Collect parse errors from all output files so the user can see them in one report.
		private void ParseScripts()
		{
			foreach ( OutputBytes output in OutputsEndingWith( ".ws" ) )
			{
				string path = output.File.Path;

				try
				{
					string text = TextDecoding.Decode( output.Bytes );

					diagnostics.AddRange( NativeSyntax( text, path ) );
					project.Add( new( path, Parser.ParseSource( text, false ) ) );
				}
				catch ( ScriptSyntaxException error )
				{
					diagnostics.Add( new( "error", "C006", path, error.Line, error.Message ) );
				}
				catch ( Exception error ) when ( error is OverflowException or FormatException )
				{
					diagnostics.Add( new( "error", "C008", path, 0, error.Message ) );
				}
			}
		}

		// For each loose-script path, compile against the source that wins in the game's load order.
		private void SelectActiveReferences()
		{
			HashSet<string> suppressed = build.Manifest.Inputs.Where( input => input.SuppressedPath is not null )
				.Select( input => Path.GetFullPath( input.Path ) ).ToHashSet( StringComparer.OrdinalIgnoreCase );
			HashSet<string> replaced = project.Select( file => Relative( file.Path ) ).ToHashSet( StringComparer.OrdinalIgnoreCase );
			List<ReferenceFile> candidates = new();

			foreach ( MergeModule module in loaded.ReferenceModules )
			{
				BlobMergeSource source = loaded.Sources.Single( item => item.Name.Equals( module.Name, StringComparison.OrdinalIgnoreCase ) );

				if ( module.IsBlob )
				{
					foreach ( SourceFile file in module.Files.Where( file => Path.IsPathRooted( file.Path ) && !suppressed.Contains( Path.GetFullPath( file.Path ) ) ) )
						candidates.Add( new( module, file, file.Path ) );

					continue; // Loose scripts beside an active blob are not loaded by the game.
				}

				foreach ( SourceFile file in module.Files )
				{
					string physical = MergeBuilder.SafePath( Path.Combine( source.Directory, "content", "scripts" ), file.Path );

					if ( !replaced.Contains( Normal( file.Path ) ) && !suppressed.Contains( physical ) )
						candidates.Add( new( module, file, physical ) );
				}
			}

			// Keep unrelated reference files when replacing the winning version of a loose-script path.
			active = candidates.Where( file => file.Module.IsBlob )
				.Concat( candidates.Where( file => !file.Module.IsBlob )
					.GroupBy( file => Normal( file.Source.Path ), StringComparer.OrdinalIgnoreCase )
					.Select( group => group.OrderBy( file => file.Module.Order ).First() ) )
				.ToArray();
		}

		// An original loose file still active beside the output would declare the same thing twice.
		private void FindLooseDuplicates()
		{
			HashSet<string> projectKeys = project
				.SelectMany( file => file.Items.Select( Key ).OfType<string>() )
				.ToHashSet( StringComparer.Ordinal );

			foreach ( ReferenceFile file in active.Where( file => !file.Module.IsBlob ) )
			{
				foreach ( string key in file.Source.Items.Select( Key ).OfType<string>().Where( projectKeys.Contains ) )
				{
					diagnostics.Add( new( "error", "V001", file.Physical, 1,
						$"'{key[( key.IndexOf( ':' ) + 1 )..]}' is also declared by the merged scripts. Its original loose file is still active." ) );
				}
			}
		}

		private void FindVanillaEnumCopies()
		{
			foreach ( SourceFile file in project )
			{
				foreach ( EnumItem enumeration in file.Items.OfType<EnumItem>() )
				{
					SourceFile? vanilla = loaded.Vanilla.FirstOrDefault( original =>
						original.Items.OfType<EnumItem>().Any( item => item.Decl.Name == enumeration.Decl.Name ) );

					if ( vanilla is null || Normal( vanilla.Path ).Equals( Relative( file.Path ), StringComparison.OrdinalIgnoreCase ) )
						continue;

					var members = vanilla.Items.OfType<EnumItem>().Single( item => item.Decl.Name == enumeration.Decl.Name ).Decl.Members;

					if ( members.Count > 0 && enumeration.Decl.Members.Take( members.Count ).SequenceEqual( members ) )
					{
						diagnostics.Add( new( "error", "V002", file.Path, 1,
							$"The vanilla enum '{enumeration.Decl.Name}' is duplicated outside its original file ({vanilla.Path})." ) );
					}
				}
			}
		}

		private IReadOnlyList<Diagnostic> CompileProject()
		{
			List<MergeModule> referenceModules = ReferenceModules();
			SourceFile[] references = RequiredReferences( referenceModules );
			string environmentKey = EnvironmentKey( references );
			string identity = BlobMergeArtifacts.Identity( new
			{
				environmentKey,
				Outputs = outputs.Select( output => new { output.File.Path, output.File.Sha256 } )
			} );
			CompilationReferences environment;

			if ( BlobMergeArtifacts.Read<SuccessfulBlobValidation>( "output-validation", identity ) is { } cached )
			{
				Remember( cached.Diagnostics );

				return cached.Diagnostics;
			}

			environment = PrepareEnvironment( environmentKey, references );
			BlobMergeArtifacts.RecordBuild( "output-validation" );
			diagnostics.AddRange( Compiler.CompileSources( project, loaded.VanillaDirectory, preparedReferences: environment ).Diagnostics );
			ThrowErrors( diagnostics );
			BlobMergeArtifacts.Write( "output-validation", identity, new SuccessfulBlobValidation( diagnostics.ToArray() ) );
			Remember( diagnostics.ToArray() );

			return diagnostics;
		}

		private List<MergeModule> ReferenceModules()
		{
			List<MergeModule> modules = active
				.GroupBy( file => file.Module.Name, StringComparer.OrdinalIgnoreCase )
				.Select( group => group.First().Module with { Files = group.Select( file => file.Source ).ToArray() } )
				.ToList();

			foreach ( OutputBytes output in OutputsEndingWith( ".rsblob" ) )
				modules.Add( new( "<merged-compiled>", [BlobReferences.Read( output.Bytes, output.File.Path )], IsBlob: true ) );

			modules.Add( new( OutputName, project ) );

			return modules;
		}

		// Follow references needed by the output. Unrelated loose scripts do not need a merge plan.
		private SourceFile[] RequiredReferences( List<MergeModule> referenceModules )
		{
			MergeDependencyScope scope = MergeDependencyScope.Create( referenceModules, loaded.Vanilla, new HashSet<string> { OutputName },
				new( [], [], [], [], [], SourceFiles: [] ) );
			var required = scope.Files.Where( file => file.Mod != OutputName ).ToArray();

			return referenceModules
				.Where( module => module.Name != OutputName )
				.SelectMany( module => module.Files.Where( file => required.Any( item => item.Mod.Equals( module.Name, StringComparison.OrdinalIgnoreCase )
					&& Normal( item.Path ).Equals( Normal( file.Path ), StringComparison.OrdinalIgnoreCase ) ) ) )
				.ToArray();
		}

		private string EnvironmentKey( SourceFile[] references )
		{
			HashSet<string> referencePaths = active.Where( file => references.Contains( file.Source ) )
				.Select( file => file.Physical ).ToHashSet( StringComparer.OrdinalIgnoreCase );
			string vanillaFolder = Path.TrimEndingDirectorySeparator( loaded.VanillaDirectory ) + Path.DirectorySeparatorChar;

			return BlobMergeArtifacts.Identity( new
			{
				Vanilla = loaded.Snapshot.Where( input => input.Path.StartsWith( vanillaFolder, StringComparison.OrdinalIgnoreCase ) )
					.Select( input => new { input.Path, input.Sha256 } ),
				References = loaded.Snapshot.Where( input => referencePaths.Contains( input.Path ) ).Select( input => new { input.Path, input.Sha256 } ),
				Compiled = OutputsEndingWith( ".rsblob" ).Select( output => output.File.Sha256 ),
				// Different winning providers can change compilation even when the reference names are the same.
				Sources = active.Where( file => references.Contains( file.Source ) ).Select( file => new { file.Module.Name, file.Source.Path } )
			} );
		}

		private CompilationReferences PrepareEnvironment( string environmentKey, SourceFile[] references )
		{
			Dictionary<string, CompilationReferences> prepared = environments.GetOrCreateValue( loaded );

			lock ( prepared )
			{
				if ( prepared.TryGetValue( environmentKey, out CompilationReferences? environment ) )
					return environment;

				if ( prepared.Count >= 4 )
					prepared.Clear();

				environment = Compiler.PrepareReferences( loaded.Vanilla, references );
				prepared[environmentKey] = environment;
				BlobMergeArtifacts.RecordBuild( "validation-environment" );

				return environment;
			}
		}

		private void Remember( Diagnostic[] values )
		{
			lock ( completed )
			{
				if ( completed.Count >= 8 )
					completed.Clear();

				completed[outputKey] = values;
			}
		}
	}

	// WSCompiler accepts some syntax the game rejects in loose scripts. Catch those cases before applying the merge.
	internal static IReadOnlyList<Diagnostic> NativeSyntax( string text, string path )
	{
		var result = new List<Diagnostic>();
		var tokens = Lexer.Tokenize( text );

		for ( int index = 0; index < tokens.Count; index++ )
		{
			var token = tokens[index];

			if ( token.Kind == TokKind.Punct && token.Text == "@" && index + 1 < tokens.Count
				&& tokens[index + 1].Text is "addMethod" or "wrapMethod" or "replaceMethod" )
			{
				int declaration = index + 2;

				if ( declaration < tokens.Count && tokens[declaration].Text == "(" )
				{
					while ( declaration < tokens.Count && tokens[declaration].Text != ")" )
						declaration++;

					declaration++;
				}

				while ( declaration < tokens.Count && tokens[declaration].Text is "public" or "private" or "protected" or "import"
					or "final" or "latent" or "entry" or "exec" or "timer" or "quest" or "storyscene" or "cleanup" or "reward" )
					declaration++;

				if ( declaration < tokens.Count && tokens[declaration].Text == "event" )
					result.Add( new( "error", "V005", path, tokens[declaration].Line,
						"The game cannot add an event through a loose-script annotation. This declaration requires compiled output." ) );
			}

			if ( token.Kind == TokKind.Float && token.Text.Contains( 'e', StringComparison.OrdinalIgnoreCase ) )
				result.Add( new( "error", "V003", path, token.Line, "The game cannot read exponent-form numbers in loose scripts. Use a decimal number." ) );

			if ( token.Kind == TokKind.Ident && token.Text is "case" or "default" )
			{
				int colon = index + 1;

				while ( colon < tokens.Count && tokens[colon].Text is not ( ":" or "}" or ";" ) )
					colon++;

				if ( colon + 1 < tokens.Count && tokens[colon].Text == ":" && tokens[colon + 1].Text == "}" )
					result.Add( new( "error", "V004", path, token.Line, "The last switch branch is empty. The game requires a statement such as break;." ) );
			}
		}

		return result;
	}

	private static string? Key( Item item ) => item switch
	{
		ClassItem { Decl.IsImport: false } value => "type:" + Symbols.ClassKey( value.Decl ),
		FunctionItem { Decl.Annotation: null, Decl.Specifiers.IsImport: false } value => "function:" + value.Decl.Name,
		_ => null
	};

	private static string Normal( string path ) => path.Replace( '\\', '/' );

	private static string Relative( string path ) => Normal( path ).Split( "/content/scripts/", 2, StringSplitOptions.None )[^1];

	private static void ThrowErrors( IReadOnlyList<Diagnostic> diagnostics )
	{
		if ( diagnostics.Any( value => value.Severity == "error" ) )
			throw new BlobMergeValidationException( diagnostics );
	}
}
