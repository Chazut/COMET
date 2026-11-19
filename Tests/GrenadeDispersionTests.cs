using Comet.Core;

internal static class GrenadeDispersionTests
{
    public static int Run()
    {
        int n = 0;
        void C(bool value, string why)
        {
            n++;
            if (!value)
                throw new Exception("G06: " + why);
        }
        var c = new GrenadeDispersionConfig();
        c.Validate();
        C(c.Enabled && c.Spacing == 3 && c.MemberRange == 25, "approved defaults");
        foreach (
            var change in new Action<GrenadeDispersionConfig>[]
            {
                x => x.Spacing = 0,
                x => x.Spacing = 9,
                x => x.Spacing = double.NaN,
                x => x.MemberRange = 4,
                x => x.MemberRange = 61,
                x => x.MemberRange = double.PositiveInfinity,
            }
        )
        {
            var bad = new GrenadeDispersionConfig();
            change(bad);
            try
            {
                bad.Validate();
                C(false, "invalid accepted");
            }
            catch (ArgumentException)
            {
                C(true, "invalid rejected");
            }
        }
        var old = Newtonsoft.Json.JsonConvert.DeserializeObject<CometConfig>("{}")!;
        old.Validate();
        C(old.GrenadeDispersion.Spacing == 3, "old configs default safely");
        old.GrenadeDispersion.Spacing = 4;
        old.GrenadeDispersion.MemberRange = 30;
        var restored = Newtonsoft.Json.JsonConvert.DeserializeObject<CometConfig>(
            Newtonsoft.Json.JsonConvert.SerializeObject(old)
        )!;
        C(
            restored.GrenadeDispersion.Spacing == 4 && restored.GrenadeDispersion.MemberRange == 30,
            "snapshot roundtrip"
        );
        var peers = new[]
        {
            new GrenadeDispersionLease
            {
                Profile = "first",
                Goal = new(10, 0, 0),
                Expires = 11,
            },
            new GrenadeDispersionLease
            {
                Profile = "second",
                Goal = new(20, 0, 0),
                Expires = 11,
            },
        };
        var hit = GrenadeDispersionRules.Assess(new(11, 0, 0), peers, 3, 10);
        C(
            hit.Conflict && hit.Peer == "first" && hit.Nearest == 1 && hit.Peers == 2,
            "closest live reservation"
        );
        C(
            !GrenadeDispersionRules.Assess(new(13, 0, 0), peers, 3, 10).Conflict,
            "spacing boundary accepted"
        );
        C(
            !GrenadeDispersionRules.Assess(new(10, 3, 0), peers, 8, 10).Conflict,
            "other floor never blocks even large spacing"
        );
        C(
            !GrenadeDispersionRules.Assess(new(10, 0, 0), peers, 3, 11).Conflict,
            "exact lease expiry"
        );
        C(
            GrenadeDispersionRules.Nearby(new(0, 0, 0), new(25, 0, 0), 25)
                && !GrenadeDispersionRules.Nearby(new(0, 0, 0), new(25, 1, 0), 25),
            "3D member range boundary"
        );
        C(
            !GrenadeDispersionRules.Nearby(new(double.NaN, 0, 0), new(0, 0, 0), 25),
            "invalid position excluded"
        );
        Console.WriteLine($"G06 policy: {n} checks passed.");
        return n;
    }
}
