using System;

namespace Effigy;

/// <summary>
/// One authored particle cue: the numbers a runtime particle effect
/// needs, stored on the document — not simulated by Geppetto.
///
/// This is the bridge the kernel's Anim2D Particle event always pointed
/// at but never had: Anim2D.cs declares a Particle event as "fire/retime
/// an EXTERNAL particle effect by cue name; this clip never simulates
/// anything", and no script verb wrote one. A cue carries the emitter
/// numbers (count, speed range, life, size range) alongside the name and
/// frame, so a runtime effect chosen by cue name can actually be driven
/// from the document. Geppetto still simulates nothing — the runtime
/// owns the sim, exactly as Anim2D already declares.
///
/// Persistence: a `particlecue` line in the .effigy document (see
/// StudioDocument), one per cue, replaced by frame+cue name. The
/// `particlecue export` verb writes the same list as JSON for a
/// runtime that wants a sidecar instead of reading the document.
/// </summary>
public sealed class ParticleCue
{
	public int Frame;
	public string Cue = "";
	public int Count;
	public float SpeedMin, SpeedMax, Life, SizeMin, SizeMax;

	public string Describe() =>
		$"particlecue frame={Frame} cue={Cue} count={Count} " +
		$"speed={SpeedMin},{SpeedMax} life={Life} size={SizeMin},{SizeMax}";
}
