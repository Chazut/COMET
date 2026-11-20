using System;
using System.Collections.Generic;

namespace Comet.Core;

public sealed class DoorFakeGrenadeConfig
{
    public bool Enabled { get; set; } = true;
    public Dictionary<string, double> Chances { get; set; } =
        new()
        {
            ["GigaChad"] = 40,
            ["Chad"] = 20,
            ["Normal"] = 10,
            ["Wreckless"] = 5,
            ["Rat"] = 0,
            ["SnappingTurtle"] = 0,
            ["Coward"] = 0,
            ["Timmy"] = 0,
        };
    public double DoorDistance { get; set; } = 3.2;
    public double ContactDistance { get; set; } = 14;
    public double ContactLifetime { get; set; } = 60;
    public double TargetCooldown { get; set; } = 60;
    public double Cooldown { get; set; } = 20;

    public void Validate()
    {
        if (Chances == null || Chances.Count != 8)
            throw new ArgumentException("Missing fake grenade personality chances.");
        foreach (
            var p in new[]
            {
                "GigaChad",
                "Chad",
                "Normal",
                "Wreckless",
                "Rat",
                "SnappingTurtle",
                "Coward",
                "Timmy",
            }
        )
            if (!Chances.TryGetValue(p, out var n))
                throw new ArgumentException("Missing fake grenade chance: " + p);
            else
                CometConfig.Range(n, 0, 100, p);
        CometConfig.Range(DoorDistance, 1, 4, nameof(DoorDistance));
        CometConfig.Range(ContactDistance, 3, 15, nameof(ContactDistance));
        CometConfig.Range(ContactLifetime, 3, 120, nameof(ContactLifetime));
        CometConfig.Range(TargetCooldown, 30, 180, nameof(TargetCooldown));
        CometConfig.Range(Cooldown, 10, 60, nameof(Cooldown));
    }
}

public sealed class DoorFakeTrace
{
    public long Opportunity { get; set; }
    public string State { get; set; } = "validating";
    public string Reason { get; set; } = "fake-selected";
    public string Interrupted { get; set; } = "";
    public string Door { get; set; } = "";
    public string Target { get; set; } = "";
    public string Source { get; set; } = "";
    public string Personality { get; set; } = "";
    public string Grenade { get; set; } = "";
    public string Weapon { get; set; } = "";
    public string Reaction { get; set; } = "not-observed";
    public string Kind { get; set; } = "grenade";
    public double StyleRoll { get; set; }
    public double MedicalPreference { get; set; }
    public bool GrenadeEligible { get; set; }
    public string MedicalEligible { get; set; } = "";
    public DoorMedicalTrace? Medical { get; set; }
    public double Chance { get; set; }
    public double Roll { get; set; }
    public double ContactAge { get; set; }
    public double KnownX { get; set; }
    public double KnownY { get; set; }
    public double KnownZ { get; set; }
    public double Elapsed { get; set; }
    public bool CoverVerified { get; set; }
    public bool DrawRequested { get; set; }
    public bool CallbackCompleted { get; set; }
    public bool CallbackSucceeded { get; set; }
    public bool HandsObserved { get; set; }
    public bool HolsterRequested { get; set; }
    public bool HolsterAccepted { get; set; }
    public bool WeaponReady { get; set; }
    public bool Guard { get; set; }
    public bool RecoveryFailed { get; set; }
    public int HolsterAttempts { get; set; }
    public double ValidFor { get; set; } = 1.5;
    public string[] Alternatives { get; set; } =
        new[] { "Normal SAIN search", "Doorway reconnaissance only if no feint was selected" };
}

// Owns no game objects. Watchdogs are failure bounds, never assumed animation durations.
public sealed class DoorFakeGrenade
{
    private long diagnosticOpportunity;
    public const double DrawWatchdog = 1.2,
        RestoreWatchdog = 3.2;
    private readonly Dictionary<string, (double at, double evidence)> history = new();
    private double next,
        started,
        phase,
        last,
        restoreAt = -1000;
    public DoorFakeTrace? Trace { get; private set; }
    public bool Active => Trace != null && Trace.State != "completed" && Trace.State != "ended";
    public bool Fault { get; private set; }

    public bool Begin(
        string door,
        string target,
        string source,
        string personality,
        DoorPoint known,
        double age,
        double now,
        double roll,
        DoorFakeGrenadeConfig c,
        bool medicalEnabled = false,
        bool planned = false,
        bool roomClear = false
    )
    {
        if (
            Active
            || Fault
            || (!c.Enabled && !medicalEnabled)
            || !known.Finite
            || !DoorPolicy.Finite(now)
            || !planned && now < next
            || !DoorPolicy.Finite(age)
            || age < 0
            || !roomClear && age > c.ContactLifetime
            || !DoorPolicy.Finite(roll)
            || roll < 0
            || roll >= 1
            || string.IsNullOrEmpty(door)
            || string.IsNullOrEmpty(target)
        )
            return false;
        double stamp = now - age;
        if (!planned && history.TryGetValue(target, out var h) && now - h.at < c.TargetCooldown)
            return false;
        history[target] = (now, stamp);
        next = now + c.Cooldown;
        started = phase = last = now;
        restoreAt = -1000;
        double chance = c.Chances.TryGetValue(personality, out var value) ? value : 0;
        Trace = new()
        {
            Opportunity = ++diagnosticOpportunity,
            Door = door,
            Target = target,
            Source = source,
            Personality = personality,
            KnownX = known.X,
            KnownY = known.Y,
            KnownZ = known.Z,
            ContactAge = age,
            Chance = chance,
            Roll = roll * 100,
        };
        if (!planned && roll * 100 >= chance)
            End("fake-roll-declined");
        return Active;
    }

    private void Phase(string state, string reason, double now)
    {
        Trace!.State = state;
        Trace.Reason = reason;
        phase = now;
    }

    public void Draw(string grenade, string weapon, double now)
    {
        if (!Active || Trace!.State != "validating")
            return;
        if (string.IsNullOrEmpty(grenade) || string.IsNullOrEmpty(weapon))
        {
            End("fake-missing-item");
            return;
        }
        Trace.Grenade = Trace.Kind == "grenade" ? grenade : "";
        Trace.Weapon = weapon;
        Trace.CoverVerified = true;
        Trace.DrawRequested = true;
        Trace.Guard = Trace.Kind == "grenade";
        Phase("drawing", "fake-draw-requested", now);
    }

    public void Callback(bool success, double now)
    {
        var t = Trace;
        if (t == null || !t.DrawRequested || t.CallbackCompleted || t.State == "completed")
            return;
        t.CallbackCompleted = true;
        t.CallbackSucceeded = success;
        if (t.State == "ended" && t.RecoveryFailed && success && t.Medical?.MedicalHandoff != true)
        {
            t.Guard = t.Kind == "grenade";
            restoreAt = -1000;
            t.HolsterAttempts = 0;
            Phase("recovering", "fake-late-draw-callback", now);
        }
        else if (Active && !success)
            Abort("fake-draw-callback-failed", now);
    }

    public void Abort(string reason, double now)
    {
        if (!Active)
            return;
        var t = Trace!;
        if (t.Interrupted.Length == 0)
            t.Interrupted = reason;
        if (!t.DrawRequested)
        {
            End(reason);
            return;
        }
        // Repeated native releases must not extend the recovery deadline.
        if (t.State is not ("holstering" or "recovering" or "suspended"))
            Phase("holstering", reason, now);
    }

    public void Suspend(double now)
    {
        if (!Active || Trace!.State == "suspended")
            return;
        Abort("fake-scope-suspended", now);
        if (Active)
        {
            Trace!.Guard = false;
            Phase("suspended", "fake-scope-suspended", now);
        }
    }

    public void Resume(double now)
    {
        if (!Active || Trace!.State != "suspended")
            return;
        Trace.Guard = Trace.DrawRequested && Trace.Kind == "grenade";
        restoreAt = -1000;
        Trace.HolsterAttempts = 0;
        Phase("recovering", "fake-scope-restored-recovering-hands", now);
    }

    public void Observe(
        bool grenadeInHands,
        bool weaponReady,
        bool otherHands,
        bool throwing,
        double now
    )
    {
        if (!Active || Trace!.State == "suspended")
            return;
        var t = Trace!;
        if (!DoorPolicy.Finite(now) || now < last)
        {
            Fail("fake-invalid-clock");
            return;
        }
        last = now;
        t.Elapsed = now - started;
        if (throwing)
        {
            Fault = true;
            End("fake-native-throw-took-over");
            return;
        }
        if (t.State == "validating")
            return;
        if (grenadeInHands)
        {
            t.HandsObserved = true;
            if (t.State == "drawing")
                Phase(
                    "holstering",
                    t.Kind == "grenade" ? "fake-grenade-in-hands" : "fake-medicine-in-hands",
                    now
                );
        }
        bool drawSettled = t.HandsObserved || t.CallbackCompleted;
        if (drawSettled && !grenadeInHands && weaponReady)
        {
            t.WeaponReady = true;
            t.Guard = false;
            if (
                t.HandsObserved
                && t.HolsterRequested
                && (t.HolsterAccepted || t.Medical?.NativeStopRequested == true)
                && t.Interrupted.Length == 0
            )
                Phase(
                    t.Medical?.ConsumptionObserved == true ? "ended" : "completed",
                    t.Medical?.ConsumptionObserved == true
                        ? "fake-real-medical-use-observed"
                        : "fake-weapon-ready",
                    now
                );
            else
                End(
                    t.Interrupted.Length > 0 ? t.Interrupted
                    : t.HandsObserved ? "fake-native-weapon-takeover"
                    : "fake-draw-not-observed"
                );
            return;
        }
        if (drawSettled && otherHands)
        {
            End("fake-other-hands-took-over");
            return;
        }
        if (t.State == "drawing" && (t.CallbackCompleted || now - phase >= DrawWatchdog))
            Phase("holstering", "fake-draw-sound-complete", now);
        else if (t.State is "holstering" or "recovering" && now - phase >= RestoreWatchdog)
        {
            t.State = "completed";
            t.Reason = "fake-holster-timeout-continue-hold";
            t.Guard = false;
        }
    }

    public bool CanRestore(bool ownedGrenade, bool changing, bool throwing, double now) =>
        Active
        && (Trace!.State is "holstering" or "recovering")
        && ownedGrenade
        && !changing
        && !throwing
        && !Trace.HolsterAccepted
        && now - restoreAt >= .5
        && Trace.HolsterAttempts < 6;

    public void RestoreRequested(bool accepted, double now)
    {
        if (!Active)
            return;
        restoreAt = now;
        Trace!.HolsterAttempts++;
        Trace.HolsterRequested = true;
        Trace.HolsterAccepted |= accepted;
        Trace.Reason =
            accepted ? "fake-holster-accepted"
            : Trace.Medical?.NativeStopRequested == true ? "fake-medical-stop-requested"
            : "fake-holster-refused";
    }

    public void Fail(string reason)
    {
        Fault = true;
        Trace!.RecoveryFailed = true;
        End(reason);
    }

    public void YieldToMedical()
    {
        if (Trace?.Medical is DoorMedicalTrace med)
        {
            med.MedicalHandoff = true;
            Trace.Interrupted = "fake-real-medical-priority";
            End("fake-real-medical-priority");
        }
    }

    public void End(string reason)
    {
        if (Trace != null)
        {
            Trace.State = "ended";
            Trace.Reason = reason;
            Trace.Guard = false;
        }
    }

    public void Clear()
    {
        Trace = null;
        history.Clear();
        next = 0;
        Fault = false;
    }
}
