using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Comet.Client;

// Bounded, value-only raid analysis. No state is fed back to the bot policies.
internal sealed class CombatReport
{
    internal sealed class Row
    {
        public string Bot="",ProfileId="",Name="",Personality="",Action="",Feature="";
        public long Samples,HitsDealt,HitsTaken,Kills,Deaths,Contacts;
        public double Seconds,DamageDealt,DamageTaken;
    }
    internal sealed class WeaponRow
    {
        public string Bot="",ProfileId="",Name="";
        public long Requests,Accepted,Refused,Observed,InHands,Ready,Unconfirmed,Interrupted,Incidents;
        public Dictionary<string,long> Reasons=new();
    }
    internal sealed class ControlRow
    {
        public string Bot="",ProfileId="",Name="",Owner="",Blocker="";
        public long Transitions,Takeovers,Returns,FreshThreatReturns,Incidents,LongWaits,Residuals;
        public double Seconds,WaitSeconds,TakeoverSeconds;
    }
    private readonly string session;
    private readonly Dictionary<string,Row> rows=new();
    private readonly Dictionary<string,WeaponRow> weapons=new();
    private readonly Dictionary<string,ControlRow> controls=new();
    private bool controlConfigured,controlEnabled;
    private long controlInvalid,controlCapacity,controlSequence;
    private readonly Dictionary<string,(Row Row,double At,string Target)> last=new();
    private bool configured,combatEnabled,weaponEnabled,cleanup,closed;
    private long revision,invalid,omitted,capacity,lastSequence;
    private double end;
    internal CombatReport(string session){this.session=session;}
    static string S(JToken? v,int max=96)=>v?.Type==JTokenType.String?((string)v!).Substring(0,Math.Min(max,((string)v!).Length)):"";
    static double N(JToken? v)=>v?.Type is JTokenType.Integer or JTokenType.Float&&double.TryParse(v.ToString(),System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var n)&&!double.IsNaN(n)&&!double.IsInfinity(n)?n:0;
    static string Key(params string[] values)=>string.Concat(values.Select(x=>x.Length+":"+x));
    internal void Observe(string json)
    {
        if(closed)return;
        if(json.IndexOf("\"T01\"",StringComparison.Ordinal)<0&&json.IndexOf("\"T03\"",StringComparison.Ordinal)<0&&json.IndexOf("\"T04\"",StringComparison.Ordinal)<0&&json.IndexOf("\"C13\"",StringComparison.Ordinal)<0)return;
        try
        {
            if(json.Length>262144){invalid++;if(json.Contains("\"T04\""))controlInvalid++;return;}
            using var reader=new Newtonsoft.Json.JsonTextReader(new System.IO.StringReader(json)){MaxDepth=48,DateParseHandling=Newtonsoft.Json.DateParseHandling.None};
            var e=JObject.Load(reader);if(S(e["session"],128)!=session||N(e["schema"])!=1)return;
            string feature=S(e["feature"]),stage=S(e["stage"]);
            if(feature=="T04")
            {
                if(stage=="configuration"){if(!controlConfigured){controlConfigured=true;controlEnabled=(bool?)e["enabled"]==true;revision=(long)N(e["configRevision"]);}return;}
                if(!controlConfigured||!controlEnabled)return;
                if(stage=="observer-unavailable"){controlInvalid++;return;}
                long id=(long)N(e["sequence"]);if(id<=controlSequence)return;controlSequence=id;
                Control(e);return;
            }
            if(feature=="T01"&&stage=="configuration")
            {if(configured)return;configured=true;combatEnabled=(bool?)e["combatEnabled"]==true;weaponEnabled=(bool?)e["weaponEnabled"]==true;revision=(long)N(e["configRevision"]);return;}
            if(feature=="C13"&&stage=="cleanup"){cleanup=true;end=N(e["realtime"]);return;}
            if(feature is not ("T01" or "T03")||!configured)return;
            if(stage=="observer-unavailable"){invalid++;return;}
            if(stage=="omitted"){omitted+=(long)Math.Max(0,N(e["count"]));return;}
            long seq=(long)N(e["sequence"]);if(seq<=lastSequence)return;lastSequence=seq;
            string bot=S(e["botInstance"],128);if(bot.Length==0){invalid++;return;}
            if(feature=="T03"&&weaponEnabled){Weapon(e,bot);return;}
            if(feature!="T01"||!combatEnabled||e["combatJournal"] is not JObject data)return;
            string kind=S(data["kind"]);if(kind=="pause"){last.Remove(bot);return;}
            if(data["context"] is not JObject context){invalid++;return;}
            string action=S(context["action"]);if(action.Length==0)action="Unknown";
            string f=S(context["feature"]),personality=S(e["personality"],40),key=Key(bot,action,f,personality);
            if(!rows.TryGetValue(key,out var r))
            {if(rows.Count>=256){capacity++;return;}rows[key]=r=new(){Bot=bot,ProfileId=S(e["profileId"],128),Name=S(e["name"]),Personality=personality,Action=action,Feature=f};}
            double damage=Math.Max(0,Math.Min(100000,N(data["damage"])));
            switch(kind)
            {
                case "context":
                    double now=N(e["realtime"]);string target=S(context["target"],128);
                    if(last.TryGetValue(bot,out var previous))
                    {double dt=now-previous.At;if(dt>=0&&dt<=7)previous.Row.Seconds+=dt;else invalid++;if(target.Length>0&&target!=previous.Target)r.Contacts++;}
                    else if(target.Length>0)r.Contacts++;
                    r.Samples++;last[bot]=(r,now,target);break;
                case "hit-dealt":r.HitsDealt++;r.DamageDealt+=damage;break;
                case "hit-taken":r.HitsTaken++;r.DamageTaken+=damage;break;
                case "kill":r.Kills++;break;
                case "death":r.Deaths++;last.Remove(bot);break;
                default:invalid++;break;
            }
        }catch{invalid++;if(json.Contains("\"T04\""))controlInvalid++;}
    }
    private void Control(JObject e)
    {
        if(e["controlJournal"] is not JObject data){controlInvalid++;return;}
        string kind=S(data["kind"]);if(kind=="gap"){controlInvalid++;return;}
        string bot=S(e["botInstance"],128),owner=S(data["owner"]),blocker=S(data["blocker"]);
        if(bot.Length==0||owner.Length==0){controlInvalid++;return;}
        string key=Key(bot,owner,blocker);
        if(!controls.TryGetValue(key,out var row))
        {if(controls.Count>=256){controlCapacity++;return;}controls[key]=row=new(){Bot=bot,ProfileId=S(e["profileId"],128),Name=S(e["name"]),Owner=owner,Blocker=blocker};}
        switch(kind)
        {
            case "context":row.Transitions++;break;
            case "takeover":row.Takeovers++;row.TakeoverSeconds+=Math.Max(0,N(data["elapsed"]));break;
            case "combat-return":row.Returns++;if(S(data["reason"])=="fresh-threat-observed")row.FreshThreatReturns++;break;
            case "incident-start":row.Incidents++;break;
            case "long-wait":row.LongWaits++;break;
            case "residual":row.Residuals++;break;
            case "interval":row.Seconds+=Math.Max(0,N(data["sampledSeconds"]));row.WaitSeconds+=Math.Max(0,N(data["waitSeconds"]));break;
        }
    }
    private void Weapon(JObject e,string bot)
    {
        if(e["weaponJournal"]?["change"] is not JObject change){invalid++;return;}
        if(!weapons.TryGetValue(bot,out var row))
        {if(weapons.Count>=256){capacity++;return;}weapons[bot]=row=new(){Bot=bot,ProfileId=S(e["profileId"],128),Name=S(e["name"])};}
        switch(S(change["state"]))
        {
            case "accepted":row.Requests++;row.Accepted++;break;
            case "refused":row.Requests++;row.Refused++;break;
            case "observed":row.Observed++;break;
            case "in-hands":row.InHands++;break;
            case "ready":row.Ready++;break;
            case "unconfirmed":row.Unconfirmed++;break;
            case "interrupted":row.Interrupted++;break;
            case "loop-start":row.Incidents++;break;
        }
        string reason=S(change["reason"]);if(!row.Reasons.ContainsKey(reason)&&row.Reasons.Count>=8)reason="other";
        row.Reasons.TryGetValue(reason,out long n);row.Reasons[reason]=n+1;
    }
    internal IEnumerable<object> Finish(long dropped)
    {
        if(closed||(!configured&&!controlConfigured))yield break;closed=true;
        foreach(string feature in new[]{"T01","T03","T04"})
        {
            if(feature=="T04"?!controlConfigured:!configured)continue;
            long errors=feature=="T04"?controlInvalid:invalid,capacityLoss=feature=="T04"?controlCapacity:capacity;
            object[] values=feature=="T01"?rows.Values.Cast<object>().ToArray():feature=="T04"?controls.Values.Cast<object>().ToArray():weapons.Values.Cast<object>().ToArray();
            int parts=1+(values.Length+7)/8;
            for(int part=0;part<parts;part++)
                yield return new{schema=1,session,sequence=-(feature=="T01"?1001:feature=="T04"?3001:2001)-part,feature,stage="summary",decision=feature=="T01"?"CombatJournal":feature=="T04"?"ControlJournal":"WeaponJournal",reason="raid-ended",profileId="",realtime=end,configRevision=revision,
                    journalReport=new{version=1,part,parts,enabled=feature=="T04"?controlEnabled:configured&&(feature=="T01"?combatEnabled:weaponEnabled),configured=feature=="T04"?controlConfigured:configured,
                        complete=cleanup&&dropped==0&&errors==0&&omitted==0&&capacityLoss==0,sawCleanup=cleanup,queueDropped=dropped,invalidEvents=errors,omittedEvents=omitted,capacitySkipped=capacityLoss,
                        rows=part==0?Array.Empty<object>():values.Skip((part-1)*8).Take(8).ToArray()}};
        }
    }
}
