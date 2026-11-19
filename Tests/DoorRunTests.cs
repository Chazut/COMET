using System;
using System.Linq;
using Comet.Core;

// Behavioral contract: zzap fbb86b83 (see SOURCE_BASELINE.md).
internal static class DoorRunTests
{
    public static int Run()
    {
        int n = 0;
        void Check(bool ok, string why)
        {
            n++;
            if (!ok)
                throw new Exception("DoorRun: " + why);
        }

        var c = new DoorRunByConfig();
        c.Validate();
        var j = new DoorJumpPeekConfig();
        var o = new DoorReconOpportunity();
        var t = o.TryBegin("d", "e", "heard", "Chad", default, 3, 100, 0, 0, j, c);
        Check(t != null && t.State == "validating", "source recon roll");
        Check(
            o.TryBegin(
                "d",
                "e",
                "heard",
                "Chad",
                default,
                3 + j.Cooldown,
                100 + j.Cooldown,
                0,
                0,
                j,
                c
            ) != null,
            "same evidence reusable after source cooldown"
        );
        var p = new DoorRunBy();
        p.Begin(t!, 100);
        p.Start(default, 100);
        p.Observe(new(2, 0, 0), 0, 100, true, true, true, true, false, 3, 100.1, c);
        p.Observe(new(2, 0, 0), 0, 100, true, true, false, true, false, 3, 101.2, c);
        Check(
            p.Trace!.State == "return-pending",
            "elapsed turn delay sufficient, sprint-exit proof not required"
        );
        p.ReturnStarted(new(2, 0, 0), 101.2);
        p.Observe(default, 0, 100, true, false, true, true, false, 3, 101.3, c);
        Check(p.Trace.Returned, "return recorded");
        var route = new[] { new DoorPoint(0, 0, 0), new DoorPoint(1, 1, 1) };
        Check(
            DoorRunGeometry.Route(new(), default, default, default, route[1], route, true) == "",
            "no added flat/corner-count/endpoint veto"
        );

        return n;
    }
}
