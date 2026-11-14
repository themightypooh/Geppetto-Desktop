using System;
using System.Collections.Generic;
using Effigy;
using Marionette.Desktop;

/// <summary>
/// Non-destructive sculpt layers, morph checkpoints and polygroups
/// (godlike-sculpt-layers-morph-targets-polygroups): layer data model, ops, bake,
/// delta storage, blob round-trip, morph export and group isolate/hide/mask/pivot.
/// Against the Desktop kernel API directly: a box fixture, no UI, no app wrapper.
/// </summary>
internal static class SculptLayerTests
{
    public static void Register( TestRegistry registry, TestSettings settings )
    {
        void Check( string name, Action test ) => registry.Add( "sculpt-layers", name, test );
        void Require( bool condition, string message = "assertion failed" )
        {
            if ( !condition ) throw new Exception( message );
        }

        void Near( Vec3 got, Vec3 want, float eps, string what )
        {
            Require( MathF.Abs( got.x - want.x ) <= eps && MathF.Abs( got.y - want.y ) <= eps && MathF.Abs( got.z - want.z ) <= eps,
                $"{what}: got ({got.x:0.#####}, {got.y:0.#####}, {got.z:0.#####}), want ({want.x:0.#####}, {want.y:0.#####}, {want.z:0.#####})" );
        }

        void NearLayer( SculptLayer got, SculptLayer want, float eps, string what )
        {
            Require( got.Count == want.Count, $"{what}: count {got.Count} != {want.Count}" );
            for ( var v = 0; v < got.Count; v++ )
                Near( got.Deltas[v], want.Deltas[v], eps, $"{what} vertex {v}" );
        }

        // A 2x2x2 box centred on the origin, outward winding, symmetric across X, Y and Z.
        // Same fixture as SculptTopologyTests: face 0 back, 1 front, 2 bottom, 3 top, 4 left, 5 right.
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

        MultiresSculpt SculptOn( PolyMesh cage, int levels = 0 )
        {
            var sculpt = new MultiresSculpt( cage );
            for ( var i = 0; i < levels; i++ )
                sculpt.AddLevel();
            return sculpt;
        }

        // A stack on level 0 with a zero base, one entry, deltas (x, 0, 0) on every vertex.
        SculptLayerStack StackWith( float x, out SculptLayerEntry entry, float intensity = 1f )
        {
            var sculpt = SculptOn( Box() );
            var stack = SculptLayerStack.Attach( sculpt, 0 );
            var deltas = new Vec3[8];
            for ( var v = 0; v < 8; v++ )
                deltas[v] = new Vec3( x, 0, 0 );
            entry = stack.Add( "Bump", new SculptLayer( deltas ) );
            entry.Intensity = intensity;
            return stack;
        }

        Check( "intensity 0 silences a layer, -1 inverts it", () =>
        {
            var stack = StackWith( 0.2f, out var entry );
            Near( stack.ComposeEntries().Deltas[0], new Vec3( 0.2f, 0, 0 ), 1e-6f, "full intensity" );
            stack.SetIntensity( 0, 0f );
            Near( stack.ComposeEntries().Deltas[0], Vec3.Zero, 1e-6f, "zero intensity" );
            stack.SetIntensity( 0, -1f );
            Near( stack.ComposeEntries().Deltas[0], new Vec3( -0.2f, 0, 0 ), 1e-6f, "inverted" );
            Require( entry.Intensity == -1f, "intensity clamps to -1..1" );
            stack.SetIntensity( 0, 5f );
            Require( entry.Intensity == 1f, $"intensity 5 should clamp to 1, got {entry.Intensity}" );
        } );

        Check( "per-layer mask gates vertices", () =>
        {
            var stack = StackWith( 0.2f, out _ );
            var mask = new float[8];
            mask[0] = 1f;
            stack.SetMask( 0, mask );
            var composed = stack.ComposeEntries();
            Near( composed.Deltas[0], new Vec3( 0.2f, 0, 0 ), 1e-6f, "masked-in vertex" );
            for ( var v = 1; v < 8; v++ )
                Near( composed.Deltas[v], Vec3.Zero, 1e-6f, $"masked-out vertex {v}" );
            // The mask is copied on set: the caller's array cannot move the layer afterwards.
            mask[0] = 0f;
            Near( stack.ComposeEntries().Deltas[0], new Vec3( 0.2f, 0, 0 ), 1e-6f, "mask copy" );
        } );

        Check( "blend modes combine on a non-zero base", () =>
        {
            var baseDeltas = new Vec3[8];
            for ( var v = 0; v < 8; v++ )
                baseDeltas[v] = new Vec3( 0.1f, 0, 0 );
            var stack = new SculptLayerStack( 0, 8, new SculptLayer( baseDeltas ) );

            var add = new Vec3[8];
            for ( var v = 0; v < 8; v++ )
                add[v] = new Vec3( 0.2f, 0, 0 );
            stack.Add( "Add", new SculptLayer( add ) );
            Near( stack.EffectiveDeltas().Deltas[0], new Vec3( 0.3f, 0, 0 ), 1e-6f, "add" );

            stack.SetBlend( 0, SculptLayerBlend.Subtract );
            Near( stack.EffectiveDeltas().Deltas[0], new Vec3( -0.1f, 0, 0 ), 1e-6f, "subtract" );

            stack.SetBlend( 0, SculptLayerBlend.Multiply );
            Near( stack.EffectiveDeltas().Deltas[0], new Vec3( 0.12f, 0, 0 ), 1e-6f, "multiply 0.1 * (1 + 0.2)" );

            stack.SetBlend( 0, SculptLayerBlend.Replace );
            Near( stack.EffectiveDeltas().Deltas[0], new Vec3( 0.2f, 0, 0 ), 1e-6f, "replace at full strength" );
            stack.SetIntensity( 0, 0.5f );
            Near( stack.EffectiveDeltas().Deltas[0], new Vec3( 0.15f, 0, 0 ), 1e-6f, "replace at half strength" );
        } );

        Check( "duplicate and branch are independent copies", () =>
        {
            var stack = StackWith( 0.2f, out _ );
            var copy = stack.Duplicate( 0, "Copy" );
            copy.Deltas.Deltas[0] = new Vec3( 9, 0, 0 );
            Near( stack.Entries[0].Deltas.Deltas[0], new Vec3( 0.2f, 0, 0 ), 1e-6f, "original after editing the copy" );
            var branch = stack.Branch( 0, "Variant" );
            Require( stack.Active == branch, "branch becomes active" );
            Require( stack.Entries.Count == 3, $"expected 3 entries, got {stack.Entries.Count}" );
            branch.Deltas.Deltas[1] = new Vec3( 7, 0, 0 );
            Near( stack.Entries[0].Deltas.Deltas[1], Vec3.Zero, 1e-6f, "original after editing the branch" );
        } );

        Check( "delete and reorder move only the removed layer's contribution", () =>
        {
            var stack = StackWith( 0.2f, out _ );
            var second = new Vec3[8];
            for ( var v = 0; v < 8; v++ )
                second[v] = new Vec3( 0.3f, 0, 0 );
            stack.Add( "Second", new SculptLayer( second ) );
            Near( stack.ComposeEntries().Deltas[0], new Vec3( 0.5f, 0, 0 ), 1e-6f, "both layers" );
            stack.Delete( 1 );
            Require( stack.Entries.Count == 1, "one entry left after delete" );
            Near( stack.ComposeEntries().Deltas[0], new Vec3( 0.2f, 0, 0 ), 1e-6f, "after delete" );

            // Order matters for Replace: A then B ends at B's value, B then A ends at A's.
            var ordered = new SculptLayerStack( 0, 8, new SculptLayer( new Vec3[8] ) );
            var a = new Vec3[8];
            var b = new Vec3[8];
            for ( var v = 0; v < 8; v++ )
            {
                a[v] = new Vec3( 0.5f, 0, 0 );
                b[v] = new Vec3( 0.9f, 0, 0 );
            }
            ordered.Add( "A", new SculptLayer( a ) ).Blend = SculptLayerBlend.Replace;
            ordered.Add( "B", new SculptLayer( b ) ).Blend = SculptLayerBlend.Replace;
            Near( ordered.EffectiveDeltas().Deltas[0], new Vec3( 0.9f, 0, 0 ), 1e-6f, "A then B" );
            ordered.Move( 1, 0 );
            Near( ordered.EffectiveDeltas().Deltas[0], new Vec3( 0.5f, 0, 0 ), 1e-6f, "B then A" );
        } );

        Check( "bake-current folds without moving the shape", () =>
        {
            var stack = StackWith( 0.2f, out _ );
            var second = new Vec3[8];
            for ( var v = 0; v < 8; v++ )
                second[v] = new Vec3( 0.3f, 0, 0 );
            stack.Add( "Second", new SculptLayer( second ) );
            var before = stack.EffectiveDeltas();
            stack.SetActive( 0 );
            stack.BakeCurrent();
            Require( stack.Entries.Count == 1, "baked entry is gone" );
            NearLayer( stack.EffectiveDeltas(), before, 1e-6f, "shape after bake-current" );
            Near( stack.Base.Deltas[0], new Vec3( 0.2f, 0, 0 ), 1e-6f, "baked into the base" );
        } );

        Check( "bake-all equals sequential bake", () =>
        {
            SculptLayerStack TwoLayers()
            {
                var stack = StackWith( 0.2f, out _ );
                var second = new Vec3[8];
                var third = new Vec3[8];
                for ( var v = 0; v < 8; v++ )
                {
                    second[v] = new Vec3( 0, 0.3f, 0 );
                    third[v] = new Vec3( 0, 0, 0.4f );
                }
                stack.Add( "Y", new SculptLayer( second ) ).Blend = SculptLayerBlend.Multiply;
                stack.Add( "Z", new SculptLayer( third ) ).Blend = SculptLayerBlend.Replace;
                stack.SetIntensity( 1, 0.5f );
                return stack;
            }

            var all = TwoLayers();
            all.BakeAll();
            Require( all.Entries.Count == 0 && all.ActiveIndex == -1, "bake-all empties the stack" );

            var oneByOne = TwoLayers();
            while ( oneByOne.Entries.Count > 0 )
            {
                oneByOne.SetActive( 0 );
                oneByOne.BakeCurrent();
            }

            NearLayer( all.Base, oneByOne.Base, 1e-6f, "bake-all vs sequential base" );
            var flat = TwoLayers();
            flat.Flatten();
            NearLayer( flat.Base, all.Base, 1e-6f, "flatten vs bake-all" );
        } );

        Check( "delta storage is smaller than a full mesh copy", () =>
        {
            var sculpt = SculptOn( Box(), levels: 2 );
            var stack = SculptLayerStack.Attach( sculpt, sculpt.TopLevel );
            var top = sculpt.Evaluate( sculpt.TopLevel );
            Require( top.VertexCount > 8, $"L2 should subdivide past 8 vertices, got {top.VertexCount}" );
            var deltas = new Vec3[top.VertexCount];
            for ( var v = 0; v < deltas.Length; v++ )
                deltas[v] = new Vec3( 0.1f, 0.05f, 0 );
            stack.Add( "Bump", new SculptLayer( deltas ) );
            stack.Add( "Second", new SculptLayer( deltas ) );
            var stored = stack.StoredBytes();
            var copy = SculptLayerStack.FullMeshCopyBytes( top );
            Require( stored < copy, $"delta storage {stored} bytes should beat a full mesh copy {copy} bytes at L2" );
        } );

        Check( "blob v2 round-trips layers, morphs and groups; v1 reads with neither", () =>
        {
            var sculpt = SculptOn( Box() );
            var stack = SculptLayerStack.Attach( sculpt, 0 );
            var deltas = new Vec3[8];
            for ( var v = 0; v < 8; v++ )
                deltas[v] = new Vec3( 0.2f, 0.1f, 0 );
            var entry = stack.Add( "Bump", new SculptLayer( deltas ) );
            entry.Intensity = 0.75f;
            entry.Blend = SculptLayerBlend.Subtract;
            var mask = new float[8];
            mask[3] = 0.5f;
            entry.Mask = mask;
            stack.CaptureMorph( sculpt, "Smile" );

            var groups = new PolygroupSet( 6 );
            groups.Assign( 5, groups.AddGroup( "Right" ) );
            groups.Rename( 1, "Right" );

            var bytes = SculptBlob.Write( sculpt, stack, groups );
            var loaded = SculptBlob.Read( bytes, Box(), out var readStack, out var readGroups );
            Require( loaded is not null && readStack is not null && readGroups is not null,
                "v2 blob should carry a stack and groups" );
            Require( readStack.Entries.Count == 1, $"expected 1 entry, got {readStack.Entries.Count}" );
            var readEntry = readStack.Entries[0];
            Require( readEntry.Name == "Bump", $"entry name {readEntry.Name}" );
            Require( MathF.Abs( readEntry.Intensity - 0.75f ) < 1e-6f, $"intensity {readEntry.Intensity}" );
            Require( readEntry.Blend == SculptLayerBlend.Subtract, $"blend {readEntry.Blend}" );
            Require( readEntry.Mask is not null && MathF.Abs( readEntry.Mask[3] - 0.5f ) < 1e-6f,
                "mask round-trips" );
            NearLayer( readEntry.Deltas, entry.Deltas, 1e-4f, "entry deltas after quantisation" );
            Require( readStack.Morphs.Count == 1 && readStack.Morphs[0].Name == "Smile",
                "morph round-trips" );
            Require( readGroups.GroupOf( 5 ) == 1 && readGroups.NameOf( 1 ) == "Right",
                "groups round-trip" );

            var v1 = SculptBlob.Write( sculpt );
            _ = SculptBlob.Read( v1, Box(), out var v1Stack, out var v1Groups );
            Require( v1Stack is null && v1Groups is null, "a v1 blob reads with no stack and no groups" );
        } );

        Check( "morph checkpoint lands on the mesh as a valid morph", () =>
        {
            var sculpt = SculptOn( Box() );
            var stack = StackWith( 0.2f, out _ );
            stack.CaptureMorph( sculpt, "Smile" );
            var mesh = sculpt.Evaluate( sculpt.TopLevel );
            stack.AddMorphsToMesh( mesh, sculpt.FramesFor( sculpt.TopLevel ) );
            var found = false;
            foreach ( var morph in mesh.ValidMorphs )
            {
                if ( morph.Name != "Smile" )
                    continue;
                found = true;
                Require( morph.Deltas.Length == mesh.VertexCount, "morph covers every vertex" );
                Require( morph.Deltas[0].Length > 0.01f, "morph carries the displacement in world space" );
            }
            Require( found, "Smile should be among the mesh's valid morphs" );
            Require( stack.DeleteMorph( "Smile" ), "delete returns true for an existing morph" );
            Require( !stack.DeleteMorph( "Smile" ), "delete returns false the second time" );
        } );

        Check( "polygroup isolate, hide, mask and centroid", () =>
        {
            var mesh = Box();
            var groups = new PolygroupSet( mesh.FaceCount );
            var right = groups.AddGroup( "Right" );
            groups.AssignFaces( new[] { 5 }, right );

            var isolated = groups.Isolate( right );
            Require( isolated.Length == 6, "isolate covers every face" );
            for ( var f = 0; f < 6; f++ )
                Require( isolated[f] == ( f == 5 ), $"isolate face {f}" );

            var visible = groups.VisibleFaces( new[] { right } );
            for ( var f = 0; f < 6; f++ )
                Require( visible[f] == ( f != 5 ), $"hide face {f}" );

            var mask = groups.MaskForGroup( mesh, right );
            foreach ( var v in new[] { 1, 2, 5, 6 } )
                Require( MathF.Abs( mask[v] - 1f ) < 1e-6f, $"right-face vertex {v} masked in" );
            foreach ( var v in new[] { 0, 3, 4, 7 } )
                Require( MathF.Abs( mask[v] ) < 1e-6f, $"other vertex {v} masked out" );

            Near( groups.Centroid( right, mesh ), new Vec3( 1, 0, 0 ), 1e-6f, "right-face centroid" );
            Require( groups.FacesInGroup( right ).Length == 1, "one face in the group" );

            var thrown = false;
            try { _ = groups.Centroid( 99, mesh ); } catch ( InvalidOperationException ) { thrown = true; }
            Require( thrown, "an empty group has no pivot" );
        } );

        Check( "polygroups from materials", () =>
        {
            var mesh = Box();
            mesh.Faces[0].Material = 1;
            mesh.Faces[1].Material = 1;
            var groups = PolygroupSet.FromMaterials( mesh );
            Require( groups.GroupOf( 0 ) == groups.GroupOf( 1 ), "same material, same group" );
            Require( groups.GroupOf( 0 ) != groups.GroupOf( 2 ), "different material, different group" );
            Require( groups.NameOf( groups.GroupOf( 0 ) ).StartsWith( "Material", StringComparison.Ordinal ),
                "material groups are named" );
        } );

        Check( "topology-change warning fires only with unbaked detail", () =>
        {
            var stack = StackWith( 0.2f, out _ );
            Require( stack.TopologyChangeWarning() is not null, "detail without a bake warns" );
            stack.SetEnabled( 0, false );
            Require( stack.TopologyChangeWarning() is null, "a disabled layer holds nothing to lose" );
            stack.SetEnabled( 0, true );
            stack.BakeAll();
            Require( stack.TopologyChangeWarning() is null, "baked detail warns about nothing" );
        } );

        Check( "layer-aware stroke lands in the active entry", () =>
        {
            var sculpt = SculptOn( Box() );
            var stack = SculptLayerStack.Attach( sculpt, 0 );
            stack.Add( "Stroke layer", new SculptLayer( new Vec3[8] ) );
            var stroke = new BrushStroke
            {
                Kind = BrushKind.Draw,
                Samples =
                {
                    new BrushSample( new Vec3( 1, 1, 1 ), new Vec3( 0.577f, 0.577f, 0.577f ), 3f, 1f ),
                },
            };
            var touched = stack.ApplyStroke( sculpt, stroke );
            Require( touched > 0, $"the stroke should touch vertices, got {touched}" );
            Require( stack.Active.HasDetail(), "the stroke's displacement landed in the active entry" );
            Require( !stack.HasUncapturedChanges( sculpt ), "the level layer is back to base after the stroke" );

            // No active entry: refused, not silently dropped.
            var empty = SculptLayerStack.Attach( sculpt, 0 );
            var refused = false;
            try { _ = empty.ApplyStroke( sculpt, stroke ); } catch ( InvalidOperationException ) { refused = true; }
            Require( refused, "a stroke with no active layer is refused" );

            // A stroke that bypassed the stack: refused until it is captured.
            _ = sculpt.Stroke( 0, stroke );
            Require( stack.HasUncapturedChanges( sculpt ), "direct stroke shows as uncaptured" );
            refused = false;
            try { _ = stack.ApplyStroke( sculpt, stroke ); } catch ( InvalidOperationException ) { refused = true; }
            Require( refused, "a stroke over uncaptured changes is refused" );
        } );

        Check( "capture-new-layer takes session strokes into the stack", () =>
        {
            var sculpt = SculptOn( Box() );
            var stack = SculptLayerStack.Attach( sculpt, 0 );
            var stroke = new BrushStroke
            {
                Kind = BrushKind.Draw,
                Samples =
                {
                    new BrushSample( new Vec3( 1, 1, 1 ), new Vec3( 0.577f, 0.577f, 0.577f ), 3f, 1f ),
                },
            };
            _ = sculpt.Stroke( 0, stroke );
            var before = (Vec3[])sculpt.LayerAt( 0 ).Deltas.Clone();
            Require( stack.HasUncapturedChanges( sculpt ), "stroke is uncaptured before capture" );
            var entry = stack.CaptureNewLayer( sculpt, "Session strokes" );
            Require( stack.Active == entry, "captured layer becomes active" );
            Require( !stack.HasUncapturedChanges( sculpt ), "level layer is back to base after capture" );
            NearLayer( entry.Deltas, new SculptLayer( before ), 1e-6f, "captured deltas" );
            NearLayer( stack.EffectiveDeltas(), new SculptLayer( before ), 1e-6f, "composed shape does not move" );
        } );
    }
}
