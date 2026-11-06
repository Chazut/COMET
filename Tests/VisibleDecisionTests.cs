using Comet.Core;
using Comet.Server;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class VisibleDecisionTests
{
    internal static int Run()
    {
        int count = 0;
        void Check(bool ok, string why) { if (!ok) throw new Exception("A02: " + why); count++; }
        VisibleSituation Frame() => new() {
            Contact = new Situation { Now = 20, Target = "one", IsAi = true, IsPmc = true, IsAuthority = true, IsActive = true,
                Visible = true, HasContact = true, ContactAge = .1, KnownDistance = 12, Personality = "Normal" },
            WeaponReady = true, CanShoot = true, ProbeReady = true, CoverReachable = true, PushReachable = true,
            CoverId = "wall", ThreatSignature = "one:visible", ThreatCount = 1, PushPathLength = 12,
            CurrentExposure = .2, CoverExposure = .1, CoverRouteRisk = .1, PushRouteRisk = .3
        };
        CometConfig Only(VisibleTactic tactic)
        {
            var c = new CometConfig(); c.VisibleCombat.ShootWeight = tactic == VisibleTactic.Shoot ? 1 : 0;
            c.VisibleCombat.CoverWeight = tactic == VisibleTactic.Cover ? 1 : 0; c.VisibleCombat.PushWeight = tactic == VisibleTactic.Push ? 1 : 0;
            return c;
        }
        foreach (var mutation in new Action<VisibleSituation>[] {
            v => v.Contact.IsAi = false, v => v.Contact.IsPmc = false, v => v.Contact.IsAuthority = false,
            v => v.Contact.IsActive = false, v => v.Contact.Ghost = true, v => v.Contact.Visible = false,
            v => v.Contact.HasContact = false, v => v.Contact.Target = "", v => v.Contact.ContactAge = 26,
            v => v.Contact.ContactAge = -1, v => v.Contact.Now = double.NaN, v => v.Contact.KnownDistance = double.PositiveInfinity,
            v => v.WeaponReady = false, v => v.ProbeReady = false, v => v.CoverRouteRisk = double.NaN,
            v => v.CurrentExposure = 1.1, v => v.PushPathLength = -1, v => v.ThreatCount = 0,
            v => v.Contact.KnownX = double.NaN, v => v.Contact.KnownY = double.PositiveInfinity
        })
        {
            var e = new VisibleEnemyDecision(new()); var v = Frame(); Check(e.Evaluate(v).Decision.HasValue, "initial eligible");
            mutation(v); var result = e.Evaluate(v);
            Check(result.Decision == null && result.Changed && e.Current == null, "immediate eligibility revocation");
        }
        foreach (var action in Enum.GetValues<VisibleTactic>())
        {
            var e = new VisibleEnemyDecision(Only(action)); var v = Frame();
            Check(e.Evaluate(v).Decision == action, "can select " + action);
            if (action == VisibleTactic.Shoot) v.CanShoot = false;
            if (action == VisibleTactic.Cover) v.CoverReachable = false;
            if (action == VisibleTactic.Push) v.PushReachable = false;
            var result = e.Evaluate(v);
            Check(result.Decision == null, "cannot select unusable " + action);
            Check(result.Candidates.Single(c => c.Decision == action.ToString()).Eligible == false, "reason for unavailable " + action);
        }
        foreach (var mutation in new Action<VisibleSituation>[] {
            v => v.Contact.Hurt = true, v => v.Contact.LowAmmo = true, v => v.Contact.Suppressed = true,
            v => v.ThreatCount = 2, v => v.ThreatsTruncated = true, v => v.PushPathLength = 26, v => v.PushRouteRisk = .8
        })
        {
            var e = new VisibleEnemyDecision(Only(VisibleTactic.Push)); var v = Frame(); e.Evaluate(v); mutation(v);
            Check(e.Evaluate(v).Decision == null, "unsafe push loses commitment immediately");
        }
        {
            var v = Frame(); v.CoverRouteRisk = .8;
            Check(new VisibleEnemyDecision(Only(VisibleTactic.Cover)).Evaluate(v).Decision == null, "destination cover does not excuse an exposed route");
            v.CoverRouteRisk = .1; v.CoverExposure = .8;
            Check(new VisibleEnemyDecision(Only(VisibleTactic.Cover)).Evaluate(v).Decision == null, "reachable but exposed destination rejected");
            v.CoverExposure = .1; v.Contact.Hurt = true; v.Contact.LowAmmo = true;
            Check(new VisibleEnemyDecision(new()).Evaluate(v).Decision == VisibleTactic.Cover, "own health and ammo favor cover");
        }
        {
            var v = Frame(); v.Contact.Personality = "GigaChad"; v.CurrentExposure = 0; v.PushRouteRisk = 0;
            Check(new VisibleEnemyDecision(new()).Evaluate(v).Decision == VisibleTactic.Push, "aggressive healthy single-contact push");
            v.Contact.Personality = "Coward"; v.CurrentExposure = .6;
            Check(new VisibleEnemyDecision(new()).Evaluate(v).Decision == VisibleTactic.Cover, "cautious bot values shelter");
        }
        {
            var e = new VisibleEnemyDecision(new()); var v = Frame();
            Check(e.Evaluate(v).Decision == VisibleTactic.Shoot, "clear shot initial");
            v.Contact.Now += .1; v.CurrentExposure = .9;
            var stable = e.Evaluate(v);
            Check(stable.Decision == VisibleTactic.Shoot && stable.Reason == "commitment", "no score flicker within commitment");
            v.ThreatSignature += "|two:heard"; v.ThreatCount = 2;
            Check(e.Evaluate(v).Decision == VisibleTactic.Cover, "new known threat forces rescore immediately");
            v.CoverReachable = false;
            Check(e.Evaluate(v).Decision == VisibleTactic.Shoot, "blocked cover route defeats commitment");
        }
        {
            var e = new VisibleEnemyDecision(new()); var v = Frame(); e.Evaluate(v);
            v.RecentHit = true; v.RecentHitAt = v.Contact.Now; v.Contact.Now += .1;
            Check(e.Evaluate(v).Decision == VisibleTactic.Cover, "hit forces rescore");
            v.RecentHitAt += .2; v.Contact.Now += .2;
            var rechecked = e.Evaluate(v);
            Check(rechecked.Reconsidered && !rechecked.Changed && rechecked.Decision == VisibleTactic.Cover,
                "second hit rescored without restarting the same cover");
        }
        {
            var c = new CometConfig(); c.VisibleCombat.SwitchMargin = 50;
            var e = new VisibleEnemyDecision(c); var v = Frame(); e.Evaluate(v); v.Contact.Now += 3; v.CurrentExposure = .3;
            Check(e.Evaluate(v).Reason == "hysteresis", "small improvement does not switch action after commitment");
            v.CoverId = "another"; v.CanShoot = false;
            Check(e.Evaluate(v).Decision == VisibleTactic.Cover, "lost shot is never retained by hysteresis");
        }
        {
            var c = new CometConfig(); foreach (var key in c.Weights.Keys) c.Weights[key] = key == "Push" ? 1 : 0;
            var intent = new CoverIntent(); var v = Frame(); v.Contact.Visible = false;
            intent.Accepted(v.Contact.Target, v.Contact.Now, 3); intent.Apply(v.Contact);
            Check(new HiddenEnemyDecision(c).Evaluate(v.Contact).Decision == null, "A02 cover cannot immediately become an A01 push");
            Check(v.Contact.Unavailable[Tactic.Push] == "recent-cover-intent", "handoff explains blocked push");
            v.Contact.Now += 3.1; v.Contact.Unavailable.Clear(); intent.Apply(v.Contact);
            Check(new HiddenEnemyDecision(c).Evaluate(v.Contact).Decision == Tactic.Push, "handoff bounded in time");
            intent.Accepted("one", 30, 3); Check(!intent.Allows("two", 31), "intent never leaks to another target");
            intent.Clear(); Check(!intent.Allows("one", 31), "cleanup revokes cover intent");
        }
        var random = new Random(4202); var engine = new VisibleEnemyDecision(new()); var twin = new VisibleEnemyDecision(new());
        {
            var v = Frame(); v.CoverDestination = "120:10/0/20";
            var e = new VisibleEnemyDecision(Only(VisibleTactic.Cover));
            int starts = 0, rescored = 0;
            foreach (double at in new[] { 266.655, 266.762, 266.885, 267.018, 267.185, 267.297 })
            {
                v.Contact.Now = at; v.RecentHit = true; v.RecentHitAt = at;
                var choice = e.Evaluate(v); if (choice.Changed) starts++; if (choice.Reconsidered) rescored++;
            }
            Check(starts == 1 && rescored == 6, "raid388 burst keeps one cover execution but evaluates every hit");
        }
        foreach (string change in new[] { "target", "cover", "destination", "native-refusal", "resume" })
        {
            var v = Frame(); v.CoverDestination = "120:10/0/20";
            var e = new VisibleEnemyDecision(Only(VisibleTactic.Cover)); e.Evaluate(v); v.Contact.Now += .1;
            switch (change)
            {
                case "target": v.Contact.Target = "other"; break;
                case "cover": v.CoverId = "other"; break;
                case "destination": v.CoverDestination = "120:20/0/20"; break;
                case "native-refusal": e.NotifyNativeRefusal(); break;
                case "resume": e.SuspendForSight(); break;
            }
            var choice = e.Evaluate(v);
            Check(choice.Changed && choice.Decision == VisibleTactic.Cover, "cover restarts when required: " + change);
        }
        {
            var v = Frame(); v.Contact.Hurt = true; v.CurrentExposure = .8;
            var e = new VisibleEnemyDecision(new()); Check(e.Evaluate(v).Decision == VisibleTactic.Cover, "cover before invalidation");
            v.Contact.Now += .1; v.CoverReachable = false;
            var choice = e.Evaluate(v);
            Check(choice.Changed && choice.Decision != VisibleTactic.Cover, "same-cover continuity cannot preserve an invalid route");
        }
        for (int i = 0; i < 20000; i++)
        {
            var v = Frame(); v.Contact.Now = i * .1; v.Contact.Target = "enemy-" + i / 97;
            v.Contact.Ghost = random.Next(80) == 0; v.Contact.Visible = random.Next(5) != 0;
            v.CanShoot = random.Next(4) != 0; v.CoverReachable = random.Next(3) != 0; v.PushReachable = random.Next(3) != 0;
            v.ProbeReady = random.Next(5) != 0; v.WeaponReady = random.Next(10) != 0;
            v.CurrentExposure = random.NextDouble(); v.CoverRouteRisk = random.NextDouble(); v.PushRouteRisk = random.NextDouble();
            v.ThreatCount = random.Next(1, 5); v.ThreatSignature = "known-" + v.ThreatCount;
            v.Contact.LowAmmo = random.Next(4) == 0; v.Contact.Hurt = random.Next(5) == 0;
            var a = engine.Evaluate(v); var b = twin.Evaluate(v);
            Check(a.Decision == b.Decision && a.Reason == b.Reason, "deterministic transitions " + i);
            if (a.Decision.HasValue) Check(a.Candidates.Single(x => x.Decision == a.Decision.ToString()).Eligible, "never chooses infeasible " + i);
        }
        var json = JsonNode.Parse(JsonSerializer.Serialize(new CometConfig()))!.AsObject(); json.Remove("VisibleCombat");
        var old = JsonSerializer.Deserialize<CometConfig>(json.ToJsonString())!; old.Validate();
        Check(old.VisibleCombat.Enabled && old.VisibleCombat.ShootWeight == 1, "A01 config gains A02 defaults without losing old fields");
        var root = Path.Combine(Path.GetTempPath(), "COMET-A02-" + Guid.NewGuid().ToString("N"));
        var store = new ConfigStore(Path.Combine(root, "config.json")); var config = store.Read();
        config.VisibleCombat.CoverWeight = 2.3; config.VisibleCombat.RiskTolerance = .25; config.VisibleCombat.Enabled = false;
        store.Save(config, 1); var roundtrip = store.Read();
        Check(roundtrip.VisibleCombat.CoverWeight == 2.3 && roundtrip.VisibleCombat.RiskTolerance == .25 && !roundtrip.VisibleCombat.Enabled, "server config round trip");
        foreach (var mutate in new Action<CometConfig>[] { c => c.VisibleCombat = null!, c => c.VisibleCombat.RiskTolerance = 2,
            c => c.VisibleCombat.CoverWeight = double.NaN, c => c.VisibleCombat.Commitment = 0, c => c.VisibleCombat.CoverMemory = 99,
            c => c.VisibleCombat.MaxPushPath = -1, c => c.VisibleCombat.ShootWeight = 4, c => c.VisibleCombat.SwitchMargin = 51 })
        {
            var bad = new CometConfig(); mutate(bad);
            try { bad.Validate(); throw new Exception("A02 invalid config accepted"); } catch (ArgumentException) { count++; }
        }
        var disabled = new CometConfig(); disabled.VisibleCombat.Enabled = false;
        Check(new VisibleEnemyDecision(disabled).Evaluate(Frame()).Decision == null, "A02 toggle yields to SAIN");
        disabled.VisibleCombat.Enabled = true; disabled.Enabled = false;
        Check(new VisibleEnemyDecision(disabled).Evaluate(Frame()).Decision == null, "global toggle yields to SAIN");
        return count;
    }
}
