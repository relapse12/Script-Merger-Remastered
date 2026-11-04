using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace WitcherScriptMerger;

internal sealed record ShortcutOperation( string Id, string Caption, Keys Default, string Menu = null );

// The menu labels and keyboard handling use the same shortcut definitions.
internal static class KeyboardShortcuts
{
	internal static readonly ShortcutOperation[] Operations = [
		new("SearchMods", "Search by mod name", Keys.Control | Keys.F),
		new("SearchFiles", "Search by filename", Keys.Control | Keys.Shift | Keys.F),
		new("RefreshConflicts", "Refresh conflicts", Keys.F5),
		new("RefreshMerges", "Refresh merges", Keys.F6),
		new("CreateSelected", "Create selected merges", Keys.C),
		new("CreateScripts", "Create all loose-script merges", Keys.A),
		new("DeleteSelected", "Delete selected merges", Keys.D),
		new("DeleteAll", "Delete all merges", Keys.E),
		new("Diagnostics", "Show diagnostics", Keys.None),
		new("InspectAll", "Inspect All", Keys.None, "mnInspectAll"),
		new("Options", "Options", Keys.None, "mnOptions"),
		new("Dependencies", "Dependency locations", Keys.None, "mnDependencies"),
		new("Export", "Export conflicts", Keys.None, "mnExportConflicts"),
		new("IgnoreRules", "Permanent ignore rules", Keys.None, "mnPermanentIgnoreRules"),
		new("RestoreIgnored", "Restore temporarily ignored conflicts", Keys.None, "mnRestoreIgnoredConflicts"),
		new("ClearCaches", "Clear memory caches", Keys.None, "mnClearMemoryCaches"),
		new("OpenLoadOrder", "Open load order file", Keys.None, "mnOpenLoadOrderFile"),
		new("OpenMerged", "Open merged mod folders", Keys.None, "mnOpenMergedModDir"),
		new("OpenBundle", "Open merged bundle content", Keys.None, "mnOpenBundleContentDir"),
		new("OpenCompiled", "Locate merged compiled scripts", Keys.None, "mnOpenCompiledMerge"),
		new("CheckUpdates", "Check for app updates", Keys.None, "mnCheckForUpdates"),
		new("Shortcuts", "Keyboard shortcuts", Keys.None, "mnKeyboardShortcuts"),
		new("ExitLaunch", "Exit and launch Remastered", Keys.None, "mnExitAndPlayDX12"),
		new("Exit", "Exit", Keys.Escape, "mnExit")
	];

	internal static string Caption( ShortcutOperation operation ) => UiText.T( "KeyboardShortcuts", operation.Caption );

	internal static string Display( Keys keys ) => keys == Keys.None ? UiText.T( "KeyboardShortcuts", "Unassigned" )
		: new KeysConverter().ConvertToString( keys );

	internal static Keys Read( AppSettings settings, ShortcutOperation operation )
	{
		string stored = settings.Get( "Shortcut." + operation.Id );

		return int.TryParse( stored, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number ) && Valid( (Keys)number )
			? (Keys)number : operation.Default;
	}

	internal static bool Valid( Keys keys )
	{
		Keys code = keys & Keys.KeyCode;

		return keys == Keys.None || ( keys & ~( Keys.KeyCode | Keys.Control | Keys.Shift | Keys.Alt ) ) == 0 && Enum.IsDefined( code )
			&& code is not ( Keys.None or Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin )
			&& keys != Keys.Tab && keys != ( Keys.Shift | Keys.Tab ) && keys != ( Keys.Alt | Keys.F4 ) && keys != ( Keys.Shift | Keys.F10 );
	}

	internal static string Validate( IReadOnlyDictionary<string, Keys> values )
	{
		foreach ( var operation in Operations )
			if ( !values.TryGetValue( operation.Id, out var keys ) || !Valid( keys ) )
				return UiText.Format( "KeyboardShortcuts", "Choose a valid shortcut for {0}.", Caption( operation ) );

		var duplicate = Operations.Where( operation => values[operation.Id] != Keys.None ).GroupBy( operation => values[operation.Id] )
			.FirstOrDefault( group => group.Count() > 1 );

		return duplicate is null ? null : UiText.Format( "KeyboardShortcuts", "{0} is assigned to more than one operation: {1}.",
			Display( duplicate.Key ), string.Join( ", ", duplicate.Select( Caption ) ) );
	}

	// Saves all the shortcuts or none. Invalid values throw, and a failed write puts the previous values back.
	internal static void Save( AppSettings settings, IReadOnlyDictionary<string, Keys> values )
	{
		if ( Validate( values ) is { } error )
			throw new ArgumentException( error );

		var previous = Operations.ToDictionary(
			operation => "Shortcut." + operation.Id,
			operation => settings.Get( "Shortcut." + operation.Id ) );

		try
		{
			foreach ( var operation in Operations )
				settings.Set( "Shortcut." + operation.Id, ( (int)values[operation.Id] ).ToString( CultureInfo.InvariantCulture ) );

			settings.SaveToFile();
		}
		catch
		{
			foreach ( var entry in previous )
				settings.Set( entry.Key, entry.Value );

			throw;
		}
	}

	internal static string Match( AppSettings settings, Keys keys ) => keys == Keys.None ? null
		: Operations.FirstOrDefault( operation => Read( settings, operation ) == keys )?.Id;

	internal static void UpdateMenu( ToolStripItemCollection items, AppSettings settings )
	{
		foreach ( ToolStripMenuItem item in items.OfType<ToolStripMenuItem>() )
		{
			var operation = Operations.FirstOrDefault( operation => operation.Menu == item.Name );

			if ( operation is not null )
			{
				Keys keys = Read( settings, operation );

				item.ShortcutKeyDisplayString = keys == Keys.None ? "" : Display( keys );
			}

			UpdateMenu( item.DropDownItems, settings );
		}
	}
}
