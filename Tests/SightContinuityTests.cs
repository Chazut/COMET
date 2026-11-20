using System;
using System.Linq;
using Comet.Core;

// Behavioral contract: zzap fbb86b83 (see SOURCE_BASELINE.md).
internal static class SightContinuityTests
{
    public static int Run()
    {
        int n = 0;
        void Check(bool ok, string why)
        {
            n++;
            if (!ok)
                throw new Exception("SightContinuity: " + why);
        }

        var c = new CometConfig();
        Check(
            c.SightContinuity.CloseDuration == .8 && c.SightContinuity.MediumDuration == 1.5,
            "source durations"
        );
        foreach (var distance in new[] { 5d, 15d })
        {
            var p = new SightContinuity(c);
            var f = new SightFrame
            {
                Now = 10,
                Target = "e",
                Action = "Shoot",
                Eligible = true,
                Visible = true,
                Observed = true,
                Distance = distance,
                X = 4,
                Y = 2,
                Z = 1,
            };
            p.Evaluate(f);
            f.Visible = false;
            f.Now = 10.1;
            f.SeenAge = .1;
            f.X = 999;
            var hold = p.Evaluate(f);
            Check(
                hold != null && hold.X == 4,
                "single visible sample is enough; hidden transform ignored"
            );
            f.Hurt = f.LowAmmo = f.Suppressed = true;
            f.Now = 10.2;
            f.SeenAge = .2;
            Check(p.Evaluate(f) != null, "no added health/ammo/suppression abort");
            f.SeenAge = 2;
            f.Now = 12;
            Check(p.Evaluate(f) == null, "source sight window expires");
        }
        var q = new SightContinuity(c);
        var a = new SightFrame
        {
            Now = 10,
            Target = "e",
            Action = "DogFight",
            Jiggling = true,
            Eligible = true,
            Visible = true,
            Observed = true,
            Distance = 8,
        };
        q.Evaluate(a);
        a.Now = 11;
        a.SeenAge = 1;
        a.Visible = false;
        Check(q.Evaluate(a) != null, "jiggle has 1.2s continuity");
        a.OtherThreat = true;
        Check(q.Evaluate(a) == null, "source competing threat releases");
        a.OtherThreat = false;
        a.Visible = true;
        a.Jiggling = false;
        a.Action = "Push";
        a.Now = 12;
        a.SeenAge = 0;
        a.X = 20;
        Check(q.Evaluate(a) == null, "reacquisition closes the old hold");
        a.Visible = false;
        a.X = 100;
        a.Now += .1;
        a.SeenAge = .1;
        var nextHold = q.Evaluate(a);
        Check(nextHold?.Action == "Push" && nextHold.X == 20, "next occlusion preserves the new action and last visible position");

        return n;
    }
}
