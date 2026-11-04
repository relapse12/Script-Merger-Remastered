using System;
using System.Collections.Generic;

namespace WitcherScriptMerger.LoadOrder;

// Orders mods the way the game loads them: by runtime group, then priority, then folder name.
internal class LoadOrderComparer : IComparer<string>, IComparer<ModLoadSetting>
{
	internal static IComparer<string> GameAlphabeticalComparer { get; } =
		Comparer<string>.Create( CompareAlphabetically );

	private readonly CustomLoadOrder order;

	public LoadOrderComparer()
	{
	}

	internal LoadOrderComparer( CustomLoadOrder order )
	{
		this.order = order;
	}

	public int Compare( string x, string y )
	{
		ModLoadSetting xSetting = order?.GetModLoadSettingByName( x );
		ModLoadSetting ySetting = order?.GetModLoadSettingByName( y );

		return CompareRuntimeOrder( x, xSetting, y, ySetting );
	}

	public int Compare( ModLoadSetting x, ModLoadSetting y )
	{
		return CompareRuntimeOrder( x.ModName, x, y.ModName, y );
	}

	// Negative when x loads ahead of y, which means x wins.
	internal static int CompareRuntimeOrder( string xName, ModLoadSetting xSetting,
		string yName, ModLoadSetting ySetting )
	{
		int groupComparison = GetRuntimeGroup( xName, xSetting ).CompareTo( GetRuntimeGroup( yName, ySetting ) );

		if ( groupComparison != 0 )
			return groupComparison;

		bool xHasPriority = xSetting?.Priority.HasValue == true;
		bool yHasPriority = ySetting?.Priority.HasValue == true;

		if ( IsModIo( xName, xSetting ) && IsModIo( yName, ySetting ) )
			return xHasPriority && yHasPriority ? xSetting.Priority.Value.CompareTo( ySetting.Priority.Value ) : 0;

		if ( xHasPriority && yHasPriority )
		{
			int priorityComparison = xSetting.Priority.Value.CompareTo( ySetting.Priority.Value );

			if ( priorityComparison != 0 )
				return priorityComparison;
		}

		return CompareAlphabetically( xName, yName );
	}

	private static int GetRuntimeGroup( string name, ModLoadSetting setting )
	{
		bool disabled = setting?.StateKnown == true && setting.IsEnabled == false;

		if ( disabled )
			return 3;

		if ( IsModIo( name, setting ) )
			return 0;

		if ( setting?.Priority.HasValue == true )
			return 1;

		return 2;
	}

	internal static bool IsModIo( string name, ModLoadSetting setting = null ) => setting?.IsExternal == true
		|| name?.Contains( "[Mod.io #", StringComparison.OrdinalIgnoreCase ) == true;

	internal static int CompareAlphabetically( string x, string y )
	{
		return StringComparer.Ordinal.Compare( x?.ToLowerInvariant(), y?.ToLowerInvariant() );
	}
}
