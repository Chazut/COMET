using System;

namespace Comet.Core;

public sealed class AngleWatchConfig
{
    public bool Enabled { get; set; }=true;
    public bool WallCorrection { get; set; }=true;
    public bool SafeLean { get; set; }=true;
    public double CornerRange { get; set; }=6;
    public double MovingLimit { get; set; }=4;
    public double VisionAge { get; set; }=10;
    public double HearingAge { get; set; }=5;
    public double MovingWall { get; set; }=1.2;
    public double StillWall { get; set; }=4;
    public double HoldMinimum { get; set; }=1.2;
    public double Refresh { get; set; }=.25;
    public double PitchLimit { get; set; }=10;
    public void Validate()
    {
        CometConfig.Range(CornerRange,2,8,nameof(CornerRange));
        CometConfig.Range(MovingLimit,1,6,nameof(MovingLimit));
        CometConfig.Range(VisionAge,1,15,nameof(VisionAge));
        CometConfig.Range(HearingAge,1,8,nameof(HearingAge));
        CometConfig.Range(MovingWall,.5,2,nameof(MovingWall));
        CometConfig.Range(StillWall,1,4,nameof(StillWall));
        CometConfig.Range(HoldMinimum,.3,2,nameof(HoldMinimum));
        CometConfig.Range(Refresh,.1,.5,nameof(Refresh));
        CometConfig.Range(PitchLimit,5,45,nameof(PitchLimit));
        if(StillWall<MovingWall)throw new ArgumentException("Stationary wall distance must not be smaller than moving distance.");
    }
}
public sealed class AngleCandidate
{
    public string Id="",Kind="";
    public DoorPoint Aim;
    public bool Clear,Elevation,LeanSafe;
    public int Side;
}
public sealed class AngleWatchFrame
{
    public double Now,ObservedAt,GeometryAt;
    public string Target="",Source="",Action="",Block="";
    public DoorPoint Known,Position,Origin,NativeDirection,ObservedDirection;
    public bool Moving,WallBlocked;
    public AngleCandidate[] Candidates=Array.Empty<AngleCandidate>();
}
public sealed class AngleWatchTrace
{
    public string State="watching",Reason="",Target="",Source="",Action="",Candidate="",Kind="";
    public int Episode,LeanSide;
    public double ContactAge,Remaining,ValidFor,KnownX,KnownY,KnownZ,AimX,AimY,AimZ,RequestedPitch,ObservedPitch,AngleError;
    public bool Requested,Aligned,Elevation,WallBlocked,Moving,LeanRequested;
    public string[] Alternatives=Array.Empty<string>();
}
// Detached inputs only. This feature requests orientation, never firing or a path.
public sealed class AngleWatch
{
    public AngleWatchTrace? Trace { get; private set; }
    public bool Active=>Trace?.State=="watching";
    public DoorPoint Aim { get; private set; }
    public int Side { get; private set; }
    private double started,selectedAt,last,deadline;
    private int episode;
    private string exhaustedTarget="";
    private DoorPoint exhaustedPosition,exhaustedKnown;
    public static string Gate(AngleWatchFrame f,AngleWatchConfig c)
    {
        if(!c.Enabled)return "angle-disabled";
        if(f.Block.Length>0)return f.Block;
        if(f.Target.Length==0||f.Action.Length==0)return "angle-no-contact-or-action";
        if(!DoorPolicy.Finite(f.Now)||!DoorPolicy.Finite(f.ObservedAt)||f.ObservedAt<0||f.Now<f.ObservedAt
            ||!CornerChase.Point(f.Known)||!CornerChase.Point(f.Position)||!CornerChase.Point(f.Origin)
            ||!f.NativeDirection.Finite||f.NativeDirection.Length<.1||!f.ObservedDirection.Finite||f.ObservedDirection.Length<.1)return "angle-invalid-evidence";
        return "";
    }
    public bool Tick(AngleWatchFrame f,AngleWatchConfig c)
    {
        string gate=Gate(f,c);
        if(gate.Length>0){End(gate);return false;}
        if(Active&&(f.Target!=Trace!.Target||f.Source!=Trace.Source||f.Action!=Trace.Action||f.Now<last))End("angle-context-changed");
        if(exhaustedTarget.Length>0)exhaustedTarget="";
        AngleCandidate? chosen=null;
        // Hold only while the exact angle remains safe; newly blocked geometry cancels the hold immediately.
        foreach(var a in f.Candidates) {
            if(!a.Clear||!CornerChase.Point(a.Aim)||(a.Aim-f.Origin).Length<.5)continue;
            if(!a.Elevation&&Math.Abs(Pitch(a.Aim-f.Origin))>c.PitchLimit+.01)continue;
            if(a.Kind=="wall"&&!c.WallCorrection||a.Kind=="wall"&&!f.WallBlocked)continue;
            if(a.Kind!="corner"&&a.Kind!="wall"&&a.Kind!="pitch")continue;
            if(chosen==null)chosen=a;
            if(Active&&a.Id==Trace!.Candidate&&(a.Aim-Aim).Length<.4&&f.Now-selectedAt<c.HoldMinimum){chosen=a;break;}
        }
        if(chosen==null){
            End("angle-no-useful-clear-opening");
            if(Trace==null||Trace.Target!=f.Target)Trace=new(){State="ended",Episode=++episode,Target=f.Target,Source=f.Source,Action=f.Action,
                Reason="angle-no-useful-clear-opening",Candidate="none",Kind="none",ContactAge=f.Now-f.ObservedAt,
                KnownX=f.Known.X,KnownY=f.Known.Y,KnownZ=f.Known.Z,ObservedPitch=Pitch(f.ObservedDirection),AngleError=180,
                Alternatives=new[]{"No opening has both clear eye and muzzle lanes", "Keep native SAIN gaze"}};
            return false;
        }
        if(!Active) {
            started=selectedAt=f.Now;deadline=double.MaxValue;
            Trace=new(){Episode=++episode,Target=f.Target,Source=f.Source,Action=f.Action};
        }
        if(Trace!.Candidate!=chosen.Id)selectedAt=f.Now;
        Aim=chosen.Aim;Side=c.SafeLean&&chosen.LeanSafe?chosen.Side:0;last=f.Now;
        var t=Trace;t.Candidate=chosen.Id;t.Kind=chosen.Kind;t.State="watching";
        t.Reason=chosen.Kind=="corner"?"angle-prepare-known-approach":chosen.Kind=="wall"?"angle-wall-blocks-native-gaze":"angle-unjustified-pitch";
        t.KnownX=f.Known.X;t.KnownY=f.Known.Y;t.KnownZ=f.Known.Z;t.AimX=Aim.X;t.AimY=Aim.Y;t.AimZ=Aim.Z;
        t.Elevation=chosen.Elevation;t.WallBlocked=f.WallBlocked;t.Moving=f.Moving;t.LeanSide=Side;
        t.ContactAge=f.Now-f.ObservedAt;t.Remaining=0;t.ValidFor=.3;
        t.RequestedPitch=Pitch(Aim-f.Origin);t.ObservedPitch=Pitch(f.ObservedDirection);t.AngleError=Angle(Aim-f.Origin,f.ObservedDirection);
        t.Aligned=t.AngleError<=8;t.Requested=true;t.LeanRequested=false;
        var alt=new string[Math.Min(6,f.Candidates.Length)+1];alt[0]="Native SAIN gaze; no firing or movement requested";
        for(int i=1;i<alt.Length;i++){var a=f.Candidates[i-1];alt[i]=a.Id+": "+(a.Clear?"clear eye and muzzle lanes":"blocked")+(a.Id==chosen.Id?"; selected":"; alternative");}
        t.Alternatives=alt;return true;
    }
    public void End(string reason){if(!Active)return;Trace!.State="ended";Trace.Reason=reason;Trace.ValidFor=Trace.Remaining=0;Side=0;}
    public static double Pitch(DoorPoint p)=>Math.Atan2(p.Y,Math.Sqrt(p.X*p.X+p.Z*p.Z))*180/Math.PI;
    public static double Angle(DoorPoint a,DoorPoint b)=>a.Length<.001||b.Length<.001?180:Math.Acos(Math.Max(-1,Math.Min(1,a.Dot(b)/(a.Length*b.Length))))*180/Math.PI;
    public static DoorPoint ClampPitch(DoorPoint d,double degrees)
    {
        double h=Math.Sqrt(d.X*d.X+d.Z*d.Z);if(h<.001)return new(0,0,1);
        double cap=h*Math.Tan(degrees*Math.PI/180);return new(d.X,Math.Max(-cap,Math.Min(cap,d.Y)),d.Z);
    }
}
