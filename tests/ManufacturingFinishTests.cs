#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Effigy;

/// <summary>
/// Checks for godlike-manufacturing-finish-hole-wizard-drawings (Wave 3 P2):
/// Hole Wizard ISO numbers, fastener tables, patterns, direct-edit records, import
/// healing, and the drawing release workflow (views, H7, hole table, out-of-date flag,
/// DXF + PDF).
///
/// Honesty note: these checks run the real computation and the real mesh-boolean cut
/// (ExactMeshBoolean is the kernel's default provider, the same path HoleFeature uses).
/// They do NOT verify a B-rep solid or a STEP round-trip — that is the OCCT ticket's —
/// and they do not run the full suite or a GPU smoke. See the ticket handoff.
///
/// Not yet registered in tests/TestRegistry.cs: that file is coordinator-owned and the
/// final integrator adds the one ManufacturingFinishTests.Register(registry, settings);
/// line.
/// </summary>
internal static class ManufacturingFinishTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		// --- Hole Wizard: ISO metric numbers -------------------------------------------------
		registry.Add( "manufacturing", "tap drills are D minus pitch", () =>
		{
			AssertNear( IsoMetric.Coarse( "M3" ).TapDrillMm, 2.5f, 1e-6f, "M3 tap drill" );
			AssertNear( IsoMetric.Coarse( "M6" ).TapDrillMm, 5.0f, 1e-6f, "M6 tap drill" );
			AssertNear( IsoMetric.Coarse( "M10" ).TapDrillMm, 8.5f, 1e-6f, "M10 tap drill" );
			AssertNear( IsoMetric.Coarse( "M12" ).TapDrillMm, 10.2f, 1e-6f, "M12 tap drill" );
			AssertNear( IsoMetric.Coarse( "M20" ).TapDrillMm, 17.5f, 1e-6f, "M20 tap drill" );
			AssertNear( IsoMetric.Coarse( "M6" ).PitchMm, 1.0f, 1e-6f, "M6 pitch" );
			AssertNear( IsoMetric.Coarse( "M12" ).PitchMm, 1.75f, 1e-6f, "M12 pitch" );
		} );

		registry.Add( "manufacturing", "clearances follow ISO 273", () =>
		{
			var m6 = IsoMetric.Coarse( "M6" );
			AssertNear( m6.ClearanceFineMm, 6.4f, 1e-6f, "M6 fine clearance" );
			AssertNear( m6.ClearanceMediumMm, 6.6f, 1e-6f, "M6 medium clearance" );
			AssertNear( m6.ClearanceCoarseMm, 7.0f, 1e-6f, "M6 coarse clearance" );
			AssertNear( IsoMetric.Coarse( "M10" ).ClearanceMediumMm, 11.0f, 1e-6f, "M10 medium clearance" );
		} );

		registry.Add( "manufacturing", "counterbore and countersink follow ISO 4762 and ISO 10642", () =>
		{
			var m6 = IsoMetric.Coarse( "M6" );
			AssertNear( m6.CounterboreDiaMm, 10f, 1e-6f, "M6 counterbore dia" );
			AssertNear( m6.CounterboreDepthMm, 6f, 1e-6f, "M6 counterbore depth" );
			AssertNear( m6.CountersinkDiaMm, 11f, 1e-6f, "M6 countersink dia" );
			AssertNear( m6.CountersinkAngleDeg, 90f, 1e-6f, "countersink angle" );
			// Countersink depth follows from the two diameters and the angle: (11 - 6.6) / 2 / tan(45).
			var spec = HoleWizardFeature.Compute( m6, HoleStyle.Countersink, HoleThreadMode.None,
				true, 0f, "Medium", new Vec3( 0, 0, 0 ), new Vec3( 0, 0, -1 ) );
			AssertNear( spec.SinkDepthMm, 2.2f, 1e-4f, "M6 countersink depth" );
			AssertNear( spec.ShaftDiameterMm, 6.6f, 1e-6f, "countersink shaft is the clearance" );
		} );

		registry.Add( "manufacturing", "blind thread depth leaves drill point allowance", () =>
		{
			var m6 = IsoMetric.Coarse( "M6" );
			var errors = HoleWizardFeature.Validate( m6, HoleStyle.Simple, HoleThreadMode.Modelled, false, 1.0f );
			Assert( errors.Count == 1, $"a 1 mm blind M6 thread must be refused, got {errors.Count} errors" );
			var spec = HoleWizardFeature.Compute( m6, HoleStyle.Simple, HoleThreadMode.Modelled,
				false, 10f, "Medium", new Vec3( 0, 0, 0 ), new Vec3( 0, 0, -1 ) );
			AssertNear( spec.DrillDepthMm, 10f, 1e-6f, "blind drill depth" );
			AssertNear( spec.ThreadDepthMm, 8.5f, 1e-6f, "blind thread depth is drill depth minus 1.5 pitches" );
			AssertNear( spec.ShaftDiameterMm, 5.0f, 1e-6f, "modelled thread cuts the tap drill" );
			Assert( spec.ThreadNote.Length > 0, "modelled thread must record its helix profile" );
		} );

		registry.Add( "manufacturing", "modelled thread records major and minor diameters", () =>
		{
			var m10 = IsoMetric.Coarse( "M10" );
			var spec = HoleWizardFeature.Compute( m10, HoleStyle.Simple, HoleThreadMode.Modelled,
				true, 0f, "Medium", new Vec3( 0, 0, 0 ), new Vec3( 0, 0, -1 ) );
			AssertNear( spec.TapDrillMm, 8.5f, 1e-6f, "M10 tap drill" );
			AssertNear( spec.PitchMm, 1.5f, 1e-6f, "M10 pitch" );
			Assert( spec.ThreadNote.Contains( "M10x1.5" ), $"thread note must name the thread, got '{spec.ThreadNote}'" );
			Assert( spec.IsThrough, "through hole must say so" );
			AssertNear( spec.ThreadDepthMm, 0f, 1e-6f, "through thread depth 0 means full thickness" );
		} );

		registry.Add( "manufacturing", "counterbore wider than its face is refused", () =>
		{
			var m6 = IsoMetric.Coarse( "M6" );
			var errors = HoleWizardFeature.Validate( m6, HoleStyle.Counterbore, HoleThreadMode.None, true, 0f, 8f );
			Assert( errors.Count == 1, $"a 10 mm counterbore on an 8 mm face must be refused, got {errors.Count} errors" );
			Assert( errors[0].Contains( "10" ), $"refusal must name the counterbore size, got '{errors[0]}'" );
			var ok = HoleWizardFeature.Validate( m6, HoleStyle.Counterbore, HoleThreadMode.None, true, 0f, 12f );
			Assert( ok.Count == 0, "the same counterbore on a 12 mm face must pass" );
		} );

		registry.Add( "manufacturing", "parameters round-trip through save and load", () =>
		{
			var wizard = new HoleWizardFeature();
			wizard.Size.Index = Array.IndexOf( IsoMetric.Sizes, "M10" );
			wizard.Style.Index = 1; // Counterbore
			wizard.Thread.Index = 2; // Modelled
			wizard.DepthKind.Index = 1; // Blind
			wizard.Depth.Value = 12f;
			var saved = wizard.SaveParameters();
			var restored = new HoleWizardFeature();
			restored.LoadParameters( saved );
			Assert( restored.Size.Value == "M10", $"size must survive, got {restored.Size.Value}" );
			Assert( restored.Style.Value == "Counterbore", $"style must survive, got {restored.Style.Value}" );
			AssertNear( restored.Depth.Value, 12f, 1e-6f, "depth must survive" );
			var a = HoleWizardFeature.Compute( IsoMetric.Coarse( wizard.Size.Value ), HoleStyle.Counterbore,
				HoleThreadMode.Modelled, false, wizard.Depth.Value, "Medium", new Vec3( 0, 0, 0 ), new Vec3( 0, 0, -1 ) );
			var b = HoleWizardFeature.Compute( IsoMetric.Coarse( restored.Size.Value ), HoleStyle.Counterbore,
				HoleThreadMode.Modelled, false, restored.Depth.Value, "Medium", new Vec3( 0, 0, 0 ), new Vec3( 0, 0, -1 ) );
			AssertNear( a.ShaftDiameterMm, b.ShaftDiameterMm, 1e-6f, "restored spec shaft" );
			AssertNear( a.ThreadDepthMm, b.ThreadDepthMm, 1e-6f, "restored spec thread depth" );
			AssertThrows<InvalidOperationException>( () => restored.LoadParameters(
				new Dictionary<string, string> { ["Size"] = "M99" } ), "unknown size must be refused" );
		} );

		// --- Fasteners -----------------------------------------------------------------------
		registry.Add( "manufacturing", "ISO fastener dimensions", () =>
		{
			var bolt = FastenerLibrary.HexBolt( "M10", 40f );
			AssertNear( bolt.HeadAcrossFlatsMm, 16f, 1e-6f, "M10 bolt across flats" );
			AssertNear( bolt.HeadHeightMm, 6.4f, 1e-6f, "M10 bolt head height" );
			Assert( bolt.Standard == "ISO 4014", "bolt must name its standard" );
			var socket = FastenerLibrary.SocketHeadCap( "M8", 20f );
			AssertNear( socket.HeadDiaMm, 13f, 1e-6f, "M8 socket head dia" );
			AssertNear( socket.HeadHeightMm, 8f, 1e-6f, "M8 socket head height" );
			AssertNear( socket.SocketAcrossFlatsMm, 6f, 1e-6f, "M8 socket size" );
			var nut = FastenerLibrary.HexNut( "M12" );
			AssertNear( nut.AcrossFlatsMm, 18f, 1e-6f, "M12 nut across flats" );
			AssertNear( nut.HeightMm, 10.8f, 1e-6f, "M12 nut height" );
			var washer = FastenerLibrary.Washer( "M6" );
			AssertNear( washer.OuterDiaMm, 12f, 1e-6f, "M6 washer outer dia" );
			AssertNear( washer.ThicknessMm, 1.6f, 1e-6f, "M6 washer thickness" );
			AssertThrows<InvalidOperationException>( () => FastenerLibrary.HexBolt( "M99", 10f ), "unknown bolt size must be refused" );
		} );

		registry.Add( "manufacturing", "bolt head fits its counterbore", () =>
		{
			// The agreement that makes the wizard and the fastener library one system: an M6
			// hex bolt head is 10 mm across flats and the M6 counterbore is 10 mm across.
			foreach ( var size in new[] { "M6", "M8", "M10", "M12" } )
			{
				var bolt = FastenerLibrary.HexBolt( size, 20f );
				var thread = IsoMetric.Coarse( size );
				AssertNear( bolt.HeadAcrossFlatsMm, thread.CounterboreDiaMm, 1e-6f,
					$"{size} bolt head must fit its counterbore" );
				var socket = FastenerLibrary.SocketHeadCap( size, 20f );
				AssertNear( socket.HeadDiaMm, thread.CounterboreDiaMm, 1e-6f,
					$"{size} socket head must fit its counterbore" );
			}
		} );

		registry.Add( "manufacturing", "bearing and O-ring specs", () =>
		{
			var bearing = FastenerLibrary.Bearing( "6204" );
			AssertNear( bearing.BoreMm, 20f, 1e-6f, "6204 bore" );
			AssertNear( bearing.OutsideDiaMm, 47f, 1e-6f, "6204 outside dia" );
			AssertNear( bearing.WidthMm, 14f, 1e-6f, "6204 width" );
			var ring = FastenerLibrary.ORing( 20f, 3f );
			AssertNear( ring.OutsideDiaMm, 26f, 1e-6f, "20x3 O-ring outside dia" );
			AssertNear( ring.GrooveDepthMm, 2.25f, 1e-6f, "20x3 groove depth is 0.75 x section" );
			AssertNear( ring.GrooveWidthMm, 4.05f, 1e-6f, "20x3 groove width is 1.35 x section" );
		} );

		// --- Patterns and mirror ---------------------------------------------------------------
		registry.Add( "manufacturing", "linear and circular patterns and mirror", () =>
		{
			var seeds = new[] { new Vec3( 0, 0, 0 ), new Vec3( 0, 10, 0 ) };
			var linear = FastenerLibrary.PatternLinear( seeds, new Vec3( 1, 0, 0 ), 3, 10f );
			Assert( linear.Count == 6, $"2 seeds x 3 must be 6 placements, got {linear.Count}" );
			AssertNear( linear[2].x, 10f, 1e-6f, "second copy is one spacing along" );
			AssertNear( linear[4].x, 20f, 1e-6f, "third copy is two spacings along" );
			AssertNear( linear[5].y, 10f, 1e-6f, "seed offset survives the pattern" );

			var circular = FastenerLibrary.PatternCircular(
				new[] { new Vec3( 10, 0, 0 ) }, new Vec3( 0, 0, 0 ), new Vec3( 0, 0, 1 ), 4 );
			Assert( circular.Count == 4, $"4-way circular pattern must be 4 placements, got {circular.Count}" );
			AssertNear( circular[0].x, 10f, 1e-5f, "first copy is the seed" );
			AssertNear( circular[1].y, 10f, 1e-5f, "second copy is 90 degrees on" );
			AssertNear( circular[2].x, -10f, 1e-5f, "third copy is opposite the seed" );

			var mirrored = FastenerLibrary.MirrorPlacements(
				new[] { new Vec3( 5, 3, 0 ) }, new Vec3( 0, 0, 0 ), new Vec3( 1, 0, 0 ) );
			Assert( mirrored.Count == 1, "mirror returns one copy per seed" );
			AssertNear( mirrored[0].x, -5f, 1e-6f, "mirror flips x across the YZ plane" );
			AssertNear( mirrored[0].y, 3f, 1e-6f, "mirror keeps y" );
			AssertThrows<InvalidOperationException>( () =>
				FastenerLibrary.PatternLinear( seeds, new Vec3( 1, 0, 0 ), 0, 10f ), "a 0-count pattern must be refused" );
		} );

		// --- Direct edit -----------------------------------------------------------------------
		registry.Add( "manufacturing", "direct-edit ops move, offset, delete, replace and fillet records", () =>
		{
			var faces = new List<FaceRecord>
			{
				new() { Id = "top", Normal = new Vec3( 0, 0, 1 ), Centroid = new Vec3( 0, 0, 10 ), AreaMm2 = 600, Kind = "Planar" },
				new() { Id = "side", Normal = new Vec3( 1, 0, 0 ), Centroid = new Vec3( 25, 0, 5 ), AreaMm2 = 300, Kind = "Planar" },
				new() { Id = "edge", Normal = new Vec3( 0, 1, 0 ), Centroid = new Vec3( 0, 15, 5 ), AreaMm2 = 100, Kind = "Fillet", FilletRadiusMm = 2f },
			};
			var moved = DirectEditFeature.Apply( DirectEditOp.MoveFace, faces, "top", 2f, new Vec3( 0, 0, 1 ), 0f, new Vec3( 0, 0, 1 ), 0f );
			AssertNear( moved[0].Centroid.z, 12f, 1e-6f, "move face shifts its centroid" );
			AssertNear( moved[1].Centroid.x, 25f, 1e-6f, "move face leaves the other faces alone" );
			var offset = DirectEditFeature.Apply( DirectEditOp.OffsetSurface, faces, "", 1.5f, new Vec3( 0, 0, 1 ), 0f, new Vec3( 0, 0, 1 ), 0f );
			AssertNear( offset[1].Centroid.x, 26.5f, 1e-6f, "offset moves a face along its own normal" );
			var deleted = DirectEditFeature.Apply( DirectEditOp.DeleteFace, faces, "side", 0f, new Vec3( 0, 0, 1 ), 0f, new Vec3( 0, 0, 1 ), 0f );
			Assert( deleted.Count == 2, $"delete must leave 2 faces, got {deleted.Count}" );
			Assert( deleted.All( f => f.Id != "side" ), "deleted face must be gone" );
			var replaced = DirectEditFeature.Apply( DirectEditOp.ReplaceFace, faces, "top", 0f, new Vec3( 0, 0, 1 ), 0f, new Vec3( 0, 1, 0 ), 42f );
			AssertNear( replaced[0].Normal.y, 1f, 1e-6f, "replace sets the new normal" );
			AssertNear( replaced[0].AreaMm2, 42f, 1e-6f, "replace sets the new area" );
			var filleted = DirectEditFeature.Apply( DirectEditOp.ModifyFillet, faces, "edge", 0f, new Vec3( 0, 0, 1 ), 2.5f, new Vec3( 0, 0, 1 ), 0f );
			AssertNear( filleted[2].FilletRadiusMm, 2.5f, 1e-6f, "modify fillet records the new radius" );
			Assert( faces[2].FilletRadiusMm == 2f, "the input table must not be mutated" );
			AssertThrows<InvalidOperationException>( () =>
				DirectEditFeature.Apply( DirectEditOp.DeleteFace, faces, "", 0f, new Vec3( 0, 0, 1 ), 0f, new Vec3( 0, 0, 1 ), 0f ),
				"delete with no target must be refused" );
			AssertThrows<InvalidOperationException>( () =>
				DirectEditFeature.Apply( DirectEditOp.ModifyFillet, faces, "edge", 0f, new Vec3( 0, 0, 1 ), 0f, new Vec3( 0, 0, 1 ), 0f ),
				"a zero fillet radius must be refused" );
		} );

		// --- Import healing ----------------------------------------------------------------------
		registry.Add( "manufacturing", "healing reports closed, healed and open imports honestly", () =>
		{
			var box = BoxEdges( 10f );
			var closed = ImportHealing.Heal( box, 0.1f );
			Assert( closed.GapsFound == 0, $"a closed box has no gaps, got {closed.GapsFound}" );
			Assert( closed.IsClosed, "a closed box must report closed" );

			// One real gap: two dangling edge ends 0.05 mm apart, inside the 0.1 mm
			// stitch tolerance. (Removing one edge from a closed box is NOT a gap
			// under the naked-endpoint definition — every corner keeps two edges —
			// so the gap has to be dangling ends.)
			var gapped = box.ToList();
			gapped.Add( (new Vec3( 0, 0, 0 ), new Vec3( 0, 0, 5 )) );
			gapped.Add( (new Vec3( 0, 0, 0 ), new Vec3( 0.05f, 0, 5 )) );
			var healed = ImportHealing.Heal( gapped, 0.1f );
			Assert( healed.GapsFound == 1, $"two dangling ends are one gap, got {healed.GapsFound}" );
			Assert( healed.GapsHealed == 1, $"the gap must heal inside tolerance, got {healed.GapsHealed}" );
			Assert( healed.IsClosed, "the healed box must report closed" );

			var far = new List<(Vec3, Vec3)>
			{
				(new Vec3( 0, 0, 0 ), new Vec3( 10, 0, 0 )),
				(new Vec3( 10, 0, 0 ), new Vec3( 10, 10, 0 )),
				(new Vec3( 10, 10, 0 ), new Vec3( 15, 10, 0 )),
			};
			var open = ImportHealing.Heal( far, 0.1f );
			Assert( open.GapsRemaining == 1, $"a 5 mm gap at 0.1 mm tolerance must remain, got {open.GapsRemaining}" );
			Assert( !open.IsClosed, "an open import must not report closed" );

			var meshClosed = ImportHealing.HealMesh( Primitives.Box( 10f, 10f, 10f ), 0.01f );
			Assert( meshClosed.IsClosed, $"a box mesh must heal closed, got {meshClosed}" );
		} );

		// --- Drawing workspace -------------------------------------------------------------------
		registry.Add( "manufacturing", "drawing builds views, H7 dimensions and a hole table", () =>
		{
			var doc = DrawingDocument.Build( BracketSource() );
			Assert( doc.Views.Count == 4, $"a drawing has 4 views, got {doc.Views.Count}" );
			Assert( doc.Views.Any( v => v.Kind == DrawingViewKind.Front ), "front view missing" );
			Assert( doc.Views.Any( v => v.Kind == DrawingViewKind.Top ), "top view missing" );
			Assert( doc.Views.Any( v => v.Kind == DrawingViewKind.Section ), "section view missing" );
			Assert( doc.Views.Any( v => v.Kind == DrawingViewKind.Detail ), "detail view missing" );
			Assert( doc.HoleTable.Count == 2, $"hole table must have 2 rows, got {doc.HoleTable.Count}" );
			Assert( doc.HoleTable[0].Size == "M6x1", $"hole table must carry the thread, got '{doc.HoleTable[0].Size}'" );
			var h7 = doc.Dimensions.FirstOrDefault( d => d.Label == "Hole H1 dia" );
			Assert( h7 is not null, "M6 hole must have an H7 dimension" );
			AssertNear( h7!.UpperMm, 0.015f, 1e-6f, "6.6 mm bore H7 upper is 0.015" );
			Assert( h7.Text.Contains( "H7" ), $"H7 dimension text must say H7, got '{h7.Text}'" );
			var width = doc.Dimensions.FirstOrDefault( d => d.Label == "Width" );
			AssertNear( width!.NominalMm, 100f, 1e-6f, "overall width dimension" );
			Assert( !doc.IsOutOfDate, "a freshly built drawing is not out of date" );
		} );

		registry.Add( "manufacturing", "H7 upper deviations follow ISO 286-2", () =>
		{
			AssertNear( IsoMetric.H7UpperDeviationMm( 2.5f )!.Value, 0.010f, 1e-6f, "H7 to 3 mm" );
			AssertNear( IsoMetric.H7UpperDeviationMm( 5f )!.Value, 0.012f, 1e-6f, "H7 to 6 mm" );
			AssertNear( IsoMetric.H7UpperDeviationMm( 8.5f )!.Value, 0.015f, 1e-6f, "H7 to 10 mm" );
			AssertNear( IsoMetric.H7UpperDeviationMm( 10.2f )!.Value, 0.018f, 1e-6f, "H7 to 18 mm" );
			AssertNear( IsoMetric.H7UpperDeviationMm( 17.5f )!.Value, 0.018f, 1e-6f, "H7 to 18 mm (M20 tap)" );
			AssertNear( IsoMetric.H7UpperDeviationMm( 25f )!.Value, 0.021f, 1e-6f, "H7 to 30 mm" );
			Assert( IsoMetric.H7UpperDeviationMm( 100f ) is null, "H7 past 50 mm must be refused, not invented" );
		} );

		registry.Add( "manufacturing", "out-of-date flag fires on model change and clears on regenerate", () =>
		{
			var source = BracketSource();
			var doc = DrawingDocument.Build( source );
			Assert( !doc.CheckOutOfDate( source ), "unchanged source is not out of date" );
			var moved = BracketSource();
			moved.Holes[0].XMm = 25f;
			Assert( doc.CheckOutOfDate( moved ), "a moved hole must flag the drawing out of date" );
			doc.Regenerate( moved );
			Assert( !doc.CheckOutOfDate( moved ), "regenerate must clear the flag" );
			Assert( doc.HoleTable[0].XMm == 25f, "regenerate must pick up the moved hole" );
			var fingerprintBefore = doc.SourceFingerprint;
			doc.Regenerate( moved );
			Assert( doc.SourceFingerprint == fingerprintBefore, "regenerate of an unchanged source must be a no-op" );
		} );

		registry.Add( "manufacturing", "DXF export is real R12 text with the holes in it", () =>
		{
			var dxf = DrawingDocument.Build( BracketSource() ).ToDxf();
			Assert( dxf.Contains( "$ACADVER" ), "DXF must carry its version header" );
			Assert( dxf.Contains( "SECTION" ) && dxf.Contains( "ENTITIES" ), "DXF must have an entities section" );
			Assert( dxf.Contains( "CIRCLE" ), "DXF must carry the hole circles" );
			Assert( dxf.Contains( "H1 M6x1" ), "DXF must carry the hole table with its thread" );
			Assert( dxf.TrimEnd().EndsWith( "EOF" ), "DXF must end with EOF" );
			var lines = dxf.Split( '\n' );
			Assert( lines.Length > 50, $"a drawing DXF must be substantial, got {lines.Length} lines" );
		} );

		registry.Add( "manufacturing", "PDF export is a valid minimal PDF with a sound xref table", () =>
		{
			var pdf = DrawingDocument.Build( BracketSource() ).ToPdf();
			var text = Encoding.ASCII.GetString( pdf );
			Assert( text.StartsWith( "%PDF-1.4" ), "PDF must start with its header" );
			Assert( text.TrimEnd().EndsWith( "%%EOF" ), "PDF must end with %%EOF" );
			Assert( text.Contains( "Bracket" ), "PDF must carry the title-block title" );
			Assert( text.Contains( "REV A" ), "PDF must carry the revision" );
			// Every xref offset must land on its object's header.
			var xrefAt = text.IndexOf( "xref\n", StringComparison.Ordinal );
			Assert( xrefAt >= 0, "PDF must have an xref table" );
			var xrefLines = text[(xrefAt + 5)..].Split( '\n' );
			Assert( xrefLines[0].Trim() == "0 6", $"xref must cover 6 objects, got '{xrefLines[0].Trim()}'" );
			for ( var i = 1; i <= 5; i++ )
			{
				var offset = int.Parse( xrefLines[1 + i][..10], System.Globalization.CultureInfo.InvariantCulture );
				var at = text[offset..];
				Assert( at.StartsWith( $"{i} 0 obj", StringComparison.Ordinal ),
					$"xref offset for object {i} must land on its header" );
			}
			var startxref = text.IndexOf( "startxref\n", StringComparison.Ordinal );
			var startValue = int.Parse( text[(startxref + 10)..].Split( '\n' )[0].Trim(),
				System.Globalization.CultureInfo.InvariantCulture );
			Assert( startValue == xrefAt, "startxref must point at the xref table" );
		} );

		// --- Studio acceptance slice: bracket workflow -------------------------------------------
		registry.Add( "manufacturing", "bracket workflow cuts an M6 counterbore in the studio", () =>
		{
			var studio = new PartStudio();
			var plate = studio.Add( new PrimitiveFeature() );
			plate.SizeX.Value = 50f;
			plate.SizeY.Value = 30f;
			plate.SizeZ.Value = 10f;
			studio.Rebuild();
			var before = studio.Bodies[0].Mesh.SignedVolume();
			AssertNear( before, 15000f, 1f, "plate volume before drilling" );

			var wizard = studio.Add( new HoleWizardFeature() );
			wizard.Size.Index = Array.IndexOf( IsoMetric.Sizes, "M6" );
			wizard.Style.Index = 1; // Counterbore
			wizard.Thread.Index = 1; // Cosmetic
			wizard.DepthKind.Index = 0; // Through
			wizard.Faces.Add( new FaceRef( studio.Bodies[0].Id, new Vec3( 0, 0, 5 ), new Vec3( 0, 0, 1 ) ) );
			studio.Rebuild();
			Assert( wizard.Error is null, $"wizard must not error, got '{wizard.Error}'" );
			Assert( wizard.HolesCut == 1, $"one hole must be cut, got {wizard.HolesCut}" );
			Assert( wizard.LastSpecs.Count == 1, "one spec must be recorded" );
			AssertNear( wizard.LastSpecs[0].ShaftDiameterMm, 5.0f, 1e-6f, "cosmetic M6 cuts the tap drill" );
			Assert( wizard.LastSpecs[0].CutStatus == HoleCutStatus.MeshBooleanCut, "cut must be recorded as a mesh-boolean cut" );
			var after = studio.Bodies[0].Mesh.SignedVolume();
			Assert( after < 14500f, $"counterbore must remove material: {before} -> {after}" );
			Assert( after > 13000f, $"counterbore must not remove the plate: {before} -> {after}" );
		} );

		registry.Add( "manufacturing", "drawing sheet feature exports DXF and PDF from the studio", () =>
		{
			var studio = new PartStudio();
			var plate = studio.Add( new PrimitiveFeature() );
			plate.SizeX.Value = 100f;
			plate.SizeY.Value = 60f;
			plate.SizeZ.Value = 10f;
			studio.Rebuild();
			var sheet = studio.Add( new DrawingSheetFeature() );
			sheet.Title.Value = "Bracket";
			sheet.DrawingNo.Value = "DWG-001";
			sheet.Holes.Add( new DrawingHole { Label = "H1", XMm = 20f, YMm = 30f, DiameterMm = 6.6f, DepthMm = 0f, Thread = "M6x1" } );
			sheet.Parts.Add( new BomItem { PartNo = "1", Name = "Bracket plate", Quantity = 1, Material = "6061-T6" } );
			studio.Rebuild();
			Assert( sheet.Error is null, $"sheet must not error, got '{sheet.Error}'" );
			Assert( sheet.LastDocument is not null, "sheet must hold its document" );
			Assert( sheet.LastDocument!.HoleTable.Count == 1, "sheet must table its hole" );
			Assert( sheet.DxfText.Contains( "CIRCLE" ), "sheet DXF must carry the hole circle" );
			Assert( sheet.PdfBytes.Length > 0 && Encoding.ASCII.GetString( sheet.PdfBytes, 0, 8 ).StartsWith( "%PDF-1.4" ),
				"sheet PDF must start with its header" );
			Assert( sheet.LastDocument.Bom.Count == 1, "sheet must carry its BOM" );
		} );
	}

	static DrawingSource BracketSource() => new()
	{
		Min = new Vec3( 0, 0, 0 ),
		Max = new Vec3( 100, 60, 10 ),
		Holes = new List<DrawingHole>
		{
			new() { Label = "H1", XMm = 20f, YMm = 30f, DiameterMm = 6.6f, DepthMm = 0f, Thread = "M6x1" },
			new() { Label = "H2", XMm = 80f, YMm = 30f, DiameterMm = 11f, DepthMm = 0f, Thread = "M10x1.5" },
		},
		Parts = new List<BomItem> { new() { PartNo = "1", Name = "Bracket plate", Quantity = 1, Material = "6061-T6" } },
		Title = "Bracket",
		DrawingNo = "DWG-001",
		Revision = "A",
		Scale = 1f,
		SheetSize = "A4",
		DrawnBy = "muse",
		Date = "2026-11-13",
	};

	static List<(Vec3, Vec3)> BoxEdges( float size )
	{
		var h = size / 2f;
		var v = new[]
		{
			new Vec3( -h, -h, -h ), new Vec3( h, -h, -h ), new Vec3( h, h, -h ), new Vec3( -h, h, -h ),
			new Vec3( -h, -h, h ), new Vec3( h, -h, h ), new Vec3( h, h, h ), new Vec3( -h, h, h ),
		};
		var pairs = new (int, int)[]
		{
			(0, 1), (1, 2), (2, 3), (3, 0), (4, 5), (5, 6), (6, 7), (7, 4), (0, 4), (1, 5), (2, 6), (3, 7),
		};
		return pairs.Select( p => (v[p.Item1], v[p.Item2]) ).ToList();
	}

	static void Assert( bool condition, string message )
	{
		if ( !condition ) throw new Exception( $"manufacturing: {message}" );
	}

	static void AssertNear( float actual, float expected, float tolerance, string what )
	{
		if ( MathF.Abs( actual - expected ) > tolerance )
			throw new Exception( $"manufacturing: {what}: expected {expected}, got {actual}" );
	}

	static void AssertThrows<T>( Action action, string what ) where T : Exception
	{
		try { action(); }
		catch ( T ) { return; }
		throw new Exception( $"manufacturing: {what}: expected {typeof( T ).Name}, nothing was thrown" );
	}
}
