using System;
using System.Collections.Generic;

namespace Comet.Core;

public sealed class DoorAmbushWait
{
    public double Min { get; set; }
    public double Max { get; set; }

    public DoorAmbushWait() { }

    public DoorAmbushWait(double min, double max)
    {
        Min = min;
        Max = max;
    }
}

public sealed class DoorAmbushConfig
{
    public bool Enabled { get; set; } = true;
    public double DoorDistance { get; set; } = 8;
    public double ContactLifetime { get; set; } = 60;
    public double TargetCooldown { get; set; } = 60;
    public bool AllowFeint { get; set; } = true;
    public double MinimumUsefulWait { get; set; } = 2;
    public Dictionary<string, DoorAmbushWait> Waits { get; set; } =
        new()
        {
            ["Normal"] = new(15, 35),
            ["Chad"] = new(15, 35),
            ["GigaChad"] = new(15, 35),
            ["Wreckless"] = new(15, 35),
            ["Coward"] = new(15, 35),
            ["Timmy"] = new(15, 35),
            ["SnappingTurtle"] = new(30, 60),
            ["Rat"] = new(40, 80),
        };

    public void Validate()
    {
        CometConfig.Range(DoorDistance, 2, 15, nameof(DoorDistance));
        CometConfig.Range(ContactLifetime, 1, 120, nameof(ContactLifetime));
        CometConfig.Range(TargetCooldown, 30, 180, nameof(TargetCooldown));
        CometConfig.Range(MinimumUsefulWait, 0, 10, nameof(MinimumUsefulWait));
        if (Waits == null || Waits.Count != 8)
            throw new ArgumentException("Door ambush needs all eight personality ranges.");
        foreach (
            var key in new[]
            {
                "Normal",
                "Chad",
                "GigaChad",
                "Wreckless",
                "Coward",
                "Timmy",
                "SnappingTurtle",
                "Rat",
            }
        )
        {
            if (!Waits.TryGetValue(key, out var w) || w == null)
                throw new ArgumentException("Missing door ambush range: " + key);
            CometConfig.Range(w.Min, 1, 120, key + " minimum");
            CometConfig.Range(w.Max, w.Min, 120, key + " maximum");
        }
    }
}

public sealed class DoorAmbushTrace
{
    public string PlanningInput { get; set; } = "";
    public int DoorsScanned { get; set; }
    public int DoorsEligible { get; set; }
    public string State { get; set; } = "planning";
    public string Reason { get; set; } = "ambush-find-useful-angle";
    public string Target { get; set; } = "";
    public string Source { get; set; } = "";
    public string Personality { get; set; } = "";
    public string Door { get; set; } = "";
    public string Side { get; set; } = "";
    public string DoorState { get; set; } = "";
    public string Feint { get; set; } = "none";
    public string FeintResult { get; set; } = "";
    public double KnownX { get; set; }
    public double KnownY { get; set; }
    public double KnownZ { get; set; }
    public double ContactAge { get; set; }
    public double EvidenceAt { get; set; }
    public double Elapsed { get; set; }
    public double WaitDuration { get; set; }
    public double HoldElapsed { get; set; }
    public double Remaining { get; set; }
    public double SharedRemaining { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public double AimX { get; set; }
    public double AimY { get; set; }
    public double AimZ { get; set; }
    public double Pose { get; set; }
    public bool Prepared { get; set; }
    public bool MovementRequested { get; set; }
    public bool MovementAccepted { get; set; }
    public bool MovementObserved { get; set; }
    public bool Arrived { get; set; }
    public bool RetainedPosition { get; set; }
    public bool EyesClear { get; set; }
    public bool MuzzleClear { get; set; }
    public bool AngleVerified { get; set; }
    public bool VisualContact { get; set; }
    public double ValidFor { get; set; } = 1.5;
    public List<DoorCandidate> Candidates { get; set; } = new();
    public string[] Alternatives { get; set; } =
        new[]
        {
            "Keep the native hold if no safe doorway angle exists",
            "Reconsider search, cover change or retreat when waiting ends",
        };
}

// Detached execution evidence. Histories survive release and door/target changes, not raid disposal.
public sealed class DoorAmbush
{
    public readonly PreparationWait Preparation = new();

    private readonly Dictionary<string, (double at, double evidence)> history = new();
    private double start,
        deadline,
        holdAt = -1,
        memoryUntil,
        last;
    private DoorPoint origin;
    public DoorAmbushTrace? Trace { get; private set; }
    public bool Active { get; private set; }

    public bool CanBegin(string target, double age, double now, DoorAmbushConfig c) =>
        !Active
        && target.Length > 0
        && Valid(age)
        && Valid(now)
        && age >= 0
        && age <= c.ContactLifetime
        && now >= age
        && (!history.TryGetValue(target, out var h) || now - h.at >= c.TargetCooldown);

    public bool Begin(
        string target,
        string source,
        string personality,
        DoorPoint known,
        double age,
        double now,
        double roll,
        double memoryLimit,
        double sharedHeld,
        double sharedLimit,
        DoorAmbushConfig c
    )
    {
        if (
            !c.Enabled
            || !known.Finite
            || !Valid(roll)
            || roll < 0
            || roll >= 1
            || !c.Waits.TryGetValue(personality, out var wait)
            || !CanBegin(target, age, now, c)
        )
            return false;
        Preparation.Clear();
        history[target] = (now, now - age);
        start = last = now;
        holdAt = -1;
        deadline = now + 150;
        memoryUntil = deadline;
        Trace = new()
        {
            Target = target,
            Source = source,
            Personality = personality,
            KnownX = known.X,
            KnownY = known.Y,
            KnownZ = known.Z,
            EvidenceAt = now - age,
            ContactAge = age,
            WaitDuration = wait.Min + (wait.Max - wait.Min) * roll,
            SharedRemaining = 150,
            Remaining = Math.Min(deadline, memoryUntil) - now,
        };
        Active = true;
        return true;
    }

    public void Prepare(DoorCandidate candidate, DoorPoint aim, double pose, string doorState)
    {
        if (
            !Active
            || Trace!.State != "planning"
            || !candidate.Eligible
            || !candidate.Point.Finite
            || !aim.Finite
            || !Valid(pose)
            || pose < 0
            || pose > 1
        )
            return;
        var t = Trace;
        t.Prepared = true;
        t.Door = candidate.Door;
        t.Side = candidate.Side;
        t.X = candidate.X;
        t.Y = candidate.Y;
        t.Z = candidate.Z;
        t.AimX = aim.X;
        t.AimY = aim.Y;
        t.AimZ = aim.Z;
        t.Pose = pose;
        t.DoorState = doorState;
    }

    public void Request(DoorPoint from, bool accepted)
    {
        if (!Active || !Trace!.Prepared || Trace.State != "planning" || !from.Finite)
            return;
        origin = from;
        Trace.MovementRequested = true;
        Trace.MovementAccepted = accepted;
        if (!accepted)
        {
            End("ambush-movement-refused");
            return;
        }
        Trace.State = "approaching";
        Trace.Reason = "ambush-native-move-accepted";
    }

    public void Movement(DoorPoint position, bool complete, bool owned, double now)
    {
        if (!Active || Trace!.State != "approaching")
            return;
        if (!owned)
        {
            End("ambush-route-replaced");
            return;
        }
        if (!position.Finite || !Valid(now))
        {
            End("ambush-invalid-movement");
            return;
        }
        var t = Trace;
        t.MovementObserved |= (position - origin).Length >= .15;
        if ((position - new DoorPoint(t.X, t.Y, t.Z)).Length <= .6)
        {
            t.Arrived = true;
            t.State = "settling";
            t.Reason = "ambush-position-reached";
            if (holdAt < 0)
                holdAt = now;
        }
    }

    public bool Angle(bool eyes, bool muzzle, double now)
    {
        if (!Active || !(Trace!.Arrived || Trace.RetainedPosition) || !Valid(now))
            return false;
        var t = Trace;
        t.EyesClear = eyes;
        t.MuzzleClear = muzzle;
        t.AngleVerified = true;
        if (!t.AngleVerified)
        {
            if (t.State == "holding" || now - holdAt > 1)
                End("ambush-angle-blocked");
            return false;
        }
        t.State = "holding";
        t.Reason = "ambush-watching-exit";
        return true;
    }

    public bool MaintainPosition(DoorPoint position)
    {
        if (!Active || !(Trace!.Arrived || Trace.RetainedPosition))
            return false;
        return true;
    }

    public bool Retain(DoorPoint position, bool eyes, bool muzzle, double now)
    {
        if (
            !Active
            || Trace!.State != "planning"
            || !Trace.Prepared
            || Trace.MovementRequested
            || !Valid(now)
            || now < last
            || now >= deadline
            || now >= memoryUntil
            || !position.Finite
            || (position - new DoorPoint(Trace.X, Trace.Y, Trace.Z)).Length > .15
        )
            return false;
        Trace.RetainedPosition = true;
        holdAt = now;
        Trace.State = "settling";
        return Angle(eyes, muzzle, now);
    }

    public bool Observe(
        double now,
        double age,
        bool known,
        DoorPoint place,
        double sharedHeld,
        double sharedLimit,
        double approachLimit
    )
    {
        if (!Active)
            return false;
        var t = Trace!;
        if (
            !Valid(now)
            || !Valid(age)
            || !Valid(sharedHeld)
            || !Valid(sharedLimit)
            || !place.Finite
            || now < last
            || age < 0
        )
        {
            End("ambush-invalid-observation");
            return false;
        }
        last = now;
        t.ContactAge = age;
        t.Elapsed = now - start;
        t.SharedRemaining = Math.Max(0, deadline - now);
        t.HoldElapsed = holdAt < 0 ? 0 : Math.Max(0, now - holdAt);
        t.Remaining = Math.Max(
            0,
            Math.Min(
                t.SharedRemaining,
                Math.Min(
                    memoryUntil - now,
                    holdAt < 0 ? double.MaxValue : t.WaitDuration - t.HoldElapsed
                )
            )
        );
        if (now >= deadline)
            End("ambush-session-timeout");
        else if (!known)
            End("ambush-contact-forgotten");
        else if (holdAt >= 0 && t.HoldElapsed >= t.WaitDuration)
            End("ambush-wait-complete-reconsider");
        else if (t.State == "approaching" && now - start > approachLimit)
            End("ambush-approach-timeout");
        return Active;
    }

    public void End(string reason, bool visible = false)
    {
        if (!Active)
            return;
        Active = false;
        Trace!.State = "ended";
        Trace.Reason = reason;
        Trace.VisualContact |= visible;
        Trace.ValidFor = 0;
        Trace.Remaining = 0;
    }

    public double Deadline =>
        Math.Min(
            deadline,
            Math.Min(memoryUntil, holdAt < 0 ? double.MaxValue : holdAt + Trace!.WaitDuration)
        );

    public void Suspend()
    {
        if (Active)
        {
            Active = false;
            Trace!.State = "suspended";
            Trace.ValidFor = 0;
            Trace.AngleVerified = false;
        }
    }

    public bool Resume(DoorPoint from, double now, double approachLimit)
    {
        if (Trace?.State != "suspended" || !from.Finite || !Valid(now) || now < last)
            return false;
        Active = true;
        origin = from;
        start = last = now;
        deadline = memoryUntil = now + 150;
        holdAt = -1;
        Trace.State = "approaching";
        Trace.Arrived = Trace.RetainedPosition = Trace.AngleVerified = false;
        Trace.MovementRequested = Trace.MovementAccepted = true;
        Trace.ValidFor = 1.5;
        return true;
    }

    public void AbandonSuspended(string reason)
    {
        if (Trace?.State == "suspended")
        {
            Trace.State = "ended";
            Trace.Reason = reason;
            Trace.Remaining = 0;
        }
    }

    public void Clear()
    {
        Active = false;
        Trace = null;
        history.Clear();
    }

    private static bool Valid(double n) => DoorPolicy.Finite(n);
}
