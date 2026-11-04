using System;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using WitcherScriptMerger.FileIndex;
using WitcherScriptMerger.LoadOrder;

namespace WitcherScriptMerger.Inventory;

// An inventory entry links an output to its sources. BlobMergeId associates it with the saved blob merge.
[XmlRoot]
public class Merge : ModFile
{
	[XmlElement]
	public string MergedModName;

	[XmlElement]
	public string BlobMergeId { get; set; }

	[XmlIgnore]
	public bool IsBlobMerge => !string.IsNullOrEmpty( BlobMergeId );

	[XmlIgnore]
	public string OutdatedReason { get; set; } = "";

	[XmlIgnore]
	public override ModFileCategory Category => IsBlobMerge && RelativePath?.EndsWith( ".rsblob", System.StringComparison.OrdinalIgnoreCase ) == true
		? Categories.PrecompiledScript : base.Category;

	public string GetMergedFile( GamePaths game )
	{
		if ( IsBlobMerge && Category == Categories.PrecompiledScript )
			return Path.Combine( game.ModsDirectory, MergedModName, "content", RelativePath );

		if ( base.Category != Categories.Script )
		{
			if ( base.Category != Categories.Xml )
			{
				if ( base.Category != Categories.BundleText )
				{
					throw new NotImplementedException();
				}

				return Path.Combine( GamePaths.MergedBundleContent, base.RelativePath );
			}

			return Path.Combine( game.ModsDirectory, MergedModName, base.RelativePath );
		}

		return Path.Combine( game.ModsDirectory, MergedModName, GamePaths.ModScriptBase, base.RelativePath );
	}

	public string GetMergedBundle( GamePaths game )
	{
		if ( base.Category == Categories.BundleText )
		{
			return Path.Combine( game.ModsDirectory, MergedModName, GamePaths.BundleBase, base.BundleName );
		}

		throw new Exception( UiText.Format( "Merge", "Can't get bundle for file of category '{0}'.", base.Category.DisplayName ) );
	}

	// For bundled merges, GetMergedFile points to the text awaiting packing. The installed output is the bundle itself.
	internal string GetOutputFile( GamePaths game )
	{
		return base.Category == Categories.BundleText ? GetMergedBundle( game ) : GetMergedFile( game );
	}

	// Repacking needs this text even when the installed bundle still contains the merge.
	internal bool HasStagedText( GamePaths game )
	{
		try
		{
			using FileStream staged = File.OpenRead( GetMergedFile( game ) );

			return true;
		}
		catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException )
		{
			return false;
		}
	}

	// An output resolves a conflict only while enabled and ahead of all its contributors.
	// Folder renames need a disk check because mods.settings may still contain the original name.
	internal bool OutputResolves( CustomLoadOrder order, GamePaths game )
	{
		LoadOrderComparer comparer = new LoadOrderComparer( order );

		try
		{
			return !Mods.Any( source => comparer.Compare( MergedModName, source.Name ) > 0 ) && !MergeOutputFolder.IsRenamed( this, game );
		}
		catch ( Exception error ) when ( error is IOException or UnauthorizedAccessException )
		{
			// Keep the contributors visible until the output can be checked.
			return false;
		}
	}
}
