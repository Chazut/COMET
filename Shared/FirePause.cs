using System;

namespace Comet.Core;

public sealed class FirePauseConfig
{
    public bool Enabled { get; set; } = true;
    public double Chance { get; set; } = 45;
    public double MinDistance { get; set; } = 8;
    public int StepsBeforePause { get; set; } = 2;
    public double AutomaticMin { get; set; } = .2;
    public double AutomaticMax { get; set; } = .3;
    public double SingleMin { get; set; } = .3;
    public double SingleMax { get; set; } = .45;
    public double BrakeLimit { get; set; } = .2;
    public double HitDelay { get; set; } = 1;
    public void Validate()
    {
        void Range(double v,double min,double max,string name) {
            if(!DoorPolicy.Finite(v)||v<min||v>max)throw new ArgumentException("Firing pause: invalid "+name);
        }
        Range(Chance,0,100,nameof(Chance));Range(MinDistance,3,40,nameof(MinDistance));
        Range(StepsBeforePause,2,6,nameof(StepsBeforePause));
        Range(AutomaticMin,.1,.5,nameof(AutomaticMin));Range(AutomaticMax,.1,.5,nameof(AutomaticMax));
        Range(SingleMin,.1,.6,nameof(SingleMin));Range(SingleMax,.1,.6,nameof(SingleMax));
        Range(BrakeLimit,.1,.3,nameof(BrakeLimit));Range(HitDelay,.5,5,nameof(HitDelay));
        if(AutomaticMin>AutomaticMax||SingleMin>SingleMax)throw new ArgumentException("Firing pause: reversed duration range.");
    }
}

public sealed class FirePauseFrame
{
    public double Now, SequenceDeadline, Distance, SinceHit, Speed, ContactAge;
    public int CompletedSteps;
    public string Target="",Action="StandAndShoot",Weapon="",Mode="automatic",Gate="";
    public DoorPoint Known;
    public bool CanFire, RouteFree;
}

public sealed class FirePauseTrace
{
    public string State="waiting",Reason="pause-selected",Target="",Action="",Weapon="",Mode="",Source="personal-vision";
    public int Episode,CompletedSteps,Shots,StationaryShots;
    public double Chance,Roll,Duration,Remaining,SequenceRemaining,BrakeElapsed,Speed,ContactAge,KnownX,KnownY,KnownZ,ValidFor;
    public bool Selected,StopRequested,StopObserved;
    public string[] Alternatives=Array.Empty<string>();
}

// One attempt inside an existing M01 sequence. Never owns shooting or extends the parent deadline.
public sealed class FirePause
{
    public FirePauseTrace? Trace { get; private set; }
    public bool Pausing=>Trace?.State is "braking" or "settled";
    public bool Waiting=>Trace?.State=="waiting";
    private double durationRoll,requestedAt,settledAt,last,deadline,sequenceDeadline;
    private const double StationarySpeed=.12, StableTime=.05, MaxGap=.1;
    private double stableSince=-1;
    public void Begin(int episode,string target,double now,double roll,double durationSample,FirePauseConfig c)
    {
        if(!DoorPolicy.Finite(now)||!Unit(roll)||!Unit(durationSample))throw new ArgumentException("Firing pause: invalid sample.");
        durationRoll=durationSample;last=now;stableSince=-1;
        bool selected=c.Enabled&&roll*100<c.Chance;
        Trace=new(){Episode=episode,Target=target,Chance=c.Chance,Roll=roll*100,Selected=selected,
            State=selected?"waiting":"ended",Reason=!c.Enabled?"pause-disabled":selected?"pause-selected":"pause-roll-declined"};
    }
    public bool TryStart(FirePauseFrame f,FirePauseConfig c)
    {
        if(!Waiting)return false;
        string why=Gate(f,c);
        if(why!="pause-invalid-observation")Update(f);
        if(why.Length==0&&f.CompletedSteps<c.StepsBeforePause)why="pause-waiting-for-two-steps";
        if(why.Length>0){Trace!.Reason=why;return false;}
        double min=f.Mode=="single"?c.SingleMin:c.AutomaticMin,max=f.Mode=="single"?c.SingleMax:c.AutomaticMax;
        double duration=min+(max-min)*durationRoll;
        if(f.SequenceDeadline-f.Now<c.BrakeLimit+duration){End("pause-insufficient-sequence-time");return false;}
        Trace!.State="braking";Trace.Reason="pause-stop-planned";Trace.Duration=Trace.Remaining=duration;
        Trace.Weapon=f.Weapon;Trace.Mode=f.Mode;requestedAt=last=f.Now;sequenceDeadline=f.SequenceDeadline;
        deadline=f.Now+c.BrakeLimit;stableSince=-1;Trace.ValidFor=Math.Min(MaxGap,deadline-f.Now);return true;
    }
    public void ConfirmRequest(){if(Pausing){Trace!.StopRequested=true;Trace.Reason="pause-stop-requested";}}
    public bool Observe(FirePauseFrame f,FirePauseConfig c)
    {
        if(!Pausing)return false;
        string why=Gate(f,c);
        if(why.Length==0&&!f.RouteFree)why="pause-route-replaced";
        if(why.Length==0&&(f.Weapon!=Trace!.Weapon||f.Mode!=Trace.Mode))why="pause-weapon-changed";
        if(why.Length==0&&(f.Now<last||f.Now-last>MaxGap+.000001))why="pause-observation-gap";
        if(why.Length==0&&f.Now>=sequenceDeadline)why="pause-sequence-ended";
        if(why.Length>0){End(why);return false;}
        if(f.Now==last)return true; // Several callbacks in one rendered frame are not new evidence.
        last=f.Now;Update(f);
        Trace!.BrakeElapsed=Math.Max(0,(Trace.State=="settled"?settledAt:f.Now)-requestedAt);
        if(!Trace.StopRequested){End("pause-request-unconfirmed");return false;}
        if(Trace.State=="braking")
        {
            if(f.Speed<=StationarySpeed){if(stableSince<0)stableSince=f.Now;}else stableSince=-1;
            if(f.Now>deadline+.000001){End("pause-brake-timeout");return false;}
            if(stableSince>=0&&f.Now-stableSince>=StableTime-.000001)
            {
                Trace.State="settled";Trace.Reason="pause-stop-observed";Trace.StopObserved=true;
                settledAt=f.Now;deadline=f.Now+Trace.Duration;
            }
            else if(f.Now>=deadline){End("pause-brake-timeout");return false;}
        }
        else if(f.Speed>StationarySpeed){End("pause-motion-resumed");return false;}
        if(Trace.State=="settled")Trace.Remaining=Math.Max(0,deadline-f.Now);
        if(f.Now>=deadline){End(Trace.Shots>0?"pause-completed":"pause-completed-without-shot",true);return false;}
        Trace.ValidFor=Math.Min(MaxGap,Math.Min(deadline,sequenceDeadline)-f.Now);return true;
    }
    public void Shot(double now)
    {
        if(!Pausing||!Trace!.StopRequested||now<last||now-last>MaxGap||now>=deadline||now>=sequenceDeadline)return;
        Trace.Shots++;if(Trace.State=="settled"&&Trace.Speed<=StationarySpeed)Trace.StationaryShots++;
    }
    private string Gate(FirePauseFrame f,FirePauseConfig c)
    {
        if(!c.Enabled)return "pause-disabled";
        if(f.Gate.Length>0)return f.Gate;
        if(!DoorPolicy.Finite(f.Now)||!DoorPolicy.Finite(f.SequenceDeadline)||!DoorPolicy.Finite(f.Distance)||!DoorPolicy.Finite(f.SinceHit)
            ||!DoorPolicy.Finite(f.Speed)||f.Speed<0||!DoorPolicy.Finite(f.ContactAge)||f.ContactAge<0
            ||!DoorPolicy.Finite(f.Known.X)||!DoorPolicy.Finite(f.Known.Y)||!DoorPolicy.Finite(f.Known.Z))return "pause-invalid-observation";
        if(f.Target!=Trace!.Target)return "pause-contact-replaced";
        if(f.Action!="StandAndShoot"&&f.Action!="DogFight")return "pause-native-action-changed";
        if(f.Distance<c.MinDistance)return "pause-too-close";
        if(f.SinceHit<c.HitDelay)return "pause-recent-hit";
        if(!f.CanFire||f.Weapon.Length==0||f.Mode!="automatic"&&f.Mode!="single")return "pause-weapon-not-ready";
        return "";
    }
    private void Update(FirePauseFrame f)
    {
        var t=Trace!;t.Action=f.Action;t.CompletedSteps=f.CompletedSteps;t.SequenceRemaining=Math.Max(0,f.SequenceDeadline-f.Now);
        t.Speed=f.Speed;t.ContactAge=f.ContactAge;t.KnownX=f.Known.X;t.KnownY=f.Known.Y;t.KnownZ=f.Known.Z;
    }
    public void End(string why,bool completed=false)
    {
        if(Trace==null||Trace.State is "ended" or "resumed")return;
        Trace.State=completed?"resumed":"ended";Trace.Reason=why;Trace.ValidFor=Trace.Remaining=0;
        Trace.Alternatives=new[]{"Keep moving: "+why,"Shooting stays under SAIN control"};
    }
    private static bool Unit(double x)=>DoorPolicy.Finite(x)&&x>=0&&x<1;
}
