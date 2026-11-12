using System;
using System.Collections.Generic;
using System.Linq;
namespace Comet.Core;

public sealed class FireLaneConfig
{
    public bool Enabled {get;set;}=true;
    public double Width {get;set;}=.9;
    public double Prediction {get;set;}=.3;
    public double Refresh {get;set;}=.1;
    public double ShotGrace {get;set;}=.2;
    public double MaxHold {get;set;}=.8;
    public double SideDistance {get;set;}=1.2;
    public double Retry {get;set;}=1;
    public void Validate()
    {
        CometConfig.Range(Width,.3,1.5,nameof(Width));CometConfig.Range(Prediction,.1,.6,nameof(Prediction));
        CometConfig.Range(Refresh,.05,.2,nameof(Refresh));CometConfig.Range(ShotGrace,.05,.4,nameof(ShotGrace));
        CometConfig.Range(MaxHold,.1,1.5,nameof(MaxHold));CometConfig.Range(SideDistance,.4,1.5,nameof(SideDistance));
        CometConfig.Range(Retry,.5,3,nameof(Retry));
    }
}
public sealed class FireLaneRay
{
    public string Shooter="",Name="";
    public DoorPoint From,To;
    public double Age;
}
public static class FireLaneGeometry
{
    public static double SegmentDistance(DoorPoint p,DoorPoint q,DoorPoint a,DoorPoint b)
    {
        var d=q-p;var e=b-a;var r=p-a;double dd=d.Dot(d),ee=e.Dot(e),de=d.Dot(e),dr=d.Dot(r),er=e.Dot(r);
        double s=0,t=0;
        if(dd<=1e-10&&ee<=1e-10)return r.Length;
        if(dd<=1e-10)t=Clamp(er/ee);
        else if(ee<=1e-10)s=Clamp(-dr/dd);
        else {double den=dd*ee-de*de;s=den>1e-10?Clamp((de*er-dr*ee)/den):0;t=(de*s+er)/ee;
            if(t<0){t=0;s=Clamp(-dr/dd);}else if(t>1){t=1;s=Clamp((de-dr)/dd);}}
        return (p+d*s-a-e*t).Length;
    }
    private static double Clamp(double x)=>Math.Max(0,Math.Min(1,x));
    public static double PointDistance(DoorPoint p,FireLaneRay ray,out double t)
    {var d=ray.To-ray.From;double len=d.Dot(d);t=len>0?Clamp((p-ray.From).Dot(d)/len):0;return(p-(ray.From+d*t)).Length;}
    public static double BodyDistance(DoorPoint from,DoorPoint to,FireLaneRay ray)
        =>PointDistance(from+new DoorPoint(0,1.2,0),ray,out _);
    public static bool Blocks(DoorPoint from,DoorPoint to,FireLaneRay ray,double width)
    {
        double now=PointDistance(from+new DoorPoint(0,1.2,0),ray,out var t);
        if(t<=.02||t>=.98)return false;
        double ahead=PointDistance(to+new DoorPoint(0,1.2,0),ray,out _);
        return now<width||(ahead<width&&ahead<now-.05);
    }
    public static bool SafeExit(DoorPoint from,DoorPoint goal,IReadOnlyList<FireLaneRay> rays,double width)=>true;
    public static FireLaneRay? Threat(DoorPoint from,DoorPoint ahead,IReadOnlyList<FireLaneRay> rays,FireLaneConfig c)
        =>rays.FirstOrDefault(r=>Blocks(from,ahead,r,c.Width));
}

public sealed class FireLaneFrame
{
    public double Now,Speed;public string Block="",Action="",Context="";
    public DoorPoint Position,Desired;
    public FireLaneRay? Threat;
    public bool Inside,GeometryFresh,LeftSafe,RightSafe;
    public DoorPoint Left,Right;
    public string[] Rejected=Array.Empty<string>();
}
public sealed class FireLaneTrace
{
    public string State="native",Reason="",Action="",Context="",Shooter="",ShooterName="",Side="",Execution="none";
    public double ValidFor,Wait,ShotAge,Width,Prediction,Speed,X,Y,Z,FromX,FromY,FromZ,ToX,ToY,ToZ,GoalX,GoalY,GoalZ;
    public bool Applied,Confirmed,Inside;
    public string[] Alternatives=Array.Empty<string>();
}
public sealed class FireLanePolicy
{
    public FireLaneTrace? Trace {get;private set;}
    public bool Active=>Trace?.State is "holding" or "stepping";
    public double RetryAt {get;private set;}
    private double started,last=-1,stepStarted;
    private string context="",shooter="";private DoorPoint origin,goal;
    public bool Tick(FireLaneFrame f,FireLaneConfig c)
    {
        if(!c.Enabled||f.Block.Length>0||!MedicalChoice.Finite(f.Now)){End(f.Block);return false;}
        if(f.Now<RetryAt)return false;
        if(f.Threat==null){started=-1;End("lane-clear");return false;}
        if(f.Inside&&!f.LeftSafe&&!f.RightSafe){started=-1;End("lane-no-open-side");return false;}
        bool hold=!f.Inside;
        if(hold){if(Trace?.State!="holding")started=f.Now;
            if(f.Now-started>c.MaxHold){RetryAt=f.Now+2;started=-1;End("lane-source-deadlock-release");return false;}}
        else started=-1;
        goal=f.LeftSafe?f.Left:f.Right;
        Trace=new(){State=hold?"holding":"stepping",Reason=hold?"lane-let-shooter-finish":"lane-clear-shooters-axis",
            Context=f.Context,Action=f.Action,Shooter=f.Threat.Shooter,ShooterName=f.Threat.Name,ShotAge=f.Threat.Age,
            Wait=hold?f.Now-started:0,Speed=f.Speed,Inside=f.Inside,Width=c.Width,Prediction=1,Side=f.LeftSafe?"left":"right",
            X=f.Position.X,Y=f.Position.Y,Z=f.Position.Z,FromX=f.Threat.From.X,FromY=f.Threat.From.Y,FromZ=f.Threat.From.Z,
            ToX=f.Threat.To.X,ToY=f.Threat.To.Y,ToZ=f.Threat.To.Z,GoalX=goal.X,GoalY=goal.Y,GoalZ=goal.Z,Alternatives=f.Rejected};
        return true;
    }
    public void Applied(bool confirmed,FireLaneConfig c)
    {if(!Active)return;var t=Trace!;t.Applied=true;t.Confirmed=confirmed;t.ValidFor=confirmed?Math.Min(.25,c.Refresh+.1):0;t.Execution=confirmed?(t.State=="holding"?"stationary observed":"lateral movement observed"):"input applied; awaiting motion sample";}
    public void End(string reason){if(Trace==null)return;Trace.State="ended";Trace.Reason=reason;Trace.Applied=Trace.Confirmed=false;Trace.ValidFor=0;Trace.Execution="native movement restored";}
}
