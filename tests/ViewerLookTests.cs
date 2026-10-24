using System;
using Effigy;
using Marionette.Desktop;

/// <summary>Wave-5 look fix: vertex colours reach the viewport vertex buffer, and
/// garment-detail slots (Wear rust/algae) resolve to kind colours instead of the
/// neutral default. CPU-only: no GL context needed.</summary>
internal static class ViewerLookTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		static void Check( bool ok, string reason ) { if ( !ok ) throw new Exception( reason ); }
		static PolyMesh Cube()
		{
			var m = new PolyMesh();
			for ( int i = 0; i < 8; i++ ) m.AddVertex( new Vec3( ( i & 1 ) == 0 ? -1f : 1f, ( i & 2 ) == 0 ? -1f : 1f, ( i & 4 ) == 0 ? -1f : 1f ) );
			foreach ( var f in new[] { new[] { 0, 2, 3, 1 }, new[] { 4, 5, 7, 6 }, new[] { 0, 1, 5, 4 }, new[] { 2, 6, 7, 3 }, new[] { 0, 4, 6, 2 }, new[] { 1, 3, 7, 5 } } ) m.AddFace( f );
			return m;
		}

		registry.Add( "viewer-look", "vertex layout carries a colour channel", () =>
		{
			Check( ViewportMesh.FloatsPerVertex == 24, $"FloatsPerVertex is {ViewportMesh.FloatsPerVertex}, expected 24 (20 + rgba)" );
		} );

		registry.Add( "viewer-look", "extract writes mesh vertex colours per corner, white when absent", () =>
		{
			var coloured = Cube();
			var colours = new Vec4[8];
			for ( int i = 0; i < 8; i++ ) colours[i] = new Vec4( 1f, 0f, 0f, 1f );
			colours[0] = new Vec4( 0.25f, 0.5f, 0.75f, 1f );
			coloured.VertexColors = colours;
			using var cm = ViewportMesh.FromPolyMesh( coloured, _ => "cube" );
			// First face is { 0, 2, 3, 1 }: first corner is vertex 0.
			Check( cm.Vertices.Length == 24 * ViewportMesh.FloatsPerVertex, $"coloured cube vertices {cm.Vertices.Length}, expected {24 * ViewportMesh.FloatsPerVertex}" );
			Check( Math.Abs( cm.Vertices[20] - 0.25f ) < 1e-6 && Math.Abs( cm.Vertices[21] - 0.5f ) < 1e-6 && Math.Abs( cm.Vertices[22] - 0.75f ) < 1e-6 && Math.Abs( cm.Vertices[23] - 1f ) < 1e-6,
				$"first corner colour is ({cm.Vertices[20]},{cm.Vertices[21]},{cm.Vertices[22]},{cm.Vertices[23]}), expected (0.25,0.5,0.75,1)" );
			using var plain = ViewportMesh.FromPolyMesh( Cube(), _ => "cube" );
			Check( plain.Vertices[20] == 1f && plain.Vertices[21] == 1f && plain.Vertices[22] == 1f && plain.Vertices[23] == 1f, "colourless mesh must default to white" );
		} );

		registry.Add( "viewer-look", "merged zero padding resolves to white, wind-zero brown survives", () =>
		{
			// MeshTransform.Append pads colourless bodies with exact Vec4.Zero: that padding must
			// not render black. Only the exact zero vector remaps; a wind-zero foliage vertex
			// (0.4, 0.3, 0.2, 0) keeps its brown.
			var m = Cube();
			var colours = new Vec4[8];
			for ( int i = 0; i < 8; i++ ) colours[i] = new Vec4( 0.25f, 0.5f, 0.75f, 1f );
			colours[0] = new Vec4( 0f, 0f, 0f, 0f );
			colours[2] = new Vec4( 0.4f, 0.3f, 0.2f, 0f );
			m.VertexColors = colours;
			using var cm = ViewportMesh.FromPolyMesh( m, _ => "cube" );
			// First face { 0, 2, 3, 1 }: corner 0 = vertex 0 (padding), corner 1 = vertex 2 (wind-zero).
			int o0 = 20, o1 = ViewportMesh.FloatsPerVertex + 20;
			Check( cm.Vertices[o0] == 1f && cm.Vertices[o0 + 1] == 1f && cm.Vertices[o0 + 2] == 1f && cm.Vertices[o0 + 3] == 1f,
				$"padded zero vertex extracted as ({cm.Vertices[o0]},{cm.Vertices[o0 + 1]},{cm.Vertices[o0 + 2]},{cm.Vertices[o0 + 3]}), expected white" );
			Check( Math.Abs( cm.Vertices[o1] - 0.4f ) < 1e-6 && Math.Abs( cm.Vertices[o1 + 1] - 0.3f ) < 1e-6 && Math.Abs( cm.Vertices[o1 + 2] - 0.2f ) < 1e-6 && cm.Vertices[o1 + 3] == 0f,
				$"wind-zero vertex extracted as ({cm.Vertices[o1]},{cm.Vertices[o1 + 1]},{cm.Vertices[o1 + 2]},{cm.Vertices[o1 + 3]}), expected (0.4,0.3,0.2,0)" );
		} );

		registry.Add( "viewer-look", "ForDetail rust is rust-brown, not the neutral default", () =>
		{
			var d = MaterialPalette.ForDetail( new DetailMaterial { Kind = "rust", Slot = 2, Key = "rust" }, null );
			Check( d.BaseColorFactor.x > d.BaseColorFactor.y && d.BaseColorFactor.y > d.BaseColorFactor.z, $"rust colour {d.BaseColorFactor} is not rust-brown" );
			Check( Math.Abs( d.RoughnessFactor - 0.85f ) < 1e-6 && Math.Abs( d.MetallicFactor - 0.05f ) < 1e-6, "rust rough/metal wrong" );
			Check( Math.Abs( d.BaseColorFactor.x - 0.62f ) > 0.05, "rust must differ from the neutral default" );
		} );

		registry.Add( "viewer-look", "ForDetail algae is green and stored colours pass through", () =>
		{
			var a = MaterialPalette.ForDetail( new DetailMaterial { Kind = "algae", Slot = 3, Key = "algae" }, null );
			Check( a.BaseColorFactor.y > a.BaseColorFactor.x && a.BaseColorFactor.y > a.BaseColorFactor.z, $"algae colour {a.BaseColorFactor} is not green" );
			var h = MaterialPalette.ForDetail( new DetailMaterial { Kind = "hard", Slot = 4, Key = "hard", Colour = new Vec3( 0.2f, 0.3f, 0.4f ), Metalness = 0.9f, Roughness = 0.25f }, null );
			Check( Math.Abs( h.MetallicFactor - 0.9f ) < 1e-6 && Math.Abs( h.RoughnessFactor - 0.25f ) < 1e-6, "stored metal/rough must pass through" );
			Check( h.BaseColorFactor.x < h.BaseColorFactor.y && h.BaseColorFactor.y < h.BaseColorFactor.z, "stored colour order must survive linearization" );
		} );

		registry.Add( "viewer-look", "ForDetail worn fades from the source look", () =>
		{
			var source = new SlotLook { Color = new Vec3( 0.2f, 0.4f, 0.6f ), Rough = 0.3f, Metal = 0.1f };
			var w = MaterialPalette.ForDetail( new DetailMaterial { Kind = "worn", Slot = 5, SourceSlot = 0, Key = "worn" }, source );
			Check( Math.Abs( w.RoughnessFactor - 0.3f ) < 1e-6 && Math.Abs( w.MetallicFactor - 0.1f ) < 1e-6, "worn must inherit source rough/metal" );
			Check( w.BaseColorFactor.x != 0.62f, "worn must not be the neutral default" );
		} );
	}
}
