using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Effigy;

public static partial class BuildScript
{
	public const string RefImageUsage =
		"refimage front|side|top PATH.png [height=72] [offset=x,y] [depth=0] [opacity=0.5] [visible=1], "
		+ "refimage VIEW key=value… to change the one already up, refimage clear [VIEW]";

	public static readonly string[] RefImageViews = { "front", "side", "top" };

	/// <summary>
	/// refimage side fish.png height=30 offset=0,4 opacity=0.5 — stand a picture in the viewport to
	/// build against (<see cref="ReferenceImage"/>). One picture per view: a second line for the same
	/// view replaces the first, so re-running a script never stacks copies. With no path it changes
	/// the one already on that view. The path is stored absolute, so the document still finds the
	/// picture when it is opened from somewhere else (a live run's revisions live in their own folder).
	/// </summary>
	static bool RefImage( PartStudio studio, List<string> args, string baseDir, Result r )
	{
		if ( args.Count == 0 )
			return Fail( r, "refimage needs a view and a picture: " + RefImageUsage );

		var view = args[0].ToLowerInvariant();

		if ( view == "calibrate" )
			return CalibrateVerb( studio, args.Skip( 1 ).ToList(), baseDir, r );

		if ( view == "fit" )
			return FitVerb( studio, args.Skip( 1 ).ToList(), baseDir, r );

		if ( view == "frame" )
			return FrameVerb( studio, args.Skip( 1 ).ToList(), baseDir, r );

		if ( view == "clear" )
		{
			var only = args.Count > 1 ? args[1].ToLowerInvariant() : null;
			int removed = studio.ReferenceImages.RemoveAll( i => only is null || i.View == only );
			r.Log.Add( removed == 0 ? "refimage clear: there were no reference images" : $"refimage clear: took down {removed}" );
			return true;
		}

		if ( view == "right" ) view = "side";

		if ( Array.IndexOf( RefImageViews, view ) < 0 )
			return Fail( r, $"refimage: no view called '{args[0]}'. Views: front, side, top." );

		var rest = args.Skip( 1 ).ToList();
		string path = rest.Count > 0 && !rest[0].Contains( '=' ) ? rest[0] : null;
		if ( path is not null ) rest.RemoveAt( 0 );
		var pairs = Pairs( rest );

		// Tag selects which picture in a view: default (no tag) is the untagged
		// one, so adding a tagged section picture never displaces the main one.
		var tagSel = pairs.GetValueOrDefault( "tag", "" );
		var image = studio.ReferenceImages.FirstOrDefault( i => i.View == view && (i.Tag ?? "") == tagSel );

		if ( path is null && image is null )
			return Fail( r, $"refimage {view}: there is no {view} picture up to change; give a path: " + RefImageUsage );

		if ( path is not null )
		{
			var full = Path.GetFullPath( Resolve( baseDir, path ) );

			if ( !File.Exists( full ) )
				return Fail( r, $"refimage: no picture at {full}." );

			var fresh = new ReferenceImage { View = view, Path = full, Tag = tagSel };

			// Replacing keeps the old placement unless the line says otherwise: a new photo of the
			// same thing usually goes where the last one was.
			if ( image is not null )
			{
				fresh.Height = image.Height; fresh.Offset = image.Offset; fresh.Depth = image.Depth; fresh.Kind = image.Kind;
				fresh.Opacity = image.Opacity; fresh.Visible = image.Visible;
				studio.ReferenceImages[studio.ReferenceImages.IndexOf( image )] = fresh;
			}
			else studio.ReferenceImages.Add( fresh );

			image = fresh;
		}

		foreach ( var (key, value) in pairs )
		{
			switch ( key )
			{
				case "height":
				case "size":
				{
					var h = Number( value );
					if ( !(h > 0) ) return Fail( r, $"refimage height must be above 0, not {value}." );
					image.Height = h; image.Calibrated = false;
					break;
				}

				case "offset":
				{
					var bits = value.Split( ',' );
					if ( bits.Length != 2 ) return Fail( r, $"refimage offset is two numbers x,y (along the picture, then up it), not '{value}'." );
					image.Offset = new Vec2( Number( bits[0] ), Number( bits[1] ) ); image.Calibrated = false;
					break;
				}

				case "depth": image.Depth = Number( value ); break;

				case "opacity":
				{
					var o = Number( value );
					if ( o < 0.05f || o > 1f ) return Fail( r, $"refimage opacity goes from 0.05 to 1, not {value} (visible=0 hides it)." );
					image.Opacity = o;
					break;
				}

				case "visible": image.Visible = Truthy( value ); break;

				case "kind":
				{
					var k = value.ToLowerInvariant();
					if ( k is not ("diagram" or "photo" or "unknown") ) return Fail( r, $"refimage kind is diagram, photo, or unknown, not '{value}'." );
					image.Kind = k;
					break;
				}

				case "tag": break;

				default:
					return Fail( r, $"refimage: no setting called '{key}'. " + RefImageUsage );
			}
		}

		r.Log.Add( $"refimage {view} {Path.GetFileName( image.Path )} height={image.Height:0.###} offset={image.Offset.x:0.###},{image.Offset.y:0.###} depth={image.Depth:0.###} opacity={image.Opacity:0.##}{(image.Visible ? "" : " hidden")}{(string.IsNullOrEmpty( image.Tag ) ? "" : $" tag={image.Tag}")} kind={image.Kind}{(image.Calibrated ? " calibrated" : "")}" );
		return true;
	}
}
