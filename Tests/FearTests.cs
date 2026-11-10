using Comet.Core;
using Comet.Server;
using Newtonsoft.Json;
using Comet.Client;

internal static class FearTests
{
    internal static int Run()
    {
        int n=0;
        void Check(bool b,string why) { if(!b) throw new Exception("A06: "+why); n++; }
        void Near(double x,double y,string why) => Check(Math.Abs(x-y)<.00001,why+" ("+x+" vs "+y+")");
        var config=new CometConfig(); var c=config.Fear;
        Check(c.Enabled && c.Multiplier==1 && c.AllyDeath==.3 && c.DeathMemory==30 && c.Injury==.25 && c.Outnumbered==.15
            && c.HeavySuppression==.1 && c.RisePerSecond==.5 && c.RecoveryPerSecond==.05 && c.RecoveryDelay==3
            && c.PanicEnter==.75 && c.PanicExit==.55 && c.ScoreCap==15,"approved defaults and capped extra influence");
        var expected=new Dictionary<string,(double b,double s)> { ["Coward"]=(.35,1.5),["Timmy"]=(.2,1.3),["Rat"]=(.2,1.2),
            ["Normal"]=(.1,1),["SnappingTurtle"]=(.1,.9),["Chad"]=(.05,.6),["GigaChad"]=(0,.4),["Wreckless"]=(0,.3) };
        foreach(var p in expected) { Near(c.Base[p.Key],p.Value.b,p.Key+" baseline"); Near(c.Sensitivity[p.Key],p.Value.s,p.Key+" sensitivity"); }
        foreach(var edit in new Action<FearConfig>[] { x=>x.PanicExit=x.PanicEnter, x=>x.PanicEnter=double.NaN, x=>x.RisePerSecond=0,
            x=>x.RecoveryPerSecond=double.PositiveInfinity,x=>x.DeathMemory=0,x=>x.ScoreCap=26,x=>x.Sensitivity.Remove("Normal"),
            x=>x.Base["Rat"]=-1,x=>x.Multiplier=-1,x=>x.Injury=2 }) {
            var invalid=new FearConfig();edit(invalid);bool threw=false;try{invalid.Validate();}catch(ArgumentException){threw=true;}Check(threw,"invalid settings rejected");
        }
        var f=new FearModel(c);var s=new FearInput {Now=0};f.Update(s);
        Check(!f.ObserveDeath("unseen",0,false),"unperceived death ignored"); Near(f.Update(s).Target,.1,"no telepathic squad death");
        Check(f.ObserveDeath("seen",0,true),"witnessed death accepted");
        s.Health=.5;s.HeavySuppression=true;s.KnownEnemies=2;
        Near(f.Update(s).Target,.775,"approved Normal half-health example"); Near(f.Current.Level,.1,"no instantaneous fear jump");
        s.Now=.25;Near(f.Update(s).Level,.225,"rise limited to .5/sec");
        s.Now=.5;Near(f.Update(s).Level,.35,"consistent rise across samples");
        Check(!f.ObserveDeath("seen",.5,true),"same death cannot refresh stress");
        f.ObserveDeath("other",.5,true);Check(!f.ObserveDeath("seen",.75,true),"interleaved duplicate cannot refresh stress");
        s.Personality="Wreckless";Near(f.Update(s).Target,.2025,"approved Wreckless example");
        var old=f.Current; s.Now=double.NaN;Check(ReferenceEquals(old,f.Update(s)),"bad time ignored");
        s.Now=.25;Check(ReferenceEquals(old,f.Update(s)),"backward time ignored");s.Now=1;s.Health=double.NaN;Check(ReferenceEquals(old,f.Update(s)),"bad health ignored");
        var panic=new FearModel(c); var pInput=new FearInput {Personality="Coward",Now=0,Health=.2,KnownEnemies=3,HeavySuppression=true};
        panic.ObserveDeath("mate",0,true);panic.Update(pInput);
        for(int i=1;i<=8;i++){pInput.Now=i*.25;panic.Update(pInput);}
        Check(panic.Current.Panic && panic.Current.Transition=="panic-enter","panic entry after rise");
        pInput.Health=1;pInput.KnownEnemies=0;pInput.HeavySuppression=false;
        double before=panic.Current.Level;
        for(int i=9;i<=19;i++){pInput.Now=i*.25;panic.Update(pInput);}
        Near(panic.Current.Level,before,"recovery waits for three quiet seconds");
        for(int i=20;i<=160;i++){pInput.Now=i*.25;double prior=panic.Current.Level;var t=panic.Update(pInput);
            Check(prior-t.Level<=.012501,"recovery rate bounded"); if(t.Level>.55 && t.Level<.75)Check(t.Panic,"panic hysteresis");}
        Check(!panic.Current.Panic && panic.Current.Transition=="panic-exit" && panic.Current.PanicChangedAt.HasValue,"panic exit recorded with time");
        Near(panic.Current.Level,.35,"returns to Coward baseline");
        var injury=new FearModel(c);var inj=new FearInput {Now=0,Health=.1};injury.Update(inj);
        for(int i=1;i<=200;i++){inj.Now=i*.25;injury.Update(inj);} Near(injury.Current.Level,.325,"persistent wounds remain a floor");
        var ghostBefore=injury.Current.Level;injury.Pause(100);Near(injury.Current.Level,ghostBefore,"ghost suspension preserves per-bot state");
        inj.Now=100.25;inj.Health=0;Check(injury.Update(inj).Level-ghostBefore<=.125001,"no stress catch-up on wake");
        var disabled=new FearModel(new FearConfig {Enabled=false});Check(!disabled.ObserveDeath("mate",0,true),"disabled death ignored");
        Check(disabled.Update(new FearInput {Health=0,KnownEnemies=9,HeavySuppression=true}).Level==0 && !disabled.Current.Panic,"disabled neutral");
        var nums=new FearModel(c);var ni=new FearInput {KnownEnemies=3,AvailableAllies=2};Near(nums.Update(ni).Outnumbered,0,"self counts as one ally");
        ni.AvailableAllies=1;Near(nums.Update(ni).Outnumbered,.15,"perceived disadvantage");ni.CountsTruncated=true;Near(nums.Update(ni).Outnumbered,0,"incomplete counts do not invent disadvantage");
        Check(FearEvidence.KnownThreat(19,100,25) && !FearEvidence.KnownThreat(21,100,25) && !FearEvidence.KnownThreat(5,4000,25)
            && !FearEvidence.KnownThreat(double.NaN,10,25) && !FearEvidence.KnownThreat(1,double.NaN,25),"dated local contacts only");
        Check(FearEvidence.InView(15,20,40,60,40,false) && !FearEvidence.InView(15,120,40,60,40,false)
            && !FearEvidence.InView(15,20,10,60,40,false) && !FearEvidence.InView(15,20,40,60,40,true),"range FOV and flash perception gates");

        Situation Contact() => new() {Now=10,Target="first",IsAi=true,IsSupportedBot=true,IsActive=true,IsAuthority=true,HasContact=true,ContactAge=2,KnownDistance=10};
        var sit=Contact();sit.Fear=new FearTrace {Level=1,Panic=true};
        var candidates=new List<Candidate> {new(){Decision="Shoot",Score=70,Eligible=true},new(){Decision="Cover",Score=60,Eligible=true},
            new(){Decision="Push",Score=65,Eligible=true},new(){Decision="Hold",Score=50,Eligible=true},new(){Decision="FallBack",Score=99,Eligible=false}};
        FearModel.Adjust(candidates,sit,c);
        Near(candidates[0].Score,70,"panic does not reduce shooting");Near(candidates[1].Score,75,"bounded cover preference");
        Near(candidates[3].FearDelta,0,"open-ground hold receives no fear bonus");Check(!candidates[4].Eligible&&candidates[4].FearDelta==0,"fear cannot revive unsafe choice");
        sit.InCover=true;var hold=new List<Candidate>{new(){Decision="Hold",Score=50,Eligible=true}};FearModel.Adjust(hold,sit,c);Near(hold[0].Score,57.5,"protected hold preference");
        config.DecisionVariety.HiddenScoreGap=.3;config.DecisionVariety.VisibleScoreGap=.3;foreach(var key in config.TacticalMistakes.Chances.Keys.ToArray())config.TacticalMistakes.Chances[key]=1;
        var hidden=new HiddenEnemyDecision(config,"fear-integration");var sharedFear=new FearModel(c);sharedFear.Update(new FearInput{Now=9});
        sit=Contact();sit.Fear=sharedFear.Current;var calm=hidden.Evaluate(sit);
        sit.Now=10.1;sit.Fear=new FearTrace {Level=.9,Panic=true};var pan=hidden.Evaluate(sit);
        Check(pan.Changed && pan.Reason=="panic-best-feasible" && pan.Mixing != null && pan.Mixing.Roll==null && pan.Mixing.Mistake?.Roll==null,"panic breaks commitment and bypasses A04/A05");
        Check(pan.Candidates.Single(x=>x.Decision==pan.Decision.ToString()).Score==pan.Candidates.Where(x=>x.Eligible).Max(x=>x.Score),"best feasible A01 response");
        sit.Target="second";sit.Now=10.2;var changedTarget=hidden.Evaluate(sit);Check(changedTarget.Candidates.Any(x=>x.FearDelta!=0),"target handoff retains same fear snapshot");
        sit.Unavailable[Tactic.FallBack]="no-reachable-cover";sit.Now=10.3;Check(hidden.Evaluate(sit).Candidates.Single(x=>x.Decision=="FallBack").Eligible==false,"panic preserves native route gates");
        sit.IsSupportedBot=false;Check(hidden.Evaluate(sit).Decision==null,"non-PMC excluded");sit.IsSupportedBot=true;sit.Ghost=true;Check(hidden.Evaluate(sit).Decision==null,"ghost excluded");sit.Ghost=false;sit.IsAuthority=false;Check(hidden.Evaluate(sit).Decision==null,"client excluded");
        var vis=new VisibleSituation {Contact=Contact(),WeaponReady=true,CanShoot=true,ProbeReady=true,CoverReachable=true,PushReachable=true,ThreatCount=1,PushPathLength=10,CoverId="c"};
        vis.Contact.Visible=true;vis.Contact.Fear=new FearTrace {Level=.9,Panic=true};var ve=new VisibleEnemyDecision(config,"panic-visible");var vc=ve.Evaluate(vis);
        Check(vc.Mixing != null && vc.Mixing.Roll==null && vc.Mixing.Mistake?.Roll==null,"visible panic bypasses variety and mistakes");
        vis.CoverRouteRisk=.9;vis.Contact.Now+=.1;var blocked=ve.Evaluate(vis);Check(!blocked.Candidates.Single(x=>x.Decision=="Cover").Eligible,"panic cannot force unsafe cover route");
        vis.WeaponReady=false;Check(ve.Evaluate(vis).Decision==null,"native urgent hands/weapon work retains priority");
        var sight=new SightContinuity(config);var frame=new SightFrame {Target="enemy",Eligible=true,Visible=true,Observed=true,WeaponReady=true,Action="Shoot",Distance=15};
        for(int i=0;i<=5;i++){frame.Now=i*.1;sight.Evaluate(frame);}frame.Now=.6;frame.Visible=false;Check(sight.Evaluate(frame)!=null,"A03 armed before panic");
        frame.Now=.65;frame.Panic=true;Check(sight.Evaluate(frame)==null&&sight.Reason=="panic-reassessment","panic cancels A03 hold");
        frame.Urgent=true;frame.Now=.7;Check(sight.Evaluate(frame)==null&&sight.Reason=="sight-urgent-action","SAIN urgency still comes first");
        var random=new Random(606);var stress=new FearModel(c);var si=new FearInput();
        for(int i=0;i<10000;i++) {
            si.Now=i*.1;si.Health=random.NextDouble();si.HeavySuppression=random.Next(2)==1;si.KnownEnemies=random.Next(5);si.AvailableAllies=random.Next(3);
            var prior=stress.Current.Level;var st=stress.Update(si);
            Check(st.Level>=0 && st.Level<=1 && st.Target>=0 && st.Target<=1,"finite bounded fear over changing combat");
            if(i>0)Check(st.Level-prior<=.050001 && prior-st.Level<=.005001,"bounded temporal changes");
        }
        var path=Path.Combine(Path.GetTempPath(),"comet-a06-"+Guid.NewGuid()+"/config.json");
        try {var store=new ConfigStore(path);var edit=store.Read();edit.Fear.Multiplier=1.2;edit.Fear.Base["Normal"]=.15;edit.Fear.ScoreCap=12;
            store.Save(edit,edit.Revision);var saved=System.Text.Json.JsonSerializer.Deserialize<CometConfig>(store.ForGame())!;
            Check(saved.Fear.Multiplier==1.2 && saved.Fear.Base["Normal"]==.15 && saved.Fear.ScoreCap==12,"server persistence and game payload");
            var legacy=System.Text.Json.JsonSerializer.Deserialize<CometConfig>("{\"Enabled\":true}")!;legacy.Validate();Check(legacy.Fear.Enabled,"old config defaults migrate");
        } finally {Directory.Delete(Path.GetDirectoryName(path)!,true);}
        var json=JsonConvert.SerializeObject(new {fear=panic.Current,candidates},Diagnostics.Json);
        Check(json.Contains("\"panicChangedAt\"")&&json.Contains("\"scoreBeforeFear\"")&&json.Contains("\"fearDelta\""),"production JSONL fields for RR");
        Console.WriteLine("A06 fear: "+n+" checks passed (perception, dynamics, panic, A01-A05, server, telemetry).");
        return n;
    }
}
