using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Effigy;

/// <summary>
/// Writes Linework 2D drawings (.linework JSON) matching the native Linework schema
/// (linework/src/model.cpp: docToJson / strokeToJson).
/// </summary>
public static class LineworkWriter
{
	public static string ToJson( LineworkDocument doc, string paper = "graphite", string grid = "dots" )
	{
		var root = new JsonObject
		{
			["app"] = "linework",
			["v"] = 2,
			["name"] = string.IsNullOrWhiteSpace( doc.Name ) ? "untitled" : doc.Name,
			["paper"] = paper,
			["grid"] = grid,
			["material"] = JsonSerializer.SerializeToNode( doc.Material, LineworkMaterialSettings.JsonOptions ),
			["sym"] = new JsonObject
			{
				["on"] = doc.SymOn,
				["x"] = MathF.Round( doc.SymX, 2 )
			}
		};

		var layersArray = new JsonArray();
		foreach ( var layer in doc.Layers )
		{
			var layerObj = new JsonObject
			{
				["id"] = string.IsNullOrWhiteSpace( layer.Id ) ? Guid.NewGuid().ToString( "N" ) : layer.Id,
				["name"] = string.IsNullOrWhiteSpace( layer.Name ) ? "Layer" : layer.Name,
				["visible"] = layer.Visible,
				["locked"] = layer.Locked,
				["opacity"] = layer.Opacity,
				["height"] = layer.Height,
				["roughness"] = layer.Roughness,
				["metalness"] = layer.Metalness
			};

			if ( layer.Ref )
				layerObj["ref"] = true;

			var strokesArray = new JsonArray();
			foreach ( var stroke in layer.Strokes )
			{
				var strokeObj = new JsonObject
				{
					["id"] = string.IsNullOrWhiteSpace( stroke.Id ) ? Guid.NewGuid().ToString( "N" ) : stroke.Id,
					["tool"] = string.IsNullOrWhiteSpace( stroke.Tool ) ? "pen" : stroke.Tool,
					["color"] = string.IsNullOrWhiteSpace( stroke.Color ) ? "#ecebe6" : stroke.Color,
					["size"] = stroke.Size,
					["opacity"] = stroke.Opacity,
					["height"] = stroke.Height,
					["roughness"] = stroke.Roughness,
					["metalness"] = stroke.Metalness,
					["channels"] = (int)stroke.Channels
				};

				if ( stroke.Closed )
					strokeObj["closed"] = true;

				var ptsArray = new JsonArray();
				for ( var i = 0; i < stroke.Points.Count; i++ )
				{
					var pt = stroke.Points[i];
					var p = i < stroke.Pressures.Count ? stroke.Pressures[i] : 1f;
					ptsArray.Add( (double)MathF.Round( pt.x, 2 ) );
					ptsArray.Add( (double)MathF.Round( pt.y, 2 ) );
					ptsArray.Add( (double)MathF.Round( p, 3 ) );
				}
				strokeObj["pts"] = ptsArray;

				if ( stroke.Holes.Count > 0 )
				{
					var holesArray = new JsonArray();
					foreach ( var hole in stroke.Holes )
					{
						var hPtsArray = new JsonArray();
						foreach ( var hp in hole )
						{
							hPtsArray.Add( (double)MathF.Round( hp.x, 2 ) );
							hPtsArray.Add( (double)MathF.Round( hp.y, 2 ) );
							hPtsArray.Add( 1.0 );
						}
						holesArray.Add( hPtsArray );
					}
					strokeObj["holes"] = holesArray;
				}

				strokesArray.Add( strokeObj );
			}

			layerObj["strokes"] = strokesArray;
			layersArray.Add( layerObj );
		}

		root["layers"] = layersArray;
		return root.ToJsonString();
	}

	public static bool TrySave( LineworkDocument doc, string filePath, out string error )
	{
		error = null;
		try
		{
			var dir = Path.GetDirectoryName( filePath );
			if ( !string.IsNullOrEmpty( dir ) && !Directory.Exists( dir ) )
				Directory.CreateDirectory( dir );

			var json = ToJson( doc );
			File.WriteAllText( filePath, json );
			return true;
		}
		catch ( Exception ex )
		{
			error = ex.Message;
			return false;
		}
	}

	public static void Write( LineworkDocument doc, string path ) => File.WriteAllText( path, ToJson( doc ) );

	public static string Serialize( LineworkDocument doc ) => ToJson( doc );
}
