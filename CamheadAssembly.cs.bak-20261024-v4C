using System;
using System.Collections.Generic;
using System.IO;
using Effigy;

namespace Marionette.Desktop;

/// <summary>
/// Camhead's .effigy is only the original mechanical core. The actual character's
/// ModelDoc adds six separately authored FBX meshes. Load those source meshes in
/// their bind pose, without changing the editable .effigy or duplicating its head.
/// </summary>
public static class CamheadAssembly
{
	static readonly string[] Additions =
	{
		"camhead_hands.fbx", "camhead_pelvis.fbx", "camhead_flanks.fbx",
		"camhead_legs.fbx", "camhead_chest.fbx", "camhead_neck.fbx",
	};

	static readonly Dictionary<string, string> Materials = new( StringComparer.OrdinalIgnoreCase )
	{
		["M_CreamArmor"] = "panel",
		["M_DarkChassis"] = "frame",
		["M_PalmPanel"] = "frame",
		["M_SocketDark"] = "frame",
		["M_Joint"] = "joint",
		["M_MilledChamfer"] = "joint",
		["M_ChromeAxle"] = "chrome",
		["M_RubberGrip"] = "rubber",
		["M_OrangeCable"] = "cable",
	};

	public static PolyMesh Load( PartStudio studio, string documentPath )
	{
		var (assembled, ranges) = studio.ToMeshWithBodies( b => b.Visible );
		if ( studio.Rig.Count > 0 )
			assembled.Skin = SkinBinder.BindBodies( assembled, ranges, studio.BodyBoneMap, studio.Rig );

		var dir = Path.GetDirectoryName( documentPath )!;
		var slots = new Dictionary<string, int>( StringComparer.OrdinalIgnoreCase );
		foreach ( var (slot, name) in studio.MaterialNames )
			slots[name] = slot;

		foreach ( var filename in Additions )
		{
			var path = Path.Combine( dir, filename );
			var imported = FbxImport.Read( File.ReadAllBytes( path ) );
			var remap = new Dictionary<int, int>();
			foreach ( var (sourceSlot, sourceName) in imported.MaterialSlots )
			{
				if ( !Materials.TryGetValue( sourceName, out var material ) ||
					!slots.TryGetValue( $"materials/camhead/{material}.vmat", out var destSlot ) )
					throw new InvalidDataException( $"Unmapped Camhead material {sourceName} in {filename}" );
				remap[sourceSlot] = destSlot;
			}

			for ( int pi = 0; pi < imported.Pieces.Count; pi++ )
			{
				var piece = imported.Pieces[pi];
				// These six meshes were authored in the citizen Blender frame:
				// P(model) = (model.y, -model.x, model.z) * 2.54. FBX supplies
				// the centimetre-to-inch scale, but its axis metadata does not
				// encode that authoring-frame rotation. Invert it here.
				for ( int i = 0; i < piece.Mesh.Positions.Count; i++ )
				{
					var p = piece.Mesh.Positions[i];
					piece.Mesh.Positions[i] = new Vec3( -p.y, p.x, p.z );
				}
				foreach ( var face in piece.Mesh.Faces )
					face.Material = remap[face.Material];

				if ( studio.Rig.Count > 0 )
				{
					var named = pi < imported.Weights.Count ? imported.Weights[pi] : null;
					piece.Mesh.Skin = FbxImport.Resolve( named, studio.Rig, null, out _ )
						?? SkinBinder.BindRigid( piece.Mesh, studio.Rig );
				}

				MeshTransform.Append( assembled, piece.Mesh );
			}
			Console.WriteLine( $"  added   : {filename} ({imported.Pieces.Count} pieces)" );
		}
		return assembled;
	}
}
