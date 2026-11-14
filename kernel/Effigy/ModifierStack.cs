using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>What a live modifier does to the mesh.</summary>
public enum ModifierKind
{
	/// <summary>The mesh plus its mirror image across the axis plane, welded at the seam.</summary>
	Mirror,

	/// <summary>Round (or chamfer) every edge sharper than the angle threshold.</summary>
	Bevel,

	/// <summary>Union / subtract / intersect with a tool mesh, through the board's
	/// Manifold backend when it is installed — never a second boolean.</summary>
	Boolean,

	/// <summary>Give the mesh a wall: an inner shell offset by the thickness.</summary>
	Solidify,

	/// <summary>Catmull-Clark subdivision.</summary>
	Subdivision,
}

/// <summary>
/// One live modifier on a mesh: a named, enableable, parameterised step.
/// The tool mesh of a Boolean modifier is a live reference the host owns
/// (a body in the same document, a cutter the user placed); it is not
/// serialised with the modifier, and a null tool refuses honestly at
/// evaluation time instead of at build time.
/// </summary>
public sealed class Modifier
{
	public Modifier( ModifierKind kind )
	{
		Kind = kind;
		Name = kind.ToString();
	}

	public ModifierKind Kind { get; set; }
	public string Name { get; set; }
	public bool Enabled { get; set; } = true;

	/// <summary>Every kind here changes topology today. Kept as a property so a
	/// future non-topology modifier (a smooth, a displace) can answer false and
	/// the apply warning below stays correct without touching the stack.</summary>
	public bool ChangesTopology => true;

	// Mirror
	public MirrorAxis Axis { get; set; } = MirrorAxis.X;

	// Bevel
	public float Width { get; set; } = 0.1f;
	public int Segments { get; set; } = 1;
	public bool Rounded { get; set; } = false;
	public float AngleThresholdDegrees { get; set; } = 30f;

	// Boolean
	public BooleanOp Op { get; set; } = BooleanOp.Union;
	public PolyMesh Tool { get; set; }

	// Solidify
	public float Thickness { get; set; } = 0.1f;
	public bool Smooth { get; set; } = false;

	// Subdivision
	public int Levels { get; set; } = 1;

	public Modifier Clone() => (Modifier)MemberwiseClone();
}

/// <summary>The result of applying a stack: the evaluated mesh, the warnings
/// the apply produced, and how many morph targets were dropped because the
/// vertex count changed.</summary>
public sealed class ApplyResult
{
	public ApplyResult( PolyMesh mesh, IReadOnlyList<string> warnings, int droppedMorphs )
	{
		Mesh = mesh;
		Warnings = warnings;
		DroppedMorphs = droppedMorphs;
	}

	public PolyMesh Mesh { get; }
	public IReadOnlyList<string> Warnings { get; }
	public int DroppedMorphs { get; }
}

/// <summary>
/// A non-destructive modifier stack, Blender's modifier stack for the
/// kernel: an ordered, reorderable list of live modifiers evaluated on
/// demand, with the base mesh untouched until the stack is applied.
///
/// Evaluate is the live path and the export path — what an exporter writes
/// is Evaluate(base), so apply-at-export and the live preview are the same
/// mesh by construction, and the base is never rewritten by looking at it.
/// Apply is the destructive path: it evaluates, replaces the base, and the
/// caller clears the stack (MeshEditSession.ApplyModifiers does both in one
/// undo step). A modifier that fails — a boolean whose solids do not touch,
/// a solidify on an invalid mesh, a bevel with no width — is skipped and
/// named in LastError, so one bad modifier never takes the whole stack down.
///
/// The stack is engine-free: every modifier is one of the kernel's own
/// operations (MeshTransform, EdgeBlend, MeshBoolean, ShellOperation,
/// CatmullClark), and the Boolean modifier routes through
/// MeshBoolean.Provider, which is the Manifold backend once
/// ManifoldBoolean.Install() has run — no second boolean lives here.
///
/// HOST WIRING (final integrator, shared entry point — not this file): the
/// exporter calls stack.Evaluate(baseMesh) and writes the result; document
/// persistence of the stack itself (its modifiers and their parameters)
/// belongs to the document owner, not to this type.
/// </summary>
public sealed class ModifierStack
{
	readonly List<Modifier> _modifiers = new();

	public IReadOnlyList<Modifier> Modifiers => _modifiers;

	/// <summary>The last modifier that failed during an Evaluate, "name: reason",
	/// or null when the last evaluation ran clean. A failed modifier is skipped,
	/// never fatal.</summary>
	public string LastError { get; private set; }

	public void Add( Modifier modifier )
	{
		if ( modifier is null ) throw new ArgumentNullException( nameof( modifier ) );
		_modifiers.Add( modifier );
	}

	public bool Remove( Modifier modifier ) => _modifiers.Remove( modifier );

	public void Clear() => _modifiers.Clear();

	/// <summary>Reorder: move the modifier at <paramref name="from"/> to
	/// <paramref name="to"/>. Evaluation order is list order.</summary>
	public void Move( int from, int to )
	{
		if ( from < 0 || from >= _modifiers.Count ) throw new ArgumentOutOfRangeException( nameof( from ) );
		if ( to < 0 || to >= _modifiers.Count ) throw new ArgumentOutOfRangeException( nameof( to ) );
		if ( from == to ) return;
		var m = _modifiers[from];
		_modifiers.RemoveAt( from );
		_modifiers.Insert( to, m );
	}

	/// <summary>True when an enabled modifier would change the vertex count,
	/// which is when an apply has something to warn about.</summary>
	public bool WillChangeTopology() => _modifiers.Any( m => m.Enabled && m.ChangesTopology );

	/// <summary>What an apply would cost, before it costs it: morph targets
	/// that would be dropped and sculpt layers that would no longer fit.
	/// Morph deltas and sculpt-layer deltas are per-vertex arrays sized to
	/// the mesh they were captured on; a topology-changing apply makes them
	/// meaningless, so the apply warns first and drops them, rather than
	/// keeping a list that no longer lines up with the vertices.</summary>
	public IReadOnlyList<string> TopologyWarnings( PolyMesh mesh, IReadOnlyList<SculptLayer> sculptLayers = null )
	{
		if ( mesh is null ) throw new ArgumentNullException( nameof( mesh ) );
		var warnings = new List<string>();
		if ( !WillChangeTopology() ) return warnings;
		if ( mesh.HasMorphs )
			warnings.Add( $"Applying the stack changes the vertex count, so the {mesh.ValidMorphs.Count()} morph target(s) on this mesh will be dropped. Capture them again after the apply if they still matter." );
		if ( sculptLayers is not null && sculptLayers.Count > 0 )
			warnings.Add( $"{sculptLayers.Count} sculpt layer(s) are sized to this mesh's {mesh.VertexCount} vertices; applying the stack changes the vertex count, so the layers will no longer fit and must be re-captured." );
		return warnings;
	}

	/// <summary>The live evaluation, and the mesh an exporter writes: the base
	/// mesh run through every enabled modifier in order. The input is never
	/// mutated. A modifier that fails is skipped and named in LastError.
	/// Morph targets are stripped from the result when the vertex count
	/// changed — they are sized to the old count and ValidMorphs would drop
	/// them anyway; the base mesh keeps its own.</summary>
	public PolyMesh Evaluate( PolyMesh mesh )
	{
		if ( mesh is null ) throw new ArgumentNullException( nameof( mesh ) );
		LastError = null;
		var result = mesh.Clone();
		foreach ( var m in _modifiers )
		{
			if ( !m.Enabled ) continue;
			try
			{
				result = ApplyModifier( m, result );
			}
			catch ( Exception ex )
			{
				LastError = $"{m.Name}: {ex.Message}";
			}
		}
		if ( result.VertexCount != mesh.VertexCount && result.Morphs.Count > 0 )
			result.Morphs.Clear();
		return result;
	}

	/// <summary>The destructive path: evaluate, and say what it cost. Does not
	/// clear the stack — the caller does that once it has committed the
	/// result (MeshEditSession.ApplyModifiers clears it in the same undo
	/// step that replaces the mesh).</summary>
	public ApplyResult Apply( PolyMesh mesh, IReadOnlyList<SculptLayer> sculptLayers = null )
	{
		if ( mesh is null ) throw new ArgumentNullException( nameof( mesh ) );
		var warnings = TopologyWarnings( mesh, sculptLayers );
		var evaluated = Evaluate( mesh );
		var dropped = mesh.Morphs.Count - evaluated.Morphs.Count;
		return new ApplyResult( evaluated, warnings, dropped );
	}

	static PolyMesh ApplyModifier( Modifier m, PolyMesh mesh )
	{
		switch ( m.Kind )
		{
			case ModifierKind.Mirror:
				return Mirror( mesh, m.Axis );
			case ModifierKind.Bevel:
			{
				if ( !float.IsFinite( m.Width ) || m.Width <= 0f )
					throw new InvalidOperationException( "The bevel width has to be more than 0." );
				var report = m.Rounded
					? EdgeBlend.FilletReport( mesh, m.Width, m.AngleThresholdDegrees, Math.Max( 1, m.Segments ) )
					: EdgeBlend.ChamferReport( mesh, m.Width, m.AngleThresholdDegrees );
				if ( report.Failure is { } failure )
					throw new InvalidOperationException( failure.Cause is null ? failure.Problem : $"{failure.Problem}. {failure.Cause}" );
				return report.Mesh;
			}
			case ModifierKind.Boolean:
				if ( m.Tool is null )
					throw new InvalidOperationException( $"The {m.Name} modifier needs a tool mesh." );
				return MeshBoolean.Apply( m.Op, mesh, m.Tool );
			case ModifierKind.Solidify:
				return MeshSolidify.Solidify( mesh, m.Thickness, m.Smooth );
			case ModifierKind.Subdivision:
				return CatmullClark.Subdivide( mesh, Math.Max( 1, m.Levels ) );
			default:
				throw new InvalidOperationException( $"Unknown modifier kind {m.Kind}." );
		}
	}

	/// <summary>Mirror across the axis plane, welded at the seam. X goes
	/// straight through MeshTransform.MirrorX; Y and Z permute the axis onto
	/// X first, so the seam welding and skin-weight handling are the same
	/// code in every direction.</summary>
	static PolyMesh Mirror( PolyMesh mesh, MirrorAxis axis )
	{
		if ( axis == MirrorAxis.None ) return mesh.Clone();
		if ( axis == MirrorAxis.X ) return MeshModifiers.MirrorX( mesh );
		var permuted = mesh.Clone();
		for ( var i = 0; i < permuted.Positions.Count; i++ )
		{
			var p = permuted.Positions[i];
			permuted.Positions[i] = axis == MirrorAxis.Y ? new Vec3( p.y, p.x, p.z ) : new Vec3( p.z, p.y, p.x );
		}
		permuted.InvalidateVertexCache();
		var mirrored = MeshModifiers.MirrorX( permuted );
		for ( var i = 0; i < mirrored.Positions.Count; i++ )
		{
			var p = mirrored.Positions[i];
			mirrored.Positions[i] = axis == MirrorAxis.Y ? new Vec3( p.y, p.x, p.z ) : new Vec3( p.z, p.y, p.x );
		}
		mirrored.InvalidateVertexCache();
		return mirrored;
	}
}
