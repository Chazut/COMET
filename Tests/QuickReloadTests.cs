using Comet.Core;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

static class QuickReloadTests
{
    public static int Run()
    {
        int n=0;void Check(bool v,string why){n++;if(!v)throw new Exception("W04: "+why);}
        void Throws(Action a,string why){try{a();}catch(ArgumentException){n++;return;}throw new Exception("W04: "+why);}
        var c=new QuickReloadConfig();c.Validate();
        Check(c.Enabled&&c.MaxAmmoPercent==30&&c.ThreatDistance==35&&c.ContactMemory==3&&c.MinimumSpareMagazines==2,"approved defaults");
        foreach(var prop in typeof(QuickReloadConfig).GetProperties().Where(p=>p.PropertyType==typeof(double)))
            foreach(var bad in new[]{double.NaN,double.PositiveInfinity,-1,1000}){var v=new QuickReloadConfig();prop.SetValue(v,bad);Throws(v.Validate,prop.Name+" bounds");}
        foreach(int bad in new[]{-1,0,1,6})Throws(()=>new QuickReloadConfig{MinimumSpareMagazines=bad}.Validate(),"always keep a loaded spare");
        Throws(()=>new CometConfig{QuickReload=null!}.Validate(),"missing section");
        Check(JsonSerializer.Deserialize<CometConfig>("{}")!.QuickReload.MinimumSpareMagazines==2,"old config migration defaults");
        var saved=JsonSerializer.Deserialize<CometConfig>(JsonSerializer.Serialize(new CometConfig{QuickReload=new(){MaxAmmoPercent=25,ThreatDistance=30,ContactMemory=2,MinimumSpareMagazines=3}}))!;
        saved.Validate();Check(saved.QuickReload.MaxAmmoPercent==25&&saved.QuickReload.MinimumSpareMagazines==3,"server persistence");
        QuickReloadFrame F()=>new(){Now=10,Weapon="rifle",OldMagazine="old-mag",Replacement="new-mag",Target="enemy",Source="personal-vision",
            Loaded=7,OldRounds=6,Capacity=31,ReplacementRounds=30,SpareMagazines=2,Ready=true,External=true,Reachable=true,Distance=20,ContactAge=1};
        var p=new QuickReloadPolicy();var f=F();var wire=new List<JsonElement>();
        void Save()=>wire.Add(JsonSerializer.Deserialize<JsonElement>(Newtonsoft.Json.JsonConvert.SerializeObject(p.Trace,Comet.Client.Diagnostics.Json)));
        Check(p.Evaluate(f,c),"urgent low ammo with two useful spares");Save();
        Check(!p.Active&&!p.Trace!.Requested&&!p.Trace.Dropped,"choice not request or drop");
        foreach(var entry in new (Action<QuickReloadFrame> change,string reason)[]{
            (x=>x.External=false,"quick-external-magazine-required"),(x=>x.OldMagazine="","quick-external-magazine-required"),
            (x=>x.Ready=false,"quick-hands-busy"),(x=>x.Reachable=false,"quick-replacement-not-useful"),
            (x=>x.Replacement=x.OldMagazine,"quick-replacement-not-useful"),(x=>x.ReplacementRounds=6,"quick-replacement-not-useful"),
            (x=>x.SpareMagazines=1,"quick-preserve-spare-magazines"),(x=>x.Loaded=10,"quick-preserve-loaded-magazine"),
            (x=>x.Protected=true,"quick-protective-cover"),(x=>x.Distance=150,"quick-no-immediate-pressure"),
            (x=>x.ContactAge=3.001,"quick-no-immediate-pressure"),(x=>x.Target="","quick-no-immediate-pressure"),
            (x=>x.Block="inventory-budget","inventory-budget")})
        {p=new();f=F();entry.change(f);Check(!p.Evaluate(f,c)&&p.Trace!.Reason==entry.reason,entry.reason);Save();}
        p=new();f=F();f.Distance=35;f.ContactAge=3;f.Loaded=9;f.Capacity=30;Check(p.Evaluate(f,c),"inclusive exact approved boundaries");
        f.Distance=35.001;Check(!p.Evaluate(f,c),"outside close range");
        f.Incoming=true;f.Protected=true;f.Target="";f.ContactAge=999;Check(p.Evaluate(f,c),"actual incoming fire overrides nominal cover and absent contact");
        f.Loaded=10;Check(!p.Evaluate(f,c),"incoming fire does not discard a well-loaded magazine");
        f.Loaded=9;f.SpareMagazines=1;Check(!p.Evaluate(f,c),"incoming fire does not consume last spare");
        p=new();f=F();Check(!p.Evaluate(f,new(){Enabled=false})&&p.Trace!.Reason=="quick-disabled","disabled keeps native route");Save();
        // A native success callback is not an observed drop or an observed completed reload.
        p=new();f=F();p.Evaluate(f,c);p.Request(10);Save();
        Check(p.Active&&p.Trace!.Requested&&!p.Trace.Started&&!p.Trace.Dropped,"pending owner established");
        Check(!p.Evaluate(F(),c)&&p.Trace.Episode==1,"overlapping caller cannot overwrite ownership");
        p.Drop("other-mag",30);Check(!p.Trace.Dropped,"ignore unrelated drops");p.Drop("old-mag",-1);Check(!p.Trace.Dropped,"ignore corrupt ammo evidence");
        p.Observe(10.1,true,true,true,"old-mag",7);Check(p.Trace.Started&&!p.Trace.ReplacementInserted,"animation observed before insertion");Save();
        p.Drop("old-mag",6);Check(p.Trace.Dropped&&p.Trace.DroppedRounds==6&&!p.Trace.Finished,"actual drop is not completion");Save();
        p.Callback(true);Check(p.Trace.CallbackSuccess&&!p.Trace.Finished,"callback is not completion");Save();
        p.Observe(10.5,true,true,true,"new-mag",31);Check(p.Active&&!p.Trace.Finished&&p.Trace.ReplacementInserted,"insertion not end of animation");Save();
        p.Observe(11,true,false,false,"new-mag",31);Check(!p.Active&&p.Trace.Finished&&p.Trace.Improved&&p.Trace.Dropped,"completed with confirmed cost");Save();
        p.Callback(false);Check(p.Trace.Finished,"late callback cannot undo terminal observation");
        p=new();p.Evaluate(F(),c);p.Request(10);p.Callback(true);p.Observe(10.2,true,false,false,"new-mag",31);
        Check(p.Trace!.Finished&&!p.Trace.Dropped&&!p.Trace.Started&&p.Trace.Reason=="quick-complete-drop-unconfirmed","fast observed completion without fabricating missed animation/drop");Save();
        foreach(var mode in new[]{"refused","timeout","clock","nan","changed","interrupted","ghost","raid-ended","deadline"})
        {
            p=new();p.Evaluate(F(),c);p.Request(10);
            if(mode is "interrupted" or "deadline"){p.Observe(10.1,true,true,true,"old-mag",7);p.Drop("old-mag",6);}
            if(mode=="refused")p.Callback(false);
            else if(mode is "ghost" or "raid-ended")p.End("quick-"+mode);
            else p.Observe(mode=="timeout"?13:mode=="clock"?9:mode=="nan"?double.NaN:mode=="deadline"?70:11,mode!="changed",mode=="deadline",mode=="deadline","old-mag",7);
            Check(!p.Active&&!p.Trace!.Finished&&!p.Trace.Improved&&p.Trace.ValidFor==0,"no false completion: "+mode);
            if(mode=="interrupted")Check(p.Trace.Dropped&&p.Trace.DroppedRounds==6,"interruption does not refund the cost");Save();
        }
        foreach(var change in new Action<QuickReloadFrame>[]{x=>x.Now=double.NaN,x=>x.Distance=-1,x=>x.ContactAge=double.PositiveInfinity,x=>x.Capacity=0,x=>x.Loaded=99,x=>x.OldRounds=99,x=>x.ReplacementRounds=0,x=>x.SpareMagazines=-1})
        {p=new();f=F();change(f);Check(!p.Evaluate(f,c),"invalid evidence declines");}
        // Ammo and threat boundary matrix: every successful choice still pays the resource/pressure constraints.
        for(int loaded=0;loaded<=31;loaded++)for(int spares=0;spares<5;spares++)foreach(var age in new[]{0,3,3.01,30})foreach(var distance in new[]{0,35,35.01,150})
        {p=new();f=F();f.Loaded=loaded;f.OldRounds=Math.Max(0,loaded-1);f.SpareMagazines=spares;f.ContactAge=age;f.Distance=distance;
            bool expected=loaded<=9&&spares>=2&&age<=3&&distance<=35;
            Check(p.Evaluate(f,c)==expected,"ammo/reserve/recent-range matrix");}
        using(var pe=new PEReader(File.OpenRead("Client/bin/Release/netstandard2.1/COMET.dll")))
        {
            var md=pe.GetMetadataReader();var type=md.TypeDefinitions.Select(md.GetTypeDefinition).Single(t=>md.GetString(t.Name)=="Plugin");
            MethodDefinition M(string name)=>type.GetMethods().Select(md.GetMethodDefinition).Single(m=>md.GetString(m.Name)==name);
            foreach(var gate in new[]{"IsPmcAi","get_Authority","Ghost","get_BotActive","get_IsDead","get_GameEnding","get_SAINLayersActive"})Check(ReloadTimingTests.Calls(pe,md,M("QuickReloadScope"),gate)==1,"compiled scope "+gate);
            Check(ReloadTimingTests.Calls(pe,md,M("PatchQuickReload"),"Patch")==2,"reload redirect plus physical drop observer");
            Check(ReloadTimingTests.Calls(pe,md,M("DispatchMagazineReload"),"QuickReloadMag")==1&&ReloadTimingTests.Calls(pe,md,M("DispatchMagazineReload"),"ReloadMag")==1,"single native fast route plus untouched normal fallback");
            foreach(var method in new[]{"QuickReloadFrameFor","DispatchMagazineReload","SampleQuickReload","ObserveDroppedReloadMagazine"})
                foreach(var forbidden in new[]{"get_RealDistance","get_EnemyPosition","get_BulletCount","CanReload","BotCancelReload","AddAmmoToMagazines","FastForward","SetSlotItem","ThrowItem"})
                    Check(ReloadTimingTests.Calls(pe,md,M(method),forbidden)==0,"no hidden position, synthetic ammo/drop or cancel "+method+forbidden);
            Check(ReloadTimingTests.Calls(pe,md,M("QuickReloadFrameFor"),"CheckMoveIgnoringTargetItem")==1&&ReloadTimingTests.Calls(pe,md,M("QuickReloadFrameFor"),"IsAmmoCompatible")==1,"spares must fit weapon and fire compatible ammo");
            Check(ReloadTimingTests.Calls(pe,md,M("ObserveDroppedReloadMagazine"),"get_Item")==1,"returned physical loot is checked against exact magazine");
        }
        // Verify the real game patch target still has exactly the native call redirected by W04.
        using(var pe=new PEReader(File.OpenRead(@"C:\Games\SPT-4.1\EscapeFromTarkov_Data\Managed\Assembly-CSharp.dll")))
        {
            var md=pe.GetMetadataReader();var type=md.TypeDefinitions.Select(md.GetTypeDefinition).Single(t=>md.GetString(t.Name)=="BotReload");
            var method=type.GetMethods().Select(md.GetMethodDefinition).Single(m=>md.GetString(m.Name)=="ReloadMagazine");
            Check(ReloadTimingTests.Calls(pe,md,method,"ReloadMag")==1,"native transpiler target exactly once");
        }
        File.WriteAllText("artifacts/w04-wire-traces.json",JsonSerializer.Serialize(wire));
        var active=wire.First(t=>t.GetProperty("dropped").GetBoolean()&&!t.GetProperty("callbackSeen").GetBoolean());
        var node=JsonSerializer.Deserialize<JsonObject>(active.GetRawText())!;node["action"]="StandAndShoot";
        File.WriteAllText("artifacts/w04-quick-reload.json",JsonSerializer.Serialize(new{botInstance="instance",quickReload=node,quickReloadConfig=c}));
        Console.WriteLine($"W04: {n} assertions passed.");return n;
    }
}
