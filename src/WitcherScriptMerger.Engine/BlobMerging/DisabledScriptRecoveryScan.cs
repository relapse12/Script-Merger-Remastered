#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WitcherScriptMerger.Inventory;
using WitcherScriptMerger.Merging;

namespace WitcherScriptMerger.BlobMerging;

internal sealed record DisabledScriptRecovery( IReadOnlyList<string> Unclaimed, IReadOnlyList<string> Issues );

// An .smr-disabled file can be restored only after its merge no longer needs it.
// Coordinate restoration with output removal so both versions do not become active together.
internal static class DisabledScriptRecoveryScan
{
	// If inventory or recovery state is incomplete, keep the disabled files. Their merge may still need them.
	// Also keep and report a disabled copy when a file already exists at its original path.
	internal static DisabledScriptRecovery Find( string mods, string state, MergeInventory inventory )
	{
		var unclaimed = new List<string>();
		var issues = new List<string>();

		if ( inventory.LoadError is not null || !Directory.Exists( mods ) || new BlobMergeStore( state ).HasInterruptedUpdate )
			return new( unclaimed, issues );

		try
		{
			using var inventoryLock = File.Exists( state ) ? new FileStream( state, FileMode.Open, FileAccess.Read, FileShare.Read ) : null;
			var owned = inventory.Merges.SelectMany( merge => merge.Mods ).Select( source => source.SourceFilePath )
				.Where( path => path?.EndsWith( ".smr-disabled", StringComparison.OrdinalIgnoreCase ) == true )
				.ToHashSet( StringComparer.OrdinalIgnoreCase );

			if ( BlobMergeTransaction.HasState( state ) )
			{
				var applied = BlobMergeTransaction.ReadApplied( state, mods, state );
				var folders = MergeSourceLocations.Find( applied.Manifest );

				if ( applied.Manifest.Mods.Any( mod => !Directory.Exists( MergeSourceLocations.PathFor( mods, MergeBuilder.SafePath( mods, mod ), folders ) ) ) )
					return new( unclaimed, issues );

				foreach ( var input in applied.Manifest.Inputs.Where( input => input.SuppressedPath is not null ) )
				{
					owned.Add( input.SuppressedPath! );
					owned.Add( MergeSourceLocations.PathFor( mods, input.SuppressedPath!, folders ) );
				}
			}
			else if ( inventory.Merges.Any( merge => merge.IsBlobMerge ) )
				return new(
					unclaimed,
					[UiText.T( "BlobMerge", "Disabled scripts were kept because their merge recovery information is missing." )] );

			var external = inventory.Merges.SelectMany( merge => merge.Mods ).Where( source => source.IsExternal )
				.Select( source => Path.TrimEndingDirectorySeparator( Path.GetFullPath( source.SourceDirectory ) ) + Path.DirectorySeparatorChar ).ToArray();
			var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
			var files = Directory.EnumerateDirectories( mods ).Where( folder => ( File.GetAttributes( folder ) & FileAttributes.ReparsePoint ) == 0 )
				.Select( folder => Path.Combine( folder, "content" ) ).Where( Directory.Exists ).SelectMany( content =>
					Directory.EnumerateFiles( content, "*.rsblob.smr-disabled", SearchOption.TopDirectoryOnly )
						.Concat( Directory.Exists( Path.Combine( content, "scripts" ) )
							? Directory.EnumerateFiles( Path.Combine( content, "scripts" ), "*.ws.smr-disabled", options ) : Array.Empty<string>() ) ).Where( path =>
					!owned.Contains( path ) && !external.Any( root => path.StartsWith( root, StringComparison.OrdinalIgnoreCase ) ) ).ToArray();

			foreach ( string path in files )
			{
				string original = path[..^".smr-disabled".Length];

				if ( !original.EndsWith( ".ws", StringComparison.OrdinalIgnoreCase ) && !original.EndsWith( ".rsblob", StringComparison.OrdinalIgnoreCase ) )
					continue;

				_ = MergeBuilder.SafePath( mods, Path.GetRelativePath( mods, path ) );

				if ( File.Exists( original ) || Directory.Exists( original ) )
				{
					issues.Add( UiText.Format( "BlobMerge", "An active script already exists. The unclaimed disabled copy was kept: {0}", path ) );

					continue;
				}

				unclaimed.Add( path );
			}
		}
		catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException )
		{
			issues.Add( UiText.Format( "BlobMerge", "Disabled scripts could not be reconciled: {0}", BlobMergeLifecycle.RecoveryMessage( error ) ) );
		}

		return new( unclaimed, issues );
	}
}
