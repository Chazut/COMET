using System;
using System.Collections.Generic;
using System.Linq;
using Comet.Core;

// zzap fbb86b83 behavior; see SOURCE_BASELINE.md.
internal static class CoverTravelTests
{
    public static int Run()
    {
        int n = 0;
        void Check(bool ok, string why)
        {
            n++;
            if (!ok)
                throw new Exception("CoverTravel: " + why);
        }

        var c = new CoverTravelConfig();
        c.Validate();
        var p = new CoverTravelPolicy();
        var f = new CoverTravelFrame
        {
            Now = 10,
            ObservedAt = 9,
            Target = "e",
            Context = "cover",
            Position = default,
            Known = new(0, 0, 20),
            Goal = new(9, 0, 0),
            Length = 9,
        };
        Check(p.Tick(f, c) == "face-walk", "close recently seen threat uses source walking choice");
        f.Geometry = false;
        f.Protected = true;
        f.Ready = false;
        Check(p.Tick(f, c) == "face-walk", "no extra geometry or readiness veto");
        f.Length = 8;
        Check(p.Tick(f, c) == "native", "short dash retains native decision");
        f.Length = 9;
        f.Now = 13;
        Check(p.Tick(f, c) == "native", "source four second sight limit");
        f.Now = 10;
        f.Known = new(0, 0, 31);
        Check(p.Tick(f, c) == "native", "source thirty meter distance");

        return n;
    }
}
