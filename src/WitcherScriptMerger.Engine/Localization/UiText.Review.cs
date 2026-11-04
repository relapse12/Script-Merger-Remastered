using System.Collections.Generic;

namespace WitcherScriptMerger;

internal static partial class UiText
{
	private static IEnumerable<TextEntry> ReviewEntries => new TextEntry[] {
		new("KeyboardShortcuts", "Keyboard shortcuts", false),
		new("KeyboardShortcuts", "Unassigned", false),
		new("KeyboardShortcuts", "Operation", false),
		new("KeyboardShortcuts", "Shortcut", false),
		new("KeyboardShortcuts", "Default", false),
		new("KeyboardShortcuts", "Filter operations…", false),
		new(
			"KeyboardShortcuts",
			"Select an operation and press its shortcut. Use Clear to leave it unassigned. Changes apply when you save; typing in search fields keeps normal text-editing behavior.",
			false),
		new("KeyboardShortcuts", "Choose a valid shortcut for {0}.", true),
		new("KeyboardShortcuts", "{0} is assigned to more than one operation: {1}.", true),
		new("KeyboardShortcuts", "{0} ({1})", true),
		new("KeyboardShortcuts", "Cancel", false),
		new("KeyboardShortcuts", "Save", false),
		new("KeyboardShortcuts", "Clear", false),
		new("KeyboardShortcuts", "Restore defaults", false),
		new("KeyboardShortcuts", "Could not save shortcuts", false),
		new("ScanCaches", "A cache folder is a linked directory. No caches were removed.", false),
		new("MainForm", "Clear memory caches...", false),
		new("MainForm", "Clear memory caches", false),
		new(
			"MainForm",
			"Clear saved bundle, script-symbol and merge-check caches? They will be rebuilt on the next scan. Your settings, merges and recovery files will be kept.",
			false),
		new("MainForm", "Clearing caches", false),
		new("MainForm", "Caches cleared. Refresh conflicts to rebuild them.", false),
		new("MainForm", "Caches could not be cleared.\n\n{0}", true),
		new("MainForm", "Open Merged Compiled Scripts", false),
		new(
			"MainForm",
			"Existing merged files have no matching merge inventory:\n\n{0}\n\nRemove them so they can be recreated? Preserve any manual edits first. If you keep them, the app will stay open, but these files must be removed or their inventory restored before creating new merges.",
			true),
		new(
			"MainForm",
			"Removing old merges failed:\n\n{0}\n\nThe app will stay open. Preserve any manual edits, then remove this directory manually:\n\n{1}",
			true),
		new("BlobMerge", "Reading installed mod information", false),
		new("BlobMerge", "Planning required script changes", false),
		new("BlobMerge", "Comparing with original scripts", false),
		new("BlobMerge", "Reading original game scripts", false),
		new("BlobMerge", "Reading mod scripts and dependencies", false),
		new("BlobMerge", "Recovering compiled scripts", false),
		new("BlobMerge", "Checking merge inputs", false),
		new("BlobMerge", "Writing merged loose scripts", false),
		new("BlobMerge", "Reusing unchanged compiled scripts", false),
		new("BlobMerge", "Compiling required scripts", false),
		new("BlobMerge", "Preparing required compilation", false),
		new("BlobMerge", "Preparing merge inventory", false),
		new("BlobMerge", "Reading saved merge choices", false),
		new("BlobMerge", "Saving merge recovery information", false),
		new("BlobMerge", "Applying merged scripts", false),
		new("BlobMerge", "Finishing merge", false),
		new("BlobMerge", "Merging script changes—waiting for KDiff3", false)
	};
}
