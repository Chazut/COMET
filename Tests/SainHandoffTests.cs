using System;
using System.Linq;
using Comet.Core;

// Behavioral contract: zzap fbb86b83 (see SOURCE_BASELINE.md).
internal static class SainHandoffTests
{
    public static int Run()
    {
        int n = 0;
        void Check(bool ok, string why)
        {
            n++;
            if (!ok)
                throw new Exception("SainHandoff: " + why);
        }

        var c = new HandoffConfig();
        var p = new SainHandoff(c);
        var f = new HandoffCue { Target = "enemy", At = 1 };
        p.Accepted(1, "A01", "Search", f);
        Check(!p.Yielded, "accepted source decision");
        Check(p.Begin(2, "no candidate", "normal", f), "native handoff logged");
        Check(!p.Blocked(2, f), "no invented delay on normal native handoff");
        Check(p.ObserveNative(2, "Search", "None", "None"), "native decision recorded");
        Check(!p.ObserveNative(2, "Search", "None", "None"), "record once");
        p.Accepted(3, "A02", "Shoot", f);
        Check(!p.Yielded, "can resume immediately");
        p.Begin(4, "exception", "error", f);
        Check(p.Blocked(4, f), "exception isolation still applies");
        Check(!p.Blocked(4 + c.ErrorRetry, f), "fault retry becomes available");
        p.Clear();
        Check(!p.Yielded && !p.Blocked(0, f), "raid cleanup");

        return n;
    }
}
