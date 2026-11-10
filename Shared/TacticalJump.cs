using System;
using System.Collections.Generic;

namespace Comet.Core;

public sealed class TacticalJumpConfig
{
    public bool Enabled { get; set; } = true;
    public bool NativeRush { get; set; } = true;
    public double AirShotChance { get; set; } = 25;
    public double RushMinSpeed { get; set; } = 2;
    // Extra floor only. Native stamina permissions and stricter feature floors always apply.
    public double MinimumStamina { get; set; } = 0;
    public double TakeoffTimeout { get; set; } = .5;
    public double SafetyRefresh { get; set; } = .1;
    public void Validate()
    {
        CometConfig.Range(AirShotChance,0,100,nameof(AirShotChance));
        CometConfig.Range(RushMinSpeed,1,5,nameof(RushMinSpeed));
        CometConfig.Range(MinimumStamina,0,90,nameof(MinimumStamina));
        CometConfig.Range(TakeoffTimeout,.1,1,nameof(TakeoffTimeout));
        CometConfig.Range(SafetyRefresh,.05,.2,nameof(SafetyRefresh));
    }
}
public sealed class TacticalJumpFrame
{
    public double Now,Speed,ForwardSpeed,MinSpeed,Stamina,MinStamina,GeometryAge;
    public string Feature="",Kind="",Target="",Action="",Block="",ClearanceDetail="";
    public bool Grounded,CanJump,Clearance;
    public DoorPoint Position,Landing;
}
public sealed class TacticalJumpTrace
{
    public string State="refused",Reason="",SourceFeature="",Kind="",Target="",Action="",LastRefusal="",ClearanceDetail="";
    public double GeometryAge;
    public double ValidFor,Speed,ForwardSpeed,MinimumSpeed,Stamina,MinimumStamina,Chance,Roll=-1,Elapsed;
    public double X,Y,Z,LandingX,LandingY,LandingZ;
    public int Attempt,ConcurrentRefusals,AirShots,GroundShots;
    public bool Requested,Accepted,TakeoffObserved,LandingObserved,Grounded,AirShotAllowed,FireHeld,StyleCached;
    public string[] Alternatives=Array.Empty<string>();
}
// One flight per actor; a bounded style history stores IDs/booleans only, never raid objects.
public sealed class TacticalJumpPolicy
{
    private readonly Dictionary<string,(bool Allowed,double Roll)> styles=new(StringComparer.Ordinal);
    private const int StyleCapacity=64;
    private double acceptedAt=-1000,lastAccepted=-1000,lastTime=-1;
    private DoorPoint lastTakeoff;
    private bool hasTakeoff;
    private int attempt;
    public TacticalJumpTrace? Trace { get; private set; }
    public bool Active => Trace?.State is "requested" or "pending" or "airborne";
    public bool HoldFire => Active && Trace!.Accepted && !Trace.Grounded && !Trace.AirShotAllowed;
    public int StyleCount => styles.Count;
    private static readonly string[] Choices={"Jump after real run-up and fresh safe clearance", "Continue on foot if the jump is refused", "Air fire permission keeps all native aiming and shooting checks", "Hold fire until observed landing for the other styles"};
    public static string Gate(TacticalJumpFrame f,TacticalJumpConfig c)
    {
        if(!c.Enabled)return "jump-control-disabled";
        if(f.Block.Length>0)return f.Block;
        if(f.Feature!="D02"&&f.Feature!="M03"&&f.Feature!="M05"&&f.Feature!="M11"&&f.Feature!="M14"&&f.Feature!="SAIN")return "jump-unknown-source";
        if(f.Feature=="SAIN"&&!c.NativeRush)return "jump-native-rush-disabled";
        if(f.Target.Length==0||f.Kind.Length==0)return "jump-missing-context";
        if(!Finite(f.Now)||!Finite(f.Speed)||!DoorPolicy.Finite(f.ForwardSpeed)||!Finite(f.MinSpeed)||f.MinSpeed<=0
            ||!Finite(f.Stamina)||f.Stamina>100||!Finite(f.MinStamina)||f.MinStamina>100||!Finite(f.GeometryAge)
            ||!CornerChase.Point(f.Position)||!CornerChase.Point(f.Landing))return "jump-invalid-evidence";
        if(!f.Grounded)return "jump-not-grounded";
        if(!f.CanJump)return "jump-native-permission-refused";
        if(f.Speed<f.MinSpeed||f.ForwardSpeed<f.MinSpeed)return "jump-insufficient-directional-speed";
        if(f.Stamina<Math.Max(c.MinimumStamina,f.MinStamina))return "jump-insufficient-stamina";
        if(!f.Clearance||f.GeometryAge>c.SafetyRefresh)return "jump-clearance-unavailable";
        return "";
    }
    public bool Request(TacticalJumpFrame f,TacticalJumpConfig c,Func<double> random)
    {
        if(Active){Trace!.ConcurrentRefusals++;Trace.LastRefusal="jump-already-in-flight-or-requested";return false;}
        string reason=Gate(f,c);
        if(reason.Length==0&&f.Now<lastTime)reason="jump-clock-reset";
        if(reason.Length==0&&f.Now-lastAccepted<.5)reason="jump-native-retry-delay";
        // Directional speed alone could be reported while colliding with a wall.
        if(reason.Length==0&&hasTakeoff&&FlatDistance(f.Position,lastTakeoff)<.5)reason="jump-no-progress-since-last-takeoff";
        Trace=new(){Attempt=++attempt,Reason=reason,SourceFeature=f.Feature,Kind=f.Kind,Target=f.Target,Action=f.Action,
            Grounded=f.Grounded,Speed=Safe(f.Speed),ForwardSpeed=Safe(f.ForwardSpeed),MinimumSpeed=Safe(f.MinSpeed),
            ClearanceDetail=f.ClearanceDetail,GeometryAge=Safe(f.GeometryAge),
            Stamina=Safe(f.Stamina),MinimumStamina=Math.Max(c.MinimumStamina,Safe(f.MinStamina)),Chance=c.AirShotChance,
            X=Safe(f.Position.X),Y=Safe(f.Position.Y),Z=Safe(f.Position.Z),LandingX=Safe(f.Landing.X),LandingY=Safe(f.Landing.Y),LandingZ=Safe(f.Landing.Z),Alternatives=Choices};
        if(reason.Length>0)return false;
        if(styles.TryGetValue(f.Target,out var style)){Trace.StyleCached=true;}
        else if(styles.Count<StyleCapacity) {
            double roll=random();
            if(!DoorPolicy.Finite(roll)||roll<0||roll>=1){Trace.Reason="jump-invalid-style-roll";return false;}
            style=(roll*100<c.AirShotChance,roll*100);styles.Add(f.Target,style);
        }else style=(false,-1); // No eviction/reroll exploit; excess targets use hold-fire style.
        Trace.Roll=style.Roll;Trace.AirShotAllowed=style.Allowed;Trace.Requested=true;
        Trace.State="requested";Trace.Reason="jump-authorized";Trace.ValidFor=.25;lastTime=f.Now;
        return true;
    }
    public void Result(bool accepted,double now)
    {
        if(Trace?.State!="requested")return;
        Trace.Accepted=accepted;
        Trace.State=accepted?"pending":"refused";
        Trace.Reason=accepted?"jump-accepted-awaiting-takeoff":"jump-native-refused";
        Trace.ValidFor=accepted?.25:0;
        if(accepted){acceptedAt=lastAccepted=now;}
    }
    public bool Observe(bool grounded,double now,DoorPoint position,double speed,double stamina,TacticalJumpConfig c)
    {
        if(!Active)return false;
        var t=Trace!;string previous=t.State;
        if(!Finite(now)||now<lastTime||!CornerChase.Point(position)||!Finite(speed)||!Finite(stamina)||stamina>100)
        {End("jump-observation-invalid");return true;}
        lastTime=now;t.Grounded=grounded;t.Speed=speed;t.Stamina=stamina;t.Elapsed=Math.Max(0,now-acceptedAt);
        if(t.Accepted&&t.State=="pending"&&!grounded){t.TakeoffObserved=true;t.State="airborne";t.Reason="jump-takeoff-observed";hasTakeoff=true;lastTakeoff=position;}
        if(t.State=="airborne"&&grounded){t.LandingObserved=true;t.State="landed";t.Reason="jump-landed-native-fire-restored";}
        if(t.State=="pending"&&now-acceptedAt>c.TakeoffTimeout){t.State="ended";t.Reason="jump-takeoff-not-observed";}
        t.FireHeld=HoldFire;t.ValidFor=Active?.25:0;
        return previous!=t.State;
    }
    public void Shot(bool grounded,double now)
    {
        if(Trace==null||!Trace.Accepted||!Finite(now)||now<acceptedAt||now-acceptedAt>2.5)return;
        if(grounded)Trace.GroundShots++;else Trace.AirShots++;
    }
    public void End(string reason)
    {if(Trace!=null){Trace.State="ended";Trace.Reason=reason;Trace.ValidFor=0;Trace.FireHeld=false;}}
    public void Clear(string reason){End(reason);styles.Clear();hasTakeoff=false;lastAccepted=-1000;lastTime=-1;}
    private static bool Finite(double v)=>DoorPolicy.Finite(v)&&v>=0;
    private static double Safe(double v)=>DoorPolicy.Finite(v)?v:0;
    private static double FlatDistance(DoorPoint a,DoorPoint b)=>Math.Sqrt((a.X-b.X)*(a.X-b.X)+(a.Z-b.Z)*(a.Z-b.Z));
}
