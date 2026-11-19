using System;
using System.Collections.Generic;
using System.Linq;
using Comet.Core;

// zzap fbb86b83 behavior; see SOURCE_BASELINE.md.
internal static class CornerChaseTests
{
    public static int Run()
    {
        int n = 0;
        void Check(bool ok, string why)
        {
            n++;
            if (!ok)
                throw new Exception("CornerChase: " + why);
        }

        var c = new CornerChaseConfig();
        c.Validate();
        CornerChaseFrame F(double now = 10) =>
            new()
            {
                Combat = new()
                {
                    Now = now,
                    Action = "RushEnemy",
                    Target = "e",
                    Visible = false,
                    Known = new(4, 0, 4),
                    Personality = "Chad",
                },
                SeenAt = 9.5,
                ObservationEpisode = 1,
                RealCorner = true,
                PathLength = 8,
                Speed = 3,
                CanJump = true,
                Fleeing = true,
            };
        var p = new CornerChase();
        Check(p.Begin(F(), c, 0, .99, 1), "source close chase");
        Check(
            Math.Abs(p.Deadline - (F().SeenAt + c.ObservationMaxAge)) < .001,
            "source six-second opportunity deadline"
        );
        var f = F(10.1);
        f.Combat.ProtectedCover = true;
        f.Combat.Ready = false;
        Check(p.Observe(f, c), "source chase not vetoed by cover or extra ready flag");
        f.Combat.Visible = true;
        Check(!p.Observe(f, c), "source stops chase on visibility");
        var q = new CornerChase();
        f = F();
        f.PathLength = c.PathLimit + 1;
        Check(!q.Begin(f, c, 0, .99, 1), "source chase path limit retained");

        return n;
    }
}
