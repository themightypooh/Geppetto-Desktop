using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using Effigy;

namespace Marionette.Desktop;

// G11: control registry and search index (data only, engine-free).
//
// Everything here is built by reflection over the kernel that is actually compiled, the same
// way Effigy.ApiCatalog builds EFFIGY-API.md, so a feature added to the kernel shows up here
// without a hand-maintained list. No UI: this is the data a future panel (Godot dock, desktop
// dialog, AI tool) reads to know what controls exist, what they take, and how to find them.
//
// EVERY LABEL, TOOLTIP, CATEGORY AND SYNONYM IN THIS FILE IS A DRAFT for pooh's review.
// Each entry carries IsDraft = true and DraftSource = ControlRegistry.DraftSource so a UI can
// badge them and a later review pass can replace them one at a time.

/// <summary>What kind of value a control edits.</summary>
public enum ControlKind
{
	Number,
	Integer,
	Toggle,
	Choice,
	Point,
	Colour,
	Text,
	Bodies,
	Points,
	Pairs,
	FeatureRef,
	FeatureRefs,
	Faces,
	Edges,
	Strokes,
	Indices,
	Other,
}

/// <summary>Where a control came from.</summary>
public enum ControlSource
{
	/// <summary>A feature parameter (IParam in Parameters/AdvancedParameters).</summary>
	FeatureParameter,
	/// <summary>A public feature field the script accepts (points, face refs, feature names...).</summary>
	FeatureField,
	/// <summary>A scalar property of core MaterialDescriptor (the material lobes the renderer reads).</summary>
	MaterialDescriptor,
}

/// <summary>One editable property of a feature (or of a material).</summary>
public sealed class ControlParam
{
	/// <summary>Stable id: "FeatureName.key", or "Material.PropertyName".</summary>
	public string Id { get; init; }
	/// <summary>Owner: the feature's script name ("DraftPanels"), or "Material".</summary>
	public string Owner { get; init; }
	/// <summary>The script key (`add DraftPanels waist=…`), lower-case with spaces removed.</summary>
	public string Key { get; init; }
	/// <summary>The label the kernel gives the parameter (or the field/property name).</summary>
	public string KernelLabel { get; init; }
	public ControlKind Kind { get; init; }
	public ControlSource Source { get; init; }
	/// <summary>Default value as text, invariant culture. Null when there is no meaningful default.</summary>
	public string Default { get; init; }
	public double? Min { get; init; }
	public double? Max { get; init; }
	public string Unit { get; init; }
	/// <summary>Choice options, or null.</summary>
	public IReadOnlyList<string> Options { get; init; }
	/// <summary>True when the kernel puts this behind the Advanced disclosure.</summary>
	public bool Advanced { get; init; }
	/// <summary>For integers: false when the number is an identifier (a material slot), not a magnitude.</summary>
	public bool Slider { get; init; } = true;
	/// <summary>DRAFT grouping inside the owner: Main, Advanced, Placement, Material slots, Inputs, ...</summary>
	public string Group { get; init; }
	/// <summary>DRAFT display label.</summary>
	public string DraftLabel { get; init; }
	/// <summary>DRAFT tooltip, generated from kind/range/default.</summary>
	public string DraftTooltip { get; init; }
	public bool IsDraft { get; init; } = true;
	public string DraftSource { get; init; } = ControlRegistry.DraftSource;

	public override string ToString() => $"{Id} ({Kind}{(Default is null ? "" : ", default " + Default)})";
}

/// <summary>One feature type the catalog exposes, with its editable properties.</summary>
public sealed class ControlFeature
{
	/// <summary>The script name (`add NAME`), exactly as ApiCatalog.FeatureNames lists it.</summary>
	public string Name { get; init; }
	/// <summary>The C# type name (e.g. "DraftPanelsFeature").</summary>
	public string ClassName { get; init; }
	/// <summary>The kernel's TypeName (what the feature tree shows).</summary>
	public string KernelTypeName { get; init; }
	/// <summary>DRAFT category (Modelling, Sculpt, Materials, Clothing, Character, Hair, ...).</summary>
	public string Category { get; init; }
	public string DraftLabel { get; init; }
	public string DraftTooltip { get; init; }
	public bool IsDraft { get; init; } = true;
	public string DraftSource { get; init; } = ControlRegistry.DraftSource;
	public IReadOnlyList<ControlParam> Params { get; init; }

	public override string ToString() => $"{Name} [{Category}] ({Params.Count} params)";
}

public enum ControlHitKind { Feature, Param }

/// <summary>One ranked search result.</summary>
public sealed class ControlSearchHit
{
	public ControlHitKind Kind { get; init; }
	/// <summary>Feature name, or param id.</summary>
	public string Id { get; init; }
	public ControlFeature Feature { get; init; }
	/// <summary>Null for a feature hit.</summary>
	public ControlParam Param { get; init; }
	public double Score { get; init; }
	/// <summary>The index words that matched (for "why did this come up" debugging).</summary>
	public IReadOnlyList<string> Matched { get; init; }

	public override string ToString() => $"{Score:0.00} {Kind} {Id}";
}

/// <summary>
/// G11 control registry: every feature type in Effigy.ApiCatalog and each of its editable
/// properties, plus the core MaterialDescriptor lobes, with a ranked keyword + synonym search.
/// Engine-free; the Godot plugin wraps it in EffigyControlRegistry.
/// </summary>
public sealed class ControlRegistry
{
	public const string DraftSource = "DRAFT (generated by G11 ControlRegistry from kernel labels; awaiting pooh review)";
	/// <summary>Owner name used for the MaterialDescriptor pseudo-feature.</summary>
	public const string MaterialOwner = "Material";

	static readonly Lazy<ControlRegistry> _shared = new( Build );
	/// <summary>The registry for the loaded kernel, built once on first use.</summary>
	public static ControlRegistry Shared => _shared.Value;

	readonly Dictionary<string, ControlFeature> _byName;
	readonly Dictionary<string, ControlParam> _paramById;
	readonly Dictionary<string, List<Posting>> _index;
	readonly string[] _sortedTokens;
	readonly List<object> _entries; // ControlFeature or ControlParam, by entry id

	/// <summary>The 118-ish catalog feature types, in ApiCatalog order.</summary>
	public IReadOnlyList<ControlFeature> Features { get; }
	/// <summary>MaterialDescriptor lobes (roughness, metallic, sheen, ...). Not counted as features.</summary>
	public ControlFeature Material { get; }
	public int FeatureParamCount => Features.Sum( f => f.Params.Count );

	public ControlFeature Feature( string name ) => name is not null && _byName.TryGetValue( Norm( name ), out var f ) ? f : null;
	public ControlParam Param( string id ) => id is not null && _paramById.TryGetValue( id, out var p ) ? p : null;

	readonly struct Posting
	{
		public readonly int Entry;
		public readonly double Weight;
		public Posting( int entry, double weight ) { Entry = entry; Weight = weight; }
	}

	ControlRegistry( List<ControlFeature> features, ControlFeature material )
	{
		Features = features;
		Material = material;
		_byName = new( StringComparer.Ordinal );
		_paramById = new( StringComparer.Ordinal );
		_entries = new();
		_index = new( StringComparer.Ordinal );

		foreach ( var f in features.Append( material ) )
		{
			_byName[Norm( f.Name )] = f;
			foreach ( var p in f.Params ) _paramById[p.Id] = p;
		}

		foreach ( var f in features.Append( material ) )
		{
			int fe = _entries.Count;
			_entries.Add( f );
			AddWords( fe, f.Name, Weights.FeatureName );
			AddWords( fe, f.KernelTypeName, Weights.FeatureName );
			AddWords( fe, f.Category, Weights.Category );
			foreach ( var p in f.Params )
				AddWords( fe, p.KernelLabel, Weights.FeatureViaParam );

			foreach ( var p in f.Params )
			{
				int pe = _entries.Count;
				_entries.Add( p );
				AddToken( pe, p.Key, Weights.ParamKey );
				AddWords( pe, p.KernelLabel, Weights.ParamLabel );
				AddWords( pe, f.Name, Weights.ParamViaFeature );
				AddWords( pe, f.KernelTypeName, Weights.ParamViaFeature );
				AddWords( pe, p.Group, Weights.Category );
				AddWords( pe, f.Category, Weights.Category );
				if ( p.Options is not null )
					foreach ( var o in p.Options ) AddWords( pe, o, Weights.Option );
			}
		}

		foreach ( var list in _index.Values ) Dedupe( list );
		_sortedTokens = _index.Keys.OrderBy( k => k, StringComparer.Ordinal ).ToArray();
	}

	static class Weights
	{
		public const double ParamKey = 10, ParamLabel = 9, FeatureName = 10, FeatureViaParam = 2,
			ParamViaFeature = 3, Option = 2, Category = 1.5;
		public const double Synonym = 0.6, Prefix = 0.5, AllTermsBonus = 1.25, FeatureOwnParam = 4;
	}

	void AddWords( int entry, string text, double weight )
	{
		foreach ( var w in Words( text ) ) AddToken( entry, w, weight );
	}

	void AddToken( int entry, string token, double weight )
	{
		token = Stem( Norm( token ) );
		if ( token.Length == 0 ) return;
		if ( !_index.TryGetValue( token, out var list ) ) _index[token] = list = new();
		list.Add( new Posting( entry, weight ) );
	}

	/// <summary>Keep only the best weight per entry for a token.</summary>
	static void Dedupe( List<Posting> list )
	{
		var best = new Dictionary<int, double>();
		foreach ( var p in list )
			if ( !best.TryGetValue( p.Entry, out var w ) || p.Weight > w ) best[p.Entry] = p.Weight;
		list.Clear();
		foreach ( var kv in best.OrderBy( k => k.Key ) ) list.Add( new Posting( kv.Key, kv.Value ) );
	}

	// --- search ------------------------------------------------------------------------------------

	/// <summary>
	/// Ranked keyword + synonym search over feature names, kernel labels, script keys, choice
	/// options, and the draft categories. Each query word scores an entry by its best match
	/// (exact &gt; synonym &gt; prefix); an entry matching every query word gets a bonus.
	/// </summary>
	public IReadOnlyList<ControlSearchHit> Search( string query, int limit = 20, bool includeMaterial = true )
	{
		var terms = Words( query ).Select( w => Stem( Norm( w ) ) ).Where( w => w.Length > 0 ).Distinct().ToList();
		if ( terms.Count == 0 || limit <= 0 ) return Array.Empty<ControlSearchHit>();

		var score = new Dictionary<int, double>();
		var termsHit = new Dictionary<int, int>();
		var matched = new Dictionary<int, HashSet<string>>();
		// Best per-term weight per entry: a param matching only through its
		// feature's name never exceeds Weights.ParamViaFeature (3), so a best
		// above that means it matched on its own key/label/option too.
		var bestOwn = new Dictionary<int, double>();

		foreach ( var term in terms )
		{
			var perEntry = new Dictionary<int, (double w, string tok)>();
			void Take( string token, double factor )
			{
				if ( !_index.TryGetValue( token, out var list ) ) return;
				foreach ( var p in list )
				{
					double w = p.Weight * factor;
					if ( !perEntry.TryGetValue( p.Entry, out var cur ) || w > cur.w ) perEntry[p.Entry] = (w, token);
				}
			}

			Take( term, 1.0 );
			foreach ( var syn in Synonyms( term ) ) Take( syn, Weights.Synonym );
			if ( term.Length >= 3 )
				foreach ( var tok in PrefixTokens( term ) )
					if ( tok != term ) Take( tok, Weights.Prefix );

			foreach ( var (entry, hit) in perEntry )
			{
				score[entry] = score.GetValueOrDefault( entry ) + hit.w;
				if ( !bestOwn.TryGetValue( entry, out var bw ) || hit.w > bw ) bestOwn[entry] = hit.w;
				termsHit[entry] = termsHit.GetValueOrDefault( entry ) + 1;
				if ( !matched.TryGetValue( entry, out var set ) ) matched[entry] = set = new();
				set.Add( hit.tok );
			}
		}

		var hits = new List<ControlSearchHit>();
		foreach ( var (entry, s) in score )
		{
			double final = terms.Count > 1 && termsHit[entry] == terms.Count ? s * Weights.AllTermsBonus : s;
			var e = _entries[entry];
			if ( e is ControlFeature f )
			{
				if ( !includeMaterial && ReferenceEquals( f, Material ) ) continue;
				hits.Add( new ControlSearchHit { Kind = ControlHitKind.Feature, Id = f.Name, Feature = f, Score = final, Matched = matched[entry].ToList() } );
			}
			else if ( e is ControlParam p )
			{
				if ( !includeMaterial && p.Source == ControlSource.MaterialDescriptor ) continue;
				// A param of the feature the query names, that also matches through
			// its own key/label, ranks with the feature itself: searching "eye"
			// should surface Eye's own controls ahead of other features' eye
			// params (which is why the eye check counts Eye entries near the top).
			if ( bestOwn.TryGetValue( entry, out var own ) && own > Weights.ParamViaFeature
					&& terms.Any( t => Stem( Norm( p.Owner ) ) == t ) )
					final += Weights.FeatureOwnParam;
				hits.Add( new ControlSearchHit { Kind = ControlHitKind.Param, Id = p.Id, Feature = Feature( p.Owner ), Param = p, Score = final, Matched = matched[entry].ToList() } );
			}
		}

		return hits
			.OrderByDescending( h => h.Score )
			.ThenBy( h => h.Kind ) // a feature before its own params on a tie
			.ThenBy( h => h.Id, StringComparer.Ordinal )
			.Take( limit )
			.ToList();
	}

	IEnumerable<string> PrefixTokens( string prefix )
	{
		int lo = Array.BinarySearch( _sortedTokens, prefix, StringComparer.Ordinal );
		if ( lo < 0 ) lo = ~lo;
		for ( int i = lo; i < _sortedTokens.Length && _sortedTokens[i].StartsWith( prefix, StringComparison.Ordinal ); i++ )
			yield return _sortedTokens[i];
	}

	// --- DRAFT synonyms ----------------------------------------------------------------------------
	// Each row is a cluster: any word in it also finds the others at Weights.Synonym. Words are
	// stemmed the same way the index is. DRAFT for pooh's review — add/remove rows freely.

	static readonly string[][] SynonymClusters =
	{
		new[] { "waist", "torso", "belly", "midriff", "hip", "abdomen", "trunk" },
		new[] { "torso", "chest", "body", "trunk" },
		new[] { "roughness", "rough", "gloss", "glossy", "shine", "shiny", "specular", "matte", "sheen", "clearcoat" },
		new[] { "material", "shader", "surface", "flesh", "fabric", "paint", "texture" },
		new[] { "metal", "metallic", "metalness" },
		new[] { "eye", "iris", "pupil", "sclera", "cornea", "eyeball", "catchlight", "eyelash" },
		new[] { "hair", "fur", "strand", "groom", "beard", "moustache", "brow", "mane", "scalp" },
		new[] { "colour", "color", "tint", "hue", "albedo" },
		new[] { "glow", "emission", "emissive", "light", "brightness" },
		new[] { "garment", "cloth", "clothing", "fabric", "outfit", "drape", "wear" },
		new[] { "fillet", "bevel", "chamfer", "round" },
		new[] { "boolean", "cut", "subtract", "union", "intersect", "hole" },
		new[] { "rig", "bone", "skin", "weight", "skeleton" },
		new[] { "particle", "emitter", "fx", "sim", "spark" },
		new[] { "wrinkle", "crease", "fold", "pleat" },
		new[] { "uv", "unwrap" },
		new[] { "smooth", "subdivide", "subdivision", "relax" },
		new[] { "mirror", "symmetry", "symmetric" },
		new[] { "seed", "random", "variation" },
		new[] { "density", "count", "amount" },
		new[] { "size", "scale", "radius", "width", "height", "length", "thickness" },
		new[] { "position", "place", "location", "move", "offset" },
		new[] { "rotation", "rotate", "angle", "twist" },
		new[] { "mouth", "lip", "teeth", "tooth", "tongue", "jaw" },
		new[] { "transparent", "transparency", "opacity", "alpha", "transmission" },
		new[] { "subsurface", "sss", "translucent", "lightthrough" },
		new[] { "slot", "material" },
	};

	static readonly Dictionary<string, string[]> _synonyms = BuildSynonyms();

	static Dictionary<string, string[]> BuildSynonyms()
	{
		var map = new Dictionary<string, HashSet<string>>( StringComparer.Ordinal );
		foreach ( var row in SynonymClusters )
		{
			var words = row.Select( w => Stem( Norm( w ) ) ).Distinct().ToArray();
			foreach ( var w in words )
			{
				if ( !map.TryGetValue( w, out var set ) ) map[w] = set = new();
				foreach ( var o in words ) if ( o != w ) set.Add( o );
			}
		}
		return map.ToDictionary( k => k.Key, v => v.Value.ToArray(), StringComparer.Ordinal );
	}

	public static IReadOnlyList<string> Synonyms( string word ) =>
		_synonyms.TryGetValue( Stem( Norm( word ) ), out var s ) ? s : Array.Empty<string>();

	// --- text helpers ------------------------------------------------------------------------------

	/// <summary>The kernel's script-key normalisation (BuildScript.Normalise).</summary>
	public static string Norm( string s ) => (s ?? "").ToLowerInvariant().Replace( " ", "" ).Replace( "_", "" ).Replace( "'", "" );

	/// <summary>Tiny plural stemmer, applied identically to index and query.</summary>
	static string Stem( string w )
	{
		if ( w.Length > 3 && w.EndsWith( 's' ) && !w.EndsWith( "ss" ) && !w.EndsWith( "us" ) && !w.EndsWith( "is" ) )
			return w[..^1];
		return w;
	}

	/// <summary>Split on non-alphanumerics and camelCase ("DraftPanels" → draft, panels, draftpanels).</summary>
	static IEnumerable<string> Words( string text )
	{
		if ( string.IsNullOrWhiteSpace( text ) ) yield break;
		var parts = new List<string>();
		var sb = new StringBuilder();
		for ( int i = 0; i < text.Length; i++ )
		{
			char c = text[i];
			if ( !char.IsLetterOrDigit( c ) )
			{
				if ( sb.Length > 0 ) { parts.Add( sb.ToString() ); sb.Clear(); }
				continue;
			}
			bool boundary = sb.Length > 0 && char.IsUpper( c ) &&
				( char.IsLower( text[i - 1] ) || ( i + 1 < text.Length && char.IsLower( text[i + 1] ) && char.IsUpper( text[i - 1] ) ) );
			if ( boundary ) { parts.Add( sb.ToString() ); sb.Clear(); }
			sb.Append( c );
		}
		if ( sb.Length > 0 ) parts.Add( sb.ToString() );
		foreach ( var p in parts ) yield return p.ToLowerInvariant();
		if ( parts.Count > 1 )
		{
			// The joined form too, so "draftpanels", "irisradius" or "ParticleFx" match whole.
			yield return string.Concat( parts ).ToLowerInvariant();
		}
	}

	static string Inv( double v ) => v.ToString( "0.####", CultureInfo.InvariantCulture );

	static string SentenceCase( string label )
	{
		if ( string.IsNullOrWhiteSpace( label ) ) return label;
		var words = Words( label ).ToList();
		if ( words.Count > 1 ) words.RemoveAt( words.Count - 1 ); // drop the joined form
		var text = string.Join( " ", words );
		return char.ToUpperInvariant( text[0] ) + text[1..];
	}

	// --- building ----------------------------------------------------------------------------------

	/// <summary>The same set and order as BuildScript.FeatureTypes (and so ApiCatalog.FeatureNames).</summary>
	static IEnumerable<Type> FeatureTypes() => typeof( Effigy.Feature ).Assembly.GetTypes()
		.Where( t => t.IsSubclassOf( typeof( Effigy.Feature ) ) && !t.IsAbstract )
		.OrderBy( t => t.Name, StringComparer.Ordinal );

	static string ScriptName( Type t ) => t.Name.EndsWith( "Feature" ) ? t.Name[..^7] : t.Name;

	public static ControlRegistry Build()
	{
		var features = new List<ControlFeature>();
		foreach ( var type in FeatureTypes() )
		{
			var name = ScriptName( type );
			Effigy.Feature instance;
			try { instance = (Effigy.Feature)Activator.CreateInstance( type ); }
			catch { instance = null; }
			features.Add( BuildFeature( name, type, instance ) );
		}
		return new ControlRegistry( features, BuildMaterial() );
	}

	static ControlFeature BuildFeature( string name, Type type, Effigy.Feature feature )
	{
		var category = CategoryFor( name );
		var ps = new List<ControlParam>();
		var usedKeys = new HashSet<string>( StringComparer.Ordinal );
		string Unique( string key )
		{
			var k = key.Length == 0 ? "param" : key;
			for ( int n = 2; !usedKeys.Add( k ); n++ ) k = key + n.ToString( CultureInfo.InvariantCulture );
			return k;
		}

		string kernelType = name;
		if ( feature is not null )
		{
			try { kernelType = feature.TypeName ?? name; } catch { }
			var advanced = new HashSet<IParam>( ReferenceEqualityComparer.Instance );
			foreach ( var a in feature.AdvancedParameters ) advanced.Add( a );

			foreach ( var param in feature.Parameters.Concat( feature.AdvancedParameters ).Distinct() )
			{
				var key = Unique( Norm( param.Label ) );
				bool adv = advanced.Contains( param );
				ps.Add( FromParam( name, key, param, adv ) );
			}

			foreach ( var f in type.GetFields( BindingFlags.Public | BindingFlags.Instance ) )
			{
				if ( f.Name is "Id" or "Name" or "Suppressed" or "Visible" ) continue;
				if ( f.GetValue( feature ) is IParam ) continue;
				var kind = FieldKind( f.FieldType );
				if ( kind is null ) continue;
				var key = Unique( Norm( f.Name ) );
				ps.Add( new ControlParam
				{
					Id = name + "." + key,
					Owner = name,
					Key = key,
					KernelLabel = f.Name,
					Kind = kind.Value,
					Source = ControlSource.FeatureField,
					Group = "Inputs",
					DraftLabel = SentenceCase( f.Name ),
					DraftTooltip = FieldTooltip( kind.Value ),
				} );
			}
		}

		return new ControlFeature
		{
			Name = name,
			ClassName = type.Name,
			KernelTypeName = kernelType,
			Category = category,
			DraftLabel = kernelType,
			DraftTooltip = $"{kernelType}: {category.ToLowerInvariant()} feature. Script: add {name} key=value …",
			Params = ps,
		};
	}

	static ControlParam FromParam( string owner, string key, IParam param, bool advanced )
	{
		ControlKind kind;
		string def = null, unit = null;
		double? min = null, max = null;
		string[] options = null;
		bool slider = true;

		switch ( param )
		{
			case FloatParam f:
				kind = ControlKind.Number;
				def = Inv( f.Value );
				if ( f.Min > float.MinValue ) min = f.Min;
				if ( f.Max < float.MaxValue ) max = f.Max;
				unit = f.Unit;
				break;
			case IntParam i:
				kind = ControlKind.Integer;
				def = i.Value.ToString( CultureInfo.InvariantCulture );
				if ( i.Min > int.MinValue ) min = i.Min;
				if ( i.Max < int.MaxValue ) max = i.Max;
				slider = i.Slider;
				break;
			case BoolParam b:
				kind = ControlKind.Toggle;
				def = b.Value ? "on" : "off";
				break;
			case ChoiceParam c:
				kind = ControlKind.Choice;
				options = c.Options?.ToArray() ?? Array.Empty<string>();
				def = options.Length > 0 ? c.Value : null;
				break;
			case Vec3Param v:
				kind = LooksLikeColour( param.Label ) ? ControlKind.Colour : ControlKind.Point;
				def = $"{Inv( v.Value.x )},{Inv( v.Value.y )},{Inv( v.Value.z )}";
				break;
			case StringParam s:
				kind = LooksLikeColour( param.Label ) ? ControlKind.Colour : ControlKind.Text;
				def = string.IsNullOrEmpty( s.Value ) ? null : s.Value;
				break;
			case BodySelectionParam:
				kind = ControlKind.Bodies;
				def = "";
				break;
			default:
				kind = ControlKind.Other;
				break;
		}

		var p = new ControlParam
		{
			Id = owner + "." + key,
			Owner = owner,
			Key = key,
			KernelLabel = param.Label,
			Kind = kind,
			Source = ControlSource.FeatureParameter,
			Default = def,
			Min = min,
			Max = max,
			Unit = unit,
			Options = options,
			Advanced = advanced,
			Slider = slider,
			Group = GroupFor( key, kind, advanced, slider ),
			DraftLabel = SentenceCase( param.Label ),
		};
		return WithTooltip( p );
	}

	static ControlParam WithTooltip( ControlParam p )
	{
		var sb = new StringBuilder();
		sb.Append( p.DraftLabel ).Append( ": " );
		switch ( p.Kind )
		{
			case ControlKind.Number:
			case ControlKind.Integer:
				sb.Append( p.Kind == ControlKind.Integer ? "whole number" : "number" );
				if ( p.Min.HasValue && p.Max.HasValue ) sb.Append( $" from {Inv( p.Min.Value )} to {Inv( p.Max.Value )}" );
				else if ( p.Min.HasValue ) sb.Append( $", at least {Inv( p.Min.Value )}" );
				else if ( p.Max.HasValue ) sb.Append( $", at most {Inv( p.Max.Value )}" );
				if ( !string.IsNullOrEmpty( p.Unit ) ) sb.Append( $" ({p.Unit})" );
				if ( p.Kind == ControlKind.Integer && !p.Slider ) sb.Append( ", an identifier rather than an amount" );
				break;
			case ControlKind.Toggle: sb.Append( "on or off" ); break;
			case ControlKind.Choice: sb.Append( "one of " ).Append( string.Join( ", ", p.Options ?? Array.Empty<string>() ) ); break;
			case ControlKind.Point: sb.Append( "a point x,y,z or @landmark" ); break;
			case ControlKind.Colour: sb.Append( "a colour" ); break;
			case ControlKind.Text: sb.Append( "text" ); break;
			case ControlKind.Bodies: sb.Append( "which bodies (empty = every body)" ); break;
			default: sb.Append( p.Kind.ToString().ToLowerInvariant() ); break;
		}
		if ( !string.IsNullOrEmpty( p.Default ) ) sb.Append( $". Default {p.Default}" );
		sb.Append( '.' );
		return new ControlParam
		{
			Id = p.Id, Owner = p.Owner, Key = p.Key, KernelLabel = p.KernelLabel, Kind = p.Kind, Source = p.Source,
			Default = p.Default, Min = p.Min, Max = p.Max, Unit = p.Unit, Options = p.Options, Advanced = p.Advanced,
			Slider = p.Slider, Group = p.Group, DraftLabel = p.DraftLabel, DraftTooltip = sb.ToString(),
		};
	}

	static bool LooksLikeColour( string label )
	{
		var n = Norm( label );
		return n.Contains( "colour" ) || n.Contains( "color" ) || n.Contains( "tint" );
	}

	static ControlKind? FieldKind( Type t ) =>
		t == typeof( List<Vec3> ) ? ControlKind.Points
		: t == typeof( List<Vec2> ) ? ControlKind.Pairs
		: t == typeof( string ) ? ControlKind.FeatureRef
		: t == typeof( List<string> ) ? ControlKind.FeatureRefs
		: t == typeof( List<FaceRef> ) ? ControlKind.Faces
		: t == typeof( List<EdgeRef> ) ? ControlKind.Edges
		: t == typeof( List<PaintStroke> ) ? ControlKind.Strokes
		: t == typeof( List<int> ) ? ControlKind.Indices
		: null;

	static string FieldTooltip( ControlKind kind ) => kind switch
	{
		ControlKind.Points => "Points x,y,z;x,y,z;… (landmarks allowed).",
		ControlKind.Pairs => "Pairs a,b;a,b;…",
		ControlKind.FeatureRef => "A feature name.",
		ControlKind.FeatureRefs => "Feature names, comma-separated.",
		ControlKind.Faces => "Faces: @Body.top, normal:up, box:…, sharp:>35deg, index:… (separate with ;).",
		ControlKind.Edges => "Edges: @Body.top, normal:up, box:…, sharp:>35deg, index:… (separate with ;).",
		ControlKind.Strokes => "Paint strokes (separate with ;).",
		ControlKind.Indices => "Indices 1,2,5 or a face selection.",
		_ => kind.ToString(),
	};

	/// <summary>DRAFT grouping heuristic inside a feature.</summary>
	static string GroupFor( string key, ControlKind kind, bool advanced, bool slider )
	{
		if ( key.EndsWith( "slot" ) || ( kind == ControlKind.Integer && !slider ) ) return "Material slots";
		if ( advanced ) return "Advanced";
		if ( kind == ControlKind.Bodies ) return "Targets";
		if ( kind == ControlKind.Point || key is "across" or "up" or "angle" or "side" or "where" or "faces" ) return "Placement";
		if ( kind == ControlKind.Colour ) return "Colour";
		if ( key.Contains( "seed" ) ) return "Variation";
		return "Main";
	}

	/// <summary>DRAFT feature categories by script name. Unlisted names fall back to keyword rules.</summary>
	static readonly (string Category, string[] Names)[] CategoryTable =
	{
		("Character", new[] { "BodyFromBones", "Head", "Ear", "Eye", "Mouth", "MuscleBulge", "Tendons", "FaceSkin", "ExpressionPack", "Wearer", "Viewmodel", "Footprint", "Asymmetry" }),
		("Rigging & shape keys", new[] { "Bind", "Skin", "CorrectiveShapeKey", "ShapeKey", "ShapeKeyEnd", "ShapeKeySplit" }),
		("Hair & fur", new[] { "Hair", "Fur", "Strands", "Foliage" }),
		("Clothing", new[] { "Garment", "DraftPanels", "PatternPanels", "PatternStitch", "Drape", "Collar", "CollarStand", "Cuff", "Dart", "DoubleFoldHem", "Pleat", "Pocket", "Piping", "Topstitch", "ButtonPlacket", "Fastener", "Padding", "Seam", "Wear", "Glow" }),
		("Materials", new[] { "FleshMaterial", "FaceMaterial", "Fabric", "Paint", "EdgeWear", "Wound", "UVProject", "VdmCapture", "VdmStamp" }),
		("Sculpt", new[] { "Sculpt", "Stroke", "Detail", "Inflate2D", "InflateTextureProjection", "Blob", "Barnacle", "Groove", "Knurl" }),
		("Particles", new[] { "Emitter", "Particle", "ParticleBake", "ParticleFx", "ParticlePreset", "ParticleSim", "Trail" }),
		("Deform", new[] { "CageDeform", "CurveDeform", "Lattice", "Shrinkwrap", "PressureBalloon", "SimpleDeform", "Transform", "MoveFace" }),
		("Mesh", new[] { "MeshEdit", "MeshFilter", "EditOp", "DeleteFace", "Cleanup", "Remesh", "Subdivide", "CreaseSubdivide", "FaceCheck" }),
		("Pattern & array", new[] { "CircularPattern", "CurvePattern", "LinearPattern", "Mirror", "Scatter", "PathMark" }),
		("Assembly & import", new[] { "Assembly", "Import", "Part", "Hardware", "GeoNodes", "Fracture", "ShadowBox" }),
	};

	static string CategoryFor( string name )
	{
		foreach ( var (cat, names) in CategoryTable )
			if ( names.Contains( name, StringComparer.Ordinal ) ) return cat;
		// Anything new the kernel grows lands here until pooh files it.
		var n = name.ToLowerInvariant();
		if ( n.StartsWith( "particle" ) ) return "Particles";
		if ( n.Contains( "shapekey" ) ) return "Rigging & shape keys";
		if ( n.Contains( "pattern" ) ) return "Pattern & array";
		return "Modelling";
	}

	static ControlFeature BuildMaterial()
	{
		var ps = new List<ControlParam>();
		MaterialDescriptor sample = null;
		try { sample = new MaterialDescriptor( "registry" ); } catch { }
		foreach ( var prop in typeof( MaterialDescriptor ).GetProperties( BindingFlags.Public | BindingFlags.Instance ).OrderBy( p => p.MetadataToken ) )
		{
			if ( !prop.CanWrite ) continue; // init-only setters count; computed aliases do not
			var t = prop.PropertyType;
			ControlKind kind;
			if ( t == typeof( float ) || t == typeof( double ) ) kind = ControlKind.Number;
			else if ( t == typeof( int ) ) kind = ControlKind.Integer;
			else if ( t == typeof( bool ) ) kind = ControlKind.Toggle;
			else if ( t == typeof( Vec3 ) || t == typeof( Vec4 ) ) kind = prop.Name.Contains( "Color" ) || prop.Name.Contains( "Tint" ) || prop.Name.Contains( "Factor" ) ? ControlKind.Colour : ControlKind.Point;
			else if ( t.IsEnum ) kind = ControlKind.Choice;
			else continue;

			object value = null;
			try { value = sample is null ? null : prop.GetValue( sample ); } catch { }
			string def = value switch
			{
				null => null,
				float f => Inv( f ),
				double d => Inv( d ),
				int i => i.ToString( CultureInfo.InvariantCulture ),
				bool b => b ? "on" : "off",
				Vec3 v => $"{Inv( v.x )},{Inv( v.y )},{Inv( v.z )}",
				Vec4 v4 => $"{Inv( v4.x )},{Inv( v4.y )},{Inv( v4.z )},{Inv( v4.w )}",
				_ => value.ToString(),
			};
			// "RoughnessFactor" is keyed "roughness": the word a person searches for and types.
			var key = Norm( prop.Name.EndsWith( "Factor" ) ? prop.Name[..^6] : prop.Name );
			var p = new ControlParam
			{
				Id = MaterialOwner + "." + prop.Name,
				Owner = MaterialOwner,
				Key = key,
				KernelLabel = prop.Name,
				Kind = kind,
				Source = ControlSource.MaterialDescriptor,
				Default = def,
				Options = t.IsEnum ? Enum.GetNames( t ) : null,
				Group = prop.Name.StartsWith( "Ps1" ) ? "PS1 look" : prop.Name.StartsWith( "Look" ) ? "Stylised look" : "PBR lobes",
				DraftLabel = SentenceCase( prop.Name.Replace( "Factor", "" ) ),
			};
			ps.Add( WithTooltip( p ) );
		}

		return new ControlFeature
		{
			Name = MaterialOwner,
			ClassName = nameof( MaterialDescriptor ),
			KernelTypeName = "Material",
			Category = "Materials",
			DraftLabel = "Material",
			DraftTooltip = "Material lobes the renderer reads (core MaterialDescriptor). Not a kernel feature.",
			Params = ps,
		};
	}
}
