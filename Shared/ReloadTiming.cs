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
    public bool ResourcesChecked;
    public int MagazineCandidates,BeltMagazines,NonImprovingMagazines,RejectedMagazines;
    public string ResourceLocation="",ResourceItem="",ResourceRejection="";
}
public sealed class ReloadTimingTrace
{
    public string State="choice",Choice="Native",Reason="",Weapon="",Target="",Source="",WeaponClass="",Resource="none",Action="";
    public int Episode,Loaded,Capacity,Chamber,Magazine,Reserve,Threats,ReserveForFight,After,RepeatedFailures;
    public double Ratio,ReloadScore,HoldScore,ContactAge,Distance,Elapsed,ValidFor=.5;
    public bool Critical,Protected,Visible,Incoming,BusyObserved,Requested,Accepted,Started,Finished,Improved;
    public string[] Alternatives=Array.Empty<string>();
    public bool ResourcesChecked;
    public int MagazineCandidates,BeltMagazines,NonImprovingMagazines,RejectedMagazines;
    public string ResourceLocation="",ResourceItem="",ResourceRejection="";
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
    public bool RetryBlocked(ReloadTimingFrame f)=>f.Now<retryAt;
    public string Evaluate(ReloadTimingFrame f,ReloadTimingConfig c)
    {
        var t=new ReloadTimingTrace{Episode=++episode,Weapon=f.Weapon,Target=f.Target,Source=f.Source,Loaded=f.Loaded,After=f.Loaded,
            Capacity=f.Capacity,ContactAge=f.ContactAge,Distance=f.Distance,Protected=f.Protected,Visible=f.Visible,Incoming=f.Incoming,BusyObserved=f.BusyObserved,
            ResourceLocation=f.ResourceLocation,ResourceItem=f.ResourceItem,ResourceRejection=f.ResourceRejection,
            MagazineCandidates=f.MagazineCandidates,BeltMagazines=f.BeltMagazines,NonImprovingMagazines=f.NonImprovingMagazines,RejectedMagazines=f.RejectedMagazines};Trace=t;
        if(f.Capacity<=0){t.Reason="reload-no-capacity";return t.Choice="Native";}
        double r=t.Ratio=(double)f.Loaded/f.Capacity,need=1-r;
        bool justDucked=!f.Visible&&!f.Protected&&Math.Min(f.SightAge,f.ContactAge)<c.LostContactDelay&&f.Distance<c.CloseDistance;
        bool keepUp=justDucked&&r>=.3;
        t.ReloadScore=.15+.7*need*need+(f.Protected||!f.Visible&&!justDucked?.25:0)+(f.BusyObserved&&!f.Visible?.15:0)
            -(keepUp?.15:0)-(f.Visible&&!f.Looking?.1:0)-(f.Looking?.35:0)-(f.Distance<10&&f.Visible?.2:0)-(f.Incoming&&f.Visible?.15:0);
        t.HoldScore=.2+.4*r+(f.Looking?.3:0)+(f.BusyObserved&&f.Visible?.25:0)+(f.Visible&&!f.Looking?.2:0)
            +(f.Distance<10&&f.Visible?.15:0)+(keepUp?.15:0)-(r<=.02?.6:r<.1?.25:0);
        if(!c.Enabled||r>=c.ConsiderBelow/100){t.Reason="reload-native-threshold";return t.Choice="Native";}
        t.Reason="reload-source-ranking";return t.Choice=t.ReloadScore>t.HoldScore?"Reload":"Hold";
    }
    public void Request(ReloadTimingFrame f)
    {if(Trace?.Choice!="Reload")return;Trace.Requested=true;Trace.State="requested";Trace.Reason="reload-native-request";requestAt=f.Now;}
    public void Result(bool accepted,ReloadTimingFrame f,ReloadTimingConfig c)
    {if(Trace?.Requested!=true)return;Trace.Accepted=accepted;if(!accepted){End("reload-native-refused");Refuse(f,c);}}
    public void Refuse(ReloadTimingFrame f,ReloadTimingConfig c){retryAt=f.Now+(f.Loaded>0?5:1);}
    public void CompleteObservation(ReloadTimingFrame f,ReloadTimingConfig c){}
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
