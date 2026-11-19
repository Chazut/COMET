using System;
using System.Collections.Generic;

namespace Comet.Core;

public sealed class CombatStepConfig
{
    public bool Enabled { get; set; } = true;
    public double MinDistance { get; set; } = 3;
    public double MaxDistance { get; set; } = 25;
    public double Radius { get; set; } = 1.5;
    public double StepMin { get; set; } = .35;
    public double StepMax { get; set; } = .7;
    public double SequenceMin { get; set; } = 1.5;
    public double SequenceMax { get; set; } = 3;
    public double CooldownMin { get; set; } = 2;
    public double CooldownMax { get; set; } = 4;
    public Dictionary<string, double> Chances { get; set; } =
        new()
        {
            ["Normal"] = 100,
            ["Chad"] = 100,
            ["GigaChad"] = 100,
            ["Wreckless"] = 100,
            ["Rat"] = 0,
            ["SnappingTurtle"] = 0,
            ["Coward"] = 0,
            ["Timmy"] = 0,
        };

    public void Validate()
    {
        static void Range(double v, double min, double max, string name)
        {
            if (!DoorPolicy.Finite(v) || v < min || v > max)
                throw new ArgumentException("Combat movement: invalid " + name);
        }
        Range(MinDistance, 1.5, 15, nameof(MinDistance));
        Range(MaxDistance, 5, 40, nameof(MaxDistance));
        Range(Radius, .5, 2, nameof(Radius));
        Range(StepMin, .25, 1, nameof(StepMin));
        Range(StepMax, .25, 1, nameof(StepMax));
        Range(SequenceMin, 1, 5, nameof(SequenceMin));
        Range(SequenceMax, 1, 5, nameof(SequenceMax));
        Range(CooldownMin, 1, 15, nameof(CooldownMin));
        Range(CooldownMax, 1, 15, nameof(CooldownMax));
        if (
            MinDistance >= MaxDistance
            || StepMin > StepMax
            || SequenceMin > SequenceMax
            || CooldownMin > CooldownMax
            || StepMax > SequenceMin
        )
            throw new ArgumentException("Combat movement: minimum must not exceed maximum.");
        var names = new[]
        {
            "Normal",
            "Chad",
            "GigaChad",
            "Wreckless",
            "Rat",
            "SnappingTurtle",
            "Coward",
            "Timmy",
        };
        if (Chances == null || Chances.Count != names.Length)
            throw new ArgumentException("Combat movement requires all eight personalities.");
        foreach (var name in names)
        {
            if (!Chances.TryGetValue(name, out var v))
                throw new ArgumentException("Combat movement: missing " + name);
            Range(v, 0, 100, name);
        }
    }
}

public sealed class CombatStepFrame
{
    public bool Enabled = true,
        Pmc = true,
        Authority = true,
        Active = true,
        Ghost,
        Visible = true,
        Ready = true,
        Grounded = true,
        NativeRouteValid = true;
    public bool Urgent,
        Busy,
        ProtectedCover,
        Immobile,
        Sprinting;
    public string Target = "",
        Action = "StandAndShoot",
        Personality = "Normal",
        Source = "personal-vision";
    public double Now,
        Distance,
        ContactAge;
    public DoorPoint Position,
        Known;
}

public sealed class CombatStepTrace
{
    public string State = "planning",
        Reason = "movement-selected",
        Target = "",
        Action = "",
        Personality = "",
        Source = "",
        Direction = "";
    public double Chance,
        Roll,
        Remaining,
        StepRemaining,
        ValidFor,
        ActualDistance,
        Displacement,
        ContactAge,
        KnownX,
        KnownY,
        KnownZ;
    public double AnchorX,
        AnchorY,
        AnchorZ,
        GoalX,
        GoalY,
        GoalZ,
        Duration;
    public int Requests,
        Episode,
        CompletedSteps;
    public bool Rolled,
        Requested,
        MovementObserved;
    public string[] Alternatives = Array.Empty<string>();
}

// Pure, bounded policy. An accepted movement request never counts as observed movement.
public sealed class CombatStep
{
    public CombatStepTrace? Trace { get; private set; }
    public bool Active => Trace?.State is "planning" or "moving";
    public double NextAttempt { get; private set; }
    public double Deadline { get; private set; }
    public double StepDeadline { get; private set; }
    public DoorPoint Anchor { get; private set; }
    private DoorPoint previous,
        progress;
    private double last,
        started,
        progressAt,
        cooldown;
    private int episode;
    private double stepTravel;
    private bool stepCounted;

    public static string Gate(CombatStepFrame f, CombatStepConfig c)
    {
        if (!c.Enabled || !f.Enabled || !f.Pmc || !f.Authority || !f.Active || f.Ghost)
            return "movement-out-of-scope";
        if (!f.NativeRouteValid)
            return "movement-route-replaced";
        if (f.Action != "StandAndShoot" && f.Action != "DogFight")
            return "movement-native-action-changed";
        if ((!f.Visible && f.ContactAge >= 1) || string.IsNullOrEmpty(f.Target))
            return "movement-no-visible-contact";
        if (
            !Finite(f.Position)
            || !Finite(f.Known)
            || !DoorPolicy.Finite(f.Now)
            || f.Now < 0
            || !DoorPolicy.Finite(f.ContactAge)
            || f.ContactAge < 0
            || !DoorPolicy.Finite(f.Distance)
            || f.Distance < c.MinDistance
            || f.Distance > c.MaxDistance
        )
            return "movement-contact-out-of-range";
        if (f.Busy)
            return "movement-native-priority";
        if (f.Immobile)
            return "movement-mobility-unavailable";
        return "";
    }

    public bool Begin(
        CombatStepFrame f,
        CombatStepConfig c,
        double roll,
        double durationRoll,
        double cooldownRoll
    )
    {
        if (
            Active
            || !DoorPolicy.Finite(f.Now)
            || f.Now < NextAttempt
            || !Unit(roll)
            || !Unit(durationRoll)
            || !Unit(cooldownRoll)
        )
            return false;
        cooldown = 0;
        NextAttempt = f.Now;
        string gate = Gate(f, c);
        double chance = c.Chances.TryGetValue(f.Personality, out var value) ? value : 0;
        Trace = new()
        {
            Episode = ++episode,
            Target = f.Target,
            Action = f.Action,
            Personality = f.Personality,
            Chance = chance,
            Roll = roll * 100,
            Source = f.Source,
            ContactAge = f.ContactAge,
            KnownX = f.Known.X,
            KnownY = f.Known.Y,
            KnownZ = f.Known.Z,
            AnchorX = f.Position.X,
            AnchorY = f.Position.Y,
            AnchorZ = f.Position.Z,
        };
        if (gate.Length > 0)
        {
            End(gate, f.Now);
            return false;
        }
        Trace.Rolled = true;
        if (roll * 100 >= chance)
        {
            End("movement-roll-declined", f.Now);
            return false;
        }
        Anchor = previous = progress = f.Position;
        last = started = progressAt = f.Now;
        // DiamondStepper runs for the engagement, not short COMET sequences.
        Deadline = double.MaxValue;
        StepDeadline = f.Now;
        Trace.Duration = Trace.Remaining = Deadline - f.Now;
        Trace.ValidFor = .4;
        return true;
    }

    public bool Observe(CombatStepFrame f, CombatStepConfig c, bool firingPause = false)
    {
        if (!Active)
            return false;
        string gate = Gate(f, c);
        if (gate.Length > 0)
        {
            End(gate, f.Now);
            return false;
        }
        if (f.Target != Trace!.Target)
        {
            End("movement-contact-replaced", f.Now);
            return false;
        }
        if (f.Now < last)
        {
            End("movement-observation-gap", f.Now);
            return false;
        }

        double delta = (f.Position - previous).Length;
        if (Trace.Requested)
            Trace.ActualDistance += delta;
        if (Trace.Requested && !firingPause)
            stepTravel += delta;
        if (Trace.Requested && !stepCounted && !firingPause && f.Now >= StepDeadline)
        {
            Trace.CompletedSteps++;
            stepCounted = true;
        }
        Trace.Displacement = (f.Position - Anchor).Length;
        Trace.MovementObserved |= Trace.Requested && Trace.ActualDistance >= .1;
        Trace.Action = f.Action;
        Trace.ContactAge = f.ContactAge;
        Trace.KnownX = f.Known.X;
        Trace.KnownY = f.Known.Y;
        Trace.KnownZ = f.Known.Z;
        previous = f.Position;
        last = f.Now;
        if ((f.Position - progress).Length >= .08)
        {
            progress = f.Position;
            progressAt = f.Now;
        }
        Trace.Remaining = Math.Max(0, Deadline - f.Now);
        Trace.StepRemaining = Math.Max(0, StepDeadline - f.Now);
        Trace.ValidFor = Math.Min(.4, Trace.Remaining);
        if (f.Now >= Deadline)
        {
            End(
                Trace.MovementObserved
                    ? "movement-sequence-finished"
                    : "movement-no-motion-observed",
                f.Now
            );
            return false;
        }
        return true;
    }

    public bool Request(
        DoorPoint goal,
        string direction,
        double now,
        double durationRoll,
        CombatStepConfig c
    )
    {
        if (
            !Active
            || !Unit(durationRoll)
            || !DoorPolicy.Finite(now)
            || now < last
            || now >= Deadline
            || now < StepDeadline
            || !Finite(goal)
        )
            return false;
        Trace!.State = "moving";
        Trace.Reason = "movement-step-requested";
        Trace.Direction = direction;
        stepTravel = 0;
        stepCounted = false;
        Trace.GoalX = goal.X;
        Trace.GoalY = goal.Y;
        Trace.GoalZ = goal.Z;
        Trace.Requested = true;
        Trace.Requests++;
        StepDeadline = Math.Min(Deadline, now + Mix(c.StepMin, c.StepMax, durationRoll));
        Trace.StepRemaining = StepDeadline - now;
        Trace.ValidFor = Math.Min(.4, Deadline - now);
        return true;
    }

    public void End(string reason, double now)
    {
        if (Trace == null)
            return;
        Trace.State = "ended";
        Trace.Reason = reason;
        Trace.ValidFor = Trace.StepRemaining = Trace.Remaining = 0;
        if (DoorPolicy.Finite(now))
            NextAttempt = Math.Max(NextAttempt, now + cooldown);
    }

    public void DeferUntil(double now)
    {
        if (DoorPolicy.Finite(now))
            NextAttempt = Math.Max(NextAttempt, now);
    }

    public void ResumeAfterPause(double now)
    {
        if (!Active || !DoorPolicy.Finite(now))
            return;
        // Pause consumes the parent sequence, but cannot be counted as a stuck moving step.
        progressAt = now;
        progress = previous;
        StepDeadline = now;
        stepTravel = 0;
        stepCounted = true;
    }

    public static bool Inside(DoorPoint anchor, DoorPoint p, double radius) =>
        Finite(anchor)
        && Finite(p)
        && DoorPolicy.Finite(radius)
        && radius > 0
        && Math.Abs(p.Y - anchor.Y) <= .6
        && (p - anchor).Length <= radius;

    public static bool Route(
        DoorPoint anchor,
        DoorPoint start,
        DoorPoint end,
        DoorPoint[] route,
        bool complete,
        double radius
    )
    {
        if (!complete || route == null || route.Length < 2 || !Finite(start) || !Finite(end))
            return false;
        foreach (var point in route)
            if (!Finite(point))
                return false;
        return true;
    }

    public static bool UsefulCover(int mask) => mask >= 0 && mask <= 7 && (mask & 3) != 0;

    private static bool Finite(DoorPoint p) =>
        DoorPolicy.Finite(p.X) && DoorPolicy.Finite(p.Y) && DoorPolicy.Finite(p.Z);

    private static bool Unit(double v) => DoorPolicy.Finite(v) && v >= 0 && v < 1;

    private static double Mix(double a, double b, double t) => a + (b - a) * t;
}
