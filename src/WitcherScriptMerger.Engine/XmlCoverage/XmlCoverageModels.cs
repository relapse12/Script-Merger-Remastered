#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace WitcherScriptMerger.XmlCoverage;

// ReviewRequired means the comparison could not establish whether the source's changes are included.
public enum XmlCoverageStatus
{
	Covered,
	ReviewRequired,
	NotCovered
}

public sealed class XmlCoverageFinding
{
	public string SourcePath { get; }

	public string Location { get; }

	public XmlCoverageStatus Status { get; }

	public string Message { get; }

	internal XmlCoverageFinding(
		string sourcePath,
		string location,
		XmlCoverageStatus status,
		string message )
	{
		SourcePath = sourcePath ?? string.Empty;
		Location = string.IsNullOrWhiteSpace( location ) ? "/" : location;
		Status = status;
		Message = message ?? string.Empty;
	}
}

public sealed class XmlCoverageSourceResult
{
	public string SourcePath { get; }

	public XmlCoverageStatus Status { get; }

	public IReadOnlyList<XmlCoverageFinding> Findings { get; }

	internal XmlCoverageSourceResult( string sourcePath, IEnumerable<XmlCoverageFinding> findings )
	{
		SourcePath = sourcePath;
		Findings = Array.AsReadOnly( ( findings ?? throw new ArgumentNullException( nameof( findings ) ) ).ToArray() );
		Status = XmlCoverageResult.CombineStatus( Findings.Select( item => item.Status ) );
	}
}

public sealed class XmlCoverageResult
{
	public string VanillaPath { get; }

	public string CandidateWinnerPath { get; }

	public XmlCoverageStatus Status { get; }

	public bool IsCovered => Status == XmlCoverageStatus.Covered;

	public IReadOnlyList<XmlCoverageSourceResult> SourceResults { get; }

	public IReadOnlyList<XmlCoverageFinding> Findings { get; }

	internal XmlCoverageResult(
		string vanillaPath,
		string candidateWinnerPath,
		IEnumerable<XmlCoverageSourceResult> sourceResults )
	{
		VanillaPath = vanillaPath;
		CandidateWinnerPath = candidateWinnerPath;
		SourceResults = Array.AsReadOnly( ( sourceResults ?? throw new ArgumentNullException( nameof( sourceResults ) ) ).ToArray() );
		Findings = Array.AsReadOnly( SourceResults.SelectMany( item => item.Findings ).ToArray() );
		Status = CombineStatus( SourceResults.Select( item => item.Status ) );
	}

	internal static XmlCoverageStatus CombineStatus( IEnumerable<XmlCoverageStatus> statuses )
	{
		XmlCoverageStatus result = XmlCoverageStatus.Covered;

		foreach ( XmlCoverageStatus status in statuses )
		{
			if ( status == XmlCoverageStatus.NotCovered )
			{
				return XmlCoverageStatus.NotCovered;
			}

			if ( status == XmlCoverageStatus.ReviewRequired )
			{
				result = XmlCoverageStatus.ReviewRequired;
			}
		}

		return result;
	}
}
