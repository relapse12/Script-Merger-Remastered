namespace WitcherScriptMerger.FileIndex;

internal static class Categories
{
	public static ModFileCategory Script = new ModFileCategory(
		1,
		() => UiText.T( "Categories", "Scripts" ),
		() => UiText.T( "Categories", "These plaintext .ws files can be merged" ),
		isSupported: true,
		isBundled: false );

	public static ModFileCategory Xml = new ModFileCategory(
		2,
		() => UiText.T( "Categories", "Non-Bundled XML" ),
		() => UiText.T( "Categories", "These .xml text files can be merged" ),
		isSupported: true,
		isBundled: false );

	public static ModFileCategory BundleText = new ModFileCategory(
		3,
		() => UiText.T( "Categories", "Bundled Text" ),
		() => UiText.T( "Categories", "These bundled text files can be merged" ),
		isSupported: true,
		isBundled: true );

#if RSBLOB_SEMANTIC_ANALYSIS
	public static ModFileCategory PrecompiledScript = new ModFileCategory(
		5,
		() => UiText.T( "Categories", "Precompiled Script Symbols" ),
		() => UiText.T(
			"Categories",
			"Overlapping compiled definitions. Select conventional conflicts and use Create Selected Merges to merge them. Mod.io remains read-only." ),
		isSupported: false,
		isBundled: false );
#else
	public static ModFileCategory PrecompiledScript = new ModFileCategory(5, () => UiText.T("Categories", "Precompiled Scripts - Not Mergeable"), () => UiText.T("Categories", "Semantic blob inspection is not included. Multiple precompiled.rsblob files are reported conservatively and are never merged"), isSupported: false, isBundled: false);
#endif

	public static ModFileCategory XmlDefinitionOverride = new ModFileCategory(
		6,
		() => UiText.T( "Categories", "XML Definition Overrides - Not Mergeable" ),
		() => UiText.T(
			"Categories",
			"Overlapping XML definition changes. The declared override policy controls field extension or whole-definition replacement." ),
		isSupported: false,
		isBundled: true );

	public static ModFileCategory DlcBundleResource = new ModFileCategory(
		7,
		() => UiText.T( "Categories", "DLC Bundle Resources - Not Mergeable" ),
		() => UiText.T( "Categories", "Multiple DLCs supply this resource. DLC load order selects the active version." ),
		isSupported: false,
		isBundled: true );

	public static ModFileCategory BundleNotMergeable = new ModFileCategory(
		8,
		() => UiText.T( "Categories", "Bundled Non-text - Not Mergeable" ),
		() => UiText.T( "Categories", "Right-click mods to define your load order instead of merging" ),
		isSupported: false,
		isBundled: true );

	public static ModFileCategory FlatNotMergeable = new ModFileCategory(
		9,
		() => UiText.T( "Categories", "Not Mergeable" ),
		() => UiText.T( "Categories", "Script Merger Remastered doesn't know what these files are" ),
		isSupported: false,
		isBundled: false );

	public static bool UsesDlcRuntimeOrder( ModFileCategory category )
	{
		return ReferenceEquals( category, XmlDefinitionOverride )
			|| ReferenceEquals( category, DlcBundleResource );
	}
}
