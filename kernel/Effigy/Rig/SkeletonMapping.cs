using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>Standard skeleton a rig exports as. The names are the engines' own: Unity's
/// Humanoid avatar, Unreal's Manny, Mixamo's mixamorig rig, Maya HumanIK.</summary>
public enum SkeletonPreset
{
	UnityHumanoid,
	UnrealManny,
	Mixamo,
	HumanIK,
}

/// <summary>Standard-skeleton mapping: Geppetto bone names to a preset's names, and back.
///
/// The maps already in the tree point the other way or cover one engine:
/// <see cref="CitizenBoneMap.UnrealStyle"/> maps Unreal-style names INTO Geppetto, and
/// <see cref="MocapBoneMap.Mixamo"/> maps mixamorig names INTO Geppetto. This is the export
/// direction — Geppetto names OUT — so an export can rename the skeleton, and the inverse is
/// the round-trip check that the rename loses nothing.
///
/// NAMING. Geppetto has two leg names in the tree (`leg_upper_L` from the retargeting work,
/// `thigh_L` from the humanoid template) and two foot names (`ankle_L`, `foot_L`). Both are
/// accepted on the way out; the way back normalises to the retargeting names
/// (`leg_upper_L`, `leg_lower_L`, `ankle_L`) — a round-trip that lands on the alias counts
/// as normalised, not as a failure, and the report says so.
///
/// The root bone maps only where the preset HAS a root: Unreal Manny (`root`). Unity Humanoid,
/// Mixamo and HumanIK all hang the rig off the hips, so the root is unmapped by design — it
/// becomes the GameObject / character transform, not a bone. That is reported, not dropped:
/// unmapped bones are kept under their own names and listed, never silently deleted.</summary>
public static class SkeletonMapping
{
	/// <summary>Geppetto name to preset name. The Mixamo map is the inverse of
	/// <see cref="MocapBoneMap.Mixamo"/> — one source of truth, not a second copy of the
	/// table. The others are written out here because no in-tree table maps them in either
	/// direction.</summary>
	public static Dictionary<string, string> PresetMap( SkeletonPreset preset ) => preset switch
	{
		SkeletonPreset.Mixamo => Invert( MocapBoneMap.Mixamo() ),
		SkeletonPreset.UnityHumanoid => UnityMap(),
		SkeletonPreset.UnrealManny => MannyMap(),
		SkeletonPreset.HumanIK => HumanIkMap(),
		_ => throw new ArgumentOutOfRangeException( nameof( preset ) ),
	};

	/// <summary>One Geppetto bone name to its preset name, or null when the preset has no
	/// such bone (the root for Unity/Mixamo/HumanIK, a custom bone anywhere).</summary>
	public static string MapName( string geppetto, SkeletonPreset preset )
	{
		if ( string.IsNullOrWhiteSpace( geppetto ) )
			return null;
		return PresetMap( preset ).TryGetValue( geppetto, out var mapped ) ? mapped : null;
	}

	/// <summary>Rename a skeleton to a preset: a clone, so the original is untouched. Bones
	/// the preset has no name for keep their Geppetto names and are listed in
	/// <paramref name="unmapped"/> — kept and reported, never dropped.</summary>
	public static Skeleton MapSkeleton( Skeleton skeleton, SkeletonPreset preset, out List<string> unmapped )
	{
		if ( skeleton is null )
			throw new ArgumentNullException( nameof( skeleton ) );
		var map = PresetMap( preset );
		var renamed = skeleton.Clone();
		unmapped = new List<string>();
		for ( var i = 0; i < renamed.Count; i++ )
		{
			var name = renamed.Bones[i].Name;
			if ( map.TryGetValue( name, out var mapped ) )
				renamed.RenameBone( i, mapped );
			else
				unmapped.Add( name );
		}
		return renamed;
	}

	/// <summary>Round-trip: map to the preset and back. True when every bone that mapped out
	/// comes back under its original name; <paramref name="normalised"/> lists bones that
	/// came back under the canonical alias (thigh_L → leg_upper_L and friends);
	/// <paramref name="unmapped"/> lists bones the preset has no name for. A rig whose
	/// mapped bones all round-trip is safe to export under that preset and re-import.</summary>
	public static bool RoundTrip( Skeleton skeleton, SkeletonPreset preset, out List<string> normalised, out List<string> unmapped )
	{
		if ( skeleton is null )
			throw new ArgumentNullException( nameof( skeleton ) );
		var forward = PresetMap( preset );
		var back = Invert( forward );
		normalised = new List<string>();
		unmapped = new List<string>();
		var exact = true;
		foreach ( var bone in skeleton.Bones )
		{
			if ( !forward.TryGetValue( bone.Name, out var mapped ) )
			{
				unmapped.Add( bone.Name );
				continue;
			}
			if ( !back.TryGetValue( mapped, out var returned ) || returned != bone.Name )
			{
				// An alias that lands on the canonical name is a normalisation, not a loss.
				if ( returned is not null && CanonicalAlias( bone.Name ) == returned )
					normalised.Add( bone.Name );
				else
					exact = false;
			}
		}
		return exact;
	}

	static Dictionary<string, string> Invert( IReadOnlyDictionary<string, string> map )
	{
		var inverted = new Dictionary<string, string>( StringComparer.Ordinal );
		foreach ( var (key, value) in map )
		{
			// First writer wins: the canonical name keeps the inverse entry, an alias that
			// maps to the same preset name does not steal it.
			if ( !inverted.ContainsKey( value ) )
				inverted[value] = key;
		}
		return inverted;
	}

	/// <summary>Canonical name for a leg alias: thigh_L → leg_upper_L, calf_L → leg_lower_L,
	/// foot_L → ankle_L. Anything else is already canonical.</summary>
	public static string CanonicalAlias( string name ) => name switch
	{
		"thigh_L" => "leg_upper_L",
		"thigh_R" => "leg_upper_R",
		"calf_L" => "leg_lower_L",
		"calf_R" => "leg_lower_R",
		"foot_L" => "ankle_L",
		"foot_R" => "ankle_R",
		_ => name,
	};

	static Dictionary<string, string> UnityMap()
	{
		var map = new Dictionary<string, string>( StringComparer.Ordinal );
		void Add( string geppetto, string preset ) => map[geppetto] = preset;
		Add( "pelvis", "Hips" );
		Add( "spine_0", "Spine" );
		Add( "spine_1", "Chest" );
		Add( "spine_2", "UpperChest" );
		Add( "neck_0", "Neck" );
		Add( "head", "Head" );
		foreach ( var side in new[] { "L", "R" } )
		{
			var u = side == "L" ? "Left" : "Right";
			Add( $"clavicle_{side}", $"{u}Shoulder" );
			Add( $"arm_upper_{side}", $"{u}UpperArm" );
			Add( $"arm_lower_{side}", $"{u}LowerArm" );
			Add( $"hand_{side}", $"{u}Hand" );
			Add( $"leg_upper_{side}", $"{u}UpperLeg" );
			Add( $"thigh_{side}", $"{u}UpperLeg" );
			Add( $"leg_lower_{side}", $"{u}LowerLeg" );
			Add( $"calf_{side}", $"{u}LowerLeg" );
			Add( $"ankle_{side}", $"{u}Foot" );
			Add( $"foot_{side}", $"{u}Foot" );
			Add( $"ball_{side}", $"{u}Toes" );
			foreach ( var (finger, unity) in new[] { ("thumb", "Thumb"), ("index", "Index"), ("middle", "Middle"), ("ring", "Ring"), ("pinky", "Little") } )
			foreach ( var (joint, part) in new[] { (0, "Proximal"), (1, "Intermediate"), (2, "Distal") } )
				Add( $"finger_{finger}_{joint}_{side}", $"{u} {unity} {part}" );
		}
		return map;
	}

	static Dictionary<string, string> MannyMap()
	{
		var map = new Dictionary<string, string>( StringComparer.Ordinal );
		void Add( string geppetto, string preset ) => map[geppetto] = preset;
		Add( "root", "root" );
		Add( "pelvis", "pelvis" );
		Add( "spine_0", "spine_01" );
		Add( "spine_1", "spine_02" );
		Add( "spine_2", "spine_03" );
		Add( "neck_0", "neck_01" );
		Add( "head", "head" );
		foreach ( var side in new[] { "L", "R" } )
		{
			var m = side == "L" ? "l" : "r";
			Add( $"clavicle_{side}", $"clavicle_{m}" );
			Add( $"arm_upper_{side}", $"upperarm_{m}" );
			Add( $"arm_lower_{side}", $"lowerarm_{m}" );
			Add( $"hand_{side}", $"hand_{m}" );
			Add( $"leg_upper_{side}", $"thigh_{m}" );
			Add( $"thigh_{side}", $"thigh_{m}" );
			Add( $"leg_lower_{side}", $"calf_{m}" );
			Add( $"calf_{side}", $"calf_{m}" );
			Add( $"ankle_{side}", $"foot_{m}" );
			Add( $"foot_{side}", $"foot_{m}" );
			Add( $"ball_{side}", $"ball_{m}" );
			foreach ( var finger in new[] { "thumb", "index", "middle", "ring", "pinky" } )
			for ( var joint = 0; joint < 3; joint++ )
				Add( $"finger_{finger}_{joint}_{side}", $"{finger}_0{joint + 1}_{m}" );
		}
		return map;
	}

	static Dictionary<string, string> HumanIkMap()
	{
		var map = new Dictionary<string, string>( StringComparer.Ordinal );
		void Add( string geppetto, string preset ) => map[geppetto] = preset;
		Add( "pelvis", "Hips" );
		Add( "spine_0", "Spine" );
		Add( "spine_1", "Chest" );
		// HumanIK's classic skeleton has two spine bones; a third spine bone is kept under
		// its own name and reported as unmapped, not forced onto Chest.
		Add( "neck_0", "Neck" );
		Add( "head", "Head" );
		foreach ( var side in new[] { "L", "R" } )
		{
			var h = side == "L" ? "Left" : "Right";
			Add( $"clavicle_{side}", $"{h}Shoulder" );
			Add( $"arm_upper_{side}", $"{h}Arm" );
			Add( $"arm_lower_{side}", $"{h}ForeArm" );
			Add( $"hand_{side}", $"{h}Hand" );
			Add( $"leg_upper_{side}", $"{h}UpLeg" );
			Add( $"thigh_{side}", $"{h}UpLeg" );
			Add( $"leg_lower_{side}", $"{h}Leg" );
			Add( $"calf_{side}", $"{h}Leg" );
			Add( $"ankle_{side}", $"{h}Foot" );
			Add( $"foot_{side}", $"{h}Foot" );
			Add( $"ball_{side}", $"{h}ToeBase" );
			foreach ( var (finger, name) in new[] { ("thumb", "Thumb"), ("index", "Index"), ("middle", "Middle"), ("ring", "Ring"), ("pinky", "Pinky") } )
			for ( var joint = 0; joint < 3; joint++ )
				Add( $"finger_{finger}_{joint}_{side}", $"{h}Hand{name}{joint + 1}" );
		}
		return map;
	}
}
