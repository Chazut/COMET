using System;
using System.Linq;
using Comet.Core;

// Behavioral contract: zzap fbb86b83 (see SOURCE_BASELINE.md).
internal static class DoorSearchClearTests
{
    public static int Run()
    {
        int n = 0;
        void Check(bool ok, string why)
        {
            n++;
            if (!ok)
                throw new Exception("DoorSearchClear: " + why);
        }

        var c = new DoorRoomClearConfig();
        var q = new DoorSearchClearConfig();
        q.Validate(c.ContactLifetime);
        var p = new DoorRoomClear();
        double age = c.ContactLifetime + 1;
        Check(
            DoorSearchClear.Gate(age, c, q, 120, 120) == "",
            "old known contact enters source search clear"
        );
        Check(
            DoorSearchClear.Gate(q.ContactLifetime + .01, c, q, 120, 120) != "",
            "source age limit"
        );
        Check(
            DoorSearchClear.Factor(age, c.ContactLifetime, q.ContactLifetime) == 1,
            "no source chance fade"
        );
        Check(
            p.BeginSearch("d", "e", "heard", "Chad", default, age, 100, 0, c, q, 120, 120),
            "search clear starts"
        );
        p.End("done");
        // No additional permanently-consumed-evidence rule.
        Check(
            p.BeginSearch(
                "d",
                "e",
                "heard",
                "Chad",
                default,
                age,
                100 + c.TargetCooldown,
                0,
                c,
                q,
                120,
                120
            ),
            "same contact allowed after source cooldown"
        );

        Check(
            DoorSearchClear.Gate(119, c, q, 5, 10) == "",
            "own source window independent of generic lifetime"
        );
        Check(
            DoorPolicy.Gate(true, true, false, true, false, 119, 10, new(), q.ContactLifetime)
                == "",
            "approach cannot preempt D08 selection window"
        );
        Check(
            DoorSearchClear.Factor(119, c.ContactLifetime, 120) == 1,
            "full chance to 120 seconds"
        );
        Check(
            DoorSearchClear.Gate(2, c, q, 5, 10) == "",
            "native search also clears with a fresh contact"
        );
        Check(c.ContactLifetime == 60, "recent tactic source window is sixty seconds");
        return n;
    }
}
