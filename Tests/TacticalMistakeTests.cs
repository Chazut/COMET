using System;
using System.Collections.Generic;
using System.Linq;
using Comet.Core;

// Reference: R_F SAIN zzap fbb86b838e0fa692b759b2c0b11fc30158a53876.
internal static class TacticalMistakeTests
{
    internal static int Run()
    {
        int n = 0;
        void Check(bool ok, string why)
        {
            n++;
            if (!ok)
                throw new Exception("TacticalMistake: " + why);
        }
        void Near(double actual, double expected, string why) =>
            Check(Math.Abs(actual - expected) < 1e-9, why);

        var c = new CometConfig();
        c.DecisionVariety.Enabled = false;
        foreach (var key in c.TacticalMistakes.Chances.Keys.ToArray())
            c.TacticalMistakes.Chances[key] = 1;
        List<Candidate> Pool() =>
            new()
            {
                new()
                {
                    Decision = "Shoot",
                    Score = 80,
                    Eligible = true,
                },
                new()
                {
                    Decision = "Cover",
                    Score = 76,
                    Eligible = true,
                },
                new()
                {
                    Decision = "Push",
                    Score = 66,
                    Eligible = true,
                },
                new()
                {
                    Decision = "Search",
                    Score = 64,
                    Eligible = true,
                },
            };
        var m = new DecisionMixer(
            c.DecisionVariety,
            "source",
            true,
            new TacticalMistakes(c.TacticalMistakes, "source")
        );
        var p = Pool();
        var first = m.Select(p, "a", "Normal", "test", now: 10)!;
        int draws = m.Last!.Mistake!.DrawIndex;
        Check(first.Decision is "Cover" or "Push", "mistake picks second or third, not fourth");
        foreach (double at in new[] { 10.1, 11, 14.999 })
            Check(
                m.Select(p, "changed-contact", "Normal", "test", now: at)!.Decision
                    == first.Decision
                    && m.Last!.Mistake!.DrawIndex == draws,
                "five-second verdict survives target changes"
            );
        m.Select(p, "a", "Normal", "test", now: 15);
        Check(m.Last!.Mistake!.DrawIndex > draws, "verdict expires at five seconds");
        var counts = new Dictionary<string, int>();
        for (int i = 0; i < 30000; i++)
        {
            var x = m.Select(p, "a", "Normal", "test", now: 20 + i * 5)!.Decision;
            counts[x] = counts.GetValueOrDefault(x) + 1;
        }
        Check(
            Math.Abs(counts["Push"] / 30000d - .3) < .015,
            "source third-choice rate is 30 percent"
        );
        p[1].Score = 45;
        p[2].Eligible = false;
        p[3].Eligible = false;
        Check(
            m.Select(p, "a", "Normal", "test", now: 200000)!.Decision == "Cover",
            "absolute 0.35 utility loss is allowed"
        );
        p[1].Score = 44.99;
        Check(
            m.Select(p, "a", "Normal", "test", now: 200006)!.Decision == "Shoot",
            "greater absolute loss rejects mistake"
        );
        p[1].Score = 79;
        Check(
            m.Select(p, "a", "Normal", "test", urgent: true, retry: true, now: 200012)!.Decision
                == "Cover",
            "no extra COMET urgency or near-score exclusion"
        );
        p[1].Eligible = false;
        Check(
            m.Select(p, "a", "Normal", "test", now: 200018)!.Decision == "Shoot",
            "native eligibility still respected"
        );

        var contact = new Situation
        {
            Target = "a", IsAi = true, IsAuthority = true, IsSupportedBot = true, IsActive = true,
            HasContact = true, Visible = true, Looking = true, ContactAge = 6,
            KnownDistance = 13, RealDistance = 13, Now = 100,
        };
        var v = new VisibleSituation
        {
            Contact = contact, CanShoot = true, WeaponReady = true, PushReachable = true,
            CoverDistance = 1, CurrentExposure = .48,
        };
        // This stream draws the third option on its first verdict.
        var visible = new VisibleEnemyDecision(c, "third2");
        var picked = visible.Evaluate(v);
        Check(picked.Candidates.OrderByDescending(x => x.Score).Select(x => x.Decision)
            .SequenceEqual(new[] { "Cover", "Shoot", "Push" }) && picked.Decision == VisibleTactic.Push,
            "audit fixture picks third-ranked Push");
        var verdict = picked.Mixing!.Mistake!;
        contact.Now += .1;
        v.PushReachable = false;
        var fallback = visible.Evaluate(v);
        Check(fallback.Decision == VisibleTactic.Shoot && verdict.SwappedFromRank == 3 && !fallback.Reconsidered
            && ReferenceEquals(fallback.Mixing!.Mistake, verdict),
            $"after a third-place mistake, blocked Push falls back to Shoot; got {fallback.Decision}");
        v.PushReachable = true;
        Check(visible.Evaluate(v).Decision == VisibleTactic.Push,
            "Push regains priority when its path returns within the same ranking");

        contact.Visible = false;
        contact.Now = 100;
        var scores = ZzapUtility.Hidden(contact, c.Aggression["Normal"], 0, false);
        foreach (var tactic in Enum.GetValues<Tactic>())
            c.Weights[tactic.ToString()] = tactic switch
            {
                Tactic.Push => 30 / scores[tactic],
                Tactic.Flank => 29 / scores[tactic],
                Tactic.Search => 28 / scores[tactic],
                _ => 0,
            };
        string RefuseSearch(Tactic tactic) => tactic == Tactic.Search ? "path-lost" : "";
        var hidden = new HiddenEnemyDecision(c, "third2");
        Check(hidden.Evaluate(contact).Decision == Tactic.Search, "hidden mistake picks third-ranked Search");
        contact.Now += .1;
        Check(hidden.Evaluate(contact, RefuseSearch).Decision == Tactic.Flank,
            "hidden execution falls back to the middle candidate after a third-place mistake");
        contact.Now = 106;
        var kept = hidden.Evaluate(contact);
        Check(kept.Decision == Tactic.Search && kept.Mixing!.Mistake!.Selected == "Flank"
            && kept.Mixing.Mistake.SwappedFromRank == 2,
            "a later verdict swaps in Flank before the close-score rule retains Search");
        Check(hidden.Evaluate(contact, RefuseSearch).Decision == Tactic.Flank,
            "retaining Search preserves the new verdict's fallback order");

        c.DecisionVariety.Enabled = true;
        var varied = new HiddenEnemyDecision(c, "third2");
        var weighted = varied.Evaluate(contact);
        Check(weighted.Decision == Tactic.Search && weighted.Mixing!.Mistake!.State == "a04-already-varied"
            && weighted.Mixing.Mistake.SwappedFromRank == null
            && varied.Evaluate(contact, RefuseSearch).Decision == Tactic.Push,
            "an ordinary weighted third-place pick still falls back to the original best");

        var reloadMistakes = new TacticalMistakes(c.TacticalMistakes, "third2");
        var hold = new Candidate { Decision = "Hold", Score = 80, Eligible = true };
        var reload = new Candidate { Decision = "Reload", Score = 76, Eligible = true };
        var pistol = new Candidate { Decision = "Pistol", Score = 66, Eligible = true };
        var reloadOrder = new List<Candidate> { hold, reload, pistol };
        var reloadMix = new MixTrace();
        reloadMistakes.ApplyToRanking(reloadOrder, hold, reloadMix, "Normal", false, 100);
        Check(reloadOrder.SequenceEqual(new[] { pistol, reload, hold }) && reloadMix.Mistake!.SwappedFromRank == 3,
            "if the selected pistol is unavailable, Reload is tried before Hold ends the native loop");
        pistol.Score = 78;
        reloadOrder = new() { hold, pistol, reload };
        var cachedMix = new MixTrace();
        reloadMistakes.ApplyToRanking(reloadOrder, hold, cachedMix, "Normal", false, 101);
        Check(reloadOrder.SequenceEqual(new[] { pistol, hold, reload }) && cachedMix.Mistake!.SwappedFromRank == 2
            && cachedMix.Mistake.DrawIndex == reloadMix.Mistake!.DrawIndex && cachedMix.Mistake.Roll == null,
            "a cached verdict swaps the chosen option from its current rank without another draw");
        return n;
    }
}
