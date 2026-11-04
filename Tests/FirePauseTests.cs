using Comet.Core;
using Comet.Server;
using System.Text.Json;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

internal static class FirePauseTests
{
    public static int Run()
    {
        int count=0;void Check(bool ok,string why){if(!ok)throw new Exception("M02: "+why);count++;}
        void Invalid(Action a){try{a();}catch(ArgumentException){count++;return;}throw new Exception("M02 accepted invalid settings");}
        var c=new FirePauseConfig();c.Validate();
        FirePauseFrame Frame(double now=10)=>new(){Now=now,SequenceDeadline=13,Distance=12,SinceHit=3,CompletedSteps=2,Target="enemy",
            Action="StandAndShoot",Weapon="rifle",Mode="automatic",CanFire=true,RouteFree=true,Speed=0,Known=new(0,0,12)};
        FirePause Begin(double roll=0){var p=new FirePause();p.Begin(1,"enemy",10,roll,0,c);return p;}
        FirePause Start(string mode="automatic") {var p=Begin();var f=Frame();f.Mode=mode;Check(p.TryStart(f,c),"eligible start");p.ConfirmRequest();return p;}
        FirePause Settled(){var p=Start();Check(p.Observe(Frame(10.04),c),"first stopped sample");Check(p.Observe(Frame(10.1),c)&&p.Trace!.StopObserved,"continuous stopped samples");return p;}
        Check(c.Enabled&&c.Chance==45&&c.MinDistance==8&&c.StepsBeforePause==2&&c.AutomaticMin==.2&&c.AutomaticMax==.3
            &&c.SingleMin==.3&&c.SingleMax==.45&&c.BrakeLimit==.2&&c.HitDelay==1,"approved defaults");
        var p=Begin(.45);Check(!p.Waiting&&!p.TryStart(Frame(),c)&&!p.Pausing,"chance boundary declines once");
        p=Begin(.44999);Check(p.Waiting,"below chance boundary selected");
        var f=Frame();f.CompletedSteps=1;Check(!p.TryStart(f,c)&&p.Waiting,"two actual steps required");
        Check(p.TryStart(Frame(),c)&&!p.TryStart(Frame(),c),"cannot request twice");
        Check(!p.Trace!.StopRequested&&!p.Trace.StopObserved,"planning is not an applied stop");
        p.Observe(Frame(10.02),c);Check(!p.Pausing&&p.Trace.Reason=="pause-request-unconfirmed","missing native confirmation ends");
        p=Start();f=Frame(10.04);f.Speed=.8;p.Observe(f,c);Check(!p.Trace!.StopObserved,"inertia does not masquerade as stationary");
        f=Frame(10.1);p.Observe(f,c);Check(!p.Trace.StopObserved,"first zero sample not enough");
        p.Observe(Frame(10.16),c);Check(p.Trace.State=="settled"&&Math.Abs(p.Trace.Remaining-.2)<.00001,"duration starts after actual stop");
        p.Shot(10.17);Check(p.Trace.Shots==1&&p.Trace.StationaryShots==1,"actual callback during steady window");
        p.Observe(Frame(10.24),c);p.Observe(Frame(10.32),c);p.Observe(Frame(10.361),c);
        Check(!p.Pausing&&p.Trace.State=="resumed"&&p.Trace.Reason=="pause-completed","pause ends on fixed clock");
        Check(!p.TryStart(Frame(11),c),"one pause per sequence even after success");p.Shot(11);Check(p.Trace.Shots==1,"late shot ignored");
        p=Start();p.Shot(10.01);Check(p.Trace!.Shots==1&&p.Trace.StationaryShots==0,"braking shot is not a stationary shot");
        for(int i=1;i<=5;i++){f=Frame(10+i*.04);f.Speed=.5;p.Observe(f,c);}
        Check(!p.Pausing&&!p.Trace.StopObserved&&p.Trace.Reason=="pause-brake-timeout","braking cannot freeze bot indefinitely");
        p=Start();p.Observe(Frame(10.05),c);p.Observe(Frame(10.05),c);Check(!p.Trace!.StopObserved,"repeated callbacks are not new evidence");
        p.Observe(Frame(10.1),c);Check(p.Trace.StopObserved,"stable minimum interval");
        p=Settled();f=Frame(10.15);f.Speed=.13;p.Observe(f,c);Check(!p.Pausing&&p.Trace!.Reason=="pause-motion-resumed","motion during pause aborts");
        p=Settled();p.Observe(Frame(10.201),c);Check(!p.Pausing&&p.Trace!.Reason=="pause-observation-gap","stale samples cannot prove stationary");
        p=Settled();p.Observe(Frame(9),c);Check(!p.Pausing,"clock reversal");
        p=Begin();f=Frame();f.SequenceDeadline=10.39;Check(!p.TryStart(f,c)&&!p.Waiting,"not enough parent time for brake plus pause");
        p=Settled();f=Frame(10.15);f.SequenceDeadline=10.14;
        // The original parent deadline is immutable; parent validity is enforced by M01 on every callback.
        f.Gate="movement-sequence-finished";Check(!p.Observe(f,c),"parent expiry wins");
        foreach(var mutate in new Action<FirePauseFrame>[] {
            x=>x.Gate="movement-out-of-scope",x=>x.Gate="movement-no-visible-contact",x=>x.Gate="movement-native-priority",
            x=>x.Gate="movement-mobility-unavailable",x=>x.Gate="movement-preserve-used-cover",x=>x.Gate="movement-contact-replaced",
            x=>x.Gate="movement-native-route-queued",x=>x.Gate="movement-adapter-error",
            x=>x.Action="Search",x=>x.Target="other",x=>x.CanFire=false,x=>x.Weapon="",x=>x.Mode="unknown",
            x=>x.Distance=7.99,x=>x.SinceHit=.99,x=>x.Speed=double.NaN,x=>x.Speed=-1,x=>x.Known=new(double.NaN,0,0),x=>x.ContactAge=-1
        }){
            p=Begin();f=Frame();mutate(f);Check(!p.TryStart(f,c),"refuse invalid opportunity");
            p=Start();f=Frame(10.05);mutate(f);Check(!p.Observe(f,c)&&!p.Pausing,"interrupt during braking");
            p=Settled();f=Frame(10.15);mutate(f);Check(!p.Observe(f,c)&&!p.Pausing,"interrupt during stationary phase");
        }
        foreach(var mutate in new Action<FirePauseFrame>[] {x=>x.RouteFree=false,x=>x.Weapon="different",x=>x.Mode="single"})
        {p=Settled();f=Frame(10.15);mutate(f);Check(!p.Observe(f,c)&&!p.Pausing,"ownership or weapon change aborts");}
        p=Settled();c.Enabled=false;Check(!p.Observe(Frame(10.15),c),"toggle revokes active pause");c=new();
        p=Start("single");f=Frame(10.04);f.Mode="single";p.Observe(f,c);f.Now=10.1;p.Observe(f,c);
        Check(p.Trace!.Duration==.3&&p.Trace.StopObserved,"single fire gets longer stabilization");
        p=new();p.Begin(1,"enemy",10,0,.999,c);p.TryStart(Frame(),c);Check(p.Trace!.Duration>.299&&p.Trace.Duration<.3,"duration upper bound");
        p=Settled();p.Observe(Frame(10.18),c);p.Observe(Frame(10.26),c);p.Observe(Frame(10.301),c);
        Check(p.Trace!.Reason=="pause-completed-without-shot"&&p.Trace.Shots==0,"no manufactured shooting success");
        foreach(var prop in typeof(FirePauseConfig).GetProperties().Where(x=>x.PropertyType==typeof(double)))
            foreach(double v in new[]{double.NaN,double.PositiveInfinity,-1d,1001d}){var bad=new FirePauseConfig();prop.SetValue(bad,v);Invalid(bad.Validate);}
        Invalid(()=>new FirePauseConfig{AutomaticMin=.4,AutomaticMax=.2}.Validate());Invalid(()=>new FirePauseConfig{StepsBeforePause=1}.Validate());
        Invalid(()=>new FirePauseConfig{SingleMin=.5,SingleMax=.3}.Validate());Invalid(()=>new CometConfig{FirePause=null!}.Validate());
        var legacy=JsonSerializer.Deserialize<CometConfig>("{}")!;legacy.Validate();Check(legacy.FirePause.Chance==45,"legacy default migration");
        var dir=Path.Combine(Path.GetTempPath(),"comet-m02-"+Guid.NewGuid().ToString("N"));var file=Path.Combine(dir,"config.json");
        try {var store=new ConfigStore(file);var settings=store.Read();settings.FirePause.Chance=60;settings.FirePause.SingleMax=.5;store.Save(settings,settings.Revision);
            Check(store.Read().FirePause.Chance==60&&store.Read().FirePause.SingleMax==.5,"server save reload");
            var before=File.ReadAllText(file);settings=store.Read();settings.FirePause.Chance=101;Invalid(()=>store.Save(settings,settings.Revision));
            Check(File.ReadAllText(file)==before,"invalid edit is atomic");
        }finally{foreach(var path in new[]{file,file+".bak"})if(File.Exists(path))File.Delete(path);if(Directory.Exists(dir))Directory.Delete(dir);}
        // Coupled M01/M02 timeline: real progress required; firing pause cannot cause a false stall or extend a sequence.
        var m=new CombatStep();var mc=new CombatStepConfig();CombatStepFrame Move(double now,double x)=>new(){Now=now,Target="enemy",Distance=12,Known=new(0,0,12),Position=new(x,0,0)};
        Check(m.Begin(Move(0,0),mc,0,.999,0),"parent started");m.Request(new(.8,0,0),"right",0,0,mc);
        m.Observe(Move(.2,.2),mc);m.Observe(Move(.4,.4),mc);Check(m.Trace!.CompletedSteps==1,"one real held direction");
        m.Request(new(-.4,0,0),"left",.4,0,mc);m.Observe(Move(.6,.2),mc);m.Observe(Move(.8,0),mc);
        Check(m.Trace.CompletedSteps==2,"second real held direction");double end=m.Deadline;
        for(int i=1;i<=7;i++)Check(m.Observe(Move(.8+i*.1,0),mc,true),"planned stop not stalled");
        m.ResumeAfterPause(1.5);Check(m.Deadline==end&&m.StepDeadline==1.5,"parent deadline unchanged and ready to move");
        m.Request(new(.8,0,0),"right",1.5,0,mc);for(int i=1;i<=5;i++)m.Observe(Move(1.5+i*.2,0),mc);
        Check(!m.Active&&m.Trace.Reason=="movement-stalled","unplanned stalling still enforced");
        m=new();m.Begin(Move(0,0),mc,0,0,0);m.Request(new(.8,0,0),"right",0,0,mc);m.Observe(Move(.4,0),mc);
        Check(m.Trace!.CompletedSteps==0,"requested but unmoved route never counts");
        using(var pe=new PEReader(File.OpenRead("references/sain-4.5.1/SAIN.dll"))){var md=pe.GetMetadataReader();
            var type=md.TypeDefinitions.Select(h=>md.GetTypeDefinition(h)).Single(t=>md.GetString(t.Name)=="PlayerComponent");
            Check(type.GetMethods().Any(h=>md.GetString(md.GetMethodDefinition(h).Name)=="OnMakingShot"),"official native shot notification contract");}
        p=Settled();p.Shot(10.1);
        File.WriteAllText("artifacts/m02-fire-pause.json",JsonSerializer.Serialize(new{firePause=p.Trace},new JsonSerializerOptions{IncludeFields=true,PropertyNamingPolicy=JsonNamingPolicy.CamelCase,WriteIndented=true}));
        Console.WriteLine($"M02: {count} checks passed (stationary evidence, interruption, shooting observation, parent movement and config)");return count;
    }
}
