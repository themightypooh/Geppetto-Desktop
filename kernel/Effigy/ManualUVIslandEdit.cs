using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Effigy;

/// <summary>A hand-authored transform for one UV island. FaceKey includes its face and vertex
/// indices, so an edit is ignored if a later topology change makes the old island ambiguous.</summary>
public sealed class ManualUVIslandEdit
{
	public string BodyId = "";
	public string FaceKey = "";
	public Vec2 Offset;
	public float RotationDegrees;
	public Vec2 Scale = new( 1, 1 );

	public static string Key( PolyMesh mesh, IEnumerable<int> faces ) => string.Join( ";", faces.Order().Select( f =>
		$"{f}:{string.Join( ",", mesh.Faces[f].Indices )}" ) );

	public static ManualUVIslandEdit Find( PartStudio studio, string bodyId, string faceKey ) =>
		studio?.ManualUVEdits?.FirstOrDefault( e => e.BodyId == bodyId && e.FaceKey == faceKey );

	public static string Fingerprint( IReadOnlyList<ManualUVIslandEdit> edits )
	{
		var inv = CultureInfo.InvariantCulture;
		return string.Join( "\n", (edits ?? Array.Empty<ManualUVIslandEdit>()).Where( e => e is not null )
			.OrderBy( e => e.BodyId, StringComparer.Ordinal ).ThenBy( e => e.FaceKey, StringComparer.Ordinal )
			.Select( e => string.Join( "|", e.BodyId, e.FaceKey, e.Offset.x.ToString( "R", inv ), e.Offset.y.ToString( "R", inv ),
				e.RotationDegrees.ToString( "R", inv ), e.Scale.x.ToString( "R", inv ), e.Scale.y.ToString( "R", inv ) ) ) );
	}

	/// <summary>Apply to feature output before downstream paint features, or to final bodies when no
	/// paint feature follows. Caller tracks cached bodies so transforms never compound.</summary>
	public static void Apply( PartStudio studio ) => Apply( studio?.Bodies, studio?.ManualUVEdits );

	public static void Apply( IEnumerable<Body> bodies, IReadOnlyList<ManualUVIslandEdit> edits, ISet<string> alreadyApplied = null )
	{
		if ( edits is not { Count: > 0 } || bodies is null ) return;
		foreach ( var body in bodies )
		{
			if ( alreadyApplied?.Contains( body.Id ) == true ) continue;
			var bodyEdits = edits.Where( e => e.BodyId == body.Id ).ToList();
			if ( bodyEdits.Count == 0 || body.Mesh is null ) continue;
			var layout = UVIslands.Find( body.Mesh );
			var applied = false;
			foreach ( var edit in bodyEdits )
			{
				var faces = layout.Islands.FirstOrDefault( island => Key( body.Mesh, island ) == edit.FaceKey );
				if ( faces is null ) continue; // Upstream topology changed; don't apply stale face indices.
				applied = true;
				var centre = UVIslands.Centre( body.Mesh, faces );
				var radians = edit.RotationDegrees * (MathF.PI / 180f);
				var c = MathF.Cos( radians ); var s = MathF.Sin( radians );
				foreach ( var f in faces )
				{
					var face = body.Mesh.Faces[f];
					for ( var i = 0; i < face.UVs.Length; i++ )
					{
						var u = face.UVs[i].x - centre.x; var v = face.UVs[i].y - centre.y;
						u *= edit.Scale.x; v *= edit.Scale.y;
						face.UVs[i] = new Vec2( centre.x + u * c - v * s + edit.Offset.x,
							centre.y + u * s + v * c + edit.Offset.y );
					}
				}
			}
			if ( applied ) alreadyApplied?.Add( body.Id );
		}
	}

	public string WriteLine()
	{
		var inv = CultureInfo.InvariantCulture;
		var key = Convert.ToBase64String( System.Text.Encoding.UTF8.GetBytes( FaceKey ) );
		string F( float f ) => f.ToString( "R", inv );
		return $"manualuv {BodyId} {key} {F( Offset.x )} {F( Offset.y )} {F( RotationDegrees )} {F( Scale.x )} {F( Scale.y )}";
	}

	public static ManualUVIslandEdit ReadLine( string line )
	{
		try
		{
			var p = line[9..].Split( ' ', StringSplitOptions.RemoveEmptyEntries );
			if ( p.Length != 7 ) return null;
			var inv = CultureInfo.InvariantCulture;
			float F( int i ) => float.Parse( p[i], NumberStyles.Float, inv );
			return new ManualUVIslandEdit
			{
				BodyId = p[0], FaceKey = System.Text.Encoding.UTF8.GetString( Convert.FromBase64String( p[1] ) ),
				Offset = new Vec2( F( 2 ), F( 3 ) ), RotationDegrees = F( 4 ), Scale = new Vec2( F( 5 ), F( 6 ) ),
			};
		}
		catch { return null; }
	}
}
