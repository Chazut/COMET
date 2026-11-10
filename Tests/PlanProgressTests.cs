using Comet.Core;
using Comet.Server;
using System.Text.Json;

static class PlanProgressTests
{
    public static int Run()
    {
        int n=0;void Check(bool v,string message){if(!v)throw new Exception("A12: "+message);n++;}
        void Near(double a,double b,string message)=>Check(Math.Abs(a-b)<.00001,message+" ("+a+" vs "+b+")");
        ProgressFrame Frame(string action="Hold")=>new(){Now=0,Owned=true,HasThreat=true,Target="enemy",Action=action,Attempt="wall",Intent="wall"};
        void Advance(PlanProgress m,ProgressFrame f,double until){while(f.Now+.25<=until){f.Now+=.25;m.Observe(f);}}
        List<Candidate> Choices()=>new(){new(){Decision="Hold",Score=70,Eligible=true},new(){Decision="Search",Score=60,Eligible=true},new(){Decision="Push",Score=90,Eligible=false,Reason="unsafe"},new(){Decision="Cover",Score=80,Eligible=true}};
        var config=new CometConfig();var c=config.Progress;
        Check(c.Enabled&&c.HoldSoft==8&&c.HoldLimit==20&&c.StalledLimit==5&&c.ProgressMeters==.75&&c.ReviewInterval==12&&c.Retry==8&&c.RepeatRetry==15&&c.QuietReset==20,"approved defaults");
        var m=new PlanProgress(c);var f=Frame();m.Observe(f);Advance(m,f,8);var choices=Choices();m.Adjust(choices,f.Now);Near(choices[0].ProgressDelta,0,"no early fatigue");
        Advance(m,f,14);choices=Choices();m.Adjust(choices,f.Now);Near(choices[0].ProgressDelta,-12.5,"progressive hold fatigue");Check(!choices[2].Eligible&&choices[2].Reason=="unsafe","cannot resurrect unsafe actions");
        f.Target="other";f.Action="KeepAngle";Advance(m,f,20);Check(m.Current.Reconsider&&m.Current.Reason=="progress-hold-limit","A01/A03 and target handoff share wait budget");
        choices=Choices();m.Adjust(choices,f.Now);Check(!choices[0].Eligible&&choices[1].Eligible,"reject only sterile hold");
        f.CueAt=f.Now;f.CueX=20;m.Observe(f);f.Now+=.25;f.CueAt=f.Now;f.CueX=18;m.Observe(f);choices=Choices();m.Adjust(choices,f.Now);
        Check(choices[0].Eligible&&choices[0].ProgressReason=="progress-fresh-approach","dated approaching cue briefly justifies hold");Near(m.Current.HoldSeconds,20.25,"cue does not erase wait budget");
        Advance(m,f,22.5);choices=Choices();m.Adjust(choices,f.Now);Check(!choices[0].Eligible,"repeated cue cannot extend grace");
        foreach(var reason in new[]{"progress-hands-pause","progress-door-pause","progress-native-priority","progress-search-pause","progress-shooting"}){
            m=new(c);f=Frame();m.Observe(f);Advance(m,f,4);f.Paused=true;f.PauseReason=reason;Advance(m,f,34);Near(m.Current.HoldSeconds,4,"legitimate pause freezes budget "+reason);
            f.Paused=false;Advance(m,f,35);Near(m.Current.HoldSeconds,4.75,"resume excludes first paused interval");
        }
        m=new(c);f=Frame();m.Observe(f);Advance(m,f,10);f.Owned=false;Advance(m,f,30);Near(m.Current.HoldSeconds,10,"unaccepted/native actions don't consume COMET budget");
        f.HasThreat=false;Advance(m,f,50.25);Near(m.Current.HoldSeconds,0,"quiet encounter resets");
        m=new(c);f=Frame();m.Observe(f);Advance(m,f,10);f.Now=100;m.Observe(f);Near(m.Current.HoldSeconds,10,"loading/frame gap cannot charge wall clock");f.Now=1;m.Observe(f);Near(m.Current.HoldSeconds,0,"backward clock resets");
        f.Scoped=false;m.Observe(f);Check(m.Current.Reason=="progress-reset","ghost/non-authority scope clears state");
        foreach(var bad in new[]{double.NaN,double.PositiveInfinity}){f=Frame();f.X=bad;m.Observe(f);Check(m.Current.Reason=="progress-reset","bad positions fail safely");}

        foreach(var action in new[]{"Push","Search","FallBack","Cover","Flank","ContinuePush"}) {
            m=new(c);f=Frame(action);m.Observe(f);Advance(m,f,5);Check(m.Current.Reconsider&&m.Current.Failures==1&&m.Blocked("wall",5),"blocked "+action);
            Near(m.Current.RetryRemaining,8,"first cooldown");m.Fail("wall",6,"again");Check(!m.Blocked("wall",13),"blocked callbacks never extend cooldown");
            m.Fail("wall",13,"progress-stalled");Near(m.Current.RetryRemaining,15,"repeated failure cooldown");Check(!m.Blocked("other-cover",14),"other cover remains possible");
            m.CandidateAttempts["Search"]="wall";m.CandidateAttempts["Cover"]="other-cover";choices=Choices();m.Adjust(choices,14);Check(!choices[1].Eligible&&choices[3].Eligible,"destination exclusion not tactic ban");
        }
        m=new(c);f=Frame("Push");m.Observe(f);for(int i=1;i<=20;i++){f.Now=i*.25;f.Action=i%2==0?"Search":"FallBack";f.Target="target"+i;m.Observe(f);}
        Check(m.Current.Reconsider,"switching actions/targets cannot erase movement stall");
        m=new(c);f=Frame("Search");m.Observe(f);bool churnFailed=false;
        for(int i=1;i<=24;i++){f.Now=i*.25;f.Attempt="corner"+i;f.Intent="goal"+i;churnFailed|=m.Observe(f);}
        Check(churnFailed,"constant route/goal churn without movement cannot evade stall detection");
        m=new(c);f=Frame("Search");f.Intent="enemy-place";f.Attempt="search-corner";m.Observe(f);Advance(m,f,5);
        Check(m.Blocked("search-corner",6)&&m.Blocked("enemy-place",6)&&!m.Blocked("safe-cover",6),"failed intermediate route excludes same intent across actions");
        foreach(var motion in new[]{"wiggle","path-change","oscillate"}){
            m=new(c);f=Frame("Search");f.Route="route";f.Remaining=20;m.Observe(f);bool failed=false;
            for(int i=1;i<=80;i++){f.Now=i*.25;f.X=motion=="wiggle"?(i%2)*.3:motion=="oscillate"?i%2:0;f.Route=motion=="path-change"?"route"+i:"route";f.Remaining=20-i*.2;failed|=m.Observe(f);if(failed)break;}
            Check(failed,"sterile movement cannot earn perpetual progress: "+motion);
        }
        m=new(c);f=Frame("Push");f.Route="detour";f.Remaining=50;m.Observe(f);bool reviewed=false;
        for(int i=1;i<=160;i++){f.Now=i*.25;f.X=i*.1;f.Remaining=50+i*.1;Check(!m.Observe(f),"real detour continues");reviewed|=m.Current.State=="reviewed";}
        Check(reviewed&&!m.Blocked("wall",f.Now),"review does not expire a useful long move");
        m=new(c);f=Frame("Shoot");m.Observe(f);Advance(m,f,80);Check(!m.Current.Reconsider&&m.Current.HoldSeconds==0,"no arbitrary shooting timeout");
        m=new(c);f=Frame("Cover");m.Observe(f);Advance(m,f,3);f.Arrived=true;Advance(m,f,5);Check(m.Current.State=="arrived"&&!m.Current.Reconsider,"native arrival succeeds");
        f.Paused=true;f.PauseReason="progress-shooting";Advance(m,f,40);Check(!m.Current.Reconsider,"shooting from arrived cover stays purposeful");
        f.Paused=false;Advance(m,f,61);Check(m.Blocked("wall",f.Now),"arrived-but-idle cover cannot freeze forever");
        m=new(c);f=Frame("Search");m.Observe(f);Advance(m,f,4.75);f.Arrived=true;f.Now=5;m.Observe(f);Check(!m.Blocked("wall",5),"true arrival wins over stall deadline");
        var disabled=new PlanProgress(new(){Enabled=false});disabled.Fail("a",0,"bad");f=Frame();disabled.Observe(f);Check(!disabled.Blocked("a",0),"disabled neutral");

        // Raid391 Kelltic: firing from a reached cover must reset idle gaps,
        // while real inactivity and sterile travel retain their deadlines.
        foreach(var action in new[]{"Cover","FallBack","Hold"}) {
            m=new(c);f=Frame(action);f.Arrived=true;m.Observe(f);
            for(int i=1;i<=400;i++) {
                f.Now=i*.25;f.Paused=f.Productive=i%40==0;f.PauseReason="progress-shooting";
                Check(!m.Observe(f),"productive combat does not exhaust cover: "+action);
            }
            Check(!m.Blocked("wall",f.Now),"useful cover remains available");
            f.Paused=f.Productive=false;Advance(m,f,121);
            Check(m.Current.Reconsider||m.Blocked("wall",f.Now),"idle limit resumes after firing stops");
        }
        m=new(c);f=Frame("Push");m.Observe(f);Advance(m,f,4);
        f.Paused=f.Productive=true;Advance(m,f,8);f.Paused=f.Productive=false;Advance(m,f,10);
        Check(m.Blocked("wall",f.Now),"shooting cannot erase an unreachable travel route");

        {
            var late=new CometConfig();late.DecisionVariety.Enabled=false;late.TacticalMistakes.Enabled=false;
            Situation Contact(double age)=>new(){Now=100+age,Target="heard-enemy",HasContact=true,IsAi=true,IsSupportedBot=true,IsAuthority=true,IsActive=true,ContactAge=age,KnownDistance=15};
            var decision=new HiddenEnemyDecision(late).Evaluate(Contact(late.ContactLifetime-.12));
            Check(decision.Candidates.Where(x=>x.Decision is "Flank" or "Push" or "Search").All(x=>!x.Eligible&&x.Reason=="contact-window-too-short-to-start"),"raid391 no new movement for a contact expiring in 120ms");
            Check(decision.Candidates.Any(x=>x.Decision=="FallBack"&&x.Eligible),"protective retreat stays available for stale contact");
            foreach(var key in late.Weights.Keys.ToArray())late.Weights[key]=key=="Flank"?1:0;
            var engine=new HiddenEnemyDecision(late);
            Check(engine.Evaluate(Contact(late.ContactLifetime-2)).Decision==Tactic.Flank,"fresh contact permits flank");
            Check(engine.Evaluate(Contact(late.ContactLifetime-.12)).Decision==Tactic.Flank,"already executing flank keeps its remaining memory");
            Check(engine.Evaluate(Contact(late.ContactLifetime+.01)).Reason=="contact-expired","no artificial extension of memory");
        }
        // Production decision engines use the shared memory before candidate selection, including commitment.
        config.DecisionVariety.Enabled=false;config.TacticalMistakes.Enabled=false;
        foreach(var k in config.Weights.Keys.ToArray())config.Weights[k]=k=="Hold"||k=="Search"?1:0;
        m=new(c);f=Frame();m.Observe(f);Advance(m,f,6);
        Situation S()=>new(){Now=f.Now,Target="e",HasContact=true,IsAi=true,IsSupportedBot=true,IsAuthority=true,IsActive=true,ContactAge=2,KnownDistance=12,InCover=true,Progress=m};
        var hidden=new HiddenEnemyDecision(config);var s=S();Check(hidden.Evaluate(s).Decision==Tactic.Hold,"native safety allows useful hold");f.Now=6.25;m.Observe(f);s.Now=f.Now+5.5;Check(hidden.Evaluate(s).Decision==Tactic.Hold,"A12 replaces old wall-clock hold cap");
        Advance(m,f,20);s.Now=f.Now;Check(hidden.Evaluate(s).Decision==Tactic.Search,"hard hold exhaustion defeats commitment");
        m.Fail("goal",21,"progress-stalled");m.CandidateAttempts["Search"]="goal";s.Now=21;Check(hidden.Evaluate(s).Decision==null,"all unsafe/exhausted options yield to SAIN");
        var vis=new VisibleSituation{Contact=S(),WeaponReady=true,CanShoot=true,ProbeReady=true,CoverReachable=true,PushReachable=true,ThreatCount=1,PushPathLength=5,CoverId="wall"};
        vis.Contact.Visible=true;vis.Contact.Now=22;m.CandidateAttempts["Cover"]="goal";m.CandidateAttempts["Push"]="goal";
        var vc=new VisibleEnemyDecision(config).Evaluate(vis);Check(vc.Candidates.Where(x=>x.Decision=="Cover"||x.Decision=="Push").All(x=>!x.Eligible),"A02 uses same failed-goal memory");
        m=new(c);for(int i=0;i<1000;i++)m.Fail("point"+i,i,"failed");
        var memory=(System.Collections.IDictionary)typeof(PlanProgress).GetField("attempts",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.GetValue(m)!;
        Check(memory.Count<=32,"encounter destination memory bounded");
        foreach(var edit in new Action<ProgressConfig>[] {x=>x.HoldSoft=0,x=>x.HoldLimit=8,x=>x.StalledLimit=1,x=>x.ProgressMeters=double.NaN,x=>x.ReviewInterval=4,x=>x.Retry=31,x=>x.RepeatRetry=7,x=>x.QuietReset=double.PositiveInfinity}){
            var bad=new ProgressConfig();edit(bad);bool threw=false;try{bad.Validate();}catch(ArgumentException){threw=true;}Check(threw,"invalid settings rejected");
        }
        Check(JsonSerializer.Deserialize<CometConfig>("{}")!.Progress.HoldLimit==20,"old settings migrate defaults");
        string dir=Path.Combine(Path.GetTempPath(),"COMET-A12-"+Guid.NewGuid().ToString("N"));var store=new ConfigStore(Path.Combine(dir,"config.json"));
        var draft=store.Read();draft.Progress.Retry=9;store.Save(draft,draft.Revision);Near(store.Read().Progress.Retry,9,"server persistence");
        draft=store.Read();draft.Progress.HoldLimit=2;bool rejected=false;try{store.Save(draft,draft.Revision);}catch(ArgumentException){rejected=true;}
        Check(rejected&&store.Read().Progress.HoldLimit==20,"invalid save atomic");
        var json=Newtonsoft.Json.JsonConvert.SerializeObject(new{progress=m.Current,candidates=choices},Comet.Client.Diagnostics.Json);
        Check(json.Contains("retryRemaining")&&json.Contains("scoreBeforeProgress")&&json.Contains("progressReason"),"RR telemetry contract");
        Console.WriteLine($"A12 passed: {n} assertions (execution, progress, pauses, retries, scope, configuration and telemetry).");return n;
    }
}
