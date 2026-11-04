using System;
using System.Collections.Generic;

namespace Comet.Core;

public sealed class DoorInterruptionConfig
{
    public bool Enabled { get; set; }=true;
    public double ResumeWindow { get; set; }=15;
    public double QuietPeriod { get; set; }=3;
    public double HeardDistance { get; set; }=25;
    public double HeardAge { get; set; }=3;
    public double GunfireDistance { get; set; }=12;
    public double GunfireAge { get; set; }=2;
    public int MaxResumes { get; set; }=1;
    public void Validate()
    {
        CometConfig.Range(ResumeWindow,1,30,nameof(ResumeWindow));
        CometConfig.Range(QuietPeriod,.5,10,nameof(QuietPeriod));
        CometConfig.Range(HeardDistance,0,40,nameof(HeardDistance));
        CometConfig.Range(HeardAge,.1,5,nameof(HeardAge));
        CometConfig.Range(GunfireDistance,0,25,nameof(GunfireDistance));
        CometConfig.Range(GunfireAge,.1,5,nameof(GunfireAge));
        CometConfig.Range(MaxResumes,0,1,nameof(MaxResumes));
    }
}

// Only personal dated perception. Incoming fire deliberately has no inferred shooter location.
public sealed class DoorThreat
{
    public string Id="",Source="";
    public bool Visible,Gunfire,OtherFloor,InTargetRoom;
    public double Age,Distance;
}
public static class DoorInterruptionThreat
{
    public static string Reason(DoorThreat t,DoorInterruptionConfig c)
    {
        if(t.Visible&&t.Source=="seen")return "other-visible-enemy";
        if(t.Source!="heard"||!DoorPolicy.Finite(t.Age)||t.Age<0||!DoorPolicy.Finite(t.Distance)||t.Distance<0)return "";
        if(t.Gunfire&&c.GunfireDistance>0&&t.Age<=c.GunfireAge&&t.Distance<=c.GunfireDistance)return "nearby-gunfire";
        if(!t.InTargetRoom&&!t.OtherFloor&&c.HeardDistance>0&&t.Age<=c.HeardAge&&t.Distance<=c.HeardDistance)return "nearby-heard-enemy";
        return "";
    }
}
// Preserve a verified floor result for the same dated sound after P04's geometry cache expires.
// New evidence or listener movement requires another verified result. Values only; bounded per bot.
public sealed class DoorFloorEvidence
{
    private sealed class Entry { public double At; public DoorPoint Listener,Source; }
    private readonly Dictionary<string,Entry> entries=new();
    private readonly Queue<string> order=new();
    public int Count=>entries.Count;
    public bool OtherFloor(string id,double now,double at,DoorPoint listener,DoorPoint source,bool verified,double maxAge)
    {
        if(id.Length==0||!DoorPolicy.Finite(now)||!DoorPolicy.Finite(at)||now<at||now-at>maxAge||!listener.Finite||!source.Finite)return false;
        if(verified)
        {
            if(!entries.ContainsKey(id)){if(entries.Count>=32)entries.Remove(order.Dequeue());order.Enqueue(id);}
            entries[id]=new(){At=at,Listener=listener,Source=source};return true;
        }
        return entries.TryGetValue(id,out var e)&&Math.Abs(e.At-at)<.01
            &&(e.Listener-listener).Length<=.25&&(e.Source-source).Length<=.25;
    }
    public void Clear(){entries.Clear();order.Clear();}
}
public sealed class DoorInterruptionTrace
{
    public string Attempt { get; set; }="";
    public string Plan { get; set; }="";
    public string Phase { get; set; }="";
    public string Door { get; set; }="";
    public string Target { get; set; }="";
    public string State { get; set; }="interrupted";
    public string Reason { get; set; }="";
    public string Cause { get; set; }="";
    public string Threat { get; set; }="";
    public string Evidence { get; set; }="";
    public double Remaining { get; set; }
    public double QuietRemaining { get; set; }
    public int Resumes { get; set; }
    public bool SafeStage { get; set; }
    public bool FeintConsumed { get; set; }
    public bool GrenadeConsumed { get; set; }
    public bool NativeAccepted { get; set; }
    public bool ExecutionObserved { get; set; }
    public string[] Gates { get; set; }=Array.Empty<string>();
    public double ValidFor { get; set; }=1.5;
}
// The original deadline never moves, including during combat, resume validation and native handoff.
public sealed class DoorInterruption
{
    public DoorInterruptionTrace? Trace { get; private set; }
    public bool Pending=>Trace?.State is "interrupted" or "waiting" or "requested";
    public bool Resumed=>Trace?.State=="resumed";
    public double Deadline { get; private set; }
    private double lastThreat,last;
    public bool Interrupt(DoorInterruptionTrace snapshot,double now,double originalDeadline,string cause,string threat,string evidence,DoorInterruptionConfig c)
    {
        int used=Trace?.Attempt==snapshot.Attempt?Trace.Resumes:0;
        if(Trace?.Attempt==snapshot.Attempt)originalDeadline=Math.Min(originalDeadline,Deadline);
        Trace=snapshot;Trace.Resumes=used;Trace.Cause=Trace.Reason=cause;Trace.Threat=threat;Trace.Evidence=evidence;
        Deadline=Math.Min(originalDeadline,now+c.ResumeWindow);last=lastThreat=now;
        if(!DoorPolicy.Finite(now)||!DoorPolicy.Finite(Deadline)||Deadline<=now)Abandon("original-budget-expired");
        else if(!c.Enabled||c.MaxResumes==0)Abandon("resume-disabled");
        else if(used>=c.MaxResumes)Abandon("resume-limit-reached");
        else if(!snapshot.SafeStage)Abandon("committed-action-not-replayed");
        Tick(now,c);return Pending;
    }
    public void Threat(double now,string reason,string id,string evidence)
    {if(!Pending)return;lastThreat=now;Trace!.State="waiting";Trace.NativeAccepted=false;Trace.Cause=reason;Trace.Threat=id;Trace.Evidence=evidence;Trace.Reason=reason;}
    public bool Tick(double now,DoorInterruptionConfig c)
    {
        if(!Pending&&!Resumed)return false;
        if(!DoorPolicy.Finite(now)||now<last){Abandon("invalid-clock");return false;}
        last=now;Trace!.Remaining=Math.Max(0,Deadline-now);Trace.QuietRemaining=Math.Max(0,c.QuietPeriod-(now-lastThreat));
        if(!c.Enabled||c.MaxResumes==0)Abandon("resume-disabled");
        else if(now>=Deadline)Abandon("original-budget-expired");
        return Pending||Resumed;
    }
    public bool Ready(double now,string selected,bool safe,DoorInterruptionConfig c)
    {
        if(!Tick(now,c)||!Pending||Trace!.State=="requested")return false;
        Trace.State="waiting";
        Trace.Reason=selected!=Trace.Target?"waiting-for-normal-target-selection":!safe?"waiting-for-native-priority":Trace.QuietRemaining>0?"waiting-for-quiet":"ready-for-validation";
        return Trace.Reason=="ready-for-validation";
    }
    public void Request(){if(Pending&&Trace!.Reason=="ready-for-validation"){Trace.State="requested";Trace.Reason="awaiting-native-acceptance";}}
    public void Accept(bool accepted){if(Trace?.State!="requested")return;Trace.NativeAccepted=accepted;if(!accepted)Abandon("native-action-refused");}
    public bool Execute(double now,DoorInterruptionConfig c)
    {
        if(!Tick(now,c)||Trace!.State!="requested"||!Trace.NativeAccepted)return false;
        Trace.Resumes++;Trace.ExecutionObserved=true;Trace.State="resumed";Trace.Reason="safe-stage-resumed";return true;
    }
    public void Abandon(string reason)
    {if(Trace==null)return;Trace.State="abandoned";Trace.Reason=reason;Trace.Remaining=Trace.QuietRemaining=0;}
    public void Complete(){if(Trace==null)return;Trace.State="completed";Trace.Reason="door-plan-ended";Trace.Remaining=0;}
    public void Clear(){Trace=null;Deadline=last=lastThreat=0;}
}
