using System;
using System.Collections.Generic;
using System.Linq;

namespace Comet.Core;

public sealed class CornerChaseConfig
{
    public bool Enabled { get; set; }=true;
    public double LostSightMin { get; set; }=.3;
    public double ObservationMaxAge { get; set; }=6;
    public double PathLimit { get; set; }=15;
    public double SequenceLimit { get; set; }=6;
    public double WideOffset { get; set; }=1.8;
    public double JumpMinSpeed { get; set; }=2.2;
    public double JumpMinStamina { get; set; }=0;
    public double Cooldown { get; set; }=0;
    public Dictionary<string,double> Chances { get; set; }=new() {
        ["GigaChad"]=90,["Wreckless"]=85,["Chad"]=75,["Normal"]=40,["Rat"]=0,["SnappingTurtle"]=0,["Coward"]=0,["Timmy"]=0 };
    public void Validate()
    {
        void R(double v,double a,double b){if(!DoorPolicy.Finite(v)||v<a||v>b)throw new ArgumentException("Corner chase: invalid limit.");}
        R(LostSightMin,.2,1);R(ObservationMaxAge,1,6);R(PathLimit,3,20);R(SequenceLimit,1,10);R(WideOffset,.5,2);
        R(JumpMinSpeed,2.2,4);R(JumpMinStamina,0,90);R(Cooldown,0,30);
        if(LostSightMin>=ObservationMaxAge||Chances==null||Chances.Count!=8||new[]{"GigaChad","Wreckless","Chad","Normal","Rat","SnappingTurtle","Coward","Timmy"}.Any(x=>!Chances.ContainsKey(x)))
            throw new ArgumentException("Corner chase: incomplete personality chances.");
        foreach(double v in Chances.Values)R(v,0,100);
    }
}

// Only the adapter's personally visible observations enter here. Hidden/shared updates cannot move this memory.
public sealed class ChaseObservation
{
    public string Target { get; private set; }="";
    public DoorPoint Known { get; private set; }
    public DoorPoint Direction { get; private set; }
    public double SeenAt { get; private set; }=-1000;
    public double Speed { get; private set; }
    public int Episode { get; private set; }
    public bool Lost { get; private set; }
    private bool visible;
    public void Sample(string target,bool seen,DoorPoint point,double now)
    {
        if(!DoorPolicy.Finite(now)||now<0||now<SeenAt){Clear();return;}
        if(Target!=target){Clear();Target=target;}
        if(!seen) {if(visible){Episode++;Lost=true;}visible=false;return;}
        if(string.IsNullOrEmpty(target)||!CornerChase.Point(point))return;
        double dt=now-SeenAt;
        if(visible&&dt>=.05&&dt<=.3) {
            var delta=point-Known;delta=new(delta.X,0,delta.Z);Speed=delta.Length/dt;
            Direction=Speed>=.5&&Speed<=10?delta*(1/delta.Length):default;
            if(Speed>10)Speed=0;
        }else if(!visible||dt>.3){Direction=default;Speed=0;}
        Known=point;SeenAt=now;visible=true;Lost=false;
    }
    public void Clear(){Target="";SeenAt=-1000;Direction=default;Speed=0;visible=Lost=false;}
}
public sealed class CornerChaseFrame
{
    public CombatStepFrame Combat=new();
    public string GeometryReason="";
    public int ObservationEpisode;
    public double SeenAt,PathLength,Speed,Stamina=100;
    public double Weakness=.2,AmmoRatio=1,PrefireChance=60,JumpChance=30;
    public bool Healthy=true,Hurt,Confident,AmmoOk,AngleHeld,PrefireEnabled=true;
    public bool RealCorner,RouteValid=true,GeometryFresh=true,LaneSafe=true,AtGoal,CanJump,Fleeing,NativeLeanOwned;
}
public sealed class CornerChaseTrace
{
    public string State="planning",Reason="chase-selected",Target="",Action="RushEnemy",Source="personal-vision",Style="pie",GeometryReason="";
    public int Episode,ObservationEpisode,Requests,Jumps,Side;
    public double Chance,Roll,Remaining,ValidFor,ContactAge,KnownX,KnownY,KnownZ,GoalX,GoalY,GoalZ,PathLength,Travel,Speed;
    public bool Selected,Requested,MovementObserved,JumpRequested,JumpAccepted,TakeoffObserved,LandingObserved;
    public string[] Alternatives=Array.Empty<string>();
}
public sealed class CornerChase
{
    public CornerChaseTrace? Trace { get; private set; }
    public bool Active=>Trace is {Selected:true}&&Trace.State!="ended";
    public double Deadline { get; private set; }
    public double NextAttempt { get; private set; }
    public DoorPoint Known { get; private set; }
    private double last,seenAt,cooldown,jumpAt;
    private int consumed=-1,episode;
    private DoorPoint previous;
    public static string Gate(CornerChaseFrame f,CornerChaseConfig c,bool starting)
    {
        var b=f.Combat;
        if(!c.Enabled||!b.Enabled||!b.Pmc||!b.Authority||!b.Active||b.Ghost)return "chase-out-of-scope";
        if(b.Action!="RushEnemy")return "chase-native-action-changed";
        if(b.Visible)return "chase-contact-reacquired";
        if(b.Busy||b.Urgent)return "chase-native-priority";
        if(b.Target.Length==0||!Point(b.Known)||!Point(b.Position)
            ||!DoorPolicy.Finite(b.Now)||!DoorPolicy.Finite(f.SeenAt)||f.SeenAt<0||b.Now<f.SeenAt||!DoorPolicy.Finite(f.Speed)||f.Speed<0)return "chase-no-personal-observation";
        if(b.Now-f.SeenAt<c.LostSightMin||b.Now-f.SeenAt>c.ObservationMaxAge)return "chase-observation-window";
        return "";
    }
    public bool Eligible(CornerChaseFrame f,CornerChaseConfig c)=>!Active&&f.Combat.Now>=NextAttempt&&f.ObservationEpisode!=consumed&&Gate(f,c,true).Length==0;
    public bool Begin(CornerChaseFrame f,CornerChaseConfig c,double roll,double styleRoll,int side)
    {
        if(!Eligible(f,c)||!Unit(roll)||!Unit(styleRoll))return false;
        consumed=f.ObservationEpisode;cooldown=c.Cooldown;NextAttempt=f.Combat.Now+cooldown;
        Known=f.Combat.Known;seenAt=f.SeenAt;last=f.Combat.Now;previous=f.Combat.Position;
        Trace=new(){Episode=++episode,ObservationEpisode=consumed,Target=f.Combat.Target,Chance=c.Chances.TryGetValue(f.Combat.Personality,out var chance)?chance:0,
            Roll=roll*100,PathLength=DoorPolicy.Finite(f.PathLength)?Math.Max(0,f.PathLength):0,Side=side,GeometryReason=f.GeometryReason};Snapshot(f);
        Trace.Chance*=f.Weakness>=.4?1.3:f.Weakness<.15?.8:1;
        if(!f.Healthy)Trace.Chance*=.5;
        if(f.AmmoRatio<.6)Trace.Chance*=.6;
        if(roll*100>=Trace.Chance){End("chase-roll-declined",last);return false;}
        if(!DoorPolicy.Finite(f.PathLength)||f.PathLength>c.PathLimit)
        {End("chase-no-real-reachable-corner",last);return false;}
        Trace.Selected=true;Deadline=seenAt+c.ObservationMaxAge;
        bool bold=f.Combat.Personality is "GigaChad" or "Chad" or "Wreckless";
        double prefire=f.PrefireEnabled&&f.Confident&&f.AmmoOk?1.5*f.PrefireChance/60+(last-seenAt<1.5?1.5:0)+(f.AngleHeld?1:0):0;
        double jump=bold&&f.CanJump?(f.JumpChance/30+(f.AngleHeld?1.5:0))*(f.Hurt?.4:1):0;
        double lean=1+(!f.Hurt&&f.AmmoRatio>.6?1:0)+(f.Fleeing?1.5:0)+(f.Combat.Personality is "GigaChad" or "Wreckless"?1:0)+(f.Weakness>=.5?1:0);
        double pie=1+(f.Hurt||f.AmmoRatio<.6?2:0)+(f.Weakness<.2?1.5:0)+(f.Combat.Personality=="Normal"?.5:0);
        double draw=styleRoll*(prefire+jump+lean+pie);
        Trace.Style=draw<prefire?"prefire":draw<prefire+jump?"jump":draw<prefire+jump+lean?"lean-in":"pie";
        Alternative($"Style weights: prefire {prefire:0.##}, jump {jump:0.##}, lean {lean:0.##}, pie {pie:0.##}");
        Alternative(f.Fleeing?"Recent personal movement suggests retreat; direct angle preferred":"No fresh retreat evidence; reveal the angle progressively");
        if(Trace.Style!="jump")Alternative("Jump not selected: context, personality, space or style roll");
        Snapshot(f);return true;
    }
    public bool Observe(CornerChaseFrame f,CornerChaseConfig c)
    {
        if(!Active)return false;
        var b=f.Combat;string gate=Gate(f,c,false);
        if(gate.Length>0){End(gate,b.Now);return false;}
        if(b.Target!=Trace!.Target||f.ObservationEpisode!=Trace.ObservationEpisode||b.Now<last)
        {End("chase-contact-or-callback-expired",b.Now);return false;}
        if(b.Now>=Deadline){End("chase-time-limit",b.Now);return false;}

        if(Trace.JumpAccepted) {
            if(!b.Grounded)Trace.TakeoffObserved=true;
            else if(Trace.TakeoffObserved)Trace.LandingObserved=true;

        }
        var delta=(b.Position-previous).Length;
        if(Trace.Requested){Trace.Travel+=delta;Trace.MovementObserved|=Trace.Travel>=.2;}
        previous=b.Position;last=b.Now;Snapshot(f);return true;
    }
    public void Request(DoorPoint goal){if(!Active)return;Trace!.Requested=true;Trace.Requests++;Trace.State="moving";Trace.Reason="chase-route-requested";Trace.GoalX=goal.X;Trace.GoalY=goal.Y;Trace.GoalZ=goal.Z;}
    public bool MayJump(double now,double forwardSpeed,double distance,bool grounded,bool canJump,double stamina,CornerChaseConfig c)=>Active&&Trace!.Style=="jump"&&!Trace.JumpRequested
        &&DoorPolicy.Finite(now)&&now<Deadline&&DoorPolicy.Finite(forwardSpeed)&&forwardSpeed>=c.JumpMinSpeed
        &&DoorPolicy.Finite(distance)&&distance<=Math.Min(3,Math.Max(1.6,forwardSpeed*.42))
        &&grounded&&canJump;
    public void JumpResult(bool accepted,double now){if(!Active||Trace!.JumpRequested)return;Trace.JumpRequested=true;Trace.JumpAccepted=accepted;Trace.Jumps=accepted?1:0;jumpAt=now;if(!accepted)Fallback("Native jump refused");}
    public void Fallback(string reason){if(!Active)return;Trace!.Style="lean-in";Trace.Reason="chase-walking-fallback";Alternative(reason);}
    public void Alternative(string reason){if(Trace!=null)Trace.Alternatives=Trace.Alternatives.Concat(new[]{reason}).TakeLast(6).ToArray();}
    private void Snapshot(CornerChaseFrame f){var t=Trace!;t.ContactAge=Math.Max(0,f.Combat.Now-seenAt);t.KnownX=Known.X;t.KnownY=Known.Y;t.KnownZ=Known.Z;t.Speed=f.Speed;t.Remaining=Active?Math.Max(0,Deadline-f.Combat.Now):0;t.ValidFor=Math.Min(.2,t.Remaining);}
    public void End(string reason,double now){if(Trace==null||Trace.State=="ended")return;Trace.State="ended";Trace.Reason=reason;Trace.ValidFor=Trace.Remaining=0;if(DoorPolicy.Finite(now))NextAttempt=Math.Max(NextAttempt,now+cooldown);}
    public static bool Point(DoorPoint p)=>DoorPolicy.Finite(p.X)&&DoorPolicy.Finite(p.Y)&&DoorPolicy.Finite(p.Z);
    private static bool Unit(double n)=>DoorPolicy.Finite(n)&&n>=0&&n<1;
    // A real navigation turn is required in addition to the adapter's solid-wall test.
    public static bool Route(DoorPoint start,DoorPoint end,DoorPoint[] points,bool complete,double limit,out double length)
    {
        length=0;if(!complete||points.Length<2||!Point(start)||!Point(end)||points.Any(p=>!Point(p))
            )return false;
        for(int i=1;i<points.Length;i++){length+=(points[i]-points[i-1]).Length;}
        return DoorPolicy.Finite(limit)&&length>.1&&length<=limit;
    }
}
