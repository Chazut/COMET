using System;
using System.Linq;
using Comet.Core;

// Behavioral contract: zzap fbb86b83 (see SOURCE_BASELINE.md).
internal static class DoorAmbushTests
{
    public static int Run()
    {
        int n = 0;
        void Check(bool ok, string why)
        {
            n++;
            if (!ok)
                throw new Exception("DoorAmbush: " + why);
        }

        var c = new DoorAmbushConfig();
        c.Validate();
        Check(
            c.Waits["Rat"].Min == 40
                && c.Waits["Rat"].Max == 80
                && c.Waits["SnappingTurtle"].Max == 60,
            "source personality waits"
        );
        foreach (var v in c.Waits)
        {
            var p = new DoorAmbush();
            Check(
                p.Begin("e", "heard", v.Key, default, 20, 100, .5, 25, 90, 1, c),
                "no additional short memory/shared hold allowance"
            );
            Check(p.Trace!.WaitDuration == (v.Value.Min + v.Value.Max) / 2, "source wait selected");
            Check(
                p.Observe(101, 100, true, new(5, 0, 0), 100, 1, 1),
                "active ambush does not expire from extra age/shared cap"
            );
            p.Clear();
            Check(!p.Active, "clear");
        }

        foreach (var (personality, seconds) in new[] { ("Rat", 90d), ("SnappingTurtle", 67.5d), ("GigaChad", 30d) })
        {
            var trap = new DoorAmbush();
            Check(trap.Begin("e", "heard", personality, default, 5, 100, .5, 60, 0, 150, c, selectedCombatTrap: true)
                && trap.Trace!.WaitDuration == seconds, "selected combat trap duration for " + personality);
        }
        return n;
    }
}
