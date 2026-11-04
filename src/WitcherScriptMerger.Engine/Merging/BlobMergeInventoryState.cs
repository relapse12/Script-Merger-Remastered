#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;


namespace WitcherScriptMerger.Merging;

// Recovery state lives in a BlobMergeState element inside MergeInventory.xml.
// Separate JSON state files remain supported; the path extension selects the format.
public static partial class BlobMergeTransaction
{
	public const string InventoryStateElement = "BlobMergeState";

	public static bool UsesInventory( string path ) => path.EndsWith( ".xml", StringComparison.OrdinalIgnoreCase );
	// Keep recovery files beside their state file, including when the caller supplies a journal path.
	public static string StorageDirectory(
		string path )
	{
		string full = Path.GetFullPath( path );

		foreach ( string suffix in new[] { ".pending", ".update" } )
			if ( full.EndsWith( suffix, StringComparison.OrdinalIgnoreCase ) )
				full = full[..^suffix.Length];

		return UsesInventory( full ) ? Path.Combine( Path.GetDirectoryName( full )!, "Data", "Blob Merges" ) : Path.GetDirectoryName( full )!;
	}

	public static bool HasState( string path ) => File.Exists( path ) && ( !UsesInventory( path )
		|| InventoryDocument( File.ReadAllBytes( path ) ).Root!.Elements( InventoryStateElement ).Any() );

	public static byte[] SetInventoryState(
		byte[] inventory,
		AppliedBlobMerge? applied )
	{
		var document = InventoryDocument( inventory );
		document.Root!.Elements( InventoryStateElement ).Remove();

		if ( applied is not null )
			document.Root.Add( StateElement( applied ) );

		using var stream = new MemoryStream();
		document.Save( stream );

		return stream.ToArray();
	}

	// Exclude blob state from the hash so saving it does not look like a change to ordinary merge records.
	public static string InventoryDigest( byte[] inventory ) => Hash( SetInventoryState( inventory, null ) );

	private static XDocument InventoryDocument(
		byte[] bytes )
	{
		try
		{
			using var stream = new MemoryStream( bytes, writable: false );
			using var reader = XmlReader.Create( stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreWhitespace = true, MaxCharactersInDocument = 32 * 1024 * 1024 } );
			var document = XDocument.Load( reader );

			if ( document.Root?.Name != "MergeInventory" || document.Root.Elements( InventoryStateElement ).Count() > 1 )
				throw new InvalidDataException( "Invalid merge inventory state." );

			return document;
		}
		catch ( XmlException error )
		{
			throw new InvalidDataException( "Invalid merge inventory XML.", error );
		}
	}

	private static XElement StateElement(
		AppliedBlobMerge applied )
	{
		applied = VanillaBaselineStore.Compact( applied );
		using var reader = JsonReaderWriterFactory.CreateJsonReader( JsonSerializer.SerializeToUtf8Bytes( applied ), XmlDictionaryReaderQuotas.Max );
		var element = XElement.Load( reader );
		element.Name = InventoryStateElement;

		return element;
	}

	// Keep the vanilla file list in Data and save only its reference in the inventory.
	public static byte[] CompactVanillaBaseline(
		byte[] inventory,
		string vanilla,
		string inventoryPath )
	{
		if ( InventoryDocument( inventory ).Root!.Element( InventoryStateElement ) is null )
			return inventory;

		var applied = RebindInventory( ReadInventoryState( inventory ), inventoryPath );
		Validate( applied.Manifest );
		var baseline = applied.Manifest.VanillaBaseline
			?? VanillaBaselineStore.Describe( vanilla, applied.Manifest.Inputs, applied.Manifest.ModsRoot );

		return baseline is null ? inventory : SetInventoryState( inventory,
			applied with
			{
				Manifest = applied.Manifest with
				{
					VanillaBaseline = baseline
				}
			} );
	}

	private static AppliedBlobMerge ReadInventoryState(
		byte[] bytes )
	{
		var element = InventoryDocument( bytes ).Root!.Element( InventoryStateElement )
			?? throw new InvalidDataException( "The compiled merge recovery information is missing from MergeInventory.xml." );
		element.Name = "root";
		using var stream = new MemoryStream();
		using ( var writer = JsonReaderWriterFactory.CreateJsonWriter( stream, System.Text.Encoding.UTF8, ownsStream: false ) )
		using ( var reader = element.CreateReader() )
			writer.WriteNode( reader, true );

		return JsonSerializer.Deserialize<AppliedBlobMerge>( stream.ToArray() ) ?? throw new InvalidDataException( "Invalid merge inventory state." );
	}

	// Use the inventory's current location after it has been moved.
	private static AppliedBlobMerge RebindInventory(
		AppliedBlobMerge applied,
		string path )
	{
		if ( applied.Inventory is not { } inventory )
			return applied;

		string target = Path.GetFullPath( path );

		return applied with
		{
			Inventory = inventory with
			{
				Path = target
			},
			Manifest = applied.Manifest with
			{
				Inputs = applied.Manifest.Inputs.Select( input => input.Path.Equals( inventory.Path, StringComparison.OrdinalIgnoreCase )
					? input with
					{
						Path = target
					} : input ).ToArray()
			}
		};
	}
}
