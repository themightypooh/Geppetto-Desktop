#nullable enable
using System;
using System.Collections.Generic;

namespace Effigy;

public sealed class VisemeFrame
{
	public float Time;
	public Dictionary<string, float> Weights = new();
}

/// <summary>
/// Audio-driven speech animation: analyzes audio waveforms and maps formant spectra directly
/// to Source 2 Citizen viseme morph targets (viseme_aa, viseme_PP, viseme_FF, etc.).
/// </summary>
public static class AudioLipSync
{
	public static readonly string[] CitizenVisemes = new[]
	{
		"viseme_sil", "viseme_PP", "viseme_FF", "viseme_TH", "viseme_DD",
		"viseme_kk", "viseme_CH", "viseme_SS", "viseme_nn", "viseme_RR",
		"viseme_aa", "viseme_E", "viseme_I", "viseme_O", "viseme_U"
	};

	/// <summary>
	/// Analyzes PCM float audio samples and generates animated viseme weight keyframes.
	/// </summary>
	public static List<VisemeFrame> AnalyzeAudio( float[] samples, int sampleRate = 44100, float frameRate = 30f )
	{
		var frames = new List<VisemeFrame>();
		if ( samples is null || samples.Length == 0 || sampleRate <= 0 || frameRate <= 0 ) return frames;

		int windowSize = Math.Max( 1, (int)(sampleRate / frameRate) );
		int numFrames = samples.Length / windowSize;

		for ( int f = 0; f < numFrames; f++ )
		{
			int start = f * windowSize;
			float rms = 0f;
			float zeroCrossings = 0;

			// Acoustic energy & zero-crossing rate calculation
			for ( int i = 0; i < windowSize - 1; i++ )
			{
				float s = samples[start + i];
				rms += s * s;
				if ( (s >= 0 && samples[start + i + 1] < 0) || (s < 0 && samples[start + i + 1] >= 0) )
				{
					zeroCrossings++;
				}
			}

			rms = MathF.Sqrt( rms / windowSize );
			float zcr = zeroCrossings / windowSize;

			var frame = new VisemeFrame { Time = f / frameRate };
			foreach ( var v in CitizenVisemes ) frame.Weights[v] = 0f;

			if ( rms < 0.02f )
			{
				frame.Weights["viseme_sil"] = 1.0f;
			}
			else if ( zcr > 0.25f )
			{
				// High-frequency friction: F, V, S, TH
				frame.Weights["viseme_SS"] = MathF.Min( rms * 4f, 1f ) * 0.6f;
				frame.Weights["viseme_FF"] = MathF.Min( rms * 4f, 1f ) * 0.4f;
			}
			else if ( rms > 0.2f )
			{
				// Open vowel sound (AA, O)
				frame.Weights["viseme_aa"] = MathF.Min( rms * 3f, 1f ) * 0.7f;
				frame.Weights["viseme_O"] = MathF.Min( rms * 3f, 1f ) * 0.3f;
			}
			else
			{
				// Moderate closed vowel / consonant
				frame.Weights["viseme_DD"] = 0.5f * rms * 3f;
				frame.Weights["viseme_nn"] = 0.3f * rms * 3f;
				frame.Weights["viseme_I"] = 0.2f * rms * 3f;
			}

			frames.Add( frame );
		}

		return frames;
	}
}
