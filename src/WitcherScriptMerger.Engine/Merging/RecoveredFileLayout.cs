#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;


namespace WitcherScriptMerger.Merging;

// Group recovered declarations by source mod, keeping compiled and loose output separate.
internal static class RecoveredFileLayout
{
	// Append a hash when cleaning a filename so distinct mod names remain distinct.
	internal static IReadOnlyList<PlannedFile> Combine(
		IReadOnlyList<PlannedFile> files,
		IReadOnlyDictionary<string, string> owners )
	{
		var result = files.Where( file => !owners.ContainsKey( file.Path ) ).ToList();

		foreach ( bool compile in new[] { false, true } )
		{
			var groups = files.Where( file => file.Compile == compile && owners.ContainsKey( file.Path ) )
				.GroupBy( file => owners[file.Path], StringComparer.OrdinalIgnoreCase ).ToArray();

			foreach ( var group in groups )
			{
				string name = group.Key;
				string safe = new( name.Select( character => char.IsLetterOrDigit( character ) || character is '_' or '-' ? character : '_' ).Take( 80 ).ToArray() );

				if ( safe != name )
					safe += "_" + Convert.ToHexString( SHA256.HashData( Encoding.UTF8.GetBytes( name ) ) )[..8];

				result.Add( new( $"local/{safe}{( compile ? ".compiled" : "" )}.ws",
					group.SelectMany( file => file.Items ).ToArray(), compile,
					group.SelectMany( file => file.Owners ).Distinct( StringComparer.OrdinalIgnoreCase ).ToArray() ) );
			}
		}

		return result;
	}
}
