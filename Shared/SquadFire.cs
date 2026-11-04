using System;
namespace Comet.Core;

public sealed class SquadFireConfig
{
    public bool Enabled {get;set;}=true;
    public double Interval {get;set;}=.05;
    public double Prediction {get;set;}=.3;
    public double PredictionRadius {get;set;}=.55;
    public double BodyRadius {get;set;}=.2;
    public double ClearDelay {get;set;}=.1;
    public void Validate(){CometConfig.Range(Interval,.02,.1,nameof(Interval));CometConfig.Range(Prediction,.1,.5,nameof(Prediction));
        CometConfig.Range(PredictionRadius,.2,.8,nameof(PredictionRadius));CometConfig.Range(BodyRadius,.05,.35,nameof(BodyRadius));CometConfig.Range(ClearDelay,.05,.3,nameof(ClearDelay));}
}
public sealed class SquadFireFrame
{
    public double Now,Distance,Speed;public bool Fresh,Risk;public string Ally="",AllyName="",Kind="",Action="",Geometry="";
    public DoorPoint Muzzle,End,Position,Predicted;public string[] Alternatives=Array.Empty<string>();
}
public sealed class SquadFireTrace
{
    public string State="native",Reason="",Ally="",AllyName="",Kind="",Action="",Geometry="",Execution="none";
    public bool Applied,StopRequested,Stopped,StartPrevented;
    public double ValidFor,BlockedFor,ClearFor,Prediction,Radius,Distance,Speed,MuzzleX,MuzzleY,MuzzleZ,EndX,EndY,EndZ,AllyX,AllyY,AllyZ,PredictedX,PredictedY,PredictedZ;
    public string[] Alternatives=Array.Empty<string>();
}
public static class SquadFireGeometry
{
    // A swept body point can cross the barrel even if neither its current nor predicted endpoint touches it.
    public static bool Crosses(DoorPoint muzzle,DoorPoint end,DoorPoint current,DoorPoint predicted,double radius)
    {
        if(!muzzle.Finite||!end.Finite||!current.Finite||!predicted.Finite||!DoorPolicy.Finite(radius)||radius<=0)return false;
        var axis=end-muzzle;double length=axis.Length;if(length<.05)return false;var direction=axis*(1/length);
        double a=(current-muzzle).Dot(direction),b=(predicted-muzzle).Dot(direction);
        if(Math.Max(a,b)<=0||Math.Min(a,b)>length)return false;
        return FireLaneGeometry.SegmentDistance(current,predicted,muzzle,end)<radius;
    }
    // A low penetration estimate is not proof of safety: protect the ally unless a known solid cannot penetrate at all.
    public static bool CanReach(ShotGeometryTrace t)=>t.Reason!="geometry-solid-obstacle" ||
        !Array.Exists(t.Surfaces,s=>s.Kind=="solid"&&s.Known&&s.Chance==0);
}
public sealed class SquadFirePolicy
{
    public SquadFireTrace? Trace {get;private set;}
    public bool Blocking=>Trace?.State is "blocked" or "clearing";
    private double started,clearAt=-1,last=-1,freshAt=-1;
    public bool Tick(SquadFireFrame f,SquadFireConfig c)
    {
        if(!c.Enabled||!DoorPolicy.Finite(f.Now)||f.Now<0||f.Now<last){End("squad-fire-disabled-or-clock-reset");last=f.Now;return false;}
        double gap=last<0?0:f.Now-last;last=f.Now;
        if(!f.Fresh){clearAt=-1;if(Blocking&&f.Now-freshAt<=.25){ResetEffect();Trace!.Reason="squad-fire-awaiting-fresh-check";return true;}
            End("squad-fire-check-unavailable");return false;}
        freshAt=f.Now;
        if(f.Risk){
            if(!Blocking){started=f.Now;Trace=new();}clearAt=-1;
            var t=Trace!;ResetEffect();t.State="blocked";t.Reason=f.Kind=="body"?"squad-fire-body-in-line":"squad-fire-predicted-crossing";
            t.Ally=f.Ally;t.AllyName=f.AllyName;t.Kind=f.Kind;t.Action=f.Action;t.Geometry=f.Geometry;t.Prediction=c.Prediction;
            t.Radius=f.Kind=="body"?c.BodyRadius:c.PredictionRadius;t.Distance=f.Distance;t.Speed=f.Speed;
            t.MuzzleX=f.Muzzle.X;t.MuzzleY=f.Muzzle.Y;t.MuzzleZ=f.Muzzle.Z;t.EndX=f.End.X;t.EndY=f.End.Y;t.EndZ=f.End.Z;
            t.AllyX=f.Position.X;t.AllyY=f.Position.Y;t.AllyZ=f.Position.Z;t.PredictedX=f.Predicted.X;t.PredictedY=f.Predicted.Y;t.PredictedZ=f.Predicted.Z;
            t.BlockedFor=f.Now-started;t.ClearFor=0;t.Alternatives=f.Alternatives;return true;
        }
        if(!Blocking)return false;
        ResetEffect();Trace!.Action=f.Action;if(clearAt<0||gap>Math.Max(.15,c.Interval*2.5))clearAt=f.Now;
        Trace!.ClearFor=f.Now-clearAt;Trace.BlockedFor=f.Now-started;
        if(Trace.ClearFor+1e-6>=c.ClearDelay){End("squad-fire-lane-clear-native-decides");return false;}
        Trace.State="clearing";Trace.Reason="squad-fire-confirming-clear-lane";return true;
    }
    private void ResetEffect(){if(Trace==null)return;Trace.Applied=Trace.StopRequested=Trace.Stopped=Trace.StartPrevented=false;Trace.ValidFor=0;Trace.Execution="veto proposed";}
    public void Applied(bool requested,bool stopped,bool prevented,SquadFireConfig c)
    {if(!Blocking)return;var t=Trace!;t.Applied=requested||prevented;t.StopRequested=requested;t.Stopped=stopped&&requested;t.StartPrevented=prevented;
        t.ValidFor=t.Stopped||prevented?Math.Min(.25,c.Interval+.1):0;t.Execution=prevented?"new trigger request blocked":t.Stopped?"trigger release observed":requested?"trigger release requested; not observed":"veto proposed";}
    public void End(string reason){if(Trace==null)return;ResetEffect();Trace.State="ended";Trace.Reason=reason;Trace.Execution="S08 veto removed; native fire decisions retained";clearAt=-1;}
}
