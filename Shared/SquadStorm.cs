using System;
using System.Collections.Generic;
using System.Linq;
namespace Comet.Core;

public sealed class SquadStormConfig
{
    public bool Enabled {get;set;}=true;
    public double Weakness {get;set;}=.5;
    public double SquadDistance {get;set;}=35;
    public double MaxPath {get;set;}=40;
    public double ContactAge {get;set;}=12;
    public int MinimumAttackers {get;set;}=2;
    public double PrepareTimeout {get;set;}=4;
    public double Duration {get;set;}=10;
    public double MinimumAmmo {get;set;}=40;
    public double Retry {get;set;}=20;
    public double GigaChad {get;set;}=100;
    public double Chad {get;set;}=100;
    public double Wreckless {get;set;}=100;
    public double Normal {get;set;}=80;
    public double Timmy {get;set;}=60;
    public double Rat {get;set;}=35;
    public double SnappingTurtle {get;set;}=35;
    public double Coward {get;set;}=10;
    public double Chance(string p)=>p switch {"GigaChad"=>GigaChad,"Chad"=>Chad,"Wreckless"=>Wreckless,"Normal"=>Normal,"Timmy"=>Timmy,"Rat"=>Rat,"SnappingTurtle"=>SnappingTurtle,"Coward"=>Coward,_=>0};
    public void Validate()
    {
        CometConfig.Range(Weakness,.1,1,nameof(Weakness));CometConfig.Range(SquadDistance,5,80,nameof(SquadDistance));
        CometConfig.Range(MaxPath,3,60,nameof(MaxPath));CometConfig.Range(ContactAge,1,15,nameof(ContactAge));
        CometConfig.Range(MinimumAttackers,2,16,nameof(MinimumAttackers));CometConfig.Range(PrepareTimeout,.25,6,nameof(PrepareTimeout));
        CometConfig.Range(Duration,2,20,nameof(Duration));CometConfig.Range(MinimumAmmo,1,100,nameof(MinimumAmmo));CometConfig.Range(Retry,5,120,nameof(Retry));
        foreach(var p in new[]{"GigaChad","Chad","Wreckless","Normal","Timmy","Rat","SnappingTurtle","Coward"})CometConfig.Range(Chance(p),0,100,p);
    }
}
// Values are observed external categories, never live health, armor class or a hidden transform.
public sealed class StormEvidence
{
    private bool numbersApplied;
    public void ApplyNumbers(int count,double threshold){if(!numbersApplied&&count>=3&&Score>0){numbersApplied=true;Score=Math.Min(1,Score+.15);Details=Details.Concat(new[]{"three or more eligible squad members"}).ToArray();Reason=Danger?"perceived-danger-or-opening-closed":Score>=threshold?"perceived-weakness":"insufficient-observed-weakness";}}
    public string Source="",Reason="",Cue="";
    public double At,Score,Until=double.MaxValue;
    public bool Danger;
    public string[] Details=Array.Empty<string>();
    public static StormEvidence Assess(double now,string source,List<(string kind,string value,double at)> observations,
        bool apparentlyAlone,string cue,double cueAt,double cueRemaining,bool interrupted,SquadStormConfig c)
    {
        var e=new StormEvidence{Source=source,At=-1};var details=new List<string>();
        foreach(var group in observations.GroupBy(x=>x.kind))
        {
            var o=group.OrderByDescending(x=>x.at).First();
            if(!MedicalChoice.Finite(o.at)||o.at>now||now-o.at>c.ContactAge)continue;
            double weight=(o.kind,o.value) switch {("body","none-visible")=>.3,("head","none-visible")=>.15,("weapon","pistol")=>.3,("weapon","smg")=>.05,("injury","possibly-injured")=>.05,_=>0};
            if(weight<=0)continue;
            e.Score+=weight;e.At=Math.Max(e.At,o.at);e.Until=Math.Min(e.Until,o.at+c.ContactAge);details.Add(o.kind+": "+o.value);
        }
        e.Danger=interrupted;
        if(!interrupted&&MedicalChoice.Finite(cueAt)&&cueAt<=now&&MedicalChoice.Finite(cueRemaining)&&cueRemaining>0
            &&cue is "reload" or "heal" or "surgery" or "looting" or "dry-fire")
        {e.Score+=.4;e.Cue=cue;e.At=Math.Max(e.At,cueAt);e.Until=Math.Min(e.Until,now+cueRemaining);details.Add("personally heard "+cue);}
        if(e.Score>0&&apparentlyAlone){e.Score+=.1;details.Add("no other fresh known threat nearby");}
        e.Score=Math.Min(1,e.Score);e.Details=details.ToArray();e.Reason=interrupted?"perceived-danger-or-opening-closed":e.Score>=c.Weakness?"perceived-weakness":"insufficient-observed-weakness";
        return e;
    }
}
public sealed class StormMember
{
    public string Id="",Name="",Status="planning",Reason="",Personality="";
    public double Roll,Chance,Ammo,LastCallback=-1,Eta,ReadyAt=-1;
    public bool Ready,Confirmed;
}
public sealed class SquadStormTrace
{
    public string Plan="",State="preparing",Status="planning",Reason="",Observer="",Cue="";
    public double Score,Threshold,Roll,Chance,Ammo,Remaining,EvidenceAge,Departure=-1,Eta;
    public int Ready,Required;
    public bool Confirmed;
    public string[] Evidence=Array.Empty<string>(),Members=Array.Empty<string>();
}
public sealed class SquadStormPlan
{
    public string Id="",Group="",Enemy="",Observer="",State="preparing",Reason="waiting-for-ready-attackers";
    public double Started,Departure=-1,Until,EvidenceAt,EndedAt=-1;
    public StormEvidence Evidence=new();
    public readonly List<StormMember> Members=new();
    public bool Active=>State!="ended";
    public StormMember? Member(string id)=>Members.Find(x=>x.Id==id);
    public void Drop(string id,string why)
    {var m=Member(id);if(m==null)return;m.Status="ended";m.Reason=why;m.Ready=m.Confirmed=false;if(!Members.Any(x=>x.Status!="ended"&&x.Status!="declined"))End("storm-no-attacker");}
    public void End(string why)
    {State="ended";Reason=why;foreach(var m in Members){if(m.Status!="declined"){m.Status="ended";m.Reason=why;}m.Ready=m.Confirmed=false;}}
    public bool Tick(double now,SquadStormConfig c)
    {
        if(!Active)return false;
        if(!MedicalChoice.Finite(now)||now<Started||!c.Enabled){End("storm-disabled-or-clock-reset");return false;}
        if(now>=Until){End("storm-opening-ended");return false;}
        if(State=="preparing")
        {
            // zzap shares the opportunity, then each volunteer acts independently.
            // It does not require simultaneous route/callback confirmations.
            if(Members.Any(m=>m.Ready&&m.Status is not ("ended" or "declined")))
            {
                State="active";Departure=now;Until=now+c.Duration;Reason="storm-volunteer-departure";

            }
        }
        return Active;
    }
    public SquadStormTrace Trace(string id,double now,SquadStormConfig c)
    {
        var m=Member(id)!;return new(){Plan=Id,State=State,Status=m.Status,Reason=m.Reason.Length>0?m.Reason:Reason,Observer=Observer,Cue=Evidence.Cue,
            Score=Evidence.Score,Threshold=c.Weakness,Roll=m.Roll*100,Chance=m.Chance,Ammo=m.Ammo,Remaining=Math.Max(0,Math.Min(Until,Evidence.Until)-now),
            EvidenceAge=Math.Max(0,now-Evidence.At),Departure=Departure,Eta=m.Eta,Ready=Members.Count(x=>x.Ready),Required=c.MinimumAttackers,Confirmed=m.Confirmed,
            Evidence=Evidence.Details,Members=Members.Select(x=>FormattableString.Invariant($"{x.Name}: {x.Status} ({x.Roll*100:0.#}/{x.Chance:0.#}%) {x.Reason}")).ToArray()};
    }
}
public sealed class SquadStormBoard
{
    private readonly Dictionary<string,SquadStormPlan> plans=new();
    private readonly Dictionary<string,(double until,double evidence)> history=new();
    private double clock=-1;private int serial;
    public SquadStormPlan? LastAttempt {get;private set;}
    public SquadStormPlan? Get(string group)=>plans.TryGetValue(group,out var p)?p:null;
    public SquadStormPlan? Start(string group,string enemy,double now,StormEvidence e,List<StormMember> members,SquadStormConfig c)
    {
        if(!MedicalChoice.Finite(now)||now<0||!MedicalChoice.Finite(e.At)||e.At<0||e.At>now||!MedicalChoice.Finite(e.Until)||e.Until<=now||e.Danger||!MedicalChoice.Finite(e.Score)||e.Score<c.Weakness||e.Score>1||!c.Enabled)return null;
        if(now<clock)Clear();clock=now;
        if(plans.TryGetValue(group,out var old)&&old.Active)return old.Enemy==enemy?old:null;
        if(old!=null){Finish(old,now,c,old.Reason);plans.Remove(group);}
        string key=group+"/"+enemy;
        if(history.TryGetValue(key,out var h)&&now<h.until)return null;
        if(members.Count<2||members.Select(x=>x.Id).Distinct().Count()!=members.Count)return null;
        var p=new SquadStormPlan{Id="storm-"+(++serial),Group=group,Enemy=enemy,Observer=e.Source,Started=now,Until=now+c.PrepareTimeout+c.Duration,EvidenceAt=e.At,Evidence=e};
        foreach(var m in members)
        {
            m.Chance=c.Chance(m.Personality);
            if(!MedicalChoice.Finite(m.Roll)||m.Roll<0||m.Roll>=1||m.Roll*100>=m.Chance||!MedicalChoice.Finite(m.Ammo)||m.Ammo<c.MinimumAmmo)
            {m.Status="declined";m.Reason=m.Ammo<c.MinimumAmmo?"low-loaded-magazine":"personality-choice";}
            p.Members.Add(m);
        }
        history[key]=(now+c.Retry,e.At);LastAttempt=p;
        if(!p.Members.Any(x=>x.Status!="declined")){p.End("storm-no-volunteer");return null;}
        plans[group]=p;return p;
    }
    public void Finish(SquadStormPlan p,double now,SquadStormConfig c,string why)
    {if(p.EndedAt>=0)return;if(p.Active)p.End(why);p.EndedAt=now;history[p.Group+"/"+p.Enemy]=(now+c.Retry,p.EvidenceAt);}
    public void Clear(){plans.Clear();history.Clear();clock=-1;serial=0;LastAttempt=null;}
}
