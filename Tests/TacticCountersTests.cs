using Comet.Client;
using Comet.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

static class TacticCountersTests
{
    static string Json(object value)=>JsonConvert.SerializeObject(value,Diagnostics.Json);
    static TacticCounters New(bool enabled=true,int limit=16384)
    {
        var c=new TacticCounters("raid");c.Observe(Json(new{schema=1,session="raid",feature="T02",stage="configuration",enabled,maxOpportunities=limit,configRevision=7}));return c;
    }
    static JObject Choice(int op=1,string stage="selected",string bot="bot",string feature="A01")=>JObject.Parse(Json(new{
        schema=1,session="raid",feature,stage,reason="test",profileId="pmc",botInstance=bot,personality="Normal",decision="Hold",tacticObservationVersion=1,
        candidates=new[]{new{decision="Hold",eligible=true,reason="available"},new{decision="Push",eligible=false,reason="unsafe"}},
        mixing=new{opportunity=op,encounter=1,selected="Hold",roll=(double?).2,state="weighted-roll",
            options=new[]{new{decision="Hold",probability=.8},new{decision="Push",probability=.2}},
            mistake=new{selected="Hold",roll=(double?).9,state="rolled",options=new[]{new{decision="Hold",probability=.7},new{decision="Push",probability=.3}}}}
    }));
    static void Feed(TacticCounters c,JObject e)=>c.Observe(e.ToString(Formatting.None));
    static JObject[] Finish(TacticCounters c,long dropped=0,bool cleanup=true)
    {
        if(cleanup)c.Observe(Json(new{schema=1,session="raid",feature="C13",stage="cleanup",realtime=1000}));
        return c.Finish(dropped).Select(x=>JObject.Parse(Json(x))).ToArray();
    }
    static IEnumerable<JToken> Rows(JObject[] pages)=>pages.SelectMany(p=>p["tacticCounters"]!["rows"]!);
    static JToken Row(JObject[] pages,string feature,string decision)=>Rows(pages).Single(r=>(string?)r["feature"]==feature&&(string?)r["decision"]==decision);
    public static int Run()
    {
        int checks=0;void Check(bool v,string why){checks++;if(!v)throw new Exception("T02: "+why);}
        var c=New();
        foreach(var stage in new[]{"selected","accepted","started","released"})for(int i=0;i<10;i++)Feed(c,Choice(stage:stage));
        var pages=Finish(c);var r=Row(pages,"A01","Hold");
        Check((int)r["opportunities"]! ==1&&(int)r["selected"]! ==1,"40 repeated snapshots describe one opportunity");
        Check(new[]{"requested","accepted","started","released"}.All(k=>(int)r[k]! ==1),"lifecycle stages deduplicated independently");
        Check((int)r["interrupted"]! ==0&&(int)r["completed"]! ==0,"release is neither interruption nor completed tactic");
        Check((int)Row(pages,"A01","Push")["eligible"]! ==0&&(int)Row(pages,"A01","Push")["reasons"]!["unsafe"]! ==1,"condition refusal counted once");
        Check(Math.Abs((double)Row(pages,"A04","A01 / Hold")["expectedSelections"]!-.8)<1e-9&&Math.Abs((double)Row(pages,"A05","A01 / Hold")["expectedSelections"]!-.7)<1e-9,"A04 and conditional A05 expectations stay separate");
        Check(c.Finish(0).Length==0,"finalization idempotent");
        c=New();Feed(c,Choice());Feed(c,Choice(bot:"recreated"));Feed(c,Choice(feature:"A02"));Feed(c,Choice(2));
        pages=Finish(c);Check((int)Row(pages,"A01","Hold")["opportunities"]! ==3&&(int)Row(pages,"A02","Hold")["opportunities"]! ==1,"instance, policy and opportunity identify distinct decisions");
        Check((int)Row(pages,"A01","Hold")["selectionWithoutObservedStart"]! ==3,"requests without an observed start remain unknown");
        c=New();var deterministic=Choice();deterministic["mixing"]!["roll"]=null;deterministic["mixing"]!["mistake"]!["roll"]=null;Feed(c,deterministic);
        pages=Finish(c);Check((int)Row(pages,"A04","A01 / Hold")["randomTrials"]! ==0&&(int)Row(pages,"A05","A01 / Hold")["randomTrials"]! ==0,"no-draw choices excluded from random trials");
        c=New();var unknown=Choice();unknown["mixing"]!["opportunity"]=null;Feed(c,unknown);var foreign=Choice();foreign["session"]="other";Feed(c,foreign);pages=Finish(c);
        Check(!Rows(pages).Any()&&(int)pages[0]["tacticCounters"]!["unsupportedEvents"]! >0,"missing identity is coverage only; foreign session excluded");
        c=New();Feed(c,Choice(stage:"accepted"));Feed(c,Choice(stage:"rejected"));pages=Finish(c);
        Check((int)Row(pages,"A01","Hold")["commandRefused"]! ==1&&(int)Row(pages,"A01","Hold")["requested"]! ==0,"acceptance does not invent missing request and rejection is not RNG loss");
        c=New();var noObserver=Choice();noObserver.Remove("tacticObservationVersion");Feed(c,noObserver);pages=Finish(c);
        Check(!(bool)Row(pages,"A01","Hold")["executionTracked"]!,"older evidence never claims start observation support");
        // Exercise actual production feint lifecycle and its monotonic identity, including Clear.
        var feint=new DoorFakeGrenade();var cfg=new DoorFakeGrenadeConfig();c=New();
        void FeintEvent(string feature="D05")=>c.Observe(Json(new{schema=1,session="raid",feature,stage="evaluated",profileId="pmc",botInstance="bot",doorFakeGrenade=feature=="D05"?feint.Trace:null,doorFakeMedical=feature=="D06"?feint.Trace:null}));
        Check(feint.Begin("door","enemy","seen","Normal",new DoorPoint(1,2,3),0,1,.01,cfg),"real selected feint");FeintEvent();
        feint.Draw("grenade","gun",1.1);feint.Callback(true,1.2);feint.Observe(true,false,false,false,1.3);feint.RestoreRequested(true,1.4);feint.Observe(false,true,false,false,1.5);
        for(int i=0;i<10;i++)FeintEvent();
        feint.Clear();Check(!feint.Begin("door","enemy","seen","Normal",new DoorPoint(1,2,3),0,2,.9,cfg),"real declined feint");FeintEvent();
        feint.Clear();feint.Begin("door","enemy","seen","Normal",new DoorPoint(1,2,3),0,3,.01,cfg);feint.Trace!.Kind="heal";FeintEvent("D06");
        feint.Draw("med","gun",3.1);feint.Callback(false,3.2);feint.End("fake-draw-callback-failed");FeintEvent("D06");
        pages=Finish(c);r=Row(pages,"D05+D06","Grenade feint");
        Check((int)r["opportunities"]! ==2&&(int)r["randomSelections"]! ==1&&(int)r["randomDeclined"]! ==1,"feint Clear does not reuse identity or double count phases");
        Check((int)r["started"]! ==1&&(int)r["completed"]! ==1&&(int)r["interrupted"]! ==0,"feint observed execution and completion distinct");
        r=Row(pages,"D05+D06","Medical feint");Check((int)r["commandRefused"]! ==1&&(int)r["started"]! ==0&&(int)r["interrupted"]! ==1,"medical callback failure is not a start");
        c=New(limit:1024);for(int i=1;i<=1030;i++)Feed(c,Choice(i));Feed(c,Choice(stage:"started"));pages=Finish(c);
        Check((int)Row(pages,"A01","Hold")["opportunities"]! ==1024&&(int)Row(pages,"A01","Hold")["started"]! ==1,"capacity never evicts old identities; existing lifecycle can finish");
        Check(!(bool)pages[0]["tacticCounters"]!["complete"]!&&(int)pages[0]["tacticCounters"]!["capacitySkipped"]! ==6,"bounded omissions disclosed");
        c=New();c.Observe("[]");c.Observe(new string('x',262145));pages=Finish(c,2,false);
        Check(!(bool)pages[0]["tacticCounters"]!["complete"]!&&(int)pages[0]["tacticCounters"]!["invalidEvents"]! ==1,"malformed, oversized, dropped and missing cleanup make report partial");
        c=New(false);Feed(c,Choice());pages=Finish(c);Check(!Rows(pages).Any()&&!(bool)pages[0]["tacticCounters"]!["enabled"]!,"disabled collection has explicit empty report");
        Check(new TacticCounters("raid").Finish(0).Length==0,"unconfigured legacy writer has no counter report");
        // Compare identical real chooser streams while only one is observed, including retained snapshots.
        var candidates=new List<Candidate>{new(){Decision="Hold",Eligible=true,Score=70},new(){Decision="Search",Eligible=true,Score=69}};
        var a=new DecisionMixer(new(),"seed",false);var b=new DecisionMixer(new(),"seed",false);c=New();
        for(int i=0;i<1000;i++)
        {
            var selected=a.Select(candidates,"enemy","Normal","test");b.Select(candidates,"enemy","Normal","test");
            var e=Choice(i+1);e["decision"]=selected!.Decision;e["candidates"]=JArray.Parse(Json(candidates));e["mixing"]=JObject.Parse(Json(a.Last!));Feed(c,e);Feed(c,e);
            Check(Json(a.Last!)==Json(b.Last!),"counter analysis cannot consume a policy random draw");
        }
        pages=Finish(c);Check((int)pages[0]["tacticCounters"]!["recordedOpportunities"]! ==1000,"sample size is unique choices, not 2000 events");
        Check(pages.All(p=>Json(p).Length<32768),"summary pages fit RR transport");
        var evidence=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../artifacts/t02"));Directory.CreateDirectory(evidence);
        File.WriteAllText(Path.Combine(evidence,"summary.json"),Json(pages));
        // Final pages are delivered after draining, even when another optional subscriber throws.
        var received=new List<string>();int warnings=0;Action<string> bad=_=>throw new Exception("test");Action<string> good=received.Add;
        Comet.Api.Decisions.SummaryPublished+=bad;Comet.Api.Decisions.SummaryPublished+=good;
        try
        {
            var path=Path.Combine(Path.GetTempPath(),"comet-t02-"+Guid.NewGuid().ToString("N"));var writer=new Diagnostics(path,"raid",_=>warnings++);
            writer.ConfigureCounters(true,16384,7,"raid");writer.Write(Choice().ToString(Formatting.None));writer.Write(Json(new{schema=1,session="raid",feature="C13",stage="cleanup",realtime=5}));
            writer.Dispose();writer.Completion.GetAwaiter().GetResult();
            Check(received.Count==2&&warnings==2,"throwing consumer cannot block final page delivery");
            Check(File.ReadAllLines(Directory.GetFiles(path).Single()).Count(l=>l.Contains("\"stage\":\"summary\""))==2,"summary preserved in JSONL as well as RR callback");
        }
        finally{Comet.Api.Decisions.SummaryPublished-=bad;Comet.Api.Decisions.SummaryPublished-=good;}
        Console.WriteLine($"T02: {checks} counter, real-policy, bounded collection and final delivery checks.");return checks;
    }
}
