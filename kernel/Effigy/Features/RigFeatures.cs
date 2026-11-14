using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Effigy;

/// <summary>
/// A cheap fingerprint of a skeleton - names, parents, bind pose and lengths - so a feature built
/// from the rig can tell the rig changed under it. The rig is not a feature (see
/// <see cref="PartStudio.Rig"/>), so moving a bone dirties nothing; a feature that reads the rig
/// asks this in <see cref="Feature.IsStale"/> instead.
/// </summary>
public static class RigFingerprint
{
	public static long Of( Skeleton skeleton )
	{
		if ( skeleton is null )
			return 0;

		unchecked
		{
			long hash = 1469598103934665603L;
			void Mix( long v ) => hash = (hash ^ v) * 1099511628211L;
			void MixF( float f ) => Mix( BitConverter.SingleToInt32Bits( f ) );
			void MixV( Vec3 v ) { MixF( v.x ); MixF( v.y ); MixF( v.z ); }

			Mix( skeleton.Count );

			foreach ( var bone in skeleton.Bones )
			{
				Mix( StringComparer.Ordinal.GetHashCode( bone.Name ?? "" ) );
				Mix( bone.Parent );
				MixV( bone.Local.X );
				MixV( bone.Local.Y );
				MixV( bone.Local.Z );
				MixV( bone.Local.Origin );
				MixF( bone.Length );
			}

			return hash;
		}
	}
}

/// <summary>
/// Body from Bones as a step in the history: a closed, all-quad body grown along the rig - a cube
/// per joint, a tube per bone, smoothed (see <see cref="SkinBlockout"/>). The block-out a
/// character starts from, on the bones it will be rigged to.
///
/// PARAMETRIC, unlike the one-shot import it replaces: move a bone, or change a thickness here,
/// and the body rebuilds. Radius per bone is text so a script and the dialog write the same thing:
/// <c>spine_1 4; arm_upper_* 1.6; * 1.2</c> - a bone name, a name ending in * for every bone that
/// starts that way, or * alone for every bone not otherwise named. A bone with no radius takes a
/// fifth of its length.
/// </summary>
public sealed class BodyFromBonesFeature : Feature
{
	public override string TypeName => "Body from Bones";

	/// <summary>Thickness per bone: "name radius; prefix* radius; * radius". Empty = a fifth of each
	/// bone's length.</summary>
	public readonly StringParam Radii = new( "Radii", "" );

	/// <summary>How many times the blocky skin is smoothed. 0 keeps the cubes and tubes.</summary>
	public readonly IntParam Levels = new( "Smooth levels", 2, 0, 4 );

	/// <summary>How joints are made: Cube is the original (six bones at most per joint); Hull takes any number (see <see cref="SkinBlockout"/>); Auto tries Cube and falls back to Hull where a joint is too crowded.</summary>
	public readonly ChoiceParam Joints = new( "Joints", new[] { "Auto", "Cube", "Hull" } );

	public readonly IntParam Material = new( "Material slot", 0, 0, 63 ) { Slider = false };

	public override IReadOnlyList<IParam> Parameters => new IParam[] { Radii, Levels, Joints, Material };

	public override IReadOnlyList<IParam> AdvancedParameters => new IParam[] { Material };

	Skeleton _rig;
	long _built;

	public override bool IsStale => _rig is not null && RigFingerprint.Of( _rig ) != _built;

	protected override void Execute( FeatureContext ctx )
	{
		_rig = ctx.Rig;
		_built = RigFingerprint.Of( ctx.Rig );

		if ( ctx.Rig is null || ctx.Rig.Count == 0 )
		{
			Fail(
				"Body from Bones needs a rig",
				"The body is grown along the bones, and there are none yet.",
				"Any rig works: `rig human` in a script, a template rig, or bones placed with Add Bone in the Rig workspace — a template is not required, bone names are free",
				"Or place bones with Add Bone in the Rig workspace" );
		}

		var radii = ParseRadii( Radii.Value, ctx.Rig, out var problem );

		if ( problem is not null )
			FailOn( "Radii", "A radius could not be read", problem,
				"Write each as a bone name and a number: spine_1 4; arm_upper_* 1.6; * 1.2" );

		PolyMesh mesh;

		Func<int, float> radiusOf = radii is null ? null : (Func<int, float>)(i => radii[i] ?? DefaultRadius( ctx.Rig, i ));
		var usedHull = false;
		try
		{
			if ( Joints.Value == "Hull" )
				mesh = SkinBlockout.Build( ctx.Rig, radiusOf, Levels.Clamped, SkinBlockout.Joints.Hull );
			else if ( Joints.Value == "Cube" )
				mesh = SkinBlockout.Build( ctx.Rig, radiusOf, Levels.Clamped );
			else
			{
				try { mesh = SkinBlockout.Build( ctx.Rig, radiusOf, Levels.Clamped ); }
				catch ( InvalidOperationException e ) when ( e.Message.Contains( "more than six" ) )
				{
					mesh = SkinBlockout.Build( ctx.Rig, radiusOf, Levels.Clamped, SkinBlockout.Joints.Hull );
					usedHull = true;
				}
			}
		}
		catch ( InvalidOperationException e )
		{
			Fail( "Body from Bones could not build", e.Message,
				Joints.Value == "Cube" ? "Set joints=Hull, or leave joints=Auto so a crowded joint falls back to hull joints" : "Give the bones some length, or check no joint has more than six bones leaving it" );
			return;
		}

		foreach ( var face in mesh.Faces )
			face.Material = Material.Clamped;

		ctx.Bodies.Add( new Body( ctx.NewBodyId(), Name ?? "Body", mesh ) );
		Warning = $"{mesh.FaceCount:N0} quads over {ctx.Rig.Count} bones. It has no UVs yet: add a UV project (Unwrap) before baking.";
		if ( usedHull ) Warning += " Cube joints refused at a crowded joint, so hull joints were used.";
	}

	static float DefaultRadius( Skeleton rig, int bone )
	{
		if ( !rig.TryBodySpan( bone, out var head, out var tail ) )
			return MathF.Max( rig.Bones[bone].Length * 0.2f, 0.05f );

		return MathF.Max( (tail - head).Length * 0.2f, 0.05f );
	}

	/// <summary>Per-bone radius from the text, null for bones it does not mention; null overall
	/// when the text is empty. <paramref name="problem"/> names an unreadable entry or a name that
	/// matches no bone, with the bones listed.</summary>
	public static float?[] ParseRadii( string text, Skeleton rig, out string problem )
	{
		problem = null;

		if ( string.IsNullOrWhiteSpace( text ) )
			return null;

		var result = new float?[rig.Count];
		float? fallback = null;
		var exact = new bool[rig.Count];
		var prefixLength = new int[rig.Count];

		foreach ( var raw in text.Split( new[] { ';', '\n' }, StringSplitOptions.RemoveEmptyEntries ) )
		{
			var entry = raw.Trim();

			if ( entry.Length == 0 )
				continue;

			var words = entry.Split( (char[])null, StringSplitOptions.RemoveEmptyEntries );

			if ( words.Length != 2 || !float.TryParse( words[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var r ) || !(r > 0f) )
			{
				problem = $"'{entry}' is not a bone name and a positive radius.";
				return null;
			}

			var name = words[0];

			if ( name == "*" )
			{
				fallback = r;
				continue;
			}

			if ( name.EndsWith( '*' ) )
			{
				var prefix = name[..^1];
				var any = false;

				for ( var i = 0; i < rig.Count; i++ )
				{
					if ( exact[i] || !rig.Bones[i].Name.StartsWith( prefix, StringComparison.OrdinalIgnoreCase ) || prefix.Length < prefixLength[i] )
						continue;

					result[i] = r;
					prefixLength[i] = prefix.Length;
					any = true;
				}

				if ( !any )
				{
					problem = $"No bone starts with '{prefix}'. Bones: {string.Join( ", ", rig.Bones.Select( b => b.Name ).Take( 40 ) )}.";
					return null;
				}

				continue;
			}

			var index = rig.IndexOf( name );

			if ( index < 0 )
			{
				problem = $"No bone called '{name}'. Bones: {string.Join( ", ", rig.Bones.Select( b => b.Name ).Take( 40 ) )}.";
				return null;
			}

			result[index] = r;
			exact[index] = true;
		}

		if ( fallback is { } all )
		{
			for ( var i = 0; i < rig.Count; i++ )
				result[i] ??= all;
		}

		return result;
	}
}

/// <summary>
/// Automatic Weights: skin weights for the chosen bodies from the rig, so they bend with the bones
/// without anyone painting. The weights ride the mesh (<see cref="PolyMesh.Skin"/>), so export,
/// the pose preview and every later feature carry them; a later Subdivide blends them.
///
/// METHODS. Smooth is distance to each bone segment, then averaged over the surface so joints
/// bend instead of hinging. Envelope is the distance part alone - softer through space, blind to
/// the surface. Rigid gives each vertex wholly to its nearest bone: right for machinery. Heat is
/// the bone-heat solve (weights diffused over the surface from each bone); it is a hook another
/// build fills in through <see cref="HeatBinder"/>; when no build provides one, Bind uses
/// <see cref="AutoWeights.Heat"/> directly and reports its <see cref="BindReport"/> in the warning.
///
/// Helper bones - twist, IK, aim, hold - take no weight directly. With Twist on, each limb's
/// weight is then shared onto its twist bones by position along the limb
/// (<see cref="TwistWeights.Spread"/>). The result is kept to <see cref="MaxInfluences"/> bones per
/// vertex, strongest first, summing to one.
/// </summary>
public sealed class BindFeature : Feature
{
	public override string TypeName => "Automatic Weights";

	public override GeometryKind Accepts => GeometryKind.Body;

	public static readonly string[] MethodNames = { "Smooth", "Envelope", "Rigid", "Heat" };

	/// <summary>Bone heat: (mesh, deform skeleton) → weights indexed into that skeleton. A build
	/// may provide one; when it does not, Method = Heat uses AutoWeights.Heat directly.</summary>

	/// <summary>The BindReport from the last Heat bind in this process, or null when the last
	/// bind was not Heat (or a custom HeatBinder was used, which carries its own report).</summary>
	public static BindReport LastHeatReport;
	public static Func<PolyMesh, Skeleton, SkinWeights> HeatBinder;

	/// <summary>Bodies to weight. Empty = every body that is not a reference.</summary>
	public readonly BodySelectionParam Bodies = new( "Bodies" );

	public readonly ChoiceParam Method = new( "Method", MethodNames );

	/// <summary>How quickly a bone's pull fades with distance. 1 reaches across the model, 4 is
	/// nearly rigid.</summary>
	public readonly FloatParam Falloff = new( "Falloff", 2f, 0.5f, 8f );

	/// <summary>Passes of averaging over the surface (Smooth only).</summary>
	public readonly IntParam Smoothing = new( "Smoothing", 3, 0, 20 );

	/// <summary>Share each limb's weight onto its twist bones.</summary>
	public readonly BoolParam Twist = new( "Use twist bones", true );

	/// <summary>Most bones one vertex may follow. Engines take four.</summary>
	public readonly IntParam MaxInfluences = new( "Bones per vertex", 4, 1, 8 );

	public override IReadOnlyList<IParam> Parameters => new IParam[] { Bodies, Method, Falloff, Smoothing, Twist, MaxInfluences };

	public override IReadOnlyList<IParam> AdvancedParameters => new IParam[] { Falloff, MaxInfluences };

	Skeleton _rig;
	long _built;

	public override bool IsStale => _rig is not null && RigFingerprint.Of( _rig ) != _built;

	/// <summary>Vertices weighted by the last rebuild, over every body.</summary>
	public int WeightedVertices { get; private set; }

	protected override void Execute( FeatureContext ctx )
	{
		_rig = ctx.Rig;
		_built = RigFingerprint.Of( ctx.Rig );
		WeightedVertices = 0;

		if ( ctx.Rig is null || ctx.Rig.Count == 0 )
		{
			Fail(
				"Automatic Weights needs a rig",
				"Weights say how much each bone moves each vertex, and there are no bones yet.",
				"Any rig works: `rig human` in a script, a template rig, or bones placed with Add Bone in the Rig workspace — a template is not required, bone names are free",
				"Or place bones with Add Bone in the Rig workspace" );
		}

		var targets = ctx.Bodies.Where( b => !b.IsReference && Bodies.Matches( b ) && b.Mesh is { VertexCount: > 0 } ).ToList();

		if ( targets.Count == 0 )
		{
			Fail(
				"Nothing to weight",
				Bodies.BodyIds.Count == 0 ? "There are no bodies before this in the history." : "None of the chosen bodies is in the model any more.",
				"Pick the bodies to weight, or move this step below the step that makes them" );
		}

		var method = Method.Value;


		var deform = DeformSkeleton( ctx.Rig, out var map );

		if ( deform.Count == 0 )
		{
			Fail( "Every bone is a helper",
				"Twist, IK, aim and hold bones take no weight of their own, and the rig has nothing else.",
				"Add the limb bones those helpers belong to" );
		}

		var twists = Twist.Value && ctx.Rig.Bones.Any( b => RigBuild.IsTwistBone( b.Name ) );

		foreach ( var body in targets )
		{
			var mesh = body.Mesh;
			SkinWeights weights;
			BindReport heatReport = null;

			if ( method == "Heat" && HeatBinder is not null )
			{
				weights = HeatBinder( mesh, deform );
			}
			else if ( method == "Heat" )
			{
				weights = AutoWeights.Heat( mesh, deform, out heatReport );
				LastHeatReport = heatReport;
			}
			else if ( method == "Rigid" )
			{
				weights = SkinBinder.BindRigid( mesh, deform );
			}
			else
			{
				weights = SkinBinder.BindSmooth( mesh, deform, Falloff.Clamped );
			}

			if ( heatReport is not null )
				Warning = heatReport.ToString();

			if ( weights is null || weights.Count != mesh.VertexCount )
				Fail( "The weights did not come back for every vertex", $"{body.Name}: {weights?.Count ?? 0} of {mesh.VertexCount}.", "Use Smooth" );

			if ( method == "Smooth" && Smoothing.Clamped > 0 )
				weights = SkinBinder.SmoothWeights( mesh, weights, Smoothing.Clamped );

			// Back to the full rig's bone numbers.
			for ( var v = 0; v < weights.Count; v++ )
				weights[v] = weights[v].Select( w => new BoneWeight( map[w.Bone], w.Weight ) ).ToArray();

			mesh.Skin = weights;

			if ( twists )
				TwistWeights.Spread( mesh, ctx.Rig );

			for ( var v = 0; v < mesh.Skin.Count; v++ )
				mesh.Skin[v] = SkinWeights.Prune( mesh.Skin[v], MaxInfluences.Clamped );

			WeightedVertices += mesh.VertexCount;
		}

		Warning = $"{WeightedVertices:N0} vertices on {targets.Count} bod{(targets.Count == 1 ? "y" : "ies")} weighted to {deform.Count} bones ({method})"
			+ (twists ? ", shared onto twist bones." : ".")
			+ (LastHeatReport is not null && method == "Heat" ? $" Bind report: {LastHeatReport}." : "");
	}

	/// <summary>Helpers that should take no weight directly: twist stations, IK targets, aim
	/// matrices, hold points, clothing attachments. Face bones (jaw, lids) DO take weight.</summary>
	public static bool IsHelper( string name )
	{
		if ( RigBuild.IsTwistBone( name ) )
			return true;

		var n = (name ?? "").ToLowerInvariant();
		return n.Contains( "helper" ) || n.Contains( "_ik" ) || n.StartsWith( "ik_" ) || n.Contains( "aim_" )
			|| n.StartsWith( "hold" ) || n.Contains( "_hold" ) || n.Contains( "clothing" );
	}

	/// <summary>
	/// What the binders measure distance to: one segment per bone that takes weight, head to the
	/// end of the limb it actually spans (<see cref="Skeleton.TryBodySpan"/>). That matters for an
	/// imported rig - citizen's bones run down +X with a one-inch +Y tail, and measuring to that
	/// inch hands an elbow's vertices to the wrong bone. The segments are flat roots: only their
	/// ends are read. <paramref name="map"/> takes a segment's index back to the rig's.
	/// </summary>
	public static Skeleton DeformSkeleton( Skeleton rig, out int[] map )
	{
		var deform = new Skeleton();
		var back = new List<int>();

		for ( var i = 0; i < rig.Count; i++ )
		{
			if ( IsHelper( rig.Bones[i].Name ) )
				continue;

			if ( !rig.TryBodySpan( i, out var head, out var tail ) )
			{
				head = rig.HeadWorld( i );
				tail = rig.TailWorld( i );
			}

			if ( (tail - head).Length < 1e-4f )
				tail = head + rig.WorldBind( i ).Y.Normal * 0.01f;

			deform.AddBoneFromPoints( rig.Bones[i].Name, -1, head, tail );
			back.Add( i );
		}

		map = back.ToArray();
		return deform;
	}
}

// ---- Hammer interop: rig / animate on map props (hammer-interop-rig-animate-map-props) ----
//
// A prop_dynamic / prop_physics entity can reference a rigged model + an animation
// clip (named clips, loop-seam check, root motion from GameClipPipeline); doors /
// movers animate through the I/O system driving clip playback; animated props
// export with their clips (see PropClipExport).
//
// Backend / data-model / API only — no Desktop UI. The rig lives here (this file),
// the map entities live in MapEntities.cs (read-only here), the I/O rows live in
// EntityIo.cs, the named clips live in ClipLibrary, the seam + root-motion rules
// live in GameClipPipeline.cs. This layer only binds them together.
//
// DEPENDENCIES, as landed on disk 2026-11-13 (read, not assumed):
//   * MapEntities (hammer-entity-place-edit-validate, DONE): MapEntities.cs.
//     Placement, keyvalues, targetnames, validation. This layer never edits it:
//     it reads MapEntity.Keyvalues ("model", "DefaultAnim") and calls the public
//     SetKeyvalue for SetDefaultAnimation (registry-checked: DefaultAnim is a
//     declared prop_dynamic keyvalue in core-source1.json).
//   * EntityRegistry (DONE): core-source1.json (14 classes). prop_dynamic
//     declares model / DefaultAnim / RandomAnimation / DisableBoneFollowers and
//     inputs SetAnimation / SetDefaultAnimation / TurnOn / TurnOff / Enable /
//     Disable; prop_physics declares model only and NO animation inputs;
//     prop_static declares model only and NO inputs; func_door declares inputs
//     Open / Close / Toggle / Lock / Unlock; func_button declares Press / Lock /
//     Unlock. The supported-input lists below mirror that file (schemaVersion 1);
//     a registry addition needs the list extended — the validator cross-checks
//     every binding's input against the registry, so a stale list is caught.
//   * EntityIo (DONE): EntityIoInput / EntityIoOutput rows. The player fires on
//     EntityIoInput: TargetEntity is a targetname (case-insensitive) or "#id".
//   * ClipLibrary + GameClipPipeline (DONE): named clips (Find / Resolve),
//     LoopSeamChecker.Check, RootMotionPolicy (RootBoneOf / Extract / Strip).
//
// SEAMS (owned elsewhere, documented not implemented here):
//   * Model / particle / sound registries (hammer-model-particle-sound-registries,
//     worker 61, LANDED 2026-11-13 while this ticket was being written):
//     kernel/Effigy/Map/AssetRegistries.cs — ModelEntry (Name / Path /
//     Attachments / Scale / PropType Static|Dynamic|Physics), ParticleEntry,
//     SoundEntry, ModelAssetRegistry / ParticleAssetRegistry /
//     SoundAssetRegistry, AssetRegistries.PlaceModel (places a prop from the
//     registry through the entity API) and ValidateFiles (MISSING_MODEL when a
//     registered model path is not on disk). The "model exists" check lives
//     there and is NOT duplicated here: this layer validates the clip binding,
//     that layer validates the model path. What the registry does NOT carry is
//     a skeleton per model — ModelEntry has no rig reference — so there is
//     still no model-path -> skeleton lookup, and the caller supplies the
//     Skeleton the clips were authored for (one rig per validator / player
//     call). When a rig reference lands on ModelEntry (or the export resolves
//     the skeleton from the model's file), these calls take a resolver instead
//     of a skeleton. PropType.Dynamic maps to prop_dynamic through
//     AssetRegistries.ClassNameFor.
//   * Registry keyvalues for movers: core-source1.json has NO clip keyvalues on
//     func_door / func_button (no OpenClip / CloseClip), and MapEntities rejects
//     unknown keyvalues at placement — so door / button clip bindings live in
//     PropClipBindings (records keyed by entity id + input), not in keyvalues.
//     A future registry addition could add OpenClip / CloseClip keyvalues; this
//     layer would then read them in BindDefault's place. Do NOT invent keyvalues
//     here and place them through MapEntities — the registry will reject them.
//   * Export: MapBakeExport exports baked mesh (OBJ / GLB) and knows nothing
//     about entities or clips. PropClipExport.Manifest is the data a future
//     export step consumes (entity, model, clips, frames, root bone, seam) —
//     it writes no files and no clip bytes.
//
// LIMITS, stated plainly:
//   * One skeleton per validator / player call (see above). A map whose props
//     were authored on different rigs validates / plays them one rig at a time.
//   * A looping clip is seam-checked; a one-shot clip is not (its seam is the
//     cut back to idle, which the game handles). A clip whose motion lives only
//     in Lane K channel curves bakes its bind pose — the GameClipPipeline limit,
//     not this layer's.
//   * prop_physics can reference a rigged model (its model keyvalue is read for
//     the manifest) but cannot play clips: the registry gives it no animation
//     inputs, and physics owns its motion. prop_static cannot play clips either.

/// <summary>Named error from the prop rig / clip layer. <see cref="Code"/> is the
/// stable machine-readable name; the message says what to do about it.</summary>
public sealed class PropClipException : Exception
{
	public const string CodeUnknownClip = "UnknownClip";
	public const string CodeUnplayableClip = "UnplayableClip";
	public const string CodeNotAnimatableProp = "NotAnimatableProp";
	public const string CodeUnknownInput = "UnknownInput";
	public const string CodeUnsupportedInput = "UnsupportedInput";
	public const string CodeNoBinding = "NoBinding";
	public const string CodeUnknownTarget = "UnknownTarget";
	public const string CodeAmbiguousTarget = "AmbiguousTarget";
	public const string CodeInvalidRootMotion = "InvalidRootMotion";

	public string Code = "";

	public PropClipException( string code, string message ) : base( message ) { Code = code; }
}

/// <summary>One clip binding: which clip an entity plays, and on what trigger.
/// <see cref="Input"/> "" is the default / spawn clip (prop_dynamic DefaultAnim);
/// anything else is the I/O input that plays it ("Open", "Close", "Toggle",
/// "Press", "TurnOn"). <see cref="Loop"/> true means the clip must close its
/// loop seam (checked by <see cref="PropClipValidator"/>). <see cref="RootMotion"/>
/// is one of the RootMotion* constants below; anything else is rejected.</summary>
public sealed class PropClipBinding
{
	public const string RootMotionAsAuthored = "as-authored";
	public const string RootMotionExtract = "extract";
	public const string RootMotionStrip = "strip";
	public const string RootMotionInPlace = "inplace";

	public int EntityId;
	public string ClassName = "";
	public string Model = "";
	public string ClipName = "";
	public string Input = "";
	public bool Loop;
	public string RootMotion = RootMotionAsAuthored;

	public override string ToString() => string.IsNullOrEmpty( Input )
		? $"#{EntityId} {ClassName} default '{ClipName}'{( Loop ? " (loop)" : "" )}"
		: $"#{EntityId} {ClassName} on '{Input}' plays '{ClipName}'{( Loop ? " (loop)" : "" )}";
}

/// <summary>Clip bindings for one map: the default clip of a prop_dynamic (read
/// from its DefaultAnim keyvalue) and the input-driven clips of doors, buttons
/// and props. Bindings are records, not keyvalues — the registry has no clip
/// keyvalues on movers (see the file header). A later bind for the same entity +
/// input replaces the earlier one; binding never touches the entity.</summary>
public sealed class PropClipBindings
{
	readonly Dictionary<string, PropClipBinding> _byKey = new( StringComparer.OrdinalIgnoreCase );

	public IReadOnlyList<PropClipBinding> All => _byKey.Values.OrderBy( b => b.EntityId ).ThenBy( b => b.Input, StringComparer.OrdinalIgnoreCase ).ToArray();
	public int Count => _byKey.Count;

	static string Key( int entityId, string input ) => entityId + "|" + ( input ?? "" );

	/// <summary>Classes that can play clips at all, per core-source1.json v1:
	/// prop_dynamic (DefaultAnim + SetAnimation), func_door and func_button
	/// (movers driven through I/O). prop_static and prop_physics cannot.</summary>
	public static bool IsAnimatableClass( string className ) => className is "prop_dynamic" or "func_door" or "func_button";

	/// <summary>Inputs a clip may be bound to, per class. SetAnimation /
	/// SetDefaultAnimation take the clip as their I/O parameter at fire time,
	/// so they are played, not bound; TurnOff stops playback and takes no clip.</summary>
	public static IReadOnlyList<string> BindableInputs( string className ) => className switch
	{
		"prop_dynamic" => new[] { "TurnOn" },
		"func_door" => new[] { "Open", "Close", "Toggle" },
		"func_button" => new[] { "Press" },
		_ => Array.Empty<string>(),
	};

	/// <summary>The default / spawn clip of a prop_dynamic, from its DefaultAnim
	/// keyvalue. Returns null when the entity names no default clip — a prop
	/// with no default animation is valid. Throws NotAnimatableProp on any other
	/// class, UnknownClip when the named clip is not in the library.</summary>
	public PropClipBinding BindDefault( MapEntity entity, ClipLibrary clips, bool loop = false,
		string rootMotion = PropClipBinding.RootMotionAsAuthored )
	{
		if ( entity is null ) throw new ArgumentNullException( nameof( entity ) );
		if ( clips is null ) throw new ArgumentNullException( nameof( clips ) );
		if ( !string.Equals( entity.ClassName, "prop_dynamic", StringComparison.OrdinalIgnoreCase ) )
			throw new PropClipException( PropClipException.CodeNotAnimatableProp,
				$"Only prop_dynamic has a default animation clip: '{entity.ClassName}' does not. " +
				( string.Equals( entity.ClassName, "prop_static", StringComparison.OrdinalIgnoreCase )
					? "A prop_static is baked into the map and never animates."
					: string.Equals( entity.ClassName, "prop_physics", StringComparison.OrdinalIgnoreCase )
						? "A prop_physics is driven by physics, not by clips."
						: "Bind a clip to a prop_dynamic, a func_door or a func_button instead." ) );
		var clipName = ModelOf( entity, "DefaultAnim" );
		if ( clipName.Length == 0 ) return null;
		PropClipValidator.ResolveClip( clips, clipName );
		var binding = new PropClipBinding
		{
			EntityId = entity.Id,
			ClassName = entity.ClassName,
			Model = ModelOf( entity, "model" ),
			ClipName = clipName,
			Input = "",
			Loop = loop,
			RootMotion = NormaliseRootMotion( rootMotion ),
		};
		_byKey[Key( entity.Id, "" )] = binding;
		return binding;
	}

	/// <summary>Bind a clip to one I/O input of an entity: "Open" / "Close" /
	/// "Toggle" on a func_door, "Press" on a func_button, "TurnOn" on a
	/// prop_dynamic. Throws NotAnimatableProp on prop_static / prop_physics /
	/// anything else, UnknownInput for an input this class cannot bind a clip
	/// to, UnknownClip for a clip the library does not have.</summary>
	public PropClipBinding BindInput( MapEntity entity, string input, string clipName, ClipLibrary clips,
		bool loop = false, string rootMotion = PropClipBinding.RootMotionAsAuthored )
	{
		if ( entity is null ) throw new ArgumentNullException( nameof( entity ) );
		if ( clips is null ) throw new ArgumentNullException( nameof( clips ) );
		if ( !IsAnimatableClass( entity.ClassName ) )
			throw new PropClipException( PropClipException.CodeNotAnimatableProp,
				$"Cannot bind a clip to '{entity.ClassName}': it cannot play clips. " +
				( string.Equals( entity.ClassName, "prop_static", StringComparison.OrdinalIgnoreCase )
					? "A prop_static is baked into the map and never animates."
					: string.Equals( entity.ClassName, "prop_physics", StringComparison.OrdinalIgnoreCase )
						? "A prop_physics is driven by physics, not by clips — bind the clip to a prop_dynamic instead."
						: "Bind a clip to a prop_dynamic, a func_door or a func_button instead." ) );
		if ( string.IsNullOrWhiteSpace( input ) || !BindableInputs( entity.ClassName ).Contains( input, StringComparer.OrdinalIgnoreCase ) )
			throw new PropClipException( PropClipException.CodeUnknownInput,
				$"Cannot bind a clip to input '{input}' on '{entity.ClassName}': " +
				$"that class binds clips to {string.Join( ", ", BindableInputs( entity.ClassName ).Select( i => "'" + i + "'" ) )} " +
				"(SetAnimation / SetDefaultAnimation take the clip as their I/O parameter instead)." );
		if ( string.IsNullOrWhiteSpace( clipName ) )
			throw new PropClipException( PropClipException.CodeUnknownClip, "Cannot bind an unnamed clip: name the clip to play." );
		PropClipValidator.ResolveClip( clips, clipName );
		var binding = new PropClipBinding
		{
			EntityId = entity.Id,
			ClassName = entity.ClassName,
			Model = ModelOf( entity, "model" ),
			ClipName = clipName,
			Input = input,
			Loop = loop,
			RootMotion = NormaliseRootMotion( rootMotion ),
		};
		_byKey[Key( entity.Id, input )] = binding;
		return binding;
	}

	/// <summary>The binding for one entity + input ("" = default clip), or null.</summary>
	public PropClipBinding Find( int entityId, string input ) =>
		_byKey.TryGetValue( Key( entityId, input ), out var binding ) ? binding : null;

	/// <summary>All bindings for one entity, default clip first.</summary>
	public IReadOnlyList<PropClipBinding> ForEntity( int entityId ) =>
		_byKey.Values.Where( b => b.EntityId == entityId ).OrderBy( b => b.Input.Length ).ThenBy( b => b.Input, StringComparer.OrdinalIgnoreCase ).ToArray();

	/// <summary>Drop one binding. Returns false when there was none.</summary>
	public bool Remove( int entityId, string input ) => _byKey.Remove( Key( entityId, input ) );

	internal static string ModelOf( MapEntity entity, string key ) =>
		entity.Keyvalues.TryGetValue( key, out var value ) ? value.Trim() : "";

	internal static string NormaliseRootMotion( string mode )
	{
		var m = ( mode ?? "" ).Trim().ToLowerInvariant();
		if ( m is PropClipBinding.RootMotionAsAuthored or PropClipBinding.RootMotionExtract
			or PropClipBinding.RootMotionStrip or PropClipBinding.RootMotionInPlace )
			return m;
		throw new PropClipException( PropClipException.CodeInvalidRootMotion,
			$"Unknown root-motion mode '{mode}': use 'as-authored', 'extract', 'strip' or 'inplace'." );
	}
}

/// <summary>Severity of one <see cref="PropClipIssue"/>.</summary>
public enum PropClipIssueSeverity
{
	Error,
	Warning
}

/// <summary>One validation finding from <see cref="PropClipValidator"/>: a named
/// clip that does not exist, a looping clip whose seam pops, a clip that cannot
/// be resolved, a bound clip on a class that cannot play clips, or an animated
/// prop with no model. <see cref="EntityId"/> is the placed entity's stable id,
/// or 0 when the finding is not about one entity.</summary>
public sealed class PropClipIssue
{
	public const string CodeUnknownClip = "UnknownClip";
	public const string CodeLoopSeamPops = "LoopSeamPops";
	public const string CodeUnplayableClip = "UnplayableClip";
	public const string CodeMissingModel = "MissingModel";
	public const string CodeNotAnimatableProp = "NotAnimatableProp";
	public const string CodeUnknownInput = "UnknownInput";

	public PropClipIssueSeverity Severity;
	public string Code = "";
	public string Message = "";
	public int EntityId;
	public string Key = "";

	public PropClipIssue() { }
	public PropClipIssue( PropClipIssueSeverity severity, string code, string message, int entityId = 0, string key = "" )
	{
		Severity = severity; Code = code; Message = message; EntityId = entityId; Key = key;
	}

	public override string ToString() => string.IsNullOrEmpty( Key )
		? $"[{Severity} {Code}] {Message}"
		: $"[{Severity} {Code}] {Message} (entity #{EntityId}, key '{Key}')";
}

/// <summary>Clip validation for map props: a named clip must exist in the library,
/// resolve to a playable clip, and — when the binding loops — close its loop seam
/// (<see cref="LoopSeamChecker"/>). A prop_dynamic naming a DefaultAnim that is
/// not in the library is an error; an animated prop with no model is a warning
/// (the registry does not require a model, so the map still opens).</summary>
public static class PropClipValidator
{
	/// <summary>The clip a name plays, resolved through the library (trim /
	/// scale / repeat applied). Throws UnknownClip when the library has no such
	/// name, UnplayableClip when the entry cannot resolve.</summary>
	public static RigAnimFile ResolveClip( ClipLibrary clips, string clipName )
	{
		if ( clips is null ) throw new ArgumentNullException( nameof( clips ) );
		if ( string.IsNullOrWhiteSpace( clipName ) )
			throw new PropClipException( PropClipException.CodeUnknownClip, "No clip is named." );
		var entry = clips.Find( clipName );
		if ( entry is null )
			throw new PropClipException( PropClipException.CodeUnknownClip,
				$"No clip named '{clipName}' is in the library ({clips.Count} clip(s): {string.Join( ", ", clips.Entries.Select( e => "'" + e.Name + "'" ) )})." );
		try
		{
			return entry.Resolve();
		}
		catch ( Exception ex )
		{
			throw new PropClipException( PropClipException.CodeUnplayableClip,
				$"Clip '{clipName}' cannot be resolved: {ex.Message}" );
		}
	}

	/// <summary>Validate one binding: the clip exists and resolves, and — when
	/// it loops — its seam closes on the given skeleton. A binding on a class
	/// that cannot play clips, or an input the registry does not declare for
	/// that class, is an error. The binding's own entity is not needed: the
	/// class name rides on the binding.</summary>
	public static IReadOnlyList<PropClipIssue> ValidateBinding( PropClipBinding binding, ClipLibrary clips,
		Skeleton skeleton, EntityRegistry registry = null )
	{
		var issues = new List<PropClipIssue>();
		if ( binding is null ) throw new ArgumentNullException( nameof( binding ) );
		if ( clips is null ) throw new ArgumentNullException( nameof( clips ) );
		if ( skeleton is null ) throw new ArgumentNullException( nameof( skeleton ) );

		if ( !PropClipBindings.IsAnimatableClass( binding.ClassName ) )
		{
			issues.Add( new PropClipIssue( PropClipIssueSeverity.Error, PropClipIssue.CodeNotAnimatableProp,
				$"Binding '{binding.ClipName}' is on '{binding.ClassName}', which cannot play clips.", binding.EntityId ) );
			return issues;
		}

		if ( binding.Input.Length > 0 && registry is not null )
		{
			var cls = registry.Find( binding.ClassName );
			if ( cls is not null && cls.FindInput( binding.Input ) is null )
			{
				issues.Add( new PropClipIssue( PropClipIssueSeverity.Error, PropClipIssue.CodeUnknownInput,
					$"Input '{binding.Input}' is not declared for '{binding.ClassName}' in the entity registry.", binding.EntityId, binding.Input ) );
				return issues;
			}
		}

		RigAnimFile clip;
		try
		{
			clip = ResolveClip( clips, binding.ClipName );
		}
		catch ( PropClipException ex )
		{
			issues.Add( new PropClipIssue( PropClipIssueSeverity.Error,
				ex.Code == PropClipException.CodeUnknownClip ? PropClipIssue.CodeUnknownClip : PropClipIssue.CodeUnplayableClip,
				ex.Message, binding.EntityId, binding.Input.Length == 0 ? "DefaultAnim" : binding.Input ) );
			return issues;
		}

		if ( binding.Loop )
		{
			var seam = LoopSeamChecker.Check( clip, skeleton );
			if ( !seam.Pass )
				issues.Add( new PropClipIssue( PropClipIssueSeverity.Error, PropClipIssue.CodeLoopSeamPops,
					$"Looping clip '{binding.ClipName}' does not close its loop seam: {seam}.", binding.EntityId,
					binding.Input.Length == 0 ? "DefaultAnim" : binding.Input ) );
		}

		return issues;
	}

	/// <summary>Validate every animated prop in the map, in placement order:
	/// each stored binding (ValidateBinding), each prop_dynamic whose
	/// DefaultAnim names a clip with no stored default binding, and each
	/// prop_dynamic / prop_physics / func_door / func_button with no model
	/// (warning — the map still opens, but there is nothing to animate).</summary>
	public static IReadOnlyList<PropClipIssue> Validate( MapEntities entities, PropClipBindings bindings,
		ClipLibrary clips, Skeleton skeleton )
	{
		if ( entities is null ) throw new ArgumentNullException( nameof( entities ) );
		if ( bindings is null ) throw new ArgumentNullException( nameof( bindings ) );
		if ( clips is null ) throw new ArgumentNullException( nameof( clips ) );
		if ( skeleton is null ) throw new ArgumentNullException( nameof( skeleton ) );

		var issues = new List<PropClipIssue>();
		var validatedDefault = new HashSet<int>();

		foreach ( var binding in bindings.All )
		{
			issues.AddRange( ValidateBinding( binding, clips, skeleton, entities.Registry ) );
			if ( binding.Input.Length == 0 )
				validatedDefault.Add( binding.EntityId );
		}

		foreach ( var entity in entities.Entities )
		{
			var isProp = string.Equals( entity.ClassName, "prop_dynamic", StringComparison.OrdinalIgnoreCase )
				|| string.Equals( entity.ClassName, "prop_physics", StringComparison.OrdinalIgnoreCase );

			if ( isProp && PropClipBindings.ModelOf( entity, "model" ).Length == 0 )
				issues.Add( new PropClipIssue( PropClipIssueSeverity.Warning, PropClipIssue.CodeMissingModel,
					$"Entity #{entity.Id} ('{entity.ClassName}') has no model, so there is nothing to animate.", entity.Id, "model" ) );

			if ( !string.Equals( entity.ClassName, "prop_dynamic", StringComparison.OrdinalIgnoreCase ) )
				continue;
			if ( validatedDefault.Contains( entity.Id ) )
				continue;

			var clipName = PropClipBindings.ModelOf( entity, "DefaultAnim" );
			if ( clipName.Length == 0 )
				continue;

			var ghost = new PropClipBinding
			{
				EntityId = entity.Id, ClassName = entity.ClassName,
				Model = PropClipBindings.ModelOf( entity, "model" ), ClipName = clipName, Input = "",
			};
			issues.AddRange( ValidateBinding( ghost, clips, skeleton, entities.Registry ) );
		}

		return issues;
	}
}

/// <summary>One entity's clip playback state: which clip is playing, at which
/// frame, whether it is still playing, and — for a door — whether it ended open.
/// <see cref="LastFiredOutput"/> is the registry output the finished clip fires
/// (prop_dynamic OnAnimationFinished, func_door OnFullyOpen / OnFullyClosed,
/// func_button OnIn), or "" while nothing has finished.</summary>
public sealed class PropClipPlayback
{
	public int EntityId;
	public string ClassName = "";
	public string ClipName = "";
	public string Input = "";
	public RigAnimFile Clip;
	public int Frame;
	public bool Playing;
	public bool Finished;
	public bool Loop;
	public bool DoorOpen;
	public string LastFiredOutput = "";

	public int LastFrame => Clip?.LastFrame ?? 0;
	public float TimeSeconds => Clip is null || Clip.FrameRate <= 0 ? 0f : Frame / (float)Clip.FrameRate;

	public override string ToString() => Clip is null
		? $"#{EntityId} {ClassName}: idle"
		: $"#{EntityId} {ClassName}: '{ClipName}' frame {Frame}/{LastFrame}{( Playing ? " playing" : Finished ? " finished" : " stopped" )}";
}

/// <summary>Headless I/O -&gt; clip playback: an <see cref="EntityIoInput"/> row
/// ("door_01", "Open") resolves its target entity and starts the bound clip, so
/// a door with an open clip plays the clip on the Open input — no render, no
/// viewer. SetAnimation plays the clip its parameter names; SetDefaultAnimation
/// re-points the prop's DefaultAnim keyvalue (registry-checked) and rebinds it;
/// TurnOn plays the default clip, TurnOff stops. Advance steps frames (a
/// looping clip wraps, a one-shot stops at its last frame and fires its
/// registry output); Sample returns the played local pose at the current frame
/// (<see cref="RigAnimFile.SampleLocal"/>), or null when nothing is playing.</summary>
public sealed class PropClipPlayer
{
	readonly MapEntities _entities;
	readonly ClipLibrary _clips;
	readonly PropClipBindings _bindings;
	readonly Skeleton _skeleton;
	readonly Dictionary<int, PropClipPlayback> _states = new();

	public PropClipPlayer( MapEntities entities, ClipLibrary clips, PropClipBindings bindings, Skeleton skeleton )
	{
		_entities = entities ?? throw new ArgumentNullException( nameof( entities ) );
		_clips = clips ?? throw new ArgumentNullException( nameof( clips ) );
		_bindings = bindings ?? throw new ArgumentNullException( nameof( bindings ) );
		_skeleton = skeleton ?? throw new ArgumentNullException( nameof( skeleton ) );
	}

	public MapEntities Entities => _entities;
	public ClipLibrary Clips => _clips;
	public PropClipBindings Bindings => _bindings;

	/// <summary>Fire one I/O row at its target. The target is a targetname
	/// (case-insensitive, Source-style) or "#id". Throws UnknownTarget when
	/// nothing has that name, AmbiguousTarget when several entities share it —
	/// the engine would fire all of them, and this headless player refuses to
	/// pick one silently.</summary>
	public PropClipPlayback Fire( EntityIoInput input )
	{
		if ( input is null ) throw new ArgumentNullException( nameof( input ) );
		var target = ( input.TargetEntity ?? "" ).Trim();
		if ( target.Length == 0 )
			throw new PropClipException( PropClipException.CodeUnknownTarget, "The I/O row names no target entity." );
		MapEntity entity;
		if ( target.StartsWith( "#", StringComparison.Ordinal ) && int.TryParse( target[1..], out var id ) )
		{
			entity = _entities.FindById( id )
				?? throw new PropClipException( PropClipException.CodeUnknownTarget, $"No entity with id {id}." );
		}
		else
		{
			var matches = _entities.FindByTargetName( target );
			if ( matches.Count == 0 )
				throw new PropClipException( PropClipException.CodeUnknownTarget,
					$"No entity is named '{target}': the I/O row has a red target." );
			if ( matches.Count > 1 )
				throw new PropClipException( PropClipException.CodeAmbiguousTarget,
					$"{matches.Count} entities are named '{target}': the I/O row is ambiguous." );
			entity = matches[0];
		}
		return Fire( entity.Id, input.Input, input.Parameter );
	}

	/// <summary>Fire one input on one entity by id. The clip comes from the
	/// binding (Open / Close / Toggle / Press / TurnOn), from the I/O
	/// parameter (SetAnimation / SetDefaultAnimation), or from the default
	/// clip (TurnOn with no TurnOn binding).</summary>
	public PropClipPlayback Fire( int entityId, string inputName, string parameter = "" )
	{
		var entity = _entities.FindById( entityId )
			?? throw new PropClipException( PropClipException.CodeUnknownTarget, $"No entity with id {entityId}." );
		var input = ( inputName ?? "" ).Trim();

		// The registry is the authority on which inputs a class accepts: an
		// undeclared input is rejected here, before any clip is touched, so
		// SetAnimation on a func_door (or Open on a prop_dynamic) fails with
		// the registry's own vocabulary instead of playing the wrong thing.
		var declared = _entities.Registry.Find( entity.ClassName );
		if ( declared is not null && declared.FindInput( input ) is null )
			throw new PropClipException( PropClipException.CodeUnknownInput,
				$"Input '{input}' is not declared for '{entity.ClassName}' in the entity registry." );

		if ( string.Equals( input, "SetAnimation", StringComparison.OrdinalIgnoreCase ) )
		{
			if ( string.IsNullOrWhiteSpace( parameter ) )
				throw new PropClipException( PropClipException.CodeUnknownClip,
					$"SetAnimation on entity #{entity.Id} ('{entity.ClassName}') names no clip: pass the clip name as the input parameter." );
			RequireAnimatable( entity );
			return Start( entity, input, parameter, loop: false, PropClipBinding.RootMotionAsAuthored );
		}

		if ( string.Equals( input, "SetDefaultAnimation", StringComparison.OrdinalIgnoreCase ) )
		{
			if ( string.IsNullOrWhiteSpace( parameter ) )
				throw new PropClipException( PropClipException.CodeUnknownClip,
					$"SetDefaultAnimation on entity #{entity.Id} ('{entity.ClassName}') names no clip: pass the clip name as the input parameter." );
			RequireAnimatable( entity );
			PropClipValidator.ResolveClip( _clips, parameter );
			_entities.SetKeyvalue( entity, "DefaultAnim", parameter );
			_bindings.BindDefault( entity, _clips );
			return State( entity.Id );
		}

		if ( string.Equals( input, "TurnOff", StringComparison.OrdinalIgnoreCase ) )
		{
			RequireAnimatable( entity );
			var stopped = State( entity.Id );
			stopped.Playing = false;
			return stopped;
		}

		if ( string.Equals( input, "TurnOn", StringComparison.OrdinalIgnoreCase ) )
		{
			RequireAnimatable( entity );
			var bound = _bindings.Find( entity.Id, "TurnOn" ) ?? _bindings.Find( entity.Id, "" );
			if ( bound is null )
			{
				var replayed = _bindings.BindDefault( entity, _clips );
				if ( replayed is null )
					throw new PropClipException( PropClipException.CodeNoBinding,
						$"TurnOn on entity #{entity.Id} ('{entity.ClassName}') has no clip to play: give it a DefaultAnim or bind a clip to TurnOn." );
				bound = replayed;
			}
			return Start( entity, "TurnOn", bound.ClipName, bound.Loop, bound.RootMotion );
		}

		if ( string.Equals( input, "Toggle", StringComparison.OrdinalIgnoreCase )
			&& string.Equals( entity.ClassName, "func_door", StringComparison.OrdinalIgnoreCase ) )
		{
			var state = State( entity.Id );
			var want = state.DoorOpen ? "Close" : "Open";
			var bound = _bindings.Find( entity.Id, want )
				?? throw new PropClipException( PropClipException.CodeNoBinding,
					$"Toggle on door #{entity.Id} has no '{want}' clip bound: bind '{want}' first." );
			return Start( entity, want, bound.ClipName, bound.Loop, bound.RootMotion );
		}

		if ( input is "Open" or "Close" or "Press" )
		{
			RequireAnimatable( entity );
			var bound = _bindings.Find( entity.Id, input )
				?? throw new PropClipException( PropClipException.CodeNoBinding,
					$"Input '{input}' on entity #{entity.Id} ('{entity.ClassName}') has no clip bound: bind '{input}' first." );
			return Start( entity, input, bound.ClipName, bound.Loop, bound.RootMotion );
		}

		// Reached only for an input the registry declares but that plays no
		// clip (Lock, Unlock, Enable, Disable, Wake, ...).
		throw new PropClipException( PropClipException.CodeUnsupportedInput,
			$"Input '{input}' on '{entity.ClassName}' is a real input, but it does not play a clip (it is not SetAnimation, Open, Close, Toggle, Press, TurnOn or TurnOff)." );
	}

	/// <summary>Step one entity's playback forward by whole frames. A looping
	/// clip wraps; a one-shot stops at its last frame, marks itself finished
	/// and fires its registry output (DoorOpen flips with it). No state, or a
	/// stopped clip, is a no-op.</summary>
	public void Advance( int entityId, int frames )
	{
		if ( frames < 0 ) throw new ArgumentException( "cannot step playback backwards", nameof( frames ) );
		if ( !_states.TryGetValue( entityId, out var state ) || !state.Playing || state.Clip is null )
			return;
		state.Frame += frames;
		var last = state.LastFrame;
		if ( state.Frame <= last )
			return;
		if ( state.Loop )
		{
			state.Frame = last <= 0 ? 0 : state.Frame % ( last + 1 );
			return;
		}
		state.Frame = last;
		state.Playing = false;
		state.Finished = true;
		state.LastFiredOutput = state.ClassName switch
		{
			"prop_dynamic" => "OnAnimationFinished",
			"func_door" when string.Equals( state.Input, "Open", StringComparison.OrdinalIgnoreCase ) => "OnFullyOpen",
			"func_door" when string.Equals( state.Input, "Close", StringComparison.OrdinalIgnoreCase ) => "OnFullyClosed",
			"func_button" => "OnIn",
			_ => "",
		};
		if ( string.Equals( state.ClassName, "func_door", StringComparison.OrdinalIgnoreCase ) )
			state.DoorOpen = string.Equals( state.Input, "Open", StringComparison.OrdinalIgnoreCase );
	}

	/// <summary>The played local pose at the current frame (keys + limits + IK,
	/// as played), or null when the entity has no clip playing.</summary>
	public Xform[] Sample( int entityId, Skeleton skeleton )
	{
		if ( skeleton is null ) throw new ArgumentNullException( nameof( skeleton ) );
		if ( !_states.TryGetValue( entityId, out var state ) || state.Clip is null || !state.Playing && !state.Finished )
			return null;
		return state.Clip.SampleLocal( skeleton, state.Frame );
	}

	/// <summary>The entity's playback state, creating an idle one when there is
	/// none yet. Idle means no clip: Playing false, Clip null.</summary>
	public PropClipPlayback State( int entityId )
	{
		if ( !_states.TryGetValue( entityId, out var state ) )
		{
			var entity = _entities.FindById( entityId );
			state = new PropClipPlayback { EntityId = entityId, ClassName = entity?.ClassName ?? "" };
			_states[entityId] = state;
		}
		return state;
	}

	/// <summary>Stop one entity's playback, keeping its frame and clip.</summary>
	public void Stop( int entityId )
	{
		if ( _states.TryGetValue( entityId, out var state ) )
			state.Playing = false;
	}

	void RequireAnimatable( MapEntity entity )
	{
		if ( !PropClipBindings.IsAnimatableClass( entity.ClassName ) )
			throw new PropClipException( PropClipException.CodeNotAnimatableProp,
				$"Entity #{entity.Id} ('{entity.ClassName}') cannot play clips." );
	}

	PropClipPlayback Start( MapEntity entity, string input, string clipName, bool loop, string rootMotion )
	{
		var clip = PropClipValidator.ResolveClip( _clips, clipName );
		clip = ApplyRootMotion( clip, rootMotion );
		var state = State( entity.Id );
		state.ClassName = entity.ClassName;
		state.ClipName = clipName;
		state.Input = input;
		state.Clip = clip;
		state.Frame = 0;
		state.Playing = true;
		state.Finished = false;
		state.Loop = loop;
		state.LastFiredOutput = "";
		return state;
	}

	/// <summary>Apply the binding's root-motion mode to a freshly resolved copy
	/// (Resolve returns a new clip each call, so the library entry is never
	/// rewritten): extract moves the travel onto the root bone, strip re-keys
	/// the root in place, inplace sets PlayInPlace so the game moves the prop
	/// by the travel, as-authored leaves the clip alone.</summary>
	RigAnimFile ApplyRootMotion( RigAnimFile clip, string rootMotion )
	{
		var mode = PropClipBindings.NormaliseRootMotion( rootMotion );
		switch ( mode )
		{
			case PropClipBinding.RootMotionExtract:
			{
				var bone = RootMotionPolicy.RootBoneOf( clip, _skeleton );
				if ( bone.Length == 0 )
					throw new PropClipException( PropClipException.CodeInvalidRootMotion,
						$"Cannot extract root motion from '{clip.SourceModel}': no root bone is named (clip RootMotion.Bone is empty and the skeleton has no root bone)." );
				// The travel source rides on the clip's own root-motion settings
				// (Source = the hips / pelvis when the travel is keyed there);
				// an empty source means the travel is already on the root.
				var result = RootMotionPolicy.Extract( clip, _skeleton,
					new RootMotionSettings { Bone = bone, Source = clip.RootMotion?.Source ?? "", Enabled = true } );
				if ( result.Error is not null )
					throw new PropClipException( PropClipException.CodeInvalidRootMotion,
						$"Cannot extract root motion: {result.Error}" );
				return clip;
			}
			case PropClipBinding.RootMotionStrip:
			{
				var result = RootMotionPolicy.Strip( clip, _skeleton );
				if ( result.Error is not null )
					throw new PropClipException( PropClipException.CodeInvalidRootMotion,
						$"Cannot strip root motion: {result.Error}" );
				return clip;
			}
			case PropClipBinding.RootMotionInPlace:
				clip.PlayInPlace = true;
				return clip;
			default:
				return clip;
		}
	}
}

/// <summary>One row of the animated-prop export: which entity, which model, which
/// clip on which input, how long it plays, which bone carries its root motion,
/// and whether its loop seam closes. The manifest is data, not a file:
/// MapBakeExport owns the OBJ / GLB writing; a future export step consumes
/// these rows so animated props export WITH their clips instead of as static
/// geometry.</summary>
public sealed class PropClipExportRow
{
	public int EntityId;
	public string ClassName = "";
	public string TargetName = "";
	public string Model = "";
	public string ClipName = "";
	public string Input = "";
	public bool Loop;
	public int Frames;
	public int FrameRate;
	public string RootBone = "";
	public bool HasRootMotion;
	public bool SeamPass;
	public string SeamSummary = "";

	public override string ToString() => string.IsNullOrEmpty( Input )
		? $"#{EntityId} {ClassName} '{TargetName}' model '{Model}' default clip '{ClipName}' {Frames}f @{FrameRate}fps root '{RootBone}' seam {( SeamPass ? "closed" : "POPS" )}"
		: $"#{EntityId} {ClassName} '{TargetName}' model '{Model}' on '{Input}' clip '{ClipName}' {Frames}f @{FrameRate}fps root '{RootBone}' seam {( SeamPass ? "closed" : "POPS" )}";
}

/// <summary>Animated props export with their clips: one row per binding, plus one
/// row per prop_dynamic DefaultAnim that has no stored default binding. Throws
/// UnknownClip / UnplayableClip when a row's clip cannot be resolved — validate
/// first (<see cref="PropClipValidator.Validate"/>), export second.</summary>
public static class PropClipExport
{
	public static IReadOnlyList<PropClipExportRow> Manifest( MapEntities entities, PropClipBindings bindings,
		ClipLibrary clips, Skeleton skeleton )
	{
		if ( entities is null ) throw new ArgumentNullException( nameof( entities ) );
		if ( bindings is null ) throw new ArgumentNullException( nameof( bindings ) );
		if ( clips is null ) throw new ArgumentNullException( nameof( clips ) );
		if ( skeleton is null ) throw new ArgumentNullException( nameof( skeleton ) );

		var rows = new List<PropClipExportRow>();
		var coveredDefault = new HashSet<int>();

		foreach ( var binding in bindings.All )
		{
			rows.Add( RowFor( entities.FindById( binding.EntityId ), binding, clips, skeleton ) );
			if ( binding.Input.Length == 0 )
				coveredDefault.Add( binding.EntityId );
		}

		foreach ( var entity in entities.Entities )
		{
			if ( !string.Equals( entity.ClassName, "prop_dynamic", StringComparison.OrdinalIgnoreCase ) )
				continue;
			if ( coveredDefault.Contains( entity.Id ) )
				continue;
			var clipName = PropClipBindings.ModelOf( entity, "DefaultAnim" );
			if ( clipName.Length == 0 )
				continue;
			rows.Add( RowFor( entity, new PropClipBinding
			{
				EntityId = entity.Id, ClassName = entity.ClassName,
				Model = PropClipBindings.ModelOf( entity, "model" ), ClipName = clipName, Input = "",
			}, clips, skeleton ) );
		}

		return rows;
	}

	static PropClipExportRow RowFor( MapEntity entity, PropClipBinding binding, ClipLibrary clips, Skeleton skeleton )
	{
		var clip = PropClipValidator.ResolveClip( clips, binding.ClipName );
		var seam = binding.Loop ? LoopSeamChecker.Check( clip, skeleton ) : null;
		return new PropClipExportRow
		{
			EntityId = binding.EntityId,
			ClassName = binding.ClassName,
			TargetName = entity?.TargetName ?? "",
			Model = binding.Model,
			ClipName = binding.ClipName,
			Input = binding.Input,
			Loop = binding.Loop,
			Frames = clip.LastFrame + 1,
			FrameRate = clip.FrameRate,
			RootBone = RootMotionPolicy.RootBoneOf( clip, skeleton ),
			HasRootMotion = clip.RootMotion is { Enabled: true },
			SeamPass = seam?.Pass ?? true,
			SeamSummary = seam?.ToString() ?? "not a loop — no seam check",
		};
	}
}
