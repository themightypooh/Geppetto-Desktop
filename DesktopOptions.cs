using System;
using System.Globalization;

namespace Marionette.Desktop;

public sealed class DesktopOptions
{
	public string Model = "/home/poo/games/projects/deep_sentance/Assets/models/player/player_suit.fbx";
	public string Clip, Screenshot;
	public int Frames, Width = 1600, Height = 900;
	public bool Stats, Animate, ListBones, Iso, NoVsync, Help, Headless;
	public float Exposure = 1f;
	public float? Time;
	public const string Usage = "Geppetto: [--model file.effigy|file.fbx] [--clip file.riganim] [--animate] [--time seconds] [--frames N] [--size WxH] [--screenshot file.png] [--stats] [--no-vsync] [--list-bones] [--iso] [--headless] [--exposure factor]";

	public static DesktopOptions Parse( string[] args )
	{
		var result = new DesktopOptions();
		for ( int i = 0; i < args.Length; i++ )
		{
			string flag = args[i];
			string Value()
			{
				if ( i + 1 >= args.Length || args[i + 1].StartsWith( "--", StringComparison.Ordinal ) )
					throw new ArgumentException( $"{flag} needs a value." );
				return args[++i];
			}
			int Positive( string value ) => int.TryParse( value, out int n ) && n > 0 ? n
				: throw new ArgumentException( $"{flag} requires positive integers." );
			switch ( flag )
			{
				case "--model": result.Model = Value(); break;
				case "--clip": result.Clip = Value(); break;
				case "--screenshot": result.Screenshot = Value(); break;
				case "--frames": result.Frames = Positive( Value() ); break;
				case "--size":
					var size = Value().Split( 'x' );
					if ( size.Length != 2 ) throw new ArgumentException( "--size must be WxH." );
					result.Width = Positive( size[0] ); result.Height = Positive( size[1] );
					if ( result.Width > 8192 || result.Height > 8192 ) throw new ArgumentException( "--size is limited to 8192 per side." );
					break;
				case "--time":
					if ( !float.TryParse( Value(), NumberStyles.Float, CultureInfo.InvariantCulture, out float time ) || !float.IsFinite( time ) || time < 0 )
						throw new ArgumentException( "--time requires finite, nonnegative seconds." );
					result.Time = time;
					break;
				case "--stats": result.Stats = true; break;
				case "--headless": result.Headless = true; break;
				case "--exposure":
					if ( !float.TryParse( Value(), NumberStyles.Float, CultureInfo.InvariantCulture, out float exposure ) || !float.IsFinite( exposure ) || exposure <= 0 || exposure > 16 )
						throw new ArgumentException( "--exposure must be a finite factor in (0,16]." );
					result.Exposure = exposure;
					break;
				case "--animate": result.Animate = true; break;
				case "--list-bones": result.ListBones = true; break;
				case "--iso": result.Iso = true; break;
				case "--no-vsync": result.NoVsync = true; break;
				case "--help": result.Help = true; break;
				default: throw new ArgumentException( $"Unknown option '{flag}'. {Usage}" );
			}
		}
		return result;
	}
}
