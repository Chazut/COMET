using Comet.Client;
using Comet.Core;
using Comet.Server;
using Comet.Server.Editing;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text.Json.Nodes;

static class QuietLoggingTests
{
    public static async Task<int> Run()
    {
        int checks=0;
        void Check(bool ok,string why) { checks++;if(!ok)throw new Exception("Quiet logging: "+why); }
        string Json(object value)=>Diagnostics.Serialize(value);
        string directory=Path.Combine(Path.GetTempPath(),"comet-quiet-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            Check(!new CometConfig().QuietLogging,"default remains detailed for current testing");
            var legacy=new CometConfig { Revision=42 };legacy.Performance.Enabled=true;
            var saved=JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(legacy))!.AsObject();
            saved.Remove("QuietLogging");
            string path=Path.Combine(directory,"config.json");File.WriteAllText(path,saved.ToJsonString());
            var store=new ConfigStore(path);var existing=store.Read();
            Check(!existing.QuietLogging&&existing.Revision==42&&existing.Performance.Enabled,"old config defaults OFF without changing performance or revision");
            Check(File.ReadAllText(path)==saved.ToJsonString(),"reading old current-baseline config does not rewrite it");
            var editor=new ConfigEditor(existing);var field=editor.Catalog.Fields[nameof(CometConfig.QuietLogging)];
            Check(field.Boolean&&field.Simple&&editor.Catalog.Sections[field.Section].Category=="Diagnostics","toggle is available in simple Diagnostics UI");
            Check(editor.Visible(false,"quiet",false).Contains(field),"search finds Quiet without advanced mode");
            Check(editor.Set(field,"true")&&editor.CanSave&&editor.Snapshot().QuietLogging,"UI draft can enable Quiet");
            Check(!store.Read().QuietLogging&&!existing.QuietLogging,"editing draft does not mutate saved or running configuration");
            var enabled=store.Save(editor.Snapshot(),42);editor.AcceptSaved(enabled);
            Check(store.Read().QuietLogging&&JsonConvert.DeserializeObject<CometConfig>(store.ForGame())!.QuietLogging,"saved mode reaches game endpoint");
            Check(store.Read().Performance.Enabled,"Quiet does not change independent performance setting");
            Check(editor.Set(field,"false"),"UI can disable Quiet");store.Save(editor.Snapshot(),43);
            Check(!store.Read().QuietLogging&&store.Read().Revision==44,"OFF survives save/reload");
            var old=JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(new CometConfig { QuietLogging=true }))!.AsObject();
            old["BehaviorBaseline"]="older";
            Check(SourceBaselineMigration.Upgrade(old)&&old["QuietLogging"]!.GetValue<bool>(),"behavior migration preserves logging preference");

            var events=new List<string>();long sequence=0;
            foreach(var stage in new[]{"selected","accepted","started","released"})
                events.Add(Json(new {schema=1,session="raid",sequence=++sequence,feature="A01",stage,reason="test",profileId="pmc",botInstance="bot",personality="Normal",decision="Hold",tacticObservationVersion=1,
                    candidates=new[]{new{decision="Hold",eligible=true,reason="available",score=46}},
                    mixing=new{opportunity=1,encounter=1,selected="Hold",roll=.2,state="weighted-roll",options=new[]{new{decision="Hold",probability=1d}}},
                    contact=new{target="known",age=.3},handoff=new{action="Hold",reason="test"}}));
            foreach(double at in new[]{1d,2d})
                events.Add(Json(new {schema=1,session="raid",sequence=++sequence,feature="T01",stage="evaluated",realtime=at,botInstance="bot",profileId="pmc",name="Test PMC",personality="Normal",
                    combatJournal=new{kind="context",context=new{action="Hold",feature="A01",target="enemy"}}}));
            for(int i=0;i<40;i++)events.Add(Json(new {schema=1,session="raid",sequence=++sequence,feature="S01",stage="evaluated",botInstance="bot",profileId="pmc",decision="Flank",squad="team",index=i}));
            string fault=Json(new {schema=1,session="raid",feature="C08",stage="evaluated",reason="fault-test",faultIsolation=new{message="test exception"}});
            string cleanup=Json(new {schema=1,session="raid",feature="C13",stage="cleanup",realtime=10});
            var warnings=new List<string>();
            async Task<(string[] Decisions,string[] Reports,JObject[] Files)> Capture(bool quiet)
            {
                string folder=Path.Combine(directory,quiet?"quiet":"detailed");
                var decisions=new List<string>();var reports=new List<string>();
                Action<string> receive=decisions.Add,summary=reports.Add;
                Comet.Api.Decisions.RaidClosed=false;Comet.Api.Decisions.Clear();
                Comet.Api.Decisions.Published+=receive;Comet.Api.Decisions.SummaryPublished+=summary;
                try
                {
                    var journal=new Diagnostics(folder,"raid",warnings.Add,2048);
                    journal.ConfigureLogging(quiet,44,"raid");
                    journal.ConfigureSnapshot(new CometConfig { QuietLogging=quiet,Revision=44 },"raid");
                    journal.ConfigureJournal(true,true,44,"raid");journal.ConfigureControlJournal(true,44,"raid");journal.ConfigureCounters(true,16384,44,"raid");
                    foreach(string e in events) { journal.Write(e);Comet.Api.Decisions.Publish(e,ex=>throw ex); }
                    journal.WriteEssential(fault);Comet.Api.Decisions.Publish(fault,ex=>throw ex);
                    journal.WriteEssential(cleanup);Comet.Api.Decisions.Publish(cleanup,ex=>throw ex);
                    Comet.Api.Decisions.FlushPending();journal.Dispose();await journal.Completion;
                    var files=Directory.GetFiles(folder,"*.jsonl").SelectMany(File.ReadLines).Select(JObject.Parse).ToArray();
                    return (decisions.ToArray(),reports.ToArray(),files);
                }
                finally { Comet.Api.Decisions.Published-=receive;Comet.Api.Decisions.SummaryPublished-=summary;Comet.Api.Decisions.Clear(); }
            }
            var full=await Capture(false);var quiet=await Capture(true);
            Check(full.Decisions.SequenceEqual(events.Concat(new[]{fault,cleanup})),"full RR stream contains every original action, score, contact, squad event and fault");
            Check(quiet.Decisions.SequenceEqual(full.Decisions),"Quiet RR events are byte-for-byte identical and in the same order");
            Check(quiet.Reports.Length>0&&quiet.Reports.SequenceEqual(full.Reports),"RR receives identical non-empty counters and journal reports in both modes");
            foreach(string feature in new[]{"T01","T02","T03","T04"})
                Check(quiet.Reports.Select(JObject.Parse).Any(p=>(string?)p["feature"]==feature),"summary retained: "+feature);
            var counters=quiet.Reports.Select(JObject.Parse).Where(p=>(string?)p["feature"]=="T02").ToArray();
            var row=counters.SelectMany(p=>p["tacticCounters"]!["rows"]!).Single(r=>(string?)r["feature"]=="A01");
            Check((int)row["opportunities"]! ==1&&(int)row["started"]! ==1,"filtered local events still contribute to observed tactic execution");
            Check((bool)counters[0]["tacticCounters"]!["complete"]!,"intentional filtering does not falsely mark summaries incomplete");
            var combat=quiet.Reports.Select(JObject.Parse).Where(p=>(string?)p["feature"]=="T01").SelectMany(p=>p["journalReport"]!["rows"]!).Single();
            Check((double)combat["seconds"]! ==1,"combat duration remains measurable with Quiet");
            Check(full.Files.Count(p=>p["sequence"]!=null&&(int)p["sequence"]! >0)==events.Count,"OFF writes all detail across segments");
            Check(!quiet.Files.Any(p=>p["sequence"]!=null&&(int)p["sequence"]! >0),"ON omits detailed local events only");
            Check(quiet.Files.Any(p=>(string?)p["reason"]=="fault-test")&&quiet.Files.Any(p=>(string?)p["stage"]=="cleanup"),"fault and cleanup evidence always persisted");
            Check(quiet.Files.Any(p=>(string?)p["stage"]=="configuration"&&p["config"] is JObject),"full configuration retained for interpreting raid");
            Check(quiet.Files.Count(p=>(string?)p["stage"]=="summary")==quiet.Reports.Length,"every summary also retained locally");
            foreach(var result in new[]{full,quiet})
            {
                var footer=result.Files.Single(p=>(string?)p["stage"]=="diagnostics-end");
                Check((string?)footer["summaryReader"]?["mode"]=="shared-projection-v1"&&(long)footer["summaryReader"]!["legacyFallbacks"]! ==0,"projected background reader completed without fallback");
                Check((long)footer["summaryReader"]!["projectedEvents"]! >0&&(long)footer["summaryReader"]!["skippedProperties"]! >0,"unused snapshots were skipped while keeping reports and RR intact");
                Check((int)footer["queueDropped"]! ==0&&(int)footer["writeFailed"]! ==0,"no accidental data loss");
                Check((int)footer["quietSuppressed"]! ==((bool)footer["quietLogging"]!?events.Count:0),"intentional omissions separately counted");
            }
            Check(warnings.Count==0,"normal filtering generates no warning spam");
            // Multiple game/raid instances must not inherit a previous Quiet mode.
            var fresh=new Diagnostics(Path.Combine(directory,"next"),"next",warnings.Add);
            fresh.Write(Json(new{stage="next-raid-detail"}));fresh.Dispose();await fresh.Completion;
            Check(Directory.GetFiles(Path.Combine(directory,"next")).SelectMany(File.ReadLines).Any(l=>l.Contains("next-raid-detail")),"new writer defaults to detailed independently of previous instance");
            // Failed disk IO must remain visible even when detailed file output is suppressed.
            string blocked=Path.Combine(directory,"blocked");File.WriteAllText(blocked,"not a directory");
            bool failed=false;try { _=new Diagnostics(blocked,"raid",warnings.Add); }catch(IOException){failed=true;}
            Check(failed,"file initialization failure is not hidden by Quiet");
        }
        finally { Directory.Delete(directory,true); }
        Console.WriteLine($"Quiet logging: {checks} assertions passed.");return checks;
    }
}
