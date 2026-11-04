using System;
using System.IO;

namespace WitcherScriptMerger;

internal static class GameSettingsLocation
{
	internal static string DirectoryPath( IEngineSettings settings )
	{
		return Resolve(
			settings.Get( "GameSettingsDirectory" ),
			Environment.GetFolderPath( Environment.SpecialFolder.Personal ) );
	}

	internal static string Resolve( string configuredPath, string documentsPath )
	{
		if ( !string.IsNullOrWhiteSpace( configuredPath ) )
			return Path.GetFullPath( configuredPath.Trim() );

		return string.IsNullOrWhiteSpace( documentsPath ) ? string.Empty
			: Path.Combine( documentsPath, "The Witcher 3" );
	}

	internal static string FilePath( IEngineSettings settings, string name )
	{
		string directory = DirectoryPath( settings );

		return string.IsNullOrWhiteSpace( directory ) ? string.Empty : Path.Combine( directory, name );
	}
}
