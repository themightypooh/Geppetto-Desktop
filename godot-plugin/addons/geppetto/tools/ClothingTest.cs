#if TOOLS
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Effigy;
using Godot;
using Marionette.Desktop;

namespace Geppetto;

/// <summary>
/// Headless test of the Clothing workspace, driven by tools/clothing_test.gd. On a copy of g1-spike it
/// loads a wearer (round-tripped through a Godot Skeleton3D + mesh, the way the Wearer tool reads a model),
/// adds every clothing feature with fixed inputs through the same calls the dial tools make, and checks
/// each rebuilds clean, changes the mesh, undoes and redoes; then Check / Push out, the material files,
/// Make clothing / Make live cloth, and a save + reload.
/// </summary>
public partial class ClothingTest : RefCounted
{
	readonly Godot.Collections.Array _checks = new();
	int _failed;

	public Godot.Collections.Dictionary Run( string fixtureRes, string workDir )
	{
		try { Body( fixtureRes, workDir ); }
		catch ( Exception ex ) { Add( "exception", false, ex.ToString() ); }
		return new Godot.Collections.Dictionary { { "checks", _checks }, { "failed", _failed }, { "total", _checks.Count } };
	}

	void Add( string name, bool ok, object? value = null )
	{
		if ( !ok ) _failed++;
		_checks.Add( new Godot.Collections.Dictionary { { "name", name }, { "ok", ok }, { "value", value?.ToString() ?? "" } } );
	}

	static int Verts( PartStudio s ) => s.Bodies.Sum( b => b.Mesh?.VertexCount ?? 0 );

	/// <summary>Vertex count, face count and slot use: changes when a feature adds, cuts or re-slots faces.</summary>
	static string Signature( PartStudio s ) =>
		$"{Verts( s )}/{s.Bodies.Sum( b => b.Mesh?.Faces.Count ?? 0 )}/{s.Bodies.Sum( b => b.Mesh?.Faces.Sum( f => (long)f.Material * 7 + 1 ) ?? 0 )}";

	bool Clean( GeppettoDocument doc, Feature? f = null ) =>
		doc.LastReport is { Errors.Count: 0 } && ( f == null || f.Error == null );

	string Errors( GeppettoDocument doc, Feature? f = null ) =>
		string.Join( " | ", ( doc.LastReport?.Errors.Select( e => e.ToString() ) ?? Array.Empty<string>() ).Append( f?.Error ?? "" ).Where( e => e.Length > 0 ).Take( 3 ) );

	static void Undo( GeppettoDocument doc ) { doc.Session.History.Undo(); doc.Session.BumpRevision(); doc.Refresh(); }
	static void Redo( GeppettoDocument doc ) { doc.Session.History.Redo(); doc.Session.BumpRevision(); doc.Refresh(); }

	void Body( string fixtureRes, string workDir )
	{
		// ---- a private copy of g1-spike ------------------------------------------------------------
		string fixture = ProjectSettings.GlobalizePath( fixtureRes );
		if ( Directory.Exists( workDir ) ) Directory.Delete( workDir, true );
		Directory.CreateDirectory( workDir );
		string copy = Path.Combine( workDir, "clothing-spike.effigy" );
		File.Copy( fixture, copy );
		var doc = GeppettoDocument.Get( copy );
		var studio = doc.Studio;
		Add( "fixture rebuilds clean", Clean( doc ), Errors( doc ) );
		int spikeVerts = Verts( studio );

		// g1-spike's rig (a stand and a wheel) is no human: delete its bones first, as the Wearer tool says.
		var spikeRig = studio.Rig;
		doc.Execute( new DelegateEditCommand( "Delete bones", () => { studio.Rig = new Skeleton(); studio.MarkAllDirty(); },
			() => { studio.Rig = spikeRig; studio.MarkAllDirty(); } ) );
		Add( "bones deleted, still clean", studio.Rig.Count == 0 && Clean( doc ), Errors( doc ) );

		// ---- Wearer: a citizen body, through Godot and back ---------------------------------------
		var rig = CitizenSkeleton.Build();
		var bodyMesh = SkinBlockout.Build( rig );
		var root = new Node3D { Name = "Citizen" };
		var skel = EffigySkeletonBridge.ToGodotSkeleton( rig );
		root.AddChild( skel );
		var mi = new MeshInstance3D { Mesh = EffigyMeshBridge.ToGodotArrayMesh( CpuMesh.FromPolyMesh( bodyMesh, _ => "skin" ) ) };
		skel.AddChild( mi );
		var (poly, rig2) = ClothingWearer.FromGodot( root );
		root.Free();

		Add( "wearer: skeleton read back", rig2 != null && rig2.Count == rig.Count && rig2.Bones.Select( b => b.Name ).SequenceEqual( rig.Bones.Select( b => b.Name ) ),
			$"{rig2?.Count} of {rig.Count} bones" );
		float boneErr = rig2 == null ? 999f : rig.Bones.Zip( rig2.Bones ).Max( z => ( z.First.Local.Origin - z.Second.Local.Origin ).Length );
		float axisErr = rig2 == null ? 999f : rig.Bones.Zip( rig2.Bones ).Max( z => MathF.Max( ( z.First.Local.X - z.Second.Local.X ).Length, ( z.First.Local.Z - z.Second.Local.Z ).Length ) );
		Add( "wearer: bone rests match (in, axis)", boneErr < 0.01f && axisErr < 0.01f, $"{boneErr:0.#####} / {axisErr:0.#####}" );
		var (bMin, bMax) = Bounds( bodyMesh.Positions );
		var (pMin, pMax) = Bounds( poly.Positions );
		float boundsErr = MathF.Max( ( bMin - pMin ).Length, ( bMax - pMax ).Length );
		Add( "wearer: surface read back (bounds, winding)", boundsErr < 0.01f && poly.Faces.Count > 0 && SignedVolume( poly ) > 0 == SignedVolume( bodyMesh ) > 0,
			$"bounds err {boundsErr:0.#####}, {poly.Faces.Count} tris, volume {SignedVolume( poly ):0} vs {SignedVolume( bodyMesh ):0}" );

		string msg = ClothingWearer.LoadWearer( doc, poly, "citizen", "res://citizen.glb", rig2 );
		var wearer = studio.Features.OfType<WearerFeature>().LastOrDefault();
		int afterWearer = Verts( studio );
		Add( "wearer: loaded, rig installed", wearer != null && studio.Rig.Count == rig.Count && !msg.StartsWith( "!" ), msg );
		Add( "wearer: rebuild clean, mesh grew", Clean( doc, wearer ) && afterWearer > spikeVerts, $"{spikeVerts} -> {afterWearer} {Errors( doc, wearer )}" );
		Add( "wearer: side-car written", wearer != null && File.Exists( ImportSidecar.PathFor( copy, wearer.Id ) ) );
		Undo( doc );
		Add( "wearer: undo restores", Verts( studio ) == spikeVerts && studio.Rig.Count == 0 && !studio.Features.OfType<WearerFeature>().Any(), Verts( studio ) );
		Redo( doc );
		Add( "wearer: redo", Verts( studio ) == afterWearer && studio.Rig.Count == rig.Count );

		// ---- every feature tool ---------------------------------------------------------------------
		var added = new List<Feature>();
		int spot = 0;
		foreach ( var tool in new[] { "Garment", "Trim", "Fabric", "Pocket", "Hardware", "Wear", "Glow", "Fur" } )
		{
			var f = ClothingTools.NewFeature( tool )!;
			ClothingTools.Prepare( studio, f );
			if ( f is GarmentFeature g ) { g.Sleeve.Value = 1f; g.Length.Value = 0.8f; g.Drape.Value = false; }
			// The kernel's tee on the blockout body leaves the default Chest spot uncovered (the same in
			// s&box's own build of it), so details go on the first spot this tee actually reaches.
			if ( f is GarmentDetailFeature d ) d.Spot.Index = spot;
			string before = Signature( studio );
			int vBefore = Verts( studio );
			doc.AddFeature( f );
			string after = Signature( studio );
			int vAfter = Verts( studio );
			Add( $"{tool}: rebuild clean", Clean( doc, f ), Errors( doc, f ) );
			if ( f is FabricFeature fab )
			{
				ClothingMaterials.Sync( doc );
				Add( $"{tool}: dresses the garment slots", fab.ResolvedSlots.Count > 0 && fab.ResolvedSlots.All( s => studio.MaterialNames.TryGetValue( s, out var n ) && n.StartsWith( "models/effigy/fabric/" ) ),
					string.Join( ",", fab.ResolvedSlots ) );
			}
			else if ( f is WearFeature )
				Add( $"{tool}: changes the garment", after != before, $"{before} -> {after}" );
			else
				Add( $"{tool}: mesh grew", vAfter > vBefore, $"{vBefore} -> {vAfter}" );
			Undo( doc );
			Add( $"{tool}: undo restores", Signature( studio ) == before && !studio.Features.Contains( f ), Signature( studio ) );
			Redo( doc );
			Add( $"{tool}: redo", Signature( studio ) == after && studio.Features.Contains( f ) );
			added.Add( f );
			if ( f is GarmentFeature )
			{
				spot = ReachedSpot( studio );
				Add( "Garment: a detail spot is covered", spot >= 0, spot >= 0 ? DetailSpots.Names[spot] : "none" );
				if ( spot < 0 ) spot = 0;
			}
		}

		// ---- Shape garment ------------------------------------------------------------------------
		var shapeable = ClothingTools.ShapeableGarments( studio );
		Add( "Shape garment: finds the garment", shapeable.Count == 1, shapeable.Count );
		if ( shapeable.Count > 0 )
		{
			string before = Signature( studio );
			int count = studio.Features.Count;
			var edit = ClothingTools.ShapeGarment( doc, shapeable[0] );
			Add( "Shape garment: mesh edit on the garment, clean", studio.Features.Contains( edit ) && edit.Bodies.BodyIds.Count == 1 && edit.KeepIfChanged.Value && Clean( doc, edit ) && Signature( studio ) == before,
				Errors( doc, edit ) );
			Undo( doc );
			Add( "Shape garment: undo removes it", studio.Features.Count == count );
			Redo( doc );
			added.Add( edit );
		}

		// ---- materials ----------------------------------------------------------------------------
		ClothingMaterials.Sync( doc );
		string dir = workDir;
		bool Has( string sub, string pattern ) => Directory.Exists( Path.Combine( dir, sub ) ) && Directory.GetFiles( Path.Combine( dir, sub ), pattern ).Length > 0;
		Add( "materials: fabric vmat + maps", Has( "models/effigy/fabric", "*.vmat" ) && Has( "models/effigy/fabric", "*_color.png" ) && Has( "models/effigy/fabric", "*_normal.png" ) );
		Add( "materials: fur vmat + maps", Has( "models/effigy/fur", "*.vmat" ) && Has( "models/effigy/fur", "*_noise.png" ) );
		Add( "materials: detail vmats", Has( "models/effigy/details", "*.vmat" ), Directory.Exists( Path.Combine( dir, "models/effigy/details" ) ) ? Directory.GetFiles( Path.Combine( dir, "models/effigy/details" ) ).Length : 0 );
		var fabricF = studio.Features.OfType<FabricFeature>().First();
		var bound = studio.MaterialNames[fabricF.ResolvedSlots[0]];
		var oldColour = fabricF.Colour.Value;
		fabricF.Colour.Value = "#aa2222"; studio.MarkDirty( fabricF ); doc.Refresh(); ClothingMaterials.Sync( doc );
		var pngs = Directory.GetFiles( Path.Combine( dir, "models/effigy/fabric" ), $"fabric_{ClothingMaterials.Tag( fabricF.Id )}_*_color.png" );
		Add( "materials: a colour change writes new maps, old ones go", pngs.Length == 1 && File.ReadAllText( Path.Combine( dir, bound ) ).Contains( Path.GetFileName( pngs[0] ) ), pngs.Length );
		fabricF.Colour.Value = oldColour; studio.MarkDirty( fabricF ); doc.Refresh(); ClothingMaterials.Sync( doc );

		// ---- Check / Push out ---------------------------------------------------------------------
		var garment = studio.Features.OfType<GarmentFeature>().First();
		var reports = ClothingCheck.Check( doc, out var checkMsg );
		Add( "Check: reports every garment", reports.Count == studio.Bodies.Count( b => b.IsGarment ), $"{reports.Count}: {checkMsg}" );
		if ( ClothingCheck.ClipDepths.Count == 0 )
		{
			// Nothing clipped at the default clearance: pull the garment into the body so there is something to push out.
			garment.Clearance.Value = 0f; garment.Fit.Index = 0; studio.MarkDirty( garment ); doc.Refresh();
			ClothingCheck.Check( doc, out checkMsg );
		}
		Add( "Check: finds clipping when the garment is pulled in", ClothingCheck.ClipDepths.Count > 0 && ClothingCheck.Marks.Count > 0, checkMsg );
		float clearance = garment.Clearance.Value;
		int clipsBefore = reports.Sum( r => r.Clipping );
		clipsBefore = ClothingCheck.Marks.Count;
		string pushMsg = ClothingCheck.PushOut( doc );
		Add( "Push out: raises Clearance", garment.Clearance.Value > clearance, $"{clearance} -> {garment.Clearance.Value}: {pushMsg}" );
		Add( "Push out: fewer clipping vertices", ClothingCheck.Marks.Count < clipsBefore, $"{clipsBefore} -> {ClothingCheck.Marks.Count}" );
		Undo( doc );
		Add( "Push out: undo restores Clearance", garment.Clearance.Value == clearance, garment.Clearance.Value );
		Redo( doc );
		float pushed = garment.Clearance.Value;
		string again = ClothingCheck.PushOut( doc );
		Add( "Push out again: keeps going while clipping is left", garment.Clearance.Value > pushed || ( ClothingCheck.ClipDepths.Count == 0 && again.StartsWith( "Nothing" ) ),
			$"{pushed} -> {garment.Clearance.Value}: {again}" );

		// ---- publish ------------------------------------------------------------------------------
		var (liveMsg, livePath) = ClothingPublish.MakeLiveCloth( doc );
		Add( "Make live cloth: file written", livePath != null && File.Exists( livePath ) && new FileInfo( livePath ).Length > 100, liveMsg );
		var (clothMsg, glb, clothing) = ClothingPublish.MakeClothing( doc );
		Add( "Make clothing: .glb + .clothing written", glb != null && File.Exists( glb ) && new FileInfo( glb ).Length > 1000 && clothing != null && File.Exists( clothing ), clothMsg );
		if ( glb != null && File.Exists( glb ) )
		{
			var gdoc = new GltfDocument();
			var state = new GltfState();
			var err = gdoc.AppendFromFile( glb, state );
			var scene = err == Error.Ok ? gdoc.GenerateScene( state ) : null;
			var meshes = scene == null ? new List<MeshInstance3D>() : scene.FindChildren( "*", "MeshInstance3D", true, false ).OfType<MeshInstance3D>().ToList();
			int skelBones = scene?.FindChildren( "*", "Skeleton3D", true, false ).OfType<Skeleton3D>().FirstOrDefault()?.GetBoneCount() ?? 0;
			var aabb = meshes.FirstOrDefault()?.Mesh?.GetAabb() ?? new Aabb();
			Add( "Make clothing: glb is skinned on the rig, garment only", meshes.Count > 0 && skelBones == studio.Rig.Count && aabb.Size.Y < 1.6f && aabb.Size.Y > 0.1f,
				$"{meshes.Count} meshes, {skelBones} bones, height {aabb.Size.Y:0.###} m" );
			scene?.Free();
		}

		// ---- save + reload -------------------------------------------------------------------------
		var saved = doc.Bridge.SaveAll( force: true );
		Add( "save: written", saved.Count > 0 && saved[0].Outcome is EffigySaveOutcome.Written or EffigySaveOutcome.Unchanged, saved.Count > 0 ? saved[0].Outcome + " " + saved[0].Message : "none" );
		var reread = StudioDocument.ReadFile( copy );
		ImportSidecar.Load( reread, copy );
		var report = reread.Rebuild();
		Add( "reload: rebuilds clean", report.Errors.Count == 0, string.Join( " | ", report.Errors.Take( 3 ) ) );
		Add( "reload: keeps every clothing feature", reread.Features.Select( f => f.TypeName ).SequenceEqual( studio.Features.Select( f => f.TypeName ) ),
			string.Join( ",", reread.Features.Select( f => f.TypeName ) ) );
		Add( "reload: keeps the rig and the wearer", reread.Rig.Count == studio.Rig.Count && reread.Bodies.Any( b => b.IsReference ) );
		Add( "reload: same mesh", Verts( reread ) == Verts( studio ), $"{Verts( reread )} vs {Verts( studio )}" );
		var g2 = reread.Features.OfType<GarmentFeature>().First();
		Add( "reload: keeps settings (Clearance, Sleeve)", MathF.Abs( g2.Clearance.Value - garment.Clearance.Value ) < 1e-4f && MathF.Abs( g2.Sleeve.Value - 1f ) < 1e-4f, g2.Clearance.Value );
		Add( "reload: keeps the fabric binding", reread.MaterialNames.Values.Any( v => v.StartsWith( "models/effigy/fabric/" ) ) );
	}

	/// <summary>The first named spot whose left side lands on the first garment.</summary>
	static int ReachedSpot( PartStudio studio )
	{
		var gb = studio.Bodies.First( b => b.IsGarment && !b.IsGarmentDetail );
		var surface = new DetailSurface( DetailEdges.Sheet( gb.Mesh ) );
		var map = new BodyRegions.Map( studio.Rig );
		for ( int i = 0; i < DetailSpots.Names.Length; i++ )
			if ( DetailSpots.Find( surface, map, DetailSpots.Names[i], 1 ) != null ) return i;
		return -1;
	}

	static (Vec3, Vec3) Bounds( List<Vec3> ps )
	{
		var lo = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
		var hi = new Vec3( float.MinValue, float.MinValue, float.MinValue );
		foreach ( var p in ps )
		{
			lo = new Vec3( MathF.Min( lo.x, p.x ), MathF.Min( lo.y, p.y ), MathF.Min( lo.z, p.z ) );
			hi = new Vec3( MathF.Max( hi.x, p.x ), MathF.Max( hi.y, p.y ), MathF.Max( hi.z, p.z ) );
		}
		return ( lo, hi );
	}

	/// <summary>Signed volume (fan-triangulated): its sign says which way the faces wind.</summary>
	static float SignedVolume( PolyMesh m )
	{
		double v = 0;
		foreach ( var f in m.Faces )
			for ( int i = 1; i + 1 < f.Indices.Length; i++ )
			{
				var a = m.Positions[f.Indices[0]]; var b = m.Positions[f.Indices[i]]; var c = m.Positions[f.Indices[i + 1]];
				v += Vec3.Dot( a, Vec3.Cross( b, c ) ) / 6.0;
			}
		return (float)v;
	}
}
#endif
