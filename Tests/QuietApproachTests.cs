using System;
using System.Linq;
using Comet.Core;

// Behavioral contract: zzap fbb86b83 (see SOURCE_BASELINE.md).
internal static class QuietApproachTests
{
    public static int Run()
    {
        int n = 0;
        void Check(bool ok, string why)
        {
            n++;
            if (!ok)
                throw new Exception("QuietApproach: " + why);
        }

        var c = new QuietApproachConfig();
        int rolls = 0;
        var p = new QuietApproach(c, () =>
        {
            rolls++;
            return .1;
        });
        var f = new QuietFrame
        {
            Now = 10,
            Target = "e",
            Enabled = true,
            Scoped = true,
            Personality = "Normal",
            Path = 20,
            SinceSeen = 10,
            SinceFire = 10,
        };
        Check(p.Evaluate(f).State == "walk", "source quiet walk");
        f.Path = 8;
        f.Now = 11;
        Check(
            p.Evaluate(f).State == "crouch" && rolls == 1,
            "same target keeps roll and crouches close"
        );
        f.Now = 31;
        Check(!p.Evaluate(f).Active, "source 20-second budget");
        f.Target = "other";
        Check(p.Evaluate(f).Active && rolls == 2, "source new target receives new roll");
        f.Visible = true;
        Check(!p.Evaluate(f).Active, "visible combat ends quiet approach");
        f.Visible = false;
        f.SinceFire = 1;
        Check(!p.Evaluate(f).Active, "source five-second fire delay");
        p = new(c, () => .99);
        f.SinceFire = 10;
        f.LootHeard = true;
        Check(p.Evaluate(f).Active, "heard looting guarantees source quiet approach");

        return n;
    }
}
