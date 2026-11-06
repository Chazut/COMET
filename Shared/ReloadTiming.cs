using System;

namespace Comet.Core;

public sealed class ReloadTimingConfig
{
    public bool Enabled { get; set; } = true;
    public double ConsiderBelow { get; set; } = 80;
    public double CriticalBelow { get; set; } = 20;
    public double CloseDistance { get; set; } = 25;
    public double LostContactDelay { get; set; } = 2;
    public double Commitment { get; set; } = .6;
    public double RetryDelay { get; set; } = 2;
    public void Validate()
    {
        CometConfig.Range(ConsiderBelow,30,95,nameof(ConsiderBelow));
        CometConfig.Range(CriticalBelow,5,40,nameof(CriticalBelow));
        CometConfig.Range(CloseDistance,5,50,nameof(CloseDistance));
        CometConfig.Range(LostContactDelay,.5,5,nameof(LostContactDelay));
        CometConfig.Range(Commitment,0,2,nameof(Commitment));
        CometConfig.Range(RetryDelay,.5,10,nameof(RetryDelay));
        if(CriticalBelow>=ConsiderBelow)throw new ArgumentException("Critical ammo must be below the reload consideration threshold.");
    }
}
public sealed class ReloadTimingFrame
{
    public double Now,Distance,SightAge,ContactAge,Aggression=.5;
    public string Weapon="",Target="",Source="personal-memory",WeaponClass="",Resource="none",ResourceKey="",Block="";
    public int Loaded,Capacity,Chamber,Magazine,Reserve,Threats;
    public bool Visible,Protected,ThreatsTruncated,Incoming,Looking,BusyObserved;
}
public sealed class ReloadTimingTrace
{
    public string State="choice",Choice="Native",Reason="",Weapon="",Target="",Source="",WeaponClass="",Resource="none",Action="";
    public int Episode,Loaded,Capacity,Chamber,Magazine,Reserve,Threats,ReserveForFight,After,RepeatedFailures;
    public double Ratio,ReloadScore,HoldScore,ContactAge,Distance,Elapsed,ValidFor=.5;
    public bool Critical,Protected,Visible,Incoming,BusyObserved,Requested,Accepted,Started,Finished,Improved;
    public string[] Alternatives=Array.Empty<string>();
}
// Per-bot scalar state only. Hold postpones reloading, it never owns movement, shooting or healing.
public sealed class ReloadTimingPolicy
{
    public ReloadTimingTrace? Trace { get; private set; }
    private double choiceAt,requestAt,retryAt,lastNow;
    private string previousKey="",refusedKey="";
    private int episode;
    private int failures;
    public bool Active=>Trace?.State is "requested" or "reloading";
    public static int FightReserve(string weaponClass)=>weaponClass switch {"shotgun"=>2,"sniperRifle"=>2,"machinegun"=>10,"smg"=>8,_=>5};
    private static string Key(ReloadTimingFrame f)=>f.Weapon+":"+f.Loaded+":"+f.ResourceKey;
    public bool RetryBlocked(ReloadTimingFrame f)=>f.Now<retryAt&&Key(f)==refusedKey;
    public string Evaluate(ReloadTimingFrame f,ReloadTimingConfig c)
    {
        if(Active)return "Pending";
        if(f.Now<lastNow){retryAt=choiceAt=0;failures=0;previousKey=refusedKey="";}lastNow=f.Now;
        var old=Trace;
        var t=new ReloadTimingTrace{Episode=++episode,Weapon=f.Weapon,Target=f.Target,Source=f.Source,WeaponClass=f.WeaponClass,
            Loaded=f.Loaded,After=f.Loaded,Capacity=f.Capacity,Chamber=f.Chamber,Magazine=f.Magazine,Reserve=f.Reserve,Resource=f.Resource,
            Threats=f.Threats,Protected=f.Protected,Visible=f.Visible,Incoming=f.Incoming,BusyObserved=f.BusyObserved,
            ContactAge=f.ContactAge,Distance=f.Distance,ReserveForFight=FightReserve(f.WeaponClass)};
        Trace=t;
        string Set(string choice,string reason){t.Choice=choice;t.Reason=reason;return choice;}
        if(f.Block.Length>0)return Set("Native",f.Block);
        if(!DoorPolicy.Finite(f.Now)||f.Now<0||!OutdoorRelocation.Valid(f.Distance)||!OutdoorRelocation.Valid(f.SightAge)
            ||!OutdoorRelocation.Valid(f.ContactAge)||!DoorPolicy.Finite(f.Aggression)||f.Aggression<0||f.Aggression>1
            ||f.Loaded<0||f.Capacity<=0||f.Loaded>f.Capacity||f.Chamber<0||f.Magazine<0||f.Loaded!=f.Chamber+f.Magazine
            ||f.Reserve<0||f.Threats<0||f.Weapon.Length==0)return Set("Native","reload-invalid-context");
        t.Ratio=(double)f.Loaded/f.Capacity;
        t.Critical=f.Loaded==0||(t.Ratio<c.CriticalBelow/100&&f.Loaded<=t.ReserveForFight);
        t.Alternatives=new[]{"Reload: compare need, protection and known threats","Keep weapon ready: SAIN continues combat","Native fallback: weapon choice remains with SAIN"};
        bool justLost=!f.Visible&&Math.Min(f.SightAge,f.ContactAge)<c.LostContactDelay&&f.Distance<c.CloseDistance;
        bool quiet=!f.Visible&&!justLost&&!f.Incoming&&f.Threats==0&&!f.ThreatsTruncated;
        double need=1-t.Ratio;
        t.ReloadScore=.15+.7*need*need+(f.Protected?.25:quiet?.12:0)+(1-f.Aggression)*.12
            +(t.Critical?.35:0)-(f.Looking?.35:0)-(f.Visible&&f.Distance<10?.2:0)-(f.Incoming?.2:0)
            -(f.Threats>0||f.ThreatsTruncated?.25:0)-(justLost&&!f.Protected?.15:0);
        t.HoldScore=.2+.4*t.Ratio+(f.Looking?.3:0)+(f.BusyObserved&&f.Visible?.25:0)
            +(f.Visible&&!f.Looking?.2:0)+(f.Visible&&f.Distance<10?.15:0)+(justLost?.15:0)
            -(t.Critical?.25:0)+f.Aggression*.08;
        // W02 compares these scores even for an empty weapon or unavailable reload resource.
        if(!c.Enabled)return Set("Native","reload-disabled");
        if(RetryBlocked(f))return Set("Native","reload-retry-delay");
        if(t.Ratio>=c.ConsiderBelow/100)return Set("Hold","reload-enough-ammo");
        if(f.Reserve==0||f.Resource=="none")return Set("Native","reload-no-improving-resource");
        if(f.Loaded==0)return Set("Reload","reload-empty-weapon");
        string choice=t.ReloadScore>t.HoldScore?"Reload":"Hold";
        // A new threat, depleted gun or changed resource invalidates stability immediately.
        string key=Key(f)+":"+f.Target+":"+f.Visible+":"+f.Incoming+":"+f.Looking+":"+f.Threats+":"+f.Protected+":"+f.ThreatsTruncated+":"+f.BusyObserved+":"+justLost;
        if(old?.State=="choice"&&old.Choice is "Reload" or "Hold"&&key==previousKey&&f.Now-choiceAt<c.Commitment)
            return Set(old.Choice,"reload-stable-choice");
        previousKey=key;choiceAt=f.Now;
        return Set(choice,choice=="Reload"?(t.Critical?"reload-critical-ammo":"reload-safe-opportunity"):(justLost?"reload-expect-repeek":"reload-keep-weapon-ready"));
    }
    public void Request(ReloadTimingFrame f)
    {if(Trace?.Choice!="Reload")return;Trace.Requested=true;Trace.State="requested";Trace.Reason="reload-native-request";requestAt=f.Now;}
    public void Result(bool accepted,ReloadTimingFrame f,ReloadTimingConfig c)
    {if(Trace?.Requested!=true)return;Trace.Accepted=accepted;if(!accepted){End("reload-native-refused");Refuse(f,c);}}
    public void Refuse(ReloadTimingFrame f,ReloadTimingConfig c)
    {
        var key=Key(f);failures=key==refusedKey?Math.Min(5,failures+1):1;
        refusedKey=key;retryAt=f.Now+Math.Min(30,c.RetryDelay*Math.Pow(2,failures-1));
        if(Trace!=null)Trace.RepeatedFailures=failures;
    }
    public void CompleteObservation(ReloadTimingFrame f,ReloadTimingConfig c)
    {
        if(Trace?.Improved==true){failures=0;retryAt=0;refusedKey="";}
        else if(Trace?.Reason is "reload-start-unconfirmed" or "reload-ended-without-ammo-gain" or "reload-observation-limit")Refuse(f,c);
    }
    public void Observe(double now,bool operation,bool nativeFlag,int loaded,bool sameWeapon)
    {
        if(!Active)return;var t=Trace!;t.Elapsed=Math.Max(0,now-requestAt);t.After=loaded;
        if(!DoorPolicy.Finite(now)||now<requestAt||!sameWeapon){End("reload-weapon-or-clock-changed");return;}
        if(operation){t.Started=true;t.State="reloading";t.Reason="reload-animation-observed";}
        if(t.Started&&!operation&&!nativeFlag){t.Finished=true;t.Improved=loaded>t.Loaded;End(t.Improved?"reload-completed":"reload-ended-without-ammo-gain");}
        else if(!t.Started&&t.Elapsed>=3)End("reload-start-unconfirmed");
        else if(t.Elapsed>=60)End("reload-observation-limit"); // Stop observing, never cancel the native operation.
    }
    public void End(string reason){if(Trace==null)return;Trace.State="ended";Trace.Reason=reason;Trace.ValidFor=0;}
    public void Clear(string reason){End(reason);retryAt=choiceAt=0;failures=0;previousKey=refusedKey="";}
}
