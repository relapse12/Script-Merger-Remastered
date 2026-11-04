using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace WitcherScriptMerger.FileIndex;

// One Mod.io package can contain several mod and DLC folders.
internal sealed class ModIoInstalledMod
{
	public long ModId { get; init; }

	public string FolderName { get; init; }

	public string DisplayName => $"{FolderName} [Mod.io #{ModId}]";

	public string Title { get; init; }

	public string SourceDirectory { get; init; }

	public string PackageRootDirectory { get; init; }

	public IReadOnlyList<string> DlcDirectories { get; init; } = Array.Empty<string>();

	public bool? IsEnabled { get; init; }

	public int? Priority { get; init; }
}

// Mod.io keeps installed paths in state.json and enabled state and priority in modioModsConfig.json.
// SetPriority is the only operation here that writes to those files.
internal sealed class ModIoDiscovery
{
	internal const string OriginName = "Mod.io";

	private const string WitcherGameId = "8254";

	private readonly string gameStorageDirectory;

	private readonly string configPath;

	public ModIoDiscovery( string storageRoot, string configPath )
	{
		storageRoot ??= ResolveDefaultStorageRoot( Environment.GetEnvironmentVariable( "PUBLIC" ) );

		gameStorageDirectory = ResolveGameStorageDirectory( storageRoot );
		this.configPath = configPath;
	}

	internal static ModIoDiscovery ForSettings( IEngineSettings settings )
	{
		string configured = settings.Get( "ModIoDirectory" );

		return new ModIoDiscovery(
			string.IsNullOrWhiteSpace( configured ) ? null : configured,
			ConfigPath( settings ) );
	}

	internal static string ConfigPath( IEngineSettings settings )
	{
		return GameSettingsLocation.FilePath( settings, "modioModsConfig.json" );
	}

	internal static string ResolveDefaultStorageRoot( string publicProfile )
	{
		string root = string.IsNullOrWhiteSpace( publicProfile )
			? Path.GetDirectoryName( Environment.GetFolderPath( Environment.SpecialFolder.CommonDocuments ) )
			: publicProfile;

		return string.IsNullOrWhiteSpace( root ) ? string.Empty : Path.Combine( root, "mod.io" );
	}

	// Accept either the Mod.io root or the game-specific folder so a saved path is not nested twice.
	internal static string ResolveGameStorageDirectory( string storageRoot )
	{
		if ( string.IsNullOrWhiteSpace( storageRoot ) )
			return string.Empty;

		string fullRoot = Path.GetFullPath( storageRoot );

		return Path.GetFileName( Path.TrimEndingDirectorySeparator( fullRoot ) )
			.Equals( WitcherGameId, StringComparison.OrdinalIgnoreCase )
			? fullRoot
			: Path.Combine( fullRoot, WitcherGameId );
	}

	public IReadOnlyList<ModIoInstalledMod> Discover()
	{
		try
		{
			Dictionary<long, ModIoConfiguration> configuration = ReadConfiguration();
			Dictionary<long, InstalledRecord> installed = ReadInstalledRecords();

			List<ModIoInstalledMod> result = new();

			foreach ( InstalledRecord record in installed.Values.OrderBy( item => item.ModId ) )
			{
				string payloadParent = Path.Combine( record.PathOnDisk, "mods" );

				if ( !Directory.Exists( payloadParent ) )
					continue;

				string dlcParent = Path.Combine( record.PathOnDisk, "dlc" );
				string[] dlcDirectories = Directory.Exists( dlcParent )
					? Directory.GetDirectories( dlcParent, "*", SearchOption.TopDirectoryOnly )
						.OrderBy( path => path, StringComparer.OrdinalIgnoreCase )
						.Select( Path.GetFullPath )
						.ToArray()
					: Array.Empty<string>();

				configuration.TryGetValue( record.ModId, out ModIoConfiguration settings );

				foreach ( string payloadDirectory in Directory.GetDirectories( payloadParent, "*", SearchOption.TopDirectoryOnly )
					.OrderBy( path => path, StringComparer.OrdinalIgnoreCase ) )
				{
					if ( !Directory.Exists( Path.Combine( payloadDirectory, "content" ) ) )
						continue;

					result.Add( new ModIoInstalledMod
					{
						ModId = record.ModId,
						FolderName = Path.GetFileName( payloadDirectory ),
						Title = record.Title,
						SourceDirectory = Path.GetFullPath( payloadDirectory ),
						PackageRootDirectory = Path.GetFullPath( record.PathOnDisk ),
						DlcDirectories = dlcDirectories,
						IsEnabled = settings?.IsEnabled,
						Priority = settings?.Priority
					} );
				}
			}

			return result;
		}
		catch ( IOException )
		{
			return Array.Empty<ModIoInstalledMod>();
		}
		catch ( UnauthorizedAccessException )
		{
			return Array.Empty<ModIoInstalledMod>();
		}
		catch ( JsonException )
		{
			return Array.Empty<ModIoInstalledMod>();
		}
	}

	// Moving one mod renumbers the surrounding entries. Refuse malformed configuration so rewriting it cannot lose mods.
	public bool SetPriority( long modId, int requestedPriority )
	{
		if ( !File.Exists( configPath ) )
			throw new FileNotFoundException( UiText.T( "ModIoDiscovery", "The Mod.io configuration file was not found." ), configPath );

		using JsonDocument document = JsonDocument.Parse( File.ReadAllText( configPath ) );

		if ( document.RootElement.ValueKind != JsonValueKind.Object )
			throw new InvalidDataException( UiText.T( "ModIoDiscovery", "The Mod.io configuration root must be a JSON object." ) );

		List<PriorityEntry> entries = ReadPriorityEntries( document.RootElement );
		int currentIndex = entries.FindIndex( entry => entry.ModId == modId );

		if ( currentIndex < 0 )
			throw new InvalidDataException( UiText.F( "ModIoDiscovery", $"Mod.io mod {modId} is not present in the configuration file." ) );

		List<PriorityEntry> ordered = entries
			.OrderBy( entry => entry.Priority )
			.ThenBy( entry => entry.SourceOrder )
			.ToList();
		PriorityEntry selected = ordered.Single( entry => entry.ModId == modId );

		ordered.Remove( selected );

		int targetIndex = Math.Clamp( requestedPriority, 0, ordered.Count );

		ordered.Insert( targetIndex, selected );

		Dictionary<long, int> updatedPriorities = ordered
			.Select( ( entry, index ) => new { entry.ModId, Priority = index } )
			.ToDictionary( entry => entry.ModId, entry => entry.Priority );
		bool changed = entries.Any( entry => updatedPriorities[entry.ModId] != entry.Priority );

		if ( !changed )
			return false;

		WritePriorities( document.RootElement, updatedPriorities );

		return true;
	}

	// Priority edits need a stricter read than scanning because this data will be written back.
	private static List<PriorityEntry> ReadPriorityEntries( JsonElement root )
	{
		List<PriorityEntry> entries = new();
		HashSet<long> modIds = new();
		int sourceOrder = 0;

		foreach ( JsonProperty property in root.EnumerateObject() )
		{
			if ( !property.Name.Equals( "mod", StringComparison.OrdinalIgnoreCase ) )
				continue;

			if ( property.Value.ValueKind != JsonValueKind.Object
				|| !TryGetInt64( property.Value, "modID", out long modId )
				|| !property.Value.TryGetProperty( "priority", out JsonElement priorityElement )
				|| !priorityElement.TryGetInt32( out int priority ) )
			{
				throw new InvalidDataException( UiText.T( "ModIoDiscovery", "Each Mod.io entry must contain integer modID and priority values." ) );
			}

			if ( !modIds.Add( modId ) )
				throw new InvalidDataException( UiText.F( "ModIoDiscovery", $"The Mod.io configuration contains duplicate entries for mod {modId}." ) );

			entries.Add( new PriorityEntry( modId, priority, sourceOrder++ ) );
		}

		return entries;
	}

	// Keep unknown JSON fields when changing priorities so the game can still use them.
	private void WritePriorities( JsonElement root, IReadOnlyDictionary<long, int> priorities )
	{
		string temporaryPath = configPath + ".script-merger-remastered-new";

		try
		{
			using MemoryStream buffer = new();

			using ( Utf8JsonWriter writer = new( buffer, new JsonWriterOptions
			{
				Indented = true,
				Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
			} ) )
			{
				writer.WriteStartObject();

				foreach ( JsonProperty property in root.EnumerateObject() )
				{
					writer.WritePropertyName( property.Name );

					if ( property.Name.Equals( "mod", StringComparison.OrdinalIgnoreCase )
						&& property.Value.ValueKind == JsonValueKind.Object
						&& TryGetInt64( property.Value, "modID", out long modId )
						&& priorities.TryGetValue( modId, out int priority ) )
					{
						writer.WriteStartObject();

						foreach ( JsonProperty modProperty in property.Value.EnumerateObject() )
						{
							writer.WritePropertyName( modProperty.Name );

							if ( modProperty.Name.Equals( "priority", StringComparison.OrdinalIgnoreCase ) )
								writer.WriteNumberValue( priority );
							else
								modProperty.Value.WriteTo( writer );
						}

						writer.WriteEndObject();
					}
					else
					{
						property.Value.WriteTo( writer );
					}
				}

				writer.WriteEndObject();
			}

			buffer.WriteByte( (byte)'\n' );

			using ( FileStream output = new(
				temporaryPath,
				FileMode.Create,
				FileAccess.Write,
				FileShare.None,
				4096,
				FileOptions.WriteThrough ) )
			{
				buffer.Position = 0;
				buffer.CopyTo( output );
				output.Flush( flushToDisk: true );
			}

			File.Move( temporaryPath, configPath, overwrite: true );
		}
		catch
		{
			if ( File.Exists( temporaryPath ) )
				File.Delete( temporaryPath );

			throw;
		}
	}

	private Dictionary<long, InstalledRecord> ReadInstalledRecords()
	{
		Dictionary<long, InstalledRecord> records = new();

		if ( string.IsNullOrWhiteSpace( gameStorageDirectory ) )
			return new();

		string statePath = Path.Combine( gameStorageDirectory, "metadata", "state.json" );

		if ( !File.Exists( statePath ) )
			return records;

		using JsonDocument document = JsonDocument.Parse( File.ReadAllText( statePath ) );

		if ( !document.RootElement.TryGetProperty( "Mods", out JsonElement mods ) || mods.ValueKind != JsonValueKind.Array )
			return records;

		foreach ( JsonElement mod in mods.EnumerateArray() )
		{
			if ( !TryGetInt64( mod, "ID", out long modId ) )
				continue;

			string path = TryGetString( mod, "PathOnDisk" );

			if ( string.IsNullOrWhiteSpace( path ) )
				continue;

			string title = null;

			if ( mod.TryGetProperty( "Profile", out JsonElement profile ) && profile.ValueKind == JsonValueKind.Object )
				title = TryGetString( profile, "name" );

			records[modId] = new InstalledRecord( modId, path, title );
		}

		return records;
	}

	private Dictionary<long, ModIoConfiguration> ReadConfiguration()
	{
		Dictionary<long, ModIoConfiguration> result = new();

		if ( !File.Exists( configPath ) )
			return result;

		try
		{
			using JsonDocument document = JsonDocument.Parse( File.ReadAllText( configPath ) );

			if ( document.RootElement.ValueKind != JsonValueKind.Object )
				return result;

			// The file can contain repeated mod properties. Enumerate them so none are lost.
			foreach ( JsonProperty property in document.RootElement.EnumerateObject() )
			{
				if ( !property.Name.Equals( "mod", StringComparison.OrdinalIgnoreCase )
					|| property.Value.ValueKind != JsonValueKind.Object
					|| !TryGetInt64( property.Value, "modID", out long modId ) )
					continue;

				bool? isEnabled = null;

				if ( property.Value.TryGetProperty( "isEnabled", out JsonElement enabled )
					&& ( enabled.ValueKind == JsonValueKind.True || enabled.ValueKind == JsonValueKind.False ) )
					isEnabled = enabled.GetBoolean();

				int? priority = null;

				if ( property.Value.TryGetProperty( "priority", out JsonElement priorityElement )
					&& priorityElement.TryGetInt32( out int parsedPriority ) )
					priority = parsedPriority;

				result[modId] = new ModIoConfiguration( isEnabled, priority );
			}
		}
		catch ( JsonException )
		{
		}

		return result;
	}

	private static string TryGetString( JsonElement element, string propertyName )
	{
		return element.TryGetProperty( propertyName, out JsonElement value ) && value.ValueKind == JsonValueKind.String
			? value.GetString()
			: null;
	}

	private static bool TryGetInt64( JsonElement element, string propertyName, out long value )
	{
		value = 0;

		return element.TryGetProperty( propertyName, out JsonElement property ) && property.TryGetInt64( out value );
	}

	private sealed record InstalledRecord( long ModId, string PathOnDisk, string Title );

	private sealed record ModIoConfiguration( bool? IsEnabled, int? Priority );

	private sealed record PriorityEntry( long ModId, int Priority, int SourceOrder );
}
