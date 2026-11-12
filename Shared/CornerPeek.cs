using System;
using System.Linq;

namespace Comet.Core;

public sealed class CornerPeekConfig
{
    public bool Enabled { get; set; } = true;
    public double Chance { get; set; } = 50;
    public double MinDistance { get; set; } = 5;
    public double MaxDistance { get; set; } = 25;
    public double MaxStep { get; set; } = 1;
    public double ExposedMin { get; set; } = .35;
    public double ExposedMax { get; set; } = .6;
    public double HiddenMin { get; set; } = .25;
    public double HiddenMax { get; set; } = .45;
    public int MaxPeeks { get; set; } = 2;
    public double SequenceLimit { get; set; } = 4;
    public double CooldownMin { get; set; } = 4;
    public double CooldownMax { get; set; } = 6;
    public double SightGrace { get; set; } = 1.2;
    public double JumpShare { get; set; } = 25;
    public double RunShare { get; set; } = 15;
    public void Validate()
    {
        void Range(double x,double a,double b,string n){if(!DoorPolicy.Finite(x)||x<a||x>b)throw new ArgumentException("Corner peek: invalid "+n);}
        Range(Chance,0,100,nameof(Chance));Range(MinDistance,3,20,nameof(MinDistance));Range(MaxDistance,5,40,nameof(MaxDistance));
        Range(MaxStep,.5,1,nameof(MaxStep));Range(ExposedMin,.2,1,nameof(ExposedMin));Range(ExposedMax,.2,1,nameof(ExposedMax));
        Range(HiddenMin,.1,.6,nameof(HiddenMin));Range(HiddenMax,.1,.6,nameof(HiddenMax));Range(MaxPeeks,1,2,nameof(MaxPeeks));
        Range(SequenceLimit,2,5,nameof(SequenceLimit));Range(CooldownMin,2,15,nameof(CooldownMin));Range(CooldownMax,2,15,nameof(CooldownMax));
        Range(SightGrace,.5,1.2,nameof(SightGrace));Range(JumpShare,0,100,nameof(JumpShare));Range(RunShare,0,100,nameof(RunShare));
        if(MinDistance>=MaxDistance||ExposedMin>ExposedMax||HiddenMin>HiddenMax||CooldownMin>CooldownMax||JumpShare+RunShare>100
            ||HiddenMax>=SightGrace)throw new ArgumentException("Corner peek: inconsistent ranges or style shares.");
    }
}

public sealed class CornerPeekFrame
{
    public CombatStepFrame Combat=new();
    public bool GeometryFresh, LaneSafe, AtGoal, Hidden, Open, Grounded=true, RouteValid=true;
    public double Speed, SinceHit=10;
}
public sealed class CornerPeekTrace
{
    public string State="planning",Reason="corner-selected",Target="",Action="",Style="step",RequestedStyle="step",Source="personal-vision";
    public int Episode,Peeks,Shots,ExposedShots,Jumps;
    public double Chance,Roll,StyleRoll,Remaining,PhaseRemaining,ValidFor,ContactAge,KnownX,KnownY,KnownZ;
    public double ExposedX,ExposedY,ExposedZ,HiddenX,HiddenY,HiddenZ,GoalX,GoalY,GoalZ,Travel;
    public bool Selected,Requested,Arrived,HiddenObserved,OpenObserved,AirborneObserved,LandedObserved,Grace;
    public string[] Alternatives=Array.Empty<string>();
}

// M03 replaces a successfully rolled M01 opportunity; one bounded sequence, never an unbounded A/D loop.
public sealed class CornerPeek
{
    public readonly PreparationWait Preparation=new();

    public CornerPeekTrace? Trace { get; private set; }
    public bool Active=>Trace!=null&&Trace.Selected&&Trace.State!="ended";
    public bool Moving=>Trace?.State is "retreat" or "peek" or "crossing";
    public bool WantsRoute=>Trace?.State is "retreat-pending" or "peek-pending" or "crossing-pending";
    public bool Holding=>Trace?.State is "exposed" or "hidden";
    public double Deadline { get; private set; }
    public double NextAttempt { get; private set; }
    public DoorPoint Exposed { get; private set; }
    public DoorPoint HiddenPoint { get; private set; }
    public DoorPoint Goal { get; private set; }
    public DoorPoint Known { get; private set; }
    private double last,started,lastVisible,phaseEnd,cooldown,progressAt;
    private DoorPoint previous,progress;
    private double exposedDuration,hiddenDuration;
    private DoorPoint selectionOrigin;
    private bool originSettled;
    public bool Begin(CombatStepFrame f,int episode,CornerPeekConfig c,double roll,double styleRoll,double durationRoll,double cooldownRoll,bool bold)
    {
        if(Active||f.Now<NextAttempt||!Unit(roll)||!Unit(styleRoll)||!Unit(durationRoll)||!Unit(cooldownRoll)||!Finite(f.Now))return false;
        cooldown=Mix(c.CooldownMin,c.CooldownMax,cooldownRoll);NextAttempt=f.Now+cooldown;
        Trace=new(){Episode=episode,Target=f.Target,Action=f.Action,Chance=c.Chance,Roll=roll*100,StyleRoll=styleRoll*100};
        if(!c.Enabled||roll*100>=c.Chance){End(!c.Enabled?"corner-disabled":"corner-roll-declined",f.Now);return false;}
        if(Scope(f).Length>0||!f.Visible||f.Source!="personal-vision"||!Point(f.Known)||!Point(f.Position)||!Distance(f,c)) {End("corner-no-visible-opportunity",f.Now);return false;}
        Trace.Selected=true;Trace.Style=Trace.RequestedStyle=bold?(styleRoll*100<c.JumpShare?"jump":styleRoll*100<c.JumpShare+c.RunShare?"run-by":"step"):"step";
        Preparation.Clear();Exposed=previous=progress=selectionOrigin=f.Position;originSettled=false;Known=f.Known;last=started=lastVisible=progressAt=f.Now;Deadline=f.Now+c.SequenceLimit;
        exposedDuration=Mix(c.ExposedMin,c.ExposedMax,durationRoll);hiddenDuration=Mix(c.HiddenMin,c.HiddenMax,1-durationRoll);
        Trace.ExposedX=Exposed.X;Trace.ExposedY=Exposed.Y;Trace.ExposedZ=Exposed.Z;Update(f.Now);return true;
    }
    // Stop releases the path before physical inertia ends. Capture the firing origin once,
    // before probing geometry, without extending the planning or sequence deadlines.
    public bool SettleOrigin(DoorPoint position,double speed,bool grounded,double now)
    {
        if(!Active||Trace!.State!="planning")return false;
        if(!Point(position)||!Finite(speed)||speed<0||!Finite(now)||now<last)
        {End("corner-invalid-planning-origin",now);return false;}
        if((position-selectionOrigin).Length>1||Math.Abs(position.Y-selectionOrigin.Y)>.5)
        {End("corner-planning-origin-displaced",now);return false;}
        if(now-started>=2+Preparation.Seconds){End("corner-planning-timeout",now);return false;}
        if(originSettled)
        {
            if((position-Exposed).Length>.4||speed>.7||!grounded)
            {End("corner-planning-origin-moved",now);return false;}
            return true;
        }
        if(speed>.7||!grounded){Trace.Reason="corner-waiting-for-stop";return false;}
        originSettled=true;Exposed=position;
        Trace.ExposedX=position.X;Trace.ExposedY=position.Y;Trace.ExposedZ=position.Z;
        Trace.Reason="corner-planning-origin-settled";return true;
    }
    public bool Plan(DoorPoint hidden,string style,double now)
    {
        if(!Active||Trace!.State!="planning"||!Point(hidden)||style!="step"&&style!="jump"&&style!="run-by"||!Finite(now)||now<last||now>=Deadline)return false;
        HiddenPoint=hidden;Trace.HiddenX=hidden.X;Trace.HiddenY=hidden.Y;Trace.HiddenZ=hidden.Z;
        Trace.Style=style;Trace.State="exposed";Trace.Reason="corner-plan-validated";phaseEnd=now+exposedDuration;last=now;Update(now);return true;
    }
    public void Alternative(string reason)
    {if(Trace!=null)Trace.Alternatives=Trace.Alternatives.Concat(new[]{reason}).TakeLast(6).ToArray();}
    public bool Request(double now)
    {
        if(!Active||!WantsRoute||!Finite(now)||now<last||now>=Deadline)return false;
        string phase=Trace!.State;
        Goal=phase=="peek-pending"?Exposed:HiddenPoint;
        Trace.State=phase=="peek-pending"?"peek":phase=="crossing-pending"?"crossing":"retreat";
        Trace.Reason="corner-route-requested";Trace.Requested=true;Trace.Arrived=false;
        Trace.GoalX=Goal.X;Trace.GoalY=Goal.Y;Trace.GoalZ=Goal.Z;progress=previous;progressAt=last=now;Update(now);return true;
    }
    public bool Observe(CornerPeekFrame f,CornerPeekConfig c,string gate)
    {
        if(!Active)return false;
        var b=f.Combat;double now=b.Now;
        if(gate.Length==0)gate=Scope(b,Trace!.State=="peek"&&Trace.Jumps==1,Trace.State=="crossing");
        if(gate.Length>0){End(gate,now);return false;}
        if(!c.Enabled||!Finite(now)||now<last||now-last>.4||!Point(b.Position)||!Point(b.Known)||!Finite(f.Speed)||f.Speed<0||!Finite(f.SinceHit))
        {End("corner-invalid-or-stale-observation",now);return false;}
        if(b.Target!=Trace!.Target||b.Action!=Trace.Action||!f.RouteValid){End("corner-owner-or-contact-changed",now);return false;}
        if(now>=Deadline){End("corner-sequence-time-limit",now);return false;}
        if(!Distance(b,c)||f.SinceHit<1){End("corner-range-or-recent-hit",now);return false;}
        if(b.Visible){lastVisible=now;Known=b.Known;}
        else if(!MayKeep(now,c)){End("corner-personal-contact-expired",now);return false;}
        double delta=(b.Position-previous).Length;
        if(delta>Math.Max(.3,(now-last)*9)){End("corner-position-discontinuity",now);return false;}
        if(Moving)Trace.Travel+=delta;
        if((b.Position-progress).Length>=.06){progress=b.Position;progressAt=now;}
        previous=b.Position;last=now;Trace.Grace=!b.Visible;
        if(Trace.State=="planning") {if(now-started>=2+Preparation.Seconds)End("corner-planning-timeout",now);Update(now);return Active;}
        if(!f.GeometryFresh||!f.LaneSafe){End("corner-geometry-invalidated",now);return false;}
        if(Holding&&((b.Position-(Trace.State=="exposed"?Exposed:HiddenPoint)).Length>.4||f.Speed>.7))
        {End("corner-hold-position-lost",now);return false;}
        if(Trace.State=="peek"&&Trace.Jumps>0&&!f.Grounded)Trace.AirborneObserved=true;
        if(Trace.AirborneObserved&&f.Grounded)Trace.LandedObserved=true;
        if(Moving)
        {
            if(f.AtGoal&&f.Grounded&&f.Speed<=.7)
            {
                Trace.Arrived=true;
                if(Trace.State=="peek") {
                    if(!f.Open||!b.Visible){End("corner-return-no-visual-acquisition",now);return false;}
                    Trace.Peeks++;Trace.OpenObserved=true;Trace.State="exposed";Trace.Reason="corner-exposure-observed";phaseEnd=now+exposedDuration;
                } else {
                    if(!f.Hidden){End("corner-arrival-not-protected",now);return false;}
                    Trace.HiddenObserved=true;
                    if(Trace.State=="crossing"){End("corner-crossing-completed",now);return false;}
                    if(Trace.Peeks>=c.MaxPeeks){End("corner-peeks-completed",now);return false;}
                    Trace.State="hidden";Trace.Reason="corner-cover-observed";phaseEnd=now+hiddenDuration;
                }
            }
            else if(now-progressAt>=.8){End("corner-movement-stalled",now);return false;}
        }
        else if(Trace.State=="exposed") {
            if(!f.Open||!b.Visible){End("corner-exposed-angle-lost",now);return false;}
            Trace.OpenObserved=true;
            if(now>=phaseEnd){Trace.State=Trace.Style=="run-by"?"crossing-pending":"retreat-pending";Trace.Reason="corner-exposure-window-ended";}
        }
        else if(Trace.State=="hidden") {
            if(!f.Hidden){End("corner-cover-lost",now);return false;}
            if(now>=phaseEnd){Trace.State="peek-pending";Trace.Reason="corner-next-peek";}
        }
        Update(now);return Active;
    }
    public bool MayKeep(double now,CornerPeekConfig c)=>Active&&Trace!.State!="planning"&&Trace.State!="exposed"&&Finite(now)
        &&now>=last&&now-last<=.2&&now-lastVisible<=c.SightGrace&&now<Deadline;
    public bool MayJump(double now,double forwardSpeed,double remaining,bool grounded,bool canJump,double stamina)=>Active&&Trace!.Style=="jump"
        &&Trace.State=="peek"&&Trace.Jumps==0&&grounded&&canJump&&Finite(stamina)&&stamina>=30&&Finite(forwardSpeed)&&forwardSpeed>=1.1
        &&remaining>=.35&&Finite(now)&&now>=last&&now-last<=.1&&now<Deadline-.5;
    public void JumpAccepted(bool accepted){if(Active&&Trace!.State=="peek"&&Trace.Jumps==0){if(accepted){Trace.Jumps=1;Trace.Reason="corner-jump-requested";}else Fallback("jump refused by native mover");}}
    public void Fallback(string why){if(Trace==null)return;Alternative(Trace.Style+": "+why+"; using lateral step");Trace.Style="step";}
    public void Shot(double now,bool canShoot)
    {
        if(!Active||!Trace!.Requested&&!Trace.OpenObserved||!Finite(now)||now<last||now-last>.1||now>=Deadline)return;
        Trace.Shots++;if(canShoot&&Trace.State=="exposed"&&Trace.OpenObserved)Trace.ExposedShots++;
    }
    public void End(string why,double now)
    {if(Trace==null||Trace.State=="ended")return;Trace.State="ended";Trace.Reason=why;Trace.ValidFor=Trace.Remaining=Trace.PhaseRemaining=0;Trace.Grace=false;
        if(Finite(now))NextAttempt=Math.Max(NextAttempt,now+cooldown);}
    private void Update(double now)
    {
        if(!Active)return;var t=Trace!;t.Remaining=Math.Max(0,Deadline-now);t.PhaseRemaining=Holding?Math.Max(0,phaseEnd-now):0;
        t.ValidFor=Math.Min(.2,t.Remaining);t.ContactAge=Math.Max(0,now-lastVisible);t.KnownX=Known.X;t.KnownY=Known.Y;t.KnownZ=Known.Z;
    }
    public static bool Lane(DoorPoint start,DoorPoint end,DoorPoint p,bool airborne=false)
    {
        if(!Point(start)||!Point(end)||!Point(p))return false;var d=end-start;double len=d.X*d.X+d.Z*d.Z;
        if(len<.01)return (p-start).Length<=.2;
        double u=((p.X-start.X)*d.X+(p.Z-start.Z)*d.Z)/len;
        double x=start.X+u*d.X,z=start.Z+u*d.Z;
        return u>=-.15&&u<=1.15&&Math.Sqrt((p.X-x)*(p.X-x)+(p.Z-z)*(p.Z-z))<=.25
            &&p.Y>=Math.Min(start.Y,end.Y)-.3&&p.Y<=Math.Max(start.Y,end.Y)+(airborne?1.2:.3);
    }
    public static string Scope(CombatStepFrame f,bool ownedAir=false,bool ownedSprint=false)
    {
        if(!f.Enabled||!f.Pmc||!f.Authority||!f.Active||f.Ghost)return "corner-out-of-scope";
        if(f.Action!="StandAndShoot"&&f.Action!="DogFight"||!f.NativeRouteValid)return "corner-native-owner-changed";
        if(f.Busy||f.Urgent||!f.Ready||f.Immobile||f.ProtectedCover)return "corner-native-priority";
        if(!f.Grounded&&!ownedAir||f.Sprinting&&!ownedSprint)return "corner-mobility-unavailable";
        return "";
    }
    private static bool Distance(CombatStepFrame f,CornerPeekConfig c)=>Finite(f.Distance)&&f.Distance>=c.MinDistance&&f.Distance<=c.MaxDistance;
    private static bool Unit(double x)=>Finite(x)&&x>=0&&x<1;
    private static bool Point(DoorPoint x)=>Finite(x.X)&&Finite(x.Y)&&Finite(x.Z);
    private static bool Finite(double x)=>DoorPolicy.Finite(x);
    private static double Mix(double a,double b,double x)=>a+(b-a)*x;
}
