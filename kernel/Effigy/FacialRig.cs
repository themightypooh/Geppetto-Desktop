using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// Lane F: 16-viseme facial animation + FACS morph targets for voice lip-sync.
/// Exports the standard Valve/Source 2 viseme morph target names used by s&box.
/// </summary>
public static class FacialRig
{
	public static readonly string[] Visemes = new[]
	{
		"v_sil", "v_pp", "v_ff", "v_th", "v_dd", "v_kk", "v_ch", "v_ss",
		"v_nn", "v_rr", "v_aa", "v_e", "v_ih", "v_oh", "v_ou",
	};

	public static readonly string[] FacsExtras = new[] { "blink_left", "blink_right", "smile", "frown" };

	public static List<string> AllMorphNames()
	{
		var list = new List<string>( Visemes );
		list.AddRange( FacsExtras );
		return list;
	}

	/// <summary>Returns true if a material flag string should include F_MORPH_SUPPORTED 1.</summary>
	public static string MorphFlag() => "F_MORPH_SUPPORTED 1";
}
