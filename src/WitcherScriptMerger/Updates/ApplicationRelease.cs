using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;

namespace WitcherScriptMerger.Updates;

internal sealed record ApplicationFile( string Name, string Version, string Category, long UploadedAt, string[] Changelog );
internal sealed record ReleaseChangelog( string Version, string[] Notes );
// Notes include the releases since the installed version.
internal sealed record ApplicationRelease( string Version, ReleaseChangelog[] Changelog );

internal static class ApplicationReleaseSelector
{
	internal const string DownloadsUrl = "https://www.nexusmods.com/witcher3/mods/13076?tab=files";
	internal const string ChangelogUrl = "https://www.nexusmods.com/witcher3/mods/13076?tab=description";
	private static readonly Regex ExcludedTitle = new(
		@"\b(source|sources|language|languages|translation|translations|beta|alpha|preview|candidate|test|testing|experimental|qt|linux|mac|macos|rc)\b",
		RegexOptions.IgnoreCase | RegexOptions.CultureInvariant );

	internal static bool TryVersion( string text, out Version version )
	{
		version = null;

		string value = ( text ?? "" ).Trim().TrimStart( 'v', 'V' );

		if ( value.Length > 32 || !Regex.IsMatch( value, @"^\d+(\.\d+){0,3}$", RegexOptions.CultureInvariant ) )
			return false;

		string[] parts = value.Split( '.' );
		int[] numbers = new int[4];

		for ( int i = 0; i < parts.Length; i++ )
			if ( !int.TryParse( parts[i], out numbers[i] ) )
				return false;

		version = new Version( numbers[0], numbers[1], numbers[2], numbers[3] );

		return true;
	}

	// Only application releases should appear as updates. Exclude source, language and test packages.
	internal static bool IsApplicationFile( ApplicationFile file )
	{
		string title = Regex.Replace( file.Name ?? "", @"[-_]", " " );

		return title.Contains( "Script Merger", StringComparison.OrdinalIgnoreCase )
			&& title.Contains( "Remastered", StringComparison.OrdinalIgnoreCase )
			&& !title.Contains( "Next Gen", StringComparison.OrdinalIgnoreCase )
			&& !ExcludedTitle.IsMatch( title )
			&& TryVersion( file.Version, out _ );
	}

	internal static bool IsNewer( ApplicationRelease release, string installedVersion )
		=> TryVersion( release?.Version, out var latest ) && TryVersion( installedVersion, out var installed ) && latest > installed;

	internal static ApplicationRelease Select( IEnumerable<ApplicationFile> files, string installedVersion )
	{
		if ( !TryVersion( installedVersion, out var installed ) )
			throw new ApplicationUpdateException( UiText.T( "ApplicationUpdates", "The installed application version could not be determined." ) );

		var candidates = files.Where( IsApplicationFile ).ToArray();
		var latest = candidates.Where( f => string.Equals( f.Category, "MAIN", StringComparison.OrdinalIgnoreCase ) )
			.OrderByDescending( f => { TryVersion( f.Version, out var v ); return v; } )
			.ThenByDescending( f => f.UploadedAt ).FirstOrDefault();

		if ( latest == null )
			throw new ApplicationUpdateException( UiText.T( "ApplicationUpdates", "Nexus did not return a stable Script Merger Remastered release." ) );

		TryVersion( latest.Version, out var newest );

		var history = candidates.Where( f => f.Category?.ToUpperInvariant() is "MAIN" or "OLD_VERSION" or "ARCHIVED" )
			.Where( f => { TryVersion( f.Version, out var v ); return v <= newest && ( v > installed || v == newest ); } )
			.GroupBy( f => { TryVersion( f.Version, out var v ); return v; } )
			.OrderByDescending( g => g.Key ).Take( 10 )
			.Select( g => g.OrderByDescending( f => f.UploadedAt ).FirstOrDefault( f => CleanNotes( f.Changelog ).Length != 0 )
				?? g.OrderByDescending( f => f.UploadedAt ).First() )
			.Select( f => new ReleaseChangelog( f.Version, CleanNotes( f.Changelog ) ) ).ToArray();

		return new ApplicationRelease( latest.Version, history );
	}

	private static string[] CleanNotes( string[] notes )
	{
		var cleaned = new List<string>();
		int remaining = 20000;

		foreach ( string note in ( notes ?? Array.Empty<string>() ).Take( 100 ) )
		{
			if ( remaining <= 0 )
				break;

			string text = WebUtility.HtmlDecode( note ?? "" ).Trim();

			if ( text.Length == 0 )
				continue;

			int limit = Math.Min( 4000, remaining );

			cleaned.Add( text.Length > limit ? text[..limit] + "…" : text );
			remaining -= cleaned[^1].Length;
		}

		return cleaned.ToArray();
	}
}

internal sealed class ApplicationUpdateException : Exception
{
	internal ApplicationUpdateException( string message ) : base( message ) { }
}
