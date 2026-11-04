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

// One bot's scalar ownership. Observations, not selector return values, start holding periods.
public sealed class WeaponReturnPolicy
{
    public WeaponReturnTrace? Trace { get; private set; }
    public bool Active=>Trace?.State is "requested" or "drawing";
    private string held="",heldSlot="",refusedTarget="";
    private double readyAt,lastNow=-1,lastThreat=-1,returnUntil,retryUntil,requestedAt;
    private int episode;
    public double PistolBlockedFor(double now)=>Math.Max(0,returnUntil-now);
    public void Observe(double now,string id,string slot,bool ready,WeaponReturnConfig c)
    {
        if(!DoorPolicy.Finite(now)||now<0){Clear("weapon-clock-invalid");return;}
        if(lastNow>=0&&now<lastNow)Clear("weapon-clock-reversed");
        lastNow=now;
        if(ready&&id.Length>0&&id!=held)
        {
            if(heldSlot=="Holster"&&IsPrimary(slot))returnUntil=now+c.PistolReturnDelay;
            readyAt=held.Length==0?now-c.MinimumHold:now;
            held=id;heldSlot=slot;
        }
        if(!Active)return;
        var t=Trace!;
        t.ContactAge+=Math.Max(0,now-requestedAt-t.Elapsed);t.Elapsed=Math.Max(0,now-requestedAt);
        if(id==t.To)
        {
            t.InHands=true;t.State="drawing";t.Reason="weapon-target-in-hands";
            if(ready){t.Ready=true;t.State="ready";t.Reason="weapon-ready-observed";return;}
        }
        else if(id.Length>0&&(id!=t.From||t.InHands)){End("weapon-changed-externally");retryUntil=now+c.RetryDelay;refusedTarget=t.To;return;}
        if(t.Elapsed>=5){End("weapon-switch-unconfirmed");retryUntil=now+c.RetryDelay;refusedTarget=t.To;}
    }
    public static bool IsPrimary(string slot)=>slot is "FirstPrimaryWeapon" or "SecondPrimaryWeapon";
    private static bool ValidGun(WeaponReturnGun g)=>g.Loaded>=0&&g.Chamber>=0&&g.Magazine>=0&&g.Capacity>=0&&g.Reserve>=0
        &&g.Loaded==g.Chamber+g.Magazine&&g.Loaded<=g.Capacity&&(g.Id.Length==0?g.Loaded==0&&!g.Usable:g.Slot=="Holster"||IsPrimary(g.Slot));
    public void NoteThreat(WeaponReturnFrame f)
    {
        if(lastThreat<0)lastThreat=f.Now; // Establish quiet time; absence of a sample is not six seconds of calm.
        if(DoorPolicy.Finite(f.LastThreatAt)&&f.LastThreatAt>=0&&f.LastThreatAt<=f.Now)lastThreat=Math.Max(lastThreat,f.LastThreatAt);
        if(f.Incoming||f.Truncated)lastThreat=f.Now;
    }
    public string Recommend(WeaponReturnFrame f,WeaponReturnConfig c)
    {
        NoteThreat(f);
        if(Active||f.Busy||f.Reloading||!f.Ready)return "";
        // Empty/broken weapons may be replaced even during the ordinary minimum hold.
        if(!f.Current.LoadedUseful)
        {
            foreach(var g in new[]{f.Main,f.Other,f.Pistol})if(g.Id!=f.Current.Id&&g.LoadedUseful)return g.Id;
            if(f.Current.Reloadable)return "";
            foreach(var g in new[]{f.Main,f.Other,f.Pistol})if(g.Id!=f.Current.Id&&g.Reloadable)return g.Id;
            return "";
        }
        if(f.Current.Slot!="Holster")return "";
        bool calm=f.Now-lastThreat>=c.CalmDelay;
        if(!(calm||f.Far))return "";
        foreach(var g in new[]{f.Main,f.Other})if(g.Id!=f.Current.Id&&(g.LoadedUseful||g.Reloadable&&(calm||f.Protected)))return g.Id;
        return "";
    }
    public bool Evaluate(WeaponReturnFrame f,WeaponReturnGun target,string origin,WeaponReturnConfig c)
    {
        if(Active)return false; // Never overwrite the pending request's evidence.
        NoteThreat(f);
        var t=new WeaponReturnTrace{Episode=++episode,Origin=origin,From=f.Current.Id,FromSlot=f.Current.Slot,To=target.Id,ToSlot=target.Slot,
            Loaded=f.Current.Loaded,Reserve=f.Current.Reserve,TargetLoaded=target.Loaded,TargetReserve=target.Reserve,Target=f.Target,Source=f.Source,
            ContactAge=f.ContactAge,Distance=f.Distance,QuietFor=Math.Max(0,f.Now-lastThreat),HoldRemaining=Math.Max(0,readyAt+c.MinimumHold-f.Now),
            ReturnRemaining=PistolBlockedFor(f.Now),RetryRemaining=target.Id==refusedTarget?Math.Max(0,retryUntil-f.Now):0,
            Incoming=f.Incoming,Far=f.Far,Protected=f.Protected,Reloading=f.Reloading,Weapons=new[]{f.Current,f.Main,f.Other,f.Pistol},
            Alternatives=new[]{"Keep: retain a useful loaded weapon and avoid contradictory changes","Reload: give a usable weapon time to reload","Switch: draw a useful loaded weapon, or a reloadable fallback when needed"}};
        Trace=t;
        bool No(string reason){t.Reason=reason;return false;}
        if(!c.Enabled){t.Choice="Native";return No("weapon-return-disabled");}
        if(!DoorPolicy.Finite(f.Now)||f.Now<0||!OutdoorRelocation.Valid(f.ContactAge)||!OutdoorRelocation.Valid(f.Distance)
            ||!ValidGun(f.Current)||!ValidGun(f.Main)||!ValidGun(f.Other)||!ValidGun(f.Pistol)||!ValidGun(target))return No("weapon-invalid-context");
        if(f.Block.Length>0)return No(f.Block);
        if(f.Reloading||f.Busy||!f.Ready)return No("weapon-native-action-in-progress");
        if(target.Id.Length==0||!target.Usable||(!target.LoadedUseful&&!target.Reloadable))return No("weapon-target-unusable");
        if(target.Id==f.Current.Id)return No("weapon-already-held");
        if(t.RetryRemaining>0)return No("weapon-refusal-delay");
        bool emergency=!f.Current.LoadedUseful&&target.LoadedUseful;
        if(!emergency&&t.HoldRemaining>0)return No("weapon-minimum-hold");
        bool urgent=emergency&&(!f.Current.Usable||f.Incoming||!f.Far&&f.Target.Length>0&&f.ContactAge<=1.5);
        if(target.Slot=="Holster"&&t.ReturnRemaining>0&&!urgent&&f.Current.Reloadable)return No("weapon-let-primary-reload");
        if(!target.LoadedUseful)
        {
            if(f.Current.Reloadable&&!f.Current.LoadedUseful)return No("weapon-reload-current");
            if(f.Current.LoadedUseful&&f.Current.Slot!="Holster")return No("weapon-keep-loaded-primary");
            if(f.Current.LoadedUseful&&(target.Slot=="Holster"||!(f.Protected||t.QuietFor>=c.CalmDelay)))return No("weapon-no-safe-reload-return");
        }
        if(f.Current.Slot=="Holster"&&f.Current.LoadedUseful&&IsPrimary(target.Slot)
            &&!(f.Far||t.QuietFor>=c.CalmDelay))return No("weapon-keep-pistol-under-pressure");
        // Native toggles and distance selection must not override W02's close-threat choice.
        if(target.Slot=="Holster"&&f.Current.LoadedUseful&&origin!="W02")return No("weapon-keep-loaded-primary");
        if(origin=="native-toggle"&&f.Current.LoadedUseful)return No("weapon-no-blind-toggle");
        t.Choice="Switch";
        t.Reason=emergency?"weapon-loaded-emergency-fallback":!target.LoadedUseful?"weapon-return-to-reload":origin=="W02"?"weapon-w02-authorized":f.Far?"weapon-threat-far":"weapon-calm-return";
        return true;
    }
    public void Request(double now)
    {if(Trace?.Choice!="Switch"||Active)return;Trace.State="requested";Trace.Requested=true;Trace.Reason="weapon-native-request";requestedAt=now;}
    public void Result(bool accepted,double now,WeaponReturnConfig c)
    {
        if(Trace?.Requested!=true||!Active)return;
        Trace.Accepted=accepted;
        if(!accepted){End("weapon-native-refused");retryUntil=now+c.RetryDelay;refusedTarget=Trace.To;}
    }
    public void End(string reason){if(Trace==null)return;Trace.State="ended";Trace.Reason=reason;Trace.ValidFor=0;}
    public void Clear(string reason)
    {End(reason);held=heldSlot=refusedTarget="";readyAt=returnUntil=retryUntil=0;lastThreat=lastNow=-1;}
}
