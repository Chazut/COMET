using System;
using System.Collections.Generic;
using System.Linq;
using Comet.Core;

// zzap fbb86b83 behavior; see SOURCE_BASELINE.md.
internal static class AngleWatchTests
{
    public static int Run()
    {
        int n = 0;
        void Check(bool ok, string why)
        {
            n++;
            if (!ok)
                throw new Exception("AngleWatch: " + why);
        }

        var c = new AngleWatchConfig();
        c.Validate();
        var f = new AngleWatchFrame
        {
            Now = 20,
            ObservedAt = 1,
            Target = "e",
            Source = "heard",
            Action = "Search",
            Known = new(1, 0, 5),
            Origin = new(0, 1.2, 0),
            NativeDirection = new(0, 0, 1),
            ObservedDirection = new(0, 0, 1),
            Moving = true,
            Candidates = new[]
            {
                new AngleCandidate
                {
                    Id = "angle",
                    Kind = "corner",
                    Aim = new(-1, 1.2, 3),
                    Clear = true,
                },
            },
        };
        var p = new AngleWatch();
        Check(p.Tick(f, c), "no added personal-only or 10 second observation gate");
        f.Now = 30;
        Check(p.Tick(f, c), "no four second moving limit");
        f.Candidates[0].Clear = false;
        Check(!p.Tick(f, c), "source blocked opening rejected");
        f.Candidates[0].Clear = true;
        f.Now = 31;
        Check(p.Tick(f, c), "same contact can reacquire angle");

        return n;
    }
}
