internal sealed record CompileOptions(
	string Scripts,
	string? Vanilla,
	string? Output,
	string? Rtti,
	bool Overwrite,
	IReadOnlyList<string> References )
{
	public static CompileOptions Parse( string[] args, bool checkOnly )
	{
		string? scripts = null, vanilla = null, output = null, rtti = null;
		bool overwrite = false;
		var references = new List<string>();
		var seen = new HashSet<string>();

		for ( int i = 1; i < args.Length; i++ )
		{
			string option = args[i];

			if ( option != "--reference" && !seen.Add( option ) )
				throw new ArgumentException( $"Repeated option: {option}" );

			if ( option == "--json" )
				continue;

			if ( checkOnly && option is "--out" or "--overwrite" )
				throw new ArgumentException( $"{option} applies to compile. The check command never writes a blob." );

			if ( option == "--overwrite" )
			{
				overwrite = true;
				continue;
			}

			if ( option is not ( "--scripts" or "--vanilla" or "--out" or "--reference" or "--rtti" ) )
				throw new ArgumentException( $"Unknown option: {option}" );

			if ( ++i >= args.Length || args[i].StartsWith( "--" ) || string.IsNullOrWhiteSpace( args[i] ) )
				throw new ArgumentException( $"Missing value for {option}" );

			string value = Path.GetFullPath( args[i] );

			switch ( option )
			{
				case "--scripts":
					scripts = value;
					break;
				case "--vanilla":
					vanilla = value;
					break;
				case "--out":
					output = value;
					break;
				case "--reference":
					references.Add( value );
					break;
				case "--rtti":
					rtti = value;
					break;
			}
		}

		if ( scripts is null )
			throw new ArgumentException( "--scripts <folder> is required." );

		if ( !checkOnly )
		{
			output ??= Path.GetFullPath( "precompiled.rsblob" );

			if ( Directory.Exists( output ) || !output.EndsWith( ".rsblob", StringComparison.OrdinalIgnoreCase ) )
				output = Path.Combine( output, "precompiled.rsblob" );

			if ( references.Any( path => path.Equals( output, StringComparison.OrdinalIgnoreCase ) ) )
				throw new ArgumentException( "Output cannot replace a reference input." );

			if ( File.Exists( output ) && !overwrite )
				throw new IOException( $"Output already exists: {output}. Rerun with --overwrite to replace it." );
		}

		return new( scripts, vanilla, output, rtti, overwrite, references );
	}
}
