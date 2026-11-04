#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WSCompiler.Core.Scripts;
using WSCompiler.Core.Scripts.Standalone;
using WSCompiler;
using WitcherScriptMerger.Merging;
using WSCompiler.Recovery;

namespace WitcherScriptMerger.BlobMerging;

// Compiled and loose scripts can express the same method differently. Recover both through the compiler
// so comparisons use a consistent form, then check that recompilation preserves the signature and control flow.
internal static class BlobBodyComparisons
{
	// Try only methods that also exist in vanilla. Every side, including vanilla, must pass the checks
	// before the conflict can use recovered text. A successful change requires a new merge plan.
	internal static bool Prepare( LoadedBlobMerge loaded, MergePlan plan, Action<string>? progress = null )
	{
		bool changed = false;

		// Changed inputs need a fresh attempt, even if this conflict failed earlier.
		string Attempt( MergeConflict conflict ) => conflict.Key + ":" + BlobMergeArtifacts.Identity(
			( conflict.Inputs ?? conflict.Options ).Select( option => new { option.Mod, option.Id } ).OrderBy( option => option.Mod, StringComparer.Ordinal ) );

		var conflicts = plan.Conflicts.Where( conflict => conflict.Original is not null && conflict.Key.StartsWith( "type:", StringComparison.Ordinal )
			&& conflict.Key.Contains( "/method:", StringComparison.Ordinal )
			&& !loaded.PlanningCache.WasAttempted( Attempt( conflict ) ) ).ToArray();

		if ( conflicts.Length == 0 )
			return false;

		progress?.Invoke( conflicts[0].Key );

		var declarations = loaded.Vanilla.Concat( loaded.Modules.SelectMany( module => module.Files ) ).ToArray();
		ControlFlowContext? context = null;
		var enums = EnumResolver.ReadEnums( declarations.SelectMany( file => file.Items ).OfType<EnumItem>().Select( item => item.Decl ) );
		CompilationReferences? prepared = null;
		// Other scripts and enum values can affect compilation, so include them in the cache key.
		string environment = BlobMergeArtifacts.Identity( new
		{
			Inputs = loaded.Snapshot.Where( input => input.Path.EndsWith( ".ws", StringComparison.OrdinalIgnoreCase )
			|| input.Path.EndsWith( ".rsblob", StringComparison.OrdinalIgnoreCase ) ).Select( input => new { input.Path, input.Sha256 } ),
			Enums = enums
		} );

		foreach ( var conflict in conflicts )
		{
			progress?.Invoke( conflict.Key );
			// Remember failures too, so this load does not keep retrying the same compilation.
			loaded.PlanningCache.RecordAttempt( Attempt( conflict ) );

			int separator = conflict.Key.IndexOf( "/method:", StringComparison.Ordinal );
			string owner = conflict.Key[5..separator], method = conflict.Key[( separator + 8 )..];
			var compared = new Dictionary<string, MergeBodyComparison>( StringComparer.Ordinal );

			try
			{
				foreach ( string mod in ( conflict.Inputs ?? conflict.Options ).Select( option => option.Mod ).Prepend( "Vanilla" ).Distinct( StringComparer.Ordinal ) )
				{
					var files = mod == "Vanilla" ? loaded.Vanilla : loaded.Modules.Single( module => module.Name == mod ).Files;
					var type = files.SelectMany( file => file.Items ).OfType<ClassItem>().Single( item => Symbols.ClassKey( item.Decl ) == owner ).Decl;
					var function = type.Methods.Single( value => value.Name == method );

					if ( function.Annotation is not null )
						throw new InvalidDataException( "Only ordinary bodies have comparison forms." );

					var partial = new ClassItem( new ClassDecl
					{
						Name = type.Name,
						Parent = type.Parent,
						StateMachineOf = type.StateMachineOf,
						IsImport = type.IsImport,
						IsAbstract = type.IsAbstract,
						IsStateMachine = type.IsStateMachine,
						IsStruct = type.IsStruct,
						Methods = [function]
					} );
					string identity = BlobMergeArtifacts.Identity( new
					{
						environment,
						Partial = partial
					} );
					var cached = BlobMergeArtifacts.Read<MergeBodyComparison>( "body-comparison", identity );

					if ( cached is not null )
					{
						compared.Add( mod, cached );

						continue;
					}

					if ( prepared is null )
					{
						var references = BlobMergeBuilder.CompilerReferences( loaded, loaded.Sources );

						prepared = Compiler.PrepareReferences( loaded.VanillaDirectory, references.Paths, referenceSources: references.Sources );
					}

					BlobMergeArtifacts.RecordBuild( "body-comparison" );

					// Recompile the recovered text before allowing it to replace the original comparison text.
					var before = Compiler.CompileSources( [new( "comparison.ws", [partial] )], loaded.VanillaDirectory, preparedReferences: prepared );

					if ( !before.Success )
						throw new InvalidDataException( "The original comparison source did not compile." );

					var recovered = BlobDecompiler.Recover( before.Blob!, "comparison", enums );

					if ( !recovered.CanMerge )
						throw new InvalidDataException( "The comparison source could not be recovered." );

					var after = Compiler.CompileSources( recovered.Sources, loaded.VanillaDirectory, preparedReferences: prepared );

					if ( !after.Success )
						throw new InvalidDataException( "The reconstructed comparison source did not compile." );

					var left = ScriptBlob.FromBytes( before.Blob!, "comparison before" );
					var right = ScriptBlob.FromBytes( after.Blob!, "comparison after" );
					var leftMethod = left.AllFunctions.Single();
					var rightMethod = right.AllFunctions.Single();

					context ??= new ControlFlowContext( declarations );

					string contract = BlobComparison.FunctionContract( leftMethod );

					if ( contract != BlobComparison.FunctionContract( rightMethod )
						|| !ControlFlowComparison.Read( left, leftMethod, context ).Graph.SequenceEqual( ControlFlowComparison.Read( right, rightMethod, context ).Graph ) )
						throw new InvalidDataException( "The comparison form changed the function contract or qualified control flow." );

					var result = recovered.Sources.SelectMany( file => file.Items ).OfType<ClassItem>().Single().Decl.Methods.Single();
					var comparison = new MergeBodyComparison( MergeBodyComparison.Identity( new FunctionItem( function ) ),
						SourceWriter.Write( [new FunctionItem( result )] ), contract );

					BlobMergeArtifacts.Write( "body-comparison", identity, comparison );
					compared.Add( mod, comparison );
				}
			}
			// If any side fails, compare the original text for the whole conflict.
			// Successful results already written to disk can still be reused on a later attempt.
			catch ( Exception error ) when ( error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or ScriptSyntaxException or NotSupportedException or FormatException )
			{
				continue;
			}

			foreach ( var pair in compared )
				loaded.PlanningCache.Store( conflict.Key, pair.Key, pair.Value );

			changed = true;
		}

		return changed;
	}
}
