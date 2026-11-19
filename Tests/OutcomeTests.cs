using Comet.Core;
using Comet.Server;

internal static class OutcomeTests
{
    internal static int Run()
    {
        int n = 0;
        void Check(bool v, string why)
        {
            n++;
            if (!v)
                throw new Exception("E04: " + why);
        }
        var c = new OutcomeConfig();
        c.Validate();
        Check(
            c.Enabled
                && c.EvaluationWindow == 6
                && c.MinimumResults == 2
                && c.PenaltyCap == 10
                && c.BonusCap == 5
                && c.Retention == 30,
            "defaults"
        );
        foreach (
            var edit in new Action<OutcomeConfig>[]
            {
                x => x.EvaluationWindow = double.NaN,
                x => x.EvaluationWindow = 13,
                x => x.MinimumResults = 1,
                x => x.PenaltyCap = 21,
                x => x.BonusCap = -1,
                x => x.Retention = double.PositiveInfinity,
            }
        )
        {
            var bad = new OutcomeConfig();
            edit(bad);
            bool ok = false;
            try
            {
                bad.Validate();
            }
            catch (ArgumentException)
            {
                ok = true;
            }
            Check(ok, "invalid settings");
        }
        var old = Newtonsoft.Json.JsonConvert.DeserializeObject<CometConfig>(
            "{\"outcomes\":null}"
        )!;
        old.Validate();
        Check(old.Outcomes.MinimumResults == 2, "migration");
        string dir = Path.Combine(Path.GetTempPath(), "comet-e04-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ConfigStore(dir);
            var cfg = store.Read();
            cfg.Outcomes.Retention = 20;
            store.Save(cfg, cfg.Revision);
            var read = store.Read();
            Check(read.Outcomes.Retention == 20, "server save");
            read.Outcomes.MinimumResults = 0;
            try
            {
                store.Save(read, read.Revision);
            }
            catch (ArgumentException) { }
            Check(store.Read().Outcomes.MinimumResults == 2, "invalid save atomic");
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
        OutcomeFrame F(double t, string action = "Cover") =>
            new()
            {
                Now = t,
                Target = "enemy",
                Instance = 7,
                Action = action,
                Owned = true,
                Origin = new(0, 0, 0),
                Known = new(20, 0, 0),
                Destination = new(2, 0, 0),
                HasDestination = true,
                ExposureKnown = true,
                Exposure = .8,
            };
        OutcomeTrace Good(TacticalOutcomes m, double t, Action<OutcomeFrame>? change = null)
        {
            var f = F(t);
            m.Observe(f, c);
            OutcomeTrace trace = new();
            for (int i = 1; i <= 14; i++)
            {
                f = F(t + i * .1);
                f.Origin = new(2, 0, 0);
                f.Exposure = .1;
                f.Protected = true;
                change?.Invoke(f);
                trace = m.Observe(f, c);
            }
            return trace;
        }
        void Detach(TacticalOutcomes m, double t)
        {
            for (int i = 0; i <= 8; i++)
            {
                var f = F(t + i * .1);
                f.Owned = false;
                m.Observe(f, c);
            }
        }
        OutcomeTrace Review(TacticalOutcomes m, double t, Action<OutcomeFrame>? change = null)
        {
            var f = F(t);
            change?.Invoke(f);
            return m.Review(f, new[] { ("Cover", (DoorPoint?)f.Destination) }, c);
        }
        var mem = new TacticalOutcomes();
        Check(Good(mem, 0).Result == "favorable", "observed protection");
        Check(Review(mem, 1.4).Effects[0].Delta == 0, "one result insufficient");
        Detach(mem, 1.5);
        Good(mem, 2.4);
        var evidence = Review(mem, 3.8);
        Check(
            evidence.Effects[0].Delta == 5 && evidence.Effects[0].Results == 2,
            "two comparable results"
        );
        for (int i = 39; i <= 180; i++)
        {
            var f = F(i * .1);
            f.Protected = true;
            f.Exposure = .1;
            mem.Observe(f, c);
        }
        Check(mem.HistoryCount == 2, "no repeat trials during one continuous action");
        foreach (
            var edit in new Action<OutcomeFrame>[]
            {
                f => f.Target = "other",
                f => f.Instance = 8,
                f => f.Visible = true,
                f => f.Hurt = true,
                f => f.Origin = new(4, 0, 0),
                f => f.Known = new(20, 2, 0),
                f => f.Destination = new(7, 0, 0),
                f => f.Known = new(50, 0, 0),
            }
        )
            Check(Review(mem, 18, edit).Effects[0].Delta == 0, "comparable context only");
        Check(Review(mem, 40).Effects[0].Delta == 0, "retention");
        Check(Review(new(), 3.8).Effects[0].Delta == 0, "observer isolation");
        foreach (
            var edit in new Action<OutcomeFrame>[]
            {
                f => f.Protected = false,
                f => f.Exposure = .7,
                f => f.ExposureKnown = false,
                f => f.Health = .95,
                f => f.Owned = false,
                f => f.Paused = true,
            }
        )
        {
            var m = new TacticalOutcomes();
            Good(m, 0, edit);
            Check(m.HistoryCount == 0, "arrival silence/ambiguous interruption not a success");
        }
        var gap = new TacticalOutcomes();
        gap.Observe(F(0), c);
        var g = F(2);
        g.Protected = true;
        g.Exposure = .1;
        Check(gap.Observe(g, c).Result == "neutral" && gap.HistoryCount == 0, "gap neutral");
        var idle = new TacticalOutcomes();
        for (int i = 0; i <= 65; i++)
            idle.Observe(F(i * .1, "Hold"), c);
        Check(
            idle.Trace.Result == "neutral" && idle.HistoryCount == 0,
            "holding without evidence neutral"
        );
        OutcomeTrace Bad(TacticalOutcomes m, double t, bool ambiguous = false, bool forced = true)
        {
            var f = F(t, "Push");
            m.Observe(f, c);
            m.Hit(t + .05, ambiguous ? "other" : "enemy", 7, 20);
            f = F(t + .1, "Push");
            f.Health = .8;
            f.Owned = false;
            f.ForcedRetreat = forced;
            return m.Observe(f, c);
        }
        var hurt = new TacticalOutcomes();
        Check(Bad(hurt, 0).Result == "unfavorable", "own hit followed by forced retreat");
        Detach(hurt, .2);
        Bad(hurt, 1.1);
        var q = F(1.21, "Push");
        var penalty = hurt.Review(q, new[] { ("Push", (DoorPoint?)q.Destination) }, c);
        Check(penalty.Effects[0].Delta == -10, "bounded penalty");
        Check(Bad(new(), 0, true).Result == "neutral", "unidentified or other shooter neutral");
        Check(Bad(new(), 0, false, false).Result == "neutral", "damage alone not defeat");
        var covering = new TacticalOutcomes();
        covering.Observe(F(0), c);
        covering.Hit(.05, "enemy", 7, 10);
        var cv = F(.1);
        cv.Health = .8;
        cv.ForcedRetreat = true;
        Check(covering.Observe(cv, c).Result == "pending", "already in cover not forced retreat");
        var late = new TacticalOutcomes();
        late.Observe(F(0), c);
        late.Hit(7, "enemy", 7, 20);
        Check(late.Observe(F(7), c).RecognizedDamage == 0, "late damage ignored");
        var lateRetreat = new TacticalOutcomes();
        for (int i = 0; i <= 59; i++)
            lateRetreat.Observe(F(i * .1, "Push"), c);
        lateRetreat.Hit(5.95, "enemy", 7, 20);
        var lr = F(6.1, "Push");
        lr.Health = .8;
        lr.Owned = false;
        lr.ForcedRetreat = true;
        Check(
            lateRetreat.Observe(lr, c).Result == "neutral" && lateRetreat.HistoryCount == 0,
            "retreat after evaluation deadline stays neutral"
        );
        var clean = new TacticalOutcomes();
        Good(clean, 0);
        clean.Retain(new HashSet<string>());
        Check(clean.HistoryCount == 0, "native forgetting");
        foreach (
            var edit in new Action<OutcomeFrame>[]
            {
                f => f.Scoped = false,
                f => f.HasContact = false,
                f => f.ContactAge = 25,
                f => f.ContactAge = double.NaN,
                f => f.Exposure = double.NaN,
                f => f.Health = -1,
            }
        )
        {
            var m = new TacticalOutcomes();
            Good(m, 0);
            var f = F(1.5);
            edit(f);
            Check(m.Observe(f, c).State == "ended" && m.HistoryCount == 0, "scope/invalid clears");
        }
        mem.Clear();
        Check(mem.HistoryCount == 0 && !mem.HasAttempt, "encounter end");
        var cfg2 = new CometConfig();
        Situation S() =>
            new()
            {
                Target = "enemy",
                IsAi = true,
                IsSupportedBot = true,
                IsActive = true,
                IsAuthority = true,
                Outcome = evidence.Copy(),
            };
        List<Candidate> Candidates() =>
            new()
            {
                new()
                {
                    Decision = "Cover",
                    Score = 50,
                    Eligible = true,
                },
            };
        var candidates = Candidates();
        var s = S();
        OutcomeScoring.Adjust(candidates, s, cfg2);
        Check(candidates[0].Score == 55 && s.Outcome!.Effects[0].Applied == 5, "score applied");
        foreach (
            var edit in new Action<Situation>[]
            {
                x => x.Ghost = true,
                x => x.IsSupportedBot = false,
                x => x.IsAuthority = false,
                x => x.Hurt = true,
                x => x.LowAmmo = true,
                x => x.Suppressed = true,
                x => x.Target = "other",
                x => x.Outcome!.Remaining = 0,
            }
        )
        {
            s = S();
            edit(s);
            candidates = Candidates();
            OutcomeScoring.Adjust(candidates, s, cfg2);
            Check(candidates[0].Score == 50, "urgency/scope priority");
        }
        candidates = Candidates();
        candidates[0].Eligible = false;
        OutcomeScoring.Adjust(candidates, S(), cfg2);
        Check(candidates[0].Score == 50, "ineligible stays ineligible");
        candidates = Candidates();
        OutcomeScoring.Adjust(candidates, S(), cfg2, true);
        Check(candidates[0].Score == 50, "visible danger priority");
        cfg2.Outcomes.Enabled = false;
        candidates = Candidates();
        OutcomeScoring.Adjust(candidates, S(), cfg2);
        Check(candidates[0].Score == 50, "disabled");
        evidence.Effects[0].Delta = 999;
        cfg2.Outcomes.Enabled = true;
        candidates = Candidates();
        OutcomeScoring.Adjust(candidates, S(), cfg2);
        Check(candidates[0].Score == 55, "bonus hard cap");
        var copy = evidence.Copy();
        copy.Effects[0].Delta = 0;
        Check(evidence.Effects[0].Delta == 999, "immutable diagnostic snapshots");
        // Repeated out-of-scope samples must not clone the full trace every tick.
        var perfIdle = new TacticalOutcomes();
        perfIdle.Clear("outcome-out-of-scope");
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
            perfIdle.Clear("outcome-out-of-scope");
        Check(
            GC.GetAllocatedBytesForCurrentThread() - allocated == 0,
            "repeated idle cleanup is allocation free"
        );
        var options = new[] { "Hold", "Search", "Push", "FallBack", "Flank", "Cover", "Grenade" }
            .Select(x => (x, (DoorPoint?)new DoorPoint(2, 0, 0)))
            .ToArray();
        var reviewFrame = F(20);
        for (int i = 0; i < 100; i++)
            perfIdle.Review(reviewFrame, options, c);
        allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
            perfIdle.Review(reviewFrame, options, c);
        long perReview = (GC.GetAllocatedBytesForCurrentThread() - allocated) / 1000;
        Check(perReview <= 2048, "seven-choice review allocation bound: " + perReview);
        Console.WriteLine($"E04 seven-choice review: {perReview} bytes/call (desktop harness).");
        Console.WriteLine($"E04: {n} tactical outcome checks passed");
        return n;
    }
}
