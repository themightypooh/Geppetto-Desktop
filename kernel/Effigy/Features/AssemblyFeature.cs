using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Effigy;

/// <summary>
/// Assembly: the declarative replacement for the hardcoded CamheadAssembly and
/// SurveyPlayerAssembly desktop helpers.
///
///   add Assembly parts="hands.fbx; chest.fbx; head.obj" frame=blender_cm remap="M_CreamArmor=panel; M_DarkChassis=frame" skin=resolve
///
/// Each part is loaded (.fbx via FbxImport, .obj via ObjReader pieces, .effigy via
/// StudioCache), transformed from its authoring frame, material-remapped onto studio
/// slots, and skin-resolved against the studio rig — then merged into one body, or kept
/// as one body per piece when Merge is off. The old helpers stay exactly as they are
/// (they are live desktop paths, and the desktop kernel mirror does not carry this
/// feature until the Lane P sync); this feature is the generic path new assemblies
/// should use, and the one the old helpers should eventually call.
///
/// FRAMES. blender_cm is Camhead's citizen-Blender frame, P = (-y, x, z), applied AFTER
/// FbxImport's own axis/unit conversion, exactly as CamheadAssembly did. sbox_in is
/// identity (s&box inches in Effigy space). yup_m is Y-up metres rotated -90° about X
/// into Z-up and scaled to inches, assuming the source's +z faces forward. native skips
/// any frame transform (use it when a part is already authored in Effigy space).
///
/// MATERIALS. A remap entry "SourceName=SlotName" binds a source material name to the
/// studio slot carrying SlotName, creating that slot when it does not exist. Unmapped FBX
/// materials get a stable slot per name across parts; OBJ pieces carry no names, so
/// their material indices are offset per file instead. .effigy parts keep the slot
/// numbers their own studio used, remapped by name where the remap says so.
///
/// SKIN. resolve uses the file's own weights against ctx.Rig and falls back to rigid
/// binding; rigid binds every vertex to its nearest bone; none leaves weights alone.
/// A part with no rig in the studio is never given fake weights.
/// </summary>
public sealed class AssemblyFeature : Feature
{
	public override string TypeName => "Assembly";
	public override GeometryKind Accepts => GeometryKind.None;

	/// <summary>Part files, semicolon-separated. Relative paths resolve against BaseDir, else the working directory.</summary>
	public readonly StringParam Parts = new( "Parts", "" );

	/// <summary>blender_cm | sbox_in | yup_m | native.</summary>
	public readonly ChoiceParam Frame = new( "Frame", new[] { "blender_cm", "sbox_in", "yup_m", "native" }, 1 );

	/// <summary>Material remap: "SourceName=SlotName; ...".</summary>
	public readonly StringParam MaterialRemap = new( "Material remap", "" );

	/// <summary>resolve | rigid | none.</summary>
	public readonly ChoiceParam SkinMode = new( "Skin", new[] { "resolve", "rigid", "none" } );

	/// <summary>Merge every part into one body, or keep one body per piece.</summary>
	public readonly BoolParam Merge = new( "Merge", true );

	/// <summary>Directory relative part paths resolve against. Empty = working directory.</summary>
	public readonly StringParam BaseDir = new( "Base dir", "" );

	public override IReadOnlyList<IParam> Parameters => new IParam[] { Parts, Frame, MaterialRemap, SkinMode, Merge, BaseDir };
	public override IReadOnlyList<IParam> AdvancedParameters => new IParam[] { BaseDir };

	/// <summary>Frame transform for a frame name. Pure, for tests.</summary>
	internal static Xform FrameXform( string frame ) => frame.ToLowerInvariant() switch
	{
		// P = (-y, x, z): basis columns X=(0,1,0), Y=(-1,0,0), Z=(0,0,1). A pure rotation.
		"blender_cm" => new Xform( new Vec3( 0, 1, 0 ), new Vec3( -1, 0, 0 ), new Vec3( 0, 0, 1 ), Vec3.Zero ),
		// Y-up to Z-up, metres to inches: rotate -90° about X, then scale.
		"yup_m" => Xform.Scale( new Vec3( 39.3701f, 39.3701f, 39.3701f ) )
			* new Xform( new Vec3( 1, 0, 0 ), new Vec3( 0, 0, -1 ), new Vec3( 0, 1, 0 ), Vec3.Zero ),
		_ => Xform.Identity,
	};

	/// <summary>Parse "a=b; c=d" remap text into pairs, or fail with a reason.</summary>
	internal static List<(string From, string To)> ParseRemap( string text, out string why )
	{
		why = null;
		var pairs = new List<(string, string)>();
		foreach ( var piece in (text ?? "").Split( ';' ) )
		{
			var t = piece.Trim();
			if ( t.Length == 0 ) continue;
			var eq = t.IndexOf( '=' );
			if ( eq <= 0 || eq == t.Length - 1 ) { why = $"'{t}' is not Source=Slot."; return null; }
			pairs.Add( (t[..eq].Trim(), t[(eq + 1)..].Trim()) );
		}
		return pairs;
	}

	protected override void Execute( FeatureContext ctx )
	{
		var partPaths = Parts.Value.Split( ';' ).Select( s => s.Trim() ).Where( s => s.Length > 0 ).ToList();
		if ( partPaths.Count == 0 )
			FailOn( "Parts", "No parts listed.", "Parts lists the files to assemble, separated by ';'.", "Add at least one .fbx, .obj or .effigy path" );
		var remap = ParseRemap( MaterialRemap.Value, out var why );
		if ( remap is null )
			FailOn( "Material remap", why, "The remap is SourceName=SlotName pairs separated by ';'.", "Fix the entry, or clear the remap" );

		var baseDir = string.IsNullOrWhiteSpace( BaseDir.Value ) ? Directory.GetCurrentDirectory() : BaseDir.Value;
		var frame = FrameXform( Frame.Value );
		// Studio slot names live on the PartStudio, which a feature cannot see from its
		// context, so remap targets are resolved to slots this feature allocates, keyed by
		// name and stable across parts. To hit an existing studio slot by number, pass the
		// number as the remap target instead of a name.
		var namedSlots = new Dictionary<string, int>( StringComparer.OrdinalIgnoreCase );
		foreach ( var (from, to) in remap )
			if ( int.TryParse( to, out var num ) ) namedSlots[from] = num;

		int NextSlot()
		{
			var used = new HashSet<int>( namedSlots.Values );
			foreach ( var b in ctx.Bodies ) foreach ( var f in b.Mesh?.Faces ?? Enumerable.Empty<Face>() ) used.Add( f.Material );
			var s = 0;
			while ( used.Contains( s ) ) s++;
			return s;
		}
		int SlotForName( string name )
		{
			if ( namedSlots.TryGetValue( name, out var s ) ) return s;
			s = NextSlot();
			namedSlots[name] = s;
			return s;
		}

		var pieces = new List<(string Name, PolyMesh Mesh)>();
		var notes = new List<string>();
		foreach ( var raw in partPaths )
		{
			var path = Path.IsPathRooted( raw ) ? raw : Path.Combine( baseDir, raw );
			if ( !File.Exists( path ) )
				FailOn( "Parts", $"Part file '{raw}' does not exist.", "Parts are read from disk at rebuild.", $"Put the file at {path}, or fix the path" );
			var ext = Path.GetExtension( path ).ToLowerInvariant();
			var stem = Path.GetFileNameWithoutExtension( path );
			if ( ext == ".fbx" )
			{
				var imported = FbxImport.Read( File.ReadAllBytes( path ) );
				for ( var pi = 0; pi < imported.Pieces.Count; pi++ )
				{
					var piece = imported.Pieces[pi];
					var mesh = piece.Mesh.Clone();
					if ( Frame.Value.ToLowerInvariant() != "sbox_in" && Frame.Value.ToLowerInvariant() != "native" )
						MeshTransform.Apply( mesh, frame );
					var remapSlot = new Dictionary<int, int>();
					foreach ( var (src, srcName) in imported.MaterialSlots )
						remapSlot[src] = SlotForName( remap.FirstOrDefault( r => string.Equals( r.From, srcName, StringComparison.OrdinalIgnoreCase ) ).To ?? srcName );
					foreach ( var f in mesh.Faces )
						if ( remapSlot.TryGetValue( f.Material, out var dst ) ) f.Material = dst;
					if ( ctx.Rig is not null && SkinMode.Value != "none" )
					{
						var weights = pi < imported.Weights.Count ? imported.Weights[pi] : null;
						mesh.Skin = weights is not null && SkinMode.Value == "resolve"
							? FbxImport.Resolve( weights, ctx.Rig, null, out _ ) ?? SkinBinder.BindRigid( mesh, ctx.Rig )
							: SkinMode.Value == "rigid" ? SkinBinder.BindRigid( mesh, ctx.Rig ) : mesh.Skin;
					}
					pieces.Add( ($"{stem}_{piece.Name}", mesh) );
				}
				notes.Add( $"{stem}: {imported.Pieces.Count} FBX piece(s)" );
			}
			else if ( ext == ".obj" )
			{
				var objs = ObjReader.ReadPieces( File.ReadAllText( path ) );
				var offset = NextSlot();
				foreach ( var obj in objs )
				{
					var mesh = obj.Mesh.Clone();
					if ( Frame.Value.ToLowerInvariant() != "sbox_in" && Frame.Value.ToLowerInvariant() != "native" )
						MeshTransform.Apply( mesh, frame );
					foreach ( var f in mesh.Faces ) f.Material += offset;
					if ( ctx.Rig is not null && SkinMode.Value == "rigid" )
						mesh.Skin = SkinBinder.BindRigid( mesh, ctx.Rig );
					pieces.Add( ($"{stem}_{(string.IsNullOrWhiteSpace( obj.Name ) ? "mesh" : obj.Name)}", mesh) );
				}
				notes.Add( $"{stem}: {objs.Count} OBJ piece(s)" );
			}
			else if ( ext == ".effigy" )
			{
				var sub = StudioCache.Load( path );
				sub.Rebuild();
				foreach ( var b in sub.Bodies.Where( b => !b.IsReference && b.Mesh is not null && b.Mesh.FaceCount > 0 ) )
				{
					var mesh = b.Mesh.Clone();
					if ( Frame.Value.ToLowerInvariant() != "sbox_in" && Frame.Value.ToLowerInvariant() != "native" )
						MeshTransform.Apply( mesh, frame );
					if ( ctx.Rig is not null && SkinMode.Value == "rigid" && !mesh.IsRigged )
						mesh.Skin = SkinBinder.BindRigid( mesh, ctx.Rig );
					pieces.Add( ($"{stem}_{b.Name}", mesh) );
				}
				notes.Add( $"{stem}: {sub.Bodies.Count} .effigy body/bodies" );
			}
			else
				FailOn( "Parts", $"Part '{raw}' has extension '{ext}', not .fbx, .obj or .effigy.", "Assembly loads those three formats.", "Convert the part, or drop it from Parts" );
		}

		if ( pieces.Count == 0 )
			Fail( "No pieces came out of the parts.", "Every part file was empty or had no meshes.", "Check the part files open on their own" );

		if ( Merge.Value )
		{
			var merged = new PolyMesh();
			foreach ( var (_, mesh) in pieces ) MeshTransform.Append( merged, mesh );
			ctx.Bodies.Add( new Body( ctx.NewBodyId(), Name, merged ) );
		}
		else
		{
			foreach ( var (name, mesh) in pieces )
				ctx.Bodies.Add( new Body( ctx.NewBodyId(), name, mesh ) );
		}
		Warning = string.Join( "; ", notes ) + (Merge.Value ? " merged into one body." : " kept as separate bodies.");
	}
}
