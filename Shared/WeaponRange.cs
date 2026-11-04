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
    public string Current="",Block="";
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
    public static void Classify(WeaponRangeGun g)
    {
        bool rifle=g.Class is "assaultRifle" or "assaultCarbine" or "machinegun",dmr=g.Class=="marksmanRifle",bolt=g.Bolt||g.Class=="sniperRifle";
        g.Known=g.Optic!="unknown"&&(rifle||dmr||bolt||g.Class is "smg" or "shotgun");
        if(!g.Known)return;
        bool scope=g.Optic is "scope" or "scope-and-dot",dot=g.Optic is "dot" or "scope-and-dot";
        g.LongScore=bolt?55:dmr?50:rifle?40:10;
        if(scope)g.LongScore+=35;
        g.CloseScore=bolt?15:dmr?40:g.Class=="shotgun"?70:rifle?60:75;
        if(g.Automatic)g.CloseScore+=15;
        if(dot)g.CloseScore+=5;
        if(scope&&!dot)g.CloseScore-=10;
        // Ammunition only breaks reasonably close comparisons; it cannot turn a shotgun into a sniper rifle.
        double supply=(g.Loaded>=3?3:0)+(g.Reserve>0?2:0);
        g.LongScore+=supply;g.CloseScore+=supply;
    }
}
public sealed class WeaponRangePolicy
{
    public WeaponRangeTrace? Trace { get; private set; }
    public bool Pending=>Trace?.State is "requested" or "drawing";
    public bool ProtectCurrent { get; private set; }
    private string longChoice="",candidateKey="",candidateMode="",observedCurrent="";
    private double stableAt,lastEvaluation=-1,requestedAt;
    private int episode;
    private static bool Valid(double n)=>DoorPolicy.Finite(n)&&n>=0;
    public string Evaluate(WeaponRangeFrame f,WeaponRangeConfig c)
    {
        if(Pending)return "";
        ProtectCurrent=false;
        if(f.Now<lastEvaluation)Clear("range-clock-reversed");
        // A missed sample cannot prove a continuous stable opportunity.
        if(lastEvaluation>=0&&f.Now-lastEvaluation>.75)candidateKey="";
        lastEvaluation=f.Now;
        if(observedCurrent.Length>0&&observedCurrent!=f.Current&&f.Current!=longChoice)longChoice="";
        observedCurrent=f.Current;
        var t=new WeaponRangeTrace{Episode=++episode,From=f.Current,SinceShot=Valid(f.SinceShot)?f.SinceShot:0,Incoming=f.Incoming,Protected=f.Protected,Weapons=f.Weapons,
            Alternatives=new[]{"Keep: a small or uncertain benefit does not justify a switch","Long: recent personal sighting, suitable loaded weapon and no close known threat","Close: close known threat, or all relevant contacts lost for the configured time"}};
        Trace=t;
        string No(string reason,bool reset=true){t.Reason=reason;if(reset)candidateKey="";return "";}
        if(!c.Enabled){longChoice="";return No("range-disabled");}
        if(!Valid(f.Now)||!Valid(f.SinceShot)||f.Weapons.Length!=2||f.Weapons.Any(g=>g.Id.Length==0||!WeaponReturnPolicy.IsPrimary(g.Slot)||g.Loaded<0||g.Reserve<0||!Valid(g.CloseScore)||!Valid(g.LongScore))
            ||f.Weapons[0].Id==f.Weapons[1].Id||f.Contacts.Any(x=>x.Id.Length==0||!Valid(x.Age)||!Valid(x.Distance)||x.Source is not ("personal-vision" or "personal-hearing")))return No("range-invalid-context");
        var current=f.Weapons.FirstOrDefault(x=>x.Id==f.Current);
        if(current==null||!current.Usable||current.Loaded==0){longChoice="";return No("range-emergency-selection-priority");}
        if(f.Weapons.Any(x=>!x.Known))return No("range-unknown-equipment");
        if(!f.Weapons.Any(x=>x.Id==longChoice&&x.Usable&&x.Loaded>0))longChoice="";
        if(f.Truncated||f.Contacts.Length>16)return No("range-contact-budget");
        var near=f.Contacts.Where(x=>x.Age<=c.LostContactSeconds&&x.Distance<c.CloseDistance).OrderBy(x=>x.Distance).FirstOrDefault();
        var seen=f.Contacts.Where(x=>x.Source=="personal-vision"&&x.Age<=c.VisualMemory&&x.Distance>=c.LongDistance).OrderBy(x=>x.Age).FirstOrDefault();
        var retained=f.Contacts.Where(x=>x.Age<=c.LostContactSeconds&&x.Distance>=c.CloseDistance).OrderBy(x=>x.Age).FirstOrDefault();
        var contact=near??seen??retained;
        if(contact!=null){t.Target=contact.Id;t.Source=contact.Source;t.ContactAge=contact.Age;t.Distance=contact.Distance;}
        bool close=near!=null,far=!close&&seen!=null;
        bool lost=longChoice==f.Current&&retained==null;
        t.Mode=close||lost?"Close":far?"Long":"Keep";
        if(t.Mode=="Keep")
        {ProtectCurrent=longChoice==f.Current&&retained!=null;return No(ProtectCurrent?"range-retain-long-choice":"range-hysteresis-keep");}
        var best=f.Weapons.Where(g=>g.Usable&&g.Loaded>0).OrderByDescending(g=>t.Mode=="Long"?g.LongScore:g.CloseScore).First();
        double gain=t.Mode=="Long"?best.LongScore-current.LongScore:best.CloseScore-current.CloseScore;
        t.To=best.Id;
        if(best.Id==f.Current||gain<10)
        {
            if(far){longChoice=f.Current;ProtectCurrent=true;}
            if(close||lost)longChoice="";
            return No(best.Id==f.Current?"range-held-weapon-suitable":"range-benefit-too-small");
        }
        if(f.Block.Length>0)return No(f.Block);
        if(!f.Ready||f.Busy||f.Reloading)return No("range-native-action-priority");
        if(f.Incoming&&!f.Protected)return No("range-under-fire-without-cover");
        if(f.SinceShot<c.SinceShot)return No("range-recent-shot");
        string key=t.Mode+":"+t.Target+":"+best.Id+":"+string.Join("|",f.Weapons.Select(g=>g.Id+"/"+g.Fingerprint));
        if(key!=candidateKey){candidateKey=key;stableAt=f.Now;}
        t.StableFor=Math.Max(0,f.Now-stableAt);
        if(t.StableFor<c.StableSeconds)return No("range-wait-stable-opportunity",false);
        t.Choice="Switch";t.Reason=t.SelectionReason=close?"range-close-threat":lost?"range-contacts-lost":"range-better-long-weapon";
        candidateMode=t.Mode;return best.Id;
    }
    public void Arbitration(WeaponReturnTrace? w,double now)
    {
        if(Trace==null||Trace.Choice!="Switch"||w==null||w.To!=Trace.To)return;
        Trace.Arbitration=w.Reason;
        if(!w.Requested){Trace.Choice="Keep";Trace.Reason=w.Reason;return;}
        if(!Trace.Requested)requestedAt=now;
        Trace.Requested=true;Trace.Accepted=w.Accepted;Trace.InHands=w.InHands;Trace.Ready=w.Ready;
        Trace.Elapsed=Math.Max(0,now-requestedAt);Trace.State=w.State;Trace.Reason=w.Reason;Trace.ValidFor=w.State=="ended"?0:.5;
        if(w.Ready){longChoice=candidateMode=="Long"?w.To:"";observedCurrent=w.To;candidateKey="";}
        if(w.State=="ended")candidateKey="";
    }
    public void Decline(string reason)
    {if(Trace==null||Pending)return;Trace.Choice="Keep";Trace.Reason=reason;Trace.Arbitration=reason;candidateKey="";}
    public void Defer(string reason)
    {
        // Missing sampling budget is not evidence that the last long-range choice became invalid.
        // Expire visible context and stability, but reassess the retained preference on the next sample.
        if(Pending)return;
        candidateKey="";ProtectCurrent=false;
        if(Trace!=null){Trace.State="ended";Trace.Reason=reason;Trace.ValidFor=0;}
    }
    public void Clear(string reason)
    {longChoice=candidateKey=observedCurrent="";ProtectCurrent=false;lastEvaluation=-1;if(Trace!=null){Trace.State="ended";Trace.Reason=reason;Trace.ValidFor=0;}}
}
