#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using Effigy;
using Marionette.Desktop;

internal static class FbxClipConversionTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action body ) => registry.Add( "fbx_clip_conversion", name, body );
		static void Yes( bool value, string message ) { if ( !value ) throw new Exception( message ); }
		static void Near( Vec3 a, Vec3 b, float tolerance = .002f ) =>
			Yes( (a - b).Length <= tolerance, $"Expected {b}; got {a}." );
		static SyntheticFbxAnimationData Reference()
		{
			var data = FbxAnimationReferenceFixture.CreateReferenceAnimationData();
			// I3's golden positions are expressed in the target's Z-up basis.
			data.UpAxis = (2, 1); data.FrontAxis = (1, -1); data.CoordAxis = (0, 1);
			return data;
		}
		var skeleton = FbxAnimationReferenceFixture.CreateReferenceSkeleton();
		Check( "golden local and world frames and fractional pose", () =>
		{
			var converted = FbxClipConverter.Convert( Reference(), skeleton );
			Yes( converted.Success && converted.Clip!.FrameCount == 31, "Reference conversion failed." );
			var clip = converted.Clip!;
			Near( RigAnimFile.ModelSpace( skeleton, clip.SampleLocal( skeleton, 0 ) )[1].Origin, FbxAnimationReferenceFixture.GoldenChildWorldPos0 );
			Near( RigAnimFile.ModelSpace( skeleton, clip.SampleLocal( skeleton, 15 ) )[1].Origin, FbxAnimationReferenceFixture.GoldenChildWorldPos15 );
			var world15 = RigAnimFile.ModelSpace( skeleton, clip.SampleLocal( skeleton, 15 ) )[1];
			var rotation = Quat.FromXform( world15 );
			var expectedRotation = FbxAnimationReferenceFixture.GoldenChildWorldRot15;
			Yes( MathF.Abs( MathF.Abs( rotation.x * expectedRotation.x + rotation.y * expectedRotation.y +
				rotation.z * expectedRotation.z + rotation.w * expectedRotation.w ) - 1 ) < .001f, "World rotation differs from independent golden." );
			Near( Quat.ScaleOf( world15 ), FbxAnimationReferenceFixture.GoldenChildWorldScale15 );
			Near( RigAnimFile.ModelSpace( skeleton, clip.SampleLocal( skeleton, 30 ) )[1].Origin, FbxAnimationReferenceFixture.GoldenChildWorldPos30 );
			Near( clip.SampleLocal( skeleton, 15 )[1].Origin, FbxAnimationReferenceFixture.GoldenChildLocalPos15 );
			Near( clip.SampleLocal( skeleton, 7.5f )[0].Origin, new Vec3( 2.5f, 5, 0 ) );
		} );
		Check( "source rate and asymmetric unit axis conversion", () =>
		{
			var data = Reference();
			data.UnitScale = 1;
			data.UpAxis = (1, 1); data.FrontAxis = (2, 1);
			var converted = FbxClipConverter.Convert( data, skeleton, frameRate: 60 );
			Yes( converted.Success && converted.Clip!.FrameCount == 61, "60fps conversion failed." );
			// FBX +Y up maps to target +Z; its +Z front maps to target -Y.
			Near( converted.Clip!.SampleLocal( skeleton, 30 )[0].Origin,
				new Vec3( 5 / 2.54f, 0, 10 / 2.54f ) );
		} );
		Check( "explicit take selection and missing target", () =>
		{
			var data = Reference();
			var second = FbxAnimationReferenceFixture.CreateReferenceAnimationData().Takes[0];
			second.Name = "Second";
			data.Takes.Add( second );
			Yes( FbxClipConverter.Convert( data, skeleton ).Issues.Any( i => i.Feature == FbxAnimationContract.FeatureAmbiguousTake ), "Ambiguous take accepted." );
			Yes( FbxClipConverter.Convert( data, skeleton, "Second" ).Success, "Named take rejected." );
			Yes( !FbxClipConverter.Convert( data, skeleton, "Absent" ).Success, "Unknown take accepted." );
			var partial = new Skeleton(); partial.AddBone( "root", -1, Xform.Identity, 1 );
			Yes( FbxClipConverter.Convert( Reference(), partial ).Issues.Any( i => i.Feature == FbxAnimationContract.FeatureMissingTargetBone && i.NodeOrTakeName == "child" ), "Missing target omitted." );
		} );
		Check( "reject layers pivots unsupported cubic and duplicate targets", () =>
		{
			var data = Reference();
			data.Takes[0].LayerCount = 2;
			data.Takes[0].Nodes[0].RotationPivot = new Vec3( 1, 0, 0 );
			data.Takes[0].Nodes[1].PreRotation = new Vec3( 0, 1, 0 );
			data.Takes[0].Nodes[0].Keyframes[0] = data.Takes[0].Nodes[0].Keyframes[0] with { Interpolation = FbxInterpolationMode.Cubic };
			data.Takes[0].Nodes.Add( new SyntheticBoneNode { Name = "root" } );
			var result = FbxClipConverter.Convert( data, skeleton );
			foreach ( var feature in new[] { FbxAnimationContract.FeatureLayeredAnimation, FbxAnimationContract.FeatureUnbakedPivot,
				FbxAnimationContract.FeaturePrePostRotation, FbxAnimationContract.FeatureUnsupportedInterpolation, "DuplicateTargetBone" } )
				Yes( result.Issues.Any( i => i.Feature == feature ), $"Missing {feature} diagnostic." );
			Yes( result.Clip is null, "Invalid input produced a clip." );
		} );
		Check( "stepped channel and off-grid resampling report", () =>
		{
			var data = Reference();
			var keys = data.Takes[0].Nodes[0].Keyframes;
			keys[0] = keys[0] with { Interpolation = FbxInterpolationMode.Constant };
			keys[1] = keys[1] with { TimeTicks = FbxAnimationTime.SecondsToTicks( .51 ) };
			var result = FbxClipConverter.Convert( data, skeleton );
			Yes( result.Success && result.HasResamplingLoss && result.Issues.Any( i => i.Feature == "ResamplingLoss" ), "Off-grid warning missing." );
			Near( result.Clip!.SampleLocal( skeleton, 7.5f )[0].Origin, Vec3.Zero );
		} );
		Check( "save reopen refuses overwrite and does not alter input", () =>
		{
			var data = Reference();
			var original = data.Takes[0].Nodes[0].Keyframes[1];
			var result = FbxClipConverter.Convert( data, skeleton );
			var directory = Path.Combine( settings.WorkDirectory, "i4-" + Guid.NewGuid().ToString( "N" ) );
			Directory.CreateDirectory( directory );
			var path = Path.Combine( directory, "converted.riganim" );
			FbxClipConverter.WriteNew( result, path );
			var bytes = File.ReadAllBytes( path );
			Near( RigAnimFile.ModelSpace( skeleton, RigAnimFile.ReadFile( path ).SampleLocal( skeleton, 15 ) )[1].Origin,
				FbxAnimationReferenceFixture.GoldenChildWorldPos15 );
			try { FbxClipConverter.WriteNew( result, path ); throw new Exception( "Overwrite accepted." ); }
			catch ( IOException ) { }
			Yes( bytes.SequenceEqual( File.ReadAllBytes( path ) ) && original == data.Takes[0].Nodes[0].Keyframes[1], "Output/input changed unexpectedly." );
		} );
		Check( "raw node tree cannot fabricate animation clip", () =>
		{
			var root = FbxAnimationReferenceFixture.CreateReferenceFbxNodeTree();
			var bytes = Binary( root );
			var result = FbxClipConverter.Convert( bytes, skeleton, "ReferenceTake" );
			Yes( !result.Success && result.Issues.Any( i => i.Feature is "UnsupportedAnimationCurve" or "InvalidAnimationData" ), "Curve-free FBX falsely advertised as converted." );
		} );
		Check( "owned binary FBX curves play through headless desktop and retain source", () =>
		{
			var bytes = AnimatedBinary();
			var original = (byte[])bytes.Clone();
			var directory = Path.Combine( settings.WorkDirectory, "i4-raw-" + Guid.NewGuid().ToString( "N" ) );
			Directory.CreateDirectory( directory );
			var sourcePath = Path.Combine( directory, "owned-animation.fbx" );
			File.WriteAllBytes( sourcePath, bytes );
			var result = FbxClipConverter.Convert( File.ReadAllBytes( sourcePath ), skeleton );
			Yes( result.Success, string.Join( "; ", result.Issues.Select( i => i.Message ) ) );
			var clipPath = Path.Combine( directory, "take.riganim" );
			FbxClipConverter.WriteNew( result, clipPath );
			var player = new MarionettePlayer( skeleton );
			player.LoadClip( clipPath ); player.Playing = false;
			foreach ( var (seconds, expected) in new[] { (0f, FbxAnimationReferenceFixture.GoldenChildWorldPos0),
				(.5f, FbxAnimationReferenceFixture.GoldenChildWorldPos15), (1f, FbxAnimationReferenceFixture.GoldenChildWorldPos30) } )
			{
				player.Time = seconds; player.Step( 0 );
				Near( player.WorldPose[1].Origin, expected );
			}
			player.Time = .25f; player.Step( 0 );
			Near( player.WorldPose[1].Origin, new Vec3( 2.5f - 7f * 1.5f / MathF.Sqrt( 2 ),
				5f + 7f * 1.5f / MathF.Sqrt( 2 ), 0 ), .02f );
			Yes( original.SequenceEqual( File.ReadAllBytes( sourcePath ) ), "Input FBX file changed." );
		} );
		Check( "raw binary explicit takes missing bone and centimetre axes", () =>
		{
			var root = AnimatedTree();
			root.Child( "Objects" )!.Children.Add( new FbxNode { Name = "AnimationStack", Props = new List<object> { 9000L, "Other\0\x01AnimStack", "AnimStack" } } );
			Yes( FbxClipConverter.Convert( Binary( root ), skeleton ).Issues.Any( i => i.Feature == FbxAnimationContract.FeatureAmbiguousTake ), "Raw multi-take ambiguity accepted." );
			Yes( FbxClipConverter.Convert( Binary( root ), skeleton, "ReferenceTake" ).Success, "Explicit raw take selection failed." );
			var partial = new Skeleton(); partial.AddBone( "root", -1, Xform.Identity, 1 );
			Yes( FbxClipConverter.Convert( Binary( root ), partial, "ReferenceTake" ).Issues.Any( i => i.Feature == FbxAnimationContract.FeatureMissingTargetBone ), "Raw missing target ignored." );
			root = AnimatedTree();
			var settings = root.Child( "GlobalSettings" )!.Child( "Properties70" )!;
			settings.Children.First( n => n.Props[0] as string == "UnitScaleFactor" ).Props[4] = 1d;
			settings.Children.First( n => n.Props[0] as string == "UpAxis" ).Props[4] = 1;
			settings.Children.First( n => n.Props[0] as string == "FrontAxis" ).Props[4] = 2;
			settings.Children.First( n => n.Props[0] as string == "FrontAxisSign" ).Props[4] = 1;
			var converted = FbxClipConverter.Convert( Binary( root ), skeleton, frameRate: 60 );
			Yes( converted.Success, string.Join( "; ", converted.Issues.Select( i => i.Message ) ) );
			Yes( converted.Clip!.FrameCount == 61, "Raw take must resample to selected output rate." );
			Near( converted.Clip.SampleLocal( skeleton, 30 )[0].Origin, new Vec3( 5 / 2.54f, 0, 10 / 2.54f ) );
		} );
		Check( "binary FBX rejects cubic curves and unsupported offsets", () =>
		{
			var root = AnimatedTree();
			var curve = root.Child( "Objects" )!.Children.First( n => n.Name == "AnimationCurve" );
			curve.Child( "KeyAttrFlags" )!.Props[0] = new[] { 8, 8, 8 };
			var rejected = FbxClipConverter.Convert( Binary( root ), skeleton );
			Yes( rejected.Issues.Any( i => i.Feature == "UnsupportedAnimationCurve" ), "Cubic curve accepted without tangents." );
			root = AnimatedTree();
			var model = root.Child( "Objects" )!.Children.First( n => n.Name == "Model" );
			model.Child( "Properties70" )!.Children.Add( Prop( "RotationOffset", 1d, 0d, 0d ) );
			Yes( FbxClipConverter.Convert( Binary( root ), skeleton ).Issues.Any( i => i.Feature == FbxAnimationContract.FeatureUnbakedPivot ), "Offset ignored." );
			root = AnimatedTree();
			root.Child( "Objects" )!.Children.Add( new FbxNode { Name = "AnimationLayer", Props = new List<object> { 9001L, "Extra\0\x01AnimLayer", "AnimLayer" } } );
			root.Child( "Connections" )!.Children.Add( new FbxNode { Name = "C", Props = new List<object> { "OO", 9001L, 1000L } } );
			Yes( FbxClipConverter.Convert( Binary( root ), skeleton ).Issues.Any( i => i.Feature == FbxAnimationContract.FeatureLayeredAnimation ), "Second layer accepted." );
		} );
	}

	static FbxNode Prop( string name, params object[] values ) => new() { Name = "P", Props = new List<object>( new object[] { name, "Vector3D", "Vector", "" }.Concat( values ) ) };

	static FbxNode AnimatedTree()
	{
		var root = FbxAnimationReferenceFixture.CreateReferenceFbxNodeTree();
		var objects = root.Child( "Objects" )!;
		var connections = root.Child( "Connections" )!;
		var stack = objects.Children.First( n => n.Name == "AnimationStack" );
		stack.Children.Add( new FbxNode { Name = "Properties70", Children = new List<FbxNode>
		{
			new() { Name = "P", Props = new List<object> { "LocalStart", "KTime", "Time", "", 0L } },
			new() { Name = "P", Props = new List<object> { "LocalStop", "KTime", "Time", "", FbxAnimationTime.FbxTicksPerSecond } }
		} } );
		var rootModel = objects.Children.First( n => n.Name == "Model" && n.ObjectName == "root" );
		var childModel = objects.Children.First( n => n.Name == "Model" && n.ObjectName == "child" );
		childModel.Child( "Properties70" )!.Children.Add( Prop( "Lcl Translation", 0d, 10d, 0d ) );
		long[] times = { 0, FbxAnimationTime.FbxTicksPerSecond / 2, FbxAnimationTime.FbxTicksPerSecond };
		long next = 4000;
		void Channel( FbxNode model, string property, float[] x, float[] y, float[] z )
		{
			long nodeId = next++;
			objects.Children.Add( new FbxNode { Name = "AnimationCurveNode", Props = new List<object> { nodeId, $"CurveNode{nodeId}\0\x01AnimCurveNode", "AnimCurveNode" } } );
			connections.Children.Add( new FbxNode { Name = "C", Props = new List<object> { "OO", nodeId, 2000L } } );
			connections.Children.Add( new FbxNode { Name = "C", Props = new List<object> { "OP", nodeId, (long)model.Props[0], property } } );
			foreach ( var (axis, values) in new[] { ('X', x), ('Y', y), ('Z', z) } )
			{
				long curveId = next++;
				objects.Children.Add( new FbxNode { Name = "AnimationCurve", Props = new List<object> { curveId, $"Curve{curveId}\0\x01AnimCurve", "AnimCurve" }, Children = new List<FbxNode>
				{
					new() { Name = "KeyTime", Props = new List<object> { times } },
					new() { Name = "KeyValueFloat", Props = new List<object> { values } },
					new() { Name = "KeyAttrFlags", Props = new List<object> { new[] { 4, 4, 4 } } }
				} } );
				connections.Children.Add( new FbxNode { Name = "C", Props = new List<object> { "OP", curveId, nodeId, $"d|{axis}" } } );
			}
		}
		Channel( rootModel, "Lcl Translation", new float[] { 0, 5, 10 }, new float[] { 0, 10, 20 }, new float[] { 0, 0, 0 } );
		Channel( rootModel, "Lcl Rotation", new float[] { 0, 0, 0 }, new float[] { 0, 0, 0 }, new float[] { 0, 90, 180 } );
		Channel( rootModel, "Lcl Scaling", new float[] { 1, 2, 1 }, new float[] { 1, 2, 1 }, new float[] { 1, 2, 1 } );
		Channel( childModel, "Lcl Translation", new float[] { 0, 0, 0 }, new float[] { 10, 4, 5 }, new float[] { 0, 0, 0 } );
		Channel( childModel, "Lcl Rotation", new float[] { 0, 90, 0 }, new float[] { 0, 0, 0 }, new float[] { 0, 0, 0 } );
		Channel( childModel, "Lcl Scaling", new float[] { 1, 1, 1 }, new float[] { 1, 1, 1 }, new float[] { 1, 1, 1 } );
		// Golden fixture's source coordinates are already Z-up inches.
		var settings = root.Child( "GlobalSettings" )!.Child( "Properties70" )!;
		settings.Children.First( n => n.Props[0] as string == "UpAxis" ).Props[4] = 2;
		settings.Children.First( n => n.Props[0] as string == "FrontAxis" ).Props[4] = 1;
		settings.Children.First( n => n.Props[0] as string == "FrontAxisSign" ).Props[4] = -1;
		return root;
	}

	static byte[] AnimatedBinary() => Binary( AnimatedTree() );

	// Owned FBX 7400 test encoder. It only serializes our typed nodes; production decoding uses
	// FbxNode.Parse, not this writer. Golden poses above are analytical, not produced by this encoder.
	static byte[] Binary( FbxNode root )
	{
		using var memory = new MemoryStream();
		using var writer = new BinaryWriter( memory, Encoding.UTF8, leaveOpen: true );
		writer.Write( Encoding.ASCII.GetBytes( "Kaydara FBX Binary  \0\x1a\0" ) );
		writer.Write( 7400 );
		foreach ( var node in root.Children ) WriteNode( writer, node );
		writer.Write( new byte[13] );
		return memory.ToArray();
	}
	static void WriteNode( BinaryWriter w, FbxNode n )
	{
		long start = w.BaseStream.Position;
		w.Write( 0 ); w.Write( n.Props.Count ); w.Write( 0 );
		var name = Encoding.ASCII.GetBytes( n.Name );
		w.Write( (byte)name.Length ); w.Write( name );
		long propsStart = w.BaseStream.Position;
		foreach ( object p in n.Props )
		{
			switch ( p )
			{
				case string s: var utf = Encoding.UTF8.GetBytes( s ); w.Write( (byte)'S' ); w.Write( utf.Length ); w.Write( utf ); break;
				case long l: w.Write( (byte)'L' ); w.Write( l ); break;
				case int i: w.Write( (byte)'I' ); w.Write( i ); break;
				case double d: w.Write( (byte)'D' ); w.Write( d ); break;
				case long[] a: Array( 'l', a.Length, () => { foreach ( long v in a ) w.Write( v ); } ); break;
				case float[] a: Array( 'f', a.Length, () => { foreach ( float v in a ) w.Write( v ); } ); break;
				case int[] a: Array( 'i', a.Length, () => { foreach ( int v in a ) w.Write( v ); } ); break;
				default: throw new NotSupportedException( $"Fixture encoder cannot write {p.GetType()}" );
			}
		}
		void Array( char type, int count, Action content )
		{
			w.Write( (byte)type ); w.Write( count ); w.Write( 0 );
			w.Write( count * (type == 'l' ? 8 : 4) ); content();
		}
		long propsEnd = w.BaseStream.Position;
		foreach ( var child in n.Children ) WriteNode( w, child );
		if ( n.Children.Count > 0 ) w.Write( new byte[13] );
		long end = w.BaseStream.Position;
		w.BaseStream.Position = start;
		w.Write( checked((int)end) ); w.Write( n.Props.Count ); w.Write( checked((int)(propsEnd - propsStart)) );
		w.BaseStream.Position = end;
	}
}
