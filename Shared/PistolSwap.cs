using System;

namespace Comet.Core;

public sealed class PistolSwapConfig
{
    public bool Enabled { get; set; } = true;
    public double MaxDistance { get; set; } = 30;
    public double LowAmmoPercent { get; set; } = 10;
    public int MaxMainRounds { get; set; } = 3;
    public double ContactMemory { get; set; } = 1.5;
    public double RetryDelay { get; set; } = 3;
    public void Validate()
    {
        CometConfig.Range(MaxDistance,5,60,nameof(MaxDistance));
        CometConfig.Range(LowAmmoPercent,0,30,nameof(LowAmmoPercent));
        CometConfig.Range(MaxMainRounds,0,10,nameof(MaxMainRounds));
        CometConfig.Range(ContactMemory,0,3,nameof(ContactMemory));
        CometConfig.Range(RetryDelay,.5,10,nameof(RetryDelay));
    }
}
public sealed class PistolSwapFrame
{
    public string Pistol="",Block="";
    public int Loaded,Chamber,Magazine,Capacity;
    public bool Usable,PrimaryInHands;
}
public sealed class PistolSwapTrace
{
    public string State="choice",Choice="Native",Reason="",Weapon="",Pistol="",Target="",Source="",Action="";
    public int Episode,MainLoaded,MainCapacity,PistolLoaded,PistolChamber,PistolMagazine,PistolCapacity;
    public double Distance,ContactAge,ReloadScore,HoldScore,PistolScore,Elapsed,ValidFor=.5;
    public bool Visible,Protected,Incoming,Requested,Accepted,InHands,Ready;
    public string[] Alternatives=Array.Empty<string>();
}
public sealed class PistolSwapPolicy
{
    public PistolSwapTrace? Trace { get; private set; }
    public bool Active=>Trace?.State is "requested" or "drawing";
    private double retryAt,requestAt,lastNow;
    private int episode;
    public bool RetryBlocked(double now)=>now<retryAt;
    public bool Evaluate(ReloadTimingFrame f,PistolSwapFrame p,ReloadTimingTrace? reload,PistolSwapConfig c)
    {
        if(Active)return false;
        if(f.Now<lastNow)retryAt=0;lastNow=f.Now;
        var t=new PistolSwapTrace{Episode=++episode,Weapon=f.Weapon,Pistol=p.Pistol,Target=f.Target,Source=f.Source,
            MainLoaded=f.Loaded,MainCapacity=f.Capacity,PistolLoaded=p.Loaded,PistolChamber=p.Chamber,
            PistolMagazine=p.Magazine,PistolCapacity=p.Capacity,Distance=f.Distance,ContactAge=f.ContactAge,
            Visible=f.Visible,Protected=f.Protected,Incoming=f.Incoming,
            ReloadScore=reload?.ReloadScore??0,HoldScore=reload?.HoldScore??0,Choice=reload?.Choice??"Native"};
        Trace=t;
        bool No(string why){t.Reason=why;return false;}
        t.Alternatives=new[]{"Pistol: loaded sidearm for an immediate close threat","Reload: keep the primary weapon when protection and resources allow","Keep ready: use remaining rounds or native equipment fallback"};
        if(!c.Enabled)return No("pistol-disabled");
        if(!DoorPolicy.Finite(f.Now)||f.Now<0||!OutdoorRelocation.Valid(f.Distance)||!OutdoorRelocation.Valid(f.ContactAge)
            ||!DoorPolicy.Finite(f.Aggression)||f.Aggression<0||f.Aggression>1||f.Weapon.Length==0||f.Capacity<=0
            ||f.Loaded<0||f.Loaded>f.Capacity||p.Loaded<0||p.Loaded!=p.Chamber+p.Magazine||p.Chamber<0||p.Magazine<0
            ||p.Capacity<0||p.Loaded>p.Capacity||!DoorPolicy.Finite(t.ReloadScore)||!DoorPolicy.Finite(t.HoldScore)
            ||f.Loaded!=f.Chamber+f.Magazine||f.Reserve<0||reload?.Reason=="reload-invalid-context")return No("pistol-invalid-context");
        if(f.Block.Length>0||p.Block.Length>0)return No(p.Block.Length>0?p.Block:f.Block);
        if(!p.PrimaryInHands)return No("pistol-primary-not-in-hands");
        if(RetryBlocked(f.Now))return No("pistol-retry-delay");
        if(p.Pistol.Length==0||!p.Usable||p.Loaded==0)return No("pistol-unavailable");
        if(f.Target.Length==0||(!f.Visible&&f.ContactAge>c.ContactMemory)||f.Distance>c.MaxDistance)return No("pistol-no-close-recent-threat");
        if(f.Loaded>c.MaxMainRounds||100.0*f.Loaded/f.Capacity>c.LowAmmoPercent)return No("pistol-primary-has-useful-ammo");
        if(p.Loaded<=f.Loaded)return No("pistol-not-more-useful");
        double need=1-(double)f.Loaded/f.Capacity;
        t.PistolScore=.2+.6*need+(f.Distance<15?.2:0)+(f.Looking?.15:0)+(f.Incoming?.1:0)
            +(f.Aggression-.5)*.08-(f.Protected?.15:0);
        bool canReload=f.Reserve>0&&f.Resource!="none"&&reload?.Reason!="reload-retry-delay";
        if(canReload&&t.ReloadScore>=t.PistolScore)return No("pistol-reload-preferred");
        if(f.Loaded>0&&t.HoldScore>=t.PistolScore)return No("pistol-keep-primary-ready");
        t.Choice="Pistol";t.Reason="pistol-close-threat-low-ammo";return true;
    }
    public void Request(double now)
    {if(Trace?.Choice!="Pistol"||Active)return;Trace.Requested=true;Trace.State="requested";Trace.Reason="pistol-native-request";requestAt=now;}
    public void Result(bool accepted,double now,PistolSwapConfig c)
    {if(Trace?.Requested!=true)return;Trace.Accepted=accepted;if(!accepted){End("pistol-native-refused");retryAt=now+c.RetryDelay;}}
    public void Observe(double now,string inHands,bool ready,PistolSwapConfig c)
    {
        if(!Active)return;var t=Trace!;
        if(!DoorPolicy.Finite(now)||now<requestAt){End("pistol-clock-changed");retryAt=0;return;}
        t.ContactAge+=Math.Max(0,now-requestAt-t.Elapsed);
        t.Elapsed=now-requestAt;
        if(inHands==t.Pistol){t.InHands=true;t.State="drawing";t.Reason="pistol-in-hands";
            if(ready){t.Ready=true;t.State="ready";t.Reason="pistol-ready-observed";retryAt=now+c.RetryDelay;return;}}
        else if(inHands.Length>0&&(inHands!=t.Weapon||t.InHands)){End("pistol-other-weapon-taken");retryAt=now+c.RetryDelay;return;}
        if(t.Elapsed>=5){End("pistol-switch-unconfirmed");retryAt=now+c.RetryDelay;} // Observation timeout, never cancel animation.
    }
    public void End(string why){if(Trace==null)return;Trace.State="ended";Trace.Reason=why;Trace.ValidFor=0;}
    public void Clear(string why){End(why);retryAt=0;}
}
