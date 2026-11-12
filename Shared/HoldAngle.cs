using System;

namespace Comet.Core;

public sealed class HoldAngleConfig
{
    public bool Enabled { get; set; } = true;
    public bool AdjustPose { get; set; } = true;
    public double Delay { get; set; } = 1;
    public double MaxStep { get; set; } = 1.5;
    public double Spacing { get; set; } = .3;
    public double Limit { get; set; } = 4;
    public double Refresh { get; set; } = .25;
    public double Retry { get; set; } = 8;
    public double RepeatRetry { get; set; } = 15;
    public void Validate()
    {
        CometConfig.Range(Delay,.5,3,nameof(Delay));
        CometConfig.Range(MaxStep,.3,2,nameof(MaxStep));
        CometConfig.Range(Spacing,.2,.5,nameof(Spacing));
        CometConfig.Range(Limit,1,6,nameof(Limit));
        CometConfig.Range(Refresh,.1,.5,nameof(Refresh));
        CometConfig.Range(Retry,4,30,nameof(Retry));
        CometConfig.Range(RepeatRetry,8,60,nameof(RepeatRetry));
        if(RepeatRetry<Retry||Spacing>MaxStep)throw new ArgumentException("Hold angle retry/spacing order is invalid.");
    }
}
public sealed class HoldAngleFrame
{
    public double Now,HoldStarted,ObservedAt,GeometryAt,Pose,Speed;
    public string Target="",Source="",Block="";
    public DoorPoint Position,Known;
    public bool EyesClear,MuzzleClear,RouteOwned=true;
}
public sealed class HoldAngleCandidate
{
    public string Id="",Reason="";
    public DoorPoint Position;
    public double Pose,Cost;
    public bool Safe;
}
public sealed class HoldAngleTrace
{
    public int Episode;
    public string State="planning",Reason="hold-angle-blocked",Target="",Source="",Candidate="none";
    public double ContactAge,Remaining,ValidFor,KnownX,KnownY,KnownZ,AimX,AimY,AimZ;
    public double OriginX,OriginY,OriginZ,X,Y,Z,RequestedPose,ObservedPose,Distance;
    public bool PoseRequested,MoveRequested,Arrived,EyesClear,MuzzleClear;
    public string[] Alternatives=Array.Empty<string>();
}
// One correction per Hold opportunity. No random rolls, hidden transforms or Unity dependencies.
public sealed class HoldAngle
{
    public readonly PreparationWait Preparation=new();

    public HoldAngleTrace? Trace { get; private set; }
    public bool Active=>Trace?.State is "planning" or "adjusting" or "watching";
    public DoorPoint Origin { get; private set; }
    public DoorPoint Goal { get; private set; }
    public double Pose { get; private set; }
    public double NextAttempt { get; private set; }
    private double hold,deadline,last,basePose,lastHold=-1;
    private string lastTarget="",failedTarget="";
    private DoorPoint failedPosition,failedKnown;
    private int failures,episode;
    public static string Gate(HoldAngleFrame f,HoldAngleConfig c)
    {
        if(!c.Enabled)return "hold-angle-disabled";
        if(f.Block.Length>0)return f.Block;
        if(f.Target.Length==0||f.Source!="personal-vision"&&f.Source!="personal-hearing")return "hold-angle-no-personal-evidence";
        if(!DoorPolicy.Finite(f.Now)||!DoorPolicy.Finite(f.HoldStarted)||!DoorPolicy.Finite(f.ObservedAt)
            ||f.HoldStarted<0||f.ObservedAt<0||f.Now<f.HoldStarted||f.Now<f.ObservedAt
            ||!CornerChase.Point(f.Position)||!CornerChase.Point(f.Known)||!DoorPolicy.Finite(f.Pose)||f.Pose<0||f.Pose>1
            ||!DoorPolicy.Finite(f.Speed)||f.Speed<0)return "hold-angle-invalid-evidence";
        if(f.Now-f.ObservedAt>=(f.Source=="personal-vision"?10:5))return "hold-angle-contact-expired";
        return "";
    }
    public bool Begin(HoldAngleFrame f,HoldAngleConfig c)
    {
        if(Active||Gate(f,c).Length>0||f.Speed>.35||f.Now-f.HoldStarted<c.Delay||f.Now<NextAttempt
            ||lastTarget==f.Target&&lastHold==f.HoldStarted)return false;
        if(failedTarget==f.Target&&(f.Position-failedPosition).Length<1&&(f.Known-failedKnown).Length<2)return false;
        Preparation.Clear();Origin=Goal=f.Position;Pose=basePose=f.Pose;hold=lastHold=f.HoldStarted;lastTarget=f.Target;last=f.Now;deadline=f.Now+c.Limit;
        Trace=new(){Episode=++episode,Target=f.Target,Source=f.Source,OriginX=Origin.X,OriginY=Origin.Y,OriginZ=Origin.Z,
            X=Origin.X,Y=Origin.Y,Z=Origin.Z,RequestedPose=Pose};
        UpdateTrace(f);return true;
    }
    public bool Observe(HoldAngleFrame f,HoldAngleConfig c)
    {
        if(!Active)return false;
        string reason=Gate(f,c);
        if(reason.Length==0&&(f.Target!=Trace!.Target||f.HoldStarted!=hold||f.Now<last||f.Now-last>.5))reason="hold-angle-context-changed";
        if(reason.Length==0&&(f.Position-Origin).Length>c.MaxStep+.05)reason="hold-angle-origin-limit";
        if(reason.Length==0&&!f.RouteOwned)reason="hold-angle-native-route-taken-over";
        if(reason.Length==0&&!c.AdjustPose&&Math.Abs(Pose-basePose)>.01)reason="hold-angle-posture-disabled";
        if(reason.Length==0&&Trace!.State!="watching"&&f.Now>=deadline+Preparation.Seconds)reason="hold-angle-correction-timeout";
        if(reason.Length==0&&Trace!.State!="planning"&&(!DoorPolicy.Finite(f.GeometryAt)||f.GeometryAt>f.Now||f.Now-f.GeometryAt>c.Refresh+.1))reason="hold-angle-safety-expired";
        if(reason.Length>0){End(reason,f,c,true);return false;}
        last=f.Now;UpdateTrace(f);
        if(Trace!.State=="adjusting"&&f.Now-f.GeometryAt<.01&&(f.Position-Goal).Length<=.18&&f.Speed<=.35&&Math.Abs(f.Pose-Pose)<=.08
            &&f.EyesClear&&f.MuzzleClear) {Trace.State="watching";Trace.Reason="hold-angle-open-observed";Trace.Arrived=true;UpdateTrace(f);}
        if(Trace.State=="watching"&&(!f.EyesClear||!f.MuzzleClear||Math.Abs(f.Pose-Pose)>.15||(f.Position-Goal).Length>.25))
        {End("hold-angle-opening-lost",f,c,true);return false;}
        return true;
    }
    public bool Choose(HoldAngleCandidate candidate,DoorPoint aim,HoldAngleFrame f,HoldAngleConfig c)
    {
        if(!Active||Trace!.State!="planning"||!Observe(f,c)||!candidate.Safe||candidate.Id.Length==0
            ||!CornerChase.Point(candidate.Position)||!CornerChase.Point(aim)||(candidate.Position-Origin).Length>c.MaxStep+.001
            ||!DoorPolicy.Finite(candidate.Pose)||candidate.Pose<0||candidate.Pose>1
            ||!c.AdjustPose&&Math.Abs(candidate.Pose-Pose)>.01||!DoorPolicy.Finite(candidate.Cost))return false;
        Goal=candidate.Position;Pose=candidate.Pose;Trace.State="adjusting";Trace.Reason="hold-angle-safe-correction-selected";
        Trace.Candidate=candidate.Id;Trace.X=Goal.X;Trace.Y=Goal.Y;Trace.Z=Goal.Z;Trace.AimX=aim.X;Trace.AimY=aim.Y;Trace.AimZ=aim.Z;
        Trace.RequestedPose=Pose;return true;
    }
    // The mover can snap a checked local destination by a few centimetres.
    public void AcceptEndpoint(DoorPoint endpoint)
    {if(Active&&endpoint.Finite&&(endpoint-Goal).Length<=.3){Goal=endpoint;Trace!.X=endpoint.X;Trace.Y=endpoint.Y;Trace.Z=endpoint.Z;}}
    public void Requested(bool pose,bool move){if(!Active)return;Trace!.PoseRequested|=pose;Trace.MoveRequested|=move;}
    public void End(string reason,HoldAngleFrame f,HoldAngleConfig c,bool failed)
    {
        if(!Active)return;
        UpdateTrace(f);Trace!.State="ended";Trace.Reason=reason;Trace.Remaining=Trace.ValidFor=0;
        if(failed){failures=failedTarget==Trace.Target&&(failedPosition-Origin).Length<1?failures+1:1;
            failedTarget=Trace.Target;failedPosition=Origin;failedKnown=CornerChase.Point(f.Known)?f.Known:new(Trace.KnownX,Trace.KnownY,Trace.KnownZ);
            NextAttempt=(DoorPolicy.Finite(f.Now)?Math.Max(f.Now,last):last)+(failures>1?c.RepeatRetry:c.Retry);}
    }
    private void UpdateTrace(HoldAngleFrame f)
    {
        var t=Trace!;
        if(DoorPolicy.Finite(f.Now)&&DoorPolicy.Finite(f.ObservedAt))t.ContactAge=Math.Max(0,f.Now-f.ObservedAt);
        if(f.Source is "personal-vision" or "personal-hearing")t.Source=f.Source;
        if(CornerChase.Point(f.Known)){t.KnownX=f.Known.X;t.KnownY=f.Known.Y;t.KnownZ=f.Known.Z;}
        if(DoorPolicy.Finite(f.Pose))t.ObservedPose=Math.Max(0,Math.Min(1,f.Pose));
        if(CornerChase.Point(f.Position))t.Distance=(f.Position-Origin).Length;
        t.EyesClear=f.EyesClear;t.MuzzleClear=f.MuzzleClear;
        t.Remaining=DoorPolicy.Finite(f.Now)&&DoorPolicy.Finite(f.ObservedAt)?Math.Max(0,(t.State=="watching"?f.ObservedAt+(t.Source=="personal-vision"?10:5):deadline)-f.Now):0;
        t.ValidFor=Math.Min(.2,t.Remaining);
    }
}
