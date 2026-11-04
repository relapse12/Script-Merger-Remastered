#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using WitcherScriptMerger.Merging;

namespace WitcherScriptMerger.BlobMerging;

// Share verified comparison results and failed attempts from the scan through the merge.
// Copies of LoadedBlobMerge use the same cache and lock while planning.
internal sealed class BlobPlanningCache
{
	private readonly Dictionary<(string Key, string Mod), MergeBodyComparison> comparisons = new();
	private readonly HashSet<string> attempts = new( StringComparer.Ordinal );

	internal IReadOnlyDictionary<(string Key, string Mod), MergeBodyComparison> Comparisons => comparisons;

	internal int AttemptCount => attempts.Count;

	internal bool WasAttempted( string attempt )
	{
		return attempts.Contains( attempt );
	}

	internal void RecordAttempt( string attempt )
	{
		attempts.Add( attempt );
	}

	internal void Store( string key, string mod, MergeBodyComparison comparison )
	{
		comparisons[(key, mod)] = comparison;
	}

	internal bool TryGet( string key, string mod, [NotNullWhen( true )] out MergeBodyComparison? comparison )
	{
		return comparisons.TryGetValue( (key, mod), out comparison );
	}

	internal void Restore( IEnumerable<SavedBodyComparison> saved, IEnumerable<string> savedAttempts )
	{
		foreach ( SavedBodyComparison comparison in saved )
			Store( comparison.Key, comparison.Mod, comparison.Value );

		attempts.UnionWith( savedAttempts );
	}

	internal SavedBodyComparison[] SavedComparisons()
	{
		return comparisons.Select( pair => new SavedBodyComparison( pair.Key.Key, pair.Key.Mod, pair.Value ) ).ToArray();
	}

	internal string[] SavedAttempts()
	{
		return attempts.ToArray();
	}
}
