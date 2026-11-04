using System;
using System.Collections.Generic;
using System.Linq;
using WitcherScriptMerger.FileIndex;

namespace WitcherScriptMerger;

internal static class ConflictSearch
{
	public static List<ModFile> FindModMatches( IEnumerable<ModFile> conflicts, string query )
	{
		if ( conflicts == null || string.IsNullOrWhiteSpace( query ) )
		{
			return new List<ModFile>();
		}

		string trimmedQuery = query.Trim();

		return conflicts
			.Where( conflict => conflict.GetConflictDisplaySources().Any( mod => IsModNameMatch( mod.Name, trimmedQuery ) ) )
			.ToList();
	}

	public static List<ModFile> FindFileMatches( IEnumerable<ModFile> conflicts, string query )
	{
		if ( conflicts == null || string.IsNullOrWhiteSpace( query ) )
		{
			return new List<ModFile>();
		}

		string trimmedQuery = query.Trim();

		return conflicts
			.Where( conflict => IsFileMatch( conflict.RelativePath, trimmedQuery ) )
			.ToList();
	}

	public static bool IsModNameMatch( string modName, string query )
	{
		return !string.IsNullOrEmpty( modName )
			&& !string.IsNullOrWhiteSpace( query )
			&& modName.IndexOf( query.Trim(), StringComparison.InvariantCultureIgnoreCase ) >= 0;
	}

	public static bool IsFileMatch( string relativePath, string query )
	{
		if ( string.IsNullOrEmpty( relativePath ) || string.IsNullOrWhiteSpace( query ) )
		{
			return false;
		}

		string normalizedPath = NormalizePath( relativePath );
		string normalizedQuery = NormalizePath( query.Trim() );

		if ( !ContainsWildcard( normalizedQuery ) )
		{
			return normalizedPath.IndexOf( normalizedQuery, StringComparison.InvariantCultureIgnoreCase ) >= 0;
		}

		return MatchesWildcard( normalizedPath, normalizedQuery );
	}

	internal static bool MatchesWildcard( string text, string pattern )
	{
		int input = 0, token = 0, star = -1, retry = 0;

		while ( input < text.Length )
		{
			if ( token < pattern.Length && ( pattern[token] == '?'
				|| char.ToUpperInvariant( pattern[token] ) == char.ToUpperInvariant( text[input] ) ) )
			{
				input++;
				token++;
			}
			else if ( token < pattern.Length && pattern[token] == '*' )
			{
				star = token++;
				retry = input;
			}
			else if ( star >= 0 )
			{
				token = star + 1;
				input = ++retry;
			}
			else
				return false;
		}

		while ( token < pattern.Length && pattern[token] == '*' )
			token++;

		return token == pattern.Length;
	}

	private static bool ContainsWildcard( string query )
	{
		return query.IndexOf( '*' ) >= 0 || query.IndexOf( '?' ) >= 0;
	}

	private static string NormalizePath( string path )
	{
		return path.Replace( '/', '\\' );
	}
}
