using Comet.Core;
using Comet.Server;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;

internal static class CombatLeanTests
{
    public static int Run()
    {
        int n=0;void Check(bool ok,string why){if(!ok)throw new Exception("M04: "+why);n++;}
        void Invalid(Action action){try{action();}catch(ArgumentException){n++;return;}throw new Exception("M04 accepted invalid setting");}
        var c=new CombatLeanConfig();c.Validate();
        CombatLeanFrame Frame(double time=10)=>new(){Combat=new(){Now=Math.Round(time,6),Target="enemy",Distance=6,Known=new(0,0,6)},
            GeometryFresh=true,LeftSafe=true,RightSafe=true};
        CombatLean Begin(double burst=0,double count=0){var p=new CombatLean();Check(p.Begin(Frame(),c,0,0,burst,count,0,0),"valid uncovered stationary PMC selected");return p;}
        CombatLeanFrame Observe(CombatLean p,double time,bool pause=false){var f=Frame(time);f.FirePause=pause;
            f.SmoothedTilt=p.Trace!.Side*4;f.HeadLateral=p.Trace.Side*.1;return f;}
        Check(c.Enabled&&c.Chance==60&&c.HoldMin==.35&&c.HoldMax==1&&c.BurstIntervalMin==.25&&c.BurstIntervalMax==.4
            &&c.BurstChangesMin==2&&c.BurstChangesMax==3&&c.BurstChanceClose==45&&c.BurstChanceFar==15&&c.CloseDistance==7,"evasion defaults");
        Check(c.SequenceLimit==3&&c.CooldownMin==2&&c.CooldownMax==4&&c.MinDistance==3&&c.MaxDistance==25,"bounded independent sequences");
        var p=new CombatLean();Check(!p.Begin(Frame(),c,.6,0,0,0,0,0)&&p.Trace!.Reason=="lean-roll-declined","60 percent boundary");
        Check(!p.Begin(Frame(10.1),c,0,0,0,0,0,0),"no per-frame reroll after decline");
        var paused=Frame();paused.FirePause=true;p=new();
        Check(!p.Begin(paused,c,0,0,0,0,0,0)&&p.Trace==null&&p.NextAttempt==0,"do not start a new lean during M02 firing pause");
        paused.FirePause=false;Check(p.Begin(paused,c,0,0,0,0,0,0),"pause does not consume the next lean opportunity");
        p=Begin();Check(p.Trace!.Requested&&!p.Trace.HeadMotionObserved&&!p.Trace.TiltObserved,"request is not observed animation");
        p.Observe(Observe(p,10.1),c,0,0);Check(p.Trace.HeadMotionObserved&&p.Trace.TiltObserved&&p.Trace.HeadTravel>=.1,"root-relative bone and tilt evidence");
        var fixture=JsonSerializer.Serialize(new{combatLean=p.Trace},Json());
        for(int tick=2;tick<=59;tick++) {
            var f=Observe(p,10+tick*.05);p.Observe(f,c,0,.9);
            Check(p.Trace.BurstChanges<=2,"one two-change burst cap");Check(p.Deadline==13,"no extension by repeated holds");
        }
        Check(p.Active&&p.Trace.BurstChanges==2&&p.Trace.BurstAttempted,"burst completed then held lean");
        p.Observe(Observe(p,13),c,0,0);Check(!p.Active&&p.Trace.ValidFor==0&&p.Trace.Remaining==0&&p.NextAttempt==15,"deadline inclusive and cooldown from end");
        Check(!p.Begin(Frame(14.99),c,0,0,0,0,0,0)&&p.Begin(Frame(15),c,0,0,0,0,0,0),"bounded retry");
        p=Begin(0,.99);for(int i=1;i<60;i++)p.Observe(Observe(p,10+i*.05),c,0,.99);
        Check(p.Trace!.BurstChanges==3,"maximum three side changes");
        p=Begin(.45);for(int i=1;i<60;i++)p.Observe(Observe(p,10+i*.05),c,0,.99);
        Check(p.Trace!.BurstChanges==0&&p.Trace.Alternatives.Any(x=>x.Contains("roll declined")),"one close burst roll, exact boundary declines");
        var far=Frame();far.Combat.Distance=8;p=new();p.Begin(far,c,0,0,.15,0,0,0);
        Check(p.Trace!.BurstChance==15,"far burst chance");
        var singleSide=Frame();singleSide.LeftSafe=false;p=new();Check(p.Begin(singleSide,c,0,0,0,0,0,0)&&p.Trace!.Side==1,"blocked initial preference uses safe side");
        for(int i=1;i<=10;i++){var f=Observe(p,10+i*.05);f.LeftSafe=false;p.Observe(f,c,0,0);}
        Check(p.Active&&p.Trace!.BurstChanges==0&&p.Trace.Alternatives.Any(x=>x.Contains("obstructed")),"no burst into blocked side");
        var blocked=Observe(p,10.55);blocked.RightSafe=false;Check(!p.Observe(blocked,c,0,0)&&p.Trace.Reason=="lean-current-side-obstructed","new wall interrupts instead of clipping");
        p=Begin();for(int i=1;i<=8;i++)p.Observe(Observe(p,10+i*.05),c,0,0);
        Check(p.Trace!.State=="burst","burst starts after held side");int side=p.Trace.Side,changes=p.Trace.BurstChanges;
        for(int i=9;i<=22;i++)p.Observe(Observe(p,10+i*.05,true),c,0,0);
        Check(p.Active&&p.Trace.Side==side&&p.Trace.BurstChanges==changes&&p.Trace.State=="hold"&&p.Trace.FirePause,"M02 does not oscillate");
        for(int i=23;i<60;i++)p.Observe(Observe(p,10+i*.05),c,0,.99);
        Check(p.Trace.BurstChanges==changes&&p.Deadline==13,"interrupted burst is not restarted");
        p=Begin();for(int i=1;i<=10;i++)p.Observe(Frame(10+i*.1),c,0,0);
        Check(!p.Active&&!p.Trace!.TiltObserved&&!p.Trace.HeadMotionObserved&&p.Trace.Reason=="lean-animation-not-observed","frozen animation cannot produce fake evidence or endless spam");
        p=Begin();for(int i=1;i<=10;i++){var f=Observe(p,10+i*.1);f.HeadLateral=0;p.Observe(f,c,0,0);}
        Check(!p.Active&&p.Trace!.TiltObserved&&!p.Trace.HeadMotionObserved&&p.Trace.Reason=="lean-head-motion-not-observed","smoothed input without physical bone motion yields");
        foreach(var mutate in new Action<CombatLeanFrame>[] {
            f=>f.Combat.Pmc=false,f=>f.Combat.Authority=false,f=>f.Combat.Ghost=true,f=>f.Combat.Active=false,f=>f.Combat.Enabled=false,
            f=>f.Combat.Visible=false,f=>f.Combat.Source="shared",f=>f.Combat.Target="",f=>f.Combat.Action="Search",
            f=>f.Combat.Busy=true,f=>f.Combat.Urgent=true,f=>f.Combat.Ready=false,f=>f.Combat.Grounded=false,f=>f.Combat.Sprinting=true,f=>f.Combat.Immobile=true,
            f=>f.NativeLeanEnabled=false,f=>f.NativeLeanOwned=true,f=>f.CornerPeek=true,f=>f.GeometryFresh=false,
            f=>{f.LeftSafe=false;f.RightSafe=false;},f=>f.HeadLateral=double.NaN,f=>f.SmoothedTilt=double.PositiveInfinity,
            f=>f.Combat.ContactAge=-1,f=>f.Combat.Known=new(double.NaN,0,0),f=>f.Combat.Distance=2.99,f=>f.Combat.Distance=25.01
        }) {
            var f=Frame();mutate(f);Check(!new CombatLean().Begin(f,c,0,0,0,0,0,0),"invalid start blocked");
            p=Begin();f=Observe(p,10.1);mutate(f);Check(!p.Observe(f,c,0,0)&&p.Trace!.ValidFor==0,"immediate scope/priority interruption");
        }
        // Being near cover is not a requirement; the adapter separately yields to a native cover lean owner.
        var covered=Frame();covered.Combat.ProtectedCover=true;p=new();Check(p.Begin(covered,c,0,0,0,0,0,0),"no artificial cover requirement in policy");
        p=Begin();var other=Observe(p,10.1);other.Combat.Target="new";Check(!p.Observe(other,c,0,0),"contact identity change ends");
        p=Begin();other=Observe(p,10.1);other.Combat.Action="DogFight";Check(!p.Observe(other,c,0,0),"native owner change ends");
        p=Begin();Check(!p.Observe(Observe(p,10.31),c,0,0),"stale callbacks end");p=Begin();Check(!p.Observe(Observe(p,9),c,0,0),"clock reversal ends");
        foreach(var bad in new[]{double.NaN,double.PositiveInfinity,-1d,1d})Check(!new CombatLean().Begin(Frame(),c,bad,0,0,0,0,0),"bad random sample");
        foreach(var prop in typeof(CombatLeanConfig).GetProperties().Where(x=>x.PropertyType==typeof(double)))
            foreach(var value in new[]{double.NaN,double.PositiveInfinity,-1d,1001d}){var bad=new CombatLeanConfig();prop.SetValue(bad,value);Invalid(bad.Validate);}
        Invalid(()=>new CombatLeanConfig{BurstChangesMin=1}.Validate());Invalid(()=>new CombatLeanConfig{BurstChangesMax=4}.Validate());
        Invalid(()=>new CombatLeanConfig{BurstChangesMin=3,BurstChangesMax=2}.Validate());Invalid(()=>new CombatLeanConfig{HoldMin=1.1,HoldMax=1}.Validate());
        Invalid(()=>new CombatLeanConfig{BurstIntervalMin=.5,BurstIntervalMax=.3}.Validate());Invalid(()=>new CombatLeanConfig{CooldownMin=5,CooldownMax=2}.Validate());
        Invalid(()=>new CombatLeanConfig{MinDistance=12,MaxDistance=10}.Validate());Invalid(()=>new CometConfig{CombatLean=null!}.Validate());
        var legacy=JsonSerializer.Deserialize<CometConfig>("{}")!;legacy.Validate();Check(legacy.CombatLean.Chance==60,"old config gains defaults");
        var dir=Path.Combine(Path.GetTempPath(),"comet-m04-"+Guid.NewGuid().ToString("N"));var file=Path.Combine(dir,"config.json");
        try {
            var store=new ConfigStore(file);var cfg=store.Read();cfg.CombatLean.Chance=65;cfg.CombatLean.BurstChanceFar=5;store.Save(cfg,cfg.Revision);
            var saved=store.Read();Check(saved.CombatLean.Chance==65&&saved.CombatLean.BurstChanceFar==5,"UI server config roundtrip");
            string before=File.ReadAllText(file);saved.CombatLean.BurstChangesMax=9;Invalid(()=>store.Save(saved,saved.Revision));Check(File.ReadAllText(file)==before,"invalid save atomic");
        }finally{foreach(var path in new[]{file,file+".bak"})if(File.Exists(path))File.Delete(path);if(Directory.Exists(dir))Directory.Delete(dir);}
        using(var pe=new PEReader(File.OpenRead("references/sain-4.5.1/SAIN.dll"))) {
            var md=pe.GetMetadataReader();var type=md.TypeDefinitions.Select(h=>md.GetTypeDefinition(h)).Single(t=>md.GetString(t.Name)=="LeanClass");
            var methods=type.GetMethods().Select(h=>md.GetMethodDefinition(h)).ToArray();
            foreach(var name in new[]{"UpdateLeanSetting","ManualUpdate","SetTilt","FastLean","get_IsHoldingLean","get_LeanAngleValue"})Check(methods.Any(m=>md.GetString(m.Name)==name),"official SAIN lean hook contract "+name);
        }
        File.WriteAllText("artifacts/m04-combat-lean.json",fixture);
        Console.WriteLine($"M04: {n} checks passed (evasion, burst bounds, native priorities, physical observations, configuration)");return n;
    }
    private static JsonSerializerOptions Json()=>new(){IncludeFields=true,PropertyNamingPolicy=JsonNamingPolicy.CamelCase,WriteIndented=true};
}
