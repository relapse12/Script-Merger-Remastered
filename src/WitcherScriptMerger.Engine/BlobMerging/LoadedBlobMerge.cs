#nullable enable
using System;
using System.Collections.Generic;
using WitcherScriptMerger.Inventory;
using WitcherScriptMerger.LoadOrder;
using WSCompiler.Core.Scripts.Standalone;
using WitcherScriptMerger.Merging;

namespace WitcherScriptMerger.BlobMerging;

// ExistingLooseMerge marks an ordinary merged-output folder. StateKnown handles uncertain Mod.io settings.
internal sealed record BlobMergeSource( string Name, string Directory, bool IsModIo, IReadOnlyList<string> Blobs,
	bool ExistingLooseMerge = false, bool StateKnown = true );

// Keep the source and hashes used for planning so execution can verify the same inputs.
// Selection changes share PlanningCache; ordinary merges taken over during a run are tracked separately.
internal sealed record LoadedBlobMerge( IReadOnlyList<MergeModule> Modules, IReadOnlyList<SourceFile> Vanilla,
	IReadOnlyList<MergeInputFile> Snapshot, IReadOnlyList<BlobMergeSource> Sources,
	IReadOnlyList<ModLoadSetting> Settings, string VanillaDirectory, string ModsDirectory, string SettingsPath,
	string? SettingsHash, IReadOnlyDictionary<string, string[]> SourcePaths, string[] ConventionalFolders, string[] VanillaPaths,
	BlobPlanningCache PlanningCache )
{
	internal string LooseModName { get; init; } = MergeBuilder.LooseName;
	internal string? InventoryPath { get; init; }
	internal AppliedBlobMerge? Previous { get; init; }
	internal string? StateFile { get; init; }
	internal string? StateHash { get; init; }
	internal IReadOnlySet<string> Selected { get; init; } = new HashSet<string>();
	internal IReadOnlyList<MergeInputFile> Guards { get; init; } = Array.Empty<MergeInputFile>();
	internal IReadOnlyDictionary<string, string> PreviousOutputs { get; init; } = new Dictionary<string, string>();
	internal IReadOnlyDictionary<string, string> SourceReads { get; init; } = new Dictionary<string, string>();
	internal IReadOnlyList<MergeModule> ReferenceModules { get; init; } = Array.Empty<MergeModule>();
	internal IReadOnlyDictionary<string, Dictionary<string, Dictionary<long, string>>> EnumReferences { get; init; } = new Dictionary<string, Dictionary<string, Dictionary<long, string>>>();

	internal IReadOnlyList<Merge> RetainedAdoptions { get; init; } = Array.Empty<Merge>();
	internal IReadOnlyDictionary<string, string> RetainedMergeSources { get; init; } = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
	internal IReadOnlySet<string>? RemovedMods { get; init; }
	internal IReadOnlySet<string> ExcludedSourceFiles { get; init; } = new HashSet<string>( StringComparer.OrdinalIgnoreCase );

	internal string ReadPath( string path ) => SourceReads.TryGetValue( path, out string? read ) ? read : RetainedMergeSources.GetValueOrDefault( path, path );
}
