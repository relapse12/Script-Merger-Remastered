#nullable enable
using System;
using System.IO;

namespace WitcherScriptMerger.Inventory;

// File.Exists also returns false on access failures. Confirm absence before removing an inventory record.
internal static class PathAbsence
{
	internal static bool IsConfirmed( string path )
	{
		try
		{
			_ = File.GetAttributes( path );

			return false;
		}
		catch ( Exception error ) when ( error is FileNotFoundException or DirectoryNotFoundException )
		{
			return true;
		}
	}
}
