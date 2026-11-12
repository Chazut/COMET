using System;
namespace Comet.Core;

public sealed class ReadyStanceConfig
{
    public bool Enabled { get; set; } = true;
    public double OrdinaryDuration { get; set; } = 25;
    public double NearbyDuration { get; set; } = 25;
    public double NearbyDistance { get; set; } = 40;
    public double CueFreshness { get; set; } = 5;
    public double Interval { get; set; } = .25;
    public void Validate()
    {
        CometConfig.Range(OrdinaryDuration,1,60,nameof(OrdinaryDuration));
        CometConfig.Range(Interval,.05,1,nameof(Interval));
    }
}
public sealed class ReadyStanceTrace
{
    public string State { get; set; }="native";
    public string Reason { get; set; }="ready-stance-native";
    public string Evidence { get; set; }="";
    public string Contact { get; set; }="";
    public double? Distance { get; set; }
    public double CueAge { get; set; }
    public double SinceFight { get; set; }
    public double Remaining { get; set; }
    public bool NativePatrol { get; set; }
    public bool Requested { get; set; }
    public bool ObservedRaised { get; set; }
    public bool Applied { get; set; }
    public double ValidFor { get; set; }
    public string[] Alternatives { get; set; }=Array.Empty<string>();
}
// Owns a time-limited veto of lowered patrol posture, never a combat or movement decision.
public sealed class ReadyStance
{
    public ReadyStanceTrace? Trace { get; private set; }
    public double Deadline { get; private set; }=-1;
    private double lastFight=-1,lastCue=-1,lastNow=-1;
    public bool Pending(double now)=>MedicalChoice.Finite(now)&&now>=lastFight&&lastFight>=0&&now<=Deadline;
    public void ObserveCombatAge(double now,double age,ReadyStanceConfig c)
    {
        lastNow=now;lastFight=now-age;lastCue=lastFight;Deadline=lastFight+c.OrdinaryDuration;
        Trace??=new();Trace.Evidence="time since combat layer";
    }
    public bool Observe(double now,double cueAt,string evidence,string contact,double? distance,bool fight,ReadyStanceConfig c)
    {if(fight)ObserveCombatAge(now,0,c);return fight;}
    public bool Evaluate(double now,bool nativePatrol,string block,ReadyStanceConfig c)
    {
        if(!MedicalChoice.Finite(now)||now<0||lastNow>=0&&now<lastNow){Clear("ready-stance-clock-reset");return false;}
        lastNow=now;if(Trace==null)return false;
        var t=Trace;t.Remaining=Math.Max(0,Deadline-now);t.SinceFight=Math.Max(0,now-lastFight);t.CueAge=Math.Max(0,now-lastCue);t.NativePatrol=nativePatrol;
        t.Requested=c.Enabled&&Pending(now)&&block.Length==0&&nativePatrol;
        t.State=!c.Enabled?"released":!Pending(now)?"expired":block.Length>0?"suspended":nativePatrol?"pending":"native";
        t.Reason=!c.Enabled?"ready-stance-disabled":!Pending(now)?"ready-stance-window-ended":block.Length>0?block:nativePatrol?"ready-stance-recent-threat":"ready-stance-native-posture";
        t.Applied=false;t.ValidFor=0;
        t.Alternatives=new[]{"Combat, objectives and Ghost eligibility: unchanged","ADS and aim direction: native","Hands, sprint and interactions take priority"};
        return t.Requested;
    }
    public void ObservePosture(bool raised)
    {
        if(Trace==null)return;var t=Trace;t.ObservedRaised=raised;t.Applied=t.Requested&&raised;
        t.ValidFor=t.Applied?Math.Min(1,t.Remaining):0;
        if(t.Requested){t.State=raised?"holding":"pending";t.Reason=raised?"ready-stance-raised-observed":"ready-stance-request-not-observed";}
    }
    public void Clear(string why)
    {
        Deadline=lastFight=lastCue=lastNow=-1;
        if(Trace!=null){Trace.State="released";Trace.Reason=why;Trace.Requested=Trace.Applied=false;Trace.Remaining=Trace.ValidFor=0;}
    }
}
