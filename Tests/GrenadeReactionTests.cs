using Comet.Core;

internal static class GrenadeReactionTests
{
    public static int Run()
    {
        int n = 0;
        void C(bool v, string why)
        {
            n++;
            if (!v)
                throw new Exception("G05: " + why);
        }
        var c = new GrenadeReactionConfig();
        c.Validate();
        C(
            c.Enabled
                && c.DangerMargin == 1
                && c.ExitPadding == 4
                && c.ScatterDistance == 14
                && c.RetreatDistance == 22
                && c.Refresh == .1
                && c.Commitment == 1.5,
            "approved defaults"
        );
        foreach (
            var mutate in new Action<GrenadeReactionConfig>[]
            {
                v => v.DangerMargin = -1,
                v => v.ExitPadding = 0,
                v => v.ScatterDistance = double.NaN,
                v => v.RetreatDistance = double.PositiveInfinity,
                v => v.Refresh = .01,
                v => v.Commitment = 5,
            }
        )
        {
            var invalid = new GrenadeReactionConfig();
            mutate(invalid);
            try
            {
                invalid.Validate();
                C(false, "invalid config accepted");
            }
            catch (ArgumentException)
            {
                C(true, "invalid config rejected");
            }
        }
        var restored = Newtonsoft.Json.JsonConvert.DeserializeObject<CometConfig>("{}")!;
        restored.Validate();
        C(restored.GrenadeReaction.ScatterDistance == 14, "older configs acquire defaults");
        restored.GrenadeReaction.RetreatDistance = 25;
        var json = Newtonsoft.Json.JsonConvert.SerializeObject(restored);
        C(
            Newtonsoft
                .Json.JsonConvert.DeserializeObject<CometConfig>(json)!
                .GrenadeReaction.RetreatDistance == 25,
            "server snapshot roundtrip"
        );
        foreach (string source in new[] { "seen", "heard" })
            C(
                GrenadeReactionRules.PersonalContact(false, source, 2),
                "recent own contact " + source
            );
        C(
            !GrenadeReactionRules.PersonalContact(false, "shared-seen", 0)
                && !GrenadeReactionRules.PersonalContact(false, "seen", 3.1)
                && !GrenadeReactionRules.PersonalContact(true, "seen", double.NaN),
            "shared/stale/nonfinite contact excluded"
        );
        var pos = new DoorPoint(2, 0, 0);
        var blast = new DoorPoint(0, 0, 0);
        var away = GrenadeReactionRules.Direction(
            pos,
            blast,
            new(-1, 0, 0),
            new(-30, 0, 0),
            "Push"
        );
        C(away.X > 0, "known opponent behind blast does not pull escape toward it");
        C(
            GrenadeReactionRules.Direction(blast, blast, new(0, 0, 0), null, "Scatter").Length > 0,
            "zero separation deterministic direction"
        );
        var dangers = new[]
        {
            new OwnGrenadeEvidence { Position = blast, Radius = 7 },
            new OwnGrenadeEvidence { Position = new(20, 0, 0), Radius = 7 },
        };
        C(
            !GrenadeReactionRules.DestinationSafe(new(15, 0, 0), dangers, 4, (a, b) => false),
            "second hazard vetoes endpoint"
        );
        C(
            GrenadeReactionRules.DestinationSafe(new(15, 0, 20), dangers, 4, (a, b) => false),
            "safe displacement"
        );
        C(
            !GrenadeReactionRules.DestinationSafe(new(double.NaN, 0, 0), dangers, 4, (a, b) =>
                true
            ),
            "nonfinite destination rejected"
        );
        Console.WriteLine($"G05 policy: {n} checks passed.");
        return n;
    }
}
