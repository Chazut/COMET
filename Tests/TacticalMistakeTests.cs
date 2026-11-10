using Comet.Core;
using Comet.Server;
using System.Text.Json;

internal static class TacticalMistakeTests
{
    internal static int Run()
    {
        int count = 0;
        void Check(bool ok, string why) { if (!ok) throw new Exception("A05: " + why); count++; }
        List<Candidate> Pool() => new() {
            new() { Decision="Shoot", Score=80, Eligible=true }, new() { Decision="Cover", Score=76, Eligible=true },
            new() { Decision="Push", Score=66, Eligible=true }, new() { Decision="Search", Score=64, Eligible=true },
            new() { Decision="Hold", Score=60, Eligible=true }, new() { Decision="Unsafe", Score=65, Eligible=false, Reason="no-path" } };
        CometConfig Always() {
            var c=new CometConfig();c.DecisionVariety.Enabled=false;
            foreach(var name in c.TacticalMistakes.Chances.Keys.ToArray())c.TacticalMistakes.Chances[name]=1;
            return c;
        }
        DecisionMixer Mixer(CometConfig c, string seed, bool visible=false, TacticalMistakes? shared=null) =>
            new(c.DecisionVariety,seed,visible,shared ?? new TacticalMistakes(c.TacticalMistakes,seed));
        var defaults=new CometConfig();var d=defaults.TacticalMistakes;
        Check(d.Enabled && d.Multiplier==1 && d.MinimumInterval==10 && d.HiddenMaxLoss==.25 && d.VisibleMaxLoss==.2,"approved controls");
        Check(string.Join(",",d.Chances.Values)==string.Join(",",new[]{.12,.08,.06,.05,.04,.03,.03,.02}),"approved personality rates");
        var c=Always();var m=Mixer(c,"bounds",true);var chosen=m.Select(Pool(),"enemy","Normal","new-plan",now:100);
        var trace=m.Last!.Mistake!;
        Check(chosen!.Decision is "Push" or "Search" && trace.State=="mistake-selected","visible alternate outside A04, within 20%");
        Check(m.Last.Selected=="Shoot" && trace.Before=="Shoot" && trace.BestScore==80 && trace.SelectedScore==chosen.Score,"A04 result is preserved alongside the final choice");
        Check(trace.ScoreLoss<=.2 && trace.ScoreLoss>.1 && trace.NextAllowedAt==110 && trace.Roll.HasValue,"loss, roll and cooldown logged");
        Check(trace.Options.Single(x=>x.Decision=="Cover").Reason=="inside-a04-band" && trace.Options.Single(x=>x.Decision=="Unsafe").Reason=="no-path","exclusion reasons retained");
        Check(trace.Options.Single(x=>x.Decision=="Hold").Reason=="score-loss-too-large" && Math.Abs(trace.Options.Sum(x=>x.Probability)-1)<1e-12,"bounded conditional distribution");
        foreach(var now in new[]{100d,101d,109.999}) {
            m.Select(Pool(),"changed-"+now,"Normal","new-contact",now:now);
            Check(m.Last!.Mistake!.State=="cooldown" && m.Last.Mistake.Roll==null && m.Last.Mistake.DrawIndex==1,"target changes cannot reset cooldown or draw");
        }
        m.Select(Pool(),"another","Normal","new-contact",now:110);
        Check(m.Last!.Mistake!.State=="mistake-selected" && m.Last.Mistake.DrawIndex==2,"inclusive cooldown boundary");

        // Both production policies share the same mistake stream and timer.
        var shared=new TacticalMistakes(c.TacticalMistakes,"one-bot");var h=Mixer(c,"h",false,shared);var v=Mixer(c,"v",true,shared);
        h.Select(Pool(),"one","Normal","new-plan",now:10);v.Select(Pool(),"two","Normal","new-contact",now:11);
        Check(v.Last!.Mistake!.State=="cooldown" && v.Last.Mistake.DrawIndex==1,"hidden to visible shares timer");
        v.Select(Pool(),"two","Normal","new-plan",now:20);h.Select(Pool(),"three","Normal","new-contact",now:21);
        Check(h.Last!.Mistake!.State=="cooldown" && h.Last.Mistake.DrawIndex==2,"visible to hidden shares timer");
        foreach(string mode in new[]{"urgent","retry","off","zero","no-pool","zero-score","invalid-score","invalid-time"}) {
            var cc=Always();var pp=Pool();
            if(mode=="off")cc.TacticalMistakes.Enabled=false;if(mode=="zero")cc.TacticalMistakes.Multiplier=0;
            if(mode=="no-pool")cc.TacticalMistakes.HiddenMaxLoss=.15;
            if(mode=="zero-score")foreach(var x in pp)x.Score=0;
            if(mode=="invalid-score")foreach(var x in pp)x.Score=double.NaN;
            var test=Mixer(cc,mode);test.Select(pp,"e","Normal","test",mode=="urgent",mode=="retry",mode=="invalid-time"?double.NaN:10);
            Check(test.Last!.Mistake!.Roll==null && test.Last.Mistake.DrawIndex==0,"no A05 draw: "+mode);
        }
        // Exact boundary: ordinary A04 gap is excluded; max A05 loss is included.
        var bp=Pool();bp[1].Score=68;var bm=Mixer(c,"hidden-bounds");bm.Select(bp,"e","Normal","test",now:1);
        Check(bm.Last!.Mistake!.Options[1].Probability==0 && bm.Last.Mistake.Options[4].Probability>0,"strict A04 boundary, inclusive A05 boundary");
        var enabled=Always();enabled.DecisionVariety.Enabled=true;
        var a=Mixer(enabled,"separate-stream",true);var disabled=Always();disabled.DecisionVariety.Enabled=true;disabled.TacticalMistakes.Enabled=false;
        var b=Mixer(disabled,"separate-stream",true);int varied=0;
        for(int i=0;i<2000;i++) {
            a.Select(Pool(),"e-"+i/50,"Normal","test",now:i*11);b.Select(Pool(),"e-"+i/50,"Normal","test",now:i*11);
            Check(a.Last!.Roll==b.Last!.Roll && a.Last.Selected==b.Last.Selected && a.Last.Seed==b.Last.Seed,"A05 toggle cannot perturb A04 stream");
            if(a.Last.Selected!="Shoot") { varied++;Check(a.Last.Mistake!.State=="a04-already-varied" && a.Last.Mistake.Roll==null,"no stacked A04 and A05 demotion"); }
        }
        Check(varied>100,"non-cumulation tested on actual A04 alternatives");

        // Frequencies per eligible opportunity, with enough time between choices.
        foreach(var pair in defaults.TacticalMistakes.Chances) {
            var cc=new CometConfig();cc.DecisionVariety.Enabled=false;var sample=Mixer(cc,"frequency-"+pair.Key);int mistakes=0;
            for(int i=0;i<20000;i++) { sample.Select(Pool(),"e",pair.Key,"new-plan",now:i*11);if(sample.Last!.Mistake!.State=="mistake-selected")mistakes++; }
            Check(Math.Abs(mistakes/20000d-pair.Value)<.008,"frequency for "+pair.Key);
            Check(sample.Last!.Mistake!.DrawIndex==20000,"one draw per eligible opportunity");
            Console.WriteLine($"A05 {pair.Key}: {mistakes}/20000 eligible choices, expected {pair.Value:P0}");
        }
        var twinA=Mixer(defaults,"same-bot");var twinB=Mixer(defaults,"same-bot");var other=Mixer(defaults,"other-bot");bool differs=false;
        for(int i=0;i<1000;i++) {
            twinA.Select(Pool(),"e","Normal","test",now:i*11);for(int n=0;n<i%7;n++)other.Select(Pool(),"x","Timmy","test",now:i*11);
            _=Random.Shared.NextDouble();twinB.Select(Pool(),"e","Normal","test",now:i*11);
            Check(twinA.Last!.Mistake!.Roll==twinB.Last!.Mistake!.Roll && twinA.Last.Mistake.Selected==twinB.Last.Mistake.Selected,"reproducible isolated A05 stream");
            differs|=other.Last?.Mistake?.Roll!=twinA.Last.Mistake.Roll;
        }
        Check(differs,"different bots do not share draws");

        Situation Contact() => new() { Target="enemy",Now=10,Personality="Normal",HasContact=true,ContactAge=0,KnownDistance=8,IsAi=true,IsSupportedBot=true,IsActive=true,IsAuthority=true };
        VisibleSituation Visible() => new() { Contact=Contact(),WeaponReady=true,CanShoot=true,CoverReachable=true,PushReachable=true,ProbeReady=true,ThreatCount=1,ThreatSignature="enemy",CoverId="cover",
            CurrentExposure=.2,CoverExposure=.1,CoverRouteRisk=.1,PushRouteRisk=.1,PushPathLength=10 };
        var pc=Always();pc.VisibleCombat.ShootWeight=80d/56;pc.VisibleCombat.CoverWeight=76d/54.5;pc.VisibleCombat.PushWeight=66d/47.5;pc.VisibleCombat.SwitchMargin=0;
        var botClock=new TacticalMistakes(pc.TacticalMistakes,"policy-bot");var ve=new VisibleEnemyDecision(pc,"policy-bot",botClock);var vs=Visible();vs.Contact.Visible=true;
        var first=ve.Evaluate(vs);Check(first.Decision==VisibleTactic.Push && first.Mixing!.Mistake!.State=="mistake-selected","real A02 mistaken plan");
        for(int i=1;i<=1000;i++) {
            vs.Contact.Now=10+i*.005;var kept=ve.Evaluate(vs);
            Check(kept.Decision==first.Decision && !kept.Changed && kept.Mixing!.Mistake!.Opportunity==1 && kept.Mixing.Mistake.Roll==first.Mixing!.Mistake!.Roll,"no per-frame or commitment-expiry reroll");
        }
        ve.SuspendForSight();vs.Contact.Now+=.1;var resumed=ve.Evaluate(vs);
        Check(resumed.Changed && resumed.Decision==first.Decision && resumed.Mixing!.Mistake==first.Mixing!.Mistake && resumed.Mixing.State=="kept-sight-continuity","A03 preserves original A05 choice and trace");
        ve.NotifyNativeRefusal();ve.Release("native-refusal");var vr=ve.Evaluate(vs);
        Check(vr.Mixing!.State=="native-retry-best" && vr.Mixing.Mistake!.State=="native-retry" && vr.Mixing.Mistake.Roll==null,"A02 native refusal skips both random selectors");
        vs.RecentHit=true;vs.RecentHitAt=vs.Contact.Now+.1;vs.Contact.Now+=.1;
        var emergency=ve.Evaluate(vs);Check(emergency.Mixing!.Mistake!.State=="urgent" && emergency.Mixing.Mistake.Roll==null,"recent hit overrides mistaken commitment immediately");
        ve.Release("ghost");var he=new HiddenEnemyDecision(pc,"policy-bot",botClock);var hs=Contact();hs.Now=16;
        Check(he.Evaluate(hs).Mixing!.Mistake!.State=="cooldown","release and policy handover preserve per-bot cooldown");

        var hc=Always();foreach(var k in hc.Weights.Keys.ToArray())hc.Weights[k]=0;
        hc.Weights["Push"]=80d/70;hc.Weights["Grenade"]=76d/75;hc.Weights["Flank"]=60d/50;
        var hidden=new HiddenEnemyDecision(hc,"hidden-policy");var contact=Contact();var initial=hidden.Evaluate(contact);
        Check(initial.Decision==Tactic.Flank && initial.Mixing!.Mistake!.ScoreLoss==.25,"real A01 lower-bound alternate");
        for(int i=1;i<=600;i++) {contact.Now=10+i*.01;var kept=hidden.Evaluate(contact);Check(!kept.Changed && kept.Decision==Tactic.Flank && kept.Mixing!.Mistake!.Opportunity==1,"A01 plan stability");}
        hidden.Fail(Tactic.Flank,contact.Now,"native-refusal");var retry=hidden.Evaluate(contact);
        Check(retry.Decision==Tactic.Push && retry.Mixing!.Mistake!.State=="native-retry" && retry.Mixing.Mistake.Roll==null,"native refusal gets deterministic fallback");
        foreach(var gate in new[]{"scav","human","remote","ghost","inactive"}) {
            var frame=Contact();frame.IsSupportedBot=gate!="scav";frame.IsAi=gate!="human";frame.IsAuthority=gate!="remote";frame.Ghost=gate=="ghost";frame.IsActive=gate!="inactive";
            Check(new HiddenEnemyDecision(hc).Evaluate(frame).Mixing==null,"no A05 outside eligible PMC authority: "+gate);
        }

        // Invalid config must not corrupt saved server settings; old revisions get defaults.
        var old=JsonSerializer.Deserialize<CometConfig>("{\"Schema\":1,\"Revision\":1}")!;old.Validate();Check(old.TacticalMistakes.Chances["Timmy"]==.12,"older server configs gain A05 defaults");
        string root=Path.Combine(Path.GetTempPath(),"COMET-A05-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        string path=Path.Combine(root,"config.json");var store=new ConfigStore(path);var draft=store.Read();draft.TacticalMistakes.Chances["Timmy"]=.15;draft.TacticalMistakes.MinimumInterval=20;store.Save(draft,draft.Revision);
        Check(store.Read().TacticalMistakes.Chances["Timmy"]==.15 && store.Read().TacticalMistakes.MinimumInterval==20,"server A05 persistence");
        foreach(Action<CometConfig> invalid in new Action<CometConfig>[] {x=>x.TacticalMistakes=null!,x=>x.TacticalMistakes.Chances=null!,x=>x.TacticalMistakes.Chances.Remove("Normal"),
            x=>x.TacticalMistakes.Chances["Extra"]=.1,x=>x.TacticalMistakes.Chances["Timmy"]=-.01,x=>x.TacticalMistakes.Chances["Normal"]=1.01,x=>x.TacticalMistakes.Multiplier=double.NaN,
            x=>x.TacticalMistakes.Multiplier=3.1,x=>x.TacticalMistakes.MinimumInterval=0,x=>x.TacticalMistakes.HiddenMaxLoss=double.PositiveInfinity,x=>x.TacticalMistakes.VisibleMaxLoss=.51}) {
            var next=store.Read();var before=File.ReadAllText(path);invalid(next);bool failed=false;try{store.Save(next,next.Revision);}catch(ArgumentException){failed=true;}
            Check(failed && File.ReadAllText(path)==before,"invalid A05 settings rejected atomically");
        }
        var json=Newtonsoft.Json.JsonConvert.SerializeObject(first.Mixing,Comet.Client.Diagnostics.Json);
        var roundtrip=JsonSerializer.Deserialize<MixTrace>(json,new JsonSerializerOptions{PropertyNameCaseInsensitive=true})!;
        Check(roundtrip.Mistake!.Roll==first.Mixing!.Mistake!.Roll && roundtrip.Mistake.Selected=="Push" && roundtrip.Selected=="Shoot","actual log serializer keeps A04 and A05 separately");
        return count;
    }
}
