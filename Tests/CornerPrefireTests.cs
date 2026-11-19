using System;
using System.Collections.Generic;
using System.Linq;
using Comet.Core;

// zzap fbb86b83 behavior; see SOURCE_BASELINE.md.
internal static class CornerPrefireTests
{
    public static int Run()
    {
        int n = 0;
        void Check(bool ok, string why)
        {
            n++;
            if (!ok)
                throw new Exception("CornerPrefire: " + why);
        }

        var c = new CornerPrefireConfig();
        c.Validate();
        Check(
            c.FireLimit == 1.5 && c.Reserve(30) == 9 && c.Start(30) == 12,
            "source ammo and burst limits"
        );
        var f = new CornerPrefireFrame
        {
            Now = 10,
            SeenAt = 9,
            ParentDeadline = 30,
            ParentActive = true,
            ParentEpisode = 1,
            ParentStyle = "prefire",
            Target = "e",
            Weapon = "rifle",
            Mode = "automatic",
            Automatic = true,
            Ammo = 30,
            Capacity = 30,
            Known = new(3, 0, 3),
            Aim = new(3, 1, 3),
            CornerDistance = 2,
            KnownCornerDistance = 1,
        };
        var p = new CornerPrefire();
        Check(p.Begin(f, c, 0), "parent already selected prefire");
        Check(p.Request(f, c), "native request without additional safety approval");
        p.Result(false);
        f.Now = 10.1;
        Check(p.Request(f, c), "native refusal retries next tick");
        p.Result(true);
        double until = p.Deadline;
        f.Now = 10.2;
        Check(p.Request(f, c), "native shoot refreshed each tick");
        p.Result(true);
        Check(p.Deadline == until, "burst timer starts only on first accepted shot");
        f.Now = until + .01;
        Check(!p.Observe(f, c), "source burst duration");

        return n;
    }
}
