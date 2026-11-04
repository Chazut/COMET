using System;

namespace Comet.Core;

public sealed class ImpactReactionConfig
{
    public bool Enabled { get; set; } = true;
    public double Memory { get; set; } = 3;
    public double MinimumInterval { get; set; } = .5;
    public void Validate()
    {
        CometConfig.Range(Memory, 1, 8, nameof(Memory));
        CometConfig.Range(MinimumInterval, .1, 2, nameof(MinimumInterval));
        if (MinimumInterval > Memory) throw new ArgumentException("Impact repeat interval must not exceed danger memory.");
    }
}

// A hit supplies danger, not a hidden enemy transform. No attacker position belongs in this contract.
public sealed class ImpactFrame
{
    public double Now, DirectionX, DirectionY, DirectionZ;
    public bool Scoped = true, Bullet = true, Delayed, Friendly;
    public string Contact = "", Evidence = "";
}

public sealed class ImpactTrace
{
    public string State { get; set; } = "received";
    public string Reason { get; set; } = "impact-received";
    public string Contact { get; set; } = "";
    public string Evidence { get; set; } = "unlocated";
    public string Requested { get; set; } = "";
    public string Observed { get; set; } = "";
    public double At { get; set; }
    public double Age { get; set; }
    public double Remaining { get; set; }
    public double ValidFor { get; set; }
    public double? SectorYaw { get; set; }
    public double SectorHalfWidth { get; set; } = 45;
    public bool Active { get; set; }
    public bool NativeAccepted { get; set; }
    public bool ActionObserved { get; set; }
    public bool MovementObserved { get; set; }
    public bool LookApplied { get; set; }
    public int Hits { get; set; }
    public int Probes { get; set; }
    public string CoverReason { get; set; } = "not-evaluated";
    public ImpactTrace Copy() => (ImpactTrace)MemberwiseClone();
}

public sealed class ImpactReaction
{
    private readonly ImpactReactionConfig config;
    private double last = double.NegativeInfinity, next = double.NegativeInfinity;
    public ImpactTrace Current { get; private set; } = new ImpactTrace { State = "ended" };
    private double? pendingSector;
    private string pendingContact = "", pendingEvidence = "unlocated";
    public bool Pending { get; private set; }
    public ImpactReaction(ImpactReactionConfig config) { this.config = config; }
    public static bool Finite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);
    public static bool Fresh(double now, double then, double window) => Finite(now) && Finite(then) && then > 0 && now >= then && now - then <= window;
    public static bool PersonalEvidence(string evidence) => evidence == "seen" || evidence == "heard-shot";
    // Incoming bullet direction is reversed, flattened and quantized into a 90-degree sector.
    // A near-vertical or invalid trajectory remains unlocated. MasterOrigin is never consulted.
    public static double? Sector(double x, double y, double z)
    {
        if (!Finite(x) || !Finite(y) || !Finite(z)) return null;
        double horizontal = Math.Sqrt(x*x + z*z), length = Math.Sqrt(x*x+y*y+z*z);
        if (!Finite(length) || length < .0001 || horizontal / length < .25) return null;
        double angle = Math.Atan2(-x, -z) * 180 / Math.PI;
        return ((Math.Floor((angle + 45) / 90) * 90) % 360 + 360) % 360;
    }
    public ImpactTrace Receive(ImpactFrame f)
    {
        string refused = !config.Enabled || !f.Scoped ? "impact-out-of-scope" : !f.Bullet || f.Delayed ? "impact-not-direct-bullet"
            : f.Friendly ? "impact-friendly-unchanged" : !Finite(f.Now) || f.Now < last ? "impact-invalid-time" : "";
        if (refused.Length > 0) return new ImpactTrace { State="rejected", Reason=refused };
        bool continuing = Active(f.Now);
        if (!continuing) { Current = new ImpactTrace(); next = double.NegativeInfinity; }
        last = f.Now; Pending = true; Current.Hits++; Current.Active = true;
        pendingSector = Sector(f.DirectionX,f.DirectionY,f.DirectionZ);
        bool known = !string.IsNullOrEmpty(f.Contact) && PersonalEvidence(f.Evidence);
        pendingContact = known ? f.Contact : "";
        pendingEvidence = known ? f.Evidence : pendingSector.HasValue ? "impact-sector" : "unlocated";
        // Keep burst updates pending until the review window, without losing its last impact.
        if (!continuing || f.Now >= next) ApplyPending();
        var trace = Snapshot(f.Now); trace.State = "received"; trace.Reason="impact-received"; return trace;
    }
    public bool Active(double now) => Finite(now) && now >= last && now-last < config.Memory && Current.Active;
    public bool Due(double now) => Active(now) && Pending && now >= next;
    private void ApplyPending() { Current.SectorYaw=pendingSector; Current.Contact=pendingContact; Current.Evidence=pendingEvidence; }
    public bool Reviewed(double now)
    {
        if (!Due(now)) return false;
        bool changed=Current.SectorYaw!=pendingSector;
        ApplyPending(); next=now+config.MinimumInterval; Pending=false; return changed;
    }
    public ImpactTrace Snapshot(double now)
    {
        var t = Current.Copy(); t.At=now;
        t.Age=Finite(last)&&Finite(now)?Math.Max(0,now-last):0;
        t.Remaining=Active(now)?Math.Max(0,config.Memory-t.Age):0;
        t.ValidFor=Math.Min(1.5,t.Remaining); t.Active=Active(now); return t;
    }
    public ImpactTrace End(double now,string reason)
    {
        var t=Snapshot(now); t.State="ended";t.Reason=reason;t.Active=false;t.ValidFor=0;
        Current=new ImpactTrace{State="ended"};Pending=false;pendingSector=null;pendingContact="";pendingEvidence="unlocated";last=next=double.NegativeInfinity;return t;
    }
}

// Value-only policy shared with offline regression tests. Runtime supplies native priority observations.
public static class ImpactPolicy
{
    public static string Priority(bool scoped,bool cooling,bool contact,bool urgent,bool owned,bool nativeAction)
        => !scoped?"impact-out-of-scope":cooling?"impact-error-cooldown":contact?"impact-native-contact"
        :urgent?"impact-native-priority":!owned&&nativeAction?"impact-native-response":"";
    public static bool Moved(double startDistance,double currentDistance,double displacement)
        => ImpactReaction.Finite(startDistance)&&ImpactReaction.Finite(currentDistance)&&ImpactReaction.Finite(displacement)
        &&currentDistance>=0&&startDistance-currentDistance>.25&&displacement>.25;
    public static bool Protection(int baseline,int shield,bool directional,int corners,double length,double maxHeight,double maxAdvance)
        => shield>baseline&&shield>=(directional?2:1)&&corners>=2&&corners<=8
        &&ImpactReaction.Finite(length)&&length>0&&length<=5
        &&ImpactReaction.Finite(maxHeight)&&maxHeight<=.75
        &&(!directional||ImpactReaction.Finite(maxAdvance)&&maxAdvance<=.5);
}
