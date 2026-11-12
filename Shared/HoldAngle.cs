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
        if(f.Target.Length==0)return "hold-angle-no-personal-evidence";
        if(!DoorPolicy.Finite(f.Now)||!DoorPolicy.Finite(f.HoldStarted)||!DoorPolicy.Finite(f.ObservedAt)
            ||f.HoldStarted<0||f.ObservedAt<0||f.Now<f.HoldStarted||f.Now<f.ObservedAt
            ||!CornerChase.Point(f.Position)||!CornerChase.Point(f.Known)||!DoorPolicy.Finite(f.Pose)||f.Pose<0||f.Pose>1
            ||!DoorPolicy.Finite(f.Speed)||f.Speed<0)return "hold-angle-invalid-evidence";
        return "";
    }
    public bool Begin(HoldAngleFrame f,HoldAngleConfig c)
    {
        if(Active||Gate(f,c).Length>0||f.Now-f.HoldStarted<c.Delay
            )return false;
        Preparation.Clear();Origin=Goal=f.Position;Pose=basePose=f.Pose;hold=lastHold=f.HoldStarted;lastTarget=f.Target;last=f.Now;deadline=f.Now+c.Limit;
        Trace=new(){Episode=++episode,Target=f.Target,Source=f.Source,OriginX=Origin.X,OriginY=Origin.Y,OriginZ=Origin.Z,
            X=Origin.X,Y=Origin.Y,Z=Origin.Z,RequestedPose=Pose};
        UpdateTrace(f);return true;
    }
    public void SourceObservation(bool moving,bool standing,DoorPoint goal,int tries)
    {
        if(!Active)return;
        Goal=goal;Pose=standing?1:basePose;
        Trace!.State=moving?"adjusting":"watching";
        Trace.Reason=moving?"hold-angle-step-to-opening":standing?"hold-angle-stand-to-see":"hold-angle-watch-corner";
        Trace.MoveRequested=moving;Trace.PoseRequested=standing;Trace.Arrived=!moving;
        Trace.X=goal.X;Trace.Y=goal.Y;Trace.Z=goal.Z;Trace.Candidate="peek-try-"+tries;
        Trace.RequestedPose=Pose;Trace.ValidFor=.3;Trace.Remaining=0;
    }
    public void Requested(bool pose,bool move){if(!Active)return;Trace!.PoseRequested|=pose;Trace.MoveRequested|=move;}
    public void End(string reason,HoldAngleFrame f,HoldAngleConfig c,bool failed)
    {
        if(!Active)return;
        UpdateTrace(f);Trace!.State="ended";Trace.Reason=reason;Trace.Remaining=Trace.ValidFor=0;
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
        t.Remaining=0; t.ValidFor=Active?.3:0;
    }
}
