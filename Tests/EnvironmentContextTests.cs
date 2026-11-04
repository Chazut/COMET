using Comet.Core;
using Comet.Server;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;

internal static class EnvironmentContextTests
{
    public static int Run()
    {
        int n=0;void Check(bool ok,string why){n++;if(!ok)throw new Exception("P10: "+why);}
        void Invalid(Action a){try{a();}catch(ArgumentException){n++;return;}throw new Exception("P10 accepted invalid configuration");}
        var c=new EnvironmentContextConfig();c.Validate();
        Check(c.Enabled&&c.Refresh==.5&&c.Movement==1&&c.CeilingDistance==20,"approved defaults");
        foreach(var v in new[]{double.NaN,double.PositiveInfinity,-1d,100d})
            foreach(var set in new Action<EnvironmentContextConfig>[] {x=>x.Refresh=v,x=>x.Movement=v,x=>x.CeilingDistance=v})
            {var q=new EnvironmentContextConfig();set(q);Invalid(q.Validate);}
        Invalid(()=>new CometConfig{EnvironmentContext=null!}.Validate());
        var old=JsonSerializer.Deserialize<CometConfig>("{\"Enabled\":false}")!;old.Validate();
        Check(old.EnvironmentContext.Enabled&&!old.Enabled,"old config migrates without enabling master");
        var dir=Path.Combine(Path.GetTempPath(),"comet-p10-"+Guid.NewGuid().ToString("N"));
        try{
            var store=new ConfigStore(Path.Combine(dir,"config.json"));var q=store.Read();q.EnvironmentContext=new(){Enabled=false,Refresh=.8,Movement=1.5,CeilingDistance=15};store.Save(q,q.Revision);
            var response=JsonSerializer.Deserialize<CometConfig>(store.ForGame())!.EnvironmentContext;
            Check(!response.Enabled&&response.Refresh==.8&&response.Movement==1.5&&response.CeilingDistance==15,"server persistence and authoritative game response");
            q=store.Read();q.EnvironmentContext.Refresh=0;Invalid(()=>store.Save(q,q.Revision));Check(store.Read().EnvironmentContext.Refresh==.8,"invalid save preserves last revision");
        }finally{Directory.Delete(dir,true);}
        var cache=new EnvironmentContext();
        Check(cache.NeedsSample(10,0,0,0,c)&&cache.NeedsCeiling(10,0,0,0,c),"new bot needs observations");
        foreach(var ai in new bool?[]{null,false,true})foreach(var eft in new bool?[]{null,false,true})
        {
            cache.Clear();cache.Observe(10,0,0,0,ai,eft);var t=cache.Read(10,0,0,0,c);
            string expected=ai==true&&eft==false?"conflicting":ai==true||eft==true?"indoor":eft==false?"outdoor":"unknown";
            Check(t.Reported==expected&&t.AiIndoorZone==ai&&t.EftIndoor==eft,"source combination "+ai+"/"+eft);
            Check(t.Ceiling=="not-checked"&&t.CeilingHeight==null&&t.Influence=="observation-only","native flags do not prove ceiling or safe cover");
        }
        cache.Observe(10,0,0,0,false,true);
        Check(!cache.Read(10,0,0,0,c).Conflict,"Factory absent zone is not contradictory");
        Check(!cache.NeedsSample(10.49,.9,0,0,c)&&cache.NeedsSample(10.5,0,0,0,c),"time expiry boundary");
        foreach(var p in new[]{(1d,0d,0d),(0d,1d,0d),(0d,0d,1d),(.6,.6,.6)})
            Check(cache.NeedsSample(10.1,p.Item1,p.Item2,p.Item3,c),"3D movement invalidation");
        foreach(var now in new[]{9d,double.NaN,double.PositiveInfinity})Check(cache.NeedsSample(now,0,0,0,c),"invalid or reversed time");
        Check(cache.Read(10.1,1,0,0,c).Reported=="unknown","stale location never returned as current");
        cache.ObserveCeiling(10,0,0,0,c,new("detected","overhead-surface",3));
        var hangar=cache.Read(10.1,0,0,0,c);
        Check(hangar.Ceiling=="detected"&&hangar.CeilingHeight==3&&hangar.CeilingAge>0&&hangar.ValidFor<.5,"cached physical hit carries age");
        cache.Observe(10.2,0,0,0,false,false);
        Check(cache.Read(10.2,0,0,0,c).Reported=="outdoor"&&cache.Read(10.2,0,0,0,c).Ceiling=="detected","canopy: physical roof does not force indoor");
        Check(cache.Read(10.5,0,0,0,c).Ceiling=="not-checked","ceiling expires independently from refreshed flags");
        Check(cache.Read(10.3,2,0,0,c).Ceiling=="not-checked","doorway movement invalidates ceiling");
        cache.Observe(10.3,2,0,0,false,false);
        Check(cache.Read(10.3,2,0,0,c).Ceiling=="not-checked","refresh flags cannot resurrect old roof");
        cache.ObserveCeiling(10.3,2,0,0,c,new("clear-to-limit","no-hit-within-limit"));
        Check(cache.Read(10.4,2,0,0,c).CheckedDistance==20&&cache.Read(10.4,2,0,0,c).Ceiling=="clear-to-limit","no-hit limit explicit");
        Check(cache.NeedsCeiling(10.4,2,0,0,new(){CeilingDistance=30}),"range change invalidates cache");
        cache.ObserveCeiling(10.4,2,0,0,c,new("deferred","probe-budget-deferred"));
        Check(cache.Read(10.4,2,0,0,c).Ceiling=="deferred"&&cache.NeedsCeiling(10.4,2,0,0,c),"budget deferred not a cached negative");
        cache.Observe(11,2,0,0,false,false);
        Check(cache.Read(11,2,0,0,c).Ceiling=="not-checked","deferred attempt also expires");
        Check(cache.Read(11,2,0,0,new(){Enabled=false}).ValidFor==0,"disabled context empty");
        cache.Clear();Check(cache.Read(11,2,0,0,c).ValidFor==0&&cache.NeedsCeiling(11,2,0,0,c),"raid/scope reset clears all observations");
        Check(CeilingClassifier.Classify(false,false,false,0,0,20).State=="clear-to-limit","open ray is only clear to limit");
        Check(CeilingClassifier.Classify(true,false,false,0,0,20).State=="uncertain","saturation cannot claim open sky");
        Check(CeilingClassifier.Classify(false,true,true,20,-.5,20).State=="detected","sloped ceiling boundary");
        foreach(var v in new[]{0d,-1,double.NaN,double.PositiveInfinity,21})
            Check(CeilingClassifier.Classify(false,true,true,v,-1,20).State=="uncertain","bad hit distance");
        foreach(var v in new[]{0d,1,double.NaN,double.PositiveInfinity,-.49})
            Check(CeilingClassifier.Classify(false,true,true,3,v,20).State=="uncertain","wall side or invalid normal");
        Check(CeilingClassifier.Classify(false,true,false,3,-1,20).State=="uncertain","unconfirmed collider not safe structure");
        var budget=new EnvironmentProbeBudget();
        for(int i=0;i<4;i++)Check(budget.TryBegin(1),"bounded shared probe allocation");
        Check(!budget.TryBegin(1)&&budget.TryBegin(2),"all bots share quota; next frame resets");
        budget.Charge(.25);Check(!budget.TryBegin(2),"soft time cutoff before next query");
        budget.Clear();Check(budget.TryBegin(2),"lifecycle resets budget");budget.Charge(double.NaN);Check(!budget.TryBegin(2),"invalid timing fails closed");
        var snapshot=hangar;cache.Observe(12,0,0,0,true,false);Check(snapshot.Reported=="indoor","published snapshot does not mutate with cache");
        using(var doc=JsonDocument.Parse(Newtonsoft.Json.JsonConvert.SerializeObject(new{environmentContext=snapshot},Comet.Client.Diagnostics.Json)))
            Check(doc.RootElement.GetProperty("environmentContext").GetProperty("influence").GetString()=="observation-only","wire contract");
        using(var pe=new PEReader(File.OpenRead("Client/bin/Release/netstandard2.1/COMET.dll")))
        {
            var md=pe.GetMetadataReader();
            List<string> Calls(string type,string method)=>FloorHearingTests.ReadIl(pe.GetMethodBody(md.TypeDefinitions.Select(md.GetTypeDefinition).Single(x=>md.GetString(x.Name)==type).GetMethods().Select(md.GetMethodDefinition).Single(x=>md.GetString(x.Name)==method).RelativeVirtualAddress).GetILBytes()!)
                .Where(x=>x.op==OpCodes.Call||x.op==OpCodes.Callvirt).Select(x=>FloorHearingTests.Name(md,(int)x.operand!)).ToList();
            var calls=Calls("Plugin","RefreshEnvironment");
            foreach(var required in new[]{"IsPmcAi","get_Authority","Ghost","get_BotActive","get_GameEnding","get_GoalEnemy","get_Environment","NeedsCeiling","RaycastNonAlloc","Clear","Charge"})Check(calls.Contains(required),"live adapter contract: "+required);
            Check(Calls("NativeAdapter","GrenadeCapability").Contains("RefreshEnvironment"),"actual grenade preparation requests geometry");
            Check(!Calls("Plugin","Update").Contains("RaycastNonAlloc"),"ordinary sweep does not directly cast");
            Check(CleanupIl.Calls(pe,md,"Plugin","EndRaid").Contains("ClearEnvironment"),"shared buffers reset on raid end");
            foreach(var banned in new[]{"set_IsIndoors","set_Environment","SetEnemyKnown","AddEnemy","CalculatePath","FindObjectsOfType","DoThrow","get_EnemyPosition","set_IsVisible"})Check(!calls.Contains(banned),"observation cannot mutate combat: "+banned);
        }
        Console.WriteLine($"P10: {n} checks passed (sources, canopy/doorway cache, geometry, budget, contracts, server persistence).");return n;
    }
}
