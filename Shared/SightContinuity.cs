using System;

namespace Comet.Core;

// No enemy transform exists in this contract. Observed coordinates are consumed ONLY while Visible.
public sealed class SightFrame
{
    public double Now, X, Y, Z, Distance, SeenAge, HitAt;
    public string Target = "", Action = "", Weapon = "";
    public bool Eligible, Visible, Observed, WeaponReady, Urgent, OtherThreat, Hurt, LowAmmo, Suppressed, PushPathValid;
    public bool Panic, OwnedPlan;
}

public sealed class SightHold
{
    public string Target { get; }
    public string Action { get; }
    public double X { get; }
    public double Y { get; }
    public double Z { get; }
    public double Distance { get; }
    public double SeenAt { get; }
    public double Until { get; }
    public SightHold(string target, string action, double x, double y, double z, double distance, double seenAt, double until)
    { Target = target; Action = action; X = x; Y = y; Z = z; Distance = distance; SeenAt = seenAt; Until = until; }
}

public sealed class SightContinuity
{
    private readonly CometConfig config;
    private string target = "", action = "", weapon = "";
    private double visibleSince = -1, lastTick = -1, lastSeen = -1, deadline = -1, x, y, z, distance, hit;
    private bool wasVisible, armed, hurt, lowAmmo, suppressed;
    public SightHold? Current { get; private set; }
    public string Reason { get; private set; } = "no-stable-sight";
    public SightContinuity(CometConfig config) { config.Validate(); this.config = config; }
    public SightHold? Evaluate(SightFrame f)
    {
        var c = config.SightContinuity;
        if (!config.Enabled || !c.Enabled) return Reset("sight-continuity-disabled");
        if (!f.Eligible || f.Target.Length == 0) return Reset("sight-ineligible");
        if (!Finite(f.Now) || !Finite(f.HitAt) || (lastTick >= 0 && (f.Now < lastTick || f.Now - lastTick > .3)))
            return Reset("sight-observation-gap");
        if (target != f.Target)
        {
            bool changed = target.Length > 0;
            Reset("sight-target-changed");
            if (changed && !f.Visible) return null;
            target = f.Target;
        }
        lastTick = f.Now;
        if (f.Urgent) return Reset("sight-urgent-action");
        if (config.Fear.Enabled && f.Panic) return Reset("panic-reassessment");
        if (f.OtherThreat) return Reset("sight-other-threat");
        if (!f.WeaponReady) return Reset("sight-weapon-unavailable");
        if (config.Loadout.Enabled && weapon.Length > 0 && weapon != f.Weapon) return Reset("sight-equipment-changed");
        if (f.Action != "Shoot" && f.Action != "Push") return Reset("sight-action-changed");
        if (action.Length != 0 && (f.Action != action || f.Hurt != hurt || f.LowAmmo != lowAmmo || f.Suppressed != suppressed
            || f.HitAt > hit + .01)) return Reset("sight-situation-changed");
        if (f.Visible)
        {
            Current = null;
            if (!f.Observed || !Finite(f.X) || !Finite(f.Y) || !Finite(f.Z) || !Finite(f.Distance)
                || f.Distance < 0 || f.Distance > c.MaxDistance && (!f.OwnedPlan || f.Action != "Shoot" || config.Handoff.Grace <= 0)
                || !Finite(f.SeenAge) || f.SeenAge < 0 || f.SeenAge > .25)
                return Reset("sight-observation-invalid");
            if (!wasVisible) visibleSince = f.Now;
            wasVisible = true;
            // A short reappearance updates the snapshot but NEVER moves an existing expiry forward.
            if (f.Now - visibleSince >= c.StableSight) { armed = true; deadline = -1; }
            x = f.X; y = f.Y; z = f.Z; distance = f.Distance; lastSeen = f.Now - f.SeenAge;
            action = f.Action; weapon = f.Weapon; hit = f.HitAt; hurt = f.Hurt; lowAmmo = f.LowAmmo; suppressed = f.Suppressed;
            Reason = armed ? "sight-armed" : "sight-waiting-stable-view";
            return null;
        }
        wasVisible = false; visibleSince = -1;
        if (!armed || lastSeen < 0) { Reason = "sight-no-stable-view"; return Current = null; }
        // Outside A03's normal range, only keep an already owned firing angle for
        // the short transition tolerance. Never acquire a new target or fire blind.
        bool distantAngle = distance > c.MaxDistance;
        if (distantAngle && (!f.OwnedPlan || action != "Shoot" || config.Handoff.Grace <= 0))
            return Reset("sight-owned-plan-ended");
        if (action == "Push" && (distance > c.CloseDistance || !f.PushPathValid || f.Hurt || f.LowAmmo || f.Suppressed))
            return Reset("sight-push-no-longer-safe");
        if (deadline < 0) deadline = lastSeen + (distantAngle ? Math.Min(config.Handoff.Grace,c.MediumDuration)
            : distance <= c.CloseDistance ? c.CloseDuration : c.MediumDuration);
        if (f.Now >= deadline) return Reset("sight-window-expired");
        Reason = action == "Push" ? "sight-continue-known-route" : "sight-hold-last-seen";
        return Current ??= new SightHold(target, action, x, y, z, distance, lastSeen, deadline);
    }
    public SightHold? Reset(string reason)
    {
        Current = null; Reason = reason; target = action = weapon = ""; visibleSince = lastSeen = lastTick = deadline = -1;
        armed = wasVisible = false;
        return null;
    }
    private static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
}
