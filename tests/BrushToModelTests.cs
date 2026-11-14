#nullable enable
using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Effigy;
using Effigy.Map;

/// <summary>
/// Propper++: brush-built detail becomes a model (see
/// kernel/Effigy/Map/BrushToModel.cs, board hammer-propper-brush-to-model).
///
/// The acceptance numbers live in the fixture
/// tests/fixtures/hammer/hammer-propper-brush-to-model/cases.json and one
/// check below recomputes them from that file; the rest build the same cases
/// synthetically so a missing fixture can never hide a geometry failure.
/// Volumes are asserted on the asset's Volume (|SignedVolume|, positive)
/// and every converted mesh must be a closed solid with its atlas UVs in
/// the 0..1 square and a filled AO map.
///
/// DEPENDENCY NOTES. MapBrush and MapBakeExport LANDED during this ticket
/// (see the status notes in BrushToModel.cs), so the MapBrush checks below
/// run through the real bake path; the PolyMesh checks cover MapCsg-style
/// callers whose solids are already PolyMeshes. MapDocument is still
/// missing and there is no canonical ModelRegistry, so these checks use
/// the interim Effigy.Map ModelRegistry in BrushToModel.cs. When those
/// land, the adapter checks belong with them; nothing here assumes their
/// signatures. MapBrushFace is written fully qualified (Effigy.MapBrushFace)
/// because Displacement.cs defines a different Effigy.Map.MapBrushFace.
///
/// NOT yet registered in tests/TestRegistry.cs — that file is
/// coordinator-owned; the final integrator adds the one
/// BrushToModelTests.Register(registry, settings); line.
/// </summary>
internal static class BrushToModelTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "brushtomodel", name, test );

		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}

		static PolyMesh Moved( PolyMesh m, float x, float y, float z )
		{
			var c = m.Clone();
			for ( int i = 0; i < c.Positions.Count; i++ ) { var p = c.Positions[i]; c.Positions[i] = new Vec3( p.x + x, p.y + y, p.z + z ); }
			c.InvalidateVertexCache();
			return c;
		}

		static bool Closed( PolyMesh? m ) => m != null && m.FaceCount > 0 && MeshValidator.Validate( m ).IsClosed;

		void RequireVolume( float got, float expected, string message )
		{
			Require( got > 0, $"{message}: volume {got} is not positive" );
			Require( Math.Abs( got - expected ) < 1e-3f * Math.Max( 1f, expected ),
				$"{message}: volume {got:0.0000} vs expected {expected:0.0000}" );
		}

		void RequireVec( Vec3 got, Vec3 expected, string message )
		{
			Require( Math.Abs( got.x - expected.x ) < 1e-3f
				&& Math.Abs( got.y - expected.y ) < 1e-3f
				&& Math.Abs( got.z - expected.z ) < 1e-3f,
				$"{message}: got ({got.x:0.###}, {got.y:0.###}, {got.z:0.###}) vs expected ({expected.x:0.###}, {expected.y:0.###}, {expected.z:0.###})" );
		}

		// The trim from the fixture: a 4x1x1 bar with a 1x1x1 riser on top
		// of its +X end, sharing the bar's top face. Volume 4 + 1 = 5.
		static PolyMesh[] TrimBrushes() => new[]
		{
			Primitives.Box( 4, 1, 1 ),
			Moved( Primitives.Box( 1, 1, 1 ), 1.5f, 0f, 1f ),
		};

		static string[] TrimMaterials() => new[] { "trim/brick" };

		static BrushToModelOptions TrimOptions( string name ) => new()
		{
			ModelName = name,
			AoSize = 32,
			AoRays = 4,
		};

		static string? FindFixture( string name )
		{
			foreach ( var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() } )
			{
				var dir = new DirectoryInfo( Path.GetFullPath( start ) );
				for ( int depth = 0; depth < 10 && dir != null; depth++, dir = dir.Parent )
				{
					string candidate = Path.Combine( dir.FullName, "tests", "fixtures", "hammer", "hammer-propper-brush-to-model", name );
					if ( File.Exists( candidate ) ) return candidate;
				}
			}
			return null;
		}

		Check( "trim selection converts to a registered model with atlas and AO", () =>
		{
			var registry = new ModelRegistry();
			var r = BrushToModel.Convert( TrimBrushes(), TrimMaterials(), TrimOptions( "trim" ), registry );
			Require( r.Ok, $"trim conversion refused: {r.Reason} {r.Message}" );
			var a = r.Asset!;
			Require( a.SourceBrushCount == 2, $"expected 2 source brushes, got {a.SourceBrushCount}" );
			Require( a.FacesKept == 12, $"trim should keep 12 faces, kept {a.FacesKept}" );
			Require( a.FacesDroppedNoDraw == 0, $"trim dropped {a.FacesDroppedNoDraw} faces" );
			RequireVolume( a.Volume, 5f, "trim" );
			RequireVolume( a.MassKg / 0.01f, 5f, "trim mass at default density" );
			RequireVec( a.Centre, new Vec3( 0f, 0f, 0.5f ), "trim centre" );
			Require( Closed( a.Mesh ), "converted trim mesh is not a closed solid" );
			Require( a.Atlas.Charts >= 1, $"atlas produced {a.Atlas.Charts} charts" );
			Require( a.Atlas.Faces == 12, $"atlas covers {a.Atlas.Faces} faces, expected 12" );
			foreach ( var f in a.Mesh.Faces )
			foreach ( var uv in f.UVs )
				Require( uv.x >= -1e-3f && uv.x <= 1.001f && uv.y >= -1e-3f && uv.y <= 1.001f,
					$"atlas UV outside 0..1: ({uv.x}, {uv.y})" );
			Require( a.AoBake?.AmbientOcclusion != null, "trim has no AO map" );
			Require( a.AoBake!.AmbientOcclusion.Width == 32 && a.AoBake.AmbientOcclusion.Height == 32,
				$"AO map is {a.AoBake.AmbientOcclusion.Width}x{a.AoBake.AmbientOcclusion.Height}, expected 32x32" );
			Require( a.AoFilledFraction > 0.1f, $"AO filled fraction {a.AoFilledFraction:0.###} is too low (atlas coverage of the square)" );
			Require( a.AoMin >= 0f && a.AoMin <= a.AoMean && a.AoMean <= a.AoMax && a.AoMax <= 1f,
				$"AO summary out of order: min {a.AoMin:0.###} mean {a.AoMean:0.###} max {a.AoMax:0.###}" );
			Require( a.AoMean > 0.5f, $"AO mean {a.AoMean:0.###} is implausibly dark for a trim" );
			Require( a.Physics != null && a.Physics.Hulls.Count >= 1,
				$"trim physics has {a.Physics?.Hulls.Count ?? 0} hulls" );
			Require( Math.Abs( a.PhysicsVolume - a.Volume ) < 1e-3f * a.Volume,
				$"physics volume {a.PhysicsVolume:0.###} must equal mesh volume {a.Volume:0.###} (one hull per brush)" );
			Require( registry.TryGet( "trim", out var found ) && ReferenceEquals( found, a ),
				"registered trim does not round-trip through the registry" );
			Require( r.Prop != null && r.Prop.ModelName == "trim", "prop reference is missing or names the wrong model" );
			RequireVec( r.Prop.Origin, a.Centre, "prop origin" );
			Require( r.RemovedBrushCount == 2 && r.KeptBrushCount == 0,
				$"default replace reports removed {r.RemovedBrushCount} kept {r.KeptBrushCount}, expected 2/0" );
		} );

		Check( "single box volume mass and bounds are sane", () =>
		{
			var registry = new ModelRegistry();
			var r = BrushToModel.Convert(
				new[] { Primitives.Box( 4, 2, 1 ) },
				new[] { "trim/brick" },
				new BrushToModelOptions { ModelName = "box", DensityKgPerCubicInch = 0.02f },
				registry );
			Require( r.Ok, $"box conversion refused: {r.Reason} {r.Message}" );
			var a = r.Asset!;
			RequireVolume( a.Volume, 8f, "box" );
			Require( Math.Abs( a.MassKg - 0.16f ) < 1e-3f, $"box mass {a.MassKg:0.0000} vs expected 0.16" );
			RequireVec( a.Centre, Vec3.Zero, "box centre" );
			RequireVec( a.Min, new Vec3( -2f, -1f, -0.5f ), "box min" );
			RequireVec( a.Max, new Vec3( 2f, 1f, 0.5f ), "box max" );
			Require( a.Materials.Count == 1 && a.Materials[0] == "trim/brick",
				$"box materials [{string.Join( ",", a.Materials )}] did not preserve the material name" );
			Require( a.Mesh.Faces.Count == 6, $"box mesh has {a.Mesh.Faces.Count} faces, expected 6" );
			Require( a.Physics!.Hulls.Count == 1, $"box physics has {a.Physics.Hulls.Count} hulls, expected 1 (one hull per brush)" );
			Require( Math.Abs( a.PhysicsVolume - 8f ) < 1e-3f * 8f, $"box physics volume {a.PhysicsVolume:0.###} vs expected 8" );
		} );

		Check( "non-uniform scale scales bounds about the centre", () =>
		{
			var registry = new ModelRegistry();
			var r = BrushToModel.Convert(
				new[] { Moved( Primitives.Box( 2, 2, 2 ), 10f, 0f, 0f ) },
				new[] { "trim/brick" },
				new BrushToModelOptions
				{
					ModelName = "scaled",
					Scale = new Vec3( 2f, 1f, 0.5f ),
					AoSize = 32,
					AoRays = 4,
				},
				registry );
			Require( r.Ok, $"scaled conversion refused: {r.Reason} {r.Message}" );
			var a = r.Asset!;
			RequireVolume( a.Volume, 8f, "scaled box (2 x 1 x 0.5 scale keeps volume 8)" );
			RequireVec( a.Centre, new Vec3( 10f, 0f, 0f ), "scaled box centre must stay where the brush was" );
			RequireVec( a.Min, new Vec3( 8f, -1f, -0.5f ), "scaled box min" );
			RequireVec( a.Max, new Vec3( 12f, 1f, 0.5f ), "scaled box max" );
			RequireVec( r.Prop!.Scale, new Vec3( 2f, 1f, 0.5f ), "prop scale" );
		} );

		Check( "conversion never mutates the input brushes", () =>
		{
			var box = Primitives.Box( 2, 2, 2 );
			float beforeVolume = box.SignedVolume();
			var beforeFirst = box.Positions[0];
			int beforeFaces = box.FaceCount;
			var r = BrushToModel.Convert( new[] { box }, new[] { "trim/brick" },
				new BrushToModelOptions { ModelName = "pure", AoSize = 32, AoRays = 4 }, new ModelRegistry() );
			Require( r.Ok, $"conversion refused: {r.Reason} {r.Message}" );
			Require( Math.Abs( box.SignedVolume() - beforeVolume ) < 1e-6f, "input brush volume changed" );
			Require( box.Positions[0].Equals( beforeFirst ), "input brush vertex moved" );
			Require( box.FaceCount == beforeFaces, "input brush lost faces" );
		} );

		Check( "nodraw faces are dropped from the baked mesh", () =>
		{
			var visible = Primitives.Box( 2, 2, 2 );
			var hidden = Moved( Primitives.Box( 2, 2, 2 ), 10f, 0f, 0f );
			foreach ( var f in hidden.Faces ) f.Material = 1;
			var registry = new ModelRegistry();
			var r = BrushToModel.Convert(
				new[] { visible, hidden },
				new[] { "trim/brick", "tools/toolsnodraw" },
				new BrushToModelOptions { ModelName = "nodraw", AoSize = 32, AoRays = 4 },
				registry );
			Require( r.Ok, $"nodraw conversion refused: {r.Reason} {r.Message}" );
			var a = r.Asset!;
			Require( a.FacesKept == 6 && a.FacesDroppedNoDraw == 6,
				$"kept {a.FacesKept} dropped {a.FacesDroppedNoDraw}, expected 6/6" );
			RequireVolume( a.Volume, 8f, "nodraw test (only the visible box counts)" );
			Require( a.Mesh.FaceCount == 6, $"mesh has {a.Mesh.FaceCount} faces, expected 6" );
			RequireVec( a.Min, new Vec3( -1f, -1f, -1f ), "nodraw test min (hidden box must not leak in)" );
			RequireVec( a.Max, new Vec3( 1f, 1f, 1f ), "nodraw test max (hidden box must not leak in)" );
			Require( Math.Abs( a.PhysicsVolume - a.Volume ) < 1e-3f * a.Volume,
				$"physics volume {a.PhysicsVolume:0.###} must equal visible-box volume {a.Volume:0.###}" );
		} );

		Check( "all-nodraw selection is refused with a named reason", () =>
		{
			var box = Primitives.Box( 2, 2, 2 );
			foreach ( var f in box.Faces ) f.Material = 1;
			var r = BrushToModel.Convert( new[] { box },
				new[] { "trim/brick", "tools/toolsnodraw" },
				new BrushToModelOptions { ModelName = "allnodraw" }, new ModelRegistry() );
			Require( !r.Ok, "all-nodraw conversion succeeded with nothing to model" );
			Require( r.Reason == "brush-to-model-all-nodraw", $"reason was '{r.Reason}'" );
			Require( r.Asset is null && r.Prop is null, "refused conversion must not carry an asset or prop" );
		} );

		Check( "prop entity JSON round-trips", () =>
		{
			var r = BrushToModel.Convert( TrimBrushes(), TrimMaterials(), TrimOptions( "roundtrip" ), new ModelRegistry() );
			Require( r.Ok, $"conversion refused: {r.Reason} {r.Message}" );
			var back = PropEntity.FromJsonString( r.Prop!.ToJsonString() );
			Require( back.Equals( r.Prop ), $"prop did not round-trip: {back} vs {r.Prop}" );
			Require( back.ModelName == "roundtrip", $"round-tripped model name '{back.ModelName}'" );
			RequireVec( back.Origin, r.Prop.Origin, "round-tripped origin" );
			RequireVec( back.Scale, r.Prop.Scale, "round-tripped scale" );
		} );

		Check( "duplicate model name is refused and the first asset survives", () =>
		{
			var registry = new ModelRegistry();
			var first = BrushToModel.Convert( TrimBrushes(), TrimMaterials(), TrimOptions( "dup" ), registry );
			Require( first.Ok, $"first conversion refused: {first.Reason} {first.Message}" );
			var second = BrushToModel.Convert( new[] { Primitives.Box( 1, 1, 1 ) }, TrimMaterials(),
				new BrushToModelOptions { ModelName = "dup", AoSize = 32, AoRays = 4 }, registry );
			Require( !second.Ok, "second conversion under a taken name succeeded" );
			Require( second.Reason == "brush-to-model-name-taken", $"reason was '{second.Reason}'" );
			Require( registry.Count == 1, $"registry holds {registry.Count} models, expected 1" );
			Require( registry.TryGet( "dup", out var kept ) && ReferenceEquals( kept, first.Asset ),
				"the first asset was replaced or lost" );
		} );

		Check( "empty and null selections are refused with named reasons", () =>
		{
			var empty = BrushToModel.Convert( Array.Empty<PolyMesh>(), TrimMaterials(),
				new BrushToModelOptions { ModelName = "empty" }, new ModelRegistry() );
			Require( !empty.Ok && empty.Reason == "brush-to-model-input-empty",
				$"empty selection reason was '{empty.Reason}'" );
			var nullSel = BrushToModel.Convert( null!, TrimMaterials(),
				new BrushToModelOptions { ModelName = "null" }, new ModelRegistry() );
			Require( !nullSel.Ok && nullSel.Reason == "brush-to-model-input-null",
				$"null selection reason was '{nullSel.Reason}'" );
		} );

		Check( "open mesh is refused with a named reason", () =>
		{
			var r = BrushToModel.Convert( new[] { Primitives.Plane( 2, 2 ) }, TrimMaterials(),
				new BrushToModelOptions { ModelName = "open" }, new ModelRegistry() );
			Require( !r.Ok && r.Reason == "brush-to-model-input-open",
				$"open mesh reason was '{r.Reason}'" );
			Require( r.Asset is null, "refused conversion must not carry an asset" );
		} );

		Check( "non-positive scale is refused with a named reason", () =>
		{
			var zero = BrushToModel.Convert( new[] { Primitives.Box( 2, 2, 2 ) }, TrimMaterials(),
				new BrushToModelOptions { ModelName = "zero", Scale = new Vec3( 2f, 0f, 1f ) }, new ModelRegistry() );
			Require( !zero.Ok && zero.Reason == "brush-to-model-bad-scale",
				$"zero scale reason was '{zero.Reason}'" );
			var neg = BrushToModel.Convert( new[] { Primitives.Box( 2, 2, 2 ) }, TrimMaterials(),
				new BrushToModelOptions { ModelName = "neg", Scale = new Vec3( -1f, 1f, 1f ) }, new ModelRegistry() );
			Require( !neg.Ok && neg.Reason == "brush-to-model-bad-scale",
				$"negative scale reason was '{neg.Reason}'" );
		} );

		Check( "bad material slot is refused with a named reason", () =>
		{
			var box = Primitives.Box( 2, 2, 2 );
			box.Faces[0].Material = 5;
			var r = BrushToModel.Convert( new[] { box }, TrimMaterials(),
				new BrushToModelOptions { ModelName = "badslot" }, new ModelRegistry() );
			Require( !r.Ok && r.Reason == "brush-to-model-bad-material-slot",
				$"bad slot reason was '{r.Reason}'" );
		} );

		Check( "keep-source-brushes flag reports kept brushes", () =>
		{
			var r = BrushToModel.Convert( TrimBrushes(), TrimMaterials(),
				new BrushToModelOptions { ModelName = "kept", AoSize = 32, AoRays = 4, KeepSourceBrushes = true },
				new ModelRegistry() );
			Require( r.Ok, $"conversion refused: {r.Reason} {r.Message}" );
			Require( r.RemovedBrushCount == 0 && r.KeptBrushCount == 2,
				$"keep-source reports removed {r.RemovedBrushCount} kept {r.KeptBrushCount}, expected 0/2" );
			Require( r.Asset != null && r.Prop != null, "kept-source conversion must still produce asset and prop" );
		} );

		Check( "AO and physics can be skipped", () =>
		{
			var r = BrushToModel.Convert( new[] { Primitives.Box( 2, 2, 2 ) }, TrimMaterials(),
				new BrushToModelOptions { ModelName = "skipped", BakeAo = false, BuildPhysics = false },
				new ModelRegistry() );
			Require( r.Ok, $"conversion refused: {r.Reason} {r.Message}" );
			var a = r.Asset!;
			Require( a.AoBake is null, "skipped AO still produced a bake" );
			Require( float.IsNaN( a.AoMean ) && float.IsNaN( a.AoMin ) && float.IsNaN( a.AoMax ),
				"skipped AO summary must be NaN" );
			Require( a.Physics is null && a.PhysicsVolume == 0f, "skipped physics still produced hulls" );
			RequireVolume( a.Volume, 8f, "skipped-stages box" );
			Require( a.Atlas.Charts >= 1, "skipped-stages box still needs an atlas" );
		} );

		Check( "fixture cases.json trim volume recomputes", () =>
		{
			string? path = FindFixture( "cases.json" );
			Require( path != null, "fixture cases.json not found under tests/fixtures/hammer/hammer-propper-brush-to-model/" );
			using var doc = JsonDocument.Parse( File.ReadAllText( path! ) );
			var trim = doc.RootElement.GetProperty( "cases" )[0];
			Require( trim.GetProperty( "name" ).GetString() == "trim", "first fixture case is not the trim" );
			var brushes = new System.Collections.Generic.List<PolyMesh>();
			foreach ( var b in trim.GetProperty( "brushes" ).EnumerateArray() )
			{
				var size = b.GetProperty( "size" );
				var off = b.GetProperty( "offset" );
				brushes.Add( Moved(
					Primitives.Box( size[0].GetSingle(), size[1].GetSingle(), size[2].GetSingle() ),
					off[0].GetSingle(), off[1].GetSingle(), off[2].GetSingle() ) );
			}
			var materials = new System.Collections.Generic.List<string>();
			foreach ( var m in trim.GetProperty( "materials" ).EnumerateArray() ) materials.Add( m.GetString()! );
			var r = BrushToModel.Convert( brushes, materials,
				new BrushToModelOptions { ModelName = "fixture-trim", AoSize = 32, AoRays = 4 }, new ModelRegistry() );
			Require( r.Ok, $"fixture trim conversion refused: {r.Reason} {r.Message}" );
			RequireVolume( r.Asset!.Volume, trim.GetProperty( "expectedVolume" ).GetSingle(), "fixture trim" );
			Require( r.Asset.FacesKept == trim.GetProperty( "expectedFaces" ).GetInt32(),
				$"fixture trim kept {r.Asset.FacesKept} faces" );
			var centre = trim.GetProperty( "expectedCentre" );
			RequireVec( r.Asset.Centre,
				new Vec3( centre[0].GetSingle(), centre[1].GetSingle(), centre[2].GetSingle() ),
				"fixture trim centre" );
		} );

		Check( "mapbrush selection converts through the bake path", () =>
		{
			var brushes = new[]
			{
				MapBrush.Box( new Vec3( -2f, -0.5f, -0.5f ), new Vec3( 2f, 0.5f, 0.5f ), "trim/brick" ),
				MapBrush.Box( new Vec3( 1f, -0.5f, 0.5f ), new Vec3( 2f, 0.5f, 1.5f ), "trim/brick" ),
			};
			var registry = new ModelRegistry();
			var r = BrushToModel.Convert( brushes, TrimOptions( "mb-trim" ), registry );
			Require( r.Ok, $"mapbrush conversion refused: {r.Reason} {r.Message}" );
			var a = r.Asset!;
			RequireVolume( a.Volume, 5f, "mapbrush trim" );
			Require( a.FacesKept == 12, $"mapbrush trim kept {a.FacesKept} faces, expected 12" );
			Require( a.FacesDroppedNoDraw == 0, $"mapbrush trim dropped {a.FacesDroppedNoDraw} faces" );
			Require( a.Materials.Count == 1 && a.Materials[0] == "trim/brick",
				$"mapbrush materials [{string.Join( ",", a.Materials )}]" );
			RequireVec( a.Centre, new Vec3( 0f, 0f, 0.5f ), "mapbrush trim centre" );
			// The bake mesh is per-face-corner soup (MapBakeExport's contract) and
			// keeps the internal shared faces, so MeshValidator-closed does not
			// apply; what must hold is that it is a valid mesh (no face errors).
			Require( MeshValidator.Validate( a.Mesh ).IsValid,
				"converted mapbrush trim mesh is not a valid mesh: " + MeshValidator.Validate( a.Mesh ).ToString() );
			Require( a.Atlas.Charts >= 1, "mapbrush trim atlas has no charts" );
			Require( a.AoFilledFraction > 0.1f, $"mapbrush trim AO filled {a.AoFilledFraction:0.###} (atlas coverage of the square)" );
			Require( a.AoMean > 0.5f, $"mapbrush trim AO mean {a.AoMean:0.###}" );
			Require( Math.Abs( a.PhysicsVolume - a.Volume ) < 1e-3f * a.Volume,
				$"mapbrush physics volume {a.PhysicsVolume:0.###} must equal mesh volume {a.Volume:0.###} (one hull per brush)" );
			Require( registry.TryGet( "mb-trim", out var found ) && ReferenceEquals( found, a ),
				"mapbrush trim does not round-trip through the registry" );
			RequireVec( r.Prop!.Origin, a.Centre, "mapbrush prop origin" );
			Require( r.RemovedBrushCount == 2 && r.KeptBrushCount == 0,
				$"mapbrush replace reports removed {r.RemovedBrushCount} kept {r.KeptBrushCount}, expected 2/0" );
		} );

		Check( "mapbrush nodraw faces are dropped", () =>
		{
			var brushes = new[]
			{
				MapBrush.Box( new Vec3( -1f, -1f, -1f ), new Vec3( 1f, 1f, 1f ), "trim/brick" ),
				MapBrush.Box( new Vec3( 9f, -1f, -1f ), new Vec3( 11f, 1f, 1f ), "tools/toolsnodraw" ),
			};
			var r = BrushToModel.Convert( brushes,
				new BrushToModelOptions { ModelName = "mb-nodraw", AoSize = 32, AoRays = 4 }, new ModelRegistry() );
			Require( r.Ok, $"mapbrush nodraw conversion refused: {r.Reason} {r.Message}" );
			var a = r.Asset!;
			Require( a.FacesKept == 6 && a.FacesDroppedNoDraw == 6,
				$"kept {a.FacesKept} dropped {a.FacesDroppedNoDraw}, expected 6/6" );
			RequireVolume( a.Volume, 8f, "mapbrush nodraw test" );
			RequireVec( a.Min, new Vec3( -1f, -1f, -1f ), "mapbrush nodraw min" );
			RequireVec( a.Max, new Vec3( 1f, 1f, 1f ), "mapbrush nodraw max" );
			Require( Math.Abs( a.PhysicsVolume - a.Volume ) < 1e-3f * a.Volume,
				$"mapbrush nodraw physics volume {a.PhysicsVolume:0.###} must equal visible-box volume {a.Volume:0.###}" );
		} );

		Check( "invalid mapbrush is refused with a named reason", () =>
		{
			var flat = MapBrush.Box( Vec3.Zero, Vec3.Zero, "trim/brick" );
			var r = BrushToModel.Convert( new[] { flat },
				new BrushToModelOptions { ModelName = "mb-invalid" }, new ModelRegistry() );
			Require( !r.Ok, "zero-volume mapbrush conversion succeeded" );
			Require( r.Reason == "brush-to-model-input-invalid", $"reason was '{r.Reason}'" );
			Require( r.Asset is null, "refused conversion must not carry an asset" );
		} );

		Check( "mapbrush UV defect still converts and reports it", () =>
		{
			var badUv = new FaceUv( Vec2.Zero, Vec2.Zero, 0f, 16f, 0 );
			var faces = new System.Collections.Generic.List<Effigy.MapBrushFace>
			{
				new Effigy.MapBrushFace( new Vec3( -1, 0, 0 ), 1f, new FaceMaterial( "trim/brick", badUv ) ),
				new Effigy.MapBrushFace( new Vec3( 1, 0, 0 ), 1f, "trim/brick" ),
				new Effigy.MapBrushFace( new Vec3( 0, -1, 0 ), 1f, "trim/brick" ),
				new Effigy.MapBrushFace( new Vec3( 0, 1, 0 ), 1f, "trim/brick" ),
				new Effigy.MapBrushFace( new Vec3( 0, 0, -1 ), 1f, "trim/brick" ),
				new Effigy.MapBrushFace( new Vec3( 0, 0, 1 ), 1f, "trim/brick" ),
			};
			var brush = MapBrush.FromPlanes( faces );
			var r = BrushToModel.Convert( new[] { brush },
				new BrushToModelOptions { ModelName = "mb-uvdefect", AoSize = 32, AoRays = 4 }, new ModelRegistry() );
			Require( r.Ok, $"UV-defect brush refused: {r.Reason} {r.Message}" );
			Require( r.Notes.Count > 0, "UV defect was not reported on the result notes" );
		} );

		Check( "no Carve operation exists", () =>
		{
			foreach ( var t in new[]
				{ typeof( BrushToModel ), typeof( BrushModelAsset ), typeof( ModelRegistry ),
				  typeof( PropEntity ), typeof( BrushToModelOptions ), typeof( BrushToModelResult ) } )
			{
				foreach ( var m in t.GetMembers( BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance ) )
					Require( !m.Name.Contains( "Carve", StringComparison.Ordinal ),
						$"{t.Name} exposes a member named '{m.Name}': Carve must not exist" );
			}
		} );
	}
}
