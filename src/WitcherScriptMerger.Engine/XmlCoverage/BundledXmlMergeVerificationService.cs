#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WitcherScriptMerger.FileIndex;
using WitcherScriptMerger.Tools;

namespace WitcherScriptMerger.XmlCoverage;

// Verify that an existing bundle contains its sources' XML changes before recording it as a merge.
// Extraction stays in the job folder; the caller decides whether to adopt the result.
internal sealed class BundledXmlMergeVerificationService
{
	private const long MinimumFreeBytes = 64L * 1024L * 1024L;
	private readonly IBundleResourceTool bundleTool;
	private readonly IVanillaBundleResourceLocator vanillaLocator;
	private readonly XmlCoverageAnalyzer analyzer;

	public BundledXmlMergeVerificationService(
		IBundleResourceTool bundleTool,
		IVanillaBundleResourceLocator vanillaLocator,
		XmlCoverageAnalyzer analyzer )
	{
		this.bundleTool = bundleTool ?? throw new ArgumentNullException( nameof( bundleTool ) );
		this.vanillaLocator = vanillaLocator ?? throw new ArgumentNullException( nameof( vanillaLocator ) );
		this.analyzer = analyzer ?? throw new ArgumentNullException( nameof( analyzer ) );
	}

	// Keep failed jobs with their report for diagnosis. Canceled jobs can be discarded.
	public async Task<BundledXmlMergeVerificationResult> VerifyAsync(
		BundledXmlVerificationRequest request,
		IProgress<string>? progress = null,
		CancellationToken cancellationToken = default )
	{
		string jobsRoot;
		string jobDirectory;
		string reportPath;
		ToolJobContext context;

		ValidateRequest( request );
		jobsRoot = Path.GetFullPath( request.JobsRoot );
		Directory.CreateDirectory( jobsRoot );
		EnsureFreeSpace( jobsRoot );
		jobDirectory = Path.Combine(
			jobsRoot,
			DateTime.UtcNow.ToString( "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture ) + "-" + Guid.NewGuid().ToString( "N" ) );
		reportPath = Path.Combine( jobDirectory, "verification-report.txt" );
		context = ToolJobContext.Create(
			jobDirectory,
			new Progress<ProcessOutputLine>( line =>
			{
				if ( !string.IsNullOrWhiteSpace( line.Text ) )
					progress?.Report( line.Text );
			} ) );

		try
		{
			BundledXmlMergeVerificationResult result = await VerifyInJobAsync( request, context, jobDirectory, reportPath, progress, cancellationToken )
				.ConfigureAwait( false );

			WriteReport( result );

			return result;
		}
		catch ( OperationCanceledException )
		{
			TryDeleteDirectory( jobDirectory );

			throw;
		}
		catch ( Exception exception )
		{
			TryWriteFailureReport( reportPath, request, exception );

			throw new InvalidOperationException(
				UiText.F( "BundledXmlMergeVerificationService", $"{exception.Message}\n\nDiagnostic report: {reportPath}" ),
				exception );
		}
	}

	private async Task<BundledXmlMergeVerificationResult> VerifyInJobAsync(
		BundledXmlVerificationRequest request,
		ToolJobContext context,
		string jobDirectory,
		string reportPath,
		IProgress<string>? progress,
		CancellationToken cancellationToken )
	{
		string vanillaBundlePath = await FindVanillaBundleAsync( request, context, progress, cancellationToken ).ConfigureAwait( false );
		List<string> inputPaths = new()
		{
			vanillaBundlePath,
			request.CandidateBundlePath,
			request.StagedResourcePath
		};
		IReadOnlyDictionary<string, BundledXmlInputSnapshot> snapshots;
		IReadOnlyDictionary<string, BundledXmlInputSnapshot> currentSnapshots;
		string vanillaResource;
		string candidateResource;
		List<string> extractedSourcePaths;
		XmlCoverageResult coverage;

		inputPaths.AddRange( request.Sources.Select( source => source.BundlePath ) );
		snapshots = await CaptureInputsAsync( inputPaths, progress, cancellationToken ).ConfigureAwait( false );

		progress?.Report( UiText.T( "BundledXmlMergeVerificationService", "Extracting the vanilla resource..." ) );
		vanillaResource = await ExtractResourceAsync( request, vanillaBundlePath, "vanilla", context, cancellationToken ).ConfigureAwait( false );

		progress?.Report( UiText.F( "BundledXmlMergeVerificationService", $"Extracting the top-priority resource from {request.CandidateModName}..." ) );
		candidateResource = await ExtractResourceAsync( request, request.CandidateBundlePath, "candidate", context, cancellationToken ).ConfigureAwait( false );

		await RequireStagedResourceAsync( candidateResource, request.StagedResourcePath, cancellationToken ).ConfigureAwait( false );
		extractedSourcePaths = await ExtractSourcesAsync( request, context, progress, cancellationToken ).ConfigureAwait( false );

		progress?.Report( UiText.T( "BundledXmlMergeVerificationService", "Comparing each mod's semantic changes with the generated winner..." ) );
		coverage = analyzer.AnalyzeFiles( vanillaResource, candidateResource, extractedSourcePaths );

		currentSnapshots = await CaptureInputsAsync( inputPaths, progress: null, cancellationToken ).ConfigureAwait( false );
		EnsureSnapshotsMatch( snapshots, currentSnapshots );

		return new BundledXmlMergeVerificationResult(
			request,
			jobDirectory,
			reportPath,
			vanillaBundlePath,
			vanillaResource,
			candidateResource,
			VerifiedSources( request, extractedSourcePaths, coverage ),
			coverage,
			snapshots );
	}

	private async Task<string> FindVanillaBundleAsync(
		BundledXmlVerificationRequest request,
		ToolJobContext context,
		IProgress<string>? progress,
		CancellationToken cancellationToken )
	{
		string? vanillaBundlePath;

		progress?.Report( UiText.T( "BundledXmlMergeVerificationService", "Locating the vanilla XML resource..." ) );
		vanillaBundlePath = await vanillaLocator.FindBundleAsync(
			request.GameDirectory,
			request.ResourcePath,
			context,
			cancellationToken ).ConfigureAwait( false );

		if ( string.IsNullOrWhiteSpace( vanillaBundlePath ) )
		{
			throw new InvalidOperationException(
				UiText.F(
					"BundledXmlMergeVerificationService",
					$"The vanilla resource '{request.ResourcePath}' could not be found. Semantic coverage cannot be proven without an ancestor." ) );
		}

		return vanillaBundlePath;
	}

	private Task<string> ExtractResourceAsync(
		BundledXmlVerificationRequest request,
		string bundlePath,
		string folder,
		ToolJobContext context,
		CancellationToken cancellationToken )
	{
		return bundleTool.ExtractAsync( bundlePath, request.ResourcePath, context.CreateDirectory( folder ), context, cancellationToken );
	}

	// Later repacking uses Merged Bundle Content, so that copy must match the installed resource.
	private static async Task RequireStagedResourceAsync(
		string candidateResource,
		string stagedResourcePath,
		CancellationToken cancellationToken )
	{
		BundledXmlInputSnapshot candidatePayload = await BundledXmlInputSnapshot.CaptureAsync( candidateResource, cancellationToken ).ConfigureAwait( false );
		BundledXmlInputSnapshot stagedPayload = await BundledXmlInputSnapshot.CaptureAsync( stagedResourcePath, cancellationToken ).ConfigureAwait( false );

		if ( !candidatePayload.Equals( stagedPayload ) )
		{
			throw new InvalidOperationException(
				UiText.Format(
					"BundledXmlMergeVerificationService",
					"The XML extracted from the installed generated bundle is not byte-identical to Merged Bundle Content. Rebuild or refresh the generated bundle before adopting it." ) );
		}
	}

	// Use separate extraction folders because all sources have the same resource path.
	private async Task<List<string>> ExtractSourcesAsync(
		BundledXmlVerificationRequest request,
		ToolJobContext context,
		IProgress<string>? progress,
		CancellationToken cancellationToken )
	{
		List<string> extracted = new( request.Sources.Count );

		for ( int index = 0; index < request.Sources.Count; index++ )
		{
			BundledXmlVerificationSource source = request.Sources[index];
			string folder = Path.Combine( "sources", index.ToString( "D3", CultureInfo.InvariantCulture ) );

			progress?.Report( UiText.F( "BundledXmlMergeVerificationService", $"Extracting source {index + 1} of {request.Sources.Count}: {source.ModName}..." ) );
			extracted.Add( await ExtractResourceAsync( request, source.BundlePath, folder, context, cancellationToken ).ConfigureAwait( false ) );
		}

		return extracted;
	}

	private static List<BundledXmlVerifiedSource> VerifiedSources(
		BundledXmlVerificationRequest request,
		IReadOnlyList<string> extractedSourcePaths,
		XmlCoverageResult coverage )
	{
		List<BundledXmlVerifiedSource> verified = new( request.Sources.Count );

		for ( int index = 0; index < request.Sources.Count; index++ )
		{
			verified.Add( new BundledXmlVerifiedSource
			{
				Source = request.Sources[index],
				ExtractedResourcePath = extractedSourcePaths[index],
				Coverage = coverage.SourceResults[index]
			} );
		}

		return verified;
	}

	private static void ValidateRequest( BundledXmlVerificationRequest request )
	{
		ArgumentNullException.ThrowIfNull( request );

		if ( string.IsNullOrWhiteSpace( request.ResourcePath ) || !ModFile.IsXml( request.ResourcePath ) )
		{
			throw new ArgumentException(
				UiText.T( "BundledXmlMergeVerificationService", "Only bundled XML resource paths can be verified." ),
				nameof( request ) );
		}

		_ = RemasteredBundleFile.NormalizeResourcePath( request.ResourcePath );

		if ( string.IsNullOrWhiteSpace( request.GameDirectory )
			|| string.IsNullOrWhiteSpace( request.CandidateModName )
			|| string.IsNullOrWhiteSpace( request.CandidateBundleName )
			|| string.IsNullOrWhiteSpace( request.CandidateBundlePath )
			|| string.IsNullOrWhiteSpace( request.StagedResourcePath )
			|| string.IsNullOrWhiteSpace( request.JobsRoot ) )
		{
			throw new ArgumentException(
				UiText.T( "BundledXmlMergeVerificationService", "The bundled XML verification request is incomplete." ),
				nameof( request ) );
		}

		if ( request.Sources == null || request.Sources.Count == 0 )
		{
			throw new ArgumentException(
				UiText.T( "BundledXmlMergeVerificationService", "At least one non-candidate source mod is required." ),
				nameof( request ) );
		}

		if ( request.Sources.Any( source => source == null
			|| string.IsNullOrWhiteSpace( source.ModName )
			|| string.IsNullOrWhiteSpace( source.BundleName )
			|| string.IsNullOrWhiteSpace( source.BundlePath ) ) )
		{
			throw new ArgumentException(
				UiText.T( "BundledXmlMergeVerificationService", "Every source mod requires a name, bundle name, and bundle path." ),
				nameof( request ) );
		}

		if ( request.Sources.Any( source => source.ModName.Equals( request.CandidateModName, StringComparison.OrdinalIgnoreCase ) )
			|| request.Sources.Select( source => source.ModName ).Distinct( StringComparer.OrdinalIgnoreCase ).Count() != request.Sources.Count )
		{
			throw new ArgumentException(
				UiText.T( "BundledXmlMergeVerificationService", "The source set contains the candidate or a duplicate mod identity." ),
				nameof( request ) );
		}
	}

	private static async Task<IReadOnlyDictionary<string, BundledXmlInputSnapshot>> CaptureInputsAsync(
		IEnumerable<string> paths,
		IProgress<string>? progress,
		CancellationToken cancellationToken )
	{
		Dictionary<string, BundledXmlInputSnapshot> result = new( StringComparer.OrdinalIgnoreCase );
		string[] uniquePaths = paths.Select( Path.GetFullPath ).Distinct( StringComparer.OrdinalIgnoreCase ).ToArray();

		for ( int index = 0; index < uniquePaths.Length; index++ )
		{
			progress?.Report( UiText.F( "BundledXmlMergeVerificationService", $"Fingerprinting input {index + 1} of {uniquePaths.Length}..." ) );
			result.Add( uniquePaths[index], await BundledXmlInputSnapshot.CaptureAsync( uniquePaths[index], cancellationToken )
				.ConfigureAwait( false ) );
		}

		return result;
	}

	private static void EnsureSnapshotsMatch(
		IReadOnlyDictionary<string, BundledXmlInputSnapshot> expected,
		IReadOnlyDictionary<string, BundledXmlInputSnapshot> current )
	{
		foreach ( (string path, BundledXmlInputSnapshot snapshot) in expected )
		{
			if ( !current.TryGetValue( path, out BundledXmlInputSnapshot? actual ) || !snapshot.Equals( actual ) )
			{
				throw new InvalidOperationException(
					UiText.F(
						"BundledXmlMergeVerificationService",
						$"'{path}' changed while it was being verified. Refresh conflicts and try again." ) );
			}
		}
	}

	private static void WriteReport( BundledXmlMergeVerificationResult result )
	{
		StringBuilder report = new();

		report.AppendLine( UiText.T( "BundledXmlMergeVerificationService", "Bundled XML Existing-Merge Verification" ) )
			.AppendLine( "========================================" )
			.Append( UiText.T( "BundledXmlMergeVerificationService", "Outcome: " ) ).AppendLine( result.IsVerified ? UiText.T( "BundledXmlMergeVerificationService", "VERIFIED" ) : result.Coverage.Status.ToString() )
			.Append( UiText.T( "BundledXmlMergeVerificationService", "Resource: " ) ).AppendLine( result.Request.ResourcePath )
			.Append( UiText.T( "BundledXmlMergeVerificationService", "Candidate: " ) ).AppendLine( result.Request.CandidateModName )
			.AppendLine( UiText.T( "BundledXmlMergeVerificationService", "Packed candidate matches staged resource: Yes" ) )
			.Append( UiText.T( "BundledXmlMergeVerificationService", "Vanilla bundle: " ) ).AppendLine( result.VanillaBundlePath )
			.AppendLine();

		foreach ( BundledXmlVerifiedSource source in result.Sources )
		{
			report.Append( source.Source.ModName ).Append( ": " ).AppendLine( source.Coverage.Status.ToString() );

			foreach ( XmlCoverageFinding finding in source.Coverage.Findings )
			{
				report.Append( "  [" ).Append( finding.Status ).Append( "] " )
					.Append( finding.Location ).Append( " - " ).AppendLine( finding.Message );
			}
		}

		if ( !result.IsVerified )
		{
			report.AppendLine()
				.AppendLine( UiText.T( "BundledXmlMergeVerificationService", "No inventory record was added. Review the non-covered or ambiguous locations above." ) );
		}

		File.WriteAllText( result.DiagnosticReportPath, report.ToString() );
	}

	// A report-writing failure must preserve the original error.
	private static void TryWriteFailureReport(
		string reportPath,
		BundledXmlVerificationRequest request,
		Exception exception )
	{
		try
		{
			Directory.CreateDirectory( Path.GetDirectoryName( reportPath )! );
			File.WriteAllText(
				reportPath,
				$"Bundled XML Existing-Merge Verification\nOutcome: FAILED\nResource: {request.ResourcePath}\nCandidate: {request.CandidateModName}\n\n{exception}" );
		}
		catch
		{
		}
	}

	private static void EnsureFreeSpace( string directory )
	{
		string? root = Path.GetPathRoot( directory );

		if ( string.IsNullOrWhiteSpace( root ) )
		{
			return;
		}

		try
		{
			DriveInfo drive = new( root );

			if ( drive.IsReady && drive.AvailableFreeSpace < MinimumFreeBytes )
			{
				throw new IOException( UiText.T( "BundledXmlMergeVerificationService", "At least 64 MB of free space is required for selective bundled XML verification." ) );
			}
		}
		catch ( ArgumentException )
		{
		}
		catch ( UnauthorizedAccessException )
		{
		}
	}

	private static void TryDeleteDirectory( string directory )
	{
		try
		{
			if ( Directory.Exists( directory ) )
			{
				Directory.Delete( directory, recursive: true );
			}
		}
		catch
		{
		}
	}
}
