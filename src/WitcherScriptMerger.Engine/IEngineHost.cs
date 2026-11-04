#nullable enable
using System.IO;
using WitcherScriptMerger.Inventory;

namespace WitcherScriptMerger;

internal interface IEngineHost
{
	IEngineSettings Settings { get; }

	// Keep this absolute path fixed even if the working directory changes.
	string MergedBundleContentDirectory { get; }

	void ShowMessage( string message );

	void ShowWarning( string message, string title );

	string? RetrieveMergedModName();

	// Called on a timer thread after reloading mods.settings; UI implementations must marshal control updates.
	void LoadOrderFileChanged();

	// External tools return zero on success. The host reports failures to the user before returning.

	// vanillaFile is the common original, when available.
	int RunTextMerge(
		FileMerger.MergeSource first,
		FileMerger.MergeSource second,
		FileInfo? vanillaFile,
		string outputPath,
		bool followFileLinks );

	// Packs sourceDirectory into a blob0.bundle inside outputDirectory.
	int PackBundle( string sourceDirectory, string outputDirectory );

	int GenerateMetadata( string bundleDirectory );
}
