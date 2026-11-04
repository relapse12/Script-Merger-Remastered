using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace WitcherScriptMerger.Updates;

internal sealed class ApplicationUpdateState
{
	public DateTimeOffset? LastAttemptUtc { get; set; }
	public DateTimeOffset? LastSuccessUtc { get; set; }
	public string NotifiedVersion { get; set; }
	public ApplicationRelease Release { get; set; }

	internal bool IsDue( DateTimeOffset now ) => LastAttemptUtc == null
		|| now < LastAttemptUtc.Value || now - LastAttemptUtc.Value >= TimeSpan.FromDays( 7 );
}

internal sealed class ApplicationUpdateStore
{
	internal const string FileName = "ApplicationUpdates.json";
	internal string FilePath { get; }

	internal ApplicationUpdateStore( string applicationDirectory )
		=> FilePath = Path.Combine( Path.GetFullPath( applicationDirectory ), "Data", FileName );

	internal ApplicationUpdateState Load()
	{
		try
		{
			if ( !File.Exists( FilePath ) || new FileInfo( FilePath ).Length > 2 * 1024 * 1024 )
				return new();

			var state = JsonSerializer.Deserialize<ApplicationUpdateState>( File.ReadAllText( FilePath ) );

			if ( state == null || state.Release != null && ( !ApplicationReleaseSelector.TryVersion( state.Release.Version, out _ )
				|| state.Release.Changelog == null || state.Release.Changelog.Length > 10
				|| state.Release.Changelog.Any( section => section == null || !ApplicationReleaseSelector.TryVersion( section.Version, out _ )
					|| section.Notes == null || section.Notes.Length > 100 || section.Notes.Any( note => note == null || note.Length > 4001 ) ) ) )
				return new();

			return state;
		}
		catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException )
		{
			return new();
		}
	}

	// Keep the update result usable even when its cache cannot be saved.
	internal string Save( ApplicationUpdateState state )
	{
		string temporary = FilePath + "." + Guid.NewGuid().ToString( "N" ) + ".tmp";

		try
		{
			Directory.CreateDirectory( Path.GetDirectoryName( FilePath ) );

			using ( var stream = new FileStream( temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None ) )
			{
				JsonSerializer.Serialize( stream, state );
				stream.Flush( true );
			}

			File.Move( temporary, FilePath, true );

			return null;
		}
		catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException or NotSupportedException )
		{
			return UiText.T(
				"ApplicationUpdates",
				"Update-check history could not be saved beside the application. Check that its folder is writable." );
		}
		finally
		{
			try
			{
				if ( File.Exists( temporary ) )
					File.Delete( temporary );
			}
			catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException ) { }
		}
	}
}
