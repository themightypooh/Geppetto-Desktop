using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using Effigy;
using Effigy.Assembly;
using Marionette.Desktop;

// AS12: assembled + per-part export with materials.
// Mirrors Mobile app/AssemblyExportChecks.cs (non-UI assertions) in Desktop
// form. Two centred boxes (16x8x32 long, 8x8x8 cube) become an assembly
// with a repeated definition, a 90-degree rotation, a mirrored part, a
// hidden part and an external (unloaded) part. It exports the assembled
// OBJ / GLB and the per-part OBJs, then reads every file back
// independently and compares coordinates, dimensions, orientation,
// hierarchy and winding against values computed from the box corners —
// not from the export code. It also proves the document and the source
// meshes are byte-identical afterwards, and that a dangling joint blocks
// the export.
//
// GLB note: the GLB re-import check below (GlbInfo) is written against an
// independent reader — a minimal GLB JSON-chunk parser, not GlbExport or
// GlbImport — but it has NOT been run: no build is allowed on this ticket
// (PO override). The final integrator runs tests/Desktop.Tests.csproj and
// must see this check pass before anyone marks GLB green.
//
// Checks compare solved bounds, volumes and file contents, never only a
// success flag. Undo/redo re-fetch records from the document after each
// step: AssemblyHistory restores snapshots by replacing the document's
// collections, so a pre-undo object reference goes stale.
// Not runtime verified: the final integrator runs tests/Desktop.Tests.csproj.
internal static class AssemblyExportTests
{
	public static void Register( TestRegistry suite, TestSettings settings )
	{
		void Check( string name, Action test ) => suite.Add( "assembly-export", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		static bool Close( float a, float b, float tol ) => MathF.Abs( a - b ) <= tol;
		void Near( float a, float b, float tol, string what )
		{
			if ( !Close( a, b, tol ) ) throw new Exception( $"{what}: expected {b}, got {a}" );
		}
		static bool BoundsClose( (Vec3 Min, Vec3 Max) got, (Vec3 Min, Vec3 Max) want, float tol ) =>
			Close( got.Min.x, want.Min.x, tol ) && Close( got.Min.y, want.Min.y, tol ) && Close( got.Min.z, want.Min.z, tol )
			&& Close( got.Max.x, want.Max.x, tol ) && Close( got.Max.y, want.Max.y, tol ) && Close( got.Max.z, want.Max.z, tol );
		void RequireBounds( (Vec3 Min, Vec3 Max) got, (Vec3 Min, Vec3 Max) want, float tol, string what )
		{
			if ( !BoundsClose( got, want, tol ) )
				throw new Exception( $"{what}: got X [{got.Min.x:0.###}..{got.Max.x:0.###}] Y [{got.Min.y:0.###}..{got.Max.y:0.###}] Z [{got.Min.z:0.###}..{got.Max.z:0.###}] want X [{want.Min.x:0.###}..{want.Max.x:0.###}] Y [{want.Min.y:0.###}..{want.Max.y:0.###}] Z [{want.Min.z:0.###}..{want.Max.z:0.###}]" );
		}
		static (Vec3 Min, Vec3 Max) MeshBounds( PolyMesh m )
		{
			var min = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
			var max = new Vec3( float.MinValue, float.MinValue, float.MinValue );
			foreach ( var p in m.Positions )
			{
				min = new Vec3( MathF.Min( min.x, p.x ), MathF.Min( min.y, p.y ), MathF.Min( min.z, p.z ) );
				max = new Vec3( MathF.Max( max.x, p.x ), MathF.Max( max.y, p.y ), MathF.Max( max.z, p.z ) );
			}
			return (min, max);
		}
		// The 8 corners of a box centred on the origin, through the
		// occurrence transform and then a unit/axis conversion: the expected
		// bounds, computed from first principles.
		static (Vec3 Min, Vec3 Max) ExpectedBounds( Vec3 half, Xform xf, Func<Vec3, Vec3> convert = null )
		{
			var min = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
			var max = new Vec3( float.MinValue, float.MinValue, float.MinValue );
			for ( int i = 0; i < 8; i++ )
			{
				var p = new Vec3( ( i & 1 ) == 0 ? -half.x : half.x, ( i & 2 ) == 0 ? -half.y : half.y, ( i & 4 ) == 0 ? -half.z : half.z );
				var q = convert == null ? xf.TransformPoint( p ) : convert( xf.TransformPoint( p ) );
				min = new Vec3( MathF.Min( min.x, q.x ), MathF.Min( min.y, q.y ), MathF.Min( min.z, q.z ) );
				max = new Vec3( MathF.Max( max.x, q.x ), MathF.Max( max.y, q.y ), MathF.Max( max.z, q.z ) );
			}
			return (min, max);
		}
		static (Vec3 Min, Vec3 Max) Union( (Vec3 Min, Vec3 Max) a, (Vec3 Min, Vec3 Max) b ) =>
			(new Vec3( MathF.Min( a.Min.x, b.Min.x ), MathF.Min( a.Min.y, b.Min.y ), MathF.Min( a.Min.z, b.Min.z ) ),
				new Vec3( MathF.Max( a.Max.x, b.Max.x ), MathF.Max( a.Max.y, b.Max.y ), MathF.Max( a.Max.z, b.Max.z ) ));
		// Signed volume: positive when every face winds outward. A mirrored
		// part that kept its original winding would come back negative.
		static float SignedVolume( PolyMesh m )
		{
			float v = 0;
			foreach ( var f in m.Faces )
			{
				for ( int i = 1; i + 1 < f.Indices.Length; i++ )
				{
					var a = m.Positions[f.Indices[0]]; var b = m.Positions[f.Indices[i]]; var c = m.Positions[f.Indices[i + 1]];
					v += Vec3.Dot( a, Vec3.Cross( b, c ) ) / 6f;
				}
			}
			return v;
		}
		static Vec3 G( Vec3 v ) => new Vec3( v.x, v.z, -v.y );   // Effigy Z-up to glTF Y-up, as GlbExport does it
		// Independent GLB reader for the re-import check: parses the GLB
		// container and JSON chunk directly, never GlbExport / GlbImport.
		static (string[] Nodes, string[] Meshes, string[] Materials, bool HasAnimations, Dictionary<string, (Vec3 Min, Vec3 Max)> PosBounds) GlbInfo( byte[] bytes )
		{
			if ( bytes.Length < 20 || BitConverter.ToUInt32( bytes, 0 ) != 0x46546C67u ) throw new FormatException( "not a GLB" );
			int jsonLen = BitConverter.ToInt32( bytes, 12 );
			var root = JsonNode.Parse( Encoding.UTF8.GetString( bytes, 20, jsonLen ) ).AsObject();
			var nodes = root["nodes"].AsArray().Select( n => n["name"]?.GetValue<string>() ).ToArray();
			var meshes = root["meshes"].AsArray().Select( m => m["name"]?.GetValue<string>() ).ToArray();
			var materials = root["materials"]?.AsArray().Select( m => m["name"]?.GetValue<string>() ).ToArray() ?? Array.Empty<string>();
			var accessors = root["accessors"].AsArray();
			var bounds = new Dictionary<string, (Vec3, Vec3)>();
			foreach ( var mesh in root["meshes"].AsArray() )
			{
				var prim = mesh["primitives"][0].AsObject();
				int ai = prim["attributes"]["POSITION"].GetValue<int>();
				var acc = accessors[ai].AsObject();
				// POSITION accessor min/max are flat [x, y, z] triples, not nested arrays.
				var mn = acc["min"].AsArray(); var mx = acc["max"].AsArray();
				bounds[mesh["name"].GetValue<string>()] =
					(new Vec3( mn[0].GetValue<float>(), mn[1].GetValue<float>(), mn[2].GetValue<float>() ),
						new Vec3( mx[0].GetValue<float>(), mx[1].GetValue<float>(), mx[2].GetValue<float>() ));
			}
			return (nodes, meshes, materials, root.ContainsKey( "animations" ), bounds);
		}
		static string TempDir()
		{
			var dir = Path.Combine( Path.GetTempPath(), "asm12-" + Guid.NewGuid().ToString( "N" ) );
			Directory.CreateDirectory( dir );
			return dir;
		}
		static void DeleteDir( string dir )
		{
			try { if ( Directory.Exists( dir ) ) Directory.Delete( dir, true ); } catch { /* best effort cleanup */ }
		}


		// Fixture: A (anchor, identity) -hinge- B (5,0,0); pin is the world
		// Z axis through the origin; limits -90..90 degrees.
		(AssemblyDocument Doc, AssemblyJoint Hinge, string A, string B, PartStudio Studio, Body LongBody) HingeFixture()
		{
			var studio = new PartStudio();
			var longBody = new Body( "body-long", "Long", BoxMesh( 8, 4, 16 ) );
			studio.Bodies.Add( longBody );
			var doc = new AssemblyDocument();
			doc.Definitions.Add( new AssemblyPartDefinition
			{
				Id = AssemblyDocument.NewId(), Name = "Long",
				SourceKind = "studio-body", SourceReference = longBody.Id,
			} );
			var def = doc.Definitions[0].Id;
			AssemblyOccurrence Occ( string name, Xform t ) => new AssemblyOccurrence
			{
				Id = AssemblyDocument.NewId(), DefinitionId = def, Name = name,
				Transform = AssemblyTransform.FromXform( t ),
			};
			var a = Occ( "Base", Xform.Identity );
			var b = Occ( "Arm", Xform.Translate( new Vec3( 5, 0, 0 ) ) );
			doc.Occurrences.AddRange( new[] { a, b } );
			var hinge = new AssemblyJoint
			{
				Id = AssemblyDocument.NewId(), Kind = "hinge", OccurrenceA = a.Id, OccurrenceB = b.Id,
				FrameA = AssemblyTransform.FromXform( Xform.Identity ),
				FrameB = AssemblyTransform.FromXform( Xform.Translate( new Vec3( -5, 0, 0 ) ) ),
				Minimum = -MathF.PI / 2, Maximum = MathF.PI / 2,
			};
			doc.Joints.Add( hinge );
			return (doc, hinge, a.Id, b.Id, studio, longBody);
		}

		// Fixture: Rail (anchor, identity) -slider- Carriage at (0,0,1),
		// limits [-2, 4] in. The carriage is the 8x8x8 cube.
		(AssemblyDocument Doc, AssemblyJoint Slider, string A, string B, PartStudio Studio, Body CubeBody) SliderFixture()
		{
			var studio = new PartStudio();
			var cubeBody = new Body( "body-cube", "Cube", BoxMesh( 4, 4, 4 ) );
			studio.Bodies.Add( cubeBody );
			var doc = new AssemblyDocument();
			doc.Definitions.Add( new AssemblyPartDefinition
			{
				Id = AssemblyDocument.NewId(), Name = "Cube",
				SourceKind = "studio-body", SourceReference = cubeBody.Id,
			} );
			var def = doc.Definitions[0].Id;
			var a = new AssemblyOccurrence { Id = AssemblyDocument.NewId(), DefinitionId = def, Name = "Rail", Transform = AssemblyTransform.FromXform( Xform.Identity ) };
			var b = new AssemblyOccurrence { Id = AssemblyDocument.NewId(), DefinitionId = def, Name = "Carriage", Transform = AssemblyTransform.FromXform( Xform.Translate( new Vec3( 0, 0, 1 ) ) ) };
			doc.Occurrences.AddRange( new[] { a, b } );
			var slider = new AssemblyJoint
			{
				Id = AssemblyDocument.NewId(), Kind = "slider", OccurrenceA = a.Id, OccurrenceB = b.Id,
				FrameA = AssemblyTransform.FromXform( Xform.Identity ),
				FrameB = AssemblyTransform.FromXform( Xform.Identity ),
				Minimum = -2f, Maximum = 4f,
			};
			doc.Joints.Add( slider );
			return (doc, slider, a.Id, b.Id, studio, cubeBody);
		}

		AssemblySourceResolver StudioResolverFor( PartStudio studio ) => AssemblyExportService.StudioResolver( studio );

		Check( "analysis: no errors, 3 exportable occurrences in document order", () =>
		{
			var f = new ExportFixture();
			var analysis = f.Analyze();
			Require( analysis.Errors.Count == 0, "expected no errors: " + string.Join( "; ", analysis.Errors ) );
			Require( analysis.Items.Count == 3, $"expected 3 items, got {analysis.Items.Count}" );
			Require( analysis.Items.Select( i => i.Occurrence.Name ).SequenceEqual( new[] { "Bracket", "Bracket", "Mirror" } ),
				"items must be in document order: " + string.Join( ", ", analysis.Items.Select( i => i.Occurrence.Name ) ) );
		} );

		Check( "analysis: shared name gets a _2 file stem", () =>
		{
			var f = new ExportFixture();
			var analysis = f.Analyze();
			Require( analysis.Items.Select( i => i.FileStem ).SequenceEqual( new[] { "Bracket", "Bracket_2", "Mirror" } ),
				"stems: " + string.Join( ", ", analysis.Items.Select( i => i.FileStem ) ) );
		} );

		Check( "analysis: external source + unsupported joint warned", () =>
		{
			var f = new ExportFixture();
			var analysis = f.Analyze();
			Require( analysis.Warnings.Count == 2
				&& analysis.Warnings.Any( w => w.Contains( "isn't loaded yet" ) )
				&& analysis.Warnings.Any( w => w.Contains( "ball" ) ),
				$"expected external + ball warnings, got: {string.Join( "; ", analysis.Warnings )}" );
		} );

		Check( "analysis: hidden, mirror, rename, joints and unjoined notes", () =>
		{
			var f = new ExportFixture();
			var analysis = f.Analyze();
			Require( analysis.Notes.Count == 5
				&& analysis.Notes.Any( n => n.Contains( "hidden" ) )
				&& analysis.Notes.Any( n => n.Contains( "Mirrored" ) )
				&& analysis.Notes.Any( n => n.Contains( "_2" ) )
				&& analysis.Notes.Any( n => n.Contains( "don't travel" ) )
				&& analysis.Notes.Any( n => n.Contains( "aren't joined" ) ),
				$"expected 5 notes, got: {string.Join( "; ", analysis.Notes )}" );
		} );

		Check( "analysis: mirrored occurrence flagged; one hidden, one unresolved skipped", () =>
		{
			var f = new ExportFixture();
			var analysis = f.Analyze();
			Require( analysis.Items.Single( i => i.Occurrence.Id == f.OccD ).Mirrored, "Mirror occurrence must be flagged mirrored" );
			Require( !analysis.Items.Single( i => i.Occurrence.Id == f.OccA ).Mirrored, "identity occurrence must not be flagged mirrored" );
			Require( analysis.HiddenSkipped == 1 && analysis.UnresolvedSkipped == 1,
				$"expected 1 hidden + 1 unresolved, got {analysis.HiddenSkipped} + {analysis.UnresolvedSkipped}" );
		} );

		Check( "assembled: vertex count is three copies of the source; ranges tile the merged mesh", () =>
		{
			var f = new ExportFixture();
			var analysis = f.Analyze();
			var (merged, ranges) = AssemblyExportService.BuildAssembled( f.Snapshot(), analysis );
			Require( merged.VertexCount == f.LongBody.Mesh.VertexCount * 3,
				$"expected {f.LongBody.Mesh.VertexCount * 3} vertices, got {merged.VertexCount}" );
			Require( ranges.Count == 3 && ranges[0].Start == 0 && ranges[1].Start == ranges[0].Count
				&& ranges[2].Start == ranges[0].Count + ranges[1].Count
				&& ranges[2].Start + ranges[2].Count == merged.VertexCount,
				"ranges must tile the merged mesh" );
			Require( ranges.Select( r => r.BodyName ).SequenceEqual( new[] { "Bracket", "Bracket", "Mirror" } ),
				"ranges must carry occurrence names" );
		} );

		Check( "assembled: bounds are the union of the three transformed boxes; source untouched", () =>
		{
			var f = new ExportFixture();
			var before = f.LongBody.Mesh.Positions.ToArray();
			var analysis = f.Analyze();
			var (merged, _) = AssemblyExportService.BuildAssembled( f.Snapshot(), analysis );
			var want = Union( Union( ExpectedBounds( f.LongHalf, f.XfA ), ExpectedBounds( f.LongHalf, f.XfB ) ), ExpectedBounds( f.LongHalf, f.XfD ) );
			RequireBounds( MeshBounds( merged ), want, 1e-4f, "assembled bounds" );
			Require( f.LongBody.Mesh.Positions.SequenceEqual( before ), "source mesh positions must be unchanged" );
		} );

		Check( "assembled OBJ: three o objects in order, bounds in inches; mtl written", () =>
		{
			var f = new ExportFixture();
			var dir = TempDir();
			try
			{
				var result = AssemblyExportService.ExportAssembledObj( f.Doc, f.Studio, f.Resolver, AssemblyExportPose.Current, dir, "assembly-export" );
				Require( result.Success, "export must succeed: " + result.ErrorMessage );
				Require( result.Files.Count == 2 && result.Files[0].Contains( "_assembly" ) && result.Files[0].EndsWith( ".obj", StringComparison.Ordinal ),
					"expected <base>_assembly.obj + .mtl, got: " + string.Join( ", ", result.Files ) );
				Require( File.Exists( result.Files[0] ) && File.Exists( result.Files[1] ), "obj + mtl must exist on disk" );
				var text = File.ReadAllText( result.Files[0] );
				Require( text.Contains( "mtllib" ), "assembled OBJ must reference its .mtl" );
				Require( File.ReadAllText( result.Files[1] ).Contains( "newmtl" ), "mtl must define a material" );
				var pieces = ObjReader.ReadPieces( text );
				Require( pieces.Select( p => p.Name ).SequenceEqual( new[] { "Bracket", "Bracket 2", "Mirror" } ),
					"three o objects in order, got: " + string.Join( ", ", pieces.Select( p => p.Name ) ) );
				RequireBounds( MeshBounds( pieces[0].Mesh ), ExpectedBounds( f.LongHalf, f.XfA ), 0.02f, "assembled OBJ: Bracket bounds in inches" );
				RequireBounds( MeshBounds( pieces[1].Mesh ), ExpectedBounds( f.LongHalf, f.XfB ), 0.02f, "assembled OBJ: rotated Bracket bounds in inches" );
				RequireBounds( MeshBounds( pieces[2].Mesh ), ExpectedBounds( f.LongHalf, f.XfD ), 0.02f, "assembled OBJ: Mirror bounds in inches" );
			}
			finally { DeleteDir( dir ); }
		} );

		Check( "individual OBJs: one file per part, winding outward; hidden + external have no file", () =>
		{
			var f = new ExportFixture();
			var dir = TempDir();
			try
			{
				var result = AssemblyExportService.ExportPartObjs( f.Doc, f.Studio, f.Resolver, AssemblyExportPose.Current, dir, "assembly-export" );
				Require( result.Success, "export must succeed: " + result.ErrorMessage );
				var partsDir = Path.Combine( dir, "assembly-export_parts" );
				Require( Directory.Exists( partsDir ), "parts folder must exist" );
				foreach ( var (stem, xf) in new[] { ("Bracket", f.XfA), ("Bracket_2", f.XfB), ("Mirror", f.XfD) } )
				{
					var file = Path.Combine( partsDir, stem + ".obj" );
					Require( File.Exists( file ), $"individual OBJ: {stem}.obj must exist" );
					var mesh = ObjReader.Read( File.ReadAllText( file ) );
					RequireBounds( MeshBounds( mesh ), ExpectedBounds( f.LongHalf, xf ), 0.02f, $"individual OBJ: {stem} bounds in inches" );
					Near( SignedVolume( mesh ), 4096f, 1f, $"individual OBJ: {stem} volume must be +4096 in^3 (winding outward)" );
				}
				Require( !File.Exists( Path.Combine( partsDir, "Cube.obj" ) ) && !File.Exists( Path.Combine( partsDir, "Ghost.obj" ) ),
					"hidden + external parts must have no file" );
			}
			finally { DeleteDir( dir ); }
		} );

		Check( "assembled bounds equal the union of the per-part exports", () =>
		{
			var f = new ExportFixture();
			var dir = TempDir();
			try
			{
				var asm = AssemblyExportService.ExportAssembledObj( f.Doc, f.Studio, f.Resolver, AssemblyExportPose.Current, dir, "assembly-export" );
				var parts = AssemblyExportService.ExportPartObjs( f.Doc, f.Studio, f.Resolver, AssemblyExportPose.Current, dir, "assembly-export" );
				Require( asm.Success && parts.Success, "both exports must succeed" );
				var asmPieces = ObjReader.ReadPieces( File.ReadAllText( asm.Files[0] ) );
				var partsDir = Path.Combine( dir, "assembly-export_parts" );
				(Vec3 Min, Vec3 Max)? union = null;
				foreach ( var stem in new[] { "Bracket", "Bracket_2", "Mirror" } )
				{
					var b = MeshBounds( ObjReader.Read( File.ReadAllText( Path.Combine( partsDir, stem + ".obj" ) ) ) );
					union = union == null ? b : Union( union.Value, b );
				}
				Require( union != null, "per-part union must exist" );
				RequireBounds( MeshBounds( asmPieces.SelectMany( p => p.Mesh.Positions ).Aggregate(
					new PolyMesh(), ( m, p ) => { m.Positions.Add( p ); return m; } ) ), union.Value, 0.02f,
					"assembled bounds must equal the union of per-part bounds" );
			}
			finally { DeleteDir( dir ); }
		} );

		Check( "assembled GLB: one node per part, no animation, positions in metres Y-up (independent re-import)", () =>
		{
			var f = new ExportFixture();
			var dir = TempDir();
			try
			{
				var result = AssemblyExportService.ExportAssembledGlb( f.Doc, f.Studio, f.Resolver, AssemblyExportPose.Current, Path.Combine( dir, "assembly-export.glb" ) );
				Require( result.Success, "export must succeed: " + result.ErrorMessage );
				Require( File.Exists( result.Files[0] ), "glb must exist on disk" );
				var info = GlbInfo( File.ReadAllBytes( result.Files[0] ) );
				Require( info.Nodes.SequenceEqual( new[] { "Bracket", "Bracket 2", "Mirror" } ),
					"one node per part, got: " + string.Join( ", ", info.Nodes ) );
				Require( info.Meshes.SequenceEqual( new[] { "Bracket", "Bracket 2", "Mirror" } ),
					"one mesh per part, got: " + string.Join( ", ", info.Meshes ) );
				Require( !info.HasAnimations, "assembled GLB must carry no animation (static export)" );
				Require( info.Materials.Contains( "steel" ), "GLB materials must name the part material, got: " + string.Join( ", ", info.Materials ) );
				RequireBounds( info.PosBounds["Bracket"], ExpectedBounds( f.LongHalf, f.XfA, p => G( p ) * 0.0254f ), 0.001f, "assembled GLB: Bracket positions in metres, Y up" );
				RequireBounds( info.PosBounds["Bracket 2"], ExpectedBounds( f.LongHalf, f.XfB, p => G( p ) * 0.0254f ), 0.001f, "assembled GLB: rotated Bracket positions in metres, Y up" );
				RequireBounds( info.PosBounds["Mirror"], ExpectedBounds( f.LongHalf, f.XfD, p => G( p ) * 0.0254f ), 0.001f, "assembled GLB: Mirror positions in metres, Y up" );
			}
			finally { DeleteDir( dir ); }
		} );

		Check( "dangling joint: preflight blocks with the missing occurrence; nothing is written", () =>
		{
			var f = new ExportFixture();
			var dir = TempDir();
			try
			{
				var badJoint = new AssemblyJoint
				{
					Id = AssemblyDocument.NewId(), Kind = "fixed", OccurrenceA = f.OccA, OccurrenceB = AssemblyDocument.NewId(),
					FrameA = AssemblyTransform.FromXform( Xform.Identity ), FrameB = AssemblyTransform.FromXform( Xform.Identity ),
				};
				try
				{
					f.Doc.Joints.Add( badJoint );
					var analysis = f.Analyze();
					Require( analysis.Errors.Any( e => e.Contains( "does not exist" ) ),
						"preflight must name the missing occurrence: " + string.Join( "; ", analysis.Errors ) );
					var result = AssemblyExportService.ExportAssembledObj( f.Doc, f.Studio, f.Resolver, AssemblyExportPose.Current, dir, "assembly-export" );
					Require( !result.Success, "export must fail while a joint dangles" );
					Require( result.Files.Count == 0 && !File.Exists( Path.Combine( dir, "assembly-export_assembly.obj" ) ),
						"a blocked export must write no file" );
				}
				finally { f.Doc.Joints.Remove( badJoint ); }
			}
			finally { DeleteDir( dir ); }
		} );

		Check( "rest vs current: hinge at 45 degrees exports posed now, at 0 at rest; live doc untouched", () =>
		{
			var (doc, hinge, a, b, studio, _) = HingeFixture();
			var dir = TempDir();
			try
			{
				var session = new AssemblyMotionSession( doc );
				Require( session.Begin( hinge ), "session must begin: " + session.Problem );
				session.SetValue( MathF.PI / 4 );
				Require( session.ApplyPreview(), "preview must apply: " + session.Problem );
				var jsonBefore = doc.ToJson();
				var current = AssemblyExportService.ExportAssembledObj( doc, studio, StudioResolverFor( studio ), AssemblyExportPose.Current, dir, "hinge-now" );
				var rest = AssemblyExportService.ExportAssembledObj( doc, studio, StudioResolverFor( studio ), AssemblyExportPose.Rest, dir, "hinge-rest" );
				Require( current.Success && rest.Success, $"both exports must succeed: {current.ErrorMessage} / {rest.ErrorMessage}" );
				Require( doc.ToJson() == jsonBefore, "a rest export must not touch the live document" );
				var nowPieces = ObjReader.ReadPieces( File.ReadAllText( current.Files[0] ) );
				var restPieces = ObjReader.ReadPieces( File.ReadAllText( rest.Files[0] ) );
				Require( nowPieces.Select( p => p.Name ).SequenceEqual( new[] { "Base", "Arm" } ), "current export must hold Base + Arm" );
				var half = new Vec3( 8, 4, 16 );
				var xf45 = Xform.Rotate( new Vec3( 0, 0, 1 ), MathF.PI / 4 ) * Xform.Translate( new Vec3( 5, 0, 0 ) );
				RequireBounds( MeshBounds( nowPieces[1].Mesh ), ExpectedBounds( half, xf45 ), 0.02f, "current export: Arm at 45 degrees" );
				RequireBounds( MeshBounds( restPieces[1].Mesh ), ExpectedBounds( half, Xform.Translate( new Vec3( 5, 0, 0 ) ) ), 0.02f, "rest export: Arm back at 0 degrees" );
			}
			finally { DeleteDir( dir ); }
		} );

		Check( "rest vs current: slider at 2.5 in exports posed now, at 0 at rest", () =>
		{
			var (doc, slider, _, _, studio, _) = SliderFixture();
			var dir = TempDir();
			try
			{
				var session = new AssemblyMotionSession( doc );
				Require( session.Begin( slider ), "session must begin: " + session.Problem );
				session.SetValue( 2.5f );
				Require( session.ApplyPreview(), "preview must apply: " + session.Problem );
				var current = AssemblyExportService.ExportAssembledObj( doc, studio, StudioResolverFor( studio ), AssemblyExportPose.Current, dir, "slider-now" );
				var rest = AssemblyExportService.ExportAssembledObj( doc, studio, StudioResolverFor( studio ), AssemblyExportPose.Rest, dir, "slider-rest" );
				Require( current.Success && rest.Success, $"both exports must succeed: {current.ErrorMessage} / {rest.ErrorMessage}" );
				var nowPieces = ObjReader.ReadPieces( File.ReadAllText( current.Files[0] ) );
				var restPieces = ObjReader.ReadPieces( File.ReadAllText( rest.Files[0] ) );
				var half = new Vec3( 4, 4, 4 );
				RequireBounds( MeshBounds( nowPieces[1].Mesh ), ExpectedBounds( half, Xform.Translate( new Vec3( 0, 0, 2.5f ) ) ), 0.02f, "current export: Carriage at 2.5 in" );
				RequireBounds( MeshBounds( restPieces[1].Mesh ), ExpectedBounds( half, Xform.Identity ), 0.02f, "rest export: Carriage back at 0 in" );
			}
			finally { DeleteDir( dir ); }
		} );

		Check( "rest: a grounded hinge keeps its angle; the rest export does not fail", () =>
		{
			var (doc, hinge, _, b, studio, _) = HingeFixture();
			var dir = TempDir();
			try
			{
				var session = new AssemblyMotionSession( doc );
				Require( session.Begin( hinge ), "session must begin: " + session.Problem );
				session.SetValue( MathF.PI / 4 );
				Require( session.ApplyPreview(), "preview must apply: " + session.Problem );
				AssemblyGrounding.SetGrounded( doc, b, true );
				var rest = AssemblyExportService.ExportAssembledObj( doc, studio, StudioResolverFor( studio ), AssemblyExportPose.Rest, dir, "hinge-grounded" );
				Require( rest.Success, "rest export must succeed with a grounded hinge: " + rest.ErrorMessage );
				var restPieces = ObjReader.ReadPieces( File.ReadAllText( rest.Files[0] ) );
				var half = new Vec3( 8, 4, 16 );
				var xf45 = Xform.Rotate( new Vec3( 0, 0, 1 ), MathF.PI / 4 ) * Xform.Translate( new Vec3( 5, 0, 0 ) );
				RequireBounds( MeshBounds( restPieces[1].Mesh ), ExpectedBounds( half, xf45 ), 0.02f, "grounded Arm stays at 45 degrees in the rest export" );
			}
			finally { DeleteDir( dir ); }
		} );

		Check( "export leaves the document text, undo state and source meshes byte-identical", () =>
		{
			var f = new ExportFixture();
			var dir = TempDir();
			try
			{
				var docTextBefore = f.Doc.ToJson();
				var longBefore = f.LongBody.Mesh.Positions.ToArray();
				var cubeBefore = f.CubeBody.Mesh.Positions.ToArray();
				var history = new EditHistory();
				Require( AssemblyExportService.ExportAssembledObj( f.Doc, f.Studio, f.Resolver, AssemblyExportPose.Current, dir, "a" ).Success, "assembled OBJ must succeed" );
				Require( AssemblyExportService.ExportPartObjs( f.Doc, f.Studio, f.Resolver, AssemblyExportPose.Current, dir, "a" ).Success, "part OBJs must succeed" );
				Require( AssemblyExportService.ExportAssembledGlb( f.Doc, f.Studio, f.Resolver, AssemblyExportPose.Current, Path.Combine( dir, "a.glb" ) ).Success, "assembled GLB must succeed" );
				Require( f.Doc.ToJson() == docTextBefore, "document text must be unchanged by all exports" );
				Require( f.Doc.Occurrences.Count == 5 && f.Doc.Joints.Count == 3, "assembly records must be unchanged by all exports" );
				Require( f.LongBody.Mesh.Positions.SequenceEqual( longBefore ), "long source positions must be unchanged" );
				Require( f.CubeBody.Mesh.Positions.SequenceEqual( cubeBefore ), "cube source positions must be unchanged" );
				Require( !history.CanUndo, "export takes no undo step" );
			}
			finally { DeleteDir( dir ); }
		} );

		Check( "save/reopen then re-export agrees byte for byte", () =>
		{
			var f = new ExportFixture();
			var dir = TempDir();
			try
			{
				var first = AssemblyExportService.ExportAssembledObj( f.Doc, f.Studio, f.Resolver, AssemblyExportPose.Current, dir, "reopen" );
				Require( first.Success, "first export must succeed" );
				var firstText = File.ReadAllText( first.Files[0] );
				var reopened = AssemblyDocument.FromJson( f.Doc.ToJson() );
				var snapshot = new AssemblySnapshotService().Build( reopened, f.Resolver );
				var analysis = AssemblyExportService.Analyze( reopened, f.Studio, snapshot );
				Require( analysis.Errors.Count == 0, "reopened document must analyse clean: " + string.Join( "; ", analysis.Errors ) );
				var dir2 = Path.Combine( dir, "second" );
				var second = AssemblyExportService.ExportAssembledObj( reopened, f.Studio, f.Resolver, AssemblyExportPose.Current, dir2, "reopen" );
				Require( second.Success, "second export must succeed" );
				Require( File.ReadAllText( second.Files[0] ) == firstText, "re-export after save/reopen must agree byte for byte" );
			}
			finally { DeleteDir( dir ); }
		} );

		Check( "empty assembly: analysis errors, export refuses", () =>
		{
			var doc = new AssemblyDocument();
			var studio = new PartStudio();
			var analysis = AssemblyExportService.Analyze( doc, studio, new AssemblySnapshotService().Build( doc, AssemblyExportService.StudioResolver( studio ) ) );
			Require( analysis.Errors.Any( e => e.Contains( "No assembly part can be exported" ) ),
				"empty assembly must error: " + string.Join( "; ", analysis.Errors ) );
			var dir = TempDir();
			try
			{
				var result = AssemblyExportService.ExportAssembledObj( doc, studio, AssemblyExportService.StudioResolver( studio ), AssemblyExportPose.Current, dir, "empty" );
				Require( !result.Success && result.Files.Count == 0, "empty export must fail with no files" );
			}
			finally { DeleteDir( dir ); }
		} );

		Check( "missing body: definition points at a body that is not in the document", () =>
		{
			var f = new ExportFixture();
			f.Doc.Definitions.Add( new AssemblyPartDefinition { Id = AssemblyDocument.NewId(), Name = "Lost", SourceKind = "studio-body", SourceReference = "body-nope" } );
			var lostDef = f.Doc.Definitions[f.Doc.Definitions.Count - 1].Id;
			f.Doc.Occurrences.Add( new AssemblyOccurrence
			{
				Id = AssemblyDocument.NewId(), DefinitionId = lostDef, Name = "Lost",
				Transform = AssemblyTransform.FromXform( Xform.Identity ),
			} );
			var analysis = f.Analyze();
			Require( analysis.Errors.Any( e => e.Contains( "not in this document" ) ),
				"missing body must error: " + string.Join( "; ", analysis.Errors ) );
		} );

		Check( "reference body, garment body and mesh-less body are refused with their own errors", () =>
		{
			var f = new ExportFixture();
			var reference = new Body( "body-ref", "Ref", BoxMesh( 1, 1, 1 ) ) { IsReference = true };
			var garment = new Body( "body-garment", "Garment", BoxMesh( 1, 1, 1 ) ) { IsGarment = true };
			var empty = new Body( "body-empty", "Empty", new PolyMesh() );
			f.Studio.Bodies.AddRange( new[] { reference, garment, empty } );
			foreach ( var (body, name) in new[] { (reference, "Ref"), (garment, "Garment"), (empty, "Empty") } )
			{
				f.Doc.Definitions.Add( new AssemblyPartDefinition { Id = AssemblyDocument.NewId(), Name = name, SourceKind = "studio-body", SourceReference = body.Id } );
				var defId = f.Doc.Definitions[f.Doc.Definitions.Count - 1].Id;
				f.Doc.Occurrences.Add( new AssemblyOccurrence
				{
					Id = AssemblyDocument.NewId(), DefinitionId = defId, Name = name,
					Transform = AssemblyTransform.FromXform( Xform.Identity ),
				} );
			}
			var analysis = f.Analyze();
			Require( analysis.Errors.Any( e => e.Contains( "reference body" ) ), "reference body must error: " + string.Join( "; ", analysis.Errors ) );
			Require( analysis.Errors.Any( e => e.Contains( "garment" ) ), "garment must error: " + string.Join( "; ", analysis.Errors ) );
			Require( analysis.Errors.Any( e => e.Contains( "no mesh yet" ) ), "mesh-less body must error: " + string.Join( "; ", analysis.Errors ) );
		} );

		Check( "hidden-only assembly: nothing exports, hidden count reported", () =>
		{
			var f = new ExportFixture();
			foreach ( var o in f.Doc.Occurrences ) o.Visible = false;
			var analysis = f.Analyze();
			Require( analysis.Items.Count == 0, "no item may export when all are hidden" );
			Require( analysis.HiddenSkipped == 5, $"expected 5 hidden skipped, got {analysis.HiddenSkipped}" );
			Require( analysis.Errors.Any( e => e.Contains( "No assembly part can be exported" ) ), "hidden-only assembly must error" );
		} );

		Check( "SanitizeFileStem: punctuation, empty, long and underscore-only names", () =>
		{
			Require( AssemblyExportService.SanitizeFileStem( "Hello World!" ) == "Hello_World", "spaces and punctuation become _" );
			Require( AssemblyExportService.SanitizeFileStem( "" ) == "body", "empty becomes body" );
			Require( AssemblyExportService.SanitizeFileStem( "___" ) == "body", "underscores only becomes body" );
			Require( AssemblyExportService.SanitizeFileStem( "a/b" ) == "a_b", "slash becomes _" );
			Require( AssemblyExportService.SanitizeFileStem( new string( 'x', 50 ) ).Length == 40, "stems are capped at 40 characters" );
			Require( AssemblyExportService.SanitizeFileStem( "  spaced  " ) == "spaced", "surrounding space is trimmed" );
		} );

		Check( "DedupeDisplayNames: repeats gain ' 2', ' 3'; blanks become part", () =>
		{
			Require( AssemblyExportService.DedupeDisplayNames( new[] { "Bracket", "Bracket", "Mirror", "Bracket" } )
				.SequenceEqual( new[] { "Bracket", "Bracket 2", "Mirror", "Bracket 3" } ), "repeat dedup must use ' 2', ' 3'" );
			Require( AssemblyExportService.DedupeDisplayNames( new[] { "", "Bracket 2", "Bracket" } )
				.SequenceEqual( new[] { "part", "Bracket 2", "Bracket" } ), "blank becomes part; an existing 'Bracket 2' is kept" );
		} );

		Check( "BuildPartMesh returns a clone: mutating it leaves the snapshot alone", () =>
		{
			var f = new ExportFixture();
			var snapshot = f.Snapshot();
			var analysis = f.Analyze();
			var item = analysis.Items[0];
			var mesh = AssemblyExportService.BuildPartMesh( snapshot, item );
			var snapBefore = snapshot.Occurrences.Single( s => s.OccurrenceId == item.Occurrence.Id ).Mesh.Positions.ToArray();
			mesh.Positions[0] = new Vec3( 999f, 999f, 999f );
			var snapAfter = snapshot.Occurrences.Single( s => s.OccurrenceId == item.Occurrence.Id ).Mesh.Positions;
			Require( snapAfter.SequenceEqual( snapBefore ), "the snapshot mesh must be untouched by edits to the built part mesh" );
		} );

		Check( "CapturePose: a deep copy; editing the copy leaves the original alone", () =>
		{
			var f = new ExportFixture();
			var copy = AssemblyExportService.CapturePose( f.Doc );
			Require( copy.ToJson() == f.Doc.ToJson(), "a captured pose must round-trip identical" );
			copy.Occurrences[0].Transform = AssemblyTransform.FromXform( Xform.Translate( new Vec3( 100, 0, 0 ) ) );
			Require( f.Doc.Occurrences[0].Transform.ToXform().Origin.x == 0f, "editing the copy must not move the original" );
		} );

		Check( "PaintForOccurrence: finds the source body's paint canvas, null when absent", () =>
		{
			var f = new ExportFixture();
			var analysis = f.Analyze();
			Require( AssemblyExportService.PaintForOccurrence( f.Studio, analysis, f.OccA ) == null, "unpainted body must report no paint" );
			f.LongBody.Mesh.Paint = new PaintCanvas( 4, 4 );
			Require( ReferenceEquals( AssemblyExportService.PaintForOccurrence( f.Studio, analysis, f.OccA ), f.LongBody.Mesh.Paint ),
				"painted body must report its own canvas" );
			Require( AssemblyExportService.PaintForOccurrence( f.Studio, analysis, AssemblyDocument.NewId() ) == null, "unknown occurrence must report no paint" );
		} );

		Check( "StudioResolver: studio bodies resolve, external kinds and missing bodies do not", () =>
		{
			var f = new ExportFixture();
			var resolver = AssemblyExportService.StudioResolver( f.Studio );
			var longDef = f.Doc.Definitions.Single( d => d.Name == "Long" );
			var ghostDef = f.Doc.Definitions.Single( d => d.Name == "Ghost" );
			var resolved = resolver( longDef );
			Require( resolved != null && ReferenceEquals( resolved.Value.Mesh, f.LongBody.Mesh ), "studio body must resolve to its mesh" );
			Require( resolved.Value.MaterialName == "material_0", $"default slot name must be material_0, got {resolved.Value.MaterialName}" );
			Require( resolver( ghostDef ) == null, "external kinds must not resolve" );
			Require( resolver( null ) == null, "null definition must not resolve" );
			Require( resolver( new AssemblyPartDefinition { Id = "x", SourceKind = "studio-body", SourceReference = "body-nope" } ) == null,
				"missing body must not resolve" );
		} );
	}

		// Centred box with the AS04 outward winding; signed volume is
		// +(2hx)(2hy)(2hz).
		static PolyMesh BoxMesh( float hx, float hy, float hz )
		{
			var mesh = new PolyMesh();
			mesh.Positions.AddRange( new[]
			{
				new Vec3( -hx, -hy, -hz ), new Vec3( hx, -hy, -hz ), new Vec3( hx, hy, -hz ), new Vec3( -hx, hy, -hz ),
				new Vec3( -hx, -hy, hz ), new Vec3( hx, -hy, hz ), new Vec3( hx, hy, hz ), new Vec3( -hx, hy, hz ),
			} );
			mesh.AddFace( new[] { 0, 2, 1 } ); mesh.AddFace( new[] { 0, 3, 2 } ); // bottom -Z
			mesh.AddFace( new[] { 4, 5, 6 } ); mesh.AddFace( new[] { 4, 6, 7 } ); // top +Z
			mesh.AddFace( new[] { 0, 1, 5 } ); mesh.AddFace( new[] { 0, 5, 4 } ); // front -Y
			mesh.AddFace( new[] { 3, 7, 6 } ); mesh.AddFace( new[] { 3, 6, 2 } ); // back +Y
			mesh.AddFace( new[] { 0, 4, 7 } ); mesh.AddFace( new[] { 0, 7, 3 } ); // left -X
			mesh.AddFace( new[] { 1, 2, 6 } ); mesh.AddFace( new[] { 1, 6, 5 } ); // right +X
			return mesh;
		}

		// Fixture: two centred boxes, 16x8x32 long and 8x8x8 cube, in a
		// studio; repeated definition, rotation, mirror, hidden, external.
		sealed class ExportFixture
		{
			public PartStudio Studio = new PartStudio();
			public Body LongBody;
			public Body CubeBody;
			public AssemblyDocument Doc = new AssemblyDocument();
			public string OccA, OccB, OccC, OccD, OccE;
			public Xform XfA = Xform.Identity;
			public Xform XfB = Xform.Rotate( new Vec3( 0, 0, 1 ), MathF.PI / 2 ) * Xform.Translate( new Vec3( 10, 0, 0 ) );
			public Xform XfD = Xform.Mirror( new Vec3( 0, 0, 0 ), new Vec3( 1, 0, 0 ) ) * Xform.Translate( new Vec3( 0, 20, 0 ) );
			public Vec3 LongHalf = new Vec3( 8, 4, 16 );
			public Vec3 CubeHalf = new Vec3( 4, 4, 4 );

			public ExportFixture()
			{
				LongBody = new Body( "body-long", "Long", BoxMesh( 8, 4, 16 ) );
				CubeBody = new Body( "body-cube", "Cube", BoxMesh( 4, 4, 4 ) );
				Studio.Bodies.Add( LongBody );
				Studio.Bodies.Add( CubeBody );
				var defLong = new AssemblyPartDefinition { Id = AssemblyDocument.NewId(), Name = "Long", SourceKind = "studio-body", SourceReference = LongBody.Id };
				var defCube = new AssemblyPartDefinition { Id = AssemblyDocument.NewId(), Name = "Cube", SourceKind = "studio-body", SourceReference = CubeBody.Id };
				var defExt = new AssemblyPartDefinition { Id = AssemblyDocument.NewId(), Name = "Ghost", SourceKind = "effigy-file", SourceReference = "parts/ghost.effigy#body-1" };
				Doc.Definitions.AddRange( new[] { defLong, defCube, defExt } );
				AssemblyOccurrence Occ( string name, string defId, Xform t, bool visible = true ) => new AssemblyOccurrence
				{
					Id = AssemblyDocument.NewId(), DefinitionId = defId, Name = name, Visible = visible,
					Transform = AssemblyTransform.FromXform( t ),
				};
				var a = Occ( "Bracket", defLong.Id, XfA );
				var b = Occ( "Bracket", defLong.Id, XfB );
				var c = Occ( "Cube", defCube.Id, Xform.Translate( new Vec3( 0, 30, 0 ) ), visible: false );
				var d = Occ( "Mirror", defLong.Id, XfD );
				var e = Occ( "Ghost", defExt.Id, Xform.Identity );
				OccA = a.Id; OccB = b.Id; OccC = c.Id; OccD = d.Id; OccE = e.Id;
				Doc.Occurrences.AddRange( new[] { a, b, c, d, e } );
				Doc.Joints.Add( new AssemblyJoint
				{
					Id = AssemblyDocument.NewId(), Kind = "fixed", OccurrenceA = a.Id, OccurrenceB = b.Id,
					FrameA = AssemblyTransform.FromXform( Xform.Identity ), FrameB = AssemblyTransform.FromXform( Xform.Identity ),
				} );
				Doc.Joints.Add( new AssemblyJoint
				{
					Id = AssemblyDocument.NewId(), Kind = "hinge", OccurrenceA = a.Id, OccurrenceB = b.Id,
					FrameA = AssemblyTransform.FromXform( Xform.Identity ), FrameB = AssemblyTransform.FromXform( Xform.Identity ),
					Minimum = -MathF.PI, Maximum = MathF.PI,
				} );
				Doc.Joints.Add( new AssemblyJoint
				{
					Id = AssemblyDocument.NewId(), Kind = "ball", OccurrenceA = a.Id, OccurrenceB = b.Id,
					FrameA = AssemblyTransform.FromXform( Xform.Identity ), FrameB = AssemblyTransform.FromXform( Xform.Identity ),
				} );
			}

			public (PolyMesh Mesh, int Revision, string MaterialName)? Resolve( AssemblyPartDefinition def )
			{
				if ( def == null || def.SourceKind != "studio-body" ) return null;
				var body = Studio.Bodies.FirstOrDefault( b => b != null && b.Id == def.SourceReference );
				if ( body?.Mesh == null ) return null;
				return (body.Mesh, 0, body.Id == LongBody.Id ? "steel" : "brass");
			}

			public AssemblySourceResolver Resolver => Resolve;
			public AssemblySnapshot Snapshot() => new AssemblySnapshotService().Build( Doc, Resolver );
			public AssemblyAnalysis Analyze() => AssemblyExportService.Analyze( Doc, Studio, Snapshot() );
		}

}
