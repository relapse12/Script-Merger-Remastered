using System.IO;

namespace WitcherScriptMerger.Tools;

// Used to detect changed merge sources.
internal static class Hasher
{
	public static string ComputeHash( string filePath )
	{
		if ( File.Exists( filePath ) )
		{
			return new xxHash( filePath ).ToString();
		}

		throw new FileNotFoundException( UiText.Format( "Hasher", "Can't find file to hash:  {0}", filePath ) );
	}
}
