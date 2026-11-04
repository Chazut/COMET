using Comet.Core;
using Comet.Server;
using System.Text.Json;

static class OpportunityTests
{
    public static int Run()
    {
        int n=0;
        void Check(bool b,string why) { if(!b)throw new Exception("A10: "+why);n++; }
        void Near(double a,double b,string why)=>Check(Math.Abs(a-b)<.00001,why);
        var cfg=new CometConfig();var c=cfg.Opportunity;
        cfg.Validate();Check(c.Enabled,"enabled default");
        Near(c.SurgeryWindow,12,"approved surgery window");Near(c.ReloadWindow,2.5,"reload");Near(c.HealWindow,4,"heal");
        Near(c.LootWindow,2,"loot");Near(c.DryFireWindow,1.5,"dry fire");Near(c.ScoreCap,20,"bonus");
        Near(c.ReactionDelay,.25,"reaction");Near(c.SafetyMargin,.5,"margin");Near(c.MaxPath,25,"path");
        OpportunityMemory Memory(string cue="surgery") { var m=new OpportunityMemory();m.Hear("e",1,10,cue,true,0,0,0,c);return m; }
        var m=Memory();var initial=m.Snapshot("e",1,10,c);
        Check(initial.State=="waiting","no instant reaction");
        Check(m.Snapshot("e",1,10.249,c).State=="waiting","reaction strict lower boundary");
        Check(m.Snapshot("e",1,10.25,c).State=="assess","reaction boundary");
        for(int i=1;i<=24;i++) {
            Check(!m.Hear("e",1,10+i,"surgery",true,100,0,0,c),"repeat does not start new episode");
            var repeated=m.Snapshot("e",1,10+i,c);Near(repeated.HeardAt,10,"first sound fixed");Near(repeated.X,0,"original position fixed");
            Check(repeated.Episode==initial.Episode,"episode fixed");
        }
        Check(m.Snapshot("e",1,34,c).State=="expired","continuous 24 s surgery does not refill window");
        Check(m.Hear("e",1,64,"surgery",true,1,0,0,c),"quiet rearm after 30 s");
        Check(m.Snapshot("e",1,64.5,c).Episode!=initial.Episode,"new episode after silence");
        Near(initial.HeardAt,10,"old snapshot frozen");
        foreach(string cue in new[]{"footstep","health-status","None","bullet-impact"}) {
            m=Memory();Check(!m.Hear("e",1,20,cue,true,0,0,0,c),"irrelevant cue ignored");Near(m.Snapshot("e",1,21,c).HeardAt,10,"irrelevant never extends");
        }
        foreach(string cue in new[]{"shot","sprint","grenade"}) {
            m=Memory();Check(m.Hear("e",1,11,cue,true,0,0,0,c),"perceived interruption");
            Check(m.Snapshot("e",1,11,c).State=="interrupted","cancelled even close");
            Check(!m.Hear("e",1,12,cue,true,0,0,0,c),"repeat interruption no event storm");
            Check(m.Hear("e",1,12.5,"reload",true,0,0,0,c),"new observed action after interruption");
        }
        m=Memory("dry-fire");Near(m.Snapshot("e",1,10.5,c).Window,1.5,"dry-fire distinct from reload");
        Check(m.Hear("e",1,11,"reload",true,0,0,0,c),"actual heard reload starts its own episode");
        Near(m.Snapshot("e",1,11.5,c).HeardAt,11,"reload starts at its own cue");
        Check(!m.Hear("e",1,10.5,"surgery",true,0,0,0,c),"older cue rejected");
        m=Memory();Check(!m.Hear("e",1,11,"shot",false,0,0,0,c),"unheard shot does not magically cancel");
        Check(m.Snapshot("e",1,11,c).State=="assess","unknown cancellation remains uncertain");
        Check(m.Snapshot("e",2,11,c).Episode==0,"instance reuse clears cue");
        Check(m.Snapshot("other",1,11,c).Episode==0,"target isolation");
        var otherObserver=new OpportunityMemory();Check(otherObserver.Snapshot("e",1,11,c).Episode==0,"observer isolation");
        for(int i=0;i<80;i++)m.Hear("e"+i,i,100+i,"heal",true,0,0,0,c);
        Check(m.Count==32,"bounded memory");m.Clear();Check(m.Count==0,"ghost/end cleanup");
        foreach(double bad in new[]{double.NaN,double.PositiveInfinity,-1d}) {
            m=new();Check(!m.Hear("e",1,bad,"heal",true,0,0,0,c),"invalid timestamp");
        }
        m=new();Check(!m.Hear("e",1,10,"heal",true,double.NaN,0,0,c),"invalid perception position");

        Situation S()=>new(){Now=10.5,Target="e",IsAi=true,IsPmc=true,IsAuthority=true,IsActive=true,HasContact=true,KnownDistance=8,ContactAge=.5};
        OpportunityTravel V()=>new(){Ready=true,Complete=true,WeaponReady=true,ThreatCount=1,PathLength=8,Corners=2,Risk=.1,RiskLimit=.65,WalkSpeed=3,SprintSpeed=6,Stamina=.8,CanSprint=true};
        OpportunityTrace Trace(Situation s, OpportunityTravel v,string cue="surgery") {var t=Memory(cue).Snapshot("e",1,s.Now,c);OpportunityModel.Assess(t,s,v,c);return t;}
        var s=S();var v=V();var t=Trace(s,v);Check(t.State=="usable"&&t.Eta>0,"safe surgery opportunity");
        double eta=t.Eta!.Value;
        v=V();v.CanSprint=false;Check(Trace(s,v).Eta>eta,"walking slower than limited sprint");
        v=V();v.Stamina=.2;Check(Trace(s,v).TravelMode=="walk","low stamina avoids sprint estimate");
        v=V();v.Visible=true;Check(Trace(s,v).TravelMode=="walk","visible SAIN dogfight does not sprint");
        v=V();v.Corners=6;v.VerticalTravel=4;Check(Trace(s,v).Eta>eta,"corners and vertical route cost time");
        v=V();v.WalkSpeed=1;v.CanSprint=false;Check(Trace(s,v).Eta>eta,"own impaired mobility changes ETA");
        foreach(var mutate in new Action<OpportunityTravel>[] {v=>v.Ready=false,v=>v.Complete=false,v=>v.WeaponReady=false,v=>v.ThreatCount=2,v=>v.ThreatCount=0,
            v=>v.ThreatsTruncated=true,v=>v.PathLength=26,v=>v.Risk=.9,v=>v.DoorBusy=true,v=>v.UnderFire=true,v=>v.PositionError=4,
            v=>v.WalkSpeed=0,v=>v.Stamina=double.NaN,v=>v.SprintSpeed=double.NaN,v=>v.Corners=0,v=>v.PathLength=double.NaN,v=>v.Risk=-1,v=>v.VerticalTravel=double.NaN}) {
            v=V();mutate(v);Check(Trace(S(),v).State=="rejected","unsafe/unknown travel refused");
        }
        foreach(var mutate in new Action<Situation>[] {s=>s.IsAi=false,s=>s.IsPmc=false,s=>s.IsAuthority=false,s=>s.IsActive=false,s=>s.Ghost=true,s=>s.HasContact=false,
            s=>s.Target="other",s=>s.Hurt=true,s=>s.LowAmmo=true,s=>s.Suppressed=true,s=>s.Fear=new(){Panic=true}}) {
            s=S();mutate(s);Check(Trace(s,V()).State=="rejected","scope and safety cannot be bypassed");
        }
        s=S();s.Now=21.6;v=V();v.PathLength=.1;Check(Trace(s,v).State=="expired","near contact never bypasses expiry");
        s=S();s.Now=21;v=V();v.PathLength=1;Check(Trace(s,v).Reason=="opportunity-too-late","ETA includes safety margin");
        foreach(string cue in new[]{"reload","heal","looting","dry-fire"}) {s=S();v=V();v.PathLength=.1;Check(Trace(s,v,cue).State=="usable","short safe "+cue);}
        s=S();t=Trace(s,V());s.Opportunity=t;
        var choices=new List<Candidate>{new(){Decision="Push",Score=80,Eligible=true,OpponentDelta=10},new(){Decision="Hold",Score=40,Eligible=true}};
        OpportunityModel.Adjust(choices,s,c);Near(choices[0].OpportunityDelta,15,"combined cap 25");Near(choices[1].Score,40,"only Push affected");
        Check(t.Bonus==15,"logged applied bonus");
        choices[0].Eligible=false;OpportunityModel.Adjust(choices,s,c);Check(t.Bonus==0&&t.Reason=="opportunity-push-unavailable","never grants eligibility");
        s=S();s.Opportunity=Trace(s,V());s.Now=11.1;OpportunityModel.Adjust(choices,s,c);Check(s.Opportunity.Bonus==0,"stale snapshot no bonus");
        var latch=new OpportunityRecheck();t.Bonus=20;
        Check(latch.Update(t)=="new-heard-opportunity","new episode interrupts commitment once");
        for(int i=0;i<30;i++)Check(latch.Update(t)=="","no per-tick reroll");
        t.Bonus=0;Check(latch.Update(t)=="opportunity-closed","closing interrupts once");
        for(int i=0;i<10;i++){t.Bonus=20;Check(latch.Update(t)=="","budget recovery no fresh episode");t.Bonus=0;Check(latch.Update(t)=="","budget flap no reroll storm");}
        t.State="expired";Check(latch.Update(t)=="opportunity-closed","expiry still interrupts after budget flap");
        t.Episode++;t.Bonus=20;Check(latch.Update(t)=="new-heard-opportunity","genuine new cue reopens");
        m=Memory();var prior=m.Snapshot("e",1,11,c);prior.Bonus=20;
        Check(OpportunityModel.CanContinueSight(prior,m.Snapshot("e",1,11.1,c)),"A03 retains same live episode");
        Check(!OpportunityModel.CanContinueSight(prior,m.Snapshot("e",1,22,c)),"A03 cannot extend expired opportunity");
        m.Hear("e",1,11.2,"shot",true,0,0,0,c);
        Check(!OpportunityModel.CanContinueSight(prior,m.Snapshot("e",1,11.3,c)),"A03 stops preserving rush after personally heard shot");
        Check(!OpportunityModel.CanContinueSight(prior,m.Snapshot("other",1,11.3,c)),"A03 cannot follow old cue to different enemy");

        cfg.DecisionVariety.Enabled=false;cfg.TacticalMistakes.Enabled=false;
        var hidden=new HiddenEnemyDecision(cfg);s=S();var first=hidden.Evaluate(s);s.Now+=.1;s.Opportunity=Trace(s,V());
        var next=hidden.Evaluate(s);Check(next.Changed&&next.Reason=="new-heard-opportunity","A01 fresh cue bypasses two-second commitment");
        s.Now+=.1;s.Opportunity=Trace(s,V());Check(!hidden.Evaluate(s).Changed,"A01 keeps choice within same opening");
        s.Opportunity.State="interrupted";s.Opportunity.Reason="heard-shot";
        next=hidden.Evaluate(s);Check(next.Changed&&next.Reason=="opportunity-closed","A01 interrupted opening reevaluates");
        var vis=new VisibleEnemyDecision(cfg);s=S();s.Visible=true;
        VisibleSituation VS()=>new(){Contact=s,WeaponReady=true,CanShoot=true,ProbeReady=true,PushReachable=true,ThreatCount=1,PushPathLength=8,ThreatSignature="e"};
        vis.Evaluate(VS());s.Now+=.1;s.Opportunity=Trace(s,V());Check(vis.Evaluate(VS()).Changed,"A02 reevaluates fresh cue");
        s.Now+=.1;s.Opportunity=Trace(s,V());Check(!vis.Evaluate(VS()).Changed,"A02 same cue no reroll");
        s.Opportunity.State="expired";Check(vis.Evaluate(VS()).Changed,"A02 expiry reevaluates");

        var json=JsonSerializer.Serialize(cfg);Check(JsonSerializer.Deserialize<CometConfig>(json)!.Opportunity.SurgeryWindow==12,"config round trip");
        Check(JsonSerializer.Deserialize<CometConfig>("{}")!.Opportunity.SurgeryWindow==12,"old config gets A10 defaults");
        foreach(var mutate in new Action<OpportunityConfig>[] {c=>c.SurgeryWindow=double.NaN,c=>c.ReloadWindow=0,c=>c.HealWindow=26,c=>c.ReactionDelay=0,
            c=>c.SafetyMargin=3,c=>c.MaxPath=-1,c=>c.ScoreCap=26,c=>c.CombinedScoreCap=20,c=>c.RearmSilence=1,c=>{c.LootWindow=1;c.ReactionDelay=.75;c.SafetyMargin=.5;}}) {
            var bad=new OpportunityConfig();mutate(bad);bool rejected=false;try{bad.Validate();}catch(ArgumentException){rejected=true;}Check(rejected,"invalid server settings rejected");
        }
        string dir=Path.Combine(Path.GetTempPath(),"COMET-A10-"+Guid.NewGuid().ToString("N"));var store=new ConfigStore(Path.Combine(dir,"config.json"));
        var draft=store.Read();draft.Opportunity.HealWindow=5;store.Save(draft,draft.Revision);
        Check(store.Read().Opportunity.HealWindow==5,"server persists A10");
        draft=store.Read();draft.Opportunity.SurgeryWindow=double.NaN;bool denied=false;
        try{store.Save(draft,draft.Revision);}catch(ArgumentException){denied=true;}
        Check(denied&&store.Read().Opportunity.SurgeryWindow==12,"invalid save atomic");
        var payload=JsonSerializer.Serialize(new{opportunity=Trace(S(),V()),candidates=choices});
        Check(payload.Contains("HeardAt")&&payload.Contains("Eta")&&payload.Contains("OpportunityDelta"),"telemetry carries evidence and score effect");
        Console.WriteLine($"A10 passed: {n} assertions (personal cues, deduplication, timing, safety, travel, commitments, config and telemetry).");
        return n;
    }
}
