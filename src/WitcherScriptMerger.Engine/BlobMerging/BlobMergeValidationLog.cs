using System;
using System.IO;
using System.Text;

namespace WitcherScriptMerger.BlobMerging;

internal static class BlobMergeValidationLog
{
	private static readonly object gate = new();
	internal const long MaximumBytes = 1024 * 1024;
	internal static string DefaultPath => Path.Combine( AppContext.BaseDirectory, "ScriptMergerRemastered.log" );

	internal static string Record( BlobMergeValidationException error, string path = null )
	{
		path ??= DefaultPath;

		try
		{
			lock ( gate )
			{
				if ( File.Exists( path ) && new FileInfo( path ).Length >= MaximumBytes )
					File.Move( path, path + ".previous", overwrite: true );

				string details = error.Details;

				if ( details.Length > MaximumBytes / 2 )
					details = details[..(int)( MaximumBytes / 2 )] + Environment.NewLine + "[Further diagnostics omitted from the log.]";

				File.AppendAllText(
					path,
					$"[{DateTimeOffset.Now:O}] Blob merge validation failed{Environment.NewLine}{details}{Environment.NewLine}{Environment.NewLine}",
					new UTF8Encoding( false ) );
			}

			return UiText.Format( "BlobMerge", "Details were saved to {0}.", path );
		}
		catch ( Exception failure ) when ( failure is IOException or UnauthorizedAccessException )
		{
			return UiText.Format( "BlobMerge", "The log could not be written: {0}", failure.Message );
		}
	}
}
