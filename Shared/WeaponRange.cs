using System;
using System.Linq;

namespace Comet.Core;

public sealed class WeaponRangeConfig
{
    public bool Enabled { get; set; } = true;
    public double LongDistance { get; set; } = 80;
    public double CloseDistance { get; set; } = 45;
    public double VisualMemory { get; set; } = 2;
    public double StableSeconds { get; set; } = 1;
    public double SinceShot { get; set; } = 1;
    public double LostContactSeconds { get; set; } = 20;
    public void Validate()
    {
        CometConfig.Range(LongDistance,30,250,nameof(LongDistance));
        CometConfig.Range(CloseDistance,5,100,nameof(CloseDistance));
        CometConfig.Range(VisualMemory,.5,5,nameof(VisualMemory));
        CometConfig.Range(StableSeconds,.5,5,nameof(StableSeconds));
        CometConfig.Range(SinceShot,.5,5,nameof(SinceShot));
        CometConfig.Range(LostContactSeconds,5,60,nameof(LostContactSeconds));
        if(CloseDistance>=LongDistance)throw new ArgumentException("Close distance must be below long distance.");
        if(VisualMemory>LostContactSeconds)throw new ArgumentException("Visual memory must not exceed lost contact time.");
    }
}
public sealed class WeaponRangeGun
{
    public string Id="",Slot="",Name="",Class="",Optic="unknown",Fingerprint="";
    public string OpticEvidence="";
    public int Loaded,Reserve;
    public bool Known,Usable,Automatic,Bolt;
    public double CloseScore,LongScore;
}
public sealed class WeaponRangeContact
{
    public string Id="",Source="personal-vision";
    public double Age,Distance;
}
public sealed class WeaponRangeFrame
{
    public double Now,SinceShot;
    public string Current="",Block="",Main="",Goal="";
    public bool Ready,Busy,Reloading,Incoming,Protected,Truncated;
    public WeaponRangeGun[] Weapons=Array.Empty<WeaponRangeGun>();
    public WeaponRangeContact[] Contacts=Array.Empty<WeaponRangeContact>();
}
public sealed class WeaponRangeTrace
{
    public string State="choice",Choice="Keep",Reason="",SelectionReason="",Mode="Keep",Action="",From="",To="",Target="",Source="personal-memory",Arbitration="";
    public int Episode;
    public double Distance,ContactAge=999,SinceShot,StableFor,Elapsed,ValidFor=.5;
    public bool Incoming,Protected,Requested,Accepted,InHands,Ready;
    public WeaponRangeGun[] Weapons=Array.Empty<WeaponRangeGun>();
    public string[] Alternatives=Array.Empty<string>();
}

// A conservative comparison, not a ballistic or accuracy simulation. Only own equipment is scored.
public static class WeaponRangeModel
{
    public static void Classify(WeaponRangeGun g){g.Known=true;g.LongScore=g.Class is "sniperRifle" or "marksmanRifle"?100:0;g.CloseScore=100-g.LongScore;}
}
public sealed class WeaponRangePolicy
{
    public WeaponRangeTrace? Trace { get; private set; }
    public bool Pending=>Trace?.State is "requested" or "drawing";
    public bool ProtectCurrent { get; private set; }
    private string longChoice="",candidateMode="";
    private double nextLong;
    private int episode;
    public bool Holds(string id)=>longChoice==id&&id.Length>0;
    public string Evaluate(WeaponRangeFrame f,WeaponRangeConfig c)
    {
        Trace=new(){Episode=++episode,From=f.Current,Weapons=f.Weapons};ProtectCurrent=false;
        string No(string why){Trace.Reason=why;return "";}
        if(!c.Enabled)return No("range-disabled");
        if(!f.Ready||f.Reloading)return No("range-native-not-ready");
        var first=f.Weapons.FirstOrDefault(g=>g.Slot=="FirstPrimaryWeapon");
        var second=f.Weapons.FirstOrDefault(g=>g.Slot=="SecondPrimaryWeapon");
        var main=f.Weapons.FirstOrDefault(g=>g.Id==f.Main);
        if(main==null)return No("range-no-main-weapon");
        var goal=f.Contacts.FirstOrDefault(g=>g.Id==f.Goal);
        if(goal!=null){Trace.Target=goal.Id;Trace.Distance=goal.Distance;Trace.ContactAge=goal.Age;}
        string Pick(string id,string mode,string reason){Trace.To=id;Trace.Mode=mode;candidateMode=mode;Trace.Choice="Switch";Trace.Reason=Trace.SelectionReason=reason;return id;}
        if(Holds(f.Current)&&second!=null&&f.Current==second.Id){
            bool close=goal!=null&&goal.Distance<c.CloseDistance;
            bool gone=goal==null||goal.Age>c.LostContactSeconds;
            if(gone&&!close&&f.Contacts.Any(g=>g.Age<=c.LostContactSeconds&&g.Distance>=c.CloseDistance))gone=false;
            if(close||gone||second.Loaded+second.Reserve<=0)return Pick(main.Id,"Close",close?"range-close-threat":gone?"range-contacts-lost":"range-out-of-ammo");
            ProtectCurrent=true;return No("range-retain-long-choice");
        }
        if(f.Current!=main.Id||f.Now<nextLong||goal==null)return No("range-no-long-opportunity");
        nextLong=f.Now+1;
        if(goal.Age>=c.VisualMemory||goal.Distance<c.LongDistance)return No("range-no-long-opportunity");
        if(second==null||first==null||second.LongScore==0||first.LongScore>0||second.Loaded+second.Reserve<=0)return No("range-no-long-weapon");
        return Pick(second.Id,"Long","range-better-long-weapon");
    }
    public void Arbitration(WeaponReturnTrace? w,double now)
    {
        if(Trace==null||w==null||w.To!=Trace.To)return;
        Trace.Requested=w.Requested;Trace.Accepted=w.Accepted;Trace.InHands=w.InHands;Trace.Ready=w.Ready;Trace.State=w.State;Trace.Reason=w.Reason;
        if(w.Accepted){longChoice=candidateMode=="Long"?w.To:"";ProtectCurrent=longChoice.Length>0;}
    }
    public void Decline(string reason){if(Trace!=null){Trace.State="ended";Trace.Reason=reason;}}
    public void Defer(string reason)=>Decline(reason);
    public void Clear(string reason){longChoice="";ProtectCurrent=false;nextLong=0;Decline(reason);}
}
