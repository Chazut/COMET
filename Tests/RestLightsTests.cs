using Comet.Core;

internal static class RestLightsTests
{
    public static int Run()
    {
        int n = 0;
        void C(bool v, string m)
        {
            n++;
            if (!v)
                throw new Exception("RestLights: " + m);
        }
        var c = new RestLightsConfig();
        C(c.HoldDelay == 2 && c.MaxSpeed == .5 && c.EpisodeInterval == 10, "source defaults");
        var p = new RestLightsPolicy();
        C(!p.Update(10, "idle", true, c), "idle begins");
        C(!p.Update(12, "different owner", true, c), "strict two second boundary");
        C(p.Update(12.01, "idle", true, c), "source idle elapsed despite owner change");
        p.Consume(12.01, c);
        C(
            !p.Update(20, "idle", true, c) && p.Update(22.02, "idle", true, c),
            "repeat after ten seconds in same pause"
        );
        C(
            !p.Update(23, "combat", false, c) && !p.Update(24, "idle", true, c),
            "moving/combat resets idle"
        );
        C(p.Update(26.01, "idle", true, c), "new pause");
        p.Release();
        C(p.Since < 0, "cleanup");
        return n;
    }
}
