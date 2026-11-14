#nullable enable
using Effigy;
using Marionette.Desktop;

/// <summary>Mobile mesh-edit v5 baked maps: a v5 edit (normal + AO baked onto the mesh's UVs)
/// opens in Desktop and the maps survive save/reopen; legacy v1-v4 edits still open with no
/// bake; malformed and newer-than-v5 sidecars fail loudly instead of silently dropping the bake.
/// Covers <see cref="MeshEditBlob"/> v5 and <see cref="MeshEditFeature"/> SetBake/save/load.
/// The bake is hung on the output body by AttachBake inside Evaluate, which needs the
/// PaintCanvas NormalMap/AmbientOcclusionMap fields from the in-progress paint-channels ticket;
/// that wiring is verified by the final integrator, not here.</summary>
internal static class MeshEditV5BakeTests
{
	static PolyMesh Quad()
	{
		var mesh = new PolyMesh();
		mesh.AddVertex( new Vec3( -1, -1, 0 ) );
		mesh.AddVertex( new Vec3( 1, -1, 0 ) );
		mesh.AddVertex( new Vec3( 1, 1, 0 ) );
		mesh.AddVertex( new Vec3( -1, 1, 0 ) );
		mesh.AddFace( new[] { 0, 1, 2, 3 } );
		return mesh;
	}

	static PolyMesh Triangle()
	{
		var mesh = new PolyMesh();
		mesh.AddVertex( new Vec3( -1, -1, 0 ) );
		mesh.AddVertex( new Vec3( 1, -1, 0 ) );
		mesh.AddVertex( new Vec3( 0, 1, 0 ) );
		mesh.AddFace( new[] { 0, 1, 2 } );
		return mesh;
	}

	/// <summary>2x2 canvas, every texel the same flat colour, so a dropped or swapped map shows.</summary>
	static PaintCanvas Flat( byte r, byte g, byte b )
	{
		var c = new PaintCanvas( 2, 2 );
		for ( var i = 0; i < c.Rgba.Length; i += 4 )
		{
			c.Rgba[i] = r;
			c.Rgba[i + 1] = g;
			c.Rgba[i + 2] = b;
			c.Rgba[i + 3] = 255;
		}
		return c;
	}

	static bool SameTexels( PaintCanvas? a, PaintCanvas? b ) =>
		a is not null && b is not null && a.Width == b.Width && a.Height == b.Height
		&& a.Rgba.AsSpan().SequenceEqual( b.Rgba );

	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "meshedit-v5", name, test );
		void Require( bool condition, string message )
		{
			if ( !condition ) throw new Exception( message );
		}
		void Throws<T>( Action action, string what ) where T : Exception
		{
			try { action(); }
			catch ( T ) { return; }
			catch ( Exception ex ) { throw new Exception( $"{what}: expected {typeof( T ).Name}, got {ex.GetType().Name}: {ex.Message}" ); }
			throw new Exception( $"{what}: expected {typeof( T ).Name}, got silent success" );
		}

		Check( "v5 blob round-trips baked normal and AO texels", () =>
		{
			var normal = Flat( 128, 128, 255 );
			var ao = Flat( 64, 64, 64 );
			var bytes = MeshEditBlob.Write( Quad(), 1234, null, null, normal, ao );
			var mesh = MeshEditBlob.Read( bytes, out var fp, out var pieces, out var creases, out var readNormal, out var readAo );
			Require( fp == 1234, $"fingerprint: expected 1234, got {fp}" );
			Require( mesh.VertexCount == 4, $"mesh: expected 4 vertices, got {mesh.VertexCount}" );
			Require( SameTexels( normal, readNormal ), "baked normal map texels did not survive the round-trip" );
			Require( SameTexels( ao, readAo ), "baked AO map texels did not survive the round-trip" );
		} );

		Check( "v5 blob with no bake reads back null maps", () =>
		{
			var bytes = MeshEditBlob.Write( Quad(), 7 );
			_ = MeshEditBlob.Read( bytes, out _, out _, out _, out var readNormal, out var readAo );
			Require( readNormal is null && readAo is null, "a bakeless edit came back with maps attached" );
		} );

		Check( "legacy v4 blob reads with no bake", () =>
		{
			// A v5 blob written with no bake is a v4 blob plus two trailing present=false flags;
			// patch the version back to 4 and drop those two bytes for a byte-exact v4 blob.
			var bytes = MeshEditBlob.Write( Quad(), 7 );
			var legacy = bytes[..^2];
			Array.Copy( BitConverter.GetBytes( 4 ), 0, legacy, 8, 4 );
			var mesh = MeshEditBlob.Read( legacy, out _, out _, out _, out var readNormal, out var readAo );
			Require( mesh.VertexCount == 4, $"legacy mesh: expected 4 vertices, got {mesh.VertexCount}" );
			Require( readNormal is null && readAo is null, "a legacy v4 edit came back with maps attached" );
			// The old four-argument Read still works on it too.
			_ = MeshEditBlob.Read( legacy, out _, out _, out _ );
		} );

		Check( "format 6 blob fails clearly instead of opening", () =>
		{
			var bytes = MeshEditBlob.Write( Quad(), 7, null, null, Flat( 128, 128, 255 ), Flat( 64, 64, 64 ) );
			Array.Copy( BitConverter.GetBytes( 6 ), 0, bytes, 8, 4 );
			Throws<InvalidDataException>( () => MeshEditBlob.Read( bytes, out _, out _, out _, out _, out _ ),
				"newer-than-v5 blob" );
		} );

		Check( "truncated blob fails loudly instead of opening as a partial mesh", () =>
		{
			var bytes = MeshEditBlob.Write( Quad(), 7, null, null, Flat( 128, 128, 255 ), Flat( 64, 64, 64 ) );
			var cut = bytes[..(bytes.Length / 2)];
			Throws<Exception>( () => MeshEditBlob.Read( cut, out _, out _, out _, out _, out _ ), "truncated blob" );
		} );

		Check( "SetBake needs a committed edit", () =>
		{
			var feature = new MeshEditFeature();
			Throws<InvalidOperationException>( () => feature.SetBake( Flat( 128, 128, 255 ), null ), "SetBake with no edit" );
		} );

		Check( "bake survives feature save and reopen", () =>
		{
			var normal = Flat( 128, 128, 255 );
			var ao = Flat( 64, 64, 64 );
			var feature = new MeshEditFeature();
			feature.Commit( Quad(), Quad() );
			feature.SetBake( normal, ao );
			Require( feature.HasBake, "HasBake is false right after SetBake" );
			var bytes = feature.SaveMesh();
			Require( bytes is not null, "SaveMesh returned nothing for a committed edit with a bake" );
			// The saved blob itself carries the bake.
			_ = MeshEditBlob.Read( bytes, out _, out _, out _, out var savedNormal, out var savedAo );
			Require( SameTexels( normal, savedNormal ), "saved blob lost the baked normal map" );
			Require( SameTexels( ao, savedAo ), "saved blob lost the baked AO map" );
			// LoadMesh stores the blob; re-saving it hands the same bytes back, bake intact.
			var reopened = new MeshEditFeature();
			reopened.LoadMesh( bytes );
			var resaved = reopened.SaveMesh();
			Require( resaved is not null && resaved.AsSpan().SequenceEqual( bytes ),
				"re-saving a loaded v5 edit changed the bytes (the bake must survive reopening)" );
		} );

		Check( "SetBake clones: later edits to the source canvas do not leak in", () =>
		{
			var normal = Flat( 128, 128, 255 );
			var feature = new MeshEditFeature();
			feature.Commit( Quad(), Quad() );
			feature.SetBake( normal, null );
			normal.Rgba[0] = 0;
			Require( feature.BakedNormal is not null && feature.BakedNormal.Rgba[0] == 128,
				"mutating the source canvas after SetBake changed the stored bake" );
		} );

		Check( "commit with changed topology drops the bake", () =>
		{
			var feature = new MeshEditFeature();
			feature.Commit( Quad(), Quad() );
			feature.SetBake( Flat( 128, 128, 255 ), Flat( 64, 64, 64 ) );
			feature.Commit( Triangle(), Quad() );
			Require( !feature.HasBake && feature.BakedNormal is null && feature.BakedAo is null,
				"a bake laid out on the old mesh's UVs survived a topology-changing commit" );
		} );

		Check( "Clear drops the bake", () =>
		{
			var feature = new MeshEditFeature();
			feature.Commit( Quad(), Quad() );
			feature.SetBake( Flat( 128, 128, 255 ), null );
			feature.Clear();
			Require( !feature.HasBake, "Clear left the bake behind" );
		} );
	}
}
