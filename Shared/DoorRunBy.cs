using System;
using System.Collections.Generic;

namespace Comet.Core;

public sealed class DoorRunByConfig
{
    public bool Enabled { get; set; } = true;
    public double Preference { get; set; } = 40;
    public bool Fallback { get; set; } = true;
    public double MinimumStamina { get; set; } = 35;
    public double TurnDelay { get; set; } = .35;
    public double Limit { get; set; } = 6;
    public double Watch { get; set; } = 2;
    public void Validate()
    {
        CometConfig.Range(Preference,0,100,nameof(Preference));
        CometConfig.Range(MinimumStamina,10,100,nameof(MinimumStamina));
        CometConfig.Range(TurnDelay,.35,1,nameof(TurnDelay));
        CometConfig.Range(Limit,3,10,nameof(Limit));
        CometConfig.Range(Watch,0,3,nameof(Watch));
    }
}

public sealed class DoorRunTrace
{
    public string State { get; set; }="validating";
    public string Reason { get; set; }="recon-opportunity-selected";
    public string Style { get; set; }="pending";
    public string Door { get; set; }="";
    public string Target { get; set; }="";
    public string Source { get; set; }="";
    public string Personality { get; set; }="";
    public double Chance { get; set; }
    public double Roll { get; set; }
    public double StyleRoll { get; set; }
    public double Preference { get; set; }
    public bool JumpEligible { get; set; }
    public bool RunEligible { get; set; }
    public string JumpReason { get; set; }="not-checked";
    public string RunReason { get; set; }="not-checked";
    public double KnownX { get; set; }
    public double KnownY { get; set; }
    public double KnownZ { get; set; }
    public double ContactAge { get; set; }
    public double StackX { get; set; }
    public double StackY { get; set; }
    public double StackZ { get; set; }
    public double FarX { get; set; }
    public double FarY { get; set; }
    public double FarZ { get; set; }
    public double AngleX { get; set; }
    public double AngleY { get; set; }
    public double AngleZ { get; set; }
    public double ActualX { get; set; }
    public double ActualY { get; set; }
    public double ActualZ { get; set; }
    public double Stamina { get; set; }
    public double ForwardSpeed { get; set; }
    public double DistanceMoved { get; set; }
    public double Elapsed { get; set; }
    public double Remaining { get; set; }
    public double TurnElapsed { get; set; }
    public double WatchElapsed { get; set; }
    public bool DoorOpenRequested { get; set; }
    public bool DoorOpenObserved { get; set; }
    public bool MovementAccepted { get; set; }
    public bool SprintObserved { get; set; }
    public bool FarArrived { get; set; }
    public bool SprintExitObserved { get; set; }
    public bool ReturnAccepted { get; set; }
    public bool Returned { get; set; }
    public bool VisualAcquired { get; set; }
    public double ValidFor { get; set; }=1.5;
    public string[] Alternatives { get; set; }=Array.Empty<string>();
}

// One opportunity shared by D02/D03. Declines also consume evidence and cooldown.
public sealed class DoorReconOpportunity
{
    private readonly Dictionary<string,double> evidence=new();
    private double blockedUntil;
    public DoorRunTrace? TryBegin(string door,string target,string source,string personality,DoorPoint known,double age,
        double now,double roll,double styleRoll,DoorJumpPeekConfig shared,DoorRunByConfig run)
    {
        if((!shared.Enabled&&!run.Enabled)||!known.Finite||!DoorPolicy.Finite(age)||age<0||age>120
            ||!DoorPolicy.Finite(now)||now<blockedUntil||!ValidRoll(roll)||!ValidRoll(styleRoll)||string.IsNullOrEmpty(door)||string.IsNullOrEmpty(target))return null;
        string key=door+"\n"+target;double stamp=now-age;
        if(evidence.TryGetValue(key,out double previous)&&stamp<=previous+.1)return null;
        if(evidence.Count>=32&&!evidence.ContainsKey(key))return null;
        evidence[key]=stamp;blockedUntil=now+shared.Cooldown;
        double chance=shared.Chances.TryGetValue(personality,out var c)?c:0;
        bool selected=roll*100<chance;
        return new(){Door=door,Target=target,Source=source,Personality=personality,Chance=chance,Roll=roll*100,
            StyleRoll=styleRoll*100,Preference=run.Preference,KnownX=known.X,KnownY=known.Y,KnownZ=known.Z,ContactAge=age,
            State=selected?"validating":"ended",Reason=selected?"recon-opportunity-selected":"recon-roll-declined"};
    }
    private static bool ValidRoll(double n)=>DoorPolicy.Finite(n)&&n>=0&&n<1;
    public static string Choose(bool jump,bool run,double roll,DoorJumpPeekConfig j,DoorRunByConfig r)
    {
        if(!ValidRoll(roll))return "none";
        jump&=j.Enabled;run&=r.Enabled;
        if(jump&&run)return roll*100<r.Preference?"run-by":"jump";
        if(jump)return "jump";
        return run&&(!j.Enabled||r.Fallback)?"run-by":"none";
    }
    public void Clear(){evidence.Clear();blockedUntil=0;}
}

// Observations, not move requests, establish success. No native game references.
public sealed class DoorRunBy
{
    public DoorRunTrace? Trace { get; private set; }
    public bool Active=>Trace!=null&&Trace.State!="ended"&&Trace.State!="completed"&&Trace.State!="delegated";
    private double last,phaseAt,started=double.NaN,progressAt;
    private DoorPoint origin,progress;
    public void Begin(DoorRunTrace trace,double now){Trace=trace;last=phaseAt=now;started=double.NaN;}
    private void Phase(string state,string reason,double now){Trace!.State=state;Trace.Reason=reason;phaseAt=now;}
    public void Opening(double now){Trace!.DoorOpenRequested=true;Phase("opening","recon-native-open-requested",now);}
    public void Opened(double now){Trace!.DoorOpenObserved=true;Phase("validating","recon-door-open-observed",now);}
    public void Delegate(double now){Trace!.Style="jump";Phase("delegated","recon-selected-jump",now);}
    public void Start(DoorPoint position,double now){Trace!.Style="run-by";Trace.MovementAccepted=true;origin=progress=position;started=progressAt=now;Phase("across","run-by-movement-accepted",now);}
    public void ReturnStarted(DoorPoint position,double now){Trace!.ReturnAccepted=true;progress=position;progressAt=now;Phase("returning","run-by-walk-return-accepted",now);}
    public bool SameContact(string target,DoorPoint known)=>Trace?.Target==target&&known.Finite&&(known-new DoorPoint(Trace.KnownX,Trace.KnownY,Trace.KnownZ)).Length<=2;
    public bool Observe(DoorPoint position,double speed,double stamina,bool grounded,bool sprinting,bool arrived,bool weaponReady,
        bool visible,double age,double now,DoorRunByConfig c)
    {
        if(!Active)return false;
        if(!position.Finite||!DoorPolicy.Finite(speed)||!DoorPolicy.Finite(stamina)||!DoorPolicy.Finite(now)||now<last||!DoorPolicy.Finite(age)||age<0)
        {End("run-by-invalid-observation",now);return false;}
        last=now;var t=Trace!;t.ActualX=position.X;t.ActualY=position.Y;t.ActualZ=position.Z;t.ForwardSpeed=speed;t.Stamina=stamina;t.ContactAge=age;
        t.VisualAcquired|=visible;if(visible){End("run-by-native-visual-acquired",now);return false;}
        t.Elapsed=DoorPolicy.Finite(started)?now-started:0;t.Remaining=Math.Max(0,c.Limit-t.Elapsed);
        if(t.MovementAccepted&&t.State!="watching"&&t.Elapsed>=c.Limit){End("run-by-time-budget-ended",now);return false;}
        double phaseTime=now-phaseAt;
        if(t.MovementAccepted&&!grounded){End("run-by-ground-contact-lost",now);return false;}
        if(t.State is "across" or "returning")
        {
            if((position-progress).Length>=.2){progress=position;progressAt=now;}
            else if(now-progressAt>1){End("run-by-motion-stalled",now);return false;}
        }
        switch(t.State)
        {
            case "validating": if(phaseTime>1)End("recon-validation-timeout",now);break;
            case "opening": if(phaseTime>4.5)End("recon-door-did-not-open",now);break;
            case "across":
                t.DistanceMoved=(position-origin).Length;
                t.SprintObserved|=sprinting&&speed>=1.2&&t.DistanceMoved>=.2;
                if(!t.SprintObserved&&phaseTime>1)End("run-by-no-sprint-observed",now);
                else if(arrived){if(!t.SprintObserved)End("run-by-arrival-without-sprint",now);else{t.FarArrived=true;Phase("turning","run-by-far-arrival-observed",now);}}
                break;
            case "turning":
                t.TurnElapsed=phaseTime;
                if(!arrived){End("run-by-far-position-lost",now);break;}
                t.SprintExitObserved=!sprinting;
                if(phaseTime>=c.TurnDelay&&!sprinting&&weaponReady)Phase("return-pending","run-by-ready-to-walk-back",now);
                else if(phaseTime>1.5)End("run-by-sprint-exit-or-weapon-not-ready",now);
                break;
            case "return-pending": if(sprinting)End("run-by-sprint-resumed",now);break;
            case "returning":
                if(sprinting){End("run-by-unexpected-return-sprint",now);break;}
                if(arrived&&weaponReady){t.Returned=true;Phase("watching","run-by-angle-arrival-observed",now);}
                break;
            case "watching":
                t.WatchElapsed=phaseTime;
                if(!arrived||sprinting||!weaponReady)End("run-by-watch-position-or-readiness-lost",now);
                else if(phaseTime>=c.Watch)Phase("completed","run-by-watch-finished",now);
                break;
        }
        return Active;
    }
    public void End(string reason,double now){if(Trace!=null){Phase("ended",reason,now);Trace.Remaining=0;}}
}

public static class DoorRunGeometry
{
    public static DoorPoint Far(DoorGeometry g,DoorPoint stack)=>g.Center+g.Normal*(Math.Sign((stack-g.Center).Dot(g.Normal))*1.25)
        +g.Axis*(-Math.Sign((stack-g.Center).Dot(g.Axis))*(g.HalfWidth+1.4));
    public static DoorPoint Angle(DoorGeometry g,DoorPoint stack)=>g.Center+g.Normal*(Math.Sign((stack-g.Center).Dot(g.Normal))*2.4)
        +g.Axis*(Math.Sign((stack-g.Center).Dot(g.Axis))*(g.HalfWidth+.8));
    public static bool ProtectedFar(DoorGeometry g,DoorPoint stack,DoorPoint far)=>far.Finite
        &&(far-g.Center).Dot(g.Axis)*Math.Sign((stack-g.Center).Dot(g.Axis))<=-g.HalfWidth-.8;
    public static string Route(DoorGeometry g,DoorPoint stack,DoorPoint start,DoorPoint raw,DoorPoint sampled,IReadOnlyList<DoorPoint> path,bool complete)
    {
        if(!stack.Finite||!start.Finite||!raw.Finite||!sampled.Finite||(raw-sampled).Length>.25||Math.Abs(sampled.Y-stack.Y)>.25)return "run-by-invalid-floor-or-endpoint";
        if(!complete||path.Count<2||path.Count>16||(path[0]-start).Length>.35||(path[path.Count-1]-sampled).Length>.25)return "run-by-incomplete-route";
        double side=Math.Sign((stack-g.Center).Dot(g.Normal));var d=sampled-start;double squared=d.Dot(d),length=0;
        if(side==0||squared<.25||squared>64)return "run-by-invalid-lane-length";
        foreach(var p in path)
        {
            if(!p.Finite||Math.Abs(p.Y-stack.Y)>.25||(p-g.Center).Dot(g.Normal)*side<.55)return "run-by-route-enters-room-or-floor";
            var projection=start+d*Math.Max(0,Math.Min(1,(p-start).Dot(d)/squared));
            if((p-projection).Length>.3)return "run-by-route-turns-corner";
        }
        for(int i=1;i<path.Count;i++)length+=(path[i]-path[i-1]).Length;
        return length>d.Length+.4?"run-by-route-detour":"";
    }
    public static bool Contains(DoorGeometry g,DoorPoint stack,DoorPoint start,DoorPoint end,DoorPoint position)
    {
        if(!position.Finite||Math.Abs(position.Y-stack.Y)>.35||(position-g.Center).Dot(g.Normal)*Math.Sign((stack-g.Center).Dot(g.Normal))<.5)return false;
        var p=new DoorPoint(position.X,start.Y,position.Z);var d=end-start;
        return (p-(start+d*Math.Max(0,Math.Min(1,(p-start).Dot(d)/Math.Max(.001,d.Dot(d)))))).Length<=.45;
    }
}
