using System.IO;

namespace WitcherScriptMerger.Tools;

internal class xxHash
{
	private const uint Seed = 0u;

	private const uint Prime1 = 2654435761u;

	private const uint Prime2 = 2246822519u;

	private const uint Prime3 = 3266489917u;

	private const uint Prime4 = 668265263u;

	private const uint Prime5 = 374761393u;

	private const int TransformSize = 16;

	private uint _v1;

	private uint _v2;

	private uint _v3;

	private uint _v4;

	private readonly string _hex;

	public override string ToString()
	{
		return _hex;
	}

	public xxHash( string filePath )
	{
		_v1 = 606290984u;
		_v2 = 2246822519u;
		_v3 = 0u;
		_v4 = 1640531535u;

		using BinaryReader reader = new BinaryReader( File.OpenRead( filePath ) );
		long streamLength = reader.BaseStream.Length;
		long limit = streamLength - 16;

		while ( reader.BaseStream.Position <= limit )
		{
			TransformBy( reader );
		}

		_hex = $"{Finalize( reader, streamLength ):X}";
	}

	private void TransformBy( BinaryReader reader )
	{
		_v1 += (uint)( (int)reader.ReadUInt32() * -2048144777 );
		_v1 = XXH_rotl( _v1, 13 );
		_v1 *= 2654435761u;
		_v2 += (uint)( (int)reader.ReadUInt32() * -2048144777 );
		_v2 = XXH_rotl( _v2, 13 );
		_v2 *= 2654435761u;
		_v3 += (uint)( (int)reader.ReadUInt32() * -2048144777 );
		_v3 = XXH_rotl( _v3, 13 );
		_v3 *= 2654435761u;
		_v4 += (uint)( (int)reader.ReadUInt32() * -2048144777 );
		_v4 = XXH_rotl( _v4, 13 );
		_v4 *= 2654435761u;
	}

	private uint Finalize( BinaryReader reader, long streamLength )
	{
		Stream stream = reader.BaseStream;
		uint hash = ( ( streamLength >= 16 ) ? ( XXH_rotl( _v1, 1 ) + XXH_rotl( _v2, 7 ) + XXH_rotl( _v3, 12 ) + XXH_rotl( _v4, 18 ) ) : 374761393u );

		hash += (uint)(int)streamLength;

		if ( stream.Position < streamLength )
		{
			while ( stream.Position + 4 <= streamLength )
			{
				hash += (uint)( (int)reader.ReadUInt32() * -1028477379 );
				hash = XXH_rotl( hash, 17 ) * 668265263;
			}

			while ( stream.Position < streamLength )
			{
				hash += (uint)( reader.ReadByte() * 374761393 );
				hash = XXH_rotl( hash, 11 ) * 2654435761u;
			}
		}

		hash ^= hash >> 15;
		hash *= 2246822519u;
		hash ^= hash >> 13;
		hash *= 3266489917u;

		return hash ^ ( hash >> 16 );
	}

	private static uint XXH_rotl( uint x, int r )
	{
		return ( x << r ) | ( x >> 32 - r );
	}
}
