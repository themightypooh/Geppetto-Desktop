using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// Lane B: auto-place combat hitboxes (HitboxSetList) from skinned mesh + skeleton.
/// Each bone with enough influenced vertices gets an oriented capsule hitbox aligned
/// along the bone's actual skeletal axis.
/// </summary>
public static class HitboxBuilder
{
	public static List<VmdlDocument.Hitbox> GenerateFromSkin( PolyMesh mesh, Skeleton skeleton )
	{
		var hitboxes = new List<VmdlDocument.Hitbox>();
		if ( mesh is null || skeleton is null || mesh.Positions is null || mesh.Skin is null )
			return hitboxes;

		for ( int b = 0; b < skeleton.Count; b++ )
		{
			var bone = skeleton.Bones[b];
			var verts = new List<Vec3>();
			for ( int i = 0; i < mesh.Positions.Count; i++ )
			{
				var w = ( i < mesh.Skin.Count ) ? mesh.Skin[i] : null;
				if ( w is null ) continue;
				if ( w.Any( x => x.Bone == b && x.Weight > 0.35f ) )
					verts.Add( mesh.Positions[i] );
			}
			if ( verts.Count < 4 )
				continue;

			var head = skeleton.HeadWorld( b );
			var tail = skeleton.TailWorld( b );
			var boneDir = tail - head;
			var axis = boneDir.LengthSquared > 1e-8f ? boneDir.Normal : skeleton.BoneDirection( b ).Normal;
			if ( axis.LengthSquared < 1e-8f ) axis = new Vec3( 0, 1, 0 );

			var (p0, p1, radius) = FitCapsuleAlongAxis( verts, axis );
			hitboxes.Add( new VmdlDocument.Hitbox
			{
				Bone = bone.Name,
				Kind = "capsule",
				Point0 = p0,
				Point1 = p1,
				Radius = radius,
				Size = Vec3.Zero,
			} );
		}
		return hitboxes;
	}

	static (Vec3 p0, Vec3 p1, float radius) FitCapsuleAlongAxis( List<Vec3> verts, Vec3 axis )
	{
		if ( verts.Count == 0 )
			return ( Vec3.Zero, Vec3.Zero, 0.5f );

		var centre = Vec3.Zero;
		foreach ( var p in verts ) centre += p;
		centre /= verts.Count;

		float tMin = float.MaxValue, tMax = float.MinValue;
		float radius = 0f;
		foreach ( var p in verts )
		{
			var rel = p - centre;
			var t = Vec3.Dot( rel, axis );
			tMin = MathF.Min( tMin, t );
			tMax = MathF.Max( tMax, t );
			var perp = rel - axis * t;
			radius = MathF.Max( radius, perp.Length );
		}

		radius = MathF.Max( 0.25f, radius );
		var halfLen = MathF.Max( 0f, (tMax - tMin) * 0.5f );
		var inset = MathF.Max( 0f, halfLen - radius );
		var mid = centre + axis * ((tMin + tMax) * 0.5f);
		var p0 = mid + axis * inset;
		var p1 = mid - axis * inset;

		return ( p0, p1, radius );
	}

	public static string MapBoneTag( string name )
	{
		if ( string.IsNullOrEmpty( name ) ) return "flesh";
		var n = name.ToLowerInvariant();
		if ( n.Contains( "head" ) || n.Contains( "neck" ) ) return "head neck";
		if ( n.Contains( "spine" ) || n.Contains( "chest" ) ) return "chest";
		if ( n.Contains( "pelvis" ) || n.Contains( "stomach" ) || n.Contains( "hip" ) ) return "stomach pelvis";
		bool isRight = n.EndsWith( "_r" ) || n.Contains( "_r_" ) || n.Contains( "right" );
		if ( n.Contains( "arm" ) || n.Contains( "hand" ) || n.Contains( "clavicle" ) ) return isRight ? "arm_right" : "arm_left";
		if ( n.Contains( "leg" ) || n.Contains( "thigh" ) || n.Contains( "calf" ) || n.Contains( "foot" ) || n.Contains( "ankle" ) ) return isRight ? "leg_right" : "leg_left";
		return "flesh";
	}
}
