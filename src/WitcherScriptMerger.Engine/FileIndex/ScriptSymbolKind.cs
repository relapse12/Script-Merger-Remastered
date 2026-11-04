#nullable enable

using System;

namespace WitcherScriptMerger.FileIndex;

[Flags]
internal enum ScriptSymbolKind
{
	None = 0,
	EnumDefinition = 1 << 0,
	StructDefinition = 1 << 1,
	ClassFunction = 1 << 2,
	GlobalFunction = 1 << 3,
	GlobalReplacement = 1 << 4,
	ClassReplacement = 1 << 5,
	AddedFunction = 1 << 6,
	WrappedFunction = 1 << 7,
	ExternalFunction = 1 << 8,
	FieldDefinition = 1 << 9,
	FieldDefault = 1 << 10,
	AddedField = 1 << 11
}

// Priority chooses one implementation. Wrappers and added functions can coexist, so they use different conflict rules.
internal static class ScriptSymbolKinds
{
	private const ScriptSymbolKind PriorityDefinitions =
		ScriptSymbolKind.StructDefinition
		| ScriptSymbolKind.ClassFunction
		| ScriptSymbolKind.GlobalFunction
		| ScriptSymbolKind.GlobalReplacement
		| ScriptSymbolKind.ClassReplacement
		| ScriptSymbolKind.FieldDefinition | ScriptSymbolKind.FieldDefault | ScriptSymbolKind.AddedField;

	public static bool HasPriorityDefinition( this ScriptSymbolKind kinds )
	{
		return ( kinds & PriorityDefinitions ) != 0;
	}

	public static bool HasFunctionImplementation( this ScriptSymbolKind kinds ) =>
		( kinds & ( ScriptSymbolKind.ClassFunction | ScriptSymbolKind.GlobalFunction
			| ScriptSymbolKind.ClassReplacement | ScriptSymbolKind.GlobalReplacement ) ) != 0;
}
