using System;

namespace Comet.Core;

public sealed class SainPresetTrace
{
    public string Source { get; set; } = "selected-preset";
    public string Name { get; set; } = "Unknown";
    public string Availability { get; set; } = "preset-missing";
    public bool SingletonMatches { get; set; }
    public bool Jump { get; set; }
    public bool Lean { get; set; }
    public bool Grenades { get; set; }
    public bool AiGrenades { get; set; }
    public bool Suppression { get; set; }
    public int ObservedChanges { get; set; }
    public int CoalescedChanges { get; set; }
    public string Reason { get; set; } = "initial";
    internal bool SameValues(SainPresetTrace other) => Name == other.Name && Availability == other.Availability
        && SingletonMatches == other.SingletonMatches && Jump == other.Jump && Lean == other.Lean
        && Grenades == other.Grenades && AiGrenades == other.AiGrenades && Suppression == other.Suppression;
}

// Metadata only: no strong reference to a preset, a bot, or a world.
// Permissions never use this diagnostic sampler's delayed/coalesced values.
public sealed class SainPresetAudit
{
    private WeakReference<object>? selected;
    private SainPresetTrace? observed;
    private int changes, reported;
    private double nextReport;
    public SainPresetTrace? Observe(object? current, SainPresetTrace sample, double now)
    {
        bool sameIdentity = current == null ? selected == null
            : selected != null && selected.TryGetTarget(out var prior) && ReferenceEquals(prior,current);
        if(observed == null || !sameIdentity || !observed.SameValues(sample))
        {
            sample.Reason = observed == null ? "initial" : !sameIdentity ? "selected-preset-changed" : "preset-context-changed";
            changes++;
            selected = current == null ? null : new(current);
            observed = sample;
        }
        if(observed == null || reported == changes || now < nextReport)return null;
        observed.ObservedChanges = changes;
        observed.CoalescedChanges = Math.Max(0,changes-reported-1);
        reported = changes; nextReport = now+5;
        return observed;
    }
    public void Clear() { selected=null;observed=null;changes=reported=0;nextReport=0; }
}
