using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using System.Xml;
using WitcherScriptMerger.FileIndex;
using WitcherScriptMerger.LoadOrder;
using WitcherScriptMerger.Tools;

namespace WitcherScriptMerger.Inventory;

// Removing some bundled merges requires a repack. Kept identifies the removals blocked by missing merge text.
internal sealed record BlockedBundleRemovals( IReadOnlyList<Merge> Kept, IReadOnlyList<Merge> Unstaged );

[XmlRoot]
public class MergeInventory
{
	private static readonly XmlSerializer _serializer = new XmlSerializer( typeof( MergeInventory ) );

	[XmlElement( "Merge" )]
	public ObservableCollection<Merge> Merges { get; private set; }

	[XmlAnyElement( "BlobMergeState" )]
	public XmlElement BlobMergeState { get; set; }

	// Set by MergeInventoryStore.Read when the file exists but cannot be read.
	[XmlIgnore]
	internal string LoadError { get; init; }

	[XmlIgnore]
	public bool ScriptsChanged { get; private set; }

	[XmlIgnore]
	public bool XmlChanged { get; private set; }

	[XmlIgnore]
	public bool BundleChanged { get; private set; }

	[XmlIgnore]
	public bool HasChanged
	{
		get
		{
			if ( !ScriptsChanged && !XmlChanged )
			{
				return BundleChanged;
			}

			return true;
		}
	}

	// Repacking reads the staging folder. Any retained merge with missing text would disappear from the bundle.
	internal IReadOnlyList<Merge> FindUnstagedMerges( string bundle, GamePaths game, IReadOnlyCollection<Merge> leaving )
	{
		return Merges.Where( merge => merge.IsBundleContent && !leaving.Contains( merge )
				&& merge.GetMergedBundle( game ).EqualsIgnoreCase( bundle ) && !merge.HasStagedText( game ) )
			.ToArray();
	}

	// Keep the records when a partial removal would require an unsafe repack. Removing the whole bundle needs no repack.
	internal BlockedBundleRemovals FindBlockedBundleRemovals( IReadOnlyCollection<Merge> deleting, GamePaths game )
	{
		var kept = new List<Merge>();
		var unstaged = new List<Merge>();

		foreach ( IGrouping<string, Merge> bundle in deleting.Where( merge => merge.IsBundleContent )
			.GroupBy( merge => merge.GetMergedBundle( game ), StringComparer.OrdinalIgnoreCase ) )
		{
			IReadOnlyList<Merge> lost = FindUnstagedMerges( bundle.Key, game, deleting );

			if ( lost.Count == 0 )
				continue;

			kept.AddRange( bundle );
			unstaged.AddRange( lost );
		}

		return new BlockedBundleRemovals( kept, unstaged );
	}

	public MergeInventory()
	{
		Merges = new ObservableCollection<Merge>();
		Merges.CollectionChanged += Merges_CollectionChanged;
	}

	// Loading records triggers collection-change events; clear those changes once loading finishes.
	internal void ClearChangeTracking()
	{
		ScriptsChanged = false;
		XmlChanged = false;
		BundleChanged = false;
	}

	private void Merges_CollectionChanged( object sender, NotifyCollectionChangedEventArgs e )
	{
		if ( ( e.NewItems != null && e.NewItems.Cast<Merge>().Any( ( Merge merge ) => merge.Category == Categories.Script ) ) || ( e.OldItems != null && e.OldItems.Cast<Merge>().Any( ( Merge merge ) => merge.Category == Categories.Script ) ) )
		{
			ScriptsChanged = true;
		}

		if ( ( e.NewItems != null && e.NewItems.Cast<Merge>().Any( ( Merge merge ) => merge.Category == Categories.Xml ) ) || ( e.OldItems != null && e.OldItems.Cast<Merge>().Any( ( Merge merge ) => merge.Category == Categories.Xml ) ) )
		{
			XmlChanged = true;
		}

		if ( ( e.NewItems != null && e.NewItems.Cast<Merge>().Any( ( Merge merge ) => merge.IsBundleContent ) ) || ( e.OldItems != null && e.OldItems.Cast<Merge>().Any( ( Merge merge ) => merge.IsBundleContent ) ) )
		{
			BundleChanged = true;
		}
	}

	public void AddModToMerge( FileMerger.MergeSource source, Merge m )
	{
		string modFilePath = ( m.IsBundleContent ? source.Bundle.FullName : source.TextFile.FullName );
		FileHash existingMod = m.Mods.Find( ( FileHash mod ) => mod.Name.EqualsIgnoreCase( source.Name ) );

		if ( existingMod != null )
		{
			existingMod.Hash = Hasher.ComputeHash( modFilePath );
			existingMod.BundleName = source.Hash?.BundleName;
			existingMod.SourceDirectory = source.Hash?.SourceDirectory;
			existingMod.Origin = source.Hash?.Origin;
			existingMod.ModIoId = source.Hash?.ModIoId ?? 0;
		}
		else
		{
			m.Mods.Add( new FileHash
			{
				Hash = Hasher.ComputeHash( modFilePath ),
				Name = source.Name,
				BundleName = source.Hash?.BundleName,
				SourceDirectory = source.Hash?.SourceDirectory,
				Origin = source.Hash?.Origin,
				ModIoId = source.Hash?.ModIoId ?? 0
			} );
		}

		if ( m.Category == Categories.Script )
		{
			ScriptsChanged = true;
		}
		else if ( m.Category == Categories.Xml )
		{
			XmlChanged = true;
		}
		else if ( m.IsBundleContent )
		{
			BundleChanged = true;
		}
	}

	internal byte[] Serialize()
	{
		using var stream = new MemoryStream();

		_serializer.Serialize( stream, this );

		return stream.ToArray();
	}

	internal static MergeInventory Deserialize( byte[] bytes )
	{
		if ( bytes is null )
			return new MergeInventory();

		using var stream = new MemoryStream( bytes, writable: false );

		return (MergeInventory)_serializer.Deserialize( stream );
	}

	public bool HasResolvedConflict( ModFile conflict, GamePaths game )
	{
		Merge merge = Merges.FirstOrDefault( ( Merge mrg ) => mrg.RelativePath.EqualsIgnoreCase( conflict.RelativePath ) );

		return HasResolvedConflict( conflict, merge, game );
	}

	// A record alone does not resolve a conflict. Its output must still win and its sources must still match the saved hashes.
	internal bool HasResolvedConflict( ModFile conflict, Merge merge, GamePaths game, Func<string, string> computeHash = null )
	{
		computeHash ??= Hasher.ComputeHash;

		if ( !string.IsNullOrEmpty( merge?.OutdatedReason ) )
			return false;

		if ( merge != null && !conflict.ParticipatingSources.Any( ( FileHash mod ) => !mod.Name.EqualsIgnoreCase( merge.MergedModName ) && !merge.ContainsMod( mod.Name ) )
			&& merge.OutputResolves( conflict.CurrentLoadOrder, game ) )
		{
			try
			{
				return merge.Mods.All( mod => mod.Hash == computeHash( mod.SourceFilePath ?? merge.GetModFile( mod.Name, game ) ) );
			}
			catch ( Exception error ) when ( error is System.IO.IOException or UnauthorizedAccessException ) { return false; }
		}

		return false;
	}

	internal bool ContainsModReference( string modName )
	{
		return Merges.Any( merge => merge.Mods.Any( mod => mod.Name.EqualsIgnoreCase( modName ) ) );
	}

	internal bool RenameModReferences( string oldName, string newName )
	{
		bool changed = false;

		foreach ( Merge merge in Merges )
		{
			foreach ( FileHash mod in merge.Mods.Where( mod => mod.Name.EqualsIgnoreCase( oldName ) ) )
			{
				mod.Name = newName;
				changed = true;
			}
		}

		return changed;
	}
}
