using System;
using System.Collections.Generic;
using System.Linq;
using WitcherScriptMerger.Inventory;
using WitcherScriptMerger.LoadOrder;

namespace WitcherScriptMerger.FileIndex;

// Changing priority to win one resource can lose another. Show that trade-off before the user changes the order.
internal sealed class PriorityTradeoffToolTips
{
	private sealed record Asset( ModFile File, FileHash[] Sources, FileHash Winner );

	private sealed record Link( string ModName, string OtherMod, string RelatedPath, bool IsWrapperAsset = false );

	private readonly Dictionary<ModFile, Dictionary<string, List<Link>>> links = new();
	internal static PriorityTradeoffToolTips Empty { get; } = new();

	// A trade-off is actionable only when both mods belong to an order the user can reverse.
	internal static PriorityTradeoffToolTips Build(
		IEnumerable<ModFile> files,
		IEnumerable<ModLoadSetting> settings,
		bool orderKnown = true )
	{
		var result = new PriorityTradeoffToolTips();

		if ( !orderKnown )
			return result;

		ModFile[] inputs = files?.Where( file => file != null ).Distinct().ToArray() ?? Array.Empty<ModFile>();
		var byName = ( settings ?? Array.Empty<ModLoadSetting>() ).Where( setting => setting != null && !string.IsNullOrWhiteSpace( setting.ModName ) )
			.GroupBy( setting => setting.ModName, StringComparer.OrdinalIgnoreCase )
			.ToDictionary( group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase );

		ModLoadSetting Setting( FileHash source ) => byName.TryGetValue( source.Name, out var setting ) ? setting : null;

		bool PriorityLinkedSource( FileHash source ) => source != null && source.Name?.StartsWith( "mod", StringComparison.OrdinalIgnoreCase ) == true
			&& source.IsExternal == ( Setting( source )?.IsExternal == true )
			&& ( string.IsNullOrEmpty( source.RuntimeOrderName ) || source.RuntimeOrderName.EqualsIgnoreCase( source.Name ) );

		bool Enabled( FileHash source ) => Setting( source ) is not { } setting || setting.StateKnown && setting.IsEnabled == true;

		int? Compare( FileHash left, FileHash right ) =>
			PrecompiledConflictAnalysis.CompareKnownOrder( left, Setting( left ), right, Setting( right ) );

		var assetsByMod = new Dictionary<string, List<Asset>>( StringComparer.OrdinalIgnoreCase );

		foreach ( ModFile file in inputs.Where( file => ReferenceEquals( file.Category, Categories.BundleNotMergeable ) && file.IsBundleContent ) )
		{
			if ( file.Mods.Any( source => !PriorityLinkedSource( source ) || Setting( source ) is { StateKnown: false } ) )
				continue;

			FileHash[] active = file.Mods.Where( Enabled ).GroupBy( source => source.Name, StringComparer.OrdinalIgnoreCase )
				.Select( group => group.First() ).ToArray();

			if ( active.Length < 2 )
				continue;

			FileHash winner = active.FirstOrDefault( candidate => active.All( other => ReferenceEquals( candidate, other ) || Compare( candidate, other ) < 0 ) );

			if ( winner == null )
				continue;

			var asset = new Asset( file, active, winner );

			foreach ( FileHash source in active )
			{
				if ( !assetsByMod.TryGetValue( source.Name, out var shared ) )
					assetsByMod.Add( source.Name, shared = new() );

				shared.Add( asset );
			}
		}

		foreach ( ModFile symbol in inputs.Where( file => ReferenceEquals( file.Category, Categories.PrecompiledScript ) && file.Mods.Count > 1 ) )
		{
			if ( symbol.Mods.Any( source => source == null || string.IsNullOrWhiteSpace( source.Name ) ) )
				continue;

			var analysis = new PrecompiledConflictAnalysis( symbol.Mods, byName.Values );
			FileHash body = analysis.WinningBody;

			if ( body == null || !PriorityLinkedSource( body ) )
				continue;

			foreach ( FileHash wrapper in analysis.CompatibleBlobWrappers.Concat( analysis.BlockedBlobWrappers ) )
			{
				if ( wrapper.HasPrecompiledPriorityConflictDefinition || !PriorityLinkedSource( wrapper )
					|| PrecompiledConflictAnalysis.IsModIoSource( body, Setting( body ) ) != PrecompiledConflictAnalysis.IsModIoSource( wrapper, Setting( wrapper ) )
					|| !assetsByMod.TryGetValue( wrapper.Name, out var assets ) )
					continue;

				bool compatible = analysis.CompatibleBlobWrappers.Contains( wrapper );

				foreach ( Asset asset in assets )
				{
					FileHash bodyAsset = asset.Sources.FirstOrDefault( source => source.Name.EqualsIgnoreCase( body.Name ) );

					if ( bodyAsset == null )
						continue;

					FileHash wrapperAsset = asset.Sources.First( source => source.Name.EqualsIgnoreCase( wrapper.Name ) );

					if ( !string.IsNullOrEmpty( bodyAsset.Hash ) && bodyAsset.Hash.Equals( wrapperAsset.Hash, StringComparison.OrdinalIgnoreCase ) )
						continue;

					if ( !( compatible && asset.Winner.Name.EqualsIgnoreCase( wrapper.Name )
						|| !compatible && asset.Winner.Name.EqualsIgnoreCase( body.Name ) ) )
						continue;

					result.Add( asset.File, bodyAsset.Name, new( bodyAsset.Name, wrapperAsset.Name, FunctionName( symbol.RelativePath ) ) );
					result.Add(
						asset.File,
						wrapperAsset.Name,
						new( wrapperAsset.Name, bodyAsset.Name, FunctionName( symbol.RelativePath ), IsWrapperAsset: true ) );

					if ( !compatible )
						result.Add( symbol, wrapper.Name, new( wrapper.Name, body.Name, asset.File.RelativePath ) );
				}
			}
		}

		return result;
	}

	private static string FunctionName( string path )
	{
		string normalized = path.Replace( '/', '\\' );

		return normalized.StartsWith( "Functions\\", StringComparison.OrdinalIgnoreCase ) ? normalized[10..].Replace( '\\', '.' ) : normalized;
	}

	private void Add( ModFile file, string sourceName, Link link )
	{
		if ( !links.TryGetValue( file, out var sources ) )
			links.Add( file, sources = new( StringComparer.OrdinalIgnoreCase ) );

		if ( !sources.TryGetValue( sourceName, out var notes ) )
			sources.Add( sourceName, notes = new() );

		notes.Add( link );
	}

	internal string Describe( ModFile file, FileHash source )
	{
		if ( file == null || source == null || string.IsNullOrEmpty( source.Name )
			|| !links.TryGetValue( file, out var sources ) || !sources.TryGetValue( source.Name, out var notes ) )
			return string.Empty;

		var paragraphs = new List<string>();

		foreach ( var group in notes.GroupBy( note => note.OtherMod, StringComparer.OrdinalIgnoreCase ).OrderBy( group => group.Key, LoadOrderComparer.GameAlphabeticalComparer ) )
			foreach ( var role in group.GroupBy( note => note.IsWrapperAsset ).OrderBy( role => role.Key ) )
			{
				string mod = role.First().ModName;
				string[] related = role.Select( note => note.RelatedPath ).Distinct( StringComparer.OrdinalIgnoreCase ).OrderBy( path => path, StringComparer.OrdinalIgnoreCase ).ToArray();

				if ( ReferenceEquals( file.Category, Categories.PrecompiledScript ) )
					paragraphs.Add( related.Length == 1
						? UiText.Format(
							"PriorityTradeoffs",
							"Giving {0} higher priority than {1} restores this wrapper, but selects its version of {2} instead of {1}'s.",
							mod,
							group.Key,
							related[0] )
						: UiText.Format(
							"PriorityTradeoffs",
							"Giving {0} higher priority than {1} restores this wrapper, but selects its versions of these files instead of {1}'s:",
							mod,
							group.Key )
							+ Environment.NewLine + IndentedList( related ) );
				else if ( role.Key )
					paragraphs.Add( related.Length == 1
						? UiText.Format(
							"PriorityTradeoffs",
							"Giving {0} higher priority than {1} selects its version of this file and allows its {2} wrapper to run.",
							mod,
							group.Key,
							related[0] )
						: UiText.Format(
							"PriorityTradeoffs",
							"Giving {0} higher priority than {1} selects its version of this file and allows these wrappers to run:",
							mod,
							group.Key )
							+ Environment.NewLine + IndentedList( related ) );
				else
					paragraphs.Add( related.Length == 1
						? UiText.Format(
							"PriorityTradeoffs",
							"Giving {0} higher priority than {1} selects its version of this file, but skips {1}'s {2} wrapper.",
							mod,
							group.Key,
							related[0] )
						: UiText.Format(
							"PriorityTradeoffs",
							"Giving {0} higher priority than {1} selects its version of this file, but skips these wrappers from {1}:",
							mod,
							group.Key )
							+ Environment.NewLine + IndentedList( related ) );
			}

		return string.Join( Environment.NewLine + Environment.NewLine, paragraphs );
	}

	private static string IndentedList( IEnumerable<string> values ) => string.Join( Environment.NewLine, values.Select( value => "    - " + value ) );
}
