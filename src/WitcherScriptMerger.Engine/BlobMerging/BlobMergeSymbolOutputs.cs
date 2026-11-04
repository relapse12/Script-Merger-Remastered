#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using WitcherScriptMerger.FileIndex;
using WSCompiler.Core.Scripts.Standalone;
using WitcherScriptMerger.Merging;

namespace WitcherScriptMerger.BlobMerging;

// Conflict rows need to show where their symbols were written. Build the lookup using the same keys as the scan.
internal static class BlobMergeSymbolOutputs
{
	internal static Dictionary<string, string[]> Create( MergePlan plan ) => plan.Files
		.SelectMany( file => file.Items.SelectMany( Keys ).Select( symbol => (Symbol: symbol, file.Path) ) )
		.GroupBy( value => value.Symbol, StringComparer.OrdinalIgnoreCase )
		.ToDictionary(
			group => group.Key.ToUpperInvariant(),
			group => group.Select( value => value.Path ).Distinct( StringComparer.OrdinalIgnoreCase ).ToArray(),
			StringComparer.Ordinal );

	// A state's members are keyed under <machine>State<name>, and a state adds no type key of its own.
	private static IEnumerable<string> Keys( Item item )
	{
		switch ( item )
		{
			case ClassItem type:
				string scope = type.Decl.StateMachineOf is { Length: > 0 } machine ? machine + "State" + type.Decl.Name : type.Decl.Name;
				if ( type.Decl.StateMachineOf is null )
					yield return ScriptSymbolKey.Create( "Types", type.Decl.Name );
				foreach ( var member in ScopedScriptMember.FromClass( type.Decl, scope ) )
					yield return member.Key;
				foreach ( var method in type.Decl.Methods )
					yield return ScriptSymbolKey.Function( scope, method.Name );
				break;

			case FunctionItem function:
				yield return ScriptSymbolKey.Function( function.Decl.Annotation?.Class ?? "", function.Decl.Name );
				break;

			case FieldItem field:
				if ( field.Decl.Annotation?.Class is { } owner )
					foreach ( string name in field.Decl.Names )
						yield return ScriptSymbolKey.Create( "Fields", owner, name );
				break;

			case EnumItem enumeration:
				yield return ScriptSymbolKey.Create( "Enums", enumeration.Decl.Name );
				break;
		}
	}
}
