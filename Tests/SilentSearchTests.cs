using Comet.Core;
using Comet.Server;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;

static class SilentSearchTests
{
    public static int Run()
    {
        int n=0;
        void Check(bool ok,string why){if(!ok)throw new Exception("P08: "+why);n++;}
        void Invalid(Action edit){try{edit();throw new Exception("P08 invalid config accepted");}catch(ArgumentException){n++;}}
        var c=new SilentSearchConfig();c.Validate();
        var expected=new Dictionary<string,double>{{"Normal",15},{"Chad",12},{"GigaChad",10},{"Wreckless",10},{"Rat",20},{"SnappingTurtle",20},{"Timmy",22},{"Coward",22}};
        Check(c.Enabled&&c.ScoreBonus==15,"enabled bounded default preference");
        foreach(var item in expected)Check(c.Personalities[item.Key].Delay==item.Value&&c.Personalities[item.Key].Enabled==(item.Key!="Coward"),"default "+item.Key);
        foreach(var v in new[]{double.NaN,double.PositiveInfinity,-1,25.1})Invalid(()=>new SilentSearchConfig{ScoreBonus=v}.Validate());
        foreach(var v in new[]{double.NaN,double.NegativeInfinity,0,120.1})Invalid(()=>{var x=new SilentSearchConfig();x.Personalities["Normal"].Delay=v;x.Validate();});
        Invalid(()=>new CometConfig{SilentSearch=null!}.Validate());
        Invalid(()=>new SilentSearchConfig{Personalities=null!}.Validate());
        Invalid(()=>{var x=new SilentSearchConfig();x.Personalities.Remove("Normal");x.Validate();});
        Invalid(()=>{var x=new SilentSearchConfig();x.Personalities["Normal"]=null!;x.Validate();});
        var old=JsonSerializer.Deserialize<CometConfig>("{\"Revision\":8,\"Enabled\":false}")!;old.Validate();
        Check(!old.Enabled&&old.Revision==8&&old.SilentSearch.Personalities["Timmy"].Delay==22,"old configuration gets defaults without overriding master switch");
        var dir=Path.Combine(Path.GetTempPath(),"comet-p08-"+Guid.NewGuid().ToString("N"));
        try{
            var store=new ConfigStore(Path.Combine(dir,"config.json"));var cfg=store.Read();cfg.SilentSearch.Personalities["Normal"].Delay=18;cfg.SilentSearch.Personalities["Coward"].Enabled=true;cfg.SilentSearch.ScoreBonus=7;
            store.Save(cfg,cfg.Revision);var loaded=store.Read();
            Check(loaded.SilentSearch.Personalities["Normal"].Delay==18&&loaded.SilentSearch.Personalities["Coward"].Enabled,"server persistence");
            var game=JsonSerializer.Deserialize<CometConfig>(store.ForGame())!;Check(game.SilentSearch.ScoreBonus==7&&game.SilentSearch.Personalities["Normal"].Delay==18,"authoritative response");
            loaded.SilentSearch.ScoreBonus=99;Invalid(()=>store.Save(loaded,loaded.Revision));Check(store.Read().SilentSearch.ScoreBonus==7,"invalid edit leaves saved configuration intact");
        }finally{if(Directory.Exists(dir))Directory.Delete(dir,true);}
        SilentSearchFrame F(double now=115)=>new(){Now=now,HeardAt=100,ContactAge=now-100,Target="heard",Personality="Normal",Instance=42,X=3,Y=2,Z=7};
        foreach(var item in expected){
            var f=F(100+item.Value);f.Personality=item.Key;
            var t=SilentSearchPolicy.Evaluate(f,c);Check(t.Eligible==(item.Key!="Coward"),"exact delay "+item.Key);
            f.Now-=.001;f.ContactAge-=.001;Check(!SilentSearchPolicy.Evaluate(f,c).Eligible,"not early "+item.Key);
        }
        foreach(var edit in new Action<SilentSearchFrame>[] {f=>f.Scoped=false,f=>f.Personal=false,f=>f.Seen=true,f=>f.HasContact=false,f=>f.Target="",f=>f.SearchAllowed=false,f=>f.AudioAllowed=false,f=>f.Arrived=true,f=>f.Unsafe=true,f=>f.Path=false,f=>f.Failed=true,f=>f.Personality="Unknown"})
        {var f=F();edit(f);var t=SilentSearchPolicy.Evaluate(f,c);Check(!t.Eligible&&t.Bonus==0,"guard: "+t.Reason);}
        foreach(var edit in new Action<SilentSearchFrame>[] {f=>f.Now=double.NaN,f=>f.HeardAt=116,f=>f.HeardAt=-1,f=>f.HeardAt=double.PositiveInfinity,f=>f.ContactAge=-1,f=>f.ContactAge=double.NaN,f=>f.Lifetime=0,f=>f.Lifetime=double.PositiveInfinity,f=>f.X=double.NaN,f=>f.Y=double.PositiveInfinity,f=>f.Z=double.NegativeInfinity})
        {var f=F();edit(f);Check(!SilentSearchPolicy.Evaluate(f,c).Eligible,"invalid evidence rejected");}
        Check(!SilentSearchPolicy.Evaluate(F(125),c).Eligible,"exact expiry");
        var lease=SilentSearchPolicy.Evaluate(F(124.9),c);Check(lease.Eligible&&Math.Abs(lease.ValidFor-.1)<1e-8,"short lease at expiry");
        var newerShared=F();newerShared.ContactAge=0;newerShared.Now=126;Check(!SilentSearchPolicy.Evaluate(newerShared,c).Eligible,"newer shared evidence cannot extend personal silence lifetime");
        var newSound=F();newSound.HeardAt=114;newSound.ContactAge=1;Check(SilentSearchPolicy.Evaluate(newSound,c).State=="waiting","new personal evidence resets silence");
        var t0=SilentSearchPolicy.Evaluate(F(),c);Check(t0.X==3&&t0.Y==2&&t0.Z==7&&!t0.Accepted&&!t0.ActionObserved&&!t0.MovementObserved,"snapshot is known place, not success");
        c.Enabled=false;Check(!SilentSearchPolicy.Evaluate(F(),c).Eligible,"disabled");c.Enabled=true;
        c.Personalities["Normal"].Delay=30;Check(!SilentSearchPolicy.Evaluate(F(125),c).Eligible,"long delay never extends memory");c.Personalities["Normal"].Delay=15;
        var reviews=new SilentSearchReviews();Check(reviews.TryReview(t0),"first evidence reconsidered");
        for(int i=0;i<200;i++)Check(!reviews.TryReview(SilentSearchPolicy.Evaluate(F(115+i*.01),c)),"same evidence not rerolled");
        var refreshed=F(116);refreshed.HeardAt=101;Check(reviews.TryReview(SilentSearchPolicy.Evaluate(refreshed,c)),"new personal evidence re-arms");
        Check(!reviews.TryReview(t0),"out-of-order old evidence rejected");
        for(int i=1;i<32;i++){var f=F();f.Target="other"+i;Check(reviews.TryReview(SilentSearchPolicy.Evaluate(f,c)),"bounded target slot");}
        var extra=F();extra.Target="overflow";Check(!reviews.TryReview(SilentSearchPolicy.Evaluate(extra,c))&&reviews.Count==32,"bounded cache refuses overflow instead of rerolling old targets");
        var replacement=F();replacement.Instance=43;Check(reviews.TryReview(SilentSearchPolicy.Evaluate(replacement,c)),"new enemy instance with same ID is independent");
        Check(!reviews.TryReview(new(){Eligible=true,Target=""}),"empty target refused");

        CometConfig Config(){var cfg=new CometConfig{HoldLimit=30,PlanLimit=60};cfg.DecisionVariety.Enabled=false;cfg.TacticalMistakes.Enabled=false;foreach(var key in cfg.Weights.Keys.ToArray())cfg.Weights[key]=key=="Hold"||key=="Search"?1:0;return cfg;}
        Situation S(double at)=>new(){Now=at,Target="heard",Personality="Normal",IsAi=true,IsSupportedBot=true,IsAuthority=true,IsActive=true,HasContact=true,ContactAge=at-100,KnownDistance=20,InCover=true};
        var config=Config();var engine=new HiddenEnemyDecision(config);Check(engine.Evaluate(S(100)).Decision==Tactic.Hold,"initial cover hold");
        var plain=S(115);Check(engine.Evaluate(plain).Decision==Tactic.Hold,"baseline hysteresis preserves hold");
        var after=S(115);after.SilentSearch=SilentSearchPolicy.Evaluate(F(),c);var chosen=engine.Evaluate(after);
        Check(after.SilentSearch.Reconsider&&chosen.Reason=="silent-search-delay-elapsed"&&chosen.Decision==Tactic.Search,"one due reevaluation can overcome hold inertia");
        Check(chosen.Candidates.Single(x=>x.Decision=="Search").SilentSearchDelta==15,"preference accounted separately");
        Check(engine.Evaluate(after).Reason!="silent-search-delay-elapsed"&&!after.SilentSearch.Reconsider,"no repeated opportunity while holding same search");
        var stronger=Config();stronger.Weights["Hold"]=2;var hold=new HiddenEnemyDecision(stronger).Evaluate(after);Check(hold.Decision==Tactic.Hold,"opportunity does not force Search over better alternative");
        var disabled=Config();disabled.SilentSearch.Enabled=false;var noBonus=new HiddenEnemyDecision(disabled).Evaluate(after);Check(noBonus.Candidates.All(x=>x.SilentSearchDelta==0)&&!after.SilentSearch.Reconsider,"master P08 disabled rejects stale eligible snapshot");
        var unavailable=S(115);unavailable.SilentSearch=SilentSearchPolicy.Evaluate(F(),c);unavailable.Unavailable[Tactic.Search]="native-refusal";var refused=new HiddenEnemyDecision(Config()).Evaluate(unavailable);
        Check(refused.Decision!=Tactic.Search&&!unavailable.SilentSearch.Reconsider&&refused.Candidates.Single(x=>x.Decision=="Search").SilentSearchDelta==0,"native refusal wins");
        var urgent=S(115);urgent.LowAmmo=true;urgent.SilentSearch=SilentSearchPolicy.Evaluate(F(),c);Check(new HiddenEnemyDecision(Config()).Evaluate(urgent).Decision!=Tactic.Search&&!urgent.SilentSearch.Reconsider,"low ammo wins");
        var early=Config();early.SilentSearch.Personalities["Normal"].Delay=1;var committed=new HiddenEnemyDecision(early);committed.Evaluate(S(100));var at1=S(101);at1.SilentSearch=SilentSearchPolicy.Evaluate(F(101),early.SilentSearch);
        committed.Evaluate(at1);Check(!at1.SilentSearch.Reconsider,"minimum commitment respected");at1.Now=102;at1.ContactAge=2;at1.SilentSearch=SilentSearchPolicy.Evaluate(F(102),early.SilentSearch);committed.Evaluate(at1);Check(at1.SilentSearch.Reconsider,"opportunity delayed, not consumed during commitment");
        using(var doc=JsonDocument.Parse(Newtonsoft.Json.JsonConvert.SerializeObject(new{feature="P08",stage="evaluated",silentSearch=t0},Comet.Client.Diagnostics.Json)))
            Check(doc.RootElement.GetProperty("silentSearch").GetProperty("silence").GetDouble()==15&&!doc.RootElement.GetProperty("silentSearch").GetProperty("actionObserved").GetBoolean(),"wire evidence/observation separation");
        using(var pe=new PEReader(File.OpenRead("references/sain-4.5.1/SAIN.dll"))){
            var md=pe.GetMetadataReader();TypeDefinition Type(string name)=>md.TypeDefinitions.Select(md.GetTypeDefinition).Single(x=>md.GetString(x.Name)==name);
            var place=Type("EnemyPlace");Check(place.GetFields().Select(md.GetFieldDefinition).Any(x=>md.GetString(x.Name)=="_timeLastUpdated"),"native stable personal evidence timestamp");
            var action=Type("SearchAction").GetMethods().Select(md.GetMethodDefinition).Single(x=>md.GetString(x.Name)=="Update");
            Check(FloorHearingTests.ReadIl(pe.GetMethodBody(action.RelativeVirtualAddress).GetILBytes()!).Any(x=>(x.op==OpCodes.Call||x.op==OpCodes.Callvirt)&&FloorHearingTests.Name(md,(int)x.operand!)=="Search"),"native action actually invokes hooked search");
            Check(Type("SearchClass").GetMethods().Select(md.GetMethodDefinition).Single(x=>md.GetString(x.Name)=="Search").GetParameters().Select(md.GetParameter).Count(x=>x.SequenceNumber>0)==2,"native search signature");
        }
        using(var pe=new PEReader(File.OpenRead("Client/bin/Release/netstandard2.1/COMET.dll"))){
            var md=pe.GetMetadataReader();var plugin=md.TypeDefinitions.Select(md.GetTypeDefinition).Single(x=>md.GetString(x.Name)=="Plugin");
            List<string> Names(string name)=>FloorHearingTests.ReadIl(pe.GetMethodBody(plugin.GetMethods().Select(md.GetMethodDefinition).Single(x=>md.GetString(x.Name)==name).RelativeVirtualAddress).GetILBytes()!).Where(x=>x.op==OpCodes.Call||x.op==OpCodes.Callvirt||x.op==OpCodes.Ldfld).Select(x=>FloorHearingTests.Name(md,(int)x.operand!)).ToList();
            var read=Names("ReadSilentSearch");Check(read.Contains("get_LastHeardPlace")&&read.Contains("get_LastKnownPlace")&&read.Contains("WillSearchFromAudio")&&read.Contains("WillSearchForEnemy"),"native permissions and personally known evidence read");
            foreach(var method in new[]{"ReadSilentSearch","SilentExecuting","SilentSearchStep","SilentSearchExecuted"})
                foreach(var forbidden in new[]{"CheckAddEnemy","AddEnemy","UpdatePersonalHeardPosition","UpdatePersonalSeenPosition","CalculatePath","FindObjectsOfType","get_EnemyPosition"})Check(!Names(method).Contains(forbidden),"no knowledge injection/scene scan: "+method+" "+forbidden);
            Check(Names("SilentSearchStep").Contains("RequestEndSprint"),"only native search speed capped");
            Check(Names("SilentSearchExecuted").Contains("Moved"),"movement measured, not inferred from decision");
        }
        Console.WriteLine($"P08: {n} checks passed (silence, personality thresholds, refusal, one reevaluation, engine, server persistence and native contracts).");return n;
    }
}
