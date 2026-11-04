#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using WSCompiler.Core.Scripts;
using WSCompiler.Core.Scripts.Standalone;


namespace WitcherScriptMerger.Merging;

public sealed class EnumRenumberingException( string enumName ) : InvalidOperationException(
	"Merging " + enumName + " would change compiled enum values. Enum renumbering is not supported." )
{
	public string EnumName { get; } = enumName;
}

// Compiled mods use enum numbers directly. Merging must preserve those values even when numbering is implicit.
internal static class EnumMergePolicy
{
	// Compare the blob's values and the merged values against the same reference definitions.
	internal static void Validate(
		IReadOnlyList<MergeModule> modules,
		IReadOnlySet<string> included,
		IReadOnlyList<SourceFile> vanilla,
		IReadOnlyList<PlannedFile> output )
	{
		var originals = modules.Where( module => module.IsBlob && included.Contains( module.Name ) )
			.Select( module => (module.Name, Enums: module.Files.SelectMany( file => file.Items ).OfType<EnumItem>()
				.Select( item => item.Decl ).ToArray()) ).Where( module => module.Enums.Length > 0 ).ToArray();

		if ( originals.Length == 0 )
			return;

		var references = vanilla.SelectMany( file => file.Items ).OfType<EnumItem>().Select( item => item.Decl )
			.Concat( modules.SelectMany( module => module.Files ).SelectMany( file => file.Items ).OfType<EnumItem>()
				.Select( item => item.Decl ).Where( declaration => declaration.Members.All( member => member.Value.HasValue ) ) ).ToArray();
		var combined = EnumResolver.ReadEnums( references.Concat( output.SelectMany( file => file.Items ).OfType<EnumItem>().Select( item => item.Decl ) ) );

		foreach ( var original in originals )
		{
			var previous = EnumResolver.ReadEnums( references.Concat( original.Enums ) );

			foreach ( var declaration in original.Enums )
			{
				var before = previous[declaration.Name].ToDictionary( pair => pair.Value, pair => pair.Key, StringComparer.Ordinal );
				var after = combined.GetValueOrDefault( declaration.Name )?.ToDictionary( pair => pair.Value, pair => pair.Key, StringComparer.Ordinal );

				foreach ( var member in declaration.Members.Where( member => member.Value is null ) )
					if ( !before.TryGetValue( member.Name, out long oldValue ) || after is null
						|| !after.TryGetValue( member.Name, out long newValue ) || oldValue != newValue )
						throw new EnumRenumberingException( declaration.Name );
			}
		}
	}
}
