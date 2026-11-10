using Comet.Core;
using Comet.Server;
using Newtonsoft.Json;

internal static class OpponentTests
{
    internal static int Run()
    {
        int n = 0;
        void Check(bool ok, string why) { if (!ok) throw new Exception("A09: " + why); n++; }
        void Near(double a, double b, string why) => Check(Math.Abs(a - b) < .001, why);
        var cfg = new CometConfig(); var c = cfg.Opponent;
        Check(c.Enabled && c.ScoreCap == 10 && c.ObservationSeconds == .5 && c.WeaponDistance == 50 && c.ProtectionDistance == 25
            && c.WeaponMemory == 5 && c.ProtectionMemory == 15 && c.InjuryMemory == 3, "approved defaults");
        OpponentObservation View() => new() { Visible = true, PersonalSight = true, SightAge = 0, VisibleFor = 1, Distance = 20,
            WeaponVisibleFor = 1, BodyVisibleFor = 1, HeadVisibleFor = 1, Weapon = "pistol", Body = "none-visible", Head = "none-visible" };
        OpponentMemory Fresh() { var m = new OpponentMemory(); m.Observe("e", 1, 10, View(), c); return m; }
        var memory = Fresh(); var t = memory.Snapshot("e",1,10,c);
        Check(t.State == "estimated" && t.Evidence.Count == 3, "coarse personal observation captured");
        Near(t.Evidence.First(e=>e.Kind=="weapon").Confidence,.8,"initial confidence is not certainty");
        var hidden = View(); hidden.Visible = false; hidden.Weapon = "shotgun"; hidden.Body = "visible-protection";
        memory.Observe("e",1,11,hidden,c);
        var later = memory.Snapshot("e",1,11,c);
        Check(later.Evidence.First(e=>e.Kind=="weapon").Value == "pistol", "weapon swap behind wall cannot update memory");
        Check(later.Evidence.First(e=>e.Kind=="body").Value == "none-visible", "hidden armor change cannot update memory");
        Near(later.Evidence.First(e=>e.Kind=="weapon").Confidence,.64,"confidence fades without renewed sight");
        Check(t.Evidence.First(e=>e.Kind=="weapon").Confidence == .8,"previous telemetry snapshot remains frozen");
        foreach (var mutate in new Action<OpponentObservation>[] {v=>v.Visible=false,v=>v.PersonalSight=false,v=>v.SightAge=1,
            v=>v.SightAge=-1,v=>v.VisibleFor=.49,v=>v.Distance=51,v=>v.Distance=-1,v=>v.Distance=double.NaN})
        {
            var m = new OpponentMemory(); var v=View(); mutate(v); int reads=0; m.Observe("e",1,10,v,c,_=>reads++);
            Check(reads==0,"live observation callback never runs through a closed perception gate");
            Check(m.Snapshot("e",1,10,c).State=="unknown","invalid/too brief/shared/far observation is neutral");
        }
        var partial=View();partial.WeaponVisibleFor=-1;partial.BodyVisibleFor=-1;
        var pm=new OpponentMemory();pm.Observe("e",1,10,partial,c);
        Check(pm.Snapshot("e",1,10,c).Evidence.Single().Kind=="head","head over cover does not reveal torso or weapon");
        partial=View();partial.Distance=30;pm=new();pm.Observe("e",1,10,partial,c);
        Check(pm.Snapshot("e",1,10,c).Evidence.Single().Kind=="weapon","separate protection distance");
        var far=memory.Snapshot("e",1,15,c);
        Check(!far.Evidence.Single(e=>e.Kind=="weapon").Active && far.Evidence.Single(e=>e.Kind=="body").Active,"independent weapon/protection lifetime");
        Check(memory.Snapshot("e",1,25,c).State=="unknown","all observations expire");
        Check(memory.Snapshot("e",2,25,c).Evidence.Count==0,"respawn/same ID new enemy instance clears evidence");
        memory=Fresh();var unknown=View();unknown.Weapon="alien";unknown.Body="custom-armor";unknown.Head="unknown";
        memory.Observe("e",1,11,unknown,c);
        Check(memory.Snapshot("e",1,11,c).State=="unknown","observed unknown replaces obsolete certainty; never unarmed");
        memory=Fresh();memory.InjuryCue("e",1,10,"seen-impact",false);
        Check(memory.Snapshot("e",1,10,c).Evidence.Count==3,"unperceived global hit grants no injury knowledge");
        memory.InjuryCue("e",1,10,"health-status",true);
        Check(memory.Snapshot("e",1,10,c).Evidence.Count==3,"exact health notification is not a cue");
        memory.InjuryCue("e",1,10,"heard-heal",true);
        Check(memory.Snapshot("e",1,10,c).Evidence.Single(e=>e.Kind=="injury").Value=="possibly-injured","heard treatment is uncertain evidence only");
        memory.InjuryCue("e",1,12,"footstep",true);
        Check(!memory.Snapshot("e",1,13,c).Evidence.Single(e=>e.Kind=="injury").Active,"footsteps never extend wound memory");
        memory.InjuryCue("e",1,14,"seen-impact",true); memory.InjuryCue("e",1,13,"heard-surgery",true);
        Check(memory.Snapshot("e",1,14,c).Evidence.Single(e=>e.Kind=="injury").Source=="seen-impact","out-of-order cue does not replace newer evidence");
        Check(memory.Snapshot("different",1,14,c).State=="unknown","target memory cannot leak between contacts");
        for(int i=0;i<100;i++)memory.Observe("enemy"+i,i,20+i,View(),c);
        Check(memory.Count==32,"contact memory bounded across a long raid");memory.Clear();Check(memory.Count==0,"raid/ghost cleanup");

        Situation S(string weapon="pistol", double distance=30)
        {
            var m = new OpponentMemory();var v=View();v.Weapon=weapon;v.Body=v.Head="unknown";m.Observe("e",1,10,v,c);
            var own=LoadoutModel.Classify("assaultRifle",false,true,800,false,true,"",cfg.Loadout);own.At=10;own.Ready=true;
            return new Situation { Now=10,Target="e",HasContact=true,IsAi=true,IsSupportedBot=true,IsAuthority=true,IsActive=true,
                KnownDistance=distance,Loadout=own,Opponent=m.Snapshot("e",1,10,c) };
        }
        List<Candidate> Choices() => new[]{"Push","Shoot","Cover","FallBack","Hold","Search","Grenade"}.Select(a=>new Candidate{Decision=a,Score=50,Eligible=true}).ToList();
        List<Candidate> Adjust(Situation s) {var a=Choices();OpponentAssessment.Adjust(a,s,c);return a;}
        Check(Adjust(S())[0].OpponentDelta>0,"observed pistol at range versus own rifle is modest advantage");
        Check(Adjust(S("pistol",5))[0].OpponentDelta==0,"pistol at five metres is not free victory");
        Check(Adjust(S("shotgun",5))[0].OpponentDelta<0,"shotgun close prevents simplistic weak-target bonus");
        Check(Adjust(S("shotgun",60))[0].OpponentDelta==0,"unknown shotgun ammo does not grant a distant safe bonus");
        Check(Adjust(S("alien",5)).All(a=>a.OpponentDelta==0),"unrecognized weapon never weak");
        var withoutOwn=S();withoutOwn.Loadout=null;Check(Adjust(withoutOwn).All(a=>a.OpponentDelta==0),"relative pistol advantage needs own weapon evidence");
        var impossible=Choices();impossible[0].Eligible=false;impossible[0].Reason="unsafe-push";
        OpponentAssessment.Adjust(impossible,S(),c);Check(!impossible[0].Eligible && impossible[0].Score==50 && impossible[0].Reason=="unsafe-push","native refusal retained");
        foreach(var mutate in new Action<Situation>[] {s=>s.IsAi=false,s=>s.IsSupportedBot=false,s=>s.IsAuthority=false,s=>s.IsActive=false,s=>s.Ghost=true,
            s=>s.HasContact=false,s=>s.Opponent=null,s=>s.Opponent!.Target="another",s=>s.Opponent!.At=11,s=>s.Now=11,s=>s.KnownDistance=double.NaN})
        {var s=S();mutate(s);Check(Adjust(s).All(a=>a.OpponentDelta==0),"scope and invalid input fail neutral");}
        c.Enabled=false;Check(Adjust(S()).All(a=>a.OpponentDelta==0),"OFF neutral");c.Enabled=true;
        c.ScoreCap=0;Check(Adjust(S()).All(a=>a.OpponentDelta==0),"zero cap neutral");c.ScoreCap=10;
        foreach(var weapon in new[]{"pistol","shotgun","smg","sniperRifle","alien"})
            foreach(double distance in new[]{0,5,10,20,50,100})
                Check(Adjust(S(weapon,distance)).All(a=>double.IsFinite(a.Score)&&Math.Abs(a.OpponentDelta)<=10&&a.Eligible),"bounded influence and unchanged eligibility");
        cfg.DecisionVariety.Enabled=false;cfg.TacticalMistakes.Enabled=false;
        var unsafeS=S();unsafeS.Hurt=true;unsafeS.LowAmmo=true;
        var hc=new HiddenEnemyDecision(cfg).Evaluate(unsafeS);
        Check(hc.Candidates.Single(a=>a.Decision=="Push").Eligible==false,"A01 still refuses injured low-ammo rush");
        cfg.Weights["Push"]=0;var noPush=new HiddenEnemyDecision(cfg).Evaluate(S());
        Check(!noPush.Candidates.Single(a=>a.Decision=="Push").Eligible,"A09 cannot reenable zero action weight");
        VisibleSituation Visible() {var s=S();s.Visible=true;return new() {Contact=s,WeaponReady=true,CanShoot=true,ProbeReady=true,
            CoverReachable=true,PushReachable=true,ThreatCount=1,PushPathLength=10,CurrentExposure=.2,CoverExposure=.1,CoverRouteRisk=.1,PushRouteRisk=.1};}
        var visible=Visible();var ve=new VisibleEnemyDecision(cfg);var firstChoice=ve.Evaluate(visible);
        Check(firstChoice.Candidates.Any(a=>a.OpponentDelta!=0),"A09 changes actual A02 candidate scores");
        visible=Visible();visible.ThreatCount=2;var danger=new VisibleEnemyDecision(cfg).Evaluate(visible);
        Check(!danger.Candidates.Single(a=>a.Decision=="Push").Eligible,"other known threat still prevents rush despite perceived advantage");
        visible=Visible();visible.PushRouteRisk=1;danger=new VisibleEnemyDecision(cfg).Evaluate(visible);
        Check(!danger.Candidates.Single(a=>a.Decision=="Push").Eligible,"unsafe route still prevents A02 push");
        visible=Visible();visible.WeaponReady=false;
        Check(new VisibleEnemyDecision(cfg).Evaluate(visible).Decision==null,"urgent reload/weapon handling yields to SAIN");
        visible=Visible();visible.Contact.Now=10.1;visible.Contact.Opponent!.At=10.1;
        var retained=ve.Evaluate(visible);Check(!retained.Changed,"ordinary observation refresh does not break commitment/reroll");
        c.ScoreCap=.006;Check(Adjust(S()).All(a=>Math.Abs(a.OpponentDelta)<=.006+.000001),"fractional score cap is respected after rounding");c.ScoreCap=10;

        var old=JsonConvert.DeserializeObject<CometConfig>("{\"revision\":1}")!;old.Validate();Check(old.Opponent.WeaponMemory==5,"old configuration gains defaults");
        foreach(var bad in new Action<OpponentConfig>[] {x=>x.ScoreCap=-1,x=>x.ScoreCap=26,x=>x.ObservationSeconds=.1,
            x=>x.WeaponDistance=double.NaN,x=>x.ProtectionDistance=60,x=>x.InjuryMemory=0,x=>x.ProtectionMemory=40,x=>x.WeaponMemory=double.PositiveInfinity})
        {var x=new OpponentConfig();bad(x);try{x.Validate();throw new Exception("invalid config accepted");}catch(ArgumentException){n++;}}
        string folder=Path.Combine(Path.GetTempPath(),"comet-a09-"+Guid.NewGuid());
        try {
            var store=new ConfigStore(Path.Combine(folder,"config.json"));var saved=store.Read();saved.Opponent.ScoreCap=7;
            store.Save(saved,saved.Revision);var reopened=new ConfigStore(Path.Combine(folder,"config.json")).Read();
            Check(reopened.Opponent.ScoreCap==7,"server config survives reopen");
            reopened.Opponent.ScoreCap=99;
            try {store.Save(reopened,reopened.Revision);throw new Exception("invalid config saved");}catch(ArgumentException){n++;}
            Check(store.Read().Opponent.ScoreCap==7 && store.Read().Revision==reopened.Revision,"invalid A09 save leaves value and revision intact");
        } finally {if(Directory.Exists(folder))Directory.Delete(folder,true);}
        Console.WriteLine($"A09 passed: {n} assertions");return n;
    }
}
