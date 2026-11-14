using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Effigy;
using Effigy.Assembly;

namespace Marionette.Desktop;

// AS04: transformed-occurrence snapshots, picking and render meshes.
//
// The snapshot is the one place an assembly's occurrences become geometry:
// each visible occurrence is resolved through a caller-supplied delegate,
// cloned, and transformed exactly once. The document, the definitions and
// every source mesh are never mutated, and the transformed clone is cached
// per occurrence until its cache key changes (source revision, transform,
// visibility, name, definition, or occurrence id).
//
// Transform: kernel MeshTransform.Transformed (Clone + Apply). It moves
// positions once, carries UVs and per-face material slots through Clone,
// and reverses face winding when the transform flips handedness, so a
// mirrored occurrence renders and exports outward. PolyMesh carries no
// stored normals — ComputeVertexNormals derives them from positions — so
// transforming the positions once IS the whole transform; there is no
// second normal array to move or forget.
//
// Picking: one MeshPicker per snapshot build, one part per occurrence with
// geometry, in snapshot order. Pick returns the OCCURRENCE id, never the
// definition id. Render: one CpuMesh per occurrence with geometry via
// CpuMesh.FromPolyMesh, in document order. The integrator calls these
// from the render/serve path; this file does not touch either.
//
// Missing source (resolver returns null, definition gone, or the stored
// transform unusable): the occurrence stays in the snapshot with
// MissingSource set and no mesh, is listed in MissingOccurrenceIds, and
// contributes no geometry, picking or render mesh. Its record stays in
// the document. Inches, Z-up, radians. Not thread-safe; call from one thread.

/// <summary>How one occurrence's source geometry arrives. AS02 owns source
/// resolution; this is a caller-supplied delegate, not a competing resolver
/// type. A null return means the source is missing or suppressed: the
/// occurrence is recorded as missing, never deleted, never a crash.
/// The returned mesh is the shared source and is never mutated.</summary>
public delegate (PolyMesh Mesh, int Revision, string MaterialName)? AssemblySourceResolver(AssemblyPartDefinition definition);

public sealed class AssemblyOccurrenceSnapshot
{
	public string OccurrenceId { get; set; } = "";
	public string DefinitionId { get; set; } = "";
	public string Name { get; set; } = "";

	/// <summary>Transformed CLONE of the source mesh. Null when the source
	/// did not resolve (see <see cref="MissingSource"/>).</summary>
	public PolyMesh Mesh { get; set; }

	public string MaterialName { get; set; } = "";

	/// <summary>Source revision the mesh was built from, or -1 when the
	/// source did not resolve.</summary>
	public int SourceRevision { get; set; }

	/// <summary>True when the source did not resolve or the stored transform
	/// was unusable: the occurrence has no geometry in this snapshot.</summary>
	public bool MissingSource { get; set; }

	public Vec3 BoundsMin { get; set; }
	public Vec3 BoundsMax { get; set; }

	/// <summary>True when the occurrence transform has a negative
	/// determinant (a mirror): face winding was reversed on the clone.</summary>
	public bool Mirrored { get; set; }
}

public sealed class AssemblySnapshot
{
	/// <summary>Visible occurrences in document order, including
	/// missing-source ones (MissingSource set, Mesh null).</summary>
	public IReadOnlyList<AssemblyOccurrenceSnapshot> Occurrences { get; set; } = Array.Empty<AssemblyOccurrenceSnapshot>();

	/// <summary>Ids of visible occurrences with no usable geometry.</summary>
	public IReadOnlyList<string> MissingOccurrenceIds { get; set; } = Array.Empty<string>();

	/// <summary>Visible occurrences in the document, including
	/// missing-source ones.</summary>
	public int VisibleCount { get; set; }

	/// <summary>Union of the visible occurrences' bounds, or
	/// (0,0,0)-(0,0,0) when none has geometry.</summary>
	public (Vec3 Min, Vec3 Max) Bounds { get; set; } = (Vec3.Zero, Vec3.Zero);

	/// <summary>Picker built with the snapshot: one part per occurrence
	/// with geometry, in Occurrences order. Null for a hand-built snapshot;
	/// Pick builds it on demand.</summary>
	internal MeshPicker Picker { get; set; }

	/// <summary>Picker part index -&gt; index into Occurrences.</summary>
	internal List<int> PickerOccurrenceIndex { get; set; }
}

public sealed class AssemblySnapshotService
{
	public int CacheHits { get; private set; }
	public int CacheMisses { get; private set; }

	AssemblySnapshot _cached;
	List<string> _cachedKeys;
	readonly Dictionary<string, (string Key, AssemblyOccurrenceSnapshot Snapshot)> _byOccurrenceId = new();

	/// <summary>Full rebuild from the document: every visible occurrence
	/// gets a fresh transformed clone. Refreshes the cache without
	/// touching the hit/miss counters (it is an explicit rebuild, not a
	/// cache lookup).</summary>
	public AssemblySnapshot Build( AssemblyDocument doc, AssemblySourceResolver source )
	{
		var resolved = ResolveAll( doc, source );
		var snapshot = BuildSnapshot( resolved, reuse: false );
		_cached = snapshot;
		_cachedKeys = resolved.Select( r => r.Key ).ToList();
		return snapshot;
	}

	/// <summary>Returns the cached snapshot when every visible
	/// occurrence's key still matches (definition source revision,
	/// occurrence transform, visibility, name, definition, id, in
	/// document order); otherwise rebuilds only the occurrences whose
	/// keys changed and reuses the rest, mesh object included.</summary>
	public AssemblySnapshot GetOrBuild( AssemblyDocument doc, AssemblySourceResolver source )
	{
		var resolved = ResolveAll( doc, source );
		var keys = resolved.Select( r => r.Key ).ToList();

		if ( _cached != null && _cachedKeys != null && _cachedKeys.Count == keys.Count
			&& _cachedKeys.SequenceEqual( keys ) )
		{
			CacheHits++;
			return _cached;
		}

		CacheMisses++;
		var snapshot = BuildSnapshot( resolved, reuse: true );
		_cached = snapshot;
		_cachedKeys = keys;
		return snapshot;
	}

	public void Invalidate()
	{
		_cached = null;
		_cachedKeys = null;
		_byOccurrenceId.Clear();
	}

	/// <summary>Exact triangle picking over the snapshot's visible
	/// geometry. Returns the OCCURRENCE id and the ray distance (the ray
	/// parameter t; world units when the direction is unit-length), or
	/// null when the ray hits nothing. Nearest hit wins.</summary>
	public (string OccurrenceId, float Distance)? Pick( AssemblySnapshot snapshot, Vec3 origin, Vec3 direction )
	{
		if ( snapshot is null )
			return null;
		if ( direction.LengthSquared < 1e-12f )
			return null;

		EnsurePicker( snapshot );
		if ( snapshot.Picker is null || snapshot.Picker.Count == 0 )
			return null;

		var o = new Vector3( origin.x, origin.y, origin.z );
		var d = new Vector3( direction.x, direction.y, direction.z );
		var part = snapshot.Picker.Pick( o, d, ReadOnlySpan<Matrix4x4>.Empty );
		if ( part < 0 || snapshot.PickerOccurrenceIndex is null || part >= snapshot.PickerOccurrenceIndex.Count )
			return null;

		var occurrence = snapshot.Occurrences[snapshot.PickerOccurrenceIndex[part]];

		// MeshPicker.Pick found the nearest part but reports no distance;
		// the same triangles, tested with the same Moller-Trumbore math,
		// give the nearest distance inside that part, which is the global
		// nearest hit because Pick already established this part holds it.
		var nearest = float.MaxValue;
		snapshot.Picker.Triangles( part, ReadOnlySpan<Matrix4x4>.Empty, ( a, b, c ) =>
		{
			var t = RayTriangle( o, d, a, b, c );
			if ( t >= 0f && t < nearest )
				nearest = t;
		} );
		if ( nearest == float.MaxValue )
			return null;

		return (occurrence.OccurrenceId, nearest);
	}

	/// <summary>One CpuMesh per visible occurrence with geometry, in
	/// document order, each named by its material. Missing-source and
	/// face-less occurrences contribute no mesh. The caller owns the
	/// returned meshes' disposal.</summary>
	public IReadOnlyList<CpuMesh> BuildRenderMeshes( AssemblySnapshot snapshot )
	{
		var meshes = new List<CpuMesh>();
		if ( snapshot?.Occurrences is null )
			return meshes;

		foreach ( var occ in snapshot.Occurrences )
		{
			if ( occ.Mesh is null || occ.Mesh.FaceCount == 0 )
				continue;
			var material = occ.MaterialName ?? "";
			meshes.Add( CpuMesh.FromPolyMesh( occ.Mesh, _ => material ) );
		}
		return meshes;
	}

	sealed class ResolvedOccurrence
	{
		public AssemblyOccurrence Occurrence;
		public AssemblyPartDefinition Definition;
		public (PolyMesh Mesh, int Revision, string MaterialName)? Resolved;
		public Xform Transform;
		public bool TransformUsable;
		public bool Missing;
		public string Key;
	}

	static List<ResolvedOccurrence> ResolveAll( AssemblyDocument doc, AssemblySourceResolver source )
	{
		ArgumentNullException.ThrowIfNull( doc );
		ArgumentNullException.ThrowIfNull( source );

		var definitions = new Dictionary<string, AssemblyPartDefinition>();
		if ( doc.Definitions != null )
		{
			foreach ( var def in doc.Definitions )
			{
				if ( def != null && !string.IsNullOrEmpty( def.Id ) && !definitions.ContainsKey( def.Id ) )
					definitions[def.Id] = def;
			}
		}

		var list = new List<ResolvedOccurrence>();
		if ( doc.Occurrences is null )
			return list;

		foreach ( var occ in doc.Occurrences )
		{
			if ( occ is null || !occ.Visible )
				continue;

			definitions.TryGetValue( occ.DefinitionId ?? "", out var def );
			(PolyMesh Mesh, int Revision, string MaterialName)? resolved = null;
			if ( def != null )
				resolved = source( def );

			var usable = occ.Transform != null && occ.Transform.IsValid( out _ );
			var xform = usable ? occ.Transform.ToXform() : Xform.Identity;
			var missing = def is null || resolved is null || resolved.Value.Mesh is null;
			var revision = resolved?.Revision ?? -1;

			list.Add( new ResolvedOccurrence
			{
				Occurrence = occ,
				Definition = def,
				Resolved = resolved,
				Transform = xform,
				TransformUsable = usable,
				Missing = missing,
				Key = KeyFor( occ, def?.Id ?? "", revision, usable, xform, missing ),
			} );
		}
		return list;
	}

	static string KeyFor( AssemblyOccurrence occ, string defId, int revision, bool usable, Xform xform, bool missing )
	{
		var id = occ.Id ?? "";
		var name = occ.Name ?? "";
		if ( missing )
			return string.Join( "|", new[] { id, defId, name, "missing", revision.ToString() } );
		if ( !usable )
			return string.Join( "|", new[] { id, defId, name, "bad-transform" } );
		return string.Join( "|", new[]
		{
			id, defId, name, revision.ToString(),
			xform.X.x.ToString( "R" ), xform.X.y.ToString( "R" ), xform.X.z.ToString( "R" ),
			xform.Y.x.ToString( "R" ), xform.Y.y.ToString( "R" ), xform.Y.z.ToString( "R" ),
			xform.Z.x.ToString( "R" ), xform.Z.y.ToString( "R" ), xform.Z.z.ToString( "R" ),
			xform.Origin.x.ToString( "R" ), xform.Origin.y.ToString( "R" ), xform.Origin.z.ToString( "R" ),
		} );
	}

	AssemblySnapshot BuildSnapshot( List<ResolvedOccurrence> resolved, bool reuse )
	{
		var snaps = new List<AssemblyOccurrenceSnapshot>( resolved.Count );
		var missing = new List<string>();

		foreach ( var r in resolved )
		{
			AssemblyOccurrenceSnapshot snap;
			if ( reuse && _byOccurrenceId.TryGetValue( r.Occurrence.Id ?? "", out var cached ) && cached.Key == r.Key )
			{
				snap = cached.Snapshot;
			}
			else
			{
				snap = BuildOccurrenceSnapshot( r );
				_byOccurrenceId[r.Occurrence.Id ?? ""] = (r.Key, snap);
			}
			snaps.Add( snap );
			if ( snap.MissingSource )
				missing.Add( snap.OccurrenceId );
		}

		var hasBounds = false;
		var min = Vec3.Zero;
		var max = Vec3.Zero;
		foreach ( var snap in snaps )
		{
			if ( snap.Mesh is null || snap.Mesh.Positions.Count == 0 )
				continue;
			if ( !hasBounds )
			{
				min = snap.BoundsMin;
				max = snap.BoundsMax;
				hasBounds = true;
			}
			else
			{
				min = new Vec3( MathF.Min( min.x, snap.BoundsMin.x ), MathF.Min( min.y, snap.BoundsMin.y ), MathF.Min( min.z, snap.BoundsMin.z ) );
				max = new Vec3( MathF.Max( max.x, snap.BoundsMax.x ), MathF.Max( max.y, snap.BoundsMax.y ), MathF.Max( max.z, snap.BoundsMax.z ) );
			}
		}

		var snapshot = new AssemblySnapshot
		{
			Occurrences = snaps,
			MissingOccurrenceIds = missing,
			VisibleCount = snaps.Count,
			Bounds = hasBounds ? (min, max) : (Vec3.Zero, Vec3.Zero),
		};
		BuildPicker( snapshot );
		return snapshot;
	}

	static AssemblyOccurrenceSnapshot BuildOccurrenceSnapshot( ResolvedOccurrence r )
	{
		var occ = r.Occurrence;
		var snap = new AssemblyOccurrenceSnapshot
		{
			OccurrenceId = occ.Id ?? "",
			DefinitionId = occ.DefinitionId ?? "",
			Name = occ.Name ?? "",
			SourceRevision = r.Resolved?.Revision ?? -1,
		};

		if ( r.Missing || !r.TransformUsable )
		{
			snap.MissingSource = true;
			snap.BoundsMin = Vec3.Zero;
			snap.BoundsMax = Vec3.Zero;
			return snap;
		}

		// Clone + transform exactly once; winding reversed when mirrored.
		var mesh = MeshTransform.Transformed( r.Resolved.Value.Mesh, r.Transform );
		snap.Mesh = mesh;
		snap.MaterialName = r.Resolved.Value.MaterialName ?? "";
		snap.Mirrored = r.Transform.FlipsWinding;
		(snap.BoundsMin, snap.BoundsMax) = MeshBounds( mesh );
		return snap;
	}

	static (Vec3 Min, Vec3 Max) MeshBounds( PolyMesh mesh )
	{
		if ( mesh.Positions.Count == 0 )
			return (Vec3.Zero, Vec3.Zero);

		var min = mesh.Positions[0];
		var max = mesh.Positions[0];
		foreach ( var p in mesh.Positions )
		{
			min = new Vec3( MathF.Min( min.x, p.x ), MathF.Min( min.y, p.y ), MathF.Min( min.z, p.z ) );
			max = new Vec3( MathF.Max( max.x, p.x ), MathF.Max( max.y, p.y ), MathF.Max( max.z, p.z ) );
		}
		return (min, max);
	}

	static void BuildPicker( AssemblySnapshot snapshot )
	{
		var picker = new MeshPicker();
		var mapping = new List<int>();
		for ( var i = 0; i < snapshot.Occurrences.Count; i++ )
		{
			var occ = snapshot.Occurrences[i];
			if ( occ.Mesh is null || occ.Mesh.FaceCount == 0 )
				continue;
			picker.Add( occ.Mesh );
			mapping.Add( i );
		}
		snapshot.Picker = picker;
		snapshot.PickerOccurrenceIndex = mapping;
	}

	static void EnsurePicker( AssemblySnapshot snapshot )
	{
		if ( snapshot.Picker != null && snapshot.PickerOccurrenceIndex != null )
			return;
		BuildPicker( snapshot );
	}

	// Same Moller-Trumbore test as MeshPicker.Pick, returning the ray
	// parameter t, or -1 when the triangle is missed.
	static float RayTriangle( Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, Vector3 c )
	{
		var edge1 = b - a;
		var edge2 = c - a;
		var cross = Vector3.Cross( direction, edge2 );
		var det = Vector3.Dot( edge1, cross );
		if ( MathF.Abs( det ) < 1e-7f )
			return -1f;
		var inverse = 1f / det;
		var delta = origin - a;
		var u = Vector3.Dot( delta, cross ) * inverse;
		if ( u < 0f || u > 1f )
			return -1f;
		var q = Vector3.Cross( delta, edge1 );
		var v = Vector3.Dot( direction, q ) * inverse;
		if ( v < 0f || u + v > 1f )
			return -1f;
		var t = Vector3.Dot( edge2, q ) * inverse;
		return t >= 0f ? t : -1f;
	}
}
