using System;

namespace Comet.Core;

public sealed class LightDisciplineConfig
{
    public bool Enabled { get; set; } = true;
    public double ContactLifetime { get; set; } = 60;
    public double MaxDistance { get; set; } = 60;
    public double MaxSpeed { get; set; } = 1.2;
    public double HoldDelay { get; set; } = 0;
    public double HiddenDelay { get; set; } = 0;
    public double VisibleDelay { get; set; } = 0;

    public void Validate()
    {
        CometConfig.Range(ContactLifetime, 1, 60, nameof(ContactLifetime));
        CometConfig.Range(MaxDistance, 5, 120, nameof(MaxDistance));
        CometConfig.Range(MaxSpeed, 0, 1.2, nameof(MaxSpeed));
        CometConfig.Range(HoldDelay, 0, 3, nameof(HoldDelay));
        CometConfig.Range(HiddenDelay, 0, 5, nameof(HiddenDelay));
        CometConfig.Range(VisibleDelay, 0, .5, nameof(VisibleDelay));
    }
}

public sealed class LightDisciplineFrame
{
    public double Now,
        ContactAge,
        Distance,
        Speed;
    public string Contact = "",
        Source = "",
        Action = "",
        Tactic = "",
        Block = "";
    public DoorPoint? Known;
    public bool Visible,
        NightVision;
}

public sealed class LightDisciplineTrace
{
    public string State = "waiting",
        Reason = "light-waiting",
        Contact = "",
        Source = "",
        Action = "",
        Tactic = "",
        Weapon = "";
    public string Execution = "No device command",
        Scope = "held weapon";
    public DoorPoint? Known;
    public double? ContactAge,
        Distance,
        Speed;
    public double HoldWait,
        HiddenWait,
        VisibleWait,
        ValidFor;
    public bool Requested,
        ObservedOff,
        NightVision;
    public int Devices,
        ActiveDevices;
    public string[] Modes = Array.Empty<string>();
    public string[] Alternatives =
    {
        "Keep SAIN's current lighting choice",
        "Stay dark while holding a known angle",
        "Use native lighting when moving, searching or seeing the enemy",
    };
}

// Per-bot state contains only values. Native objects live in the bounded adapter snapshot.
public sealed class LightDisciplinePolicy
{
    public LightDisciplineTrace? Trace { get; private set; }
    public bool Dark { get; private set; }
    private string contact = "",
        tactic = "";
    private double holdAt = -1,
        hiddenAt = -1,
        visibleAt = -1,
        last = -1;
    private bool yielded;
    private string yieldReason = "";

    public bool Evaluate(LightDisciplineFrame f, LightDisciplineConfig c)
    {
        if (!DoorPolicy.Finite(f.Now) || f.Now < 0)
        {
            Clear("light-clock-unavailable");
            return false;
        }
        if (last > f.Now)
            Clear("light-clock-reset");
        last = f.Now;
        if (contact != f.Contact)
        {
            Clear("light-contact-changed");
            contact = f.Contact;
        }
        var t = Trace ??= new();
        t.Contact = f.Contact;
        t.Source = f.Source;
        t.Action = f.Action;
        t.Tactic = f.Tactic;
        t.Known = f.Known.HasValue && f.Known.Value.Finite ? f.Known : null;
        t.NightVision = f.NightVision;
        t.ContactAge = FiniteNonnegative(f.ContactAge);
        t.Distance = FiniteNonnegative(f.Distance);
        t.Speed = FiniteNonnegative(f.Speed);
        t.HoldWait = t.HiddenWait = t.VisibleWait = 0;
        if (!c.Enabled || f.Block.Length > 0)
        {
            Release(!c.Enabled ? "light-disabled" : f.Block);
            return false;
        }
        // zzap BotLightController.ShallStayDark: immediate visibility release;
        // known enemy within 60 m/60 s, moving below 1.2 m/s, except Search.
        if (f.Visible)
        {
            Release("light-visible-contact");
            return false;
        }
        if (
            f.Contact.Length == 0
            || f.Source.Length == 0
            || f.ContactAge >= c.ContactLifetime
            || f.Distance >= c.MaxDistance
            || f.Speed >= c.MaxSpeed
            || f.Action == "Search"
        )
        {
            Release("light-not-holding-angle");
            return false;
        }
        Dark = true;
        t.State = "dark";
        t.Reason = "light-tactical-concealment";
        t.ValidFor = .8;
        return true;
    }

    private static double? FiniteNonnegative(double n) => DoorPolicy.Finite(n) && n >= 0 ? n : null;

    public void Observe(int devices, int active, string[] modes, bool requested)
    {
        if (Trace == null)
            return;
        Trace.Devices = devices;
        Trace.ActiveDevices = active;
        Trace.Modes = modes;
        Trace.Requested |= requested;
        Trace.ObservedOff = devices > 0 && active == 0;
        Trace.Execution = Trace.Requested
            ? (
                Trace.ObservedOff
                    ? "Switch-off requested; devices observed OFF"
                    : "Switch-off requested; OFF not confirmed"
            )
            : (Trace.ObservedOff ? "Already OFF; no device command" : "No device command");
    }

    public void Yield(string reason)
    {
        Dark = false;
        yielded = true;
        yieldReason = reason;
        if (Trace != null)
        {
            Trace.State = "yielded";
            Trace.Reason = reason;
            Trace.ValidFor = 0;
        }
    }

    public void Release(string reason, bool resetHidden = true)
    {
        Dark = false;
        yielded = false;
        holdAt = -1;
        if (resetHidden)
            hiddenAt = -1;
        if (Trace != null)
        {
            Trace.State = "released";
            Trace.Reason = reason;
            Trace.ValidFor = 0;
        }
    }

    public void Clear(string reason)
    {
        Release(reason);
        contact = tactic = "";
        visibleAt = last = -1;
    }
}
