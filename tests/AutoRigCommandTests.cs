#nullable enable
using System.Diagnostics;
using Effigy;
using Marionette.Desktop;

/// <summary>
/// AutoRigger through its real surfaces: <see cref="AutoRigger.Rig"/> /
/// <see cref="AutoRigger.TryRig"/> (mesh in, Skeleton + SkinWeights out) and
/// <see cref="AutoRigger.TryRigStudio"/> (the `rig auto` bot command: installs
/// the skeleton on the studio; the AiSuiteTools route is the existing
/// `rig_command` tool, which already allows the `rig` verb). The humanoid
/// template is pinned to the CitizenBoneMap names. Registration:
/// AutoRigCommandTests.Register in DesktopTestHost.Run (TestRegistry.cs,
/// coordinator-owned).
/// </summary>
internal static class AutoRigCommandTests
{
	/// <summary>Closed Z-axis cylinder from z0 to z0+height, centred at (cx, cy). Winding is not guaranteed; fixture geometry for bounds/weights only.</summary>
	static void AppendZCylinder( PolyMesh mesh, float cx, float cy, float z0, float height, float radius, int segments )
	{
		int rings = (int)MathF.Round( height ) + 1;
		int start = mesh.VertexCount;

		for ( int i = 0; i < rings; i++ )
			for ( int j = 0; j < segments; j++ )
			{
				float a = MathF.Tau * j / segments;
				mesh.AddVertex( new Vec3( cx + radius * MathF.Cos( a ), cy + radius * MathF.Sin( a ), z0 + height * i / (rings - 1) ) );
			}

		int bottom = mesh.AddVertex( new Vec3( cx, cy, z0 ) );
		int top = mesh.AddVertex( new Vec3( cx, cy, z0 + height ) );
		int V( int i, int j ) => start + i * segments + ((j % segments) + segments) % segments;

		for ( int i = 0; i < rings - 1; i++ )
			for ( int j = 0; j < segments; j++ )
				mesh.AddFace( new[] { V( i, j ), V( i, j + 1 ), V( i + 1, j + 1 ), V( i + 1, j ) } );

		for ( int j = 0; j < segments; j++ )
		{
			mesh.AddFace( new[] { bottom, V( 0, j + 1 ), V( 0, j ) } );
			mesh.AddFace( new[] { top, V( rings - 1, j ), V( rings - 1, j + 1 ) } );
		}
	}

	/// <summary>Closed X-axis cylinder from x0 to x0+length, centred at (cy, cz) in the YZ plane. Bounds/weights fixture only.</summary>
	static void AppendXCylinder( PolyMesh mesh, float cy, float cz, float x0, float length, float radius, int segments )
	{
		int rings = (int)MathF.Round( length ) + 1;
		int start = mesh.VertexCount;

		for ( int i = 0; i < rings; i++ )
			for ( int j = 0; j < segments; j++ )
			{
				float a = MathF.Tau * j / segments;
				mesh.AddVertex( new Vec3( x0 + length * i / (rings - 1), cy + radius * MathF.Cos( a ), cz + radius * MathF.Sin( a ) ) );
			}

		int startCap = mesh.AddVertex( new Vec3( x0, cy, cz ) );
		int endCap = mesh.AddVertex( new Vec3( x0 + length, cy, cz ) );
		int V( int i, int j ) => start + i * segments + ((j % segments) + segments) % segments;

		for ( int i = 0; i < rings - 1; i++ )
			for ( int j = 0; j < segments; j++ )
				mesh.AddFace( new[] { V( i, j ), V( i + 1, j ), V( i + 1, j + 1 ), V( i, j + 1 ) } );

		for ( int j = 0; j < segments; j++ )
		{
			mesh.AddFace( new[] { startCap, V( 0, j ), V( 0, j + 1 ) } );
			mesh.AddFace( new[] { endCap, V( rings - 1, j + 1 ), V( rings - 1, j ) } );
		}
	}

	/// <summary>
	/// A biped from boxes and cylinders, Z-up, 64 tall: torso cube, head cube,
	/// two hanging arm cylinders, two leg cylinders. Bounds x ±10.5, y ±6,
	/// z 0..64 — tall and slender in depth, so it reads as humanoid.
	/// </summary>
	static PolyMesh BipedMesh()
	{
		var mesh = new PolyMesh();
		AutoWeightTests.AppendBox( mesh, new Vec3( 0, 0, 40 ), 6 );
		AutoWeightTests.AppendBox( mesh, new Vec3( 0, 0, 60 ), 4 );
		AppendZCylinder( mesh, -8.5f, 0, 20, 30, 2, 8 );
		AppendZCylinder( mesh, 8.5f, 0, 20, 30, 2, 8 );
		AppendZCylinder( mesh, -3, 0, 0, 34, 2.5f, 8 );
		AppendZCylinder( mesh, 3, 0, 0, 34, 2.5f, 8 );
		return mesh;
	}

	/// <summary>A quadruped: a long horizontal body cylinder with four short leg cylinders. Longest span is its length, so it is not a biped.</summary>
	static PolyMesh QuadrupedMesh()
	{
		var mesh = new PolyMesh();
		AppendXCylinder( mesh, 0, 14, -18, 36, 5, 12 );
		AppendZCylinder( mesh, -12, -3, 0, 9, 1.5f, 8 );
		AppendZCylinder( mesh, -12, 3, 0, 9, 1.5f, 8 );
		AppendZCylinder( mesh, 12, -3, 0, 9, 1.5f, 8 );
		AppendZCylinder( mesh, 12, 3, 0, 9, 1.5f, 8 );
		return mesh;
	}

	static int NearestVertex( PolyMesh mesh, Vec3 point )
	{
		var best = 0;
		var bestD = float.MaxValue;

		for ( var v = 0; v < mesh.VertexCount; v++ )
		{
			var d = (mesh.Positions[v] - point).LengthSquared;

			if ( d < bestD )
			{
				bestD = d;
				best = v;
			}
		}

		return best;
	}

	static string DominantBone( SkinWeights weights, Skeleton rig, int vertex ) =>
		rig.Bones[weights[vertex].OrderByDescending( w => w.Weight ).First().Bone].Name;

	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "auto-rig-command", name, test );
		void Require( bool condition, string message ) { if ( !condition ) throw new Exception( message ); }
		void Throws<T>( Action action, string what ) where T : Exception
		{
			try { action(); }
			catch ( T ) { return; }
			catch ( Exception ex ) { throw new Exception( $"{what}: expected {typeof( T ).Name}, got {ex.GetType().Name}: {ex.Message}" ); }
			throw new Exception( $"{what}: expected {typeof( T ).Name}, got silent success" );
		}
		void RequireValid( SkinWeights w, PolyMesh mesh, Skeleton rig )
		{
			var problems = w.Validate( mesh.VertexCount, rig.Count );
			Require( problems.Count == 0, "invalid weights: " + string.Join( "; ", problems.Take( 5 ) ) );

			for ( int v = 0; v < w.Count; v++ )
				Require( MathF.Abs( w[v].Sum( x => x.Weight ) - 1f ) < 1e-4f, $"vertex {v} sums to {w[v].Sum( x => x.Weight )}" );
		}

		Check( "bounds rig: Root + Tip, weights sum to 1", () =>
		{
			var mesh = new PolyMesh();
			AutoWeightTests.AppendBox( mesh, new Vec3( 0, 0, 0 ), 5 );

			var result = AutoRigger.Rig( mesh );
			Require( result.Template == "bounds", "a cube is not a biped: " + result.Template );
			Require( result.Skeleton.Count == 2, $"bounds rig has {result.Skeleton.Count} bones, expected 2" );
			Require( result.Skeleton.Bones[0].Name == "Root" && result.Skeleton.Bones[1].Name == "Tip", "bounds bones are not Root + Tip" );
			Require( result.Skeleton.Bones[1].Parent == 0, "Tip is not a child of Root" );
			RequireValid( result.Weights, mesh, result.Skeleton );
		} );

		Check( "empty and degenerate meshes are refused honestly", () =>
		{
			Require( !AutoRigger.TryRig( new PolyMesh(), out _, out var whyEmpty ), "an empty mesh rigged" );
			Require( whyEmpty.Contains( "empty" ), "empty refusal does not say empty: " + whyEmpty );
			Require( !AutoRigger.TryRig( null!, out _, out var whyNull ), "a null mesh rigged" );
			Throws<ArgumentException>( () => AutoRigger.Rig( new PolyMesh() ), "Rig on an empty mesh" );

			var point = new PolyMesh();
			point.AddVertex( new Vec3( 1, 2, 3 ) );
			point.AddVertex( new Vec3( 1, 2, 3 ) );
			point.AddVertex( new Vec3( 1, 2, 3 ) );
			point.AddFace( new[] { 0, 1, 2 } );
			Require( !AutoRigger.TryRig( point, out _, out var whySame ), "a single-point mesh rigged" );
			Require( whySame.Contains( "same point" ), "degenerate refusal does not say why: " + whySame );
		} );

		Check( "template names come through CitizenBoneMap", () =>
		{
			var map = CitizenBoneMap.UnrealStyle();
			Require( map["upperarm_L"] == "arm_upper_L", "upperarm_L did not resolve to arm_upper_L" );
			Require( map["thigh_L"] == "leg_upper_L", "thigh_L did not resolve to leg_upper_L" );
			Require( map["calf_L"] == "leg_lower_L", "calf_L did not resolve to leg_lower_L" );
			Require( map["foot_L"] == "ankle_L", "foot_L did not resolve to ankle_L" );

			var template = CitizenBoneMap.HumanoidTemplateBones();
			Require( template.Count == 22, $"humanoid template has {template.Count} bones, expected 22" );

			var seen = new HashSet<string>( StringComparer.Ordinal );

			foreach ( var (bone, parent) in template )
			{
				Require( parent.Length == 0 || seen.Contains( parent ), $"template bone {bone} comes before its parent {parent}" );
				Require( map.ContainsKey( bone ), $"template bone {bone} has no citizen name in UnrealStyle" );
				seen.Add( bone );
			}
		} );

		Check( "humanoid biped rigs to a Citizen-named skeleton at plausible joints", () =>
		{
			var mesh = BipedMesh();
			Require( AutoRigger.LooksHumanoid( mesh ), "the biped fixture does not read as humanoid" );

			var result = AutoRigger.Rig( mesh );
			Require( result.Template == "humanoid", "biped did not take the humanoid path: " + result.Template );
			Require( result.Humanoid, "Humanoid flag is false on a humanoid rig" );

			var rig = result.Skeleton;
			var names = rig.Bones.Select( b => b.Name ).ToHashSet();
			foreach ( var want in new[] { "pelvis", "spine_0", "spine_1", "spine_2", "neck_0", "head",
				"clavicle_L", "clavicle_R", "arm_upper_L", "arm_upper_R", "arm_lower_L", "arm_lower_R",
				"hand_L", "hand_R", "leg_upper_L", "leg_upper_R", "leg_lower_L", "leg_lower_R",
				"ankle_L", "ankle_R", "ball_L", "ball_R" } )
				Require( names.Contains( want ), "humanoid rig is missing bone " + want );

			Require( rig.Bones[rig.IndexOf( "spine_0" )].Parent == rig.IndexOf( "pelvis" ), "spine_0 is not a child of pelvis" );
			Require( rig.Bones[rig.IndexOf( "arm_upper_L" )].Parent == rig.IndexOf( "clavicle_L" ), "arm_upper_L is not a child of clavicle_L" );
			Require( rig.Bones[rig.IndexOf( "leg_upper_L" )].Parent == rig.IndexOf( "pelvis" ), "leg_upper_L is not a child of pelvis" );

			// 64 tall: pelvis ~32, head ~56, ankle ~2.6.
			var pelvisZ = rig.HeadWorld( rig.IndexOf( "pelvis" ) ).z;
			Require( MathF.Abs( pelvisZ - 32 ) < 6.4f, $"pelvis at z={pelvisZ}, expected ~32" );
			var headZ = rig.HeadWorld( rig.IndexOf( "head" ) ).z;
			Require( MathF.Abs( headZ - 56.3f ) < 6.4f, $"head at z={headZ}, expected ~56" );
			var ankleZ = rig.HeadWorld( rig.IndexOf( "ankle_L" ) ).z;
			Require( ankleZ < 6.4f, $"ankle at z={ankleZ}, expected near the ground" );
			var headTop = rig.TailWorld( rig.IndexOf( "head" ) ).z;
			Require( MathF.Abs( headTop - 64 ) < 1f, $"head top at z={headTop}, expected 64" );

			var shoulderL = rig.HeadWorld( rig.IndexOf( "arm_upper_L" ) ).x;
			var shoulderR = rig.HeadWorld( rig.IndexOf( "arm_upper_R" ) ).x;
			Require( MathF.Abs( shoulderL + shoulderR ) < 1e-3f, $"shoulders are not mirrored: {shoulderL} vs {shoulderR}" );
			// Left is the up x facing side (AutoRigger): for this Z-up, facing-+Y
			// fixture that is -X, so the left shoulder sits at -8.4, not +8.4.
			// The old assertion pinned +8.4 — the same mirrored sign the weights
			// check caught (a left-hand vertex weighted to hand_R).
			Require( shoulderL < -5f && shoulderL > -12f, $"shoulder at x={shoulderL}, expected near the left arm cylinder at -8.5" );

			RequireValid( result.Weights, mesh, rig );
		} );

		Check( "humanoid weights assign vertices to their nearest limb bone", () =>
		{
			var mesh = BipedMesh();
			var result = AutoRigger.Rig( mesh );
			Require( result.Template == "humanoid", "biped did not take the humanoid path" );

			// A vertex at the left hand (arm cylinder, z ~26) belongs to the left arm.
			var handVertex = NearestVertex( mesh, new Vec3( -10.5f, 0, 26 ) );
			var handBone = DominantBone( result.Weights, result.Skeleton, handVertex );
			Require( handBone is "hand_L" or "arm_lower_L" or "arm_upper_L", $"hand vertex is weighted to {handBone}, not the left arm" );

			// A vertex at the left foot (leg cylinder base) belongs to the left leg.
			var footVertex = NearestVertex( mesh, new Vec3( -3, 0, 1 ) );
			var footBone = DominantBone( result.Weights, result.Skeleton, footVertex );
			Require( footBone is "ankle_L" or "ball_L" or "leg_lower_L", $"foot vertex is weighted to {footBone}, not the left leg" );

			// A vertex at the top of the head belongs to the head, not a limb.
			var crownVertex = NearestVertex( mesh, new Vec3( 0, 0, 64 ) );
			var crownBone = DominantBone( result.Weights, result.Skeleton, crownVertex );
			Require( crownBone is "head" or "neck_0", $"crown vertex is weighted to {crownBone}, not the head" );
		} );

		Check( "quadruped mesh falls back to the 2-bone rig", () =>
		{
			var mesh = QuadrupedMesh();
			Require( !AutoRigger.LooksHumanoid( mesh ), "the quadruped fixture reads as humanoid" );
			Require( AutoRigger.RigHumanoid( mesh ) is null, "RigHumanoid returned a biped for a quadruped" );

			var result = AutoRigger.Rig( mesh );
			Require( result.Template == "bounds", "quadruped did not take the bounds fallback: " + result.Template );
			Require( result.Skeleton.Count == 2, $"fallback rig has {result.Skeleton.Count} bones, expected 2" );
			Require( result.Skeleton.Bones[0].Name == "Root" && result.Skeleton.Bones[1].Name == "Tip", "fallback bones are not Root + Tip" );
			RequireValid( result.Weights, mesh, result.Skeleton );
		} );

		Check( "rig auto installs the skeleton on the studio and refuses bad calls", () =>
		{
			var studio = new PartStudio();
			var blob = BuildScript.Run( studio, "add Blob name=Body voxel=1 shapes=\"sphere 0,0,0 10\"" );
			Require( blob.Ok, "blob failed: " + blob.Error );

			Require( AutoRigger.TryRigStudio( studio, "Body", replace: false, out var result, out var why ),
				"rig auto failed: " + why );
			Require( result.Template == "bounds", "a sphere is not a biped: " + result.Template );
			Require( studio.Rig.Count == 2, $"studio rig has {studio.Rig.Count} bones, expected 2" );
			Require( studio.Rig.RootBone == "Root", "studio RootBone is not Root" );
			RequireValid( studio.Bodies[0].Mesh.Skin!, studio.Bodies[0].Mesh, studio.Rig );

			Require( !AutoRigger.TryRigStudio( studio, "Body", replace: false, out _, out var whyAgain ),
				"a second rig auto without replace succeeded" );
			Require( whyAgain.Contains( "already has" ), "second-rig refusal does not say why: " + whyAgain );

			Require( !AutoRigger.TryRigStudio( studio, "Nope", replace: true, out _, out var whyBody ),
				"rig auto on a missing body succeeded" );
			Require( whyBody.Contains( "No body called" ), "missing-body refusal does not name the body: " + whyBody );

			Require( !AutoRigger.TryRigStudio( new PartStudio(), "Body", replace: false, out _, out var whyEmpty ),
				"rig auto on an empty studio succeeded" );
			Require( whyEmpty.Contains( "no bodies" ), "empty-studio refusal does not say why: " + whyEmpty );
		} );
	}
}
