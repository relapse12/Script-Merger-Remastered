#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace WitcherScriptMerger.FileIndex;

// Read bundle lists in parallel, then return them in request order.
internal sealed class BundleScanCoordinator
{
	public const int DefaultWorkerCount = 4;

	private readonly BundleListingCache? cache;

	private readonly Func<string, IReadOnlyList<string>> listingFactory;

	private readonly int workerCount;

	public BundleScanCoordinator(
		BundleListingCache? cache,
		Func<string, IReadOnlyList<string>> listingFactory,
		int workerCount = DefaultWorkerCount )
	{
		this.cache = cache;
		this.listingFactory = listingFactory ?? throw new ArgumentNullException( nameof( listingFactory ) );

		if ( workerCount < 1 )
		{
			throw new ArgumentOutOfRangeException( nameof( workerCount ) );
		}

		this.workerCount = workerCount;
	}

	// A missing bundle would make the scan incomplete, so fail the scan. Progress callbacks may run on several threads.
	public IReadOnlyList<BundleScanResult> Scan(
		IReadOnlyList<BundleScanRequest> requests,
		Action<int, BundleScanRequest>? progress = null )
	{
		ArgumentNullException.ThrowIfNull( requests );

		BundleScanResult[] results = new BundleScanResult[requests.Count];
		int completed = 0;

		Parallel.For( 0, requests.Count, new ParallelOptions
		{
			MaxDegreeOfParallelism = workerCount
		}, index =>
		{
			BundleScanRequest request = requests[index];

			if ( !File.Exists( request.BundlePath ) )
			{
				throw new FileNotFoundException(
					UiText.T( "BundleScanCoordinator", "A bundle disappeared during conflict scanning." ),
					request.BundlePath );
			}

			BundleListingCacheResult listing = cache is null
				? new BundleListingCacheResult( listingFactory( request.BundlePath ), CacheHit: false, string.Empty )
				: cache.GetOrCreate( request.BundlePath, listingFactory );

			results[index] = new BundleScanResult( request, listing.Paths, listing.CacheHit, listing.BundleSha256 );

			int completedCount = Interlocked.Increment( ref completed );

			progress?.Invoke( completedCount, request );
		} );

		return results;
	}
}

internal sealed record BundleScanRequest( int Order, string ModName, string BundlePath );

internal sealed record BundleScanResult(
	BundleScanRequest Request,
	IReadOnlyList<string> Paths,
	bool CacheHit,
	string BundleSha256 );
