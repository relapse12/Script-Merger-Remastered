#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WitcherScriptMerger.XmlCoverage;

internal sealed record BundledXmlVerificationSource(
	string ModName,
	string BundleName,
	string BundlePath );

// The packed resource must exactly match the staged XML, since later repacking uses that staged copy.
internal sealed class BundledXmlVerificationRequest
{
	public required string GameDirectory { get; init; }
	public required string ResourcePath { get; init; }
	public required string CandidateModName { get; init; }
	public required string CandidateBundleName { get; init; }
	public required string CandidateBundlePath { get; init; }
	public required string StagedResourcePath { get; init; }
	public required IReadOnlyList<BundledXmlVerificationSource> Sources { get; init; }
	public required string JobsRoot { get; init; }
}

internal sealed class BundledXmlVerifiedSource
{
	public required BundledXmlVerificationSource Source { get; init; }
	public required string ExtractedResourcePath { get; init; }
	public required XmlCoverageSourceResult Coverage { get; init; }
}

// Keep input hashes with the result so callers can check it is still valid before recording the merge.
internal sealed class BundledXmlMergeVerificationResult
{
	private readonly IReadOnlyDictionary<string, BundledXmlInputSnapshot> inputSnapshots;

	internal BundledXmlMergeVerificationResult(
		BundledXmlVerificationRequest request,
		string jobDirectory,
		string diagnosticReportPath,
		string vanillaBundlePath,
		string vanillaResourcePath,
		string candidateResourcePath,
		IReadOnlyList<BundledXmlVerifiedSource> sources,
		XmlCoverageResult coverage,
		IReadOnlyDictionary<string, BundledXmlInputSnapshot> inputSnapshots )
	{
		Request = request;
		JobDirectory = jobDirectory;
		DiagnosticReportPath = diagnosticReportPath;
		VanillaBundlePath = vanillaBundlePath;
		VanillaResourcePath = vanillaResourcePath;
		CandidateResourcePath = candidateResourcePath;
		Sources = sources;
		Coverage = coverage;
		this.inputSnapshots = inputSnapshots;
	}

	public BundledXmlVerificationRequest Request { get; }
	public string JobDirectory { get; }
	public string DiagnosticReportPath { get; }
	public string VanillaBundlePath { get; }
	public string VanillaResourcePath { get; }
	public string CandidateResourcePath { get; }
	public IReadOnlyList<BundledXmlVerifiedSource> Sources { get; }
	public XmlCoverageResult Coverage { get; }
	public bool IsVerified => Coverage.IsCovered && Sources.All( source => source.Coverage.Status == XmlCoverageStatus.Covered );

	public async Task EnsureInputsUnchangedAsync( CancellationToken cancellationToken = default )
	{
		foreach ( (string path, BundledXmlInputSnapshot expected) in inputSnapshots )
		{
			BundledXmlInputSnapshot current = await BundledXmlInputSnapshot.CaptureAsync( path, cancellationToken )
				.ConfigureAwait( false );

			if ( !expected.Equals( current ) )
			{
				throw new InvalidOperationException(
					UiText.F(
						"BundledXmlMergeVerificationModels",
						$"'{path}' changed after verification. Refresh conflicts and verify the existing merge again." ) );
			}
		}
	}

	// Hold inputs read-locked while recording the verified merge.
	public Task<BundledXmlInputLease> AcquireVerifiedInputLeaseAsync(
		CancellationToken cancellationToken = default )
	{
		return BundledXmlInputLease.AcquireAsync( inputSnapshots, cancellationToken );
	}

	public void DeleteSuccessfulJob()
	{
		try
		{
			if ( Directory.Exists( JobDirectory ) )
			{
				Directory.Delete( JobDirectory, recursive: true );
			}
		}
		catch ( IOException )
		{
		}
		catch ( UnauthorizedAccessException )
		{
		}
	}
}

internal sealed record BundledXmlInputSnapshot( long Length, string Sha256 )
{
	public static async Task<BundledXmlInputSnapshot> CaptureAsync(
		string path,
		CancellationToken cancellationToken = default )
	{
		string fullPath = Path.GetFullPath( path );

		if ( !File.Exists( fullPath ) )
		{
			throw new FileNotFoundException(
				UiText.T( "BundledXmlMergeVerificationModels", "A bundled XML verification input is missing." ),
				fullPath );
		}

		await using FileStream stream = new(
			fullPath,
			FileMode.Open,
			FileAccess.Read,
			FileShare.Read,
			bufferSize: 128 * 1024,
			FileOptions.Asynchronous | FileOptions.SequentialScan );
		using System.Security.Cryptography.IncrementalHash hash =
			System.Security.Cryptography.IncrementalHash.CreateHash(
				System.Security.Cryptography.HashAlgorithmName.SHA256 );
		byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent( 128 * 1024 );

		try
		{
			int read;

			while ( ( read = await stream.ReadAsync( buffer.AsMemory( 0, buffer.Length ), cancellationToken )
				.ConfigureAwait( false ) ) > 0 )
			{
				hash.AppendData( buffer, 0, read );
			}

			return new BundledXmlInputSnapshot( stream.Length, Convert.ToHexString( hash.GetHashAndReset() ) );
		}
		finally
		{
			System.Buffers.ArrayPool<byte>.Shared.Return( buffer );
		}
	}
}

// Keep inputs read-locked through the inventory write so the saved verification still describes their contents.
internal sealed class BundledXmlInputLease : IDisposable
{
	private readonly IReadOnlyList<FileStream> streams;
	private bool disposed;

	private BundledXmlInputLease( IReadOnlyList<FileStream> streams )
	{
		this.streams = streams;
	}

	internal static async Task<BundledXmlInputLease> AcquireAsync(
		IReadOnlyDictionary<string, BundledXmlInputSnapshot> expectedSnapshots,
		CancellationToken cancellationToken )
	{
		List<FileStream> streams = new( expectedSnapshots.Count );

		try
		{
			foreach ( (string path, BundledXmlInputSnapshot expected) in expectedSnapshots
				.OrderBy( item => item.Key, StringComparer.OrdinalIgnoreCase ) )
			{
				cancellationToken.ThrowIfCancellationRequested();

				FileStream stream = new(
					path,
					FileMode.Open,
					FileAccess.Read,
					FileShare.Read,
					bufferSize: 128 * 1024,
					FileOptions.Asynchronous | FileOptions.SequentialScan );

				streams.Add( stream );

				BundledXmlInputSnapshot current = await CaptureOpenStreamAsync( stream, cancellationToken )
					.ConfigureAwait( false );

				if ( !expected.Equals( current ) )
				{
					throw new InvalidOperationException(
						UiText.F(
							"BundledXmlMergeVerificationModels",
							$"'{path}' changed after verification. Refresh conflicts and verify the existing merge again." ) );
				}
			}

			return new BundledXmlInputLease( streams );
		}
		catch
		{
			foreach ( FileStream stream in streams )
			{
				stream.Dispose();
			}

			throw;
		}
	}

	// Hash the locked stream so the check covers the same file held for the operation.
	private static async Task<BundledXmlInputSnapshot> CaptureOpenStreamAsync(
		FileStream stream,
		CancellationToken cancellationToken )
	{
		stream.Position = 0;

		using System.Security.Cryptography.IncrementalHash hash =
			System.Security.Cryptography.IncrementalHash.CreateHash(
				System.Security.Cryptography.HashAlgorithmName.SHA256 );
		byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent( 128 * 1024 );

		try
		{
			int read;

			while ( ( read = await stream.ReadAsync( buffer.AsMemory( 0, buffer.Length ), cancellationToken )
				.ConfigureAwait( false ) ) > 0 )
			{
				hash.AppendData( buffer, 0, read );
			}

			return new BundledXmlInputSnapshot( stream.Length, Convert.ToHexString( hash.GetHashAndReset() ) );
		}
		finally
		{
			System.Buffers.ArrayPool<byte>.Shared.Return( buffer );
		}
	}

	public void Dispose()
	{
		if ( disposed )
		{
			return;
		}

		disposed = true;

		foreach ( FileStream stream in streams )
		{
			stream.Dispose();
		}
	}
}
