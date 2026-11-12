using System;

namespace Comet.Core;

public sealed class CornerPrefireConfig
{
    public bool Enabled { get; set; }=true;
    public double Chance { get; set; }=60;
    public double ObservationMaxAge { get; set; }=2.5;
    public double KnownCornerDistance { get; set; }=3;
    public double FireCornerDistance { get; set; }=4;
    public int AutomaticRounds { get; set; }=4;
    public int SingleRounds { get; set; }=1;
    public double FireLimit { get; set; }=1.5;
    public int StartRounds { get; set; }=10;
    public double StartPercent { get; set; }=40;
    public int ReserveRounds { get; set; }=5;
    public double ReservePercent { get; set; }=30;
    public void Validate()
    {
        void R(double n,double min,double max){if(!DoorPolicy.Finite(n)||n<min||n>max)throw new ArgumentException("Corner prefire: invalid limit.");}
        R(Chance,0,100);R(ObservationMaxAge,.5,3);R(KnownCornerDistance,.5,4);R(FireCornerDistance,1,5);
        R(AutomaticRounds,1,6);R(SingleRounds,1,1);R(FireLimit,.1,1.5);R(StartRounds,1,30);R(StartPercent,10,100);
        R(ReserveRounds,1,20);R(ReservePercent,10,90);
        if(StartRounds<=ReserveRounds||StartPercent<=ReservePercent)throw new ArgumentException("Corner prefire: start ammunition must exceed reserve.");
    }
    public int Start(int capacity)=>Math.Max(StartRounds,(int)Math.Ceiling(capacity*StartPercent/100));
    public int Reserve(int capacity)=>Math.Max(ReserveRounds,(int)Math.Ceiling(capacity*ReservePercent/100));
}
public sealed class CornerPrefireFrame
{
    public double Now,SeenAt,ParentDeadline,CornerDistance,KnownCornerDistance;
    public int ParentEpisode,Ammo,Capacity;
    public string Target="",Weapon="",Mode="",ParentStyle="pie",Block="";
    public DoorPoint Known,Aim;
    public bool ParentActive,Automatic,Clear,AllySafe=true,Aligned,Fresh=true;
}
public sealed class CornerPrefireTrace
{
    public string State="waiting",Reason="prefire-selected",Target="",Weapon="",Mode="",Action="RushEnemy",Source="personal-vision";
    public int Episode,ParentEpisode,AmmoStart,Ammo,Capacity,Reserve,Quota,ActualShots;
    public double Chance,Roll,Remaining,ValidFor,ContactAge,CornerDistance,KnownCornerDistance,KnownX,KnownY,KnownZ,AimX,AimY,AimZ,FireElapsed;
    public bool Selected,Requested,Accepted,Clear,AllySafe,Aligned;
    public string[] Alternatives=Array.Empty<string>();
}
// M05 owns the route and its retry cooldown. M06 consumes that opportunity once and only owns a short firing lease.
public sealed class CornerPrefire
{
    public CornerPrefireTrace? Trace { get; private set; }
    public bool Active=>Trace is {Selected:true}&&Trace.State!="ended";
    public double Deadline { get; private set; }
    private int consumed=-1,episode;
    private double seenAt,last,fireAt=-1,fireLimit;
    public bool Begin(CornerPrefireFrame f,CornerPrefireConfig c,double roll)
    {
        if(Active||!f.ParentActive||f.ParentEpisode==consumed)return false;
        consumed=f.ParentEpisode;seenAt=f.SeenAt;last=f.Now;fireAt=-1;
        Trace=new(){Episode=++episode,ParentEpisode=f.ParentEpisode,Target=f.Target,Weapon=f.Weapon,Mode=f.Mode,
            Chance=c.Chance,Roll=DoorPolicy.Finite(roll)?Math.Max(0,Math.Min(99.999999,roll*100)):0,
            AmmoStart=Math.Max(0,f.Ammo),Capacity=Math.Max(0,f.Capacity),Reserve=c.Reserve(Math.Max(0,f.Capacity)),Quota=f.Automatic?c.AutomaticRounds:c.SingleRounds,
            KnownX=f.Known.X,KnownY=f.Known.Y,KnownZ=f.Known.Z,AimX=f.Aim.X,AimY=f.Aim.Y,AimZ=f.Aim.Z,
            Alternatives=new[]{"Continue M05 walking approach without prefire", "Reacquired contact: yield to native SAIN shooting", "Jump approach: prefire excluded"}};
        Deadline=f.ParentDeadline;Snapshot(f);
        string gate=Gate(f,c);
        if(gate.Length==0&&f.Ammo<c.Start(f.Capacity))gate="prefire-start-ammo";
        // Style has already been drawn by CornerChase; no second independent roll.
        if(gate.Length>0){End(gate);return false;}
        Trace.Selected=true;Snapshot(f);return true;
    }
    private string Gate(CornerPrefireFrame f,CornerPrefireConfig c)
    {
        if(!c.Enabled)return "prefire-disabled";
        if(!f.ParentActive||f.ParentEpisode!=Trace!.ParentEpisode)return "prefire-parent-ended";
        if(f.ParentStyle!="prefire")return "prefire-other-style";
        if(f.Block.Length>0)return f.Block;
        if(f.Target!=Trace.Target||f.Weapon!=Trace.Weapon||f.Mode!=Trace.Mode)return "prefire-contact-or-weapon-changed";
        if(f.Target.Length==0||f.Weapon.Length==0||!DoorPolicy.Finite(f.Now)||!DoorPolicy.Finite(seenAt)||seenAt<0||f.Now<seenAt
            ||!DoorPolicy.Finite(f.ParentDeadline)||!CornerChase.Point(f.Aim)||!CornerChase.Point(f.Known)
            ||!DoorPolicy.Finite(f.CornerDistance)||f.CornerDistance<0||!DoorPolicy.Finite(f.KnownCornerDistance)||f.KnownCornerDistance<0)return "prefire-invalid-evidence";


        if(f.Capacity<=0||f.Ammo<=Trace.Reserve)return "prefire-ammo-reserve";
        // ManualShoot performs the source friendly-fire check.
        if(f.Now>=Deadline)return "prefire-window-expired";
        return "";
    }
    public bool Observe(CornerPrefireFrame f,CornerPrefireConfig c)
    {
        if(!Active)return false;
        Snapshot(f);string gate=Gate(f,c);
        if(gate.Length==0&&f.Now<last)gate="prefire-callback-expired";

        if(gate.Length>0){End(gate);return false;}
        last=f.Now;
        if(!Trace!.Requested)Trace.Reason=!f.Fresh?"prefire-awaiting-safety":!f.Clear?"prefire-awaiting-muzzle-clear":f.CornerDistance>c.FireCornerDistance?"prefire-approaching-corner":!f.Aligned?"prefire-aiming":"prefire-ready";
        return true;
    }
    public bool Request(CornerPrefireFrame f,CornerPrefireConfig c)
    {
        if(!Observe(f,c)||f.CornerDistance>c.FireCornerDistance)return false;
        if(fireAt<0&&f.Ammo<c.Start(f.Capacity)){End("prefire-start-ammo");return false;}
        Trace!.Requested=true;Trace.State="firing";Trace.Reason="prefire-shot-requested";
        fireLimit=c.FireLimit;Snapshot(f);return true;
    }
    // A native request may synchronously emit the shot callback before returning its acceptance.
    public bool AuthorizeRequest(CornerPrefireFrame f,CornerPrefireConfig c)
    {
        if(!Active||Trace!.Requested!=true)return false;
        return Observe(f,c);
    }
    public void Result(bool accepted)
    {
        if(Trace?.Requested!=true)return;
        Trace.Accepted=accepted;
        if(accepted){if(fireAt<0){fireAt=last;Deadline=Math.Min(Deadline,last+fireLimit);}}
        else {Trace.Requested=false;Trace.State="waiting";Trace.Reason="prefire-native-retry";}
    }
    public void Shot(CornerPrefireFrame f,CornerPrefireConfig c)
    {
        if(!Active||Trace!.Requested!=true)return;
        Trace.ActualShots++;Snapshot(f);
        if(f.Ammo<=Trace.Reserve)End("prefire-ammo-reserve");
        else if(Observe(f,c))Trace.Reason="prefire-shot-observed";
    }
    private void Snapshot(CornerPrefireFrame f)
    {
        var t=Trace!;t.Ammo=Math.Max(0,f.Ammo);t.ContactAge=Math.Max(0,f.Now-seenAt);t.CornerDistance=f.CornerDistance;t.KnownCornerDistance=f.KnownCornerDistance;
        t.Clear=f.Clear;t.AllySafe=f.AllySafe;t.Aligned=f.Aligned;t.Remaining=Active?Math.Max(0,Deadline-f.Now):0;t.ValidFor=Math.Min(.2,t.Remaining);
        t.FireElapsed=fireAt>=0?Math.Max(0,f.Now-fireAt):0;
    }
    public void End(string reason){if(Trace==null||Trace.State=="ended")return;Trace.State="ended";Trace.Reason=reason;Trace.Remaining=Trace.ValidFor=0;}
}
