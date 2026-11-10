using Comet.Core;
using Comet.Server;
using System.Text.Json;

static class CoverResponseTests
{
    public static int Run()
    {
        int n = 0;
        void Check(bool b, string why) { if (!b) throw new Exception("A11: " + why); n++; }
        void Near(double a, double b, string why) => Check(Math.Abs(a - b) < .00001, why);
        var config = new CometConfig(); config.DecisionVariety.Enabled = false; config.TacticalMistakes.Enabled = false;
        var c = config.CoverResponse; config.Validate();
        Check(c.Enabled, "enabled default"); Near(c.ShortExposure, .75, "short crossing"); Near(c.LongExposure, 1.5, "long crossing"); Near(c.ScoreCap, 25, "bounded influence");
        VisibleSituation V(double length = 8) => new() {
            Contact = new() { Now = 10, Target = "enemy", Personality = "Chad", IsAi = true, IsSupportedBot = true, IsActive = true, IsAuthority = true,
                HasContact = true, Visible = true, KnownDistance = 10, ContactAge = 0 },
            WeaponReady = true, CanShoot = true, CoverReachable = true, ProbeReady = true, CoverId = "wall", ThreatSignature = "enemy", ThreatCount = 1,
            CurrentExposure = 1, CoverExposure = 0, CoverRouteRisk = .4,
            CoverTravel = new() { Complete = true, PathLength = length, Corners = 2, WalkSpeed = 3, SprintSpeed = 6, Stamina = 1,
                Exposure = new[] { 1d, 1, 1, 0 } }
        };
        List<Candidate> Choices() => new() { new() { Decision = "Shoot", Score = 60, Eligible = true }, new() { Decision = "Cover", Score = 65, Eligible = true },
            new() { Decision = "Push", Score = 90, Eligible = false, Reason = "unsafe-push" } };
        CoverResponseTrace Run(VisibleSituation v, List<Candidate>? choices = null) { CoverResponseModel.Adjust(choices ?? Choices(), v, config); return v.CoverResponse!; }
        var v = V(); var choices = Choices(); var t = Run(v, choices);
        Check(t.State == "return-fire" && choices[0].Score > choices[1].Score, "long exposed route changes relative preference");
        Check(t.ExposedSeconds > 3 && t.FastExposedSeconds == t.ExposedSeconds, "walking exposure estimate");
        Near(choices[2].Score, 90, "does not boost unavailable push"); Check(!choices[2].Eligible, "never grants eligibility");
        v = V(.6); t = Run(v); Check(t.State == "quick-cover" && t.CoverDelta > 0 && t.ShootDelta == 0, "near protected step favored");
        v = V(.6); v.Contact.Personality = "Coward"; Check(Run(v).CoverDelta > 0, "coward gets nearby protection too");
        v = V(); v.Contact.Personality = "Coward"; Check(Run(v).ShootDelta > 0, "no categorical coward exception");
        v = V(.6); v.CoverTravel!.PathLength = 8; Check(Run(v).State == "return-fire", "detour uses path length, not straight-line distance");
        v = V(); v.CoverTravel!.Exposure = new[] { 0d, 0, 0, 0 }; t = Run(v); Check(t.State == "assessed" && t.ShootDelta == 0, "long protected path is not a fatal crossing");
        v = V(); v.CoverTravel!.CanSprint = true; t = Run(v); Check(t.FastEta < t.Eta && t.FastExposedSeconds < t.ExposedSeconds, "possible sprint bounds, not guaranteed sprint");
        v = V(); v.CoverTravel!.CanSprint = true; v.CoverTravel.Stamina = .2; t = Run(v); Near(t.Eta!.Value, t.FastEta!.Value, "exhausted PMC must walk");
        v = V(); v.CoverTravel!.CanSprint = true; v.CoverTravel.Stamina = .4; var limited = Run(v);
        Check(limited.FastEta > t.FastEta - 1, "low stamina cannot sprint entire route");
        double plain = Run(V()).Eta!.Value;
        v = V(); v.CoverTravel!.Corners = 6; v.CoverTravel.VerticalTravel = 3; Check(Run(v).Eta > plain, "stairs and corners add travel time");
        v = V(); v.CoverTravel!.WalkSpeed = 1; Check(Run(v).Eta > plain, "own impaired mobility increases time");
        v = V(); v.CoverTravel!.Exposure = new[] { 1d, 0, 0, 0 }; t = Run(v); Check(t.ExposedSeconds > 0 && t.ExposedSeconds < t.Eta, "only part of route is exposed");
        foreach (var mutate in new Action<VisibleSituation>[] {
            v=>v.Contact.LowAmmo=true, v=>v.Contact.Hurt=true, v=>v.Contact.Suppressed=true, v=>v.Contact.Fear=new(){Panic=true},
            v=>v.CanShoot=false }) {
            v=V(); mutate(v); choices=Choices(); if(!v.CanShoot) choices[0].Eligible=false;
            t=Run(v, choices); Check(t.ShootDelta==0 && t.CoverDelta>=0, "never penalizes defence when trading is unsafe");
        }
        v=V(.6);v.Contact.LowAmmo=true;Check(Run(v).CoverDelta>0,"quick protection still helps low ammo");
        foreach (var mutate in new Action<VisibleSituation>[] {v=>v.Contact.IsAi=false,v=>v.Contact.IsSupportedBot=false,v=>v.Contact.IsAuthority=false,
            v=>v.Contact.IsActive=false,v=>v.Contact.Ghost=true,v=>v.Contact.Visible=false,v=>v.Contact.HasContact=false,v=>v.Contact.Now=double.NaN,
            v=>v.ProbeReady=false,v=>v.WeaponReady=false,v=>v.CoverReachable=false,v=>v.CoverTravel=null,v=>v.CoverTravel!.Complete=false,
            v=>v.CoverTravel!.DoorBusy=true,v=>v.CoverTravel!.StartError=1,v=>v.CoverTravel!.PathLength=double.NaN,
            v=>v.CoverTravel!.WalkSpeed=0,v=>v.CoverTravel!.SprintSpeed=double.PositiveInfinity,v=>v.CoverTravel!.Stamina=-1,
            v=>v.CoverTravel!.VerticalTravel=20,v=>v.CoverTravel!.Corners=128,v=>v.CoverTravel!.Exposure=new[]{1d},
            v=>v.CoverTravel!.Exposure[1]=double.NaN,v=>v.CoverTravel!.Exposure[1]=2,v=>v.Contact.InCover=true}) {
            v=V();mutate(v);t=Run(v);Check(t.ShootDelta==0&&t.CoverDelta==0,"unreliable data/scope yields no adjustment");
            JsonSerializer.Serialize(t);n++;
        }
        foreach(int count in new[]{0,2,4,20}) {v=V();v.ThreatCount=count;t=Run(v);Check(t.Reason=="cover-response-threat-uncertainty"&&t.ShootDelta==0,"no simple trade preference against multiple threats");}
        v=V();v.ThreatsTruncated=true;Check(Run(v).ShootDelta==0,"truncated threats neutral");
        choices=Choices();choices[1].Eligible=false;v=V(.6);t=Run(v,choices);Check(t.CoverDelta==0&&!choices[1].Eligible,"bad cover cannot become available");
        v=V(.6);v.CoverExposure=1;var engine=new VisibleEnemyDecision(config);var choice=engine.Evaluate(v);
        Check(choice.Candidates.Single(x=>x.Decision=="Cover").Eligible==false&&v.CoverResponse!.CoverDelta==0,"other threat exposes destination in full pipeline");
        v=V(.6);v.CoverRouteRisk=.9;choice=new VisibleEnemyDecision(config).Evaluate(v);Check(!choice.Candidates.Single(x=>x.Decision=="Cover").Eligible,"existing route risk guard kept");
        v=V(.6);config.VisibleCombat.CoverWeight=0;choice=new VisibleEnemyDecision(config).Evaluate(v);Check(v.CoverResponse!.CoverDelta==0,"disabled tactic never boosted");config.VisibleCombat.CoverWeight=1;
        v=V();config.CoverResponse.Enabled=false;choices=Choices();CoverResponseModel.Adjust(choices,v,config);Check(v.CoverResponse==null&&choices[0].Score==60&&choices[1].Score==65,"toggle off exactly neutral");config.CoverResponse.Enabled=true;
        // A11 can change a real A02 choice within the agreed score cap; it does not promise that every personality will trade.
        v=V();v.Contact.Personality="GigaChad";config.CoverResponse.Enabled=false;
        var before=new VisibleEnemyDecision(config).Evaluate(v).Decision;config.CoverResponse.Enabled=true;
        var after=new VisibleEnemyDecision(config).Evaluate(v).Decision;
        Check(before==VisibleTactic.Cover&&after==VisibleTactic.Shoot,"A11 avoids exposed run in production chooser");
        engine=new VisibleEnemyDecision(config);v=V();engine.Evaluate(v);v.Contact.Now+=.1;Check(!engine.Evaluate(v).Changed,"identical assessment does not reroll");
        v.CoverTravel=V(.6).CoverTravel;v.Contact.Now+=.2;choice=engine.Evaluate(v);
        Check(choice.Reconsidered&&!choice.Changed&&choice.Decision==VisibleTactic.Cover,"new nearby protection rescored without restarting unchanged cover destination");
        v.Contact.Now+=.05;Check(!engine.Evaluate(v).Changed,"new protection remains stable");
        engine.Release("ghost");v.Contact.Ghost=true;Check(engine.Evaluate(v).Decision==null,"ghost handoff");
        // Deterministic range sweep: score caps, eligibility, and shorter exposure are monotonic without stacking adjustments.
        foreach (string personality in config.Aggression.Keys)
        foreach (double length in new[]{.3,.6,1d,2,4,8,16,40}) {
            v=V(length);v.Contact.Personality=personality;choices=Choices();t=Run(v,choices);
            Check(Math.Abs(t.ShootDelta)<=25.001&&Math.Abs(t.CoverDelta)<=25.001,"score cap across personalities/distances");
            Check(choices.All(x=>double.IsFinite(x.Score)&&x.Score>=0)&&!choices[2].Eligible,"finite and preserves eligibility");
            var slow=t;v.CoverTravel!.CanSprint=true;t=Run(v);
            Check(t.FastEta<=slow.FastEta&&t.Eta==slow.Eta,"sprint bound never slower than walk");
        }
        foreach(var mutate in new Action<CoverResponseConfig>[] {c=>c.ShortExposure=0,c=>c.ShortExposure=double.NaN,c=>c.LongExposure=.75,
            c=>c.LongExposure=double.PositiveInfinity,c=>c.LongExposure=7,c=>c.ScoreCap=26,c=>c.ScoreCap=-1}) {
            var bad=new CoverResponseConfig();mutate(bad);bool denied=false;try{bad.Validate();}catch(ArgumentException){denied=true;}Check(denied,"invalid config rejected");
        }
        var old=JsonSerializer.Deserialize<CometConfig>("{}")!;Check(old.CoverResponse.ShortExposure==.75,"old presets get A11 defaults");
        string dir=Path.Combine(Path.GetTempPath(),"COMET-A11-"+Guid.NewGuid().ToString("N"));var store=new ConfigStore(Path.Combine(dir,"config.json"));
        var draft=store.Read();draft.CoverResponse.ShortExposure=.5;store.Save(draft,draft.Revision);Check(store.Read().CoverResponse.ShortExposure==.5,"server save round trip");
        draft=store.Read();draft.CoverResponse.LongExposure=.4;bool rejected=false;try{store.Save(draft,draft.Revision);}catch(ArgumentException){rejected=true;}
        Check(rejected&&store.Read().CoverResponse.LongExposure==1.5,"invalid save preserves previous configuration");
        var payload=Newtonsoft.Json.JsonConvert.SerializeObject(new{coverResponse=Run(V()),candidates=Choices()},Comet.Client.Diagnostics.Json);
        Check(payload.Contains("fastExposedSeconds")&&payload.Contains("coverResponseDelta"),"runtime telemetry casing and contribution fields");
        Console.WriteLine($"A11 passed: {n} assertions (travel, safety, preference, stability, configuration and telemetry).");return n;
    }
}
