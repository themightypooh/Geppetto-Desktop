using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Effigy;

/// <summary>
/// Strict offline scene bake plan — S&amp;box Lane 10, pure and engine-free.
///
/// The rule it enforces is the spec's: bake, never generate at runtime. A plan is a
/// finished list of real GameObjects — each with a compiled model, colliders, and game
/// components — plus the target .scene path. Nothing in here touches Sandbox, the
/// filesystem, or the editor: the engine shell (Editor/EffigyEditor/EffigySceneBaker.cs)
/// is the only part that writes a file, and it writes exactly what <see cref="Bake"/>
/// returns, after <see cref="Bake"/> has stripped the generator helpers.
///
/// WHAT BAKE DOES, in order:
///   1. <see cref="Validate"/> — every rule below, all failures reported at once, so a
///      bad plan is rejected whole instead of failing halfway through a scene write.
///   2. Strip generator helper components from every object. A component is a helper
///      when it is explicitly flagged (<see cref="BakeComponentSpec.IsGeneratorHelper"/>)
///      or its type name contains "GeneratorHelper". A component merely named
///      *Generator (a weather generator, a loot generator) is NOT stripped unless
///      flagged — stripping on that substring would eat real gameplay.
///   3. Copy everything that survives, so mutating the plan after baking (or the
///      baked scene after baking) cannot corrupt the other side.
///
/// VALIDATION RULES (each one is a test, not a comment):
///   - Project and scene names are plain names: no separators, no ".." traversal.
///     The scene name also carries no dot, so it cannot smuggle in an extension.
///   - At least one object; object names unique ignoring case; no empty objects
///     (no model AND no components — a baked scene must not contain a bare node).
///   - A model path, when present, ends .vmdl. An object with no model is allowed
///     (lights, empty holders) but then it must carry at least one component, so a
///     "ModelRenderer with nothing assigned" can never be baked: there is no
///     renderer emitted for a model-less object at all.
///   - Every material path ends .vmat. Material paths are validated and carried in
///     the baked scene, but they are NOT written as scene properties: no
///     ModelRenderer field for them is established in this tree's scene template
///     (see EffigyPlayerScene), and inventing one risks a scene that fails to
///     load. The .vmdl owns its materials; this plan proves the paths are sane.
///   - Positions, rotations, and scales are finite; scales are positive; a rotation
///     quaternion must not be all zero (it is not silently normalised — the bake
///     preserves the author's transform exactly).
///   - Every component has a type name and non-null property values.
///
/// LABELS. Baked UI and world labels follow strict rules, enforced by
/// <see cref="BakeLabelSpec.Format"/> and re-checked on bake: text word-wraps at
/// word boundaries, overlong words are hard-split so no line ever exceeds
/// <see cref="BakeLabelSpec.MaxWidthChars"/>, text past
/// <see cref="BakeLabelSpec.MaxLines"/> is cut with a trailing ellipsis on the
/// last visible line (never silently clipped), and the container grows to fit
/// the wrapped lines (<see cref="FormattedLabel.ContainerHeight"/>). Labels are
/// validated and formatted by the plan; the engine shell does not invent a
/// label component type for them (same reason as materials above), so the
/// formatted labels travel in <see cref="BakedScene"/> for the button UI to
/// render. A truncated label is also reported as a bake warning.
///
/// LIMITS, stated plainly: this proves the plan is well-formed, complete, and
/// helper-free. It does NOT prove the baked .scene loads cleanly in s&amp;box —
/// that needs an engine run, which headless testing cannot do, and no claim
/// to that effect is made here.
/// </summary>
public sealed class SceneBakePlan
{
	public string ProjectName { get; set; }
	public string SceneName { get; set; }
	public List<BakedObjectSpec> Objects { get; set; } = new List<BakedObjectSpec>();

	/// <summary>File name of the baked scene, e.g. "gearhead.scene".</summary>
	public string TargetFileName => SceneName + ".scene";

	/// <summary>Project-relative target, e.g. "Assets/scenes/gearhead.scene".</summary>
	public string TargetRelativePath => "Assets/scenes/" + TargetFileName;

	/// <summary>
	/// Absolute target under a projects root, e.g.
	/// "~/games/projects/geppetto/Assets/scenes/gearhead.scene" when the root is
	/// "~/games/projects" and the project is "geppetto".
	/// </summary>
	public string BuildTargetPath( string projectsRoot )
	{
		if ( string.IsNullOrWhiteSpace( projectsRoot ) )
			throw new ArgumentException( "Projects root must not be empty.", nameof( projectsRoot ) );
		return Path.Combine( projectsRoot, ProjectName, "Assets", "scenes", TargetFileName );
	}

	/// <summary>
	/// Structural check on a candidate target path, independent of this plan:
	/// it must end .scene, live exactly one level under Assets/scenes, and
	/// contain no ".." segment.
	/// </summary>
	public static bool IsValidTargetPath( string path )
	{
		if ( string.IsNullOrWhiteSpace( path ) ) return false;
		var parts = path.Replace( '\\', '/' ).Split( '/' );
		if ( parts.Length < 3 ) return false;
		if ( !parts[parts.Length - 1].EndsWith( ".scene", StringComparison.OrdinalIgnoreCase ) ) return false;
		for ( var i = 0; i < parts.Length; i++ )
		{
			if ( parts[i] == ".." ) return false;
			if ( parts[i] == "Assets" && i + 2 == parts.Length - 1 && parts[i + 1] == "scenes" ) return true;
		}
		return false;
	}

	/// <summary>Every validation failure, in plan order. Empty means bakeable.</summary>
	public List<string> Validate()
	{
		var errors = new List<string>();

		if ( string.IsNullOrWhiteSpace( ProjectName ) ) errors.Add( "Project name must not be empty." );
		else if ( ProjectName.Contains( '/' ) || ProjectName.Contains( '\\' ) || ProjectName.Contains( ".." ) )
			errors.Add( $"Project name '{ProjectName}' must be a plain name, not a path." );

		if ( string.IsNullOrWhiteSpace( SceneName ) ) errors.Add( "Scene name must not be empty." );
		else
		{
			if ( SceneName.Contains( '/' ) || SceneName.Contains( '\\' ) || SceneName.Contains( ".." ) )
				errors.Add( $"Scene name '{SceneName}' must be a plain name, not a path." );
			if ( SceneName.Contains( '.' ) )
				errors.Add( $"Scene name '{SceneName}' must not contain a dot — the .scene extension is added by the bake." );
		}

		if ( Objects is null || Objects.Count == 0 )
		{
			errors.Add( "A baked scene needs at least one object." );
			return errors;
		}

		var names = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		foreach ( var obj in Objects )
		{
			if ( obj is null ) { errors.Add( "Object list must not contain a null entry." ); continue; }
			var label = string.IsNullOrWhiteSpace( obj.Name ) ? "(unnamed object)" : $"'{obj.Name}'";

			if ( string.IsNullOrWhiteSpace( obj.Name ) ) errors.Add( "Every baked object needs a name." );
			else
			{
				if ( obj.Name.Contains( '/' ) || obj.Name.Contains( '\\' ) )
					errors.Add( $"Object {label} name must not contain a path separator." );
				if ( !names.Add( obj.Name ) )
					errors.Add( $"Duplicate object name {label} — scene object names must be unique." );
			}

			if ( !obj.Position.IsFinite() ) errors.Add( $"Object {label} position must be finite." );
			if ( !obj.Rotation.IsFinite() ) errors.Add( $"Object {label} rotation must be finite." );
			if ( obj.Rotation.IsZero() ) errors.Add( $"Object {label} rotation quaternion must not be all zero." );
			if ( !obj.Scale.IsFinite() ) errors.Add( $"Object {label} scale must be finite." );
			if ( obj.Scale.X <= 0 || obj.Scale.Y <= 0 || obj.Scale.Z <= 0 )
				errors.Add( $"Object {label} scale must be positive on every axis." );

			if ( obj.ModelPath is not null )
			{
				if ( obj.ModelPath.Length == 0 ) errors.Add( $"Object {label} model path must not be empty." );
				else
				{
					if ( !obj.ModelPath.EndsWith( ".vmdl", StringComparison.OrdinalIgnoreCase ) )
						errors.Add( $"Object {label} model '{obj.ModelPath}' must be a .vmdl file." );
					if ( obj.ModelPath.Contains( ".." ) )
						errors.Add( $"Object {label} model '{obj.ModelPath}' must not contain '..'." );
				}
			}

			if ( obj.MaterialPaths is not null )
			{
				foreach ( var material in obj.MaterialPaths )
				{
					if ( string.IsNullOrWhiteSpace( material ) ) errors.Add( $"Object {label} material path must not be empty." );
					else if ( !material.EndsWith( ".vmat", StringComparison.OrdinalIgnoreCase ) )
						errors.Add( $"Object {label} material '{material}' must be a .vmat file." );
					else if ( material.Contains( ".." ) )
						errors.Add( $"Object {label} material '{material}' must not contain '..'." );
				}
			}

			var components = obj.Components ?? new List<BakeComponentSpec>();
			if ( obj.ModelPath is null && components.Count == 0 )
				errors.Add( $"Object {label} has neither a model nor a component — a baked scene must not contain an empty object." );

			foreach ( var component in components )
			{
				if ( component is null ) { errors.Add( $"Object {label} component list must not contain a null entry." ); continue; }
				if ( string.IsNullOrWhiteSpace( component.TypeName ) )
					errors.Add( $"Object {label} has a component with no type name." );
				if ( component.Properties is null )
					errors.Add( $"Object {label} component '{component.TypeName}' properties must not be null." );
				else
				{
					foreach ( var pair in component.Properties )
					{
						if ( string.IsNullOrWhiteSpace( pair.Key ) )
							errors.Add( $"Object {label} component '{component.TypeName}' has a property with no name." );
						if ( pair.Value is null )
							errors.Add( $"Object {label} component '{component.TypeName}' property '{pair.Key}' must not be null." );
					}
				}
			}

			if ( obj.Labels is not null )
			{
				foreach ( var spec in obj.Labels )
				{
					if ( spec is null ) { errors.Add( $"Object {label} label list must not contain a null entry." ); continue; }
					if ( spec.Text is null ) errors.Add( $"Object {label} label text must not be null." );
					if ( spec.MaxWidthChars < 2 ) errors.Add( $"Object {label} label max width must be at least 2 (room for the ellipsis)." );
					if ( spec.MaxLines < 1 ) errors.Add( $"Object {label} label max lines must be at least 1." );
					if ( !( spec.LineHeight > 0 ) || double.IsNaN( spec.LineHeight ) || double.IsInfinity( spec.LineHeight ) )
						errors.Add( $"Object {label} label line height must be a positive finite number." );
				}
			}
		}

		return errors;
	}

	/// <summary>
	/// Validate, strip every generator helper component, and return a deep-copied
	/// baked scene. Throws <see cref="InvalidOperationException"/> listing every
	/// validation error, and writes nothing, when the plan is not bakeable.
	/// </summary>
	public BakedScene Bake()
	{
		var errors = Validate();
		if ( errors.Count > 0 )
			throw new InvalidOperationException( "Scene bake plan is not bakeable: " + string.Join( "; ", errors ) );

		var baked = new BakedScene
		{
			ProjectName = ProjectName,
			SceneName = SceneName,
			TargetRelativePath = TargetRelativePath,
		};

		foreach ( var obj in Objects )
		{
			var components = new List<BakeComponentSpec>();
			foreach ( var component in obj.Components ?? new List<BakeComponentSpec>() )
			{
				if ( component.IsHelper ) continue;
				components.Add( new BakeComponentSpec
				{
					TypeName = component.TypeName,
					Enabled = component.Enabled,
					IsGeneratorHelper = false,
					Properties = new Dictionary<string, string>( component.Properties ),
				} );
			}

			var labels = new List<FormattedLabel>();
			foreach ( var spec in obj.Labels ?? new List<BakeLabelSpec>() )
			{
				var formatted = spec.Format();
				labels.Add( formatted );
				if ( formatted.Truncated )
					baked.Warnings.Add( $"Object '{obj.Name}' label was truncated to {spec.MaxLines} lines with an ellipsis." );
			}

			if ( obj.ModelPath is not null && ( obj.MaterialPaths is null || obj.MaterialPaths.Count == 0 ) )
				baked.Warnings.Add( $"Object '{obj.Name}' has a model but no .vmat materials — it will render with the model's default material." );

			baked.Objects.Add( new BakedObject
			{
				Name = obj.Name,
				Position = obj.Position,
				Rotation = obj.Rotation,
				Scale = obj.Scale,
				ModelPath = obj.ModelPath,
				MaterialPaths = new List<string>( obj.MaterialPaths ?? new List<string>() ),
				Components = components,
				FormattedLabels = labels,
			} );
		}

		return baked;
	}
}

/// <summary>One GameObject the bake will write: model, materials, components, labels.</summary>
public sealed class BakedObjectSpec
{
	public string Name { get; set; }
	public BakeVec3 Position { get; set; } = BakeVec3.Zero;
	public BakeQuat Rotation { get; set; } = BakeQuat.Identity;
	public BakeVec3 Scale { get; set; } = BakeVec3.One;

	/// <summary>Compiled model path, e.g. "models/effigy/gearhead.vmdl". Null = no renderer.</summary>
	public string ModelPath { get; set; }

	/// <summary>Material paths for the model, each a .vmat file. Validated, not scene-written (see plan header).</summary>
	public List<string> MaterialPaths { get; set; } = new List<string>();

	/// <summary>Colliders and game components. Generator helpers in here are stripped by Bake.</summary>
	public List<BakeComponentSpec> Components { get; set; } = new List<BakeComponentSpec>();

	/// <summary>UI / world labels attached to this object, under the strict label rules.</summary>
	public List<BakeLabelSpec> Labels { get; set; } = new List<BakeLabelSpec>();
}

/// <summary>A single component on a baked object: its engine type, its properties as strings, and whether it is a generator helper.</summary>
public sealed class BakeComponentSpec
{
	public string TypeName { get; set; }
	public bool Enabled { get; set; } = true;
	public bool IsGeneratorHelper { get; set; }

	/// <summary>Property values as written into the scene (numbers/bools as text; the shell renders them unquoted when they parse).</summary>
	public Dictionary<string, string> Properties { get; set; } = new Dictionary<string, string>();

	/// <summary>Helper when explicitly flagged, or when the type name itself says GeneratorHelper.</summary>
	public bool IsHelper => IsGeneratorHelper
		|| ( TypeName is not null && TypeName.IndexOf( "GeneratorHelper", StringComparison.Ordinal ) >= 0 );
}

/// <summary>
/// A label under the strict rules: word-wrap, ellipsis, auto-growing container.
/// <see cref="Format"/> never returns a line longer than <see cref="MaxWidthChars"/>
/// and never more than <see cref="MaxLines"/> lines; overflow is shown with "…",
/// never silently clipped.
/// </summary>
public sealed class BakeLabelSpec
{
	public string Text { get; set; } = "";
	public int MaxWidthChars { get; set; } = 40;
	public int MaxLines { get; set; } = 3;
	public double LineHeight { get; set; } = 20;

	public FormattedLabel Format()
	{
		var text = Text ?? "";
		var words = text.Split( new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries );
		var lines = new List<string>();
		var current = "";

		foreach ( var rawWord in words )
		{
			var word = rawWord;
			while ( word.Length > MaxWidthChars )
			{
				if ( current.Length > 0 ) { lines.Add( current ); current = ""; }
				lines.Add( word.Substring( 0, MaxWidthChars ) );
				word = word.Substring( MaxWidthChars );
			}
			if ( current.Length == 0 ) current = word;
			else if ( current.Length + 1 + word.Length <= MaxWidthChars ) current += " " + word;
			else { lines.Add( current ); current = word; }
		}
		if ( current.Length > 0 ) lines.Add( current );

		var truncated = false;
		if ( lines.Count > MaxLines )
		{
			truncated = true;
			lines = lines.Take( MaxLines ).ToList();
			var last = lines[lines.Count - 1];
			lines[lines.Count - 1] = last.Length >= MaxWidthChars
				? last.Substring( 0, MaxWidthChars - 1 ) + "…"
				: last + "…";
		}

		return new FormattedLabel
		{
			Lines = lines,
			Truncated = truncated,
			ContainerHeight = lines.Count * LineHeight,
		};
	}
}

/// <summary>A label after formatting: its wrapped lines, whether it was truncated, and the container height that fits it.</summary>
public sealed class FormattedLabel
{
	public List<string> Lines { get; set; } = new List<string>();
	public bool Truncated { get; set; }

	/// <summary>Auto-grown container height: one line-height per wrapped line, never a fixed box that clips.</summary>
	public double ContainerHeight { get; set; }
}

/// <summary>One object in a baked scene: the plan's object with helpers already removed.</summary>
public sealed class BakedObject
{
	public string Name { get; set; }
	public BakeVec3 Position { get; set; }
	public BakeQuat Rotation { get; set; }
	public BakeVec3 Scale { get; set; }
	public string ModelPath { get; set; }
	public List<string> MaterialPaths { get; set; } = new List<string>();
	public List<BakeComponentSpec> Components { get; set; } = new List<BakeComponentSpec>();
	public List<FormattedLabel> FormattedLabels { get; set; } = new List<FormattedLabel>();
}

/// <summary>The result of <see cref="SceneBakePlan.Bake"/>: what the engine shell writes, nothing else.</summary>
public sealed class BakedScene
{
	public string ProjectName { get; set; }
	public string SceneName { get; set; }
	public string TargetRelativePath { get; set; }
	public List<BakedObject> Objects { get; set; } = new List<BakedObject>();
	public List<string> Warnings { get; set; } = new List<string>();
}

public struct BakeVec3
{
	public double X;
	public double Y;
	public double Z;

	public BakeVec3( double x, double y, double z ) { X = x; Y = y; Z = z; }

	public static BakeVec3 Zero => new BakeVec3( 0, 0, 0 );
	public static BakeVec3 One => new BakeVec3( 1, 1, 1 );

	public bool IsFinite() => IsFinite( X ) && IsFinite( Y ) && IsFinite( Z );

	public static bool IsFinite( double value ) => !double.IsNaN( value ) && !double.IsInfinity( value );

	public string ToSceneString() => string.Format( CultureInfo.InvariantCulture, "{0},{1},{2}", X, Y, Z );
}

public struct BakeQuat
{
	public double X;
	public double Y;
	public double Z;
	public double W;

	public BakeQuat( double x, double y, double z, double w ) { X = x; Y = y; Z = z; W = w; }

	public static BakeQuat Identity => new BakeQuat( 0, 0, 0, 1 );

	public bool IsFinite() => BakeVec3.IsFinite( X ) && BakeVec3.IsFinite( Y ) && BakeVec3.IsFinite( Z ) && BakeVec3.IsFinite( W );
	public bool IsZero() => X == 0 && Y == 0 && Z == 0 && W == 0;

	public string ToSceneString() => string.Format( CultureInfo.InvariantCulture, "{0},{1},{2},{3}", X, Y, Z, W );
}
