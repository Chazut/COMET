using Comet.Core;

internal static class ReadyStanceTests
{
    public static int Run()
    {
        int n = 0;
        void C(bool v, string m)
        {
            n++;
            if (!v)
                throw new Exception("ReadyStance: " + m);
        }
        var c = new ReadyStanceConfig();
        C(c.OrdinaryDuration == 25, "source default");
        var p = new ReadyStance();
        C(!p.Evaluate(10, true, "", c), "no combat yet");
        p.ObserveCombatAge(100, 5, c);
        C(p.Deadline == 120 && p.Evaluate(100, true, "", c), "25 seconds from last combat tick");
        C(!p.Evaluate(101, false, "", c), "native decision not forced");
        C(
            !p.Observe(101, 101, "heard", "x", 1, false, c) && p.Deadline == 120,
            "sounds cannot extend window"
        );
        C(
            p.Evaluate(120, true, "", c) && !p.Evaluate(120.001, true, "", c),
            "source strict greater-than boundary"
        );
        p.ObserveCombatAge(130, 0, c);
        C(p.Deadline == 155, "new combat resets timestamp");
        c.Enabled = false;
        C(!p.Evaluate(131, true, "", c), "disabled");
        p.Clear("raid ended");
        C(!p.Pending(132), "raid cleanup");
        return n;
    }
}
