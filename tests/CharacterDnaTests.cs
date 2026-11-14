using System;
using System.Linq;

/// <summary>
/// Character DNA (spec 4.1, board tasks parametric-character-dna-citizen-templates and
/// character-dna-additional-archetypes): the `add Character` verb, the ten archetype
/// templates, and the slider checks (muscularity moves deltoids at fixed height, age
/// blends the nasolabial fold, quadrupeds carry four-leg hierarchies with tail chains).
/// Register in TestRegistry.cs with: CharacterDnaTests.Register( registry, settings );
/// (that manifest is coordinator-owned; this file only adds the Register entry point).
/// </summary>
internal static class CharacterDnaTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "character-dna", name, test );

		void Assert( bool condition, string message = "assertion failed" )
		{
			if ( !condition )
				throw new Exception( message );
		}

		void Throws<T>( Action action, string message = "expected an error, got silent success" ) where T : Exception
		{
			try { action(); }
			catch ( T ) { return; }
			throw new Exception( message );
		}

		static Effigy.CharacterBuild Build( string preset, float height = 72f, float muscularity = 0.5f, float age = 35f ) =>
			Effigy.CharacterDna.Build( new Effigy.CharacterDnaOptions
			{
				Preset = preset, Height = height, Muscularity = muscularity, Age = age,
			} );

		static Effigy.CharacterBuild BuildOpts( Effigy.CharacterDnaOptions o ) =>
			Effigy.CharacterDna.Build( o );

		static int RegionCount( Effigy.CharacterBuild b, Effigy.DnaRegion r ) =>
			b.Regions.Count( x => x == (int)r );

		void AssertCleanMesh( Effigy.CharacterBuild build, string what )
		{
			var mesh = build.Mesh;
			Assert( mesh.VertexCount >= 100, $"{what}: only {mesh.VertexCount} vertices" );
			Assert( mesh.FaceCount > 0, $"{what}: no faces" );

			foreach ( var face in mesh.Faces )
			{
				Assert( face.Count == 4, $"{what}: a face has {face.Count} corners, not 4" );

				foreach ( var uv in face.UVs )
					Assert( uv.x >= 0f && uv.x <= 1f && uv.y >= 0f && uv.y <= 1f,
						$"{what}: a UV ({uv.x}, {uv.y}) is outside [0,1]" );
			}

			Assert( mesh.IsRigged, $"{what}: mesh is not rigged" );
			Assert( build.Skeleton.Count > 0, $"{what}: no skeleton" );
		}

		Check( "citizen male builds rigged and UVd at the requested height", () =>
		{
			var build = Build( "citizen_male" );
			AssertCleanMesh( build, "citizen_male" );
			Assert( build.Skeleton.Count == 95, $"citizen has {build.Skeleton.Count} bones, not 95" );
			Assert( build.Skeleton.IndexOf( "pelvis" ) >= 0, "citizen has no pelvis" );
			Assert( build.Skeleton.IndexOf( "finger_ring_0_L" ) >= 0, "citizen has no ring finger" );
			Assert( build.Skeleton.IndexOf( "finger_pinky_0_L" ) < 0, "citizen should have no pinky bone" );
			Assert( MathF.Abs( build.Measurements["height"] - 72f ) < 0.5f,
				$"citizen height is {build.Measurements["height"]}, not 72" );
			Assert( build.EyeCenters.Left.y > 0f && build.EyeCenters.Right.y < 0f, "eye centres are not on opposite sides" );
		} );

		Check( "citizen female shares the Citizen bones with female proportions", () =>
		{
			var female = Build( "citizen_female" );
			var male = Build( "citizen_male" );
			AssertCleanMesh( female, "citizen_female" );
			Assert( female.Skeleton.Count == 95, $"female citizen has {female.Skeleton.Count} bones, not 95" );
			Assert( female.Measurements["shoulder_width"] < male.Measurements["shoulder_width"],
				"female shoulders are not narrower than male shoulders" );

			float HipWidth( Effigy.CharacterBuild b )
			{
				var pelvisZ = b.Skeleton.HeadWorld( b.Skeleton.IndexOf( "pelvis" ) ).z;
				var lo = float.PositiveInfinity;
				var hi = float.NegativeInfinity;

				for ( var i = 0; i < b.Mesh.Positions.Count; i++ )
				{
					var p = b.Mesh.Positions[i];

					if ( p.z < pelvisZ - 1f || p.z > pelvisZ + 1f )
						continue;

					// Count only the pelvis ring. The A-pose fingertips hang into this
					// slab at |y| ~ 26 and are identical for both sexes, so the
					// unfiltered span measures fingertip span (52.27 for both), not hip
					// width. The pelvis ring is the hips: 16.18 female vs 14.85 male.
					if ( b.Regions[i] != (int)Effigy.DnaRegion.Pelvis )
						continue;

					lo = MathF.Min( lo, p.y );
					hi = MathF.Max( hi, p.y );
				}

				return hi - lo;
			}

			Assert( HipWidth( female ) > HipWidth( male ), "female hips are not wider than male hips" );
		} );

		Check( "muscularity grows deltoids while height stays fixed", () =>
		{
			var soft = Build( "citizen_male", muscularity: 0f );
			var built = Build( "citizen_male", muscularity: 0.9f );
			Assert( built.Measurements["deltoid_radius"] > soft.Measurements["deltoid_radius"] * 1.1f,
				$"deltoid radius {soft.Measurements["deltoid_radius"]:0.00} -> {built.Measurements["deltoid_radius"]:0.00} did not grow with muscularity" );
			Assert( MathF.Abs( built.Measurements["height"] - soft.Measurements["height"] ) < 0.01f,
				"muscularity changed the height" );
		} );

		Check( "age blends the nasolabial fold inward", () =>
		{
			var young = Build( "citizen_male", age: 20f );
			var old = Build( "citizen_male", age: 60f );
			Assert( young.Mesh.VertexCount == old.Mesh.VertexCount, "age changed the vertex count" );

			var head = old.Landmarks["head_center"];
			var wing = old.Landmarks["nose_base"];
			var corner = old.Landmarks["mouth_L"];

			float FoldRadius( Effigy.CharacterBuild b )
			{
				var sum = 0f;
				var n = 0;

				for ( var i = 0; i < b.Mesh.Positions.Count; i++ )
				{
					var p = b.Mesh.Positions[i];

					if ( Effigy.SkinBinder.DistanceToSegment( p, wing, corner ) > 0.6f )
						continue;

					sum += MathF.Sqrt( (p.x - head.x) * (p.x - head.x) + p.y * p.y );
					n++;
				}

				Assert( n > 0, "no vertices found near the nasolabial fold" );
				return sum / n;
			}

			var youngR = FoldRadius( young );
			var oldR = FoldRadius( old );
			Assert( oldR < youngR, $"nasolabial fold radius {youngR:0.000} -> {oldR:0.000} did not move inward with age" );
			Assert( young.Mesh.Positions.Zip( old.Mesh.Positions, ( a, b ) => (a - b).Length ).Max() > 1e-6f,
				"age 20 and age 60 produced identical meshes" );
		} );

		Check( "every preset builds a rigged quad mesh", () =>
		{
			foreach ( var preset in Effigy.CharacterDna.Presets )
			{
				var build = Build( preset );
				AssertCleanMesh( build, preset );
			}
		} );

		Check( "realistic human is 8-head with five fingers", () =>
		{
			var build = Build( "realistic_human" );
			Assert( MathF.Abs( build.Template.Heads - 8f ) < 1e-6f, "realistic_human is not 8 heads" );
			Assert( build.Skeleton.IndexOf( "finger_pinky_0_L" ) >= 0, "realistic_human has no pinky" );
			Assert( build.Skeleton.IndexOf( "finger_pinky_2_R" ) >= 0, "realistic_human has no right pinky tip" );
		} );

		Check( "quadrupeds carry four-leg hierarchies with tail chains", () =>
		{
			foreach ( var preset in new[] { "quadruped_canine", "quadruped_feline" } )
			{
				var build = Build( preset );

				foreach ( var bone in new[]
				{
					"scapula_L", "arm_upper_L", "arm_lower_L", "hand_L",
					"leg_upper_L", "leg_lower_L", "ankle_L", "ball_L",
					"scapula_R", "arm_upper_R", "leg_upper_R",
					"tail_00", "tail_01", "tail_02", "tail_03", "tail_04",
				} )
					Assert( build.Skeleton.IndexOf( bone ) >= 0, $"{preset}: no bone '{bone}'" );

				Assert( build.Regions.Any( r => r == (int)Effigy.DnaRegion.Tail ), $"{preset}: no tail-region vertices" );
				Assert( build.Measurements["withers"] > 0f, $"{preset}: no withers measurement" );
				Assert( MathF.Abs( build.Measurements["withers"] - 72f ) < 1f,
					$"{preset}: withers {build.Measurements["withers"]:0.0}, not the requested 72" );
			}
		} );

		Check( "creature biped is digitigrade with a tail and claws", () =>
		{
			var build = Build( "creature_biped" );
			Assert( build.Skeleton.IndexOf( "tail_00" ) >= 0, "creature_biped has no tail chain" );

			var ankle = build.Skeleton.HeadWorld( build.Skeleton.IndexOf( "ankle_L" ) );
			var ball = build.Skeleton.HeadWorld( build.Skeleton.IndexOf( "ball_L" ) );
			Assert( ankle.z > ball.z + 1f, $"creature ankle z {ankle.z:0.0} is not raised above the ball z {ball.z:0.0}" );
		} );

		Check( "fish and avian carry their own bone hierarchies", () =>
		{
			var fish = Build( "ichthyoid_fish" );
			Assert( fish.Skeleton.IndexOf( "caudal_upper" ) >= 0, "fish has no caudal fin bone" );
			Assert( fish.Skeleton.IndexOf( "spine_0" ) >= 0, "fish has no spine chain" );
			Assert( MathF.Abs( fish.Measurements["length"] - 72f ) < 1f, $"fish length {fish.Measurements["length"]:0.0}, not 72" );

			var bird = Build( "avian_bird" );
			Assert( bird.Skeleton.IndexOf( "wing_shoulder_L" ) >= 0, "bird has no wing shoulder" );
			Assert( bird.Skeleton.IndexOf( "beak" ) >= 0, "bird has no beak" );
			Assert( bird.Skeleton.IndexOf( "feather_pri_4_L" ) >= 0, "bird has no primary feathers" );
		} );

		Check( "add Character verb builds the body and installs the rig", () =>
		{
			var studio = new Effigy.PartStudio();
			var r = Effigy.BuildScript.Run( studio, "add Character name=Hero preset=citizen_male height=72 muscularity=0.65 age=35" );
			Assert( r.Ok, $"add Character failed: {r.Error}" );
			Assert( studio.Bodies.Count == 1, $"add Character made {studio.Bodies.Count} bodies, not 1" );
			Assert( studio.Bodies[0].Name == "Hero", $"body is called '{studio.Bodies[0].Name}', not 'Hero'" );
			Assert( studio.Rig.Count == 95, $"studio rig has {studio.Rig.Count} bones, not 95" );
			Assert( studio.Bodies[0].Mesh.IsRigged, "verb-built mesh is not rigged" );
		} );

		Check( "a second character with a different rig is refused, not silently replaced", () =>
		{
			var studio = new Effigy.PartStudio();
			var first = Effigy.BuildScript.Run( studio, "add Character name=Hero preset=citizen_male" );
			Assert( first.Ok, $"first add failed: {first.Error}" );
			var second = Effigy.BuildScript.Run( studio, "add Character name=Beast preset=quadruped_canine" );
			Assert( !second.Ok, "second character with a different rig was accepted" );
			Assert( studio.Rig.Count == 95, "refused second character replaced the rig" );
		} );

		Check( "unknown preset fails honestly", () =>
		{
			Throws<ArgumentException>( () => Build( "nope" ) );

			var studio = new Effigy.PartStudio();
			var r = Effigy.BuildScript.Run( studio, "add Character preset=nope" );
			Assert( !r.Ok, "add Character with an unknown preset was accepted" );
		} );

		Check( "height scales the finished character", () =>
		{
			var short_ = Build( "citizen_male", height: 60f );
			var tall = Build( "citizen_male", height: 84f );
			Assert( MathF.Abs( short_.Measurements["height"] - 60f ) < 0.5f, $"60-inch build is {short_.Measurements["height"]:0.0} tall" );
			Assert( MathF.Abs( tall.Measurements["height"] - 84f ) < 0.5f, $"84-inch build is {tall.Measurements["height"]:0.0} tall" );
		} );

		// --- adult default colonists (R05: male 1.75 m / 68.9 in, female 1.62 m / 63.78 in, age 21) ---

		Check( "adult male colonist has penis and scrotum, no female anatomy", () =>
		{
			var male = BuildOpts( new Effigy.CharacterDnaOptions
				{ Preset = "citizen_male", Height = 68.9f, Age = 21f } );
			AssertCleanMesh( male, "citizen_male 68.9in age 21" );
			Assert( MathF.Abs( male.Measurements["height"] - 68.9f ) < 0.5f,
				$"male colonist height is {male.Measurements["height"]:0.0}, not 68.9" );
			Assert( RegionCount( male, Effigy.DnaRegion.Penis ) > 0, "male has no penis geometry" );
			Assert( RegionCount( male, Effigy.DnaRegion.Glans ) > 0, "male has no glans geometry" );
			Assert( RegionCount( male, Effigy.DnaRegion.Scrotum ) > 0, "male has no scrotum geometry" );
			Assert( male.Measurements["penis_length"] > 2f && male.Measurements["penis_length"] < 6f,
				$"male penis length is {male.Measurements["penis_length"]:0.00} in, not a neutral adult length" );
			Assert( RegionCount( male, Effigy.DnaRegion.Breast ) == 0, "male has breast geometry" );
			Assert( RegionCount( male, Effigy.DnaRegion.Nipple ) == 0, "male has nipple geometry" );
			Assert( RegionCount( male, Effigy.DnaRegion.Vulva ) == 0, "male has vulva geometry" );
			Assert( RegionCount( male, Effigy.DnaRegion.Labia ) == 0, "male has labia geometry" );
			Assert( RegionCount( male, Effigy.DnaRegion.Buttocks ) > 0, "male has no buttocks region" );
			Assert( male.Landmarks.ContainsKey( "penis_tip" ), "male has no penis_tip landmark" );
			Assert( male.Landmarks.ContainsKey( "scrotum_center" ), "male has no scrotum_center landmark" );
		} );

		Check( "adult female colonist has breasts and vulva, no male anatomy", () =>
		{
			var female = BuildOpts( new Effigy.CharacterDnaOptions
				{ Preset = "citizen_female", Height = 63.78f, Age = 21f } );
			AssertCleanMesh( female, "citizen_female 63.78in age 21" );
			Assert( MathF.Abs( female.Measurements["height"] - 63.78f ) < 0.5f,
				$"female colonist height is {female.Measurements["height"]:0.0}, not 63.78" );
			Assert( RegionCount( female, Effigy.DnaRegion.Breast ) > 0, "female has no breast geometry" );
			Assert( RegionCount( female, Effigy.DnaRegion.Nipple ) > 0, "female has no nipple geometry" );
			Assert( RegionCount( female, Effigy.DnaRegion.Vulva ) > 0, "female has no vulva geometry" );
			Assert( RegionCount( female, Effigy.DnaRegion.Labia ) > 0, "female has no labia geometry" );
			Assert( female.Measurements["breast_projection"] > 1f,
				$"female breast projection is {female.Measurements["breast_projection"]:0.00} in, not a real breast" );
			Assert( RegionCount( female, Effigy.DnaRegion.Penis ) == 0, "female has penis geometry" );
			Assert( RegionCount( female, Effigy.DnaRegion.Scrotum ) == 0, "female has scrotum geometry" );
			Assert( RegionCount( female, Effigy.DnaRegion.Glans ) == 0, "female has glans geometry" );
			Assert( RegionCount( female, Effigy.DnaRegion.Buttocks ) > 0, "female has no buttocks region" );
			Assert( female.Landmarks.ContainsKey( "breast_L" ) && female.Landmarks.ContainsKey( "breast_R" ),
				"female has no breast landmarks" );
			Assert( female.Landmarks.ContainsKey( "vulva_center" ), "female has no vulva_center landmark" );
		} );

		Check( "sex anatomy is adult-only: under-18 builds have none", () =>
		{
			foreach ( var preset in new[] { "citizen_male", "citizen_female" } )
			{
				var young = BuildOpts( new Effigy.CharacterDnaOptions
					{ Preset = preset, Height = 54f, Age = 10f } );
				AssertCleanMesh( young, $"{preset} age 10" );

				foreach ( var r in new[]
				{
					Effigy.DnaRegion.Penis, Effigy.DnaRegion.Glans, Effigy.DnaRegion.Scrotum,
					Effigy.DnaRegion.Breast, Effigy.DnaRegion.Nipple,
					Effigy.DnaRegion.Vulva, Effigy.DnaRegion.Labia,
				} )
					Assert( RegionCount( young, r ) == 0,
						$"{preset} age 10 has {r} geometry ({RegionCount( young, r )} vertices)" );

				// The rest of the body still builds: toes, fingers, buttocks.
				Assert( RegionCount( young, Effigy.DnaRegion.Toe ) > 0, $"{preset} age 10 has no toes" );
				Assert( RegionCount( young, Effigy.DnaRegion.Buttocks ) > 0, $"{preset} age 10 has no buttocks" );
			}
		} );

		Check( "limbs are segmented: toes, elbows, knees, ankles, per-finger regions", () =>
		{
			var male = BuildOpts( new Effigy.CharacterDnaOptions
				{ Preset = "citizen_male", Height = 68.9f, Age = 21f } );
			// 10 toes x 3 stations x 6 sides = 180 toe vertices.
			Assert( RegionCount( male, Effigy.DnaRegion.Toe ) >= 100,
				$"male has only {RegionCount( male, Effigy.DnaRegion.Toe )} toe vertices" );
			Assert( RegionCount( male, Effigy.DnaRegion.Elbow ) > 0, "male has no elbow region" );
			Assert( RegionCount( male, Effigy.DnaRegion.Knee ) > 0, "male has no knee region" );
			Assert( RegionCount( male, Effigy.DnaRegion.Ankle ) > 0, "male has no ankle region" );

			foreach ( var finger in new[]
			{
				Effigy.DnaRegion.Thumb, Effigy.DnaRegion.IndexFinger,
				Effigy.DnaRegion.MiddleFinger, Effigy.DnaRegion.RingFinger,
			} )
				Assert( RegionCount( male, finger ) > 0, $"male has no {finger} region" );

			// Citizen has no pinky bone, so no pinky mesh either.
			Assert( RegionCount( male, Effigy.DnaRegion.PinkyFinger ) == 0,
				"citizen male has pinky geometry but no pinky bone" );
		} );

		Check( "bust and genital sliders move the anatomy", () =>
		{
			var flat = BuildOpts( new Effigy.CharacterDnaOptions
				{ Preset = "citizen_female", Height = 63.78f, Age = 21f, Bust = 0f } );
			var full = BuildOpts( new Effigy.CharacterDnaOptions
				{ Preset = "citizen_female", Height = 63.78f, Age = 21f, Bust = 0.9f } );
			Assert( full.Measurements["breast_projection"] > flat.Measurements["breast_projection"] * 1.3f,
				$"breast projection {flat.Measurements["breast_projection"]:0.00} -> {full.Measurements["breast_projection"]:0.00} did not grow with bust" );

			var small = BuildOpts( new Effigy.CharacterDnaOptions
				{ Preset = "citizen_male", Height = 68.9f, Age = 21f, GenitalSize = 0f } );
			var large = BuildOpts( new Effigy.CharacterDnaOptions
				{ Preset = "citizen_male", Height = 68.9f, Age = 21f, GenitalSize = 1f } );
			Assert( large.Measurements["penis_length"] > small.Measurements["penis_length"] * 1.3f,
				$"penis length {small.Measurements["penis_length"]:0.00} -> {large.Measurements["penis_length"]:0.00} did not grow with genital size" );
			Assert( MathF.Abs( large.Measurements["height"] - small.Measurements["height"] ) < 0.01f,
				"genital size changed the height" );
		} );

		Check( "neutral archetypes stay smooth: no citizen sex anatomy", () =>
		{
			foreach ( var preset in new[] { "realistic_human", "stylized_hero", "anime_base", "heavy_brute" } )
			{
				var build = Build( preset );

				foreach ( var r in new[]
				{
					Effigy.DnaRegion.Penis, Effigy.DnaRegion.Glans, Effigy.DnaRegion.Scrotum,
					Effigy.DnaRegion.Breast, Effigy.DnaRegion.Nipple,
					Effigy.DnaRegion.Vulva, Effigy.DnaRegion.Labia,
				} )
					Assert( RegionCount( build, r ) == 0,
						$"{preset} has {r} geometry ({RegionCount( build, r )} vertices) but no known sex" );
			}
		} );

		Check( "add Character verb accepts bust and genital size", () =>
		{
			var studio = new Effigy.PartStudio();
			var r = Effigy.BuildScript.Run( studio,
				"add Character name=Colonist preset=citizen_female height=63.78 age=21 bust=0.7 genital_size=0.5" );
			Assert( r.Ok, $"add Character with bust/genital_size failed: {r.Error}" );
			Assert( studio.Bodies.Count == 1, $"add Character made {studio.Bodies.Count} bodies, not 1" );
			Assert( studio.Rig.Count == 95, $"studio rig has {studio.Rig.Count} bones, not 95" );
			Assert( studio.Bodies[0].Mesh.IsRigged, "verb-built female mesh is not rigged" );
		} );
	}
}
