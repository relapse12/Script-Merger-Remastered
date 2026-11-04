#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using WSCompiler.Core.Scripts.Standalone;


namespace WitcherScriptMerger.Merging;

// Saved with a merge check so later scans can decide which input changes invalidate it.
// Plain declaration names allow comparison with scripts that could not be fully parsed.
public sealed record MergeDependencyScope(
	string[] Mods,
	string[] ChangedNames,
	string[] OverlapNames,
	string[] UsedNames,
	string[] Paths,
	MergeSourceFile[] Files,
	string[]? DefinedSymbols = null,
	string[]? UsedSymbols = null )
{
	// Follow dependencies beyond the selected files. Loose inputs count per file; compiled blobs must be included whole.
	public static MergeDependencyScope Create(
		IReadOnlyList<MergeModule> modules,
		IReadOnlyList<SourceFile> vanilla,
		IReadOnlySet<string> selected,
		MergePlan? plan = null )
	{
		var catalog = new DeclarationCatalog( vanilla, modules );
		var original = vanilla.SelectMany( file => file.Items ).SelectMany( DeclarationCatalog.Declares ).ToHashSet( StringComparer.Ordinal );
		var nodes = modules.SelectMany( module => module.IsBlob || module.Files.Count == 0 ? [module]
			: module.Files.Select( file => module with { Files = new[] { file } } ) ).Select( module =>
{
	var items = module.Files.SelectMany( file => file.Items ).ToArray();
	var changes = items.SelectMany( DeclarationCatalog.Defines ).ToHashSet( StringComparer.Ordinal );
	var uses = items.SelectMany( catalog.PhaseDependencies ).Concat( module.LinkedSymbols ?? [] ).ToHashSet( StringComparer.Ordinal );

	foreach ( string method in uses.Where( symbol => symbol.StartsWith( "method:", StringComparison.Ordinal ) ).ToArray() )
		foreach ( string owner in ( module.LinkedSymbols ?? [] ).Where( symbol => symbol.StartsWith( "owner:", StringComparison.Ordinal ) ) )
			uses.Add( catalog.ResolveMethod( owner[6..], method[7..] ) );

	var paths = module.IsBlob ? vanilla.Where( file => file.Items.SelectMany( DeclarationCatalog.Declares ).Any( changes.Contains ) ).Select( file => file.Path )
		: module.Files.Select( file => file.Path ).Concat( module.UnparsedPaths ?? [] );

	return (Module: module, Changes: changes, Uses: uses, Provides: items.SelectMany( DeclarationCatalog.Defines ).ToHashSet( StringComparer.Ordinal ),
		Paths: paths.Select( path => path.Replace( '\\', '/' ) ).ToHashSet( StringComparer.OrdinalIgnoreCase ));
} ).ToArray();
		var affected = nodes.Where( node => selected.Contains( node.Module.Name ) || plan?.SourceFiles?.Any( file =>
			file.Mod.Equals( node.Module.Name, StringComparison.OrdinalIgnoreCase ) && node.Module.Files.Any( source =>
				source.Path.Replace( '\\', '/' ).Equals( file.Path.Replace( '\\', '/' ), StringComparison.OrdinalIgnoreCase ) ) ) == true ).ToHashSet();

		if ( plan is not null && plan.SourceFiles is null )
			affected.UnionWith( nodes.Where( node => plan.IncludedMods.Contains( node.Module.Name, StringComparer.OrdinalIgnoreCase ) ) );

		if ( plan is null )
		{
			var symbols = affected.SelectMany( node => node.Changes ).ToHashSet( StringComparer.Ordinal );
			var names = symbols.Select( Name ).ToHashSet( StringComparer.Ordinal );
			var paths = affected.SelectMany( node => node.Paths ).ToHashSet( StringComparer.OrdinalIgnoreCase );

			foreach ( var node in nodes.Where( node => !affected.Contains( node ) ) )
				if ( node.Changes.Overlaps( symbols ) || node.Uses.Overlaps( symbols ) || node.Paths.Overlaps( paths )
					|| node.Module.UnparsedFiles is { Count: > 0 } && ( node.Module.UnparsedIdentifiers is null || node.Module.UnparsedIdentifiers.Any( names.Contains ) ) )
					affected.Add( node );
		}

		var scope = affected.ToHashSet();

		if ( plan is not null )
		{
			var external = plan.ExternalEdges.SelectMany( edge => new[] { edge.Consumer, edge.Provider } ).ToHashSet( StringComparer.OrdinalIgnoreCase );
			scope.UnionWith( nodes.Where( node => node.Module.IsBlob && external.Contains( node.Module.Name ) ) );
		}

		bool changed;
		do
		{
			changed = false;
			var used = scope.SelectMany( node => node.Uses ).Where( symbol => !original.Contains( symbol ) ).ToHashSet( StringComparer.Ordinal );

			foreach ( var node in nodes.Where( node => !scope.Contains( node ) ) )
				if ( node.Provides.Overlaps( used ) )
					changed |= scope.Add( node );
		} while ( changed );

		return new( scope.Select( node => node.Module.Name ).Distinct( StringComparer.OrdinalIgnoreCase ).Order( StringComparer.OrdinalIgnoreCase ).ToArray(),
			affected.SelectMany( node => node.Changes ).Where( symbol => !original.Contains( symbol ) ).Select( Name ).Distinct( StringComparer.Ordinal ).ToArray(),
			affected.SelectMany( node => node.Changes ).Select( Name ).Distinct( StringComparer.Ordinal ).ToArray(),
			scope.SelectMany( node => node.Uses ).Where( symbol => !original.Contains( symbol ) ).Select( Name ).Distinct( StringComparer.Ordinal ).ToArray(),
			affected.SelectMany( node => node.Paths ).Distinct( StringComparer.OrdinalIgnoreCase ).ToArray(),
			scope.SelectMany( node => node.Module.Files.Select( file => new MergeSourceFile( node.Module.Name, file.Path.Replace( '\\', '/' ) ) ) ).Distinct().ToArray(),
			affected.SelectMany( node => node.Changes ).Distinct( StringComparer.Ordinal ).ToArray(),
			scope.SelectMany( node => node.Uses ).Distinct( StringComparer.Ordinal ).ToArray() );
	}

	public static string[] DeclaredNames( IEnumerable<Item> items ) => items.SelectMany( DeclarationCatalog.Defines ).Select( Name ).Distinct( StringComparer.Ordinal ).ToArray();

	private static string Name( string symbol ) => symbol[( Math.Max( symbol.LastIndexOf( ':' ), symbol.LastIndexOf( '.' ) ) + 1 )..];
}
