#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using WSCompiler.Core.Scripts.Standalone;
using WitcherScriptMerger.Merging;
using WSCompiler.Recovery;
using WitcherScriptMerger.Tools;
using WSCompiler.Core;

namespace WitcherScriptMerger.BlobMerging;

internal sealed record SavedMergeDecision( MergeConflict Decision, string Source );
internal sealed record SavedMergeFile(
	string Path,
	bool Compile,
	IReadOnlyList<string> Owners,
	IReadOnlyList<MergeSourceFile> Inputs,
	bool Manual = false );
// recipe.json keeps the previous merge's choices and the sources behind each output.
// Rebuilds use it to preserve decisions; removal uses it to find sources that must stay together.
internal sealed record BlobMergeHistory( IReadOnlyList<SavedMergeDecision> Decisions, IReadOnlyList<SavedMergeFile> Files )
{
	// These files must stay unchanged until the merge is applied. This check is kept in memory only.
	internal IReadOnlyList<MergeInputFile> WorkspaceGuards { get; init; } = Array.Empty<MergeInputFile>();

	internal static BlobMergeHistory Load( LoadedBlobMerge loaded, BlobMergeAdoption adoption, MergePlan plan )
	{
		var adopted = new List<SavedMergeDecision>();

		foreach ( var merge in adoption.Merges.Concat( loaded.RetainedAdoptions ) )
		{
			string path = MergeBuilder.SafePath( loaded.ModsDirectory, loaded.LooseModName + "/content/scripts/" + merge.RelativePath );
			var adoptedItems = Parser.ParseSource( TextDecoding.Decode( File.ReadAllBytes( loaded.ReadPath( path ) ) ), false );

			foreach ( var decision in plan.Decisions ?? [] )
			{
				Item? item = Find( decision.Key, adoptedItems );

				if ( item is null )
					continue;

				var inputs = ( decision.Inputs ?? decision.Options ).Where( option => option.Mod.Equals( loaded.LooseModName, StringComparison.OrdinalIgnoreCase ) || merge.Mods.Any( source =>
				{
					if ( !source.Name.Equals( option.Mod, StringComparison.OrdinalIgnoreCase ) )
						return false;

					string? directory = loaded.Sources.FirstOrDefault( source => source.Name.Equals( option.Mod, StringComparison.OrdinalIgnoreCase ) )?.Directory;

					if ( directory is null )
						return false;

					string original = source.SourceFilePath ?? Path.Combine( directory, "content", "scripts", merge.RelativePath );

					return File.Exists( original ) && Hasher.ComputeHash( original ) == source.Hash;
				} ) ).ToArray();
				string adoptedSource = SourceWriter.Write( [item] );
				var current = inputs.FirstOrDefault( option => option.Mod.Equals( loaded.LooseModName, StringComparison.OrdinalIgnoreCase ) );

				if ( current is not null && item is FunctionItem { Decl.Annotation: null }
					&& loaded.PlanningCache.TryGet( decision.Key, current.Mod, out var comparison )
					&& comparison.Input == MergeBodyComparison.Identity( item ) && current.Source == comparison.Source )
					adoptedSource = comparison.Source;

				adopted.Add( new( decision with
				{
					Inputs = inputs,
					Options = inputs
				}, adoptedSource ) );
			}
		}

		if ( loaded.Previous is null )
			return new( adopted, [] );

		string workspace = BlobMergeTransaction.WorkspacePath( loaded.StateFile!, loaded.Previous.Manifest.Id );

		if ( loaded.Previous.Workspace is { } retained )
		{
			var guards = new List<MergeInputFile>();

			foreach ( var file in retained )
			{
				string path = MergeBuilder.SafePath( workspace, file.Path );

				if ( !File.Exists( path ) || MergeBuilder.Hash( path ) != file.Sha256 )
					throw new IOException( UiText.Format( "BlobMerge", "Saved merged source changed or is missing: {0}", path ) );

				guards.Add( new( path, file.Sha256 ) );
			}

			string recipe = Path.Combine( workspace, "recipe.json" );

			if ( File.Exists( recipe ) )
			{
				var history = JsonSerializer.Deserialize<BlobMergeHistory>( File.ReadAllText( recipe ) ) ?? throw new InvalidDataException( UiText.T( "BlobMerge", "Invalid saved merge source." ) );

				return history with
				{
					WorkspaceGuards = guards,
					Decisions = history.Decisions.Select( saved => MatchInputs( saved, plan, loaded ) )
					.Concat( adopted ).DistinctBy( saved => saved.Decision.Key ).ToArray()
				};
			}
		}

		throw new InvalidDataException( UiText.T( "BlobMerge", "Invalid saved merge source." ) );
	}

	// An unchanged input may have different text after comparison cleanup. Match it to the current form
	// so a rebuild does not mistake that spelling change for a new edit.
	private static SavedMergeDecision MatchInputs( SavedMergeDecision saved, MergePlan plan, LoadedBlobMerge loaded )
	{
		var current = plan.Decisions?.FirstOrDefault( decision => decision.Key == saved.Decision.Key );

		if ( current is null )
			return saved;

		var inputs = ( saved.Decision.Inputs ?? saved.Decision.Options ).Select( previous =>
		{
			var next = ( current.Inputs ?? current.Options ).FirstOrDefault( option => option.Mod == previous.Mod && option.Id == previous.Id );

			if ( next is null || next.Source == previous.Source )
				return previous;

			var module = loaded.Modules.FirstOrDefault( module => module.Name == previous.Mod );
			var original = module is null ? null : Find( saved.Decision.Key, module.Files.SelectMany( file => file.Items ).ToArray() );

			if ( original is null )
				return previous;

			try
			{
				if ( SourceWriter.Write( Parser.ParseSource( previous.Source, false ) ) == SourceWriter.Write( [original] ) )
					return next;
			}
			catch ( Exception error ) when ( error is ScriptSyntaxException or FormatException or OverflowException ) { }

			return previous;
		} ).ToArray();

		return saved with
		{
			Decision = saved.Decision with
			{
				Inputs = inputs
			}
		};
	}

	// base/ keeps each file before manual edits; compiled/ and loose/ keep the merged results.
	internal static void Save(
		string directory,
		LoadedBlobMerge loaded,
		BlobMergeAdoption adoption,
		MergePlan plan,
		IReadOnlyDictionary<string, MergeChoice> choices,
		IReadOnlyDictionary<string, PlannedFile>? baselines = null )
	{
		Directory.CreateDirectory( directory );

		var editedItems = plan.Files.Where( file => baselines?.ContainsKey( file.Path ) == true ).SelectMany( file => file.Items ).ToArray();
		var decisions = ( plan.Decisions ?? [] ).Select( decision => new SavedMergeDecision( decision,
			Find( decision.Key, editedItems ) is { } edited ? SourceWriter.Write( [edited] )
				: choices.TryGetValue( decision.Key, out var choice ) ? choice.EditedSource ?? decision.Options.Single( option => option.Id == choice.Option ).Source : decision.Options[0].Source ) ).ToArray();

		foreach ( var file in plan.Files )
		{
			string path = MergeBuilder.SafePath( directory, ( file.Compile ? "compiled/" : "loose/" ) + file.Path );

			Directory.CreateDirectory( Path.GetDirectoryName( path )! );
			File.WriteAllText( path, file.Source );

			if ( baselines?.TryGetValue( file.Path, out var baseline ) == true )
			{
				string previous = MergeBuilder.SafePath( directory, "base/" + file.Path );

				Directory.CreateDirectory( Path.GetDirectoryName( previous )! );
				File.WriteAllText( previous, NormalizeBaseline( baseline, file ).Source );
			}
		}

		File.WriteAllText( Path.Combine( directory, "recipe.json" ), JsonSerializer.Serialize( new BlobMergeHistory( decisions,
			plan.Files.Select( file => new SavedMergeFile( file.Path, file.Compile, file.Owners, Inputs( file ),
				baselines?.TryGetValue( file.Path, out var baseline ) == true && NormalizeBaseline( baseline, file ).Source != file.Source ) ).ToArray() ) ) );

		MergeSourceFile[] Inputs( PlannedFile file )
		{
			var keys = file.Items.Select( MergePlanner.Key ).ToHashSet( StringComparer.Ordinal );

			return file.Owners.SelectMany( owner =>
			{
				var module = loaded.Modules.SingleOrDefault( module => module.Name.Equals( owner, StringComparison.OrdinalIgnoreCase ) );

				if ( module?.IsBlob == true )
					return new[] { new MergeSourceFile( owner, "" ) };

				var original = module?.Files.Where( source => source.Path.Replace( '\\', '/' ).Equals( file.Path.Replace( '\\', '/' ), StringComparison.OrdinalIgnoreCase )
					|| source.Items.Any( item => keys.Contains( MergePlanner.Key( item ) ) ) ).Select( source => new MergeSourceFile( owner, source.Path ) )
					?? Enumerable.Empty<MergeSourceFile>();
				var adopted = adoption.ForFile( loaded, file )
					.Where( merge => merge.Mods.Any( source => source.Name.Equals( owner, StringComparison.OrdinalIgnoreCase ) ) )
					.Select( merge => new MergeSourceFile( owner, merge.RelativePath.Replace( '\\', '/' ) ) );

				return original.Concat( adopted );
			} ).Distinct().ToArray();
		}
	}

	// Use the same form for matching declarations so the comparison shows the user's edits.
	private static PlannedFile NormalizeBaseline( PlannedFile baseline, PlannedFile merged )
	{
		var items = Parser.ParseSource( baseline.Source, false );

		foreach ( var item in items.OfType<ClassItem>() )
		{
			var actual = merged.Items.OfType<ClassItem>().FirstOrDefault( value => Symbols.ClassKey( value.Decl ) == Symbols.ClassKey( item.Decl ) )?.Decl;

			if ( actual is null )
				continue;

			item.Decl.Parent = actual.Parent;
			item.Decl.Methods = item.Decl.Methods.Select( method => actual.Methods.FirstOrDefault( value => value.Name == method.Name ) ?? method ).ToList();
			item.Decl.Fields = item.Decl.Fields.Select( field => actual.Fields.FirstOrDefault( value => value.Names.SequenceEqual( field.Names ) ) ?? field ).ToList();
		}

		for ( int index = 0; index < items.Count; index++ )
			if ( items[index] is FunctionItem function && merged.Items.OfType<FunctionItem>().FirstOrDefault( actual => actual.Decl.Name == function.Decl.Name
				&& actual.Decl.Annotation == function.Decl.Annotation ) is { } replacement )
				items[index] = replacement;

		return baseline with
		{
			Items = items
		};
	}

	private static Item? Find( string key, IReadOnlyList<Item> items )
	{
		if ( key.StartsWith( "type:", StringComparison.Ordinal ) )
		{
			int separator = key.IndexOf( '/' );
			string owner = separator < 0 ? key[5..] : key[5..separator];
			var type = items.OfType<ClassItem>().FirstOrDefault( item => Symbols.ClassKey( item.Decl ) == owner )?.Decl;

			if ( type is null )
				return null;

			string member = separator < 0 ? "" : key[( separator + 1 )..];

			if ( member.StartsWith( "method:", StringComparison.Ordinal ) )
				return type.Methods.FirstOrDefault( method => method.Name == member[7..] ) is { } method ? new FunctionItem( method ) : null;

			if ( member.StartsWith( "field:", StringComparison.Ordinal ) )
				return type.Fields.FirstOrDefault( field => field.Names.Contains( member[6..] ) ) is { } field ? new FieldItem( field ) : null;

			if ( member == "header" )
				return new ClassItem( new ClassDecl
				{
					Name = type.Name,
					Parent = type.Parent,
					StateMachineOf = type.StateMachineOf,
					IsImport = type.IsImport,
					IsAbstract = type.IsAbstract,
					IsStateMachine = type.IsStateMachine,
					IsStruct = type.IsStruct
				} );

			if ( member.StartsWith( "default:", StringComparison.Ordinal ) )
			{
				string field = member[8..];
				int occurrence = 0;
				int suffix = field.LastIndexOf( '#' );

				if ( suffix >= 0 && int.TryParse( field[( suffix + 1 )..], out int number ) )
				{
					field = field[..suffix];
					occurrence = number - 1;
				}

				var value = type.Defaults.Where( value => value.Field == field ).Skip( occurrence ).FirstOrDefault();

				if ( value is not null )
					return new ClassItem( new ClassDecl { Name = type.Name, StateMachineOf = type.StateMachineOf, Defaults = [value] } );
			}

			if ( member.StartsWith( "hint:", StringComparison.Ordinal ) && type.Hints.TryGetValue( member[5..], out var hint ) )
				return new ClassItem( new ClassDecl { Name = type.Name, StateMachineOf = type.StateMachineOf, Hints = new() { [member[5..]] = hint } } );
		}

		if ( key.StartsWith( "enum:", StringComparison.Ordinal ) )
		{
			int separator = key.IndexOf( '/' );
			string name = separator < 0 ? key[5..] : key[5..separator];
			var value = items.OfType<EnumItem>().FirstOrDefault( item => item.Decl.Name == name );

			if ( value is not null && separator < 0 )
				return value;

			var member = value?.Decl.Members.FirstOrDefault( member => member.Name == key[( separator + 1 )..] );

			if ( member?.Name is not null )
				return new EnumItem( new EnumDecl { Name = name, Members = [member.Value] } );
		}

		return items.FirstOrDefault( item => item is not ClassItem && ( item is not FieldItem field || field.Decl.Names.Count == 1 ) && MergePlanner.Key( item ) == key );
	}
}
