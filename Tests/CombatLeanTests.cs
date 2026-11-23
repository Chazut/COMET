using System;
using System.Collections.Generic;
using System.Linq;
using Comet.Core;

// zzap fbb86b83 behavior; see SOURCE_BASELINE.md.
internal static class CombatLeanTests
{
    public static int Run()
    {
        int n = 0;
        void Check(bool ok, string why)
        {
            n++;
            if (!ok)
                throw new Exception("CombatLean: " + why);
        }

        var c = new CombatLeanConfig();
        c.Validate();
        var f = new CombatLeanFrame
        {
            Combat = new()
            {
                Now = 10,
                Target = "e",
                Distance = 12,
                Known = new(0, 0, 12),
            },
        };
        f.SimplifiedSkeleton = true;
        f.GeometryFresh = false;
        Check(
            CombatLean.Gate(f, c) == "",
            "source does not require geometry or full skeleton evidence"
        );
        var denied = new CombatLeanFrame(new CombatStepFrame { Action = "other" })
        {
            HeadLateral = double.NaN,
        };
        Check(CombatLean.ContactGate(denied.Combat, c) == "lean-native-action-changed"
            && CombatLean.Gate(denied, c) == "lean-native-action-changed",
            "rejecting native action takes priority over physical observation");
        denied.Combat.Action = "DogFight";
        denied.Combat.Target = "enemy";
        denied.Combat.Distance = 10;
        Check(CombatLean.ContactGate(denied.Combat, c) == ""
            && CombatLean.Gate(denied, c) == "lean-invalid-observation",
            "an eligible action still requires a finite physical observation");
        var p = new CombatLean();
        Check(p.Begin(f, c, 0, 0, 0, 0, 0, 0), "native tilt start");
        f.Combat.Now = 20;
        Check(p.Observe(f, c, 0, 0), "no COMET short sequence cap");
        f.CornerPeek = true;
        Check(!p.Observe(f, c, 0, 0), "source corner-peek priority");

        return n;
    }
}
