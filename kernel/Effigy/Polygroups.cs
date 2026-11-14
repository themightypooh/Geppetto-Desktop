using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Effigy;

/// <summary>
/// Polygroups / Face Sets on one mesh: one group id per face, with names.
///
/// This is the grouping half of godlike-sculpt-layers-morph-targets-polygroups — the
/// ZBrush Polygroups / Blender Face Sets behaviour: assign faces to named groups, then
/// isolate one group, hide groups, auto-mask from a group, and take a group's centroid
/// as the Pose-brush pivot.
///
/// The per-face ids are the <see cref="FaceSets"/> array (one int per face); this type
/// is the named-group bookkeeping and the isolate/hide/pivot ops on top of it, so the
/// mask itself reuses <see cref="FaceSets.MaskForSet"/> instead of re-implementing it.
/// Groups live on the top-level mesh: the face count and topology id they were made
/// against are stored in the blob, and a mismatch refuses rather than assigning ids
/// to the wrong faces.
/// </summary>
public sealed class PolygroupSet
{
	readonly int[] _groups;
	readonly Dictionary<int, string> _names = new();

	public PolygroupSet( int faceCount )
	{
		if ( faceCount < 0 )
			throw new ArgumentOutOfRangeException( nameof( faceCount ), "A polygroup set needs a face count." );

		_groups = new int[faceCount];
		_names[0] = "Default";
	}

	internal PolygroupSet( int[] groups, Dictionary<int, string> names )
	{
		_groups = groups ?? throw new ArgumentNullException( nameof( groups ) );
		_names = names ?? throw new ArgumentNullException( nameof( names ) );
	}

	/// <summary>Faces this set covers. Every entry is a group id; length never changes.</summary>
	public int FaceCount => _groups.Length;

	/// <summary>Bumped by every assignment, rename and new group, so a feature knows its cached geometry is stale.</summary>
	public int Revision { get; private set; }

	/// <summary>The group id of one face.</summary>
	public int GroupOf( int face )
	{
		if ( face < 0 || face >= _groups.Length )
			throw new ArgumentOutOfRangeException( nameof( face ), $"There is no face {face} in a set of {_groups.Length}." );

		return _groups[face];
	}

	/// <summary>Put one face in a group. New ids get the name "Group N" until renamed.</summary>
	public void Assign( int face, int group )
	{
		if ( group < 0 )
			throw new ArgumentOutOfRangeException( nameof( group ), "Group ids start at 0." );

		if ( face < 0 || face >= _groups.Length )
			throw new ArgumentOutOfRangeException( nameof( face ), $"There is no face {face} in a set of {_groups.Length}." );

		if ( _groups[face] == group && _names.ContainsKey( group ) )
			return;

		_groups[face] = group;

		if ( !_names.ContainsKey( group ) )
			_names[group] = $"Group {group}";

		Revision++;
	}

	/// <summary>Put faces in a group, ignoring out-of-range indices rather than failing the whole assignment.</summary>
	public void AssignFaces( IEnumerable<int> faces, int group )
	{
		if ( faces is null )
			throw new ArgumentNullException( nameof( faces ) );

		var changed = false;

		foreach ( var face in faces )
		{
			if ( face < 0 || face >= _groups.Length )
				continue;

			if ( _groups[face] != group )
			{
				_groups[face] = group;
				changed = true;
			}
		}

		if ( !_names.ContainsKey( group ) )
			_names[group] = $"Group {group}";

		if ( changed )
			Revision++;
	}

	/// <summary>Create a group and return its id. Ids are never reused, so a deleted group's faces keep their old id until reassigned.</summary>
	public int AddGroup( string name )
	{
		var id = _names.Count == 0 ? 0 : _names.Keys.Max() + 1;
		_names[id] = string.IsNullOrWhiteSpace( name ) ? $"Group {id}" : name;
		Revision++;
		return id;
	}

	/// <summary>Rename a group. The id — and every face in it — stays put.</summary>
	public void Rename( int group, string name )
	{
		if ( !_names.ContainsKey( group ) )
			throw new ArgumentException( $"There is no group {group} in this set." );

		_names[group] = string.IsNullOrWhiteSpace( name ) ? $"Group {group}" : name;
		Revision++;
	}

	public string NameOf( int group ) =>
		_names.TryGetValue( group, out var name ) ? name : $"Group {group}";

	/// <summary>Every group that exists, id and name, in id order.</summary>
	public IReadOnlyList<(int Id, string Name)> Groups =>
		_names.Keys.OrderBy( id => id ).Select( id => ( id, _names[id] ) ).ToList();

	/// <summary>The faces in one group, in face order.</summary>
	public int[] FacesInGroup( int group )
	{
		var faces = new List<int>();

		for ( var f = 0; f < _groups.Length; f++ )
		{
			if ( _groups[f] == group )
				faces.Add( f );
		}

		return faces.ToArray();
	}

	/// <summary>A copy of the per-face ids — the <see cref="FaceSets"/> array this set wraps.</summary>
	public int[] FaceGroups => (int[])_groups.Clone();

	/// <summary>
	/// Which faces stay visible when <paramref name="hiddenGroups"/> are hidden.
	/// Everything not hidden stays visible — hiding is a set, not a mode.
	/// </summary>
	public bool[] VisibleFaces( IReadOnlyCollection<int> hiddenGroups )
	{
		var hidden = hiddenGroups is null ? new HashSet<int>() : new HashSet<int>( hiddenGroups );
		var visible = new bool[_groups.Length];

		for ( var f = 0; f < _groups.Length; f++ )
			visible[f] = !hidden.Contains( _groups[f] );

		return visible;
	}

	/// <summary>Isolate one group: only its faces stay visible.</summary>
	public bool[] Isolate( int group )
	{
		var visible = new bool[_groups.Length];

		for ( var f = 0; f < _groups.Length; f++ )
			visible[f] = _groups[f] == group;

		return visible;
	}

	/// <summary>
	/// The Pose-brush pivot for a group: the centroid of its vertices. A group with no
	/// faces has no pivot, and that is refused rather than answered with the origin.
	/// </summary>
	public Vec3 Centroid( int group, PolyMesh mesh )
	{
		Validate( mesh );

		var sum = Vec3.Zero;
		var count = 0;
		var seen = new bool[mesh.VertexCount];

		for ( var f = 0; f < _groups.Length; f++ )
		{
			if ( _groups[f] != group )
				continue;

			foreach ( var v in mesh.Faces[f].Indices )
			{
				if ( seen[v] )
					continue;

				seen[v] = true;
				sum += mesh.Positions[v];
				count++;
			}
		}

		if ( count == 0 )
			throw new InvalidOperationException( $"Group {group} ('{NameOf( group )}') has no faces, so it has no pivot." );

		return sum / count;
	}

	/// <summary>
	/// Per-vertex mask for one group: 1 on the group's vertices, 0 elsewhere, with an
	/// optional feather over <paramref name="boundaryFalloff"/> rings. This is the
	/// auto-mask from a group — the same mask <see cref="FaceSets.MaskForSet"/> builds.
	/// </summary>
	public float[] MaskForGroup( PolyMesh mesh, int group, float boundaryFalloff = 0f )
	{
		Validate( mesh );
		return FaceSets.MaskForSet( mesh, _groups, group, boundaryFalloff );
	}

	/// <summary>Write a group's mask into a session mask, replacing what it held.</summary>
	public void ApplyMaskTo( SculptMask mask, PolyMesh mesh, int group, float boundaryFalloff = 0f )
	{
		Validate( mesh );
		FaceSets.ApplyToMask( mask, mesh, _groups, group, boundaryFalloff );
	}

	/// <summary>Groups from a mesh's material slots — the Face Sets a model already carries, named "Material N".</summary>
	public static PolygroupSet FromMaterials( PolyMesh mesh )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		var groups = FaceSets.FromMaterials( mesh );
		var names = new Dictionary<int, string>();

		foreach ( var id in groups )
		{
			if ( !names.ContainsKey( id ) )
				names[id] = $"Material {id}";
		}

		return new PolygroupSet( groups, names );
	}

	/// <summary>The mesh this set was made for — same face count, or it refuses.</summary>
	public void Validate( PolyMesh mesh )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		if ( mesh.FaceCount != _groups.Length )
			throw new ArgumentException(
				$"This polygroup set covers {_groups.Length} faces; the mesh has {mesh.FaceCount}." );
	}

	/// <summary>Write the set into a blob section, against a topology id. Read back with <see cref="ReadFrom"/>.</summary>
	internal void WriteTo( BinaryWriter w, long topologyId )
	{
		w.Write( _groups.Length );
		w.Write( topologyId );

		foreach ( var group in _groups )
			w.Write( group );

		w.Write( _names.Count );

		foreach ( var (id, name) in _names )
		{
			w.Write( id );
			w.Write( name );
		}
	}

	/// <summary>Read a set written by <see cref="WriteTo(BinaryWriter, long)"/>. The topology id must match the mesh it will be used on.</summary>
	internal static PolygroupSet ReadFrom( BinaryReader r, long topologyId )
	{
		var faceCount = r.ReadInt32();

		if ( faceCount < 0 || faceCount > 1 << 26 )
			throw new InvalidDataException( $"A polygroup set with {faceCount} faces is not a polygroup set." );

		var storedTopology = r.ReadInt64();

		if ( storedTopology != topologyId )
			throw new InvalidOperationException(
				"These polygroups were made on a different mesh. Group ids are per face, so they cannot be placed on this one." );

		var groups = new int[faceCount];

		for ( var f = 0; f < faceCount; f++ )
			groups[f] = r.ReadInt32();

		var names = new Dictionary<int, string>();
		var nameCount = r.ReadInt32();

		if ( nameCount < 0 || nameCount > 1024 )
			throw new InvalidDataException( $"A polygroup set with {nameCount} names is not a polygroup set." );

		for ( var i = 0; i < nameCount; i++ )
			names[r.ReadInt32()] = r.ReadString();

		if ( !names.ContainsKey( 0 ) )
			names[0] = "Default";

		return new PolygroupSet( groups, names );
	}
}
