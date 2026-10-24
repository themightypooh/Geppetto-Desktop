#nullable enable
using System;
using System.Numerics;

namespace Marionette.Desktop;

/// <summary>
/// Live layers for the two <see cref="ParticleRenderType"/> values that
/// <see cref="ParticleLayer"/> never drew: MeshInstance and Strand.
///
/// Pipeline constraint, stated plainly: <see cref="LiveBuilder"/> is a flat
/// vertex stream — it has no matrix channel. So the mesh-instance layer
/// CPU-expands the template mesh through
/// <see cref="ParticleSystem.InstanceMatrix"/> (scale·rotation·translation
/// per particle) and emits ordinary triangles. That is real mesh instancing
/// in effect, just expanded on the CPU; a future GPU-instancing renderer
/// can skip the expansion and upload <see cref="ParticleMeshInstanceLayer.LastMatrices"/>
/// directly. The strand layer turns <see cref="ParticleSystem.WriteStrand"/>
/// trails into camera-facing ribbons with the same math as
/// <see cref="StrandLayer"/>'s ribbon (side = cross(tangent, toEye), width
/// tapering toward the tail), reimplemented here because that ribbon is
/// private to <see cref="StrandLayer"/>.
///
/// Both layers step their system exactly like <see cref="ParticleLayer"/>:
/// a fixed-step accumulator capped at 8 steps per frame, so a slow frame
/// never spirals into catch-up steps. Neither layer needs a custom texture
/// or atlas: they draw with the layer's <see cref="LiveLayer.Material"/>
/// (leave Sprite off — these are triangles, not billboard discs).
/// </summary>

/// <summary>
/// One template mesh drawn once per live particle, transformed by
/// <see cref="ParticleSystem.InstanceMatrix"/> (so particle size and
/// rotation are honoured, unlike billboards). Template is a
/// <see cref="ParticleMesh"/> — the same triangle-soup type the system
/// already uses for mesh-surface emission and collision.
/// </summary>
public sealed class ParticleMeshInstanceLayer : LiveLayer
{
	public ParticleSystem System { get; }
	public ParticleMesh Template { get; }

	/// <summary>Same fade semantics as <see cref="ParticleLayer"/>: particles
	/// shrink toward their centre over the last <see cref="FadeOut"/> of
	/// life. 0 disables the fade.</summary>
	public float FadeOut { get; set; } = .3f;

	/// <summary>Matrices written by the last <see cref="Emit"/>, in
	/// back-to-front order, count <see cref="LastDrawn"/>.</summary>
	public Matrix4x4[] LastMatrices { get; private set; } = Array.Empty<Matrix4x4>();

	/// <summary>Particles the last <see cref="Emit"/> wrote matrices for.
	/// A particle faded fully to zero writes a matrix but no triangles.</summary>
	public int LastDrawn { get; private set; }

	float _accumulator;

	public ParticleMeshInstanceLayer( ParticleSystem system, ParticleMesh template )
	{
		System = system ?? throw new ArgumentNullException( nameof( system ) );
		Template = template ?? throw new ArgumentNullException( nameof( template ) );
	}

	public override void Advance( float dt )
	{
		_accumulator += dt;
		float step = System.Settings.FixedDeltaTime;
		int n = 0;
		while ( _accumulator >= step && n < 8 ) { System.Step(); _accumulator -= step; n++; }
		if ( n == 8 ) _accumulator = 0;
	}

	public override void Emit( LiveBuilder b, in LiveView view )
	{
		var order = System.SortBackToFront( view.Eye, view.Forward );
		if ( LastMatrices.Length < order.Length ) LastMatrices = new Matrix4x4[Math.Max( order.Length, LastMatrices.Length * 2 )];
		int count = System.WriteInstanceMatrices( order, LastMatrices );
		LastDrawn = count;
		var verts = Template.Vertices;
		var idx = Template.Indices;
		for ( int i = 0; i < count; i++ )
		{
			int slot = order[i];
			var m = LastMatrices[i];
			// Fade: shrink the expanded vertices toward the particle centre.
			float fade = 1f;
			if ( FadeOut > 0 )
			{
				var st = System.States[slot];
				float ageFrac = Math.Clamp( st.Age / System.LifetimeOf( slot ), 0f, 1f );
				fade = Math.Clamp( ( 1f - ageFrac ) / FadeOut, 0f, 1f );
			}
			if ( fade <= 0 ) continue;
			var centre = m.Translation;
			for ( int t = 0; t < Template.TriangleCount; t++ )
			{
				var a = Shrink( Vector3.Transform( verts[idx[t * 3]], m ), centre, fade );
				var bb = Shrink( Vector3.Transform( verts[idx[t * 3 + 1]], m ), centre, fade );
				var c = Shrink( Vector3.Transform( verts[idx[t * 3 + 2]], m ), centre, fade );
				var n = LiveBuilder.SafeNormalize( Vector3.Cross( bb - a, c - a ), -view.Forward );
				b.Triangle( a, bb, c, n, n, n );
			}
		}
	}

	static Vector3 Shrink( Vector3 p, Vector3 centre, float fade ) => centre + ( p - centre ) * fade;
}

/// <summary>
/// One camera-facing ribbon per live particle, from its
/// <see cref="ParticleSystem.WriteStrand"/> trail (newest point first, so
/// the ribbon is full-width at the particle's head and tapers by
/// <see cref="Taper"/> toward the tail). Ribbon width follows the
/// particle's size: head half-width = Size × <see cref="WidthScale"/> / 2.
/// </summary>
public sealed class ParticleStrandLayer : LiveLayer
{
	public ParticleSystem System { get; }

	/// <summary>Trail width as a multiple of the particle's Size.</summary>
	public float WidthScale { get; set; } = 1f;

	/// <summary>Tail width = head width × Taper (same convention as
	/// <see cref="StrandLayer"/>).</summary>
	public float Taper { get; set; } = .3f;

	/// <summary>Live particles the last <see cref="Emit"/> drew at least
	/// one ribbon quad for. A particle whose trail is still a single
	/// point (just spawned, not yet stepped) draws nothing and is not
	/// counted.</summary>
	public int LastDrawn { get; private set; }

	float _accumulator;
	Vector3[] _trail;

	public ParticleStrandLayer( ParticleSystem system )
	{
		System = system ?? throw new ArgumentNullException( nameof( system ) );
		if ( System.Settings.StrandPoints < 2 )
			throw new ArgumentException( "ParticleStrandLayer needs StrandPoints >= 2: the system was built without a strand history.", nameof( system ) );
		_trail = new Vector3[System.Settings.StrandPoints];
	}

	public override void Advance( float dt )
	{
		_accumulator += dt;
		float step = System.Settings.FixedDeltaTime;
		int n = 0;
		while ( _accumulator >= step && n < 8 ) { System.Step(); _accumulator -= step; n++; }
		if ( n == 8 ) _accumulator = 0;
	}

	public override void Emit( LiveBuilder b, in LiveView view )
	{
		int k = System.Settings.StrandPoints;
		if ( _trail.Length < k ) _trail = new Vector3[k];
		int drawn = 0;
		for ( int s = 0; s < System.HighWater; s++ )
		{
			if ( !System.IsAlive( s ) ) continue;
			int n = System.WriteStrand( s, _trail );
			if ( Ribbon( b, view, _trail.AsSpan( 0, n ), System.States[s].Size ) ) drawn++;
		}
		LastDrawn = drawn;
	}

	/// <summary>Camera-facing ribbon through <paramref name="pts"/>
	/// (newest first). Returns true when at least one quad was emitted.
	/// Segments between repeated points (a trail before birth repeats the
	/// spawn position) are skipped, not drawn as zero-length quads.</summary>
	bool Ribbon( LiveBuilder b, in LiveView view, ReadOnlySpan<Vector3> pts, float size )
	{
		if ( pts.Length < 2 ) return false;
		bool hasPrev = false;
		Vector3 prevL = default, prevR = default;
		bool any = false;
		for ( int i = 0; i < pts.Length; i++ )
		{
			var tangent = LiveBuilder.SafeNormalize( pts[Math.Min( i + 1, pts.Length - 1 )] - pts[Math.Max( i - 1, 0 )], Vector3.UnitZ );
			var toEye = LiveBuilder.SafeNormalize( view.Eye - pts[i], -view.Forward );
			var side = LiveBuilder.SafeNormalize( Vector3.Cross( tangent, toEye ), view.Right );
			float t = i / (float)( pts.Length - 1 );
			float half = size * WidthScale * .5f * ( 1f + ( Taper - 1f ) * t );
			Vector3 l = pts[i] - side * half, r = pts[i] + side * half;
			if ( !hasPrev ) { prevL = l; prevR = r; hasPrev = true; continue; }
			// Skip a segment that goes nowhere (repeated trail points).
			if ( ( pts[i] - pts[i - 1] ).LengthSquared() < 1e-12f ) continue;
			var n = LiveBuilder.SafeNormalize( Vector3.Cross( side, tangent ), toEye );
			if ( Vector3.Dot( n, toEye ) < 0 ) n = -n;
			b.Quad( prevL, prevR, r, l, n );
			prevL = l; prevR = r;
			any = true;
		}
		return any;
	}
}
