using Comet.Core;
using Comet.Server;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.Json;

internal static class DoorMedicalTests
{
    public static int Run()
    {
        int n=0;void Check(bool ok,string why){n++;if(!ok)throw new Exception("D06: "+why);}
        void Invalid(Action a){try{a();}catch(ArgumentException){n++;return;}throw new Exception("D06 accepted invalid config");}
        var cfg=new CometConfig();cfg.Validate();
        Check(cfg.DoorFakeMedical.Enabled&&cfg.DoorFakeMedical.Stimulators&&cfg.DoorFakeMedical.Preference==50,"approved defaults");
        foreach(double x in new[]{-1d,101,double.NaN,double.PositiveInfinity})Invalid(()=>new DoorFakeMedicalConfig{Preference=x}.Validate());
        Invalid(()=>new CometConfig{DoorFakeMedical=null!}.Validate());
        var old=JsonSerializer.Deserialize<CometConfig>("{\"DoorFakeGrenade\":{\"Enabled\":false,\"Cooldown\":35}}")!;old.Validate();
        Check(!old.DoorFakeGrenade.Enabled&&old.DoorFakeGrenade.Cooldown==35&&old.DoorFakeMedical.Enabled,"D05 migration preserves shared settings");
        var dir=Path.Combine(Path.GetTempPath(),"comet-d06-"+Guid.NewGuid().ToString("N"));
        try{
            var store=new ConfigStore(Path.Combine(dir,"config.json"));var c=store.Read();c.DoorFakeMedical.Preference=70;c.DoorFakeMedical.Stimulators=false;
            store.Save(c,c.Revision);var saved=JsonSerializer.Deserialize<CometConfig>(store.ForGame())!;
            Check(saved.DoorFakeMedical.Preference==70&&!saved.DoorFakeMedical.Stimulators&&saved.DoorFakeGrenade.Chances["Chad"]==20,"server saves one shared configuration");
            c=store.Read();long revision=c.Revision;c.DoorFakeMedical.Preference=101;Invalid(()=>store.Save(c,revision));Check(store.Read().Revision==revision,"invalid draft never committed");
        }finally{Directory.Delete(dir,true);}
        foreach(string med in new[]{"heal","stim"}){
            for(int pref=0;pref<=100;pref+=10){int chosen=0;for(int i=0;i<1000;i++)if(DoorFeintChoice.Pick(true,med,i/1000d,pref)==med)chosen++;
                Check(chosen==pref*10,"exact conditional medical preference "+med+" "+pref);}
            Check(DoorFeintChoice.Pick(false,med,.99,0)==med,"available medical used when grenade unavailable");
        }
        Check(DoorFeintChoice.Pick(true,"",0,100)=="grenade"&&DoorFeintChoice.Pick(false,"",0,50)=="","only eligible family");
        Check(DoorFeintChoice.Pick(false,"surgery",0,100)=="","no surgery feint");
        foreach(double value in new[]{-1d,1,double.NaN,double.PositiveInfinity})Check(DoorFeintChoice.Pick(true,"heal",value,50)=="","invalid style roll");
        foreach(var pair in cfg.DoorFakeGrenade.Chances){int medical=0,grenade=0;
            for(int i=0;i<100;i++)for(int j=0;j<100;j++){
                var p=new DoorFakeGrenade();if(p.Begin("door","enemy","heard",pair.Key,new(0,0,3),2,100,i/100d,cfg.DoorFakeGrenade,true)){
                    if(DoorFeintChoice.Pick(true,"heal",j/100d,50)=="heal")medical++;else grenade++;
                }
            }
            Check(medical==pair.Value*50&&grenade==pair.Value*50,"shared personality probability, not doubled: "+pair.Key);
        }
        DoorFakeGrenade Start(string kind="heal"){
            var p=new DoorFakeGrenade();Check(p.Begin("door","enemy","heard","Chad",new(0,0,4),2,100,0,cfg.DoorFakeGrenade,true),"opportunity");
            p.Trace!.Kind=kind;p.Trace.Medical=new(){Kind=kind,Item="med",ResourceBefore=100,ResourceAfter=100};return p;
        }
        var shared=new DoorFakeGrenade();Check(!shared.Begin("door","enemy","heard","Chad",new(0,0,4),2,100,.99,cfg.DoorFakeGrenade,true),"declined roll");
        Check(!shared.Begin("door","enemy","heard","Chad",new(0,0,4),2,101,0,cfg.DoorFakeGrenade,true),"no second medical roll after grenade decline");
        cfg.DoorFakeGrenade.Enabled=false;Check(new DoorFakeGrenade().Begin("door","enemy","heard","Chad",new(0,0,4),2,100,0,cfg.DoorFakeGrenade,true),"medical independent enable switch");cfg.DoorFakeGrenade.Enabled=true;

        foreach(string kind in new[]{"heal","stim"}){
            var p=Start(kind);p.Draw("med","rifle",100);Check(!p.Trace!.Guard&&p.Trace.Grenade=="","medical never blocks grenade decisions");
            p.Callback(true,100.1);p.Observe(true,false,false,false,100.2);Check(p.Trace.Reason=="fake-medicine-in-hands","actual med observed");
            if(kind=="heal")p.Trace.Medical!.NativeStopRequested=true;
            p.RestoreRequested(kind=="stim",100.3);p.Observe(true,false,false,false,100.4);Check(p.Active&&!p.Trace.WeaponReady,"request not readiness");
            p.Observe(false,true,false,false,100.5);Check(p.Trace.State=="completed"&&p.Trace.WeaponReady,"native return with actual weapon proof");
            Check(p.Trace.HolsterAccepted==(kind=="stim"),"void StopUse never fabricated as selector acceptance");
            p=Start(kind);p.Draw("med","rifle",100);p.Callback(true,100.1);p.Observe(false,true,false,false,100.2);Check(p.Trace!.State=="ended","callback without observed med cannot complete");
            p=Start(kind);p.Draw("med","rifle",100);p.Observe(true,false,false,false,100.1);p.Trace!.Medical!.Sample(99,false,false,false);p.RestoreRequested(true,100.2);p.Observe(false,true,false,false,100.3);
            Check(p.Trace.State=="ended"&&p.Trace.Reason=="fake-real-medical-use-observed"&&p.Trace.Medical.ResourceAfter==99,"actual use preserved and not successful fake");
            p=Start(kind);p.Draw("med","rifle",100);p.Fail("test");p.YieldToMedical();p.Callback(true,104);Check(!p.Active&&p.Trace!.Medical!.MedicalHandoff,"late callback cannot undo medical handoff");
            p=Start(kind);p.Draw("med","rifle",100);p.Suspend(101);p.Resume(103);Check(!p.Trace!.Guard,"medical resume does not guard grenades");p.YieldToMedical();Check(!p.CanRestore(true,false,false,104),"SAIN urgent heal not cancelled");
            p=Start(kind);p.Draw("med","rifle",100);p.Observe(true,false,false,false,100.1);p.Observe(false,false,true,false,100.2);Check(p.Trace!.Reason=="fake-other-hands-took-over","foreign hands preserved");
            p=Start(kind);p.Draw("med","rifle",100);p.Observe(false,false,false,false,102.5);p.Observe(false,false,false,false,105.5);Check(p.Fault&&!p.Active&&p.Trace!.RecoveryFailed,"watchdog never completes animation");
            p.Clear();p.Callback(true,106);Check(p.Trace==null&&!p.Active,"disposal rejects delayed callback");
        }
        var trace=new DoorMedicalTrace{Kind="stim"};trace.Sample(null,true,true,false);
        Check(trace.UseCallbackSucceeded&&!trace.ConsumptionObserved,"successful use callback is not consumption");
        trace.Sample(null,false,false,true);Check(trace.ConsumptionObserved&&trace.ConsumptionEvidence=="native-medical-item-consumed","native consumed item tracked even without resource");
        trace=new(){Kind="heal",ResourceBefore=100};trace.Sample(double.NaN,false,false,false);Check(trace.ResourceAfter==null&&!trace.ConsumptionObserved,"invalid resource ignored");
        trace.Sample(99.9,false,false,false);Check(trace.ConsumptionObserved,"real resource reduction");trace.Sample(100,false,false,false);Check(trace.ConsumptionObserved,"consumption evidence not erased by later refill");
        Check(DoorMedicalTrace.RestoreCooldown(90,105,100,5,true,false,false,false)==100.5,"own cancellation delay repaired");
        Check(DoorMedicalTrace.RestoreCooldown(104,105,100,5,true,false,false,false)==104,"preexisting cooldown preserved");
        Check(DoorMedicalTrace.RestoreCooldown(110,105,100,5,true,false,false,false)==105,"repair never extends native cooldown");
        foreach(var flags in new[]{(false,false,false,false),(true,true,false,false),(true,false,true,false),(true,false,false,true)})
            Check(DoorMedicalTrace.RestoreCooldown(90,105,100,5,flags.Item1,flags.Item2,flags.Item3,flags.Item4)==null,"foreign/urgent/consumed/completion cooldown untouched");
        Check(DoorMedicalTrace.RestoreCooldown(90,106,100,5,true,false,false,false)==null,"other mod cooldown write preserved");
        foreach(double x in new[]{double.NaN,double.PositiveInfinity})Check(DoorMedicalTrace.RestoreCooldown(x,105,100,5,true,false,false,false)==null,"invalid cooldown rejected");

        using(var pe=new PEReader(File.OpenRead("Client/bin/Release/netstandard2.1/COMET.dll"))){
            var md=pe.GetMetadataReader();string Name(int token){var h=MetadataTokens.EntityHandle(token);if(h.Kind==HandleKind.MethodSpecification)token=MetadataTokens.GetToken(md.GetMethodSpecification((MethodSpecificationHandle)h).Method);return FloorHearingTests.Name(md,token);}
            List<string> Calls(string method)=>FloorHearingTests.ReadIl(pe.GetMethodBody(md.TypeDefinitions.Select(md.GetTypeDefinition).Single(x=>md.GetString(x.Name)=="Plugin").GetMethods().Select(md.GetMethodDefinition).Single(x=>md.GetString(x.Name)==method).RelativeVirtualAddress).GetILBytes()!)
                .Where(x=>x.op==OpCodes.Call||x.op==OpCodes.Callvirt).Select(x=>Name((int)x.operand!)).ToList();
            foreach(string name in new[]{"FindExistingEffect","get_CurrentSelfDecision","get_HealthStatus"})Check(Calls("FakeMedicalPriority").Contains(name),"real medical gate "+name);
            foreach(string name in new[]{"ShallStartUse","CanApplyItem","CanUseNow"})Check(Calls("FindFakeMedical").Contains(name),"native eligibility "+name);
            foreach(string name in new[]{"SetInHands","TryApplyToCurrentPart"})Check(Calls("DrawDoorFakeMedical").Contains(name),"native item action "+name);
            foreach(string name in new[]{"StopUse","TakePrevWeapon","YieldToMedical","RestoreCooldown"})Check(Calls("CancelDoorFakeMedical").Contains(name),"native cancellation/handoff "+name);
            Check(Calls("TickDoorFake").Contains("ObserveFakeMedical")&&Calls("TickDoorFake").Contains("RestoreFakeMedicalCompletionCooldown"),"observations and scoped completion repair wired");
            Check(Calls("PatchDoorFake").Contains("PatchDoorFakeMedical"),"consumption observation patch wired");
            Check(Calls("TryStartDoorFake").Count(x=>x=="Begin")==1,"exactly one shared opportunity");
            foreach(string method in new[]{"FindFakeMedical","DrawDoorFakeMedical","CancelDoorFakeMedical","FakeMedicalConsumed"})
            foreach(string forbidden in new[]{"SetRandomPartToHeal","ChangeHealth","ApplyDamage","AddItem","RemoveItem","SetEnemyVisible","PlaySound","get_EnemyPosition"})Check(!Calls(method).Contains(forbidden),"no injury/refund/consumption/omniscience side effects "+method+":"+forbidden);
        }
        Console.WriteLine($"D06: {n} checks passed (shared selection, medical priority, hands, consumption, cooldowns and native contracts). No Unity raid simulated.");return n;
    }
}
