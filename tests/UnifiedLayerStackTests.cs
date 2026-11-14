using System;
using System.Collections.Generic;
using Effigy;
using Marionette.Desktop;

/// <summary>
/// Unified Layers system (godlike-unified-layers-panel-sculpt-paint): one stack for sculpt
/// deltas + paint layers, with groups, per-channel overrides, masks, instancing, opacity
/// keyframes, variant stacks A/B/C, bake, and the sculpt-to-morph bridge. Against the Desktop
/// kernel API directly: 4x4 paint canvases and an 8-vertex box fixture, no UI, no app wrapper.
/// </summary>
internal static class UnifiedLayerStackTests
{
    public static void Register( TestRegistry registry, TestSettings settings )
    {
        void Check( string name, Action test ) => registry.Add( "unified-layers", name, test );
        void Require( bool condition, string message = "assertion failed" )
        {
            if ( !condition ) throw new Exception( message );
        }

        // A 2x2x2 box centred on the origin, outward winding (same fixture as SculptTopologyTests).
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

        (byte R, byte G, byte B, byte A) Texel( PaintCanvas canvas, int x, int y )
        {
            var i = (y * canvas.Width + x) * 4;
            return (canvas.Rgba[i], canvas.Rgba[i + 1], canvas.Rgba[i + 2], canvas.Rgba[i + 3]);
        }

        void RequireTexel( PaintCanvas canvas, int x, int y, byte r, byte g, byte b, byte a, string what )
        {
            var (tr, tg, tb, ta) = Texel( canvas, x, y );
            Require( tr == r && tg == g && tb == b && ta == a,
                $"{what}: texel ({x},{y}) is ({tr},{tg},{tb},{ta}), expected ({r},{g},{b},{a})" );
        }

        Vec3[] Deltas( int count, params (int Index, Vec3 Delta)[] moved )
        {
            var deltas = new Vec3[count];
            foreach ( var (index, delta) in moved )
                deltas[index] = delta;
            return deltas;
        }

        Check( "stack JSON round-trips entries groups masks variants and paint content", () =>
        {
            var stack = new UnifiedLayerStack();
            stack.AddGroup( "Skin" );
            stack.AddGroup( "Details" );

            var mask = new float[16];
            for ( var i = 0; i < mask.Length; i++ ) mask[i] = 1f;
            mask[0] = 0f;

            var baseLayer = PaintLayer.Solid( "base", 4, 255, 255, 255, mask );
            var baseEntry = stack.AddPaint( baseLayer, "Skin" );
            baseEntry.ChannelOverrides = new Dictionary<PaintChannel, (PaintBlendMode, float)>
            {
                [PaintChannel.Roughness] = (PaintBlendMode.Multiply, 0.5f),
            };
            baseEntry.OpacityKeys.Add( new OpacityKeyframe( 0f, 0f ) );
            baseEntry.OpacityKeys.Add( new OpacityKeyframe( 2f, 1f ) );

            stack.AddSculpt( "brow", new SculptLayer( Deltas( 8, (0, new Vec3( 0.1f, 0f, 0f ) ) ) ), "Details" );
            stack.AddInstance( UnifiedLayerKind.Paint, "base-copy", "base", "Skin" );

            stack.SetVariantOverride( "A", "base", opacity: 0.5f );
            stack.SetVariantOverride( "B", "brow", intensity: 0.5f );
            stack.SetVariantOverride( "B", "base", enabled: false );
            stack.ActiveVariant = "A";

            var json = stack.ToJson();
            var restored = UnifiedLayerStack.FromJson( json );

            Require( restored.Count == 3, $"round trip should keep 3 entries, got {restored.Count}" );
            Require( restored.Groups.Count == 2 && restored.Groups[0] == "Skin" && restored.Groups[1] == "Details",
                "round trip should keep the groups in order" );
            Require( restored.ActiveVariant == "A", "round trip should keep the active variant" );

            var restoredBase = restored.Find( "base" );
            Require( restoredBase is not null && restoredBase.Paint is not null, "round trip should keep the base layer" );
            Require( MathF.Abs( restoredBase.Paint.Opacity - 1f ) < 1e-6f, "the entry opacity itself is 1; 0.5 lives in variant A" );
            Require( restoredBase.Paint.Mask is not null && restoredBase.Paint.Mask.Length == 16
                && restoredBase.Paint.Mask[0] == 0f && restoredBase.Paint.Mask[1] == 1f,
                "round trip should keep the mask" );
            Require( restoredBase.ChannelOverrides is not null
                && restoredBase.ChannelOverrides[PaintChannel.Roughness].Blend == PaintBlendMode.Multiply
                && MathF.Abs( restoredBase.ChannelOverrides[PaintChannel.Roughness].Opacity - 0.5f ) < 1e-6f,
                "round trip should keep the per-channel override" );
            Require( restoredBase.OpacityKeys.Count == 2
                && MathF.Abs( restoredBase.OpacityKeys[1].Time - 2f ) < 1e-6f,
                "round trip should keep the opacity keyframes" );

            var restoredCopy = restored.Find( "base-copy" );
            Require( restoredCopy is not null && restoredCopy.InstanceOf == "base",
                "round trip should keep the instance reference" );

            var restoredBrow = restored.Find( "brow" );
            Require( restoredBrow is not null && restoredBrow.Sculpt is not null
                && restoredBrow.Sculpt.Deltas[0].AlmostEquals( new Vec3( 0.1f, 0f, 0f ) ),
                "round trip should keep the sculpt deltas" );

            Require( restored.Variants["A"]["base"].Opacity == 0.5f, "round trip should keep variant A's override" );
            Require( restored.Variants["B"]["brow"].Intensity == 0.5f
                && restored.Variants["B"]["base"].Enabled == false,
                "round trip should keep variant B's overrides" );

            var original = stack.CompositePaint( null );
            var again = restored.CompositePaint( null );
            Require( original is not null && again is not null, "both stacks should composite something" );
            Require( original.Rgba.AsSpan().SequenceEqual( again.Rgba ),
                "the restored stack should composite byte-identical to the original" );
        } );

        Check( "three-layer composite equals the feature-tree reference", () =>
        {
            var stack = new UnifiedLayerStack();
            stack.AddPaint( PaintLayer.Solid( "white", 4, 255, 255, 255 ) );
            stack.AddPaint( PaintLayer.Solid( "red", 4, 255, 0, 0, null, PaintBlendMode.Normal, 0.5f ) );
            stack.AddPaint( PaintLayer.Solid( "green", 4, 0, 255, 0 ) );

            var result = stack.CompositePaint( null );

            PaintCanvas reference = null;
            foreach ( var layer in new[]
            {
                stack.Find( "white" ).Paint, stack.Find( "red" ).Paint, stack.Find( "green" ).Paint,
            } )
                reference = PaintLayers.CompositeSet( reference, layer.Maps, layer.Blend, layer.Opacity, layer.Mask );

            Require( result is not null && reference is not null, "both composites should produce a canvas" );
            Require( result.Rgba.AsSpan().SequenceEqual( reference.Rgba ),
                "the stack composite should equal the feature-tree composite texel for texel" );
            RequireTexel( result, 0, 0, 0, 255, 0, 255, "the top green layer should win outright" );
        } );

        Check( "a per-channel mask blocks its channel only", () =>
        {
            var maps = new PaintLayerMaps
            {
                Size = 4,
                Color = PaintLayers.Solid( 4, 4, 255, 0, 0 ),
            };
            maps.Set( PaintChannel.Roughness, PaintLayers.Solid( 4, 4, 128, 128, 128 ) );

            var stack = new UnifiedLayerStack();
            var entry = stack.AddPaint( new PaintLayer( "coat", maps ) );
            var colourMask = new float[16];
            for ( var y = 0; y < 4; y++ )
                for ( var x = 0; x < 4; x++ )
                    colourMask[y * 4 + x] = x < 2 ? 1f : 0f;
            entry.ChannelMasks = new Dictionary<PaintChannel, float[]> { [PaintChannel.Color] = colourMask };

            var result = stack.CompositePaint( null );
            Require( result is not null, "the stack should composite something" );
            RequireTexel( result, 0, 0, 255, 0, 0, 255, "the masked-in half should be red" );
            RequireTexel( result, 3, 0, 0, 0, 0, 0, "the masked-out half should stay transparent" );

            var roughness = result.RoughnessMap;
            Require( roughness is not null, "the roughness companion should survive the composite" );
            RequireTexel( roughness, 0, 0, 128, 128, 128, 255, "roughness should be painted where colour is" );
            RequireTexel( roughness, 3, 0, 128, 128, 128, 255, "roughness should be painted where colour is masked out" );
        } );

        Check( "variant switching does not mutate other variants", () =>
        {
            var stack = new UnifiedLayerStack();
            stack.AddPaint( PaintLayer.Solid( "base", 4, 255, 255, 255 ) );
            stack.AddPaint( PaintLayer.Solid( "tint", 4, 255, 0, 0 ) );
            stack.SetVariantOverride( "A", "tint", opacity: 0f );
            stack.SetVariantOverride( "B", "tint", opacity: 1f );

            stack.ActiveVariant = "A";
            var a = stack.CompositePaint( null );
            RequireTexel( a, 0, 0, 255, 255, 255, 255, "variant A should hide the tint" );

            stack.ActiveVariant = "B";
            var b = stack.CompositePaint( null );
            RequireTexel( b, 0, 0, 255, 0, 0, 255, "variant B should show the tint" );

            Require( stack.Variants["A"]["tint"].Opacity == 0f,
                "visiting variant B must not rewrite variant A's stored override" );

            stack.ActiveVariant = "A";
            var aAgain = stack.CompositePaint( null );
            RequireTexel( aAgain, 0, 0, 255, 255, 255, 255, "switching back to A should hide the tint again" );

            var bTexture = stack.PaintVariantTexture( "B" );
            Require( bTexture is not null && bTexture.Color is not null, "the B texture variant should exist" );
            RequireTexel( bTexture.Color, 0, 0, 255, 0, 0, 255, "the B texture variant should be red" );
            Require( stack.ActiveVariant == "A",
                "reading another variant's texture must not switch the active variant" );
        } );

        Check( "morph bridge puts a sculpt layer on the mesh as a morph", () =>
        {
            var mesh = Box();
            var stack = new UnifiedLayerStack();
            var entry = stack.AddSculpt( "brow", new SculptLayer( Deltas( 8,
                (0, new Vec3( 0.1f, 0f, 0f )), (5, new Vec3( 0f, 0.2f, 0f )) ) ) );

            var morph = stack.ToMorph( "brow", entry );
            Require( morph.Name == "brow", "the morph should carry the requested name" );
            Require( morph.Deltas.Length == 8, "the morph should have one delta per vertex" );
            Require( morph.Deltas[0].AlmostEquals( new Vec3( 0.1f, 0f, 0f ) )
                && morph.Deltas[5].AlmostEquals( new Vec3( 0f, 0.2f, 0f ) ),
                "the morph should carry the layer's deltas" );

            var added = stack.ApplyMorphToMesh( mesh, "brow", entry );
            Require( ReferenceEquals( added, morph ), "the mesh should hold the morph that was built" );
            Require( mesh.HasMorphs, "the mesh should report morphs after the bridge" );

            MeshMorph found = null;
            foreach ( var m in mesh.ValidMorphs )
            {
                if ( m.Name == "brow" )
                    found = m;
            }
            Require( found is not null && found.Deltas[5].AlmostEquals( new Vec3( 0f, 0.2f, 0f ) ),
                "the morph on the mesh should be a valid morph with the layer's deltas" );
        } );

        Check( "an instance follows its source edits and missing sources are reported", () =>
        {
            var stack = new UnifiedLayerStack();
            var source = stack.AddPaint( PaintLayer.Solid( "src", 4, 255, 0, 0 ) );
            stack.AddInstance( UnifiedLayerKind.Paint, "inst", "src" );

            Require( stack.MissingInstances().Count == 0, "a resolvable instance should not be missing" );
            var first = stack.CompositePaint( null );
            RequireTexel( first, 0, 0, 255, 0, 0, 255, "the instance should show the source's red" );

            source.Paint.Maps = new PaintLayerMaps
            {
                Size = 4,
                Color = PaintLayers.Solid( 4, 4, 0, 0, 255 ),
            };
            var second = stack.CompositePaint( null );
            RequireTexel( second, 0, 0, 0, 0, 255, 255, "repainting the source should repaint the instance" );

            stack.AddInstance( UnifiedLayerKind.Paint, "ghost", "no-such-layer" );
            var missing = stack.MissingInstances();
            Require( missing.Count == 1 && missing[0] == "ghost",
                "the unresolvable instance should be reported by name" );
            var third = stack.CompositePaint( null );
            RequireTexel( third, 0, 0, 0, 0, 255, 255, "the missing instance should be skipped, not throw" );
        } );

        Check( "opacity keyframes interpolate and clamp", () =>
        {
            var stack = new UnifiedLayerStack();
            var entry = stack.AddPaint( PaintLayer.Solid( "red", 4, 255, 0, 0 ) );
            entry.OpacityKeys.Add( new OpacityKeyframe( 0f, 0f ) );
            entry.OpacityKeys.Add( new OpacityKeyframe( 2f, 1f ) );

            Require( MathF.Abs( entry.OpacityAt( 1f, 1f ) - 0.5f ) < 1e-6f, "halfway through should be half opacity" );
            Require( MathF.Abs( entry.OpacityAt( -1f, 1f ) - 0f ) < 1e-6f, "before the first key should clamp to it" );
            Require( MathF.Abs( entry.OpacityAt( 3f, 1f ) - 1f ) < 1e-6f, "after the last key should clamp to it" );

            var half = stack.CompositePaintAt( 1f, null );
            var (r, g, b, a) = Texel( half, 0, 0 );
            Require( r == 255 && g == 0 && b == 0 && a >= 120 && a <= 135,
                $"the half-opacity frame should be red at about half alpha, got ({r},{g},{b},{a})" );
        } );

        Check( "groups and entries reorder without dropping anything", () =>
        {
            var stack = new UnifiedLayerStack();
            stack.AddGroup( "A" );
            stack.AddGroup( "B" );
            stack.MoveGroup( 0, 1 );
            Require( stack.Groups[0] == "B" && stack.Groups[1] == "A", "moving a group should reorder the group list" );

            stack.AddPaint( PaintLayer.Solid( "one", 4, 255, 0, 0 ), "B" );
            stack.AddPaint( PaintLayer.Solid( "two", 4, 0, 255, 0 ), "A" );
            stack.AddPaint( PaintLayer.Solid( "three", 4, 0, 0, 255 ), "A" );
            stack.Move( 0, 2 );
            Require( stack[0].Name == "two" && stack[1].Name == "three" && stack[2].Name == "one",
                "moving an entry should reorder the stack without dropping it" );
            Require( stack.Groups.Count == 2, "adding entries to existing groups should not duplicate the groups" );
        } );

        Check( "a disabled entry is skipped and layers store deltas not mesh copies", () =>
        {
            var below = PaintLayers.Solid( 4, 4, 255, 255, 255 );
            var stack = new UnifiedLayerStack();
            var entry = stack.AddPaint( PaintLayer.Solid( "red", 4, 255, 0, 0 ) );
            entry.Enabled = false;

            var result = stack.CompositePaint( below );
            Require( ReferenceEquals( result, below ),
                "a stack of only disabled layers should hand the backdrop back unchanged" );

            var sculpt = new SculptLayer( Deltas( 8, (0, new Vec3( 0.1f, 0f, 0f )) ) );
            var sculptEntry = stack.AddSculpt( "bump", sculpt );
            Require( sculptEntry.Sculpt.Deltas.Length == 8,
                "a sculpt entry should store one delta per vertex and nothing else" );

            var sum = stack.ApplySculpt();
            Require( sum.Length == 8 && sum[0].AlmostEquals( new Vec3( 0.1f, 0f, 0f ) ),
                "the summed deltas should carry the layer's displacement" );
            sum[0] = new Vec3( 9f, 9f, 9f );
            Require( sculptEntry.Sculpt.Deltas[0].AlmostEquals( new Vec3( 0.1f, 0f, 0f ) ),
                "mutating the summed array must not rewrite the stored layer" );
            Require( !ReferenceEquals( sum, stack.ApplySculpt() ),
                "each sum should be a fresh array" );

            var (bakedPaint, bakedSculpt) = stack.BakeApprovedVariant();
            Require( bakedPaint is null, "nothing enabled is painted, so the baked paint should be null" );
            Require( bakedSculpt is not null && bakedSculpt.Deltas.Length == 8,
                "the baked sculpt should be the summed deltas" );
            Require( stack.Count == 2, "baking must not destroy the source entries" );
        } );

        Check( "applying the stack to a mesh moves vertices by the summed deltas", () =>
        {
            var mesh = Box();
            var before = mesh.Positions[0];
            var frames = SculptFrames.Build( mesh );

            var stack = new UnifiedLayerStack();
            stack.AddSculpt( "bump", new SculptLayer( Deltas( 8, (0, new Vec3( 0.1f, 0f, 0f )) ) ) );

            stack.ApplyToMesh( mesh, frames );
            var moved = mesh.Positions[0] - before;
            var expected = 0.1f * frames.At[0].Scale;
            Require( MathF.Abs( moved.Length - expected ) < 1e-4f,
                $"vertex 0 should move by the delta times the frame scale ({expected}), moved {moved.Length}" );
            Require( mesh.Positions[1].Equals( new Vec3( 1, -1, -1 ) ),
                "a vertex with a zero delta should not move" );
        } );

        Check( "the panel model selects toggles and edits through the stack", () =>
        {
            var stack = new UnifiedLayerStack();
            stack.AddPaint( PaintLayer.Solid( "red", 4, 255, 0, 0 ), "Skin" );
            stack.AddSculpt( "bump", new SculptLayer( Deltas( 8 ) ), "Skin" );

            var panel = new LayersPanelModel( stack );
            panel.Select( "red" );
            Require( panel.Selected is not null && panel.Selected.Name == "red", "the panel should select by name" );
            panel.Select( "no-such-layer" );
            Require( panel.Selected is null, "selecting an unknown name should clear the selection" );

            panel.ToggleGroup( "Skin" );
            Require( panel.IsGroupExpanded( "Skin" ), "toggling a group should expand it" );
            panel.ToggleGroup( "Skin" );
            Require( !panel.IsGroupExpanded( "Skin" ), "toggling again should collapse it" );

            panel.SetOpacity( "red", 0.25f );
            Require( MathF.Abs( stack.Find( "red" ).Paint.Opacity - 0.25f ) < 1e-6f,
                "the panel opacity edit should reach the layer" );
            panel.SetIntensity( "bump", -0.5f );
            Require( MathF.Abs( stack.Find( "bump" ).Intensity + 0.5f ) < 1e-6f,
                "the panel intensity edit should reach the sculpt entry" );
            panel.ToggleEnabled( "red" );
            Require( !stack.Find( "red" ).Enabled, "the panel toggle should disable the entry" );

            panel.SetActiveVariant( "A" );
            panel.SetVariantOpacity( "A", "red", 0.75f );
            Require( stack.ActiveVariant == "A"
                && stack.Variants["A"]["red"].Opacity == 0.75f,
                "the panel variant edit should land in the variant" );

            panel.Select( "bump" );
            panel.ToggleGroup( "Skin" );
            var restored = LayersPanelModel.FromJson( stack, panel.ToJson() );
            Require( restored.SelectedName == "bump" && restored.IsGroupExpanded( "Skin" )
                && restored.Stack.ActiveVariant == "A",
                "the panel state should round-trip through JSON" );
        } );
    }
}
