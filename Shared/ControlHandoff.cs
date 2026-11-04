using System;

namespace Comet.Core;

// Observations only. This model never schedules gameplay or adds a takeover delay.
public sealed class ControlHandoffTrace
{
    public string State { get; set; } = "observed";
    public string Owner { get; set; } = "unknown";
    public string Layer { get; set; } = "";
    public string PreviousOwner { get; set; } = "";
    public string Reason { get; set; } = "";
    public string ReleasedAction { get; set; } = "";
    public double Elapsed { get; set; }
    public bool Pending { get; set; }
    public int RapidReturns { get; set; }
}

public sealed class ControlHandoff
{
    public string Owner { get; private set; } = "unknown";
    private string layer="", action="";
    private double since, releasedAt=-1, lastNow=-1;
    private bool warned;
    private int rapidReturns;
    public static bool IsCometLayer(string name) => name is "COMET : Crossfire" or "COMET : Post-combat";
    public static bool IsOutside(string owner) => owner is "orbit" or "questing" or "looting" or "extraction" or "other";
    public static string Classify(string name, bool orbit, bool questing, bool looting)
    {
        if(name=="COMET : Post-combat")return "recovery";
        if(name=="COMET : Crossfire")return "combat";
        if(questing && name=="Sleeping")return "sleep";
        if(orbit && name=="OrbitBrainLayer")return "orbit";
        if(looting && name=="Looting")return "looting";
        if(questing && name is "BotObjectiveLayer" or "BotFollowerLayer" or "BotFollowerRegroupLayer")return "questing";
        if(name=="SAIN : Extract")return "extraction";
        if(name=="SAIN : Combat Layer"||name=="SAIN : Squad Layer"||name.StartsWith("SAIN : Avoid",StringComparison.Ordinal)
            ||name.StartsWith("SAIN : Flash",StringComparison.Ordinal))return "combat";
        if(name.StartsWith("SAIN",StringComparison.Ordinal))return "sain";
        return name.Length==0?"unknown":"other";
    }
    private bool Clock(double now)
    {
        if(double.IsNaN(now)||double.IsInfinity(now)||now<0)return false;
        if(lastNow>now){Owner="unknown";layer=action="";since=now;releasedAt=-1;warned=false;rapidReturns=0;}
        lastNow=now;return true;
    }
    private ControlHandoffTrace Trace(string state,string reason,string previous,double elapsed) => new() {
        State=state,Reason=reason,PreviousOwner=previous,Owner=Owner,Layer=layer,ReleasedAction=action,
        Elapsed=Math.Max(0,elapsed),Pending=releasedAt>=0,RapidReturns=rapidReturns };
    public ControlHandoffTrace? Release(double now,string fromAction,string reason)
    {
        if(!Clock(now)||releasedAt>=0)return null;
        action=fromAction;releasedAt=now;warned=false;
        return Trace("released",reason,Owner,0);
    }
    public ControlHandoffTrace? Observe(double now,string next,string name)
    {
        if(!Clock(now))return null;
        bool change=next!=Owner||name!=layer;
        if(!change)
        {
            if(releasedAt>=0 && next is "combat" or "recovery" or "sleep")
            {double interrupted=now-releasedAt;releasedAt=-1;return Trace("observed","control-release-interrupted",Owner,interrupted);}
            if(releasedAt>=0&&IsOutside(next))
            {double waited=now-releasedAt;releasedAt=-1;return Trace("observed","control-takeover-observed",Owner,waited);}
            if(releasedAt>=0&&!warned&&now-releasedAt>=30)
            {warned=true;return Trace("waiting","control-handoff-waiting",Owner,now-releasedAt);}
            return null;
        }
        string previous=Owner;double elapsed=releasedAt>=0?now-releasedAt:now-since;
        if(next=="combat"&&IsOutside(previous)&&now-since<5)rapidReturns++;
        Owner=next;layer=name;since=now;
        string reason=next=="sleep"?"control-sleep":next=="combat"?"control-combat-resumed":"control-layer-observed";
        if(releasedAt>=0&&IsOutside(next))reason="control-takeover-observed";
        if(next=="sleep"||next=="combat"||next=="recovery"||IsOutside(next))releasedAt=-1;
        return Trace("observed",reason,previous,elapsed);
    }
}
