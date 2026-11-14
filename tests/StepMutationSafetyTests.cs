using Effigy;
using Marionette.Desktop;

internal static class StepMutationSafetyTests
{
    public static void Register(TestRegistry registry, TestSettings settings)
    {
        registry.Add("step-mutation-safety", "all editing families and new verbs consume STEP", () =>
        {
            foreach (string verb in ("add edit set remove remedy material facematerial refimage refmark refstep trace "
                + "bone chain subdivide twist mirror rig cables bind skin weight sculpt fix retopo transfer reproject "
                + "corrective pivot var rebuild attachment hitbox bodygroup ragdoll ragdoll_limit lod_chain fit_profile "
                + "texpaint load paintmaps genmaps facerig playermodel particlecue particles bendy dqs ikfk "
                + "twiststations fan bulge deltamush future_edit_verb").Split(' '))
            {
                Require(BuildScript.Mutates(verb), verb + " bypasses STEP");
                Require(BuildScript.Mutates(verb.ToUpperInvariant()), verb + " bypasses STEP in upper case");
            }
        });
        registry.Add("step-mutation-safety", "captions and known reports do not consume STEP", () =>
        {
            Require(!BuildScript.Mutates(null) && !BuildScript.Mutates(""), "empty input consumes STEP");
            foreach (string verb in "echo describe measure at measurebody fitreport distance symmetry clearance topology section validate render turnaround uvcheck match save export exportvalidate".Split(' '))
                Require(!BuildScript.Mutates(verb), verb + " consumes STEP");
        });
    }

    static void Require(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
    }
}
