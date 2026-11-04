using System;
using System.Collections.Generic;
using System.Linq;

namespace Comet.Core;

// Observation only. Never selects a weapon or cancels a native operation.
public sealed class WeaponJournalEvent
{
    public string State = "", Reason = "", From = "", To = "", Origin = "";
    public long Request, Incident;
    public int Changes;
    public double Duration;
}
public sealed class WeaponJournal
{
    private sealed class Pending { public long Id; public string From="",To="",Origin=""; public double At; public bool Seen; }
    private readonly List<(double Time,string From,string To)> changes = new();
    private Pending? pending;
    private string held="";
    private bool baseline;
    private long nextRequest,nextIncident,incident;
    private double incidentAt,lastChange;
    private int incidentChanges;
    public long Begin() => ++nextRequest;
    public List<WeaponJournalEvent> Result(long id,double now,string from,string to,string origin,bool accepted,string reason="")
    {
        var events=new List<WeaponJournalEvent>();
        events.Add(new(){State=accepted?"accepted":"refused",Reason=reason.Length>0?reason:accepted?"native-request-accepted":"native-request-refused",From=from,To=to,Origin=origin,Request=id});
        if(!accepted||to.Length==0||to==from)return events;
        if(pending!=null)events.Add(Finish("interrupted","superseded-by-another-request",now));
        pending=new(){Id=id,From=from,To=to,Origin=origin,At=now};return events;
    }
    public List<WeaponJournalEvent> Observe(double now,string actual,bool ready,WeaponJournalConfig config)
    {
        var events=new List<WeaponJournalEvent>();
        if(incident!=0&&now-lastChange>config.Window)events.Add(EndIncident(now,"no-recent-alternation"));
        // Empty hands during healing/grenades are not weapon changes and do not erase the baseline.
        if(actual.Length>0)
        {
            if(baseline&&actual!=held)
            {
                var e=new WeaponJournalEvent{State="observed",Reason="weapon-in-hands-changed",From=held,To=actual,
                    Origin=pending?.To==actual?pending.Origin:"unknown",Request=pending?.To==actual?pending.Id:0};
                events.Add(e);changes.Add((now,held,actual));lastChange=now;
                changes.RemoveAll(x=>now-x.Time>config.Window);if(changes.Count>32)changes.RemoveAt(0);
                // Require reversals, not simply three different weapons. Includes A -> B -> A -> B.
                int reversals=0;for(int i=1;i<changes.Count;i++)if(changes[i].To==changes[i-1].From)reversals++;
                if(changes.Count>=config.ChangeThreshold&&reversals>=2)
                {
                    bool start=incident==0;if(start){incident=++nextIncident;incidentAt=changes[0].Time;incidentChanges=changes.Count;}else incidentChanges++;
                    events.Add(new(){State=start?"loop-start":"loop-update",Reason="repeated-observed-alternation",Incident=incident,
                        Changes=incidentChanges,Duration=now-incidentAt,From=held,To=actual,Origin=e.Origin});
                }
                else if(incident!=0)incidentChanges++;
            }
            held=actual;baseline=true;
        }
        if(pending!=null)
        {
            if(actual==pending.To&&!pending.Seen){pending.Seen=true;events.Add(new(){State="in-hands",Reason="requested-weapon-observed",Request=pending.Id,From=pending.From,To=pending.To,Origin=pending.Origin,Duration=now-pending.At});}
            if(actual==pending.To&&ready)events.Add(Finish("ready","requested-weapon-ready",now));
            else if(now-pending.At>=config.ConfirmationTimeout)events.Add(Finish("unconfirmed",pending.Seen?"weapon-seen-readiness-unconfirmed":"accepted-without-observed-weapon",now));
        }
        return events;
    }
    private WeaponJournalEvent Finish(string state,string reason,double now)
    {
        var p=pending!;pending=null;return new(){State=state,Reason=reason,Request=p.Id,From=p.From,To=p.To,Origin=p.Origin,Duration=Math.Max(0,now-p.At)};
    }
    private WeaponJournalEvent EndIncident(double now,string reason)
    {var e=new WeaponJournalEvent{State="loop-end",Reason=reason,Incident=incident,Changes=incidentChanges,Duration=Math.Max(0,lastChange-incidentAt)};incident=0;incidentChanges=0;return e;}
    public List<WeaponJournalEvent> Suspend(double now,string reason)
    {
        var e=new List<WeaponJournalEvent>();if(pending!=null)e.Add(Finish("unconfirmed",reason,now));
        if(incident!=0)e.Add(EndIncident(now,reason));changes.Clear();held="";baseline=false;return e;
    }
}
