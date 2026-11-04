using System;
using System.Collections.Generic;

namespace Comet.Core;

public sealed class SuppressionDisciplineConfig
{
    public bool Enabled { get; set; }=true;
    public double ContactAge { get; set; }=3;
    public int BurstRounds { get; set; }=5;
    public double PauseSeconds { get; set; }=3;
    public double ReservePercent { get; set; }=60;
    public double SequenceSeconds { get; set; }=6;
    public void Validate()
    {
        CometConfig.Range(ContactAge,.5,10,nameof(ContactAge));CometConfig.Range(BurstRounds,1,15,nameof(BurstRounds));
        CometConfig.Range(PauseSeconds,.5,8,nameof(PauseSeconds));CometConfig.Range(ReservePercent,20,90,nameof(ReservePercent));
        CometConfig.Range(SequenceSeconds,1,15,nameof(SequenceSeconds));
    }
    public int Reserve(int capacity)=>(int)Math.Ceiling(capacity*ReservePercent/100);
}
public sealed class SuppressionDisciplineFrame
{
    public double Now,ObservedAt=-1;
    public string Target="",Identity="",Weapon="",Mode="",Source="",Action="",Block="",SupportPlan="";
    public int Loaded,Capacity;
    public DoorPoint Known;
    public bool Visible,KnownContact,FreshGeometry,Clear,AllySafe,Aligned,SupportUseful=true;
}
public sealed class SuppressionDisciplineTrace
{
    public string State="waiting",Reason="suppression-request",Target="",Weapon="",Mode="",Source="",Action="",SupportPlan="";
    public int Episode,Loaded,Capacity,Reserve,BurstLimit,BurstShots,ActualShots;
    public double ContactAge,Elapsed,Remaining,PauseRemaining,ValidFor,KnownX,KnownY,KnownZ,AimX,AimY,AimZ;
    public bool Requested,Accepted,Clear,AllySafe,Aligned;
    public string[] Alternatives={"Normal SAIN fire when a target is visible","Keep ammunition and wait","Continue the native movement or cover action"};
}
// Permission for native suppression, never a scheduler that fabricates shots or changes the weapon's fire mode.
public sealed class SuppressionDisciplinePolicy
{
    public SuppressionDisciplineTrace? Trace { get; private set; }
    public bool Active=>Trace!=null&&Trace.State!="ended"&&Trace.State!="rejected";
    public bool MayFire=>Active&&Trace!.State=="firing";
    public DoorPoint Aim { get; private set; }
    private readonly Dictionary<string,double> consumed=new();
    private string identity="";
    private double started,last=-1,latestCue=-1,pauseUntil;
    private int episode,quotaRemaining;
    public bool Evaluate(SuppressionDisciplineFrame f,SuppressionDisciplineConfig c)
    {
        if(last>=0&&f.Now<last){End("suppression-clock-reset");consumed.Clear();}
        last=f.Now;
        if(Active&&(identity!=f.Identity||Trace!.Weapon!=f.Weapon||Trace.Mode!=f.Mode||Trace.Action!=f.Action||Trace.SupportPlan!=f.SupportPlan))End("suppression-context-changed");
        string gate=Gate(f,c);
        if(!Active)
        {
            if(gate.Length==0&&consumed.TryGetValue(f.Identity,out var used)&&f.ObservedAt<=used+.001)gate="suppression-needs-new-contact";
            Trace=new(){Episode=episode,Target=f.Target,Weapon=f.Weapon,Mode=f.Mode,Source=f.Source,Action=f.Action,SupportPlan=f.SupportPlan,
                State="rejected",Reason=gate,Capacity=Math.Max(0,f.Capacity),Reserve=c.Reserve(Math.Max(0,f.Capacity)),BurstLimit=c.BurstRounds};
            if(gate.Length>0){Snapshot(f);return false;}
            identity=f.Identity;started=f.Now;latestCue=f.ObservedAt;pauseUntil=0;Trace.State="waiting";Trace.Episode=++episode;
            Aim=f.Known+new DoorPoint(0,1.2,0);quotaRemaining=Math.Max(0,f.Loaded-Trace.Reserve);
        }
        if(gate.Length>0){Snapshot(f);End(gate);return false;}
        latestCue=Math.Max(latestCue,f.ObservedAt);
        if(f.Now-started>=c.SequenceSeconds){Snapshot(f);End("suppression-sequence-limit");return false;}
        if(quotaRemaining<=0){Snapshot(f);End("suppression-ammo-reserve");return false;}
        Snapshot(f);Trace!.Remaining=Math.Max(0,c.SequenceSeconds-(f.Now-started));Trace.ValidFor=Math.Min(.3,Trace.Remaining);
        if(f.Now<pauseUntil){Trace.State="paused";Trace.Reason="suppression-burst-pause";return false;}
        if(Trace.State=="paused")
        {Trace.BurstShots=0;Trace.Requested=Trace.Accepted=false;Aim=f.Known+new DoorPoint(0,1.2,0);Snapshot(f);}
        // Geometry may be deferred by the common budget. This is never permission to keep firing on old checks.
        if(!f.FreshGeometry||!f.Clear||!f.AllySafe||!f.Aligned)
        {Trace.State="waiting";Trace.Reason=!f.FreshGeometry?"suppression-check-deferred":!f.AllySafe?"suppression-ally-in-line":!f.Clear?"suppression-blocked-angle":"suppression-aligning";return false;}
        Trace.State="firing";Trace.Reason="suppression-permitted";return true;
    }
    public static string Gate(SuppressionDisciplineFrame f,SuppressionDisciplineConfig c)
    {
        if(!c.Enabled)return "suppression-disabled";
        if(f.Block.Length>0)return f.Block;
        if(f.Visible)return "suppression-visible-target";
        if(!f.SupportUseful)return "suppression-support-ended";
        if(!f.KnownContact||f.Target.Length==0||f.Identity.Length==0||f.Weapon.Length==0
            ||!DoorPolicy.Finite(f.Now)||!DoorPolicy.Finite(f.ObservedAt)||f.ObservedAt<0||f.Now<f.ObservedAt
            ||!CornerChase.Point(f.Known)||f.Source is not ("personal-vision" or "personal-incoming"))return "suppression-no-personal-contact";
        if(f.Now-f.ObservedAt>c.ContactAge)return "suppression-contact-expired";
        if(f.Capacity<=0||f.Loaded<0||f.Loaded>f.Capacity)return "suppression-ammo-unknown";
        if(f.Loaded<=c.Reserve(f.Capacity))return "suppression-ammo-reserve";
        return "";
    }
    public void Request(){if(MayFire)Trace!.Requested=true;}
    public void Result(bool accepted)
    {if(Trace?.Requested!=true)return;Trace.Accepted|=accepted;if(Active&&!accepted){Trace.State="waiting";Trace.Reason="suppression-native-refused";}}
    public void Shot(SuppressionDisciplineFrame f,SuppressionDisciplineConfig c,double random)
    {
        if(!Active||Trace!.Requested!=true)return;
        bool outsidePermission=!MayFire;
        Trace.ActualShots++;Trace.BurstShots++;quotaRemaining--;Snapshot(f);
        if(outsidePermission){End("suppression-shot-outside-permission");return;}
        Trace.Remaining=Math.Max(0,c.SequenceSeconds-Trace.Elapsed);Trace.ValidFor=Math.Min(.3,Trace.Remaining);
        if(Trace.Remaining<=0){End("suppression-sequence-limit");return;}
        // Own shot callbacks can run before ammo is consumed: the quota makes that ordering safe.
        if(quotaRemaining<=0||f.Loaded-1<=Trace.Reserve){End("suppression-ammo-reserve");return;}
        if(Trace.BurstShots>=c.BurstRounds)
        {
            var roll=DoorPolicy.Finite(random)?Math.Max(0,Math.Min(1,random)):.5;
            pauseUntil=f.Now+c.PauseSeconds*(.7+.6*roll);Trace.State="paused";Trace.Reason="suppression-burst-pause";
            Trace.PauseRemaining=pauseUntil-f.Now;return;
        }
        Trace.Reason="suppression-shot-observed";
    }
    private void Snapshot(SuppressionDisciplineFrame f)
    {
        var t=Trace!;t.Loaded=Math.Max(0,f.Loaded);t.ContactAge=DoorPolicy.Finite(f.Now)&&DoorPolicy.Finite(f.ObservedAt)&&f.ObservedAt>=0?Math.Max(0,f.Now-f.ObservedAt):0;
        t.Elapsed=Active&&DoorPolicy.Finite(f.Now)?Math.Max(0,f.Now-started):0;t.PauseRemaining=Active&&DoorPolicy.Finite(f.Now)?Math.Max(0,pauseUntil-f.Now):0;
        t.Clear=f.Clear;t.AllySafe=f.AllySafe;t.Aligned=f.Aligned;t.Source=f.Source;
        var known=CornerChase.Point(f.Known)?f.Known:default;
        t.KnownX=known.X;t.KnownY=known.Y;t.KnownZ=known.Z;t.AimX=Aim.X;t.AimY=Aim.Y;t.AimZ=Aim.Z;
    }
    public void End(string reason)
    {
        if(!Active)return;
        if(consumed.Count>=16&&!consumed.ContainsKey(identity)) {string? oldest=null;double at=double.MaxValue;foreach(var e in consumed)if(e.Value<at){oldest=e.Key;at=e.Value;}if(oldest!=null)consumed.Remove(oldest);}
        consumed[identity]=latestCue;Trace!.State="ended";Trace.Reason=reason;Trace.ValidFor=Trace.Remaining=Trace.PauseRemaining=0;
    }
}
