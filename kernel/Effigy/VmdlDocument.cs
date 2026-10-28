using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Effigy;

/// <summary>
/// The .vmdl ModelDoc reads, built in one place.
///
/// WHY THIS IS A FILE AND NOT A STRING LITERAL IN THE EXPORTER. There were four copies of this
/// template — EffigyWindow.BuildVmdl, EffigyWindow.BuildSkinnedVmdl, TreeGen and TentacleGen —
/// and only the first one carried the import_rotation correction below. The other three said
/// [0, 0, 0], which is right for DMX and catastrophically wrong for OBJ, and nothing about a
/// copied literal tells you which case you are in. Anything outside the editor window that wanted
/// a model — the remesh harness, a generator — had no way to ask for a correct one, so it
/// hand-made a vmdl in ModelDoc instead and got the rotation wrong the same way.
///
/// THE ROTATION IS DERIVED FROM THE MESH FILE, not passed in, because that is the actual rule:
/// it is the OBJ importer specifically that turns the mesh, so the extension decides. See
/// <see cref="ImportRotation"/>. A caller cannot get it wrong by forgetting to pass it.
/// </summary>
public static class VmdlDocument
{
	const string Header =
		"<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} "
		+ "format:modeldoc29:version{3cec427c-1b0e-4d48-a90a-0436f33a6041} -->\n";

	/// <summary>
	/// What to put in import_rotation for a mesh file, as the three numbers of the kv3 array.
	///
	/// ModelDoc's OBJ importer does not land the mesh in the coordinates the file gives it. It
	/// reads the file as Y-up (the OBJ convention) and then turns it another quarter turn, so the
	/// whole thing arrives cyclically permuted:
	///
	///     engine.x = obj.z    engine.y = obj.x    engine.z = obj.y
	///
	/// The kernel is Z-up — its sketch planes are named "Top (XY)", "Front (XZ)", "Right (YZ)" —
	/// so this is TWO errors stacked. A bare -90 yaw undoes the extra turn and leaves the Y-up
	/// reading in place, landing the mesh at (obj.x, -obj.z, obj.y): a part drawn lying flat comes
	/// out standing on its side. [-90, -90, 0] is the full inverse of the permutation above and
	/// puts the mesh back in the coordinates the file was written in.
	///
	/// MEASURED. A two-box part whose OBJ bounds are 155 x 159 x 84 compiled to 84 x 155 x 159 at
	/// rotation zero — the permutation, read straight off the numbers — and to 155 x 159 x 84 at
	/// [-90, -90, 0], with the bar still pointing along +x and the raised lip still on top, so this
	/// is the identity and not some other transform that happens to share its bounds.
	///
	/// This matters most for collision. Physics shapes are written in kernel coordinates and
	/// import_rotation does not touch them, so the mesh has to arrive in kernel coordinates too,
	/// or the collision sits at an angle to the model it belongs to.
	///
	/// DMX AND SMD MUST NOT GET THIS. Only the OBJ importer turns the mesh; the DMX importer lands
	/// it where the file says. Applying the correction there would break the rigged path, which is
	/// why this is decided per file rather than set once.
	/// </summary>
	public static string ImportRotation( string meshFilename ) =>
		meshFilename is not null
		&& meshFilename.EndsWith( ".obj", StringComparison.OrdinalIgnoreCase )
			? "-90.0, -90.0, 0.0"
			: "0.0, 0.0, 0.0";

	/// <summary>
	/// Backlog B25: every asset path this writer emits is lowercase, slash-separated, and free
	/// of doubled slashes or a leading "./". Linux sbox-dev case-folds its content mounts, so a
	/// path written with a capital letter or a backslash simply never resolves there — the model
	/// loads with a missing mesh and nothing says why. A leading "/" is left alone: an absolute
	/// path that loses its slash points somewhere else entirely, which is worse than a miss.
	/// Applied to render meshes, animation graphs, and every new list node below; VmdlMaterials
	/// is NOT in this lane's file list, so its paths are still unwrapped — a follow-up for
	/// whoever owns that file.
	/// </summary>
	public static string NormalizeAssetPath( string path )
	{
		if ( string.IsNullOrEmpty( path ) )
			return path;
		var normalized = path.Replace( '\\', '/' ).ToLowerInvariant();
		while ( normalized.Contains( "//" ) )
			normalized = normalized.Replace( "//", "/" );
		while ( normalized.StartsWith( "./", StringComparison.Ordinal ) )
			normalized = normalized.Substring( 2 );
		return normalized;
	}

	static string Num( float value )
	{
		if ( !float.IsFinite( value ) )
			value = 0f;
		var text = value.ToString( "0.0######", System.Globalization.CultureInfo.InvariantCulture );
		return text == "-0.0" ? "0.0" : text;
	}

	static string Vec( Vec3 v ) => $"[ {Num( v.x )}, {Num( v.y )}, {Num( v.z ) } ]";

	/// <summary>The RenderMeshList node — one RenderMeshFile pointing at the mesh on disk.</summary>
	static string RenderMeshList( string meshFilename ) => RenderMeshList( new[] { meshFilename } );

	/// <summary>
	/// One RenderMeshFile node: the same text the LOD list has always written, factored out so
	/// body-group meshes (H2.3) reuse it instead of a second copy drifting out of step. The
	/// filename is normalized (see <see cref="NormalizeAssetPath"/>); the name is a node name,
	/// not a path, and is written as given.
	/// </summary>
	static string RenderMeshFile( string name, string filename ) =>
		"\t\t\t\t\t{\n"
		+ "\t\t\t\t\t\t_class = \"RenderMeshFile\"\n"
		+ $"\t\t\t\t\t\tname = \"{name}\"\n"
		+ "\t\t\t\t\t\tchildren = \n"
		+ "\t\t\t\t\t\t[\n"
		+ "\t\t\t\t\t\t]\n"
		+ $"\t\t\t\t\t\tfilename = \"{NormalizeAssetPath( filename )}\"\n"
		+ "\t\t\t\t\t\timport_translation = [ 0.0, 0.0, 0.0 ]\n"
		+ $"\t\t\t\t\t\timport_rotation = [ {ImportRotation( filename )} ]\n"
		+ "\t\t\t\t\t\timport_scale = 1.0\n"
		+ "\t\t\t\t\t\talign_origin_x_type = \"None\"\n"
		+ "\t\t\t\t\t\talign_origin_y_type = \"None\"\n"
		+ "\t\t\t\t\t\talign_origin_z_type = \"None\"\n"
		+ "\t\t\t\t\t\tparent_bone = \"\"\n"
		+ "\t\t\t\t\t},\n";

	/// <summary>The RenderMeshList node with one RenderMeshFile per level of detail, named
	/// Body_LOD0, Body_LOD1 and so on in the order given.</summary>
	static string RenderMeshList( IReadOnlyList<string> meshFilenames )
	{
		var sb = new StringBuilder();
		sb.Append( "\t\t\t{\n" );
		sb.Append( "\t\t\t\t_class = \"RenderMeshList\"\n" );
		sb.Append( "\t\t\t\tchildren = \n" );
		sb.Append( "\t\t\t\t[\n" );

		for ( var i = 0; i < meshFilenames.Count; i++ )
			sb.Append( RenderMeshFile( $"Body_LOD{i}", meshFilenames[i] ) );

		sb.Append( "\t\t\t\t]\n" );
		sb.Append( "\t\t\t},\n" );
		return sb.ToString();
	}

	/// <summary>
	/// The LODGroupList node: which mesh shows at which distance. The first group is always at
	/// zero (the full model, up close); each further one names the distance, in units, past
	/// which its mesh takes over. One group per mesh, in order.
	/// </summary>
	static string LodGroupList( IReadOnlyList<float> switchDistances )
	{
		var sb = new StringBuilder();
		sb.Append( "\t\t\t{\n" );
		sb.Append( "\t\t\t\t_class = \"LODGroupList\"\n" );
		sb.Append( "\t\t\t\tchildren = \n" );
		sb.Append( "\t\t\t\t[\n" );

		for ( var i = 0; i < switchDistances.Count; i++ )
		{
			var threshold = i == 0 ? 0f : switchDistances[i];
			sb.Append( "\t\t\t\t\t{\n" );
			sb.Append( "\t\t\t\t\t\t_class = \"LODGroup\"\n" );
			sb.Append( $"\t\t\t\t\t\tswitch_threshold = {threshold.ToString( "0.0##", System.Globalization.CultureInfo.InvariantCulture )}\n" );
			sb.Append( "\t\t\t\t\t\tmeshes = \n" );
			sb.Append( "\t\t\t\t\t\t[\n" );
			sb.Append( $"\t\t\t\t\t\t\t\"Body_LOD{i}\",\n" );
			sb.Append( "\t\t\t\t\t\t]\n" );
			sb.Append( "\t\t\t\t\t},\n" );
		}

		sb.Append( "\t\t\t\t]\n" );
		sb.Append( "\t\t\t},\n" );
		return sb.ToString();
	}

	/// <summary>
	/// A static model with levels of detail: one mesh file per level, the first shown up close and
	/// each next one from its switch distance out. Same physics and materials as <see cref="Static"/>.
	/// </summary>
	public static string StaticWithLods( IReadOnlyList<string> meshFilenames, IReadOnlyList<float> switchDistances, string physics = "", string materials = "" )
	{
		if ( meshFilenames is null || meshFilenames.Count == 0 )
			throw new ArgumentException( "at least one mesh", nameof( meshFilenames ) );
		if ( switchDistances is null || switchDistances.Count != meshFilenames.Count )
			throw new ArgumentException( "one switch distance per mesh", nameof( switchDistances ) );

		return Wrap( materials + RenderMeshList( meshFilenames ) + LodGroupList( switchDistances ) + physics );
	}

	/// <summary>
	/// A skinned model with levels of detail: one skinned mesh file per level (each carrying the
	/// same skeleton, see <see cref="LodExport"/>), the LODGroupList that switches them, and the
	/// bone markup, animations and physics of <see cref="Skinned"/> once for the whole model.
	/// </summary>
	public static string SkinnedWithLods( IReadOnlyList<string> meshFilenames, IReadOnlyList<float> switchDistances,
		Skeleton skeleton, string physics = "", string materials = "", string animations = null, string animGraph = "",
		string hitboxes = "", string attachments = "" )
	{
		if ( meshFilenames is null || meshFilenames.Count == 0 )
			throw new ArgumentException( "at least one mesh", nameof( meshFilenames ) );
		if ( switchDistances is null || switchDistances.Count != meshFilenames.Count )
			throw new ArgumentException( "one switch distance per mesh", nameof( switchDistances ) );

		return Wrap( materials
			+ RenderMeshList( meshFilenames )
			+ LodGroupList( switchDistances )
			+ attachments
			+ hitboxes
			+ VmdlAnimation.BoneMarkupList( skeleton )
			+ (string.IsNullOrEmpty( animations ) ? VmdlAnimation.BindPoseList() : animations)
			+ physics, animGraph );
	}

	static string Wrap( string children, string animGraph = "" ) =>
		Header
		+ "{\n"
		+ "\trootNode = \n"
		+ "\t{\n"
		+ "\t\t_class = \"RootNode\"\n"
		+ "\t\tchildren = \n"
		+ "\t\t[\n"
		+ children
		+ "\t\t]\n"
		+ "\t\tmodel_archetype = \"\"\n"
		+ "\t\tprimary_associated_entity = \"\"\n"
		+ $"\t\tanim_graph_name = \"{NormalizeAssetPath( animGraph )}\"\n"
		+ "\t\tbase_model_name = \"\"\n"
		+ "\t}\n"
		+ "}\n";

	/// <summary>
	/// A static model: one mesh file, whatever PhysicsShapeList and MaterialGroupList the caller
	/// built, and no skeleton.
	///
	/// MATERIALS MATTER AS MUCH AS COLLISION. An omitted MaterialGroupList is what ModelDoc fills
	/// in with use_global_default = true and materials/default.vmat — a part that rendered in the
	/// viewport with the materials that were dropped on it compiles as a blank grey prop. Pass an
	/// empty list with the global default off to leave the mesh's own names in place.
	/// </summary>
	public static string Static( string meshFilename, string physics = "", string materials = "" ) =>
		Wrap( materials + RenderMeshList( meshFilename ) + physics );

	/// <summary>
	/// Citizen's animation graph. A skinned model that names this, and carries citizen's bones
	/// under citizen's names in citizen's bind pose, is a playermodel - the graph drives it exactly
	/// as it drives citizen. Nothing validates the pairing: naming the graph on a model with its
	/// own skeleton compiles and then animates into a knot, which is why
	/// <see cref="SkeletonRetarget"/> exists.
	/// </summary>
	public const string CitizenAnimGraph = "models/citizen/citizen.vanmgrph";

	/// <summary>
	/// A skinned model: the mesh file carries the bone hierarchy, bind pose and per-vertex weights,
	/// so this adds the bone markup and the animation list on top of the static shape.
	/// </summary>
	/// <param name="meshFilename">The mesh on disk, project-relative. Its extension decides the
	/// import rotation — see <see cref="ImportRotation"/>.</param>
	/// <param name="skeleton">The bones the mesh file carries, for the bone markup node.</param>
	/// <param name="physics">The PhysicsShapeList node, or empty for no collision.</param>
	/// <param name="materials">The MaterialGroupList node.</param>
	/// <param name="animations">The AnimationList node. Null or empty means the bind pose alone —
	/// <see cref="VmdlAnimation.AnimationList"/> with no clips is byte-identical to
	/// <see cref="VmdlAnimation.BindPoseList"/>, and a test holds that, so the no-clips path is
	/// unchanged rather than merely equivalent.</param>
	/// <param name="animGraph">An animation graph to drive the model, project-relative — see
	/// <see cref="CitizenAnimGraph"/>. Empty, the default, leaves the model animated only by
	/// whatever clips it carries, which is what every Effigy export wanted before playermodels.</param>
	public static string Skinned( string meshFilename, Skeleton skeleton, string physics = "",
		string materials = "", string animations = null, string animGraph = "" ) =>
		Wrap( materials
			+ RenderMeshList( meshFilename )
			+ VmdlAnimation.BoneMarkupList( skeleton )
			+ (string.IsNullOrEmpty( animations ) ? VmdlAnimation.BindPoseList() : animations)
			+ physics, animGraph );

	// --- H2: attachments, hitboxes, body groups, ragdoll joints, break pieces ----------------
	//
	// SCHEMA HONESTY, same warning as VmdlPhysics' ragdoll nodes: these node and key names
	// follow the roadmap's stated ModelDoc schema (Attachment with name / parent_bone /
	// relative_origin / relative_angles; Hitbox with parent_bone / point0 / point1 / radius /
	// size; BodyGroup with name / meshes; BreakPiece with name / filename / mass). They have
	// NOT been compiled against s&box here — no sbox-dev on this machine — so a compiled-model
	// check is still owed before anyone calls them "working". What the tests prove is the
	// round-trip: every field written here is read back out of the text.

	/// <summary>A socket on a bone: where a muzzle flash, a hand prop, or an eye sits.</summary>
	public sealed class Attachment
	{
		public string Name = "";
		public string ParentBone = "";
		public Vec3 Origin;
		public Vec3 Angles;
	}

	/// <summary>
	/// One trace hitbox. A capsule uses Point0/Point1 as its endpoints and Radius; a box uses
	/// Point0 as its centre and Size as its full extents. Kind must be "capsule" or "box";
	/// anything else throws in HitboxSetList rather than writing a node the compiler ignores.
	/// </summary>
	public sealed class Hitbox
	{
		public string Bone = "";
		public string Kind = "capsule";
		public Vec3 Point0;
		public Vec3 Point1;
		public float Radius;
		public Vec3 Size;
	}

	/// <summary>One body group: a toggleable set of render-mesh node names (a head, a damage state).</summary>
	public sealed class BodyGroup
	{
		public string Name = "";
		public List<string> Meshes = new();
	}

	/// <summary>A piece that breaks off: its own mesh file and its mass.</summary>
	public sealed class BreakPiece
	{
		public string Name = "";
		public string MeshFilename = "";
		public float Mass;
	}

	/// <summary>The AttachmentList node, or empty when there is nothing to attach.</summary>
	public static string AttachmentList( IReadOnlyList<Attachment> attachments )
	{
		if ( attachments is null || attachments.Count == 0 )
			return "";
		var body = new StringBuilder();
		foreach ( var a in attachments )
		{
			if ( a is null || string.IsNullOrWhiteSpace( a.Name ) )
				continue;
			body.Append( "\t\t\t\t\t{\n" );
			body.Append( "\t\t\t\t\t\t_class = \"Attachment\"\n" );
			body.Append( $"\t\t\t\t\t\tname = \"{a.Name}\"\n" );
			body.Append( $"\t\t\t\t\t\tparent_bone = \"{a.ParentBone}\"\n" );
			body.Append( $"\t\t\t\t\t\trelative_origin = {Vec( a.Origin )}\n" );
			body.Append( $"\t\t\t\t\t\trelative_angles = {Vec( a.Angles )}\n" );
			body.Append( "\t\t\t\t\t},\n" );
		}
		return body.Length == 0 ? "" : ListNode( "AttachmentList", body );
	}

	/// <summary>The HitboxSetList node, or empty when there is nothing to trace against.</summary>
	public static string HitboxSetList( IReadOnlyList<Hitbox> hitboxes )
	{
		if ( hitboxes is null || hitboxes.Count == 0 )
			return "";
		var body = new StringBuilder();
		foreach ( var h in hitboxes )
		{
			if ( h is null || string.IsNullOrWhiteSpace( h.Bone ) )
				continue;
			if ( h.Kind != "capsule" && h.Kind != "box" )
				throw new ArgumentException( $"hitbox kind must be 'capsule' or 'box', not '{h.Kind}'", nameof( hitboxes ) );
			body.Append( "\t\t\t\t\t{\n" );
			body.Append( "\t\t\t\t\t\t_class = \"Hitbox\"\n" );
			body.Append( $"\t\t\t\t\t\tparent_bone = \"{h.Bone}\"\n" );
			body.Append( $"\t\t\t\t\t\tkind = \"{h.Kind}\"\n" );
			body.Append( $"\t\t\t\t\t\tpoint0 = {Vec( h.Point0 )}\n" );
			body.Append( $"\t\t\t\t\t\tpoint1 = {Vec( h.Point1 )}\n" );
			body.Append( $"\t\t\t\t\t\tradius = {Num( h.Radius )}\n" );
			body.Append( $"\t\t\t\t\t\tsize = {Vec( h.Size )}\n" );
			body.Append( "\t\t\t\t\t},\n" );
		}
		return body.Length == 0 ? "" : ListNode( "HitboxSetList", body );
	}

	/// <summary>The BodyGroupList node, or empty when the model has no toggleable parts.</summary>
	public static string BodyGroupList( IReadOnlyList<BodyGroup> groups )
	{
		if ( groups is null || groups.Count == 0 )
			return "";
		var body = new StringBuilder();
		foreach ( var g in groups )
		{
			if ( g is null || string.IsNullOrWhiteSpace( g.Name ) || g.Meshes is null || g.Meshes.Count == 0 )
				continue;
			body.Append( "\t\t\t\t\t{\n" );
			body.Append( "\t\t\t\t\t\t_class = \"BodyGroup\"\n" );
			body.Append( $"\t\t\t\t\t\tname = \"{g.Name}\"\n" );
			body.Append( "\t\t\t\t\t\tmeshes = \n" );
			body.Append( "\t\t\t\t\t\t[\n" );
			foreach ( var mesh in g.Meshes )
				body.Append( $"\t\t\t\t\t\t\t\"{mesh}\",\n" );
			body.Append( "\t\t\t\t\t\t]\n" );
			body.Append( "\t\t\t\t\t},\n" );
		}
		return body.Length == 0 ? "" : ListNode( "BodyGroupList", body );
	}

	/// <summary>The BreakPieceList node, or empty when nothing breaks off.</summary>
	public static string BreakPieceList( IReadOnlyList<BreakPiece> pieces )
	{
		if ( pieces is null || pieces.Count == 0 )
			return "";
		var body = new StringBuilder();
		foreach ( var piece in pieces )
		{
			if ( piece is null || string.IsNullOrWhiteSpace( piece.Name ) )
				continue;
			body.Append( "\t\t\t\t\t{\n" );
			body.Append( "\t\t\t\t\t\t_class = \"BreakPiece\"\n" );
			body.Append( $"\t\t\t\t\t\tname = \"{piece.Name}\"\n" );
			body.Append( $"\t\t\t\t\t\tfilename = \"{NormalizeAssetPath( piece.MeshFilename )}\"\n" );
			body.Append( $"\t\t\t\t\t\tmass = {Num( piece.Mass )}\n" );
			body.Append( "\t\t\t\t\t},\n" );
		}
		return body.Length == 0 ? "" : ListNode( "BreakPieceList", body );
	}

	static string ListNode( string cls, StringBuilder children )
	{
		var sb = new StringBuilder();
		sb.Append( "\t\t\t{\n" );
		sb.Append( $"\t\t\t\t_class = \"{cls}\"\n" );
		sb.Append( "\t\t\t\tchildren = \n" );
		sb.Append( "\t\t\t\t[\n" );
		sb.Append( children );
		sb.Append( "\t\t\t\t]\n" );
		sb.Append( "\t\t\t},\n" );
		return sb.ToString();
	}

	/// <summary>
	/// Parse a body-group export spec, "Head:HeadA,HeadB;Fins:FinsIntact,FinsTorn": groups
	/// separated by ';', each "Name:MeshA,MeshB". Malformed input throws — a half-parsed
	/// spec would toggle the wrong parts at runtime, and that failure is silent. Empty spec
	/// means no groups, not an error.
	/// </summary>
	public static List<BodyGroup> ParseBodyGroups( string spec )
	{
		var groups = new List<BodyGroup>();
		if ( string.IsNullOrWhiteSpace( spec ) )
			return groups;
		foreach ( var part in spec.Split( ';' ) )
		{
			var trimmed = part.Trim();
			if ( trimmed.Length == 0 )
				continue;
			var colon = trimmed.IndexOf( ':' );
			if ( colon <= 0 )
				throw new ArgumentException( $"body group '{trimmed}' needs a 'Name:MeshA,MeshB' shape", nameof( spec ) );
			var name = trimmed.Substring( 0, colon ).Trim();
			var meshes = trimmed.Substring( colon + 1 ).Split( ',' )
				.Select( m => m.Trim() ).Where( m => m.Length > 0 ).ToList();
			if ( meshes.Count == 0 )
				throw new ArgumentException( $"body group '{name}' names no meshes", nameof( spec ) );
			if ( groups.Any( g => g.Name == name ) )
				throw new ArgumentException( $"body group '{name}' is listed twice", nameof( spec ) );
			groups.Add( new BodyGroup { Name = name, Meshes = meshes } );
		}
		return groups;
	}

	/// <summary>
	/// The RenderMeshList node for a body group's meshes: one RenderMeshFile per (name, file)
	/// pair, reusing the same node text as the LOD list. The caller writes these meshes as
	/// separate files on disk (one per body-group part) and passes the pairs here.
	/// </summary>
	public static string BodyGroupRenderMeshList( IReadOnlyList<(string Name, string Filename)> meshes )
	{
		if ( meshes is null || meshes.Count == 0 )
			return "";
		var sb = new StringBuilder();
		sb.Append( "\t\t\t{\n" );
		sb.Append( "\t\t\t\t_class = \"RenderMeshList\"\n" );
		sb.Append( "\t\t\t\tchildren = \n" );
		sb.Append( "\t\t\t\t[\n" );
		foreach ( var (name, filename) in meshes )
		{
			if ( string.IsNullOrWhiteSpace( name ) )
				throw new ArgumentException( "a body-group mesh needs a name", nameof( meshes ) );
			sb.Append( RenderMeshFile( name, filename ) );
		}
		sb.Append( "\t\t\t\t]\n" );
		sb.Append( "\t\t\t},\n" );
		return sb.ToString();
	}

	/// <summary>
	/// One capsule hitbox per bone, head to tail in bind space, radius a fifth of the bone's
	/// length rounded by the same rule as the ragdoll capsules (never under 0.25). This is
	/// the fallback when the mesh is unskinned; a skinned mesh should use
	/// <see cref="HitboxesFromSkin"/>, which measures the vertices that actually hang off
	/// each bone.
	/// </summary>
	public static List<Hitbox> HitboxesFromSkeleton( Skeleton skeleton )
	{
		var hitboxes = new List<Hitbox>();
		if ( skeleton is null )
			return hitboxes;
		foreach ( var capsule in VmdlPhysics.CapsulesFromSkeleton( skeleton ) )
			hitboxes.Add( new Hitbox
			{
				Bone = capsule.Bone, Kind = "capsule",
				Point0 = capsule.Point0, Point1 = capsule.Point1, Radius = capsule.Radius,
			} );
		return hitboxes;
	}

	/// <summary>
	/// One capsule hitbox per bone, measured from the skinned mesh: the vertices weighted to
	/// the bone, projected onto the bone's direction, give the endpoints; the widest
	/// perpendicular distance gives the radius (never under 0.25). A bone with fewer than two
	/// weighted vertices falls back to its skeleton capsule, so a sparsely weighted bone — or
	/// one the mesh never weights at all, like a prop bone — still traces along its bind pose
	/// instead of leaving a hole in the trace set.
	/// </summary>
	public static List<Hitbox> HitboxesFromSkin( PolyMesh mesh, Skeleton skeleton )
	{
		var hitboxes = new List<Hitbox>();
		if ( mesh is null || skeleton is null || mesh.Skin is null )
			return hitboxes;
		var skeletonCapsules = VmdlPhysics.CapsulesFromSkeleton( skeleton )
			.ToDictionary( c => c.Bone, c => c );
		for ( var boneIndex = 0; boneIndex < skeleton.Count; boneIndex++ )
		{
			var boneName = skeleton.Bones[boneIndex].Name;
			var points = new List<Vec3>();
			for ( var v = 0; v < mesh.Positions.Count && v < mesh.Skin.Vertices.Count; v++ )
			{
				var weights = mesh.Skin.Vertices[v];
				if ( weights is null )
					continue;
				foreach ( var w in weights )
					if ( w.Bone == boneIndex && w.Weight > 0f )
					{
						points.Add( mesh.Positions[v] );
						break;
					}
			}
			if ( points.Count < 2 )
			{
				if ( skeletonCapsules.TryGetValue( boneName, out var fallback ) )
					hitboxes.Add( new Hitbox
					{
						Bone = boneName, Kind = "capsule",
						Point0 = fallback.Point0, Point1 = fallback.Point1, Radius = fallback.Radius,
					} );
				continue;
			}
			var centre = Vec3.Zero;
			foreach ( var p in points )
				centre += p;
			centre /= points.Count;
			var axis = skeleton.BoneDirection( boneIndex ).Normal;
			if ( axis.LengthSquared < 1e-8f )
				axis = new Vec3( 0, 1, 0 );
			var tMin = float.MaxValue;
			var tMax = float.MinValue;
			var radius = 0f;
			foreach ( var p in points )
			{
				var rel = p - centre;
				var t = Vec3.Dot( rel, axis );
				tMin = MathF.Min( tMin, t );
				tMax = MathF.Max( tMax, t );
				var perpendicular = rel - axis * t;
				radius = MathF.Max( radius, perpendicular.Length );
			}
			if ( tMax - tMin < 1e-4f )
			{
				if ( skeletonCapsules.TryGetValue( boneName, out var fallback2 ) )
					hitboxes.Add( new Hitbox
					{
						Bone = boneName, Kind = "capsule",
						Point0 = fallback2.Point0, Point1 = fallback2.Point1, Radius = fallback2.Radius,
					} );
				continue;
			}
			hitboxes.Add( new Hitbox
			{
				Bone = boneName, Kind = "capsule",
				Point0 = centre + axis * tMin,
				Point1 = centre + axis * tMax,
				Radius = MathF.Max( 0.25f, radius ),
			} );
		}
		return hitboxes;
	}

	/// <summary>
	/// <see cref="Static"/> with the H2 lists: body-group meshes, attachments, hitboxes, body
	/// groups, ragdoll joints, break pieces, then the static physics, then the ragdoll physics.
	/// With every extra empty, this is byte-identical to <see cref="Static"/> — a test holds
	/// that, so existing exports do not change by adopting this composer.
	/// </summary>
	public static string WithLists( string meshFilename, string physics = "", string materials = "",
		string bodyGroupMeshes = "", string attachments = "", string hitboxes = "", string bodyGroups = "",
		string joints = "", string breakPieces = "", string ragdollPhysics = "" ) =>
		Wrap( materials + RenderMeshList( meshFilename ) + bodyGroupMeshes + attachments + hitboxes
			+ bodyGroups + joints + breakPieces + physics + ragdollPhysics );

	/// <summary>
	/// <see cref="Skinned"/> with the H2 lists, in the same positions. With every extra empty,
	/// byte-identical to <see cref="Skinned"/>.
	/// </summary>
	public static string SkinnedWithLists( string meshFilename, Skeleton skeleton, string physics = "",
		string materials = "", string animations = null, string animGraph = "",
		string bodyGroupMeshes = "", string attachments = "", string hitboxes = "", string bodyGroups = "",
		string joints = "", string breakPieces = "", string ragdollPhysics = "" ) =>
		Wrap( materials + RenderMeshList( meshFilename ) + bodyGroupMeshes + attachments + hitboxes
			+ bodyGroups + joints + breakPieces
			+ VmdlAnimation.BoneMarkupList( skeleton )
			+ (string.IsNullOrEmpty( animations ) ? VmdlAnimation.BindPoseList() : animations)
			+ physics + ragdollPhysics, animGraph );
}
