using System;
using System.Collections.Generic;
using System.Linq;

namespace Comet.Core;

public sealed class DoorJumpPeekConfig
{
    // Legacy JSON keys retained: Chances and Cooldown now govern the shared D02/D03 opportunity.
    public bool Enabled { get; set; } = true;
    public Dictionary<string, double> Chances { get; set; } =
        new()
        {
            ["GigaChad"] = 35,
            ["Chad"] = 25,
            ["Wreckless"] = 15,
            ["Normal"] = 10,
            ["Rat"] = 0,
            ["SnappingTurtle"] = 0,
            ["Coward"] = 0,
            ["Timmy"] = 0,
        };
    public double MinimumStamina { get; set; } = 0;
    public double MinimumSpeed { get; set; } = 1.2;
    public double Observation { get; set; } = .4;
    public double Limit { get; set; } = 4;
    public double Cooldown { get; set; } = 45;
    public bool ReturnJump { get; set; } = true;

    public void Validate()
    {
        if (Chances == null || Chances.Count != 8)
            throw new ArgumentException("Missing doorway peek personality chances.");
        foreach (
            var name in new[]
            {
                "GigaChad",
                "Chad",
                "Wreckless",
                "Normal",
                "Rat",
                "SnappingTurtle",
                "Coward",
                "Timmy",
            }
        )
            if (!Chances.TryGetValue(name, out var n))
                throw new ArgumentException("Missing peek chance: " + name);
            else
                CometConfig.Range(n, 0, 100, name);
        CometConfig.Range(MinimumStamina, 0, 100, nameof(MinimumStamina));
        CometConfig.Range(MinimumSpeed, .8, 3, nameof(MinimumSpeed));
        CometConfig.Range(Observation, 0, 1, nameof(Observation));
        CometConfig.Range(Limit, 2, 8, nameof(Limit));
        CometConfig.Range(Cooldown, 10, 180, nameof(Cooldown));
    }
}

public sealed class DoorJumpTrace
{
    public string State { get; set; } = "validating";
    public string Reason { get; set; } = "peek-selected";
    public string Door { get; set; } = "";
    public string Target { get; set; } = "";
    public string Source { get; set; } = "";
    public string Personality { get; set; } = "";
    public double Chance { get; set; }
    public double Roll { get; set; }
    public double KnownX { get; set; }
    public double KnownY { get; set; }
    public double KnownZ { get; set; }
    public double ContactAge { get; set; }
    public double Stamina { get; set; }
    public double ForwardSpeed { get; set; }
    public double RunUpDistance { get; set; }
    public double Elapsed { get; set; }
    public double Remaining { get; set; }
    public double ObservationElapsed { get; set; }
    public double StackX { get; set; }
    public double StackY { get; set; }
    public double StackZ { get; set; }
    public double LandingX { get; set; }
    public double LandingY { get; set; }
    public double LandingZ { get; set; }
    public double ActualX { get; set; }
    public double ActualY { get; set; }
    public double ActualZ { get; set; }
    public bool DoorOpenRequested { get; set; }
    public bool DoorOpenObserved { get; set; }
    public bool MovementAccepted { get; set; }
    public bool JumpRequested { get; set; }
    public bool TakeoffObserved { get; set; }
    public bool LandingObserved { get; set; }
    public bool VisualAcquired { get; set; }
    public bool ReturnJumpRequested { get; set; }
    public bool ReturnTakeoffObserved { get; set; }
    public bool ReturnLandingObserved { get; set; }
    public bool Returned { get; set; }
    public double ValidFor { get; set; } = 1.5;
    public double? StyleRoll { get; set; }
    public double? RunByPreference { get; set; }
    public string[] Alternatives { get; set; } = new[] { "Normal SAIN search remains available." };
}

// Pure state machine. Native requests never count as takeoff, landing, vision or arrival.
public sealed class DoorJumpPeek
{
    public DoorJumpTrace? Trace { get; private set; }
    public bool Active => Trace != null && Trace.State != "ended" && Trace.State != "completed";
    private double last,
        phaseAt,
        started = double.NaN,
        blockedUntil;
    private DoorPoint origin,
        known;
    private readonly Dictionary<string, double> evidence = new();

    public void BeginSelected(DoorRunTrace selection, double now)
    {
        if (Active)
            throw new InvalidOperationException("A doorway jump is already active.");
        last = phaseAt = now;
        started = double.NaN;
        known = new(selection.KnownX, selection.KnownY, selection.KnownZ);
        Trace = new()
        {
            Door = selection.Door,
            Target = selection.Target,
            Source = selection.Source,
            Personality = selection.Personality,
            Chance = selection.Chance,
            Roll = selection.Roll,
            StyleRoll = selection.StyleRoll,
            RunByPreference = selection.Preference,
            KnownX = known.X,
            KnownY = known.Y,
            KnownZ = known.Z,
            ContactAge = selection.ContactAge,
            Alternatives = selection.Alternatives,
            DoorOpenRequested = selection.DoorOpenRequested,
            DoorOpenObserved = selection.DoorOpenObserved,
        };
    }

    public bool TrySelect(
        string door,
        string target,
        string source,
        string personality,
        DoorPoint contact,
        double age,
        double now,
        double random,
        DoorJumpPeekConfig c
    )
    {
        if (
            Active
            || !c.Enabled
            || !contact.Finite
            || !DoorPolicy.Finite(now)
            || !DoorPolicy.Finite(age)
            || age < 0
            || age > 120
            || !DoorPolicy.Finite(random)
            || random < 0
            || random >= 1
            || door.Length == 0
            || target.Length == 0
            || now < blockedUntil
        )
            return false;
        var key = door + "\n" + target;
        double stamp = now - age;
        // Bounded per-bot history; all evidence expires with raid state, never retain native door objects.
        evidence[key] = stamp;
        blockedUntil = now + c.Cooldown;
        last = phaseAt = now;
        started = double.NaN;
        known = contact;
        double chance = c.Chances.TryGetValue(personality, out var v) ? v : 0;
        Trace = new()
        {
            Door = door,
            Target = target,
            Source = source,
            Personality = personality,
            Chance = chance,
            Roll = random * 100,
            KnownX = contact.X,
            KnownY = contact.Y,
            KnownZ = contact.Z,
            ContactAge = age,
        };
        if (random * 100 >= chance)
        {
            End("peek-roll-declined", now);
            return false;
        }
        return true;
    }

    private void Phase(string phase, string reason, double now)
    {
        Trace!.State = phase;
        Trace.Reason = reason;
        phaseAt = now;
    }

    public void Opening(double now)
    {
        Trace!.DoorOpenRequested = true;
        Phase("opening", "peek-native-open-requested", now);
    }

    public void Opened(double now)
    {
        Trace!.DoorOpenObserved = true;
        Phase("validating", "peek-open-observed", now);
    }

    public void Start(DoorPoint position, double now)
    {
        origin = position;
        started = now;
        Trace!.MovementAccepted = true;
        Phase("run-up", "peek-building-momentum", now);
    }

    public bool Observe(
        DoorPoint position,
        double speed,
        double stamina,
        bool grounded,
        bool arrived,
        bool visible,
        double age,
        double now,
        DoorJumpPeekConfig c,
        double verticalSpeed = 0
    )
    {
        if (!Active)
            return false;
        var t = Trace!;
        if (
            !position.Finite
            || !DoorPolicy.Finite(speed)
            || !DoorPolicy.Finite(stamina)
            || !DoorPolicy.Finite(age)
            || age < 0
            || !DoorPolicy.Finite(now)
            || now < last
        )
        {
            End("peek-invalid-observation", now);
            return false;
        }
        last = now;
        t.ContactAge = age;
        t.Stamina = stamina;
        t.ForwardSpeed = speed;
        t.ActualX = position.X;
        t.ActualY = position.Y;
        t.ActualZ = position.Z;
        t.VisualAcquired |= visible;
        if (visible)
        {
            End("peek-native-visual-acquired", now);
            return false;
        }
        t.Elapsed = DoorPolicy.Finite(started) ? Math.Max(0, now - started) : 0;
        t.Remaining = Math.Max(0, c.Limit - t.Elapsed);
        if (DoorPolicy.Finite(started) && t.Elapsed >= c.Limit)
        {
            End("peek-time-budget-ended", now);
            return false;
        }
        double phaseTime = now - phaseAt;
        if (t.MovementAccepted && !grounded)
        {
            if (t.State.StartsWith("return"))
                t.ReturnTakeoffObserved = true;
            else
                t.TakeoffObserved = true;
        }
        if (grounded)
        {
            if (t.TakeoffObserved)
                t.LandingObserved = true;
            if (t.ReturnTakeoffObserved)
                t.ReturnLandingObserved = true;
        }
        switch (t.State)
        {
            case "opening":
                if (phaseTime > 4.5)
                    End("peek-door-did-not-open", now);
                break;
            case "run-up":
                t.RunUpDistance = (position - origin).Length;
                if (arrived)
                    Phase("observing", "peek-at-outward-point", now);
                break;
            case "observing":
                t.ObservationElapsed = phaseTime;
                if (phaseTime >= c.Observation)
                    Phase("return-pending", "peek-observation-ended", now);
                break;
            case "returning":
                if (arrived)
                {
                    t.Returned = true;
                    Phase("completed", "peek-return-confirmed", now);
                }
                break;
        }
        return Active;
    }

    public bool MayJump(
        bool canJump,
        bool grounded,
        double remaining,
        double now,
        DoorJumpPeekConfig c
    )
    {
        var t = Trace;
        if (
            t == null
            || !Active
            || !canJump
            || !grounded
            || !DoorPolicy.Finite(remaining)
            || !DoorPolicy.Finite(now)
        )
            return false;
        return (
                t.State == "run-up" && !t.JumpRequested
                || t.State == "returning" && c.ReturnJump && !t.ReturnJumpRequested
            )
            && t.ForwardSpeed >= c.MinimumSpeed;
    }

    public void JumpAccepted(bool accepted, double now)
    {
        var t = Trace!;
        if (!accepted)
            return;
        if (t.State == "run-up")
            t.JumpRequested = true;
        else if (t.State == "returning")
            t.ReturnJumpRequested = true;
    }

    public void ReturnStarted(double now) => Phase("returning", "peek-return-on-foot", now);

    public bool SameContact(string target, DoorPoint contact) =>
        Trace?.Target == target && contact.Finite;

    public void End(string reason, double now)
    {
        if (Trace != null)
        {
            Trace.State = "ended";
            Trace.Reason = reason;
            Trace.Remaining = 0;
        }
    }

    public void Clear()
    {
        Trace = null;
        evidence.Clear();
        blockedUntil = last = phaseAt = 0;
        started = double.NaN;
    }
}

public static class DoorJumpGeometry
{
    public static bool ContainsMotion(
        DoorGeometry g,
        DoorPoint stack,
        DoorPoint landing,
        DoorPoint position
    )
    {
        if (!position.Finite || !stack.Finite || !landing.Finite)
            return false;
        double side = Math.Sign((stack - g.Center).Dot(g.Normal));
        if (
            side == 0
            || (position - g.Center).Dot(g.Normal) * side < .5
            || position.Y < stack.Y - .35
            || position.Y > stack.Y + 1.1
        )
            return false;
        var flat = new DoorPoint(position.X, stack.Y, position.Z);
        var delta = new DoorPoint(landing.X - stack.X, 0, landing.Z - stack.Z);
        var projection =
            stack
            + delta
                * Math.Max(
                    0,
                    Math.Min(1, (flat - stack).Dot(delta) / Math.Max(.001, delta.Dot(delta)))
                );
        return (flat - projection).Length <= .5;
    }

    public static DoorPoint Landing(DoorGeometry g, DoorPoint stack)
    {
        double sign = Math.Sign((stack - g.Center).Dot(g.Normal));
        return g.Center
            + g.Normal * (sign * 1.25)
            + g.Axis * (-Math.Sign((stack - g.Center).Dot(g.Axis)) * .25);
    }

    public static string Route(
        DoorGeometry g,
        DoorPoint stack,
        DoorPoint raw,
        DoorPoint sampled,
        IReadOnlyList<DoorPoint> path,
        bool complete
    )
    {
        if (!stack.Finite || !sampled.Finite)
            return "peek-invalid-landing";
        if (!complete || path.Count < 2)
            return "peek-incomplete-route";
        return "";
    }
}
