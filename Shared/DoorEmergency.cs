using System;

namespace Comet.Core;

public sealed class DoorEmergencyConfig
{
    public bool Enabled { get; set; }=true;
    public double VisibleDistance { get; set; }=10;
    public double SoundAge { get; set; }=1.2;
    public double SoundDoorDistance { get; set; }=7;
    public double MaxPath { get; set; }=5;
    public double Duration { get; set; }=3;
    public double Stabilization { get; set; }=.5;
    public void Validate()
    {
        CometConfig.Range(VisibleDistance,3,20,nameof(VisibleDistance));
        CometConfig.Range(SoundAge,.1,2,nameof(SoundAge));
        CometConfig.Range(SoundDoorDistance,1,10,nameof(SoundDoorDistance));
        CometConfig.Range(MaxPath,1,8,nameof(MaxPath));
        CometConfig.Range(Duration,.5,5,nameof(Duration));
        CometConfig.Range(Stabilization,0,1,nameof(Stabilization));
        if(Stabilization>Duration)throw new ArgumentException("Stabilization exceeds emergency duration.");
    }
}
public static class DoorEmergencyRules
{
    public static bool CanStart(bool enabled,bool scoped,bool priority,bool fake,bool safePreparation,bool committedThrow)
        =>enabled&&scoped&&!priority&&!committedThrow&&(fake||safePreparation);
    public static bool Visible(bool personal,double age,double distance,DoorEmergencyConfig c)=>personal
        &&DoorPolicy.Finite(age)&&age>=0&&age<=.5&&DoorPolicy.Finite(distance)&&distance>=0&&distance<=c.VisibleDistance;
    public static bool Heard(string type,bool personal,bool otherFloor,double age,double distance,DoorEmergencyConfig c)=>personal&&!otherFloor
        &&type is "Sprint" or "Jump" or "Land" or "Door" or "DoorBreach"
        &&DoorPolicy.Finite(age)&&age>=0&&age<=c.SoundAge&&DoorPolicy.Finite(distance)&&distance>=0&&distance<=c.SoundDoorDistance;
    public static string Route(bool complete,bool space,bool allies,int current,int protection,double length,double height,double advance,bool exposedToOther,DoorEmergencyConfig c)
    {
        if(!complete)return "incomplete-route";
        if(!DoorPolicy.Finite(length)||length<=0||length>c.MaxPath)return "path-too-long";
        return "";
    }
}
public sealed class DoorEmergencyTrace
{
    public string Attempt { get; set; }="";
    public string Door { get; set; }="";
    public string Plan { get; set; }="";
    public string Phase { get; set; }="";
    public string Target { get; set; }="";
    public string Evidence { get; set; }="";
    public string State { get; set; }="recovering";
    public string Reason { get; set; }="";
    public string Choice { get; set; }="recover-only";
    public string Hands { get; set; }="";
    public double EvidenceAge { get; set; }
    public double Distance { get; set; }
    public double Remaining { get; set; }
    public double PathLength { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public bool Confirmed { get; set; }
    public bool WeaponReady { get; set; }
    public bool NativeAccepted { get; set; }
    public bool ActionObserved { get; set; }
    public bool MoveAccepted { get; set; }
    public bool MovementObserved { get; set; }
    public bool Arrived { get; set; }
    public bool SafePosition { get; set; }
    public string[] Alternatives { get; set; }=Array.Empty<string>();
    public double ValidFor { get; set; }=1.5;
}
// One reaction per original attempt. A heard cue may become visible danger without resetting time.
public sealed class DoorEmergency
{
    public DoorEmergencyTrace? Trace { get; private set; }
    public bool Active=>Trace!=null&&Trace.State!="ended";
    public double Deadline { get; private set; }
    private string consumed="";
    private double started,last;
    public bool Begin(DoorEmergencyTrace t,double now,DoorEmergencyConfig c)
    {
        if(Active||!c.Enabled||t.Attempt.Length==0||t.Attempt==consumed||!DoorPolicy.Finite(now))return false;
        consumed=t.Attempt;Trace=t;started=last=now;Deadline=now+c.Duration;t.Remaining=c.Duration;
        t.Reason=t.Confirmed?"visible-danger-weapon-unavailable":"heard-approach-cancel-preparation";return true;
    }
    public bool Tick(double now,bool ready,bool priority,DoorEmergencyConfig c)
    {
        if(!Active)return false;var t=Trace!;
        if(!DoorPolicy.Finite(now)||now<last){End("invalid-clock");return false;}
        last=now;t.WeaponReady=ready;t.Remaining=Math.Max(0,Deadline-now);
        if(!c.Enabled||priority)End("native-priority-or-scope");
        else if(now>=Deadline)End("emergency-time-limit");
        else if(ready&&(!t.Confirmed||now-started>=c.Stabilization))End("weapon-ready-native-combat");
        return Active;
    }
    public void Confirm(string target,double age,double distance)
    {if(!Active)return;Trace!.Confirmed=true;Trace.Target=target;Trace.Evidence="personal-vision";Trace.EvidenceAge=age;Trace.Distance=distance;Trace.Reason="visible-danger-weapon-unavailable";}
    public void Accepted(bool ok){if(!Active)return;Trace!.NativeAccepted|=ok;if(!ok)End("native-decision-refused");}
    public void Observe(double moved,double progress,bool complete,bool close)
    {
        if(!Active||!Trace!.NativeAccepted)return;
        Trace.ActionObserved=true;Trace.State="observed";
        Trace.MovementObserved|=Trace.MoveAccepted&&moved>=.15&&progress>=.15;
        Trace.Arrived|=Trace.MoveAccepted&&Trace.MovementObserved&&complete&&close;
    }
    public void End(string why){if(Trace==null)return;Trace.State="ended";Trace.Reason=why;Trace.Remaining=0;}
    public void Clear(){Trace=null;consumed="";Deadline=started=last=0;}
}
