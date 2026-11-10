using Comet.Core;
using Comet.Server;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class DecisionVarietyTests
{
    internal static int Run()
    {
        int count = 0;
        void Check(bool value, string why) { if (!value) throw new Exception("A04: " + why); count++; }
        List<Candidate> Pool() => new() {
            new() { Decision="Push", Score=80, Eligible=true }, new() { Decision="Grenade", Score=76, Eligible=true },
            new() { Decision="Flank", Score=72, Eligible=true }, new() { Decision="Hold", Score=69, Eligible=true },
            new() { Decision="Search", Score=60, Eligible=true }, new() { Decision="FallBack", Score=999, Eligible=false, Reason="no-cover" } };
        var config = new CometConfig(); var c = config.DecisionVariety;
        Check(c.Enabled && c.MaxOptions==3 && c.HiddenScoreGap==.15 && c.VisibleScoreGap==.1 && c.HiddenIntensity==1 && c.VisibleIntensity==.6, "approved defaults");
        var mix = new DecisionMixer(c,"raid/bot",false); var pool = Pool(); mix.Select(pool,"enemy","Normal","new-contact");
        var first = mix.Last!;
        Check(first.Options.Count(x=>x.Probability>0)==3 && first.Roll.HasValue && first.DrawIndex==1,"one draw over at most three good choices");
        Check(Math.Abs(first.Options.Sum(x=>x.Probability)-1)<1e-12,"normalized distribution");
        foreach(var o in first.Options.Where(x=>x.Probability>0))
            Check(Math.Abs(o.Probability-Math.Exp((o.Score-80)/4)/(1+Math.Exp(-1)+Math.Exp(-2)))<1e-12,"80/76/72 distribution");
        Check(first.Options.Single(x=>x.Decision=="FallBack").Reason=="no-cover" && first.Options.Single(x=>x.Decision=="FallBack").Probability==0,"unsafe highest score excluded before ranking");
        Check(first.Options.Single(x=>x.Decision=="Hold").Reason=="outside-top-options" && first.Options.Single(x=>x.Decision=="Search").Reason=="outside-score-gap","top count and score gap are separate filters");
        var visible = new DecisionMixer(c,"raid/bot",true); visible.Select(pool,"enemy","Normal","new-contact");
        Check(visible.Last!.Temperature==2.4 && visible.Last.Options[0].Probability>first.Options[0].Probability,"visible combat is less random");

        // Actual production RNG and distribution, repeated decision opportunities rather than game frames.
        var histogram = new Dictionary<string,int> { ["Push"]=0,["Grenade"]=0,["Flank"]=0 };
        for(int i=0;i<100000;i++) histogram[mix.Select(pool,"enemy","Normal","next-plan")!.Decision]++;
        foreach(var o in first.Options.Where(x=>x.Probability>0))
            Check(Math.Abs(histogram[o.Decision]/100000.0-o.Probability)<.006,"measured frequency matches probability: "+o.Decision);
        Console.WriteLine("A04 sample, 100000 decisions: " + string.Join(", ",histogram.Select(x=>x.Key+"="+x.Value)));
        var twinA = new DecisionMixer(c,"same-raid/bot-a",false); var twinB = new DecisionMixer(c,"same-raid/bot-a",false);
        var other = new DecisionMixer(c,"same-raid/bot-b",false); bool differs=false;
        for(int i=0;i<1000;i++)
        {
            string target="contact-"+i/100;
            var a=twinA.Select(pool,target,"Chad","new-plan");
            for(int n=0;n<i%7;n++) other.Select(pool,target,"Chad","other-bot");
            _=Random.Shared.NextDouble();
            var b=twinB.Select(pool,target,"Chad","new-plan");
            Check(a!.Decision==b!.Decision && twinA.Last!.Roll==twinB.Last!.Roll && twinA.Last.Seed==twinB.Last.Seed,"other RNG consumers cannot affect this bot");
            differs |= other.Last?.Roll != twinA.Last!.Roll;
        }
        Check(differs && twinA.Last!.Encounter==10,"independent bot streams and dated encounter sequence");
        foreach(string p in new[]{"Normal","Timmy","Chad","GigaChad","Wreckless","Rat","Coward","SnappingTurtle"})
        {
            var m=new DecisionMixer(c,"personality",false);m.Select(pool,"e",p,"new-contact");
            Check(m.Last!.Options.Where(x=>x.Probability>0).All(x=>x.Score>=68),"personality never relaxes eligibility or gap");
            if(p=="Rat" || p=="Coward") Check(m.Last.Options[0].Probability>first.Options[0].Probability,"cautious personalities vary less");
            if(p=="Wreckless") Check(m.Last.Options[0].Probability<first.Options[0].Probability,"reckless personalities vary more");
        }
        foreach(string mode in new[]{"disabled","zero-intensity","one-choice","urgent","retry","zero-scores","no-eligible"})
        {
            var cc=new DecisionVarietyConfig(); var pp=Pool();
            if(mode=="disabled")cc.Enabled=false;if(mode=="zero-intensity")cc.HiddenIntensity=0;if(mode=="one-choice")cc.MaxOptions=1;
            if(mode=="zero-scores")foreach(var x in pp)x.Score=0;
            if(mode=="no-eligible")foreach(var x in pp)x.Eligible=false;
            var m=new DecisionMixer(cc,"test",false);var result=m.Select(pp,"enemy","Normal","test",mode=="urgent",mode=="retry");
            Check(m.Last!.Roll==null && m.Last.DrawIndex==0,"no RNG consumed for "+mode);
            Check(m.Last.Options.Sum(x=>x.Probability)==(result==null?0:1),"deterministic diagnostics for "+mode);
        }
        { var pp=Pool();pp[0].Score=double.NaN;pp[1].Score=double.PositiveInfinity;pp[2].Score=-1;
            var m=new DecisionMixer(c,"bad-input",false);Check(m.Select(pp,"e","Normal","test")!.Decision=="Hold","nonfinite and negative scores never enter draw"); }

        Situation Hidden() => new() { Target="enemy", Now=10, ContactAge=0, KnownDistance=8, HasContact=true, IsAi=true,IsSupportedBot=true,IsAuthority=true,IsActive=true };
        var hc=new CometConfig { PlanLimit=60, SwitchMargin=0 };
        foreach(var k in hc.Weights.Keys.ToArray())hc.Weights[k]=0;
        hc.Weights["Push"]=80d/70;hc.Weights["Grenade"]=76d/75;hc.Weights["Flank"]=72d/50;
        HiddenEnemyDecision? hidden=null; Choice? initial=null; var s=Hidden();
        for(int i=0;i<100;i++) { hidden=new(hc,"bot-"+i);initial=hidden.Evaluate(s);if(initial.Decision!=Tactic.Push)break; }
        Check(initial!.Decision!=Tactic.Push,"exercise a lower-ranked winner");
        for(int i=1;i<=1000;i++)
        {
            s.Now=10+i*.01;var x=hidden!.Evaluate(s);
            Check(x.Decision==initial.Decision && !x.Changed && x.Mixing!.Opportunity==1 && x.Mixing.DrawIndex==1,"no reroll per frame or at commitment expiry, even with zero switch margin");
        }
        s.LowAmmo=true;s.Now+=.01;var emergency=hidden!.Evaluate(s);
        Check(emergency.Mixing!.State=="urgent-best" && emergency.Mixing.DrawIndex==1,"resource change bypasses commitment without random draw");
        var retryEngine=new HiddenEnemyDecision(hc,"retry");s=Hidden();var selected=retryEngine.Evaluate(s);
        retryEngine.Fail(selected.Decision!.Value,s.Now,"native-refusal");var retried=retryEngine.Evaluate(s);
        Check(retried.Decision!=selected.Decision && retried.Mixing!.State=="native-retry-best" && retried.Mixing.DrawIndex==selected.Mixing!.DrawIndex,"same-tick native failure does not fish for another random outcome");

        VisibleSituation Frame() => new() { Contact=Hidden(),WeaponReady=true,CanShoot=true,CoverReachable=true,PushReachable=true,ProbeReady=true,
            ThreatCount=1,ThreatSignature="enemy:visible/1",CoverId="cover",CurrentExposure=.2,CoverExposure=.1,CoverRouteRisk=.1,PushRouteRisk=.1,PushPathLength=10 };
        var vc=new CometConfig();vc.VisibleCombat.ShootWeight=80d/56;vc.VisibleCombat.CoverWeight=76d/54.5;vc.VisibleCombat.PushWeight=72d/47.5;
        vc.VisibleCombat.SwitchMargin=0;var ve=new VisibleEnemyDecision(vc,"a03-resume");var v=Frame();v.Contact.Visible=true;var vi=ve.Evaluate(v);
        for(int i=1;i<=1000;i++)
        { v.Contact.Now+=.01;var x=ve.Evaluate(v);Check(!x.Changed && x.Mixing!.Opportunity==vi.Mixing!.Opportunity,"visible stability does not reroll"); }
        for(int i=0;i<20;i++)
        {
            ve.SuspendForSight();v.Contact.Now+=.1;var x=ve.Evaluate(v);
            Check(x.Decision==vi.Decision && x.Changed && x.Mixing!.State=="kept-sight-continuity" && x.Mixing.Roll==vi.Mixing!.Roll
                && x.Mixing.Opportunity==vi.Mixing.Opportunity,"A03 return is reaccepted but never redrawn");
        }
        ve.SuspendForSight();v.Contact.Now+=.01;v.Contact.Suppressed=true;var unsafeResume=ve.Evaluate(v);
        Check(unsafeResume.Mixing!.State=="urgent-best" && unsafeResume.Mixing.DrawIndex==vi.Mixing!.DrawIndex,"A03 resume revalidates safety immediately");
        v.WeaponReady=false;Check(ve.Evaluate(v).Decision==null && ve.Current==null,"unready weapon yields instead of drawing");
        ve.SuspendForSight();Check(ve.Mixing==null,"native-only A03 cannot inherit an old COMET draw");

        var old=JsonNode.Parse(JsonSerializer.Serialize(new CometConfig()))!.AsObject();old.Remove("DecisionVariety");
        Check(JsonSerializer.Deserialize<CometConfig>(old.ToJsonString())!.DecisionVariety.HiddenScoreGap==.15,"legacy server config receives A04 defaults");
        var dir=Path.Combine(Path.GetTempPath(),"COMET-A04-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
        var path=Path.Combine(dir,"config.json");var store=new ConfigStore(path);var draft=store.Read();draft.DecisionVariety.VisibleIntensity=.4;draft.DecisionVariety.HiddenScoreGap=.12;draft.DecisionVariety.Enabled=false;store.Save(draft,1);
        var loaded=store.Read();Check(loaded.DecisionVariety.VisibleIntensity==.4 && loaded.DecisionVariety.HiddenScoreGap==.12 && !loaded.DecisionVariety.Enabled,"server roundtrip saves actual values");
        foreach(Action<CometConfig> invalid in new Action<CometConfig>[] { x=>x.DecisionVariety=null!,x=>x.DecisionVariety.MaxOptions=4,x=>x.DecisionVariety.MaxOptions=0,
            x=>x.DecisionVariety.HiddenScoreGap=.31,x=>x.DecisionVariety.VisibleScoreGap=-.1,x=>x.DecisionVariety.HiddenIntensity=double.NaN,x=>x.DecisionVariety.VisibleIntensity=double.PositiveInfinity })
        { var d=store.Read();var before=File.ReadAllText(path);invalid(d);bool failed=false;try{store.Save(d,d.Revision);}catch(ArgumentException){failed=true;}Check(failed && File.ReadAllText(path)==before,"invalid A04 config cannot overwrite saved settings"); }
        var json=Newtonsoft.Json.JsonConvert.SerializeObject(first,Comet.Client.Diagnostics.Json);
        var trace=JsonSerializer.Deserialize<MixTrace>(json,new JsonSerializerOptions { PropertyNameCaseInsensitive=true })!;
        Check(trace.Roll==first.Roll && trace.Options[0].Probability==first.Options[0].Probability && trace.Seed==first.Seed,"real telemetry serializer preserves reproducible draw and exact probabilities");
        return count;
    }
}
