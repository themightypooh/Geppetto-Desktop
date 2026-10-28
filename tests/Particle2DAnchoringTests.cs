using System.Numerics;
using Marionette.Desktop;

// Lane P48 (particles session, 2026-10-24): 2D bone/slot anchoring spec.
// Same harness shape as Particle2DTests: registry.Add("particle-2d-anchoring", name, body).
// Sweep line (NOT added to the canonical tree — coordinator sweep only):
//   Particle2DAnchoringTests.Register( registry, settings );
// placed after the GpuParticleSystemTests.Register line in
// tests/TestRegistry.cs (verified against the canonical tree 2026-10-24:
// canonical line 157; NO Particle2DTests.Register line exists in the
// canonical registry yet — all 2D particle test files sit unregistered
// pending the sweep — so if the sweep lands Particle2DTests.Register
// first, placing this line after it is equally correct).
//
// SWEEP INTEGRATION LINES (NOT applied by this lane — the 2D sim step is
// not this lane's file; the coordinator owns the wiring, and the sim's
// owner must approve the Particle2DState addition):
// 1. Particle2DEmitter gains, at the end of its field list:
//      public Particle2DAnchorSpec? Anchor;
//      public Func<string, Particle2DAnchorTransform?>? AnchorTransform;
//    (AnchorTransform is caller-supplied per step: the bone/slot resolver
//    lives with the viewport/rig owner, never in the sim. A null return
//    means "unresolved": the sweep must skip that emitter's anchored
//    spawns that step, never fire at a default position — same rule as
//    lane 4's cue bridge.)
// 2. Particle2DState gains one anchored-tracking slot per particle:
//      public Particle2DAnchoredParticle? AnchorState;
//    set at Spawn time from em.Anchor.Spawn(localSpawnPosition, anchor),
//    where localSpawnPosition is the shape/mask pick expressed in anchor
//    space (for an unanchored emitter the pick stays a world position, as
//    today — anchored and unanchored emitters must not share one code
//    path for the position pick).
// 3. Particle2DSystem.Step, before forces, for each alive particle with a
//    non-null AnchorState that IsFollowing and whose emitter's
//    AnchorTransform returns a transform: AnchorState.Step(anchor, dt,
//    localVelocity) and the particle's sim Position is overwritten with
//    AnchorState.WorldPosition (following wins over forces while
//    following; after release the sim integrates WorldPosition normally
//    and AnchorState is left as the release record). On the release step
//    itself the sim must ALSO take AnchorState.WorldVelocity(anchor) as
//    the particle's world velocity — that is the no-jump handoff; if the
//    sim keeps its own pre-follow velocity instead, the release WILL
//    jump, and that is a sweep bug, not a spec bug.
// 4. Draw-record colour for anchored particles passes through
//    spec.ApplySlotAlpha at spawn (alpha only; the sim's colour-over-life
//    lerp then runs on the already-scaled spawn colour).
// 5. P19 (Particle2DSub) depth story is untouched by anchoring:
//    AnchorState is per-particle tracking, not a spawn, so it neither
//    counts toward nor bypasses the MaxDepth guard. A child spawned from
//    a dying anchored parent gets an AnchorState only if its own sub-spec
//    carries an anchor; otherwise it spawns unanchored, as today.
// Until the sweep applies these, this module is spec + evaluator only:
// it is tested standalone here and touches no sim behaviour.
internal static class Particle2DAnchoringTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( bool ok, string reason ) { if ( !ok ) throw new Exception( reason ); }
		void Near( float got, float want, float tol, string what ) { if ( MathF.Abs( got - want ) > tol ) throw new Exception( $"{what}: got {got}, want {want} ± {tol}" ); }
		void NearPos( Vector2 got, Vector2 want, float tol, string what )
		{
			Near( got.X, want.X, tol, what + ".X" );
			Near( got.Y, want.Y, tol, what + ".Y" );
		}
		void Throws( Action body, string what )
		{
			try { body(); }
			catch ( ArgumentException ) { return; }
			catch ( InvalidOperationException ) { return; }
			throw new Exception( $"{what}: expected an exception, none thrown" );
		}
		Particle2DAnchorSpec LocalSpec( float followTime = float.PositiveInfinity, float slotAlpha = 1f ) =>
			new() { AnchorName = "hand", ReleaseMode = Particle2DReleaseMode.Local, FollowTimeSeconds = followTime, SlotAlpha = slotAlpha };
		Particle2DAnchorSpec WorldSpec() =>
			new() { AnchorName = "hand", ReleaseMode = Particle2DReleaseMode.World };
		Particle2DAnchorTransform At( float x, float y, float rot = 0f, float sx = 1f, float sy = 1f ) =>
			new( new Vector2( x, y ), rot, new Vector2( sx, sy ) );

		registry.Add( "particle-2d-anchoring", "transform identity maps local offset by position only", () =>
		{
			var a = At( 100, 200 );
			NearPos( a.TransformPoint( new Vector2( 10, -5 ) ), new Vector2( 110, 195 ), 1e-4f, "identity transform" );
			NearPos( Particle2DAnchorTransform.Identity.TransformPoint( new Vector2( 7, 3 ) ), new Vector2( 7, 3 ), 1e-4f, "Identity transform" );
		} );

		registry.Add( "particle-2d-anchoring", "rotation is clockwise on screen in Y-down degrees", () =>
		{
			var a = At( 100, 100, rot: 90 );
			// +X local (right) rotates toward +Y (down): clockwise on screen.
			NearPos( a.TransformPoint( new Vector2( 10, 0 ) ), new Vector2( 100, 110 ), 1e-4f, "rot 90 local +X" );
			NearPos( At( 100, 100, rot: 180 ).TransformPoint( new Vector2( 10, 0 ) ), new Vector2( 90, 100 ), 1e-4f, "rot 180 local +X" );
			NearPos( At( 100, 100, rot: -90 ).TransformPoint( new Vector2( 10, 0 ) ), new Vector2( 100, 90 ), 1e-4f, "rot -90 local +X" );
			NearPos( At( 100, 100, rot: 90 ).TransformDirection( new Vector2( 1, 0 ) ), new Vector2( 0, 1 ), 1e-4f, "direction rot 90" );
			NearPos( At( 500, 500, rot: 90 ).TransformDirection( new Vector2( 1, 0 ) ), new Vector2( 0, 1 ), 1e-4f, "direction ignores translation" );
		} );

		registry.Add( "particle-2d-anchoring", "scale stretches local offsets, combined with rotation", () =>
		{
			NearPos( At( 100, 100, sx: 2, sy: 3 ).TransformPoint( new Vector2( 10, 20 ) ), new Vector2( 120, 160 ), 1e-4f, "scale (2,3)" );
			// Scale first, then rotate: (10,0) * 2 = (20,0), rot 90 -> (0,20) down.
			NearPos( At( 100, 100, rot: 90, sx: 2, sy: 2 ).TransformPoint( new Vector2( 10, 0 ) ), new Vector2( 100, 120 ), 1e-4f, "rot 90 + scale 2" );
			// Negative scale is a mirror, not an error.
			NearPos( At( 100, 100, sx: -1, sy: 1 ).TransformPoint( new Vector2( 10, 0 ) ), new Vector2( 90, 100 ), 1e-4f, "mirror scale -1" );
		} );

		registry.Add( "particle-2d-anchoring", "LOCAL particle follows a moving anchor", () =>
		{
			var p = LocalSpec().Spawn( new Vector2( 10, 0 ), At( 100, 100 ) );
			NearPos( p.WorldPosition, new Vector2( 110, 100 ), 1e-4f, "spawn world" );
			Check( p.IsFollowing && !p.Released, "LOCAL particle must start following" );
			p.Step( At( 200, 150 ), 0.1f, Vector2.Zero );
			NearPos( p.WorldPosition, new Vector2( 210, 150 ), 1e-4f, "moved anchor world" );
			p.Step( At( 250, 150 ), 0.1f, Vector2.Zero );
			NearPos( p.WorldPosition, new Vector2( 260, 150 ), 1e-4f, "moved anchor world again" );
		} );

		registry.Add( "particle-2d-anchoring", "LOCAL particle follows a rotating anchor", () =>
		{
			var p = LocalSpec().Spawn( new Vector2( 10, 0 ), At( 100, 100, rot: 0 ) );
			NearPos( p.WorldPosition, new Vector2( 110, 100 ), 1e-4f, "spawn world rot 0" );
			p.Step( At( 100, 100, rot: 90 ), 0.1f, Vector2.Zero );
			NearPos( p.WorldPosition, new Vector2( 100, 110 ), 1e-4f, "rot 90 world" );
			p.Step( At( 100, 100, rot: 180 ), 0.1f, Vector2.Zero );
			NearPos( p.WorldPosition, new Vector2( 90, 100 ), 1e-4f, "rot 180 world" );
		} );

		registry.Add( "particle-2d-anchoring", "LOCAL particle follows a moving AND rotating anchor at once", () =>
		{
			var p = LocalSpec().Spawn( new Vector2( 10, 0 ), At( 100, 100, rot: 0 ) );
			// Anchor translates +100x and rotates 90 deg in one step.
			p.Step( At( 200, 100, rot: 90 ), 0.1f, Vector2.Zero );
			NearPos( p.WorldPosition, new Vector2( 200, 110 ), 1e-4f, "moved + rotated world" );
		} );

		registry.Add( "particle-2d-anchoring", "WORLD particle ignores later anchor motion and rotation", () =>
		{
			var p = WorldSpec().Spawn( new Vector2( 10, 0 ), At( 100, 100, rot: 0 ) );
			NearPos( p.WorldPosition, new Vector2( 110, 100 ), 1e-4f, "spawn world" );
			Check( p.Released && !p.IsFollowing, "WORLD particle must be released at spawn" );
			p.Step( At( 200, 150, rot: 90 ), 0.1f, Vector2.Zero );
			NearPos( p.WorldPosition, new Vector2( 110, 100 ), 1e-4f, "WORLD world after anchor moved+rotated" );
			p.Step( At( 0, 0, rot: 180 ), 1f, Vector2.Zero );
			NearPos( p.WorldPosition, new Vector2( 110, 100 ), 1e-4f, "WORLD world after second anchor move" );
		} );

		registry.Add( "particle-2d-anchoring", "follow-time 0 LOCAL behaves exactly like WORLD", () =>
		{
			var local0 = LocalSpec( followTime: 0f ).Spawn( new Vector2( 10, 0 ), At( 100, 100, rot: 0 ) );
			var world = WorldSpec().Spawn( new Vector2( 10, 0 ), At( 100, 100, rot: 0 ) );
			Check( local0.Released && !local0.IsFollowing, "follow-time 0 must release at spawn" );
			Check( local0.WorldPosition == world.WorldPosition, "follow-time 0 spawn world must equal WORLD spawn world" );
			local0.Step( At( 200, 150, rot: 90 ), 0.1f, Vector2.Zero );
			world.Step( At( 200, 150, rot: 90 ), 0.1f, Vector2.Zero );
			Check( local0.WorldPosition == world.WorldPosition, "follow-time 0 must stay fixed like WORLD under anchor motion" );
		} );

		registry.Add( "particle-2d-anchoring", "follow-time releases at the first step where age reaches it", () =>
		{
			var p = LocalSpec( followTime: 0.5f ).Spawn( new Vector2( 10, 0 ), At( 100, 100 ) );
			for ( int i = 0; i < 4; i++ )
			{
				p.Step( At( 100 + 10 * (i + 1), 100 ), 0.1f, Vector2.Zero );
				Check( p.IsFollowing, $"step {i + 1} (age {p.Age}) must still follow before 0.5 s" );
				NearPos( p.WorldPosition, new Vector2( 110 + 10 * (i + 1), 100 ), 1e-4f, $"followed world at step {i + 1}" );
			}
			Check( MathF.Abs( p.Age - 0.4f ) < 1e-4f, $"age after 4 steps must be 0.4, got {p.Age}" );
			p.Step( At( 150, 100 ), 0.1f, Vector2.Zero );
			Check( p.Released && !p.IsFollowing, "age 0.5 must release at the 5th step" );
			NearPos( p.WorldPosition, new Vector2( 160, 100 ), 1e-4f, "release-step world is the followed position under the release anchor" );
		} );

		registry.Add( "particle-2d-anchoring", "release does not teleport when the anchor is frozen", () =>
		{
			var p = LocalSpec( followTime: 0.2f ).Spawn( new Vector2( 10, 0 ), At( 100, 100 ) );
			p.Step( At( 100, 100 ), 0.1f, Vector2.Zero );
			var before = p.WorldPosition;
			p.Step( At( 100, 100 ), 0.1f, Vector2.Zero );
			Check( p.Released, "must release at age 0.2" );
			Check( p.WorldPosition == before, $"frozen-anchor release must not move: {before} -> {p.WorldPosition}" );
		} );

		registry.Add( "particle-2d-anchoring", "release moves only by the anchor motion, no extra jump", () =>
		{
			var p = LocalSpec( followTime: 0.2f ).Spawn( new Vector2( 10, 0 ), At( 100, 100 ) );
			p.Step( At( 100, 100 ), 0.1f, Vector2.Zero );
			var before = p.WorldPosition;
			// Anchor translates exactly +20x across the release step; fixed local offset.
			p.Step( At( 120, 100 ), 0.1f, Vector2.Zero );
			Check( p.Released, "must release at age 0.2" );
			NearPos( p.WorldPosition, before + new Vector2( 20, 0 ), 1e-4f, "release-step delta must equal anchor delta exactly" );
		} );

		registry.Add( "particle-2d-anchoring", "after release the tracker stops following the anchor", () =>
		{
			var p = LocalSpec( followTime: 0.1f ).Spawn( new Vector2( 10, 0 ), At( 100, 100 ) );
			p.Step( At( 100, 100 ), 0.1f, Vector2.Zero );
			Check( p.Released, "must release at age 0.1" );
			var handoff = p.WorldPosition;
			p.Step( At( 900, 900, rot: 180 ), 1f, Vector2.Zero );
			Check( p.WorldPosition == handoff, $"released particle must keep the handoff point, got {p.WorldPosition} want {handoff}" );
		} );

		registry.Add( "particle-2d-anchoring", "infinite follow-time follows for life", () =>
		{
			var p = LocalSpec( followTime: float.PositiveInfinity ).Spawn( new Vector2( 10, 0 ), At( 100, 100 ) );
			for ( int i = 0; i < 20; i++ )
			{
				p.Step( At( 100 + 5 * (i + 1), 100, rot: i * 9 ), 0.1f, Vector2.Zero );
				Check( p.IsFollowing, $"infinite follow-time must still follow at step {i + 1}" );
			}
			// Last step: anchor (200,100) rot 171 deg, local (10,0).
			var want = At( 200, 100, rot: 19 * 9 ).TransformPoint( new Vector2( 10, 0 ) );
			NearPos( p.WorldPosition, want, 1e-4f, "infinite-follow world after 20 steps" );
		} );

		registry.Add( "particle-2d-anchoring", "local drift moves a following particle in anchor space", () =>
		{
			var p = LocalSpec().Spawn( new Vector2( 0, 0 ), At( 100, 100 ) );
			for ( int i = 0; i < 10; i++ ) p.Step( At( 100, 100 ), 0.1f, new Vector2( 10, 0 ) );
			NearPos( p.LocalPosition, new Vector2( 10, 0 ), 1e-4f, "local drift after 1 s at 10 px/s" );
			NearPos( p.WorldPosition, new Vector2( 110, 100 ), 1e-4f, "drifted world under a static anchor" );
			// Drift + moving anchor compose: local keeps drifting, anchor carries it.
			p.Step( At( 200, 100 ), 0.1f, new Vector2( 10, 0 ) );
			NearPos( p.WorldPosition, new Vector2( 211, 100 ), 1e-4f, "drift + anchor move compose" );
		} );

		registry.Add( "particle-2d-anchoring", "slot-alpha scales alpha only, default 1.0 changes nothing", () =>
		{
			var baseColor = new Particle2DColor( 1f, 0.5f, 0f, 0.8f );
			var faded = LocalSpec( slotAlpha: 0.5f ).ApplySlotAlpha( baseColor );
			Near( faded.A, 0.4f, 1e-6f, "slot-alpha 0.5 on alpha 0.8" );
			Check( faded.R == 1f && faded.G == 0.5f && faded.B == 0f, "slot-alpha must not touch RGB" );
			var untouched = LocalSpec().ApplySlotAlpha( baseColor );
			Check( untouched.A == baseColor.A && untouched.R == baseColor.R, "default slot-alpha 1.0 must leave colour unchanged" );
			Near( LocalSpec( slotAlpha: 0.25f ).EffectiveAlpha( 0.8f ), 0.2f, 1e-6f, "EffectiveAlpha 0.8 * 0.25" );
			Near( LocalSpec( slotAlpha: 0f ).EffectiveAlpha( 0.8f ), 0f, 1e-6f, "slot-alpha 0 fades fully" );
		} );

		registry.Add( "particle-2d-anchoring", "Validate rejects missing names, bad follow-times, and bad slot-alpha", () =>
		{
			Throws( () => new Particle2DAnchorSpec { AnchorName = "", ReleaseMode = Particle2DReleaseMode.Local }.Validate(), "empty anchor name" );
			Throws( () => new Particle2DAnchorSpec { AnchorName = "   ", ReleaseMode = Particle2DReleaseMode.Local }.Validate(), "whitespace anchor name" );
			Throws( () => new Particle2DAnchorSpec { AnchorName = null!, ReleaseMode = Particle2DReleaseMode.Local }.Validate(), "null anchor name" );
			Throws( () => LocalSpec( followTime: -0.1f ).Validate(), "negative follow-time" );
			Throws( () => LocalSpec( followTime: float.NaN ).Validate(), "NaN follow-time" );
			Throws( () => LocalSpec( slotAlpha: -0.1f ).Validate(), "negative slot-alpha" );
			Throws( () => LocalSpec( slotAlpha: 1.5f ).Validate(), "slot-alpha above 1" );
			Throws( () => LocalSpec( slotAlpha: float.NaN ).Validate(), "NaN slot-alpha" );
			Throws( () => new Particle2DAnchorSpec { AnchorName = "hand", ReleaseMode = (Particle2DReleaseMode)99 }.Validate(), "unknown release mode" );
			// Spawn validates too, so a bad spec fails at spawn, not mid-flight.
			Throws( () => LocalSpec( followTime: -1f ).Spawn( Vector2.Zero, At( 0, 0 ) ), "spawn with negative follow-time" );
		} );

		registry.Add( "particle-2d-anchoring", "Validate rejects zero and non-finite scales", () =>
		{
			Throws( () => At( 0, 0, sx: 0, sy: 1 ).Validate(), "zero scale X" );
			Throws( () => At( 0, 0, sx: 1, sy: 0 ).Validate(), "zero scale Y" );
			Throws( () => At( 0, 0, sx: float.NaN, sy: 1 ).Validate(), "NaN scale X" );
			Throws( () => new Particle2DAnchorTransform( new Vector2( float.NaN, 0 ), 0, Vector2.One ).Validate(), "NaN anchor position" );
			Throws( () => new Particle2DAnchorTransform( Vector2.Zero, float.NaN, Vector2.One ).Validate(), "NaN anchor rotation" );
			// A default (zero-scale) struct must fail, not silently collapse.
			Throws( () => new Particle2DAnchorTransform().Validate(), "default struct zero scale" );
			// Spawn and Step fail loudly on a zero scale as well.
			Throws( () => LocalSpec().Spawn( Vector2.Zero, At( 0, 0, sx: 0 ) ), "spawn with zero scale" );
			var p = LocalSpec().Spawn( Vector2.Zero, At( 0, 0 ) );
			Throws( () => p.Step( At( 0, 0, sx: 0 ), 0.1f, Vector2.Zero ), "step with zero scale" );
		} );

		registry.Add( "particle-2d-anchoring", "Step rejects bad dt and bad local velocity", () =>
		{
			var p = LocalSpec().Spawn( Vector2.Zero, At( 0, 0 ) );
			Throws( () => p.Step( At( 0, 0 ), -0.1f, Vector2.Zero ), "negative dt" );
			Throws( () => p.Step( At( 0, 0 ), float.NaN, Vector2.Zero ), "NaN dt" );
			Throws( () => p.Step( At( 0, 0 ), 0.1f, new Vector2( float.NaN, 0 ) ), "NaN local velocity" );
			// dt 0 is legal: age does not advance, the particle keeps following.
			p.Step( At( 50, 50 ), 0f, Vector2.Zero );
			Check( p.IsFollowing && p.Age == 0f, "dt 0 must keep following at age 0" );
			NearPos( p.WorldPosition, new Vector2( 50, 50 ), 1e-4f, "dt 0 still re-applies the anchor" );
		} );

		registry.Add( "particle-2d-anchoring", "determinism: identical inputs give bit-identical state", () =>
		{
			Particle2DAnchoredParticle Run()
			{
				var p = LocalSpec( followTime: 0.5f ).Spawn( new Vector2( 10, 0 ), At( 100, 100, rot: 0 ) );
				for ( int i = 0; i < 6; i++ )
					p.Step( At( 100 + 10 * i, 100, rot: i * 15 ), 0.1f, new Vector2( 10, 5 ) );
				return p;
			}
			var a = Run();
			var b = Run();
			// Bit-identical: exact == on every tracked field, not a tolerance.
			Check( a.WorldPosition == b.WorldPosition, $"world differs: {a.WorldPosition} vs {b.WorldPosition}" );
			Check( a.LocalPosition == b.LocalPosition, $"local differs: {a.LocalPosition} vs {b.LocalPosition}" );
			Check( a.Age == b.Age, $"age differs: {a.Age} vs {b.Age}" );
			Check( a.Released == b.Released, "release state differs" );
			Check( a.LocalVelocity == b.LocalVelocity, $"local velocity differs: {a.LocalVelocity} vs {b.LocalVelocity}" );
			Check( a.Released, "both runs must have released at age 0.6 > follow-time 0.5" );
		} );

		registry.Add( "particle-2d-anchoring", "release preserves velocity: no jump at the boundary step", () =>
		{
			// Drifting particle, anchor rotates 90 deg clockwise exactly on the release step.
			var p = LocalSpec( followTime: 0.2f ).Spawn( new Vector2( 10, 0 ), At( 100, 100, rot: 0 ) );
			p.Step( At( 100, 100, rot: 0 ), 0.1f, new Vector2( 10, 0 ) );
			Check( p.IsFollowing, "must still follow at age 0.1" );
			NearPos( p.WorldVelocity( At( 100, 100, rot: 0 ) ), new Vector2( 10, 0 ), 1e-4f, "pre-release world velocity rot 0" );
			var releaseAnchor = At( 100, 100, rot: 90 );
			p.Step( releaseAnchor, 0.1f, new Vector2( 10, 0 ) );
			Check( p.Released, "must release at age 0.2" );
			// Local (10,0) under a 90 deg clockwise anchor is world (0,10) in Y-down space.
			NearPos( p.WorldVelocity( releaseAnchor ), new Vector2( 0, 10 ), 1e-4f, "release-step world velocity" );
			// Position continuity at the same boundary: followed position under the release anchor.
			NearPos( p.WorldPosition, releaseAnchor.TransformPoint( p.LocalPosition ), 1e-4f, "release position is the followed position" );
			// After release, further steps are no-ops: same release anchor still
			// reports the identical handoff velocity — no jump, ever. (Compared
			// against the captured handoff vector, not a literal, because
			// cos(90 deg) in float is ~-4e-08, not exactly 0.)
			var handoff = p.WorldVelocity( releaseAnchor );
			p.Step( At( 900, 900, rot: 180 ), 1f, new Vector2( 99, 99 ) );
			Check( p.WorldVelocity( releaseAnchor ) == handoff, $"post-release handoff velocity moved: {p.WorldVelocity( releaseAnchor )} vs {handoff}" );
			Check( p.LocalVelocity == new Vector2( 10, 0 ), $"post-release local velocity moved: {p.LocalVelocity}" );
		} );

		registry.Add( "particle-2d-anchoring", "fixed-point anchor needs no name and follows its static transform", () =>
		{
			var spec = new Particle2DAnchorSpec { AnchorKind = Particle2DAnchorKind.FixedPoint, AnchorName = "", ReleaseMode = Particle2DReleaseMode.Local, FollowTimeSeconds = float.PositiveInfinity };
			spec.Validate();
			var p = spec.Spawn( new Vector2( 10, 0 ), At( 300, 400 ) );
			NearPos( p.WorldPosition, new Vector2( 310, 400 ), 1e-4f, "fixed-point spawn world" );
			p.Step( At( 300, 400 ), 0.1f, Vector2.Zero );
			NearPos( p.WorldPosition, new Vector2( 310, 400 ), 1e-4f, "fixed-point world under the same static transform" );
			Check( p.IsFollowing, "fixed-point particle must keep following its static point" );
			// A fixed-point spec with a name is equally fine; the name is just ignored.
			new Particle2DAnchorSpec { AnchorKind = Particle2DAnchorKind.FixedPoint, AnchorName = "ignored" }.Validate();
			// But a bone/slot spec with an empty name still fails loudly.
			Throws( () => new Particle2DAnchorSpec { AnchorKind = Particle2DAnchorKind.BoneSlot, AnchorName = "" }.Validate(), "bone/slot empty name" );
			Throws( () => new Particle2DAnchorSpec { AnchorName = "hand", AnchorKind = (Particle2DAnchorKind)99 }.Validate(), "unknown anchor kind" );
		} );
	}
}
