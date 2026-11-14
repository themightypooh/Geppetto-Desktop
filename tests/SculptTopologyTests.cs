using System;
using System.Collections.Generic;
using Effigy;
using Marionette.Desktop;

/// <summary>
/// Topology-aware sculpt symmetry, mask blur/sharpen, multires level 6 and delta handling
/// (port-topology-aware-sculpt-symmetry-and-durable-mask-improvements). Mirrors Mobile's
/// ModelChecks sculpt section, against the Desktop kernel API directly: a symmetric box
/// fixture, no UI, no app wrapper.
/// </summary>
internal static class SculptTopologyTests
{
    public static void Register( TestRegistry registry, TestSettings settings )
    {
        void Check( string name, Action test ) => registry.Add( "sculpt-topology", name, test );
        void Require( bool condition, string message = "assertion failed" )
        {
            if ( !condition ) throw new Exception( message );
        }

        // A 2x2x2 box centred on the origin, outward winding, symmetric across X, Y and Z.
        PolyMesh Box()
        {
            var mesh = new PolyMesh();
            mesh.Positions.AddRange( new[]
            {
                new Vec3( -1, -1, -1 ), new Vec3( 1, -1, -1 ), new Vec3( 1, 1, -1 ), new Vec3( -1, 1, -1 ),
                new Vec3( -1, -1, 1 ), new Vec3( 1, -1, 1 ), new Vec3( 1, 1, 1 ), new Vec3( -1, 1, 1 ),
            } );
            mesh.Faces.AddRange( new[]
            {
                new Face( new[] { 0, 3, 2, 1 } ), new Face( new[] { 4, 5, 6, 7 } ),
                new Face( new[] { 0, 1, 5, 4 } ), new Face( new[] { 3, 7, 6, 2 } ),
                new Face( new[] { 0, 4, 7, 3 } ), new Face( new[] { 1, 2, 6, 5 } ),
            } );
            return mesh;
        }

        SculptSession SessionOn( PolyMesh cage, int levels = 0 )
        {
            var sculpt = new MultiresSculpt( cage );
            for ( var i = 0; i < levels; i++ )
                sculpt.AddLevel();
            return new SculptSession( sculpt );
        }

        int Partial( SculptMask mask )
        {
            var n = 0;
            foreach ( var v in mask.Values )
                if ( v > 0.02f && v < 0.98f )
                    n++;
            return n;
        }

        Check( "blur mask softens a hard edge", () =>
        {
            var session = SessionOn( Box() );
            var mask = session.MaskFor( session.Level );
            var mesh = session.Sculpt.Evaluate( session.Level );
            for ( var v = 0; v < mask.Count; v++ )
                mask[v] = mesh.Positions[v].x < 0f ? 0f : 1f;
            var hard = Partial( mask );
            var share = mask.ProtectedFraction;
            Require( hard == 0, $"hard mask should have no partial vertices, got {hard}" );
            session.BlurMask();
            Require( Partial( mask ) > hard, $"blur should soften the edge, partial stayed {hard}" );
            Require( MathF.Abs( mask.ProtectedFraction - share ) < 0.1f,
                $"blur should preserve the protected share, {share:0.###} -> {mask.ProtectedFraction:0.###}" );
        } );

        Check( "sharpen mask hardens it again", () =>
        {
            var session = SessionOn( Box() );
            var mask = session.MaskFor( session.Level );
            var mesh = session.Sculpt.Evaluate( session.Level );
            for ( var v = 0; v < mask.Count; v++ )
                mask[v] = mesh.Positions[v].x < 0f ? 0f : 1f;
            session.BlurMask();
            var soft = Partial( mask );
            Require( soft > 0, "blur should have softened the edge first" );
            session.SharpenMask();
            session.SharpenMask();
            Require( Partial( mask ) < soft, $"sharpen should harden the edge, partial {soft} -> {Partial( mask )}" );
        } );

        Check( "blur and sharpen leave an empty mask alone", () =>
        {
            var session = SessionOn( Box() );
            session.BlurMask();
            session.SharpenMask();
            Require( session.MaskFor( session.Level ).ProtectedFraction == 0f,
                $"empty mask share should stay 0, got {session.MaskFor( session.Level ).ProtectedFraction}" );
        } );

        Check( "topology mirror builds a twin map", () =>
        {
            var session = SessionOn( Box(), levels: 2 );
            session.MirrorAxes = SymmetryAxes.X;
            session.TopologyMirror = true;
            var map = session.TopologyMap;
            Require( map is not null, "topology map should exist with one mirror plane" );
            Require( TopologySymmetry.Coverage( map ) > 0.95f,
                $"twin coverage should exceed 95%, got {TopologySymmetry.Coverage( map ):P1}" );
        } );

        Check( "topology mirror finds the twins on a lopsided mesh", () =>
        {
            var session = SessionOn( Box(), levels: 2 );
            session.MirrorAxes = SymmetryAxes.X;
            session.TopologyMirror = true;
            var map = session.TopologyMap;
            Require( map is not null, "topology map should exist" );
            var rest = session.Sculpt.Rest( session.Level );
            var lop = rest.Clone();
            for ( var i = 0; i < lop.VertexCount; i++ )
                if ( lop.Positions[i].x > 1e-3f )
                    lop.Positions[i] += new Vec3( 0.37f, 0.11f, 0f );
            var lopMap = TopologySymmetry.Build( lop, MirrorAxis.X );
            var agree = 0;
            var paired = 0;
            for ( var i = 0; i < map.Length; i++ )
                if ( map[i] >= 0 )
                {
                    paired++;
                    if ( lopMap[i] == map[i] )
                        agree++;
                }
            Require( paired > 0 && agree >= paired * 0.95f,
                $"lopsided map should agree with the symmetric one, {agree}/{paired} agree" );
        } );

        Check( "topology-mirrored stroke moves each twin by the mirrored amount", () =>
        {
            var session = SessionOn( Box(), levels: 2 );
            session.MirrorAxes = SymmetryAxes.X;
            session.TopologyMirror = true;
            session.Brush = BrushKind.Draw;
            session.Radius = 1.0f;
            session.Strength = 0.5f;
            var map = session.TopologyMap;
            Require( map is not null, "topology map should exist" );
            var before = session.Sculpt.Evaluate( session.Level ).Clone();
            Require( session.BeginStroke( new Vec3( 5, 0, 0 ), new Vec3( -1, 0, 0 ) ),
                "ray from +x should hit the box" );
            var edit = session.EndStroke();
            Require( edit is not null, "topology-mirrored dab should move something" );
            var after = session.Sculpt.Evaluate( session.Level );
            var movedOwn = 0;
            var mirroredOk = 0;
            for ( var v = 0; v < before.VertexCount; v++ )
            {
                var dv = after.Positions[v] - before.Positions[v];
                if ( before.Positions[v].x <= 0f || dv.LengthSquared < 1e-8f || map[v] < 0 || map[v] == v )
                    continue;
                movedOwn++;
                var t = map[v];
                var dt = after.Positions[t] - before.Positions[t];
                if ( ( dt - Brush.Mirror( dv, MirrorAxis.X ) ).Length <= 0.05f * dv.Length + 1e-4f )
                    mirroredOk++;
            }
            Require( movedOwn > 0 && mirroredOk >= movedOwn * 0.9f,
                $"twins should mirror the stroke, {mirroredOk}/{movedOwn} mirrored" );
            Require( session.Undo(), "topology-mirrored stroke should undo" );
            var undone = session.Sculpt.Evaluate( session.Level );
            for ( var v = 0; v < before.VertexCount; v++ )
                Require( ( undone.Positions[v] - before.Positions[v] ).Length < 1e-5f,
                    $"undo should restore vertex {v}" );
        } );

        Check( "multires level cap is six and levels build to it", () =>
        {
            Require( SculptFeature.MaxMultiresLevels == 6,
                $"MaxMultiresLevels should be 6, got {SculptFeature.MaxMultiresLevels}" );
            var feature = new SculptFeature();
            Require( feature.Levels.Max == 6, $"Levels param should cap at 6, got max {feature.Levels.Max}" );
            feature.Levels.Value = 99;
            Require( feature.Levels.Clamped == 6, $"Levels should clamp to 6, got {feature.Levels.Clamped}" );
            var sculpt = new MultiresSculpt( Box() );
            for ( var i = 0; i < 6; i++ )
                sculpt.AddLevel();
            Require( sculpt.TopLevel == 6, $"top level should be 6, got {sculpt.TopLevel}" );
            var cost5 = sculpt.Cost( 5 );
            var cost6 = sculpt.Cost( 6 );
            Require( cost6.Faces == 4 * cost5.Faces,
                $"level 6 should quadruple faces over level 5, {cost5.Faces} -> {cost6.Faces}" );
            Require( cost6.Vertices > cost5.Vertices, "level 6 should add vertices over level 5" );
            long deltaBytes = 0;
            for ( var level = 0; level <= 6; level++ )
                deltaBytes += (long)sculpt.Rest( level ).VertexCount * 12;
            Require( deltaBytes < 64L * 1024 * 1024,
                $"level-6 delta footprint should stay under 64 MB, got {deltaBytes} bytes" );
        } );

        Check( "rebase with an unmoved cage is a no-op and a moved cage carries the deltas", () =>
        {
            var cage = Box();
            var sculpt = new MultiresSculpt( cage );
            sculpt.ApplyEdit( 0, new Dictionary<int, Vec3> { [6] = new Vec3( 0, 0, 0.5f ) } );
            var before = sculpt.Evaluate( 0 ).Positions[6];
            var baseBefore = cage.Positions[6];
            var revision = sculpt.Revision;
            sculpt.SetCage( cage.Clone() );
            Require( sculpt.Revision == revision,
                $"SetCage with identical positions should not bump Revision, {revision} -> {sculpt.Revision}" );
            var moved = cage.Clone();
            for ( var v = 0; v < moved.VertexCount; v++ )
                moved.Positions[v] += new Vec3( 0.2f, 0f, 0f );
            sculpt.SetCage( moved );
            Require( sculpt.Revision == revision + 1,
                $"SetCage with a moved cage should bump Revision once, {revision} -> {sculpt.Revision}" );
            var after = sculpt.Evaluate( 0 ).Positions[6];
            var baseAfter = moved.Positions[6];
            Require( ( ( after - baseAfter ) - ( before - baseBefore ) ).Length < 1e-4f,
                "the stored delta should ride the rebase unchanged" );
        } );

        Check( "sculpt blob round-trips multires edits", () =>
        {
            var cage = Box();
            var sculpt = new MultiresSculpt( cage );
            sculpt.AddLevel();
            sculpt.ApplyEdit( 1, new Dictionary<int, Vec3>
            {
                [0] = new Vec3( 0.1f, 0f, 0f ),
                [5] = new Vec3( 0f, 0.2f, 0f ),
                [10] = new Vec3( 0f, 0f, 0.3f ),
            } );
            var written = sculpt.Evaluate( 1 ).Clone();
            var bytes = SculptBlob.Write( sculpt );
            var restored = SculptBlob.Read( bytes, cage );
            Require( restored is not null, "blob should read back" );
            var read = restored.Evaluate( 1 );
            Require( read.VertexCount == written.VertexCount,
                $"round-tripped level should keep its vertices, {written.VertexCount} -> {read.VertexCount}" );
            for ( var v = 0; v < written.VertexCount; v++ )
                Require( ( read.Positions[v] - written.Positions[v] ).Length < 1e-5f,
                    $"round-tripped level should keep vertex {v}" );
        } );
    }
}
