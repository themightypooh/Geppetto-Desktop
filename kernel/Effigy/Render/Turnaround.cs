using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Effigy.Render;

/// <summary>What a turnaround renders and how.</summary>
public sealed class TurnaroundOptions
{
	/// <summary>True: plain clay, the form only. False: what the materials make it look like — Flesh
	/// Material maps or vertex materials, painted vertex colours, else clay.</summary>
	public bool Clay = true;

	/// <summary>Each view's square size in pixels.</summary>
	public int Size = 512;

	/// <summary>Degrees the camera looks down from level.</summary>
	public float Elevation = 12f;

	/// <summary>Shading samples per output pixel along each side. 1 is enough: edges come from
	/// <see cref="Coverage"/>. Raise it for strands thinner than a pixel, or for a smoother occlusion.</summary>
	public int Supersample = 1;
	/// <summary>Antialiasing samples per pixel (<see cref="ShadeScene.Coverage"/>): 16 is clean, 64 for a final.</summary>
	public int Coverage = 16;

	/// <summary>Only this body, by id. Null renders every visible body.</summary>
	public string FocusBodyId;

	/// <summary>A material preset for bodies with no Flesh Material, in material mode. Null leaves them clay.</summary>
	public FleshStack Fallback;

    /// <summary>Directory map paths in slot looks resolve against (the script/document
    /// directory). Null resolves against the working directory.</summary>
    public string BaseDir;

	/// <summary>Printed on each tile before the view's name. Null prints the view alone.</summary>
	public string Label;

	/// <summary>Draw only what is on the kept side of this plane, to see inside. Null draws everything.</summary>
	public ClipPlane? Clip;

	/// <summary>A flat backdrop colour (linear RGB) with no floor, shadow or reflection under the model, to key
	/// the renders out into sprites. Null keeps the studio gradient and floor.</summary>
	public Vec3? Backdrop;

	/// <summary>The views, by name: front, quarter, side, back (the default four), left, top.</summary>
	public IReadOnlyList<string> Views = Turnaround.DefaultViews;

	/// <summary>Call <see cref="ShadeScene.Portfolio"/> (studio IBL, AgX Punchy, denoised AO,
	/// contact shadows, bloom, vignette, floor reflection). On by default: it is the finished
	/// look, and the old legacy look is still available with portfolio=0.</summary>
	public bool Portfolio = true;

	/// <summary>How the lights move: "camera" (key turns with each view, flattering, comparable),
	/// "fixed" (one world-space studio set, honest back views), "game" (world sun + sky ambient,
	/// an approximation of s&box, labelled as one — not Source 2).</summary>
	public string Lights = "camera";

	/// <summary>Environment: studio, outdoor, neutral, zebra, none (legacy sky gradient), or a
	/// .hdr / .png lat-long file path (resolved against BaseDir). Null leaves it to Portfolio.</summary>
	public string Env;

	/// <summary>Turn the environment about z, degrees. Already a field on the environment itself.</summary>
	public float EnvYaw;

	/// <summary>Exposure multiplier. 1 is the default; a dark material is a lighting problem, not
	/// a reason to brighten the material — change this instead.</summary>
	public float Exposure = 1f;

	/// <summary>Tonemap override: legacy, aces, agx, agxpunchy. Null leaves Portfolio's choice.</summary>
	public string Tonemap;

	/// <summary>Render clay and material in one output (two rows, same cameras, same framing).</summary>
	public bool Both;

	/// <summary>Diagnostic pass: shaded (default), normal (world normal as colour), ao
	/// (occlusion as grey), wireframe (black edge lines over the shaded form).</summary>
	public string Pass = "shaded";

	/// <summary>Closeup framing: the centre of the detail to fill the frame. Null frames the whole model.</summary>
	public Vec3? DetailCentre;

	/// <summary>Radius of the detail region, model units. Only used with DetailCentre.</summary>
	public float DetailRadius;

	/// <summary>Backlit silhouette: black form on white, one light behind the model per view.</summary>
	public bool Silhouette;

	/// <summary>Append a reference tile (chrome sphere, roughness ramp, mid-grey sphere) under the
	/// same environment, so metal/roughness judgements are relative, not absolute.</summary>
	public bool Reference;

	public TurnaroundOptions Copy() => new()
	{
		Clay = Clay, Size = Size, Elevation = Elevation, Supersample = Supersample, Coverage = Coverage,
		FocusBodyId = FocusBodyId, Fallback = Fallback, BaseDir = BaseDir, Label = Label, Clip = Clip,
		Backdrop = Backdrop, Views = Views, Portfolio = Portfolio, Lights = Lights, Env = Env,
		EnvYaw = EnvYaw, Exposure = Exposure, Tonemap = Tonemap, Both = Both, Pass = Pass,
		DetailCentre = DetailCentre, DetailRadius = DetailRadius, Silhouette = Silhouette, Reference = Reference,
	};
}

/// <summary>A rendered turnaround: each view, the labelled sheet, and what was drawn how.</summary>
public sealed class TurnaroundResult
{
	public bool Ok;
	public string Error;
	public readonly List<(string View, Raster Image)> Views = new();
	public Raster Sheet;
	public readonly List<string> Log = new();
	public readonly List<string> Written = new();
}

/// <summary>
/// Render Turnaround: the model lit and shaded from the front, a front quarter, the side and the
/// back, saved as one labelled sheet plus a picture per view. The editor's File ▸ Render
/// Turnaround and the build script's `turnaround` both call this; the script's
/// `render … shade=` builds its scene here too, so the three cannot drift apart.
///
/// The cameras follow the `shade` command's: the model's front is +X, the quarter view stands
/// front-right (+X, −Y) and the side view looks from −Y. Framing is ShadedRender.Sheet's — the
/// whole model's bounding sphere fills each tile, the floor under its lowest point, and the studio
/// three-light set turned with each camera so the key always comes from its front-right.
/// </summary>
public static class Turnaround
{
	public static readonly IReadOnlyList<string> DefaultViews = new[] { "front", "quarter", "side", "back" };

	/// <summary>Camera yaw round the model, degrees, 0 looking at the front from +X.</summary>
	public static bool TryYaw( string view, out float yaw, out float elevation )
	{
		elevation = float.NaN;
		yaw = 0f;

		switch ( view?.Trim().ToLowerInvariant() )
		{
			case "front": yaw = 0f; return true;
			case "quarter": yaw = -45f; return true;
			case "side": case "right": yaw = -90f; return true;
			case "back": yaw = 180f; return true;
			case "left": yaw = 90f; return true;
			case "top": yaw = 0f; elevation = 89f; return true;
			default: return false;
		}
	}

	/// <summary>
	/// The model as a shade scene: each visible body (or the focused one) in clay, or in what its
	/// materials make it look like. <paramref name="log"/> gets one line per body saying which.
	/// Meshes are the studio's own, not copies: render before the next rebuild.
	/// </summary>
	public static ShadeScene Scene( PartStudio studio, bool clay, string focusBodyId, FleshStack fallback, List<string> log, string baseDir = null )
	{
		var scene = new ShadeScene();

		if ( studio is null )
			return scene;

		var flesh = studio.Features.OfType<FleshMaterialFeature>().Where( x => !x.Suppressed ).ToList();
		var clayBodies = new List<string>();
		var lookBodies = new Dictionary<string, List<string>>();
		var anyGuessed = false;

		foreach ( var body in studio.Bodies )
		{
			if ( body.Mesh is null || body.Mesh.Faces.Count == 0 || !body.Visible ) continue;
			if ( focusBodyId is not null && body.Id != focusBodyId ) continue;
			var name = body.Name ?? body.Id;

			if ( clay ) { scene.Add( body.Mesh, ShadeMaterial.Clay() ); continue; }

			var owner = flesh.LastOrDefault( x => x.Maps.ContainsKey( body.Id ) || x.VertexMaterials.ContainsKey( body.Id ) );
			if ( owner is not null && owner.VertexMaterials.TryGetValue( body.Id, out var vm ) )
			{
				var coloured = body.Mesh.Clone();
				coloured.VertexColors = vm.Colour;
				scene.Add( coloured, FleshMaterial.Shade( owner.Stack, vertex: vm ) );
				log?.Add( $"{name}: {owner.PresetName} (per vertex)" );
			}
			else if ( owner is not null && owner.Maps.TryGetValue( body.Id, out var maps ) )
			{
				scene.Add( body.Mesh, FleshMaterial.Shade( owner.Stack, maps ) );
				log?.Add( $"{name}: {owner.PresetName} ({maps.Size}² maps)" );
			}
			else if ( body.Mesh.Paint is { } paint )
			{
				// Painted layers: colour, roughness, metallic and the normal from painted height,
				// exactly the images `paintmaps` and the editor's compile write.
				scene.Add( body.Mesh, PaintTextureSet.Shade( paint ) );
				log?.Add( $"{name}: its paint ({paint.Width}² maps)" );
			}
			else if ( SlotLooks( studio, body.Mesh, guess: fallback is null, out var guessed ) is { Count: > 0 } looks )
			{
				// Hard-surface parts: each face shaded by its slot's look, on the whole mesh's normals.
				// A mesh carrying per-vertex emission keeps it alongside the slot looks: the look
				// gives the face its colour/glow, the vertex channel varies the glow per vertex.
				var byFace = looks.ToDictionary( kv => kv.Key, kv => kv.Value.Shade( baseDir, msg => log?.Add( msg ) ) );
				// Wave-5 hair shading: the hair_aniso preset drives the kernel's fibre
				// model (ShadeMaterial.HairAniso); every other preset leaves it off, so
				// only hair slots shade anisotropically. No SlotLook.cs change: the
				// preset name is the switch, and SlotLook.cs is the SlotLook32 agent's file.
				foreach ( var kv in looks )
					if ( string.Equals( kv.Value.Preset, "hair_aniso", StringComparison.OrdinalIgnoreCase ) )
						byFace[kv.Key].HairAniso = 1f;
				if ( body.Mesh.HasVertexEmission )
					foreach ( var sm in byFace.Values ) sm.VertexEmission = body.Mesh.VertexEmission;
				scene.Add( body.Mesh, byFace, ShadeMaterial.Clay() );
				var said = string.Join( ", ", looks.OrderBy( kv => kv.Key ).Select( kv => $"{kv.Key}={kv.Value.Preset ?? "custom"}" ) );
				if ( !lookBodies.TryGetValue( said, out var names ) ) lookBodies[said] = names = new List<string>();
				names.Add( name );
				anyGuessed |= guessed;
			}
			else if ( fallback is not null )
			{
				var v = FleshMaterial.BakeVertices( body.Mesh, fallback );
				var coloured = body.Mesh.Clone();
				coloured.VertexColors = v.Colour;
				scene.Add( coloured, FleshMaterial.Shade( fallback, vertex: v ) );
				log?.Add( $"{name}: {fallback.Name} (preset, per vertex)" );
			}
			else if ( body.Mesh.HasVertexColors || body.Mesh.HasVertexEmission )
			{
				// Vertex emission without a slot look still glows: the ShadeMaterial carries
				// the mesh's own channel (sRGB, like VertexColors), added as light per vertex.
				scene.Add( body.Mesh, new ShadeMaterial { Roughness = 0.35f, Coat = 0.3f,
					VertexEmission = body.Mesh.HasVertexEmission ? body.Mesh.VertexEmission : null } );
				log?.Add( $"{name}: its vertex colours" );
			}
			else
			{
				scene.Add( body.Mesh, ShadeMaterial.Clay() );
				clayBodies.Add( name );
			}
		}

		// One line for every clay body, not one each: a hard-surface model is mostly bodies with
		// neither flesh nor a look, and forty identical lines buried the ones that mattered.
		// Same for slot looks: one line per set of looks, naming the bodies that wear it.
		foreach ( var (said, names) in lookBodies )
			log?.Add( $"slot looks ({said}): {Names( names )}" );

		if ( anyGuessed )
			log?.Add( "some slot looks were guessed from the slot's name; set them with `material SLOT preset=…`" );

		if ( clayBodies.Count > 0 )
			log?.Add( $"clay: {Names( clayBodies )} " +
				$"(no slot look, Flesh Material or preset=; give one with `material SLOT preset=…` — {SlotLook.PresetList})" );

		return scene;
	}

	static string Names( List<string> names ) =>
		names.Count <= 6 ? string.Join( ", ", names ) : $"{string.Join( ", ", names.Take( 5 ) )} and {names.Count - 5} more";

	/// <summary>The looks of the slots a mesh's faces use: set ones from the studio, else guessed from
	/// the slot's name when <paramref name="guess"/> (not when a preset= was asked for). Slots with neither are left out and render clay.</summary>
	public static Dictionary<int, SlotLook> SlotLooks( PartStudio studio, PolyMesh mesh, bool guess, out bool guessed )
	{
		guessed = false;
		var o = new Dictionary<int, SlotLook>();

		foreach ( var slot in mesh.Faces.Select( f => f.Material ).Distinct() )
		{
			if ( studio.MaterialLooks.TryGetValue( slot, out var look ) && look is not null )
				o[slot] = look;
			else if ( guess && SlotLook.Guess( studio.NameForSlot( slot ) ) is { } g )
			{
				o[slot] = g;
				guessed = true;
			}
		}

		return o;
	}

	/// <summary>Render the views and lay them out as one labelled sheet. Writes nothing.</summary>
	public static TurnaroundResult Render( PartStudio studio, TurnaroundOptions options = null )
	{
		options ??= new TurnaroundOptions();
		var result = new TurnaroundResult();

		var views = (options.Views ?? DefaultViews).Where( v => !string.IsNullOrWhiteSpace( v ) ).ToList();
		var unknown = views.Where( v => !TryYaw( v, out _, out _ ) ).ToList();

		if ( views.Count == 0 || unknown.Count > 0 )
			return Fail( result, views.Count == 0
				? "No views asked for. Use front, quarter, side, back, left or top."
				: $"No view called '{unknown[0]}'. Use front, quarter, side, back, left or top." );

		if ( options.Size < 16 || options.Size > 4096 )
			return Fail( result, $"A view {options.Size} pixels wide can't be rendered: use a size from 16 to 4096." );

		if ( studio is null )
			return Fail( result, "No model is open to render." );

		var focus = options.FocusBodyId is null ? null : studio.Bodies.FirstOrDefault( b => b.Id == options.FocusBodyId );

		if ( options.FocusBodyId is not null && focus is null )
			return Fail( result, $"There is no body '{options.FocusBodyId}' to render. Pick a body that is in the model, or render the whole model." );

		var scene = Scene( studio, options.Clay, options.FocusBodyId, options.Fallback, result.Log, options.BaseDir );
		scene.Clip = options.Clip;
		scene.Coverage = options.Coverage;
		if ( options.Backdrop is { } flat )
		{
			scene.Floor = false;
			scene.BackgroundBottom = scene.BackgroundTop = flat;
		}

		var lighting = ApplyLighting( scene, options, out var lightError );
		if ( lightError is not null )
			return Fail( result, lightError );
		result.Log.Add( lighting );

		if ( options.Silhouette )
		{
			// Black form, white backdrop, no floor, no environment: the outline is the judgement.
			scene.FaceMaterials.Clear();
			var black = new ShadeMaterial { Albedo = Vec3.Zero, Roughness = 1f, Metallic = 0f, UseVertexColours = false };
			for ( var i = 0; i < scene.Items.Count; i++ ) scene.Items[i] = (scene.Items[i].Mesh, black);
			scene.Environment = null;
			scene.Floor = false;
			scene.BackgroundBottom = scene.BackgroundTop = Vec3.One;
			scene.Tonemap = ShadeTonemap.Legacy;
		}

		if ( scene.Items.Count == 0 )
			return Fail( result, focus is null
				? "Nothing to render: no visible body has faces. Add a body, or show the hidden ones."
				: $"{focus.Name ?? focus.Id} has no faces to render." );

		// Framing: ShadedRender.Sheet's, so a turnaround and a script `render shade=` sheet match.
		var bmin = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
		var bmax = new Vec3( float.MinValue, float.MinValue, float.MinValue );
		foreach ( var (mesh, _) in scene.Items )
			foreach ( var p in mesh.Positions )
			{
				bmin = new Vec3( MathF.Min( bmin.x, p.x ), MathF.Min( bmin.y, p.y ), MathF.Min( bmin.z, p.z ) );
				bmax = new Vec3( MathF.Max( bmax.x, p.x ), MathF.Max( bmax.y, p.y ), MathF.Max( bmax.z, p.z ) );
			}

		var centre = (bmin + bmax) * 0.5f;
		var radius = MathF.Max( 1e-3f, (bmax - bmin).Length * 0.5f );
		const float fov = 30f;
		var dist = radius / MathF.Sin( fov * MathF.PI / 360f ) * 1.08f;
		scene.FloorHeight = bmin.z;
		scene.AoRadius = radius * 0.08f;

		if ( options.DetailCentre is { } dc && options.DetailRadius > 0f )
		{
			centre = dc;
			radius = options.DetailRadius;
			dist = radius / MathF.Sin( fov * MathF.PI / 360f ) * 1.08f;
			scene.AoRadius = radius * 0.08f;
		}

		foreach ( var view in views )
		{
			TryYaw( view, out var yaw, out var up );
			var y = yaw * MathF.PI / 180f;
			var e = (float.IsNaN( up ) ? options.Elevation : up) * MathF.PI / 180f;
			var dir = new Vec3( MathF.Cos( y ) * MathF.Cos( e ), MathF.Sin( y ) * MathF.Cos( e ), MathF.Sin( e ) );
			var camera = new ShadeCamera( centre + dir * dist, centre, fov );

			if ( options.Silhouette )
			{
				// One light behind the model for this view: the form reads as a black shape with a rim.
				scene.Lights.Clear();
				scene.Lights.Add( new ShadeLight( -dir, 3f, 0.05f ) );
			}
			else if ( string.Equals( options.Lights, "camera", StringComparison.OrdinalIgnoreCase ) )
			{
				scene.Lights.Clear();
				scene.StudioLights( -dir );
			}
			// "fixed" and "game" lights were set once by ApplyLighting and are left alone per view.

			result.Views.Add( (view.Trim().ToLowerInvariant(), ShadedRender.Render( scene, camera, options.Size, options.Size, options.Supersample )) );
		}

		if ( options.Reference )
		{
			var refView = RenderReference( options );
			if ( refView is not null ) result.Views.Add( ("reference", refView) );
		}

		scene.Lights.Clear();
		var sheetLabel = options.Label;
		if ( !string.Equals( options.Pass, "shaded", StringComparison.OrdinalIgnoreCase ) )
			sheetLabel = string.IsNullOrEmpty( sheetLabel ) ? options.Pass : $"{sheetLabel} {options.Pass}";
		if ( options.Silhouette )
			sheetLabel = string.IsNullOrEmpty( sheetLabel ) ? "silhouette" : $"{sheetLabel} silhouette";
		result.Sheet = Sheet( result.Views, sheetLabel );
		result.Ok = true;
		result.Log.Add( $"rendered {(options.Clay ? "clay" : "materials")}, {result.Views.Count} views at {options.Size}²" );
		return result;
	}

	/// <summary>
	/// Render and save: <c>name_turnaround.png</c> (the sheet) and <c>name_&lt;view&gt;.png</c> per view,
	/// in <paramref name="folder"/>, which is made if missing. A folder that can't be written says so.
	/// </summary>
	public static TurnaroundResult Write( PartStudio studio, string folder, string name, TurnaroundOptions options = null )
	{
		if ( string.IsNullOrWhiteSpace( folder ) )
			return Fail( new TurnaroundResult(), "No folder to save the turnaround in. Choose one." );

		name = SafeName( name );
		var result = Render( studio, options );

		if ( !result.Ok )
			return result;

		try
		{
			Directory.CreateDirectory( folder );

			var sheet = Path.Combine( folder, $"{name}_turnaround.png" );
			result.Sheet.SavePng( sheet );
			result.Written.Add( sheet );

			foreach ( var (view, image) in result.Views )
			{
				var path = Path.Combine( folder, $"{name}_{view}.png" );
				image.SavePng( path );
				result.Written.Add( path );
			}
		}
		catch ( Exception e ) when ( e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException )
		{
			result.Ok = false;
			result.Error = $"The turnaround rendered but couldn't be saved in {folder} ({e.Message}). Choose a folder you can write to.";
			return result;
		}

		result.Log.Add( $"saved {result.Written[0]}" );
		return result;
	}


	/// <summary>Configure a scene's lighting from the options: environment, Portfolio, tonemap,
	/// exposure, diagnostic pass, and the fixed/game light sets. Returns a one-line summary for
	/// the render log, so a picture can be traced back to the conditions that made it.
	/// <paramref name="error"/> is set (and the scene left unconfigured) when an option is invalid.</summary>
	internal static string ApplyLighting( ShadeScene scene, TurnaroundOptions options, out string error, bool forSheet = false )
	{
		error = null;
		var lights = (options.Lights ?? "camera").Trim().ToLowerInvariant();
		if ( lights is not ("camera" or "fixed" or "game") )
		{
			error = $"lights={options.Lights}: use lights=camera (key turns with each view), lights=fixed (one world set), or lights=game (sun + sky, an approximation of s&box, not Source 2).";
			return null;
		}
		var pass = (options.Pass ?? "shaded").Trim().ToLowerInvariant();
		if ( pass is not ("shaded" or "normal" or "ao" or "wireframe") )
		{
			error = $"pass={options.Pass}: use pass=shaded, pass=normal, pass=ao, or pass=wireframe. (Curvature is available through `bake`, not as a render pass.)";
			return null;
		}
		ShadeTonemap? tonemap = (options.Tonemap ?? "").Trim().ToLowerInvariant() switch
		{
			"" => null,
			"legacy" => ShadeTonemap.Legacy,
			"aces" => ShadeTonemap.Aces,
			"agx" => ShadeTonemap.AgX,
			"agxpunchy" => ShadeTonemap.AgXPunchy,
			_ => null,
		};
		if ( options.Tonemap is not null && tonemap is null )
		{
			error = $"tonemap={options.Tonemap}: use tonemap=legacy, tonemap=aces, tonemap=agx, or tonemap=agxpunchy.";
			return null;
		}

		var envName = (options.Env ?? "").Trim();
		ShadeEnvironment env = null;
		var envLabel = "studio (portfolio)";
		if ( envName.Length > 0 )
		{
			switch ( envName.ToLowerInvariant() )
			{
				case "studio": env = ShadeEnvironment.Studio(); envLabel = "studio"; break;
				case "outdoor": env = ShadeEnvironment.Outdoor(); envLabel = "outdoor"; break;
				case "neutral": env = ShadeEnvironment.Neutral(); envLabel = "neutral"; break;
				case "zebra": env = ShadeEnvironment.Zebra(); envLabel = "zebra"; break;
				case "none": envLabel = "none (legacy sky gradient)"; break;
				default:
				{
					var path = envName;
					if ( !Path.IsPathRooted( path ) && options.BaseDir is not null ) path = Path.Combine( options.BaseDir, path );
					if ( !ShadeEnvironment.TryLoadImage( path, out env, out var why ) ) { error = why; return null; }
					envLabel = $"file {Path.GetFileName( path )}" + (path.EndsWith( ".png", StringComparison.OrdinalIgnoreCase ) ? " (LDR, not true HDR)" : "");
					break;
				}
			}
		}

		if ( options.Portfolio )
		{
			if ( env is not null ) scene.Environment = env;
			scene.Portfolio();
		}
		else if ( env is not null ) scene.Environment = env;
		if ( env is not null )
		{
			env.Yaw = options.EnvYaw;
			env.CameraRelative = lights == "camera";
		}
		if ( tonemap is { } tm ) scene.Tonemap = tm;
		if ( options.Exposure > 0f ) scene.Exposure = options.Exposure;

		if ( lights == "fixed" )
		{
			scene.Lights.Clear();
			scene.StudioLights( new Vec3( -1f, 0f, 0f ) );
		}
		else if ( lights == "game" )
		{
			// Approximation only, labelled as one wherever it is logged: one warm sun with shadows,
			// a sky/ground hemisphere ambient, and a dim ground bounce, all in world space.
			scene.Lights.Clear();
			scene.Lights.Add( new ShadeLight( new Vec3( 0.45f, -0.35f, 1f ), 2.4f, 0.05f, new Vec3( 1f, 0.95f, 0.85f ) ) );
			scene.Lights.Add( new ShadeLight( new Vec3( 0.2f, 0.2f, -0.5f ), 0.35f, 0.4f, new Vec3( 0.8f, 0.85f, 0.95f ) ) { Shadows = false } );
			scene.SkyColour = new Vec3( 0.55f, 0.62f, 0.75f );
			scene.GroundColour = new Vec3( 0.28f, 0.24f, 0.2f );
		}

		if ( pass != "shaded" )
		{
			scene.DebugPass = pass;
			if ( pass is "normal" or "ao" ) { scene.Bloom = 0f; scene.Vignette = 0f; scene.FloorReflection = 0f; }
		}

		var summary = $"lighting: env={envLabel}, tonemap={scene.Tonemap}, lights={lights}, exposure={scene.Exposure}, portfolio={(options.Portfolio ? "on" : "off")}";
		if ( lights == "game" ) summary += " — game approximation, not Source 2";
		if ( pass != "shaded" ) summary += $", pass={pass} (requested; renderer still returns the shaded image)";
		return summary;
	}

	/// <summary>Two sheets stacked vertically with a row label each: clay on top, material below.</summary>
	internal static Raster StackSheets( Raster top, Raster bottom, string topLabel, string bottomLabel )
	{
		var w = Math.Max( top.Width, bottom.Width );
		var h = top.Height + bottom.Height + 8;
		var sheet = new Raster( w, h, Raster.Rgb( 0x20, 0x20, 0x20 ) );
		for ( var row = 0; row < top.Height; row++ )
			Array.Copy( top.Pixels, row * top.Width, sheet.Pixels, row * sheet.Width, top.Width );
		for ( var row = 0; row < bottom.Height; row++ )
			Array.Copy( bottom.Pixels, row * bottom.Width, sheet.Pixels, (top.Height + 8 + row) * sheet.Width, bottom.Width );
		sheet.Rect( 0, 0, Raster.TextWidth( topLabel ) + 12, 22, Raster.Rgb( 0, 0, 0 ), 0.6f );
		sheet.Text( 6, 4, topLabel, Raster.Rgb( 255, 255, 255 ) );
		sheet.Rect( 0, top.Height + 8, Raster.TextWidth( bottomLabel ) + 12, 22, Raster.Rgb( 0, 0, 0 ), 0.6f );
		sheet.Text( 6, top.Height + 12, bottomLabel, Raster.Rgb( 255, 255, 255 ) );
		return sheet;
	}

	/// <summary>Clay + material turnaround in one output: one stacked sheet plus per-view files
	/// suffixed _clay / _material. Framing is computed from the model both times, so rows align.</summary>
	public static TurnaroundResult WriteBoth( PartStudio studio, string folder, string name, TurnaroundOptions options )
	{
		if ( string.IsNullOrWhiteSpace( folder ) )
			return Fail( new TurnaroundResult(), "No folder to save the turnaround in. Choose one." );
		name = SafeName( name );
		var clayOpts = options.Copy(); clayOpts.Clay = true; clayOpts.Both = false;
		var matOpts = options.Copy(); matOpts.Clay = false; matOpts.Both = false;
		var clay = Render( studio, clayOpts );
		if ( !clay.Ok ) return clay;
		var mat = Render( studio, matOpts );
		if ( !mat.Ok ) return mat;
		var result = new TurnaroundResult();
		result.Log.AddRange( clay.Log );
		result.Log.AddRange( mat.Log );
		foreach ( var (view, image) in clay.Views ) result.Views.Add( (view + "_clay", image) );
		foreach ( var (view, image) in mat.Views ) result.Views.Add( (view + "_material", image) );
		result.Sheet = StackSheets( clay.Sheet, mat.Sheet, "clay", "material" );
		try
		{
			Directory.CreateDirectory( folder );
			var sheetPath = Path.Combine( folder, $"{name}_turnaround.png" );
			result.Sheet.SavePng( sheetPath );
			result.Written.Add( sheetPath );
			foreach ( var (view, image) in result.Views )
			{
				var path = Path.Combine( folder, $"{name}_{view}.png" );
				image.SavePng( path );
				result.Written.Add( path );
			}
		}
		catch ( Exception e ) when ( e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException )
		{
			result.Ok = false;
			result.Error = $"The turnaround rendered but couldn't be saved in {folder} ({e.Message}). Choose a folder you can write to.";
			return result;
		}
		result.Log.Add( $"saved {result.Written[0]} (clay + material)" );
		result.Ok = true;
		return result;
	}

	/// <summary>One calibration tile under the same environment: a chrome sphere, five spheres
	/// stepping roughness 0 to 1, and a mid-grey sphere. Same environment as the model views;
	/// camera-relative lights for the tile, so it reads the same way in every sheet.</summary>
	static Raster RenderReference( TurnaroundOptions options )
	{
		var scene = new ShadeScene();
		var defs = new (float X, ShadeMaterial Mat)[]
		{
			(-4.5f, new ShadeMaterial { Albedo = new Vec3( 0.95f, 0.95f, 0.95f ), Roughness = 0.05f, Metallic = 1f, UseVertexColours = false }),
			(-3f, new ShadeMaterial { Albedo = new Vec3( 0.5f, 0.5f, 0.5f ), Roughness = 0f, Metallic = 0f, UseVertexColours = false }),
			(-1.5f, new ShadeMaterial { Albedo = new Vec3( 0.5f, 0.5f, 0.5f ), Roughness = 0.25f, Metallic = 0f, UseVertexColours = false }),
			(0f, new ShadeMaterial { Albedo = new Vec3( 0.5f, 0.5f, 0.5f ), Roughness = 0.5f, Metallic = 0f, UseVertexColours = false }),
			(1.5f, new ShadeMaterial { Albedo = new Vec3( 0.5f, 0.5f, 0.5f ), Roughness = 0.75f, Metallic = 0f, UseVertexColours = false }),
			(3f, new ShadeMaterial { Albedo = new Vec3( 0.5f, 0.5f, 0.5f ), Roughness = 1f, Metallic = 0f, UseVertexColours = false }),
			(4.5f, new ShadeMaterial { Albedo = new Vec3( 0.18f, 0.18f, 0.18f ), Roughness = 0.5f, Metallic = 0f, UseVertexColours = false }),
		};
		foreach ( var (x, mat) in defs )
		{
			var mesh = Primitives.QuadSphere( 0.5f, 4 );
			for ( var i = 0; i < mesh.Positions.Count; i++ ) mesh.Positions[i] += new Vec3( x, 0f, 0.5f );
			scene.Add( mesh, mat );
		}
		ApplyLighting( scene, options, out _ );
		var bmin = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
		var bmax = new Vec3( float.MinValue, float.MinValue, float.MinValue );
		foreach ( var (mesh, _) in scene.Items )
			foreach ( var p in mesh.Positions )
			{
				bmin = new Vec3( MathF.Min( bmin.x, p.x ), MathF.Min( bmin.y, p.y ), MathF.Min( bmin.z, p.z ) );
				bmax = new Vec3( MathF.Max( bmax.x, p.x ), MathF.Max( bmax.y, p.y ), MathF.Max( bmax.z, p.z ) );
			}
		var centre = (bmin + bmax) * 0.5f;
		var radius = MathF.Max( 1e-3f, (bmax - bmin).Length * 0.5f );
		const float fov = 30f;
		var dist = radius / MathF.Sin( fov * MathF.PI / 360f ) * 1.08f;
		scene.FloorHeight = bmin.z;
		scene.AoRadius = radius * 0.08f;
		scene.Coverage = options.Coverage;
		var e = options.Elevation * MathF.PI / 180f;
		var dir = new Vec3( MathF.Cos( e ), 0f, MathF.Sin( e ) );
		var camera = new ShadeCamera( centre + dir * dist, centre, fov );
		if ( string.Equals( options.Lights, "camera", StringComparison.OrdinalIgnoreCase ) )
		{
			scene.Lights.Clear();
			scene.StudioLights( -dir );
		}
		return ShadedRender.Render( scene, camera, options.Size, options.Size, options.Supersample );
	}

	/// <summary>The views in one row, each labelled in its corner, on the dark sheet colour.</summary>
	public static Raster Sheet( IReadOnlyList<(string View, Raster Image)> views, string label = null, int gap = 4 )
	{
		var w = views.Max( t => t.Image.Width );
		var h = views.Max( t => t.Image.Height );
		var sheet = new Raster( views.Count * (w + gap) + gap, h + gap * 2, Raster.Rgb( 0x20, 0x20, 0x20 ) );

		for ( var i = 0; i < views.Count; i++ )
		{
			var (view, img) = views[i];
			var ox = gap + i * (w + gap);
			for ( var row = 0; row < img.Height; row++ )
				Array.Copy( img.Pixels, row * img.Width, sheet.Pixels, (gap + row) * sheet.Width + ox, img.Width );

			var text = string.IsNullOrEmpty( label ) ? view : $"{label} - {view}";
			sheet.Rect( ox, gap, Raster.TextWidth( text ) + 12, 22, Raster.Rgb( 0, 0, 0 ), 0.6f );
			sheet.Text( ox + 6, gap + 4, text, Raster.Rgb( 255, 255, 255 ) );
		}

		return sheet;
	}

	/// <summary>A file-name-safe version of a model name; "model" when there is nothing left.</summary>
	public static string SafeName( string name )
	{
		var invalid = Path.GetInvalidFileNameChars();
		var safe = new string( (name ?? "").Trim().Select( c => invalid.Contains( c ) || c == ' ' ? '_' : c ).ToArray() );
		return string.IsNullOrEmpty( safe ) ? "model" : safe;
	}

	static TurnaroundResult Fail( TurnaroundResult result, string why )
	{
		result.Ok = false;
		result.Error = why;
		return result;
	}
}
