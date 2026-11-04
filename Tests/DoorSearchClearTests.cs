using Comet.Core;
using Comet.Server;
using System.Text.Json;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

internal static class DoorSearchClearTests
{
    public static int Run()
    {
        int n=0;void Check(bool value,string why){n++;if(!value)throw new Exception("D08: "+why);}
        void Invalid(Action action){try{action();}catch(ArgumentException){n++;return;}throw new Exception("D08 accepted invalid settings");}
        var config=new CometConfig();var c=config.DoorRoomClear;var d=config.DoorSearchClear;config.Validate();
        Check(d.Enabled&&d.Chance==70&&d.ContactLifetime==25&&d.AllowFeint,"approved D08 defaults");
        Check(c.ContactLifetime==10&&c.DepthMin==2&&c.DepthMax==3&&c.TargetCooldown==60,"shared approved timing and geometry");
        Invalid(()=>new CometConfig{DoorSearchClear=null!}.Validate());
        foreach(var x in new[]{-1d,101,double.NaN,double.PositiveInfinity})Invalid(()=>new DoorSearchClearConfig{Chance=x}.Validate(10));
        foreach(var x in new[]{0d,10,121,double.NaN,double.PositiveInfinity})Invalid(()=>new DoorSearchClearConfig{ContactLifetime=x}.Validate(10));
        Check(JsonSerializer.Deserialize<CometConfig>("{\"DoorRoomClear\":{\"ContactLifetime\":12}}")!.DoorSearchClear.Chance==70,"old config gains independent D08 defaults");
        var dir=Path.Combine(Path.GetTempPath(),"comet-d08-"+Guid.NewGuid().ToString("N"));
        try{
            var store=new ConfigStore(Path.Combine(dir,"config.json"));var change=store.Read();change.DoorSearchClear.Chance=55;change.DoorSearchClear.AllowFeint=false;
            store.Save(change,change.Revision);var saved=JsonSerializer.Deserialize<CometConfig>(store.ForGame())!;
            Check(saved.DoorSearchClear.Chance==55&&!saved.DoorSearchClear.AllowFeint&&saved.DoorRoomClear.GrenadePreference==50,"server persistence preserves D07");
            var rev=saved.Revision;saved.DoorSearchClear.ContactLifetime=9;Invalid(()=>store.Save(saved,rev));Check(store.Read().Revision==rev,"invalid fade cannot persist");
        }finally{Directory.Delete(dir,true);}
        bool Begin(DoorRoomClear p,double age=17.5,double now=100,double roll=0,string target="enemy",string personality="Normal",double memory=25)
            =>p.BeginSearch("door",target,"heard",personality,new(0,0,5),age,now,roll,c,d,memory,60);
        DoorRoomClear Start(){var p=new DoorRoomClear();Check(Begin(p),"search entry selected");return p;}
        foreach(var age in new[]{0d,1,9.9,10})Check(!Begin(new(),age),"fresh contact belongs to D07 only");
        foreach(var age in new[]{25d,26,120,double.NaN,double.PositiveInfinity,-1})Check(!Begin(new(),age),"expired or invalid contact refused");
        Check(!Begin(new(),18,memory:17),"global memory is not extended");
        Check(!new DoorRoomClear().BeginSearch("d","e","heard","Normal",new(0,0,5),17,100,0,c,d,25,16),"D01 age limit preserved");
        Check(DoorSearchClear.Gate(12,new(){Enabled=false},d,25,60)=="search-clear-disabled","shared entry toggle required");
        Check(DoorSearchClear.Gate(12,c,new(){Enabled=false},25,60)=="search-clear-disabled","D08 toggle");
        Check(DoorSearchClear.Gate(12,c,new(){Chance=0},25,60)=="search-clear-chance-disabled","zero disables preparation");
        foreach(var (age,expected) in new[]{(10d,1d),(12d,13d/15),(17.5,.5),(20d,1d/3),(25d,0d),(100d,0d)})
            Check(Math.Abs(DoorSearchClear.Factor(age,10,25)-expected)<1e-9,"linear fade at "+age);
        // Stable quantiles exercise production selection, not another implementation of the random choice.
        foreach(var (age,expected) in new[]{(12d,607),(17.5,350),(20d,234),(24d,47)}){
            int yes=0;for(int i=0;i<1000;i++)if(Begin(new(),age,roll:i/1000d))yes++;
            Check(yes==expected,"probability quantiles at "+age);
        }
        foreach(var personality in c.Chances.Keys){var p=new DoorRoomClear();Check(Begin(p,personality:personality),"already selected search may prepare: "+personality);Check(p.Trace!.Chance==35,"same search-preparation chance independent of aggression");}
        var roll=Start();roll.End("test");Check(!Begin(roll,18,100.5,0),"no second opportunity on same evidence");
        var recent=new DoorRoomClear();Check(!recent.Begin("d","enemy","heard","Normal",new(0,0,5),5,100,.99,c),"D07 declined");
        Check(!Begin(recent,17.5,112.5,0),"D07 declined roll cannot become a D08 retry");
        Check(!Begin(recent,17.5,130,0),"newer evidence still respects cooldown");
        Check(Begin(recent,17.5,161,0),"both cooldown and newer evidence unlock D08");recent.End("test");
        Check(!recent.Begin("d","enemy","heard","Chad",new(0,0,5),1,170,0,c),"shared cooldown also blocks D08 to D07");
        Check(recent.Begin("d","enemy","heard","Chad",new(0,0,5),1,222,0,c),"D07 resumes after cooldown plus fresh evidence");
        var longConfig=new DoorSearchClearConfig{ContactLifetime=120};var shared=new DoorRoomClearConfig{TargetCooldown=30};var history=new DoorRoomClear();
        Check(history.BeginSearch("d","enemy","heard","Normal",new(0,0,5),12,100,0,shared,longConfig,120,120),"long configured memory selection");history.End("done");
        Check(!history.BeginSearch("other","enemy","heard","Normal",new(0,0,6),43,131,0,shared,longConfig,120,120),"cooldown alone never refreshes the same evidence");
        var bounds=new DoorRoomClear();for(int i=0;i<32;i++){Begin(bounds,target:"enemy-"+i);bounds.End("done");}
        Check(!Begin(bounds,now:200,target:"overflow"),"bounded shared target cache");bounds.Clear();Check(Begin(bounds,now:200,target:"overflow"),"raid cleanup releases cache");
        var ageCheck=Start();
        Check(ageCheck.SearchContact(18,100.5,true,25,60)=="","unrefreshed evidence remains valid");
        Check(ageCheck.SearchContact(1,101,true,25,60)=="search-clear-new-evidence-native-review","fresh information at same coordinates does not silently turn D08 into D07");
        Check(ageCheck.SearchContact(18,100.5,false,25,60)=="search-clear-contact-forgotten","native forgetting wins");
        Check(ageCheck.SearchContact(25,107.5,true,25,60)=="search-clear-contact-expired","expiry while preparing");
        Check(ageCheck.SearchContact(18,100.5,true,17,60)=="search-clear-contact-expired","shortened memory cannot prolong a plan");
        Check(ageCheck.SearchContact(18,100.5,true,25,17)=="search-clear-contact-expired","shortened doorway lifetime wins");
        Check(ageCheck.SearchContact(19,100.5,true,25,60)=="search-clear-new-evidence-native-review","replaced/older observation yields as well");
        Check(ageCheck.SearchContact(17,99.5,true,25,60)=="search-clear-invalid-age","clock rewind rejected");
        var feint=Start();feint.Opened(100.2);feint.FeintStart("heal",100.3);feint.FeintStart("grenade",100.4);Check(feint.Trace!.Feint=="heal","only one shared feint");
        feint.FeintEnd(false,"consumed",101);Check(!feint.Active&&!feint.Trace.MovementRequested,"failed feint never proceeds");
        var entry=Start();entry.Opened(100.2);entry.Route(new(0,0,2),true);entry.Ready(100.3);
        Check(entry.Choose(true,true,"","native arc available",0,.5,100.4,c)&&entry.Trace!.Variant=="direct","even an eligible grenade cannot be selected for D08");
        Check(entry.Trace!.BlastPause==0&&entry.Trace.Alternatives.Any(x=>x.Contains("disabled for an old-contact search")),"no grenade wait; reason recorded");
        entry.ThrowRequest("nade",3,8,100.5);entry.ThrowResult(true,100.5);entry.Projectile("nade",7,100.5);entry.Explosion(7,100.6);
        Check(!entry.Trace.ThrowRequested&&!entry.Trace.ProjectileObserved&&!entry.Trace.ExplosionConfirmed,"foreign grenade callbacks cannot alter D08");
        entry.MoveRequest(new(0,0,-1),100.6);entry.MoveResult(true);entry.Movement(new(0,0,-1),false,false,true,101);
        Check(!entry.Trace.Arrived&&!entry.Trace.MovementObserved,"accepted move is not arrival");
        entry.Movement(new(0,0,2),true,false,true,101.5);Check(!entry.Trace.Arrived,"near point needs native completion");
        entry.Movement(new(0,0,2),true,true,true,102);Check(entry.Trace.Arrived&&entry.Trace.State=="completed","real completion through threshold");
        var unsafeRoute=Start();unsafeRoute.Opened(100);unsafeRoute.Ready(100);Check(!unsafeRoute.Choose(false,true,"blocked","safe arc",0,0,100,c)&&!unsafeRoute.Active,"no grenade fallback if direct route fails");
        foreach(string reason in new[]{"search-clear-contact-expired","search-clear-new-evidence-native-review","ghost","authority","target-changed","native-priority","route-replaced"}){
            var p=Start();p.Opened(100);p.Ready(100);p.End(reason);p.Choose(true,true,"","",0,0,101,c);p.MoveRequest(new(0,0,-1),101);p.MoveResult(true);p.Movement(new(0,0,2),true,true,true,102);
            Check(!p.Active&&!p.Trace!.Arrived&&!p.Trace.MovementRequested,"late callbacks cannot resume: "+reason);
        }
        using(var pe=new PEReader(File.OpenRead("Client/bin/Release/netstandard2.1/COMET.dll"))){
            var md=pe.GetMetadataReader();string Name(int token){var h=MetadataTokens.EntityHandle(token);if(h.Kind==HandleKind.MethodSpecification)token=MetadataTokens.GetToken(md.GetMethodSpecification((MethodSpecificationHandle)h).Method);return FloorHearingTests.Name(md,token);}
            List<string> Calls(string method)=>FloorHearingTests.ReadIl(pe.GetMethodBody(md.TypeDefinitions.Select(md.GetTypeDefinition).Single(x=>md.GetString(x.Name)=="Plugin").GetMethods().Select(md.GetMethodDefinition).Single(x=>md.GetString(x.Name)==method).RelativeVirtualAddress).GetILBytes()!)
                .Where(x=>x.op==OpCodes.Call||x.op==OpCodes.Callvirt).Select(x=>Name((int)x.operand!)).ToList();
            Check(Calls("TryStartDoorClear").Contains("Begin")&&Calls("TryStartDoorClear").Contains("BeginSearch"),"one executor starts both evidence modes");
            foreach(var method in new[]{"ClearGate","DirectClearSafety"})Check(Calls(method).Contains("SearchContact"),"memory guard during action and before entry: "+method);
            Check(Calls("TryStartDoorClear").Contains("DoorGate")&&Calls("ClearGate").Contains("FakeScope"),"native authority/PMC/ghost gates remain");
            Check(Calls("RecordDoorClear").Contains("get_DoorSearchClear"),"D08 settings in diagnostic transport");
        }
        var fixture=new{schema=1,feature="D08",stage="evaluated",decision="DoorSearchClear",reason=entry.Trace.Reason,doorRoomClear=entry.Trace,doorRoomClearConfig=c,doorSearchClearConfig=d};
        var options=new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase,WriteIndented=true};
        File.WriteAllText("artifacts/d08-fixture.json",JsonSerializer.Serialize(fixture,options));
        var restored=JsonSerializer.Deserialize<DoorClearTrace>(JsonSerializer.Serialize(entry.Trace));
        Check(restored!.Mode=="search"&&restored.Chance==35&&restored.SelectionContactAge==17.5&&restored.ContactLimit==25&&restored.Arrived,"full diagnostic evidence roundtrip");
        Console.WriteLine($"PASS D08: {n} checks (no Unity raid simulated)");return n;
    }
}
