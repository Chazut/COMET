using Comet.Core;

internal static class SquadVoiceTests
{
    public static int Run()
    {
        int n = 0;
        void C(bool v, string m)
        {
            n++;
            if (!v)
                throw new Exception(m);
        }
        var c = new SquadVoiceConfig();
        C(c.Mode == "Disabled", "zzap default mutes PMC squad callouts");
        var p = new SquadVoicePolicy();
        int rolls = 0;
        double Roll()
        {
            rolls++;
            return 99;
        }
        C(!p.Evaluate(new(), c, "a", "s", 1, Roll), "muted");
        C(
            !p.Evaluate(new() { Urgent = true }, c, "a", "s", 1, Roll),
            "source mute includes alerts"
        );
        c.Mode = "Normal";
        var t = new SquadVoiceTrace();
        C(p.Evaluate(t, c, "a", "s", 1, Roll), "native on");
        p.Played(t, "a", "s", 1);
        C(p.Evaluate(new(), c, "a", "s", 1.001, Roll) && rolls == 0, "no COMET chance or cooldown");
        c.Mode = "Reduced";
        C(p.Evaluate(new(), c, "a", "s", 1.001, Roll), "legacy reduced maps to native");
        p.Clear();
        return n;
    }
}
