using System;

namespace Comet.Core;

// Per bot. Episode counters and door policy traces already distinguish separate attempts.
// Only a transition with execution evidence produces an event; sampling unchanged state allocates nothing.
internal struct ReplayGestures
{
    private int cornerEpisode, cornerPeek, prefireEpisode, jumpAttempt, magazineEpisode, feintEpisode;
    private string? weaveContext;
    private double weaveAt;
    private int weaveSide;
    private DoorJumpTrace? doorJump, returnJump;
    private DoorStepTrace? step;
    private int stepNumber;
    private DoorFakeTrace? fake;
    private DoorClearTrace? entry;

    internal bool Observe(CornerPeekTrace t)
    {
        if (t.LastOpenPeek <= 0 || t.LastOpenPeek != t.Peeks || (cornerEpisode == t.Episode && cornerPeek == t.Peeks)) return false;
        cornerEpisode = t.Episode;
        cornerPeek = t.Peeks;
        return true;
    }

    internal bool Observe(CornerPrefireTrace t) => t.ActualShots > 0 && First(t.Episode, ref prefireEpisode);

    internal bool Observe(TacticalJumpTrace t) =>
        t.SourceFeature != "D02" && t.TakeoffObserved && First(t.Attempt, ref jumpAttempt);

    internal string? Observe(CoverFeintTrace t)
    {
        if (t.Kind == "magazine" && t.MagazineAccepted && t.AnimationObserved && First(t.Episode, ref magazineEpisode))
            return "FakeReload";
        if (t.Kind == "peek" && t.MovementObserved && t.ExposedObserved && First(t.Episode, ref feintEpisode))
            return "CoverFeint";
        return null;
    }

    internal bool Observe(RetreatWeaveTrace t, double now)
    {
        if (t.State != "evading" || !t.MoveRequested || !(t.Speed > 0) || t.Context.Length == 0) return false;
        int side = Side(t.Yaw);
        if (side == 0 || side != Side(t.ObservedYaw)) return false;
        bool changed = weaveContext == t.Context && now >= weaveAt && now - weaveAt <= 2 && side != weaveSide;
        weaveAt = now;
        weaveContext = t.Context;
        weaveSide = side;
        return changed;
    }

    private static int Side(double yaw) => Math.Abs(yaw) >= 5 && Math.Abs(yaw) <= 85 ? Math.Sign(yaw) : 0;

    internal bool Observe(DoorJumpTrace t)
    {
        // D02 observes both legs and also runs when the generic M12 jump controller is disabled.
        bool outbound = t.TakeoffObserved && First(t, ref doorJump);
        bool returning = t.ReturnTakeoffObserved && First(t, ref returnJump);
        return outbound || returning;
    }

    internal bool Observe(DoorStepTrace t)
    {
        if (t.State is not ("outward" or "observing" or "return-pending") || !t.Arrived || !t.Looking) return false;
        if (ReferenceEquals(t, step) && t.Peek == stepNumber) return false;
        step = t;
        stepNumber = t.Peek;
        return true;
    }

    internal bool Observe(DoorFakeTrace t) => t.State == "completed" && t.HandsObserved
        && (t.Medical == null || (!t.Medical.ConsumptionObserved && !t.Medical.MedicalHandoff)) && First(t, ref fake);

    internal bool Observe(DoorClearTrace t) => t.ThresholdCrossed && t.MovementObserved && First(t, ref entry);

    private static bool First(int id, ref int last)
    {
        if (id == last) return false;
        last = id;
        return true;
    }

    private static bool First<T>(T current, ref T? previous) where T : class
    {
        if (ReferenceEquals(current, previous)) return false;
        previous = current;
        return true;
    }
}
