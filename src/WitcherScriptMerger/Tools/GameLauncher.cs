using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace WitcherScriptMerger.Tools;

// Steam installs must launch through Steam. Other installs run the game executable directly.
internal static class GameLauncher
{
	internal const string SteamLaunchUri = "steam://rungameid/292030";

	internal static ProcessStartInfo BuildStartInfo( string gameExecutable )
	{
		string executable = Path.GetFullPath( gameExecutable ?? string.Empty );

		if ( !File.Exists( executable ) )
		{
			throw new FileNotFoundException( UiText.T( "GameLauncher", "The Remastered game executable was not found." ), executable );
		}

		if ( GetConfiguredPlatform( executable ).Equals( "steam", StringComparison.OrdinalIgnoreCase ) )
		{
			return new ProcessStartInfo
			{
				FileName = SteamLaunchUri,
				UseShellExecute = true
			};
		}

		return new ProcessStartInfo
		{
			FileName = executable,
			WorkingDirectory = Path.GetDirectoryName( executable )!,
			UseShellExecute = false
		};
	}

	internal static void Launch( string gameExecutable )
	{
		Process.Start( BuildStartInfo( gameExecutable ) );
	}

	// Reads the platform from the game launcher's configuration. Unknown or unreadable means a direct launch.
	private static string GetConfiguredPlatform( string gameExecutable )
	{
		string configurationPath = FindLauncherConfiguration( gameExecutable );

		if ( configurationPath == null )
		{
			return string.Empty;
		}

		try
		{
			using JsonDocument configuration = JsonDocument.Parse( File.ReadAllText( configurationPath ) );

			return configuration.RootElement.TryGetProperty( "platform", out JsonElement platform )
				? platform.GetString()?.Trim() ?? string.Empty
				: string.Empty;
		}
		catch ( IOException )
		{
			return string.Empty;
		}
		catch ( UnauthorizedAccessException )
		{
			return string.Empty;
		}
		catch ( JsonException )
		{
			return string.Empty;
		}
	}

	private static string FindLauncherConfiguration( string gameExecutable )
	{
		DirectoryInfo directory = new FileInfo( gameExecutable ).Directory;

		while ( directory != null )
		{
			string candidate = Path.Combine( directory.FullName, "launcher-configuration.json" );

			if ( File.Exists( candidate ) )
			{
				return candidate;
			}

			directory = directory.Parent;
		}

		return null;
	}
}
