#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WitcherScriptMerger.FileIndex;

namespace WitcherScriptMerger.BlobMerging;

// These conflicts share an input or output and must merge together. The tree presents them as one selection.
internal sealed record LinkedBlobMergeGroup( ModFile[] Conflicts, BlobMergeReadiness Readiness, string[]? Links = null )
{
	internal string? VanillaDirectory { get; init; }
	internal string Key => string.Join( "\n", BlobConflictSelection.Sources( Conflicts ).Order( StringComparer.OrdinalIgnoreCase ) );
	private IEnumerable<string> AllLinks => ( Links ?? [] ).Concat( Footprint( Readiness ) ).Distinct( StringComparer.OrdinalIgnoreCase );
	internal string SelectionKey => Key + "\n" + Readiness.Available + "\n" + string.Join( "\n", AllLinks.Order( StringComparer.OrdinalIgnoreCase ) );

	private string[] OutputsFor( ModFile file ) => Readiness.SymbolOutputs?.GetValueOrDefault( file.RelativePath.ToUpperInvariant() )
		?? ( Readiness.OutputPaths is [var path] ? [path] : [] );

	internal string[] PathsFor( ModFile file )
	{
		if ( string.IsNullOrWhiteSpace( file.VanillaScriptFile ) )
			return OutputsFor( file );

		string path = Path.GetRelativePath( file.MergePreflight?.VanillaDirectory ?? VanillaDirectory!, file.VanillaScriptFile ).Replace( '\\', '/' );

		// Official DLC sources may live outside content0, but use the same script-relative paths.
		if ( path.StartsWith( "../", StringComparison.Ordinal ) )
		{
			const string marker = "/content/scripts/";
			string full = file.VanillaScriptFile.Replace( '\\', '/' );
			int start = full.LastIndexOf( marker, StringComparison.OrdinalIgnoreCase );

			if ( start < 0 )
				return OutputsFor( file );

			path = full[( start + marker.Length )..];
		}

		return [path];
	}

	internal string[] DisplayPaths => ( Readiness.OutputPaths ?? [] ).SelectMany( path =>
	{
		var associated = Conflicts.Where( file => OutputsFor( file ).Contains( path, StringComparer.OrdinalIgnoreCase ) ).ToArray();

		return associated.Length == 0 ? [path] : associated.SelectMany( PathsFor );
	} ).Distinct( StringComparer.OrdinalIgnoreCase ).ToArray();
	internal bool HasFileSections => DisplayPaths.Length > 1;
	internal string LayoutKey => SelectionKey + "\n" + string.Join( "\n", DisplayPaths ) + "\n" + string.Join(
		"\n",
		Conflicts.OrderBy( file => file.RelativePath, StringComparer.OrdinalIgnoreCase )
		.Select( file => file.RelativePath + ":" + string.Join( "|", PathsFor( file ) ) ) );
	internal string Label => DisplayPaths is [var path] ? path.Replace( '/', '\\' )
		: UiText.Format(
			"BlobMerge",
			"Scripts from {0}",
			string.Join(
				", ",
				Readiness.BlobInputs ?? BlobConflictSelection.Sources( Conflicts ).Order( StringComparer.OrdinalIgnoreCase ).ToArray() ) );
	internal string Description => Readiness.Available
		? UiText.T(
			"BlobMerge",
			"These conflicts are merged together because they share a compiled mod or script file. Mods shown only for context remain unchanged." )
		: UiText.Format( "BlobMerge", "Merge unavailable: {0}", Readiness.Reason );
	internal static string FileDescription => UiText.T(
		"BlobMerge",
		"Conflicts are shown under their original script file. The group checkbox selects the linked resolution." );

	internal bool MatchesFile( string query ) => ConflictSearch.IsFileMatch( Label, query )
		|| DisplayPaths.Any( path => ConflictSearch.IsFileMatch( path, query ) );

	// Sharing any of these files or mods links two groups.
	private static IEnumerable<string> Footprint( BlobMergeReadiness readiness )
		=> ( readiness.BlobInputs ?? [] ).Select( name => "blob:" + name )
			.Concat( ( readiness.SourceFiles ?? [] ).Select( file => "source:" + file.Mod + "/" + file.Path.Replace( '\\', '/' ) ) )
			.Concat( ( readiness.OutputPaths ?? [] ).Select( path => "output:" + path.Replace( '\\', '/' ) ) );

	// Joining two groups can reveal more shared inputs. Recheck the combined group and repeat until no groups join.
	internal static IReadOnlyList<LinkedBlobMergeGroup> Create(
		IEnumerable<ModFile> files,
		Func<IEnumerable<string>, BlobMergeReadiness> inspect,
		string? vanillaDirectory )
	{
		var groups = files.Where( BlobConflictSelection.IsConventionalConflict )
			.Select( file => new LinkedBlobMergeGroup( [file], inspect( BlobConflictSelection.Sources( [file] ) ) ) { VanillaDirectory = vanillaDirectory } )
			.Where( group => group.Readiness is { Available: true, BlobInputs: not null, OutputPaths.Length: > 0 } ).ToList();
		bool changed;

		do
		{
			changed = false;

			var owners = new Dictionary<string, int>( StringComparer.OrdinalIgnoreCase );

			for ( int i = 0; i < groups.Count && !changed; i++ )
				foreach ( string key in groups[i].AllLinks )
				{
					if ( !owners.TryGetValue( key, out int other ) )
					{
						owners[key] = i;

						continue;
					}

					if ( other == i )
						continue;

					var members = groups[other].Conflicts.Concat( groups[i].Conflicts ).OrderBy( file => file.RelativePath, StringComparer.OrdinalIgnoreCase ).ToArray();

					groups[other] = new(
						members,
						inspect( BlobConflictSelection.Sources( members ) ),
						groups[other].AllLinks.Concat( groups[i].AllLinks ).Distinct( StringComparer.OrdinalIgnoreCase ).ToArray() )
					{
						VanillaDirectory = vanillaDirectory
					};
					groups.RemoveAt( i );
					changed = true;

					break;
				}
		} while ( changed );

		return groups;
	}
}
