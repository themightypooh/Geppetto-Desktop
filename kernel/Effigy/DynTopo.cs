using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// Sculptris-Pro-style dynamic topology (DynTopo) for the Desktop sculpt stack:
/// local add/collapse under the brush, per-brush detail size, decimation on
/// Smooth, a hard triangle cap, and a BVH rebuild after every topology change.
///
/// This is the STROKE-LOCAL half of dynamic topology. The voxel half (coarse
/// remesh of the whole mesh) is the OpenVDB board task's job; DynTopo never
/// calls it. The seam is <see cref="DynTopoSession.VoxelRemesh"/>: null until
/// that task assigns its backend, and <see cref="DynTopoSession.ApplyVoxelRemesh"/>
/// refuses honestly until then.
///
/// Topology changes are DELTA-DESTRUCTIVE for per-vertex side data, the same
/// rule the modifier stack applies: skin weights, morphs, vertex colours,
/// vertex emission and UV2 are sized to the old vertex count, so DynTopo
/// refuses a mesh that carries them instead of silently corrupting it.
///
/// Sculpt layers are per-vertex deltas against the rest surface, so a topology
/// change cannot carry them. <see cref="DynTopoSession.ApplyStroke"/> and
/// <see cref="DynTopoSession.ApplySmooth"/> refuse (never silently proceed)
/// while the attached <see cref="SculptLayerStack"/> reports unbaked detail,
/// unless the caller sets <see cref="DynTopoOptions.AllowUnbakedLayers"/>.
///
/// The BVH is REBUILT (not merely refit) after every stroke that changes
/// topology: <see cref="MeshBVH.BuildOrRefit"/> detects the topology change
/// and builds a fresh tree. The old sculpting invariant — "the tree structure
/// stays valid because topology never changes" — does not hold under DynTopo,
/// by design.
///
/// HOOKUP (for the integrator — no file is edited by this ticket): the stroke
/// path calls <c>session.ApplyStroke(hit, session.Radius)</c> after the
/// displacement dab, and <c>session.ApplySmooth(...)</c> for a Smooth dab.
/// <see cref="DynTopoSession"/> is constructed once per sculpted mesh; the
/// session keeps the BVH across strokes. The likely hook sites are
/// <c>SculptSession</c> (stroke loop) and <c>Brush.cs</c> (per-brush detail
/// size); both belong to other workers, so this ticket only exposes the API.
/// </summary>
public sealed class DynTopoOptions
{
	/// <summary>Target edge length under the brush, in inches. Per-brush: the caller passes its own detail size per stroke.</summary>
	public float DetailSize = 0.05f;

	/// <summary>Hard triangle cap. Subdivision stops at the cap instead of growing past it; a runaway stroke cannot OOM the session.</summary>
	public int MaxTriangles = 1_000_000;

	/// <summary>Edges longer than DetailSize * this are subdivided.</summary>
	public float SubdivideLengthFactor = 1.5f;

	/// <summary>Edges shorter than DetailSize * this are collapsed by ApplySmooth.</summary>
	public float CollapseLengthFactor = 0.35f;

	/// <summary>Edges shorter than DetailSize * this are collapsed by ApplySmooth. Kept separate from CollapseLengthFactor so Smooth can be gentler than the stroke collapse.</summary>
	public float SmoothCollapseFactor = 0.5f;

	/// <summary>When false (the default), a stroke refuses while the attached sculpt-layer stack holds unbaked detail. When true, the caller accepts the loss and the stroke proceeds anyway.</summary>
	public bool AllowUnbakedLayers = false;

	/// <summary>Upper bound on subdivision passes per stroke. One pass splits every over-long edge once; later passes catch the new edges a split creates.</summary>
	public int MaxPasses = 8;

	public DynTopoOptions Clone() => (DynTopoOptions)MemberwiseClone();
}

/// <summary>The outcome of one DynTopo stroke. Counts are triangles where the name says so; Faces counts faces (quads count once).</summary>
public readonly struct DynTopoStrokeResult
{
	public readonly int FacesBefore;
	public readonly int FacesAfter;
	public readonly int VerticesBefore;
	public readonly int VerticesAfter;
	public readonly int TrianglesBefore;
	public readonly int TrianglesAfter;
	public readonly int EdgesSplit;
	public readonly int EdgesCollapsed;
	public readonly int FacesAdded;
	public readonly int FacesRemoved;
	public readonly bool Capped;
	public readonly bool Refused;
	public readonly string Warning;
	public readonly bool BvhRebuilt;
	public readonly int Refits;

	public DynTopoStrokeResult( int facesBefore, int facesAfter, int verticesBefore, int verticesAfter,
		int trianglesBefore, int trianglesAfter, int edgesSplit, int edgesCollapsed,
		int facesAdded, int facesRemoved, bool capped, bool refused, string warning,
		bool bvhRebuilt, int refits )
	{
		FacesBefore = facesBefore;
		FacesAfter = facesAfter;
		VerticesBefore = verticesBefore;
		VerticesAfter = verticesAfter;
		TrianglesBefore = trianglesBefore;
		TrianglesAfter = trianglesAfter;
		EdgesSplit = edgesSplit;
		EdgesCollapsed = edgesCollapsed;
		FacesAdded = facesAdded;
		FacesRemoved = facesRemoved;
		Capped = capped;
		Refused = refused;
		Warning = warning;
		BvhRebuilt = bvhRebuilt;
		Refits = refits;
	}
}

/// <summary>
/// Stroke-local dynamic topology over one <see cref="PolyMesh"/>. The session
/// owns the mesh's BVH across strokes and rebuilds it whenever a stroke
/// changes topology. All topology changes happen in place on the session mesh,
/// except <see cref="ApplySmooth"/> and <see cref="DecimateToTarget"/>, which
/// adopt a rebuilt mesh (see <see cref="PolyEdit.CollapseEdges"/> and
/// <see cref="Decimate.Run"/>).
/// </summary>
public sealed class DynTopoSession
{
	readonly PolyMesh _mesh;
	MeshBVH _bvh;
	readonly DynTopoOptions _options;

	public DynTopoSession( PolyMesh mesh, DynTopoOptions options = null )
	{
		_mesh = mesh ?? throw new ArgumentNullException( nameof( mesh ) );
		_options = options ?? new DynTopoOptions();
		ValidateOptions( _options );
		AssertNoSideData( _mesh );
		_bvh = MeshBVH.Build( _mesh );
	}

	public PolyMesh Mesh => _mesh;
	public DynTopoOptions Options => _options;
	public MeshBVH Bvh => _bvh;
	public int TriangleCount => Decimate.TriangleCount( _mesh );

	/// <summary>
	/// VOXEL SEAM. The coarse whole-mesh remesh backend, assigned by the
	/// OpenVDB board task when it lands (voxel remesh + Instant Meshes retopo;
	/// the retopo evaluation picked Instant Meshes). Null until then.
	/// Never assigned by DynTopo itself: DynTopo is the stroke-local half and
	/// must not re-implement voxel remesh.
	/// </summary>
	public Func<PolyMesh, PolyMesh> VoxelRemesh { get; set; }

	/// <summary>The sculpt-layer bake warning for a topology change, or null when there is nothing to lose.</summary>
	public string CheckLayers( SculptLayerStack layers ) => layers is null ? null : layers.TopologyChangeWarning();

	/// <summary>A deep snapshot for undo: restore it with <see cref="Restore"/>.</summary>
	public PolyMesh Snapshot() => _mesh.Clone();

	/// <summary>Restore a snapshot taken by <see cref="Snapshot"/>. The BVH is rebuilt; the restore is exact (positions and faces are copied, not re-derived).</summary>
	public void Restore( PolyMesh snapshot )
	{
		if ( snapshot is null ) throw new ArgumentNullException( nameof( snapshot ) );
		Adopt( snapshot, copySideData: true );
		RebuildBvh();
	}

	/// <summary>
	/// One DynTopo stroke: subdivide every over-long edge of every face whose
	/// region the brush touches, until every edge under the brush is at detail
	/// size (or the pass budget / triangle cap stops it). Existing vertices
	/// never move — subdivision only adds midpoint vertices — so geometry
	/// outside the brush is bit-identical afterwards. Quads under the brush
	/// are fan-split into triangles first; nothing outside the brush is touched.
	/// </summary>
	public DynTopoStrokeResult ApplyStroke( Vec3 center, float radius, DynTopoOptions options = null, SculptLayerStack layers = null )
	{
		var o = options ?? _options;
		ValidateOptions( o );
		ValidateStroke( center, radius );
		AssertNoSideData( _mesh );
		var facesBefore = _mesh.FaceCount;
		var vertsBefore = _mesh.VertexCount;
		var trisBefore = TriangleCount;
		var warning = CheckLayers( layers );
		if ( warning is not null && !o.AllowUnbakedLayers )
			return new DynTopoStrokeResult( facesBefore, facesBefore, vertsBefore, vertsBefore,
				trisBefore, trisBefore, 0, 0, 0, 0, false, true, warning, false, _bvh.Refits );

		var region = RegionFaces( center, radius );
		var added = FanSplitRegion( region );
		var splitEdges = 0;
		var capped = false;
		var changed = added > 0;
		for ( var pass = 0; pass < o.MaxPasses; pass++ )
		{
			var addedThis = SplitLongEdges( region, o, out var passCapped, out var splitThis );
			if ( addedThis <= 0 )
			{
				capped = passCapped;
				break;
			}
			added += addedThis;
			splitEdges += splitThis;
			changed = true;
			if ( passCapped )
			{
				capped = true;
				break;
			}
		}
		var rebuilt = false;
		if ( changed )
		{
			_mesh.InvalidateVertexCache();
			rebuilt = RebuildBvh();
		}
		return new DynTopoStrokeResult( facesBefore, _mesh.FaceCount, vertsBefore, _mesh.VertexCount,
			trisBefore, TriangleCount, splitEdges, 0, added, 0, capped, false, warning, rebuilt, _bvh.Refits );
	}

	/// <summary>
	/// Decimation on Smooth: collapse every short edge under the brush (edges
	/// shorter than DetailSize * SmoothCollapseFactor, measured at the edge
	/// midpoint). Implemented on <see cref="PolyEdit.CollapseEdges"/> — the
	/// same union-find midpoint weld the poly-edit tools use — so there is
	/// exactly one collapse implementation. Faces that end up with fewer than
	/// three distinct vertices are dropped; unreferenced vertices are compacted.
	/// </summary>
	public DynTopoStrokeResult ApplySmooth( Vec3 center, float radius, DynTopoOptions options = null, SculptLayerStack layers = null )
	{
		var o = options ?? _options;
		ValidateOptions( o );
		ValidateStroke( center, radius );
		AssertNoSideData( _mesh );
		var facesBefore = _mesh.FaceCount;
		var vertsBefore = _mesh.VertexCount;
		var trisBefore = TriangleCount;
		var warning = CheckLayers( layers );
		if ( warning is not null && !o.AllowUnbakedLayers )
			return new DynTopoStrokeResult( facesBefore, facesBefore, vertsBefore, vertsBefore,
				trisBefore, trisBefore, 0, 0, 0, 0, false, true, warning, false, _bvh.Refits );

		var region = RegionFaces( center, radius );
		var threshold = o.DetailSize * o.SmoothCollapseFactor;
		var edges = new HashSet<EdgeKey>();
		foreach ( var fi in region )
		{
			if ( fi < 0 || fi >= _mesh.FaceCount ) continue;
			var f = _mesh.Faces[fi];
			for ( var k = 0; k < f.Count; k++ )
			{
				var a = f.Indices[k];
				var b = f.Indices[( k + 1 ) % f.Count];
				var mid = ( _mesh.Positions[a] + _mesh.Positions[b] ) * 0.5f;
				if ( ( mid - center ).Length > radius ) continue;
				if ( ( _mesh.Positions[a] - _mesh.Positions[b] ).Length < threshold )
					edges.Add( new EdgeKey( a, b ) );
			}
		}
		if ( edges.Count == 0 )
			return new DynTopoStrokeResult( facesBefore, facesBefore, vertsBefore, vertsBefore,
				trisBefore, trisBefore, 0, 0, 0, 0, false, false, warning, false, _bvh.Refits );

		var collapsed = PolyEdit.CollapseEdges( _mesh, edges );
		var removed = facesBefore - collapsed.FaceCount;
		Adopt( collapsed, copySideData: false );
		var rebuilt = RebuildBvh();
		return new DynTopoStrokeResult( facesBefore, _mesh.FaceCount, vertsBefore, _mesh.VertexCount,
			trisBefore, TriangleCount, 0, edges.Count, 0, removed, false, false, warning, rebuilt, _bvh.Refits );
	}

	/// <summary>
	/// Whole-mesh decimation to a triangle target, on <see cref="Decimate.Run"/>
	/// (quadric error collapse). This is the global decimation path — the
	/// "decimation on Smooth" stroke path is <see cref="ApplySmooth"/>, which
	/// is local. The decimation-preset / LOD-chain UI of the finish ticket
	/// builds on this; the decimation core itself lives in Decimate.cs and is
	/// shared with godlike-decimation-projection, not duplicated here.
	/// </summary>
	public Decimate.Result DecimateToTarget( int targetTriangles )
	{
		if ( targetTriangles < 4 )
			throw new ArgumentOutOfRangeException( nameof( targetTriangles ), "A decimation target below 4 triangles cannot enclose anything." );
		AssertNoSideData( _mesh );
		var result = Decimate.Run( _mesh, new Decimate.Options { TargetTriangles = targetTriangles } );
		Adopt( result.Mesh, copySideData: false );
		RebuildBvh();
		return result;
	}

	/// <summary>
	/// The voxel seam: run the installed coarse remesh backend over the whole
	/// mesh and adopt the result. Refuses honestly while no backend is
	/// installed — DynTopo never implements voxel remesh itself.
	/// </summary>
	public PolyMesh ApplyVoxelRemesh()
	{
		if ( VoxelRemesh is null )
			throw new InvalidOperationException( "No voxel remesh backend is installed. The OpenVDB board task (integrate-openvdb-remesh-and-instant-meshes-retopo-backends) owns the voxel half; assign DynTopoSession.VoxelRemesh when it lands. DynTopo itself never implements voxel remesh." );
		AssertNoSideData( _mesh );
		var result = VoxelRemesh( _mesh );
		if ( result is null )
			throw new InvalidOperationException( "The voxel remesh backend returned null." );
		Adopt( result, copySideData: false );
		RebuildBvh();
		return _mesh;
	}

	// --- region selection ---------------------------------------------------

	HashSet<int> RegionFaces( Vec3 center, float radius )
	{
		var found = new List<int>();
		_bvh.FacesInRadius( _mesh, center, radius, found );
		return new HashSet<int>( found );
	}

	// --- fan-split ----------------------------------------------------------

	/// <summary>Fan-split every non-triangle face in the region into triangles (from corner 0, keeping UVs and material). Returns the net faces added. The region set is rewritten to the new indices.</summary>
	int FanSplitRegion( HashSet<int> region )
	{
		if ( region.Count == 0 ) return 0;
		var old = _mesh.Faces;
		var next = new List<Face>( old.Count + region.Count );
		var nextRegion = new HashSet<int>();
		var added = 0;
		for ( var i = 0; i < old.Count; i++ )
		{
			var f = old[i];
			if ( !region.Contains( i ) || f.Count <= 3 )
			{
				if ( region.Contains( i ) ) nextRegion.Add( next.Count );
				next.Add( f );
				continue;
			}
			for ( var k = 1; k + 1 < f.Count; k++ )
			{
				next.Add( new Face(
					new[] { f.Indices[0], f.Indices[k], f.Indices[k + 1] },
					new[] { f.UVs[0], f.UVs[k], f.UVs[k + 1] },
					f.Material ) );
				nextRegion.Add( next.Count - 1 );
			}
			added += f.Count - 3;
		}
		_mesh.Faces.Clear();
		_mesh.Faces.AddRange( next );
		region.Clear();
		foreach ( var i in nextRegion ) region.Add( i );
		return added;
	}

	// --- edge subdivision ---------------------------------------------------

	/// <summary>
	/// One subdivision pass over the region: split every edge longer than
	/// DetailSize * SubdivideLengthFactor, longest first, until the triangle
	/// cap stops it. An edge is only split when EVERY face sharing it is in
	/// the region — otherwise the neighbour would keep the un-split edge and
	/// the mesh would gain a T-junction crack. The region set is rewritten to
	/// the new face indices. Returns faces added; edgesSplit counts the edges
	/// actually split; capped is true when the cap, not the geometry, stopped
	/// the pass.
	/// </summary>
	int SplitLongEdges( HashSet<int> region, DynTopoOptions o, out bool capped, out int edgesSplit )
	{
		capped = false;
		edgesSplit = 0;
		var threshold = o.DetailSize * o.SubdivideLengthFactor;
		var edgeFaces = _mesh.BuildEdgeFaces();

		var lengths = new Dictionary<EdgeKey, float>();
		foreach ( var fi in region )
		{
			if ( fi < 0 || fi >= _mesh.FaceCount ) continue;
			var f = _mesh.Faces[fi];
			if ( f.Count != 3 ) continue;
			for ( var k = 0; k < 3; k++ )
			{
				var a = f.Indices[k];
				var b = f.Indices[( k + 1 ) % 3];
				var key = new EdgeKey( a, b );
				if ( lengths.ContainsKey( key ) ) continue;
				lengths[key] = ( _mesh.Positions[a] - _mesh.Positions[b] ).Length;
			}
		}
		var candidates = new List<(EdgeKey key, float len)>();
		foreach ( var kv in lengths )
			if ( kv.Value > threshold ) candidates.Add( (kv.Key, kv.Value) );
		if ( candidates.Count == 0 ) return 0;
		candidates.Sort( ( x, y ) =>
		{
			var byLen = y.len.CompareTo( x.len );
			if ( byLen != 0 ) return byLen;
			var byA = x.key.A.CompareTo( y.key.A );
			return byA != 0 ? byA : x.key.B.CompareTo( y.key.B );
		} );

		// Budget in triangles. Splitting one edge adds exactly one triangle
		// per incident face (a triangle with k split edges becomes k+1).
		var budget = o.MaxTriangles - TriangleCount;
		if ( budget <= 0 )
		{
			capped = true;
			return 0;
		}
		var selected = new List<EdgeKey>();
		foreach ( var c in candidates )
		{
			if ( !edgeFaces.TryGetValue( c.key, out var faces ) ) continue;
			var inRegion = true;
			foreach ( var fi in faces )
				if ( !region.Contains( fi ) ) { inRegion = false; break; }
			if ( !inRegion ) continue;
			if ( faces.Count > budget ) continue;
			selected.Add( c.key );
			budget -= faces.Count;
		}
		if ( selected.Count == 0 )
		{
			capped = true;
			return 0;
		}

		// Midpoint vertices, in deterministic order.
		selected.Sort( ( x, y ) => x.A != y.A ? x.A.CompareTo( y.A ) : x.B.CompareTo( y.B ) );
		var midpoint = new Dictionary<EdgeKey, int>();
		foreach ( var e in selected )
		{
			var p = ( _mesh.Positions[e.A] + _mesh.Positions[e.B] ) * 0.5f;
			midpoint[e] = _mesh.Positions.Count;
			_mesh.AddVertex( p );
		}

		var splitFaces = new HashSet<int>();
		foreach ( var e in selected )
			foreach ( var fi in edgeFaces[e] ) splitFaces.Add( fi );

		var next = new List<Face>( _mesh.FaceCount + selected.Count * 2 );
		var nextRegion = new HashSet<int>();
		var added = 0;
		for ( var i = 0; i < _mesh.FaceCount; i++ )
		{
			var f = _mesh.Faces[i];
			if ( !splitFaces.Contains( i ) )
			{
				if ( region.Contains( i ) ) nextRegion.Add( next.Count );
				next.Add( f );
				continue;
			}
			var rebuilt = RebuildSplitTriangle( f, midpoint );
			foreach ( var t in rebuilt )
			{
				next.Add( t );
				nextRegion.Add( next.Count - 1 );
			}
			added += rebuilt.Count - 1;
		}
		_mesh.Faces.Clear();
		_mesh.Faces.AddRange( next );
		region.Clear();
		foreach ( var i in nextRegion ) region.Add( i );
		edgesSplit = selected.Count;
		return added;
	}

	/// <summary>
	/// Rebuild one triangle whose edges were split, keeping winding and
	/// interpolating corner UVs. One split edge yields 2 triangles, two yield
	/// 3, three yield 4 (the central midpoint triangle keeps the parent's
	/// orientation). A face with no split edge is returned unchanged.
	/// </summary>
	static List<Face> RebuildSplitTriangle( Face f, Dictionary<EdgeKey, int> midpoint )
	{
		// Defensive: callers only ever pass triangles, but a non-triangle
		// must never lose corners here — return it unchanged instead.
		if ( f.Count != 3 ) return new List<Face> { f };
		var a = f.Indices[0];
		var b = f.Indices[1];
		var c = f.Indices[2];
		var uva = f.UVs[0];
		var uvb = f.UVs[1];
		var uvc = f.UVs[2];
		var hasAb = midpoint.TryGetValue( new EdgeKey( a, b ), out var mAb );
		var hasBc = midpoint.TryGetValue( new EdgeKey( b, c ), out var mBc );
		var hasCa = midpoint.TryGetValue( new EdgeKey( c, a ), out var mCa );
		var out_ = new List<Face>();
		Face Tri( int i0, int i1, int i2, Vec2 t0, Vec2 t1, Vec2 t2 ) =>
			new Face( new[] { i0, i1, i2 }, new[] { t0, t1, t2 }, f.Material );
		if ( !hasAb && !hasBc && !hasCa )
		{
			out_.Add( f );
			return out_;
		}
		Vec2 Mid( Vec2 u, Vec2 v ) => new Vec2( ( u.x + v.x ) * 0.5f, ( u.y + v.y ) * 0.5f );
		var umAb = Mid( uva, uvb );
		var umBc = Mid( uvb, uvc );
		var umCa = Mid( uvc, uva );
		var splits = ( hasAb ? 1 : 0 ) + ( hasBc ? 1 : 0 ) + ( hasCa ? 1 : 0 );
		if ( splits == 1 )
		{
			if ( hasAb )
			{
				out_.Add( Tri( a, mAb, c, uva, umAb, uvc ) );
				out_.Add( Tri( mAb, b, c, umAb, uvb, uvc ) );
			}
			else if ( hasBc )
			{
				out_.Add( Tri( a, b, mBc, uva, uvb, umBc ) );
				out_.Add( Tri( a, mBc, c, uva, umBc, uvc ) );
			}
			else
			{
				out_.Add( Tri( a, b, mCa, uva, uvb, umCa ) );
				out_.Add( Tri( mCa, b, c, umCa, uvb, uvc ) );
			}
			return out_;
		}
		if ( splits == 2 )
		{
			if ( !hasCa )
			{
				out_.Add( Tri( a, mAb, c, uva, umAb, uvc ) );
				out_.Add( Tri( mAb, b, mBc, umAb, uvb, umBc ) );
				out_.Add( Tri( mAb, mBc, c, umAb, umBc, uvc ) );
			}
			else if ( !hasAb )
			{
				// bc and ca split, ab untouched: the corner at c keeps its own
				// triangle (mBc, c, mCa); the remaining quad (a, b, mBc, mCa) splits
				// on (b, mCa). The old version built (a, b, mBc) + (a, mBc, mCa),
				// which double-covers the region near (b, mBc) — that edge ends up
				// in 3 faces, non-manifold — and leaves the c-corner uncovered (a
				// hole). Both change the Euler characteristic.
				out_.Add( Tri( a, b, mCa, uva, uvb, umCa ) );
				out_.Add( Tri( b, mBc, mCa, uvb, umBc, umCa ) );
				out_.Add( Tri( mBc, c, mCa, umBc, uvc, umCa ) );
			}
			else
			{
				out_.Add( Tri( a, mAb, mCa, uva, umAb, umCa ) );
				out_.Add( Tri( mAb, b, c, umAb, uvb, uvc ) );
				out_.Add( Tri( mAb, c, mCa, umAb, uvc, umCa ) );
			}
			return out_;
		}
		out_.Add( Tri( a, mAb, mCa, uva, umAb, umCa ) );
		out_.Add( Tri( mAb, b, mBc, umAb, uvb, umBc ) );
		out_.Add( Tri( mCa, mBc, c, umCa, umBc, uvc ) );
		out_.Add( Tri( mAb, mBc, mCa, umAb, umBc, umCa ) );
		return out_;
	}

	// --- mesh adoption / BVH ------------------------------------------------

	/// <summary>Replace the session mesh's contents with another mesh's. Used for collapse/decimate/voxel results, which are rebuilt meshes.</summary>
	void Adopt( PolyMesh other, bool copySideData )
	{
		if ( ReferenceEquals( other, _mesh ) )
		{
			_mesh.InvalidateVertexCache();
			return;
		}
		_mesh.Positions.Clear();
		_mesh.Positions.AddRange( other.Positions );
		_mesh.Faces.Clear();
		foreach ( var f in other.Faces )
			_mesh.Faces.Add( new Face( (int[])f.Indices.Clone(), (Vec2[])f.UVs.Clone(), f.Material ) );
		_mesh.UV2.Clear();
		if ( other.UV2 is not null ) _mesh.UV2.AddRange( other.UV2 );
		_mesh.Seams.Clear();
		foreach ( var s in other.Seams ) _mesh.Seams.Add( s );
		if ( copySideData )
		{
			_mesh.VertexColors = other.VertexColors is null ? null : (Vec4[])other.VertexColors.Clone();
			_mesh.VertexEmission = other.VertexEmission is null ? null : (Vec3[])other.VertexEmission.Clone();
			_mesh.Skin = other.Skin;
			_mesh.Morphs.Clear();
			foreach ( var m in other.Morphs ) _mesh.Morphs.Add( m.Clone() );
			_mesh.Paint = other.Paint is null ? null : other.Paint.Clone();
		}
		else
		{
			// Collapse/decimate/voxel rebuilds drop per-vertex side data (the
			// same rule PolyEdit documents); paint is UV-space and survives
			// on the session mesh, which this adopt does not touch.
			_mesh.VertexColors = null;
			_mesh.VertexEmission = null;
			_mesh.Skin = null;
			_mesh.Morphs.Clear();
		}
		_mesh.InvalidateVertexCache();
	}

	/// <summary>Rebuild-or-refit the BVH after a topology change. Returns true when a fresh tree was built (topology changed).</summary>
	bool RebuildBvh()
	{
		var before = _bvh;
		_bvh = MeshBVH.BuildOrRefit( _bvh, _mesh );
		return !ReferenceEquals( before, _bvh );
	}

	// --- validation ---------------------------------------------------------

	static void ValidateOptions( DynTopoOptions o )
	{
		if ( o is null ) throw new ArgumentNullException( nameof( o ) );
		if ( !float.IsFinite( o.DetailSize ) || o.DetailSize <= 0f )
			throw new ArgumentOutOfRangeException( nameof( o.DetailSize ), "DetailSize must be a finite positive length in inches." );
		if ( o.MaxTriangles < 4 )
			throw new ArgumentOutOfRangeException( nameof( o.MaxTriangles ), "MaxTriangles below 4 cannot enclose anything." );
		if ( !float.IsFinite( o.SubdivideLengthFactor ) || o.SubdivideLengthFactor <= 0f )
			throw new ArgumentOutOfRangeException( nameof( o.SubdivideLengthFactor ), "SubdivideLengthFactor must be finite and positive." );
		if ( !float.IsFinite( o.CollapseLengthFactor ) || o.CollapseLengthFactor <= 0f )
			throw new ArgumentOutOfRangeException( nameof( o.CollapseLengthFactor ), "CollapseLengthFactor must be finite and positive." );
		if ( !float.IsFinite( o.SmoothCollapseFactor ) || o.SmoothCollapseFactor <= 0f )
			throw new ArgumentOutOfRangeException( nameof( o.SmoothCollapseFactor ), "SmoothCollapseFactor must be finite and positive." );
		if ( o.MaxPasses < 1 )
			throw new ArgumentOutOfRangeException( nameof( o.MaxPasses ), "MaxPasses must be at least 1." );
	}

	static void ValidateStroke( Vec3 center, float radius )
	{
		if ( !float.IsFinite( center.x ) || !float.IsFinite( center.y ) || !float.IsFinite( center.z ) )
			throw new ArgumentException( "Stroke center must be finite.", nameof( center ) );
		if ( !float.IsFinite( radius ) || radius <= 0f )
			throw new ArgumentOutOfRangeException( nameof( radius ), "Stroke radius must be finite and positive." );
	}

	static void AssertNoSideData( PolyMesh mesh )
	{
		if ( mesh.IsRigged )
			throw new InvalidOperationException( "DynTopo cannot change topology on a rigged mesh: skin weights are sized to the old vertex count. Bake or unbind the skin first." );
		if ( mesh.HasMorphs )
			throw new InvalidOperationException( "DynTopo cannot change topology on a mesh with morphs: morph deltas are sized to the old vertex count. Bake or remove the morphs first." );
		if ( mesh.HasVertexColors )
			throw new InvalidOperationException( "DynTopo cannot change topology on a mesh with vertex colours: the colour array is sized to the old vertex count." );
		if ( mesh.HasVertexEmission )
			throw new InvalidOperationException( "DynTopo cannot change topology on a mesh with vertex emission: the emission array is sized to the old vertex count." );
		if ( mesh.HasUV2 )
			throw new InvalidOperationException( "DynTopo cannot change topology on a mesh with UV2: the second UV set is per-vertex and new vertices would have no entry." );
	}
}
