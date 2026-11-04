#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WitcherScriptMerger.Inventory;
using WitcherScriptMerger.LoadOrder;
using WitcherScriptMerger.Merging;

namespace WitcherScriptMerger.BlobMerging;

internal enum BlobMergeChangeKind
{
	MissingSource, DisabledSource, RenamedSource, ChangedSource, MissingOutput
}
// Output limits a change to one output path. Without it, every output built from Mod is affected.
internal sealed record BlobMergeChange(
	BlobMergeChangeKind Kind,
	string Mod,
	string Path,
	IReadOnlySet<string> RemovedMods,
	string? CurrentName = null,
	string? Output = null );

// Collect all outdated inputs before deleting anything so removal can distinguish a partial rebuild from a full restore.
internal sealed record BlobRemovalScope( IReadOnlySet<string> Mods, IReadOnlySet<string> Outputs, IReadOnlySet<string> RemovedMods )
{
	internal static BlobRemovalScope Of( IEnumerable<BlobMergeChange> changes )
	{
		BlobMergeChange[] all = changes.ToArray();

		return new BlobRemovalScope(
			all.Where( change => change.Output is null ).Select( change => change.Mod )
				.ToHashSet( StringComparer.OrdinalIgnoreCase ),
			all.Where( change => change.Output is not null ).Select( change => change.Output! )
				.ToHashSet( StringComparer.OrdinalIgnoreCase ),
			all.SelectMany( change => change.RemovedMods ).ToHashSet( StringComparer.OrdinalIgnoreCase ) );
	}

	internal bool IsSameAs( BlobRemovalScope other )
	{
		return Mods.SetEquals( other.Mods ) && Outputs.SetEquals( other.Outputs ) && RemovedMods.SetEquals( other.RemovedMods );
	}
}

internal static class BlobMergeLifecycle
{
	internal static string RecoveryMessage( Exception error ) => error is AmbiguousMergeSourceException ambiguous
		? UiText.Format(
			"BlobMerge",
			"More than one folder matches {0}. The saved originals were kept. Remove the ambiguity before deleting this merge.",
			ambiguous.Mod )
		: error.Message;

	internal static BlobMergeChange? FindChange( string statePath, string modsDirectory, IReadOnlyList<ModLoadSetting> settings )
		=> FindChanges( statePath, modsDirectory, settings ).FirstOrDefault();

	// An unreadable file must leave the merge intact. Report the read failure to the caller.
	// Recover interrupted updates before checking for source changes.
	internal static IReadOnlyList<BlobMergeChange> FindChanges(
		string statePath,
		string modsDirectory,
		IReadOnlyList<ModLoadSetting> settings )
	{
		if ( !BlobMergeTransaction.HasState( statePath ) || new BlobMergeStore( statePath ).HasInterruptedUpdate )
			return [];

		var applied = BlobMergeTransaction.ReadApplied( statePath, modsDirectory, statePath );
		var locations = MergeSourceLocations.Find( applied.Manifest );

		string Locate( string path ) => MergeSourceLocations.PathFor( modsDirectory, path, locations );

		var removed = applied.Manifest.Mods.Where( mod =>
		{
			string root = MergeBuilder.SafePath( modsDirectory, mod );

			return PathAbsence.IsConfirmed( Locate( root ) ) || applied.Manifest.Inputs.Any( input => input.SuppressedPath is not null
				&& input.Path.StartsWith( root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase )
				&& PathAbsence.IsConfirmed( Locate( input.SuppressedPath! ) ) && PathAbsence.IsConfirmed( Locate( input.Path ) ) );
		} ).ToHashSet( StringComparer.OrdinalIgnoreCase );
		var changes = new List<BlobMergeChange>();

		foreach ( string mod in applied.Manifest.Mods )
		{
			string original = MergeBuilder.SafePath( modsDirectory, mod ), root = Locate( original ), name = Path.GetFileName( root );

			if ( PathAbsence.IsConfirmed( root ) )
			{
				changes.Add( new( BlobMergeChangeKind.MissingSource, mod, root, removed ) );

				continue;
			}

			if ( !name.StartsWith( "mod", StringComparison.OrdinalIgnoreCase )
				|| settings.FirstOrDefault( setting => setting.ModName.Equals( name, StringComparison.OrdinalIgnoreCase ) )?.IsEnabled == false )
			{
				changes.Add( new( BlobMergeChangeKind.DisabledSource, mod, root, removed, name ) );

				continue;
			}

			if ( !original.Equals( root, StringComparison.OrdinalIgnoreCase ) )
			{
				changes.Add( new( BlobMergeChangeKind.RenamedSource, mod, root, removed, name ) );

				continue;
			}

			foreach ( var input in applied.Manifest.Inputs.Where( input => input.SuppressedPath is not null
				&& input.Path.StartsWith( original + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase ) ) )
			{
				if ( File.Exists( input.Path ) )
				{
					changes.Add( new( BlobMergeChangeKind.ChangedSource, mod, input.Path, removed ) );

					break;
				}

				if ( PathAbsence.IsConfirmed( input.SuppressedPath! ) )
				{
					changes.Add( new( BlobMergeChangeKind.MissingSource, mod, input.SuppressedPath!, removed ) );

					break;
				}

				if ( MergeBuilder.Hash( input.SuppressedPath! ) != input.Sha256 )
				{
					changes.Add( new( BlobMergeChangeKind.ChangedSource, mod, input.SuppressedPath!, removed ) );

					break;
				}
			}
		}

		foreach ( var output in applied.Manifest.Outputs )
		{
			string path = MergeBuilder.SafePath( modsDirectory, output.Path );

			// A disabled output still needs its recovery records. Keep them until the user removes the merge.
			if ( PathAbsence.IsConfirmed( path ) && !MergeOutputFolder.IsRenamed( modsDirectory, output.Path ) )
				changes.Add( new( BlobMergeChangeKind.MissingOutput, "", path, removed, Output: output.Path ) );
		}

		return changes;
	}

	internal static string Describe( BlobMergeChange change ) => change.Kind switch
	{
		BlobMergeChangeKind.RenamedSource => UiText.Format(
			"MergeTree",
			"Outdated: source folder renamed from {0} to {1}. Recreate this merge to reflect the current installation.",
			change.Mod,
			change.CurrentName! ),
		BlobMergeChangeKind.DisabledSource => UiText.Format( "MergeTree", "Outdated: {0} is disabled.", change.CurrentName ?? change.Mod ),
		BlobMergeChangeKind.MissingSource => UiText.Format( "MergeTree", "Outdated: a source file or folder is missing: {0}", change.Path ),
		BlobMergeChangeKind.MissingOutput => UiText.Format( "MergeTree", "Outdated: a merged output is missing: {0}", change.Path ),
		_ => UiText.Format( "MergeTree", "Outdated: a source file has changed: {0}", change.Path )
	};
}
