using System;

namespace Comet.Core;

public sealed class WeaponReturnConfig
{
    public bool Enabled { get; set; } = true;
    public double MinimumHold { get; set; } = 3;
    public double CalmDelay { get; set; } = 6;
    public double DistanceMargin { get; set; } = 15;
    public double PistolReturnDelay { get; set; } = 5;
    public double RetryDelay { get; set; } = 3;
    public void Validate()
    {
        CometConfig.Range(MinimumHold,0,10,nameof(MinimumHold));
        CometConfig.Range(CalmDelay,1,20,nameof(CalmDelay));
        CometConfig.Range(DistanceMargin,5,40,nameof(DistanceMargin));
        CometConfig.Range(PistolReturnDelay,0,15,nameof(PistolReturnDelay));
        CometConfig.Range(RetryDelay,.5,10,nameof(RetryDelay));
    }
}

public sealed class WeaponReturnGun
{
    public string Id="",Slot="",Resource="none";
    public int Loaded,Chamber,Magazine,Capacity,Reserve;
    public bool Usable;
    public bool LoadedUseful=>Usable&&Loaded>0;
    public bool Reloadable=>Usable&&Reserve>0&&Resource!="none";
}
public sealed class WeaponReturnFrame
{
    public double Now,ContactAge=999,Distance,LastThreatAt=-1;
    public string Target="",Source="personal-memory",Block="";
    public bool Incoming,Far,Protected,Ready,Reloading,Busy,Truncated;
    public WeaponReturnGun Current=new(),Main=new(),Other=new(),Pistol=new();
}
public sealed class WeaponReturnTrace
{
    public string State="choice",Choice="Keep",Reason="",Origin="",Action="",From="",To="",FromSlot="",ToSlot="",Target="",Source="personal-memory";
    public int Episode,Loaded,Reserve,TargetLoaded,TargetReserve;
    public double ContactAge,Distance,QuietFor,HoldRemaining,ReturnRemaining,RetryRemaining,Elapsed,ValidFor=.5;
    public bool Incoming,Far,Protected,Reloading,Requested,Accepted,InHands,Ready;
    public WeaponReturnGun[] Weapons=Array.Empty<WeaponReturnGun>();
    public string[] Alternatives=Array.Empty<string>();
}

// Source: CombatWeaponClass and AmmoAwareWeaponSwapPatch. Evidence never vetoes a draw.
public sealed class WeaponReturnPolicy
{
    public WeaponReturnTrace? Trace { get; private set; }
    public bool Active=>Trace?.State is "requested" or "drawing";
    public bool PistolByUs { get; private set; }
    private string lastSlot="";
    private double returnUntil,requestAt,ourChange=-10;
    private int episode;
    public double PistolBlockedFor(double now)=>Math.Max(0,returnUntil-now);
    public static bool IsPrimary(string slot)=>slot is "FirstPrimaryWeapon" or "SecondPrimaryWeapon";
    public void Observe(double now,string id,string slot,bool ready,WeaponReturnConfig c)
    {
        if(slot.Length>0) {
            if(PistolByUs&&slot!="Holster" || lastSlot=="Holster"&&slot!="Holster"&&now-ourChange>1)
            {PistolByUs=false;returnUntil=Math.Max(returnUntil,now+c.PistolReturnDelay);}
            lastSlot=slot;
        }
        if(!Active)return;
        var t=Trace!;t.Elapsed=Math.Max(0,now-requestAt);
        if(id==t.To){t.InHands=true;t.State="drawing";t.Reason="weapon-target-in-hands";
            if(ready){t.Ready=true;t.State="ready";t.Reason="weapon-ready-observed";}}
        else if(id.Length>0&&(id!=t.From||t.InHands))End("weapon-changed-externally");
        else if(t.Elapsed>=5)End("weapon-switch-unconfirmed"); // Telemetry only.
    }
    public void NoteThreat(WeaponReturnFrame f){}
    public string Recommend(WeaponReturnFrame f,WeaponReturnConfig c)
    {
        if(!f.Ready||f.Reloading)return "";
        return PistolByUs&&f.Current.Slot=="Holster"&&(f.Current.Loaded<=0||f.Target.Length==0||f.ContactAge>c.CalmDelay&&!f.Incoming||f.Far)?f.Main.Id:"";
    }
    public string Fallback(WeaponReturnFrame f)
    {
        if(f.Current.Loaded>0)return "";
        // Preserve source order: every loaded alternative precedes any reloadable one.
        if(f.Main.Id!=f.Current.Id&&f.Main.Loaded>0)return f.Main.Id;
        if(f.Other.Id!=f.Current.Id&&f.Other.Loaded>0)return f.Other.Id;
        if(f.Pistol.Id!=f.Current.Id&&f.Pistol.Loaded>0)return f.Pistol.Id;
        if(f.Main.Id!=f.Current.Id&&f.Main.Reloadable)return f.Main.Id;
        if(f.Other.Id!=f.Current.Id&&f.Other.Reloadable)return f.Other.Id;
        if(f.Pistol.Id!=f.Current.Id&&f.Pistol.Reloadable)return f.Pistol.Id;
        return "";
    }
    public bool Evaluate(WeaponReturnFrame f,WeaponReturnGun target,string origin,WeaponReturnConfig c)
    {
        Trace=new(){Episode=++episode,Origin=origin,From=f.Current.Id,FromSlot=f.Current.Slot,To=target.Id,ToSlot=target.Slot,
            Loaded=f.Current.Loaded,Reserve=f.Current.Reserve,TargetLoaded=target.Loaded,TargetReserve=target.Reserve,
            Target=f.Target,Source=f.Source,ContactAge=f.ContactAge,Distance=f.Distance,Far=f.Far,Incoming=f.Incoming,
            Weapons=new[]{f.Main,f.Other,f.Pistol}};
        if(target.Id.Length==0){Trace.Reason="weapon-slot-missing";return false;}
        if(origin=="native-return"&&target.Loaded<=0&&!target.Reloadable){Trace.Reason="weapon-target-unusable";return false;}
        Trace.Choice="Switch";Trace.Reason="weapon-source-selection";return true;
    }
    public void Request(double now){if(Trace==null)return;Trace.Requested=true;Trace.State="requested";requestAt=ourChange=now;}
    public void Result(bool accepted,double now,WeaponReturnConfig c)
    {
        if(Trace==null)return;Trace.Accepted=accepted;
        if(!accepted){if(Trace.ToSlot=="Holster")returnUntil=now+c.RetryDelay;End("weapon-native-refused");return;}
        if(Trace.Origin=="W02")PistolByUs=true;
        if(Trace.FromSlot=="Holster"&&Trace.ToSlot!="Holster"){PistolByUs=false;returnUntil=now+c.PistolReturnDelay;}
    }
    public void End(string reason){if(Trace==null)return;Trace.State="ended";Trace.Reason=reason;Trace.ValidFor=0;}
    public void Clear(string reason){End(reason);lastSlot="";PistolByUs=false;returnUntil=0;ourChange=-10;}
}
