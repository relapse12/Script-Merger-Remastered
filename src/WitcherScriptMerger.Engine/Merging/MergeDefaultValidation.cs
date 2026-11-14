#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using WSCompiler.Core;
using WSCompiler.Core.Scripts;
using WSCompiler.Core.Scripts.Standalone;
using WSCompiler.Recovery;

namespace WitcherScriptMerger.Merging;

// Check the written defaults against the plan after conflict choices and file edits have been applied.
internal static class MergeDefaultValidation
{
	internal static void Verify( MergePlan plan, string directory, string looseName, string compiledName )
	{
		Dictionary<string, Dictionary<string, string>>? compiled = null;
		foreach ( var file in plan.Files )
		{
			var expected = file.Items.OfType<ClassItem>().Select( item => item.Decl ).ToArray();
			if ( expected.Length == 0 )
				continue;

			string path = file.Compile ? compiledName + "/content/precompiled.rsblob"
				: looseName + "/content/scripts/" + file.Path.Replace( '\\', '/' );
			Dictionary<string, Dictionary<string, string>> actual;
			if ( file.Compile )
			{
				if ( compiled is null )
				{
					var blob = ScriptBlob.Open( MergeBuilder.SafePath( directory, path ) );
					compiled = blob.Classes.ToDictionary( type => type.IsState ? Symbols.StateClassKey( type.StateOf, type.Name ) : type.Name,
						type => BinaryValues( type.Defaults ), StringComparer.Ordinal );
					foreach ( var type in blob.Structs )
						compiled.Add( type.Name, BinaryValues( type.Defaults ) );
				}
				actual = compiled;
			}
			else
			{
				string source = TextDecoding.Decode( File.ReadAllBytes( MergeBuilder.SafePath( directory, path ) ) );
				actual = Parser.ParseSource( source, false ).OfType<ClassItem>()
					.ToDictionary( item => Symbols.ClassKey( item.Decl ), item => SourceValues( item.Decl ), StringComparer.Ordinal );
			}

			foreach ( var type in expected )
			{
				string owner = Symbols.ClassKey( type );
				var intended = file.Compile ? CompiledValues( type ) : SourceValues( type );
				actual.TryGetValue( owner, out var written );
				foreach ( string field in intended.Keys.Concat( written?.Keys ?? Enumerable.Empty<string>() ).Distinct( StringComparer.Ordinal ) )
					if ( written is null || !intended.TryGetValue( field, out string? value )
						|| !written.TryGetValue( field, out string? result ) || value != result )
						throw new InvalidDataException( $"The merged output changed or omitted the starting value of {owner}.{field}: {path}" );
			}
		}
	}

	private static Dictionary<string, string> SourceValues( ClassDecl type ) => type.Defaults
		.GroupBy( value => value.Field, StringComparer.Ordinal )
		.ToDictionary( group => group.Key, group => SourceWriter.Expression( group.Last().Value ), StringComparer.Ordinal );

	private static Dictionary<string, string> BinaryValues( IEnumerable<ScriptDefault> values ) => values
		.ToDictionary( value => value.Property, value => JsonSerializer.Serialize( new ScriptDefaultNode( "", value.Value, value.Children ) ), StringComparer.Ordinal );

	private static Dictionary<string, string> CompiledValues( ClassDecl type )
	{
		var values = BinaryValues( type.BinaryDefaults );
		foreach ( var assignment in type.Defaults )
			values[assignment.Field] = JsonSerializer.Serialize( DefaultValues.ToNode( assignment.Value ) );
		return values;
	}
}
