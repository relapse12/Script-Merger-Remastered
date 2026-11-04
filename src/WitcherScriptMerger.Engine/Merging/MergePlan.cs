#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using WSCompiler.Core.Scripts.Standalone;
using WSCompiler.Recovery;


namespace WitcherScriptMerger.Merging;

// Compiled mods participate as a whole. Mod.io inputs are read-only.
// For scripts that could not be parsed, keep filenames and discovered names to check their possible dependencies.
public sealed record MergeModule(
	string Name,
	IReadOnlyList<SourceFile> Files,
	bool IsBlob = false,
	bool IsModIo = false,
	int Order = 0,
	IReadOnlyList<string>? LinkedSymbols = null,
	IReadOnlyList<string>? UnparsedFiles = null,
	IReadOnlyList<string>? UnparsedIdentifiers = null,
	IReadOnlyList<string>? UnparsedPaths = null,
	IReadOnlyList<string>? InspectedUnparsedPaths = null );

public sealed record MergeOption( string Id, string Mod, string Source );

// Options contains distinct versions; Inputs also keeps repeated copies for tracking their sources.
public sealed record MergeConflict(
	string Key,
	IReadOnlyList<MergeOption> Options,
	string? Original,
	IReadOnlyList<MergeOption>? Inputs = null );

public sealed record MergeChoice( string Option, string? EditedSource = null );

// Use a comparison result only while Input matches the declaration's current Identity.
public sealed record MergeBodyComparison(
	string Input,
	string Source,
	string Contract )
{
	// Include compiled flags that are absent from the recovered source text.
	public static string Identity( Item item ) => SourceWriter.Write( [item] )
		+ ( item is FunctionItem { Decl.CompiledFlags: { } flags } ? "\ncompiled-flags:" + flags : "" );
}

public sealed record MergeNotice( string Code, string Subject, IReadOnlyList<string> Mods );

// Keep wrapper IDs stable so a saved order can be reapplied.
public sealed record MergeWrapper( string Id, string Target, string Mod );

public sealed record MergeEdge( string Consumer, string Provider, string Symbol );

public sealed record MergeSourceFile( string Mod, string Path );

public sealed record PlannedFile(
	string Path,
	IReadOnlyList<Item> Items,
	bool Compile,
	IReadOnlyList<string> Owners )
{
	public string Source => SourceWriter.Write( Items );
}

// Decisions includes automatic and manual results for the saved merge history.
public sealed record MergePlan(
	IReadOnlyList<PlannedFile> Files,
	IReadOnlyList<MergeConflict> Conflicts,
	IReadOnlyList<MergeNotice> Notices,
	IReadOnlyList<string> IncludedMods,
	IReadOnlyList<MergeEdge> ExternalEdges,
	IReadOnlyList<MergeWrapper>? Wrappers = null,
	IReadOnlyList<MergeSourceFile>? SourceFiles = null,
	IReadOnlyList<MergeConflict>? Decisions = null )
{
	public bool NeedsCompilation => Files.Any( file => file.Compile );

	public IReadOnlySet<string> CompiledDefinitions => Files.Where( file => file.Compile ).SelectMany( file => file.Items )
		.SelectMany( DeclarationCatalog.Defines ).ToHashSet( StringComparer.Ordinal );
}

// Planning changes no files, so it can run again as the user resolves conflicts.
public static class MergePlanner
{
	public static MergePlan Create(
		IReadOnlyList<MergeModule> modules,
		IReadOnlySet<string> selected,
		IReadOnlyList<SourceFile> vanilla,
		IReadOnlyDictionary<string, MergeChoice>? choices = null,
		IReadOnlyList<string>? wrapperOrder = null,
		IReadOnlyDictionary<(string Key, string Mod), MergeBodyComparison>? bodyComparisons = null,
		IReadOnlyDictionary<string, PlannedFile>? fileOverrides = null )
		=> CreateScoped( modules, selected, vanilla, choices, wrapperOrder, new( StringComparer.OrdinalIgnoreCase ), bodyComparisons,
			fileOverrides?.ToDictionary( pair => pair.Key, pair => pair.Value with { Items = ExpandFields( new( pair.Value.Path, pair.Value.Items.ToList() ) ).Items }, StringComparer.OrdinalIgnoreCase ) );

	// Adding a dependency can change what must compile, so planning repeats until all required files are included.
	// Compilation is decided per file; placing declarations together can pull further dependencies into the blob.
	private static MergePlan CreateScoped(
		IReadOnlyList<MergeModule> modules,
		IReadOnlySet<string> selected,
		IReadOnlyList<SourceFile> vanilla,
		IReadOnlyDictionary<string, MergeChoice>? choices,
		IReadOnlyList<string>? wrapperOrder,
		Dictionary<string, HashSet<string>> loosePaths,
		IReadOnlyDictionary<(string Key, string Mod), MergeBodyComparison>? bodyComparisons,
		IReadOnlyDictionary<string, PlannedFile>? fileOverrides )
	{
		vanilla = vanilla.Select( ExpandFields ).ToArray();
		modules = modules.Select( module => module with { Files = module.Files.Select( ExpandFields ).ToArray() } ).ToArray();

		if ( selected.Count == 0 )
			throw new InvalidOperationException( "Select at least one conventional mod." );

		if ( modules.GroupBy( module => module.Name, StringComparer.OrdinalIgnoreCase ).Any( group => group.Count() != 1 ) )
			throw new InvalidDataException( "Mod identities must be unique." );

		if ( selected.Any( name => !modules.Any( module => module.Name.Equals( name, StringComparison.OrdinalIgnoreCase ) && !module.IsModIo ) ) )
			throw new InvalidOperationException( "Mod.io sources cannot be merged." );

		var included = modules.Where( module => selected.Any( name => module.Name.Equals( name, StringComparison.OrdinalIgnoreCase ) ) )
			.Select( module => module.Name ).ToHashSet( StringComparer.OrdinalIgnoreCase );
		selected = included.ToHashSet( StringComparer.OrdinalIgnoreCase );
		included.UnionWith( loosePaths.Keys );
		var inclusionReasons = new Dictionary<string, string[]>( StringComparer.OrdinalIgnoreCase );
		var catalog = new DeclarationCatalog( vanilla, fileOverrides is null ? modules : modules.Concat( new[] {
			new MergeModule("Merged source", fileOverrides.Values.Select(file => new SourceFile(file.Path, file.Items.ToList())).ToArray()) } ).ToArray() );
		var vanillaDefinitions = vanilla.SelectMany( file => file.Items ).SelectMany( DeclarationCatalog.Declares ).ToHashSet();
		IEnumerable<string> Dependencies( Item item ) => catalog.PhaseDependencies( item ).Where( symbol => !vanillaDefinitions.Contains( symbol )
			|| item is FunctionItem { Decl.Annotation.Kind: AnnotationKind.WrapMethod } wrapper
				&& symbol == "function:" + ( wrapper.Decl.Annotation.Class is { Length: > 0 } owner ? owner + "." : "" ) + wrapper.Decl.Name );
		var notices = new List<MergeNotice>();
		var conflicts = new List<MergeConflict>();
		var decisions = new List<MergeConflict>();
		var baseline = vanilla.SelectMany( file => file.Items.Select( item => new Origin( "Vanilla", file.Path, item, 0 ) ) )
			.GroupBy( value => Key( value.Item ), StringComparer.Ordinal ).ToDictionary( group => group.Key, group => group.First() );
		var all = modules.OrderBy( module => module.Order ).SelectMany( module => module.Files.SelectMany( file =>
			file.Items.Select( ( item, ordinal ) => new Origin( module.Name, file.Path, item, ordinal ) ) ) ).ToArray();

		var affectedPaths = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		var affectedDefinitions = new HashSet<string>( StringComparer.Ordinal );
		var looseDefinitions = all.GroupBy( origin => origin.Owner + "\0" + Normalize( origin.Path ), StringComparer.OrdinalIgnoreCase )
			.ToDictionary( group => group.Key, group => group.SelectMany( origin => DeclarationCatalog.Defines( origin.Item ) )
				.Where( symbol => !vanillaDefinitions.Contains( symbol ) ).Distinct( StringComparer.Ordinal ).ToArray(), StringComparer.OrdinalIgnoreCase );
		var pending = new Queue<string>( included );

		while ( pending.TryDequeue( out string? mod ) )
		{
			foreach ( var origin in all.Where( value => value.Owner == mod && Participates( value.Owner, value.Path ) ) )
			{
				string? path = baseline.GetValueOrDefault( Key( origin.Item ) )?.Path;

				if ( path is not null )
					affectedPaths.Add( Normalize( path ) );

				affectedDefinitions.UnionWith( DeclarationCatalog.Defines( origin.Item ).Where( symbol => !vanillaDefinitions.Contains( symbol ) ) );
			}

			foreach ( var module in modules.Where( module => !module.IsBlob && !selected.Contains( module.Name ) ) )
				foreach ( string path in module.Files.Select( file => file.Path ).Concat( module.UnparsedPaths ?? [] ).Distinct( StringComparer.OrdinalIgnoreCase )
					.Where( path => affectedPaths.Contains( Normalize( path ) )
						|| looseDefinitions.GetValueOrDefault( module.Name + "\0" + Normalize( path ), [] ).Any( affectedDefinitions.Contains ) ) )
				{
					if ( Participates( module.Name, path ) )
						continue;

					if ( module.IsModIo )
						throw new InvalidOperationException( "A required traditional edit belongs to read-only mod.io source: " + module.Name );

					if ( IncludeFile( module.Name, path ) )
					{
						pending.Enqueue( module.Name );
						notices.Add( new( "RelatedLooseFile", module.Name, [module.Name] ) );
						inclusionReasons[module.Name] = loosePaths[module.Name].Select( path => "file:" + path ).ToArray();
					}
				}
		}

		var fragments = Build();
		// Defaults that loose scripts cannot express, added events and native wrappers must compile.
		var compilation = new HashSet<string>( StringComparer.Ordinal );
		var edges = new List<MergeEdge>();
		var definitions = fragments.SelectMany( part => DeclarationCatalog.Defines( part.Item ).Select( symbol => (symbol, part) ) )
			.GroupBy( value => value.symbol ).ToDictionary( group => group.Key, group => group.Select( value => value.part ).ToArray() );
		IEnumerable<MergeModule> ExternalProviders( string symbol ) => symbol.StartsWith( "type:", StringComparison.Ordinal ) && definitions.ContainsKey( symbol )
			? [] : catalog.Providers( symbol ).Where( module => module.IsBlob && !included.Contains( module.Name ) );

		foreach ( var part in fragments )
		{
			if ( NeedsBinaryDefaults( part.Item ) )
			{
				compilation.Add( part.Id );
				notices.Add( new( "BinaryDefaults", part.Id, part.Owners ) );
			}

			if ( NeedsCompiledEvent( part.Item ) )
			{
				compilation.Add( part.Id );
				notices.Add( new( "AddedEvent", part.Id, part.Owners ) );
			}

			if ( catalog.WrapsNative( part.Item ) )
			{
				compilation.Add( part.Id );
				notices.Add( new( "NativeBinding", part.Id, part.Owners ) );
			}

			foreach ( string symbol in Dependencies( part.Item ) )
				foreach ( var provider in ExternalProviders( symbol ) )
				{
					compilation.Add( part.Id );
					edges.Add( new( part.Id, provider.Name, symbol ) );
				}
		}

		// An external compiled mod still links to this definition, so it must remain compiled.
		foreach ( var consumer in modules.Where( module => module.IsBlob && !included.Contains( module.Name ) ) )
			foreach ( string symbol in consumer.Files.SelectMany( file => file.Items ).SelectMany( catalog.Uses ).Concat( consumer.LinkedSymbols ?? [] ).Distinct() )
				foreach ( string target in symbol.StartsWith( "method:", StringComparison.Ordinal )
					? ( consumer.LinkedSymbols ?? [] ).Where( key => key.StartsWith( "owner:", StringComparison.Ordinal ) )
						.Select( key => catalog.ResolveMethod( key[6..], symbol[7..] ) ).Distinct() : [symbol] )
					if ( !vanillaDefinitions.Contains( target ) && definitions.TryGetValue( target, out var providers ) )
						foreach ( var provider in providers )
						{
							compilation.Add( provider.Id );
							edges.Add( new( consumer.Name, provider.Id, target ) );
						}

		// Follow callers as well as their dependencies to find everything that must compile.
		var dependents = new Dictionary<string, HashSet<string>>( StringComparer.Ordinal );

		foreach ( var part in fragments )
			foreach ( string symbol in Dependencies( part.Item ) )
				if ( definitions.TryGetValue( symbol, out var providers ) )
					foreach ( var provider in providers )
					{
						if ( !dependents.TryGetValue( provider.Id, out var targets ) )
							dependents[provider.Id] = targets = [];

						if ( part.Id != provider.Id )
							targets.Add( part.Id );
					}

		var queue = new Queue<string>( compilation );

		while ( queue.TryDequeue( out string? provider ) )
			if ( dependents.TryGetValue( provider, out var consumers ) )
				foreach ( string consumer in consumers )
					if ( compilation.Add( consumer ) )
						queue.Enqueue( consumer );

		// Also include loose callers of the definitions being compiled.
		bool expanded = false;
		var compiledSymbols = fragments.Where( part => compilation.Contains( part.Id ) ).SelectMany( part => DeclarationCatalog.Defines( part.Item ) ).ToHashSet();
		var looseQueue = new Queue<string>( compiledSymbols );
		var looseConsumers = modules.Where( module => !selected.Contains( module.Name ) && !module.IsBlob )
			.Select( module => module with { Files = module.Files.Where( file => !Participates( module.Name, file.Path ) ).ToArray() } ).ToArray();
		var references = looseConsumers.ToDictionary( module => module.Name,
			module => module.Files.SelectMany( file => file.Items ).SelectMany( Dependencies ).ToHashSet() );
		var activatedItems = new HashSet<Item>();

		while ( looseQueue.TryDequeue( out string? symbol ) )
			foreach ( var consumer in looseConsumers.Where( module => references[module.Name].Contains( symbol ) ) )
			{
				if ( consumer.IsModIo )
					throw new InvalidOperationException( $"The merge would require rewriting read-only mod.io source: {consumer.Name}." );

				foreach ( var file in consumer.Files.Where( file => file.Items.Any( item => Dependencies( item ).Contains( symbol ) ) ) )
					if ( IncludeFile( consumer.Name, file.Path ) )
						expanded = true;

				foreach ( var item in consumer.Files.SelectMany( file => file.Items ).Where( item => !activatedItems.Contains( item ) && Dependencies( item ).Contains( symbol ) ) )
				{
					activatedItems.Add( item );

					foreach ( string added in DeclarationCatalog.Defines( item ) )
						if ( compiledSymbols.Add( added ) )
							looseQueue.Enqueue( added );
				}
			}

		if ( expanded )
			return CreateScoped( modules, selected, vanilla, choices, wrapperOrder, loosePaths, bodyComparisons, fileOverrides );

		// Loose edits need the rest of their original file. Compiled output can contain only the required declarations.
		var output = new List<PlannedFile>();
		var localOwners = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
		var vanillaFiles = new HashSet<string>( StringComparer.OrdinalIgnoreCase );

		foreach ( var part in fragments )
		{
			bool compile = compilation.Contains( part.Id );

			if ( part.VanillaPath is not null )
			{
				if ( !vanillaFiles.Add( part.VanillaPath ) )
					continue;

				var original = vanilla.First( file => file.Path.Equals( part.VanillaPath, StringComparison.OrdinalIgnoreCase ) );
				var replacements = fragments.Where( value => value.VanillaPath == part.VanillaPath ).ToArray();
				var binary = replacements.Where( value => compilation.Contains( value.Id ) ).ToArray();

				if ( binary.Length > 0 )
					output.Add( new( original.Path, binary.Select( value => value.Item ).ToArray(), true, binary.SelectMany( value => value.Owners ).Distinct().ToArray() ) );

				var loose = replacements.Where( value => !compilation.Contains( value.Id ) ).ToArray();

				if ( loose.Length > 0 )
				{
					var items = original.Items.Select( item => loose.FirstOrDefault( value => Key( value.Item ) == Key( item ) )?.Item ?? item )
						.Concat( loose.Where( value => !original.Items.Any( item => Key( item ) == Key( value.Item ) ) ).Select( value => value.Item ) ).ToArray();
					output.Add( new( original.Path, items, false, loose.SelectMany( value => value.Owners ).Distinct().ToArray() ) );
				}
			}
			else
			{
				string path = LocalPath( part.Id, part.Item, output.Count );
				output.Add( new( path, [part.Item], compile, part.Owners ) );
				localOwners.Add( path, part.SourceOwner ?? part.Owners[0] );
			}
		}

		output = RecoveredFileLayout.Combine( output, localOwners ).ToList();

		// A manually edited output can introduce new reasons to compile it.
		if ( fileOverrides is not null )
			foreach ( var replacement in fileOverrides.Values )
			{
				int index = output.FindIndex( file => file.Path.Equals( replacement.Path, StringComparison.OrdinalIgnoreCase ) );

				if ( index >= 0 )
				{
					foreach ( var item in replacement.Items )
						if ( output[index].Items.FirstOrDefault( original => Key( original ) == Key( item ) ) is { } original )
							KeepMetadata( item, original );

					output[index] = replacement with
					{
						Compile = output[index].Compile || replacement.Compile
					};
				}
				else
					output.Add( replacement );
			}

		for ( int index = 0; index < output.Count; index++ )
		{
			var file = output[index];

			if ( fileOverrides?.ContainsKey( file.Path ) != true )
				continue;

			if ( file.Items.Any( item => NeedsBinaryDefaults( item ) || NeedsCompiledEvent( item ) || catalog.WrapsNative( item ) ) )
				file = file with
				{
					Compile = true
				};

			foreach ( string symbol in file.Items.SelectMany( Dependencies ) )
				foreach ( var provider in ExternalProviders( symbol ) )
				{
					file = file with
					{
						Compile = true
					};
					edges.Add( new( "file:" + file.Path, provider.Name, symbol ) );
				}

			output[index] = file;
		}

		// Compiling a whole file may bring additional declarations into the blob.
		var binaryDefinitions = output.Where( file => file.Compile ).SelectMany( file => file.Items ).SelectMany( DeclarationCatalog.Defines ).ToHashSet();
		var reverse = new Dictionary<string, HashSet<int>>();

		for ( int index = 0; index < output.Count; index++ )
			foreach ( string symbol in output[index].Items.SelectMany( Dependencies ).Distinct() )
			{
				if ( !reverse.TryGetValue( symbol, out var consumers ) )
					reverse[symbol] = consumers = [];

				consumers.Add( index );
			}

		looseQueue = new( binaryDefinitions );

		while ( looseQueue.TryDequeue( out string? symbol ) )
			if ( reverse.TryGetValue( symbol, out var consumers ) )
				foreach ( int index in consumers.Where( index => !output[index].Compile ) )
				{
					output[index] = output[index] with
					{
						Compile = true
					};

					foreach ( string added in output[index].Items.SelectMany( DeclarationCatalog.Defines ) )
						if ( binaryDefinitions.Add( added ) )
							looseQueue.Enqueue( added );
				}

		EnumMergePolicy.Validate( modules, included, vanilla, output );

		if ( output.SelectMany( file => file.Items ).OfType<FunctionItem>().Any( item => item.Decl.Annotation?.Kind == AnnotationKind.WrapMethod ) )
			notices.Add( new( "WrapperOrder", "", included.ToArray() ) );

		// Check external loose callers again after deciding which files compile.
		var remaining = modules.Where( module => !selected.Contains( module.Name ) && !module.IsBlob )
			.Select( module => module with
			{
				Files = module.Files.Where( file => !Participates( module.Name, file.Path )
				&& file.Items.SelectMany( Dependencies ).Any( binaryDefinitions.Contains ) ).ToArray()
			} ).Where( module => module.Files.Count > 0 ).ToArray();

		if ( remaining.Any( module => module.IsModIo ) )
			throw new InvalidOperationException( "A read-only mod.io source would depend on the merged blob." );

		if ( remaining.Length > 0 )
		{
			foreach ( var module in remaining )
				foreach ( var file in module.Files )
					IncludeFile( module.Name, file.Path );

			return CreateScoped( modules, selected, vanilla, choices, wrapperOrder, loosePaths, bodyComparisons, fileOverrides );
		}

		// For unparsed scripts, a matching identifier can only suggest a dependency. Stop if that dependency could break
		// when compiled; the planner cannot safely rewrite a script it cannot read.
		var binaryNames = binaryDefinitions.Where( symbol => !vanillaDefinitions.Contains( symbol ) )
			.Select( symbol => symbol[( Math.Max( symbol.LastIndexOf( ':' ), symbol.LastIndexOf( '.' ) ) + 1 )..] ).ToHashSet( StringComparer.Ordinal );

		foreach ( var module in modules.Where( module => !module.IsBlob && module.UnparsedFiles is { Count: > 0 } ) )
		{
			var inspected = ( module.InspectedUnparsedPaths ?? [] ).Select( Normalize ).ToHashSet( StringComparer.OrdinalIgnoreCase );
			bool unknown = module.UnparsedPaths is null || module.UnparsedPaths.Any( path => !inspected.Contains( Normalize( path ) ) );
			bool inspectedDependency = module.Files.Where( file => inspected.Contains( Normalize( file.Path ) ) ).SelectMany( file => file.Items )
				.SelectMany( Dependencies ).Any( binaryDefinitions.Contains );

			if ( inspectedDependency || unknown && binaryNames.Count > 0 && ( module.UnparsedIdentifiers is null || module.UnparsedIdentifiers.Any( binaryNames.Contains ) ) )
			{
				var error = new InvalidOperationException( "A possible loose-script consumer could not be preserved: " + module.Name + "\n" + string.Join( "\n", module.UnparsedFiles! ) );
				error.Data["PossibleSymbols"] = binaryDefinitions.Where( symbol => !vanillaDefinitions.Contains( symbol )
					&& ( module.UnparsedIdentifiers is null || module.UnparsedIdentifiers.Contains( symbol[( Math.Max( symbol.LastIndexOf( ':' ), symbol.LastIndexOf( '.' ) ) + 1 )..] ) ) ).ToArray();
				throw error;
			}

			notices.Add( new( "UnparsedLooseSource", module.Name, [module.Name] ) );
		}

		var wrappers = fragments.Where( part => part.Item is FunctionItem { Decl.Annotation.Kind: AnnotationKind.WrapMethod } )
			.Select( part => new MergeWrapper( part.Id, Key( part.Item )[5..], part.Owners.Single() ) ).ToArray();

		foreach ( string mod in loosePaths.Keys )
			notices.Add( new( output.Any( file => file.Compile && file.Owners.Contains( mod ) ) ? "CompiledConsumer" : "RelatedLooseFile", mod, [mod] ) );

		return new( output, conflicts.DistinctBy( conflict => conflict.Key ).ToArray(), notices, included.ToArray(), edges.Distinct().ToArray(), wrappers,
			all.Where( origin => Participates( origin.Owner, origin.Path ) ).Select( origin => new MergeSourceFile( origin.Owner, origin.Path ) ).Distinct().ToArray(),
			decisions.DistinctBy( decision => decision.Key ).ToArray() );

		bool Participates( string mod, string path ) => selected.Contains( mod ) || loosePaths.TryGetValue( mod, out var paths ) && paths.Contains( Normalize( path ) );
		bool IncludeFile(
			string mod,
			string path )
		{
			if ( selected.Contains( mod ) )
				return false;

			if ( !loosePaths.TryGetValue( mod, out var paths ) )
				loosePaths[mod] = paths = new( StringComparer.OrdinalIgnoreCase );

			included.Add( mod );

			return paths.Add( Normalize( path ) );
		}

		// Merge class and enum members separately so unrelated additions can coexist. Keep all wrappers in execution order.
		List<Part> Build(
)
		{
			foreach ( var module in modules.Where( module => included.Contains( module.Name ) && module.UnparsedFiles is { Count: > 0 } ) )
			{
				var errors = module.UnparsedFiles!.Where( ( _, index ) => selected.Contains( module.Name ) || module.UnparsedPaths is null
					|| index >= module.UnparsedPaths.Count || Participates( module.Name, module.UnparsedPaths[index] ) ).ToArray();

				if ( errors.Length == 0 )
					continue;

				var error = new InvalidOperationException( "Participating source could not be fully parsed: " + module.Name + "\n" + string.Join( "\n", errors ) );
				error.Data["IncludedMods"] = included.ToArray();
				error.Data["InclusionReasons"] = inclusionReasons;
				throw error;
			}

			var output = new List<Part>();
			var current = all.Where( origin => Participates( origin.Owner, origin.Path ) ).ToList();

			foreach ( var group in current.GroupBy( origin => Key( origin.Item ), StringComparer.Ordinal ) )
			{
				Origin? original = baseline.GetValueOrDefault( group.Key );

				if ( group.First().Item is ClassItem )
				{
					ClassDecl? baseClass = ( original?.Item as ClassItem )?.Decl;
					var entries = group.ToArray();

					if ( baseClass?.IsImport == true )
						entries = entries.Select( entry => entry.Item is ClassItem { Decl.IsImport: true, Decl.Parent: null } imported
							? entry with
							{
								Item = new ClassItem( WithNativeParent( imported.Decl, baseClass.Parent ) )
							} : entry ).ToArray();

					var selectedHeader = Choose( group.Key + "/header", entries.Select( entry => entry with { Item = new ClassItem( Header( ( (ClassItem)entry.Item ).Decl ) ) } ).ToArray(),
						original is null ? null : original with
						{
							Item = new ClassItem( Header( baseClass! ) )
						} );
					var merged = CopyClass( ( (ClassItem)selectedHeader.Item ).Decl, [], [], [] );
					merged.Fields = MergeMembers( "field", entries.SelectMany( entry => ( (ClassItem)entry.Item ).Decl.Fields.SelectMany( field => field.Names.Select( name =>
						entry with
						{
							Item = new FieldItem( Single( field, name ) )
						} ) ) ), baseClass?.Fields.SelectMany( field => field.Names.Select( name => new FieldItem( Single( field, name ) ) ) ).ToArray(),
						item => ( (FieldItem)item ).Decl.Names[0] ).Select( origin => ( (FieldItem)origin.Item ).Decl ).ToList();
					merged.Methods = MergeMembers( "method", entries.SelectMany( entry => ( (ClassItem)entry.Item ).Decl.Methods.Select( method => entry with { Item = new FunctionItem( method ) } ) ),
						baseClass?.Methods.Select( method => new FunctionItem( method ) ).ToArray(), item => ( (FunctionItem)item ).Decl.Name )
						.Select( origin => ( (FunctionItem)origin.Item ).Decl ).ToList();
					merged.Defaults = MergeValues( "default", entries, declaration => declaration.Defaults.Select( value => (value.Field, new ClassItem( new ClassDecl { Name = merged.Name, Defaults = [value] } ) as Item) ),
						baseClass ).Select( origin => ( (ClassItem)origin.Item ).Decl.Defaults.Single() ).ToList();
					merged.Hints = MergeValues( "hint", entries, declaration => declaration.Hints.Select( value => (value.Key, new ClassItem( new ClassDecl { Name = merged.Name, Hints = new() { [value.Key] = value.Value } } ) as Item) ),
						baseClass ).ToDictionary( origin => ( (ClassItem)origin.Item ).Decl.Hints.Single().Key, origin => ( (ClassItem)origin.Item ).Decl.Hints.Single().Value );
					merged.BinaryDefaults = entries.SelectMany( entry => ( (ClassItem)entry.Item ).Decl.BinaryDefaults ).GroupBy( value => value.Property ).Select( values =>
{
	var distinct = values.DistinctBy( value => JsonSerializer.Serialize( value ) ).ToArray();

	if ( distinct.Length > 1 )
		throw new InvalidOperationException( $"These mods give {merged.Name}.{values.Key} different starting values. Use compatible versions of these mods; this merge cannot combine both values." );

	return distinct[0];
} ).ToList();
					merged.RequiresBinaryDefaults = entries.Any( entry => ( (ClassItem)entry.Item ).Decl.RequiresBinaryDefaults );
					output.Add( new( group.Key, new ClassItem( merged ), entries.Select( entry => entry.Owner ).Distinct().ToArray(), original?.Path ) );

					IEnumerable<Origin> MergeMembers(
						string label,
						IEnumerable<Origin> values,
						Item[]? originals,
						Func<Item, string> name )
					{
						var originalByName = ( originals ?? [] ).ToDictionary( name, StringComparer.Ordinal );
						var members = values.GroupBy( value => name( value.Item ) ).ToDictionary( group => group.Key, group => group.ToArray() );

						foreach ( string member in originalByName.Keys.Union( members.Keys ) )
						{
							if ( !members.TryGetValue( member, out var options ) )
							{
								yield return new( "Vanilla", original?.Path ?? "", originalByName[member], 0 );
								continue;
							}

							var old = originalByName.TryGetValue( member, out var item ) ? new Origin( "Vanilla", original?.Path ?? "", item, 0 ) : null;
							yield return Choose( group.Key + "/" + label + ":" + member, options, old );
						}
					}
					IEnumerable<Origin> MergeValues(
						string label,
						Origin[] entries,
						Func<ClassDecl, IEnumerable<(string Name, Item Value)>> extract,
						ClassDecl? previous )
					{
						var originals = previous is null ? new Dictionary<string, Origin>( StringComparer.Ordinal )
							: Values( original! with
							{
								Item = new ClassItem( previous )
							} ).ToDictionary( value => value.Key, value => value.Origin, StringComparer.Ordinal );
						var members = entries.SelectMany( Values ).GroupBy( value => value.Key, StringComparer.Ordinal )
							.ToDictionary( group => group.Key, group => group.Select( value => value.Origin ).ToArray(), StringComparer.Ordinal );

						foreach ( string key in originals.Keys.Union( members.Keys, StringComparer.Ordinal ) )
						{
							if ( !members.TryGetValue( key, out var options ) )
							{
								yield return originals[key];
								continue;
							}

							yield return Choose( group.Key + "/" + label + ":" + key, options, originals.GetValueOrDefault( key ) );
						}

						IEnumerable<(string Key, Origin Origin)> Values(
							Origin entry )
						{
							var occurrences = new Dictionary<string, int>( StringComparer.Ordinal );

							foreach ( var value in extract( ( (ClassItem)entry.Item ).Decl ) )
							{
								int occurrence = occurrences.GetValueOrDefault( value.Name ) + 1;
								occurrences[value.Name] = occurrence;
								yield return (occurrence == 1 ? value.Name : value.Name + "#" + occurrence, entry with
								{
									Item = value.Value
								});
							}
						}
					}
				}
				else if ( group.First().Item is EnumItem )
				{
					var old = original?.Item as EnumItem;
					var names = ( old?.Decl.Members.Select( member => member.Name ) ?? [] )
						.Concat( group.SelectMany( entry => ( (EnumItem)entry.Item ).Decl.Members.Select( member => member.Name ) ) ).Distinct().ToArray();
					var members = new List<(string, long?)>();

					foreach ( string name in names )
					{
						var entries = group.Where( entry => ( (EnumItem)entry.Item ).Decl.Members.Any( member => member.Name == name ) )
							.Select( entry => entry with
							{
								Item = new EnumItem( new EnumDecl
								{
									Name = ( (EnumItem)entry.Item ).Decl.Name,
									Members = [( (EnumItem)entry.Item ).Decl.Members.First( member => member.Name == name )]
								} )
							} ).ToArray();
						var oldMember = old?.Decl.Members.FirstOrDefault( member => member.Name == name );
						var previous = oldMember?.Name is null ? null : original! with
						{
							Item = new EnumItem( new EnumDecl { Name = old!.Decl.Name, Members = [oldMember.Value] } )
						};
						var chosen = (EnumItem)( entries.Length == 0 ? previous! : Choose( group.Key + "/" + name, entries, previous ) ).Item;
						members.Add( chosen.Decl.Members.Single() );
					}

					output.Add( new( group.Key, new EnumItem( new EnumDecl { Name = ( (EnumItem)group.First().Item ).Decl.Name, Members = members } ), group.Select( entry => entry.Owner ).Distinct().ToArray(), original?.Path ) );
				}
				else if ( group.First().Item is FunctionItem { Decl.Annotation.Kind: AnnotationKind.WrapMethod } )
				{
					var moduleByName = modules.ToDictionary( module => module.Name, StringComparer.OrdinalIgnoreCase );
					// Wrappers are ordered inner first: loose files by path, then compiled mods by descending load order.
					var defaultOrder = group.OrderBy( entry => moduleByName[entry.Owner].IsBlob ? 1 : 0 )
						.ThenBy( entry => moduleByName[entry.Owner].IsBlob ? -moduleByName[entry.Owner].Order : 0 )
						.ThenBy( entry => moduleByName[entry.Owner].IsBlob ? "" : entry.Path.Replace( '\\', '/' ), StringComparer.OrdinalIgnoreCase )
						.ThenBy( entry => entry.Ordinal ).ToArray();
					string WrapperId( Origin entry ) => group.Key + "/" + entry.Owner + "/" + entry.Path + "/" + entry.Ordinal;
					var innerToOuter = wrapperOrder is null ? defaultOrder : defaultOrder.OrderBy( entry =>
{
	int index = wrapperOrder.ToList().IndexOf( WrapperId( entry ) );

	return index < 0 ? int.MaxValue : index;
} ).ToArray();

					foreach ( var entry in innerToOuter )
						output.Add( new( WrapperId( entry ), entry.Item, [entry.Owner], null ) );
				}
				else
				{
					var chosen = Choose( group.Key, group.ToArray(), original );
					output.Add( new( group.Key, chosen.Item, group.Select( entry => entry.Owner ).Distinct().ToArray(), original?.Path, chosen.Owner ) );
				}
			}

			return output;
		}

		// Compare normalized bodies only when every side has one. Otherwise compare source text.
		// While a conflict is unanswered, use the first option to keep the plan display complete.
		Origin Choose(
			string key,
			Origin[] entries,
			Origin? original )
		{
			MergeBodyComparison? Comparison( Origin entry ) => entry.Item is FunctionItem { Decl.Annotation: null }
				&& bodyComparisons is not null && bodyComparisons.TryGetValue( (key, entry.Owner), out var value )
				&& value.Input == Canon( entry.Item ) ? value : null;
			bool normalized = entries.All( entry => Comparison( entry ) is not null ) && ( original is null || Comparison( original ) is not null );
			string Compared( Origin entry ) => normalized && Comparison( entry ) is { } value ? value.Source + "\n" + value.Contract : Canon( entry.Item );
			string Source( Origin entry ) => normalized ? Comparison( entry )!.Source : SourceWriter.Write( [entry.Item] );
			var changed = entries.Where( entry => original is null || Compared( entry ) != Compared( original ) ).ToArray();
			var alternatives = ( changed.Length == 0 ? entries.Take( 1 ) : changed ).DistinctBy( Compared ).ToArray();
			MergeOption Option( Origin entry ) => new( entry.Owner + ":" + Convert.ToHexString( System.Security.Cryptography.SHA256.HashData(
				System.Text.Encoding.UTF8.GetBytes( Canon( entry.Item ) ) ) )[..16], entry.Owner, Source( entry ) );
			var options = alternatives.Select( Option ).ToArray();
			var decision = new MergeConflict( key, options, original is null ? null : Source( original ), entries.Select( Option ).ToArray() );
			decisions.Add( decision );

			if ( alternatives.Length > 1 )
				conflicts.Add( decision );

			if ( choices is null || !choices.TryGetValue( key, out var choice ) )
				return alternatives[0];

			int chosen = Array.FindIndex( options, option => option.Id == choice.Option );

			if ( chosen < 0 )
				throw new InvalidDataException( "Unknown merge choice: " + key );

			if ( choice.EditedSource is null )
				return alternatives[chosen];

			// A manual resolution must keep the declaration's identity and annotation.
			var parsed = Parser.ParseSource( choice.EditedSource, false );

			if ( parsed.Count != 1 || parsed[0].GetType() != alternatives[chosen].Item.GetType() || Key( parsed[0] ) != Key( alternatives[chosen].Item )
				|| parsed[0] is FunctionItem newFunction && alternatives[chosen].Item is FunctionItem oldFunction && newFunction.Decl.Annotation != oldFunction.Decl.Annotation )
				throw new InvalidDataException( "An edited choice must preserve its declaration identity and annotation: " + key );

			// Recovered text omits some compiled specifiers; preserve them from the selected version.
			if ( parsed[0] is FunctionItem { Decl.Annotation.Kind: AnnotationKind.WrapMethod or AnnotationKind.ReplaceMethod } edited
				&& alternatives[chosen].Item is FunctionItem previous )
				parsed[0] = new FunctionItem( new FunctionDecl
				{
					Name = edited.Decl.Name,
					Annotation = edited.Decl.Annotation,
					Specifiers = previous.Decl.Specifiers.Copy(),
					CompiledFlags = previous.Decl.CompiledFlags,
					Params = edited.Decl.Params,
					ReturnType = edited.Decl.ReturnType,
					Body = edited.Decl.Body,
					Line = edited.Decl.Line,
					UsesWrappedMethod = edited.Decl.UsesWrappedMethod
				} );

			KeepMetadata( parsed[0], alternatives[chosen].Item );

			return alternatives[chosen] with
			{
				Item = parsed[0]
			};
		}
	}

	private sealed record Origin( string Owner, string Path, Item Item, int Ordinal );

	// Parsing source loses compiled defaults and flags. Restore that metadata, retaining function flags only
	// when the signature still matches.
	private static void KeepMetadata(
		Item edited,
		Item original )
	{
		if ( edited is ClassItem type && original is ClassItem previous )
		{
			type.Decl.BinaryDefaults = previous.Decl.BinaryDefaults.ToList();
			type.Decl.RequiresBinaryDefaults = previous.Decl.RequiresBinaryDefaults;

			foreach ( var method in type.Decl.Methods )
				if ( previous.Decl.Methods.FirstOrDefault( value => value.Name == method.Name && value.Annotation == method.Annotation ) is { } old )
					KeepMetadata( new FunctionItem( method ), new FunctionItem( old ) );
		}

		if ( edited is FunctionItem function && original is FunctionItem oldFunction )
		{
			var options = new JsonSerializerOptions { IncludeFields = true };

			if ( function.Decl.Annotation == oldFunction.Decl.Annotation
				&& JsonSerializer.Serialize( new
				{
					function.Decl.Specifiers,
					function.Decl.Params,
					function.Decl.ReturnType
				}, options )
					== JsonSerializer.Serialize( new
					{
						oldFunction.Decl.Specifiers,
						oldFunction.Decl.Params,
						oldFunction.Decl.ReturnType
					}, options ) )
				function.Decl.CompiledFlags = oldFunction.Decl.CompiledFlags;
		}
	}

	private static string LocalPath(
		string id,
		Item item,
		int ordinal )
	{
		string name = item switch
		{
			ClassItem type => Symbols.ClassKey( type.Decl ),
			FunctionItem function => ( function.Decl.Annotation?.Class is { Length: > 0 } owner ? owner + "." : "" ) + function.Decl.Name,
			EnumItem value => value.Decl.Name,
			FieldItem field => string.Join( "_", field.Decl.Names ),
			_ => "declaration"
		};
		name = new string( name.Select( character => char.IsLetterOrDigit( character ) || character is '_' or '.' ? character : '_' ).Take( 80 ).ToArray() );
		string hash = Convert.ToHexString( System.Security.Cryptography.SHA256.HashData( System.Text.Encoding.UTF8.GetBytes( id ) ) )[..12];
		bool annotation = item is FunctionItem { Decl.Annotation: not null } or FieldItem { Decl.Annotation: not null };
		string folder = annotation ? "annotations" : item is ClassItem ? "types" : item is EnumItem ? "enums" : "functions";

		return $"local/smrmerge/{folder}/{( annotation ? ordinal.ToString( "D6" ) + "_" : "" )}{name}_{hash}.ws";
	}

	// Compilation also needs the external providers' signature dependencies.
	public static IReadOnlyList<string> ReferenceMods(
		MergePlan plan,
		IReadOnlyList<MergeModule> modules,
		IReadOnlyList<SourceFile> vanilla )
	{
		var catalog = new DeclarationCatalog( vanilla, modules );
		var original = vanilla.SelectMany( file => file.Items ).SelectMany( DeclarationCatalog.Declares ).ToHashSet( StringComparer.Ordinal );
		var owned = plan.Files.SelectMany( file => file.Items ).SelectMany( DeclarationCatalog.Defines ).ToHashSet( StringComparer.Ordinal );
		var needed = new Queue<string>( plan.Files.Where( file => file.Compile ).SelectMany( file => file.Items ).SelectMany( catalog.Uses ) );
		var visited = new HashSet<string>( StringComparer.Ordinal );
		var references = plan.ExternalEdges.Where( edge => edge.Consumer.Contains( ':' ) ).Select( edge => edge.Provider )
			.Where( name => modules.Any( module => module.Name == name && !plan.IncludedMods.Contains( name ) ) ).ToHashSet( StringComparer.OrdinalIgnoreCase );

		foreach ( var module in modules.Where( module => references.Contains( module.Name ) ) )
			foreach ( string dependency in module.Files.SelectMany( file => file.Items ).SelectMany( catalog.SignatureUses ) )
				needed.Enqueue( dependency );

		while ( needed.TryDequeue( out string? symbol ) )
		{
			if ( !visited.Add( symbol ) || original.Contains( symbol ) || owned.Contains( symbol ) )
				continue;

			foreach ( var module in catalog.Providers( symbol ) )
				if ( references.Add( module.Name ) )
					foreach ( string dependency in module.Files.SelectMany( file => file.Items ).SelectMany( catalog.SignatureUses ) )
						needed.Enqueue( dependency );
		}

		return references.ToArray();
	}

	public static IReadOnlyList<PlannedFile> LooseCompilationReferences(
		MergePlan plan,
		IReadOnlyList<MergeModule> modules,
		IReadOnlyList<SourceFile> vanilla )
	{
		var catalog = new DeclarationCatalog( vanilla, modules.Concat( new[] { new MergeModule( "Merged source", plan.Files.Select( file => new SourceFile( file.Path, file.Items.ToList() ) ).ToArray() ) } ).ToArray() );
		var needed = plan.Files.Where( file => file.Compile ).SelectMany( file => file.Items ).SelectMany( catalog.PhaseDependencies ).ToHashSet( StringComparer.Ordinal );
		var selected = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		var remaining = plan.Files.Where( file => !file.Compile ).ToList();
		var queue = new Queue<string>( needed );

		while ( queue.TryDequeue( out string? symbol ) )
			foreach ( var file in remaining.Where( file => !selected.Contains( file.Path ) && file.Items.SelectMany( DeclarationCatalog.Declares ).Contains( symbol ) ) )
			{
				selected.Add( file.Path );

				foreach ( string dependency in file.Items.SelectMany( catalog.PhaseDependencies ) )
					if ( needed.Add( dependency ) )
						queue.Enqueue( dependency );
			}

		return plan.Files.Where( file => selected.Contains( file.Path ) ).ToArray();
	}

	private sealed record Part(
		string Id,
		Item Item,
		IReadOnlyList<string> Owners,
		string? VanillaPath,
		string? SourceOwner = null );

	private static string Normalize( string path ) => path.Replace( '\\', '/' );

	private static string Canon( Item item ) => MergeBodyComparison.Identity( item );

	private static bool NeedsBinaryDefaults( Item item ) => item is ClassItem value && ( value.Decl.RequiresBinaryDefaults || value.Decl.BinaryDefaults.Any( value => value.Children.Count > 0 ) );

	private static bool NeedsCompiledEvent( Item item ) => item is FunctionItem { Decl.Annotation.Kind: AnnotationKind.AddMethod, Decl.Specifiers.IsEvent: true };

	private static ClassDecl Header( ClassDecl value ) => CopyClass( value, [], [], [] );

	private static ClassDecl WithNativeParent(
		ClassDecl value,
		string? parent )
	{
		var result = CopyClass( value, value.Fields, value.Methods, value.Defaults );
		result.Parent = parent;
		result.Hints = value.Hints;
		result.BinaryDefaults = value.BinaryDefaults;
		result.RequiresBinaryDefaults = value.RequiresBinaryDefaults;

		return result;
	}

	private static ClassDecl CopyClass(
		ClassDecl value,
		List<FieldDecl> fields,
		List<FunctionDecl> methods,
		List<DefaultAssignment> defaults ) => new()
{
	Name = value.Name,
	Parent = value.Parent,
	StateMachineOf = value.StateMachineOf,
	IsImport = value.IsImport,
	IsAbstract = value.IsAbstract,
	IsStateMachine = value.IsStateMachine,
	IsStruct = value.IsStruct,
	Fields = fields,
	Methods = methods,
	Defaults = defaults
};

	private static FieldDecl Single( FieldDecl value, string name ) => new()
	{
		Names = [name],
		Type = value.Type,
		Specifiers = value.Specifiers,
		Annotation = value.Annotation,
		IsBinding = value.IsBinding,
		Binding = value.Binding,
		Initializer = value.Initializer
	};

	private static SourceFile ExpandFields( SourceFile file ) => new( file.Path, file.Items.SelectMany( item => item is FieldItem field
		? field.Decl.Names.Select( name => (Item)new FieldItem( Single( field.Decl, name ) ) ) : [item] ).ToList() );

	// Give annotated functions separate keys so a wrapper does not conflict with the function it wraps.
	public static string Key( Item item ) => item switch
	{
		ClassItem value => "type:" + Symbols.ClassKey( value.Decl ),
		EnumItem value => "enum:" + value.Decl.Name,
		FieldItem value => ( value.Decl.Annotation is null ? "field:" : "addfield:" ) + value.Decl.Annotation?.Class + "." + value.Decl.Names.Single(),
		FunctionItem value => ( value.Decl.Annotation?.Kind switch
		{
			AnnotationKind.WrapMethod => "wrap:",
			AnnotationKind.ReplaceMethod => "replace:",
			AnnotationKind.AddMethod => "add:",
			_ => "function:"
		} )
			+ ( value.Decl.Annotation?.Class is { Length: > 0 } owner ? owner + "." : "" ) + value.Decl.Name,
		_ => throw new NotSupportedException()
	};
}
