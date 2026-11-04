using System.Xml.Serialization;
using WitcherScriptMerger.FileIndex;
using WitcherScriptMerger.LoadOrder;

namespace WitcherScriptMerger.Inventory;

// Saved as IncludedMod in the inventory. Scans also attach temporary symbol and conflict details.
[XmlRoot]
public class FileHash
{
	internal FileHash Copy() => (FileHash)MemberwiseClone();

	[XmlAttribute]
	public string Hash { get; set; }

	// Older records use ModFile.BundleName when this is absent.
	[XmlAttribute]
	public string BundleName { get; set; }

	// Mod.io sources can live outside the game's Mods folder.
	[XmlAttribute]
	public string SourceDirectory { get; set; }

	[XmlAttribute]
	public string SourceFilePath { get; set; }

	[XmlAttribute]
	public string Origin { get; set; }

	[XmlAttribute]
	public long ModIoId { get; set; }

	[XmlText]
	public string Name { get; set; }

	[XmlIgnore]
	public bool IsOutdated { get; set; }

	[XmlIgnore]
	public bool IsExternal => !string.IsNullOrWhiteSpace( SourceDirectory );

	// A conventional mod's setting must not disable a Mod.io source with the same name.
	internal bool IsDisabledBy( ModLoadSetting setting )
	{
		return setting is { StateKnown: true, IsEnabled: false } && ( !IsExternal || setting.IsExternal );
	}

	[XmlIgnore]
	public string RuntimeOrderName { get; set; }

	[XmlIgnore]
	public string ConflictDetail { get; set; }

	[XmlIgnore]
	internal XmlOverrideTooltipInfo XmlOverrideInfo { get; set; }

	[XmlIgnore]
	internal ScriptSymbolKind LooseScriptKinds { get; set; }

	[XmlIgnore]
	internal ScopedScriptMember ScopedMember { get; set; }

	[XmlIgnore]
	internal string ScopedMemberType { get; set; }

	[XmlIgnore]
	public bool HasLooseScriptDefinition => LooseScriptKinds != ScriptSymbolKind.None;

	[XmlIgnore]
	public bool HasLoosePriorityConflictDefinition => LooseScriptKinds.HasPriorityDefinition();

	[XmlIgnore]
	internal bool IsLooseScriptVanillaEquivalent { get; set; }

	[XmlIgnore]
	internal ScriptSymbolKind PrecompiledScriptKinds { get; set; }

	[XmlIgnore]
	internal System.Collections.Generic.IReadOnlyList<EnumAddition> EnumAdditions { get; set; }

	[XmlIgnore]
	public bool HasPrecompiledScriptDefinition => PrecompiledScriptKinds != ScriptSymbolKind.None;

	[XmlIgnore]
	public bool HasPrecompiledPriorityConflictDefinition => PrecompiledScriptKinds.HasPriorityDefinition();
}
