using System;
using System.Collections.Generic;
using System.IO;
using Effigy;

namespace Marionette.Desktop;

/// <summary>
/// Survey's third-person player: suit without its discarded placeholder head, plus the
/// separately authored head and neck ring. Matches player_suit.vmdl, player_head.vmdl,
/// and mirrors.scene's bone-merge assembly without loading any engine assets.
/// </summary>
public static class SurveyPlayerAssembly
{
	static readonly HashSet<string> HeadPieces = new( StringComparer.Ordinal )
	{
		"face_head", "face_eyes", "face_beard", "face_hair", "neck_ring", "neck_bellows"
	};

	public static bool IsSurveyPlayer( string path ) =>
		Path.GetFileName( path ).Equals( "player_suit.fbx", StringComparison.OrdinalIgnoreCase ) &&
		File.Exists( Path.Combine( Path.GetDirectoryName( path ) ?? ".", "player_head.fbx" ) );

	public static FbxImport.Result Load( string suitPath )
	{
		var suit = FbxImport.Read( File.ReadAllBytes( suitPath ) );
		var head = FbxImport.Read( File.ReadAllBytes( Path.Combine( Path.GetDirectoryName( suitPath ) ?? ".", "player_head.fbx" ) ) );
		if ( suit.Skeleton is not { Count: > 0 } ) throw new InvalidDataException( "Survey's suit has no skeleton." );
		for ( int i = suit.Pieces.Count - 1; i >= 0; i-- )
		{
			if ( suit.Pieces[i].Name != "suit_head" ) continue;
			suit.Pieces.RemoveAt( i );
			suit.Weights.RemoveAt( i );
		}

		// FBX imports use the same units/axes. Reconcile each head vertex's bind position
		// against the suit bind skeleton, as an engine bone merge does, before resolving weights.
		var remap = new Dictionary<int, int>();
		var bindCorrections = new Dictionary<string, Xform>( StringComparer.Ordinal );
		foreach ( var (slot, name) in head.MaterialSlots )
		{
			int dest = 0;
			foreach ( int existing in suit.MaterialSlots.Keys ) dest = Math.Max( dest, existing + 1 );
			suit.MaterialSlots[dest] = name;
			remap[slot] = dest;
		}
		int added = 0;
		for ( int i = 0; i < head.Pieces.Count; i++ )
		{
			var piece = head.Pieces[i];
			if ( !HeadPieces.Contains( piece.Name ) ) continue;
			var weights = head.Weights[i] ?? throw new InvalidDataException( $"Survey head piece '{piece.Name}' is unskinned." );
			for ( int v = 0; v < piece.Mesh.VertexCount; v++ )
			{
				var p = piece.Mesh.Positions[v];
				Vec3 merged = Vec3.Zero;
				float sum = 0;
				foreach ( var (name, weight) in weights[v] )
				{
					if ( !bindCorrections.TryGetValue( name, out var correction ) )
					{
						int source = head.Skeleton.IndexOf( name ), target = suit.Skeleton.IndexOf( name );
						if ( source < 0 || target < 0 ) throw new InvalidDataException( $"Cannot bone-merge Survey head bone '{name}'." );
						correction = suit.Skeleton.WorldBind( target ) * head.Skeleton.WorldBind( source ).Inverse;
						bindCorrections[name] = correction;
					}
					merged += correction.TransformPoint( p ) * weight;
					sum += weight;
				}
				if ( sum <= 0 ) throw new InvalidDataException( $"Survey head piece '{piece.Name}' has an unweighted vertex." );
				piece.Mesh.Positions[v] = merged / sum;
			}
			foreach ( var face in piece.Mesh.Faces ) face.Material = remap[face.Material];
			suit.Pieces.Add( piece );
			suit.Weights.Add( weights );
			added++;
		}
		if ( added != HeadPieces.Count ) throw new InvalidDataException( $"Expected {HeadPieces.Count} Survey head/neck pieces, found {added}." );
		return suit;
	}
}
