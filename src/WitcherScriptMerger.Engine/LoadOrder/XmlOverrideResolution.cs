using WitcherScriptMerger.Inventory;

namespace WitcherScriptMerger.LoadOrder;

internal enum XmlOverrideOutcome
{
	Unresolved,
	NoEnabledSource,
	SingleSource,
	RuntimeOrder,
	EquipmentVariants,
	DefaultRetained,
	VanillaDefaultRetained,
	ExistingDefinitionExtended,
	Extended,
	ExtensionBeforeDefault,
	DisabledDefaultDefinition
}

internal sealed record XmlOverrideResolution(
	bool Resolved,
	string Winner,
	XmlOverrideOutcome Outcome,
	string DefaultSource = null,
	string ExtensionSource = null );

internal sealed record XmlVariantResolution( FileHash Left, FileHash Right, FileHash Winner, bool FixedOrder );
