using Comet.Core;
using Comet.Server;
using System.Text.Json;

static class SainHandoffTests
{
    public static int Run()
    {
        int n=0;
        void Check(bool pass,string why){if(!pass)throw new Exception("A13: "+why);n++;}
        HandoffCue Cue(double at=9,bool visible=false,double x=10,string target="enemy")=>new(){Target=target,At=at,Visible=visible,X=x};
        var c=new HandoffConfig();var h=new SainHandoff(c);
        h.Accepted(10,"A01","Search",Cue());
        Check(h.TryGrace(10.1,Cue(),true,true),"safe transient path interruption continues");
        for(int i=1;i<5;i++)
        {h.Accepted(10.1+i*.1,"A01","Search",Cue());Check(h.TryGrace(10.1+i*.1,Cue(),true,true),"acceptance does not end grace");}
        Check(!h.TryGrace(10.61,Cue(),true,true),"repeated acceptance cannot extend grace");
        Check(h.Begin(10.61,"no-feasible-option","unavailable",Cue()),"handoff emitted once");
        Check(h.Current.State=="native-pending"&&!h.Current.NativeObserved,"release is not proof of native acceptance");
        for(int i=1;i<8;i++){Check(!h.Begin(10.61+i*.1,"no-feasible-option","unavailable",Cue()),"repeated callbacks quiet");}
        Check(h.Blocked(11.5,Cue()),"native gets a retry window");
        Check(!h.Blocked(11.62,Cue()),"callbacks do not extend retry");
        Check(h.ObserveNative(11.63,"Retreat","Reload","None"),"postfix records actual native choice");
        Check(h.Current.NativeSelf=="Reload"&&h.Current.NativeCombat=="Retreat"&&h.Current.FromAction=="Search","source and native outcome retained");
        Check(!h.ObserveNative(11.64,"Search","None","None"),"no per-tick native spam");
        h.Accepted(11.7,"A02","Shoot",Cue(11.6,true));Check(!h.Yielded&&!h.Current.NativeObserved,"new accepted action ends fallback");
        foreach(var unsafeCase in new[]{"not-temporary","unsafe","different-target","moved-contact","changed-visibility"})
        {
            h=new(c);h.Accepted(10,"A01","Push",Cue());
            var cue=unsafeCase=="different-target"?Cue(target:"other"):unsafeCase=="moved-contact"?Cue(x:15):unsafeCase=="changed-visibility"?Cue(visible:true):Cue();
            Check(!h.TryGrace(10.1,cue,unsafeCase!="not-temporary",unsafeCase!="unsafe"),"no grace: "+unsafeCase);
        }
        h=new(c);h.Accepted(10,"A01","Search",Cue());h.Begin(10,"blocked","unavailable",Cue());
        Check(h.Blocked(10.2,Cue(at:9.1,target:"other")),"target churn alone cannot bypass retry");
        Check(h.Blocked(10.3,Cue(at:10.2)),"same-position refreshed timestamp insufficient");
        Check(!h.Blocked(10.4,Cue(at:10.3,x:12)),"genuinely new known location permits reevaluation");
        Check(!h.Blocked(10.5,Cue(at:10.4,visible:true)),"fresh sight allows reevaluation");
        Check(h.Blocked(10.6,Cue(at:99,visible:true)),"future cue rejected");
        Check(h.Blocked(10.7,Cue(at:9,visible:true)),"replayed cue rejected");
        Check(h.Begin(10.8,"adapter-exception","error",Cue()),"error escalates existing ordinary handoff");
        Check(h.Blocked(11,Cue(at:10.9,visible:true)),"fresh data never bypasses error backoff");
        Check(!h.Begin(12,"adapter-exception","error",Cue()),"error callbacks do not extend backoff");
        Check(!h.Blocked(20.81,Cue()),"ten second backoff expires");
        h.Clear();Check(!h.Yielded&&h.Current.State=="idle"&&h.LastAction=="","scope/raid clear removes lifecycle");
        var drift=new SainHandoff(c);drift.Accepted(10,"A01","Search",Cue());drift.TryGrace(10.1,Cue(),true,true);
        drift.Accepted(10.2,"A01","Search",Cue(x:10.4));
        Check(!drift.TryGrace(10.3,Cue(x:10.8),true,true),"incremental cue drift cannot move the grace anchor");
        h.Begin(21,"no-option","unavailable",Cue(20));
        Check(!h.Blocked(22.1,Cue(20)),"first actual retry admitted");
        Check(!h.Begin(22.1,"no-option","unavailable",Cue(20)),"identical failed retry does not spam telemetry");
        Check(h.Blocked(22.2,Cue(20)),"failed retry reinstates bounded cadence");
        Check(!h.Blocked(23.2,Cue(20)),"next actual retry admitted");
        h.Clear();
        foreach(double bad in new[]{double.NaN,double.PositiveInfinity,-1}){Check(h.Blocked(bad,Cue()),"bad clock yields safely");}
        h.Accepted(10,"A01","Hold",Cue());Check(h.Blocked(9,Cue()),"backward time invalidates ownership");
        var zero=new SainHandoff(new(){Grace=0,Retry=0});zero.Accepted(10,"A01","Search",Cue());
        Check(!zero.TryGrace(10,Cue(),true,true),"zero grace supported");zero.Begin(10,"failed","unavailable",Cue());Check(!zero.Blocked(10,Cue()),"zero retry supported");
        // Real selector/memory integration: a handoff must not allow the same failed goal to win again.
        var cfg=new CometConfig();foreach(var key in cfg.Weights.Keys.ToArray())cfg.Weights[key]=key=="Search"?1:0;
        var memory=new PlanProgress(cfg.Progress);memory.CandidateAttempts["Search"]="goal";
        var s=new Situation{Now=10,Target="enemy",HasContact=true,IsAi=true,IsPmc=true,IsAuthority=true,IsActive=true,KnownDistance=10,ContactAge=1,Progress=memory};
        var engine=new HiddenEnemyDecision(cfg);Check(engine.Evaluate(s).Decision==Tactic.Search,"initial native-capable search");
        memory.Fail("goal",10.1,"progress-stalled");s.Now=10.1;Check(engine.Evaluate(s).Decision==null,"A12 refuses failed goal");
        h=new(c);h.Accepted(10.1,"A01","Search",Cue());h.Begin(10.2,"no-feasible-option","unavailable",Cue());engine.Release("handoff");
        Check(!h.TryGrace(10.3,Cue(),true,false),"confirmed failure cannot be prolonged");s.Now=11.3;
        Check(!h.Blocked(11.3,Cue())&&engine.Evaluate(s).Decision==null&&memory.Blocked("goal",11.3),"native retry retains A12 failure memory");
        memory.CandidateAttempts["Search"]="other";Check(engine.Evaluate(s).Decision==Tactic.Search,"another valid goal remains usable");
        h.Healthy(11.4);h.Accepted(11.4,"A01","Search",Cue());Check(h.TryGrace(11.5,Cue(),true,true),"healthy plan can tolerate a later independent interruption");
        foreach(var edit in new Action<HandoffConfig>[]{x=>x.Grace=-1,x=>x.Grace=2.1,x=>x.Retry=double.NaN,x=>x.ErrorRetry=0,x=>x.ErrorRetry=61})
        {var bad=new HandoffConfig();edit(bad);bool thrown=false;try{bad.Validate();}catch(ArgumentException){thrown=true;}Check(thrown,"reject invalid config");}
        Check(JsonSerializer.Deserialize<CometConfig>("{}")!.Handoff.Grace==.5,"old config migrates defaults");
        var dir=Path.Combine(Path.GetTempPath(),"COMET-A13-"+Guid.NewGuid().ToString("N"));var store=new ConfigStore(Path.Combine(dir,"config.json"));
        var draft=store.Read();draft.Handoff.Retry=1.5;store.Save(draft,draft.Revision);Check(store.Read().Handoff.Retry==1.5,"UI-backed store persists config");
        draft=store.Read();draft.Handoff.Grace=-1;bool rejected=false;try{store.Save(draft,draft.Revision);}catch(ArgumentException){rejected=true;}
        Check(rejected&&store.Read().Handoff.Grace==.5,"invalid config never partly saved");
        var probe=new SainHandoff(c);probe.Accepted(20,"A01","Push",Cue());
        Check(!probe.TryGrace(20.1,Cue(),true,false,"active-route-not-moving")&&probe.Current.PathCheck=="active-route-not-moving","native refusal explained");
        Check(probe.ReportPathCheck(20.1)&&!probe.ReportPathCheck(20.2),"path refusals bounded to one per second");
        Check(!probe.TryGrace(20.3,Cue(x:3),true,true)&&probe.Current.PathCheck=="known-contact-changed","moving known target distinguished from missing route");
        Check(!probe.ReportPathCheck(20.3)&&probe.ReportPathCheck(21.1),"changing reasons cannot flood diagnostics");
        probe.Healthy(21.2);Check(probe.Current.PathCheck==""&&!probe.ReportPathCheck(21.2),"healthy path clears stale refusal");
        Check(probe.TryGrace(21.3,Cue(),true,true),"diagnostics do not prevent valid continuation");
        probe.Clear();Check(!probe.ReportPathCheck(22),"raid cleanup resets path evidence");
        var json=Newtonsoft.Json.JsonConvert.SerializeObject(new{handoff=h.Current},Comet.Client.Diagnostics.Json);
        Check(json.Contains("fromAction")&&json.Contains("graceRemaining")&&json.Contains("nativeObserved")&&json.Contains("pathCheck"),"runtime telemetry casing");
        Console.WriteLine($"A13 passed: {n} assertions (handoff, priorities, grace, retry, native observation, memory, configuration).");return n;
    }
}
