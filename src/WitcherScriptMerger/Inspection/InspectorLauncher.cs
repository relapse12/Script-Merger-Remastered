#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace WitcherScriptMerger.Inspection;

// Open the Inspector with the selected sources, reusing its window when possible.
// SMR removes only the temporary launch files it creates.
internal static class InspectorLauncher
{
	internal static string Executable( string applicationDirectory )
	{
		return Path.Combine( applicationDirectory, "Tools", "WSAnnoInspector", "WSAnnoInspector.exe" );
	}

	internal static void Launch( string applicationDirectory, InspectorLaunchContext context, Action<ProcessStartInfo>? start = null )
	{
		string executable = Executable( applicationDirectory );

		if ( !File.Exists( executable ) )
			throw new FileNotFoundException( UiText.T(
				"InspectorLaunch", "Annotation Inspector is missing. Copy Tools/WSAnnoInspector from the matching runtime package." ), executable );

		string folder = Path.Combine( context.Folders.Data, "Launch" );
		Directory.CreateDirectory( folder );
		PruneAbandonedContexts( folder );
		string file = Path.Combine( folder, Guid.NewGuid().ToString( "N" ) + ".json" );
		File.WriteAllText( file, context.Serialize(), new UTF8Encoding( false ) );

		var launch = new ProcessStartInfo( executable )
		{
			UseShellExecute = false,
			WorkingDirectory = Path.GetDirectoryName( executable )!
		};
		launch.ArgumentList.Add( "--context" );
		launch.ArgumentList.Add( file );

		try
		{
			if ( start is null )
			{
				using var process = Process.Start( launch );
			}
			else
			{
				start( launch );
			}
		}
		catch
		{
			File.Delete( file );
			throw;
		}
	}

	private static void PruneAbandonedContexts( string folder )
	{
		foreach ( string file in Directory.EnumerateFiles( folder, "*.json" ) )
		{
			if ( !Guid.TryParseExact( Path.GetFileNameWithoutExtension( file ), "N", out _ ) )
				continue;

			try
			{
				if ( File.GetLastWriteTimeUtc( file ) < DateTime.UtcNow.AddDays( -7 ) )
					File.Delete( file );
			}
			catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException )
			{
				// An abandoned file cannot prevent a new Inspect request.
			}
		}
	}
}
