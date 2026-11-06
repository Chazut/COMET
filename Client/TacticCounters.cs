using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Comet.Client;

// Consumes value-only event strings on the diagnostic writer thread. Never reads
// Unity, calls a policy, draws randomness, or feeds observations back into gameplay.
internal sealed class TacticCounters
{
    internal sealed class Row
    {
        public string Feature = "", Decision = "", Personality = "", Kind = "";
        public long Opportunities, Eligible, Selected, RandomTrials, RandomSelections, RandomDeclined;
        public double ExpectedSelections;
        public long Requested, Accepted, Started, Completed, Interrupted, Released, CommandRefused;
        public long SelectionWithoutObservedStart;
        public bool ExecutionObserved, ExecutionTracked, CompletionTracked;
        public Dictionary<string,long> Reasons = new();
    }
    internal sealed class Coverage
    {
        public string Feature = "";
        public long Events;
        public bool ExactOpportunities;
    }
    private sealed class Attempt
    {
        public readonly HashSet<string> Seen = new();
        public Row? Selected;
    }
    private readonly string session;
    private readonly Dictionary<string,Attempt> attempts = new();
    private readonly Dictionary<string,Row> rows = new();
    private readonly Dictionary<string,Coverage> coverage = new();
    private bool configured, enabled, closed;
    private int limit = 16384;
    internal long CapacitySkipped, InvalidEvents, UnsupportedEvents, OversizeEvents;
    internal long Revision;
    internal double EndTime;
    internal bool SawCleanup;
    internal TacticCounters(string session) { this.session = session; }
    static string S(JToken? n) => n?.Type == JTokenType.String ? (string)n! : "";
    static double? N(JToken? n)
    {
        if(n?.Type is not (JTokenType.Integer or JTokenType.Float)) return null;
        double x=(double)n;return double.IsNaN(x)||double.IsInfinity(x)?null:x;
    }
    static bool B(JToken? n) => n?.Type==JTokenType.Boolean && (bool)n;
    static string Key(params string[] parts) => string.Concat(parts.Select(p=>p.Length+":"+p));
    static string Short(string s) => s.Length<=96?s:s.Substring(0,96);
    static bool Id(JToken? n, out string value)
    {
        value="";double? x=N(n);
        if(!x.HasValue||x<1||x>9007199254740991d||x!=Math.Truncate(x.Value))return false;
        value=x.Value.ToString("0",System.Globalization.CultureInfo.InvariantCulture);return true;
    }
    Row? GetRow(string feature,string decision,string personality,string kind)
    {
        if(feature.Length>12||decision.Length>96||personality.Length>40)return null;
        string key=Key(feature,decision,personality,kind);
        if(rows.TryGetValue(key,out var row))return row;
        if(rows.Count>=256){CapacitySkipped++;return null;}
        return rows[key]=new Row{Feature=feature,Decision=decision,Personality=personality,Kind=kind};
    }
    Attempt? GetAttempt(string key)
    {
        if(attempts.TryGetValue(key,out var a))return a;
        // Never evict and recount an old opportunity. Existing entries can still finish.
        if(attempts.Count>=limit){CapacitySkipped++;return null;}
        return attempts[key]=new Attempt();
    }
    static void Reason(Row row,string reason)
    {
        reason=Short(reason);if(reason.Length==0)return;
        if(!row.Reasons.ContainsKey(reason)&&row.Reasons.Count>=8)reason="other";
        row.Reasons.TryGetValue(reason,out var count);row.Reasons[reason]=count+1;
    }
    internal void Observe(string json)
    {
        if(closed)return;
        if(json.Length>262144){OversizeEvents++;return;}
        try
        {
            // Bounded depth also protects offline fixtures and corrupted diagnostic input.
            using var reader=new Newtonsoft.Json.JsonTextReader(new System.IO.StringReader(json)){MaxDepth=48,DateParseHandling=Newtonsoft.Json.DateParseHandling.None};
            var e=JObject.Load(reader);
            if(S(e["session"])!=session||N(e["schema"])!=1)return;
            string feature=S(e["feature"]),stage=S(e["stage"]);
            if(feature=="T02"&&stage=="configuration")
            {
                if(configured)return;
                configured=true;enabled=B(e["enabled"]);Revision=(long?)N(e["configRevision"])??0;
                limit=(int)Math.Max(1024,Math.Min(65536,N(e["maxOpportunities"])??16384));return;
            }
            if(feature=="C13"&&stage=="cleanup") {SawCleanup=true;EndTime=N(e["realtime"])??0;}
            if(enabled&&feature=="T02"&&stage=="observer-unavailable")InvalidEvents++;
            if(!configured||!enabled||feature=="T02")return;
            // Delivery health is a session diagnostic, not a feature attempt.
            // Keep rejecting unknown/malformed featureless events.
            if(feature.Length==0 && (stage=="diagnostic-delivery" || stage=="diagnostic-delivery-final"))return;
            if(feature.Length<2||feature.Length>12){InvalidEvents++;return;}
            if(!coverage.TryGetValue(feature,out var c))
            {
                if(coverage.Count>=160){CapacitySkipped++;return;}
                coverage[feature]=c=new Coverage{Feature=feature};
            }
            c.Events++;
            string bot=S(e["botInstance"]),profile=S(e["profileId"]);
            if(bot.Length==0||bot.Length>128||profile.Length>128){UnsupportedEvents++;return;}
            if(feature is "A01" or "A02" && stage is "selected" or "accepted" or "started" or "released" or "rejected"
                && e["mixing"] is JObject mix && Id(mix["opportunity"],out var op)&&Id(mix["encounter"],out var encounter))
            { c.ExactOpportunities=true;Core(e,mix,Key(bot,profile,feature,encounter,op),c); }
            else if(feature is "D05" or "D06" && (e["doorFakeGrenade"] as JObject ?? e["doorFakeMedical"] as JObject) is JObject fake && Id(fake["opportunity"],out var fakeOp))
            { c.ExactOpportunities=true;Feint(e,fake,Key(bot,profile,"D05+D06",fakeOp)); }
            else UnsupportedEvents++;
        }
        catch { InvalidEvents++; }
    }
    void Core(JObject e,JObject mix,string key,Coverage coverageRow)
    {
        var a=GetAttempt(key);if(a==null)return;
        string feature=S(e["feature"]),personality=S(e["personality"]),decision=S(e["decision"]),stage=S(e["stage"]);
        if(a.Seen.Add("choice"))
        {
            if(e["candidates"] is not JArray candidates || candidates.Count>32){InvalidEvents++;return;}
            foreach(var candidate in candidates.OfType<JObject>().GroupBy(x=>S(x["decision"])).Select(x=>x.First()))
            {
                string name=S(candidate["decision"]);if(name.Length==0)continue;
                var row=GetRow(feature,name,personality,"action");if(row==null)continue;
                row.Opportunities++;row.ExecutionTracked|=N(e["tacticObservationVersion"])==1;
                if(B(candidate["eligible"]))row.Eligible++;else Reason(row,S(candidate["reason"]));
                if(name==decision){row.Selected++;a.Selected=row;}
            }
            Distribution(mix,feature,personality,"A04",S(mix["selected"]));
            if(mix["mistake"] is JObject mistake)Distribution(mistake,feature,personality,"A05",S(mistake["selected"]));
        }
        if(a.Selected is not Row selected)return;
        // Repeated accepted/released snapshots refer to the same selection, not new trials.
        if(stage=="selected"&&a.Seen.Add("requested"))selected.Requested++;
        if(stage=="accepted"&&a.Seen.Add("accepted"))selected.Accepted++;
        if(stage=="started"&&a.Seen.Add("started")){selected.Started++;selected.ExecutionObserved=true;}
        if(stage=="rejected"&&a.Seen.Add("rejected")){selected.CommandRefused++;Reason(selected,S(e["reason"]));}
        if(stage=="released"&&a.Seen.Add("released")){selected.Released++;Reason(selected,S(e["reason"]));}
    }
    void Distribution(JObject trace,string policy,string personality,string feature,string chosen)
    {
        if(trace["options"] is not JArray options||options.Count>32)return;
        bool random=N(trace["roll"]).HasValue;
        foreach(var option in options.OfType<JObject>().GroupBy(x=>S(x["decision"])).Select(x=>x.First()))
        {
            string name=S(option["decision"]);double? p=N(option["probability"]);
            if(name.Length==0||!p.HasValue||p<0||p>1){InvalidEvents++;continue;}
            var row=GetRow(feature,policy+" / "+name,personality,"draw");if(row==null)continue;
            row.Opportunities++;if(p>0)row.Eligible++;
            if(chosen==name)row.Selected++;
            if(random)
            {
                row.RandomTrials++;row.ExpectedSelections+=p.Value;
                if(chosen==name)row.RandomSelections++;else row.RandomDeclined++;
            }
            else Reason(row,"no-random-draw:"+S(trace["state"]));
        }
    }
    void Feint(JObject e,JObject f,string key)
    {
        var a=GetAttempt(key);if(a==null)return;
        if(a.Seen.Add("choice"))
        {
            var row=GetRow("D05+D06",S(f["kind"])=="grenade"?"Grenade feint":"Medical feint",S(f["personality"]),"action");if(row==null)return;
            a.Selected=row;row.Opportunities++;row.Eligible++;row.ExecutionTracked=row.CompletionTracked=true;
            double? p=N(f["chance"]),roll=N(f["roll"]);
            if(!p.HasValue||!roll.HasValue||p<0||p>100||roll<0||roll>=100){InvalidEvents++;return;}
            row.RandomTrials++;row.ExpectedSelections+=p.Value/100;
            if(roll<p){row.RandomSelections++;row.Selected++;a.Seen.Add("selected");}else row.RandomDeclined++;
        }
        if(a.Selected is not Row r)return;
        if(B(f["drawRequested"])&&a.Seen.Add("requested"))r.Requested++;
        if(B(f["callbackSucceeded"])&&a.Seen.Add("accepted"))r.Accepted++;
        if(B(f["callbackCompleted"])&&!B(f["callbackSucceeded"])&&a.Seen.Add("refused")){r.CommandRefused++;Reason(r,"fake-draw-callback-failed");}
        if(B(f["handsObserved"])&&a.Seen.Add("started")){r.Started++;r.ExecutionObserved=true;}
        string state=S(f["state"]);
        if(state=="completed"&&a.Seen.Add("completed"))r.Completed++;
        if(state=="ended"&&a.Seen.Add("ended"))
        {
            if(a.Seen.Contains("selected") && a.Seen.Contains("requested") && !a.Seen.Contains("completed"))r.Interrupted++;
            Reason(r,S(f["reason"]));
        }
    }
    internal object[] Finish(long queueDropped)
    {
        if(closed)return Array.Empty<object>();closed=true;
        if(!configured)return Array.Empty<object>();
        foreach(var a in attempts.Values)
            if(a.Selected is Row r && r.Kind=="action" && !a.Seen.Contains("started") && (a.Seen.Contains("requested")||a.Seen.Contains("accepted")))r.SelectionWithoutObservedStart++;
        var sorted=rows.Values.OrderBy(x=>x.Feature,StringComparer.Ordinal).ThenBy(x=>x.Decision,StringComparer.Ordinal).ThenBy(x=>x.Personality,StringComparer.Ordinal).ToArray();
        int parts=1+(sorted.Length+7)/8;
        var result=new List<object>();
        for(int part=0;part<parts;part++)
        {
            result.Add(new {schema=1,session,sequence=-(part+1),feature="T02",stage="summary",decision="TacticCounters",reason="raid-ended",
                realtime=EndTime,configRevision=Revision,profileId="",name="Raid counters",
                tacticCounters=new{version=1,part,parts,enabled=configured&&enabled,configured,
                    complete=SawCleanup&&queueDropped==0&&CapacitySkipped==0&&InvalidEvents==0&&OversizeEvents==0,
                    sawCleanup=SawCleanup,queueDropped,capacitySkipped=CapacitySkipped,invalidEvents=InvalidEvents,oversizeEvents=OversizeEvents,
                    unsupportedEvents=UnsupportedEvents,recordedOpportunities=attempts.Count,maxOpportunities=limit,
                    coverage=part==0?coverage.Values.OrderBy(x=>x.Feature,StringComparer.Ordinal).ToArray():Array.Empty<Coverage>(),
                    rows=part==0?Array.Empty<Row>():sorted.Skip((part-1)*8).Take(8).ToArray()}});
        }
        return result.ToArray();
    }
}
