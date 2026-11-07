using System;
using System.Collections.Generic;
namespace Comet.Core;

public sealed class CrossfireHandoffTrace
{
    public string PlannedDecision { get; set; }="";
    public string Decision { get; set; }="";
    public string PathType { get; set; }="";
    public string Reason { get; set; }="";
    public int ExpectedPath { get; set; }
    public int CurrentPath { get; set; }
    public double ExpectedStarted { get; set; }
    public double CurrentStarted { get; set; }
    public int QueuedPath { get; set; }
    public int ExpectedQueuedPath { get; set; }
    public double WaitSeconds { get; set; }
    public int ReleasedPaths { get; set; }
    public bool QueueCapturedAtHandoff { get; set; }
}

public sealed class CrossfireConfig
{
    public bool Enabled { get; set; }=true;
    public double Interval { get; set; }=.5;
    public double SupportDistance { get; set; }=35;
    public double EnemyDistance { get; set; }=80;
    public double ContactAge { get; set; }=8;
    public double OutdoorAngle { get; set; }=35;
    public double IndoorAngle { get; set; }=20;
    public double Spacing { get; set; }=4;
    public double MaxPath { get; set; }=35;
    public double MoveTimeout { get; set; }=12;
    public double StallTimeout { get; set; }=3;
    public double HoldTimeout { get; set; }=20;
    public double SupportGrace { get; set; }=2;
    public double Retry { get; set; }=4;
    public double Participation { get; set; }=70;
    public void Validate()
    {
        CometConfig.Range(Interval,.1,2,nameof(Interval));CometConfig.Range(SupportDistance,5,80,nameof(SupportDistance));
        CometConfig.Range(EnemyDistance,10,150,nameof(EnemyDistance));CometConfig.Range(ContactAge,1,20,nameof(ContactAge));
        CometConfig.Range(OutdoorAngle,15,90,nameof(OutdoorAngle));CometConfig.Range(IndoorAngle,5,OutdoorAngle,nameof(IndoorAngle));
        CometConfig.Range(Spacing,2,15,nameof(Spacing));CometConfig.Range(MaxPath,5,60,nameof(MaxPath));
        CometConfig.Range(MoveTimeout,3,30,nameof(MoveTimeout));CometConfig.Range(StallTimeout,1,MoveTimeout,nameof(StallTimeout));
        CometConfig.Range(HoldTimeout,1,40,nameof(HoldTimeout));CometConfig.Range(SupportGrace,0,5,nameof(SupportGrace));
        CometConfig.Range(Retry,1,20,nameof(Retry));CometConfig.Range(Participation,0,100,nameof(Participation));
    }
}
public sealed class CrossfireTrace
{
    public bool GroundPaused { get; set; }
    public int GroundPauses { get; set; }
    public double GroundPauseSeconds { get; set; }
    public CrossfireHandoffTrace? Handoff { get; set; }
    public string EntranceFallback { get; set; }="";
    public SquadStormTrace? Storm {get;set;}
    public SquadTradeTrace? Trade {get;set;}
    public CoverMateTrace? CoverMate {get;set;}
    public ManeuverTrace? Maneuver {get;set;}
    public SecondaryEntranceTrace? Entrance { get; set; }
    public string State { get; set; }="planning";
    public string Reason { get; set; }="crossfire-opportunity";
    public string Execution { get; set; }="finding a reachable angle";
    public string Contact { get; set; }="";
    public string Source { get; set; }="";
    public string Support { get; set; }="";
    public string SupportName { get; set; }="";
    public int Episode { get; set; }
    public bool Applied { get; set; }
    public bool Reserved { get; set; }
    public double ValidFor { get; set; }
    public double ContactAge { get; set; }
    public double Remaining { get; set; }
    public double Angle { get; set; }
    public double PathLength { get; set; }
    public double Roll { get; set; }
    public double Chance { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public double? GoalX { get; set; }
    public double? GoalY { get; set; }
    public double? GoalZ { get; set; }
    public int Candidates { get; set; }
    public string[] Alternatives { get; set; }=Array.Empty<string>();
}
public sealed class Crossfire
{
    public CrossfireTrace? Trace { get; private set; }
    public bool Active=>Trace?.State is "planning" or "ready" or "moving" or "holding";
    public double RetryAt { get; private set; }
    private double started,movingAt,holdAt,progressAt,bestDistance,lastSupport,lastNow=-1;
    private int episode;
    public bool Begin(double now,CrossfireConfig c,double roll)
    {
        if(!c.Enabled||!MedicalChoice.Finite(now)||now<0||Active||now<RetryAt)return false;
        roll=MedicalChoice.Finite(roll)?Math.Max(0,Math.Min(1,roll)):1;
        Trace=new(){Episode=++episode,Chance=c.Participation,Roll=roll*100,Remaining=c.MoveTimeout};started=lastSupport=lastNow=now;
        if(roll*100>=c.Participation)return End(now,c,"crossfire-keep-current-plan");
        return true;
    }
    public bool Check(double now,CrossfireConfig c,string block,bool support,double? holdLimit=null)
    {
        if(!Active)return false;
        if(!c.Enabled||!MedicalChoice.Finite(now)||now<lastNow)return End(MedicalChoice.Finite(now)?now:lastNow,c,"crossfire-disabled-or-clock-reset");
        lastNow=now;
        if(block.Length>0)return End(now,c,block);
        if(support)lastSupport=now;
        if(!support&&now-lastSupport>=c.SupportGrace)return End(now,c,"crossfire-support-lost");
        double remaining=Trace!.State=="holding"?(holdLimit??c.HoldTimeout)-(now-holdAt):c.MoveTimeout-(now-(Trace.State=="moving"?movingAt:started));
        Trace.Remaining=Math.Max(0,remaining);
        if(remaining<=0)return End(now,c,Trace.State=="holding"?"crossfire-hold-timeout":"crossfire-move-timeout");
        return true;
    }
    public void Ready(double x,double y,double z,double angle,double length)
    {
        var t=Trace!;t.State="ready";t.Reason="crossfire-useful-angle";t.Execution="reserved; awaiting layer";t.Reserved=true;
        t.GoalX=x;t.GoalY=y;t.GoalZ=z;t.Angle=angle;t.PathLength=length;
    }
    public void Moving(double now,double distance)
    {var t=Trace!;t.State="moving";t.Execution="native route observed";movingAt=progressAt=now;bestDistance=distance;}
    public bool Progress(double now,double distance,CrossfireConfig c)
    {
        if(Trace?.State!="moving")return Active;
        if(!MedicalChoice.Finite(distance))return End(now,c,"crossfire-invalid-progress");
        if(distance<=.8){Trace.State="holding";Trace.Reason="crossfire-angle-reached";Trace.Execution="watching known position";holdAt=now;return true;}
        if(distance<bestDistance-.4){bestDistance=distance;progressAt=now;}
        return now-progressAt<c.StallTimeout||End(now,c,"crossfire-route-stalled");
    }
    public void Applied(bool value)
    {if(Trace!=null){Trace.Applied=value&&Active&&Trace.State is "moving" or "holding";Trace.ValidFor=Trace.Applied?Math.Min(1,Trace.Remaining):0;}}
    public bool End(double now,CrossfireConfig c,string reason)
    {RetryAt=now+c.Retry;if(Trace!=null){Trace.State="ended";Trace.Reason=reason;Trace.Applied=Trace.Reserved=false;Trace.ValidFor=0;Trace.Execution="native control returned";}return false;}
    public void Clear(){Trace=null;RetryAt=0;lastNow=-1;}
}

// A raid-local board. Several owners may reserve distinct positions against the same enemy.
// Detached IDs and geometry only, no BotComponent/GameWorld references.
public sealed class CrossfireClaims
{
    private sealed class Claim { public string Owner="",Group="";public double X,Y,Z,Until; }
    private readonly Dictionary<string,Claim> entries=new();
    private readonly List<string> expired=new();
    private double clock=-1,nextSweep;
    public int Count=>entries.Count;
    private bool Sweep(double now)
    {
        if(!MedicalChoice.Finite(now)||now<0||now<clock){Clear();return false;}clock=now;
        if(now<nextSweep)return true;nextSweep=now+.25;
        expired.Clear();foreach(var c in entries)if(now>=c.Value.Until)expired.Add(c.Key);
        foreach(var id in expired)entries.Remove(id);expired.Clear();return true;
    }
    public bool Free(string owner,double x,double y,double z,double spacing,double now,string group="")
    {
        if(!Sweep(now)||!MedicalChoice.Finite(x)||!MedicalChoice.Finite(y)||!MedicalChoice.Finite(z)||!MedicalChoice.Finite(spacing)||spacing<=0)return false;
        foreach(var c in entries.Values)if(c.Until>now&&c.Group==group&&c.Owner!=owner&&Math.Abs(y-c.Y)<2.5&&(x-c.X)*(x-c.X)+(z-c.Z)*(z-c.Z)<spacing*spacing)return false;
        return true;
    }
    public bool Reserve(string owner,double x,double y,double z,double spacing,double now,string group="")
    {
        if(!Sweep(now)||string.IsNullOrEmpty(owner))return false;
        if(entries.TryGetValue(owner,out var old)&&old.Until<=now)entries.Remove(owner);
        if(entries.ContainsKey(owner)||entries.Count>=512||!Free(owner,x,y,z,spacing,now,group))return false;
        entries.Add(owner,new(){Owner=owner,Group=group,X=x,Y=y,Z=z,Until=now+2});return true;
    }
    public bool Renew(string owner,double now)
    {if(!Sweep(now)||!entries.TryGetValue(owner,out var c))return false;if(c.Until<=now){entries.Remove(owner);return false;}c.Until=now+2;return true;}
    public void Release(string owner)=>entries.Remove(owner);
    public void Clear(){entries.Clear();expired.Clear();clock=-1;nextSweep=0;}
}

public static class CrossfireGeometry
{
    // Segment corridor, excluding space behind the shooter and beyond the remembered target.
    public static bool InLane(double x,double y,double z,double ax,double ay,double az,double bx,double by,double bz)
    {
        double dx=bx-ax,dz=bz-az,n=dx*dx+dz*dz;
        if(n<.01)return false;
        double t=((x-ax)*dx+(z-az)*dz)/n;
        if(t<=0||t>=1||Math.Abs(y-(ay+(by-ay)*t))>2)return false;
        double px=ax+t*dx,pz=az+t*dz;
        return (x-px)*(x-px)+(z-pz)*(z-pz)<2.25;
    }
}
