using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using WitcherScriptMerger.Inventory;
using WitcherScriptMerger.LoadOrder;

namespace WitcherScriptMerger.FileIndex;

// A conflict can represent a whole file or one script symbol. Keep all providers here and decide
// whether they conflict using the current load order, so priority changes do not require another scan.
public class ModFile
{
	[XmlIgnore]
	internal string VanillaScriptFile { get; set; }
	[XmlElement]
	public string RelativePath { get; set; }

	[XmlElement( "IncludedMod" )]
	public List<FileHash> Mods { get; private set; }

	[XmlElement]
	public string BundleName { get; set; }

	[XmlIgnore]
	public virtual ModFileCategory Category
	{
		get
		{
			if ( IndexedCategoryOverride != null )
			{
				return IndexedCategoryOverride;
			}

			if ( BundleName == null )
			{
				if ( !IsScript( RelativePath ) )
				{
					if ( !IsXml( RelativePath ) )
					{
						return Categories.FlatNotMergeable;
					}

					return Categories.Xml;
				}

				return Categories.Script;
			}

			if ( !IsTextFile( RelativePath ) )
			{
				return Categories.BundleNotMergeable;
			}

			return Categories.BundleText;
		}
	}

	[XmlIgnore]
	public bool IsBundleContent => BundleName != null;

	[XmlIgnore]
	internal ModFileCategory IndexedCategoryOverride { get; set; }

	[XmlIgnore]
	internal WitcherScriptMerger.BlobMerging.BlobMergePreflight MergePreflight { get; set; }

	// Read the window's current order when needed. Without it, priority-dependent answers remain unknown.
	[XmlIgnore]
	internal Func<CustomLoadOrder> LoadOrderSource { get; set; }

	internal CustomLoadOrder CurrentLoadOrder => LoadOrderSource?.Invoke();

	// Keep the complete source list so re-enabled mods can return without a rescan.
	// If settings cannot be read, retain the sources until their enabled state is known.
	[XmlIgnore]
	internal IReadOnlyList<FileHash> ParticipatingSources
	{
		get
		{
			CustomLoadOrder order = CurrentLoadOrder;

			if ( order?.IsValid != true )
				return Mods;

			return Mods.Where( source => !source.IsDisabledBy( order.GetModLoadSettingByName( source.Name ) ) ).ToArray();
		}
	}

	[XmlIgnore]
	public bool HasConflict
	{
		get
		{
			CustomLoadOrder order = CurrentLoadOrder;

			if ( ReferenceEquals( Category, Categories.XmlDefinitionOverride ) && order?.IsValid != false
				&& ( CustomLoadOrder.ResolveXmlOverridePolicyForSettings( order?.Mods, this ).Outcome
						is XmlOverrideOutcome.ExtensionBeforeDefault or XmlOverrideOutcome.DisabledDefaultDefinition
					|| HasNoActiveVariantPair( order ) ) )
			{
				return false;
			}

			if ( !ReferenceEquals( Category, Categories.PrecompiledScript ) )
			{
				return Mods.Count > 1 && ParticipatingSources.Count > 1;
			}

#if RSBLOB_SEMANTIC_ANALYSIS
			if ( Mods.Count < 2 )
				return false;

			if ( IsScopedMember )
				return ScopedMemberConflict;

			if ( RelativePath.StartsWith( "Enums\\", StringComparison.OrdinalIgnoreCase ) )
				return EnumConflictAnalysis.Sources( this ).Length > 1;

			return HasPrecompiledPriorityConflict || HasBlobLooseConflict || GetPrecompiledAnalysis().HasWrapperConflict;
#else
			return Mods.Count > 1;
#endif
		}
	}

	// Only overlapping pairs compete. Several enabled variants can coexist if every conflicting pair has a disabled side.
	private bool HasNoActiveVariantPair( CustomLoadOrder order )
	{
		return Mods.FirstOrDefault()?.XmlOverrideInfo?.VariantCompetitions is { Count: > 0 }
			&& CustomLoadOrder.ResolveXmlVariantCompetitionsForSettings( order?.Mods, this ) is { Count: 0 };
	}

	[XmlIgnore]
	internal bool IsScopedMember => RelativePath.StartsWith( "Fields\\", StringComparison.OrdinalIgnoreCase )
		|| RelativePath.StartsWith( "Defaults\\", StringComparison.OrdinalIgnoreCase );

	[XmlIgnore]
	internal bool ScopedMemberConflict
	{
		get
		{
			var sources = ScopedMemberSources();

			return sources.Any( source => source.HasPrecompiledScriptDefinition ) && sources.Length > 1
				&& ( sources.Any( source => source.ScopedMember?.IsAnnotation == true )
					|| sources.Select( source => source.ScopedMember?.Fingerprint( source.ScopedMemberType )
						?? source.Hash ?? source.Name ).Distinct( StringComparer.Ordinal ).Count() > 1 );
		}
	}

	internal FileHash[] ScopedMemberSources() => GetPrecompiledAnalysis().EnabledSources
		.Where( source => source.HasPrecompiledScriptDefinition || !source.IsLooseScriptVanillaEquivalent ).ToArray();

	[XmlIgnore]
	internal bool HasBlobLooseConflict
	{
		get
		{
			if ( IsScopedMember )
				return ScopedMemberConflict && ScopedMemberSources().Any( source => !source.HasPrecompiledScriptDefinition );

			var analysis = GetPrecompiledAnalysis();
			FileHash[] effective = analysis.ConflictSources;

			return effective.Any( source => source.HasPrecompiledPriorityConflictDefinition )
				&& effective.Any( source => !source.HasPrecompiledScriptDefinition
					&& ( ( source.HasLoosePriorityConflictDefinition && !source.IsLooseScriptVanillaEquivalent )
						|| analysis.BlockedLooseWrappers.Contains( source ) ) );
		}
	}

	[XmlIgnore]
	internal bool HasBlockedLooseWrapperConflict
	{
		get
		{
			return GetPrecompiledAnalysis().BlockedLooseWrappers.Length > 0;
		}
	}

	internal PrecompiledConflictAnalysis GetPrecompiledAnalysis( IEnumerable<ModLoadSetting> settings = null )
		=> new( Mods, settings ?? CurrentLoadOrder?.Mods, settings != null || CurrentLoadOrder?.IsValid != false );

	internal string GetSemanticConflictDescription()
	{
		if ( ReferenceEquals( Category, Categories.PrecompiledScript ) )
		{
			if ( RelativePath.StartsWith( "Enums\\", StringComparison.OrdinalIgnoreCase ) )
				return UiText.T(
					"EnumConflicts",
					"These mods use the same number for different options. One mod can mistake another mod's option for its own. Changing priority cannot fix this. See Diagnostics for the affected mods." );

			if ( IsScopedMember )
				return UiText.T(
					"ScopedMembers",
					"These mods give the same script field different definitions or starting values. See Diagnostics for merge options." );

			var analysis = GetPrecompiledAnalysis();

			if ( analysis.HasWrapperConflict )
				return analysis.Describe();

			if ( HasPrecompiledPriorityConflict && HasBlobLooseConflict )
				return UiText.T(
					"ModFile",
					"Multiple compiled scripts (.rsblob) and a loose script (.ws) provide this definition.\nLoad order selects a compiled version, which overrides the loose-script changes. See Diagnostics for guidance." );

			if ( HasPrecompiledPriorityConflict )
				return UiText.T(
					"ModFile",
					"Multiple compiled scripts (.rsblob) provide this definition.\nLoad order selects one version. See Diagnostics for guidance." );

			if ( HasBlobLooseConflict )
				return RelativePath.StartsWith( "Functions\\", StringComparison.OrdinalIgnoreCase )
					? UiText.T(
						"ModFile",
						"The compiled script overrides these loose-script changes.\nChanging priority or merging loose scripts alone will not restore them. See Diagnostics for merge options." )
					: UiText.T(
						"ModFile",
						"The compiled definition overrides the loose definition.\nChanging priority or merging loose scripts alone will not restore it. See Diagnostics for merge options." );

#if RSBLOB_SEMANTIC_ANALYSIS
			return string.Empty;
#else
			return Category.ToolTipText;
#endif
		}

		if ( ReferenceEquals( Category, Categories.XmlDefinitionOverride ) )
		{
			return XmlOverrideToolTips.DescribeConflict( this );
		}

		if ( ReferenceEquals( Category, Categories.Script ) )
			return UiText.T(
				"ModFile",
				"These mods provide different versions of the same script file.\nMerge the versions to combine their changes." );

		if ( ReferenceEquals( Category, Categories.Xml ) )
			return UiText.T(
				"ModFile",
				"These mods provide different versions of the same XML file.\nMerge the versions to combine their changes." );

		if ( ReferenceEquals( Category, Categories.BundleText ) )
			return UiText.T(
				"ModFile",
				"These mods provide different versions of the same bundled text file.\nMerge the versions to combine their changes." );

		if ( ReferenceEquals( Category, Categories.DlcBundleResource ) )
			return UiText.T(
				"ModFile",
				"These DLCs supply the same resource.\nDLC load order selects the active version; the resource cannot be merged." );

		if ( ReferenceEquals( Category, Categories.BundleNotMergeable ) )
			return UiText.T(
				"ModFile",
				"These mods supply the same non-text resource.\nLoad order selects the active version; the resource cannot be merged." );

		if ( ReferenceEquals( Category, Categories.FlatNotMergeable ) )
			return UiText.T( "ModFile", "These mods supply the same loose file.\nScript Merger does not support merging this file type." );

		return Category.ToolTipText;
	}

	[XmlIgnore]
	internal bool HasPrecompiledPriorityConflict
	{
		get
		{
			if ( IsScopedMember )
				return ScopedMemberConflict && ScopedMemberSources().Count( source => source.HasPrecompiledScriptDefinition ) > 1;

			return GetPrecompiledAnalysis().ConflictSources.Count( source => source.HasPrecompiledScriptDefinition
				&& source.HasPrecompiledPriorityConflictDefinition ) > 1;
		}
	}

	internal IReadOnlyList<FileHash> GetConflictDisplaySources()
	{
#if RSBLOB_SEMANTIC_ANALYSIS
		if ( ReferenceEquals( Category, Categories.PrecompiledScript ) )
		{
			if ( RelativePath.StartsWith( "Enums\\", StringComparison.OrdinalIgnoreCase ) )
				return EnumConflictAnalysis.Sources( this );

			if ( IsScopedMember )
				return ScopedMemberConflict ? ScopedMemberSources() : Array.Empty<FileHash>();

			bool includeLoose = HasBlobLooseConflict;
			bool includePrecompiled = includeLoose || HasPrecompiledPriorityConflict;
			var analysis = GetPrecompiledAnalysis();
			FileHash[] effective = analysis.ConflictSources;

			return effective
				.Where( source => !analysis.CompatibleBlobWrappers.Contains( source ) )
				.Where( source => ( ( includePrecompiled || analysis.HasWrapperConflict ) && source.HasPrecompiledPriorityConflictDefinition )
					|| ( ( includePrecompiled || analysis.HasWrapperConflict )
						&& ( source.PrecompiledScriptKinds & ScriptSymbolKind.WrappedFunction ) != 0 )
					|| ( includeLoose && !source.HasPrecompiledScriptDefinition
						&& ( ( source.HasLoosePriorityConflictDefinition && !source.IsLooseScriptVanillaEquivalent )
							|| analysis.BlockedLooseWrappers.Contains( source ) ) ) )
				.ToArray();
		}

#endif
		return ParticipatingSources;
	}

	public ModFile( string relPath, string bundlePath = null )
	{
		RelativePath = relPath;
		Mods = new List<FileHash>();

		if ( bundlePath != null )
		{
			BundleName = Path.GetFileName( bundlePath );
		}
	}

	public ModFile()
	{
		Mods = new List<FileHash>();
	}

	public bool ContainsMod( string modName )
	{
		return Mods.Any( ( FileHash mod ) => mod.Name.EqualsIgnoreCase( modName ) );
	}

	public string GetVanillaFile( GamePaths game )
	{
		if ( Category == Categories.PrecompiledScript )
			return VanillaScriptFile ?? "";

		if ( Category != Categories.Script )
		{
			if ( Category != Categories.Xml )
			{
				throw new Exception( UiText.Format( "ModFile", "Can't get vanilla file for category '{0}'.", Category.DisplayName ) );
			}

			return Path.Combine( game.GameDirectory, RelativePath );
		}

		string vanilla = Path.Combine( game.ScriptsDirectory, RelativePath );

		return File.Exists( vanilla ) ? vanilla :
			WitcherScriptMerger.GameContent.OfficialGameContent.FindDlcLooseFile(
				game.GameDirectory,
				Path.Combine( "content", "scripts", RelativePath ) ) ?? vanilla;
	}

	public string GetModFile( string modName, GamePaths game )
	{
		FileHash source = Mods.FirstOrDefault( mod => mod.Name.EqualsIgnoreCase( modName ) );

		if ( !string.IsNullOrWhiteSpace( source?.SourceFilePath ) )
		{
			return source.SourceFilePath;
		}

		string sourceDirectory = !string.IsNullOrWhiteSpace( source?.SourceDirectory )
			? source.SourceDirectory
			: Path.Combine( game.ModsDirectory, modName );

		if ( Category == Categories.FlatNotMergeable )
		{
			return Path.Combine( sourceDirectory, RelativePath );
		}

		if ( Category != Categories.Script )
		{
			if ( Category != Categories.Xml )
			{
				if ( !Category.IsBundled )
				{
					throw new NotImplementedException();
				}

				string sourceBundleName = GetBundleName( modName );

				if ( string.IsNullOrWhiteSpace( sourceBundleName ) )
				{
					throw new InvalidOperationException( UiText.F( "ModFile", $"No bundle was recorded for mod '{modName}' and resource '{RelativePath}'." ) );
				}

				return Path.Combine( sourceDirectory, GamePaths.BundleBase, sourceBundleName );
			}

			return Path.Combine( sourceDirectory, RelativePath );
		}

		return Path.Combine( sourceDirectory, GamePaths.ModScriptBase, RelativePath );
	}

	public string GetBundleName( string modName )
	{
		string sourceBundleName = Mods.FirstOrDefault( mod => mod.Name.EqualsIgnoreCase( modName ) )?.BundleName;

		return string.IsNullOrWhiteSpace( sourceBundleName ) ? BundleName : sourceBundleName;
	}

	public static string GetModNameFromPath( string modFilePath, GamePaths game )
	{
		string modsDirectory = game.ModsDirectory;

		if ( !modFilePath.StartsWithIgnoreCase( modsDirectory ) )
		{
			return GamePaths.MergedBundleContent;
		}

		int num = modsDirectory.Length + 1;
		string name = modFilePath.Substring( num, modFilePath.Length - num );

		return name.Substring( 0, name.IndexOf( '\\' ) );
	}

	public static bool IsScript( string path )
	{
		return path.EndsWithIgnoreCase( ".ws" );
	}

	public static bool IsXml( string path )
	{
		return path.EndsWithIgnoreCase( ".xml" );
	}

	public static bool IsBundle( string path )
	{
		return path.EndsWithIgnoreCase( ".bundle" );
	}

	public static bool IsRsBlob( string path )
	{
		return path.EndsWithIgnoreCase( ".rsblob" );
	}

	public static bool IsPrecompiledScriptSymbol( string relativePath )
	{
		return relativePath.StartsWith( "Enums\\", StringComparison.OrdinalIgnoreCase )
			|| relativePath.StartsWith( "Fields\\", StringComparison.OrdinalIgnoreCase )
			|| relativePath.StartsWith( "Defaults\\", StringComparison.OrdinalIgnoreCase )
			|| relativePath.StartsWith( "Types\\", StringComparison.OrdinalIgnoreCase )
			|| relativePath.StartsWith( "Functions\\", StringComparison.OrdinalIgnoreCase );
	}

	public static bool IsTextFile( string path )
	{
		if ( !path.EndsWithIgnoreCase( ".ws" ) && !path.EndsWithIgnoreCase( ".xml" ) && !path.EndsWithIgnoreCase( ".txt" ) )
		{
			return path.EndsWithIgnoreCase( ".csv" );
		}

		return true;
	}

	public override string ToString()
	{
		return Mods.Count == 1 ? UiText.F( "ModFile", $"({Mods.Count} mod) {RelativePath}" ) : UiText.F( "ModFile", $"({Mods.Count} mods) {RelativePath}" );
	}
}
