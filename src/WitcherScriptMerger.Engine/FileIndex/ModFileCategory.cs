using System;

namespace WitcherScriptMerger.FileIndex;

public class ModFileCategory
{
	public int OrderIndex { get; private set; }

	private readonly Func<string> displayName;
	private readonly Func<string> toolTipText;
	public string DisplayName => displayName();

	public string ToolTipText => toolTipText();

	public bool IsSupported { get; private set; }

	public bool IsBundled { get; private set; }

	public ModFileCategory( int orderIndex, string displayName, string toolTipText, bool isSupported, bool isBundled )
		: this( orderIndex, () => displayName, () => toolTipText, isSupported, isBundled ) { }

	internal ModFileCategory( int orderIndex, Func<string> displayName, Func<string> toolTipText, bool isSupported, bool isBundled )
	{
		OrderIndex = orderIndex;
		this.displayName = displayName;
		this.toolTipText = toolTipText;
		IsSupported = isSupported;
		IsBundled = isBundled;
	}

	public override string ToString()
	{
		return DisplayName;
	}
}
