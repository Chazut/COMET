using Comet.Client;
using Comet.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

static class DiagnosticConfigurationTests
{
    public static async Task<int> Run()
    {
        int n=0;void Check(bool value,string why){n++;if(!value)throw new Exception("Diagnostic configuration: "+why);}
        var config=new CometConfig{Revision=1};var snapshots=new DecisionConfiguration();
        Check(snapshots.Take(null,"raid")==null,"unavailable config does not consume snapshot");
        string Emit(int i)=>JsonConvert.SerializeObject(new{schema=1,session="raid",sequence=i,feature="A01",stage="selected",reason="test",
            configRevision=config.Revision,config=snapshots.Take(config,"raid"),decision="Hold",candidates=new[]{new{decision="Hold",reason="personal-memory"}},
            contact=new{target="known",age=.3},handoff=new{action="Hold",reason="test"}},Diagnostics.Json);
        var first=JObject.Parse(Emit(0));Check(first["config"] is JObject,"first RR decision contains full snapshot");
        for(int i=1;i<=1000;i++)
        {
            var next=JObject.Parse(Emit(i));Check(next["config"]!.Type==JTokenType.Null,"repeat has no full config");
            foreach(var key in new[]{"decision","candidates","contact","handoff","configRevision"})Check(JToken.DeepEquals(first[key],next[key]),"RR evidence retained: "+key);
        }
        var compact=Emit(1001);
        Check(compact.Length<first.ToString(Formatting.None).Length/10,"repeated configuration overhead removed");
        config.Revision++;config.WeaponReturn.CalmDelay=9;
        var changed=JObject.Parse(Emit(1002));Check((double?)changed["config"]?["weaponReturn"]?["calmDelay"]==9,"changed revision includes new snapshot");
        Check(snapshots.Take(config,"next-raid")!=null,"same revision is captured again in next raid");
        string dir=Path.Combine(Path.GetTempPath(),"comet-diagnostic-config-"+Guid.NewGuid().ToString("N"));
        var warnings=new List<string>();var journal=new Diagnostics(dir,"raid",warnings.Add);
        for(int i=0;i<100;i++)journal.ConfigureSnapshot(config,"raid");
        config.Revision++;config.WeaponReturn.CalmDelay=10;journal.ConfigureSnapshot(config,"raid");
        journal.Dispose();await journal.Completion;
        var lines=File.ReadLines(Directory.GetFiles(dir,"*.jsonl").Single()).Select(JObject.Parse).ToArray();
        var local=lines.Where(l=>(string?)l["stage"]=="configuration").ToArray();
        Check(local.Length==2&&warnings.Count==0,"local journal records one complete snapshot per revision even without decisions");
        Check((double?)local[0]["config"]?["weaponReturn"]?["calmDelay"]==9&&(double?)local[1]["config"]?["weaponReturn"]?["calmDelay"]==10,"snapshots detached from later config changes");
        Check((long?)lines.Last()["queueDropped"]==0&&(long?)lines.Last()["fileLimitDropped"]==0,"snapshot journal drains without loss");
        // Crossing the former size limit must preserve every event and one footer.
        string splitDir=Path.Combine(dir,"segments");var split=new Diagnostics(splitDir,"segmented",warnings.Add,512);
        for(int i=0;i<150;i++)split.Write(JsonConvert.SerializeObject(new{schema=1,sequence=i,stage="test",text=new string('x',120)},Diagnostics.Json));
        split.Dispose();await split.Completion;
        var files=Directory.GetFiles(splitDir,"*.jsonl").OrderBy(p=>p.Contains(".part")?int.Parse(Path.GetFileNameWithoutExtension(p).Split(new[]{".part"},StringSplitOptions.None).Last()):0).ToArray();
        var all=files.SelectMany(File.ReadLines).Select(JObject.Parse).ToArray();
        Check(files.Length>1,"small segment budget rotates on the worker");
        Check(all.Where(l=>(string?)l["stage"]=="test").Select(l=>(int)l["sequence"]!).SequenceEqual(Enumerable.Range(0,150)),"all events retained in original order across segments");
        Check(all.Count(l=>(string?)l["stage"]=="diagnostics-end")==1,"exactly one closing footer");
        Check((int?)all.Last()["segments"]==files.Length&&(long?)all.Last()["writeFailed"]==0,"footer reports complete segment set and no write failures");
        Check(warnings.Count==0,"rotation does not warn or lose data");
        Console.WriteLine($"Diagnostic configuration: {n} assertions passed.");return n;
    }
}
