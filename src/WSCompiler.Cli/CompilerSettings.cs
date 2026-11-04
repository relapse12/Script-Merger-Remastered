using System.Text.Json;

// Save the default beside this portable CLI copy. An explicit --vanilla argument takes precedence.
internal sealed record CompilerSettings( string? Vanilla )
{
	public static string FilePath => Path.Combine( AppContext.BaseDirectory, "WSCompiler.settings.json" );

	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		WriteIndented = true
	};

	public static string? LoadVanilla()
	{
		if ( !File.Exists( FilePath ) )
			return null;

		try
		{
			var settings = JsonSerializer.Deserialize<CompilerSettings>( File.ReadAllText( FilePath ), JsonOptions );
			return string.IsNullOrWhiteSpace( settings?.Vanilla ) ? null : settings.Vanilla;
		}
		catch ( JsonException ex )
		{
			throw new ArgumentException(
				$"Cannot read settings: {FilePath}. Run config --vanilla <scripts-folder> to save a new default, or pass --vanilla.", ex );
		}
	}

	public static void SaveVanilla( string directory )
	{
		string vanilla = Path.GetFullPath( directory );

		if ( !Directory.Exists( vanilla ) )
			throw new ArgumentException( $"Vanilla scripts folder does not exist: {vanilla}" );

		File.WriteAllText( FilePath, JsonSerializer.Serialize( new CompilerSettings( vanilla ), JsonOptions ) );
	}
}
