#nullable enable
using System;
using System.Collections.Generic;
using System.Text;

namespace Effigy;

public sealed class BreakpieceItem
{
	public string Name = "piece_0";
	public string ModelPath = "";
	public float Mass = 5.0f;
	public Vec3 Offset = Vec3.Zero;
	public float Health = 25f;
	public float ExplosionImpulse = 150f;
}

public sealed class RagdollJoint
{
	public string ParentBone = "";
	public string ChildBone = "";
	public string JointType = "ball_socket"; // "ball_socket", "conical", or "revolute"
	public float MinAngle = -45f;
	public float MaxAngle = 60f;
	public float TwistMin = -30f;
	public float TwistMax = 30f;
	public float Friction = 0.2f;
}

/// <summary>
/// Source 2 Destruction & Physics: compiles BreakPieceList KV3 markup for destructible props
/// and PhysicsJointList constraints for characters and ragdolls.
/// </summary>
public static class PhysicsBreakpiece
{
	/// <summary>
	/// Generates Source 2 ModelDoc KV3 markup for BreakPieceList.
	/// </summary>
	public static string EmitBreakPieceList( IEnumerable<BreakpieceItem> pieces, string shatterSound = "break_wood_heavy" )
	{
		var sb = new StringBuilder();
		sb.AppendLine( "BreakPieceList" );
		sb.AppendLine( "{" );
		sb.AppendLine( "\tchildren =" );
		sb.AppendLine( "\t[" );

		foreach ( var p in pieces )
		{
			sb.AppendLine( "\t\t{" );
			sb.AppendLine( "\t\t\t_class = \"BreakPieceExternal\"" );
			sb.AppendLine( $"\t\t\tname = \"{p.Name}\"" );
			sb.AppendLine( $"\t\t\tmodel_name = \"{p.ModelPath}\"" );
			sb.AppendLine( $"\t\t\tmass = {p.Mass:F1}" );
			sb.AppendLine( $"\t\t\thealth = {p.Health:F1}" );
			sb.AppendLine( $"\t\t\tburst_scale = {p.ExplosionImpulse:F1}" );
			sb.AppendLine( $"\t\t\tcollision_group = \"debris\"" );
			sb.AppendLine( "\t\t}," );
		}

		sb.AppendLine( "\t]" );
		sb.AppendLine( "}" );
		return sb.ToString();
	}

	/// <summary>
	/// Single-file destruction: every shard's mesh + hull bundled inside the primary .vmdl
	/// as BreakPieceEmbedded children, so no separate .vmdl per shard is needed.
	/// </summary>
	public static string EmitBreakPieceEmbeddedList( IEnumerable<BreakpieceItem> pieces )
	{
		var sb = new StringBuilder();
		sb.AppendLine( "BreakPieceList" );
		sb.AppendLine( "{" );
		sb.AppendLine( "\tchildren =" );
		sb.AppendLine( "\t[" );

		foreach ( var p in pieces )
		{
			sb.AppendLine( "\t\t{" );
			sb.AppendLine( "\t\t\t_class = \"BreakPieceEmbedded\"" );
			sb.AppendLine( $"\t\t\tname = \"{p.Name}\"" );
			sb.AppendLine( "\t\t\tchildren =" );
			sb.AppendLine( "\t\t\t[" );
			sb.AppendLine( "\t\t\t\t{" );
			sb.AppendLine( "\t\t\t\t\t_class = \"RenderMeshFile\"" );
			sb.AppendLine( $"\t\t\t\t\tfilename = \"{p.ModelPath}\"" );
			sb.AppendLine( "\t\t\t\t}," );
			sb.AppendLine( "\t\t\t\t{" );
			sb.AppendLine( "\t\t\t\t\t_class = \"PhysicsHullFile\"" );
			sb.AppendLine( $"\t\t\t\t\tfilename = \"{p.ModelPath}\"" );
			sb.AppendLine( "\t\t\t\t}," );
			sb.AppendLine( "\t\t\t]" );
			sb.AppendLine( $"\t\t\tmass = {p.Mass:F1}" );
			sb.AppendLine( $"\t\t\thealth = {p.Health:F1}" );
			sb.AppendLine( $"\t\t\tburst_scale = {p.ExplosionImpulse:F1}" );
			sb.AppendLine( "\t\t\tcollision_group = \"debris\"" );
			sb.AppendLine( "\t\t}," );
		}

		sb.AppendLine( "\t]" );
		sb.AppendLine( "}" );
		return sb.ToString();
	}

	/// <summary>
	/// Generates Source 2 ModelDoc KV3 markup for PhysicsJointList (ragdoll constraints).
	/// </summary>
	public static string EmitPhysicsJointList( IEnumerable<RagdollJoint> joints )
	{
		var sb = new StringBuilder();
		sb.AppendLine( "PhysicsJointList" );
		sb.AppendLine( "{" );
		sb.AppendLine( "\tchildren =" );
		sb.AppendLine( "\t[" );

		foreach ( var j in joints )
		{
			sb.AppendLine( "\t\t{" );
			sb.AppendLine( $"\t\t\t_class = \"{(j.JointType == "revolute" ? "PhysicsJointRevolute" : "PhysicsJointConical")}\"" );
			sb.AppendLine( $"\t\t\tparent_bone = \"{j.ParentBone}\"" );
			sb.AppendLine( $"\t\t\tchild_bone = \"{j.ChildBone}\"" );
			sb.AppendLine( $"\t\t\tmin_angles = [ {j.MinAngle:F1}, {j.MinAngle:F1}, {j.TwistMin:F1} ]" );
			sb.AppendLine( $"\t\t\tmax_angles = [ {j.MaxAngle:F1}, {j.MaxAngle:F1}, {j.TwistMax:F1} ]" );
			sb.AppendLine( $"\t\t\tfriction = {j.Friction:F2}" );
			sb.AppendLine( "\t\t}," );
		}

		sb.AppendLine( "\t]" );
		sb.AppendLine( "}" );
		return sb.ToString();
	}
}
