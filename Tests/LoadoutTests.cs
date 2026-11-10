using Comet.Core;
using Comet.Client;
using Comet.Server;
using Newtonsoft.Json;

internal static class LoadoutTests
{
    internal static int Run()
    {
        int n = 0;
        void Check(bool ok, string why) { if (!ok) throw new Exception("A08: " + why); n++; }
        void Near(double a, double b, string why) => Check(Math.Abs(a - b) < .001, why);
        var cfg = new CometConfig(); var c = cfg.Loadout;
        Check(c.Enabled && c.ScoreCap == 15 && c.RefreshSeconds == 5 && c.GrenadeStockCap == 3, "approved defaults");
        LoadoutTrace Kit(string cls, bool scope = false, bool dot = false, bool bolt = false, string ammo = "")
        {
            var p = LoadoutModel.Classify(cls, bolt, cls == "smg" || cls == "assaultRifle", 850, scope, dot, ammo, c);
            p.At = 10; p.WeaponId = cls + scope; p.Ready = p.ResourcesKnown = true; p.LoadedRounds = 20; return p;
        }
        Situation S(LoadoutTrace? p, double d = 12) => new() { Now = 10, IsAi = true, IsSupportedBot = true, IsAuthority = true,
            IsActive = true, HasContact = true, Target = "contact", ContactAge = 1, KnownDistance = d, Loadout = p };
        List<Candidate> Options() => new[] { "Push", "Shoot", "Hold", "Search", "FallBack", "Cover", "Grenade" }
            .Select(d => new Candidate { Decision = d, Score = 50, Eligible = true }).ToList();
        List<Candidate> Adjust(Situation s) { var list = Options(); LoadoutModel.Adjust(list, s, c); return list; }
        foreach (var t in new[] { ("pistol", false, false, false, 8d), ("shotgun", false, false, false, 8d), ("smg", false, false, false, 12d),
            ("assaultRifle", false, false, false, 20d), ("assaultRifle", false, true, false, 25d), ("assaultRifle", true, false, false, 35d),
            ("marksmanRifle", false, false, false, 30d), ("marksmanRifle", true, false, false, 50d),
            ("sniperRifle", false, false, true, 40d), ("sniperRifle", true, false, true, 70d) })
            Near(Kit(t.Item1,t.Item2,t.Item3,t.Item4).IdealRange,t.Item5,"preferred range " + t.Item1);
        Check(Kit("alien-rifle").State == "unknown-weapon" && Adjust(S(Kit("alien-rifle"))).All(x => x.LoadoutDelta == 0), "modded unknown weapon is neutral");
        Near(Kit("assaultRifle",true,true).IdealRange,25,"scope plus usable dot does not force long range");
        Check(Kit("shotgun",ammo:"slug").IdealRange > Kit("shotgun").IdealRange,"confirmed slug profile differs from buckshot");
        var close = Adjust(S(Kit("smg"))); var sniper = Adjust(S(Kit("sniperRifle",true,false,true)));
        Check(close[0].Score > sniper[0].Score && sniper[4].Score > close[4].Score,"close SMG vs scoped bolt behavior");
        var captured = Kit("smg");var frozen=captured.Copy();captured.Grenades=7;Check(frozen.Grenades==0,"telemetry snapshot detached from later resources");
        var supply = Kit("smg");supply.Grenades=3;double stock=Adjust(S(supply)).Last().LoadoutDelta;
        supply.Grenades=100;Near(Adjust(S(supply)).Last().LoadoutDelta,stock,"stock bonus saturates");
        supply.Grenades=0;Near(Adjust(S(supply)).Last().LoadoutDelta,0,"no unusable grenade bonus");
        supply.Grenades=3;supply.ResourcesKnown=false;Near(Adjust(S(supply)).Last().LoadoutDelta,0,"incomplete inventory is not a stock estimate");
        var hurt=S(Kit("smg"));hurt.Hurt=true;double untreated=Adjust(hurt)[4].Score;
        hurt.Loadout!.MedicalItems=10;Near(Adjust(hurt)[4].Score,untreated,"ten medkits do not imply treatable injury");
        hurt.Loadout.CanFirstAid=true;Check(Adjust(hurt)[4].Score>untreated,"usable treatment can favor protected cover");
        hurt.Loadout.ResourcesKnown=false;Near(Adjust(hurt)[4].Score,untreated,"unknown medical resources never justify treatment bonus");
        var impossible=Options();impossible[4].Eligible=false;impossible[4].Reason="no-reachable-cover";
        LoadoutModel.Adjust(impossible,S(Kit("sniperRifle",true)),c);
        Check(!impossible[4].Eligible && impossible[4].Score==50 && impossible[4].Reason=="no-reachable-cover","wrong range cannot permit unsafe retreat");
        foreach(var mutate in new Action<Situation>[] {s=>s.IsAi=false,s=>s.IsSupportedBot=false,s=>s.IsAuthority=false,s=>s.IsActive=false,s=>s.Ghost=true,
            s=>s.Loadout=null,s=>s.Loadout!.State="refresh-pending",s=>s.Loadout!.Ready=false,s=>s.Loadout!.At=11,s=>s.Loadout!.At=4,
            s=>s.Loadout!.Cqb=double.NaN,s=>s.Loadout!.IdealRange=0,s=>s.KnownDistance=double.PositiveInfinity}) {
            var s=S(Kit("smg"));mutate(s);Check(Adjust(s).All(x=>x.LoadoutDelta==0),"scope/missing/stale/invalid snapshot is neutral");
        }
        c.Enabled=false;Check(Adjust(S(Kit("smg"))).All(x=>x.LoadoutDelta==0),"toggle restores unadjusted scores");c.Enabled=true;
        // Continuous distance sweep: no step at old fork thresholds, finite capped contributions, no eligibility change.
        foreach(var p in new[]{Kit("smg"),Kit("sniperRifle",true),Kit("assaultRifle",true,true),Kit("shotgun",ammo:"slug")}) {
            List<Candidate>? prev=null;
            for(int i=0;i<=600;i++) {
                var choices=Adjust(S(p,i*.25));
                foreach(var a in choices) Check(double.IsFinite(a.Score)&&Math.Abs(a.LoadoutDelta)<=c.ScoreCap&&a.Eligible,"bounded progressive influence");
                if(prev!=null) for(int j=0;j<choices.Count;j++) Check(Math.Abs(choices[j].Score-prev[j].Score)<2,"continuous distance curve");
                prev=choices;
            }
        }
        // The actual engines keep safety gates, weights, commitment and native refusals with A08 active.
        cfg.DecisionVariety.Enabled=false;cfg.TacticalMistakes.Enabled=false;cfg.Fear.Enabled=false;
        var sc=S(Kit("smg"));sc.Unavailable[Tactic.Grenade]="no-trajectory";sc.Unavailable[Tactic.FallBack]="no-cover";
        var engine=new HiddenEnemyDecision(cfg);var first=engine.Evaluate(sc);
        Check(first.Candidates.Single(x=>x.Decision=="Push").LoadoutDelta>0,"A01 integrates equipment before selection");
        sc.Now+=.1;sc.KnownDistance+=.05;var retained=engine.Evaluate(sc);
        Check(retained.Decision==first.Decision&&!retained.Changed,"nearby distance jitter retains the plan");
        var confirmed = sc.Loadout;
        sc.Loadout = LoadoutTrace.Pending(confirmed,confirmed!.WeaponId,sc.Now,"inventory-changed");
        var pending = engine.Evaluate(sc);
        Check(sc.Loadout.PlanKey == confirmed.PlanKey && !pending.Changed && pending.Decision == first.Decision,
            "resource refresh cannot break equipment commitment or trigger another draw");
        Check(pending.Candidates.All(x=>x.LoadoutDelta==0) && !sc.Loadout.ResourcesKnown,
            "pending refresh does not reuse stale equipment scores or supplies");
        sc.Loadout = confirmed; Check(!engine.Evaluate(sc).Changed,"unchanged equipment after refresh retains commitment");
        Check(LoadoutTrace.Pending(confirmed,"different-gun",sc.Now,"weapon-or-ammo-changed").PlanKey != confirmed.PlanKey,
            "pending new weapon still invalidates the previous weapon commitment");
        sc.Loadout=Kit("sniperRifle",true,false,true);var swapped=engine.Evaluate(sc);
        Check(swapped.Changed && swapped.Candidates.Single(x=>x.Decision=="Push").LoadoutDelta<0,"new weapon breaks old equipment commitment");
        sc.LowAmmo=true;Check(!engine.Evaluate(sc).Candidates.Single(x=>x.Decision=="Push").Eligible,"low ammo still blocks push");
        cfg.Weights["Hold"]=0;Check(!new HiddenEnemyDecision(cfg).Evaluate(sc).Candidates.Single(x=>x.Decision=="Hold").Eligible,"zero weight remains disabled");
        var visible=new VisibleSituation { Contact=S(Kit("sniperRifle",true,false,true),8), WeaponReady=true,CanShoot=true,
            ProbeReady=true,ThreatCount=1,CoverReachable=false,CoverReason="no-cover",PushReachable=false,PushReason="no-path" };
        visible.Contact.Visible=true;var v=new VisibleEnemyDecision(cfg).Evaluate(visible);
        Check(v.Decision==VisibleTactic.Shoot,"sniper surprised at eight meters fights instead of taking an impossible route");
        Check(v.Candidates.Single(x=>x.Decision=="Shoot").LoadoutReason=="loadout-shooting-range","A02 has explained scoring");
        var refresh=new LoadoutRefresh();Check(refresh.NeedsRefresh(10,"gun1","a"),"first inspection");refresh.Complete(10,"gun1","a",5);
        Check(refresh.IsFresh(11,"gun1","a")&&!refresh.NeedsRefresh(11,"gun1","a"),"no inventory scan on every decision");
        Check(refresh.NeedsRefresh(11,"gun2","b")&&!refresh.IsFresh(11,"gun2","b"),"weapon/ammo change invalidates previous snapshot immediately");
        refresh.Complete(11,"gun2","b",5);refresh.Invalidate("inventory-changed");
        Check(!refresh.IsFresh(11.1,"gun2","b")&&!refresh.NeedsRefresh(11.1,"gun2","b"),"event bursts coalesced without using stale stock");
        Check(refresh.NeedsRefresh(11.3,"gun2","b"),"dirty scan resumes promptly");refresh.Complete(11.3,"gun2","b",5);
        Check(refresh.NeedsRefresh(16.3,"gun2","b"),"periodic safety refresh");refresh.Failed(17);
        Check(!refresh.NeedsRefresh(17.5,"gun2","b")&&refresh.NeedsRefresh(18,"gun2","b"),"failed reads back off");
        // Actual adapter policies: reject incompatible/inaccessible magazines and unusable medical supplies.
        foreach(var fast in new[]{false,true}) foreach(var fit in new[]{false,true}) foreach(var ammo in new[]{false,true})
            foreach(int rounds in new[]{0,1,30}) Check(LoadoutModel.UsableSpareMagazine(fast,fit,rounds,ammo)==(fast&&fit&&ammo&&rounds>0),"magazine eligibility");
        foreach(double remaining in new[]{double.NaN,double.PositiveInfinity,-1,0,1,400}) foreach(bool carried in new[]{false,true})
            foreach(bool target in new[]{false,true}) foreach(bool apply in new[]{false,true}) {
                var health=S(Kit("smg"));health.Hurt=true;
                health.Loadout!.CanFirstAid=LoadoutModel.UsableMedicalSupply(remaining,carried,target,apply);
                Check((Adjust(health)[4].Score>50)==(double.IsFinite(remaining)&&remaining>0&&carried&&target&&apply),"wrong, empty or inaccessible kit never causes a treatment bonus");
            }
        var continuity=new SightContinuity(cfg);var sf=new SightFrame{Target="contact",Weapon="mp7",Eligible=true,Visible=true,Observed=true,WeaponReady=true,Action="Shoot",Distance=12};
        for(int i=0;i<6;i++){sf.Now=i*.1;continuity.Evaluate(sf);}sf.Visible=false;sf.Now=.6;Check(continuity.Evaluate(sf)!=null,"sight continuity established");
        sf.Weapon="mosin";sf.Now=.7;Check(continuity.Evaluate(sf)==null&&continuity.Reason=="sight-equipment-changed","old close-combat continuity is not reused after weapon switch");
        // Configuration travels through the real server store and client JSON serializer.
        var dir=Path.Combine(Path.GetTempPath(),"comet-a08-"+Guid.NewGuid());var path=Path.Combine(dir,"config.json");
        try {
            var store=new ConfigStore(path);var draft=store.Read();draft.Loadout.Enabled=false;draft.Loadout.Ranges["BoltScope"]=90;draft.Loadout.ScoreCap=12;
            store.Save(draft,draft.Revision);var read=JsonConvert.DeserializeObject<CometConfig>(store.ForGame())!;
            Check(!read.Loadout.Enabled&&read.Loadout.Ranges["BoltScope"]==90&&read.Loadout.ScoreCap==12,"UI/server/client settings round trip");
            foreach(var invalidate in new Action<CometConfig>[]{x=>x.Loadout=null!,x=>x.Loadout.Ranges.Remove("Smg"),x=>x.Loadout.ScoreCap=26,
                x=>x.Loadout.RefreshSeconds=0,x=>x.Loadout.GrenadeStockCap=0,x=>x.Loadout.Ranges["Smg"]=double.NaN}) {
                draft=store.Read();invalidate(draft);string before=File.ReadAllText(path);bool rejected=false;
                try{store.Save(draft,draft.Revision);}catch(Exception ex) when(ex is ArgumentException){rejected=true;}
                Check(rejected&&File.ReadAllText(path)==before,"invalid settings rejected atomically");
            }
        } finally {if(Directory.Exists(dir))Directory.Delete(dir,true);}
        var old=JsonConvert.DeserializeObject<CometConfig>("{\"Schema\":1}")!;old.Validate();Check(old.Loadout.Ranges["Smg"]==12,"older configs gain A08 defaults");
        string json=JsonConvert.SerializeObject(new{loadout=sniper,config=cfg,candidates=first.Candidates},Diagnostics.Json);
        Check(json.Contains("loadoutDelta")&&json.Contains("loadoutReason")&&json.Contains("scoreBeforeLoadout"),"RR deltas and reasons serialized");
        Check(System.Text.Encoding.UTF8.GetByteCount(json)<25000,"full context fits existing event budget");
        Console.WriteLine($"A08 equipment awareness: {n} checks passed.");return n;
    }
}
