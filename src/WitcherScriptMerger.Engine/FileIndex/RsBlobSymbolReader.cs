#nullable enable

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace WitcherScriptMerger.FileIndex;

internal sealed record RsBlobSymbol( string Key, ScriptSymbolKind Kind, ScopedScriptMember? Member = null );

internal sealed record RsBlobSymbolIndex(
	uint FormatVersion,
	string BuildPlatform,
	string BuildVersion,
	string BuildConfiguration,
	IReadOnlyList<RsBlobSymbol> Symbols,
	RsBlobDependencyMetadata? Dependencies = null );

// Read definitions without decompiling function bodies so ordinary scans stay quick.
internal static class RsBlobSymbolReader
{
	private const int MaximumCollectionCount = 1_000_000;
	private const int MaximumStringBytes = 16 * 1024 * 1024;
	private const int MaximumDefaultValueDepth = 64;
	private const uint FunctionImport = 0x1;
	private const uint FunctionNative = 0x200;
	private static readonly UTF8Encoding StrictUtf8 = new( false, true );
	private static readonly UnicodeEncoding StrictUtf16 = new( false, false, true );

	public static RsBlobSymbolIndex Read( string path )
	{
		byte[] data;
		int stringTableOffset;
		BlobReader reader;
		uint formatVersion;
		string buildPlatform;
		string buildVersion;
		string buildConfiguration;
		BlobSections sections = new();

		if ( string.IsNullOrWhiteSpace( path ) )
			throw new ArgumentException( UiText.T( "RsBlobSymbolReader", "An rsblob path is required." ), nameof( path ) );

		data = File.ReadAllBytes( Path.GetFullPath( path ) );

		if ( data.Length < 20 )
			throw new InvalidDataException( UiText.T( "RsBlobSymbolReader", "The precompiled.rsblob file is too small to contain a valid header." ) );

		stringTableOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian( data.AsSpan( data.Length - 4, 4 ) ));

		if ( stringTableOffset <= 0 || stringTableOffset >= data.Length - 4 )
			throw new InvalidDataException( UiText.T( "RsBlobSymbolReader", "The precompiled.rsblob string-table offset is outside the file." ) );

		reader = new( data, 0, stringTableOffset, ReadStringTable( data, stringTableOffset ) );
		formatVersion = reader.ReadUInt32();

		if ( formatVersion != 1 )
			throw new InvalidDataException( UiText.F( "RsBlobSymbolReader", $"Unsupported precompiled.rsblob format version {formatVersion}; expected version 1." ) );

		buildPlatform = reader.ReadString();
		buildVersion = reader.ReadString();
		reader.Skip( 8, "build timestamp" );
		buildConfiguration = reader.ReadString();

		sections.ReadDefinitions( reader );
		sections.ReadImports( reader );
		sections.AddSymbolRequirements();

		return new RsBlobSymbolIndex(
			formatVersion,
			buildPlatform,
			buildVersion,
			buildConfiguration,
			sections.IndexedSymbols(),
			sections.Dependencies() );
	}

	private static string[] ReadStringTable( byte[] data, int stringTableOffset )
	{
		BlobReader reader = new( data, stringTableOffset, data.Length - 4, Array.Empty<string>() );
		string[] strings = new string[reader.ReadUInt32Count( "string table" )];

		for ( int index = 0; index < strings.Length; index++ )
			strings[index] = reader.ReadString();

		return strings;
	}

	private sealed class BlobSections
	{
		private readonly Dictionary<string, ScriptSymbolKind> symbols = new( StringComparer.OrdinalIgnoreCase );
		private readonly HashSet<string> types = new( StringComparer.OrdinalIgnoreCase );
		private readonly List<RsBlobRequirement> requirements = new();
		private readonly List<RsBlobEnumDefinition> enums = new();
		private readonly List<ScopedScriptMember> members = new();
		private readonly List<RsBlobTypeParent> parents = new();

		// The read order must match the table order in the blob.
		internal void ReadDefinitions( BlobReader reader )
		{
			ReadArray( reader, () => enums.Add( ReadEnum( reader, symbols ) ), "enum definitions" );
			ReadArray( reader, () => ReadStruct( reader, symbols ), "struct definitions" );
			ReadArray( reader, () => ReadClass( reader, symbols, types, requirements, members, parents ), "class definitions" );
			ReadFunctionArray( reader, symbols, "Global", ScriptSymbolKind.GlobalFunction, "global functions" );
			ReadFunctionArray( reader, symbols, "Global", ScriptSymbolKind.GlobalReplacement, "global replacement functions" );
			ReadFunctionArray( reader, symbols, fixedScope: null, ScriptSymbolKind.ClassReplacement, "class replacement functions" );
			ReadFunctionArray( reader, symbols, fixedScope: null, ScriptSymbolKind.AddedFunction, "added functions" );
			ReadFunctionArray( reader, symbols, fixedScope: null, ScriptSymbolKind.WrappedFunction, "wrapped functions" );
			ReadArray( reader, () =>
			{
				PropertyDefinition property = ReadProperty( reader );

				RequireType( requirements, property.ClassName );
				members.Add( property.Member( property.ClassName, true ) );
			}, "added properties" );
		}

		// Only imported type names are needed for dependency checks. Consume the other fields to reach the next section.
		internal void ReadImports( BlobReader reader )
		{
			ReadArray( reader, () =>
			{
				RequireType( requirements, reader.ReadName() );
				reader.ReadCompressedUInt32();
			}, "imported types" );
			ReadArray( reader, () =>
			{
				reader.ReadName();
				reader.ReadCompressedUInt32();
				reader.ReadCompressedUInt32();
			}, "imported properties" );
			ReadArray( reader, () =>
			{
				reader.ReadName();
				reader.ReadCompressedUInt32();
			}, "imported functions" );
		}

		// Collect dependencies before adding individual members to the symbol list.
		internal void AddSymbolRequirements()
		{
			foreach ( KeyValuePair<string, ScriptSymbolKind> symbol in symbols )
			{
				if ( ( symbol.Value & ( ScriptSymbolKind.EnumDefinition | ScriptSymbolKind.StructDefinition ) ) != 0 )
					types.Add( symbol.Key.Split( '\\' ).Last() );

				if ( ( symbol.Value & ( ScriptSymbolKind.WrappedFunction | ScriptSymbolKind.ClassReplacement | ScriptSymbolKind.GlobalReplacement ) ) != 0 )
					requirements.Add( new RsBlobRequirement( symbol.Key, RsBlobRequirementKind.Function ) );

				if ( ( symbol.Value & ScriptSymbolKind.AddedFunction ) != 0 )
				{
					string scope = symbol.Key.Split( '\\' )[1];

					if ( !scope.Equals( "Global", StringComparison.OrdinalIgnoreCase ) )
						RequireType( requirements, scope );
				}
			}
		}

		internal RsBlobSymbol[] IndexedSymbols()
		{
			Dictionary<string, ScopedScriptMember> membersByKey = members
				.GroupBy( member => member.Key, StringComparer.OrdinalIgnoreCase )
				.ToDictionary( group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase );

			foreach ( ScopedScriptMember member in members )
				symbols[member.Key] = member.Kind;

			return symbols
				.OrderBy( pair => pair.Key, StringComparer.OrdinalIgnoreCase )
				.Select( pair => new RsBlobSymbol( pair.Key, pair.Value, membersByKey.GetValueOrDefault( pair.Key ) ) )
				.ToArray();
		}

		internal RsBlobDependencyMetadata Dependencies()
		{
			return new RsBlobDependencyMetadata(
				types.OrderBy( value => value, StringComparer.OrdinalIgnoreCase ).ToArray(),
				requirements.Distinct().OrderBy( value => value.Key, StringComparer.OrdinalIgnoreCase ).ThenBy( value => value.Kind ).ToArray(),
				enums,
				parents );
		}
	}

	private static RsBlobEnumDefinition ReadEnum( BlobReader reader, Dictionary<string, ScriptSymbolKind> symbols )
	{
		string name = reader.ReadName();

		AddSymbol( symbols, ScriptSymbolKind.EnumDefinition, "Enums", name );

		List<RsBlobEnumMember> members = new();
		int enumeratorCount = reader.ReadCompressedUInt32Count( "enum members" );

		for ( int index = 0; index < enumeratorCount; index++ )
		{
			members.Add( new( reader.ReadName(), reader.ReadCompressedInt32() ) );
		}

		return new( name, members );
	}

	private static void ReadStruct( BlobReader reader, Dictionary<string, ScriptSymbolKind> symbols )
	{
		AddSymbol( symbols, ScriptSymbolKind.StructDefinition, "Types", reader.ReadName() );
		reader.ReadCompressedUInt32();
		ReadArray( reader, () => ReadProperty( reader ), "struct properties" );
		ReadArray( reader, () => ReadDefaultValueDefinition( reader ), "struct default values" );
	}

	private static void ReadClass( BlobReader reader, Dictionary<string, ScriptSymbolKind> symbols,
		ISet<string> types, ICollection<RsBlobRequirement> requirements, ICollection<ScopedScriptMember> members, ICollection<RsBlobTypeParent> parents )
	{
		string className = reader.ReadName();
		string parentName = reader.ReadName();
		string stateMachineName = reader.ReadName();
		bool isState = reader.ReadByte() != 0;
		string classScope = isState && !string.IsNullOrWhiteSpace( stateMachineName )
			? stateMachineName + "State" + className
			: className;

		// A state belongs to its state machine, so it must not also appear as a global class.
		if ( !isState )
		{
			types.Add( className );

			if ( !string.IsNullOrWhiteSpace( parentName ) )
				parents.Add( new( className, parentName ) );

			if ( !string.IsNullOrWhiteSpace( parentName ) )
				requirements.Add( new RsBlobRequirement( parentName, RsBlobRequirementKind.BaseClass ) );
		}

		reader.ReadCompressedUInt32();
		ReadArray(
			reader,
			() => { var property = ReadProperty( reader ); if ( ( property.Flags & 0x4000 ) == 0 ) members.Add( property.Member( classScope ) ); },
			"class properties" );
		ReadArray( reader, () =>
		{
			FunctionDefinition function = ReadFunction( reader );

			AddFunctionSymbol( symbols, classScope, function.Name,
				function.IsExternal ? ScriptSymbolKind.ExternalFunction : ScriptSymbolKind.ClassFunction );
		}, "class functions" );
		ReadArray(
			reader,
			() => { string property = reader.ReadName(); var value = ReadDefaultValue( reader, 0 ); AddDefaultMembers( members, classScope, property, value ); },
			"class default values" );
	}

	private static void RequireType( ICollection<RsBlobRequirement> requirements, string name )
	{
		if ( !string.IsNullOrWhiteSpace( name ) )
			requirements.Add( new RsBlobRequirement( name, RsBlobRequirementKind.Type ) );
	}

	private static void ReadFunctionArray(
		BlobReader reader,
		Dictionary<string, ScriptSymbolKind> symbols,
		string? fixedScope,
		ScriptSymbolKind kind,
		string description )
	{
		ReadArray( reader, () =>
		{
			FunctionDefinition function = ReadFunction( reader );

			AddFunctionSymbol( symbols, fixedScope ?? function.OverrideClass, function.Name,
				kind == ScriptSymbolKind.GlobalFunction && function.IsExternal ? ScriptSymbolKind.ExternalFunction : kind );
		}, description );
	}

	private static FunctionDefinition ReadFunction( BlobReader reader )
	{
		string name = reader.ReadName();
		string overrideClass = reader.ReadName();
		uint flags = reader.ReadCompressedUInt32();

		if ( reader.ReadByte() != 0 )
		{
			ReadProperty( reader );
		}

		ReadArray( reader, () => ReadProperty( reader ), "function parameters" );
		ReadArray( reader, () => ReadProperty( reader ), "function locals" );

		int bytecodeLength = reader.ReadCompressedUInt32Count( "function bytecode", MaximumStringBytes * 16 );

		reader.Skip( bytecodeLength, "function bytecode" );

		// Imported stubs can contain bytecode; native-only script bodies remain implementations.
		bool isExternal = ( flags & FunctionImport ) != 0
			|| bytecodeLength == 0 && ( flags & FunctionNative ) != 0;

		return new FunctionDefinition( name, overrideClass, isExternal );
	}

	private static PropertyDefinition ReadProperty( BlobReader reader )
	{
		string name = reader.ReadName();

		reader.ReadString();

		uint flags = reader.ReadCompressedUInt32();
		string type = reader.ReadName();
		string className = reader.ReadName();
		string binding = reader.ReadName();

		return new PropertyDefinition( name, className, type, flags, binding );
	}

	private static void ReadDefaultValueDefinition( BlobReader reader )
	{
		reader.ReadName();
		ReadDefaultValue( reader, 0 );
	}

	private static DefaultNode ReadDefaultValue( BlobReader reader, int depth )
	{
		if ( depth > MaximumDefaultValueDepth )
		{
			throw new InvalidDataException( UiText.T( "RsBlobSymbolReader", "The precompiled.rsblob default-value tree is too deeply nested." ) );
		}

		string key = reader.ReadName(), value = reader.ReadString();
		List<DefaultNode> children = new();

		ReadArray( reader, () => children.Add( ReadDefaultValue( reader, depth + 1 ) ), "nested default values" );

		return new( key, value, children );
	}

	private static void ReadArray( BlobReader reader, Action readItem, string description )
	{
		int count = reader.ReadCompressedInt32Count( description );

		for ( int index = 0; index < count; index++ )
		{
			readItem();
		}
	}

	private static void AddFunctionSymbol(
		Dictionary<string, ScriptSymbolKind> symbols,
		string scope,
		string name,
		ScriptSymbolKind kind )
	{
		AddSymbol( symbols, kind, "Functions",
			string.IsNullOrWhiteSpace( scope ) ? "Global" : scope, name );
	}

	private static void AddSymbol(
		Dictionary<string, ScriptSymbolKind> symbols,
		ScriptSymbolKind kind,
		params string[] components )
	{
		string key = ScriptSymbolKey.Create( components );

		symbols[key] = kind | ( symbols.TryGetValue( key, out ScriptSymbolKind existing )
			? existing
			: ScriptSymbolKind.None );
	}

	private sealed record FunctionDefinition( string Name, string OverrideClass, bool IsExternal );

	private sealed record PropertyDefinition( string Name, string ClassName, string Type, uint Flags, string Binding )
	{
		internal ScopedScriptMember Member(
			string owner,
			bool annotation = false ) => new( owner, Name, ScopedScriptMember.NormalizeType( Type ), Flags, Binding, "", false, annotation );
	}

	private sealed record DefaultNode( string Key, string Value, IReadOnlyList<DefaultNode> Children );

	private static void AddDefaultMembers( ICollection<ScopedScriptMember> members, string scope, string property, DefaultNode node )
	{
		if ( node.Children.Count > 0 && node.Children.All( child => child.Key.Length > 0 ) )
			foreach ( var child in node.Children )
				AddDefaultMembers( members, scope, property + "." + child.Key, child );
		else
			members.Add( new( scope, property, "", 0, "", node.Children.Count == 0 ? node.Value : System.Text.Json.JsonSerializer.Serialize( node ), true ) );
	}

	private sealed class BlobReader
	{
		private readonly byte[] data;
		private readonly int limit;
		private readonly IReadOnlyList<string> strings;

		public int Position { get; private set; }

		public BlobReader( byte[] data, int start, int limit, IReadOnlyList<string> strings )
		{
			this.data = data;
			this.limit = limit;
			this.strings = strings;
			Position = start;

			if ( start < 0 || limit < start || limit > data.Length )
			{
				throw new InvalidDataException( UiText.T( "RsBlobSymbolReader", "The precompiled.rsblob contains invalid section bounds." ) );
			}
		}

		public byte ReadByte()
		{
			EnsureAvailable( 1, "byte" );

			return data[Position++];
		}

		public uint ReadUInt32()
		{
			EnsureAvailable( 4, "32-bit value" );

			uint value = BinaryPrimitives.ReadUInt32LittleEndian( data.AsSpan( Position, 4 ) );

			Position += 4;

			return value;
		}

		public int ReadCompressedInt32()
		{
			byte first = ReadByte();
			uint value = (uint)( first & 0x3f );

			if ( ( first & 0x40 ) != 0 )
			{
				byte next = ReadByte();

				value |= (uint)( next & 0x7f ) << 6;

				if ( ( next & 0x80 ) != 0 )
				{
					next = ReadByte();
					value |= (uint)( next & 0x7f ) << 13;

					if ( ( next & 0x80 ) != 0 )
					{
						next = ReadByte();
						value |= (uint)( next & 0x7f ) << 20;

						if ( ( next & 0x80 ) != 0 )
						{
							next = ReadByte();
							value |= (uint)( next & 0x7f ) << 27;
						}
					}
				}
			}

			int signed = unchecked((int)value);

			return ( first & 0x80 ) != 0 ? unchecked(-signed) : signed;
		}

		public uint ReadCompressedUInt32()
		{
			return unchecked((uint)ReadCompressedInt32());
		}

		public int ReadCompressedInt32Count( string description, int maximum = MaximumCollectionCount )
		{
			int count = ReadCompressedInt32();

			if ( count < 0 || count > maximum )
			{
				throw new InvalidDataException( UiText.F( "RsBlobSymbolReader", $"The precompiled.rsblob has an invalid {description} count ({count})." ) );
			}

			return count;
		}

		public int ReadCompressedUInt32Count( string description, int maximum = MaximumCollectionCount )
		{
			uint count = ReadCompressedUInt32();

			if ( count > maximum )
			{
				throw new InvalidDataException( UiText.F( "RsBlobSymbolReader", $"The precompiled.rsblob has an invalid {description} count ({count})." ) );
			}

			return (int)count;
		}

		public int ReadUInt32Count( string description )
		{
			uint count = ReadUInt32();

			if ( count > MaximumCollectionCount )
			{
				throw new InvalidDataException( UiText.F( "RsBlobSymbolReader", $"The precompiled.rsblob has an invalid {description} count ({count})." ) );
			}

			return (int)count;
		}

		public string ReadName()
		{
			uint index = ReadCompressedUInt32();

			if ( index >= strings.Count )
			{
				throw new InvalidDataException( UiText.F( "RsBlobSymbolReader", $"The precompiled.rsblob references missing string-table entry {index}." ) );
			}

			return strings[(int)index];
		}

		public string ReadString()
		{
			int length = ReadCompressedInt32();

			if ( length <= 0 )
			{
				int byteLength = checked(-length);

				return ReadEncodedString( byteLength, StrictUtf8 );
			}

			return ReadEncodedString( checked(length * 2), StrictUtf16 );
		}

		public void Skip( int count, string description )
		{
			EnsureAvailable( count, description );
			Position += count;
		}

		private string ReadEncodedString( int byteLength, Encoding encoding )
		{
			if ( byteLength > MaximumStringBytes )
			{
				throw new InvalidDataException( UiText.F( "RsBlobSymbolReader", $"The precompiled.rsblob contains an oversized string ({byteLength} bytes)." ) );
			}

			EnsureAvailable( byteLength, "string" );

			string value = encoding.GetString( data, Position, byteLength );

			Position += byteLength;

			return value;
		}

		private void EnsureAvailable( int count, string description )
		{
			if ( count < 0 || Position < 0 || Position > limit - count )
			{
				throw new InvalidDataException( UiText.F( "RsBlobSymbolReader", $"The precompiled.rsblob ends inside a {description}." ) );
			}
		}
	}
}
