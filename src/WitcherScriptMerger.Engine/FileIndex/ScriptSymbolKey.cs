#nullable enable

using System;
using System.IO;
using System.Linq;

namespace WitcherScriptMerger.FileIndex;

internal static class ScriptSymbolKey
{
	public static string Function( string scope, string name )
	{
		return Create( "Functions", ScopeOrGlobal( scope ), name );
	}

	public static string Create( params string[] components )
	{
		if ( components.Any( string.IsNullOrWhiteSpace ) )
		{
			throw new InvalidDataException( UiText.T( "ScriptSymbolKey", "A script definition contains an unnamed component." ) );
		}

		return string.Join( "\\", components.Select( EscapeComponent ) );
	}

	private static string ScopeOrGlobal( string scope )
	{
		return string.IsNullOrWhiteSpace( scope ) ? "Global" : scope;
	}

	// Escape separators inside names so they cannot create extra levels in the conflict key.
	private static string EscapeComponent( string value )
	{
		return value.Replace( "%", "%25", StringComparison.Ordinal )
			.Replace( "\\", "%5C", StringComparison.Ordinal )
			.Replace( "/", "%2F", StringComparison.Ordinal );
	}
}
