#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace WitcherScriptMerger.Tools;

// Align parsed CSV rows before text merging so moving an effect does not appear to edit it.
internal sealed record CsvMergePlan( byte[]? Vanilla, byte[] First, byte[] Second,
	bool Structured, IReadOnlyList<string> ConflictRows );

// Prepare temporary CSV copies for the editor, preserving source bundles and existing output.
internal sealed class CsvMergeInputs : IDisposable
{
	private const int MaximumInputBytes = 16 * 1024 * 1024;
	private static readonly UTF8Encoding StrictUtf8 = new( false, true );
	private string? directory;
	private int outputPadding;

	internal string FirstPath { get; private set; }
	internal string SecondPath { get; private set; }
	internal string? VanillaPath { get; private set; }
	internal bool HasConflicts { get; private set; }

	private CsvMergeInputs( string first, string second, string? vanilla )
	{
		FirstPath = first;
		SecondPath = second;
		VanillaPath = vanilla;
	}

	internal static CsvMergeInputs Prepare( string first, string second, string? vanilla,
		string outputPath, string temporaryRoot )
	{
		var inputs = new CsvMergeInputs( first, second, vanilla );

		if ( !Path.GetExtension( outputPath ).Equals( ".csv", StringComparison.OrdinalIgnoreCase ) )
			return inputs;

		if ( new[] { first, second, vanilla }.Where( path => path != null )
			.Any( path => new FileInfo( path! ).Length > MaximumInputBytes ) )
			return inputs;

		byte[] firstBytes = File.ReadAllBytes( first );
		byte[] secondBytes = File.ReadAllBytes( second );
		byte[]? vanillaBytes = vanilla == null ? null : File.ReadAllBytes( vanilla );
		CsvMergePlan plan = Analyze( vanillaBytes, firstBytes, secondBytes );

		inputs.HasConflicts = plan.ConflictRows.Count != 0;
		inputs.outputPadding = plan.Structured ? 0
			: Math.Max( PaddingLength( firstBytes ), PaddingLength( secondBytes ) );

		try
		{
			inputs.FirstPath = inputs.Stage( first, firstBytes, plan.First, "First", temporaryRoot );
			inputs.SecondPath = inputs.Stage( second, secondBytes, plan.Second, "Second", temporaryRoot );

			if ( vanilla != null )
				inputs.VanillaPath = inputs.Stage( vanilla, vanillaBytes!, plan.Vanilla!, "Vanilla", temporaryRoot );

			return inputs;
		}
		catch
		{
			inputs.Dispose();

			throw;
		}
	}

	// Fall back to the original bytes when the tables cannot be aligned reliably.
	internal static CsvMergePlan Analyze( byte[]? vanilla, byte[] first, byte[] second )
	{
		DecodedInput a = Decode( vanilla );
		DecodedInput b = Decode( first );
		DecodedInput c = Decode( second );
		var fallback = new CsvMergePlan( a.Bytes, b.Bytes!, c.Bytes!, false, Array.Empty<string>() );

		if ( a.Text == null || b.Text == null || c.Text == null )
			return fallback;

		foreach ( char delimiter in new[] { ';', ',' } )
		{
			Table? baseTable = Table.Parse( a.Text, delimiter );
			Table? firstTable = Table.Parse( b.Text, delimiter );
			Table? secondTable = Table.Parse( c.Text, delimiter );

			if ( baseTable == null || firstTable == null || secondTable == null
				|| !baseTable.Header.SequenceEqual( firstTable.Header )
				|| !baseTable.Header.SequenceEqual( secondTable.Header )
				|| !baseTable.Keys.SequenceEqual( firstTable.Keys )
				|| !baseTable.Keys.SequenceEqual( secondTable.Keys ) )
				continue;

			var firstRows = new List<string> { baseTable.RawRows[0] };
			var secondRows = new List<string> { baseTable.RawRows[0] };
			var conflicts = new List<string>();
			int[][] effectGroups = FindEffectGroups( baseTable.Header );

			for ( int row = 1; row < baseTable.Rows.Count; row++ )
			{
				string[] original = baseTable.Rows[row];
				string[] left = firstTable.Rows[row];
				string[] right = secondTable.Rows[row];
				string[] merged = new string[original.Length];
				bool conflict = PrepareMovedEffects( original, left, right, effectGroups, merged,
					out HashSet<int> relocatedColumns );

				for ( int column = 0; column < original.Length; column++ )
				{
					if ( relocatedColumns.Contains( column ) )
						continue;

					if ( left[column] == right[column] || right[column] == original[column] )
						merged[column] = left[column];
					else if ( left[column] == original[column] )
						merged[column] = right[column];
					else
						conflict = true;
				}

				// Effect, type and priority must move together.
				conflict |= effectGroups.Where( group => !group.All( relocatedColumns.Contains ) ).Any( group =>
					group.Any( column => left[column] != original[column] )
					&& group.Any( column => right[column] != original[column] )
					&& group.Any( column => left[column] != right[column] ) );

				if ( conflict )
				{
					conflicts.Add( original[0] );
					firstRows.Add( firstTable.RawRows[row] );
					secondRows.Add( secondTable.RawRows[row] );

					continue;
				}

				string raw = merged.SequenceEqual( original ) ? baseTable.RawRows[row]
					: merged.SequenceEqual( left ) ? firstTable.RawRows[row]
					: merged.SequenceEqual( right ) ? secondTable.RawRows[row]
					: string.Join( delimiter, merged.Select( value => Quote( value, delimiter ) ) );

				firstRows.Add( raw );
				secondRows.Add( raw );
			}

			return new CsvMergePlan( a.Bytes, Encode( baseTable.Join( firstRows ), b.Bom ),
				Encode( baseTable.Join( secondRows ), c.Bom ), true, conflicts );
		}

		return fallback;
	}

	private string Stage( string source, byte[] original, byte[] prepared, string label, string root )
	{
		if ( original.AsSpan().SequenceEqual( prepared ) )
			return source;

		directory ??= Path.Combine( Path.GetFullPath( root ), "csv-" + Guid.NewGuid().ToString( "N" ) );
		Directory.CreateDirectory( directory );

		string path = Path.Combine( directory, label + ".csv" );

		File.WriteAllBytes( path, prepared );

		return path;
	}

	public void Dispose()
	{
		if ( directory != null && Directory.Exists( directory ) )
			Directory.Delete( directory, recursive: true );

		directory = null;
	}

	// Decoding strips trailing zero bytes; restore them when saving an unstructured merge.
	internal void RestoreOutputPadding( string outputPath )
	{
		if ( outputPadding == 0 || !File.Exists( outputPath ) )
			return;

		using FileStream output = new( outputPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None );
		int existing = 0;

		for ( long position = output.Length - 1; position >= 0 && existing < outputPadding; position-- )
		{
			output.Position = position;

			if ( output.ReadByte() != 0 )
				break;

			existing++;
		}

		if ( existing < outputPadding )
		{
			output.Position = output.Length;
			output.Write( new byte[outputPadding - existing] );
		}
	}

	private static int PaddingLength( byte[] bytes )
	{
		DecodedInput decoded = Decode( bytes );

		return decoded.Text == null ? 0 : bytes.Length - decoded.Bytes!.Length;
	}

	private sealed record DecodedInput( byte[]? Bytes, string? Text, bool Bom );

	private static DecodedInput Decode( byte[]? bytes )
	{
		if ( bytes == null || bytes.Length > MaximumInputBytes )
			return new DecodedInput( bytes, null, false );

		int end = bytes.Length;

		while ( end > 0 && bytes[end - 1] == 0 )
			end--;

		if ( end == 0 && bytes.Length != 0 )
			return new DecodedInput( bytes, null, false );

		bool bom = end >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf;
		int start = bom ? 3 : 0;

		try
		{
			string text = StrictUtf8.GetString( bytes, start, end - start );

			if ( text.Contains( '\0' ) )
				return new DecodedInput( bytes, null, bom );

			return new DecodedInput( end == bytes.Length ? bytes : bytes[..end], text, bom );
		}
		catch ( DecoderFallbackException )
		{
			return new DecodedInput( bytes, null, bom );
		}
	}

	private static byte[] Encode( string text, bool bom )
	{
		return StrictUtf8.GetBytes( bom ? "\uFEFF" + text : text );
	}

	private static string Quote( string value, char delimiter )
	{
		return value.Contains( delimiter ) || value.Contains( '"' )
			? '"' + value.Replace( "\"", "\"\"" ) + '"' : value;
	}

	private static int[][] FindEffectGroups( string[] header )
	{
		var groups = new List<int[]>();

		for ( int slot = 1; slot <= 5; slot++ )
		{
			int[] columns = new[] { "effect", "priority", "prob", "strength", "type" }
				.Select( suffix => Array.IndexOf( header, slot + "_" + suffix ) ).ToArray();

			if ( columns.All( column => column >= 0 ) )
				groups.Add( columns );
		}

		return groups.ToArray();
	}

	// Carry an edit to the effect's new slot. Moving the same effect to different slots remains a conflict.
	private static bool PrepareMovedEffects( string[] original, string[] left, string[] right,
		int[][] groups, string[] merged, out HashSet<int> handled )
	{
		var columns = new HashSet<int>();

		handled = columns;

		Dictionary<int, int> leftMoves = FindMoves( original, left, groups );
		Dictionary<int, int> rightMoves = FindMoves( original, right, groups );

		if ( leftMoves.Any( move => rightMoves.TryGetValue( move.Key, out int target ) && target != move.Value ) )
			return true;

		return Apply( left, right, leftMoves, rightMoves ) || Apply( right, left, rightMoves, leftMoves );

		bool Apply( string[] moved, string[] edited, Dictionary<int, int> moves, Dictionary<int, int> otherMoves )
		{
			foreach ( (int source, int target) in moves )
			{
				int[] from = groups[source];
				int[] to = groups[target];

				if ( SameGroup( original, from, edited, from ) )
					continue;

				if ( otherMoves.TryGetValue( source, out int otherTarget )
					&& otherTarget == target )
					continue;

				// Only carry a path change when the original effect is otherwise unchanged.
				if ( edited[from[0]] == original[from[0]] || edited[from[0]].Length == 0
					|| from.Skip( 1 ).Any( column => edited[column] != original[column] )
					|| to.Any( column => edited[column] != original[column] )
					|| groups.Count( group => edited[group[0]] == edited[from[0]] ) != 1 )
					return true;

				if ( moves.Any( other => other.Key != source && ( other.Key == target || other.Value == source ) )
					|| from.Concat( to ).Any( columns.Contains ) )
					return true;

				for ( int field = 0; field < from.Length; field++ )
				{
					merged[from[field]] = moved[from[field]];
					merged[to[field]] = edited[from[field]];
					columns.Add( from[field] );
					columns.Add( to[field] );
				}
			}

			return false;
		}
	}

	// Only treat a group as moved when its values identify one destination unambiguously.
	private static Dictionary<int, int> FindMoves( string[] original, string[] changed, int[][] groups )
	{
		var moves = new Dictionary<int, int>();

		for ( int source = 0; source < groups.Length; source++ )
		{
			int[] from = groups[source];

			if ( original[from[0]].Length == 0 || SameGroup( original, from, changed, from )
				|| groups.Count( group => original[group[0]] == original[from[0]] ) != 1 )
				continue;

			int[] matches = Enumerable.Range( 0, groups.Length )
				.Where( target => SameGroup( original, from, changed, groups[target] ) ).ToArray();

			if ( matches.Length == 1 && matches[0] != source
				&& groups.Count( group => changed[group[0]] == original[from[0]] ) == 1 )
				moves.Add( source, matches[0] );
		}

		return moves;
	}

	private static bool SameGroup( string[] left, int[] leftColumns, string[] right, int[] rightColumns )
	{
		return leftColumns.Select( column => left[column] ).SequenceEqual( rightColumns.Select( column => right[column] ) );
	}

	// Keep raw rows and line endings so unchanged content can be written back exactly.
	private sealed class Table
	{
		internal List<string[]> Rows { get; } = new();
		internal string[] Header => Rows[0];
		internal IEnumerable<string> Keys => Rows.Skip( 1 ).Select( row => row[0] );
		internal string[] RawRows { get; private init; } = Array.Empty<string>();
		private string lineEnding = "\n";
		private bool terminated;

		internal string Join( IEnumerable<string> rows ) => string.Join( lineEnding, rows ) + ( terminated ? lineEnding : "" );

		// Alignment needs unique keys and consistently shaped rows.
		internal static Table? Parse( string text, char delimiter )
		{
			string[] lines = text.Replace( "\r\n", "\n" ).Replace( '\r', '\n' ).Split( '\n' );
			bool terminated = lines[^1].Length == 0;

			if ( terminated )
				lines = lines[..^1];

			if ( lines.Length < 2 )
				return null;

			var table = new Table
			{
				RawRows = lines,
				terminated = terminated,
				lineEnding = text.Contains( "\r\n" ) ? "\r\n" : text.Contains( '\r' ) ? "\r" : "\n"
			};

			foreach ( string line in lines )
			{
				string[]? fields = ParseRecord( line, delimiter );

				if ( fields == null )
					return null;

				table.Rows.Add( fields );
			}

			string[] header = table.Header;

			if ( header.Length < 2 || !header[0].Equals( "name", StringComparison.OrdinalIgnoreCase )
				|| header.Any( string.IsNullOrWhiteSpace ) || header.Distinct( StringComparer.Ordinal ).Count() != header.Length
				|| table.Rows.Any( row => row.Length != header.Length )
				|| table.Keys.Any( string.IsNullOrWhiteSpace )
				|| table.Keys.Distinct( StringComparer.Ordinal ).Count() != table.Rows.Count - 1 )
				return null;

			return table;
		}

		private static string[]? ParseRecord( string line, char delimiter )
		{
			var fields = new List<string>();
			var value = new StringBuilder();
			bool quoted = false;
			bool closed = false;

			for ( int index = 0; index < line.Length; index++ )
			{
				char character = line[index];

				if ( quoted )
				{
					if ( character != '"' )
						value.Append( character );
					else if ( index + 1 < line.Length && line[index + 1] == '"' )
					{
						value.Append( '"' );
						index++;
					}
					else
					{
						quoted = false;
						closed = true;
					}
				}
				else if ( character == delimiter )
				{
					fields.Add( value.ToString() );
					value.Clear();
					closed = false;
				}
				else if ( closed )
					return null;
				else if ( character == '"' )
				{
					if ( value.Length != 0 )
						return null;

					quoted = true;
				}
				else
					value.Append( character );
			}

			if ( quoted )
				return null;

			fields.Add( value.ToString() );

			return fields.ToArray();
		}
	}
}
