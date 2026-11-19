using System;
using System.Collections.Generic;

namespace Comet.Core;

public sealed class QuietApproachConfig
{
    public bool Enabled { get; set; } = true;
    public double WalkDistance { get; set; } = 30;
    public double CrouchDistance { get; set; } = 12;
    public double WalkExitMargin { get; set; } = 3;
    public double CrouchExitMargin { get; set; } = 2;
    public double Budget { get; set; } = 20;
    public double ContactAge { get; set; } = 10;
    public double SeenDelay { get; set; } = 3;
    public double FireDelay { get; set; } = 5;
    public double WalkSpeed { get; set; } = .7;
    public double CrouchSpeed { get; set; } = .45;
    public double CrouchPose { get; set; } = .6;
    public double LootBonus { get; set; } = .15;
    public double LootCap { get; set; } = .95;
    public double LootWindow { get; set; } = 2;
    public double ResetSilence { get; set; } = 30;
    public Dictionary<string, double> Chances { get; set; } =
        new Dictionary<string, double>
        {
            ["Rat"] = .9,
            ["SnappingTurtle"] = .9,
            ["Coward"] = .9,
            ["Timmy"] = .8,
            ["Normal"] = .7,
            ["Chad"] = .5,
            ["GigaChad"] = .3,
            ["Wreckless"] = .15,
        };

    public void Validate()
    {
        CometConfig.Range(WalkDistance, 5, 60, nameof(WalkDistance));
        CometConfig.Range(CrouchDistance, 1, WalkDistance, nameof(CrouchDistance));
        CometConfig.Range(WalkExitMargin, 0, 10, nameof(WalkExitMargin));
        CometConfig.Range(CrouchExitMargin, 0, 5, nameof(CrouchExitMargin));
        if (CrouchDistance + CrouchExitMargin > WalkDistance)
            throw new ArgumentException("Crouch exit must not exceed walk entry.");
        CometConfig.Range(Budget, 1, 40, nameof(Budget));
        CometConfig.Range(ContactAge, 1, 30, nameof(ContactAge));
        CometConfig.Range(SeenDelay, 0, 10, nameof(SeenDelay));
        CometConfig.Range(FireDelay, 1, 15, nameof(FireDelay));
        CometConfig.Range(WalkSpeed, .4, 1, nameof(WalkSpeed));
        CometConfig.Range(CrouchSpeed, .25, WalkSpeed, nameof(CrouchSpeed));
        CometConfig.Range(CrouchPose, .4, 1, nameof(CrouchPose));
        CometConfig.Range(LootBonus, 0, .3, nameof(LootBonus));
        CometConfig.Range(LootCap, 0, 1, nameof(LootCap));
        CometConfig.Range(LootWindow, .5, 5, nameof(LootWindow));
        CometConfig.Range(ResetSilence, 20, 120, nameof(ResetSilence));
        if (Chances == null || Chances.Count != 8)
            throw new ArgumentException("Missing quiet approach personality chances.");
        foreach (
            var name in new[]
            {
                "Rat",
                "SnappingTurtle",
                "Coward",
                "Timmy",
                "Normal",
                "Chad",
                "GigaChad",
                "Wreckless",
            }
        )
            if (!Chances.TryGetValue(name, out var value))
                throw new ArgumentException("Missing " + name);
            else
                CometConfig.Range(value, 0, 1, name);
    }
}

public sealed class QuietFrame
{
    public double Now,
        Age,
        Path,
        SinceSeen = double.MaxValue,
        SinceFire = double.MaxValue,
        X,
        Y,
        Z;
    public string Target = "",
        Personality = "Normal",
        Action = "",
        Source = "";
    public int TargetInstance;
    public bool Executing = true;
    public bool Scoped,
        Enabled,
        HasThreat,
        HasContact,
        Complete,
        Visible,
        OtherVisibleThreat,
        Urgent,
        Panic,
        Suppressed,
        Paused,
        Blocked,
        LootHeard;
}

public sealed class QuietTrace
{
    public string State { get; set; } = "normal";
    public string Reason { get; set; } = "quiet-no-contact";
    public string Target { get; set; } = "";
    public string Action { get; set; } = "";
    public string Source { get; set; } = "";
    public string Personality { get; set; } = "";
    public long Encounter { get; set; }
    public long Draw { get; set; }
    public double At { get; set; }
    public double? Age { get; set; }
    public double? Path { get; set; }
    public double? Chance { get; set; }
    public double? Roll { get; set; }
    public bool LootBonus { get; set; }
    public double Used { get; set; }
    public double Remaining { get; set; }
    public double Stalled { get; set; }
    public double SpeedCap { get; set; } = 1;
    public double PoseCap { get; set; } = 1;
    public bool Active { get; set; }
    public bool Applied { get; set; }
    public double ValidFor { get; set; }
    public double? NativeSpeed { get; set; }
    public double? AppliedSpeed { get; set; }
    public double? NativePose { get; set; }
    public double? AppliedPose { get; set; }

    public QuietTrace Copy() => (QuietTrace)MemberwiseClone();
}

// Per-PMC encounter memory. Target/action changes cannot refund time or reroll a previous target.
public sealed class QuietApproach
{
    private readonly QuietApproachConfig config;
    private readonly Func<double> random;

    private sealed class Draw
    {
        public double Roll,
            Chance;
        public bool Loot;
        public long Index;
    }

    private readonly Dictionary<string, Draw> draws = new Dictionary<string, Draw>();
    private readonly List<(double X, double Y, double Z)> trail =
        new List<(double, double, double)>();
    private double last = -1,
        silence = -1,
        used,
        stalled;
    private bool previousActive,
        exhausted,
        blocked;
    private string band = "normal",
        bandTarget = "";
    private long encounter = 1,
        drawIndex;
    public QuietTrace Current { get; private set; } = new QuietTrace();
    public int RememberedTargets => draws.Count;

    public QuietApproach(QuietApproachConfig config, Func<double> random)
    {
        this.config = config;
        this.random = random;
    }

    private static bool Finite(double n) => !double.IsNaN(n) && !double.IsInfinity(n);

    public void Clear()
    {
        chosen = "";
        applied = -1000;
        draws.Clear();
        trail.Clear();
        last = silence = -1;
        used = stalled = 0;
        previousActive = exhausted = blocked = false;
        band = "normal";
        bandTarget = "";
        encounter++;
        Current = new QuietTrace();
    }

    public QuietTrace Stop(double now, string reason)
    {
        previousActive = false;
        band = "normal";
        bandTarget = "";
        stalled = 0;
        trail.Clear();
        last = Finite(now) ? now : last;
        var t = Current.Copy();
        t.At = Finite(now) ? now : 0;
        t.Active = t.Applied = false;
        t.State = "normal";
        t.Reason = reason;
        t.ValidFor = 0;
        Current = t;
        return t;
    }

    private double started,
        applied = -1000;
    private string chosen = "";

    public QuietTrace Evaluate(QuietFrame f)
    {
        var t = new QuietTrace
        {
            At = f.Now,
            Target = f.Target,
            Action = f.Action,
            Source = f.Source,
            Personality = f.Personality,
            Age = f.Age,
            Path = f.Path,
        };
        QuietTrace Reject(string reason)
        {
            t.Reason = reason;
            Current = t;
            return t;
        }
        if (!f.Scoped || !f.Enabled || !config.Enabled)
            return Reject("quiet-out-of-scope");
        if (f.Visible || f.SinceSeen < config.SeenDelay)
            return Reject("quiet-visible-or-recent-fight");
        if (f.SinceFire < config.FireDelay || f.Urgent)
            return Reject("quiet-urgent");
        if (!Finite(f.Path) || f.Path <= 0 || f.Path > config.WalkDistance)
            return Reject("quiet-too-far");
        var key = f.Target + ":" + f.TargetInstance;
        if (chosen != key || f.Now - applied > 30)
        {
            chosen = key;
            draws.Clear();
            started = f.Now;
            var chance =
                f.LootHeard ? 1
                : config.Chances.TryGetValue(f.Personality, out var p) ? p
                : .6;
            draws[key] = new Draw
            {
                Chance = chance,
                Roll = random(),
                Loot = f.LootHeard,
                Index = ++drawIndex,
            };
        }
        applied = f.Now;
        var d = draws[key];
        t.Roll = d.Roll;
        t.Chance = d.Chance;
        t.LootBonus = d.Loot;
        t.Draw = d.Index;
        if (d.Roll >= d.Chance)
            return Reject("quiet-roll-declined");
        t.Used = Math.Max(0, f.Now - started);
        t.Remaining = Math.Max(0, config.Budget - t.Used);
        if (t.Used > config.Budget)
            return Reject("quiet-budget-ended");
        bool crouch = f.Path <= config.CrouchDistance;
        t.State = crouch ? "crouch" : "walk";
        t.Active = true;
        t.Reason = crouch ? "quiet-close-approach" : "quiet-approach";
        t.SpeedCap = crouch ? config.CrouchSpeed : config.WalkSpeed;
        t.PoseCap = crouch ? config.CrouchPose : 1;
        Current = t;
        return t;
    }
}
