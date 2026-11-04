#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using WitcherScriptMerger.FileIndex;
using WitcherScriptMerger.Inventory;
using WitcherScriptMerger.LoadOrder;
using WitcherScriptMerger.Merging;

namespace WitcherScriptMerger.Inspection;

// Read current settings and merge records before launching the Inspector; the last scan may be out of date.
internal static class InspectorLaunchSnapshot
{
	internal static InspectorLaunchContext Capture(
		InspectorFolders folders,
		string language,
		bool scanCurrent,
		InspectorSelection? selection )
	{
		string[] inventoryFiles =
		{
			folders.Inventory,
			folders.Inventory + ".pending",
			folders.Inventory + ".update"
		};

		string modIoGame = ModIoDiscovery.ResolveGameStorageDirectory( folders.ModIo );
		string[] loadFiles =
		{
			Path.Combine( folders.GameSettings, "mods.settings" ),
			Path.Combine( folders.GameSettings, "modioModsConfig.json" ),
			Path.Combine( modIoGame, "metadata", "state.json" )
		};

		byte[]?[] inventoryBytes = inventoryFiles.Select( InspectorLaunchRevision.ReadOptional ).ToArray();
		byte[]?[] loadBytes = loadFiles.Select( InspectorLaunchRevision.ReadOptional ).ToArray();
		string? mergeRevision = InspectorLaunchRevision.Hash( inventoryBytes, Array.Empty<string[]>() );

		var loadOrder = ReadLoadOrder( loadBytes[0] );
		var external = new ModIoDiscovery( folders.ModIo, loadFiles[1] ).Discover();
		var loadFolders = new[] { folders.Mods, modIoGame }
			.Concat( external.Select( mod => Path.GetDirectoryName( mod.SourceDirectory )! ) )
			.Distinct( StringComparer.OrdinalIgnoreCase )
			.ToArray();

		string[]?[] children = loadFolders.Select( InspectorLaunchRevision.ChildFolders ).ToArray();
		string? loadRevision = InspectorLaunchRevision.Hash( loadBytes, children );

		var mods = new List<InspectorMod>();

		foreach ( string path in children[0] ?? Array.Empty<string>() )
		{
			string name = Path.GetFileName( path );
			var setting = loadOrder?.FirstOrDefault(
				item => name.Equals( item.ModName, StringComparison.OrdinalIgnoreCase ) );
			string state = "enabled";

			if ( !name.StartsWith( "mod", StringComparison.OrdinalIgnoreCase ) )
				state = "disabled";
			else if ( loadOrder is null )
				state = "unknown";
			else if ( setting?.IsEnabled == false )
				state = "disabled";

			mods.Add( new InspectorMod( Path.GetFullPath( path ), state ) );
		}

		foreach ( var mod in external )
		{
			string state = mod.IsEnabled switch
			{
				true => "enabled",
				false => "disabled",
				_ => "unknown"
			};

			mods.Add( new InspectorMod( mod.SourceDirectory, state ) );
		}

		var merges = new List<InspectorMerge>();

		try
		{
			var inventory = inventoryBytes[0] is null
				? new MergeInventory()
				: MergeInventory.Deserialize( inventoryBytes[0]! );

			var game = GamePaths.ForFolders( folders.Game, folders.Mods, folders.VanillaScripts );
			var ordinaryScriptMerges = inventory.Merges.Where(
				merge => !merge.IsBlobMerge && merge.Category == Categories.Script );

			foreach ( var merge in ordinaryScriptMerges )
			{
				string relative = Path.GetRelativePath( folders.Mods, merge.GetOutputFile( game ) );
				string path = MergeOutputFolder.ResolveOutput( folders.Mods, relative );
				string[] contributors = merge.Mods.Select( mod => mod.Name ).ToArray();

				merges.Add( new InspectorMerge(
					path, "loose", contributors,
					Array.Empty<InspectorSuppressedOriginal>() ) );
			}

			if ( inventory.BlobMergeState is not null )
			{
				var decompiledOutputs = inventory.Merges
					.Where( merge => merge.IsBlobMerge && merge.Category == Categories.Script &&
						merge.Mods.Any( mod => mod.SourceFilePath is { } source &&
							( source.EndsWith( ".rsblob", StringComparison.OrdinalIgnoreCase ) ||
								source.EndsWith( ".rsblob.smr-disabled", StringComparison.OrdinalIgnoreCase ) ) ) )
					.Select( merge => Path.GetFullPath( merge.GetOutputFile( game ) ) )
					.ToHashSet( StringComparer.OrdinalIgnoreCase );

				var applied = BlobMergeTransaction.ReadApplied( folders.Inventory, folders.Mods, folders.Inventory );
				var suppressed = applied.Manifest.Inputs
					.Where( input => input.SuppressedPath is not null )
					.Select( input => new InspectorSuppressedOriginal( input.Path, input.SuppressedPath! ) )
					.ToArray();

				foreach ( var output in applied.Manifest.Outputs )
				{
					string path = MergeOutputFolder.ResolveOutput( folders.Mods, output.Path );
					string kind = path.EndsWith( ".rsblob", StringComparison.OrdinalIgnoreCase )
						? "compiled"
						: "loose";

					string recordedPath = Path.GetFullPath( Path.Combine( folders.Mods, output.Path ) );

					merges.Add( new InspectorMerge( path, kind, output.Mods ?? applied.Manifest.Mods, suppressed )
					{
						ContainsDecompiledCode = decompiledOutputs.Contains( recordedPath )
					} );
				}
			}
		}
		catch ( Exception error ) when (
			error is IOException or UnauthorizedAccessException or InvalidDataException
				or InvalidOperationException or System.Xml.XmlException or JsonException or ArgumentException )
		{
			mergeRevision = null;
			merges.Clear();
		}

		// Files may have changed while being read. Check them again before passing these results to the Inspector.
		if ( mergeRevision != InspectorLaunchRevision.Hash(
			inventoryFiles.Select( InspectorLaunchRevision.ReadOptional ),
			Array.Empty<string[]>() ) )
			mergeRevision = null;

		if ( loadOrder is null || loadRevision != InspectorLaunchRevision.Hash(
			loadFiles.Select( InspectorLaunchRevision.ReadOptional ),
			loadFolders.Select( InspectorLaunchRevision.ChildFolders ) ) )
			loadRevision = null;

		string? mergedScripts = merges
			.Where( merge => merge.Kind == "loose" )
			.Select( merge => Path.GetRelativePath( folders.Mods, merge.Path ).Split( Path.DirectorySeparatorChar )[0] )
			.Select( name => Path.Combine( folders.Mods, name, "content", "scripts" ) )
			.FirstOrDefault();

		return new InspectorLaunchContext(
			Schema: 1,
			SourceIdentity: InspectorLaunchRevision.SourceIdentity( folders ),
			MergeRevision: mergeRevision,
			LoadStateRevision: loadRevision,
			ScanCurrent: scanCurrent,
			Language: language,
			Folders: folders with { MergedScripts = mergedScripts },
			LoadStateFiles: loadFiles,
			LoadStateFolders: loadFolders,
			Mods: mods,
			Merges: merges,
			Selection: selection );
	}

	private static IReadOnlyList<ModLoadSetting>? ReadLoadOrder( byte[]? content )
	{
		if ( content is null )
			return Array.Empty<ModLoadSetting>();

		using var reader = new StreamReader(
			new MemoryStream( content ), Encoding.UTF8, detectEncodingFromByteOrderMarks: true );

		var lines = new List<string>();
		string? line;

		while ( ( line = reader.ReadLine() ) is not null )
			lines.Add( line );

		return CustomLoadOrder.ParseSettings( lines, false, _ => { } );
	}
}
