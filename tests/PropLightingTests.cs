#nullable enable
using Effigy;
using Effigy.Map;
using Marionette.Desktop;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

// Hammer Desktop P2b: prop lighting correctness for negatively-scaled / mirrored geometry
// (hammer-prop-lighting-negative-scale).
//
// A negatively-scaled prop import flips the winding of every polygon, so the normal
// computed from the mirrored winding points inward and the prop lights as if its faces
// faced the wrong way (CS2-era Hammer pain, r/hammer 197j4cx). This file tests the
// correction in LightmapBake (PropLighting: negate the mirrored normal / reverse the
// mirrored winding, evaluate with the same direct-light model as the face bake), the
// prop scale model in MapEntities (Scale property + SetScale + ZeroScale validation),
// and the zero-scale rule in IssueEngine (ZeroScalePropRule). The mirrored prop must
// light identically to its unmirrored twin; a zero-scale prop is a named issue,
// never a silent break; every correction is flagged in the bake log.
//
// Not yet registered in tests/TestRegistry.cs: that file is coordinator-owned and the
// final integrator adds the one PropLightingTests.Register(registry, settings); line.
internal static class PropLightingTests
{
	static void Check( bool ok, string message ) { if ( !ok ) throw new Exception( message ); }
	static bool Near( float a, float b, float eps = 1e-4f ) => MathF.Abs( a - b ) <= eps;
	static bool Near( Vec3 a, Vec3 b, float eps = 1e-4f ) =>
		Near( a.x, b.x, eps ) && Near( a.y, b.y, eps ) && Near( a.z, b.z, eps );

	static T Throws<T>( Action action ) where T : Exception
	{
		try { action(); }
		catch ( T ex ) { return ex; }
		throw new Exception( $"expected {typeof( T ).Name}, got silent success" );
	}

	/// <summary>A 16x16 floor quad centred on (128,128,0), normal +Z, lightmap scale 16: exactly one luxel, at its centre (same shape as LightmapBakeTests.SmallFloor).</summary>
	static LiveFace SmallFloor( int id = 0 ) => new( id, new[]
	{
		new Vec3( 120f, 120f, 0f ), new Vec3( 136f, 120f, 0f ),
		new Vec3( 136f, 136f, 0f ), new Vec3( 120f, 136f, 0f ),
	} );

	static string RepoRoot()
	{
		string? dir = AppContext.BaseDirectory;
		while ( dir is not null )
		{
			if ( File.Exists( Path.Combine( dir, "kernel", "Effigy", "Map", "registry", "core-source1.json" ) ) ) return dir;
			dir = Directory.GetParent( dir )?.FullName;
		}
		dir = Directory.GetCurrentDirectory();
		while ( dir is not null )
		{
			if ( File.Exists( Path.Combine( dir, "kernel", "Effigy", "Map", "registry", "core-source1.json" ) ) ) return dir;
			dir = Directory.GetParent( dir )?.FullName;
		}
		throw new Exception( "cannot locate repo root: kernel/Effigy/Map/registry/core-source1.json not found" );
	}

	static string CorePath() => Path.Combine( RepoRoot(), "kernel", "Effigy", "Map", "registry", "core-source1.json" );

	static string Fixture( string name )
	{
		var rel = Path.Combine( "tests", "fixtures", "hammer", "hammer-prop-lighting-negative-scale", name );
		var d = new DirectoryInfo( Directory.GetCurrentDirectory() );
		for ( var i = 0; i < 6 && d is not null; i++, d = d.Parent )
		{
			var probe = Path.Combine( d.FullName, rel );
			if ( File.Exists( probe ) ) return probe;
		}
		var fromBase = Path.GetFullPath( Path.Combine( AppContext.BaseDirectory, "..", "..", "..", "..", rel ) );
		if ( File.Exists( fromBase ) ) return fromBase;
		throw new Exception( $"Fixture not found: {rel} (searched from {Directory.GetCurrentDirectory()})." );
	}

	static Vec3 ReadVec3( JsonArray pair )
	{
		if ( pair.Count != 3 ) throw new Exception( $"Expected a [x, y, z] triple, got {pair.Count} values." );
		return new Vec3( (float)pair[0]!.GetValue<double>(), (float)pair[1]!.GetValue<double>(), (float)pair[2]!.GetValue<double>() );
	}

	static MapEntities NewEntities() => new MapEntities( EntityRegistry.Load( CorePath() ) );

	static MapEntity PlaceProp( MapEntities entities, Vec3 origin, Vec3 scale, string model = "models/props_c17/oildrum001.mdl" )
	{
		var prop = entities.PlacePointEntity( "prop_static", origin,
			new Dictionary<string, string> { ["model"] = model } );
		entities.SetScale( prop, scale );
		return prop;
	}

	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "proplighting", name, test );

		Check( "determinant sign classifies mirrored, double-mirrored and zero scales", () =>
		{
			// One negative component: mirrored. Two negatives: determinant +1, a proper
			// rotation-like transform, NOT mirrored. Zero: not mirrored, un-lightable.
			Require( MapEntities.PropTransformDeterminant( new Vec3( -1f, 1f, 1f ) ) == -1f, "det(-1,1,1) must be -1" );
			Require( MapEntities.IsMirroredScale( new Vec3( -1f, 1f, 1f ) ), "(-1,1,1) must be mirrored" );
			Require( MapEntities.IsMirroredScale( new Vec3( 1f, -2f, 1f ) ), "(1,-2,1) must be mirrored" );
			Require( !MapEntities.IsMirroredScale( new Vec3( -1f, -1f, 1f ) ), "(-1,-1,1) has det +1 and must NOT be mirrored" );
			Require( !MapEntities.IsMirroredScale( new Vec3( 1f, 1f, 1f ) ), "(1,1,1) must not be mirrored" );
			Require( !MapEntities.IsMirroredScale( new Vec3( 0f, 1f, 1f ) ), "a zero scale is not mirrored" );
			Require( MapEntities.HasZeroScale( new Vec3( 1f, 0f, 1f ) ), "(1,0,1) must be zero-scale" );
			Require( !MapEntities.HasZeroScale( new Vec3( -1f, 1f, 1f ) ), "a mirrored scale is not zero-scale" );
			Require( MapEntities.ZeroScaleAxes( new Vec3( 0f, 1f, 0f ) ).SequenceEqual( new[] { "x", "z" } ),
				"zero axes of (0,1,0) must be x, z" );
			Require( MapEntities.IsPropClass( "prop_static" ) && MapEntities.IsPropClass( "PROP_DYNAMIC" ), "prop classes must match case-insensitively" );
			Require( !MapEntities.IsPropClass( "light" ) && !MapEntities.IsPropClass( "" ), "non-props must not match" );
			// The twin predicates in PropLighting must agree with the entity layer's.
			foreach ( var scale in new[] { new Vec3( 1f, 1f, 1f ), new Vec3( -1f, 1f, 1f ), new Vec3( -1f, -1f, 1f ), new Vec3( 1f, 0f, 1f ) } )
			{
				Require( PropLighting.Determinant( scale ) == MapEntities.PropTransformDeterminant( scale ), $"determinant must agree for {scale}" );
				Require( PropLighting.IsMirrored( scale ) == MapEntities.IsMirroredScale( scale ), $"mirrored must agree for {scale}" );
				Require( PropLighting.HasZeroScale( scale ) == MapEntities.HasZeroScale( scale ), $"zero-scale must agree for {scale}" );
			}
		} );

		Check( "CorrectNormal negates the mirrored normal and leaves the twin unchanged; zero scale throws", () =>
		{
			var up = new Vec3( 0f, 0f, 1f );
			var down = new Vec3( 0f, 0f, -1f );
			Require( PropLighting.CorrectNormal( up, new Vec3( 1f, 1f, 1f ) ).AlmostEquals( up ), "unmirrored normal must pass through" );
			Require( PropLighting.CorrectNormal( down, new Vec3( -1f, 1f, 1f ) ).AlmostEquals( up ), "mirrored inward normal must flip outward" );
			Require( PropLighting.CorrectNormal( up, new Vec3( -2f, 1f, 1f ) ).AlmostEquals( down ), "mirrored outward normal must flip inward" );
			Require( PropLighting.CorrectNormal( up, new Vec3( -1f, -1f, 1f ) ).AlmostEquals( up ), "double-negative scale must not flip" );
			var error = Throws<ArgumentException>( () => PropLighting.CorrectNormal( up, new Vec3( 1f, 0f, 1f ) ) );
			Require( error.Message.Contains( "zero scale" ), $"zero scale must be named, got: {error.Message}" );
		} );

		Check( "CorrectWinding reverses only the mirrored polygon", () =>
		{
			var quad = new[] { new Vec3( 0f, 0f, 0f ), new Vec3( 1f, 0f, 0f ), new Vec3( 1f, 1f, 0f ), new Vec3( 0f, 1f, 0f ) };
			var kept = PropLighting.CorrectWinding( quad, new Vec3( 1f, 1f, 1f ) );
			Require( kept.SequenceEqual( quad ), "unmirrored winding must keep its order" );
			var reversed = PropLighting.CorrectWinding( quad, new Vec3( -1f, 1f, 1f ) );
			Require( reversed.SequenceEqual( quad.Reverse() ), "mirrored winding must reverse" );
			Require( reversed.Count == quad.Length, "reversing must not drop vertices" );
			Throws<ArgumentException>( () => PropLighting.CorrectWinding( new[] { new Vec3( 0f, 0f, 0f ), new Vec3( 1f, 0f, 0f ) }, new Vec3( 1f, 1f, 1f ) ) );
			var error = Throws<ArgumentException>( () => PropLighting.CorrectWinding( quad, new Vec3( 0f, 1f, 1f ) ) );
			Require( error.Message.Contains( "zero scale" ), $"zero scale must be named, got: {error.Message}" );
		} );

		Check( "mirrored prop lights the same as its unmirrored twin (sun and point light)", () =>
		{
			var sample = new Vec3( 128f, 128f, 64f );
			var up = new Vec3( 0f, 0f, 1f );
			var down = new Vec3( 0f, 0f, -1f );
			// Sun overhead: the twin (normal up, scale 1) and the mirrored prop (its
			// imported normal points down, scale -1 on x) must both light at (1,1,1).
			var sun = LiveLight.Sun( 0, new Vec3( 0f, 0f, -1f ), Vec3.One, 1f );
			var sunJob = new LightmapBakeJob( new[] { SmallFloor() }, new[] { sun }, null, new[]
			{
				new PropLightSample( 1, sample, up, new Vec3( 1f, 1f, 1f ) ),
				new PropLightSample( 2, sample, down, new Vec3( -1f, 1f, 1f ) ),
			} );
			var sunLit = sunJob.EvaluatePropLighting();
			Require( sunLit.Count == 2, $"expected 2 results, got {sunLit.Count}" );
			Require( sunLit[0].Succeeded && sunLit[1].Succeeded, "both props must light" );
			Require( !sunLit[0].WasMirrored && sunLit[1].WasMirrored, "only the mirrored prop must be flagged" );
			Require( sunLit[1].CorrectedNormal.AlmostEquals( up ), $"corrected normal must be up, got {sunLit[1].CorrectedNormal}" );
			Require( Near( sunLit[0].Radiance, new Vec3( 1f, 1f, 1f ) ), $"twin radiance: {sunLit[0].Radiance}" );
			Require( Near( sunLit[1].Radiance, sunLit[0].Radiance ), $"mirrored radiance {sunLit[1].Radiance} must match twin {sunLit[0].Radiance}" );
			// Sanity: without the correction the inward normal lights black under this sun.
			var uncorrected = new LightmapBakeJob( new[] { SmallFloor() }, new[] { sun }, null, new[]
			{
				new PropLightSample( 9, sample, down, new Vec3( 1f, 1f, 1f ) ),
			} ).EvaluatePropLighting()[0];
			Require( Near( uncorrected.Radiance, Vec3.Zero ), $"uncorrected inward normal must light black, got {uncorrected.Radiance}" );
			// Point light, analytic case: 64 below the light, falloff 0.5, intensity 2 -> 1.0.
			var point = LiveLight.Point( 0, new Vec3( 128f, 128f, 128f ), Vec3.One, 2f );
			var pointJob = new LightmapBakeJob( new[] { SmallFloor() }, new[] { point }, null, new[]
			{
				new PropLightSample( 1, sample, up, new Vec3( 1f, 1f, 1f ) ),
				new PropLightSample( 2, sample, down, new Vec3( 1f, -1f, 1f ) ),
			} );
			var pointLit = pointJob.EvaluatePropLighting();
			Require( Near( pointLit[0].Radiance, new Vec3( 1f, 1f, 1f ) ), $"point twin radiance: {pointLit[0].Radiance}" );
			Require( Near( pointLit[1].Radiance, pointLit[0].Radiance ), $"point mirrored radiance {pointLit[1].Radiance} must match twin" );
		} );

		Check( "zero-scale prop is a named error in the lighting evaluation, not a silent black", () =>
		{
			var job = new LightmapBakeJob( new[] { SmallFloor() }, new[] { LiveLight.Sun( 0, new Vec3( 0f, 0f, -1f ), Vec3.One, 1f ) }, null, new[]
			{
				new PropLightSample( 7, new Vec3( 128f, 128f, 64f ), new Vec3( 0f, 0f, 1f ), new Vec3( 1f, 0f, 1f ) ),
			} );
			var result = job.EvaluatePropLighting()[0];
			Require( !result.Succeeded, "a zero-scale prop must not succeed" );
			Require( result.Error is not null && result.Error.Contains( "zero scale" ) && result.Error.Contains( "y" ),
				$"error must name the zero scale and axis, got: {result.Error}" );
			Require( Near( result.Radiance, Vec3.Zero ), "a failed prop must report zero radiance" );
			// The job constructor still rejects structural nonsense, naming it.
			Throws<ArgumentException>( () => new LightmapBakeJob( new[] { SmallFloor() }, new[] { LiveLight.Sun( 0, new Vec3( 0f, 0f, -1f ), Vec3.One, 1f ) }, null, new[]
			{
				new PropLightSample( 1, new Vec3( 128f, 128f, 64f ), new Vec3( 0f, 0f, 1f ), new Vec3( 1f, 1f, 1f ) ),
				new PropLightSample( 1, new Vec3( 128f, 128f, 64f ), new Vec3( 0f, 0f, 1f ), new Vec3( 1f, 1f, 1f ) ),
			} ) );
		} );

		Check( "SetScale defaults to (1,1,1), accepts zero, and rejects NaN/Infinity", () =>
		{
			var entities = NewEntities();
			var prop = entities.PlacePointEntity( "prop_static", new Vec3( 0f, 0f, 0f ),
				new Dictionary<string, string> { ["model"] = "models/props_c17/oildrum001.mdl" } );
			Require( prop.Scale.AlmostEquals( Vec3.One ), $"default scale must be (1,1,1), got {prop.Scale}" );
			entities.SetScale( prop, new Vec3( -1f, 2f, 1f ) );
			Require( prop.Scale.AlmostEquals( new Vec3( -1f, 2f, 1f ) ), $"scale did not stick: {prop.Scale}" );
			// Zero is accepted here: it is a validation issue, not a placement rejection.
			entities.SetScale( prop, new Vec3( 1f, 0f, 1f ) );
			Require( prop.Scale.AlmostEquals( new Vec3( 1f, 0f, 1f ) ), "zero scale must be settable" );
			var nan = Throws<MapEntityException>( () => entities.SetScale( prop, new Vec3( float.NaN, 1f, 1f ) ) );
			Require( nan.Code == MapEntityException.CodeInvalidScale, $"NaN scale must be InvalidScale, got {nan.Code}" );
			var inf = Throws<MapEntityException>( () => entities.SetScale( prop, new Vec3( float.PositiveInfinity, 1f, 1f ) ) );
			Require( inf.Code == MapEntityException.CodeInvalidScale, $"infinite scale must be InvalidScale, got {inf.Code}" );
			// "scales" is not a registry keyvalue: setting it as one is rejected.
			var unknown = Throws<EntityRegistryException>( () => entities.SetKeyvalue( prop, "scales", "1 1 1" ) );
			Require( unknown.Code == EntityRegistry.CodeUnknownKeyValue, $"scales keyvalue must be UnknownKeyValue, got {unknown.Code}" );
		} );

		Check( "Validate flags a zero-scale prop_static as a ZeroScale error and ignores healthy props", () =>
		{
			var entities = NewEntities();
			PlaceProp( entities, new Vec3( 0f, 0f, 0f ), new Vec3( 1f, 1f, 1f ) );
			var mirrored = PlaceProp( entities, new Vec3( 64f, 0f, 0f ), new Vec3( -1f, 1f, 1f ) );
			var broken = PlaceProp( entities, new Vec3( 128f, 0f, 0f ), new Vec3( 1f, 0f, 1f ) );
			var issues = entities.Validate();
			var zero = issues.Where( i => i.Code == MapEntityIssue.CodeZeroScale ).ToList();
			Require( zero.Count == 1, $"expected exactly 1 ZeroScale issue, got {zero.Count}" );
			Require( zero[0].EntityId == broken.Id, $"ZeroScale must name entity #{broken.Id}, got #{zero[0].EntityId}" );
			Require( zero[0].Severity == MapEntityIssueSeverity.Error, "ZeroScale must be an error" );
			Require( zero[0].Message.Contains( "y" ), $"issue must name the zero axis, got: {zero[0].Message}" );
			Require( zero[0].Key == "scales", $"issue key must be 'scales', got '{zero[0].Key}'" );
			Require( !issues.Any( i => i.EntityId == mirrored.Id ), "a mirrored (non-zero) prop must not be flagged" );
		} );

		Check( "IssueEngine flags a zero-scale prop, ignores healthy props and non-props, and refreshes on ReCheck", () =>
		{
			var engine = new IssueEngine();
			var doc = new IssueDocument
			{
				Entities = new List<IssueEntity>
				{
					new IssueEntity( 1, "prop_static", new Vec3( 0f, 0f, 0f ) ) { Scale = new Vec3( 1f, 1f, 1f ) },
					new IssueEntity( 2, "prop_static", new Vec3( 64f, 0f, 0f ) ) { Scale = new Vec3( -1f, 1f, 1f ) },
					new IssueEntity( 3, "prop_static", new Vec3( 128f, 0f, 0f ) ) { Scale = new Vec3( 1f, 0f, 1f ) },
					new IssueEntity( 4, "light", new Vec3( 0f, 0f, 64f ) ) { Scale = new Vec3( 0f, 0f, 0f ) },
				},
			};
			var issues = engine.Run( doc );
			var zero = issues.Where( i => i.Kind == IssueKind.ZeroScaleProp ).ToList();
			Require( zero.Count == 1, $"expected exactly 1 ZeroScaleProp issue, got {zero.Count}" );
			Require( zero[0].EntityId == 3, $"ZeroScaleProp must name entity #3, got #{zero[0].EntityId}" );
			Require( zero[0].Code == "ZERO_SCALE_PROP", $"code must be ZERO_SCALE_PROP, got {zero[0].Code}" );
			Require( zero[0].Severity == IssueSeverity.Error, "ZeroScaleProp must be an error" );
			Require( zero[0].Message.Contains( "y" ), $"issue must name the zero axis, got: {zero[0].Message}" );
			Require( zero[0].FixHint.Contains( "SetScale" ), $"fix hint must point at SetScale, got: {zero[0].FixHint}" );
			// Fixing the scale clears the issue on re-check; the rule is entity-scoped.
			doc.Entities[2].Scale = new Vec3( 1f, 1f, 1f );
			var refreshed = engine.ReCheck( doc, Array.Empty<int>(), new[] { 3 }, issues );
			Require( refreshed.All( i => i.Kind != IssueKind.ZeroScaleProp ), "fixing the scale must clear the issue" );
			Require( refreshed.Count == issues.Count - 1, $"re-check must drop exactly the fixed issue, got {refreshed.Count} of {issues.Count}" );
		} );

		Check( "the bake log records every corrected prop and warns on zero-scale props", () =>
		{
			var sun = LiveLight.Sun( 0, new Vec3( 0f, 0f, -1f ), Vec3.One, 1f );
			var sample = new Vec3( 128f, 128f, 64f );
			var up = new Vec3( 0f, 0f, 1f );
			var down = new Vec3( 0f, 0f, -1f );
			var job = new LightmapBakeJob( new[] { SmallFloor() }, new[] { sun }, null, new[]
			{
				new PropLightSample( 1, sample, up, new Vec3( 1f, 1f, 1f ) ),
				new PropLightSample( 2, sample, down, new Vec3( -1f, 1f, 1f ) ),
				new PropLightSample( 3, sample, down, new Vec3( 1f, 1f, -1f ) ),
			} );
			var result = job.Refine();
			Require( result.Succeeded, $"bake failed: {result.Error}" );
			Require( result.PropCorrections.Count == 2, $"expected 2 corrections, got {result.PropCorrections.Count}" );
			Require( result.PropCorrections.Any( c => c.Contains( "prop 2" ) ), "log must name prop 2" );
			Require( result.PropCorrections.Any( c => c.Contains( "prop 3" ) ), "log must name prop 3" );
			Require( result.PropCorrections.All( c => c.Contains( "mirrored" ) ), "every correction line must say mirrored" );
			Require( !result.PropCorrections.Any( c => c.Contains( "prop 1" ) ), "the unmirrored twin must not be logged" );
			var zeroJob = new LightmapBakeJob( new[] { SmallFloor() }, new[] { sun }, null, new[]
			{
				new PropLightSample( 4, sample, up, new Vec3( 0f, 1f, 1f ) ),
			} );
			var zeroResult = zeroJob.Refine();
			Require( zeroResult.Succeeded, $"bake with a zero-scale prop must still succeed: {zeroResult.Error}" );
			Require( zeroResult.PropCorrections.Count == 0, "a zero-scale prop is not a correction" );
			Require( zeroResult.Warnings.Any( w => w.Contains( "prop 4" ) && w.Contains( "zero scale" ) ),
				$"warnings must name the zero-scale prop, got: {string.Join( " | ", zeroResult.Warnings )}" );
		} );

		Check( "fixture props light and validate as recorded", () =>
		{
			var json = JsonNode.Parse( File.ReadAllText( Fixture( "props.json" ) ) )!.AsObject();
			var entities = NewEntities();
			var samples = new List<PropLightSample>();
			var byId = new Dictionary<int, MapEntity>();
			foreach ( var node in json["props"]!.AsArray() )
			{
				var p = node!.AsObject();
				var id = p["id"]!.GetValue<int>();
				var entity = entities.PlacePointEntity( p["class"]!.GetValue<string>(), ReadVec3( p["origin"]!.AsArray() ),
					new Dictionary<string, string> { ["model"] = p["model"]!.GetValue<string>() } );
				Require( entity.Id == id, $"fixture prop {id} placed as entity #{entity.Id}" );
				entities.SetScale( entity, ReadVec3( p["scale"]!.AsArray() ) );
				byId[id] = entity;
				samples.Add( new PropLightSample( id, ReadVec3( p["origin"]!.AsArray() ), ReadVec3( p["normal"]!.AsArray() ), ReadVec3( p["scale"]!.AsArray() ) ) );
			}
			var expected = json["expected"]!.AsObject();
			var twinPair = expected["twinPair"]!.AsArray().Select( n => n!.GetValue<int>() ).ToArray();
			var tolerance = (float)expected["twinTolerance"]!.GetValue<double>();
			var mirroredIds = expected["mirroredPropIds"]!.AsArray().Select( n => n!.GetValue<int>() ).ToArray();
			var zeroIds = expected["zeroScalePropIds"]!.AsArray().Select( n => n!.GetValue<int>() ).ToArray();

			var issues = entities.Validate();
			var zeroIssues = issues.Where( i => i.Code == MapEntityIssue.CodeZeroScale ).Select( i => i.EntityId ).OrderBy( i => i ).ToArray();
			Require( zeroIssues.SequenceEqual( zeroIds ), $"ZeroScale entities must be [{string.Join( ",", zeroIds )}], got [{string.Join( ",", zeroIssues )}]" );

			var sun = LiveLight.Sun( 0, new Vec3( 0f, 0f, -1f ), Vec3.One, 1f );
			var job = new LightmapBakeJob( new[] { SmallFloor() }, new[] { sun }, null, samples );
			var lit = job.EvaluatePropLighting().ToDictionary( r => r.PropId );
			var twin = lit[twinPair[0]];
			var mirror = lit[twinPair[1]];
			Require( twin.Succeeded && mirror.Succeeded, "both twins must light" );
			Require( Near( mirror.Radiance, twin.Radiance, tolerance ), $"twin radiances must match within {tolerance}: {twin.Radiance} vs {mirror.Radiance}" );
			foreach ( var id in mirroredIds )
				Require( lit[id].WasMirrored, $"fixture prop {id} must be flagged mirrored" );
			foreach ( var id in zeroIds )
				Require( !lit[id].Succeeded && lit[id].Error is not null && lit[id].Error.Contains( "zero scale" ), $"fixture prop {id} must be a named zero-scale error" );
			var baked = job.Refine();
			Require( baked.Succeeded, $"fixture bake failed: {baked.Error}" );
			Require( baked.PropCorrections.Count == mirroredIds.Length, $"log must hold {mirroredIds.Length} corrections, got {baked.PropCorrections.Count}" );
		} );
	}

	static void Require( bool condition, string message = "assertion failed" )
	{
		if ( !condition ) throw new Exception( message );
	}
}
