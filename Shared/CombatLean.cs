using System;
using System.Linq;

namespace Comet.Core;

public sealed class CombatLeanConfig
{
    public bool Enabled { get; set; } = true;
    public double Chance { get; set; } = 60;
    public double MinDistance { get; set; } = 3;
    public double MaxDistance { get; set; } = 25;
    public double HoldMin { get; set; } = .35;
    public double HoldMax { get; set; } = 1;
    public double BurstIntervalMin { get; set; } = .25;
    public double BurstIntervalMax { get; set; } = .4;
    public int BurstChangesMin { get; set; } = 2;
    public int BurstChangesMax { get; set; } = 3;
    public double CloseDistance { get; set; } = 7;
    public double BurstChanceClose { get; set; } = 45;
    public double BurstChanceFar { get; set; } = 15;
    public double SequenceLimit { get; set; } = 3;
    public double CooldownMin { get; set; } = 2;
    public double CooldownMax { get; set; } = 4;
    public void Validate()
    {
        void Range(double v,double a,double b,string n) { if(!DoorPolicy.Finite(v)||v<a||v>b)throw new ArgumentException("Combat lean: invalid "+n); }
        Range(Chance,0,100,nameof(Chance));Range(MinDistance,1.5,15,nameof(MinDistance));Range(MaxDistance,5,40,nameof(MaxDistance));
        Range(HoldMin,.35,2,nameof(HoldMin));Range(HoldMax,.35,2,nameof(HoldMax));
        Range(BurstIntervalMin,.25,.8,nameof(BurstIntervalMin));Range(BurstIntervalMax,.25,.8,nameof(BurstIntervalMax));
        Range(BurstChangesMin,2,3,nameof(BurstChangesMin));Range(BurstChangesMax,2,3,nameof(BurstChangesMax));
        Range(CloseDistance,3,15,nameof(CloseDistance));Range(BurstChanceClose,0,100,nameof(BurstChanceClose));Range(BurstChanceFar,0,100,nameof(BurstChanceFar));
        Range(SequenceLimit,1.5,5,nameof(SequenceLimit));Range(CooldownMin,1,15,nameof(CooldownMin));Range(CooldownMax,1,15,nameof(CooldownMax));
        if(MinDistance>=MaxDistance||HoldMin>HoldMax||BurstIntervalMin>BurstIntervalMax||BurstChangesMin>BurstChangesMax
            ||CooldownMin>CooldownMax||HoldMax>SequenceLimit)throw new ArgumentException("Combat lean: inconsistent ranges.");
    }
}

public sealed class CombatLeanFrame
{
    public CombatStepFrame Combat=new();
    public bool NativeLeanEnabled=true,NativeLeanOwned,CornerPeek,FirePause;
    public bool GeometryFresh,LeftSafe,RightSafe;
    // Own head bone relative to the bot's root, not the enemy's head or aim.
    public double HeadLateral,SmoothedTilt;
}
public sealed class CombatLeanTrace
{
    public string State="hold",Reason="lean-selected",Target="",Action="",Source="personal-vision";
    public int Episode,Side,Requests,BurstChanges,BurstLimit;
    public double Chance,Roll,BurstChance,BurstRoll,Remaining,PhaseRemaining,ValidFor,ContactAge,KnownX,KnownY,KnownZ;
    public double SmoothedTilt,HeadLateral,HeadTravel;
    public bool Selected,Requested,TiltObserved,HeadMotionObserved,BurstAttempted,FirePause,LeftSafe,RightSafe;
    public string[] Alternatives=Array.Empty<string>();
}

// Evasion by native lean input. Cover is not a prerequisite and no opponent accuracy is changed.
public sealed class CombatLean
{
    public CombatLeanTrace? Trace { get; private set; }
    public bool Active=>Trace!=null&&Trace.State!="ended"&&Trace.Selected;
    public double NextAttempt { get; private set; }
    public double Deadline { get; private set; }
    private double last,started,phaseEnd,commandAt,cooldown,headMin,headMax,commandHead,commandTilt;
    private bool commandObserved;
    private int episode;
    public static string Gate(CombatLeanFrame f,CombatLeanConfig c)
    {
        var b=f.Combat;
        if(!c.Enabled||!b.Enabled||!b.Pmc||!b.Authority||!b.Active||b.Ghost)return "lean-out-of-scope";
        if(b.Action!="StandAndShoot"&&b.Action!="DogFight")return "lean-native-action-changed";
        if(!b.Visible||b.Source!="personal-vision"||string.IsNullOrEmpty(b.Target))return "lean-no-personal-vision";
        if(!Finite(b.Now)||b.Now<0||!Finite(b.Distance)||b.Distance<c.MinDistance||b.Distance>c.MaxDistance
            ||!Finite(b.ContactAge)||b.ContactAge<0||!Point(b.Known)||!Finite(f.HeadLateral)||!Finite(f.SmoothedTilt))return "lean-invalid-observation";
        if(!f.NativeLeanEnabled||f.NativeLeanOwned)return "lean-native-lean-priority";
        if(f.CornerPeek)return "lean-preserve-corner-peek";
        if(b.Busy||b.Urgent||!b.Ready)return "lean-native-priority";
        if(!b.Grounded||b.Sprinting||b.Immobile)return "lean-mobility-unavailable";
        if(!f.GeometryFresh||!f.LeftSafe&&!f.RightSafe)return "lean-no-safe-head-sweep";
        return "";
    }
    public bool Begin(CombatLeanFrame f,CombatLeanConfig c,double roll,double sideRoll,double burstRoll,double countRoll,double timingRoll,double cooldownRoll)
    {
        if(Active||f.FirePause||!Finite(f.Combat.Now)||f.Combat.Now<NextAttempt||Gate(f,c).Length>0
            ||!Unit(roll)||!Unit(sideRoll)||!Unit(burstRoll)||!Unit(countRoll)||!Unit(timingRoll)||!Unit(cooldownRoll))return false;
        double now=f.Combat.Now;cooldown=Mix(c.CooldownMin,c.CooldownMax,cooldownRoll);NextAttempt=now+cooldown;
        Trace=new(){Episode=++episode,Chance=c.Chance,Roll=roll*100,Target=f.Combat.Target,Action=f.Combat.Action,
            BurstChance=f.Combat.Distance<=c.CloseDistance?c.BurstChanceClose:c.BurstChanceFar,BurstRoll=burstRoll*100,
            BurstLimit=c.BurstChangesMin+(int)(countRoll*(c.BurstChangesMax-c.BurstChangesMin+1))};
        if(roll*100>=c.Chance){End("lean-roll-declined",now);return false;}
        Trace.Selected=true;last=started=now;Deadline=now+c.SequenceLimit;headMin=headMax=f.HeadLateral;
        int preferred=sideRoll<.5?-1:1;
        if(!Safe(preferred,f)){Alternative("Preferred side blocked; use the other side");preferred=-preferred;}
        Command(preferred,now,Mix(c.HoldMin,c.HoldMax,timingRoll),f);
        Snapshot(f);return true;
    }
    public bool Observe(CombatLeanFrame f,CombatLeanConfig c,double timingRoll,double sideRoll)
    {
        if(!Active)return false;
        double now=f.Combat.Now;string why=Gate(f,c);
        if(why.Length>0){End(why,now);return false;}
        if(now<last||now-last>.3||!Unit(timingRoll)||!Unit(sideRoll)){End("lean-observation-expired",now);return false;}
        if(f.Combat.Target!=Trace!.Target||f.Combat.Action!=Trace.Action){End("lean-contact-or-action-changed",now);return false;}
        if(now>=Deadline){End("lean-sequence-completed",now);return false;}
        if(!Safe(Trace.Side,f)){End("lean-current-side-obstructed",now);return false;}
        last=now;
        // A desired side is never reported as a physical head displacement.
        headMin=Math.Min(headMin,f.HeadLateral);headMax=Math.Max(headMax,f.HeadLateral);
        Trace.HeadTravel=headMax-headMin;
        if(Math.Abs(f.SmoothedTilt-commandTilt)>=.5&&Math.Abs(f.HeadLateral-commandHead)>=.02)Trace.HeadMotionObserved=true;
        if(f.SmoothedTilt*Trace.Side>=2.5) {commandObserved=true;Trace.TiltObserved=true;}
        if(!commandObserved&&now-commandAt>=1){End("lean-animation-not-observed",now);return false;}
        if(!Trace.HeadMotionObserved&&now-started>=1){End("lean-head-motion-not-observed",now);return false;}
        if(f.FirePause) {
            if(Trace.State=="burst") {Trace.State="hold";Alternative("Burst stopped to keep the M02 firing pause stable");}
            Trace.Reason="lean-hold-during-fire-pause";
            // Keep this side, without extending the sequence deadline.
            phaseEnd=Math.Max(phaseEnd,now+.1);Snapshot(f);return true;
        }
        if(now>=phaseEnd&&commandObserved) {
            if(Trace.State=="burst") {
                if(Trace.BurstChanges<Trace.BurstLimit&&Safe(-Trace.Side,f)) {
                    Trace.BurstChanges++;Command(-Trace.Side,now,Mix(c.BurstIntervalMin,c.BurstIntervalMax,timingRoll),f);
                }else {Trace.State="hold";Trace.Reason="lean-burst-finished";phaseEnd=now+Mix(c.HoldMin,c.HoldMax,timingRoll);}
            } else if(!Trace.BurstAttempted) {
                Trace.BurstAttempted=true;
                if(Trace.BurstRoll<Trace.BurstChance&&f.LeftSafe&&f.RightSafe) {
                    Trace.State="burst";Trace.BurstChanges=1;Command(-Trace.Side,now,Mix(c.BurstIntervalMin,c.BurstIntervalMax,timingRoll),f);
                } else {
                    Alternative(Trace.BurstRoll>=Trace.BurstChance?"Burst roll declined; keep a held lean":"Opposite side obstructed; no Q/E burst");
                    phaseEnd=now+Mix(c.HoldMin,c.HoldMax,timingRoll);Trace.Reason="lean-held-side";
                }
            } else {
                // Slow ordinary changes are distinct from the single short burst.
                if(sideRoll<.3&&Safe(-Trace.Side,f))Command(-Trace.Side,now,Mix(c.HoldMin,c.HoldMax,timingRoll),f);
                else {phaseEnd=now+Mix(c.HoldMin,c.HoldMax,timingRoll);Trace.Reason="lean-held-side";}
            }
        }
        Snapshot(f);return true;
    }
    private void Command(int side,double now,double duration,CombatLeanFrame f)
    {
        Trace!.Side=side;Trace.Requested=true;Trace.Requests++;Trace.Reason=Trace.State=="burst"?"lean-short-alternation":"lean-held-side";
        commandAt=now;commandHead=f.HeadLateral;commandTilt=f.SmoothedTilt;commandObserved=false;phaseEnd=now+duration;
    }
    private void Snapshot(CombatLeanFrame f)
    {
        var t=Trace!;var b=f.Combat;t.Remaining=Math.Max(0,Deadline-b.Now);t.PhaseRemaining=Math.Min(t.Remaining,Math.Max(0,phaseEnd-b.Now));
        t.ValidFor=Math.Min(.2,t.Remaining);t.ContactAge=b.ContactAge;t.KnownX=b.Known.X;t.KnownY=b.Known.Y;t.KnownZ=b.Known.Z;
        t.SmoothedTilt=f.SmoothedTilt;t.HeadLateral=f.HeadLateral;t.FirePause=f.FirePause;t.LeftSafe=f.LeftSafe;t.RightSafe=f.RightSafe;
    }
    public void End(string why,double now)
    {
        if(Trace==null||Trace.State=="ended")return;
        Trace.State="ended";Trace.Reason=why;Trace.ValidFor=Trace.Remaining=Trace.PhaseRemaining=0;Trace.FirePause=false;
        if(Finite(now))NextAttempt=Math.Max(NextAttempt,now+cooldown);
    }
    public void Alternative(string why){if(Trace!=null)Trace.Alternatives=Trace.Alternatives.Concat(new[]{why}).TakeLast(6).ToArray();}
    private static bool Safe(int side,CombatLeanFrame f)=>side<0?f.LeftSafe:f.RightSafe;
    private static bool Point(DoorPoint p)=>Finite(p.X)&&Finite(p.Y)&&Finite(p.Z);
    private static bool Unit(double x)=>Finite(x)&&x>=0&&x<1;
    private static bool Finite(double x)=>DoorPolicy.Finite(x);
    private static double Mix(double a,double b,double x)=>a+(b-a)*x;
}
