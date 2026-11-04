using Comet.Core;
using Comet.Server;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;

static class QuietApproachTests
{
    public static int Run()
    {
        int n=0;
        void Check(bool ok,string why){if(!ok)throw new Exception("P02: "+why);n++;}
        QuietFrame Frame()=>new(){Now=10,Target="enemy",TargetInstance=1,Action="Search",Scoped=true,Enabled=true,HasThreat=true,HasContact=true,Age=1,Path=20,Complete=true,Source="heard"};
        QuietApproach New()=>new(new(),()=>.1);
        var cfg=new CometConfig();cfg.Validate();
        Check(cfg.QuietApproach.WalkDistance==30&&cfg.QuietApproach.CrouchDistance==8&&cfg.QuietApproach.Budget==20,"approved defaults");
        Check(cfg.QuietApproach.ContactAge==10&&cfg.QuietApproach.LootBonus==.15,"fresh contact and modest loot bonus");
        var old=JsonSerializer.Deserialize<CometConfig>("{\"Enabled\":false,\"Revision\":3}")!;old.Validate();
        Check(!old.Enabled&&old.QuietApproach.Enabled&&old.Revision==3,"migration keeps global switch");
        foreach(var edit in new Action<QuietApproachConfig>[] {c=>c.Budget=double.NaN,c=>c.WalkSpeed=double.PositiveInfinity,c=>c.CrouchDistance=31,c=>c.CrouchSpeed=.8,c=>c.CrouchExitMargin=30,c=>c.Chances.Remove("Rat"),c=>c.Chances["Timmy"]=-1,c=>c.ResetSilence=1,c=>c.LootWindow=0})
        {var c=new QuietApproachConfig();edit(c);try{c.Validate();throw new Exception("invalid config accepted");}catch(ArgumentException){n++;}}
        var root=Path.Combine(Path.GetTempPath(),"comet-p02-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var store=new ConfigStore(Path.Combine(root,"config.json"));var save=store.Read();save.QuietApproach.WalkDistance=25;save.QuietApproach.Chances["Rat"]=.2;store.Save(save,save.Revision);
        var loaded=store.Read();Check(loaded.QuietApproach.WalkDistance==25&&loaded.QuietApproach.Chances["Rat"]==.2,"server persistence");
        foreach(var pair in new QuietApproachConfig().Chances)
        {
            var f=Frame();f.Personality=pair.Key;
            Check(new QuietApproach(new(),()=>pair.Value-.001).Evaluate(f).Active,"chance below threshold "+pair.Key);
            Check(!new QuietApproach(new(),()=>pair.Value).Evaluate(f).Active,"chance boundary "+pair.Key);
        }
        foreach(var edit in new Action<QuietFrame>[] {f=>f.Scoped=false,f=>f.Enabled=false,f=>f.Visible=true,f=>f.OtherVisibleThreat=true,f=>f.SinceSeen=2.99,f=>f.SinceFire=4.99,f=>f.Urgent=true,f=>f.Panic=true,f=>f.Suppressed=true,f=>f.Paused=true,f=>f.HasContact=false,f=>f.Age=10.01,f=>f.Age=-1,f=>f.Age=double.NaN,f=>f.Path=double.NaN,f=>f.Complete=false,f=>f.Path=34,f=>f.Blocked=true,f=>f.Action="Push",f=>f.Action="Retreat",f=>f.Action="Cover",f=>f.Action="ORBIT:Travel",f=>f.SinceSeen=double.NaN,f=>f.SinceFire=double.NaN})
        {var e=New();var f=Frame();Check(e.Evaluate(f).Active,"initial eligibility");f.Now+=.25;edit(f);Check(!e.Evaluate(f).Active,"immediate revocation");}
        {
            var e=New();var f=Frame();f.Path=30;Check(e.Evaluate(f).State=="walk","walk entry");
            f.Path=33;Check(e.Evaluate(f).Active,"walk exit margin");f.Path=33.01;Check(!e.Evaluate(f).Active,"walk exit");
            f.Path=31;Check(!e.Evaluate(f).Active,"cannot reenter at exit threshold");f.Path=8;Check(e.Evaluate(f).State=="crouch","close entry");
            f.Path=10;Check(e.Evaluate(f).State=="crouch","close hysteresis");f.Path=10.01;Check(e.Evaluate(f).State=="walk","leave crouch");
            f.Path=9;Check(e.Evaluate(f).State=="walk","no jitter into crouch");f.Path=8;e.Evaluate(f);f.TargetInstance=2;f.Path=9;
            Check(e.Evaluate(f).State=="walk","new enemy instance cannot inherit crouch hysteresis");
        }
        {
            int draws=0;var e=new QuietApproach(new(),()=>{draws++;return .72;});var f=Frame();
            Check(!e.Evaluate(f).Active,"initial decline");f.LootHeard=true;f.Now++;Check(!e.Evaluate(f).Active&&draws==1,"new sound does not reroll declined target");
            f.Target="second";Check(e.Evaluate(f).Active&&e.Current.LootBonus,"other target gets dated bonus at first draw");
            f.Target="enemy";Check(!e.Evaluate(f).Active&&draws==2,"target flip keeps previous decline");
            f.Target="second";f.LootHeard=false;Check(e.Evaluate(f).Chance==.85,"draw snapshot is stable");
            f.TargetInstance=2;e.Evaluate(f);Check(draws==3,"real target replacement independent");
        }
        foreach(double chance in new[]{0,.9,1})
        {var c=new QuietApproachConfig();c.Chances["Normal"]=chance;var f=Frame();f.LootHeard=true;var t=new QuietApproach(c,()=>.01).Evaluate(f);Check(t.Chance==(chance==.9?.95:chance),"zero disabled and cap never reduces custom chance");}
        {
            var e=New();var f=Frame();e.Evaluate(f);
            for(int i=1;i<=20;i++){f.Now=10+i;f.X=i;f.Target=i%2==0?"enemy":"second";f.Action=i%2==0?"Search":"MoveToEngage";e.Evaluate(f);}
            Check(e.Current.Reason=="quiet-budget-ended"&&e.Current.Used==20,"shared budget survives target and action changes");
            f.Now++;f.Target="third";Check(!e.Evaluate(f).Active,"new target cannot refund budget");
            f.HasThreat=false;f.HasContact=false;f.Now++;e.Evaluate(f);f.Now+=29;e.Evaluate(f);Check(e.Current.Used==20,"brief quiet interval does not reset");
            f.Now++;e.Evaluate(f);f.HasThreat=f.HasContact=true;f.Now++;Check(e.Evaluate(f).Active&&e.Current.Used==0,"new encounter after thirty seconds without threats");
        }
        {
            var e=New();var f=Frame();f.Executing=false;e.Evaluate(f);
            for(int i=0;i<30;i++){f.Now++;e.Evaluate(f);}Check(e.Current.Used==0&&e.Current.Active,"evaluation without application cannot consume time or stall");
            f.Executing=true;for(int i=0;i<5;i++){f.Now++;f.X=i%2*.1;e.Evaluate(f);}Check(e.Current.Reason=="quiet-no-progress","tiny oscillation is not progress");
            f.Now++;f.Target="other";f.X=10;Check(!e.Evaluate(f).Active,"stall cannot reset by target flip");
        }
        {
            var e=New();var f=Frame();e.Evaluate(f);for(int i=1;i<=19;i++){f.Now+=1;f.X+=.25;e.Evaluate(f);Check(e.Current.Active,"legitimate slow movement survives stall guard");}
            e.Stop(f.Now,"pause");f.Now+=30;f.X+=1;Check(e.Evaluate(f).Used==19,"paused period is not charged");
            f.Now--;Check(e.Evaluate(f).Reason=="quiet-clock-reset","clock rollback fails closed");
        }
        {
            var e=New();var f=Frame();for(int i=0;i<32;i++){f.Target="e"+i;e.Evaluate(f);}f.Target="overflow";
            Check(e.Evaluate(f).Reason=="quiet-target-memory-full"&&e.RememberedTargets==32,"bounded target memory without wholesale reroll");
            e.Clear();Check(e.RememberedTargets==0&&e.Evaluate(f).Active,"raid reset");
            foreach(double roll in new[]{double.NaN,-1,1,double.PositiveInfinity})Check(!new QuietApproach(new(),()=>roll).Evaluate(f).Active,"invalid RNG");
        }
        var memory=new OpportunityMemory();var oc=new OpportunityConfig{Enabled=true,LootWindow=2,ReactionDelay=0,SafetyMargin=0};
        memory.Hear("enemy",1,10,"looting",false,0,0,0,oc);Check(memory.Snapshot("enemy",1,10,oc).Cue!="looting","shared or unconfirmed loot not evidence");
        memory.Hear("enemy",1,11,"looting",true,0,0,0,oc);Check(memory.Snapshot("enemy",1,11.1,oc).State=="assess","personal sound provides cue independent from A10 toggle");
        Check(memory.Snapshot("enemy",1,13.1,oc).State!="assess","loot cue expires");
        memory.Hear("other",2,20,"looting",true,0,0,0,oc);memory.Hear("other",2,20.1,"shot",true,0,0,0,oc);
        Check(memory.Snapshot("other",2,20.2,oc).State!="assess","shot cancels loot opportunity");
        // Actual official SAIN binary, not copied method stubs.
        using var pe=new PEReader(File.OpenRead("references/sain-4.5.1/SAIN.dll"));var md=pe.GetMetadataReader();
        foreach(var spec in new[]{("MoveToEngageAction","Update",1),("SearchAction","Update",1),("SearchClass","Search",2),("SAINMoverClass","SetTargetMoveSpeed",1),("SAINMoverClass","SetTargetPose",1),("SAINMoverClass","RunToPoint",5)})
        {var type=md.TypeDefinitions.Select(md.GetTypeDefinition).Single(t=>md.GetString(t.Name)==spec.Item1);var method=type.GetMethods().Select(md.GetMethodDefinition).Single(m=>md.GetString(m.Name)==spec.Item2);
            Check(method.GetParameters().Select(md.GetParameter).Count(p=>p.SequenceNumber>0)==spec.Item3,"official native hook signature "+spec.Item2);}
        var trace=New().Evaluate(Frame());var json=Newtonsoft.Json.JsonConvert.SerializeObject(new{feature="P02",stage="evaluated",quiet=trace},Comet.Client.Diagnostics.Json);
        using(var doc=JsonDocument.Parse(json))Check(doc.RootElement.GetProperty("quiet").GetProperty("applied").GetBoolean()==false&&doc.RootElement.GetProperty("quiet").GetProperty("chance").GetDouble()==.7,"diagnostics distinguish evaluated from applied");
        Console.WriteLine($"P02: {n} checks passed (encounters, safety, progress, probabilities, config, native hooks).");return n;
    }
}
