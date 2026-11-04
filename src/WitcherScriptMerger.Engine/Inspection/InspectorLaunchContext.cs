#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WitcherScriptMerger.Inspection;

// Sent to the separate Inspector process to describe the inputs for this launch.
internal sealed record InspectorLaunchContext(
	int Schema, string SourceIdentity, string? MergeRevision, string? LoadStateRevision,
	bool ScanCurrent, string Language, InspectorFolders Folders,
	IReadOnlyList<string> LoadStateFiles, IReadOnlyList<string> LoadStateFolders,
	IReadOnlyList<InspectorMod> Mods, IReadOnlyList<InspectorMerge> Merges, InspectorSelection? Selection )
{
	internal string Serialize()
	{
		return JsonSerializer.Serialize( this, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase } );
	}
}

internal sealed record InspectorFolders(
	string Game, string Mods, string VanillaScripts, string GameSettings, string ModIo,
	string Inventory, string Data, string Languages, string? MergedScripts );

internal sealed record InspectorMod( string Path, string State );
internal sealed record InspectorSuppressedOriginal( string Original, string Suppressed );
internal sealed record InspectorMerge(
	string Path, string Kind, IReadOnlyList<string> Contributors,
	IReadOnlyList<InspectorSuppressedOriginal> SuppressedOriginals )
{
	public bool ContainsDecompiledCode { get; init; }
}
internal sealed record InspectorSelection(
	string Category, string? RelativePath, string? SymbolKey, IReadOnlyList<string> Mods );

// Both applications must compute the same hash from this data.
internal static class InspectorLaunchRevision
{
	internal static string SourceIdentity( InspectorFolders folders )
	{
		var paths = new[] { folders.Game, folders.Mods, folders.Inventory }
			.Select( path => Path.TrimEndingDirectorySeparator( Path.GetFullPath( path ) ).ToUpperInvariant() );

		return Convert.ToHexString( SHA256.HashData( Encoding.UTF8.GetBytes( string.Join( "\n", paths ) ) ) ).ToLowerInvariant();
	}

	internal static byte[]? ReadOptional( string path )
	{
		try
		{
			return File.ReadAllBytes( path );
		}
		catch ( Exception error ) when ( error is FileNotFoundException or DirectoryNotFoundException )
		{
			return null;
		}
	}

	internal static string[]? ChildFolders( string path )
	{
		try
		{
			return Directory.GetDirectories( path ).Order( StringComparer.OrdinalIgnoreCase ).ToArray();
		}
		catch ( DirectoryNotFoundException )
		{
			return null;
		}
	}

	internal static string Hash( IEnumerable<byte[]?> files, IEnumerable<string[]?> folders )
	{
		using var hash = IncrementalHash.CreateHash( HashAlgorithmName.SHA256 );

		foreach ( byte[]? content in files )
		{
			hash.AppendData( content is null ? new byte[] { 0 } : new byte[] { 1 } );

			if ( content is null )
				continue;

			hash.AppendData( BitConverter.GetBytes( (long)content.Length ) );
			hash.AppendData( content );
		}

		foreach ( string[]? children in folders )
		{
			hash.AppendData( children is null ? new byte[] { 0 } : new byte[] { 1 } );

			if ( children is null )
				continue;

			var names = children.Select( path => Path.GetFileName( path ).ToUpperInvariant() ).Order( StringComparer.Ordinal );

			foreach ( string name in names )
				hash.AppendData( Encoding.UTF8.GetBytes( name + "\n" ) );
		}

		return Convert.ToHexString( hash.GetHashAndReset() ).ToLowerInvariant();
	}
}
