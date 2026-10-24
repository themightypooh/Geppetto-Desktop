using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Effigy;

namespace Marionette.Desktop;

/// <summary>
/// The viewer side of the Face Poser panel: one pane's flex controller settings over the model it
/// shows. <c>face …</c> serve commands land here; the result is morph weights on the mesh (the renderer
/// deforms by them) and a <c>face</c> event the chest's panel draws its sliders from.
///
/// Settings are kept by controller name, so they survive the live model being replaced by the next
/// revision of a build: the new mesh gets the same face as long as it has the keys.
/// </summary>
public sealed class FacePoserService
{
	FaceRig _rig = new();
	string _signature = "";
	string _model;
	readonly Dictionary<string, FaceRig.Setting> _settings = new( StringComparer.OrdinalIgnoreCase );
	float _scale = 1;
	string _preset;
	readonly Random _random = new();

	/// <summary>
	/// The morph document this pane edits, when the pane has one. ServeMode builds this service
	/// per pane over a viewport mesh, so it is null unless the host attaches a
	/// <see cref="MorphEditService"/> for the document — <c>symmetrize</c>/<c>split</c> then fail
	/// clearly instead of pretending to edit targets. Attaching it in ServeMode is a follow-up
	/// for that file's owner, not something this service can do on its own (it never sees the
	/// PolyMesh, only the viewport mesh).
	/// </summary>
	public MorphEditService Morphs { get; set; }

	public bool HasRig => _rig.Controllers.Count > 0;

	static IEnumerable<string> MorphNames( ViewportMesh mesh ) =>
		mesh?.Morphs?.TargetLookup.Keys ?? Enumerable.Empty<string>();

	/// <summary>Re-read the rig when the mesh's keys or the model path changed. True when it did.</summary>
	public bool Sync( ViewportMesh mesh, string model )
	{
		var names = MorphNames( mesh ).OrderBy( n => n, StringComparer.Ordinal ).ToList();
		var signature = model + "|" + string.Join( ",", names );
		if ( signature == _signature ) return false;
		_signature = signature; _model = model;
		_rig = FaceRig.For( string.IsNullOrEmpty( model ) ? null : model, names );
		return true;
	}

	/// <summary>Put the current face on <paramref name="mesh"/>.</summary>
	public void Apply( ViewportMesh mesh )
	{
		if ( mesh is null ) return;
		foreach ( var (k, v) in _rig.Evaluate( _settings, _scale ) )
			if ( v > 1e-5f ) mesh.MorphWeights[k] = v; else mesh.MorphWeights.Remove( k );
	}

	/// <summary>Run one <c>face</c> command; returns the event to emit.</summary>
	public JsonObject Command( string arg, ViewportMesh mesh, string model )
	{
		Sync( mesh, model );
		var p = arg.Split( ' ', StringSplitOptions.RemoveEmptyEntries );
		string message = null;
		float F( int i ) => float.Parse( p[i], CultureInfo.InvariantCulture );
		switch ( p.Length == 0 ? "" : p[0] )
		{
			case "": break;
			case "set":                                   // set NAME VALUE [BALANCE]
			{
				if ( p.Length < 3 ) throw new ArgumentException( "face set needs a controller and a value: face set smile 0.8 [balance 0..1]" );
				var c = _rig.Find( p[1] ) ?? throw new ArgumentException( NoController( p[1] ) );
				var old = _settings.TryGetValue( c.Name, out var s ) ? s : new FaceRig.Setting( 0 );
				_settings[c.Name] = new FaceRig.Setting( Math.Clamp( F( 2 ), c.Min, c.Max ), p.Length > 3 ? Math.Clamp( F( 3 ), 0, 1 ) : old.Balance );
				_preset = null;
				break;
			}
			case "preset":
			{
				var name = p.Length > 1 ? p[1] : throw new ArgumentException( "face preset needs a name." );
				var key = _rig.Presets.Keys.FirstOrDefault( k => string.Equals( k, name, StringComparison.OrdinalIgnoreCase ) )
					?? throw new ArgumentException( $"No preset '{name}'. Presets: {string.Join( ", ", _rig.Presets.Keys )}." );
				_settings.Clear();
				foreach ( var (c, s) in _rig.Presets[key] ) _settings[c] = s;
				_preset = key;
				break;
			}
			case "zero": _settings.Clear(); _preset = null; break;
			case "random":                                // a random face, kept mild so it reads as a face
				_settings.Clear();
				foreach ( var c in _rig.Controllers.Where( _ => _random.NextDouble() < 0.35 ) )
					_settings[c.Name] = new FaceRig.Setting( (float)Math.Round( (c.Bipolar ? _random.NextDouble() * 2 - 1 : _random.NextDouble()) * 0.7, 2 ), 0.5f );
				_preset = null;
				break;
			case "scale":                                 // Face Poser's flex scale: everything at once
				if ( p.Length < 2 ) throw new ArgumentException( "face scale needs a number, e.g. face scale 1.5" );
				_scale = Math.Clamp( F( 1 ), -5, 5 );   // GMod's range: negative flips every shape
				break;
			case "save":
			{
				var name = p.Length > 1 ? p[1] : throw new ArgumentException( "face save needs a preset name." );
				if ( string.IsNullOrEmpty( _model ) ) throw new InvalidOperationException( "This pane shows no saved model, so there is no file to keep presets next to." );
				FaceRig.SavePreset( _model, name, _settings );
				_signature = "";                          // pick the new preset up
				Sync( mesh, model );
				_preset = name;
				message = $"saved preset '{name}' to {Path.GetFileName( FaceRig.SidecarPath( _model ) )}";
				break;
			}
			case "export":                                // export PATH.vmdl_prefab: the presets as MorphFrames
			{
				var path = p.Length > 1 ? string.Join( ' ', p.Skip( 1 ) ).Trim( '"' )
					: !string.IsNullOrEmpty( _model ) ? Path.Combine( Path.GetDirectoryName( Path.GetFullPath( _model ) ) ?? ".", Path.GetFileNameWithoutExtension( _model ) + "_faces.vmdl_prefab" )
					: throw new ArgumentException( "face export needs a .vmdl_prefab path (this pane shows no saved model to put it next to)." );
				if ( _rig.Presets.Count == 0 ) throw new InvalidOperationException( "No presets to export." );
				_rig.WritePrefab( path );
				message = $"wrote {_rig.Presets.Count} preset(s) to {path}";
				break;
			}
			case "symmetrize":                           // symmetrize TARGET [NEWNAME]
			{
				if ( p.Length < 2 ) throw new ArgumentException( "face symmetrize needs a morph target: face symmetrize smile [symmetric]" );
				var morphs = Morphs ?? throw new InvalidOperationException( "This pane has no morph document attached, so face symmetrize cannot edit targets here." );
				morphs.SymmetrizeTarget( p[1], p.Length > 2 ? p[2] : null );
				message = p.Length > 2 ? $"symmetrized '{p[1]}' as '{p[2]}'" : $"symmetrized '{p[1]}'";
				break;
			}
			case "split":                                   // split TARGET LEFT RIGHT [WIDTH]
			{
				if ( p.Length < 4 ) throw new ArgumentException( "face split needs a target and two names: face split smile smile_L smile_R [width]" );
				var morphs = Morphs ?? throw new InvalidOperationException( "This pane has no morph document attached, so face split cannot edit targets here." );
				morphs.SplitStereoTarget( p[1], p[2], p[3], p.Length > 4 ? F( 4 ) : 0f );
				message = $"split '{p[1]}' into '{p[2]}' and '{p[3]}'";
				break;
			}
			default: throw new ArgumentException( $"face: no command '{p[0]}' (set, preset, zero, random, scale, save, export, symmetrize, split)." );
		}
		Apply( mesh );
		return Snapshot( message );
	}

	string NoController( string name ) =>
		_rig.Controllers.Count == 0
			? "This model has no face rig keys yet. Sculpt shape keys named after the controllers (jaw_drop, corner_puller split into _L/_R, …) and the sliders appear."
			: $"No controller '{name}'. Controllers: {string.Join( ", ", _rig.Controllers.Select( c => c.Name ) )}.";

	public JsonObject Snapshot( string message = null )
	{
		var controllers = new JsonArray();
		foreach ( var c in _rig.Controllers )
		{
			var s = _settings.TryGetValue( c.Name, out var v ) ? v : new FaceRig.Setting( 0 );
			controllers.Add( new JsonObject
			{
				["name"] = c.Name, ["group"] = c.Group, ["tip"] = c.Tip,
				["stereo"] = c.Stereo, ["min"] = c.Min, ["max"] = c.Max,
				["value"] = s.Value, ["balance"] = s.Balance,
			} );
		}
		var missing = FaceRig.Default().ExpectedKeys().Except( _rig.ExpectedKeys() ).Count();
		var ev = new JsonObject
		{
			["ev"] = "face",
			["groups"] = new JsonArray( FaceRig.Groups.Where( g => _rig.Controllers.Any( c => c.Group == g ) ).Select( g => (JsonNode)g ).ToArray() ),
			["controllers"] = controllers,
			["presets"] = new JsonArray( _rig.Presets.Keys.Select( k => (JsonNode)k ).ToArray() ),
			["preset"] = _preset,
			["scale"] = _scale,
			["missing"] = missing,
			["saveable"] = !string.IsNullOrEmpty( _model ),
		};
		if ( message is not null ) ev["message"] = message;
		return ev;
	}
}
