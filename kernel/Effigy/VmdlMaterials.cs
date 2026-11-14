using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Text;

namespace Effigy;

/// <summary>
/// The MaterialGroupList node a .vmdl needs so faces keep the materials they were given.
///
/// WHAT THIS CLOSES. Faces carry a slot number and PartStudio.MaterialNames binds that number to a
/// vmat. Every exporter writes the name — OBJ as <c>usemtl</c>, DMX as <c>mtlName</c>, SMD as the
/// triangle's material line — and that was as far as it went. The .vmdl Compile writes had no
/// MaterialGroupList at all. ModelDoc fills a missing one in with
/// <c>use_global_default = true</c> and <c>materials/default.vmat</c>, which is why a part that
/// rendered in brushed steel in the viewport compiled to a blank grey prop.
///
/// COPIED, NOT GUESSED. The remap shape is the one this project's own lightswitch and first-person
/// arms models already ship:
///
/// <code>
/// from = "sw_plate.vmat"
/// to   = "materials/lightswitch/sw_plate.vmat"
/// </code>
///
/// <c>from</c> is the name the mesh file carries. <c>to</c> is the asset the compiler should bind.
/// <c>use_global_default</c> stays false: true is the switch that replaces every slot with default,
/// which is the failure this exists to stop.
///
/// WHY SEVERAL <c>from</c> SPELLINGS. The mesh writers emit whatever NameForSlot returns, which for
/// a bound slot is the full vmat path. ModelDoc's importer is documented as dropping everything
/// after a period in a material name (the Blender <c>.001</c> rule), and an OBJ <c>usemtl</c> with
/// slashes is sometimes taken as the last segment only. The lightswitch files remap from the
/// filename. Emitting the path, the filename, and both with <c>.vmat</c> stripped means whichever
/// spelling the importer keeps still hits a remap.
///
/// WHY THE KERNEL AND NOT THE EDITOR. Same reason as VmdlPhysics: it is text, it has no engine
/// types, and a headless test can say whether the node names the slots the mesh actually uses.
/// </summary>
public static class VmdlMaterials
{
	/// <summary>
	/// What an unbound slot compiles to.
	///
	/// EVERY SLOT MUST NAME A REAL ASSET. Leaving an unbound slot alone meant the compiled model
	/// carried the mesh's own name for it - <c>material_0</c> - which resolves to no asset at all,
	/// so the model rendered in the bright red missing-material shader. That is the first thing
	/// anybody saw after their first export, and it reads as the exporter having failed.
	///
	/// IT DOES NOT SHOW PAINT, and an earlier version of this comment claimed it did. The claim
	/// was that <c>complex.vfx</c>'s <c>g_flModelTintAmount 1.0</c> would multiply a vertex colour
	/// in. It does not: model tint is a per-draw constant and the tint MASK is a texture, and
	/// neither is a per-vertex COLOR stream. That was true of the vertex-colour paint this once
	/// carried and it is why painting appeared to do nothing at all.
	///
	/// PAINT NO LONGER ARRIVES HERE. It is a texture: <see cref="PaintMaterial"/> writes the canvas
	/// out as a PNG wrapped in a .vmat, and that .vmat is bound to the painted body's slot through
	/// MaterialNames like any dropped material. A bound slot never takes a fallback, so there is no
	/// longer such a thing as a painted slot arriving unbound and needing a material that reads
	/// paint.
	/// </summary>
	public const string DefaultMaterial = "materials/default.vmat";

	/// <summary>
	/// What an unbound slot compiles to when a paint layer asks to cover rather than tint.
	///
	/// White albedo, so the vertex-colour multiply lands on 1.0 and the result IS the paint colour.
	/// That is the whole trick: covering needs no shader, only a surface with nothing of its own to
	/// show through. Verified white rather than assumed - its generated colour texture is the same
	/// asset <c>bright_100.vmat</c> uses.
	/// </summary>
	public const string ReplaceMaterial = "materials/default/white.vmat";

	/// <summary>
	/// Which fallback an unbound slot in this studio should take.
	///
	/// THE MESH GETS NO VOTE, and it used to. A mesh carrying vertex colours took a third answer -
	/// vertex_color.vmat, the one material that read the COLOR stream - because paint was per-vertex
	/// colour and every ordinary material threw it away. Paint is a texture now, bound to its body's
	/// slot as an ordinary .vmat, so a painted slot reaches export BOUND and a fallback is only ever
	/// asked about slots nobody has given a material to. There is nothing left for the mesh to say.
	///
	/// Whole-document by design: see PaintFeature.Blend. A suppressed or failed paint layer does
	/// not get a vote, the same rule the rest of the exporters apply to a feature that produced no
	/// geometry.
	/// </summary>
	public static string FallbackFor( PartStudio studio )
	{
		// Paint is a bound texture now. Blend used to pick white.vmat so vertex colour would show
		// on an unbound slot; that path is gone, and consulting Blend here made an unbound slot
		// compile white whenever a paint layer existed anywhere in the tree — including one that
		// had never been brushed. Unbound is default.vmat. A painted body reaches export bound.
		_ = studio;
		return DefaultMaterial;
	}

	/// <summary>
	/// The MaterialGroupList node, indented to sit among a RootNode's children.
	///
	/// ALWAYS A NODE, even when nothing is bound. An omitted list is what ModelDoc replaces with
	/// the global default, and every slot the mesh uses is named here explicitly - bound ones to
	/// their vmat, the rest to whichever fallback <see cref="FallbackFor"/> picked - so nothing is
	/// left to resolve on a name no asset answers to.
	/// </summary>
	public static string GroupList( PartStudio studio, PolyMesh mesh )
	{
		if ( studio is null )
			return GroupList( mesh, null, null );

		return GroupList( mesh, studio.NameForSlot, studio.MaterialBindings, FallbackFor( studio ) );
	}

	/// <summary>
	/// The same node, from the two facts export already has: what the mesh writers will call each
	/// slot, and which slots have a vmat bound.
	/// </summary>
	public static string GroupList( PolyMesh mesh, Func<int, string> nameForSlot,
		IReadOnlyDictionary<int, string> materialNames, string fallback = DefaultMaterial )
	{
		var remaps = Remaps( mesh, nameForSlot, materialNames, fallback );
		var sb = new StringBuilder();

		sb.Append( "\t\t\t{\n" );
		sb.Append( "\t\t\t\t_class = \"MaterialGroupList\"\n" );
		sb.Append( "\t\t\t\tchildren = \n" );
		sb.Append( "\t\t\t\t[\n" );
		sb.Append( "\t\t\t\t\t{\n" );
		sb.Append( "\t\t\t\t\t\t_class = \"DefaultMaterialGroup\"\n" );
		sb.Append( "\t\t\t\t\t\tremaps = \n" );
		sb.Append( "\t\t\t\t\t\t[\n" );

		foreach ( var (from, to) in remaps )
		{
			sb.Append( "\t\t\t\t\t\t\t{\n" );
			sb.Append( $"\t\t\t\t\t\t\t\tfrom = {Quote( from )}\n" );
			sb.Append( $"\t\t\t\t\t\t\t\tto = {Quote( to )}\n" );
			sb.Append( "\t\t\t\t\t\t\t},\n" );
		}

		sb.Append( "\t\t\t\t\t\t]\n" );
		sb.Append( "\t\t\t\t\t\tuse_global_default = false\n" );
		sb.Append( "\t\t\t\t\t\tglobal_default_material = \"\"\n" );
		sb.Append( "\t\t\t\t\t},\n" );
		sb.Append( "\t\t\t\t]\n" );
		sb.Append( "\t\t\t},\n" );

		return sb.ToString();
	}

	/// <summary>
	/// Every <c>from → to</c> pair the node will write, in the order they appear.
	///
	/// Public so a test can count remaps without scraping KV3, and so the editor can log how many
	/// slots actually went out.
	/// </summary>
	public static List<(string From, string To)> Remaps( PolyMesh mesh, Func<int, string> nameForSlot,
		IReadOnlyDictionary<int, string> materialNames, string fallback = DefaultMaterial )
	{
		var remaps = new List<(string, string)>();
		var seenFrom = new HashSet<string>( StringComparer.OrdinalIgnoreCase );

		foreach ( var slot in SlotsOn( mesh ) )
		{
			var written = nameForSlot is not null ? nameForSlot( slot ) : ObjWriter.DefaultMaterialName( slot );

			// AN UNBOUND SLOT IS STILL A REMAP, to the default. Skipping it left the compiled model
			// asking for `material_0`, which is not an asset - see DefaultMaterial for why that is
			// the red model, and why paint needs this to show at all. A slot carrying a hand-typed
			// display name ("anodised") lands here too: there is no asset by that name either, and
			// the default is a better answer than the missing-material shader.
			if ( !TryBoundVmat( slot, materialNames, out var target ) )
			{
				if ( seenFrom.Add( written ) )
					remaps.Add( (written, string.IsNullOrWhiteSpace( fallback ) ? DefaultMaterial : VmdlDocument.NormalizeAssetPath( fallback )) );

				continue;
			}

			// Wave 3: .vmdl material paths are normalized (lowercase, forward slashes),
			// the follow-up VmdlDocument's own comment asked for; already-clean paths
			// are unchanged.
			target = VmdlDocument.NormalizeAssetPath( target );

			foreach ( var from in FromAliases( written, target ) )
			{
				if ( !seenFrom.Add( from ) )
					continue;

				remaps.Add( (from, target) );
			}
		}

		return remaps;
	}

	/// <summary>One generated sidecar .vmat for a slot nothing else bound: the file to write
	/// beside the model, the asset path the .vmdl remap points at, and the diffuse colour
	/// the OBJ's .mtl entry uses for the same slot.</summary>
	public sealed class Sidecar
	{
		public int Slot;
		public string AssetPath = "";
		public string VmatText = "";
		public Vec3 Colour = Vec3.One;
	}

	/// <summary>Persist used slot looks even when no .vmat path was entered. Bind these only
	/// in the exported model, not in the document, so exporting cannot change the preview.</summary>
	public static List<Sidecar> LookSidecarsFor( PolyMesh mesh, PartStudio studio, string folder, string modelName )
	{
		var sidecars = new List<Sidecar>();
		if ( studio is null ) return sidecars;
		var bindings = studio.MaterialBindings;
		foreach ( var slot in SlotsOn( mesh ) )
		{
			if ( !studio.MaterialLooks.TryGetValue( slot, out var look ) || look is null
				|| TryBoundVmat( slot, bindings, out _ ) ) continue;
			sidecars.Add( new Sidecar
			{
				Slot = slot,
				AssetPath = $"{folder}/materials/{modelName}_look_slot{slot}.vmat",
				VmatText = look.Vmat(),
				Colour = look.Color,
			} );
		}
		return sidecars;
	}

	/// <summary>Sidecar .vmats for a mesh's unbound detail slots (Wear rust/algae, garment
	/// worn/dirt, hard finishes). A slot is only a number until a material definition exists
	/// for it: without this, GroupList remaps every such slot to materials/default.vmat and
	/// the rust on a buoy compiles as blank grey. Bound slots are never overridden — a name
	/// somebody set wins over anything generated here.
	///
	/// COLOURS ARE CHOSEN, NOT MEASURED. No rust/algae colour exists anywhere in the
	/// codebase (WearFeature registers the slots with default grey metal values), so rust
	/// is a reddish brown, algae a dull green, worn/dirt derive from the source slot's look
	/// through the existing Worn/Dirty helpers, and hard finishes use their own values.
	/// Glow and hole kinds get a plain HardVmat from their colour: glow textures and hole
	/// masks are not exported, and a plain material beats default.vmat for those slots too.</summary>
	public static List<Sidecar> SidecarsFor( PolyMesh mesh, IReadOnlyList<DetailMaterial> details,
		Func<int, SlotLook> lookForSlot, IReadOnlyDictionary<int, string> existingBindings,
		string folder, string modelName )
	{
		var sidecars = new List<Sidecar>();
		if ( mesh is null || details is null )
			return sidecars;
		var used = SlotsOn( mesh );
		var seen = new HashSet<int>();
		foreach ( var d in details )
		{
			if ( d is null || !used.Contains( d.Slot ) || !seen.Add( d.Slot ) )
				continue;
			if ( existingBindings is not null && existingBindings.TryGetValue( d.Slot, out var bound ) && !string.IsNullOrWhiteSpace( bound ) )
				continue;
			var colour = d.Colour;
			var metal = d.Metalness;
			var rough = d.Roughness;
			switch ( d.Kind )
			{
				case "rust":
					colour = new Vec3( 0.48f, 0.23f, 0.10f ); metal = 0.1f; rough = 0.9f;
					break;
				case "algae":
					colour = new Vec3( 0.23f, 0.42f, 0.20f ); metal = 0f; rough = 0.95f;
					break;
				case "worn":
				case "dirt":
				{
					var source = d.SourceSlot >= 0 ? lookForSlot?.Invoke( d.SourceSlot ) : null;
					var baseColour = source?.Color ?? d.Colour;
					colour = d.Kind == "worn" ? DetailMaterials.Worn( baseColour ) : DetailMaterials.Dirty( baseColour );
					metal = 0f; rough = d.Kind == "worn" ? 0.9f : 0.95f;
					break;
				}
			}
			var m = new DetailMaterial { Kind = d.Kind, Slot = d.Slot, Key = d.Key, Colour = colour, Metalness = metal, Roughness = rough };
			var file = $"{modelName}_slot{d.Slot}.vmat";
			sidecars.Add( new Sidecar
			{
				Slot = d.Slot,
				AssetPath = $"{folder}/materials/{file}",
				VmatText = DetailMaterials.HardVmat( m ),
				Colour = colour,
			} );
		}
		return sidecars;
	}

	/// <summary>Slots the mesh actually paints. A name sitting on a slot no face wears does not
	/// reach the compiled model, so it is not a remap.</summary>
	static SortedSet<int> SlotsOn( PolyMesh mesh )
	{
		var slots = new SortedSet<int>();

		if ( mesh?.Faces is null )
			return slots;

		foreach ( var face in mesh.Faces )
			slots.Add( face.Material );

		return slots;
	}

	/// <summary>
	/// The vmat a slot should compile to, or nothing.
	///
	/// A hand-typed display name — "anodised", "brushed steel" — is what the mesh writers already
	/// emit, and there is no asset to point <c>to</c> at. Only a path that looks like a material
	/// asset is remappable.
	/// </summary>
	static bool TryBoundVmat( int slot, IReadOnlyDictionary<int, string> materialNames, out string path )
	{
		path = null;

		if ( materialNames is null || !materialNames.TryGetValue( slot, out var name )
			|| string.IsNullOrWhiteSpace( name ) )
			return false;

		var n = name.Trim().Replace( '\\', '/' );

		if ( !n.EndsWith( ".vmat", StringComparison.OrdinalIgnoreCase )
			&& !n.StartsWith( "materials/", StringComparison.OrdinalIgnoreCase ) )
			return false;

		path = n;
		return true;
	}

	/// <summary>
	/// Every spelling of <paramref name="written"/> the importer might keep, plus the filename of
	/// <paramref name="target"/> — see the class comment for why there is more than one.
	/// </summary>
	static List<string> FromAliases( string written, string target )
	{
		var names = new List<string>();
		var seen = new HashSet<string>( StringComparer.OrdinalIgnoreCase );

		void Add( string value )
		{
			if ( string.IsNullOrWhiteSpace( value ) )
				return;

			var n = value.Trim().Replace( '\\', '/' );

			if ( seen.Add( n ) )
				names.Add( n );
		}

		Add( written );
		Add( target );
		Add( FileName( target ) );
		Add( StripVmat( FileName( target ) ) );
		Add( StripVmat( target ) );

		return names;
	}

	static string FileName( string path )
	{
		if ( string.IsNullOrEmpty( path ) )
			return path;

		var n = path.Replace( '\\', '/' );
		var cut = n.LastIndexOf( '/' );

		return cut >= 0 && cut < n.Length - 1 ? n[(cut + 1)..] : n;
	}

	static string StripVmat( string name )
	{
		if ( string.IsNullOrEmpty( name ) )
			return name;

		return name.EndsWith( ".vmat", StringComparison.OrdinalIgnoreCase )
			? name[..^5]
			: name;
	}

	/// <summary>
	/// Lane C: the .vmat parameter lines a <see cref="SlotLook"/> adds to its complex.vfx material.
	/// Called by <see cref="SlotLook.Vmat"/> so the mapping lives here, beside the other .vmdl/.vmat
	/// knowledge, instead of being duplicated in the look. complex.vfx supports diffuse wrap,
	/// not the invented F_SUBSURFACE_SCATTERING / F_CLEARCOAT switches. Wrap is an approximation
	/// of scattering; true subsurface and clearcoat remain features of Effigy's own renderer.
	/// Empty when the look uses none of them.
	/// </summary>
	public static string LookParameters( SlotLook look )
	{
		if ( look is null ) return "";
		var sb = new StringBuilder();
		if ( look.Subsurface > 0f )
		{
			sb.Append( "\tF_DIFFUSE_WRAP 1\n" );
			sb.Append( $"\tg_flDiffuseWrap \"{F( Math.Clamp( look.Subsurface, 0f, 1f ) )}\"\n" );
			sb.Append( $"\tg_vDiffuseWrapColor \"[{F6( look.SubsurfaceColor.x )} {F6( look.SubsurfaceColor.y )} {F6( look.SubsurfaceColor.z )} 0.000000]\"\n" );
		}
		if ( look.Opacity < 1f )
		{
			sb.Append( "\tF_TRANSLUCENT 1\n" );
			sb.Append( $"\tg_flOpacityScale \"{F( Math.Clamp( look.Opacity, 0f, 1f ) )}\"\n" );
		}
		return sb.ToString();
	}

	/// <summary>
	/// Lane C: bake the stylized look into a colour sample so it survives in s&amp;box's standard
	/// shader when `bake ... vmat=1` runs. <paramref name="cavity"/> is normalised: -1 on a ridge,
	/// +1 in a crease (scale <see cref="SurfaceDetail.Cavity"/> inches into that range first);
	/// <paramref name="ao"/> is 0 (open) to 1 (shut in). Ridges get brighter (+0.18 per unit of
	/// stylized strength, the roadmap's number), creases shift warm and darken with AO, and
	/// roughness broadens in creases and tightens on ridges. When only Toon is on (no Stylized),
	/// the bake still runs at 0.5 strength: cel bands themselves are lighting and cannot be baked
	/// into a colour map, but the ridge/cavity tint is what keeps the hand-painted read.
	/// </summary>
	public static Vec3 BakeColour( Vec3 colour, float cavity, float ao, SlotLook look )
	{
		if ( look is null ) return colour;
		var strength = look.Stylized > 0f ? look.Stylized : look.Toon > 0f ? 0.5f : 0f;
		if ( strength <= 0f ) return colour;
		var ridge = Math.Clamp( -cavity, 0f, 1f );
		var crease = Math.Clamp( cavity, 0f, 1f );
		var c = colour * (1f + 0.18f * strength * ridge);
		c = Vec3.Lerp( c, new Vec3( c.x, c.y * 0.85f, c.z * 0.65f ), strength * crease * 0.5f );
		c *= 1f - 0.25f * strength * Math.Clamp( ao, 0f, 1f ) * crease;
		return c;
	}

	/// <summary>The roughness half of <see cref="BakeColour"/>: same strength rule, same inputs.</summary>
	public static float BakeRoughness( float roughness, float cavity, float ao, SlotLook look )
	{
		if ( look is null ) return roughness;
		var strength = look.Stylized > 0f ? look.Stylized : look.Toon > 0f ? 0.5f : 0f;
		if ( strength <= 0f ) return roughness;
		var ridge = Math.Clamp( -cavity, 0f, 1f );
		var crease = Math.Clamp( cavity, 0f, 1f );
		_ = ao;
		return Math.Clamp( roughness + 0.15f * strength * crease - 0.10f * strength * ridge, 0f, 1f );
	}

	/// <summary>Array form of <see cref="BakeColour"/> for a whole _color.png worth of samples.</summary>
	public static Vec3[] BakeColours( Vec3[] colours, float[] cavity, float[] ao, SlotLook look )
	{
		if ( colours is null ) return null;
		if ( cavity is null || ao is null || cavity.Length != colours.Length || ao.Length != colours.Length )
			throw new ArgumentException( "BakeColours needs one cavity and one AO value per colour." );
		var outp = new Vec3[colours.Length];
		for ( var i = 0; i < outp.Length; i++ ) outp[i] = BakeColour( colours[i], cavity[i], ao[i], look );
		return outp;
	}

	/// <summary>Array form of <see cref="BakeRoughness"/> for a whole _rough.png worth of samples.</summary>
	public static float[] BakeRoughnesses( float[] roughnesses, float[] cavity, float[] ao, SlotLook look )
	{
		if ( roughnesses is null ) return null;
		if ( cavity is null || ao is null || cavity.Length != roughnesses.Length || ao.Length != roughnesses.Length )
			throw new ArgumentException( "BakeRoughnesses needs one cavity and one AO value per roughness." );
		var outp = new float[roughnesses.Length];
		for ( var i = 0; i < outp.Length; i++ ) outp[i] = BakeRoughness( roughnesses[i], cavity[i], ao[i], look );
		return outp;
	}

	static string F( float v ) => v.ToString( "0.###", CultureInfo.InvariantCulture );
	static string F6( float v ) => v.ToString( "0.000000", CultureInfo.InvariantCulture );

	static string Quote( string value )
	{
		if ( value is null )
			return "\"\"";

		return "\"" + value.Replace( "\\", "\\\\" ).Replace( "\"", "\\\"" ) + "\"";
	}
}
