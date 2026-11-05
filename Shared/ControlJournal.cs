using System;
using System.Collections.Generic;

namespace Comet.Core;

// Observations only. No references to game objects and no gameplay commands.
public sealed class ControlObservation
{
    public string Owner="unknown", Layer="", Action="", Feature="", Blocker="unknown", Target="", Residual="";
    public double ContactAt=-1;
    public bool Visible, UnderFire;
}
public sealed class ControlJournalEvent
{
    public string Kind { get; set; }="";
    public string Owner { get; set; }="";
    public string PreviousOwner { get; set; }="";
    public string Layer { get; set; }="";
    public string Action { get; set; }="";
    public string Feature { get; set; }="";
    public string Blocker { get; set; }="unknown";
    public string Reason { get; set; }="";
    public string Residual { get; set; }="";
    public double Elapsed { get; set; }
    public double SampledSeconds { get; set; }
    public double WaitSeconds { get; set; }
    public bool Waiting { get; set; }
    public int Cycles { get; set; }
    public int Incident { get; set; }
}
public sealed class ControlJournal
{
    private ControlObservation? previous, departure;
    private double last=-1,born=-1,waitAt=-1,outsideAt=-1,emittedAt=-1,residualAt=-1;
    private double sampled,waitSampled;
    private string releaseReason="",reportedResidual="";
    private bool longReported,armed,incident;
    private int incidentId;
    private readonly Queue<double> cycles=new();
    static bool Finite(double t)=>!double.IsNaN(t)&&!double.IsInfinity(t)&&t>=0;
    static bool Outside(string owner)=>owner is "orbit" or "questing" or "looting" or "extraction" or "other";
    static bool Combat(string owner)=>owner=="combat";
    static bool Suspended(string owner)=>owner is "sleep" or "downed" or "removed";
    static bool Same(ControlObservation a,ControlObservation b)=>a.Owner==b.Owner&&a.Layer==b.Layer&&a.Action==b.Action
        &&a.Feature==b.Feature&&a.Blocker==b.Blocker&&a.Residual==b.Residual;
    private ControlJournalEvent Event(string kind,ControlObservation x,string reason,double elapsed=0)=>new(){
        Kind=kind,Owner=x.Owner,PreviousOwner=previous?.Owner??"",Layer=x.Layer,Action=x.Action,Feature=x.Feature,
        Blocker=x.Blocker,Reason=reason,Residual=x.Residual,Elapsed=Math.Max(0,elapsed>0?elapsed:waitAt>=0?last-waitAt:0),Waiting=waitAt>=0,
        Cycles=cycles.Count,Incident=incident?incidentId:0};
    private void Reset()
    {previous=departure=null;last=born=waitAt=outsideAt=emittedAt=residualAt=-1;sampled=waitSampled=0;cycles.Clear();armed=incident=longReported=false;releaseReason=reportedResidual="";}
    public void Release(string reason){releaseReason=reason.Length>96?reason.Substring(0,96):reason;}
    public List<ControlJournalEvent> Observe(double now,ControlObservation x,ControlJournalConfig c)
    {
        var events=new List<ControlJournalEvent>();
        if(!c.Enabled||!Finite(now))return events;
        bool gap=last>=0&&(now<last||now-last>2);
        if(gap){Reset();events.Add(Event("gap",x,"observation-gap"));}
        if(previous==null)
        {born=last=emittedAt=now;previous=x;events.Add(Event("baseline",x,"observed-baseline"));return events;}
        double dt=now-last;last=now;
        sampled+=dt;if(waitAt>=0)waitSampled+=dt;
        while(cycles.Count>0&&now-cycles.Peek()>c.Window)cycles.Dequeue();
        if(incident&&cycles.Count<c.CycleThreshold){events.Add(Event("incident-end",x,"rapid-cycle-window-ended"));incident=false;}
        bool changed=!Same(previous,x);
        if(Suspended(x.Owner))
        {
            if(!Suspended(previous.Owner))events.Add(Event("suspended",x,x.Owner));
            waitAt=outsideAt=-1;armed=incident=false;cycles.Clear();departure=null;reportedResidual="";residualAt=-1;
        }
        else if(Suspended(previous.Owner))
        {
            // Wake/revive is a new baseline, never an old pending handoff.
            born=now;events.Add(Event("resumed",x,"fresh-awake-baseline"));
        }
        else
        {
            if(Combat(previous.Owner)&&!Combat(x.Owner))
            {waitAt=now;longReported=false;armed=true;departure=previous;outsideAt=-1;events.Add(Event("combat-ended",x,releaseReason.Length>0?releaseReason:"combat-layer-left"));releaseReason="";}
            if(waitAt>=0&&Outside(x.Owner))
            {double elapsed=now-waitAt;waitAt=-1;outsideAt=now;events.Add(Event("takeover",x,"outside-layer-observed",elapsed));}
            if(Combat(x.Owner)&&!Combat(previous.Owner))
            {
                bool fresh=departure!=null&&(x.UnderFire&&!departure.UnderFire||x.Visible&&!departure.Visible||x.Target.Length>0&&(x.Target!=departure.Target||x.ContactAt>departure.ContactAt+.05));
                double elapsed=waitAt>=0?now-waitAt:outsideAt>=0?now-outsideAt:0;
                events.Add(Event("combat-return",x,fresh?"fresh-threat-observed":"combat-resumed",elapsed));
                if(armed&&outsideAt>=0&&elapsed<c.RapidReturn&&now-born>=20&&!fresh)
                {
                    // Fixed capacity: enough timestamps to establish the threshold, never an unbounded history.
                    if(cycles.Count>=c.CycleThreshold)cycles.Dequeue();cycles.Enqueue(now);
                    if(cycles.Count>=c.CycleThreshold)
                    {if(!incident){incident=true;incidentId++;events.Add(Event("incident-start",x,"repeated-rapid-returns"));}else events.Add(Event("incident-update",x,"repeated-rapid-returns"));}
                }
                else if(fresh){cycles.Clear();if(incident)events.Add(Event("incident-end",x,"fresh-threat-observed"));incident=false;}
                waitAt=outsideAt=-1;armed=false;departure=null;releaseReason="";
            }
            if(waitAt>=0&&!longReported&&now-waitAt>=c.LongWait)
            {longReported=true;events.Add(Event("long-wait",x,"transition-review",now-waitAt));}
            // A stale record is a candidate to inspect, not proof of an issued native command.
            if(Outside(x.Owner)&&x.Residual.Length>0)
            {
                if(x.Residual!=previous.Residual||residualAt<0)residualAt=now;
                if(now-residualAt>=1&&reportedResidual!=x.Residual)
                {reportedResidual=x.Residual;events.Add(Event("residual",x,"comet-record-remains-after-handoff",now-residualAt));}
            }
            else{reportedResidual="";residualAt=-1;}
        }
        // Attribute observed time to the previous context, never the next owner.
        if(changed||events.Count>0||now-emittedAt>=5)
        {
            var duration=Event("interval",previous,"observed-time");duration.SampledSeconds=sampled;duration.WaitSeconds=waitSampled;
            events.Insert(0,duration);sampled=waitSampled=0;emittedAt=now;
            if(changed&&!events.Exists(e=>e.Kind is "suspended" or "resumed"))events.Add(Event("context",x,"context-changed"));
        }
        previous=x;return events;
    }
}
