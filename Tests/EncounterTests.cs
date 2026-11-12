using Comet.Core;
using Comet.Server;
using System.Text.Json;

internal static class EncounterTests
{
    internal static int Run()
    {
        int checks=0;
        void Check(bool ok,string name){checks++;if(!ok)throw new Exception("E03: "+name);}
        void Invalid(Action action){bool rejected=false;try{action();}catch(ArgumentException){rejected=true;}Check(rejected,"invalid config rejected");}
        var c=new EncounterConfig();c.Validate();
        Check(c.Enabled&&c.Appearances==3&&c.Window==20&&c.PositionTolerance==2&&c.HeightTolerance==1
            &&c.SightConfirmation==.2&&c.SightLoss==.5&&c.ScoreCap==15&&c.Retention==30,"approved defaults");
        foreach(var edit in new Action<EncounterConfig>[] {x=>x.Appearances=1,x=>x.Appearances=11,x=>x.Window=double.NaN,x=>x.PositionTolerance=0,x=>x.HeightTolerance=3,x=>x.SightConfirmation=0,x=>x.SightLoss=0,x=>x.ScoreCap=26,x=>x.Retention=double.PositiveInfinity})
        {var bad=new EncounterConfig();edit(bad);Invalid(bad.Validate);}
        var old=Newtonsoft.Json.JsonConvert.DeserializeObject<CometConfig>("{}")!;old.Validate();Check(old.Encounter.Appearances==3,"old config migration");
        var dir=Path.Combine(Path.GetTempPath(),"comet-e03-"+Guid.NewGuid().ToString("N"));
        try
        {
            var store=new ConfigStore(dir);var saved=store.Read();saved.Encounter.ScoreCap=9;store.Save(saved,saved.Revision);
            var loaded=Newtonsoft.Json.JsonConvert.DeserializeObject<CometConfig>(store.ForGame())!;
            Check(loaded.Encounter.ScoreCap==9,"server authoritative config");loaded.Encounter.Retention=-1;Invalid(()=>store.Save(loaded,loaded.Revision));
            Check(store.Read().Encounter.ScoreCap==9,"invalid config does not replace saved revision");
        }finally{if(Directory.Exists(dir))Directory.Delete(dir,true);}
        EncounterFrame F(double now,bool visible=true,string target="target")=>new(){Now=now,Visible=visible,Target=target,Instance=1,Geometry=true,
            Seen=new(10,0,10),Observer=new(0,0,0),Surface=7,Side=1,Edge=new(11,1,10),Normal=new(0,0,-1),ContactAge=visible?0:.1};
        EncounterTrace Run(EncounterMemory mem,int ticks=31,Action<EncounterFrame>? edit=null)
        {
            EncounterTrace t=new();for(int i=0;i<ticks;i++){var f=F(i*.1,i%12<5);edit?.Invoke(f);t=mem.Sample(f,c);}return t;
        }
        var m=new EncounterMemory();var ready=Run(m);
        Check(ready.Eligible&&ready.Count==3&&ready.Bonus==15,"three separate personally seen returns qualify");
        Check(ready.Observations.Length==3&&ready.X==10&&ready.Y==0&&ready.Z==10,"frozen opening and actual observation dates");
        var single=new EncounterMemory();EncounterTrace last=new();for(int i=0;i<200;i++)last=single.Sample(F(i*.1),c);
        Check(last.Count==1&&!last.Eligible,"continuous sight is one appearance");
        var flicker=Run(new(),60,f=>f.Visible=(int)Math.Round(f.Now*10)%3!=2);
        Check(flicker.Count<=1&&!flicker.Eligible,"visibility flicker is not repeated peeking");
        Check(!Run(new(),31,f=>f.Visible=false).Eligible,"hearing alone cannot build evidence");
        Check(Run(new(),31,f=>f.Geometry=false).Count==0,"open ground or deferred geometry cannot create a pattern");
        var away=Run(new(),31,f=>{if(!f.Visible)f.Watching=false;});Check(!away.Eligible&&away.Count<3,"looking away is not a hide-and-return cycle");
        var gap=new EncounterMemory();Run(gap,5);gap.Sample(F(2,false),c);last=gap.Sample(F(2.1),c);
        for(int i=22;i<=26;i++)last=gap.Sample(F(i*.1),c);
        Check(last.Count==1,"sampling gap does not manufacture a return");
        var different=Run(new(),31,f=>f.Seen=new(10+(int)(f.Now/1.2)*4,0,10));Check(!different.Eligible,"different locations do not accumulate");
        Check(!Run(new(),31,f=>f.Seen=new(10,(int)(f.Now/1.2)*2,10)).Eligible,"different floors do not accumulate");
        Check(!Run(new(),31,f=>f.Observer=new((int)(f.Now/1.2)*3,0,0)).Eligible,"changed vantage invalidates inference");
        Check(!Run(new(),31,f=>f.Surface=7+(int)(f.Now/1.2)).Eligible,"different geometry invalidates inference");
        foreach(var change in new Action<EncounterFrame>[] {f=>f.Geometry=false,f=>f.Surface=8,f=>f.Side=-1,f=>f.PersonalLatest=false,f=>f.Seen=new(20,0,10),f=>f.ContactAge=25,f=>f.HasContact=false,f=>f.Scoped=false,f=>f.Watching=false})
        {var mem=new EncounterMemory();Run(mem);var f=F(3.1,false);change(f);Check(!mem.Sample(f,c).Eligible,"hidden guard invalidates preference");}
        foreach(var change in new Action<EncounterFrame>[] {f=>f.Now=double.NaN,f=>f.ContactAge=-1,f=>f.ContactLifetime=double.NaN,f=>f.Observer=new(double.NaN,0,0),f=>f.Seen=new(double.NaN,0,0)})
        {var mem=new EncounterMemory();Run(mem);var f=F(3.1);change(f);Check(!mem.Sample(f,c).Eligible,"invalid evidence cannot influence");}
        var identity=new EncounterMemory();Run(identity);var other=F(3.1,false,"other-human");Check(!identity.Sample(other,c).Eligible,"Fika/player IDs isolated");
        other=F(3.2,false);other.Instance=2;Check(!identity.Sample(other,c).Eligible,"reused profile/new native instance isolated");
        Check(!new EncounterMemory().Sample(F(3.1,false),c).Eligible,"different observer has no inherited knowledge");
        var native=new EncounterMemory();Run(native);native.Retain(new HashSet<string>());Check(native.Count==0,"native forgetting removes inactive targets too");
        var bounded=new EncounterMemory();for(int i=0;i<40;i++)bounded.Sample(F(0,true,"id"+i),c);Check(bounded.Count==8,"bounded observer memory");
        bounded.Clear();Check(bounded.Count==0,"raid/ghost cleanup");
        var expiry=new EncounterMemory();Run(expiry);Check(!expiry.Sample(F(34,false),c).Eligible&&expiry.Count==0,"no retention across quiet encounter gaps");
        var disabled=new EncounterConfig{Enabled=false};Check(!m.Sample(F(3.1,false),disabled).Eligible&&m.Count==0,"disable clears memory");
        Situation S()=>new(){Now=3,Target="target",IsAi=true,IsSupportedBot=true,IsAuthority=true,IsActive=true,HasContact=true,ContactAge=.1,KnownDistance=14,
            Encounter=ready.Copy()};
        var cfg=new CometConfig();cfg.DecisionVariety.Enabled=false;cfg.TacticalMistakes.Enabled=false;
        var selection=new HiddenEnemyDecision(cfg).Evaluate(S());var hold=selection.Candidates.Single(x=>x.Decision=="Hold");
        Check(hold.EncounterDelta==0,"bounded preference does not force Hold over better alternatives");
        foreach(var edit in new Action<Situation>[] {s=>s.Hurt=true,s=>s.Suppressed=true,s=>s.LowAmmo=true,s=>s.Unavailable[Tactic.Hold]="no-safe-hold",s=>s.Encounter!.Target="someone-else"})
        {var s=S();edit(s);Check(new HiddenEnemyDecision(cfg).Evaluate(s).Candidates.Single(x=>x.Decision=="Hold").EncounterDelta==0,"safety and target identity win");}
        cfg.Weights["Hold"]=0;Check(new HiddenEnemyDecision(cfg).Evaluate(S()).Candidates.Single(x=>x.Decision=="Hold").EncounterDelta==0,"disabled action stays disabled");
        cfg.Weights["Hold"]=3;Check(new HiddenEnemyDecision(cfg).Evaluate(S()).Candidates.Single(x=>x.Decision=="Hold").EncounterDelta==0,"source utility has no COMET encounter bonus");
        cfg.Encounter.Enabled=false;Check(new HiddenEnemyDecision(cfg).Evaluate(S()).Candidates.Single(x=>x.Decision=="Hold").EncounterDelta==0,"disabled module ignores stale context");
        cfg.Encounter.Enabled=true;var engine=new HiddenEnemyDecision(cfg);var a=S();var first=engine.Evaluate(a);a.Now+=.1;var next=engine.Evaluate(a);
        Check(!next.Changed,"existing commitment prevents per-frame rerolls");
        using(var doc=JsonDocument.Parse(Newtonsoft.Json.JsonConvert.SerializeObject(new{encounter=ready},Comet.Client.Diagnostics.Json)))
            Check(doc.RootElement.GetProperty("encounter").GetProperty("observations").GetArrayLength()==3,"wire preserves dates and count");
        var copied=ready.Copy();copied.Observations[0]=-9;Check(ready.Observations[0]>=0,"detached diagnostic snapshot");
        var example=S();var choice=new HiddenEnemyDecision(new CometConfig()).Evaluate(example);example.Encounter!.Choice=choice.Decision?.ToString()??"";
        example.Encounter.Alternatives=choice.Candidates.Select(x=>x.Decision+": "+(x.Eligible?"eligible":x.Reason)).ToArray();
        Directory.CreateDirectory("artifacts");File.WriteAllText("artifacts/e03-encounter.json",Newtonsoft.Json.JsonConvert.SerializeObject(new{encounter=example.Encounter,encounterConfig=c},Comet.Client.Diagnostics.Json));
        Console.WriteLine($"E03: {checks} checks passed");return checks;
    }
}
