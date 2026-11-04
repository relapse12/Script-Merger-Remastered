#nullable enable

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WitcherScriptMerger.Tools;

// Extraction must stay inside the caller's job folder.
internal interface IBundleResourceTool
{
	Task<IReadOnlyList<string>> ListPathsAsync(
		string bundlePath,
		ToolJobContext context,
		CancellationToken cancellationToken = default );

	Task<string> ExtractAsync(
		string bundlePath,
		string resourcePath,
		string outputDirectory,
		ToolJobContext context,
		CancellationToken cancellationToken = default );
}

internal interface IVanillaBundleResourceLocator
{
	Task<string?> FindBundleAsync(
		string gameDirectory,
		string resourcePath,
		ToolJobContext context,
		CancellationToken cancellationToken = default );
}
