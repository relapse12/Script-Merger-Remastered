#nullable enable

using System;
using System.Collections.Generic;
using System.IO;

namespace WitcherScriptMerger.Tools;

// The merge coordinator owns these directories and decides when to clean them up.
internal sealed class ToolJobContext
{
	private readonly string jobPrefix;

	private ToolJobContext( string jobDirectory, IProgress<ProcessOutputLine>? progress )
	{
		string fullJobDirectory = Path.GetFullPath( jobDirectory );
		string root = Path.GetPathRoot( fullJobDirectory ) ?? string.Empty;

		JobDirectory = fullJobDirectory.Equals( root, StringComparison.OrdinalIgnoreCase )
			? fullJobDirectory
			: fullJobDirectory.TrimEnd( Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar );

		if ( string.IsNullOrWhiteSpace( JobDirectory ) )
		{
			throw new ArgumentException( UiText.T( "ToolJobContext", "A job directory is required." ), nameof( jobDirectory ) );
		}

		jobPrefix = JobDirectory.EndsWith( Path.DirectorySeparatorChar )
			|| JobDirectory.EndsWith( Path.AltDirectorySeparatorChar )
			? JobDirectory
			: JobDirectory + Path.DirectorySeparatorChar;
		TempDirectory = Path.Combine( JobDirectory, "temp" );
		HomeDirectory = Path.Combine( JobDirectory, "home" );
		RoamingAppDataDirectory = Path.Combine( HomeDirectory, "AppData", "Roaming" );
		LocalAppDataDirectory = Path.Combine( HomeDirectory, "AppData", "Local" );
		JavaPreferencesDirectory = Path.Combine( HomeDirectory, "java-prefs" );
		Progress = progress;
	}

	public string JobDirectory { get; }

	public string TempDirectory { get; }

	public string HomeDirectory { get; }

	public string RoamingAppDataDirectory { get; }

	public string LocalAppDataDirectory { get; }

	public string JavaPreferencesDirectory { get; }

	public IProgress<ProcessOutputLine>? Progress { get; }

	public static ToolJobContext Create(
		string jobDirectory,
		IProgress<ProcessOutputLine>? progress = null )
	{
		ToolJobContext context = new( jobDirectory, progress );

		Directory.CreateDirectory( context.JobDirectory );
		Directory.CreateDirectory( context.TempDirectory );
		Directory.CreateDirectory( context.HomeDirectory );
		Directory.CreateDirectory( context.RoamingAppDataDirectory );
		Directory.CreateDirectory( context.LocalAppDataDirectory );
		Directory.CreateDirectory( context.JavaPreferencesDirectory );

		return context;
	}

	// Keep tool settings and temporary files inside this job, including Java settings when requested.
	public IReadOnlyDictionary<string, string?> BuildEnvironment( bool isolateJava )
	{
		Dictionary<string, string?> environment = new( StringComparer.OrdinalIgnoreCase )
		{
			["TEMP"] = TempDirectory,
			["TMP"] = TempDirectory,
			["HOME"] = HomeDirectory,
			["USERPROFILE"] = HomeDirectory,
			["APPDATA"] = RoamingAppDataDirectory,
			["LOCALAPPDATA"] = LocalAppDataDirectory
		};

		if ( isolateJava )
		{
			string homeOption = QuoteJavaProperty( "user.home", HomeDirectory );
			string preferencesOption = QuoteJavaProperty( "java.util.prefs.userRoot", JavaPreferencesDirectory );

			environment["JAVA_TOOL_OPTIONS"] = homeOption + " " + preferencesOption;
			environment["_JAVA_OPTIONS"] = null;
			environment["JDK_JAVA_OPTIONS"] = null;
		}

		return environment;
	}

	public string CreateDirectory( string relativeName )
	{
		if ( string.IsNullOrWhiteSpace( relativeName ) || Path.IsPathRooted( relativeName ) )
		{
			throw new ArgumentException( UiText.T( "ToolJobContext", "A relative job path is required." ), nameof( relativeName ) );
		}

		string fullPath = EnsurePathInsideJob( Path.Combine( JobDirectory, relativeName ) );

		Directory.CreateDirectory( fullPath );

		return fullPath;
	}

	public string EnsurePathInsideJob( string path )
	{
		if ( string.IsNullOrWhiteSpace( path ) )
		{
			throw new ArgumentException( UiText.T( "ToolJobContext", "A path is required." ), nameof( path ) );
		}

		string fullPath = Path.GetFullPath( path );

		if ( !fullPath.Equals( JobDirectory, StringComparison.OrdinalIgnoreCase )
			&& !fullPath.StartsWith( jobPrefix, StringComparison.OrdinalIgnoreCase ) )
		{
			throw new InvalidOperationException( UiText.F( "ToolJobContext", $"Tool output must stay inside the job directory: {JobDirectory}" ) );
		}

		return fullPath;
	}

	public string PrepareOutputFile( string outputPath )
	{
		string fullPath = EnsurePathInsideJob( outputPath );

		if ( fullPath.Equals( JobDirectory, StringComparison.OrdinalIgnoreCase ) )
		{
			throw new InvalidOperationException( UiText.T( "ToolJobContext", "The job directory cannot be used as an output file." ) );
		}

		string? parent = Path.GetDirectoryName( fullPath );

		if ( parent is not null )
		{
			Directory.CreateDirectory( parent );
		}

		if ( File.Exists( fullPath ) )
		{
			File.Delete( fullPath );
		}

		return fullPath;
	}

	public string PrepareEmptyDirectory( string outputDirectory )
	{
		string fullPath = EnsurePathInsideJob( outputDirectory );

		if ( fullPath.Equals( JobDirectory, StringComparison.OrdinalIgnoreCase ) )
		{
			throw new InvalidOperationException( UiText.T( "ToolJobContext", "The entire job directory cannot be cleared as a tool output." ) );
		}

		if ( Directory.Exists( fullPath ) )
		{
			Directory.Delete( fullPath, recursive: true );
		}

		Directory.CreateDirectory( fullPath );

		return fullPath;
	}

	private static string QuoteJavaProperty( string name, string value )
	{
		if ( value.Contains( '"' ) )
		{
			throw new ArgumentException(
				UiText.T( "ToolJobContext", "Java isolation paths cannot contain quote characters." ),
				nameof( value ) );
		}

		return $"-D{name}=\"{value}\"";
	}
}
