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
                3 + 60,
                100 + 60,
                0,
                0,
                j,
                c
            ) != null,
            "same evidence reusable after source cooldown"
        );
        var declined = new DoorReconOpportunity();
        DoorRunTrace? Try(string door, double now, double roll = 0) =>
            declined.TryBegin(door, "e", "heard", "Chad", default, 3, now, roll, 0, j, c);
        Check(Try("A", 0, .99)?.State == "ended", "high roll declines");
        Check(Try("B", 7.99) == null && Try("A", 24.99) == null, "global and per-door decline cooldown");
        Check(Try("B", 8)?.State == "validating" && Try("A", 25)?.State == "validating", "another door opens after eight seconds and first after 25");
        Check(DoorReconOpportunity.Choose(false, false, 0, j, c) == "ground", "no special move falls back to a ground peek");
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

        Check(DoorPlanWeights.Pick(true, true, true, 2, 0, 0, false, 4, false, .5) == "peek"
            && DoorPlanWeights.Pick(true, true, true, 15, .6, 2, false, 4, false, .5) == "clear"
            && DoorPlanWeights.Pick(true, true, true, 2, 0, 0, true, 4, true, .5) == "trap",
            "same draw changes with quiet, weakness, squad support and vulnerable observer");
        Check(DoorPlanWeights.Pick(true, true, true, 20, .9, 2, true, 2, true, .99, 0, 100) == "peek",
            "zero-weight clear and trap stay excluded even with bonuses");
        Check(DoorPlanWeights.Pick(true, true, true, 4, 0, 0, false, 1, false, .5, 0, 0) == "trap",
            "custom door weights select trap when it is the only weighted plan");
        var gates = new DoorSessionCooldowns();
        gates.Finish("314", 100, declined: true);
        Check(!gates.CanUse("203", 107.9) && gates.CanUse("203", 108), "neighbour opens after the eight second bot cooldown");
        Check(!gates.CanUse("314", 124.9) && gates.CanUse("314", 125), "decline is local to the door for 25 seconds");
        gates.Finish("203", 130, declined: false);
        Check(gates.CanUse("314", 138) && !gates.CanUse("203", 189.9) && gates.CanUse("203", 190), "completed session cools only its door for one minute");
        gates.Finish("314", 140, declined: true);
        Check(!gates.CanProbe(141) && gates.CanUseHold("314", 141)
            && gates.CanUseHold("new", 141) && !gates.CanUseHold("203", 141),
            "a utility hold may bypass a declined roll, including its door but no other active door timer");
        gates.FinishFailure("314", 142);
        Check(!gates.CanProbeHold(171.999) && gates.CanUseHold("new", 172)
            && !gates.CanUseHold("314", 201.999) && gates.CanUseHold("314", 202),
            "a real failure also blocks utility holds for 30 seconds globally and 60 at the failed door");
        foreach (var reason in Enum.GetValues<DoorSessionEnd>())
        foreach (var age in new[] { 0d, 1.499, 1.5, 2 })
        {
            var cooldowns = new DoorSessionCooldowns();
            cooldowns.Finish("previous", 95, declined: false);
            const double end = 120;
            cooldowns.FinishSession("314", end - age, end, reason);
            bool early = age < 1.5 && reason is DoorSessionEnd.GoalEnemyChanged
                or DoorSessionEnd.EnemySpotted or DoorSessionEnd.RushInstead or DoorSessionEnd.Interrupted;
            bool resume = reason == DoorSessionEnd.ResumableGoalChange;
            double same = end + (resume ? 5 : early ? 8 : 60);
            bool failed = reason is DoorSessionEnd.MoveTimeout or DoorSessionEnd.Stuck or DoorSessionEnd.NoPath
                or DoorSessionEnd.DoorDidNotOpen or DoorSessionEnd.OpenFailed;
            double other = end + (early || resume ? 3 : failed ? 30 : 8);
            Check(!cooldowns.CanProbe(other - .001) && cooldowns.CanUse("203", other),
                $"global retry boundary for {reason} at {age}");
            Check(!cooldowns.CanUse("314", same - .001) && cooldowns.CanUse("314", same),
                $"same door retry boundary for {reason} at {age}");
            Check(!cooldowns.CanUse("previous", other), "shortening one door preserves other door timers");
        }
        var phase = new DoorMovePhase();
        string Move(string state, double x, double time) => phase.Check(state, new(x, 0, 0), false, time, out _);
        Check(Move("out", 0, 0) == "" && Move("out", .2, 2.51) == "move-stalled", "wedged bot exits after 2.5s");
        phase.Reset();
        Check(Move("out", 0, 20) == "" && Move("out", .3, 20.05f) == ""
            && Move("out", .5, 22.51f) == "" && Move("out", .5, 20.05f + 2.5f) == ""
            && Move("out", .5, 22.56f) == "move-stalled",
            "an early update restarts the 2.5s window and measures progress from the new position");
        phase.Reset();
        Check(Move("out", 0, 0) == "" && Move("out", 0, .1f) == ""
            && Move("out", 0, 2.51f) == "move-stalled", "exactly 100 ms is outside the rearm window");
        phase.Reset();
        Check(Move("out", 0, 0) == "" && Move("out", 0, .05f) == ""
            && Move("out", .4f, 2.56f) == "" && Move("out", .4f, 5.07f) == "move-stalled",
            "40 cm counts as progress and starts a fresh check");
        phase.Reset();
        Check(Move("out", 0, 0) == "" && Move("out", 1, 3) == "" && Move("out", 2, 6) == ""
            && Move("out", 3, 9) == "" && Move("out", 4, 12.01) == "move-timeout"
            && Move("back", 4, 12.02) == "" && Move("back", 4.5, 15) == "", "each leg has its own movement budget");
        p.Begin(new DoorRunTrace(), 20);
        p.Start(default, 20);
        bool ObserveRun(float now) => p.Observe(default, 0, 100, true, true, false, true, false, 0, now, c);
        Check(ObserveRun(20.3f) && ObserveRun(20.37f) && !ObserveRun(22.84f)
            && p.Trace!.Reason == "run-by-move-stalled",
            "late first observation arms the check but does not restart the phase's first 100 ms");
        p.Begin(new DoorRunTrace(), 20);
        p.Start(default, 20);
        Check(!ObserveRun(32.01f) && p.Trace!.Reason == "run-by-move-timeout",
            "a late first observation still respects the movement phase timeout");
        return n;
    }
}
