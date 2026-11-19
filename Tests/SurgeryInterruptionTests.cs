using Comet.Core;

internal static class SurgeryInterruptionTests
{
    public static int Run()
    {
        int n = 0;
        void C(bool v, string m)
        {
            n++;
            if (!v)
                throw new Exception("SurgeryInterruption: " + m);
        }
        var c = new SurgeryInterruptionConfig();
        var p = new SurgeryInterruption();
        p.Begin(0, "cms", "arm");
        C(
            !p.Tick(
                new()
                {
                    Now = 1,
                    Hands = true,
                    SourceAllowed = true,
                    Hit = true,
                    Grenade = true,
                },
                c
            ),
            "no duplicate custom interruption thresholds"
        );
        C(
            p.Tick(
                new()
                {
                    Now = 2,
                    Hands = true,
                    SourceAllowed = false,
                },
                c
            )
                && p.RetryAt == 0,
            "source eligibility cancels without custom retry lock"
        );
        C(
            !p.Tick(
                new()
                {
                    Now = 3,
                    Hands = false,
                    WeaponReady = true,
                },
                c
            ) && !p.Active,
            "native hand return observed"
        );
        p.Begin(4, "cms", "leg");
        C(
            !p.Tick(
                new()
                {
                    Now = 5,
                    Hands = true,
                    Completed = true,
                    SourceAllowed = false,
                },
                c
            ),
            "completion wins"
        );
        var s = new SourceSurgery();
        C(
            s.Allows(10, "Normal", false, true, true, false, true, 0, 0, 0, 0, () => 100),
            "native strict eligibility"
        );
        C(
            !s.Allows(10, "Normal", false, false, true, true, false, 100, 100, 100, 10, () => 0),
            "requires usable kit"
        );
        foreach (
            var name in new[]
            {
                "Rat",
                "Coward",
                "SnappingTurtle",
                "Timmy",
                "Chad",
                "GigaChad",
                "Wreckless",
                "Normal",
            }
        )
        {
            var a = SourceSurgery.Limits(name);
            s = new();
            C(
                !s.Allows(
                    10,
                    name,
                    false,
                    true,
                    false,
                    true,
                    false,
                    a.distance - .01,
                    100,
                    100,
                    10,
                    () => 0
                ),
                name + " distance"
            );
            C(
                !s.Allows(
                    10,
                    name,
                    false,
                    true,
                    false,
                    true,
                    false,
                    100,
                    a.quiet - .01,
                    100,
                    10,
                    () => 0
                ),
                name + " seen age"
            );
            C(
                !s.Allows(
                    10,
                    name,
                    false,
                    true,
                    false,
                    true,
                    false,
                    100,
                    100,
                    a.quiet - .01,
                    10,
                    () => 0
                ),
                name + " known age"
            );
            C(
                s.Allows(
                    10,
                    name,
                    false,
                    true,
                    false,
                    true,
                    false,
                    a.distance,
                    a.quiet,
                    a.quiet,
                    10,
                    () => a.chance - .01
                ),
                name + " relaxed source boundaries"
            );
            C(
                s.Allows(11, name, true, true, false, false, false, a.distance * .5, 0, 0, 1, () =>
                    100
                ),
                name + " ongoing relaxed uses half distance and no cover veto"
            );
            C(
                !s.Allows(12, name, true, true, false, true, true, 100, 100, 100, 10, () => 0),
                name + " visible cancels"
            );
        }
        s = new();
        int rolls = 0;
        double Miss()
        {
            rolls++;
            return 100;
        }
        C(
            !s.Allows(10, "Normal", false, true, false, true, false, 100, 100, 100, 10, Miss),
            "failed roll"
        );
        C(
            !s.Allows(11, "Normal", false, true, false, true, false, 100, 100, 100, 10, Miss)
                && rolls == 1,
            "20s source roll cache"
        );
        s.Allows(30, "Normal", false, true, false, true, false, 100, 100, 100, 10, Miss);
        C(rolls == 2, "cache expires");
        return n;
    }
}
