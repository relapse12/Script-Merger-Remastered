#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WitcherScriptMerger.LoadOrder;
using WitcherScriptMerger.Inventory;
using WSCompiler.Core;
using WSCompiler.Core.Scripts;
using WSCompiler.Core.Scripts.Standalone;
using WSCompiler;
using WitcherScriptMerger.Merging;

namespace WitcherScriptMerger.BlobMerging;

// Saved checks include the files and settings they used, so a later scan can tell which results are still valid.
internal sealed record SavedBlobMergeChecks( string Identity, string Fingerprint, MergeInputFile[] Inputs,
	string ModsDirectory, string VanillaDirectory, string InventoryPath, string[] VanillaPaths,
	Dictionary<string, SavedBlobMergeSource> Sources, string[] Recovered,
	Dictionary<string, DeferredText> RecoveryFailures, Dictionary<string, BlobMergeReadiness> Results,
	Dictionary<string, SavedBlobMergeScope> Scopes, Dictionary<string, bool> OutputChecks, string ManagedInventory );
internal sealed record SavedBlobMergeSource( string Identity, MergeInputFile[] Inputs, string[] Paths );
internal sealed record SavedBlobMergeScope( MergeDependencyScope Dependencies, string[] SettingsMods, string Fingerprint,
	Dictionary<string, bool> Outputs, string? PlacementContext, Dictionary<string, SavedBlobMergeSource> Sources, string Inventory );

// Each Mods folder and inventory has its own cache. Reuse a result only while the inputs it used are unchanged.
internal sealed class BlobMergeCheckCache
{
	private sealed record Envelope( int Version, string Engine, string Payload, string Sha256 );

	private const int Version = 4;
	private const int MaximumBytes = 32 * 1024 * 1024;
	// Changing merge-related code changes this fingerprint and invalidates saved checks.
	internal static string Engine { get; } = ReadEngine();

	private static string ReadEngine()
	{
		using var stream = typeof( BlobMergeCheckCache ).Assembly.GetManifestResourceStream( "ScriptMergerRemastered.MergeSemantics" )
			?? throw new InvalidOperationException( "The merge semantic fingerprint is missing." );
		using var reader = new StreamReader( stream );

		return MergeBuilder.Hash( Encoding.UTF8.GetBytes( reader.ReadToEnd() ) );
	}

	internal static string DefaultDirectory => Path.Combine( BlobMergeStore.ApplicationStorage, "Cache" );
	internal string FilePath { get; }

	internal BlobMergeCheckCache( string directory, string mods, string inventory )
		=> FilePath = System.IO.Path.Combine( directory, MergeBuilder.Hash( Encoding.UTF8.GetBytes(
			System.IO.Path.GetFullPath( mods ).ToUpperInvariant() + "\n" + System.IO.Path.GetFullPath( inventory ).ToUpperInvariant() ) ) + ".json" );

	internal SavedBlobMergeChecks? Read(
		string identity,
		IReadOnlyList<BlobMergeSource> sources,
		IReadOnlyList<ModLoadSetting> settings,
		bool valid )
	{
		try
		{
			if ( !File.Exists( FilePath ) || new FileInfo( FilePath ).Length > MaximumBytes )
				return null;

			var envelope = JsonSerializer.Deserialize<Envelope>( File.ReadAllBytes( FilePath ) );

			if ( envelope is null || envelope.Version != Version || envelope.Engine != Engine
				|| envelope.Payload is null || MergeBuilder.Hash( Encoding.UTF8.GetBytes( envelope.Payload ) ) != envelope.Sha256 )
				return null;

			var saved = JsonSerializer.Deserialize<SavedBlobMergeChecks>( envelope.Payload );

			return saved is not null && saved.Identity == identity && Valid( saved ) ? Filter( saved, sources, settings, valid ) : null;
		}
		catch ( Exception error ) when ( BlobMergeWorkspace.IsInspectionFailure( error ) ) { return null; }
	}

	// These checks apply to every saved result. Individual selections are filtered separately.
	internal static bool Matches( SavedBlobMergeChecks saved )
	{
		try
		{
			if ( new BlobMergeStore( saved.InventoryPath ).HasInterruptedUpdate )
				return false;

			if ( ManagedInventoryFingerprint( ReadInventory( saved.InventoryPath ) ) != saved.ManagedInventory )
				return false;

			MergeBuilder.VerifyInputs( saved.Inputs );

			foreach ( var output in saved.OutputChecks )
			{
				MergeBuilder.SafePath( saved.ModsDirectory, System.IO.Path.GetRelativePath( saved.ModsDirectory, output.Key ) );
			}

			return saved.VanillaPaths.SequenceEqual(
				Directory.GetFiles( saved.VanillaDirectory, "*.ws", SearchOption.AllDirectories ).Order( StringComparer.OrdinalIgnoreCase ),
				StringComparer.OrdinalIgnoreCase );
		}
		catch ( Exception error ) when ( BlobMergeWorkspace.IsInspectionFailure( error ) ) { return false; }
	}

	// Keep only checks whose inputs still match. RelevantChange determines whether another changed source can affect them.
	internal static SavedBlobMergeChecks? Filter(
		SavedBlobMergeChecks saved,
		IReadOnlyList<BlobMergeSource> sources,
		IReadOnlyList<ModLoadSetting> settings,
		bool valid )
	{
		if ( !valid || sources.Any( source => source.IsModIo && !source.StateKnown ) || !Matches( saved ) )
			return null;

		var current = sources.ToDictionary( source => source.Name, StringComparer.OrdinalIgnoreCase );
		var inventory = ReadInventory( saved.InventoryPath );
		var unchanged = sources.ToDictionary(
			source => source.Name,
			source => saved.Sources.TryGetValue( source.Name, out var before ) && Matches( before, source ),
			StringComparer.OrdinalIgnoreCase );
		var facts = new Dictionary<string, (string[] Declared, string[] Mentioned, string[] Paths)?>( StringComparer.OrdinalIgnoreCase );

		// A changed source matters if it uses or defines a name involved in the merge, or touches one of its files.
		// Treat unreadable changes as relevant because their effect cannot be checked.
		bool RelevantChange( SavedBlobMergeScope scope, BlobMergeSource source )
		{
			if ( unchanged[source.Name] )
				return false;

			if ( !facts.TryGetValue( source.Name, out var value ) )
			{
				saved.Sources.TryGetValue( source.Name, out var before );

				var changed = ActivePaths( source )
					.Where( path => before?.Inputs.FirstOrDefault(
						input => input.Path.Equals( path, StringComparison.OrdinalIgnoreCase ) ) is not { } input
						|| !MatchesInputs( [input] ) )
					.ToArray();

				facts[source.Name] = value = Inspect( source, changed );
			}

			return value is null || value.Value.Mentioned.Intersect( scope.Dependencies.ChangedNames, StringComparer.Ordinal ).Any()
				|| value.Value.Declared.Intersect( scope.Dependencies.UsedNames, StringComparer.Ordinal ).Any()
				|| value.Value.Declared.Intersect( scope.Dependencies.OverlapNames, StringComparer.Ordinal ).Any()
				|| value.Value.Paths.Intersect( scope.Dependencies.Paths, StringComparer.OrdinalIgnoreCase ).Any();
		}

		var scopes = saved.Scopes
			.Where( pair =>
			{
				var scope = pair.Value;

				if ( scope.Fingerprint != ScopeFingerprint( settings, scope.SettingsMods, valid ) )
					return false;

				bool sourcesMatch = scope.Sources.All( source =>
				{
					if ( !current.TryGetValue( source.Key, out var now ) || source.Value.Identity != SourceIdentity( now ) )
						return false;

					bool pathsMatch = source.Value.Paths.All( path =>
						ActivePaths( now ).Contains( path, StringComparer.OrdinalIgnoreCase )
						|| source.Value.Inputs.Any( input =>
							input.Path.Equals( path, StringComparison.OrdinalIgnoreCase ) && input.ReadPath is not null ) );

					return pathsMatch && MatchesInputs( source.Value.Inputs );
				} );

				if ( !sourcesMatch )
					return false;

				if ( scope.Inventory != InventoryFingerprint(
					inventory, scope.Dependencies, scope.Outputs.Keys, saved.ModsDirectory ) )
					return false;

				if ( scope.PlacementContext is not null && scope.PlacementContext != PlacementFingerprint( sources, settings ) )
					return false;

				bool outputsMatch = scope.Outputs.All( output =>
					output.Value == ( File.Exists( output.Key ) || Directory.Exists( output.Key ) ) );

				return outputsMatch && !sources.Any( source => RelevantChange( scope, source ) );
			} )
			.ToDictionary( pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase );

		var results = saved.Results
			.Where( pair => scopes.ContainsKey( pair.Key ) )
			.ToDictionary( pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase );

		if ( results.Count == 0 )
			return null;

		var retained = results.Keys.SelectMany( key => key.Split( '\n' ) ).ToHashSet( StringComparer.OrdinalIgnoreCase );

		return saved with
		{
			Results = results,
			Scopes = scopes,
			RecoveryFailures = saved.RecoveryFailures.Where( pair => retained.Contains( pair.Key ) )
				.ToDictionary( pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase )
		};
	}

	// Hash only the settings this selection uses so unrelated priority edits do not force a recheck.
	internal static string ScopeFingerprint( IReadOnlyList<ModLoadSetting> settings, IEnumerable<string> names, bool valid )
	{
		var selected = names.ToHashSet( StringComparer.OrdinalIgnoreCase );

		return BlobMergePreflight.SettingsFingerprint( settings.Where( setting => selected.Contains( setting.ModName ) ).ToArray(), valid );
	}

	internal static string SourceIdentity( BlobMergeSource source ) => JsonSerializer.Serialize( new
	{
		source.Name,
		source.Directory,
		source.IsModIo,
		source.ExistingLooseMerge,
		source.StateKnown,
		Blobs = source.Blobs.Order( StringComparer.OrdinalIgnoreCase )
	} );

	internal static string PlacementFingerprint( IReadOnlyList<BlobMergeSource> sources, IReadOnlyList<ModLoadSetting> settings )
		=> JsonSerializer.Serialize( sources.Where( source => !source.IsModIo ).Select( source => source.Name ).Concat( settings.Select( setting => setting.ModName ) )
			.Distinct( StringComparer.OrdinalIgnoreCase ).Order( StringComparer.OrdinalIgnoreCase ) );

	internal static string[] ActivePaths( BlobMergeSource source ) => source.Blobs.Count > 0 ? source.Blobs.Order( StringComparer.OrdinalIgnoreCase ).ToArray()
		: ScriptPaths( System.IO.Path.Combine( source.Directory, "content", "scripts" ) );

	internal static bool Matches( SavedBlobMergeSource before, BlobMergeSource source )
	{
		try
		{
			if ( before.Identity != SourceIdentity( source ) || !before.Paths.SequenceEqual( ActivePaths( source ), StringComparer.OrdinalIgnoreCase ) )
				return false;

			MergeBuilder.VerifyInputs( before.Inputs );

			return true;
		}
		catch ( Exception error ) when ( BlobMergeWorkspace.IsInspectionFailure( error ) ) { return false; }
	}

	internal static MergeInventory ReadInventory( string path ) => MergeInventory.Deserialize( File.Exists( path ) ? File.ReadAllBytes( path ) : null );

	internal static string ManagedInventoryFingerprint( MergeInventory inventory ) => InventoryHash( inventory, merge => merge.IsBlobMerge, managed: true );

	internal static string InventoryFingerprint(
		MergeInventory inventory,
		MergeDependencyScope scope,
		IEnumerable<string> outputs,
		string mods )
	{
		var paths = scope.Paths.Concat( scope.Files.Select( file => file.Path ) ).Select( path => path.Replace( '\\', '/' ) ).ToHashSet( StringComparer.OrdinalIgnoreCase );
		var outputPaths = outputs.ToHashSet( StringComparer.OrdinalIgnoreCase );

		return InventoryHash(
			inventory,
			merge => merge.Category == FileIndex.Categories.Script && ( paths.Contains( merge.RelativePath.Replace( '\\', '/' ) )
			|| outputPaths.Contains( System.IO.Path.Combine( mods, merge.MergedModName, "content", "scripts", merge.RelativePath ) ) ) );
	}

	private static string InventoryHash( MergeInventory inventory, Func<Merge, bool> include, bool managed = false )
	{
		var subset = new MergeInventory { BlobMergeState = managed ? inventory.BlobMergeState : null };

		foreach ( var merge in inventory.Merges.Where( include ).OrderBy( merge => merge.MergedModName, StringComparer.OrdinalIgnoreCase )
			.ThenBy( merge => merge.RelativePath, StringComparer.OrdinalIgnoreCase ) )
			subset.Merges.Add( merge );

		return MergeBuilder.Hash( subset.Serialize() );
	}

	internal static Dictionary<string, SavedBlobMergeSource> ScopeSources(
		IReadOnlyList<BlobMergeSource> sources,
		IReadOnlyList<MergeInputFile> snapshot,
		MergeDependencyScope scope )
	{
		return sources
			.Where( source => scope.Mods.Contains( source.Name, StringComparer.OrdinalIgnoreCase ) )
			.ToDictionary( source => source.Name, source =>
			{
				string scriptsRoot = System.IO.Path.Combine( source.Directory, "content", "scripts" );
				var observed = snapshot
					.Where( input => input.Path.StartsWith(
						System.IO.Path.TrimEndingDirectorySeparator( source.Directory ) + System.IO.Path.DirectorySeparatorChar,
						StringComparison.OrdinalIgnoreCase ) )
					.ToArray();

				var blobs = observed
					.Where( input => input.Path.EndsWith( ".rsblob", StringComparison.OrdinalIgnoreCase ) )
					.ToArray();

				var inputs = blobs;

				if ( blobs.Length == 0 )
				{
					inputs = observed
						.Where( input => input.Path.StartsWith(
							scriptsRoot + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase )
							&& scope.Files.Any( file =>
								file.Mod.Equals( source.Name, StringComparison.OrdinalIgnoreCase )
								&& file.Path.Equals(
									System.IO.Path.GetRelativePath( scriptsRoot, input.Path ).Replace( '\\', '/' ),
									StringComparison.OrdinalIgnoreCase ) ) )
						.ToArray();
				}

				return new SavedBlobMergeSource(
					SourceIdentity( source ), inputs, inputs.Select( input => input.Path ).ToArray() );
			}, StringComparer.OrdinalIgnoreCase );
	}

	private static bool MatchesInputs( IEnumerable<MergeInputFile> inputs )
	{
		try
		{
			MergeBuilder.VerifyInputs( inputs );

			return true;
		}
		catch ( Exception error ) when ( BlobMergeWorkspace.IsInspectionFailure( error ) ) { return false; }
	}

	// Look for both declarations and name references in changed files. Either can affect an existing plan.
	internal static (string[] Declared, string[] Mentioned, string[] Paths)? Inspect( BlobMergeSource source, IReadOnlyList<string> paths )
	{
		try
		{
			var items = new List<Item>();
			var mentioned = new HashSet<string>( StringComparer.Ordinal );

			foreach ( string path in paths )
			{
				byte[] data = File.ReadAllBytes( path );

				if ( source.Blobs.Count > 0 )
				{
					items.AddRange( BlobReferences.Read( data, path ).Items );

					foreach ( string symbol in ScriptBlob.FromBytes( data, path ).Names.Concat( BlobReferences.LinkedSymbols( data, path ) ) )
						foreach ( Match match in Regex.Matches( symbol, @"[\p{L}_][\p{L}\p{N}_]*" ) )
							mentioned.Add( match.Value );
				}
				else
				{
					string text = TextDecoding.Decode( data );

					items.AddRange( Parser.ParseSource( text, skipBodies: true ) );
					mentioned.UnionWith( Lexer.Tokenize( text, lenient: true ).Where( token => token.Kind == TokKind.Ident ).Select( token => token.Text ) );
				}
			}

			return (MergeDependencyScope.DeclaredNames( items ), mentioned.ToArray(), source.Blobs.Count > 0 ? [] : paths
				.Select( path => System.IO.Path.GetRelativePath( System.IO.Path.Combine( source.Directory, "content", "scripts" ), path ).Replace( '\\', '/' ) ).ToArray());
		}
		catch ( Exception error ) when ( BlobMergeWorkspace.IsInspectionFailure( error ) ) { return null; }
	}

	internal static string[] ScriptPaths( string content ) => Directory.Exists( content )
		? Directory.GetFiles( content, "*", SearchOption.AllDirectories ).Where( path => path.EndsWith( ".ws", StringComparison.OrdinalIgnoreCase )
			|| System.IO.Path.GetFileName( path ).Equals( "precompiled.rsblob", StringComparison.OrdinalIgnoreCase ) ).Order( StringComparer.OrdinalIgnoreCase ).ToArray() : [];

	// Do not save results from inputs that changed during the check. Cache write failures are harmless.
	internal void Write( SavedBlobMergeChecks saved, bool snapshotVerified = false )
	{
		string? temporary = null;

		try
		{
			if ( !snapshotVerified )
			{
				if ( !Matches( saved ) )
					return;

				MergeBuilder.VerifyInputs( saved.Sources.Values.SelectMany( source => source.Inputs ) );
			}

			string payload = JsonSerializer.Serialize( saved );
			byte[] data = JsonSerializer.SerializeToUtf8Bytes( new Envelope( Version, Engine, payload, MergeBuilder.Hash( Encoding.UTF8.GetBytes( payload ) ) ) );

			if ( data.Length > MaximumBytes )
				return;

			Directory.CreateDirectory( System.IO.Path.GetDirectoryName( FilePath )! );
			temporary = FilePath + "." + Guid.NewGuid().ToString( "N" ) + ".tmp";
			File.WriteAllBytes( temporary, data );
			File.Move( temporary, FilePath, overwrite: true );
			temporary = null;
		}
		catch ( Exception error ) when ( BlobMergeWorkspace.IsInspectionFailure( error ) ) { }
		finally
		{
			if ( temporary is not null )
				try
				{
					File.Delete( temporary );
				}
				catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException ) { }
		}
	}

	// JSON can leave required fields null. Reject incomplete entries before using them.
	private static bool Valid( SavedBlobMergeChecks saved ) => saved.Inputs is not null && saved.Inputs.All( input => input is not null
			&& !string.IsNullOrEmpty( input.Path ) && input.Sha256 is not null )
		&& !string.IsNullOrEmpty( saved.ModsDirectory ) && !string.IsNullOrEmpty( saved.VanillaDirectory ) && !string.IsNullOrEmpty( saved.InventoryPath )
		&& saved.VanillaPaths is not null && saved.VanillaPaths.All( path => path is not null )
		&& saved.Sources is not null && saved.Sources.All( pair => pair.Value is { Identity: not null, Inputs: not null, Paths: not null }
			&& pair.Value.Inputs.All( input => input is { Path: not null, Sha256: not null } ) && pair.Value.Paths.All( path => path is not null ) )
		&& saved.Recovered is not null && saved.Recovered.All( name => name is not null )
		&& saved.RecoveryFailures is not null && saved.RecoveryFailures.All( pair => ValidMessage( pair.Value ) )
		&& saved.Results is not null && saved.Results.Count > 0 && saved.Results.All( pair => pair.Value is not null
			&& ( pair.Value.Failure is null || ValidMessage( pair.Value.Failure ) ) )
		&& saved.Scopes is not null && saved.Scopes.All( pair => pair.Value is
		{
			Dependencies.Mods: not null, Dependencies.ChangedNames: not null,
			Dependencies.OverlapNames: not null, Dependencies.UsedNames: not null, Dependencies.Paths: not null, Dependencies.Files: not null,
			SettingsMods: not null, Fingerprint: not null, Outputs: not null, Sources: not null, Inventory: not null
		} )
		&& saved.Scopes.Values.All( scope => scope.Sources.Values.All( source => source is { Identity: not null, Inputs: not null, Paths: not null }
			&& source.Inputs.All( input => input is { Path: not null, Sha256: not null } ) && source.Paths.All( path => path is not null ) )
			&& scope.Dependencies.Files.All( file => file is { Mod: not null, Path: not null } ) )
		&& saved.OutputChecks is not null && saved.ManagedInventory is not null;

	private static bool ValidMessage( DeferredText? value ) => value?.Text is not null
		&& ( value.Arguments is null || value.Arguments.All( ValidMessage ) );
}
