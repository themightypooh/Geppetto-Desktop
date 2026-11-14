#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;
using Marionette.Desktop;
using System.Text;

/// <summary>MikkTSpace tangent provider: the managed <see cref="MikkTangents"/> generator
/// and its wiring into <see cref="NormalBake"/> (hook, dispatch, <c>MikkRuns</c>), plus
/// the agreement between the bake and the GLB export, which share the one basis.
/// The green-channel convention itself is pinned by GlbInterchangeTests' painted-bodies
/// test ("normal pixels must be the G-flipped height-to-normal map") and is not
/// duplicated here. A full sphere-seam bake test is skipped: it needs UVUnwrap.XAtlas,
/// another ticket's in-flight work.</summary>
internal static class MikkTangentTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Assert( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}

		void AssertNear( float actual, float expected, float tolerance, string what )
		{
			Assert( MathF.Abs( actual - expected ) <= tolerance, $"{what}: expected {expected}, got {actual} (tolerance {tolerance})" );
		}

		void AssertTangent( Vec4 actual, float x, float y, float z, float w, string what )
		{
			AssertNear( actual.x, x, 1e-4f, $"{what} x" );
			AssertNear( actual.y, y, 1e-4f, $"{what} y" );
			AssertNear( actual.z, z, 1e-4f, $"{what} z" );
			AssertNear( actual.w, w, 1e-4f, $"{what} w" );
		}

		// A unit quad in the XY plane, two triangles, normals +Z, UVs over [0,1]².
		// dP/du is (1,0,0) and the layout is not mirrored, so every tangent is (1,0,0,1).
		(Vec3[] Positions, Vec3[] Normals, Vec2[] Uvs) Quad( bool mirrorU = false )
		{
			var positions = new[]
			{
				new Vec3( 0, 0, 0 ), new Vec3( 1, 0, 0 ), new Vec3( 1, 1, 0 ),
				new Vec3( 0, 0, 0 ), new Vec3( 1, 1, 0 ), new Vec3( 0, 1, 0 ),
			};
			var normals = Enumerable.Repeat( new Vec3( 0, 0, 1 ), 6 ).ToArray();
			var uvs = mirrorU
				? new[]
				{
					new Vec2( 1, 0 ), new Vec2( 0, 0 ), new Vec2( 0, 1 ),
					new Vec2( 1, 0 ), new Vec2( 0, 1 ), new Vec2( 1, 1 ),
				}
				: new[]
				{
					new Vec2( 0, 0 ), new Vec2( 1, 0 ), new Vec2( 1, 1 ),
					new Vec2( 0, 0 ), new Vec2( 1, 1 ), new Vec2( 0, 1 ),
				};
			return (positions, normals, uvs);
		}

		registry.Add( "mikk", "quad tangent equals dP/du with w +1", () =>
		{
			var (positions, normals, uvs) = Quad();
			var tangents = MikkTangents.Generate( positions, normals, uvs );
			Assert( tangents is not null && tangents.Length == 6, $"Expected 6 tangents, got {tangents?.Length}" );
			for ( int i = 0; i < 6; i++ )
				AssertTangent( tangents[i], 1f, 0f, 0f, 1f, $"Quad tangent {i}" );
		} );

		registry.Add( "mikk", "mirrored UVs flip w and mirror the tangent", () =>
		{
			var (positions, normals, uvs) = Quad( mirrorU: true );
			var tangents = MikkTangents.Generate( positions, normals, uvs );
			Assert( tangents is not null && tangents.Length == 6, $"Expected 6 tangents, got {tangents?.Length}" );
			for ( int i = 0; i < 6; i++ )
				AssertTangent( tangents[i], -1f, 0f, 0f, -1f, $"Mirrored quad tangent {i}" );
		} );

		registry.Add( "mikk", "box hard edges keep each face's own dP/du", () =>
		{
			// Six faces of a unit cube, each its own two triangles with its own
			// normal and a [0,1]² UV layout. A corner welds only with corners that
			// share its position, normal and UV, so no tangent bleeds across an edge.
			var positions = new List<Vec3>();
			var normals = new List<Vec3>();
			var uvs = new List<Vec2>();
			// (normal, u-axis, v-axis): the tangent must be the u-axis, w +1.
			// Each face's (u, v) is chosen so N × T = +v (right-handed): a face
			// whose u/v ran the other way would honestly report w -1 instead.
			var faces = new[]
			{
				(new Vec3( 0, 0, 1 ), new Vec3( 1, 0, 0 ), new Vec3( 0, 1, 0 ), new Vec3( 0, 0, 1 )),
				(new Vec3( 0, 0, -1 ), new Vec3( -1, 0, 0 ), new Vec3( 0, 1, 0 ), new Vec3( 0, 0, 0 )),
				(new Vec3( 0, 1, 0 ), new Vec3( -1, 0, 0 ), new Vec3( 0, 0, 1 ), new Vec3( 1, 1, 0 )),
				(new Vec3( 0, -1, 0 ), new Vec3( -1, 0, 0 ), new Vec3( 0, 0, -1 ), new Vec3( 1, 0, 1 )),
				(new Vec3( 1, 0, 0 ), new Vec3( 0, 0, -1 ), new Vec3( 0, 1, 0 ), new Vec3( 1, 0, 1 )),
				(new Vec3( -1, 0, 0 ), new Vec3( 0, 0, 1 ), new Vec3( 0, 1, 0 ), new Vec3( 0, 0, 0 )),
			};
			foreach ( var (n, uAxis, vAxis, origin) in faces )
			{
				var corners = new[]
				{
					origin,
					origin + uAxis,
					origin + uAxis + vAxis,
					origin,
					origin + uAxis + vAxis,
					origin + vAxis,
				};
				var cornerUvs = new[]
				{
					new Vec2( 0, 0 ), new Vec2( 1, 0 ), new Vec2( 1, 1 ),
					new Vec2( 0, 0 ), new Vec2( 1, 1 ), new Vec2( 0, 1 ),
				};
				positions.AddRange( corners );
				normals.AddRange( Enumerable.Repeat( n, 6 ) );
				uvs.AddRange( cornerUvs );
			}
			var tangents = MikkTangents.Generate( positions.ToArray(), normals.ToArray(), uvs.ToArray() );
			Assert( tangents is not null && tangents.Length == 36, $"Expected 36 tangents, got {tangents?.Length}" );
			for ( int f = 0; f < 6; f++ )
			{
				var (_, uAxis, _, _) = faces[f];
				for ( int i = 0; i < 6; i++ )
					AssertTangent( tangents[f * 6 + i], uAxis.x, uAxis.y, uAxis.z, 1f, $"Box face {f} tangent {i}" );
			}
		} );

		registry.Add( "mikk", "degenerate UV triangle yields zero tangents, no crash, no NaN", () =>
		{
			var positions = new[] { new Vec3( 0, 0, 0 ), new Vec3( 1, 0, 0 ), new Vec3( 0, 1, 0 ) };
			var normals = new[] { new Vec3( 0, 0, 1 ), new Vec3( 0, 0, 1 ), new Vec3( 0, 0, 1 ) };
			var uvs = new[] { new Vec2( 0.5f, 0.5f ), new Vec2( 0.5f, 0.5f ), new Vec2( 0.5f, 0.5f ) };
			var tangents = MikkTangents.Generate( positions, normals, uvs );
			Assert( tangents is not null && tangents.Length == 3, $"Expected 3 tangents, got {tangents?.Length}" );
			for ( int i = 0; i < 3; i++ )
			{
				AssertTangent( tangents[i], 0f, 0f, 0f, 1f, $"Degenerate tangent {i}" );
				Assert( !float.IsNaN( tangents[i].x ) && !float.IsNaN( tangents[i].y ) && !float.IsNaN( tangents[i].z ), "Degenerate tangent must not be NaN" );
			}
		} );

		registry.Add( "mikk", "invalid input returns null", () =>
		{
			var (positions, normals, uvs) = Quad();
			Assert( MikkTangents.Generate( Array.Empty<Vec3>(), Array.Empty<Vec3>(), Array.Empty<Vec2>() ) is null, "Empty input must return null" );
			Assert( MikkTangents.Generate( positions.Take( 4 ).ToArray(), normals.Take( 4 ).ToArray(), uvs.Take( 4 ).ToArray() ) is null, "Non-triangle count must return null" );
			Assert( MikkTangents.Generate( positions, normals.Take( 3 ).ToArray(), uvs ) is null, "Mismatched lengths must return null" );
			Assert( MikkTangents.Generate( null!, normals, uvs ) is null, "Null positions must return null" );
		} );

		registry.Add( "mikk", "Install wires the hook and NormalBake dispatches through it (MikkRuns)", () =>
		{
			var previous = NormalBake.MikkTangents;
			try
			{
				NormalBake.MikkTangents = null;
				NormalBake.MikkRuns = 0;
				MikkTangents.Install();
				Assert( NormalBake.MikkTangents is not null, "Install must wire the hook" );
				MikkTangents.Install();
				Assert( NormalBake.MikkTangents is not null, "Install must be idempotent" );

				var mesh = new PolyMesh();
				mesh.Positions.AddRange( new[]
				{
					new Vec3( 0, 0, 0 ), new Vec3( 1, 0, 0 ), new Vec3( 1, 1, 0 ),
					new Vec3( 0, 0, 0 ), new Vec3( 1, 1, 0 ), new Vec3( 0, 1, 0 ),
				} );
				mesh.AddFace( new[] { 0, 1, 2 }, new[] { new Vec2( 0, 0 ), new Vec2( 1, 0 ), new Vec2( 1, 1 ) }, material: 0 );
				mesh.AddFace( new[] { 3, 4, 5 }, new[] { new Vec2( 0, 0 ), new Vec2( 1, 1 ), new Vec2( 0, 1 ) }, material: 0 );
				var (cornerNormals, normals) = MeshNormals.ComputeCornerNormals( mesh, MeshNormals.DefaultSmoothingAngleDegrees );
				int before = NormalBake.MikkRuns;
				var tangents = NormalBake.ExportTangents( mesh, cornerNormals, normals );
				Assert( NormalBake.MikkRuns > before, $"MikkRuns must increase through the bake (was {before}, now {NormalBake.MikkRuns})" );
				Assert( tangents is not null && tangents.Length == 2, $"Expected tangents for 2 faces, got {tangents?.Length}" );
				// The quad's tangents are (1,0,0,1) through the Mikk path.
				AssertNear( tangents[0][0].x, 1f, 1e-4f, "Bake tangent face 0 corner 0 x" );
				AssertNear( tangents[0][0].w, 1f, 1e-4f, "Bake tangent face 0 corner 0 w" );
			}
			finally
			{
				NormalBake.MikkTangents = previous;
			}
		} );

		registry.Add( "mikk", "null hook falls back to the approximation and still produces tangents", () =>
		{
			var previous = NormalBake.MikkTangents;
			try
			{
				NormalBake.MikkTangents = null;
				var mesh = new PolyMesh();
				mesh.Positions.AddRange( new[]
				{
					new Vec3( 0, 0, 0 ), new Vec3( 1, 0, 0 ), new Vec3( 1, 1, 0 ),
				} );
				mesh.AddFace( new[] { 0, 1, 2 }, new[] { new Vec2( 0, 0 ), new Vec2( 1, 0 ), new Vec2( 1, 1 ) }, material: 0 );
				var (cornerNormals, normals) = MeshNormals.ComputeCornerNormals( mesh, MeshNormals.DefaultSmoothingAngleDegrees );
				var tangents = NormalBake.ExportTangents( mesh, cornerNormals, normals );
				Assert( tangents is not null && tangents.Length == 1 && tangents[0].Length == 3, "Fallback must still produce one tangent per corner" );
				var t0 = tangents[0][0];
				Assert( t0.x * t0.x + t0.y * t0.y + t0.z * t0.z + t0.w * t0.w > 0f, "Fallback tangent must be non-zero" );
			}
			finally
			{
				NormalBake.MikkTangents = previous;
			}
		} );

		registry.Add( "mikk", "GLB export TANGENT agrees with the bake for the same mesh", () =>
		{
			var previous = NormalBake.MikkTangents;
			try
			{
				MikkTangents.Install();
				var mesh = new PolyMesh();
				mesh.Positions.AddRange( new[]
				{
					new Vec3( 0, 0, 0 ), new Vec3( 1, 0, 0 ), new Vec3( 1, 1, 0 ),
					new Vec3( 0, 0, 0 ), new Vec3( 1, 1, 0 ), new Vec3( 0, 1, 0 ),
				} );
				mesh.AddFace( new[] { 0, 1, 2 }, new[] { new Vec2( 0, 0 ), new Vec2( 1, 0 ), new Vec2( 1, 1 ) }, material: 0 );
				mesh.AddFace( new[] { 3, 4, 5 }, new[] { new Vec2( 0, 0 ), new Vec2( 1, 1 ), new Vec2( 0, 1 ) }, material: 0 );
				var material = new GlbMaterial( "quad", new Vec4( 1f, 1f, 1f, 1f ), 0f, 1f, new Vec3( 0f, 0f, 0f ), null, null, null, null );
				byte[] glb = GlbExport.Write( new[] { new GlbPart( "quad", mesh, material ) } );

				// Independent parse: find the TANGENT accessor and read its floats.
				Assert( BitConverter.ToUInt32( glb, 0 ) == 0x46546C67u, "GLB magic must be 'glTF'" );
				uint jsonLength = BitConverter.ToUInt32( glb, 12 );
				using var json = System.Text.Json.JsonDocument.Parse( Encoding.UTF8.GetString( glb, 20, (int)jsonLength ).TrimEnd( ' ', '\0' ) );
				var root = json.RootElement;
				int binOffset = 20 + (int)( ( jsonLength + 3 ) & ~3u );
				uint binLength = BitConverter.ToUInt32( glb, binOffset );
				var bin = new byte[binLength];
				Array.Copy( glb, binOffset + 8, bin, 0, (int)binLength );
				var primitive = root.GetProperty( "meshes" )[0].GetProperty( "primitives" )[0];
				int tangentAccessor = primitive.GetProperty( "attributes" ).GetProperty( "TANGENT" ).GetInt32();
				var accessor = root.GetProperty( "accessors" )[tangentAccessor];
				var view = root.GetProperty( "bufferViews" )[accessor.GetProperty( "bufferView" ).GetInt32()];
				int offset = view.TryGetProperty( "byteOffset", out var off ) ? off.GetInt32() : 0;
				int count = accessor.GetProperty( "count" ).GetInt32();
				var exported = new float[count * 4];
				Buffer.BlockCopy( bin, offset, exported, 0, exported.Length * 4 );

				var (cornerNormals, normals) = MeshNormals.ComputeCornerNormals( mesh, MeshNormals.DefaultSmoothingAngleDegrees );
				var baked = NormalBake.ExportTangents( mesh, cornerNormals, normals );
				// Six corners in face order; each exported VEC4 must equal the baked
				// tangent through G (x, z, -y), with the same w.
				int corner = 0;
				for ( int fi = 0; fi < 2; fi++ )
					for ( int c = 0; c < 3; c++, corner++ )
					{
						var t = baked[fi][c];
						var g = new Vec3( t.x, t.z, -t.y );
						AssertNear( exported[corner * 4], g.x, 1e-5f, $"Exported tangent corner {corner} x" );
						AssertNear( exported[corner * 4 + 1], g.y, 1e-5f, $"Exported tangent corner {corner} y" );
						AssertNear( exported[corner * 4 + 2], g.z, 1e-5f, $"Exported tangent corner {corner} z" );
						AssertNear( exported[corner * 4 + 3], t.w, 1e-5f, $"Exported tangent corner {corner} w" );
					}
			}
			finally
			{
				NormalBake.MikkTangents = previous;
			}
			_ = settings;
		} );
	}
}
