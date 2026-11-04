using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WitcherScriptMerger.Updates;

internal interface IApplicationReleaseClient
{
	Task<ApplicationRelease> GetReleaseAsync( string installedVersion, CancellationToken cancellationToken );
}

// Reads the mod's file list from the Nexus Mods GraphQL API. Requests carry only the application's name and version.
internal sealed class NexusApplicationClient : IApplicationReleaseClient, IDisposable
{
	private const string Endpoint = "https://api.nexusmods.com/v2/graphql";
	private const int MaximumResponseBytes = 2 * 1024 * 1024;
	private readonly HttpClient client;
	private string gameId;

	internal NexusApplicationClient( string applicationVersion, HttpMessageHandler handler = null )
	{
		client = handler == null ? new HttpClient( new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false } ) : new HttpClient( handler );
		client.Timeout = Timeout.InfiniteTimeSpan;
		client.DefaultRequestHeaders.Add( "Accept", "application/json" );
		client.DefaultRequestHeaders.Add( "Application-Name", "Script-Merger-Remastered" );
		client.DefaultRequestHeaders.Add( "Application-Version", applicationVersion );
		client.DefaultRequestHeaders.UserAgent.ParseAdd( "Script-Merger-Remastered/" + applicationVersion );
	}

	public async Task<ApplicationRelease> GetReleaseAsync( string installedVersion, CancellationToken cancellationToken )
	{
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource( cancellationToken );

		timeout.CancelAfter( TimeSpan.FromSeconds( 15 ) );

		try
		{
			if ( gameId == null )
			{
				using var game = await QueryAsync( "query GameId($domainName: String!) { game(domainName: $domainName) { id } }", new
				{
					domainName = "witcher3"
				}, timeout.Token ).ConfigureAwait( false );

				ThrowGraphQlErrors( game.RootElement );
				gameId = game.RootElement.GetProperty( "data" ).GetProperty( "game" ).GetProperty( "id" ).ToString();

				if ( !long.TryParse( gameId, out long numericId ) || numericId <= 0 )
					throw new ApplicationUpdateException( UiText.T( "ApplicationUpdates", "Nexus did not return the Witcher 3 game identifier." ) );
			}

			using var files = await QueryFilesAsync( timeout.Token ).ConfigureAwait( false );
			var rows = files.RootElement.GetProperty( "data" ).GetProperty( "modFiles" );

			if ( rows.ValueKind == JsonValueKind.Object )
				foreach ( string wrapper in new[] { "nodes", "files", "results", "items" } )
					if ( rows.TryGetProperty( wrapper, out var nested ) )
					{
						rows = nested;

						break;
					}

			if ( rows.ValueKind != JsonValueKind.Array )
				throw new ApplicationUpdateException( UiText.T( "ApplicationUpdates", "Nexus returned an unreadable application file list." ) );

			var releases = new List<ApplicationFile>();

			foreach ( var row in rows.EnumerateArray() )
			{
				if ( releases.Count >= 10000 )
					throw new ApplicationUpdateException( UiText.T( "ApplicationUpdates", "The Nexus application file list is too large." ) );

				var notes = new List<string>();

				if ( row.TryGetProperty( "changelogText", out var changelog ) )
				{
					if ( changelog.ValueKind == JsonValueKind.Array )
						foreach ( var note in changelog.EnumerateArray() )
						{
							if ( notes.Count == 100 )
								break;

							if ( note.ValueKind == JsonValueKind.String )
								notes.Add( note.GetString() );
						}
					else if ( changelog.ValueKind == JsonValueKind.String )
						notes.Add( changelog.GetString() );
				}

				long.TryParse( Text( row, "date" ), out long uploaded );
				releases.Add( new ApplicationFile( Text( row, "name" ), Text( row, "version" ), Text( row, "category" ), uploaded, notes.ToArray() ) );
			}

			return ApplicationReleaseSelector.Select( releases, installedVersion );
		}
		catch ( OperationCanceledException ) when ( !cancellationToken.IsCancellationRequested )
		{
			throw new ApplicationUpdateException( UiText.T( "ApplicationUpdates", "Nexus did not respond in time. Please try again later." ) );
		}
		catch ( HttpRequestException )
		{
			throw new ApplicationUpdateException( UiText.T( "ApplicationUpdates", "Could not connect to Nexus Mods. Please check your connection and try again later." ) );
		}
		catch ( Exception error ) when ( error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException )
		{
			throw new ApplicationUpdateException( UiText.T( "ApplicationUpdates", "Nexus returned an unreadable application update response." ) );
		}
	}

	// Some Nexus API versions omit changelogText. Retry without it when that is the only query error.
	// The caller must dispose the returned document.
	private async Task<JsonDocument> QueryFilesAsync( CancellationToken cancellationToken )
	{
		const string query = "query SmrReleases($modId: ID!, $gameId: ID!) { modFiles(modId: $modId, gameId: $gameId) { fileId name version date category primary changelogText } }";
		var variables = new
		{
			modId = "13076",
			gameId
		};
		var response = await QueryAsync( query, variables, cancellationToken ).ConfigureAwait( false );

		if ( response.RootElement.TryGetProperty( "errors", out var errors ) && errors.ValueKind == JsonValueKind.Array )
		{
			bool unsupportedChangelog = errors.GetArrayLength() > 0;

			foreach ( var error in errors.EnumerateArray() )
			{
				string message = Text( error, "message" );

				unsupportedChangelog &= message.Contains( "changelogText", StringComparison.Ordinal )
					&& message.Contains( "Cannot query field", StringComparison.OrdinalIgnoreCase );
			}

			if ( unsupportedChangelog )
			{
				response.Dispose();
				response = await QueryAsync( query.Replace( " changelogText", "" ), variables, cancellationToken ).ConfigureAwait( false );
			}
		}

		try
		{
			ThrowGraphQlErrors( response.RootElement );

			return response;
		}
		catch { response.Dispose(); throw; }
	}

	private async Task<JsonDocument> QueryAsync( string query, object variables, CancellationToken cancellationToken )
	{
		using var request = new HttpRequestMessage( HttpMethod.Post, Endpoint )
		{
			Content = new StringContent( JsonSerializer.Serialize( new { query, variables } ), Encoding.UTF8, "application/json" )
		};
		using var response = await client.SendAsync( request, HttpCompletionOption.ResponseHeadersRead, cancellationToken ).ConfigureAwait( false );

		if ( (int)response.StatusCode == 429 )
			throw new ApplicationUpdateException( UiText.T( "ApplicationUpdates", "Nexus is limiting requests. Please try again later." ) );

		if ( !response.IsSuccessStatusCode )
			throw new ApplicationUpdateException( UiText.F( "ApplicationUpdates", $"Nexus could not complete the update check (HTTP {(int)response.StatusCode}). Please try again later." ) );

		if ( response.Content.Headers.ContentLength > MaximumResponseBytes )
			throw new ApplicationUpdateException( UiText.T( "ApplicationUpdates", "The Nexus update response is too large." ) );

		using var stream = await response.Content.ReadAsStreamAsync( cancellationToken ).ConfigureAwait( false );
		using var buffer = new MemoryStream();
		byte[] chunk = new byte[8192];
		int count;

		while ( ( count = await stream.ReadAsync( chunk, cancellationToken ).ConfigureAwait( false ) ) != 0 )
		{
			if ( buffer.Length + count > MaximumResponseBytes )
				throw new ApplicationUpdateException( UiText.T( "ApplicationUpdates", "The Nexus update response is too large." ) );

			buffer.Write( chunk, 0, count );
		}

		buffer.Position = 0;

		return await JsonDocument.ParseAsync( buffer, cancellationToken: cancellationToken ).ConfigureAwait( false );
	}

	private static void ThrowGraphQlErrors( JsonElement root )
	{
		if ( root.TryGetProperty( "errors", out var errors ) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() != 0 )
			throw new ApplicationUpdateException( UiText.T( "ApplicationUpdates", "Nexus could not complete the application update query. Please try again later." ) );
	}

	private static string Text( JsonElement row, string name )
		=> row.TryGetProperty( name, out var value ) && value.ValueKind is JsonValueKind.String or JsonValueKind.Number ? value.ToString() : "";

	public void Dispose() => client.Dispose();
}
