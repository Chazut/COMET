using System;
using System.Linq;
using Comet.Core;

// Behavioral contract: zzap fbb86b83 (see SOURCE_BASELINE.md).
internal static class SilentSearchTests
{
    public static int Run()
    {
        int n = 0;
        void Check(bool ok, string why)
        {
            n++;
            if (!ok)
                throw new Exception("SilentSearch: " + why);
        }

        var c = new SilentSearchConfig();
        var f = new SilentSearchFrame
        {
            Now = 20,
            HeardAt = 5,
            Target = "e",
            Personality = "Chad",
            ContactAge = 15,
        };
        Check(SilentSearchPolicy.Evaluate(f, c).Eligible, "source 15 seconds");
        f.Now = 19.9;
        Check(!SilentSearchPolicy.Evaluate(f, c).Eligible, "not before source delay");
        f.Now = 20;
        f.Path = false;
        f.Failed = true;
        f.Unsafe = true;
        f.Personal = false;
        Check(
            SilentSearchPolicy.Evaluate(f, c).Eligible,
            "no extra path/failed/origin veto before native search"
        );
        f.Personality = "Coward";
        Check(!SilentSearchPolicy.Evaluate(f, c).Eligible, "source coward exclusion");
        f.Personality = "Normal";
        f.Arrived = true;
        Check(!SilentSearchPolicy.Evaluate(f, c).Eligible, "already searched known point");
        f.Arrived = false;
        f.TimeBeforeSearch = 10;
        Check(
            !SilentSearchPolicy.Evaluate(f, c).Eligible,
            "leave already permitted native search alone"
        );

        return n;
    }
}
