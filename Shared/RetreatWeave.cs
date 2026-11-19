using System;

namespace Comet.Core;

public sealed class RetreatWeaveConfig
{
    public bool Enabled { get; set; } = true;
    public bool Weave { get; set; } = true;
    public bool HeadDown { get; set; } = true;
    public bool Jumps { get; set; } = true;
    public double Angle { get; set; } = 30;
    public double SwitchMin { get; set; } = .45;
    public double SwitchMax { get; set; } = .8;
    public double Pitch { get; set; } = 55;
    public double Distance { get; set; } = 80;
    public double SwitchJumpChance { get; set; } = 20;
    public double LandingJumpChance { get; set; } = 60;
    public double JumpMinSpeed { get; set; } = 3.8;
    public double Refresh { get; set; } = .1;

    public void Validate()
    {
        CometConfig.Range(Angle, 5, 45, nameof(Angle));
        CometConfig.Range(SwitchMin, .3, 1.5, nameof(SwitchMin));
        CometConfig.Range(SwitchMax, .3, 2, nameof(SwitchMax));
        if (SwitchMax < SwitchMin)
            throw new ArgumentException("Switch maximum must be at least the minimum.");
        CometConfig.Range(Pitch, 10, 65, nameof(Pitch));
        CometConfig.Range(Distance, 10, 150, nameof(Distance));
        CometConfig.Range(SwitchJumpChance, 0, 100, nameof(SwitchJumpChance));
        CometConfig.Range(LandingJumpChance, 0, 100, nameof(LandingJumpChance));
        CometConfig.Range(JumpMinSpeed, 3, 6, nameof(JumpMinSpeed));
        CometConfig.Range(Refresh, .05, .2, nameof(Refresh));
    }
}

public sealed class RetreatWeaveFrame
{
    public double Now,
        VisionAge = 1000,
        FireAge = 1000,
        Distance,
        RearAngle,
        Speed,
        ForwardDot,
        GoalDistance,
        GoalHeight,
        GeometryAge = 1000,
        ObservedYaw,
        ObservedPitch;
    public string Action = "",
        Target = "",
        Context = "",
        Source = "",
        Block = "";
    public DoorPoint Position,
        Known,
        Goal;
    public bool Sprinting,
        Grounded = true,
        CanJump,
        Left,
        Right,
        Straight,
        JumpSafe,
        LandingSafe,
        AtCover;
}

public sealed class RetreatWeaveTrace
{
    public string State = "native",
        Reason = "",
        Action = "",
        Target = "",
        Context = "",
        Source = "",
        Jump = "",
        JumpOutcome = "none";
    public double ValidFor,
        Yaw,
        Pitch,
        Speed,
        ObservedYaw,
        ObservedPitch,
        ContactAge,
        GoalDistance,
        KnownX,
        KnownY,
        KnownZ,
        X,
        Y,
        Z;
    public int Switches,
        JumpRequests,
        AcceptedJumps,
        Takeoffs,
        Landings;
    public bool MoveRequested,
        GazeRequested,
        Grounded,
        LandingRollSpent;
    public string[] Alternatives = Array.Empty<string>();
}

// R_F's alternating sprint weave and two jump opportunities, using frozen personal evidence.
// Interruptions do not reset the landing roll for the same cover. Actual takeoff is not inferred from acceptance.
public sealed class RetreatWeavePolicy
{
    public bool Active { get; private set; }
    public RetreatWeaveTrace? Trace { get; private set; }
    private string context = "",
        target = "";
    private double nextSwitch,
        last = -1,
        lastSprint = -1000,
        headUntil,
        straightUntil,
        pendingUntil,
        acceptedAt = -1000,
        progressAt;
    private double bestDistance;
    private static readonly string[] Choices =
    {
        "Zigzag: clear side on the native escape route",
        "Straight sprint: obstacle, flight or no fresh clearance",
        "Jump: forward speed, native permission and verified landing",
        "SAIN: firing, changed action, stop or no progress",
    };
    private int sign = 1,
        switches,
        requests,
        accepted,
        takeoffs,
        landings;
    private bool landingSpent,
        awaitingTakeoff,
        airborne,
        stuck;
    private string outcome = "none";

    public static string Gate(RetreatWeaveFrame f, RetreatWeaveConfig c)
    {
        if (!c.Enabled || !c.Weave && !c.HeadDown && !c.Jumps)
            return "weave-disabled";
        if (f.Block.Length > 0)
            return f.Block;
        if (f.Target.Length == 0 || f.Context.Length == 0)
            return "weave-no-personal-threat";
        if (
            !CornerChase.Point(f.Position)
            || !CornerChase.Point(f.Known)
            || !CornerChase.Point(f.Goal)
        )
            return "weave-invalid-position";
        if (
            !Nonnegative(f.Now)
            || !Nonnegative(f.VisionAge)
            || !Nonnegative(f.FireAge)
            || !Nonnegative(f.Distance)
            || !Nonnegative(f.RearAngle)
            || !Nonnegative(f.Speed)
            || !Nonnegative(f.GoalDistance)
            || !Nonnegative(f.GoalHeight)
            || !Nonnegative(f.GeometryAge)
        )
            return "weave-invalid-evidence";
        if (
            !DoorPolicy.Finite(f.ForwardDot)
            || Math.Abs(f.ForwardDot) > 1
            || !DoorPolicy.Finite(f.ObservedYaw)
            || !DoorPolicy.Finite(f.ObservedPitch)
        )
            return "weave-invalid-motion";
        if (f.Distance > c.Distance)
            return "weave-threat-too-far";
        if (f.AtCover || f.GoalDistance < 1.1)
            return "weave-cover-reached";
        if (f.Action != "SeekCover" && f.Action != "Retreat" && f.Action != "RunAway")
            return "weave-native-action-priority";
        return "";
    }

    public bool Tick(RetreatWeaveFrame f, RetreatWeaveConfig c, Func<double> random)
    {
        if (context != f.Context || target != f.Target)
        {
            End("weave-new-context");
            context = f.Context;
            target = f.Target;
            landingSpent = stuck = false;
            switches = requests = accepted = takeoffs = landings = 0;
            awaitingTakeoff = airborne = false;
            outcome = "none";
            straightUntil = pendingUntil = 0;
        }
        ObserveFlight(f.Grounded, f.Now);
        string reason = Gate(f, c);
        if (reason.Length == 0 && f.Now < last)
            reason = "weave-clock-reset";
        bool was = Active;
        Active = false;
        if (f.Sprinting)
            lastSprint = f.Now;
        bool running = f.Sprinting || was && f.Now - lastSprint < .3 && f.Speed > 2.5;
        if (reason.Length == 0 && !running)
            reason = "weave-not-running";
        if (
            reason.Length == 0
            && (was ? Math.Min(f.VisionAge, f.FireAge) >= 10 : f.VisionAge >= 2 && f.FireAge >= 3)
        )
            reason = "weave-threat-memory-expired";
        if (
            reason.Length == 0
            && f.RearAngle <= 90
            && !(was && f.Now < headUntil && f.RearAngle > 80)
        )
            reason = "weave-threat-no-longer-behind";
        double yaw = 0,
            pitch = 0;
        string jump = "";
        if (reason.Length == 0)
        {
            if (!was)
            {
                sign = random() < .5 ? -1 : 1;
                nextSwitch = f.Now + Interval(c, random);
                headUntil = f.Now + .8;
                progressAt = f.Now;
                bestDistance = f.GoalDistance;
            }
            {
                Active = true;
                reason = "weave-running-from-known-threat";
                pitch = c.HeadDown ? c.Pitch : 0;
                bool fresh = f.GeometryAge <= c.Refresh + .05;
                bool fast = f.Speed >= c.JumpMinSpeed && f.ForwardDot > .85;
                bool canJump =
                    c.Jumps
                    && fresh
                    && fast
                    && f.CanJump
                    && f.Grounded
                    && !awaitingTakeoff
                    && f.Now >= straightUntil;
                if (
                    canJump
                    && !landingSpent
                    && f.GoalDistance >= 3
                    && f.GoalDistance <= 5
                    && f.GoalHeight <= .4
                    && f.LandingSafe
                    && f.JumpSafe
                    && f.GoalDistance <= Math.Max(3, Math.Min(5, f.Speed * .7))
                )
                {
                    landingSpent = true;
                    if (random() * 100 < c.LandingJumpChance)
                        jump = "landing";
                }
                if (jump.Length == 0 && f.Grounded && f.Now >= straightUntil && !awaitingTakeoff)
                {
                    if (f.Now >= nextSwitch)
                    {
                        sign = -sign;
                        switches++;
                        nextSwitch = f.Now + Interval(c, random);
                        if (c.Jumps && random() * 100 < c.SwitchJumpChance)
                            pendingUntil = f.Now + .5;
                    }
                    if (f.GoalDistance < 6)
                        pendingUntil = 0;
                    if (f.Now < pendingUntil && canJump && f.JumpSafe)
                    {
                        jump = "switch";
                        pendingUntil = 0;
                    }
                    if (jump.Length == 0 && c.Weave && fresh)
                    {
                        bool clear = sign < 0 ? f.Left : f.Right,
                            other = sign < 0 ? f.Right : f.Left;
                        if (!clear && other)
                        {
                            sign = -sign;
                            nextSwitch = f.Now + Interval(c, random);
                            clear = true;
                        }
                        if (clear)
                            yaw = sign * c.Angle;
                        else
                            reason = "weave-blocked-use-native-route";
                    }
                }

                if (jump.Length > 0)
                {
                    requests++;
                    straightUntil = f.Now + .8;
                    pendingUntil = 0;
                    yaw = 0;
                }
            }
        }
        if (!Active)
        {
            yaw = pitch = 0;
            jump = "";
            pendingUntil = 0;
        }
        last = f.Now;
        Trace = new()
        {
            State = Active ? "ready" : "native",
            Reason = reason,
            Action = f.Action,
            Target = f.Target,
            Context = f.Context,
            Source = f.Source,
            Yaw = yaw,
            Pitch = pitch,
            Jump = jump,
            JumpOutcome = outcome,
            Speed = Safe(f.Speed),
            ObservedYaw = Safe(f.ObservedYaw),
            ObservedPitch = Safe(f.ObservedPitch),
            ContactAge = Safe(Math.Min(f.VisionAge, f.FireAge)),
            GoalDistance = Safe(f.GoalDistance),
            Grounded = f.Grounded,
            LandingRollSpent = landingSpent,
            Switches = switches,
            JumpRequests = requests,
            AcceptedJumps = accepted,
            Takeoffs = takeoffs,
            Landings = landings,
            KnownX = Safe(f.Known.X),
            KnownY = Safe(f.Known.Y),
            KnownZ = Safe(f.Known.Z),
            X = Safe(f.Goal.X),
            Y = Safe(f.Goal.Y),
            Z = Safe(f.Goal.Z),
            Alternatives = Choices,
        };
        return Active;
    }

    public bool ObserveFlight(bool grounded, double now)
    {
        bool changed = false;
        if (awaitingTakeoff && !grounded)
        {
            awaitingTakeoff = false;
            airborne = true;
            takeoffs++;
            outcome = "airborne-observed";
            changed = true;
        }
        if (airborne && grounded)
        {
            airborne = false;
            landings++;
            outcome = "landing-observed";
            changed = true;
        }
        if (awaitingTakeoff && now - acceptedAt > .5)
        {
            awaitingTakeoff = false;
            outcome = "takeoff-not-observed";
            changed = true;
        }
        if (changed && Trace != null)
        {
            Trace.JumpOutcome = outcome;
            Trace.Takeoffs = takeoffs;
            Trace.Landings = landings;
            Trace.Grounded = grounded;
        }
        return changed;
    }

    public void JumpResult(bool success, double now)
    {
        if (Trace == null)
            return;
        outcome = success ? "accepted-awaiting-takeoff" : "native-refused";
        if (success)
        {
            accepted++;
            awaitingTakeoff = true;
            acceptedAt = now;
        }
        Trace.AcceptedJumps = accepted;
        Trace.JumpOutcome = outcome;
        if (Active)
            Trace.State =
                Trace.MoveRequested || Trace.GazeRequested || success ? "evading" : "ready";
    }

    public void Requested(bool move, bool gaze, RetreatWeaveConfig c)
    {
        if (Trace == null)
            return;
        Trace.MoveRequested = Active && move;
        Trace.GazeRequested = Active && gaze;
        bool applied = Active && (move || gaze || awaitingTakeoff || airborne);
        Trace.State = Active ? (applied ? "evading" : "ready") : Trace.State;
        Trace.ValidFor = applied ? Math.Min(.25, c.Refresh + .1) : 0;
    }

    public void End(string reason)
    {
        Active = false;
        pendingUntil = 0;
        if (Trace != null)
        {
            Trace.State = "ended";
            Trace.Reason = reason;
            Trace.ValidFor = Trace.Yaw = Trace.Pitch = 0;
            Trace.MoveRequested = Trace.GazeRequested = false;
            Trace.Jump = "";
        }
    }

    private static double Interval(RetreatWeaveConfig c, Func<double> r) =>
        c.SwitchMin + (c.SwitchMax - c.SwitchMin) * r();

    private static bool Nonnegative(double v) => DoorPolicy.Finite(v) && v >= 0;

    private static double Safe(double v) => DoorPolicy.Finite(v) ? v : 0;
}
