namespace WitcherScriptMerger.LoadOrder;

// Preserve Vortex's VK value when saving. A null IsEnabled leaves enablement unspecified.
internal class ModLoadSetting
{
	public string ModName { get; set; }

	public string VK { get; set; }

	public bool? IsEnabled { get; set; }

	public int? Priority { get; set; }

	public bool IsExternal { get; set; }

	public bool StateKnown { get; set; } = true;

	public string Origin { get; set; }

	public long ModIoId { get; set; }

	public ModLoadSetting()
	{
	}

	public ModLoadSetting( string modName )
	{
		ModName = modName;
	}

	public override string ToString()
	{
		return UiText.F(
			"ModLoadSetting",
			$"{ModName}, priority {Priority}, {( ( !IsEnabled.HasValue || IsEnabled.Value ) ? UiText.T( "ModLoadSetting", "enabled" ) : UiText.T( "ModLoadSetting", "disabled" ) )}" );
	}
}
