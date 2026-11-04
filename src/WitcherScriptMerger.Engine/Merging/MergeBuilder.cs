#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using WSCompiler;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WSCompiler.Core.Scripts.Standalone;


namespace WitcherScriptMerger.Merging;

// An empty hash requires absence. ReadPath locates a source already moved aside by an earlier merge.
public sealed record MergeInputFile( string Path, string Sha256, string? SuppressedPath = null, string? ReadPath = null );

public sealed record MergeOutputFile( string Path, string Sha256, IReadOnlyList<string>? Mods = null );

// Saved as merge.json with the candidate. Null settings and priority values distinguish absent files or entries.
public sealed record BlobMergeManifest(
	string Id,
	string ModsRoot,
	string LooseMod,
	string CompiledMod,
	int? CompiledPriority,
	IReadOnlyList<MergeInputFile> Inputs,
	IReadOnlyList<MergeOutputFile> Outputs,
	IReadOnlyList<string> Mods,
	string? SettingsSha256 = null,
	int Schema = MergeBuilder.ManifestSchema,
	bool SharedLooseFolder = false,
	VanillaBaseline? VanillaBaseline = null );

public sealed record MergeBuildResult(
	string Directory,
	BlobMergeManifest Manifest,
	IReadOnlyList<Diagnostic> Diagnostics,
	bool UsedCachedCompilation = false );

public enum MergeBuildStage
{
	VerifyInputs, WriteLooseScripts, ReuseCompiledScripts, CompileScripts, SaveManifest
}

// Build in a temporary folder. BlobMergeTransaction applies the verified result to the game.
public static class MergeBuilder
{
	public const int ManifestSchema = 4;

	public const string LooseName = "mod0000_MergedFiles";

	public const string CompiledName = "modzzSMR_MergedCompiled";

	// Verify inputs before and after compilation so the candidate describes the same files throughout the build.
	public static MergeBuildResult Build(
		MergePlan plan,
		IReadOnlyDictionary<string, MergeChoice> choices,
		string vanilla,
		IReadOnlyList<string> references,
		string destination,
		string modsRoot,
		IReadOnlyList<MergeInputFile> inputs,
		int? compiledPriority,
		string? settingsSha256 = null,
		CancellationToken cancellation = default,
		string? compiledModName = null,
		bool compiledAlphabetical = false,
		string? looseModName = null,
		bool sharedLooseFolder = false,
		MergeInputFile? cachedCompilation = null,
		IReadOnlyList<SourceFile>? referenceSources = null,
		Action<MergeBuildStage>? progress = null )
	{
		if ( plan.Conflicts.Any( conflict => !choices.ContainsKey( conflict.Key ) ) )
			throw new InvalidOperationException( "Resolve each overlapping definition before building." );

		if ( plan.NeedsCompilation && compiledPriority is null && !compiledAlphabetical )
			throw new InvalidOperationException( "The compiled output needs a valid position between its consumers and dependencies." );

		string compiledName = compiledModName ?? CompiledName;
		string looseName = looseModName ?? LooseName;

		if ( !System.Text.RegularExpressions.Regex.IsMatch( compiledName, @"\Amod[A-Za-z0-9_-]{1,123}\z" )
			|| !looseName.StartsWith( "mod", StringComparison.OrdinalIgnoreCase ) || looseName.Length is < 4 or > 127
			|| looseName.IndexOfAny( Path.GetInvalidFileNameChars() ) >= 0 || looseName.IndexOfAny( ['[', ']'] ) >= 0
			|| looseName.TrimEnd( ' ', '.' ) != looseName || compiledName.Equals( looseName, StringComparison.OrdinalIgnoreCase ) )
			throw new InvalidDataException( "Invalid compiled output folder name." );

		if ( Directory.Exists( destination ) || File.Exists( destination ) )
			throw new IOException( "The candidate directory already exists." );

		progress?.Invoke( MergeBuildStage.VerifyInputs );
		VerifyInputs( inputs );
		Directory.CreateDirectory( destination );
		var diagnostics = new List<Diagnostic>();

		if ( plan.Files.Any( file => !file.Compile ) )
			progress?.Invoke( MergeBuildStage.WriteLooseScripts );

		foreach ( var file in plan.Files.Where( file => !file.Compile ) )
		{
			cancellation.ThrowIfCancellationRequested();
			string relative = looseName + "/content/scripts/" + file.Path.Replace( '\\', '/' );
			Write( relative, Encoding.UTF8.GetBytes( file.Source ) );
		}

		if ( plan.NeedsCompilation && cachedCompilation is not null )
		{
			progress?.Invoke( MergeBuildStage.ReuseCompiledScripts );
			VerifyInputs( [cachedCompilation] );
			byte[] bytes = File.ReadAllBytes( cachedCompilation.Path );

			if ( Hash( bytes ) != cachedCompilation.Sha256 )
				throw new IOException( "The previous compiled merge changed." );

			Write( compiledName + "/content/precompiled.rsblob", bytes );
		}
		else if ( plan.NeedsCompilation )
		{
			progress?.Invoke( MergeBuildStage.CompileScripts );
			string loose = Path.Combine( destination, looseName, "content", "scripts" );
			var compileReferences = references.Concat( Directory.Exists( loose ) ? [loose] : Array.Empty<string>() ).ToArray();
			var sources = plan.Files.Where( file => file.Compile ).Select( file => new SourceFile( file.Path, file.Items.ToList() ) ).ToArray();
			var result = Compiler.CompileSources( sources, vanilla, compileReferences, cancellation: cancellation, referenceSources: referenceSources );
			diagnostics.AddRange( result.Diagnostics );

			if ( !result.Success )
			{
				File.WriteAllText( Path.Combine( destination, "compile-errors.json" ), JsonSerializer.Serialize( diagnostics ) );
				throw new InvalidDataException( string.Join( Environment.NewLine, diagnostics.Select( value => value.File + ": " + value.Message ) ) );
			}

			Write( compiledName + "/content/precompiled.rsblob", result.Blob! );
		}

		progress?.Invoke( MergeBuildStage.VerifyInputs );
		VerifyInputs( inputs );
		var outputs = Directory.EnumerateFiles( destination, "*", SearchOption.AllDirectories )
			.Where( path => Path.GetRelativePath( destination, path ).StartsWith( "mod", StringComparison.Ordinal ) )
			.Select( path =>
{
	string relative = Path.GetRelativePath( destination, path );
	var owners = relative.EndsWith( ".rsblob", StringComparison.OrdinalIgnoreCase )
		? plan.Files.Where( file => file.Compile ).SelectMany( file => file.Owners )
		: plan.Files.Where( file => !file.Compile && relative.Replace( '\\', '/' ) == looseName + "/content/scripts/" + file.Path.Replace( '\\', '/' ) ).SelectMany( file => file.Owners );

	return new MergeOutputFile( relative, Hash( path ), owners.Distinct( StringComparer.OrdinalIgnoreCase ).ToArray() );
} ).ToArray();

		if ( outputs.Length == 0 )
			throw new InvalidDataException( "No runnable merged output was produced." );

		var manifest = new BlobMergeManifest( Guid.NewGuid().ToString( "N" ), Path.GetFullPath( modsRoot ), looseName,
			compiledName, compiledPriority, inputs, outputs, plan.IncludedMods, settingsSha256,
			SharedLooseFolder: sharedLooseFolder, VanillaBaseline: VanillaBaselineStore.Describe( vanilla, inputs, modsRoot ) );
		progress?.Invoke( MergeBuildStage.SaveManifest );
		File.WriteAllText( Path.Combine( destination, "merge.json" ), JsonSerializer.Serialize( manifest, new JsonSerializerOptions { WriteIndented = true } ) );

		return new( destination, manifest, diagnostics, plan.NeedsCompilation && cachedCompilation is not null );

		void Write(
			string relative,
			byte[] bytes )
		{
			string path = SafePath( destination, relative );
			Directory.CreateDirectory( Path.GetDirectoryName( path )! );
			using var stream = new FileStream( path, FileMode.CreateNew, FileAccess.Write, FileShare.None );
			stream.Write( bytes );
		}
	}

	public static string Hash(
		string path )
	{
		using var stream = File.OpenRead( path );

		return Convert.ToHexString( SHA256.HashData( stream ) );
	}

	public static string Hash( ReadOnlySpan<byte> bytes ) => Convert.ToHexString( SHA256.HashData( bytes ) );

	public static void VerifyInputs(
		IEnumerable<MergeInputFile> inputs )
	{
		foreach ( var input in inputs )
			if ( input.ReadPath is not null && File.Exists( input.Path )
				|| ( input.Sha256.Length == 0 ? File.Exists( input.ReadPath ?? input.Path )
					: !File.Exists( input.ReadPath ?? input.Path ) || Hash( input.ReadPath ?? input.Path ) != input.Sha256 ) )
				throw new IOException( "Source changed; rescan before merging: " + input.Path );
	}

	// Saved paths must remain under root, including when directories contain links.
	public static string SafePath(
		string root,
		string relative )
	{
		string fullRoot = Path.GetFullPath( root ).TrimEnd( Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar );

		if ( Path.IsPathRooted( relative ) )
			throw new InvalidDataException( "Expected a relative managed path." );

		string path = Path.GetFullPath( Path.Combine( fullRoot, relative ) );

		if ( !path.StartsWith( fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase ) )
			throw new InvalidDataException( "Managed path escapes its root." );

		for ( string? part = path; part is not null && part.Length >= fullRoot.Length; part = Path.GetDirectoryName( part ) )
			if ( ( File.Exists( part ) || Directory.Exists( part ) ) && ( File.GetAttributes( part ) & FileAttributes.ReparsePoint ) != 0 )
				throw new InvalidDataException( "Managed paths cannot traverse directory links." );

		return path;
	}
}
