using Comet.Core;
using Comet.Server;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;

internal static class DoorFakeTests
{
    public static int Run()
    {
        int n=0;void Check(bool ok,string why){n++;if(!ok)throw new Exception("D05: "+why);}
        void Invalid(Action a){try{a();}catch(ArgumentException){n++;return;}throw new Exception("D05 invalid config accepted");}
        var c=new DoorFakeGrenadeConfig();c.Validate();
        Check(c.Enabled&&c.DoorDistance==3&&c.ContactDistance==10&&c.ContactLifetime==15&&c.TargetCooldown==60&&c.Cooldown==20,"approved bounds and timings");
        Check(c.Chances.Values.SequenceEqual(new double[]{30,20,10,5,0,0,0,0}),"approved personality mix");
        foreach(double v in new[]{double.NaN,double.PositiveInfinity,-1,999})
        foreach(var change in new Action<DoorFakeGrenadeConfig>[]{x=>x.Chances["Normal"]=v,x=>x.DoorDistance=v,x=>x.ContactDistance=v,x=>x.ContactLifetime=v,x=>x.TargetCooldown=v,x=>x.Cooldown=v})
        {var cfg=new DoorFakeGrenadeConfig();change(cfg);Invalid(cfg.Validate);}
        Invalid(()=>new DoorFakeGrenadeConfig{Chances=null!}.Validate());Invalid(()=>new DoorFakeGrenadeConfig{Chances=new()}.Validate());
        Invalid(()=>new CometConfig{DoorFakeGrenade=null!}.Validate());
        var old=JsonSerializer.Deserialize<CometConfig>("{\"DoorStepPeek\":{\"Enabled\":false},\"DoorRunBy\":{\"Preference\":20}}")!;old.Validate();
        Check(old.DoorFakeGrenade.Chances["Chad"]==20&&!old.DoorStepPeek.Enabled&&old.DoorRunBy.Preference==20,"old config preserves previous settings");
        var dir=Path.Combine(Path.GetTempPath(),"comet-d05-"+Guid.NewGuid().ToString("N"));
        try{
            var store=new ConfigStore(Path.Combine(dir,"config.json"));var cfg=store.Read();cfg.DoorFakeGrenade.Chances["Chad"]=35;cfg.DoorFakeGrenade.DoorDistance=2;
            store.Save(cfg,cfg.Revision);var saved=JsonSerializer.Deserialize<CometConfig>(store.ForGame())!;
            Check(saved.DoorFakeGrenade.Chances["Chad"]==35&&saved.DoorFakeGrenade.DoorDistance==2,"server persistence and game response");
            cfg=store.Read();long rev=cfg.Revision;cfg.DoorFakeGrenade.Cooldown=1;Invalid(()=>store.Save(cfg,rev));Check(store.Read().Revision==rev,"invalid draft not committed");
        }finally{Directory.Delete(dir,true);}
        bool Begin(DoorFakeGrenade p,double now=100,double age=2,double roll=0,string target="enemy",string personality="Chad")=>p.Begin("door",target,"heard",personality,new(0,0,4),age,now,roll,c);
        DoorFakeGrenade New(){var p=new DoorFakeGrenade();Check(Begin(p),"eligible opportunity selected");return p;}
        foreach(var pair in c.Chances){int selected=0;for(int i=0;i<1000;i++)if(Begin(new(),roll:i/1000d,personality:pair.Key))selected++;Check(selected==pair.Value*10,"exact chance for "+pair.Key);}
        foreach(double roll in new[]{-1d,1,double.NaN,double.PositiveInfinity})Check(!Begin(new(),roll:roll),"invalid probability");
        foreach(double age in new[]{-1d,15.001,double.NaN,double.PositiveInfinity})Check(!Begin(new(),age:age),"fresh finite personal contact");
        Check(Begin(new(),age:15),"inclusive contact lifetime");Check(!Begin(new(),personality:"unknown"),"unknown personality cannot feint");
        c.Enabled=false;Check(!Begin(new()),"feature switch");c.Enabled=true;
        c.Chances["Chad"]=100;Check(Begin(new(),roll:.999999),"100 percent");c.Chances["Chad"]=20;
        var p=New();p.End("test");Check(!Begin(p,119.99,target:"other"),"global cooldown");Check(Begin(p,120,target:"other"),"global cooldown boundary");p.End("test");
        Check(!Begin(p,159.99),"same target cooldown");Check(Begin(p,160),"same target with newer information");p.End("test");
        // At the configurable boundaries the same remembered timestamp remains ineligible.
        c.ContactLifetime=30;c.TargetCooldown=30;var frozen=new DoorFakeGrenade();Check(Begin(frozen,100,age:0),"initial evidence");frozen.End("test");
        Check(!Begin(frozen,130,age:30),"same evidence cannot be replayed");Check(Begin(frozen,130,age:29.8),"genuinely newer contact after cooldown");c.ContactLifetime=15;c.TargetCooldown=60;
        var declined=new DoorFakeGrenade();Check(!Begin(declined,roll:.99)&&declined.Trace!.Reason=="fake-roll-declined","decline recorded");
        Check(!Begin(declined,101,roll:0),"no roll every frame");
        p=New();Check(!Begin(p,200,target:"other"),"no overlapping episode");p.End("test");
        for(int i=1;i<32;i++){Check(Begin(p,100+i*60,target:"enemy"+i),"bounded history entry");p.End("test");}
        Check(!Begin(p,100+32*60,target:"extra"),"full history fails closed, no unbounded allocation");
        Check(Begin(p,100+33*60,target:"enemy"),"existing targets still usable with new evidence");

        p=New();p.Observe(false,true,false,false,100.2);Check(p.Active&&p.Trace!.State=="validating","existing weapon cannot complete undrawn feint");
        p.Draw("nade","weapon",100.2);p.Observe(false,true,false,false,100.3);Check(p.Active&&!p.Trace!.WeaponReady,"old weapon while draw pending is not recovery");
        p.Callback(true,100.4);Check(p.Trace!.CallbackCompleted&&!p.Trace!.HandsObserved,"callback is not observation");
        p.Observe(true,false,false,false,100.5);Check(p.Trace!.State=="holstering"&&p.Trace!.HandsObserved&&p.Trace!.Guard,"actual grenade starts return immediately");
        Check(!p.CanRestore(false,false,false,100.5)&&!p.CanRestore(true,true,false,100.5)&&!p.CanRestore(true,false,true,100.5),"ownership, selector and throw guards");
        Check(p.CanRestore(true,false,false,100.5),"actual owned grenade can be restored");p.RestoreRequested(false,100.5);
        Check(!p.CanRestore(true,false,false,100.99)&&p.CanRestore(true,false,false,101),"native refusals throttled");p.RestoreRequested(true,101);
        Check(!p.CanRestore(true,false,false,101.6),"accepted animation not hammered");p.Observe(true,false,false,false,101.1);Check(p.Active&&!p.Trace!.WeaponReady,"acceptance is not readiness");
        p.Observe(false,true,false,false,101.2);Check(!p.Active&&p.Trace!.State=="completed"&&p.Trace!.WeaponReady&&!p.Trace!.Guard,"only real hands-to-weapon cycle succeeds");
        p=New();p.Draw("nade","weapon",100);p.Callback(true,100.1);p.Observe(false,true,false,false,100.2);Check(p.Trace!.State=="ended"&&p.Trace!.Reason=="fake-draw-not-observed","callback alone never proves a feint");
        p=New();p.Draw("nade","weapon",100);p.Callback(false,100.1);p.Observe(false,true,false,false,100.2);Check(!p.Active&&p.Trace!.State=="ended"&&p.Trace!.Interrupted.Length>0,"failed callback is failure");
        p=New();p.Draw("nade","weapon",100);p.Observe(true,false,false,false,100.1);p.Observe(false,true,false,false,100.2);Check(p.Trace!.Reason=="fake-native-weapon-takeover","unrequested native return not called successful feint");
        foreach(string reason in new[]{"visible-threat","medical-priority","incoming-fire","heard-approach","scope-disabled","frame-lost","adapter-error"}){
            p=New();p.Draw("nade","weapon",100);p.Abort(reason,100.1);p.Observe(true,false,false,false,100.2);Check(p.CanRestore(true,false,false,100.2),"interrupted draw still recovered: "+reason);
            p.RestoreRequested(true,100.2);p.Observe(false,true,false,false,100.4);Check(p.Trace!.State=="ended"&&p.Trace!.Interrupted==reason&&p.Trace!.WeaponReady,"abort remains abort after actual recovery");
        }
        p=New();p.Abort("unsafe",100);Check(!p.Active&&!p.Trace!.DrawRequested,"unsafe cover cannot draw");
        p=New();p.Observe(false,true,false,false,101.01);Check(p.Trace!.Reason=="fake-validation-timeout","bounded planning");
        p=New();p.Draw("nade","weapon",100);p.Observe(false,true,false,false,102.5);Check(p.Active&&p.Trace!.Interrupted=="fake-draw-timeout","draw watchdog initiates recovery, not success");
        p.Abort("repeated",104);p.Observe(false,true,false,false,105.5);Check(p.Fault&&!p.Active&&p.Trace!.RecoveryFailed,"repeated releases cannot prolong watchdog");
        Check(!Begin(p,300,target:"other"),"unsafe hands fault disables future feints for bot");
        p.Callback(true,106);Check(p.Active&&p.Trace!.State=="recovering","late callback receives bounded recovery");p.Observe(true,false,false,false,106.1);p.RestoreRequested(true,106.1);p.Observe(false,true,false,false,106.3);
        Check(!p.Active&&p.Trace!.WeaponReady&&p.Trace!.State=="ended"&&p.Fault,"late recovery remains recorded failure");
        p=New();p.Draw("nade","weapon",100);p.Observe(true,false,false,false,100.1);
        for(int i=0;i<6;i++){double at=100.1+i*.51;Check(p.CanRestore(true,false,false,at),"bounded refused attempt");p.RestoreRequested(false,at);}
        Check(!p.CanRestore(true,false,false,103.2),"six attempts maximum");p.Observe(true,false,false,false,103.2);Check(p.Fault&&!p.Active,"refused selector terminates cleanly");
        p=New();p.Draw("nade","weapon",100);p.Observe(true,false,false,false,100.1);p.Observe(false,false,true,false,100.2);Check(!p.Active&&p.Trace!.Reason=="fake-other-hands-took-over","medical or foreign hands preserved");
        p=New();p.Draw("nade","weapon",100);p.Observe(true,false,false,true,100.1);Check(!p.Active&&p.Fault&&!p.Trace!.Guard,"already-started native throw not cancelled");
        p=New();p.Draw("nade","weapon",100);p.Suspend(100.1);p.Abort("repeated-release",101);p.Observe(true,false,false,false,200);
        Check(p.Trace!.State=="suspended"&&!p.Trace!.Guard&&!p.CanRestore(true,false,false,200),"ghost/non-authority never changes hands or advances animation");
        p.Resume(200);p.Observe(true,false,false,false,200.1);Check(p.CanRestore(true,false,false,200.1),"resume recovers only observed owned grenade");p.RestoreRequested(true,200.1);p.Observe(false,true,false,false,200.3);Check(p.Trace!.State=="ended","suspended episode not success");
        foreach(double at in new[]{99d,double.NaN,double.PositiveInfinity}){p=New();p.Draw("nade","weapon",100);p.Observe(false,false,false,false,at);Check(p.Fault&&!p.Active,"invalid clocks bounded");}
        p=New();p.Draw("nade","weapon",100);p.Clear();p.Callback(true,101);Check(!p.Active&&p.Trace==null,"disposed callback cannot revive state");

        // Purposeful D05 hands time must not exhaust A12's Search stall budget.
        var progress=new PlanProgress(new());var frame=new ProgressFrame{Owned=true,HasThreat=true,Action="Search",Attempt="door",Intent="door"};
        for(int i=0;i<=16;i++){frame.Now=i*.25;progress.Observe(frame);}
        double stalled=progress.Current.StalledSeconds;p=New();p.Draw("nade","weapon",100);
        frame.Paused=p.Active;frame.PauseReason="progress-door-feint";
        for(int i=17;i<=24;i++){frame.Now=i*.25;progress.Observe(frame);}
        Check(progress.Current.State=="paused"&&!progress.Current.Reconsider&&progress.Current.StalledSeconds==stalled,"intentional feint preserves prior movement budget");
        p.Observe(true,false,false,false,100.1);p.RestoreRequested(true,100.1);p.Observe(false,true,false,false,100.5);
        frame.Paused=p.Active;bool stalledAfter=false;
        for(int i=25;i<=32;i++){frame.Now=i*.25;stalledAfter|=progress.Observe(frame);}
        Check(stalledAfter,"stall detection resumes after hands recovery");

        using(var pe=new PEReader(File.OpenRead("Client/bin/Release/netstandard2.1/COMET.dll"))){
            var md=pe.GetMetadataReader();List<string> Calls(string method)=>FloorHearingTests.ReadIl(pe.GetMethodBody(md.TypeDefinitions.Select(md.GetTypeDefinition).Single(x=>md.GetString(x.Name)=="Plugin").GetMethods().Select(md.GetMethodDefinition).Single(x=>md.GetString(x.Name)==method).RelativeVirtualAddress).GetILBytes()!)
                .Where(x=>x.op==OpCodes.Call||x.op==OpCodes.Callvirt).Select(x=>FloorHearingTests.Name(md,(int)x.operand!)).ToList();
            foreach(string name in new[]{"IsPmcAi","get_Authority","Ghost","get_BotActive","get_GameEnding"})Check(Calls("FakeScope").Contains(name),"native scope "+name);
            foreach(string name in new[]{"get_IsVisible","get_IsUnderFire","get_CurrentSelfDecision","get_CurrentSquadDecision","get_LastKnownPosition","get_IsGrounded"})Check(Calls("FakeGate").Contains(name),"native interruption "+name);
            Check(Calls("FakeCover").Contains("Raycast")&&Calls("FakeCover").Contains("get_LastKnownPosition"),"personal remembered position cover probes");
            foreach(string name in new[]{"SetInHands","CheckGrenade","FakeWeaponReady"})Check(Calls("DrawDoorFake").Contains(name),"native draw contract "+name);
            foreach(string name in new[]{"get_HandsController","TakePrevWeapon","get_IsAlive","Suspend","Resume","Observe"})Check(Calls("TickDoorFake").Contains(name),"actual lifecycle "+name);
            Check(Calls("Release").Contains("EndDoorFake")&&Calls("Update").Contains("RefreshDoorFake")&&Calls("DoorSearchStep").Contains("StepDoorFake"),"hooks wired");
            Check(Calls("StepDoor").Contains("TryStartDoorFake"),"D01 arrival opportunity");
            foreach(string method in new[]{"FinishDoorJump","FinishDoorRun","FinishDoorStep"})Check(!Calls(method).Contains("TryStartDoorFake"),"no automatic chain after "+method);
            foreach(string method in new[]{"TryStartDoorFake","FakeGate","FakeCover","TickDoorFake","DrawDoorFake"})
            foreach(string banned in new[]{"get_EnemyPosition","get_EnemyPlayer","FindObjectsOfType","ThrowGrenade","TryThrowGrenade","Shoot","SetEnemyVisible","PlaySound"})Check(!Calls(method).Contains(banned),"no omniscience, fake sound or throw "+method+":"+banned);
        }
        Console.WriteLine($"D05: {n} checks passed (chance, evidence, hands, recovery, failures, settings, native contracts). No Unity raid simulated.");return n;
    }
}
