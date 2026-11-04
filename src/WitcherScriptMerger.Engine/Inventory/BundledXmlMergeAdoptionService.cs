using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WitcherScriptMerger.FileIndex;
using WitcherScriptMerger.Tools;

namespace WitcherScriptMerger.Inventory;

// Record an existing XML merge only after verifying that it includes the supplied sources' changes.
internal static class BundledXmlMergeAdoptionService
{
	internal sealed record AdoptionResult( Merge Merge, int ReplacedRecordCount );

	internal enum SavedState
	{
		Saved,
		NotSaved,
		Unconfirmed
	}

	// A save can replace the file and still report a failure. Read it back to determine whether this exact record was saved.
	internal static SavedState ReadSavedState( Func<MergeInventory> loadSaved, Merge adopted )
	{
		try
		{
			MergeInventory saved = loadSaved();

			if ( saved.LoadError != null )
				return SavedState.Unconfirmed;

			return saved.Merges.Any( merge => IsSameRecord( merge, adopted ) ) ? SavedState.Saved : SavedState.NotSaved;
		}
		catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException )
		{
			return SavedState.Unconfirmed;
		}
	}

	// Compare only what is saved; display flags do not identify a merge.
	internal static bool IsSameRecord( Merge saved, Merge adopted )
	{
		return AsSaved( saved ).SequenceEqual( AsSaved( adopted ) );
	}

	private static byte[] AsSaved( Merge merge )
	{
		MergeInventory single = new();

		single.Merges.Add( merge );

		return single.Serialize();
	}

	// Exclude the merged output and known-disabled sources. Sources with unknown state still need verification.
	internal static IReadOnlyList<FileHash> VerifiedSources( ModFile conflict, string candidateMergedModName )
	{
		return conflict.ParticipatingSources
			.Where( source => !source.Name.Equals( candidateMergedModName, StringComparison.OrdinalIgnoreCase ) )
			.ToList();
	}

	public static AdoptionResult AdoptVerified(
		MergeInventory inventory,
		ModFile conflict,
		string candidateMergedModName,
		IEnumerable<string> verifiedCoveredSourceModNames,
		GamePaths game,
		string mergedBundleContentDirectory )
	{
		return AdoptVerified(
			inventory,
			conflict,
			candidateMergedModName,
			verifiedCoveredSourceModNames,
			( modName, bundleName ) => Path.Combine(
				game.ModsDirectory,
				modName,
				GamePaths.BundleBase,
				bundleName ),
			Path.Combine( mergedBundleContentDirectory, conflict?.RelativePath ?? string.Empty ) );
	}

	// Finish validation and hashing before changing the inventory.
	internal static AdoptionResult AdoptVerified(
		MergeInventory inventory,
		ModFile conflict,
		string candidateMergedModName,
		IEnumerable<string> verifiedCoveredSourceModNames,
		Func<string, string, string> bundlePathResolver,
		string stagedMergedResourcePath )
	{
		List<IGrouping<string, FileHash>> modGroups;
		FileHash candidate;
		IReadOnlyList<FileHash> contributors;
		string candidateBundleName;
		Merge adoptedMerge;

		ArgumentNullException.ThrowIfNull( inventory );
		ArgumentNullException.ThrowIfNull( conflict );
		ArgumentNullException.ThrowIfNull( verifiedCoveredSourceModNames );
		ArgumentNullException.ThrowIfNull( bundlePathResolver );

		RequireAdoptableConflict( conflict, candidateMergedModName );
		modGroups = ModGroups( conflict );
		candidate = Candidate( conflict, modGroups, candidateMergedModName );
		contributors = Contributors( conflict, candidateMergedModName );
		RequireFullCoverage( verifiedCoveredSourceModNames, contributors, candidateMergedModName );
		RequireRecordedContributors( inventory, conflict, candidate.Name, contributors );

		candidateBundleName = ResolveBundleName( conflict, candidate );
		RequireFile( bundlePathResolver( candidate.Name, candidateBundleName ), $"candidate bundle for '{candidate.Name}'" );

		RequireFile( stagedMergedResourcePath, "staged merged resource" );

		adoptedMerge = BuildAdoptedMerge( conflict, candidate.Name, candidateBundleName, contributors, bundlePathResolver );

		return new AdoptionResult( adoptedMerge, ReplaceRecords( inventory, conflict.RelativePath, adoptedMerge ) );
	}

	private static void RequireAdoptableConflict( ModFile conflict, string candidateMergedModName )
	{
		if ( string.IsNullOrWhiteSpace( conflict.RelativePath )
			|| !conflict.IsBundleContent
			|| !ModFile.IsXml( conflict.RelativePath ) )
		{
			throw new ArgumentException(
				UiText.T( "BundledXmlMergeAdoptionService", "Only bundled XML resources can be adopted as verified XML merges." ),
				nameof( conflict ) );
		}

		if ( string.IsNullOrWhiteSpace( candidateMergedModName ) )
			throw new ArgumentException(
				UiText.T( "BundledXmlMergeAdoptionService", "An explicit candidate merged mod is required." ),
				nameof( candidateMergedModName ) );
	}

	private static List<IGrouping<string, FileHash>> ModGroups( ModFile conflict )
	{
		List<IGrouping<string, FileHash>> modGroups = conflict.Mods
			.Where( mod => mod != null && !string.IsNullOrWhiteSpace( mod.Name ) )
			.GroupBy( mod => mod.Name, StringComparer.OrdinalIgnoreCase )
			.ToList();

		if ( modGroups.Count != conflict.Mods.Count || modGroups.Any( group => group.Count() != 1 ) )
		{
			throw new InvalidDataException(
				UiText.F(
					"BundledXmlMergeAdoptionService",
					$"The conflict for '{conflict.RelativePath}' contains missing or duplicate mod identities, so its source bundles are ambiguous." ) );
		}

		return modGroups;
	}

	private static FileHash Candidate( ModFile conflict, IEnumerable<IGrouping<string, FileHash>> modGroups, string candidateMergedModName )
	{
		FileHash candidate = modGroups
			.FirstOrDefault( group => group.Key.Equals( candidateMergedModName, StringComparison.OrdinalIgnoreCase ) )?
			.Single();

		if ( candidate == null )
		{
			throw new InvalidDataException(
				UiText.F(
					"BundledXmlMergeAdoptionService",
					$"Candidate merged mod '{candidateMergedModName}' does not provide '{conflict.RelativePath}'." ) );
		}

		return candidate;
	}

	private static IReadOnlyList<FileHash> Contributors( ModFile conflict, string candidateMergedModName )
	{
		IReadOnlyList<FileHash> contributors = VerifiedSources( conflict, candidateMergedModName );

		if ( contributors.Count == 0 )
			throw new InvalidDataException( UiText.T( "BundledXmlMergeAdoptionService", "There are no lower-priority source mods to record for this conflict." ) );

		return contributors;
	}

	private static void RequireFullCoverage(
		IEnumerable<string> coveredNames,
		IReadOnlyCollection<FileHash> contributors,
		string candidateMergedModName )
	{
		HashSet<string> coveredSources = new(
			coveredNames.Where( name => !string.IsNullOrWhiteSpace( name ) ),
			StringComparer.OrdinalIgnoreCase );

		if ( coveredSources.Contains( candidateMergedModName )
			|| coveredSources.Count != contributors.Count
			|| contributors.Any( source => !coveredSources.Contains( source.Name ) ) )
		{
			throw new InvalidDataException(
				UiText.T(
					"BundledXmlMergeAdoptionService",
					"The verified coverage set must contain every non-candidate source mod, and only those source mods." ) );
		}
	}

	// Keep earlier contributors in the record: their edits may still be present in the merged file.
	private static void RequireRecordedContributors(
		MergeInventory inventory,
		ModFile conflict,
		string candidateName,
		IReadOnlyCollection<FileHash> contributors )
	{
		string dropped = inventory.Merges
			.Where( merge => string.Equals( merge.RelativePath, conflict.RelativePath, StringComparison.OrdinalIgnoreCase ) )
			.SelectMany( merge => merge.Mods )
			.Select( source => source.Name )
			.FirstOrDefault( name => !name.Equals( candidateName, StringComparison.OrdinalIgnoreCase )
				&& !contributors.Any( source => source.Name.Equals( name, StringComparison.OrdinalIgnoreCase ) ) );

		if ( dropped != null )
		{
			throw new InvalidDataException(
				UiText.F(
					"BundledXmlMergeAdoptionService",
					$"The existing record for '{conflict.RelativePath}' names '{dropped}', which this adoption would drop. Delete that merge, then adopt again." ) );
		}
	}

	private static Merge BuildAdoptedMerge(
		ModFile conflict,
		string candidateName,
		string candidateBundleName,
		IEnumerable<FileHash> contributors,
		Func<string, string, string> bundlePathResolver )
	{
		Merge adoptedMerge = new()
		{
			RelativePath = conflict.RelativePath,
			MergedModName = candidateName,
			BundleName = candidateBundleName
		};

		foreach ( FileHash source in contributors )
		{
			string sourceBundleName = ResolveBundleName( conflict, source );
			string sourceBundlePath = RequireFile( bundlePathResolver( source.Name, sourceBundleName ), $"source bundle for '{source.Name}'" );

			adoptedMerge.Mods.Add( new FileHash
			{
				Name = source.Name,
				BundleName = sourceBundleName,
				Hash = Hasher.ComputeHash( sourceBundlePath ),
				SourceDirectory = source.SourceDirectory,
				Origin = source.Origin,
				ModIoId = source.ModIoId
			} );
		}

		return adoptedMerge;
	}

	private static int ReplaceRecords( MergeInventory inventory, string relativePath, Merge adoptedMerge )
	{
		List<Merge> existing = inventory.Merges
			.Where( merge => string.Equals( merge.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase ) )
			.ToList();
		int insertionIndex = existing.Count == 0 ? inventory.Merges.Count : inventory.Merges.IndexOf( existing[0] );

		foreach ( Merge stale in existing )
			inventory.Merges.Remove( stale );

		inventory.Merges.Insert( Math.Min( insertionIndex, inventory.Merges.Count ), adoptedMerge );

		return existing.Count;
	}

	// Require a filename here so the bundle stays inside the mod's folder.
	private static string ResolveBundleName( ModFile conflict, FileHash source )
	{
		string bundleName = string.IsNullOrWhiteSpace( source.BundleName )
			? conflict.BundleName
			: source.BundleName;

		if ( string.IsNullOrWhiteSpace( bundleName ) )
		{
			throw new InvalidDataException(
				UiText.F(
					"BundledXmlMergeAdoptionService",
					$"No source bundle was recorded for mod '{source.Name}' and resource '{conflict.RelativePath}'." ) );
		}

		if ( !Path.GetFileName( bundleName ).Equals( bundleName, StringComparison.Ordinal ) )
		{
			throw new InvalidDataException( UiText.F( "BundledXmlMergeAdoptionService", $"The recorded source bundle name '{bundleName}' is not a file name." ) );
		}

		return bundleName;
	}

	private static string RequireFile( string path, string description )
	{
		if ( string.IsNullOrWhiteSpace( path ) || !File.Exists( path ) )
		{
			throw new FileNotFoundException( UiText.F( "BundledXmlMergeAdoptionService", $"The {description} does not exist." ), path );
		}

		return Path.GetFullPath( path );
	}
}
