using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WitcherScriptMerger.FileIndex;
using WitcherScriptMerger.Inventory;
using WitcherScriptMerger.Tools;

namespace WitcherScriptMerger;

// Hide conflicts only when a current merge resolves them. Each window keeps its own preparation state.
internal sealed class ConflictPreparation
{
	private readonly ConcurrentDictionary<string, (long Length, DateTime Written, string Hash)> sourceHashes =
		new ConcurrentDictionary<string, (long Length, DateTime Written, string Hash)>( StringComparer.OrdinalIgnoreCase );

	internal List<(ModFile Conflict, Merge Merge)> Prepare( IEnumerable<ModFile> conflicts, MergeInventory inventory, GamePaths game )
	{
		var mergesByPath = inventory.Merges.GroupBy( merge => merge.RelativePath, StringComparer.InvariantCultureIgnoreCase )
			.ToDictionary( group => group.Key, group => group.First(), StringComparer.InvariantCultureIgnoreCase );
		var prepared = new List<(ModFile, Merge)>();

		foreach ( ModFile conflict in conflicts )
		{
			mergesByPath.TryGetValue( conflict.RelativePath, out Merge merge );

			if ( !inventory.HasResolvedConflict( conflict, merge, game, HashSource ) )
				prepared.Add( (conflict, merge) );
		}

		return prepared;
	}

	internal void ClearSourceHashes()
	{
		sourceHashes.Clear();
	}

	// Reuse hashes while size and write time match. The next scan clears this cache.
	private string HashSource( string path )
	{
		FileInfo file = new FileInfo( path );
		long length = file.Length;
		DateTime written = file.LastWriteTimeUtc;

		if ( sourceHashes.TryGetValue( path, out var saved ) && saved.Length == length && saved.Written == written )
			return saved.Hash;

		string hash = Hasher.ComputeHash( path );

		sourceHashes[path] = (length, written, hash);

		return hash;
	}
}
