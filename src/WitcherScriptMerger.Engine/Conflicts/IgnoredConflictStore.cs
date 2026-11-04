using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WitcherScriptMerger.FileIndex;

namespace WitcherScriptMerger;

// Temporary ignores expire when their source files change. Permanent rules match paths across future scans.
// Keep each game's rules in its own file.
internal sealed class IgnoredConflictStore
{
	internal sealed class Snapshot
	{
		public string Key { get; set; }
		public string RelativePath { get; set; }
		public string Mods { get; set; }
		public string Fingerprint { get; set; }
		public bool IsBundled { get; set; }
	}

	internal sealed class Document
	{
		public int Version { get; set; } = 1;
		public List<Snapshot> Conflicts { get; set; } = new();
		public List<string> PermanentRules { get; set; } = new();
	}

	private readonly string path;
	private readonly GamePaths game;
	private Document document = new();
	public string LoadError { get; private set; }
	public IReadOnlyList<Snapshot> Conflicts => document.Conflicts;
	public IReadOnlyList<string> PermanentRules => document.PermanentRules;

	// If saved rules cannot be read, leave the file alone. A later save must not erase them.
	public IgnoredConflictStore( string path, GamePaths game )
	{
		this.path = Path.GetFullPath( path );
		this.game = game;

		try
		{
			if ( !File.Exists( path ) )
				return;

			Document loaded = JsonSerializer.Deserialize<Document>( File.ReadAllText( path ) );

			if ( loaded == null || loaded.Version != 1 || loaded.Conflicts == null || loaded.PermanentRules == null
				|| loaded.Conflicts.Any( s => s == null || string.IsNullOrEmpty( s.Key )
					|| string.IsNullOrWhiteSpace( s.RelativePath ) || string.IsNullOrEmpty( s.Fingerprint ) )
				|| loaded.PermanentRules.Any( string.IsNullOrWhiteSpace ) )
				throw new InvalidDataException( UiText.T( "IgnoredConflictStore", "The ignore file has an unsupported or incomplete format." ) );

			foreach ( string rule in loaded.PermanentRules )
				NormalizeRule( rule );

			document = loaded;
		}
		catch ( Exception ex ) when ( ex is IOException || ex is UnauthorizedAccessException || ex is JsonException || ex is ArgumentException )
		{
			LoadError = $"Could not read ignored conflicts from {path}: {ex.Message}";
		}
	}

	public static string GetStorePath( string modsDirectory )
	{
		string scope = Path.TrimEndingDirectorySeparator( Path.GetFullPath( modsDirectory ) ).ToUpperInvariant();
		string id = Convert.ToHexString( SHA256.HashData( Encoding.UTF8.GetBytes( scope ) ) );

		return Path.Combine( AppContext.BaseDirectory, "Data", "IgnoredConflicts", id + ".json" );
	}

	public static string KeyFor( ModFile conflict ) => ( conflict.IsBundleContent ? "bundle:" : "loose:" )
		+ conflict.RelativePath.Replace( '/', '\\' ).ToUpperInvariant();

	public bool IsPermanent( ModFile conflict ) => document.PermanentRules.Any( rule => MatchesRule( conflict.RelativePath, rule ) );

	public static string NormalizeRule( string rule )
	{
		string value = ( rule ?? "" ).Trim().Replace( '/', '\\' );

		if ( value.Length == 0 || value.Length > 1024 || value.StartsWith( '\\' )
			|| value.IndexOfAny( new[] { ':', '\0', '\r', '\n', '"', '<', '>', '|' } ) >= 0
			|| value.Split( '\\' ).Any( segment => segment == ".." || segment == "." || segment.Length == 0 ) )
			throw new ArgumentException( UiText.T( "IgnoredConflictStore", "Enter a filename or relative path, optionally using * and ?. Example: strings.list" ) );

		return value;
	}

	public static bool MatchesRule( string relativePath, string rule )
	{
		string normalized = relativePath.Replace( '/', '\\' );
		string pattern = NormalizeRule( rule );

		if ( !pattern.Contains( '\\' ) )
			normalized = normalized.Substring( normalized.LastIndexOf( '\\' ) + 1 );

		return ConflictSearch.MatchesWildcard( normalized, pattern );
	}

	private string Fingerprint( ModFile conflict, Dictionary<string, string> hashes )
	{
		var sources = new List<string>();

		foreach ( var mod in conflict.Mods.OrderBy( m => m.Name, StringComparer.OrdinalIgnoreCase ) )
		{
			string source = Path.GetFullPath( conflict.GetModFile( mod.Name, game ) );

			if ( !hashes.TryGetValue( source, out string hash ) )
			{
				var before = new FileInfo( source );
				long length = before.Length;
				DateTime modified = before.LastWriteTimeUtc;

				using ( var stream = new FileStream( source, FileMode.Open, FileAccess.Read, FileShare.Read ) )
					hash = Convert.ToHexString( SHA256.HashData( stream ) );

				var after = new FileInfo( source );

				if ( after.Length != length || after.LastWriteTimeUtc != modified )
					throw new IOException( UiText.F( "IgnoredConflictStore", $"The source changed while being checked: {source}" ) );

				hashes.Add( source, hash );
			}

			sources.Add( mod.Name.ToUpperInvariant() );
			sources.Add( source.ToUpperInvariant() );
			sources.Add( hash );
		}

		return Convert.ToHexString( SHA256.HashData( JsonSerializer.SerializeToUtf8Bytes( sources ) ) );
	}

	// Hash each file once for the batch. A repeated conflict replaces its earlier saved ignore.
	public void IgnoreUntilChanged( IEnumerable<ModFile> conflicts )
	{
		var hashes = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
		var snapshots = conflicts.Select( c => new Snapshot
		{
			Key = KeyFor( c ),
			RelativePath = c.RelativePath,
			Mods = string.Join( ", ", c.Mods.Select( m => m.Name ) ),
			Fingerprint = Fingerprint( c, hashes ),
			IsBundled = c.IsBundleContent
		} ).ToList();
		var next = Copy();

		foreach ( Snapshot snapshot in snapshots )
		{
			next.Conflicts.RemoveAll( old => old.Key == snapshot.Key );
			next.Conflicts.Add( snapshot );
		}

		Commit( next );
	}

	// Unreadable or changed sources make temporary ignores expire. During a partial refresh,
	// leave ignores for sources outside that refresh alone.
	public HashSet<string> Evaluate(
		IEnumerable<ModFile> conflicts,
		bool checkScripts = true,
		bool checkXml = true,
		bool checkBundles = true )
	{
		var files = conflicts.GroupBy( KeyFor ).ToDictionary( g => g.Key, g => g.First() );
		var result = files.Values.Where( IsPermanent ).Select( KeyFor ).ToHashSet();
		var hashes = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
		var next = Copy();

		foreach ( Snapshot snapshot in document.Conflicts )
		{
			if ( !files.ContainsKey( snapshot.Key ) && !( snapshot.IsBundled ? checkBundles
				: ModFile.IsScript( snapshot.RelativePath ) || ModFile.IsRsBlob( snapshot.RelativePath ) || ModFile.IsPrecompiledScriptSymbol( snapshot.RelativePath )
					? checkScripts
					: checkXml ) )
				continue;

			bool unchanged = false;

			if ( files.TryGetValue( snapshot.Key, out ModFile conflict ) )
			{
				try
				{
					unchanged = Fingerprint( conflict, hashes ) == snapshot.Fingerprint;
				}
				catch ( Exception ex ) when ( ex is IOException || ex is UnauthorizedAccessException ) { }
			}

			if ( unchanged )
				result.Add( snapshot.Key );
			else
				next.Conflicts.Remove( snapshot );
		}

		if ( next.Conflicts.Count != document.Conflicts.Count )
			Commit( next );

		return result;
	}

	public void Restore( IEnumerable<string> keys )
	{
		var selected = keys.ToHashSet();
		var next = Copy();

		next.Conflicts.RemoveAll( s => selected.Contains( s.Key ) );
		Commit( next );
	}

	public void AddPermanentRules( IEnumerable<string> rules )
	{
		var next = Copy();

		next.PermanentRules = next.PermanentRules.Concat( rules.Select( NormalizeRule ) )
			.Distinct( StringComparer.OrdinalIgnoreCase ).ToList();
		Commit( next );
	}

	public void RemovePermanentRules( IEnumerable<string> rules )
	{
		var selected = rules.ToHashSet( StringComparer.OrdinalIgnoreCase );
		var next = Copy();

		next.PermanentRules.RemoveAll( selected.Contains );
		Commit( next );
	}

	private Document Copy() => new Document
	{
		Conflicts = document.Conflicts.ToList(),
		PermanentRules = document.PermanentRules.ToList()
	};

	// Update the in-memory rules only after the file is saved, so both copies agree.
	private void Commit( Document next )
	{
		if ( LoadError != null )
			throw new IOException( UiText.Format( "IgnoredConflictStore", "{0}\nThe existing file was left untouched.", LoadError ) );

		Directory.CreateDirectory( Path.GetDirectoryName( path ) );

		string temp = path + "." + Guid.NewGuid().ToString( "N" ) + ".tmp";

		try
		{
			File.WriteAllText( temp, JsonSerializer.Serialize( next, new JsonSerializerOptions { WriteIndented = true } ) );
			File.Move( temp, path, overwrite: true );
			document = next;
		}
		finally { if ( File.Exists( temp ) ) File.Delete( temp ); }
	}
}
