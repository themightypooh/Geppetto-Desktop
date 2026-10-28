#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using Marionette.Desktop;
using Effigy;

namespace Geppetto;

/// <summary>
/// G10 headless test body, driven by tools/pose_ik_test.gd. Every check compares what Godot's
/// Skeleton3D reports (its own forward kinematics, GetBoneGlobalPose) against the kernel's answer
/// computed independently in kernel space and converted with the skeleton bridge's transform.
/// </summary>
public partial class EffigyPoseIkTest : RefCounted
{
	readonly Godot.Collections.Array _checks = new();
	int _failed;

	/// <summary>Run everything. <paramref name="spikeRoot"/>/<paramref name="armRoot"/> are the
	/// importer's scenes for the two res:// paths (already in the tree).</summary>
	public Godot.Collections.Dictionary Run( Node3D spikeRoot, string spikePath, Node3D armRoot, string armPath )
	{
		try
		{
			ClipPose( spikeRoot, spikePath );
			IkAndAutoKey( armRoot, armPath );
		}
		catch ( Exception ex )
		{
			Add( "exception", false, -1, ex.ToString() );
		}

		return new Godot.Collections.Dictionary
		{
			{ "checks", _checks },
			{ "failed", _failed },
			{ "total", _checks.Count },
		};
	}

	// ---- 1. sampled clip pose and rig pose ------------------------------------------------------

	void ClipPose( Node3D root, string path )
	{
		var (studio, disk) = Load( path );
		var rig = studio.Rig;
		var skel = root.GetNode<Skeleton3D>( "Skeleton3D" );
		string clipFile = Path.Combine( Path.GetDirectoryName( disk )!, Path.GetFileNameWithoutExtension( disk ) + "_spin.riganim" );
		var clip = RigAnimFile.ReadFile( clipFile );

		// Rig (bind) pose: Godot's global pose must equal its global rest and the kernel's bind pose.
		EffigyPoseBridge.ApplyRestPose( rig, skel );
		var bind = RigAnimFile.ModelSpace( rig, EffigyPoseBridge.RestPose( rig ) );
		float e0 = CompareGlobal( rig, skel, bind, out var w0 );
		Add( "rest pose global = kernel bind", e0 <= 1e-5f, e0, w0 );

		// Clip at several times, including fractional frames.
		foreach ( double t in new[] { 0.0, 0.25, 0.37, 1.0 } )
		{
			var local = EffigyPoseBridge.ApplyClipPose( clip, rig, skel, t );
			var world = RigAnimFile.ModelSpace( rig, local );
			float err = CompareGlobal( rig, skel, world, out var detail );
			Add( $"clip spin @ {t:0.###}s global = kernel", err <= 1e-5f, err, detail );

			// Round trip: Godot pose read back to kernel space equals what was written.
			var back = EffigyPoseBridge.ReadLocalPose( rig, skel );
			float rt = 0f;
			for ( int b = 0; b < rig.Count; b++ ) rt = MathF.Max( rt, XformErr( back[b], local[b] ) );
			Add( $"clip spin @ {t:0.###}s readback = kernel local (inches)", rt <= 1e-3f, rt );
		}

		Add( "imported skeleton is inside the scene tree", skel.IsInsideTree(), 0 );

		// Same on a Skeleton3D that is NOT in the scene tree (Godot skips dirty-marking there).
		var loose = EffigySkeletonBridge.ToGodotSkeleton( rig );
		try
		{
			EffigyPoseBridge.ApplyClipPose( clip, rig, loose, 0.0 );
			loose.GetBoneGlobalPose( 0 ); // computes and caches the t=0 globals
			var looseLocal = EffigyPoseBridge.ApplyClipPose( clip, rig, loose, 0.37 );
			float le = CompareGlobal( rig, loose, RigAnimFile.ModelSpace( rig, looseLocal ), out var lw );
			Add( "clip spin @ 0.37s on out-of-tree Skeleton3D global = kernel", le <= 1e-5f, le, lw );
		}
		finally
		{
			loose.Free();
		}

		// Cross-check with the importer's baked AnimationPlayer at a whole frame.
		var player = root.GetNodeOrNull<AnimationPlayer>( "AnimationPlayer" );
		if ( player != null && player.HasAnimation( "spin" ) )
		{
			var anim = player.GetAnimation( "spin" );
			int frame = 6;
			double t = frame / (double)(clip.FrameRate > 0 ? clip.FrameRate : 30);
			EffigyPoseBridge.ApplyClipPose( clip, rig, skel, t );
			float err = 0f;
			for ( int b = 0; b < rig.Count; b++ )
			{
				string trackPath = $"Skeleton3D:{rig.Bones[b].Name}";
				int pt = anim.FindTrack( trackPath, Animation.TrackType.Position3D );
				int rt = anim.FindTrack( trackPath, Animation.TrackType.Rotation3D );
				int gi = skel.FindBone( rig.Bones[b].Name );
				err = MathF.Max( err, (anim.PositionTrackInterpolate( pt, t ) - skel.GetBonePosePosition( gi )).Length() );
				var qa = anim.RotationTrackInterpolate( rt, t );
				var qb = skel.GetBonePoseRotation( gi );
				err = MathF.Max( err, 1f - MathF.Abs( qa.Dot( qb ) ) );
			}
			Add( "clip pose @ frame 6 = imported AnimationPlayer track", err <= 1e-5f, err );
		}
		else
		{
			Add( "imported AnimationPlayer has 'spin'", false, -1, "missing" );
		}
	}

	// ---- 2. IK solves and 3. auto-key -----------------------------------------------------------

	void IkAndAutoKey( Node3D root, string path )
	{
		var (studio, disk) = Load( path );
		var rig = studio.Rig;
		var skel = root.GetNode<Skeleton3D>( "Skeleton3D" );
		var bindWorld = RigAnimFile.ModelSpace( rig, EffigyPoseBridge.RestPose( rig ) );

		// Two-bone on Hand (Upper -> Lower -> Hand).
		var targetE = new Vec3( 14f, 3f, 30f );
		var poleE = new Vec3( 0f, 0f, 1f );
		EffigyPoseBridge.ApplyRestPose( rig, skel );
		var r2 = EffigyIkBridge.SolveTwoBone( rig, skel, "Hand",
			EffigyPoseBridge.ToGodotPosition( targetE ), EffigyPoseBridge.ToGodotDirection( poleE ) );
		Add( "two-bone solve succeeded", r2.Success, r2.MissDistance, r2.Diagnostic ?? "" );
		if ( r2.Success )
		{
			var kernel = TwoBoneIk.Apply( rig, bindWorld, rig.IndexOf( "Hand" ), targetE, poleE, 0f );
			float err = CompareGlobal( rig, skel, kernel, out var d );
			Add( "two-bone Godot global pose = kernel TwoBoneIk.Apply", err <= 1e-4f, err, d );
			float reach = (skel.GetBoneGlobalPose( skel.FindBone( "Hand" ) ).Origin - EffigyPoseBridge.ToGodotPosition( targetE )).Length();
			Add( "two-bone Hand reaches Godot target (m)", reach <= 1e-4f, reach );
			Add( "two-bone bone lengths kept (m)", LengthErr( rig, skel ) <= 1e-5f, LengthErr( rig, skel ) );
		}

		// CCD and FABRIK on Finger with 3 bones above it (Upper, Lower, Hand turn).
		foreach ( var solver in new[] { EffigyIkSolver.Ccd, EffigyIkSolver.Fabrik } )
		{
			var tE = new Vec3( 18f, -4f, 28f );
			EffigyPoseBridge.ApplyRestPose( rig, skel );
			var rc = EffigyIkBridge.SolveChain( rig, skel, "Finger", 3, EffigyPoseBridge.ToGodotPosition( tE ), solver, iterations: 20 );
			Add( $"{solver} solve succeeded", rc.Success, rc.MissDistance, rc.Diagnostic ?? "" );
			if ( !rc.Success ) continue;

			var ik = new RigIk { EndBone = "Finger", ChainLength = 3, Iterations = 20, Fabrik = solver == EffigyIkSolver.Fabrik, GoalRotation = false };
			var kernel = ChainIk.Apply( rig, bindWorld, ik, new RigPose( tE, Quat.Identity, Vec3.One ) );
			float err = CompareGlobal( rig, skel, kernel, out var d );
			Add( $"{solver} Godot global pose = kernel ChainIk.Apply", err <= 5e-4f, err, d );
			float reach = (skel.GetBoneGlobalPose( skel.FindBone( "Finger" ) ).Origin - EffigyPoseBridge.ToGodotPosition( rc.SolvedEndPosition )).Length();
			Add( $"{solver} Finger lands where the kernel solve put it (m)", reach <= 1e-5f, reach,
				$"kernel miss to target {rc.MissDistance:0.####} in" );
			// CCD creeps toward a goal and stops after its passes; FABRIK should land on it.
			float allowed = solver == EffigyIkSolver.Fabrik ? 0.01f : 0.25f;
			Add( $"{solver} kernel miss to target within {allowed} in", rc.MissDistance <= allowed, rc.MissDistance );
			Add( $"{solver} bone lengths kept (m)", LengthErr( rig, skel ) <= 1e-5f, LengthErr( rig, skel ) );
		}

		// Auto-key an IK result as one undo step, with Godot UndoRedo in the loop.
		var clip = new RigAnimFile { FrameRate = 30, FrameCount = 1 };
		var session = DocumentSession.CreateEffigy( disk, studio, clip: clip );
		var godotUndo = new UndoRedo();
		try
		{
			var adapter = new EffigyUndoRedoAdapter( session, godotUndo );
			var keyer = new EffigyAutoKey( session, adapter, rig );

			EffigyPoseBridge.ApplyRestPose( rig, skel );
			var solved = EffigyIkBridge.SolveTwoBone( rig, skel, "Hand",
				EffigyPoseBridge.ToGodotPosition( targetE ), EffigyPoseBridge.ToGodotDirection( poleE ) );
			int frame = keyer.FrameAtTime( 10 / 30.0 );
			bool keyed = keyer.KeyIkChain( skel, "Hand", 2, frame );
			int keyedTracks = clip.BoneTracks.Count( t => t.Keys.Any( k => k.Frame == frame ) );
			Add( "auto-key IK chain: 3 bone tracks keyed at frame 10, one history entry",
				solved.Success && keyed && frame == 10 && keyedTracks == 3 && session.History.UndoCount == 1 && godotUndo.GetHistoryCount() == 1,
				keyedTracks, $"frame={frame} geppettoUndo={session.History.UndoCount} godotUndo={godotUndo.GetHistoryCount()}" );

			// The clip now samples back to the solved pose.
			var sampled = RigAnimFile.ModelSpace( rig, clip.SampleLocal( rig, frame ) );
			float err = CompareGlobal( rig, skel, sampled, out var d );
			Add( "keyed clip samples back to Godot pose", err <= 1e-4f, err, d );

			adapter.Undo();
			int afterUndo = clip.BoneTracks.Count( t => t.Keys.Any( k => k.Frame == frame ) );
			Add( "Godot undo removes all 3 keys in one step", afterUndo == 0 && clip.FrameCount == 1, afterUndo, $"frameCount={clip.FrameCount}" );
			adapter.Redo();
			int afterRedo = clip.BoneTracks.Count( t => t.Keys.Any( k => k.Frame == frame ) );
			Add( "Godot redo restores all 3 keys", afterRedo == 3 && clip.FrameCount == 11, afterRedo, $"frameCount={clip.FrameCount}" );
		}
		finally
		{
			godotUndo.Free();
		}

		// Auto-key only what changed, Geppetto EditHistory only (no Godot UndoRedo).
		var clip2 = new RigAnimFile { FrameRate = 30, FrameCount = 1 };
		var session2 = DocumentSession.CreateEffigy( disk, studio, clip: clip2 );
		var keyer2 = new EffigyAutoKey( session2, new EffigyUndoRedoAdapter( session2 ), rig );
		EffigyPoseBridge.ApplyRestPose( rig, skel );
		var before = EffigyPoseBridge.ReadLocalPose( rig, skel );
		int lower = skel.FindBone( "Lower" );
		skel.SetBonePoseRotation( lower, skel.GetBonePoseRotation( lower ) * new Quaternion( Vector3.Right, 0.4f ) );
		var changed = keyer2.KeyChangedBones( skel, before, 5 );
		var key = clip2.Find( "Lower" )?.Keys.FirstOrDefault( k => k.Frame == 5 );
		float kerr = key is null ? -1 : XformErr( key.Local.ToXform(), EffigyPoseBridge.ReadLocalPose( rig, skel )[rig.IndexOf( "Lower" )] );
		Add( "manual bone change auto-keys only that bone", changed.Count == 1 && changed[0] == "Lower" && key != null && kerr <= 1e-4f, kerr,
			string.Join( ",", changed ) );
		session2.History.Undo();
		Add( "EditHistory undo removes the manual key", clip2.Find( "Lower" )?.Keys.Count is null or 0 && !session2.History.CanUndo, 0 );
	}

	// ---- helpers --------------------------------------------------------------------------------

	static (PartStudio studio, string disk) Load( string resPath )
	{
		string disk = Path.GetFullPath( ProjectSettings.GlobalizePath( resPath ) );
		var studio = StudioDocument.ReadFile( disk );
		studio.Rebuild();
		if ( studio.Rig == null || studio.Rig.Count == 0 )
			throw new InvalidOperationException( $"{resPath} has no rig." );
		return (studio, disk);
	}

	/// <summary>Max over bones of |Godot global pose - converted kernel model pose| (metres for
	/// origins, unitless for basis columns).</summary>
	static float CompareGlobal( Skeleton rig, Skeleton3D skel, IReadOnlyList<Xform> kernelWorld, out string worst )
	{
		float max = 0f;
		worst = "";
		for ( int b = 0; b < rig.Count; b++ )
		{
			int gi = skel.FindBone( rig.Bones[b].Name );
			var g = skel.GetBoneGlobalPose( gi );
			var k = EffigySkeletonBridge.TransformXform( kernelWorld[b] );
			float e = (g.Origin - k.Origin).Length();
			e = MathF.Max( e, (g.Basis.Column0 - k.Basis.Column0).Length() );
			e = MathF.Max( e, (g.Basis.Column1 - k.Basis.Column1).Length() );
			e = MathF.Max( e, (g.Basis.Column2 - k.Basis.Column2).Length() );
			if ( e > max ) { max = e; worst = rig.Bones[b].Name; }
		}
		return max;
	}

	/// <summary>Max over bones of |posed bone length - bind bone length| in metres.</summary>
	static float LengthErr( Skeleton rig, Skeleton3D skel )
	{
		float max = 0f;
		for ( int b = 0; b < rig.Count; b++ )
		{
			int p = rig.Bones[b].Parent;
			if ( p < 0 ) continue;
			int gi = skel.FindBone( rig.Bones[b].Name ), gp = skel.FindBone( rig.Bones[p].Name );
			float posed = (skel.GetBoneGlobalPose( gi ).Origin - skel.GetBoneGlobalPose( gp ).Origin).Length();
			float rest = (skel.GetBoneGlobalRest( gi ).Origin - skel.GetBoneGlobalRest( gp ).Origin).Length();
			max = MathF.Max( max, MathF.Abs( posed - rest ) );
		}
		return max;
	}

	static float XformErr( in Xform a, in Xform b ) => MathF.Max(
		MathF.Max( (a.Origin - b.Origin).Length, (a.X - b.X).Length ),
		MathF.Max( (a.Y - b.Y).Length, (a.Z - b.Z).Length ) );

	void Add( string name, bool ok, double value, string detail = "" )
	{
		var d = new Godot.Collections.Dictionary { { "name", name }, { "ok", ok }, { "value", value } };
		if ( !string.IsNullOrEmpty( detail ) ) d["detail"] = detail;
		_checks.Add( d );
		if ( !ok ) _failed++;
	}
}
