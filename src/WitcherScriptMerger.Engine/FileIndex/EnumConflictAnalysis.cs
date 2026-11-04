using System;
using System.Collections.Generic;
using System.Linq;
using WitcherScriptMerger.Inventory;
using WitcherScriptMerger.LoadOrder;

namespace WitcherScriptMerger.FileIndex;

internal sealed record EnumAddition( string Name, int CompiledValue );
internal sealed record RsBlobEnumMember( string Name, int Value );
internal sealed record RsBlobEnumDefinition( string Name, IReadOnlyList<RsBlobEnumMember> Members );

// Different compiled enum members can share a number. Priority cannot repair that collision.
internal static class EnumConflictAnalysis
{
	internal static bool HasReport( ModFile file ) => file.RelativePath.StartsWith( "Enums\\", StringComparison.OrdinalIgnoreCase )
		&& Sources( file ).Length > 1;

	internal static IReadOnlyList<EnumAddition> AddedMembers( RsBlobEnumDefinition definition,
		IReadOnlyDictionary<string, int> baseline )
	{
		var values = new Dictionary<string, int>( baseline, StringComparer.Ordinal );
		var occupied = values.Values.ToHashSet();
		var result = new List<EnumAddition>();

		foreach ( var member in definition.Members )
		{
			if ( values.ContainsKey( member.Name ) )
				continue;

			int value = member.Value;

			if ( value == int.MaxValue )
			{
				value = 0;

				while ( occupied.Contains( value ) )
					value = checked(value + 1);

				result.Add( new( member.Name, value ) );
			}

			values[member.Name] = value;
			occupied.Add( value );
		}

		return result;
	}

	internal static FileHash[] Sources( ModFile file, IEnumerable<ModLoadSetting> settings = null )
	{
		var enabled = file.Mods.Where( source => source.HasPrecompiledScriptDefinition && source.EnumAdditions != null
			&& !( settings ?? file.CurrentLoadOrder?.Mods ?? Array.Empty<ModLoadSetting>() ).Any( setting =>
				setting.ModName.Equals( source.Name, StringComparison.OrdinalIgnoreCase ) && setting.IsEnabled == false ) ).ToArray();

		return enabled.Where( left => enabled.Any( right => !left.Name.Equals( right.Name, StringComparison.OrdinalIgnoreCase )
			&& left.EnumAdditions.Any( a => right.EnumAdditions.Any( b => a.CompiledValue == b.CompiledValue && a.Name != b.Name ) ) ) )
			.ToArray();
	}

	internal static IReadOnlyList<string> Report( IEnumerable<ModFile> files ) => files
		.Where( file => file.RelativePath.StartsWith( "Enums\\", StringComparison.OrdinalIgnoreCase ) )
		.Select( file => (File: file, Sources: Sources( file )) ).Where( value => value.Sources.Length > 1 )
		.Select( value => UiText.Format( "EnumConflicts", "Compiled enum additions — {0}", value.File.RelativePath[6..] )
			+ Environment.NewLine + Environment.NewLine
			+ UiText.T(
				"EnumConflicts",
				"These mods assign the same number to different options. If they pass those options between mods, one option can be mistaken for another:" )
			+ Environment.NewLine
			+ string.Join(
				Environment.NewLine,
				value.Sources.OrderBy( source => source.Name[3..], LoadOrderComparer.GameAlphabeticalComparer )
				.Select( source => "    - " + source.Name + ": " + string.Join( ", ", source.EnumAdditions
					.Where( member => value.Sources.Any( other => other != source && other.EnumAdditions
						.Any( candidate => candidate.CompiledValue == member.CompiledValue && candidate.Name != member.Name ) ) )
					.Select( member => member.Name ) ) ) )
			+ Environment.NewLine + Environment.NewLine
			+ UiText.T( "EnumConflicts", "Priority cannot resolve it. Use compatible versions of these mods." ) )
		.ToArray();
}
