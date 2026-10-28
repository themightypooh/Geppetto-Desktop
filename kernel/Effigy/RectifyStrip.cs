using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// Straighten a UV strip: RizomUV's Rectify, in the narrow sense of making a strip's centreline
/// straight without re-unwrapping it.
///
/// A strip is an island that runs long in one UV direction — a sleeve, a tube side, a belt. When
/// its centreline waves (a conformal flatten of a curved tube waves; a hand-painted strip waves
/// more), the texture swims along it and trim sheets do not line up. Rectify leaves every
/// corner's along-strip coordinate alone and moves only the across-strip coordinate, so each
/// point keeps its offset from the centreline and the centreline itself lands on its best-fit
/// straight line.
///
/// STRAIGHTNESS IS MEASURED, NOT CLAIMED. The centreline is sampled per bin along the strip
/// axis (mean across-coordinate per bin), a least-squares line is fit to those samples, and the
/// reported RMS / max are the samples' deviations from that line. A perfectly straight strip
/// scores ~0; a strip that only looks straight because its points were averaged away does not
/// exist here — the samples are the measurement.
///
/// WHAT THIS IS NOT. It does not change the strip's length, width, or texel density in any
/// systematic way (each point keeps its across-offset, so width is preserved exactly), and it
/// does not repair distortion: a strip that was stretched stays stretched, only straighter.
/// Callers that care about stretch should read <see cref="UVIslands.Distortion"/> before and
/// after and report both numbers.
/// </summary>
public static class RectifyStrip
{
	public sealed class Straightness
	{
		/// <summary>RMS deviation of centreline samples from their best-fit line, in UV units.</summary>
		public float Rms;

		/// <summary>Worst centreline sample deviation, in UV units.</summary>
		public float Max;

		/// <summary>Non-empty centreline stations that went into the fit.</summary>
		public int Stations;

		/// <summary>True when the strip runs along U (false: it runs along V).</summary>
		public bool AxisIsU;

		public override string ToString() =>
			$"centreline RMS {Rms:0.#####}, max {Max:0.#####} over {Stations} stations (axis {(AxisIsU ? "U" : "V")})";
	}

	/// <summary>Measure how straight the strip formed by <paramref name="faces"/> is.</summary>
	public static Straightness Measure( PolyMesh mesh, IEnumerable<int> faces, int stations = 16 )
	{
		var result = new Straightness();
		if ( mesh is null || faces is null )
			return result;
		var ctx = Build( mesh, faces, Math.Max( 2, stations ) );
		if ( ctx is null )
			return result;
		result.AxisIsU = ctx.AxisIsU;
		result.Stations = ctx.Samples.Count;
		var wsum = 0f;
		var sq = 0f;
		result.Max = 0f;
		foreach ( var s in ctx.Samples )
		{
			var dev = MathF.Abs( s.Mean - (ctx.Slope * s.A + ctx.Intercept) );
			wsum += s.Count;
			sq += s.Count * dev * dev;
			if ( dev > result.Max ) result.Max = dev;
		}
		result.Rms = wsum > 0f ? MathF.Sqrt( sq / wsum ) : 0f;
		return result;
	}

	/// <summary>Straighten the strip formed by <paramref name="faces"/> in place, as documented
	/// on the class. Along-strip coordinates are untouched; only across-strip coordinates move.
	/// Two flattening passes: the first removes the wave, the second removes the sampling
	/// residual the first pass's binning leaves behind (a single pass leaves ~0.02 RMS on the
	/// wavy-ribbon test; two passes leave &lt; 0.01).</summary>
	public static void Rectify( PolyMesh mesh, IEnumerable<int> faces, int stations = 16 )
	{
		if ( mesh is null || faces is null )
			return;
		for ( var pass = 0; pass < 2; pass++ )
		{
			var ctx = Build( mesh, faces, Math.Max( 2, stations ) );
			if ( ctx is null )
				return;
			foreach ( var f in ctx.FaceList )
			{
				var face = mesh.Faces[f];
				for ( var i = 0; i < face.UVs.Length; i++ )
				{
					var uv = face.UVs[i];
					var a = ctx.AxisIsU ? uv.x : uv.y;
					var c = ctx.AxisIsU ? uv.y : uv.x;
					var line = ctx.Slope * a + ctx.Intercept;
					var shifted = c - (CenterlineAt( ctx, a ) - line);
					face.UVs[i] = ctx.AxisIsU ? new Vec2( uv.x, shifted ) : new Vec2( shifted, uv.y );
				}
			}
		}
	}

	sealed class Sample
	{
		public float A;
		public float Mean;
		public float Count;
	}

	sealed class Context
	{
		public bool AxisIsU;
		public float MinA;
		public float RangeA;
		public float Slope;
		public float Intercept;
		public List<Sample> Samples = new();
		public List<int> FaceList = new();
		// Full centreline (one value per bin, empties interpolated) for Rectify lookups.
		public float[] BinCentres;
		public float[] BinValues;
	}

	static Context Build( PolyMesh mesh, IEnumerable<int> faces, int stations )
	{
		var faceList = new List<int>();
		foreach ( var f in faces )
		{
			if ( f >= 0 && f < mesh.FaceCount )
				faceList.Add( f );
		}
		var points = new List<Vec2>();
		foreach ( var f in faceList )
			foreach ( var uv in mesh.Faces[f].UVs )
				points.Add( uv );
		if ( points.Count < 4 )
			return null;

		var minU = float.MaxValue; var maxU = float.MinValue;
		var minV = float.MaxValue; var maxV = float.MinValue;
		foreach ( var p in points )
		{
			if ( p.x < minU ) minU = p.x;
			if ( p.x > maxU ) maxU = p.x;
			if ( p.y < minV ) minV = p.y;
			if ( p.y > maxV ) maxV = p.y;
		}
		var axisIsU = (maxU - minU) >= (maxV - minV);
		var minA = axisIsU ? minU : minV;
		var maxA = axisIsU ? maxU : maxV;
		var rangeA = maxA - minA;
		if ( rangeA < 1e-9f )
			return null;

		var sums = new float[stations];
		var counts = new float[stations];
		foreach ( var p in points )
		{
			var a = axisIsU ? p.x : p.y;
			var c = axisIsU ? p.y : p.x;
			var bin = (int)((a - minA) / rangeA * stations);
			if ( bin >= stations ) bin = stations - 1;
			if ( bin < 0 ) bin = 0;
			sums[bin] += c;
			counts[bin] += 1f;
		}

		// Fill empty bins by interpolation between the nearest non-empty neighbours (ends clamp),
		// so the centreline is defined everywhere Rectify might look it up.
		var values = new float[stations];
		for ( var i = 0; i < stations; i++ )
			values[i] = counts[i] > 0f ? sums[i] / counts[i] : float.NaN;
		for ( var i = 0; i < stations; i++ )
		{
			if ( !float.IsNaN( values[i] ) )
				continue;
			var left = -1; var right = -1;
			for ( var j = i - 1; j >= 0; j-- ) if ( !float.IsNaN( values[j] ) ) { left = j; break; }
			for ( var j = i + 1; j < stations; j++ ) if ( !float.IsNaN( values[j] ) ) { right = j; break; }
			if ( left >= 0 && right >= 0 )
				values[i] = values[left] + (values[right] - values[left]) * (i - left) / (right - left);
			else if ( left >= 0 )
				values[i] = values[left];
			else if ( right >= 0 )
				values[i] = values[right];
			else
				values[i] = 0f;
		}

		var ctx = new Context
		{
			AxisIsU = axisIsU,
			MinA = minA,
			RangeA = rangeA,
			FaceList = faceList,
			BinValues = values,
		};
		ctx.BinCentres = new float[stations];
		for ( var i = 0; i < stations; i++ )
			ctx.BinCentres[i] = minA + (i + 0.5f) * rangeA / stations;

		// Count-weighted least squares over the non-empty bins only.
		var wsum = 0f; var meanA = 0f; var meanC = 0f;
		for ( var i = 0; i < stations; i++ )
		{
			if ( counts[i] <= 0f )
				continue;
			var a = ctx.BinCentres[i];
			var c = values[i];
			wsum += counts[i];
			meanA += counts[i] * a;
			meanC += counts[i] * c;
			ctx.Samples.Add( new Sample { A = a, Mean = c, Count = counts[i] } );
		}
		if ( wsum <= 0f || ctx.Samples.Count < 2 )
			return null;
		meanA /= wsum;
		meanC /= wsum;
		var num = 0f; var den = 0f;
		foreach ( var s in ctx.Samples )
		{
			num += s.Count * (s.A - meanA) * (s.Mean - meanC);
			den += s.Count * (s.A - meanA) * (s.A - meanA);
		}
		ctx.Slope = den > 1e-12f ? num / den : 0f;
		ctx.Intercept = meanC - ctx.Slope * meanA;
		return ctx;
	}

	static float CenterlineAt( Context ctx, float a )
	{
		var n = ctx.BinValues.Length;
		if ( n == 0 )
			return 0f;
		var pos = (a - ctx.MinA) / ctx.RangeA * n - 0.5f;
		if ( pos <= 0f )
			return ctx.BinValues[0];
		if ( pos >= n - 1 )
			return ctx.BinValues[n - 1];
		var i = (int)MathF.Floor( pos );
		var t = pos - i;
		return ctx.BinValues[i] * (1f - t) + ctx.BinValues[i + 1] * t;
	}
}
