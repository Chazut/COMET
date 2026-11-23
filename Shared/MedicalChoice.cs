using System;
using System.Collections.Generic;
using System.Linq;

namespace Comet.Core;

public sealed class MedicalChoiceConfig
{
    public bool Enabled { get; set; } = true;
    public double Interval { get; set; } = .5;
    public double HitDelay { get; set; } = 2;
    public double EmergencyStimDelay { get; set; } = .5;
    public double StimHealth { get; set; } = .35;
    public double FootstepAge { get; set; } = 5;
    public double FootstepDistance { get; set; } = 30;
    public double SurgeryQuiet { get; set; } = 25;
    public double SurgeryDistance { get; set; } = 40;
    public double RetryDelay { get; set; } = 3;

    public void Validate()
    {
        CometConfig.Range(Interval, .25, 2, nameof(Interval));
        CometConfig.Range(HitDelay, .5, 10, nameof(HitDelay));
        CometConfig.Range(EmergencyStimDelay, .25, HitDelay, nameof(EmergencyStimDelay));
        CometConfig.Range(StimHealth, .1, .6, nameof(StimHealth));
        CometConfig.Range(FootstepAge, 1, 15, nameof(FootstepAge));
        CometConfig.Range(FootstepDistance, 5, 60, nameof(FootstepDistance));
        CometConfig.Range(SurgeryQuiet, 10, 90, nameof(SurgeryQuiet));
        CometConfig.Range(SurgeryDistance, 20, 100, nameof(SurgeryDistance));
        CometConfig.Range(RetryDelay, 1, 15, nameof(RetryDelay));
    }
}

public sealed class MedicalOption
{
    public string Action { get; set; } = "Wait";
    public string Item { get; set; } = "";
    public string Part { get; set; } = "";
    public string Need { get; set; } = "";
    public bool Usable { get; set; }
    public double Duration { get; set; }
    public double Resource { get; set; }
    public double Score { get; set; }
    public string Reason { get; set; } = "medical-unavailable";

    public MedicalOption Copy() => (MedicalOption)MemberwiseClone();
}

public sealed class MedicalFrame
{
    public double Now,
        Health = 1,
        SinceHit = 999,
        Aggression = .5;
    public bool HeavyBleed,
        LightBleed,
        Vital,
        ReachedCover,
        ProtectiveCover,
        Moving,
        Visible,
        Footsteps,
        ThreatsIncomplete;
    public double Nearest = 100000,
        Quiet = 999;
    public int Contacts,
        Meds;
    public bool RunningToCover,
        LegsHold,
        InHisView,
        CoverTowardHim;
    public string FootstepContact = "";
    public string Contact = "",
        Priority = "";
    public string Personality = "";
    public string? SurgeryCheck;
    public bool HasKnownEnemy;
    public double? X,
        Y,
        Z;
    public readonly List<MedicalOption> Options = new();
}

public sealed class MedicalTrace
{
    public string State { get; set; } = "evaluated";
    public string Choice { get; set; } = "Wait";
    public string NaturalChoice { get; set; } = "Wait";
    public string Personality { get; set; } = "";
    public bool HasKnownEnemy { get; set; }
    public bool Urgent { get; set; }
    public string MistakeState { get; set; } = "disabled";
    public double MistakeChance { get; set; }
    public double? MistakeRoll { get; set; }
    public double? VerdictUntil { get; set; }
    public string Reason { get; set; } = "medical-wait";
    public string Item { get; set; } = "";
    public string Part { get; set; } = "";
    public string Need { get; set; } = "";
    public double Duration { get; set; }
    public double Health { get; set; }
    public bool HeavyBleed { get; set; }
    public bool LightBleed { get; set; }
    public bool Vital { get; set; }
    public bool Cover { get; set; }
    public bool ReachedCover { get; set; }
    public bool Visible { get; set; }
    public bool Footsteps { get; set; }
    public string FootstepContact { get; set; } = "";
    public bool ThreatsIncomplete { get; set; }
    public int Contacts { get; set; }
    public string Contact { get; set; } = "";
    public double? X { get; set; }
    public double? Y { get; set; }
    public double? Z { get; set; }
    public double Quiet { get; set; }
    public double Distance { get; set; }
    public double SinceHit { get; set; }
    public bool Started { get; set; }
    public bool? TargetDestroyedAtStart { get; set; }
    public bool? TargetBleedCleared { get; set; }
    public string? StartCover { get; set; }
    public string? SurgeryAtSelection { get; set; }
    public string? SurgeryAtStart { get; set; }
    public double? StartTreatmentScore { get; set; }
    public double? StartWaitScore { get; set; }
    public bool Applied { get; set; }
    public double ValidFor { get; set; }
    public string Outcome { get; set; } = "unconfirmed";
    public MedicalOption[] Alternatives { get; set; } = Array.Empty<MedicalOption>();

    public MedicalTrace Copy() => (MedicalTrace)MemberwiseClone();
}

// Start selection only. Execution, resources and actual duration belong to native medicine.
public sealed class MedicalChoice
{
    private readonly UtilityMistakeVerdict verdict = new();
    public MedicalTrace? Trace { get; private set; }
    public bool Active => Trace?.State is "selected" or "requested" or "started";
    public double SelectedAt { get; private set; }
    public double RequestedAt { get; private set; }
    public double StartedAt { get; private set; }
    public double RetryAt { get; private set; }

    public static bool IsUrgent(bool heavyBleed, bool vital, double health) =>
        heavyBleed || vital || health <= .35;

    public MedicalTrace Evaluate(MedicalFrame f, MedicalChoiceConfig c,
        TacticalMistakesConfig? mistakes = null, Func<double>? nextRoll = null)
    {
        if (Active)
            return Trace!;
        var t = new MedicalTrace
        {
            Health = f.Health,
            Urgent = IsUrgent(f.HeavyBleed, f.Vital, f.Health),
            Personality = f.Personality,
            HasKnownEnemy = f.HasKnownEnemy,
            HeavyBleed = f.HeavyBleed,
            LightBleed = f.LightBleed,
            Vital = f.Vital,
            Cover = f.ReachedCover && f.ProtectiveCover,
            ReachedCover = f.ReachedCover,
            Visible = f.Visible,
            Footsteps = f.Footsteps,
            FootstepContact = f.FootstepContact,
            Contacts = f.Contacts,
            Contact = f.Contact,
            X = f.X,
            Y = f.Y,
            Z = f.Z,
            Quiet = f.Quiet,
            Distance = f.Nearest,
            SinceHit = f.SinceHit,
            ThreatsIncomplete = f.ThreatsIncomplete,
            SurgeryAtSelection = f.SurgeryCheck,
        };
        var options = f.Options.Select(o => o.Copy()).ToList();
        // HealUtility (zzap): pressure changes utility, it does not ban a treatment.
        double Clamp(double n) => Math.Max(0, Math.Min(1, n));
        double safe = Clamp((f.Quiet - 3) / 15) * .5 + Clamp((f.Nearest - 10) / 30) * .5;
        double close = Clamp((30 - f.Nearest) / 25) * Clamp((25 - f.Quiet) / 15);
        double medBias =
            f.Meds >= 3 ? .1
            : f.Meds <= 1 && f.Health > .35 ? -.1
            : 0;
        foreach (var o in options)
        {
            if (!o.Usable || !Finite(o.Duration) || o.Duration <= 0 || f.Priority.Length > 0)
            {
                o.Score = -10000;
                o.Reason = f.Priority.Length > 0 ? f.Priority : "medical-no-usable-treatment";
                continue;
            }
            double score = o.Action switch
            {
                "FirstAid" => medBias
                    + .2
                    + .5 * (1 - f.Health)
                    + (t.Cover ? .2 : 0)
                    + .3 * safe
                    - (f.Visible ? .6 : 0)
                    - (f.Footsteps ? .55 : 0)
                    - (f.SinceHit < c.HitDelay ? .3 : 0)
                    - (f.InHisView ? .4 : 0)
                    - (f.RunningToCover ? (f.LegsHold ? .2 : .5) : 0)
                    - .35 * close
                    - (f.CoverTowardHim ? .5 : 0),
                "Stims" when f.Health <= c.StimHealth => .3
                    + .4 * (1 - f.Health)
                    + (f.Visible ? (t.Cover ? .1 : -.25) : 0)
                    - (f.InHisView && !t.Cover ? .2 : 0)
                    - (f.SinceHit < c.EmergencyStimDelay ? .2 : 0),
                "Surgery" => .1
                    + .4 * safe
                    + (t.Cover ? .15 : 0)
                    - (f.Visible ? .8 : 0)
                    - (f.Footsteps ? .7 : 0)
                    - (f.Quiet < 10 ? .3 : 0)
                    - (f.InHisView ? .5 : 0)
                    - (f.CoverTowardHim ? .5 : 0),
                _ => -10000,
            };
            o.Score = score * 100;
            o.Reason = "medical-" + o.Need;
        }
        // Native zzap deliberately wins ties with Wait.
        options.Insert(
            0,
            new MedicalOption
            {
                Action = "Wait",
                Usable = true,
                Score = 100 * (.31 + .45 * close + (f.Footsteps ? .25 : 0) + (f.Visible ? .2 : 0)),
                Reason = "medical-wait-score",
            }
        );
        t.Alternatives = options.OrderByDescending(o => o.Score).ToArray();
        t.NaturalChoice = t.Alternatives[0].Action;
        int rank = 0;
        if (f.Priority.Length > 0) t.MistakeState = f.Priority;
        else if (t.Urgent) t.MistakeState = "urgent-medical-priority";
        else if (!f.HasKnownEnemy) t.MistakeState = "no-known-enemy";
        else if (mistakes?.Enabled == true && nextRoll != null)
        {
            t.MistakeChance = mistakes.ChanceFor(f.Personality);
            var pick = verdict.Choose(t.Alternatives, o => o.Action, o => o.Score,
                f.Now, t.MistakeChance, nextRoll);
            rank = pick.Rank;
            t.MistakeState = pick.State;
            t.MistakeRoll = pick.Roll;
            t.VerdictUntil = pick.Until;
        }
        var choice = t.Alternatives[rank];
        t.Choice = choice.Action;
        t.Reason = choice.Reason;
        t.Item = choice.Item;
        t.Part = choice.Part;
        t.Need = choice.Need;
        t.Duration = choice.Duration;
        if (choice.Action != "Wait")
        {
            t.State = "selected";
            SelectedAt = f.Now;
        }
        return Trace = t;
    }

    public void Request(double now)
    {
        if (Trace?.State != "selected")
            return;
        Trace.State = "requested";
        Trace.Reason = "medical-native-request";
        RequestedAt = now;
    }

    public void ObserveStart(double now)
    {
        if (Trace?.State != "requested")
            return;
        Trace.State = "started";
        Trace.Reason = "medical-hands-observed";
        Trace.Started = true;
        Trace.Applied = true;
        Trace.ValidFor = 1;
        StartedAt = now;
    }

    public void End(
        double now,
        string reason,
        MedicalChoiceConfig c,
        string outcome = "unconfirmed"
    )
    {
        if (!Active)
            return;
        Trace!.State = "ended";
        Trace.Reason = reason;
        Trace.Applied = false;
        Trace.ValidFor = 0;
        Trace.Outcome = outcome;
        RetryAt = now;
    }

    public static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
