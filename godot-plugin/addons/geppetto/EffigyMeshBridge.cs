using System;
using System.Collections.Generic;
using Godot;
using Marionette.Desktop;

namespace Geppetto;

/// <summary>
/// Converts Geppetto <see cref="CpuMesh"/> into Godot <see cref="ArrayMesh"/>.
/// Applies verified coordinate transform: (X,Y,Z)_Godot = 0.0254 * M * (x,y,z)_Effigy,
/// triangle winding reversal (A, C, B) for Godot CW front-faces, normal transform,
/// and inverted tangent chirality sign.
/// </summary>
public static class EffigyMeshBridge
{
	public const float ScaleToMetres = 0.0254f;

	/// <summary>
	/// Transforms position from Effigy inches to Godot metres:
	/// X_G = 0.0254 * y_E
	/// Y_G = 0.0254 * z_E
	/// Z_G = 0.0254 * x_E
	/// </summary>
	public static Vector3 TransformPosition( float x, float y, float z ) =>
		new( ScaleToMetres * y, ScaleToMetres * z, ScaleToMetres * x );

	public static Vector3 TransformPosition( in System.Numerics.Vector3 p ) =>
		TransformPosition( p.X, p.Y, p.Z );

	/// <summary>
	/// Transforms normal vector by matrix M:
	/// N_GX = n_Ey
	/// N_GY = n_Ez
	/// N_GZ = n_Ex
	/// </summary>
	public static Vector3 TransformNormal( float x, float y, float z ) =>
		new( y, z, x );

	public static Vector3 TransformNormal( in System.Numerics.Vector3 n ) =>
		TransformNormal( n.X, n.Y, n.Z );

	/// <summary>
	/// Converts a <see cref="CpuMesh"/> to a Godot <see cref="ArrayMesh"/>.
	/// Each material batch creates one surface on the ArrayMesh.
	/// </summary>
	public static ArrayMesh ToGodotArrayMesh( CpuMesh mesh, MaterialResolver resolver = null )
	{
		ArgumentNullException.ThrowIfNull( mesh );
		var arrayMesh = new ArrayMesh();

		if ( mesh.Batches == null || mesh.Batches.Length == 0 || mesh.Vertices == null || mesh.Indices == null )
			return arrayMesh;

		var baseVertices = mesh.Vertices;
		var indices = mesh.Indices;

		// Add blend shapes first if present
		var morphNames = new List<string>();
		if ( mesh.Morphs != null && mesh.MorphCornerMap != null )
		{
			foreach ( var target in mesh.Morphs.Targets )
			{
				if ( !string.IsNullOrWhiteSpace( target.Name ) )
				{
					arrayMesh.AddBlendShape( target.Name );
					morphNames.Add( target.Name );
				}
			}
		}

		// Convert each batch to an ArrayMesh surface
		for ( int batchIndex = 0; batchIndex < mesh.Batches.Length; batchIndex++ )
		{
			var batch = mesh.Batches[batchIndex];
			if ( batch.IndexCount == 0 ) continue;

			int indexOffset = batch.IndexOffset / sizeof( uint );
			int indexCount = batch.IndexCount;

			// Collect unique vertices used by this batch to make compact per-surface arrays
			var remap = new Dictionary<int, int>();
			var surfaceVertIndices = new List<int>();
			var surfaceIndices = new int[indexCount];

			for ( int i = 0; i < indexCount; i += 3 )
			{
				// Reverse winding: (A, B, C) -> (A, C, B) for Godot CW front-face
				int i0 = (int)indices[indexOffset + i + 0];
				int i1 = (int)indices[indexOffset + i + 1];
				int i2 = (int)indices[indexOffset + i + 2];

				int Remap( int v )
				{
					if ( !remap.TryGetValue( v, out int newIdx ) )
					{
						newIdx = surfaceVertIndices.Count;
						remap[v] = newIdx;
						surfaceVertIndices.Add( v );
					}
					return newIdx;
				}

				surfaceIndices[i + 0] = Remap( i0 );
				surfaceIndices[i + 1] = Remap( i2 );
				surfaceIndices[i + 2] = Remap( i1 );
			}

			int surfVertCount = surfaceVertIndices.Count;
			var positions = new Vector3[surfVertCount];
			var normals = new Vector3[surfVertCount];
			var tangents = new float[surfVertCount * 4];
			var uvs = new Vector2[surfVertCount];
			var colors = new Color[surfVertCount];
			int[] bones = mesh.HasSkin ? new int[surfVertCount * 4] : null;
			float[] weights = mesh.HasSkin ? new float[surfVertCount * 4] : null;

			for ( int i = 0; i < surfVertCount; i++ )
			{
				int srcVert = surfaceVertIndices[i];
				int offset = srcVert * CpuMesh.FloatsPerVertex;

				float px = baseVertices[offset + 0];
				float py = baseVertices[offset + 1];
				float pz = baseVertices[offset + 2];
				positions[i] = TransformPosition( px, py, pz );

				float nx = baseVertices[offset + 3];
				float ny = baseVertices[offset + 4];
				float nz = baseVertices[offset + 5];
				normals[i] = TransformNormal( nx, ny, nz );

				if ( mesh.HasSkin )
				{
					bones[i * 4 + 0] = (int)baseVertices[offset + 6];
					bones[i * 4 + 1] = (int)baseVertices[offset + 7];
					bones[i * 4 + 2] = (int)baseVertices[offset + 8];
					bones[i * 4 + 3] = (int)baseVertices[offset + 9];

					weights[i * 4 + 0] = baseVertices[offset + 10];
					weights[i * 4 + 1] = baseVertices[offset + 11];
					weights[i * 4 + 2] = baseVertices[offset + 12];
					weights[i * 4 + 3] = baseVertices[offset + 13];
				}

				uvs[i] = new Vector2( baseVertices[offset + 14], baseVertices[offset + 15] );

				float tx = baseVertices[offset + 16];
				float ty = baseVertices[offset + 17];
				float tz = baseVertices[offset + 18];
				float tw = baseVertices[offset + 19];
				// Tangent direction rotated by M: (ty, tz, tx)
				// Chirality sign inverted: w_Godot = -w_Effigy
				tangents[i * 4 + 0] = ty;
				tangents[i * 4 + 1] = tz;
				tangents[i * 4 + 2] = tx;
				tangents[i * 4 + 3] = -tw;

				colors[i] = new Color(
					baseVertices[offset + 20],
					baseVertices[offset + 21],
					baseVertices[offset + 22],
					baseVertices[offset + 23]
				);
			}

			var arrays = new Godot.Collections.Array();
			arrays.Resize( (int)Mesh.ArrayType.Max );
			arrays[(int)Mesh.ArrayType.Vertex] = positions;
			arrays[(int)Mesh.ArrayType.Normal] = normals;
			arrays[(int)Mesh.ArrayType.Tangent] = tangents;
			arrays[(int)Mesh.ArrayType.Color] = colors;
			arrays[(int)Mesh.ArrayType.TexUV] = uvs;
			if ( mesh.HasSkin )
			{
				arrays[(int)Mesh.ArrayType.Bones] = bones;
				arrays[(int)Mesh.ArrayType.Weights] = weights;
			}
			arrays[(int)Mesh.ArrayType.Index] = surfaceIndices;

			// Build blend shape delta arrays for this surface
			Godot.Collections.Array<Godot.Collections.Array> blendShapeArrays = null;
			if ( morphNames.Count > 0 && mesh.MorphCornerMap != null )
			{
				blendShapeArrays = new Godot.Collections.Array<Godot.Collections.Array>();
				for ( int m = 0; m < morphNames.Count; m++ )
				{
					var morphTarget = mesh.Morphs.Targets[m];
					var deltaPositions = new Vector3[surfVertCount];
					var deltaNormals = new Vector3[surfVertCount];

					for ( int i = 0; i < surfVertCount; i++ )
					{
						int srcVert = surfaceVertIndices[i];
						if ( srcVert < mesh.MorphCornerMap.CornerToSource.Length )
						{
							int sourceVert = mesh.MorphCornerMap.CornerToSource[srcVert];
							if ( sourceVert >= 0 && sourceVert < morphTarget.PositionDeltas.Length )
							{
								var dp = morphTarget.PositionDeltas[sourceVert];
								deltaPositions[i] = TransformPosition( dp.x, dp.y, dp.z );

								if ( morphTarget.NormalDeltas != null && sourceVert < morphTarget.NormalDeltas.Length )
								{
									var dn = morphTarget.NormalDeltas[sourceVert];
									deltaNormals[i] = TransformNormal( dn.x, dn.y, dn.z );
								}
							}
						}
					}

					var morphArray = new Godot.Collections.Array();
					morphArray.Resize( (int)Mesh.ArrayType.Max );
					morphArray[(int)Mesh.ArrayType.Vertex] = deltaPositions;
					morphArray[(int)Mesh.ArrayType.Normal] = deltaNormals;
					blendShapeArrays.Add( morphArray );
				}
			}

			arrayMesh.AddSurfaceFromArrays(
				Mesh.PrimitiveType.Triangles,
				arrays,
				blendShapeArrays
			);

			// Surface name and material
			int surface = arrayMesh.GetSurfaceCount() - 1;
			arrayMesh.SurfaceSetName( surface, batch.MaterialName ?? $"surface_{batchIndex}" );
			arrayMesh.SurfaceSetMaterial( surface, EffigyMaterialBridge.ToGodotMaterial( batch, resolver ) );
		}

		return arrayMesh;
	}

	/// <summary>
	/// Flat PBR factors from the batch (colour, roughness, metal, emission, alpha, sidedness).
	/// </summary>
	public static StandardMaterial3D ToGodotMaterial( in CpuMesh.DrawBatch batch, MaterialResolver resolver = null ) =>
		EffigyMaterialBridge.ToGodotMaterial( batch, resolver );
}
