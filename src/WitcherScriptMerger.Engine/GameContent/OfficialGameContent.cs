#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace WitcherScriptMerger.GameContent;

// Custom DLC must stay separate from the vanilla baseline.
internal static class OfficialGameContent
{
	// Patch folders come last when collecting official bundles.
	public static IReadOnlyList<IReadOnlyList<string>> DiscoverBundleGroups(
		string gameDirectory )
	{
		List<string> directories = new();
		string content = Path.Combine( gameDirectory, "content" );

		if ( Directory.Exists( content ) )
			directories.AddRange( Directory.EnumerateDirectories( content )
				.Where( path => IsOfficialContentDirectoryName( Path.GetFileName( path ) ) )
				.Select( path => Path.Combine( path, "bundles" ) ).Where( Directory.Exists ) );

		string dlc = Path.Combine( gameDirectory, "DLC" );

		if ( Directory.Exists( dlc ) )
			directories.AddRange( Directory.EnumerateDirectories( dlc )
				.Where( path => IsOfficialDlcDirectoryName( Path.GetFileName( path ) ) )
				.Select( path => Path.Combine( path, "content", "bundles" ) ).Where( Directory.Exists ) );

		var ordered = directories.Distinct( StringComparer.OrdinalIgnoreCase )
			.OrderBy( path => path.Contains( "patch", StringComparison.OrdinalIgnoreCase ) )
			.ThenBy( path => Path.GetRelativePath( gameDirectory, path ), NaturalPathComparer.Instance );

		return ordered.Select( directory => (IReadOnlyList<string>)Directory
			.EnumerateFiles( directory, "*.bundle" ).Select( Path.GetFullPath )
			.OrderBy( path => path, StringComparer.OrdinalIgnoreCase ).ToArray() )
			.Where( group => group.Count > 0 ).ToArray();
	}

	public static string? FindDlcLooseFile( string gameDirectory, string relativePath )
	{
		string dlc = Path.Combine( gameDirectory, "DLC" );
		IEnumerable<string> roots = Directory.Exists( dlc )
			? Directory.EnumerateDirectories( dlc ).Where( path => IsOfficialDlcDirectoryName( Path.GetFileName( path ) ) )
				.OrderByDescending( path => path, NaturalPathComparer.Instance )
			: Array.Empty<string>();

		return roots.Select( root => Path.Combine( root, relativePath ) ).FirstOrDefault( File.Exists );
	}

	private static readonly Regex DlcDirectoryPattern = new(
		@"^(?:DLC\d*|ep\d|bob)$",
		RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled );

	private static readonly Regex ContentDirectoryPattern = new(
		@"^(?:content|patch)\d+$",
		RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled );

	public static bool IsOfficialDlcDirectoryName( string? directoryName )
	{
		return !string.IsNullOrWhiteSpace( directoryName )
			&& DlcDirectoryPattern.IsMatch( directoryName );
	}

	public static bool IsOfficialContentDirectoryName( string? directoryName )
	{
		return !string.IsNullOrWhiteSpace( directoryName )
			&& ContentDirectoryPattern.IsMatch( directoryName );
	}
}

// Compare numbered paths in numeric order, such as content2 before content10.
internal sealed class NaturalPathComparer : IComparer<string>
{
	public static NaturalPathComparer Instance { get; } = new();

	private NaturalPathComparer()
	{
	}

	public int Compare( string? left, string? right )
	{
		if ( ReferenceEquals( left, right ) )
		{
			return 0;
		}

		if ( left is null )
		{
			return -1;
		}

		if ( right is null )
		{
			return 1;
		}

		int leftIndex = 0;
		int rightIndex = 0;

		while ( leftIndex < left.Length && rightIndex < right.Length )
		{
			char leftCharacter = NormalizeSeparator( left[leftIndex] );
			char rightCharacter = NormalizeSeparator( right[rightIndex] );

			if ( IsAsciiDigit( leftCharacter ) && IsAsciiDigit( rightCharacter ) )
			{
				int numericComparison = CompareNumberRuns( left, ref leftIndex, right, ref rightIndex );

				if ( numericComparison != 0 )
				{
					return numericComparison;
				}

				continue;
			}

			int characterComparison = char.ToUpperInvariant( leftCharacter )
				.CompareTo( char.ToUpperInvariant( rightCharacter ) );

			if ( characterComparison != 0 )
			{
				return characterComparison;
			}

			leftIndex++;
			rightIndex++;
		}

		int lengthComparison = ( left.Length - leftIndex ).CompareTo( right.Length - rightIndex );

		if ( lengthComparison != 0 )
		{
			return lengthComparison;
		}

		int caseInsensitiveComparison = StringComparer.OrdinalIgnoreCase.Compare( left, right );

		return caseInsensitiveComparison != 0
			? caseInsensitiveComparison
			: StringComparer.Ordinal.Compare( left, right );
	}

	// Compare digit runs without converting them to an integer, which could overflow.
	private static int CompareNumberRuns(
		string left,
		ref int leftIndex,
		string right,
		ref int rightIndex )
	{
		int leftRunStart = leftIndex;
		int rightRunStart = rightIndex;

		while ( leftIndex < left.Length && left[leftIndex] == '0' )
		{
			leftIndex++;
		}

		while ( rightIndex < right.Length && right[rightIndex] == '0' )
		{
			rightIndex++;
		}

		int leftSignificantStart = leftIndex;
		int rightSignificantStart = rightIndex;

		while ( leftIndex < left.Length && IsAsciiDigit( left[leftIndex] ) )
		{
			leftIndex++;
		}

		while ( rightIndex < right.Length && IsAsciiDigit( right[rightIndex] ) )
		{
			rightIndex++;
		}

		int leftSignificantLength = leftIndex - leftSignificantStart;
		int rightSignificantLength = rightIndex - rightSignificantStart;
		int significantLengthComparison = leftSignificantLength.CompareTo( rightSignificantLength );

		if ( significantLengthComparison != 0 )
		{
			return significantLengthComparison;
		}

		for ( int offset = 0; offset < leftSignificantLength; offset++ )
		{
			int digitComparison = left[leftSignificantStart + offset]
				.CompareTo( right[rightSignificantStart + offset] );

			if ( digitComparison != 0 )
			{
				return digitComparison;
			}
		}

		// Equal numeric values sort the shorter spelling first (2 before 02).
		return ( leftIndex - leftRunStart ).CompareTo( rightIndex - rightRunStart );
	}

	private static bool IsAsciiDigit( char value )
	{
		return value >= '0' && value <= '9';
	}

	private static char NormalizeSeparator( char value )
	{
		return value == '/' ? '\\' : value;
	}
}
