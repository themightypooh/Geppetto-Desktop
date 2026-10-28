using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

// Rigging wave 3 script verbs. These follow the `fix` verb's precedent in
// BuildScript.cs: they act on the studio's currently built meshes/rig and log
// what they did. They are NOT features, so a later rebuild from the feature
// tree does not keep them — the log says so rather than pretending otherwise.
// The library itself (RiggingWave3.cs) is what the GUI wires to for persistence.

namespace Effigy;

public static partial class BuildScript
{
	static bool RigWave3Command( PartStudio studio, string verb, List<string> args, Result r, out bool ok )
	{
		ok = false;
		switch ( verb )
		{
			case "rigcheck": ok = RigCheckVerb( studio, args, r ); return true;
			case "bendy": ok = BendyVerb( studio, args, r ); return true;
			case "dqs": ok = DqsVerb( studio, args, r ); return true;
			case "ikfk": ok = IkFkVerb( studio, args, r ); return true;
			case "twiststations": ok = TwistStationsVerb( studio, args, r ); return true;
			case "fan": ok = FanVerb( studio, args, r ); return true;
			case "bulge": ok = BulgeVerb( studio, args, r ); return true;
			case "posecheck": ok = PoseCheckVerb( studio, args, r ); return true;
			case "deltamush": ok = DeltaMushVerb( studio, args, r ); return true;
			default: return false;
		}
	}

	static bool FindRigBody( PartStudio studio, string name, Result r, out Body body )
	{
		body = null;
		if ( string.IsNullOrWhiteSpace( name ) )
			return Fail( r, "this verb needs a body name first" );
		body = studio.Bodies.FirstOrDefault( b => string.Equals( b.Name ?? b.Id, name, StringComparison.OrdinalIgnoreCase ) );
		if ( body is null )
			return Fail( r, $"No body called '{name}'. Bodies: {string.Join( ", ", studio.Bodies.Select( b => b.Name ?? b.Id ) )}." );
		if ( body.Mesh is null )
			return Fail( r, $"Body '{name}' has no mesh yet." );
		return true;
	}

	static bool FindRigBone( PartStudio studio, string name, Result r, out int index )
	{
		index = -1;
		if ( studio.Rig is not { Count: > 0 } )
			return Fail( r, "this verb needs a rig: build one first (rig human, or bone …)." );
		index = studio.Rig.IndexOf( name );
		if ( index < 0 )
			return Fail( r, $"No bone called '{name}'; bones: {string.Join( ", ", studio.Rig.Bones.Select( b => b.Name ) )}." );
		return true;
	}

	static bool RequireSkin( Body body, Result r )
	{
		if ( body.Mesh.IsRigged ) return true;
		return Fail( r, $"Body '{body.Name}' has no skin weights yet. Bind it first (bind {body.Name} method=heat, or method=smooth)." );
	}

	static bool ParseVec( string text, out Vec3 v )
	{
		v = default;
		if ( string.IsNullOrWhiteSpace( text ) ) return false;
		var p = text.Split( ',', StringSplitOptions.TrimEntries );
		return p.Length == 3
			&& float.TryParse( p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x )
			&& float.TryParse( p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y )
			&& float.TryParse( p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z )
			&& (v = new Vec3( x, y, z )).LengthSquared >= 0f;
	}

	static float Num( Dictionary<string, string> opts, string key, float fallback )
		=> opts.TryGetValue( key, out var s ) && float.TryParse( s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v ) ? v : fallback;

	static Vec3 ParseAxis( PartStudio studio, int bone, Dictionary<string, string> opts )
	{
		if ( opts.TryGetValue( "axis", out var a ) && !a.Equals( "auto", StringComparison.OrdinalIgnoreCase ) )
		{
			if ( a.Equals( "x", StringComparison.OrdinalIgnoreCase ) ) return new Vec3( 1, 0, 0 );
			if ( a.Equals( "y", StringComparison.OrdinalIgnoreCase ) ) return new Vec3( 0, 1, 0 );
			if ( a.Equals( "z", StringComparison.OrdinalIgnoreCase ) ) return new Vec3( 0, 0, 1 );
			if ( ParseVec( a, out var v ) && v.Length > 1e-6f ) return v.Normal;
		}
		return FlexReport.HingeAxis( studio.Rig, bone );
	}

	// P6 + P10: collapse heatmap, Fix weights, NLA verification.
	static bool RigCheckVerb( PartStudio studio, List<string> args, Result r )
	{
		if ( args.Count < 1 || args[0].Contains( '=' ) )
			return Fail( r, "rigcheck needs a body: rigcheck Body [bone=Bone] [angles=90,140] [max=4]" );
		if ( !FindRigBody( studio, args[0], r, out var body ) ) return false;
		if ( !RequireSkin( body, r ) ) return false;
		var opts = Pairs( args.Skip( 1 ) );
		var max = (int)Num( opts, "max", 4 );
		var angles = new List<float> { 90f, 140f };
		if ( opts.TryGetValue( "angles", out var angleText ) )
		{
			angles.Clear();
			foreach ( var part in angleText.Split( ',', StringSplitOptions.TrimEntries ) )
				if ( float.TryParse( part, NumberStyles.Float, CultureInfo.InvariantCulture, out var a ) ) angles.Add( a );
			if ( angles.Count == 0 ) angles.Add( 90f );
		}

		List<int> bones;
		if ( opts.TryGetValue( "bone", out var boneName ) )
		{
			if ( !FindRigBone( studio, boneName, r, out var bi ) ) return false;
			bones = new List<int> { bi };
		}
		else
		{
			bones = FlexReport.DefaultBones( studio.Rig, body.Mesh.Skin );
			if ( bones.Count == 0 )
				return Fail( r, $"No joint of the rig has {body.Name}'s skin on both sides of it, so there is nothing to check. Bind the body to more than one bone, or name bone= to check." );
		}

		foreach ( var b in bones )
		{
			foreach ( var a in angles )
			{
				var loss = CollapseHeatmap.LossPerVertex( body.Mesh.Skin, studio.Rig, b, a );
				var (worst, mean, over) = CollapseHeatmap.Summarize( loss );
				r.Log.Add( $"rigcheck {body.Name} {studio.Rig.Bones[b].Name} at {a:0}°: worst predicted loss {worst:P0}, mean {mean:P1}, {over} vertices over 25% loss (joint ring at w=1/2 reads 29% at 90°, 66% at 140°)." );
			}
		}

		var (fixedWeights, report) = FixWeights.Run( body.Mesh, body.Mesh.Skin, max );
		body.Mesh.Skin = fixedWeights;
		r.Log.Add( $"rigcheck fix weights on {body.Name}: {report}. Written to the built mesh (a later rebuild re-derives weights from the bind feature — this is a runtime operation, not a feature, like fix)." );

		// Fan-bone weight range: fan bones are deform bones, so report their share.
		var fanBones = studio.Rig.Bones.Select( ( b, i ) => (b, i) ).Where( x => x.b.Name.Contains( "_fan", StringComparison.OrdinalIgnoreCase ) ).ToList();
		foreach ( var (bone, i) in fanBones )
		{
			var share = 0f;
			var count = 0;
			for ( var v = 0; v < body.Mesh.Skin.Count; v++ )
			{
				var w = body.Mesh.Skin[v].FirstOrDefault( x => x.Bone == i ).Weight;
				if ( w > 0f ) { share += w; count++; }
			}
			r.Log.Add( $"rigcheck fan bone {bone.Name}: {count} vertices weighted, total share {share:0.##} (a fan bone outside the cap region is weight theft — check these vertices are on the cap)." );
		}

		foreach ( var note in NlaVerification.Run() )
			r.Log.Add( $"rigcheck NLA: {note}" );
		return true;
	}

	// P2: BendyBone deform vs rigid deform at a bend.
	static bool BendyVerb( PartStudio studio, List<string> args, Result r )
	{
		if ( args.Count < 1 || args[0].Contains( '=' ) )
			return Fail( r, "bendy needs a body: bendy Body bone=Bone [joint=Child] [segments=8] [easein=1] [easeout=1] [angle=90] [axis=auto]" );
		if ( !FindRigBody( studio, args[0], r, out var body ) ) return false;
		if ( !RequireSkin( body, r ) ) return false;
		var opts = Pairs( args.Skip( 1 ) );
		if ( !opts.TryGetValue( "bone", out var boneName ) )
			return Fail( r, "bendy needs bone=: bendy Body bone=Spine [angle=90]" );
		if ( !FindRigBone( studio, boneName, r, out var bone ) ) return false;
		// A B-Bone curves when its CHILD joint bends (head frame stays, tail frame
		// turns); bending the bone's own subtree rotates head and tail together, so
		// the bone stays rigid and the verb correctly reports 0. joint= names the
		// joint to bend, defaulting to the bone itself.
		var joint = bone;
		var jointName = boneName;
		if ( opts.TryGetValue( "joint", out var jn ) && !FindRigBone( studio, jn, r, out joint ) ) return false;
		if ( opts.TryGetValue( "joint", out var jn2 ) ) jointName = jn2;
		var settings = new BendyBoneSettings
		{
			Segments = Math.Max( 1, (int)Num( opts, "segments", 8 ) ),
			EaseIn = Num( opts, "easein", 1f ),
			EaseOut = Num( opts, "easeout", 1f ),
		};
		var angle = Num( opts, "angle", 90f );
		var axis = ParseAxis( studio, joint, opts );
		var bind = PoseHelper.BindWorld( studio.Rig );
		var pose = PoseHelper.BendPose( studio.Rig, joint, axis, angle );
		var rigid = SkinBinder.Deform( body.Mesh.Positions, body.Mesh.Skin, bind, pose );
		var bendy = BendyDeform.Deform( body.Mesh.Positions, body.Mesh.Skin, bind, pose, studio.Rig, bone, settings );
		var (total, maxStep) = BendyDeform.KinkMetric( studio.Rig, pose, bone, settings );
		var diff = 0f;
		var maxDiff = 0f;
		for ( var v = 0; v < rigid.Length; v++ )
		{
			var d = (bendy[v] - rigid[v]).Length;
			diff += d;
			maxDiff = MathF.Max( maxDiff, d );
		}
		r.Log.Add( $"bendy {body.Name} bone {boneName} joint {jointName} at {angle:0}°: B-Bone orientation turns {total:0.#}° in total with largest single step {maxStep:0.#}° over {settings.Segments} segments (a rigid joint puts ~all {angle:0}° in one step); bendy vs rigid mean vertex move {diff / Math.Max( 1, rigid.Length ):0.###}, max {maxDiff:0.###}. Unflagged bones deform exactly as before — this verb only bends the named bone." );
		return true;
	}

	// P3: DQS preview numbers + bake-to-corrective, with the runtime warning.
	static bool DqsVerb( PartStudio studio, List<string> args, Result r )
	{
		if ( args.Count < 1 || args[0].Contains( '=' ) )
			return Fail( r, "dqs needs a body: dqs Body bone=Bone [angle=90] [axis=auto]" );
		if ( !FindRigBody( studio, args[0], r, out var body ) ) return false;
		if ( !RequireSkin( body, r ) ) return false;
		var opts = Pairs( args.Skip( 1 ) );
		if ( !opts.TryGetValue( "bone", out var boneName ) )
			return Fail( r, "dqs needs bone=: dqs Body bone=Forearm [angle=90]" );
		if ( !FindRigBone( studio, boneName, r, out var bone ) ) return false;
		var angle = Num( opts, "angle", 90f );
		var axis = ParseAxis( studio, bone, opts );
		var bind = PoseHelper.BindWorld( studio.Rig );
		var pose = PoseHelper.BendPose( studio.Rig, bone, axis, angle );
		var lbs = SkinBinder.Deform( body.Mesh.Positions, body.Mesh.Skin, bind, pose );
		var dqs = DualQuaternionSkinning.Deform( body.Mesh.Positions, body.Mesh.Skin, bind, pose, out var scaledBones, out var scaledVertices );
		var joint = studio.Rig.HeadWorld( bone );
		// Joint ring: vertices whose bend weight is in the blend zone.
		var subtree = CorrectiveMorph.Subtree( studio.Rig, bone );
		var bendW = CorrectiveMorph.BendWeights( body.Mesh.Skin, subtree, body.Mesh.VertexCount );
		var ringBind = new List<Vec3>();
		var ringLbs = new List<Vec3>();
		var ringDqs = new List<Vec3>();
		for ( var v = 0; v < bendW.Length; v++ )
		{
			if ( bendW[v] < 0.3f || bendW[v] > 0.7f ) continue;
			ringBind.Add( body.Mesh.Positions[v] );
			ringLbs.Add( lbs[v] );
			ringDqs.Add( dqs[v] );
		}
		var bindR = PoseHelper.RingRadius( ringBind, joint, axis );
		var lbsR = PoseHelper.RingRadius( ringLbs, joint, axis );
		var dqsR = PoseHelper.RingRadius( ringDqs, joint, axis );
		var deltas = DualQuaternionSkinning.BakeCorrective( body.Mesh.Positions, body.Mesh.Skin, bind, pose );
		var maxDelta = 0f;
		var moved = 0;
		foreach ( var d in deltas )
		{
			maxDelta = MathF.Max( maxDelta, d.Length );
			if ( d.Length > 0.01f ) moved++;
		}
		r.Log.Add( $"dqs {body.Name} bone {boneName} at {angle:0}°: joint-ring radius bind {bindR:0.###}, LBS {lbsR:0.###} ({(bindR > 0 ? lbsR / bindR : 0):P0} kept), DQS {dqsR:0.###} ({(bindR > 0 ? dqsR / bindR : 0):P0} kept) over {ringBind.Count} ring vertices. Scaled bones fell back to LBS: {scaledBones.Count} bones, {scaledVertices} vertices. Baked DQS-minus-LBS corrective: max delta {maxDelta:0.###}, {moved} vertices over 0.01. DQS is preview + bake ONLY — the s&box runtime skins LBS with 4 weights, so ship the baked corrective, never the DQS deform." );
		return true;
	}

	// P4: IK/FK blend + snap, verified by rebuilding world from snapped locals.
	static bool IkFkVerb( PartStudio studio, List<string> args, Result r )
	{
		var opts = Pairs( args );
		if ( !opts.TryGetValue( "root", out var rootName ) || !opts.TryGetValue( "mid", out var midName ) || !opts.TryGetValue( "end", out var endName ) )
			return Fail( r, "ikfk needs root=, mid=, end=: ikfk root=Upper mid=Lower end=Hand target=x,y,z [pole=x,y,z] [blend=0.5]" );
		if ( !FindRigBone( studio, rootName, r, out var root ) ) return false;
		if ( !FindRigBone( studio, midName, r, out var mid ) ) return false;
		if ( !FindRigBone( studio, endName, r, out var end ) ) return false;
		if ( !opts.TryGetValue( "target", out var targetText ) || !ParseVec( targetText, out var target ) )
			return Fail( r, "ikfk needs target=x,y,z for the end effector." );
		var blend = Math.Clamp( Num( opts, "blend", 0.5f ), 0f, 1f );
		var softness = Num( opts, "softness", 0f );
		var fk = IkFk.BindWorld( studio.Rig );
		Vec3 pole = default;
		var poleGiven = opts.TryGetValue( "pole", out var poleText ) && ParseVec( poleText, out pole );
		if ( !poleGiven )
		{
			pole = IkFk.DefaultPole( fk[root].Origin, fk[mid].Origin, fk[end].Origin );
			r.Log.Add( $"ikfk: no pole given, using default pole {pole.x:0.##},{pole.y:0.##},{pole.z:0.##} (mid joint pushed off the root-end line). A chain without a pole warns every frame it is posed — pass pole= explicitly for production." );
		}
		var ik = IkFk.IkWorld( studio.Rig, root, mid, end, target, pole, softness );
		if ( ik is null )
			return Fail( r, $"ikfk: chain {rootName}/{midName}/{endName} is degenerate (zero-length bone) or not three bones deep; TwoBoneIk.Apply returned null." );
		var blended = IkFk.BlendWorld( fk, ik, blend, root, mid, end );
		var snapLocals = IkFk.SnapLocals( studio.Rig, fk, ik, root, mid, end );
		if ( snapLocals is null )
			return Fail( r, "ikfk: snap locals hit a singular transform; the IK pose cannot be represented as FK locals." );
		var rebuilt = IkFk.WorldFromLocals( studio.Rig, snapLocals );
		var snapErr = 0f;
		foreach ( var b in new[] { root, mid, end } )
			snapErr = MathF.Max( snapErr, (rebuilt[b].Origin - ik[b].Origin).Length );
		var fkEnd = fk[end].Origin;
		var ikEnd = ik[end].Origin;
		var blEnd = blended[end].Origin;
		r.Log.Add( $"ikfk {rootName}/{midName}/{endName} blend {blend:0.##}: FK end {fkEnd.x:0.##},{fkEnd.y:0.##},{fkEnd.z:0.##}, IK end {ikEnd.x:0.##},{ikEnd.y:0.##},{ikEnd.z:0.##}, blended end {blEnd.x:0.##},{blEnd.y:0.##},{blEnd.z:0.##}. Snap FK-to-IK: locals rebuilt from IK world reproduce the IK joints with max error {snapErr:0.####} (0 = no pop at the switch frame). Snap IK-to-FK target is the FK end above with pole {IkFk.DefaultPole( fk[root].Origin, fk[mid].Origin, fk[end].Origin ).x:0.##},{IkFk.DefaultPole( fk[root].Origin, fk[mid].Origin, fk[end].Origin ).y:0.##},{IkFk.DefaultPole( fk[root].Origin, fk[mid].Origin, fk[end].Origin ).z:0.##}." );
		return true;
	}

	// P7: twist stations declared explicitly, beside Citizen discovery.
	static bool TwistStationsVerb( PartStudio studio, List<string> args, Result r )
	{
		if ( args.Count < 1 || args[0].Contains( '=' ) )
			return Fail( r, "twiststations needs a body: twiststations Body bone=Parent stations=TwistA:0.25,TwistB:0.6 [profile=linear|eased]" );
		if ( !FindRigBody( studio, args[0], r, out var body ) ) return false;
		if ( !RequireSkin( body, r ) ) return false;
		var opts = Pairs( args.Skip( 1 ) );
		if ( !opts.TryGetValue( "bone", out var parentName ) || !FindRigBone( studio, parentName, r, out var parent ) ) return false;
		if ( !opts.TryGetValue( "stations", out var stationText ) )
			return Fail( r, "twiststations needs stations=: twiststations Body bone=Forearm stations=RollA:0.3,RollB:0.7" );
		var stations = new List<(float Along, int Bone)>();
		foreach ( var part in stationText.Split( ',', StringSplitOptions.TrimEntries ) )
		{
			var kv = part.Split( ':', StringSplitOptions.TrimEntries );
			if ( kv.Length != 2 || !FindRigBone( studio, kv[0], r, out var bi ) ) return false;
			if ( !float.TryParse( kv[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var along ) )
				return Fail( r, $"twiststations: station '{part}' needs a number after the colon (0..1 along the parent)." );
			stations.Add( (Math.Clamp( along, 0f, 1f ), bi) );
		}
		var eased = opts.TryGetValue( "profile", out var profile ) && profile.Equals( "eased", StringComparison.OrdinalIgnoreCase );
		var map = new Dictionary<int, List<(float Along, int Bone)>> { [parent] = stations };
		var changed = TwistStations.SpreadWithStations( body.Mesh, studio.Rig, map, eased );
		r.Log.Add( $"twiststations {body.Name} parent {parentName} profile {(eased ? "eased" : "linear")}: {changed} vertices re-spread across {stations.Count} declared stations. Written to the built mesh (a later rebuild re-derives weights — run this before any paint layer, since hand-painted vertices overrule the spread, as with the Citizen path, which is untouched)." );
		return true;
	}

	// P8: fan bones as rig content.
	static bool FanVerb( PartStudio studio, List<string> args, Result r )
	{
		var opts = Pairs( args );
		if ( !opts.TryGetValue( "parent", out var parentName ) || !opts.TryGetValue( "limb", out var limbName ) )
			return Fail( r, "fan needs parent= and limb=: fan parent=Clavicle limb=UpperArm count=3" );
		if ( !FindRigBone( studio, parentName, r, out var parent ) ) return false;
		if ( !FindRigBone( studio, limbName, r, out var limb ) ) return false;
		var count = Math.Max( 1, (int)Num( opts, "count", 3 ) );
		var prefix = opts.TryGetValue( "prefix", out var pfx ) && !string.IsNullOrWhiteSpace( pfx ) ? pfx : limbName;
		List<int> added;
		try { added = FanBones.AddFan( studio.Rig, parent, limb, count, prefix ); }
		catch ( Exception e ) { return Fail( r, $"fan: {e.Message}" ); }
		var names = string.Join( ", ", added.Select( i => studio.Rig.Bones[i].Name ) );
		r.Log.Add( $"fan: added {added.Count} deform fan bones under {parentName} toward {limbName}: {names}. These are DEFORM bones, not helpers — bind after adding them so they carry cap weight, then run rigcheck to confirm their weight stays on the cap region (weight outside the cap is weight theft)." );
		return AfterRig( studio, r );
	}

	// P8 (bulge half): bend-angle-driven perpendicular scale on a bone, measured
	// on the belly ring, pose-time only.
	static bool BulgeVerb( PartStudio studio, List<string> args, Result r )
	{
		if ( args.Count < 1 || args[0].Contains( '=' ) )
			return Fail( r, "bulge needs a body: bulge Body bone=Bone [joint=Child] [angle=90] [maxbulge=0.25] [peak=90]" );
		if ( !FindRigBody( studio, args[0], r, out var body ) ) return false;
		if ( !RequireSkin( body, r ) ) return false;
		var opts = Pairs( args.Skip( 1 ) );
		if ( !opts.TryGetValue( "bone", out var boneName ) || !FindRigBone( studio, boneName, r, out var bone ) ) return false;
		var joint = bone;
		if ( opts.TryGetValue( "joint", out var jn ) && !FindRigBone( studio, jn, r, out joint ) ) return false;
		var angle = Num( opts, "angle", 90f );
		var maxBulge = Num( opts, "maxbulge", 0.25f );
		var peak = Num( opts, "peak", 90f );
		var axis = ParseAxis( studio, joint, opts );
		var bindW = PoseHelper.BindWorld( studio.Rig );
		var pose = PoseHelper.BendPose( studio.Rig, joint, axis, angle );
		var scale = BulgeBones.ScaleFor( angle, maxBulge, peak );
		var bulged = BulgeBones.ScaledPose( pose, studio.Rig, bone, scale );
		// Belly: vertices in the middle 40% of the bone's length where this bone
		// is the dominant weight. Heat-bound meshes never give any vertex 0.9 on
		// one bone (the field is diffuse across neighbours), so a 0.9 threshold
		// would report "no belly" on exactly the meshes Heat produces; dominance
		// is the honest criterion, and the log reports the mean belly weight so
		// dilution is visible instead of hidden.
		var head = studio.Rig.HeadWorld( bone );
		var tail = studio.Rig.TailWorld( bone );
		var len = (tail - head).Length;
		var dir = len > 1e-6f ? (tail - head) * (1f / len) : new Vec3( 0, 0, 1 );
		var belly = new List<int>();
		var bellyWeight = 0f;
		for ( var v = 0; v < body.Mesh.VertexCount; v++ )
		{
			var ws = body.Mesh.Skin[v];
			if ( ws is null || ws.Length == 0 ) continue;
			var w = ws.FirstOrDefault( x => x.Bone == bone ).Weight;
			if ( w <= 0f || ws.Any( x => x.Weight > w ) ) continue;
			var along = Vec3.Dot( body.Mesh.Positions[v] - head, dir ) / Math.Max( len, 1e-6f );
			if ( along >= 0.3f && along <= 0.7f ) { belly.Add( v ); bellyWeight += w; }
		}
		if ( belly.Count == 0 )
			return Fail( r, $"bulge: no belly vertices found for bone '{boneName}' (need vertices in the middle 40% of its length where it is the dominant weight)." );
		var meanBellyWeight = bellyWeight / belly.Count;
		var bent = SkinBinder.Deform( body.Mesh.Positions, body.Mesh.Skin, bindW, pose );
		var withBulge = SkinBinder.Deform( body.Mesh.Positions, body.Mesh.Skin, bindW, bulged );
		// Radius is measured against the POSED bone axis, not the bind axis: after
		// the bend the axis has rotated, and measuring from the bind line would
		// report the swing of the limb as a radius change.
		var posedHead = pose[bone].Origin;
		var posedDir = Quat.FromXform( pose[bone] ).Rotate( new Vec3( 0, 1, 0 ) );
		float Ring( Vec3[] p, Vec3 centre, Vec3 axisDir ) => (float)belly.Average( v => (p[v] - centre - axisDir * Vec3.Dot( p[v] - centre, axisDir )).Length );
		var rBind = Ring( body.Mesh.Positions.ToArray(), head, dir );
		var rBent = Ring( bent, posedHead, posedDir );
		var rBulge = Ring( withBulge, posedHead, posedDir );
		r.Log.Add( $"bulge {body.Name} bone {boneName} at {angle:0}°: scale {scale:0.###} (max {maxBulge:0.##} at {peak:0}°). Belly radius over {belly.Count} vertices (mean {boneName} weight {meanBellyWeight:0.##}): bind {rBind:0.###}, bent {rBent:0.###}, bent+bulge {rBulge:0.###} ({(rBent > 1e-6f ? (rBulge / rBent - 1f) * 100f : 0f):0.#}% over bent). Pose-time scale only — it does not change the bind pose or the skin weights, and the runtime still skins LBS." );
		return true;
	}

	// P5: multi-pose RBF interpolation + stale-corrective guard.
	static bool PoseCheckVerb( PartStudio studio, List<string> args, Result r )
	{
		if ( args.Count < 1 || args[0].Contains( '=' ) )
			return Fail( r, "posecheck needs a body: posecheck Body bone=Bone [angle=90]" );
		if ( !FindRigBody( studio, args[0], r, out var body ) ) return false;
		if ( !RequireSkin( body, r ) ) return false;
		var opts = Pairs( args.Skip( 1 ) );
		if ( !opts.TryGetValue( "bone", out var boneName ) || !FindRigBone( studio, boneName, r, out var bone ) ) return false;
		var angle = Num( opts, "angle", 90f );
		var shape = new CorrectiveShape { Bone = boneName, Angle = angle };
		var before = body.Mesh.Skin;
		var morph = CorrectiveMorph.Build( body.Mesh, studio.Rig, shape );
		if ( morph is null )
			return Fail( r, $"posecheck: no corrective could be built for bone '{boneName}' — the mesh may have no shared vertices between that bone and its parent (a rigid joint)." );
		var fingerprint = CorrectiveStaleness.Fingerprint( before );
		// Simulate a later weight edit: one smoothing pass, as a paint fix would do.
		var edited = SkinBinder.SmoothWeights( body.Mesh, before, 1 );
		var (stale, changed) = CorrectiveStaleness.CheckAgainst( edited, before );
		r.Log.Add( $"posecheck {body.Name} corrective {morph.Name}: {morph.Deltas.Count( d => d.LengthSquared > 1e-10f )} vertices moved. After one weight-smoothing pass, stale={stale}, {changed} vertices changed — a corrective built from the old weights must be rebuilt from the stored sculpted pose, not silently kept. (The built mesh now carries this corrective as a check artifact; it is not a feature.)" );
		// RBF: samples at (0,0), (90,0), (90,45); evaluate at the combination pose and away from it.
		var samples = new List<PoseInterpolator.Sample>
		{
			new() { Pose = new[] { 0f, 0f }, Deltas = new Vec3[body.Mesh.VertexCount] },
			new() { Pose = new[] { 90f, 0f }, Deltas = morph.Deltas.Select( d => d * 0.5f ).ToArray() },
			new() { Pose = new[] { 90f, 45f }, Deltas = morph.Deltas },
		};
		var atCombo = PoseInterpolator.Weights( samples, new[] { 90f, 45f }, 45f );
		var atRaiseOnly = PoseInterpolator.Weights( samples, new[] { 90f, 0f }, 45f );
		var atRotateOnly = PoseInterpolator.Weights( samples, new[] { 0f, 45f }, 45f );
		r.Log.Add( $"posecheck RBF (falloff 45°): at raise+rotate (90,45) weights [{string.Join( ", ", atCombo.Select( w => w.ToString( "0.###", CultureInfo.InvariantCulture ) ) )}] — the combination sample dominates. At raise-only (90,0) [{string.Join( ", ", atRaiseOnly.Select( w => w.ToString( "0.###", CultureInfo.InvariantCulture ) ) )}], at rotate-only (0,45) [{string.Join( ", ", atRotateOnly.Select( w => w.ToString( "0.###", CultureInfo.InvariantCulture ) ) )}] — the combination corrective does not fire on a single-axis pose." );
		return true;
	}

	// P9: delta mush on a bent body, measured against the undeformed-mush result.
	static bool DeltaMushVerb( PartStudio studio, List<string> args, Result r )
	{
		if ( args.Count < 1 )
			return Fail( r, "deltamush needs a body, or test: deltamush Body bone=Bone [angle=90] [iterations=2] [detail=0.5], or deltamush test" );
		if ( args[0].Equals( "test", StringComparison.OrdinalIgnoreCase ) )
		{
			// Synthetic strip: the BIND pose carries one spike; the deformed pose
			// is the same strip. Delta mush preserves bind detail, so detail=1
			// must keep the spike and detail=0 must smooth it away, measurably.
			// (A clean bind + spiked deform cannot show this: the detail term is
			// bind-minus-smoothed-bind, which is zero on a clean bind.)
			var positions = new List<Vec3>();
			for ( var i = 0; i < 9; i++ ) positions.Add( new Vec3( i, i == 4 ? 3f : 0f, 0f ) );
			var faces = new List<Face>();
			for ( var i = 0; i + 1 < positions.Count; i++ ) faces.Add( new Face( new[] { i, i + 1 } ) );
			var mesh = new PolyMesh( positions, faces );
			var kept = DeltaMush.Apply( positions, positions, mesh, 1, 1f );
			var washed = DeltaMush.Apply( positions, positions, mesh, 1, 0f );
			r.Log.Add( $"deltamush test: spike vertex bind 3, deformed 3, mush detail=1 -> {kept[4].y:0.###}, detail=0 -> {washed[4].y:0.###}. Detail=1 preserves the spike, detail=0 smooths it away; bind pose itself is never modified." );
			return true;
		}
		if ( !FindRigBody( studio, args[0], r, out var body ) ) return false;
		if ( !RequireSkin( body, r ) ) return false;
		var opts = Pairs( args.Skip( 1 ) );
		if ( !opts.TryGetValue( "bone", out var boneName ) || !FindRigBone( studio, boneName, r, out var bone ) ) return false;
		var angle = Num( opts, "angle", 90f );
		var iterations = Math.Max( 0, (int)Num( opts, "iterations", 2 ) );
		var detail = Math.Clamp( Num( opts, "detail", 0.5f ), 0f, 1f );
		var axis = ParseAxis( studio, bone, opts );
		var bindW = PoseHelper.BindWorld( studio.Rig );
		var pose = PoseHelper.BendPose( studio.Rig, bone, axis, angle );
		var deformed = SkinBinder.Deform( body.Mesh.Positions, body.Mesh.Skin, bindW, pose );
		var mushed = DeltaMush.Apply( body.Mesh.Positions, deformed, body.Mesh, iterations, detail );
		var maxMove = 0f;
		var meanMove = 0f;
		for ( var v = 0; v < deformed.Length; v++ )
		{
			var d = (mushed[v] - deformed[v]).Length;
			maxMove = MathF.Max( maxMove, d );
			meanMove += d;
		}
		meanMove /= Math.Max( 1, deformed.Length );
		r.Log.Add( $"deltamush {body.Name} bone {boneName} at {angle:0}° iterations={iterations} detail={detail:0.##}: mean move from the deformed pose {meanMove:0.###}, max {maxMove:0.###}. This is a post-deform pass on the bent pose only — it never changes the bind pose, and it is scoped to this body: applied globally it would wash out fingers and faces." );
		return true;
	}
}
