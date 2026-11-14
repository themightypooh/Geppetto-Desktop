#nullable enable
using System.Diagnostics;
using Effigy;
using Marionette.Desktop;

/// <summary>
/// `add Wound` through the real bot surfaces: the BuildScript `add` verb
/// (which resolves WoundFeature by reflection, like every other feature) and
/// the AiSuiteTools `add_feature` catalog entry (same reflection). The wound
/// record is <see cref="WoundFeature.LastRecord"/>; the coordinator's snippet
/// returns it from add_feature. Registration: WoundCommandTests.Register in
/// DesktopTestHost.Run (TestRegistry.cs, coordinator-owned).
/// </summary>
internal static class WoundCommandTests
{
	/// <summary>A blob sphere torso, radius 5 at the origin, named Torso.</summary>
	static PartStudio TorsoStudio()
	{
		var studio = new PartStudio();
		var blob = BuildScript.Run( studio, "add Blob name=Torso voxel=0.5 shapes=\"sphere 0,0,0 5\"" );

		if ( !blob.Ok )
			throw new Exception( "torso blob failed: " + blob.Error );

		return studio;
	}

	const string WoundLine = "add Wound target=Torso path=\"0,0,5; 2.5,0,4.33; 4.33,0,2.5; 5,0,0\" width=1.2 depth=0.6 liproll=0.5 layers=Skin,Fat,Muscle,Bone tendons=0";

	static bool SameColour( Vec4 a, Vec4 b ) =>
		MathF.Abs( a.x - b.x ) < 1e-6f && MathF.Abs( a.y - b.y ) < 1e-6f && MathF.Abs( a.z - b.z ) < 1e-6f && MathF.Abs( a.w - b.w ) < 1e-6f;

	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "wound-command", name, test );
		void Require( bool condition, string message ) { if ( !condition ) throw new Exception( message ); }

		Check( "add Wound displaces the torso surface and colours terraces", () =>
		{
			var studio = TorsoStudio();
			var before = studio.Bodies[0].Mesh.Positions.ToArray();
			var run = BuildScript.Run( studio, WoundLine );
			Require( run.Ok, "add Wound failed: " + run.Error );

			var mesh = studio.Bodies.Single( b => b.Name == "Torso" ).Mesh;
			var moved = 0;

			for ( var v = 0; v < mesh.VertexCount; v++ )
				if ( (mesh.Positions[v] - before[v]).Length > 1e-4f )
					moved++;

			Require( moved > 0, "no vertex moved — the wound touched nothing" );
			Require( mesh.HasVertexColors, "wound left no vertex colours (no terraces to read)" );

			var colours = mesh.VertexColors.ToHashSet();
			Require( colours.Any( c => SameColour( c, FleshMaterial.WoundColour( "Skin" ) ) ), "no Skin terrace colour on the mesh" );
			Require( colours.Any( c => SameColour( c, FleshMaterial.WoundColour( "Bone" ) ) ), "no Bone terrace colour on the mesh" );

			var wound = studio.Features.OfType<WoundFeature>().Single();
			Require( wound.LastRecord is not null, "WoundFeature.LastRecord is null after a successful wound" );
			// Affected counts every vertex inside the wound, including ones displaced by less than the 1e-4 measuring threshold.
			Require( wound.LastRecord!.AffectedVertices >= moved, $"record says {wound.LastRecord.AffectedVertices} affected, but {moved} moved" );
			Require( wound.LastRecord.Layers.SequenceEqual( new[] { "Skin", "Fat", "Muscle", "Bone" } ), "record layers are not Skin,Fat,Muscle,Bone" );
			Require( wound.LastRecord.Tendons == 0, "record tendons should be 0" );
			Require( wound.LastRecord.Target == "Torso", "record target is not Torso" );
			Require( wound.Warning is not null && wound.Warning.Contains( "vertices wounded" ), "no wound summary: " + wound.Warning );
		} );

		Check( "add Wound with tendons spans sinew and grows the mesh", () =>
		{
			var studio = TorsoStudio();
			var before = studio.Bodies[0].Mesh.VertexCount;
			var run = BuildScript.Run( studio, WoundLine.Replace( "tendons=0", "tendons=2" ) );
			Require( run.Ok, "add Wound tendons=2 failed: " + run.Error );

			var wound = studio.Features.OfType<WoundFeature>().Single();
			Require( wound.LastRecord is not null && wound.LastRecord.Tendons > 0, "no tendon spanned: " + wound.Warning );
			Require( studio.Bodies[0].Mesh.VertexCount > before, "tendon geometry added no vertices" );
		} );

		Check( "unknown body is refused honestly", () =>
		{
			var studio = TorsoStudio();
			var run = BuildScript.Run( studio, "add Wound target=NoSuchBody path=\"0,0,5; 5,0,0\"" );
			Require( !run.Ok, "wounding a missing body succeeded" );
			Require( run.Error.Contains( "No body called" ), "error does not name the missing body: " + run.Error );
		} );

		Check( "bad path is refused honestly", () =>
		{
			var studio = TorsoStudio();
			var notAPoint = BuildScript.Run( studio, "add Wound target=Torso path=\"banana\"" );
			Require( !notAPoint.Ok, "a nonsense path succeeded" );
			Require( notAPoint.Error.Contains( "not a point" ), "error does not say the point is bad: " + notAPoint.Error );

			var onePoint = BuildScript.Run( TorsoStudio(), "add Wound target=Torso path=\"0,0,5\"" );
			Require( !onePoint.Ok, "a one-point path succeeded" );
			Require( onePoint.Error.Contains( "at least two points" ), "error does not say the path needs two points: " + onePoint.Error );
		} );

		Check( "a path missing the mesh warns instead of failing", () =>
		{
			var studio = TorsoStudio();
			var run = BuildScript.Run( studio, "add Wound target=Torso path=\"50,50,50; 60,50,50\"" );
			Require( run.Ok, "a missing path failed the build: " + run.Error );

			var wound = studio.Features.OfType<WoundFeature>().Single();
			Require( wound.Warning is not null && wound.Warning.Contains( "touches no vertex" ), "no honest miss warning: " + wound.Warning );
			Require( wound.LastRecord is not null && wound.LastRecord.AffectedVertices == 0, "miss record should say 0 affected" );
		} );

		Check( "wound record round-trips through save/reopen", () =>
		{
			var studio = TorsoStudio();
			var run = BuildScript.Run( studio, WoundLine );
			Require( run.Ok, "add Wound failed: " + run.Error );

			var wounded = studio.Bodies[0].Mesh.Positions.ToArray();
			var affected = studio.Features.OfType<WoundFeature>().Single().LastRecord!.AffectedVertices;

			var reloaded = StudioDocument.Read( StudioDocument.Write( studio ) );
			reloaded.Rebuild();

			var feature = reloaded.Features.OfType<WoundFeature>().Single();
			Require( feature.Target.Value == "Torso", "reloaded wound lost its target" );
			Require( feature.LastRecord is not null && feature.LastRecord.AffectedVertices == affected,
				$"reloaded record says {feature.LastRecord?.AffectedVertices}, expected {affected}" );

			var mesh = reloaded.Bodies.Single( b => b.Name == "Torso" ).Mesh;
			Require( mesh.VertexCount == wounded.Length, $"reloaded mesh has {mesh.VertexCount} vertices, wounded had {wounded.Length}" );

			for ( var v = 0; v < mesh.VertexCount; v++ )
				Require( mesh.Positions[v].AlmostEquals( wounded[v], 1e-3f ), $"vertex {v} moved on reopen" );
		} );
	}
}
