using System;

namespace Comet.Core;

public sealed class CoverTravelConfig
{
    public bool Enabled { get; set; } = true;
    public double CloseDistance { get; set; } = 30;
    public double VisionAge { get; set; } = 4;
    public double ShortDash { get; set; } = 8;
    public double FaceLimit { get; set; } = 2;
    public double Stability { get; set; } = .6;
    public double Refresh { get; set; } = .25;
    public void Validate()
    {
        CometConfig.Range(CloseDistance,5,60,nameof(CloseDistance));
        CometConfig.Range(VisionAge,.5,5,nameof(VisionAge));
        CometConfig.Range(ShortDash,2,12,nameof(ShortDash));
        CometConfig.Range(FaceLimit,.5,3,nameof(FaceLimit));
        CometConfig.Range(Stability,.1,1,nameof(Stability));
        CometConfig.Range(Refresh,.1,.5,nameof(Refresh));
        if(Stability>FaceLimit)throw new ArgumentException("Cover travel stability cannot exceed the facing phase.");
    }
}
public sealed class CoverTravelFrame
{
    public double Now,ObservedAt,GeometryAt,Length,Turn,Exposure,Speed,FacingDot;
    public string Target="",Context="",Block="";
    public DoorPoint Position,Known,Goal;
    public bool Geometry,Protected,Ready,CanSprint,NativeSprint,ActualSprint;
}
public sealed class CoverTravelTrace
{
    public string State="native",Reason="",Target="",Context="",Source="personal-vision",Observed="stationary";
    public double ContactAge,ValidFor,Remaining,Length,Turn,Exposure,Speed,FacingDot,KnownX,KnownY,KnownZ,X,Y,Z;
    public bool Geometry,Protected,NativeSprint,SprintRequested,WalkRequested,GazeRequested;
    public string[] Alternatives=Array.Empty<string>();
}
// Changes only the travel mode of an existing native route. A spent facing phase cannot
// restart through a new path object, brief safety deferral or native decision oscillation.
public sealed class CoverTravelPolicy
{
    public CoverTravelTrace? Trace { get; private set; }
    public static string Gate(CoverTravelFrame f,CoverTravelConfig c)
    {
        if(!c.Enabled)return "travel-disabled";
        if(f.Block.Length>0)return f.Block;
        if(f.Target.Length==0||f.Context.Length==0)return "travel-no-context";
        if(!DoorPolicy.Finite(f.Now)||!DoorPolicy.Finite(f.ObservedAt)||f.ObservedAt<0||f.Now<f.ObservedAt
            ||!CornerChase.Point(f.Position)||!CornerChase.Point(f.Known)||!CornerChase.Point(f.Goal)
            ||!DoorPolicy.Finite(f.Speed)||f.Speed<0||!DoorPolicy.Finite(f.FacingDot)||Math.Abs(f.FacingDot)>1)
            return "travel-invalid-evidence";
        if(f.Now-f.ObservedAt>=c.VisionAge)return "travel-visual-memory-expired";
        if((f.Known-f.Position).Length>c.CloseDistance)return "travel-threat-too-far";
        if(!DoorPolicy.Finite(f.Length)||f.Length<0)return "travel-invalid-route";
        return "";
    }
    public string Tick(CoverTravelFrame f,CoverTravelConfig c)
    {
        string reason=Gate(f,c),next="native";
        if(reason.Length==0)
        {
            if(f.Length<=c.ShortDash){next="native";reason="travel-short-dash-native";}
            else{next="face-walk";reason="travel-face-near-threat";}
        }
        double age=Finite(f.Now-f.ObservedAt);
        Trace=new(){State=next,Reason=reason,Target=f.Target,Context=f.Context,ContactAge=age,
            ValidFor=next=="native"?0:Math.Max(0,Math.Min(.3,c.VisionAge-age)),
            Remaining=Math.Max(0,c.VisionAge-age),
            Length=Finite(f.Length),Turn=Finite(f.Turn),Exposure=Finite(f.Exposure),Geometry=f.Geometry,Protected=f.Protected,
            Speed=Finite(f.Speed),FacingDot=DoorPolicy.Finite(f.FacingDot)?f.FacingDot:0,NativeSprint=f.NativeSprint,
            KnownX=Finite(f.Known.X),KnownY=Finite(f.Known.Y),KnownZ=Finite(f.Known.Z),X=Finite(f.Goal.X),Y=Finite(f.Goal.Y),Z=Finite(f.Goal.Z),
            Observed=f.ActualSprint?"sprinting":f.Speed<.15?"stationary":f.FacingDot<-.25?"backpedaling":Math.Abs(f.FacingDot)<=.5?"strafing":"walking-forward",
            Alternatives=new[]{"Face the close enemy seen recently", "Keep native sprint choice for nearby cover"}};
        return next;
    }
    private static double Finite(double v)=>DoorPolicy.Finite(v)?v:0;
    public void Requested(bool walk,bool sprint,bool gaze=false)
    {if(Trace!=null){Trace.WalkRequested=walk;Trace.SprintRequested=sprint;Trace.GazeRequested|=gaze;}}
    public void End(string reason)
    {
        if(Trace==null)return;
        Trace.State="ended";Trace.Reason=reason;Trace.ValidFor=0;Trace.Remaining=0;
        Trace.WalkRequested=Trace.SprintRequested=Trace.GazeRequested=false;
    }
}
