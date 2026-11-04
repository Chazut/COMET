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
    public static double BodyDistance(DoorPoint from,DoorPoint to,FireLaneRay ray)
    {
        if(!from.Finite||!to.Finite||!ray.From.Finite||!ray.To.Finite)return double.PositiveInfinity;
        // A swept standing-body envelope, not a flat X/Z corridor shared by different floors.
        double best=double.PositiveInfinity;
        foreach(double h in new[]{.4,1.0,1.65})best=Math.Min(best,SegmentDistance(from+new DoorPoint(0,h,0),to+new DoorPoint(0,h,0),ray.From,ray.To));
        return best;
    }
    public static bool Blocks(DoorPoint from,DoorPoint to,FireLaneRay ray,double width)
    {
        var axis=ray.To-ray.From;double length=axis.Length;if(length<.5)return false;
        double along=(from+new DoorPoint(0,1.1,0)-ray.From).Dot(axis)/length;
        double next=(to+new DoorPoint(0,1.1,0)-ray.From).Dot(axis)/length;
        if(Math.Max(along,next)<=.25||Math.Min(along,next)>=length)return false;
        return BodyDistance(from,to,ray)<width;
    }
    public static bool SafeExit(DoorPoint from,DoorPoint goal,IReadOnlyList<FireLaneRay> rays,double width)
    {
        foreach(var ray in rays){double now=BodyDistance(from,from,ray),end=BodyDistance(goal,goal,ray);
            if(Blocks(from,from,ray,width)){
                // Keep the original side; never escape by crossing through the muzzle axis.
                var axis=ray.To-ray.From;var side=new DoorPoint(-axis.Z,0,axis.X);
                double a=(from-ray.From).Dot(side),z=(goal-ray.From).Dot(side);
                if(end<width||end<now+.15||a*z<-.001)return false;
            }else if(Blocks(from,goal,ray,width))return false;
        }
        return true;
    }
    public static FireLaneRay? Threat(DoorPoint from,DoorPoint ahead,IReadOnlyList<FireLaneRay> rays,FireLaneConfig c)
        =>rays.Where(r=>r.Age>=0&&r.Age<=c.ShotGrace&&Blocks(from,ahead,r,c.Width))
            .OrderBy(r=>BodyDistance(from,from,r)).ThenBy(r=>r.Shooter,StringComparer.Ordinal).FirstOrDefault();
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
        if(!MedicalChoice.Finite(f.Now)||f.Now<0||f.Now<last){End("lane-clock-reset");RetryAt=0;last=f.Now;return false;}last=f.Now;
        if(!c.Enabled||f.Block.Length>0||!f.Position.Finite||!f.Desired.Finite||!MedicalChoice.Finite(f.Speed)||f.Speed<0)
        {End(f.Block.Length>0?f.Block:"lane-disabled-or-invalid");return false;}
        if(f.Threat==null){End("lane-clear");return false;}
        if(f.Threat.Age<0||f.Threat.Age>c.ShotGrace||!MedicalChoice.Finite(f.Threat.Age)){End("lane-shot-expired");return false;}
        if(!f.GeometryFresh){if(Active)RetryAt=f.Now+c.Retry;End("lane-no-fresh-clearance");return false;}
        if(f.Now<RetryAt)return false;
        if(!Active||f.Context!=context){End("lane-route-changed");context=f.Context;origin=f.Position;started=f.Now;shooter=f.Threat.Shooter;
            Trace=new(){Context=context,Action=f.Action,State="holding",Reason="lane-let-shooter-finish"};}
        var t=Trace!;
        t.Applied=t.Confirmed=false;t.ValidFor=0;t.Execution="proposed";t.Shooter=f.Threat.Shooter;t.ShooterName=f.Threat.Name;t.ShotAge=f.Threat.Age;
        t.X=f.Position.X;t.Y=f.Position.Y;t.Z=f.Position.Z;t.FromX=f.Threat.From.X;t.FromY=f.Threat.From.Y;t.FromZ=f.Threat.From.Z;
        t.ToX=f.Threat.To.X;t.ToY=f.Threat.To.Y;t.ToZ=f.Threat.To.Z;t.Wait=Math.Max(0,(t.State=="stepping"?stepStarted:f.Now)-started);t.Speed=f.Speed;t.Inside=f.Inside;t.Width=c.Width;t.Prediction=c.Prediction;t.Alternatives=f.Rejected;
        if(t.State=="stepping"){
            bool safe=t.Side=="left"?f.LeftSafe:f.RightSafe;
            if(shooter!=f.Threat.Shooter||!safe||(f.Position-origin).Length>c.SideDistance+.1||f.Now-stepStarted>1.2)
            {RetryAt=f.Now+c.Retry;End("lane-detour-ended-or-unsafe");return false;}
            if((f.Position-goal).Length<.18){RetryAt=f.Now+c.Retry;End("lane-detour-complete");return false;}
        }else if(f.Inside||t.Wait>=c.MaxHold){
            if(!f.LeftSafe&&!f.RightSafe){RetryAt=f.Now+c.Retry;End("lane-no-safe-detour-return-native");return false;}
            t.Side=f.LeftSafe?"left":"right";goal=f.LeftSafe?f.Left:f.Right;stepStarted=f.Now;origin=f.Position;shooter=f.Threat.Shooter;
            t.State="stepping";t.Reason="lane-clear-shooters-axis";
        }
        if(t.State=="holding"&&t.Wait>c.MaxHold){RetryAt=f.Now+c.Retry;End("lane-wait-limit");return false;}
        t.GoalX=goal.X;t.GoalY=goal.Y;t.GoalZ=goal.Z;return true;
    }
    public void Applied(bool confirmed,FireLaneConfig c)
    {if(!Active)return;var t=Trace!;t.Applied=true;t.Confirmed=confirmed;t.ValidFor=confirmed?Math.Min(.25,c.Refresh+.1):0;t.Execution=confirmed?(t.State=="holding"?"stationary observed":"lateral movement observed"):"input applied; awaiting motion sample";}
    public void End(string reason){if(Trace==null)return;Trace.State="ended";Trace.Reason=reason;Trace.Applied=Trace.Confirmed=false;Trace.ValidFor=0;Trace.Execution="native movement restored";}
}
