using System;
using System.Linq;
using Comet.Core;

// Behavioral contract: zzap fbb86b83 (see SOURCE_BASELINE.md).
internal static class DoorApproachTests
{
    public static int Run()
    {
        int n = 0;
        void Check(bool ok, string why)
        {
            n++;
            if (!ok)
                throw new Exception("DoorApproach: " + why);
        }

        var c = new DoorApproachConfig();
        c.Validate();
        var g = new DoorGeometry
        {
            Id = "door",
            State = "Open",
            Center = new(0, 0, 0),
            Normal = new(0, 0, 1),
            Axis = new(1, 0, 0),
            HalfWidth = .5,
        };
        var b = new DoorPoint(0, 0, 3);
        var enemy = new DoorPoint(20, 0, -1);
        Check(
            DoorPolicy.Geometry(g, b, enemy, Array.Empty<DoorPoint>(), c) == "door-contact-too-far",
            "source twelve metre contact radius"
        );
        enemy = new(7, 0, -1);
        Check(
            DoorPolicy.Geometry(g, b, enemy, Array.Empty<DoorPoint>(), c) == "",
            "no added finite opening/path crossing gate"
        );
        Check(
            DoorPolicy.Geometry(g, new(0, 2, 3), enemy, Array.Empty<DoorPoint>(), c)
                == "door-wrong-floor",
            "source floor test remains"
        );
        Check(
            DoorPolicy.Geometry(g, b, new(0, 0, 1), Array.Empty<DoorPoint>(), c)
                == "door-not-between-contact",
            "source opposite-side test remains"
        );
        Check(
            Enumerable.Range(0, 16).Select(v => DoorPolicy.Stack(g, b, 1, v)).Distinct().Count()
                == 16,
            "all source depth/gap combinations"
        );
        var point = new DoorPoint(.5, .65, .5);
        var path = new[] { b, point };
        Check(
            DoorPolicy.Placement(g, b, point, point, path, true) == "",
            "no added floor/width margin beyond source sample"
        );
        Check(
            DoorPolicy.Placement(g, b, point, point, path, false) != "",
            "real incomplete path rejected"
        );
        Check(
            DoorPolicy.Placement(g, point, point, point, Array.Empty<DoorPoint>(), false, approachStack: true) == "",
            "an already reachable stack needs no two-corner navigation path"
        );
        var p = new DoorApproach();
        p.Begin("e", "heard", enemy, 3, 10);
        var candidate = new DoorCandidate
        {
            Eligible = true,
            Door = "door",
            X = point.X,
            Y = point.Y,
            Z = point.Z,
        };
        p.Request(candidate, b, 10);
        p.Accept(true, 10);
        Check(
            p.Observe("e", new(4, 0, -1), b, 3, 11, 12),
            "updated known position does not cancel route"
        );
        Check(
            !p.Observe("e", enemy, point, 3, 12, 12) && p.Trace!.Arrived,
            "arrival recorded"
        );
        Check(p.Trace!.Episode == 1 && p.Trace.MovementAccepted, "issued episode and native acceptance");
        Check(DoorApproach.AtStack(new(.59, .3, 0), new(0, 0, 0)), "horizontal arrival without an active or completed path");
        Check(DoorApproach.NearStack(new(.8, 0, 0), new(0, 0, 0)) && !DoorApproach.NearStack(new(.91, 0, 0), new(0, 0, 0)), "source stack reachability shortcut");
        Check(!DoorApproach.AtStack(new(.6, 0, 0), new(0, 0, 0)), "source arrival boundary");
        p.Begin("e", "resume", enemy, 3, 20);
        p.Request(candidate, point, 20);
        p.Accept(true, 20);
        Check(!p.Observe("e", enemy, point, 3, 20, 12) && p.Trace!.Arrived && !p.Trace.Moved && p.Trace.Episode == 2,
            "already at the stack: arrival is distinct from movement on a new episode");
        p.Begin("e", "heard", enemy, 3, 30);
        p.Request(candidate, b, 30);
        p.Accept(false, 30);
        Check(!p.Active && p.Trace!.Reason == "door-mover-refused" && !p.Trace.Arrived,
            "an actual native refusal ends the request without reporting arrival");
        Check(DoorSessionCooldowns.FailureReason(p.Trace!.Reason, false) == DoorSessionEnd.NoPath,
            "a rejected native stack move reports a failed session");
        p.Clear();
        Check(p.Trace == null, "raid lifecycle reset");

        return n;
    }
}
