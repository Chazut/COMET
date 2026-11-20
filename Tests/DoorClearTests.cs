using System;
using Comet.Core;

// Behavioral contract: zzap fbb86b83 (see SOURCE_BASELINE.md).
internal static class DoorClearTests
{
    public static int Run()
    {
        int n = 0;
        void Check(bool ok, string why)
        {
            n++;
            if (!ok)
                throw new Exception("DoorClear: " + why);
        }

        var c = new DoorRoomClearConfig();
        c.Validate();
        for (int i = 0; i < 64; i++)
        {
            var p = new DoorRoomClear();
            Check(
                p.Begin("d" + i, "e" + i, "heard", "Chad", default, 1, 100, 0, c),
                "source room selection"
            );
            p.End("done");
        }
        var many = new DoorRoomClear();
        for (int i = 0; i < 40; i++)
        {
            Check(
                many.Begin("d" + i, "e" + i, "heard", "Chad", default, 1, 100 + i * 100, 0, c),
                "no 32-contact lifetime veto"
            );
            many.End("done");
        }
        many.Clear();
        Check(!many.Active, "lifecycle resets");

        DoorClearPlan Plan(double preference = 55, bool have = true, bool feints = true,
            bool moving = false, double age = 1, double depth = 2, double weakness = 0,
            bool rush = false, double roll = .5, double fake = .65) =>
            new(have, preference, feints, moving, age, depth, weakness, rush, roll, fake);

        Check(c.GrenadePreference == 55, "factory room preference matches the fork");
        var neutral = Plan();
        Check(neutral.GrenadeWeight == 1 && neutral.DirectWeight == 1 && !neutral.RealGrenade,
            "neutral weights are even and a tie picks direct");
        Check(Plan(roll: .49999).RealGrenade, "roll below the threshold picks grenade");
        var close = Plan(age: 3.01, depth: 1.49, weakness: .5, rush: true);
        Check(close.Camping && close.GrenadeWeight == 3.5 && close.DirectWeight == 1.8,
            "camping close applies the dash multiplier before weakness and personality bonuses");
        Check(Plan(age: 3, depth: 1.49).GrenadeWeight == 1 && Plan(age: 4, depth: 1.5).GrenadeWeight == 2.5,
            "camping and shallow depth use strict thresholds");
        Check(Plan(depth: 2.5).GrenadeWeight == 2 && Plan(depth: 2.49).GrenadeWeight == 1,
            "deep room threshold is inclusive");
        var moving = Plan(moving: true, age: 9, depth: 1, weakness: .499);
        Check(!moving.Camping && moving.GrenadeWeight == 1 && moving.DirectWeight == 2.5 && moving.Feints == 0,
            "recent steps ignore stale contact age and suppress both fakes");
        Check(Plan(have: false, age: 8, depth: 3, roll: 0).GrenadeWeight == 0
            && Plan(have: false, age: 8).Feints == 0 && Plan(feints: false, age: 8).Feints == 0,
            "inventory and feint setting are respected");
        foreach (var (age, fake, expected) in new[] {
            (4d, .1999, 0), (4d, .2, 1), (4d, .6499, 1), (4d, .65, 2), (3d, .4999, 0), (3d, .5, 1) })
            Check(Plan(age: age, fake: fake).Feints == expected, "source feint distribution");

        DoorRoomClear Start(DoorClearPlan plan, bool search = false)
        {
            var clear = new DoorRoomClear();
            Check(clear.BeginSelected("door", "enemy", "heard", "Chad", default, 4, 100,
                30, 12, 60, c, search, plan), "accepted room starts with the original plan");
            clear.Opened(101);
            return clear;
        }

        foreach (bool search in new[] { false, true })
        foreach (bool direct in new[] { false, true })
        foreach (bool authorizedThrow in new[] { false, true })
        foreach (double preference in new[] { 0d, 1d, 55d, 100d })
        {
            c.GrenadePreference = preference;
            var clear = Start(Plan(preference, feints: false), search);
            clear.Ready(102);
            bool chosen = clear.Choose(direct, authorizedThrow, "blocked", "throw refused", .5, 103, c);
            bool grenade = authorizedThrow && preference > 0 && (!direct || preference > 55);
            Check(chosen == (direct || grenade) && (clear.Trace!.Variant == "grenade") == grenade,
                "throw approval respects weights and single feasible alternatives for either clear mode");
            Check(clear.Trace!.Alternatives[1].Contains("disabled by preset") == (preference == 0),
                "eligibility diagnostic matches choice");
        }
        c.GrenadePreference = 0;
        var silent = Plan(preference: 0, age: 12, depth: 3, roll: 0);
        Check(!silent.RealGrenade && silent.Feints == 2, "zero forbids a real grenade even in deep quiet rooms");
        var pair = Start(silent, true);
        for (int i = 1; i <= 2; i++)
        {
            pair.Suspend();
            Check(pair.Resume(102 + i) && ReferenceEquals(silent, pair.Trace!.Plan) && pair.HasPendingFeint,
                "resuming keeps the same plan and remaining feints");
            pair.FeintStart(102 + i);
            pair.FeintStart(102 + i);
            Check(pair.Trace!.FeintsStarted == i && !pair.HasPendingFeint, "no second draw before recovery");
            pair.FeintEnd(true, "weapon-ready", 102 + i, true);
        }
        Check(pair.Trace!.State == "ready" && pair.Trace.FeintsCompleted == 2 && !pair.HasPendingFeint,
            "exactly two draws completed");
        Check(pair.Choose(true, true, "", "", .5, 106, c) && pair.Trace.Variant == "direct",
            "two feints followed by direct entry at zero preference");
        var noDraw = Start(silent);
        noDraw.FeintStart(102);
        noDraw.FeintEnd(false, "fake-no-accessible-grenade", 103, true);
        Check(noDraw.Trace!.State == "ready" && !noDraw.HasPendingFeint && noDraw.Trace.FeintsCompleted == 0,
            "failed draw skips remaining feints once");
        var heard = Start(silent);
        heard.FeintStart(102);
        heard.FeintEnd(false, "fake-heard-approach", 103, true);
        Check(!heard.Active, "native threat interruption cancels the clear");
        var busyHands = Start(silent);
        busyHands.FeintStart(102);
        busyHands.FeintEnd(false, "fake-draw-callback-failed", 103, false);
        Check(!busyHands.Active, "never skip failed hands recovery into an entry");
        c.GrenadePreference = 55;
        var noGrenade = Start(Plan(have: false));
        noGrenade.Ready(102);
        Check(!noGrenade.Choose(false, true, "blocked", "", .5, 103, c),
            "a grenade acquired later cannot create a real throw absent at admission");
        var noFake = Start(Plan(age: 9, depth: 3, roll: 0));
        noFake.Ready(102);
        Check(noFake.Trace!.FeintsStarted == 0 && noFake.Trace.FeintOutcome == "draw-unavailable"
            && noFake.Choose(true, false, "", "unsafe trajectory", .5, 103, c)
            && noFake.Trace.Variant == "direct", "unavailable fake and refused throw still allow direct entry");
        var refused = Start(Plan(feints: false, roll: 0));
        refused.Ready(102);
        refused.Choose(true, true, "", "", .5, 103, c);
        refused.ThrowRequest("item", 4, 8, 104);
        refused.ThrowResult(false, 104);
        Check(refused.Trace!.Plan!.RealGrenade && refused.Trace.Variant == "direct"
            && refused.Trace.GrenadeOutcome == "native-throw-refused",
            "telemetry distinguishes the original grenade choice from the executed fallback");
        return n;
    }
}
