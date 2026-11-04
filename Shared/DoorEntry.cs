using System;
using System.Collections.Generic;

namespace Comet.Core;

public sealed class DoorEntryConfig
{
    public bool Enabled { get; set; }=true;
    public double MinimumStamina { get; set; }=35;
    public double CrossingLimit { get; set; }=3.5;
    public double DoorRetry { get; set; }=30;
    public double PreparedChance { get; set; }=40;
    public double RushChance { get; set; }=50;
    public double RetreatChance { get; set; }=60;
    public double SearchChance { get; set; }=15;
    public double MoveChance { get; set; }=10;
    public double Chance(string context)=>context switch {"prepared"=>PreparedChance,"rush"=>RushChance,"retreat"=>RetreatChance,"search"=>SearchChance,"move"=>MoveChance,_=>0};
    public void Validate()
    {
        CometConfig.Range(MinimumStamina,20,80,nameof(MinimumStamina));
        CometConfig.Range(CrossingLimit,1,5,nameof(CrossingLimit));
        CometConfig.Range(DoorRetry,10,120,nameof(DoorRetry));
        foreach(var v in new[]{PreparedChance,RushChance,RetreatChance,SearchChance,MoveChance})CometConfig.Range(v,0,100,"Entry chance");
    }
}
public sealed class DoorEntryTrace
{
    public string State {get;set;}="evaluating";
    public string Reason {get;set;}="entry-evaluating";
    public string Context {get;set;}="";
    public string Door {get;set;}="";
    public string Target {get;set;}="";
    public string Source {get;set;}="";
    public string Interaction {get;set;}="native-kick";
    public string Network {get;set;}="solo";
    public double Chance {get;set;}
    public double Roll {get;set;}
    public bool Rolled {get;set;}
    public double Stamina {get;set;}
    public double Remaining {get;set;}
    public double KnownX {get;set;}
    public double KnownY {get;set;}
    public double KnownZ {get;set;}
    public double ContactAge {get;set;}
    public bool Requested {get;set;}
    public bool BreachObserved {get;set;}
    public bool DoorOpenObserved {get;set;}
    public bool AnimationFinished {get;set;}
    public bool Crossed {get;set;}
    public bool Arrived {get;set;}
    public double ValidFor {get;set;}=1.5;
    public string[] Alternatives {get;set;}=new[]{"normal door opening if still eligible","native combat priority"};
}

// Observations, never elapsed time, establish success. History is per bot and raid.
public sealed class DoorEntry
{
    private readonly Dictionary<string,double> history=new();
    public DoorEntryTrace? Trace {get;private set;}
    public bool Active=>Trace?.State is "approaching" or "interacting" or "crossing";
    public double Deadline {get;private set;}
    private double phase,last,crossingDeadline;
    public bool Available(string door,double now)=>!history.TryGetValue(door,out var t)||now>=t;
    public bool Refuse(string door,string target,string context,double now,string reason,DoorEntryConfig c)
    {
        if(Active||!c.Enabled||string.IsNullOrEmpty(door)||string.IsNullOrEmpty(target)||!Valid(now)||!Available(door,now))return false;
        foreach(var key in new List<string>(history.Keys))if(history[key]<=now)history.Remove(key);
        if(history.Count>=32)return false;
        history[door]=now+c.DoorRetry;
        Trace=new(){Door=door,Target=target,Context=context,Chance=c.Chance(context),Reason=reason,State="ended",ValidFor=0};
        return true;
    }
    public bool Begin(string door,string target,string context,double now,double originalDeadline,double roll,DoorEntryConfig c)
    {
        if(Active||!c.Enabled||string.IsNullOrEmpty(door)||string.IsNullOrEmpty(target)||!Valid(now)||!Valid(originalDeadline)
            ||originalDeadline<=now||!Valid(roll)||roll>=1||!Available(door,now))return false;
        foreach(var key in new List<string>(history.Keys))if(history[key]<=now)history.Remove(key);
        if(history.Count>=32)return false;
        history[door]=now+c.DoorRetry;last=phase=now;Deadline=Math.Min(originalDeadline,now+3+8+c.CrossingLimit);
        Trace=new(){Door=door,Target=target,Context=context,Chance=c.Chance(context),Roll=roll*100,Rolled=true,Remaining=Deadline-now};
        if(Trace.Roll>=Trace.Chance){End("entry-roll-declined");return false;}
        Trace.State="approaching";Trace.Reason="entry-selected";return true;
    }
    // Native execution owns the kick animation. The 8 s guard below never shortens or accelerates it.
    public bool Request(double now,double stamina,bool shut,DoorEntryConfig c)
    {
        if(!Tick(now)||Trace!.State!="approaching"||Trace.Requested)return false;
        if(!Valid(stamina)){End("entry-invalid-stamina");return false;}
        Trace.Stamina=stamina;
        if(!shut){End("entry-door-opened-by-other");return false;}
        if(stamina<c.MinimumStamina){End("entry-low-stamina");return false;}
        Trace.Requested=true;Trace.State="interacting";Trace.Reason="entry-native-kick-requested";phase=now;return true;
    }
    public void Breached(bool observed,double now)
    {
        if(!Tick(now)||Trace!.State!="interacting"||!Trace.Requested||!observed)return;
        Trace.BreachObserved=true;Trace.Reason="entry-native-breach-observed";
    }
    public void Opened(bool animationFinished,double now,DoorEntryConfig c)
    {
        if(!Tick(now))return;
        Trace!.DoorOpenObserved=true;
        if(!Trace.BreachObserved){End("entry-door-opened-by-other");return;}
        Trace.AnimationFinished|=animationFinished;
        if(Trace.State=="interacting"&&animationFinished){Trace.State="crossing";Trace.Reason="entry-crossing";phase=now;crossingDeadline=Math.Min(Deadline,now+c.CrossingLimit);}
    }
    public void Movement(bool crossed,bool arrived,double now)
    {
        if(!Tick(now)||Trace!.State!="crossing")return;
        Trace.Crossed|=crossed;Trace.Arrived=Trace.Crossed&&arrived;
        if(Trace.Crossed&&arrived){Trace.State="completed";Trace.Reason="entry-crossing-completed";Trace.ValidFor=0;}
    }
    public bool Tick(double now)
    {
        if(!Active)return false;
        if(!Valid(now)||now<last){End("entry-invalid-clock");return false;}
        last=now;Trace!.Remaining=Math.Max(0,Deadline-now);
        if(now>=Deadline){End("entry-original-budget-ended");return false;}
        if(Trace.State=="approaching"&&now-phase>=3){End("entry-approach-timeout");return false;}
        if(Trace.State=="crossing"&&now>=crossingDeadline){End("entry-crossing-timeout");return false;}
        if(Trace.State=="interacting"&&now-phase>=8){End("entry-native-interaction-timeout");return false;}
        return true;
    }
    public void End(string reason){if(Trace==null)return;Trace.State="ended";Trace.Reason=reason;Trace.ValidFor=0;}
    public static double Budget(double now,double contactRemaining,double? parentDeadline,DoorEntryConfig c)
        =>Math.Min(now+Math.Max(0,contactRemaining),Math.Min(parentDeadline??double.PositiveInfinity,now+3+8+c.CrossingLimit));
    public static bool Valid(double v)=>!double.IsNaN(v)&&!double.IsInfinity(v)&&v>=0;
    public static bool Lane(DoorGeometry g,DoorPoint approach,DoorPoint exit,DoorPoint from)
    {
        if(!g.Center.Finite||!g.Normal.Finite||!g.Axis.Finite||!approach.Finite||!exit.Finite||!from.Finite||g.HalfWidth<.3)return false;
        double side=(from-g.Center).Dot(g.Normal),a=(approach-g.Center).Dot(g.Normal),b=(exit-g.Center).Dot(g.Normal);
        return Math.Abs(side)>.15&&side*a>0&&a*b<0&&Math.Abs(a)>=.35&&Math.Abs(a)<=2&&Math.Abs(b)>=1.5&&Math.Abs(b)<=3.5
            &&Math.Abs((approach-g.Center).Dot(g.Axis))<Math.Max(.1,g.HalfWidth-.25)
            &&Math.Abs((exit-g.Center).Dot(g.Axis))<Math.Max(.1,g.HalfWidth-.25)
            &&Math.Abs(approach.Y-exit.Y)<=.6;
    }
}
