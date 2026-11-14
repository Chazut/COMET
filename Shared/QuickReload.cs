using System;

namespace Comet.Core;

public sealed class QuickReloadConfig
{
    public bool Enabled { get; set; } = true;
    public bool RecoverDroppedMagazine { get; set; } = true;
    public double MaxAmmoPercent { get; set; } = 30;
    public double ThreatDistance { get; set; } = 35;
    public double ContactMemory { get; set; } = 3;
    // Includes the replacement: at least one loaded spare remains with the default.
    public int MinimumSpareMagazines { get; set; } = 2;
    public void Validate()
    {
        CometConfig.Range(MaxAmmoPercent,0,50,nameof(MaxAmmoPercent));
        CometConfig.Range(ThreatDistance,5,60,nameof(ThreatDistance));
        CometConfig.Range(ContactMemory,.5,5,nameof(ContactMemory));
        CometConfig.Range(MinimumSpareMagazines,2,5,nameof(MinimumSpareMagazines));
    }
}
public sealed class QuickReloadFrame
{
    public double Now,Distance,ContactAge=999;
    public string Weapon="",OldMagazine="",Replacement="",Target="",Source="personal-memory",Block="";
    public int Loaded,Capacity,OldRounds,ReplacementRounds,SpareMagazines;
    public bool External,Ready,Reachable,Incoming,Protected,InFight,Urgent,UtilityReload=true;
}
public sealed class QuickReloadTrace
{
    public string State="choice",Choice="Normal",Reason="",Action="",Weapon="",OldMagazine="",Replacement="",Target="",Source="personal-memory";
    public int Episode,Loaded,Capacity,OldRounds,ReplacementRounds,SpareMagazines,After,DroppedRounds;
    public double Distance,ContactAge,Elapsed,ValidFor=.5;
    public bool Incoming,Protected,Requested,Started,CallbackSeen,CallbackSuccess,Dropped,ReplacementInserted,Finished,Improved;
    public string[] Alternatives=Array.Empty<string>();
}

// Evidence only. Never changes ammunition, timing, inventory or native operations.
public sealed class QuickReloadPolicy
{
    public QuickReloadTrace? Trace { get; private set; }
    public bool Active=>Trace?.State is "requested" or "reloading";
    private double requestedAt,lastNow=-1;
    private int episode;
    public bool Evaluate(QuickReloadFrame f,QuickReloadConfig c)
    {

        Trace=new(){Episode=++episode,Weapon=f.Weapon,OldMagazine=f.OldMagazine,Replacement=f.Replacement,Target=f.Target,Source=f.Source,
            Loaded=f.Loaded,Capacity=f.Capacity,OldRounds=f.OldRounds,ReplacementRounds=f.ReplacementRounds,SpareMagazines=f.SpareMagazines,
            Distance=f.Distance,ContactAge=f.ContactAge,Incoming=f.Incoming,Protected=f.Protected,
            Alternatives=new[]{"Normal reload: retain the old magazine when inventory space permits","Pistol: W02 decides before the reload request","Quick reload: native animation, old magazine and its rounds left behind"}};
        string reason=!c.Enabled?"quick-disabled":!f.InFight?"quick-not-in-fight":
            !f.Urgent&&f.UtilityReload&&f.Capacity>0&&f.Loaded*100.0/f.Capacity>=c.MaxAmmoPercent?"quick-preserve-loaded-magazine":
            f.SpareMagazines<c.MinimumSpareMagazines?"quick-preserve-spare-magazines":"quick-urgent-low-ammo";
        Trace.Reason=reason;Trace.Choice=reason=="quick-urgent-low-ammo"?"Quick":"Normal";
        return Trace.Choice=="Quick";
    }
    private static bool Valid(QuickReloadFrame f)=>DoorPolicy.Finite(f.Now)&&f.Now>=0&&DoorPolicy.Finite(f.Distance)&&f.Distance>=0
        &&DoorPolicy.Finite(f.ContactAge)&&f.ContactAge>=0&&f.Weapon.Length>0&&f.Replacement.Length>0
        &&f.Capacity>0&&f.Loaded>=0&&f.Loaded<=f.Capacity&&f.OldRounds>=0&&f.OldRounds<=f.Loaded&&f.ReplacementRounds>0&&f.SpareMagazines>=0;
    public void Request(double now)
    {
        if(Trace?.Choice!="Quick"||Active)return;
        requestedAt=lastNow=now;Trace.Requested=true;Trace.State="requested";Trace.Reason="quick-requested";
    }
    public void Callback(bool success)
    {
        if(!Active)return;
        Trace!.CallbackSeen=true;Trace.CallbackSuccess=success;
        if(!success)End("quick-native-refused");
    }
    public void Drop(string magazine,int rounds)
    {
        if(!Active||magazine!=Trace!.OldMagazine||rounds<0)return;
        Trace.Dropped=true;Trace.DroppedRounds=rounds;Trace.Reason="quick-drop-observed";
    }
    public void Observe(double now,bool sameWeapon,bool nativeOperation,bool nativeFlag,string magazine,int loaded)
    {
        if(!Active)return;
        if(!DoorPolicy.Finite(now)||now<lastNow){End("quick-clock-invalid");return;}
        var t=Trace!;t.ContactAge+=now-lastNow;lastNow=now;t.Elapsed=now-requestedAt;
        if(!sameWeapon){End("quick-weapon-changed");return;}
        if(loaded<0){End("quick-invalid-ammo");return;}
        t.After=loaded;t.ReplacementInserted=magazine==t.Replacement;
        if(nativeOperation){t.Started=true;t.State="reloading";if(!t.Dropped)t.Reason="quick-animation-observed";}
        if(!nativeOperation&&!nativeFlag&&t.CallbackSuccess&&t.ReplacementInserted)
        {t.Finished=true;t.Improved=loaded>t.Loaded;t.State="completed";t.Reason=t.Dropped?"quick-complete-drop-confirmed":"quick-complete-drop-unconfirmed";return;}
        if(t.Started&&!nativeOperation&&!nativeFlag){End("quick-interrupted");return;}
        if(!t.Started&&t.Elapsed>=3){End("quick-start-unconfirmed");return;}
        if(t.Elapsed>=60)End("quick-observation-timeout");
    }
    public void End(string reason)
    {if(Trace==null)return;Trace.State="ended";Trace.Reason=reason;Trace.ValidFor=0;}
}
