using System;
using System.Collections.Generic;

// HARNESS-ONLY STUBS. The real Feature/Body types live in
// kernel/Effigy/Features/Feature.cs, which pulls in half the kernel; MapBrush.cs
// (real, included verbatim) only needs these signatures for its MapBrushFeature
// tail, which no hintskip test executes. Signatures copied from the real file.

namespace Effigy;

public interface IParam
{
	string Label { get; }
}

public sealed class Body
{
	public string Id;
	public string Name;
	public PolyMesh Mesh;

	public Body( string id, string name, PolyMesh mesh )
	{
		Id = id;
		Name = name;
		Mesh = mesh;
	}
}

public sealed class FeatureContext
{
	public List<Body> Bodies = new();

	public string NewBodyId() => Guid.NewGuid().ToString( "N" )[..8];
}

public abstract class Feature
{
	public string Name = "";

	public abstract string TypeName { get; }
	public abstract IReadOnlyList<IParam> Parameters { get; }

	protected abstract void Execute( FeatureContext ctx );

	protected static void FailOn( string parameterLabel, string problem, string cause, params string[] remedies ) =>
		throw new InvalidOperationException( $"{parameterLabel}: {problem} ({cause})" );

	protected void Info( string text )
	{
	}
}

/// <summary>
/// HARNESS-ONLY STUBS for the peripheral types PolyMesh.cs (real, included
/// verbatim) holds as fields. Only the members PolyMesh itself touches are
/// stubbed; the real PaintCanvas / SkinWeights / MorphDriver files pull in the
/// rig and paint subsystems (Quat, Skeleton, PaintChannel, RigEase, ...) and no
/// hintskip test exercises them. MapBrush.ToMesh only uses PolyMesh.Positions,
/// AddFace and FaceCount.
/// </summary>
public sealed class SkinWeights
{
	public int Count => 0;
	public SkinWeights Clone() => new();
}

public sealed class PaintCanvas
{
	public PaintCanvas Clone() => new();
}

public sealed class MorphDriver
{
	public MorphDriver Clone() => (MorphDriver)MemberwiseClone();
}
