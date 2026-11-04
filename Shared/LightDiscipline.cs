using System;

namespace Comet.Core;

public sealed class LightDisciplineConfig
{
    public bool Enabled { get; set; } = true;
    public double ContactLifetime { get; set; } = 15;
    public double MaxDistance { get; set; } = 60;
    public double MaxSpeed { get; set; } = .5;
    public double HoldDelay { get; set; } = .5;
    public double HiddenDelay { get; set; } = 1;
    public double VisibleDelay { get; set; } = .25;
    public void Validate()
    {
        CometConfig.Range(ContactLifetime, 1, 60, nameof(ContactLifetime));
        CometConfig.Range(MaxDistance, 5, 120, nameof(MaxDistance));
        CometConfig.Range(MaxSpeed, 0, 1.2, nameof(MaxSpeed));
        CometConfig.Range(HoldDelay, .1, 3, nameof(HoldDelay));
        CometConfig.Range(HiddenDelay, .1, 5, nameof(HiddenDelay));
        CometConfig.Range(VisibleDelay, 0, .5, nameof(VisibleDelay));
    }
}

public sealed class LightDisciplineFrame
{
    public double Now, ContactAge, Distance, Speed;
    public string Contact = "", Source = "", Action = "", Tactic = "", Block = "";
    public DoorPoint? Known;
    public bool Visible, NightVision;
}

public sealed class LightDisciplineTrace
{
    public string State = "waiting", Reason = "light-waiting", Contact = "", Source = "", Action = "", Tactic = "", Weapon = "";
    public string Execution = "No device command", Scope = "held weapon";
    public DoorPoint? Known;
    public double? ContactAge, Distance, Speed;
    public double HoldWait, HiddenWait, VisibleWait, ValidFor;
    public bool Requested, ObservedOff, NightVision;
    public int Devices, ActiveDevices;
    public string[] Modes = Array.Empty<string>();
    public string[] Alternatives = { "Keep SAIN's current lighting choice", "Wait for stable tactical concealment", "Release on confirmed vision or a higher priority action" };
}

// Per-bot state contains only values. Native objects live in the bounded adapter snapshot.
public sealed class LightDisciplinePolicy
{
    public LightDisciplineTrace? Trace { get; private set; }
    public bool Dark { get; private set; }
    private string contact = "", tactic = "";
    private double holdAt = -1, hiddenAt = -1, visibleAt = -1, last = -1;
    private bool yielded;
    private string yieldReason = "";

    public bool Evaluate(LightDisciplineFrame f, LightDisciplineConfig c)
    {
        if (!DoorPolicy.Finite(f.Now) || f.Now < 0) { Clear("light-clock-unavailable"); return false; }
        if (last > f.Now) Clear("light-clock-reset");
        last = f.Now;
        if (contact != f.Contact) { Clear("light-contact-changed"); contact = f.Contact; }
        var t = Trace ??= new();
        t.Contact = f.Contact; t.Source = f.Source; t.Action = f.Action; t.Tactic = f.Tactic;
        t.Known = f.Known; t.NightVision = f.NightVision;
        t.ContactAge = FiniteNonnegative(f.ContactAge); t.Distance = FiniteNonnegative(f.Distance); t.Speed = FiniteNonnegative(f.Speed);
        t.HoldWait = t.HiddenWait = t.VisibleWait = 0;
        if (!c.Enabled || f.Block.Length > 0) { Release(!c.Enabled ? "light-disabled" : f.Block); return false; }
        // An already hidden bot tolerates brief visual flicker. No new darkness begins on a visible enemy.
        if (f.Visible)
        {
            hiddenAt = holdAt = -1;
            if (visibleAt < 0) visibleAt = f.Now;
            t.VisibleWait = Math.Max(0, c.VisibleDelay - (f.Now - visibleAt));
            if (Dark && t.VisibleWait > 1e-6) { t.Reason = "light-confirming-vision"; return true; }
            Release("light-visible-contact"); return false;
        }
        visibleAt = -1;
        if (f.Contact.Length == 0 || f.Source.Length == 0 || f.Known == null || !f.Known.Value.Finite
            || t.ContactAge == null || f.ContactAge > c.ContactLifetime || t.Distance == null || f.Distance > c.MaxDistance)
        { Release("light-no-recent-known-contact"); return false; }
        if (hiddenAt < 0) hiddenAt = f.Now;
        if (f.Tactic.Length == 0 || t.Speed == null || f.Speed > c.MaxSpeed)
        { Release(f.Tactic.Length == 0 ? "light-not-tactical-hold" : "light-moving", false); return false; }
        if (tactic != f.Tactic) { Release("light-tactic-changed", false); tactic = f.Tactic; }
        if (yielded) { t.State = "yielded"; t.Reason = yieldReason; t.ValidFor = 0; return false; }
        if (holdAt < 0)
        {
            holdAt = f.Now;
            if (!Dark) { t.Requested = t.ObservedOff = false; t.Devices = t.ActiveDevices = 0; t.Modes = Array.Empty<string>(); t.Weapon = ""; t.Execution = "No device command"; }
        }
        t.HoldWait = Dark ? 0 : Math.Max(0, c.HoldDelay - (f.Now - holdAt));
        t.HiddenWait = Dark ? 0 : Math.Max(0, c.HiddenDelay - (f.Now - hiddenAt));
        if (!Dark && (t.HoldWait > 1e-6 || t.HiddenWait > 1e-6))
        { t.State = "waiting"; t.Reason = "light-stabilizing-hold"; t.ValidFor = 0; return false; }
        Dark = true; t.State = "dark"; t.Reason = "light-tactical-concealment"; t.ValidFor = .8; return true;
    }
    private static double? FiniteNonnegative(double n) => DoorPolicy.Finite(n) && n >= 0 ? n : null;
    public void Observe(int devices, int active, string[] modes, bool requested)
    {
        if (Trace == null) return;
        Trace.Devices = devices; Trace.ActiveDevices = active; Trace.Modes = modes;
        Trace.Requested |= requested; Trace.ObservedOff = devices > 0 && active == 0;
        Trace.Execution = Trace.Requested ? (Trace.ObservedOff ? "Switch-off requested; devices observed OFF" : "Switch-off requested; OFF not confirmed")
            : (Trace.ObservedOff ? "Already OFF; no device command" : "No device command");
    }
    public void Yield(string reason)
    {
        Dark = false; yielded = true; yieldReason = reason;
        if (Trace != null) { Trace.State = "yielded"; Trace.Reason = reason; Trace.ValidFor = 0; }
    }
    public void Release(string reason, bool resetHidden = true)
    {
        Dark = false; yielded = false; holdAt = -1; if (resetHidden) hiddenAt = -1;
        if (Trace != null) { Trace.State = "released"; Trace.Reason = reason; Trace.ValidFor = 0; }
    }
    public void Clear(string reason)
    { Release(reason); contact = tactic = ""; visibleAt = last = -1; }
}
