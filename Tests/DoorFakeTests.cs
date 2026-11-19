using System;
using System.Linq;
using Comet.Core;

// Behavioral contract: zzap fbb86b83 (see SOURCE_BASELINE.md).
internal static class DoorFakeTests
{
    public static int Run()
    {
        int n = 0;
        void Check(bool ok, string why)
        {
            n++;
            if (!ok)
                throw new Exception("DoorFake: " + why);
        }

        var c = new DoorFakeGrenadeConfig();
        c.Validate();
        Check(
            c.DoorDistance == 3.2 && c.ContactDistance == 14 && c.ContactLifetime == 60,
            "source fake context"
        );
        foreach (string personality in new[] { "GigaChad", "Chad", "Normal", "Wreckless" })
        {
            var p = new DoorFakeGrenade();
            Check(
                p.Begin("d", "e", "heard", personality, default, 10, 100, 0, c),
                "source chance can select"
            );
            p.Draw("grenade", "rifle", 100);
            p.Observe(false, false, false, false, 101.3);
            Check(
                p.Active && p.Trace!.State == "holstering",
                "source timed restore without draw confirmation"
            );
            p.Observe(true, false, false, false, 101.4);
            p.RestoreRequested(true, 101.4);
            p.Observe(false, true, false, false, 101.5);
            Check(!p.Active && p.Trace!.WeaponReady, "restore completes on actual firearm");
            p.Clear();
            Check(p.Trace == null, "late data reset");
        }

        return n;
    }
}
