using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Effigy;

/// <summary>
/// A garment as live cloth: the file Marionette's GarmentCloth component simulates in the game.
///
/// WHY A SECOND EXPORT. A .clothing item is a skinned model - the shirt is baked into the pose
/// the drape left it in and then rides the bones rigidly. That is how every s&amp;box garment
/// works and it is fine for a fitted top; it is not a shirt hanging loose off a robot's
/// shoulders, which has to swing when he turns. That takes a simulation at runtime, and the
/// simulation needs things a model cannot carry: which bone each collider rides on and how thick
/// it is, and which bone each bit of cloth is leashed to.
///
/// EVERYTHING IN THE WEARER'S BIND POSE, in model space - the same space the garment was fitted
/// in. The component reads the model's bind transforms at start and turns each of these into an
/// offset from its bone, so the file does not care what pose the character is in when it loads.
///
/// JSON, WRITTEN BY HAND, for the reason ClothingDefinition gives: the kernel stays engine-free
/// and testable, and the game reads it with the engine's own deserialiser.
/// </summary>
public static class ClothExport
{
	/// <summary>
	/// How the cloth behaves at runtime, in the component's own terms (0..1 sliders, not
	/// compliances), so the file is the fabric the garment was given and the component has
	/// nothing to be set up on. See GarmentCloth.
	/// </summary>
	public sealed class Settings
	{
		public float Stiffness = 0.9f;
		public float Bend = 0.15f;
		public float Damping = 0.03f;
		public float Friction = 0.6f;
	}

	/// <summary>The Garment's Fabric and Stiffness as runtime settings. The numbers match the
	/// drape presets in ClothSim.WithFabric in spirit - denim holds its panels, stretch gives,
	/// leather is heavy - and are what looked right on a T-shirt, not measured anything.</summary>
	public static Settings FabricSettings( Fabric fabric, float stiffness )
	{
		stiffness = Math.Clamp( stiffness, 0f, 1f );

		return fabric switch
		{
			Fabric.Denim => new Settings { Stiffness = 0.6f + 0.4f * stiffness, Bend = 0.4f, Damping = 0.06f, Friction = 0.5f },
			Fabric.Leather => new Settings { Stiffness = 0.7f + 0.3f * stiffness, Bend = 0.6f, Damping = 0.1f, Friction = 0.4f },
			Fabric.Stretch => new Settings { Stiffness = 0.2f + 0.5f * stiffness, Bend = 0.05f, Damping = 0.02f, Friction = 0.75f },
			_ => new Settings { Stiffness = 0.4f + 0.6f * stiffness, Bend = 0.15f, Damping = 0.03f, Friction = 0.6f },
		};
	}

	/// <summary>
	/// Pin weights for a garment: 1 rides its bone exactly, 0 is free cloth. The top band of the
	/// garment - the collar and the shoulder yoke of a shirt, the waistband of trousers - is what
	/// holds a garment on a body, and free cloth there slides down the chest or bunches at the
	/// neck the moment the wearer runs. The band fades from pinned at the top to free at
	/// <paramref name="fraction"/> of the garment's height, so there is no crease where it ends.
	/// </summary>
	public static float[] PinTop( PolyMesh garment, float fraction )
	{
		var pins = new float[garment.VertexCount];

		if ( garment.VertexCount == 0 || fraction <= 0f )
			return pins;

		var top = garment.Positions.Max( p => p.z );
		var bottom = garment.Positions.Min( p => p.z );
		var band = MathF.Max( (top - bottom) * fraction, 1e-3f );

		for ( var i = 0; i < pins.Length; i++ )
		{
			var down = top - garment.Positions[i].z;
			var t = Math.Clamp( 1f - down / band, 0f, 1f );
			pins[i] = t * t * (3f - 2f * t);
		}

		return pins;
	}

	/// <summary>
	/// What each particle is wearing over and under it: which layer it is in, how much room its
	/// own garment takes, and the padding sewn into it there. Make live cloth adds every garment's
	/// sheet in the order it adds the sheet itself, so the lists line up with the cloth's vertices.
	///
	/// THE LAYERS, AS THE CLOTH RUNS THEM. A jacket over a shirt is fitted over the shirt, and as
	/// live cloth it has to stay there: its body capsules are inflated by the room the shirt takes
	/// (Under), each jacket particle is held off the shirt particle it was fitted over (Over), and
	/// the capsules get the last word. Without it the jacket swings straight through the shirt.
	///
	/// PADDING IS PER PARTICLE. A quilted yoke is thicker (a bigger gap off the shirt), stiffer to
	/// fold (BendScale) and heavier (Mass), and the solver splits every correction by mass, so the
	/// heavy yoke drags the light shirt cloth beside it rather than the other way round.
	/// </summary>
	public sealed class Layering
	{
		public readonly List<int> Layer = new();
		public readonly List<float> Room = new();
		public readonly List<float> Pad = new();
		public readonly List<float> Mass = new();
		public readonly List<float> BendScale = new();
		public readonly List<Vec3> Normal = new();

		public int Count => Layer.Count;

		/// <param name="sheet">The garment's cloth as it goes into the file - its outer wall, fur left out.</param>
		/// <param name="body">The garment the sheet came from, for its layer and its room.</param>
		/// <param name="pads">Every Padding in the document. The ones that padded this garment are used.</param>
		public void Add( PolyMesh sheet, Body body, IEnumerable<PaddingFeature> pads, BodyRegions.Map map )
		{
			var n = sheet.VertexCount;
			var pad = new float[n];
			var mass = Enumerable.Repeat( 1f, n ).ToArray();
			var bend = Enumerable.Repeat( 1f, n ).ToArray();

			foreach ( var p in pads ?? Enumerable.Empty<PaddingFeature>() )
			{
				if ( p.Suppressed || p.Error is not null || !p.PaddedBodies.Contains( body.Id ) )
					continue;

				var field = p.Field( sheet, map, out var weight );

				for ( var i = 0; i < n; i++ )
				{
					pad[i] += field[i];
					mass[i] *= 1f + (p.Heavier.Clamped - 1f) * weight[i];
					bend[i] *= 1f + (p.Stiffer.Clamped - 1f) * weight[i];
				}
			}

			var normals = sheet.ComputeVertexNormals();

			for ( var i = 0; i < n; i++ )
			{
				Layer.Add( body.Layer );
				Room.Add( MathF.Max( 0f, body.LayerAllowance ) );
				Pad.Add( pad[i] );
				Mass.Add( MathF.Min( mass[i], 4f ) );
				BendScale.Add( MathF.Min( bend[i], 10f ) );
				Normal.Add( normals[i] );
			}
		}

		/// <summary>
		/// Per particle, how far further off the capsules it has to sit for the garments under it:
		/// the room each lower layer takes where it is nearest, padding included. 0 on the base layer.
		/// And the pairs: each particle of an outer layer with the nearest particle of the layers
		/// under it, and how far off that one's surface it is held - its own room and padding, or
		/// where it was fitted if that was closer, so a fit that left a garment snug is not blown apart.
		/// </summary>
		public (float[] Under, List<(int Outer, int Inner, float Gap)> Over) Pairs( PolyMesh cloth, float reach = 2f )
		{
			var n = Count;
			var under = new float[n];
			var over = new List<(int, int, float)>();
			var layers = Layer.Where( l => l >= 0 ).Distinct().OrderBy( l => l ).ToArray();

			if ( layers.Length < 2 || n != cloth.VertexCount )
				return (under, over);

			var byLayer = layers.ToDictionary( l => l, l => Enumerable.Range( 0, n ).Where( i => Layer[i] == l ).ToArray() );

			for ( var i = 0; i < n; i++ )
			{
				if ( Layer[i] <= layers[0] )
					continue;

				var p = cloth.Positions[i];
				var own = Room[i] + Pad[i];
				var best = -1;
				var bestD = float.MaxValue;

				foreach ( var l in layers )
				{
					if ( l >= Layer[i] )
						break;

					var nearest = -1;
					var nearestD = float.MaxValue;

					foreach ( var j in byLayer[l] )
					{
						var d = (cloth.Positions[j] - p).LengthSquared;

						if ( d < nearestD )
						{
							nearestD = d;
							nearest = j;
						}
					}

					if ( nearest < 0 )
						continue;

					// Inflated only for the layers it is actually over; held off the nearest one
					// wherever it is, since a shirt a few inches off at rest is right under it with
					// the arm raised.
					if ( MathF.Sqrt( nearestD ) <= reach + own )
						under[i] += Room[nearest] + Pad[nearest];

					if ( nearestD < bestD )
					{
						bestD = nearestD;
						best = nearest;
					}
				}

				if ( best < 0 )
					continue;

				var along = Vec3.Dot( p - cloth.Positions[best], Normal[best] );
				over.Add( (i, best, Math.Clamp( along, 0f, own )) );
			}

			return (under, over);
		}
	}

	/// <param name="garment">The finished garment, in the wearer's model space.</param>
	/// <param name="capsules">What it hangs on - see BoneCapsules. Their Bone indexes <paramref name="skeleton"/>.</param>
	/// <param name="settings">The fabric. Null writes none, and the component uses its own.</param>
	/// <param name="pins">Per-vertex pin weight, see <see cref="PinTop"/>. Null pins nothing.</param>
	/// <param name="layering">Layers and padding per vertex, see <see cref="Layering"/>. Null writes none.</param>
	public static string Write( PolyMesh garment, IReadOnlyList<Capsule> capsules, Skeleton skeleton, string material,
		Settings settings = null, float[] pins = null, Layering layering = null )
	{
		string Name( int bone ) => bone >= 0 && bone < skeleton.Count ? skeleton.Bones[bone].Name : "";

		var usable = capsules.Where( c => c.Bone >= 0 && c.Bone < skeleton.Count ).ToList();
		var sb = new StringBuilder();
		var inv = CultureInfo.InvariantCulture;

		string F( float v ) => float.IsFinite( v ) ? v.ToString( "0.####", inv ) : "0";

		sb.Append( "{\n" );
		sb.Append( $"  \"Material\": \"{Escape( material ?? "" )}\",\n" );

		if ( settings is not null )
			sb.Append( $"  \"Fabric\": {{\"Stiffness\":{F( settings.Stiffness )},\"Bend\":{F( settings.Bend )},"
				+ $"\"Damping\":{F( settings.Damping )},\"Friction\":{F( settings.Friction )}}},\n" );

		if ( pins is not null && pins.Length == garment.VertexCount && pins.Any( w => w > 0f ) )
		{
			sb.Append( "  \"Pins\": [" );
			sb.Append( string.Join( ",", pins.Select( F ) ) );
			sb.Append( "],\n" );
		}

		if ( layering is not null && layering.Count == garment.VertexCount )
		{
			// Only what differs from a plain single garment, so old files and plain shirts stay as they were.
			if ( layering.Mass.Any( m => MathF.Abs( m - 1f ) > 1e-4f ) )
				sb.Append( $"  \"Mass\": [{string.Join( ",", layering.Mass.Select( F ) )}],\n" );

			if ( layering.BendScale.Any( m => MathF.Abs( m - 1f ) > 1e-4f ) )
				sb.Append( $"  \"BendScale\": [{string.Join( ",", layering.BendScale.Select( F ) )}],\n" );

			var (under, over) = layering.Pairs( garment );

			if ( layering.Layer.Distinct().Count() > 1 )
				sb.Append( $"  \"Layers\": [{string.Join( ",", layering.Layer )}],\n" );

			if ( under.Any( u => u > 0f ) )
				sb.Append( $"  \"Under\": [{string.Join( ",", under.Select( F ) )}],\n" );

			if ( over.Count > 0 )
				sb.Append( $"  \"Over\": [{string.Join( ",", over.Select( o => $"{o.Outer},{o.Inner},{F( o.Gap )}" ) )}],\n" );
		}

		// Per vertex: its bind position, a UV (the first corner that names one - a seam's two
		// sides differ, and cloth is simulated per vertex, not per corner) and the bone it is
		// leashed to, which is the bone of the capsule it lies nearest.
		var uvs = new Vec2[garment.VertexCount];

		foreach ( var face in garment.Faces )
		{
			if ( face.UVs is null )
				continue;

			for ( var c = 0; c < face.Count && c < face.UVs.Length; c++ )
				if ( uvs[face.Indices[c]].Equals( default( Vec2 ) ) )
					uvs[face.Indices[c]] = face.UVs[c];
		}

		sb.Append( "  \"Positions\": [" );
		sb.Append( string.Join( ",", garment.Positions.Select( p => $"{F( p.x )},{F( p.y )},{F( p.z )}" ) ) );
		sb.Append( "],\n" );

		sb.Append( "  \"UVs\": [" );
		sb.Append( string.Join( ",", uvs.Select( u => $"{F( u.x )},{F( u.y )}" ) ) );
		sb.Append( "],\n" );

		sb.Append( "  \"Triangles\": [" );
		var tris = new List<int>();

		foreach ( var face in garment.Faces )
			for ( var c = 1; c + 1 < face.Count; c++ )
			{
				tris.Add( face.Indices[0] );
				tris.Add( face.Indices[c] );
				tris.Add( face.Indices[c + 1] );
			}

		sb.Append( string.Join( ",", tris ) );
		sb.Append( "],\n" );

		sb.Append( "  \"VertexBones\": [" );
		sb.Append( string.Join( ",", garment.Positions.Select( p =>
		{
			var best = -1;
			var bestD = float.MaxValue;

			foreach ( var c in usable )
			{
				var d = (c.Closest( p ) - p).Length - c.Radius;

				if ( d < bestD )
				{
					bestD = d;
					best = c.Bone;
				}
			}

			return $"\"{Escape( Name( best ) )}\"";
		} ) ) );
		sb.Append( "],\n" );

		// WHERE THE CLOTH SAT, for each capsule it sat close to: its distance from the capsule's
		// core when it was fitted. A capsule is a rough stand-in for an arm or a chest - fatter
		// than the body in places, thinner in others - and a close-fitting top lies inside the fat
		// parts. Pushed out to the capsule's surface on the first frame, it blew apart. With this,
		// the component holds the cloth no closer than it was fitted and no further in, and the
		// capsule still catches it when the body moves into it. Only the close pairs are written:
		// a particle and the capsule core, nearer than the radius plus an inch (the component's
		// Skin is well under that).
		var inside = new List<string>();

		for ( var c = 0; c < usable.Count; c++ )
			for ( var v = 0; v < garment.VertexCount; v++ )
			{
				var p = garment.Positions[v];
				var gap = (usable[c].Closest( p ) - p).Length;

				if ( gap < usable[c].Radius + 1f )
					inside.Add( $"{v},{c},{F( gap )}" );
			}

		if ( inside.Count > 0 )
		{
			sb.Append( "  \"Fitted\": [" );
			sb.Append( string.Join( ",", inside ) );
			sb.Append( "],\n" );
		}

		sb.Append( "  \"Capsules\": [" );
		sb.Append( string.Join( ",", usable.Select( c =>
			$"{{\"Bone\":\"{Escape( Name( c.Bone ) )}\",\"A\":[{F( c.A.x )},{F( c.A.y )},{F( c.A.z )}],"
			+ $"\"B\":[{F( c.B.x )},{F( c.B.y )},{F( c.B.z )}],\"Radius\":{F( c.Radius )}}}" ) ) );
		sb.Append( "]\n" );

		sb.Append( "}\n" );
		return sb.ToString();
	}


	/// <summary>
	/// HEADLESS LIVE-CLOTH EXPORT — the one call a script verb (`export_cloth PATH.cloth.json`,
	/// wired by the API lane) makes, so a garment built in Effigy works in GarmentCloth without
	/// going through the editor's Publish panel.
	///
	/// This is the assembly the editor already does (EffigyWindow.PublishLiveCloth) and the
	/// clothing test mirrors, promoted into the kernel so it cannot drift from either: every
	/// garment body in the studio becomes one merged sheet — outer wall only (MeshSolidify),
	/// fur shells stripped, skin weights carried — pinned at the top band, layered with its
	/// padding, and hung on capsules built from the non-garment bodies. The fabric comes from
	/// the first garment feature found; a studio with no garment feature still exports, with
	/// no Fabric block, and the component falls back to its own sliders.
	///
	/// Returns null, and writes nothing, when there is no garment cloth to export or no
	/// capsule on the wearer — a file with no capsules is one GarmentCloth refuses to load,
	/// so emitting it would only look like a success.
	/// </summary>
	public static string ExportLiveCloth( PartStudio studio, string material )
	{
		if ( studio is null )
			return null;

		var mesh = new PolyMesh();
		var pins = new List<float>();
		Settings fabric = null;
		var layering = new Layering();
		var pads = studio.Features.OfType<PaddingFeature>().ToList();
		var regions = new BodyRegions.Map( studio.Rig );
		var furSlots = studio.Features.OfType<FurFeature>()
			.Where( f => !f.Suppressed && f.ResolvedSlot >= 0 )
			.Select( f => f.ResolvedSlot ).ToHashSet();

		foreach ( var body in studio.Bodies.Where( b => b.IsGarment ) )
		{
			var cloth = WithoutFur( MeshSolidify.OuterSheet( body.Mesh, out _ ) ?? body.Mesh, furSlots );

			if ( cloth is null || cloth.VertexCount == 0 )
				continue;

			var offset = mesh.VertexCount;

			foreach ( var pos in cloth.Positions )
				mesh.AddVertex( pos );

			foreach ( var face in cloth.Faces )
				mesh.AddFace( face.Indices.Select( i => i + offset ).ToArray(), face.UVs, face.Material );

			var feature = studio.Features.OfType<GarmentFeature>().FirstOrDefault( g => g.Id == body.FeatureId );
			fabric ??= feature is null ? null : FabricSettings( feature.FabricValue, feature.Stiffness.Clamped );
			pins.AddRange( PinTop( cloth, feature?.LivePinFraction ?? 0.12f ) );
			layering.Add( cloth, body, pads, regions );
		}

		if ( mesh.VertexCount == 0 )
			return null;

		var worn = studio.Bodies.Where( b => !b.IsGarment ).Select( b => b.Mesh ).ToList();
		var capsules = BoneCapsules.Build( new BodyRegions.Map( studio.Rig ), worn );

		if ( capsules.Count == 0 )
			return null;

		return Write( mesh, capsules, studio.Rig, material, fabric, pins.ToArray(), layering );
	}

	/// <summary>ExportLiveCloth, written to <paramref name="path"/>. Returns the path, or null
	/// when there was nothing to export (see ExportLiveCloth).</summary>
	public static string ExportLiveClothToFile( PartStudio studio, string material, string path )
	{
		var json = ExportLiveCloth( studio, material );

		if ( json is null )
			return null;

		File.WriteAllText( path, json );
		return path;
	}

	/// <summary>Drop every face on a fur slot, keeping surviving vertices in place. Fur is a
	/// shell over the cloth, not cloth itself, and the runtime simulates per vertex — a fur
	/// shell in the file would hang in the air as a second garment. Matches the editor's
	/// WithoutFur, so the headless file matches the panel's file vertex for vertex.</summary>
	static PolyMesh WithoutFur( PolyMesh source, HashSet<int> furSlots )
	{
		if ( source is null || furSlots.Count == 0 || !source.Faces.Any( f => furSlots.Contains( f.Material ) ) )
			return source;

		var map = new int[source.VertexCount];
		Array.Fill( map, -1 );

		foreach ( var face in source.Faces )
			if ( !furSlots.Contains( face.Material ) )
				foreach ( var vi in face.Indices )
					if ( (uint)vi < (uint)map.Length )
						map[vi] = 0;

		var cloth = new PolyMesh();
		var skin = source.IsRigged ? new SkinWeights() : null;

		for ( var vi = 0; vi < map.Length; vi++ )
		{
			if ( map[vi] < 0 )
				continue;

			map[vi] = cloth.VertexCount;
			cloth.AddVertex( source.Positions[vi] );
			skin?.Vertices.Add( source.Skin[vi] );
		}

		foreach ( var face in source.Faces )
		{
			if ( furSlots.Contains( face.Material ) || face.Indices.Any( vi => (uint)vi >= (uint)map.Length ) )
				continue;

			cloth.AddFace( face.Indices.Select( vi => map[vi] ).ToArray(), face.UVs, face.Material );
		}

		cloth.Skin = skin;
		return cloth;
	}

	static string Escape( string s ) => s.Replace( "\\", "\\\\" ).Replace( "\"", "\\\"" );
}
