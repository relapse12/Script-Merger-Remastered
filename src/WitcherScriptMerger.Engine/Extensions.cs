using System;
using System.IO;
using System.Text.RegularExpressions;

namespace WitcherScriptMerger;

internal static class Extensions
{
	public static bool EqualsIgnoreCase( this string s, string otherString )
	{
		return s.Equals( otherString, StringComparison.InvariantCultureIgnoreCase );
	}

	public static int IndexOfIgnoreCase( this string s, string value, int startIndex = 0 )
	{
		return s.IndexOf( value, startIndex, StringComparison.InvariantCultureIgnoreCase );
	}

	public static bool StartsWithIgnoreCase( this string s, string value )
	{
		return s.StartsWith( value, StringComparison.InvariantCultureIgnoreCase );
	}

	public static bool EndsWithIgnoreCase( this string s, string value )
	{
		return s.EndsWith( value, StringComparison.InvariantCultureIgnoreCase );
	}

	public static bool IsAlphaNumeric( this string s )
	{
		return new Regex( "^[_a-zA-Z0-9]*$" ).IsMatch( s );
	}

	public static string ResolveTargetFileFullName( this FileInfo fileInfo )
	{
		if ( !fileInfo.Exists )
		{
			return fileInfo.FullName;
		}

		return SimLink.GetSymbolicLinkTarget( fileInfo );
	}
}
