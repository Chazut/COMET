using System;
using System.Linq;
using Comet.Core;

// Behavioral contract: zzap fbb86b83 (see SOURCE_BASELINE.md).
internal static class VisibleDecisionTests
{
    public static int Run()
    {
        int n = 0;
        void Check(bool ok, string why)
        {
            n++;
            if (!ok)
                throw new Exception("VisibleDecision: " + why);
        }

        VisibleSituation Frame() =>
            new()
            {
                Contact = new()
                {
                    Now = 20,
                    Target = "enemy",
                    IsAi = true,
                    IsSupportedBot = true,
                    IsAuthority = true,
                    IsActive = true,
                    Visible = true,
                    HasContact = true,
                    KnownDistance = 10,
                    Health = 1,
                    Ammo = 1,
                },
                WeaponReady = true,
                CanShoot = true,
                PushReachable = true,
                PushPathLength = 10,
                CoverDistance = 2,
            };
        CometConfig Only(VisibleTactic a)
        {
            var c = new CometConfig();
            c.TacticalMistakes.Enabled = false;
            c.VisibleCombat.ShootWeight = a == VisibleTactic.Shoot ? 1 : 0;
            c.VisibleCombat.CoverWeight = a == VisibleTactic.Cover ? 1 : 0;
            c.VisibleCombat.PushWeight = a == VisibleTactic.Push ? 1 : 0;
            return c;
        }
        // Case 47844 from the seeded comparison with VisibleEnemyUtility.Rank.
        {
            var c = new CometConfig();
            c.TacticalMistakes.Enabled = false;
            var f = Frame();
            f.Contact.RealDistance = f.Contact.KnownDistance = 19;
            f.Contact.Health = .270472687795047;
            f.Contact.Ammo = .7409727884181648;
            f.Contact.Weakness = .7336250593576698;
            f.Contact.Looking = f.Contact.Busy = f.Contact.GrenadeOut = true;
            f.Contact.VisibleMates = .5;
            f.CurrentExposure = .18018585405321133;
            f.CoverDistance = 1;
            f.RecentHit = f.CoverReachable = true;
            var engine = new VisibleEnemyDecision(c);
            var choice = engine.Evaluate(f);
            var mix = choice.Mixing!;
            Check(choice.Decision == VisibleTactic.Cover && mix.Selected == "Cover"
                && mix.Mistake!.Best == "Cover", "full precision gives Cover the lead over Shoot");
            Check(choice.Candidates.Where(x => x.Decision != "Push").All(x => x.Score == 71.01)
                && mix.Options.Where(x => x.Decision != "Push").All(x => x.Score == 71.01)
                && mix.Mistake!.Options.Where(x => x.Decision != "Push").All(x => x.Score == 71.01)
                && mix.Mistake.BestScore == 71.01 && mix.Mistake.SelectedScore == 71.01,
                "candidate, mixing and mistake diagnostics keep the two-decimal scores");
            f.RecentHit = false;
            f.Contact.Now += .1;
            Check(engine.Evaluate(f).Decision == VisibleTactic.Cover
                && engine.Mixing!.Opportunity == mix.Opportunity,
                "cached preference preserves the winner when displayed scores tie");

            // Push leads at these weights. Cover still narrowly beats Shoot as a fallback.
            c.VisibleCombat.CoverWeight = c.VisibleCombat.ShootWeight = .1;
            c.VisibleCombat.PushWeight = 2;
            engine = new VisibleEnemyDecision(c);
            f.RecentHit = true;
            f.PushReachable = false;
            var fallback = engine.Evaluate(f);
            Check(fallback.Decision == VisibleTactic.Cover && fallback.Mixing!.Mistake!.Best == "Push",
                "the second and third options also keep their full-precision order");
            f.RecentHit = false;
            f.PushReachable = true;
            f.Contact.Now += .1;
            Check(engine.Evaluate(f).Decision == VisibleTactic.Push, "cached first choice becomes available");
            f.PushReachable = false;
            Check(engine.Evaluate(f).Decision == VisibleTactic.Cover
                && engine.Mixing!.Opportunity == fallback.Mixing!.Opportunity,
                "losing the path again uses the original second choice");

            var raw = ZzapUtility.Visible(f, c.Aggression["Normal"]);
            c.VisibleCombat.PushWeight = 0;
            c.VisibleCombat.ShootWeight = 50.001 / raw[VisibleTactic.Shoot];
            c.VisibleCombat.CoverWeight = 50.003 / raw[VisibleTactic.Cover];
            Check(new VisibleEnemyDecision(c).Evaluate(f).Decision == VisibleTactic.Cover,
                "weighted scores retain precision");
            f.Contact.Outcome = new OutcomeTrace
            {
                State = "review", Target = f.Contact.Target, Remaining = 1,
                Effects = new[] { new OutcomeEffect { Action = "Shoot", Results = 2, Delta = .003 } },
            };
            Check(new VisibleEnemyDecision(c).Evaluate(f).Decision == VisibleTactic.Shoot
                && f.Contact.Outcome.Effects[0].Applied == .003,
                "encounter adjustment can reverse a narrow lead before ranking");

            f.Contact.Outcome = null;
            c.TacticalMistakes.Enabled = true;
            c.TacticalMistakes.Chances["Normal"] = 1;
            c.VisibleCombat.ShootWeight = 71.004 / raw[VisibleTactic.Shoot];
            c.VisibleCombat.CoverWeight = 36.003 / raw[VisibleTactic.Cover];
            var mistake = new VisibleEnemyDecision(c).Evaluate(f);
            Check(mistake.Decision == VisibleTactic.Shoot
                && mistake.Mixing!.Mistake!.State == "score-loss-too-large",
                "mistake rejects a raw loss above 35 even when displayed scores differ by 35");
        }
        foreach (var a in Enum.GetValues<VisibleTactic>())
        {
            var e = new VisibleEnemyDecision(Only(a));
            var f = Frame();
            Check(e.Evaluate(f).Decision == a, "source candidate can execute: " + a);
        }
        {
            var c = new CometConfig();
            c.TacticalMistakes.Enabled = false;
            var f = Frame();
            f.Contact.Looking = true;
            f.Contact.Loadout = new LoadoutTrace { Cqb = .5, IdealRange = 20 };
            f.CurrentExposure = .6;
            f.CoverDistance = 5;
            VisibleChoice Rank(double? actual, double remembered)
            {
                f.Contact.RealDistance = actual;
                f.Contact.KnownDistance = remembered;
                return new VisibleEnemyDecision(c).Evaluate(f);
            }
            foreach (var (actual, remembered, expected) in new[]
            {
                (8d, 15d, VisibleTactic.Push),
                (15d, 8d, VisibleTactic.Shoot),
            })
            {
                var baseline = Rank(null, actual);
                var seen = Rank(actual, remembered);
                Check(
                    baseline.Decision == expected && seen.Decision == expected,
                    $"visible target at {actual}m, last known at {remembered}m: expected {expected}"
                );
                Check(
                    seen.Candidates.Select(x => x.Score).SequenceEqual(baseline.Candidates.Select(x => x.Score)),
                    "all three visible scores use the sighted distance"
                );
            }
            Check(Rank(8, double.NaN).Decision == VisibleTactic.Push, "valid sighted distance is sufficient");
            Check(Rank(double.NaN, 15).Decision == VisibleTactic.Shoot, "unavailable sighted distance uses memory");
            Check(Rank(null, double.NaN).Reason == "invalid-contact", "no usable distance to score");

            f.Contact.Visible = false;
            double HiddenHold(double actual, double remembered)
            {
                f.Contact.RealDistance = actual;
                f.Contact.KnownDistance = remembered;
                return ZzapUtility.Hidden(f.Contact, .5, 0, false)[Tactic.Hold];
            }
            double farMemory = HiddenHold(8, 90);
            Check(
                farMemory == HiddenHold(15, 90) && farMemory < HiddenHold(15, 8),
                "hidden hold responds to remembered distance even as the unseen target moves"
            );
        }
        foreach (
            Action<VisibleSituation> change in new Action<VisibleSituation>[]
            {
                v => v.ProbeReady = false,
                v => v.ThreatCount = 20,
                v => v.ThreatsTruncated = true,
                v => v.PushRouteRisk = 1,
                v => v.CoverRouteRisk = 1,
                v => v.PushPathLength = 100,
                v => v.Contact.Hurt = true,
                v => v.Contact.LowAmmo = true,
            }
        )
        {
            var f = Frame();
            change(f);
            Check(
                new VisibleEnemyDecision(Only(VisibleTactic.Push)).Evaluate(f).Decision
                    == VisibleTactic.Push,
                "removed extra route/threat/status veto"
            );
        }
        var frame = Frame();
        frame.PushReachable = false;
        Check(
            new VisibleEnemyDecision(Only(VisibleTactic.Push)).Evaluate(frame).Decision == null,
            "source requires rush capability"
        );
        {
            var c = Only(VisibleTactic.Push);
            c.DecisionVariety.Enabled = false;
            c.VisibleCombat.ShootWeight = .1;
            c.VisibleCombat.PushWeight = 3;
            var e = new VisibleEnemyDecision(c);
            var f = Frame();
            f.PushReachable = false;
            var first = e.Evaluate(f);
            var scores = first.Candidates.Select(x => x.Score).ToArray();
            int opportunity = first.Mixing!.Opportunity;
            Check(
                first.Decision == VisibleTactic.Shoot,
                "pending rush path falls through to shooting"
            );
            f.Contact.Now += .1;
            f.PushReachable = true;
            var ready = e.Evaluate(f);
            Check(
                ready.Decision == VisibleTactic.Push && ready.Changed && !ready.Reconsidered,
                "newly usable path consumes cached preferred push"
            );
            Check(
                ready.Candidates.Select(x => x.Score).SequenceEqual(scores)
                    && ready.Mixing!.Opportunity == opportunity,
                "path completion does not rerank or reroll"
            );
            f.Contact.Now += .1;
            f.PushReachable = false;
            var blocked = e.Evaluate(f);
            Check(
                blocked.Decision == VisibleTactic.Shoot
                    && blocked.Mixing!.Selected == "Shoot"
                    && blocked.Candidates.Single(x => x.Decision == "Push").Reason == f.PushReason,
                "path loss updates refusal and falls through during source cache"
            );
            f.Contact.Now = 23.01;
            Check(
                e.NeedsEvidence(f.Contact, false),
                "capability checks do not extend visible scoring window"
            );
        }
        {
            var e = new VisibleEnemyDecision(Only(VisibleTactic.Push));
            var f = Frame();
            f.PushReachable = false;
            var first = e.Evaluate(f);
            f.Contact.Now += .1;
            f.PushReachable = true;
            Check(
                !e.NeedsEvidence(f.Contact, false)
                    && e.Evaluate(f).Decision == VisibleTactic.Push
                    && e.Mixing!.Opportunity == first.Mixing!.Opportunity,
                "all-blocked ranking survives to recover without new score"
            );
        }
        frame = Frame();
        frame.Contact.KnownDistance = 25;
        Check(
            new VisibleEnemyDecision(Only(VisibleTactic.Push)).Evaluate(frame).Decision == null,
            "source visible distance limit"
        );
        var policy = new VisibleEnemyDecision(Only(VisibleTactic.Shoot));
        frame = Frame();
        policy.Evaluate(frame);
        frame.Contact.Now += .1;
        frame.CanShoot = false;
        Check(
            policy.Evaluate(frame).Decision == VisibleTactic.Shoot,
            "source ranked memory survives temporary no-shot"
        );
        frame.Contact.Now += 4;
        Check(
            policy.Evaluate(frame).Decision == null,
            "no-shot eventually yields to native decisions"
        );
        foreach (var tactic in Enum.GetValues<VisibleTactic>())
        {
            var e = new VisibleEnemyDecision(Only(tactic));
            var f = Frame();
            f.EffectiveWeaponDistance = 8;
            f.Contact.KnownDistance = 2;
            f.Contact.RealDistance = 10;
            var initial = e.Evaluate(f);
            Check(initial.Decision == tactic, "exactly 125% of effective range is eligible");
            f.Contact.Now += .1;
            f.Contact.RealDistance = 10.01;
            f.CanShoot = false;
            var blink = e.Evaluate(f);
            Check(blink.Decision == tactic && !blink.Reconsidered && blink.Reason == "zzap-kept-no-shot",
                "a blocked muzzle checks the same-enemy cache before range");
            f.CanShoot = true;
            var outside = e.Evaluate(f);
            Check(outside.Changed && outside.Decision == null && e.Current == null && outside.KeepRankingOnHandoff
                && outside.Reason == "out-of-weapon-range",
                "clear muzzle outside range yields immediately, even with a fresh ranking: " + tactic);
            Check(!e.Evaluate(f).Changed, "remaining out of range does not repeatedly release a choice");
            f.Contact.Now += .1;
            f.Contact.RealDistance = 10;
            var resumed = e.Evaluate(f);
            Check(resumed.Decision == tactic && resumed.Changed && !resumed.Reconsidered
                && ReferenceEquals(initial.Candidates, resumed.Candidates),
                "back in range of the same enemy reuses the original ranking");
            f.Contact.RealDistance = 11;
            f.Contact.Now += 4;
            var expired = e.Evaluate(f);
            Check(expired.Decision == null && !expired.KeepRankingOnHandoff, "expired ranking outside range still yields");
            f.CanShoot = false;
            Check(e.Evaluate(f).Decision == null, "an expired ranking cannot be revived by a blocked muzzle");
            f.Contact.RealDistance = 9;
            f.CanShoot = true;
            Check(e.Evaluate(f).Reconsidered, "return after expiry scores the target again");
        }
        {
            var e = new VisibleEnemyDecision(Only(VisibleTactic.Shoot));
            var f = Frame();
            f.EffectiveWeaponDistance = 8;
            f.Contact.RealDistance = 11;
            Check(e.Evaluate(f).Decision == null, "no initial ranking for a distant target with a clear line");
            f.CanShoot = false;
            Check(e.Evaluate(f).Decision == null, "a blocked muzzle cannot create a ranking");
            f.Contact.RealDistance = 9;
            f.CanShoot = true;
            e.Evaluate(f);
            f.CanShoot = false;
            f.RecentHit = true;
            Check(e.Evaluate(f).Decision == null, "a recent hit prevents retention during a muzzle blink");
            f.Contact.Target = "another enemy";
            f.RecentHit = false;
            var other = e.Evaluate(f);
            Check(other.Decision == null && !other.KeepRankingOnHandoff, "another enemy cannot inherit a blocked-muzzle ranking");
        }
        foreach (
            Action<VisibleSituation> change in new Action<VisibleSituation>[]
            {
                v => v.Contact.Ghost = true,
                v => v.Contact.IsAuthority = false,
                v => v.Contact.IsSupportedBot = false,
                v => v.Contact.Visible = false,
            }
        )
        {
            frame = Frame();
            change(frame);
            Check(
                new VisibleEnemyDecision(new()).Evaluate(frame).Decision == null,
                "ownership boundary preserved"
            );
        }

        return n;
    }
}
