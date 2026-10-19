#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Effigy;

namespace Marionette.Desktop;

/// <summary>Result of a bounded, in-memory FBX take conversion. This is not a general FBX curve reader.</summary>
public sealed record FbxClipConversionResult(
	RigAnimFile? Clip,
	IReadOnlyList<FeasibilityIssue> Issues,
	string? TakeName,
	int ResampledFrames,
	bool HasResamplingLoss )
{
	public bool Success => Clip is not null && !Issues.Any( i => i.Severity == FeasibilityIssueSeverity.Error );
}

/// <summary>
/// Converts I3's synthetic channels or a strict binary FBX subset (one stack/layer, connected
/// XYZ linear/constant curves with explicit time range) to native local-bone keys. The input's
/// keyframes are FBX local T/R/S property values, not bind matrices or already-converted poses.
/// Each output frame is evaluated in the declared source axes before world-to-local derivation.
/// Cubic source curves have no approved tangent evaluation and are deliberately rejected.
/// </summary>
public static class FbxClipConverter
{
	const string InvalidData = "InvalidAnimationData";
	const string DuplicateTarget = "DuplicateTargetBone";
	const string UnsupportedCurve = "UnsupportedAnimationCurve";
	const string Resampling = "ResamplingLoss";

	public static FbxClipConversionResult Convert( byte[] fbxBytes, Skeleton skeleton, string? takeName = null, int frameRate = 30 )
	{
		ArgumentNullException.ThrowIfNull( fbxBytes );
		ArgumentNullException.ThrowIfNull( skeleton );
		if ( !FbxNode.IsBinary( fbxBytes ) )
			return RawError( InvalidData, takeName ?? "", "Only binary FBX is supported by this animation extractor." );
		try
		{
			return ConvertRaw( FbxNode.Parse( fbxBytes ), FbxScene.Read( fbxBytes ), skeleton, takeName, frameRate );
		}
		catch ( Exception e ) when ( e is FormatException or ArgumentException or OverflowException or IndexOutOfRangeException or InvalidOperationException )
		{
			return RawError( InvalidData, takeName ?? "", $"Malformed or unsupported FBX structure: {e.Message}" );
		}
	}

	static FbxClipConversionResult RawError( string feature, string target, string message ) =>
		new( null, new[] { new FeasibilityIssue( FeasibilityIssueSeverity.Error, feature, target, message ) }, target, 0, false );

	sealed record RawCurve( string Name, char Channel, long[] Times, float[] Values, FbxInterpolationMode[] Modes )
	{
		public float At( long ticks )
		{
			if ( ticks <= Times[0] ) return Values[0];
			for ( int i = 0; i < Times.Length - 1; i++ )
			{
				if ( ticks > Times[i + 1] ) continue;
				if ( Modes[i] == FbxInterpolationMode.Constant && ticks < Times[i + 1] ) return Values[i];
				float t = (float)((double)(ticks - Times[i]) / (Times[i + 1] - Times[i]));
				return Values[i] + (Values[i + 1] - Values[i]) * t;
			}
			return Values[^1];
		}
		public FbxInterpolationMode ModeAt( long ticks )
		{
			for ( int i = 0; i < Times.Length - 1; i++ )
				if ( ticks >= Times[i] && ticks < Times[i + 1] ) return Modes[i];
			return FbxInterpolationMode.Linear;
		}
	}

	static FbxClipConversionResult ConvertRaw( FbxNode root, FbxScene scene, Skeleton skeleton, string? requested, int frameRate )
	{
		var objects = root.Child( "Objects" );
		if ( objects is null ) return RawError( FbxAnimationContract.FeatureNoAnimation, requested ?? "", "FBX has no Objects section." );
		var issues = new List<FeasibilityIssue>();
		void Error( string feature, string target, string message ) =>
			issues.Add( new( FeasibilityIssueSeverity.Error, feature, target, message ) );
		var byId = new Dictionary<long, FbxNode>();
		foreach ( var obj in objects.Children )
		{
			if ( obj.Props.Count == 0 || obj.Props[0] is not long id ) continue;
			if ( !byId.TryAdd( id, obj ) ) Error( InvalidData, obj.ObjectName, "Duplicate FBX object ID." );
		}
		var connections = new List<(string Type, long Child, long Parent, string Property)>();
		foreach ( var c in root.Child( "Connections" )?.Children ?? new List<FbxNode>() )
		{
			if ( c.Name != "C" ) continue;
			if ( c.Props.Count < 3 || c.Props[0] is not string type || c.Props[1] is not long child || c.Props[2] is not long parent )
			{ Error( InvalidData, "Connections", "Malformed FBX connection." ); continue; }
			connections.Add( (type, child, parent, c.Props.Count > 3 ? c.Props[3] as string ?? "" : "") );
		}
		var stacks = objects.Children.Where( o => o.Name == "AnimationStack" ).ToList();
		if ( stacks.Count == 0 ) Error( FbxAnimationContract.FeatureNoAnimation, "", "No animation stacks." );
		if ( requested is null && stacks.Count > 1 ) Error( FbxAnimationContract.FeatureAmbiguousTake, "", "Select one of the animation stacks explicitly." );
		var matching = requested is null ? stacks : stacks.Where( s => s.ObjectName == requested ).ToList();
		if ( requested is not null && matching.Count != 1 ) Error( FbxAnimationContract.FeatureAmbiguousTake, requested, "Take must match exactly one animation stack." );
		if ( issues.Count > 0 ) return new( null, issues, requested, 0, false );
		var stack = matching[0];
		long stackId = (long)stack.Props[0];
		var layers = connections.Where( c => c.Type == "OO" && c.Parent == stackId && byId.TryGetValue( c.Child, out var n ) && n.Name == "AnimationLayer" ).ToList();
		if ( layers.Count != 1 ) Error( FbxAnimationContract.FeatureLayeredAnimation, stack.ObjectName, "Exactly one connected animation layer is required." );
		long start = ScalarTicks( stack, "LocalStart" ), stop = ScalarTicks( stack, "LocalStop" );
		if ( start < 0 || stop <= start ) Error( InvalidData, stack.ObjectName, "Explicit LocalStart/LocalStop time range is required." );
		if ( issues.Count > 0 ) return new( null, issues, stack.ObjectName, 0, false );
		long layerId = layers[0].Child;
		var layer = byId[layerId];
		foreach ( var p in layer.Child( "Properties70" )?.Children ?? new List<FbxNode>() )
		{
			if ( p.Name != "P" || p.Props.Count < 5 || p.Props[0] is not string name ) continue;
			if ( (name == "Weight" && System.Convert.ToDouble( p.Props[4] ) != 100) ||
				(name == "BlendMode" && System.Convert.ToInt32( p.Props[4] ) != 0) ||
				((name == "Mute" || name == "Solo") && System.Convert.ToInt32( p.Props[4] ) != 0) )
				Error( FbxAnimationContract.FeatureLayeredAnimation, stack.ObjectName, $"Non-default layer {name} is unsupported." );
		}
		var curveNodes = connections.Where( c => c.Type == "OO" && c.Parent == layerId && byId.TryGetValue( c.Child, out var n ) && n.Name == "AnimationCurveNode" ).Select( c => c.Child ).ToHashSet();
		if ( curveNodes.Count == 0 ) Error( UnsupportedCurve, stack.ObjectName, "Selected layer has no connected AnimationCurveNodes." );
		var models = objects.Children.Where( o => o.Name == "Model" ).ToDictionary( o => (long)o.Props[0] );
		var nodes = new Dictionary<long, SyntheticBoneNode>();
		var curves = new Dictionary<(long Model, string Property, char Channel), RawCurve>();
		foreach ( long curveNodeId in curveNodes )
		{
			if ( connections.Count( c => c.Type == "OO" && c.Child == curveNodeId && byId.TryGetValue( c.Parent, out var n ) && n.Name == "AnimationLayer" ) != 1 )
				Error( FbxAnimationContract.FeatureLayeredAnimation, byId[curveNodeId].ObjectName, "Curve node must belong to only the selected layer." );
			var targets = connections.Where( c => c.Type == "OP" && c.Child == curveNodeId && models.ContainsKey( c.Parent ) ).ToList();
			if ( targets.Count != 1 ) { Error( UnsupportedCurve, byId[curveNodeId].ObjectName, "Curve node must target exactly one Model property." ); continue; }
			var target = targets[0];
			if ( target.Property is not ("Lcl Translation" or "Lcl Rotation" or "Lcl Scaling") )
			{ Error( UnsupportedCurve, models[target.Parent].ObjectName, $"Unsupported animated property '{target.Property}'." ); continue; }
			var model = models[target.Parent];
			if ( !nodes.TryGetValue( target.Parent, out var node ) )
			{
				node = new SyntheticBoneNode { Name = model.ObjectName };
				var parents = connections.Where( c => c.Type == "OO" && c.Child == target.Parent && models.ContainsKey( c.Parent ) ).ToList();
				if ( parents.Count > 1 ) Error( InvalidData, node.Name, "Multiple Model parents are unsupported." );
				node.ParentName = parents.Count == 1 ? models[parents[0].Parent].ObjectName : null;
				ValidateModel( model, node, Error );
				nodes.Add( target.Parent, node );
			}
			var connected = connections.Where( c => c.Type == "OP" && c.Parent == curveNodeId && byId.TryGetValue( c.Child, out var n ) && n.Name == "AnimationCurve" ).ToList();
			if ( connections.Any( c => c.Parent == curveNodeId && byId.TryGetValue( c.Child, out var n ) && n.Name == "AnimationCurve" && c.Type != "OP" ) )
				Error( UnsupportedCurve, node.Name, "Animation curves must use OP axis-channel connections." );
			if ( connected.Count == 0 ) Error( UnsupportedCurve, node.Name, $"{target.Property} has no connected curves." );
			foreach ( var connection in connected )
			{
				if ( connection.Property is not ("d|X" or "d|Y" or "d|Z") )
				{ Error( UnsupportedCurve, node.Name, $"Unsupported curve channel '{connection.Property}'." ); continue; }
				var key = (target.Parent, target.Property, connection.Property[^1]);
				if ( curves.ContainsKey( key ) ) { Error( DuplicateTarget, node.Name, $"Duplicate {target.Property}/{connection.Property} curve." ); continue; }
				var curve = byId[connection.Child];
				if ( !TryCurve( curve, start, stop, out var decoded, out string reason ) )
				{ Error( UnsupportedCurve, node.Name, $"Curve '{curve.ObjectName}': {reason}" ); continue; }
				curves[key] = decoded! with { Channel = connection.Property[^1] };
			}
		}
		if ( nodes.Count == 0 ) Error( UnsupportedCurve, stack.ObjectName, "No supported bone transform curves found." );
		foreach ( var (modelId, node) in nodes )
		{
			var parent = connections.FirstOrDefault( c => c.Type == "OO" && c.Child == modelId && models.ContainsKey( c.Parent ) );
			if ( parent != default && !nodes.ContainsKey( parent.Parent ) )
				Error( InvalidData, node.Name, $"Unanimated source parent '{node.ParentName}' cannot be assumed to match the target's bind placement." );
		}
		// We cannot silently discard animated objects outside the selected take, nor retarget names.
		foreach ( var model in nodes.Keys )
		{
			var node = nodes[model];
			var timeline = curves.Where( c => c.Key.Model == model ).SelectMany( c => c.Value.Times )
				.Append( start ).Append( stop ).Distinct().OrderBy( t => t ).ToArray();
			for ( int k = 0; k < timeline.Length; k++ )
			{
				long ticks = timeline[k];
				var defaults = new[] { PropertyVector( models[model], "Lcl Translation", Vec3.Zero ),
					PropertyVector( models[model], "Lcl Rotation", Vec3.Zero ), PropertyVector( models[model], "Lcl Scaling", Vec3.One ) };
				string[] property = { "Lcl Translation", "Lcl Rotation", "Lcl Scaling" };
				for ( int p = 0; p < 3; p++ )
				{
					float[] components = { defaults[p].x, defaults[p].y, defaults[p].z };
					for ( int axis = 0; axis < 3; axis++ )
						if ( curves.TryGetValue( (model, property[p], "XYZ"[axis]), out var curve ) ) components[axis] = curve.At( ticks );
					defaults[p] = new Vec3( components[0], components[1], components[2] );
				}
				var modes = curves.Where( c => c.Key.Model == model ).Select( c => c.Value.ModeAt( ticks ) ).Distinct().ToList();
				if ( k < timeline.Length - 1 && modes.Count > 1 )
					Error( FbxAnimationContract.FeatureUnsupportedInterpolation, node.Name, "Mixed per-channel interpolation within one segment cannot be represented by I3's transform key descriptor." );
				node.Keyframes.Add( new SyntheticKeyframe( ticks, defaults[0], defaults[1], defaults[2], modes.FirstOrDefault() ) );
			}
		}
		if ( issues.Count > 0 ) return new( null, issues, stack.ObjectName, 0, false );
		var data = new SyntheticFbxAnimationData
		{
			UnitScale = scene.Creator.StartsWith( "Effigy", StringComparison.Ordinal ) && Math.Abs( scene.UnitScale - 1 ) < 1e-9 ? 2.54 : scene.UnitScale,
			UpAxis = scene.Up, FrontAxis = scene.Front, CoordAxis = scene.Coord
		};
		var take = new SyntheticAnimationTake { Name = stack.ObjectName, StartTicks = start, StopTicks = stop, LayerCount = 1 };
		foreach ( var node in nodes.Values ) take.Nodes.Add( node );
		data.Takes.Add( take );
		return Convert( data, skeleton, stack.ObjectName, frameRate );
	}

	static long ScalarTicks( FbxNode node, string property )
	{
		var p = node.Child( "Properties70" )?.Children.FirstOrDefault( n => n.Name == "P" && n.Props.Count >= 5 && n.Props[0] as string == property );
		return p?.Props[4] is long value ? value : -1;
	}
	static Vec3 PropertyVector( FbxNode model, string property, Vec3 fallback )
	{
		var p = model.Child( "Properties70" )?.Children.FirstOrDefault( n => n.Name == "P" && n.Props.Count >= 7 && n.Props[0] as string == property );
		return p is null ? fallback : new Vec3( System.Convert.ToSingle( p.Props[4] ), System.Convert.ToSingle( p.Props[5] ), System.Convert.ToSingle( p.Props[6] ) );
	}
	static void ValidateModel( FbxNode model, SyntheticBoneNode node, Action<string, string, string> error )
	{
		foreach ( var property in new[] { "RotationPivot", "ScalingPivot", "RotationOffset", "ScalingOffset", "PreRotation", "PostRotation" } )
			if ( PropertyVector( model, property, Vec3.Zero ).LengthSquared > 1e-8f )
				error( property is "RotationPivot" or "ScalingPivot" or "RotationOffset" or "ScalingOffset" ? FbxAnimationContract.FeatureUnbakedPivot : FbxAnimationContract.FeaturePrePostRotation,
					node.Name, $"Nonzero {property} is unsupported." );
		var props = model.Child( "Properties70" )?.Children ?? new List<FbxNode>();
		foreach ( var prop in props.Where( p => p.Name == "P" && p.Props.Count >= 5 ) )
		{
			var name = prop.Props[0] as string;
			if ( name == "RotationOrder" && System.Convert.ToInt32( prop.Props[4] ) != 0 )
				error( FbxAnimationContract.FeatureCustomRotationOrder, node.Name, "Only EulerXYZ is supported." );
			if ( name == "InheritType" && System.Convert.ToInt32( prop.Props[4] ) != 0 )
				error( InvalidData, node.Name, "Only default parent transform inheritance is supported." );
		}
	}
	static bool TryCurve( FbxNode curve, long start, long stop, out RawCurve? result, out string reason )
	{
		result = null; reason = "";
		long[]? times = curve.Child( "KeyTime" )?.Props.FirstOrDefault() as long[];
		float[]? values = curve.Child( "KeyValueFloat" )?.Props.FirstOrDefault() as float[];
		int[]? flags = curve.Child( "KeyAttrFlags" )?.Ints();
		if ( times is null || values is null || flags is null || times.Length == 0 || times.Length != values.Length || times.Length != flags.Length )
		{ reason = "KeyTime(long[]), KeyValueFloat(float[]) and KeyAttrFlags(int[]) must have equal, nonzero lengths."; return false; }
		var modes = new FbxInterpolationMode[times.Length];
		for ( int i = 0; i < times.Length; i++ )
		{
			if ( times[i] < start || times[i] > stop || (i > 0 && times[i] <= times[i - 1]) || !float.IsFinite( values[i] ) )
			{ reason = "Keys must be finite, strictly ordered and inside the take range."; return false; }
			// FBX SDK interpolation bits: constant=0x2, linear=0x4, cubic=0x8.
			int bits = flags[i] & 0x0e;
			if ( bits is not (0x2 or 0x4) )
			{ reason = $"Unsupported interpolation flags 0x{flags[i]:X}; cubic tangents and unknown flags are not evaluated."; return false; }
			modes[i] = bits == 0x2 ? FbxInterpolationMode.Constant : FbxInterpolationMode.Linear;
		}
		result = new RawCurve( curve.ObjectName, '?', times, values, modes );
		return true;
	}

	public static FbxClipConversionResult Convert( SyntheticFbxAnimationData data, Skeleton skeleton,
		string? takeName = null, int frameRate = 30 )
	{
		ArgumentNullException.ThrowIfNull( data );
		ArgumentNullException.ThrowIfNull( skeleton );
		var issues = new List<FeasibilityIssue>();
		void Error( string feature, string target, string message ) =>
			issues.Add( new FeasibilityIssue( FeasibilityIssueSeverity.Error, feature, target, message ) );
		if ( frameRate <= 0 || frameRate > 240 ) Error( InvalidData, takeName ?? "", "Output frame rate must be in 1..240." );
		if ( data.Takes.Count == 0 ) Error( FbxAnimationContract.FeatureNoAnimation, "", "No take exists." );
		var matches = takeName is null ? data.Takes : data.Takes.Where( t => t.Name == takeName ).ToList();
		if ( takeName is null && matches.Count > 1 ) Error( FbxAnimationContract.FeatureAmbiguousTake, "", "Select a take explicitly." );
		if ( takeName is not null && matches.Count != 1 ) Error( FbxAnimationContract.FeatureAmbiguousTake, takeName, "Take name must match exactly one take." );
		if ( issues.Count > 0 ) return new( null, issues, takeName, 0, false );

		var take = matches[0];
		if ( take.LayerCount != 1 ) Error( FbxAnimationContract.FeatureLayeredAnimation, take.Name, "Exactly one layer is required." );
		if ( take.StopTicks < take.StartTicks || take.StartTicks < 0 )
			Error( InvalidData, take.Name, "Invalid take time range." );
		if ( !double.IsFinite( data.UnitScale ) || data.UnitScale <= 0 )
			Error( InvalidData, take.Name, "Invalid unit scale." );
		var axes = new[] { data.CoordAxis, data.UpAxis, data.FrontAxis };
		if ( axes.Any( a => a.Axis is < 0 or > 2 || Math.Abs( a.Sign ) != 1 ) || axes.Select( a => a.Axis ).Distinct().Count() != 3 )
			Error( InvalidData, take.Name, "Axes must be three distinct signed basis directions." );

		var bones = new Dictionary<string, int>( StringComparer.Ordinal );
		for ( int i = 0; i < skeleton.Count; i++ )
			if ( !bones.TryAdd( skeleton.Bones[i].Name, i ) )
				Error( DuplicateTarget, skeleton.Bones[i].Name, "Target skeleton contains duplicate bone names." );
		var nodes = new Dictionary<string, SyntheticBoneNode>( StringComparer.Ordinal );
		foreach ( var node in take.Nodes )
		{
			if ( string.IsNullOrWhiteSpace( node.Name ) || !nodes.TryAdd( node.Name, node ) )
				Error( DuplicateTarget, node.Name, "Animation contains a duplicate or empty node name." );
			if ( !bones.ContainsKey( node.Name ) )
				Error( FbxAnimationContract.FeatureMissingTargetBone, node.Name, "Animated node has no exact-name target in the skeleton." );
			if ( node.RotationOrder != FbxRotationOrder.EulerXYZ )
				Error( FbxAnimationContract.FeatureCustomRotationOrder, node.Name, "Only EulerXYZ is supported." );
			if ( node.RotationPivot.LengthSquared > 1e-8f || node.ScalingPivot.LengthSquared > 1e-8f )
				Error( FbxAnimationContract.FeatureUnbakedPivot, node.Name, "Unbaked pivots are unsupported." );
			if ( node.PreRotation.LengthSquared > 1e-8f || node.PostRotation.LengthSquared > 1e-8f )
				Error( FbxAnimationContract.FeaturePrePostRotation, node.Name, "Pre/post rotations are unsupported." );
			if ( node.Keyframes.Count == 0 ) Error( InvalidData, node.Name, "Animated node has no keys." );
			long previous = long.MinValue;
			foreach ( var key in node.Keyframes )
			{
				if ( key.TimeTicks <= previous || key.TimeTicks < take.StartTicks || key.TimeTicks > take.StopTicks )
					Error( InvalidData, node.Name, "Keys must be strictly ordered and inside the take." );
				previous = key.TimeTicks;
				if ( key.Interpolation == FbxInterpolationMode.Cubic )
					Error( FbxAnimationContract.FeatureUnsupportedInterpolation, node.Name, "Cubic tangents are unavailable in the I3 key descriptor." );
				if ( key.Interpolation is not (FbxInterpolationMode.Linear or FbxInterpolationMode.Constant) ||
					!Finite( key.Translation ) || !Finite( key.RotationEuler ) || !Finite( key.Scale ) ||
					key.Scale.x <= 0 || key.Scale.y <= 0 || key.Scale.z <= 0 ||
					MathF.Abs( key.Scale.x - key.Scale.y ) > 1e-5f || MathF.Abs( key.Scale.x - key.Scale.z ) > 1e-5f )
					Error( InvalidData, node.Name, "Only finite, positive uniform-scale linear/stepped TRS keys are supported." );
			}
			if ( bones.TryGetValue( node.Name, out int index ) )
			{
				string? targetParent = skeleton.Bones[index].Parent < 0 ? null : skeleton.Bones[skeleton.Bones[index].Parent].Name;
				if ( node.ParentName != targetParent )
					Error( InvalidData, node.Name, $"Source parent '{node.ParentName}' does not match target parent '{targetParent}'." );
			}
		}
		if ( issues.Count > 0 ) return new( null, issues, take.Name, 0, false );
		var duration = FbxAnimationTime.TicksToSeconds( take.StopTicks - take.StartTicks );
		if ( duration * frameRate > 100_000 || !double.IsFinite( duration ) )
		{
			Error( InvalidData, take.Name, "Take exceeds 100000 output frames." );
			return new( null, issues, take.Name, 0, false );
		}

		// Preserve the exact end sample, even when the duration is not an integer frame span.
		int last = (int)Math.Ceiling( duration * frameRate );
		var clip = new RigAnimFile { SourceModel = "", FrameRate = frameRate, FrameCount = last + 1 };
		var tracks = nodes.Values.Select( n => new RigTrack { Name = n.Name, Kind = RigTrackKind.Bone } ).ToDictionary( t => t.Name, StringComparer.Ordinal );
		foreach ( var track in tracks.Values ) clip.Tracks.Add( track );
		var axis = FbxImport.AxisMap( new FbxScene { Up = data.UpAxis, Front = data.FrontAxis, Coord = data.CoordAxis } );
		float unit = (float)(data.UnitScale / FbxExportAdapter.InchesToCentimetres);
		bool lossy = duration * frameRate != last;
		foreach ( var node in nodes.Values )
			lossy |= node.Keyframes.Any( k => Math.Abs( (FbxAnimationTime.TicksToSeconds( k.TimeTicks - take.StartTicks ) * frameRate) -
				Math.Round( FbxAnimationTime.TicksToSeconds( k.TimeTicks - take.StartTicks ) * frameRate ) ) > 1e-6 );
		for ( int frame = 0; frame <= last; frame++ )
		{
			long ticks = frame == last ? take.StopTicks : take.StartTicks + FbxAnimationTime.SecondsToTicks( (double)frame / frameRate );
			var world = new Xform[skeleton.Count];
			for ( int b = 0; b < skeleton.Count; b++ )
			{
				var bone = skeleton.Bones[b];
				int parent = bone.Parent;
				Xform local;
				if ( nodes.TryGetValue( bone.Name, out var node ) )
				{
					var source = Evaluate( node, ticks );
					var sourceLocal = FbxAnimationMath.EulerToQuat( source.RotationEuler.x, source.RotationEuler.y, source.RotationEuler.z )
						.ToXform( source.Translation * unit, source.Scale );
						// Basis change A * M * A^-1 (not a second mesh axis correction).
						var childWorld = (parent < 0 ? Xform.Identity : world[parent]) * (axis * sourceLocal * axis.Inverse);
						local = FbxAnimationMath.DeriveLocal( parent < 0 ? Xform.Identity : world[parent], childWorld );
						tracks[bone.Name].Keys.Add( new RigKey { Frame = frame, Local = RigPose.FromXform( local ), Ease = RigEase.Linear } );
				}
				else local = bone.Local;
				world[b] = (parent < 0 ? Xform.Identity : world[parent]) * local;
			}
		}
		if ( lossy ) issues.Add( new FeasibilityIssue( FeasibilityIssueSeverity.Warning, Resampling, take.Name,
			"Off-grid source key times/end time were sampled to output frames; fractional-time source interpolation may differ." ) );
		// Dense keys reproduce stepped sections at frame samples. Fractional interpolation of stepped
		// source segments needs stepped outgoing keys, except the final interval before a transition.
		foreach ( var node in nodes.Values )
			foreach ( var key in tracks[node.Name].Keys )
				if ( key.Frame < last && OutgoingMode( node, take.StartTicks + FbxAnimationTime.SecondsToTicks( (double)key.Frame / frameRate ) ) == FbxInterpolationMode.Constant )
					key.Ease = RigEase.Stepped;
		return new( clip, issues, take.Name, last + 1, lossy );
	}

	/// <summary>Writes only a successful conversion to a new path; never overwrites an existing clip/source.</summary>
	public static void WriteNew( FbxClipConversionResult result, string destination )
	{
		ArgumentNullException.ThrowIfNull( result );
		if ( !result.Success ) throw new InvalidOperationException( "Cannot write an unsuccessful FBX conversion." );
		if ( string.IsNullOrWhiteSpace( destination ) || !destination.EndsWith( ".riganim", StringComparison.OrdinalIgnoreCase ) )
			throw new ArgumentException( "An explicit .riganim destination is required.", nameof( destination ) );
		if ( File.Exists( destination ) ) throw new IOException( $"Destination already exists: {destination}" );
		string staging = destination + "." + Guid.NewGuid().ToString( "N" ) + ".tmp";
		try
		{
			byte[] bytes = new UTF8Encoding( false ).GetBytes( RigAnimWriter.Write( result.Clip! ) );
			using ( var stream = new FileStream( staging, FileMode.CreateNew, FileAccess.Write, FileShare.None ) )
			{
				stream.Write( bytes );
				stream.Flush( true );
			}
			File.Move( staging, destination, overwrite: false );
		}
		catch
		{
			if ( File.Exists( staging ) ) File.Delete( staging );
			throw;
		}
	}

	static bool Finite( Vec3 v ) => float.IsFinite( v.x ) && float.IsFinite( v.y ) && float.IsFinite( v.z );
	static FbxInterpolationMode OutgoingMode( SyntheticBoneNode node, long ticks )
	{
		var keys = node.Keyframes;
		for ( int i = 0; i < keys.Count - 1; i++ )
			if ( ticks >= keys[i].TimeTicks && ticks < keys[i + 1].TimeTicks ) return keys[i].Interpolation;
		return FbxInterpolationMode.Linear;
	}
	static SyntheticKeyframe Evaluate( SyntheticBoneNode node, long ticks )
	{
		var keys = node.Keyframes;
		if ( ticks <= keys[0].TimeTicks ) return keys[0];
		if ( ticks >= keys[^1].TimeTicks ) return keys[^1];
		for ( int i = 0; i < keys.Count - 1; i++ )
		{
			var a = keys[i]; var b = keys[i + 1];
			if ( ticks > b.TimeTicks ) continue;
			if ( a.Interpolation == FbxInterpolationMode.Constant && ticks < b.TimeTicks ) return a;
			float t = (float)((double)(ticks - a.TimeTicks) / (b.TimeTicks - a.TimeTicks));
			return a with { Translation = Vec3.Lerp( a.Translation, b.Translation, t ),
				RotationEuler = Vec3.Lerp( a.RotationEuler, b.RotationEuler, t ), Scale = Vec3.Lerp( a.Scale, b.Scale, t ) };
		}
		return keys[^1];
	}
}
