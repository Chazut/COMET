using System;

namespace Comet.Core;

// No enemy transform exists in this contract. Observed coordinates are consumed ONLY while Visible.
public sealed class SightFrame
{
    public double Now, X, Y, Z, Distance, SeenAge, HitAt;
    public string Target = "", Action = "", Weapon = "";
    public bool Eligible, Visible, Observed, WeaponReady, Urgent, OtherThreat, Hurt, LowAmmo, Suppressed, PushPathValid;
    public bool Panic, OwnedPlan, Jiggling;
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
        var c=config.SightContinuity;
        if(!config.Enabled||!c.Enabled||!f.Eligible||f.Target.Length==0)return Reset("sight-ineligible");
        if(!Finite(f.Now)||f.Now<0)return Reset("sight-invalid-clock");
        if(target!=f.Target){Reset("sight-target-changed");target=f.Target;}
        if(f.Visible){
            Current=null;
            if(f.Action is "Shoot" or "Push" or "DogFight"){
                action=f.Action;lastSeen=f.Now-f.SeenAge;visibleSince=f.Now;
                x=f.X;y=f.Y;z=f.Z;distance=f.Distance;armed=f.Observed;
            }
            Reason="sight-visible-fight";return null;
        }
        bool close=f.Distance<=c.CloseDistance;
        double keep=f.Jiggling?1.2:close?c.CloseDuration:c.MediumDuration;
        if(!armed||f.SeenAge>keep||f.Now-visibleSince>keep+.4||(!f.Jiggling&&f.Distance>c.MaxDistance))return Reset("sight-window-expired");
        if(f.OtherThreat)return Reset("sight-other-threat");
        if(f.Action is not ("Shoot" or "Push")&&!(f.Jiggling&&f.Action=="DogFight"))return Reset("sight-action-changed");
        if(!f.Jiggling&&!close&&f.Action!="Shoot")return Reset("sight-medium-shoot-only");
        Reason="sight-source-continuity";
        return Current=new SightHold(target,f.Action,x,y,z,f.Distance,f.Now-f.SeenAge,f.Now-f.SeenAge+keep);
    }
    public SightHold? Reset(string reason)
    {
        Current = null; Reason = reason; target = action = weapon = ""; visibleSince = lastSeen = lastTick = deadline = -1;
        armed = wasVisible = false;
        return null;
    }
    private static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
}
