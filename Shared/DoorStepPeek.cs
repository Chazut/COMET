using System;
using System.Collections.Generic;

namespace Comet.Core;

public sealed class DoorStepPeekConfig
{
    public bool Enabled { get; set; } = true;
    public double Chance { get; set; } = 100;
    public int MaximumPeeks { get; set; } = 3;
    public double Distance { get; set; } = .8;
    public double ObservationMin { get; set; } = .15;
    public double ObservationMax { get; set; } = .3;
    public double RestMin { get; set; } = 0;
    public double RestMax { get; set; } = 0;
    public double Limit { get; set; } = 6;
    public bool VaryPosture { get; set; } = true;

    public void Validate()
    {
        CometConfig.Range(Chance, 0, 100, nameof(Chance));
        CometConfig.Range(MaximumPeeks, 1, 3, nameof(MaximumPeeks));
        CometConfig.Range(Distance, .3, 1, nameof(Distance));
        CometConfig.Range(ObservationMin, .1, 1.5, nameof(ObservationMin));
        CometConfig.Range(ObservationMax, ObservationMin, 1.5, nameof(ObservationMax));
        CometConfig.Range(RestMin, 0, 2, nameof(RestMin));
        CometConfig.Range(RestMax, RestMin, 2, nameof(RestMax));
        CometConfig.Range(Limit, 3, 10, nameof(Limit));
    }
}

public sealed class DoorStepTrace
{
    public string State { get; set; } = "planning";
    public string Reason { get; set; } = "step-continuation-selected";
    public string Parent { get; set; } = "D02";
    public string Door { get; set; } = "";
    public string Target { get; set; } = "";
    public string Source { get; set; } = "";
    public string Personality { get; set; } = "";
    public string Purpose { get; set; } = "pending";
    public double Chance { get; set; }
    public double Roll { get; set; }
    public int Peek { get; set; } = 1;
    public int Completed { get; set; }
    public double KnownX { get; set; }
    public double KnownY { get; set; }
    public double KnownZ { get; set; }
    public double ContactAge { get; set; }
    public double StackX { get; set; }
    public double StackY { get; set; }
    public double StackZ { get; set; }
    public double PointX { get; set; }
    public double PointY { get; set; }
    public double PointZ { get; set; }
    public double LookX { get; set; }
    public double LookY { get; set; }
    public double LookZ { get; set; }
    public double ActualX { get; set; }
    public double ActualY { get; set; }
    public double ActualZ { get; set; }
    public double Pose { get; set; } = 1;
    public double ActualPose { get; set; }
    public double Observation { get; set; }
    public double RequiredObservation { get; set; }
    public double Rest { get; set; }
    public double RequiredRest { get; set; }
    public double Elapsed { get; set; }
    public double Remaining { get; set; }
    public bool PoseObserved { get; set; }
    public bool MovementAccepted { get; set; }
    public bool MovementObserved { get; set; }
    public bool Arrived { get; set; }
    public bool ReturnAccepted { get; set; }
    public bool Returned { get; set; }
    public bool Looking { get; set; }
    public bool VisualAcquired { get; set; }
    public double ValidFor { get; set; } = 1.5;
    public string[] Alternatives { get; set; } = Array.Empty<string>();
}

// Continuation only. This policy cannot draw another D02/D03 opportunity or reset its cooldown.
public sealed class DoorStepPeek
{
    private readonly DoorMovePhase movement = new();
    public DoorStepTrace? Trace { get; private set; }
    public bool Active => Trace != null && Trace.State != "ended" && Trace.State != "completed";
    private double started,
        last,
        phaseAt;
    private DoorPoint origin;

    public bool Begin(DoorJumpTrace parent, double now, double roll, DoorStepPeekConfig c)
    {
        if (
            Active
            || !c.Enabled
            || parent.State != "completed"
            || !parent.Returned
            || !new DoorPoint(parent.KnownX, parent.KnownY, parent.KnownZ).Finite
            || !new DoorPoint(parent.ActualX, parent.ActualY, parent.ActualZ).Finite
            || !DoorPolicy.Finite(parent.ContactAge)
            || parent.ContactAge < 0
            || string.IsNullOrEmpty(parent.Door)
            || string.IsNullOrEmpty(parent.Target)
            || !DoorPolicy.Finite(now)
            || !DoorPolicy.Finite(roll)
            || roll < 0
            || roll >= 1
        )
            return false;
        Trace = new()
        {
            Door = parent.Door,
            Target = parent.Target,
            Source = parent.Source,
            VisualAcquired = parent.VisualAcquired,
            Personality = parent.Personality,
            KnownX = parent.KnownX,
            KnownY = parent.KnownY,
            KnownZ = parent.KnownZ,
            ContactAge = parent.ContactAge,
            StackX = parent.ActualX,
            StackY = parent.ActualY,
            StackZ = parent.ActualZ,
            Chance = c.Chance,
            Roll = roll * 100,
            Remaining = c.Limit,
        };
        started = last = phaseAt = now;
        if (roll * 100 >= c.Chance)
            End("step-continuation-declined", now);
        return Active;
    }

    private void Phase(string state, string reason, double now)
    {
        Trace!.State = state;
        Trace.Reason = reason;
        phaseAt = now;
    }

    public void Prepared(
        DoorPoint point,
        DoorPoint look,
        double pose,
        double observation,
        double rest,
        string purpose,
        double now
    )
    {
        if (!Active || Trace!.State != "planning")
            return;
        if (
            !point.Finite
            || !look.Finite
            || !DoorPolicy.Finite(pose)
            || pose < 0
            || pose > 1
            || !DoorPolicy.Finite(observation)
            || observation <= 0
            || !DoorPolicy.Finite(rest)
            || rest < 0
            || !DoorPolicy.Finite(now)
            || now < last
        )
        {
            End("step-invalid-plan", last);
            return;
        }
        var t = Trace!;
        t.PointX = point.X;
        t.PointY = point.Y;
        t.PointZ = point.Z;
        t.LookX = look.X;
        t.LookY = look.Y;
        t.LookZ = look.Z;
        t.Pose = pose;
        t.RequiredObservation = observation;
        t.RequiredRest = rest;
        t.Purpose = purpose;
        t.MovementAccepted =
            t.MovementObserved =
            t.Arrived =
            t.ReturnAccepted =
            t.Returned =
            t.PoseObserved =
            t.Looking =
                false;
        t.Observation = t.Rest = 0;
        Phase("preparing", "step-angle-verified", now);
    }

    public void Start(DoorPoint position, double now)
    {
        if (!Active || Trace!.State != "outward-pending")
            return;
        origin = position;
        Trace.MovementAccepted = true;
        Phase("outward", "step-movement-accepted", now);
    }

    public void ReturnStarted(DoorPoint position, double now)
    {
        if (!Active || Trace!.State != "return-pending")
            return;
        Trace.ReturnAccepted = true;
        Phase("returning", "step-return-accepted", now);
    }

    public void NoUsefulNext(double now)
    {
        if (Active && Trace!.State == "planning" && Trace.Completed > 0)
        {
            Phase("completed", "step-no-different-safe-angle", now);
            Trace.Remaining = 0;
        }
    }

    public bool SameContact(string target, DoorPoint p) => Trace?.Target == target && p.Finite;

    public bool Observe(
        DoorPoint position,
        double pose,
        bool grounded,
        bool sprinting,
        bool arrived,
        bool ready,
        bool looking,
        bool visible,
        double contactAge,
        double now,
        DoorStepPeekConfig c
    )
    {
        if (!Active)
            return false;
        var t = Trace!;
        if (
            !position.Finite
            || !DoorPolicy.Finite(pose)
            || pose < 0
            || pose > 1.01
            || !DoorPolicy.Finite(now)
            || now < last
            || !DoorPolicy.Finite(contactAge)
            || contactAge < 0
        )
        {
            End("step-invalid-observation", now);
            return false;
        }
        double dt = Math.Min(.25, now - last);
        last = now;
        t.Elapsed = now - started;
        t.Remaining = Math.Max(0, c.Limit - t.Elapsed);
        t.ActualX = position.X;
        t.ActualY = position.Y;
        t.ActualZ = position.Z;
        t.ActualPose = pose;
        t.PoseObserved = Math.Abs(pose - t.Pose) <= .08;
        t.Looking = looking;
        t.ContactAge = contactAge;
        t.VisualAcquired |= visible;
        t.Elapsed = DoorPolicy.Finite(started) ? Math.Max(0, now - started) : 0;
        if (t.State is "outward" or "returning")
        {
            string failure = movement.Check(t.State, position, arrived, now, out double remaining);
            t.Remaining = remaining;
            if (failure.Length > 0)
            {
                End("step-" + failure, now);
                return false;
            }
        }
        else
            movement.Reset();
        switch (t.State)
        {
            case "planning":
                break;
            case "preparing":
                Phase("outward-pending", "step-posture-requested", now);
                break;
            case "outward":
                t.MovementObserved |= (position - origin).Length >= .12;
                t.Arrived |= arrived;
                if (arrived || now - phaseAt > .45)
                    Phase("observing", "step-outward-window", now);
                break;
            case "observing":
                t.Observation = now - phaseAt;
                if (t.Observation >= t.RequiredObservation)
                    Phase("return-pending", "step-observation-complete", now);
                break;
            case "returning":
                t.Returned |= arrived;
                if (arrived || now - phaseAt > .45)
                {
                    t.Completed++;
                    Phase("resting", "step-return-window", now);
                }
                break;
            case "resting":
                t.Rest = now - phaseAt;
                if (t.Rest >= t.RequiredRest)
                {
                    if (t.Completed >= c.MaximumPeeks)
                    {
                        Phase("completed", "step-sequence-completed", now);
                        t.Remaining = 0;
                    }
                    else
                    {
                        t.Peek++;
                        Phase("planning", "step-check-another-angle", now);
                    }
                }
                break;
        }
        return Active;
    }

    public void End(string reason, double now)
    {
        if (Trace != null)
        {
            Phase("ended", reason, now);
            Trace.Remaining = 0;
        }
    }
}

public static class DoorStepGeometry
{
    public static DoorPoint Candidate(DoorGeometry g, DoorPoint stack, double distance) =>
        stack + g.Axis * (-Math.Sign((stack - g.Center).Dot(g.Axis)) * distance);

    public static bool Different(
        DoorPoint previousEye,
        DoorPoint previousLook,
        DoorPoint eye,
        DoorPoint look
    )
    {
        var a = previousLook - previousEye;
        var b = look - eye;
        if (!a.Finite || !b.Finite || a.Length < .1 || b.Length < .1)
            return false;
        return a.Dot(b) / (a.Length * b.Length) < Math.Cos(12 * Math.PI / 180);
    }

    public static string Route(
        DoorGeometry g,
        DoorPoint stack,
        DoorPoint start,
        DoorPoint goal,
        IReadOnlyList<DoorPoint> corners,
        bool complete,
        double maximum
    )
    {
        if (!goal.Finite || !complete || corners.Count < 2)
            return "step-no-path";
        return "";
    }

    public static bool Contains(DoorGeometry g, DoorPoint stack, DoorPoint point, DoorPoint actual)
    {
        if (
            !actual.Finite
            || Math.Abs(actual.Y - stack.Y) > .3
            || (actual - g.Center).Dot(g.Normal) * Math.Sign((stack - g.Center).Dot(g.Normal)) < .45
        )
            return false;
        var d = point - stack;
        var p =
            stack
            + d * Math.Max(0, Math.Min(1, (actual - stack).Dot(d) / Math.Max(.001, d.Dot(d))));
        return (actual - p).Length <= .2;
    }
}
