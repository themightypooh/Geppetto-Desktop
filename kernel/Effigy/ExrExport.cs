using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Effigy;

/// <summary>16-bit EXR export for film-hero work, plus the ACES tonemap that goes with it.
///
/// The kernel had no EXR reader or writer at all (see the note on <see cref="VdmStamp"/>), so
/// this is a small self-contained writer, not a binding to a native EXR library: uncompressed
/// scanline EXR, HALF (16-bit float) R/G/B/A channels, one scanline per chunk, channels in
/// alphabetical order (A, B, G, R) as the format requires. Any EXR reader — Blender, Nuke,
/// Photoshop, OpenEXR's own tools — opens the result.
///
/// A canvas texel is 8-bit sRGB. It is decoded to linear light, optionally run through the
/// ACES fitted curve (the Narkowicz approximation used across film pipelines), and stored as
/// half floats. Alpha is coverage, not light: it is stored as-is (alpha / 255). Per-tile EXR
/// is one <see cref="Write"/> call per tile — a UDIM set exports as one file per tile, never
/// as a single file pretending tiles are one image.</summary>
public static class ExrExport
{
	/// <summary>Write a canvas as an uncompressed scanline EXR (HALF channels). With
	/// <paramref name="applyAces"/> the linear values pass through the ACES fitted curve
	/// first; without it they are stored as plain linear light.</summary>
	public static byte[] Write( PaintCanvas canvas, bool applyAces = false )
	{
		if ( canvas is null )
			throw new ArgumentNullException( nameof( canvas ) );
		var width = canvas.Width;
		var height = canvas.Height;
		using var stream = new MemoryStream();
		using var w = new BinaryWriter( stream, Encoding.ASCII, leaveOpen: true );
		w.Write( 20000630 );
		w.Write( 2 );
		WriteChannelList( w );
		WriteByteAttribute( w, "compression", 0 );
		WriteBoxAttribute( w, "dataWindow", 0, 0, width - 1, height - 1 );
		WriteBoxAttribute( w, "displayWindow", 0, 0, width - 1, height - 1 );
		WriteByteAttribute( w, "lineOrder", 0 );
		WriteFloatAttribute( w, "pixelAspectRatio", 1f );
		WriteV2fAttribute( w, "screenWindowCenter", 0f, 0f );
		WriteFloatAttribute( w, "screenWindowWidth", 1f );
		w.Write( (byte)0 );

		// One chunk per scanline, channels alphabetical: A, B, G, R, each a row of halves.
		var row = new ushort[width];
		for ( var y = 0; y < height; y++ )
		{
			using var chunk = new MemoryStream();
			for ( var channel = 3; channel >= 0; channel-- )
			{
				for ( var x = 0; x < width; x++ )
				{
					var texel = canvas.Rgba[(y * width + x) * 4 + channel];
					var value = channel == 3
						? texel / 255f
						: SrgbToLinear( texel );
					if ( applyAces && channel != 3 )
						value = Aces( value );
					row[x] = FloatToHalf( value );
				}
				var bytes = new byte[width * 2];
				Buffer.BlockCopy( row, 0, bytes, 0, bytes.Length );
				chunk.Write( bytes, 0, bytes.Length );
			}
			w.Write( y );
			w.Write( (int)chunk.Length );
			w.Write( chunk.GetBuffer(), 0, (int)chunk.Length );
		}
		w.Flush();
		return stream.ToArray();
	}

	/// <summary>Read just the header of an EXR file: image size, channel names, and where the
	/// pixel chunks start. False on anything malformed (bad magic, short buffer, a truncated
	/// attribute) — a half-written export is reported, never misread as a valid image.</summary>
	public static bool TryReadHeader( byte[] exr, out int width, out int height, out string[] channels, out int headerLength )
	{
		width = height = headerLength = 0;
		channels = Array.Empty<string>();
		if ( exr is null || exr.Length < 8 )
			return false;
		try
		{
			using var r = new BinaryReader( new MemoryStream( exr ), Encoding.ASCII );
			if ( r.ReadInt32() != 20000630 )
				return false;
			_ = r.ReadInt32();
			var found = new List<string>();
			var xmin = 0;
			var ymin = 0;
			var xmax = -1;
			var ymax = -1;
			while ( true )
			{
				var name = ReadCString( r );
				if ( name is null )
					return false;
				if ( name.Length == 0 )
					break;
				var type = ReadCString( r );
				if ( type is null )
					return false;
				var size = r.ReadInt32();
				if ( size < 0 || r.BaseStream.Position + size > r.BaseStream.Length )
					return false;
				var value = r.ReadBytes( size );
				if ( name == "dataWindow" && type == "box2i" && size == 16 )
				{
					xmin = BitConverter.ToInt32( value, 0 );
					ymin = BitConverter.ToInt32( value, 4 );
					xmax = BitConverter.ToInt32( value, 8 );
					ymax = BitConverter.ToInt32( value, 12 );
				}
				else if ( name == "channels" && type == "chlist" )
				{
					var i = 0;
					while ( i < value.Length && value[i] != 0 )
					{
						var end = Array.IndexOf( value, (byte)0, i );
						if ( end < 0 || end + 18 > value.Length )
							return false;
						found.Add( Encoding.ASCII.GetString( value, i, end - i ) );
						i = end + 1 + 4 + 1 + 3 + 4 + 4;
					}
				}
			}
			if ( xmax < xmin || ymax < ymin || found.Count == 0 )
				return false;
			width = xmax - xmin + 1;
			height = ymax - ymin + 1;
			channels = found.ToArray();
			headerLength = (int)r.BaseStream.Position;
			return true;
		}
		catch ( EndOfStreamException )
		{
			return false;
		}
		catch ( IOException )
		{
			return false;
		}
	}

	/// <summary>Half float back to float, for verifying an export. Handles zero, subnormals,
	/// normals and infinity.</summary>
	public static float HalfToFloat( ushort half )
	{
		var sign = (half >> 15) & 1;
		var exp = (half >> 10) & 0x1F;
		var mant = half & 0x3FF;
		int bits;
		if ( exp == 0 )
		{
			if ( mant == 0 )
				bits = sign << 31;
			else
			{
				// Subnormal: value = mant * 2^-24. Normalise into a float.
				var e = -14;
				while ( (mant & 0x400) == 0 )
				{
					mant <<= 1;
					e--;
				}
				mant &= 0x3FF;
				bits = (sign << 31) | ((e + 127) << 23) | (mant << 13);
			}
		}
		else if ( exp == 31 )
		{
			bits = (sign << 31) | (0xFF << 23) | (mant << 13);
		}
		else
		{
			bits = (sign << 31) | ((exp - 15 + 127) << 23) | (mant << 13);
		}
		return BitConverter.Int32BitsToSingle( bits );
	}

	/// <summary>The ACES fitted curve (Narkowicz approximation): the filmic tonemap film
	/// pipelines expect before a linear EXR is viewed. Input and output are linear 0..1.</summary>
	public static float Aces( float x )
	{
		x = Math.Clamp( x, 0f, 1f );
		return Math.Clamp( (x * (2.51f * x + 0.03f)) / (x * (2.43f * x + 0.59f) + 0.14f), 0f, 1f );
	}

	/// <summary>sRGB 8-bit to linear light, the standard piecewise decode.</summary>
	public static float SrgbToLinear( byte v )
	{
		var c = v / 255f;
		return c <= 0.04045f ? c / 12.92f : MathF.Pow( (c + 0.055f) / 1.055f, 2.4f );
	}

	static void WriteChannelList( BinaryWriter w )
	{
		using var value = new MemoryStream();
		using var v = new BinaryWriter( value, Encoding.ASCII, leaveOpen: true );
		foreach ( var name in new[] { "A", "B", "G", "R" } )
		{
			WriteCString( v, name );
			v.Write( 1 ); // HALF
			v.Write( (byte)0 ); // pLinear
			v.Write( new byte[3] ); // reserved
			v.Write( 1 ); // xSampling
			v.Write( 1 ); // ySampling
		}
		v.Write( (byte)0 );
		v.Flush();
		WriteAttributeHeader( w, "channels", "chlist", (int)value.Length );
		w.Write( value.GetBuffer(), 0, (int)value.Length );
	}

	static void WriteByteAttribute( BinaryWriter w, string name, byte value )
	{
		WriteAttributeHeader( w, name, name == "compression" ? "compression" : "lineOrder", 1 );
		w.Write( value );
	}

	static void WriteBoxAttribute( BinaryWriter w, string name, int xmin, int ymin, int xmax, int ymax )
	{
		WriteAttributeHeader( w, name, "box2i", 16 );
		w.Write( xmin );
		w.Write( ymin );
		w.Write( xmax );
		w.Write( ymax );
	}

	static void WriteFloatAttribute( BinaryWriter w, string name, float value )
	{
		WriteAttributeHeader( w, name, "float", 4 );
		w.Write( value );
	}

	static void WriteV2fAttribute( BinaryWriter w, string name, float x, float y )
	{
		WriteAttributeHeader( w, name, "v2f", 8 );
		w.Write( x );
		w.Write( y );
	}

	static void WriteAttributeHeader( BinaryWriter w, string name, string type, int size )
	{
		WriteCString( w, name );
		WriteCString( w, type );
		w.Write( size );
	}

	static void WriteCString( BinaryWriter w, string value )
	{
		w.Write( Encoding.ASCII.GetBytes( value ) );
		w.Write( (byte)0 );
	}

	static string ReadCString( BinaryReader r )
	{
		var bytes = new List<byte>();
		while ( true )
		{
			if ( r.BaseStream.Position >= r.BaseStream.Length )
				return null;
			var b = r.ReadByte();
			if ( b == 0 )
				return Encoding.ASCII.GetString( bytes.ToArray() );
			bytes.Add( b );
		}
	}

	static ushort FloatToHalf( float value )
	{
		if ( float.IsNaN( value ) )
			return 0x7E00;
		var bits = BitConverter.SingleToInt32Bits( value );
		var sign = (bits >> 16) & 0x8000;
		if ( float.IsInfinity( value ) )
			return (ushort)(sign | 0x7C00);
		var exp = ((bits >> 23) & 0xFF) - 112;
		var mant = bits & 0x7FFFFF;
		if ( exp >= 16 )
			return (ushort)(sign | 0x7C00);
		if ( exp <= 0 )
		{
			if ( exp < -10 )
				return (ushort)sign;
			// Subnormal: value = m * 2^-24 with m = round((2^23 + mant) * 2^(exp - 14)).
			var m = mant | 0x800000;
			var shift = 14 - exp;
			m = (m + (1 << (shift - 1))) >> shift;
			if ( m >= 1024 )
				return (ushort)(sign | (1 << 10));
			return (ushort)(sign | m);
		}
		// Normal, round to nearest: add half an ulp of the kept mantissa first.
		var rounded = mant + 0x1000;
		if ( (rounded & 0x800000) != 0 )
		{
			rounded = 0;
			exp += 1;
			if ( exp >= 16 )
				return (ushort)(sign | 0x7C00);
		}
		return (ushort)(sign | (exp << 10) | (rounded >> 13));
	}
}
