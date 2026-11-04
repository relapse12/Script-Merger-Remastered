using System.Diagnostics;
using System.Text.Json;
using WSCompiler;

return CommandLine.Run( args );

// Handles commands and output; the shared library performs compilation.
internal static class CommandLine
{
	private const string Version = "WSCompiler 0.1.5";
	private const string Help = """
        WitcherScript compiler for Remastered 5.x

        WSCompiler compile --scripts <folder> [--vanilla <scripts-folder>]
            [--out <folder|file.rsblob>] [--overwrite] [--json]
            [--reference <scripts-folder|file.ws|file.rsblob>]...
            [--rtti <dump.xml|slim.txt|slim.txt.gz>]
        WSCompiler check --scripts <folder> [--vanilla <scripts-folder>]
            [--reference <path>]... [--rtti <file>] [--json]
        WSCompiler config --vanilla <scripts-folder>
        WSCompiler config

        compile writes precompiled.rsblob in the current working directory by default.
        --out accepts a folder or an explicit .rsblob filename.
        --overwrite replaces an existing output only after successful compilation.
        check runs the same compilation checks without writing a blob.
        config saves the default vanilla path beside WSCompiler.exe in WSCompiler.settings.json.
        config without options displays that path. --vanilla overrides it for one command.

        --reference adds declarations only; reference code is never included in output.
        References are read in the supplied order. No installed mods are autodetected.
        --rtti overrides the embedded native declarations; vanilla scripts are still required.
        --json writes one machine-readable result and diagnostics, without progress text.

        Example (PowerShell; replace the example folders with your script folders):
          .\WSCompiler.exe compile --scripts "My Mod\scripts" --vanilla "Vanilla Scripts" --out build

        Folder inputs contain loose .ws files. Pass a precompiled mod's .rsblob explicitly.
        No game launch or deployment. Only config saves settings.
        Exit codes: 0 success; 1 compilation/input failure; 2 command/output error; 130 cancelled.
        --help / --version / compile --help / check --help / config --help
        """;

	public static int Run( string[] args )
	{
		if ( args.Length == 0 || args is ["--help"] or ["-h"] or ["help"]
			or ["compile" or "check" or "config", "--help" or "-h"] )
		{
			Console.WriteLine( Version );
			Console.WriteLine( Help );
			return 0;
		}

		if ( args is ["--version"] )
		{
			Console.WriteLine( Version );
			return 0;
		}

		bool json = args.Contains( "--json" );
		var timer = Stopwatch.StartNew();

		try
		{
			if ( args[0] == "config" )
			{
				Configure( args );
				return 0;
			}

			if ( args[0] is not ( "compile" or "check" ) )
				throw new ArgumentException( "Expected compile, check or config. Use --help for examples." );

			bool checkOnly = args[0] == "check";
			var options = CompileOptions.Parse( args, checkOnly );
			string vanilla = options.Vanilla ?? CompilerSettings.LoadVanilla()
				?? throw new ArgumentException(
					"Provide --vanilla <scripts-folder>, or save a default with config --vanilla <scripts-folder>." );

			if ( !json )
				Console.WriteLine( checkOnly ? "Checking scripts..." : "Compiling..." );

			using var cancel = new CancellationTokenSource();
			ConsoleCancelEventHandler handler = ( _, e ) =>
			{
				e.Cancel = true;
				cancel.Cancel();
			};

			Console.CancelKeyPress += handler;
			CompilationResult result;

			try
			{
				result = Compiler.Compile( options.Scripts, vanilla, options.References, options.Rtti, cancel.Token );
			}
			finally
			{
				Console.CancelKeyPress -= handler;
			}

			if ( result.Success && !checkOnly )
				WriteOutput( options.Output!, result.Blob!, options.Overwrite );

			string summary;

			if ( !result.Success )
				summary = checkOnly ? "Check failed; no output was written." : "Compilation failed; output was not replaced.";
			else if ( checkOnly )
				summary = $"Check passed in {timer.Elapsed.TotalSeconds:F3}s; no output was written.";
			else
				summary = $"Compiled {result.Blob!.Length:N0} bytes in {timer.Elapsed.TotalSeconds:F3}s: {options.Output}";

			WriteResult( json, result.Success, options.Output, result.Blob?.Length ?? 0,
				timer.Elapsed.TotalSeconds, result.Diagnostics, summary );

			return result.Success ? 0 : 1;
		}
		catch ( OperationCanceledException )
		{
			WriteResult( json, false, null, 0, timer.Elapsed.TotalSeconds,
				[new( "error", "CANCEL", null, 0, "Compilation cancelled." )],
				"Cancelled; output was not replaced." );

			return 130;
		}
		catch ( Exception ex ) when ( ex is ArgumentException or IOException or UnauthorizedAccessException )
		{
			WriteResult( json, false, null, 0, timer.Elapsed.TotalSeconds,
				[new( "error", "CLI001", null, 0, ex.Message )],
				"Command failed; no output was written. Use --help for examples." );

			return 2;
		}
	}

	private static void Configure( string[] args )
	{
		if ( args is ["config", "--vanilla", var directory] && !directory.StartsWith( "--" ) )
		{
			CompilerSettings.SaveVanilla( directory );
		}
		else if ( args is not ["config"] )
		{
			throw new ArgumentException( "Use config --vanilla <scripts-folder> to save the default, or config to display it." );
		}

		Console.WriteLine( "Default vanilla scripts: " + ( CompilerSettings.LoadVanilla() ?? "Not set" ) );
		Console.WriteLine( "Settings: " + CompilerSettings.FilePath );
	}

	// Keep existing output intact if writing the replacement fails.
	private static void WriteOutput( string output, byte[] bytes, bool overwrite )
	{
		string directory = Path.GetDirectoryName( output )!;
		Directory.CreateDirectory( directory );

		string temporary = Path.Combine( directory, ".wscompiler-" + Guid.NewGuid().ToString( "N" ) + ".tmp" );

		try
		{
			File.WriteAllBytes( temporary, bytes );
			File.Move( temporary, output, overwrite );
		}
		finally
		{
			if ( File.Exists( temporary ) )
				File.Delete( temporary );
		}
	}

	// SMR and launchers parse this JSON. Send progress separately.
	private static void WriteResult(
		bool json,
		bool success,
		string? output,
		int bytes,
		double seconds,
		IReadOnlyList<Diagnostic> diagnostics,
		string summary )
	{
		if ( json )
		{
			Console.WriteLine( JsonSerializer.Serialize(
				new { success, output = success ? output : null, bytes, seconds, diagnostics },
				new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase } ) );

			return;
		}

		foreach ( var diagnostic in diagnostics )
		{
			Console.Error.WriteLine(
				$"{diagnostic.File ?? "compiler"}({diagnostic.Line}): {diagnostic.Severity} {diagnostic.Code}: {diagnostic.Message}" );
		}

		int errors = diagnostics.Count( diagnostic => diagnostic.Severity == "error" );
		int warnings = diagnostics.Count( diagnostic => diagnostic.Severity == "warning" );

		Console.WriteLine( summary );
		Console.WriteLine( $"{errors} error(s), {warnings} warning(s)." );
	}
}
