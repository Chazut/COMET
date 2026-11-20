using System.Reflection;
using System.Text.Json;
using Comet.Core;

static class HiddenExecutionTests
{
    public static int Run()
    {
        int checks = 0;
        void Check(bool ok, string why)
        {
            checks++;
            if (!ok)
                throw new Exception(why);
        }
        static Situation Frame(double now = 10) =>
            new()
            {
                Now = now,
                Target = "enemy",
                IsAi = true,
                IsSupportedBot = true,
                IsAuthority = true,
                IsActive = true,
                HasContact = true,
                ContactAge = 5,
                KnownDistance = 30,
            };
        static HiddenEnemyDecision Seed(List<Candidate> ranked)
        {
            var config = new CometConfig();
            config.DecisionVariety.Enabled = false;
            config.TacticalMistakes.Enabled = false;
            var engine = new HiddenEnemyDecision(config);
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            foreach (
                var pair in new (string, object)[]
                {
                    ("ranked", ranked),
                    ("preference", ranked.ToList()),
                    ("target", "enemy"),
                    ("until", 15d),
                }
            )
                typeof(HiddenEnemyDecision)
                    .GetField(pair.Item1, flags)!
                    .SetValue(engine, pair.Item2);
            return engine;
        }
        static List<Candidate> Scores(params (Tactic, double)[] values) =>
            values
                .Select(v => new Candidate
                {
                    Decision = v.Item1.ToString(),
                    Score = v.Item2,
                    Eligible = true,
                    Reason = "available",
                })
                .ToList();
        using (
            var fixture = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "back4streetz-hidden.json"))
            )
        )
        {
            var rows = JsonSerializer.Deserialize<List<Candidate>>(
                fixture.RootElement.GetProperty("candidates").GetRawText(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            )!;
            rows = rows.OrderByDescending(c => c.Score).ToList();
            var s = Frame();
            foreach (var row in rows.Where(c => !c.Eligible))
                s.Unavailable[Enum.Parse<Tactic>(row.Decision)] = row.Reason;
            var result = Seed(rows).Evaluate(s);
            Check(
                result.Decision == null,
                "Back4Streetz 03:10.603: refused Search must not promote negative Hold/FallBack"
            );
        }
        foreach (double score in new[] { -100d, 0, 5, 5.01 })
        {
            int calls = 0;
            var c = Seed(Scores((Tactic.Hold, score)))
                .Evaluate(Frame(), _ =>
                {
                    calls++;
                    return "";
                });
            Check(
                c.Decision.HasValue == (score > 5) && calls == (score > 5 ? 1 : 0),
                "exact source score floor"
            );
        }
        {
            var config = new CometConfig();
            foreach (string key in config.Weights.Keys.ToArray())
                config.Weights[key] = key == "Hold" ? 1 : 0;
            Situation Contact(double seconds, string target, bool frozen)
            {
                var s = Frame(100 + seconds);
                s.KnownDistance = 11;
                s.ContactAge = 5 + seconds;
                s.Target = target;
                s.Frozen = frozen;
                s.InCover = true;
                return s;
            }
            static double HoldScore(Choice choice) =>
                choice.Candidates.Single(c => c.Decision == "Hold").Score;

            foreach (bool alternate in new[] { false, true })
            {
                var engine = new HiddenEnemyDecision(config);
                Choice choice = null!;
                for (int i = 0; i <= 9; i++)
                    choice = engine.Evaluate(Contact(5 * i, alternate && i % 2 == 1 ? "b" : "a", true));
                Check(HoldScore(choice) == -100 && choice.Decision == null,
                    $"continuous Freeze caps Hold at 45 seconds, alternating targets={alternate}; actual score={HoldScore(choice)}");
            }

            var cached = new HiddenEnemyDecision(config);
            var first = cached.Evaluate(Contact(0, "a", false), _ => "unavailable");
            var retry = cached.Evaluate(Contact(1, "a", false), _ => "unavailable");
            Check(first.Decision == null && ReferenceEquals(first.Candidates, retry.Candidates)
                && cached.RankedHoldSeconds == 0 && !cached.RankedHoldCapped,
                "native refusals reuse the top Hold preference");
            Check(HoldScore(cached.Evaluate(Contact(39.99, "b", true))) > -100,
                "the continuous Freeze is below the cap just before forty seconds");
            Check(HoldScore(cached.Evaluate(Contact(40, "a", true))) == -100
                && cached.RankedHoldSeconds == 40 && cached.RankedHoldCapped,
                "cached execution refusals preserve the ranked Hold clock through the exact cap");
            Check(HoldScore(cached.Evaluate(Contact(41, "b", false))) > -100
                && cached.RankedHoldSeconds == 0 && !cached.RankedHoldCapped,
                "a new target starts fresh once SAIN leaves Freeze");

            config.Weights["Push"] = 3;
            config.DecisionVariety.Enabled = false;
            config.TacticalMistakes.Enabled = false;
            var push = new HiddenEnemyDecision(config);
            Check(push.Evaluate(Contact(0, "a", true)).Decision == Tactic.Push,
                "a native Freeze starts the clock even with Push ranked first");
            var moved = push.Evaluate(Contact(10, "b", false));
            Check(moved.Decision == Tactic.Push, "a mobile bot reranks without Hold");
            push.Evaluate(Contact(11, "c", true));
            Check(HoldScore(push.Evaluate(Contact(45, "a", true))) > -100,
                "time before leaving Freeze does not carry into the next Freeze");
            Check(HoldScore(push.Evaluate(Contact(51, "b", true))) == -100,
                "the new Freeze reaches its own cap even with Push ranked first");
        }
        {
            var e = Seed(Scores((Tactic.Flank, 70), (Tactic.Search, 60)));
            var hidden = e.Evaluate(Frame());
            var s = Frame(15.5);
            s.Visible = s.SightWithoutShot = true;
            s.UnderFire = true;
            Check(!e.NeedsEvidence(s), "seeing the same target behind cover needs no new scores");
            var kept = e.Evaluate(s);
            Check(kept.Decision == Tactic.Flank && !kept.Changed
                && ReferenceEquals(kept.Candidates, hidden.Candidates)
                && kept.Reason == "zzap-hidden-memory-on-sight",
                "blocked muzzle continues the same flank without a new selection");
            s.Now = 15.999;
            var fallback = e.Evaluate(s, t => t == Tactic.Flank ? "path-lost" : "");
            Check(fallback.Decision == Tactic.Search && fallback.Changed,
                "cached options still go through native execution checks");
            s.Now = 16;
            Check(e.Evaluate(s).Decision == null, "repeated samples cannot extend the one-second grace");
        }
        foreach (var change in new Action<Situation>[] {
            s => s.Target = "other", s => s.SightWithoutShot = false,
            s => s.HasContact = false, s => s.IsAuthority = false, s => s.Ghost = true,
        })
        {
            var s = Frame(14);
            s.Visible = s.SightWithoutShot = true;
            change(s);
            Check(Seed(Scores((Tactic.Flank, 70))).Evaluate(s).Decision == null,
                "visibility fallback respects target, sightline, contact and lifetime");
        }
        {
            var s = Frame();
            s.Visible = s.SightWithoutShot = true;
            Check(new HiddenEnemyDecision(new CometConfig()).Evaluate(s).Decision == null,
                "seeing a target with a blocked shot cannot create a hidden ranking");
            var e = Seed(Scores((Tactic.Flank, 70)));
            e.Evaluate(Frame());
            e.SuspendSelection();
            s.Target = "other";
            Check(!e.CanResumeOnSight(s) && e.Current == null,
                "a visible handoff cannot give another target the old action");
            s.Target = "enemy";
            Check(e.Evaluate(s).Changed && e.Current == Tactic.Flank,
                "reacquiring the original target can reselect its own unexpired flank");
        }
        foreach (var fallback in new[] { Tactic.Hold, Tactic.FallBack })
        foreach (double score in new[] { 37.99, 38, 49 })
        {
            var c = Seed(Scores((Tactic.Search, 50), (fallback, score)))
                .Evaluate(Frame(), t => t == Tactic.Search ? "path-refused" : "");
            Check(c.Decision.HasValue == (score >= 38), "source fallback margin is 12, inclusive");
        }
        foreach (
            var reason in new[] { "zzap-hold-under-fire", "zzap-hold-other-enemy", "disabled" }
        )
        {
            var c = Seed(Scores((Tactic.Hold, 80), (Tactic.FallBack, 6)))
                .Evaluate(Frame(), t => t == Tactic.Hold ? reason : "");
            Check(
                (c.Decision == Tactic.FallBack) == (reason != "disabled"),
                "only source danger skips permit distant-score fallback"
            );
        }
        {
            var calls = new List<Tactic>();
            var e = Seed(Scores((Tactic.Search, 40), (Tactic.Flank, 30), (Tactic.Hold, 29)));
            var a = e.Evaluate(Frame(), t =>
            {
                calls.Add(t);
                return t == Tactic.Search ? "path-refused" : "";
            });
            Check(
                a.Decision == Tactic.Flank
                    && calls.SequenceEqual(new[] { Tactic.Search, Tactic.Flank }),
                "native probes execute once in rank order and stop on success"
            );
            calls.Clear();
            var b = e.Evaluate(Frame(10.1), t =>
            {
                calls.Add(t);
                return "";
            });
            Check(
                b.Decision == Tactic.Search
                    && calls.SequenceEqual(new[] { Tactic.Search })
                    && !e.NeedsEvidence(Frame(10.1)),
                "native availability refreshes without reranking"
            );
        }
        {
            // Deliberate mistake at the front must stay the preference, not the numeric maximum.
            var e = Seed(Scores((Tactic.Hold, 6), (Tactic.Search, 70)));
            Check(e.Evaluate(Frame()).Decision == Tactic.Hold, "source preference order retained");
            e.Evaluate(Frame(10.1), _ => "unavailable");
            var keep = e.Keep(Tactic.Hold, "enemy");
            Check(
                keep.Decision == Tactic.Hold && !keep.Changed && keep.Reason == "utilityKeep",
                "transient refusal does not emit a fake selection every tick"
            );
            Check(
                !e.NeedsEvidence(Frame(10.2)) && e.NeedsEvidence(Frame(15)),
                "keep never extends ranking deadline"
            );
            e.Release("ghost");
            Check(e.NeedsEvidence(Frame(10.3)), "real lifetime handoff clears ranking");
        }
        foreach (
            var (action, falling, inCover, distance, limit) in new[]
            {
                ("SeekCover", true, false, 20d, 5d),
                ("Search", false, false, 20d, 6d),
                ("RushEnemy", false, false, 20d, 6d),
                ("Search", false, false, 1.99, 2d),
            }
        )
        {
            var w = new HiddenMovementWatch();
            var at = new DoorPoint(1, 2, 3);
            Check(
                w.Observe(10, at, action, falling, inCover, distance) == ""
                    && w.Observe(10 + limit - .01, at, action, falling, inCover, distance) == "",
                "stall window not shortened"
            );
            Check(
                w.Observe(10 + limit, at, action, falling, inCover, distance) != ""
                    && w.FallBackBanUntil == 25 + limit,
                "source stall trips at exact threshold"
            );
            Check(
                w.MoveBanUntil == (falling ? 0 : 22 + limit),
                "only Search/Push get source movement ban"
            );
        }
        foreach (var action in new[] { "Freeze", "ShiftCover", "StandAndShoot" })
        {
            var w = new HiddenMovementWatch();
            w.Observe(0, default, action, false, false, 1);
            Check(
                w.Observe(999, default, action, false, false, 1) == "",
                "intentional static actions never trip move watchdog"
            );
        }
        {
            var w = new HiddenMovementWatch();
            w.Observe(0, default, "Search", false, false, 20);
            w.Observe(5, new DoorPoint(.5001, 0, 0), "RushEnemy", false, false, 20);
            Check(
                w.Observe(6, new DoorPoint(.5001, 0, 0), "Search", false, false, 20) == "",
                "movement over half metre resets watch"
            );
            w.Pause();
            Check(
                w.Observe(999, default, "Search", false, false, 20) == "",
                "Ghost pause does not count as stuck time"
            );
            w = new();
            w.Observe(0, default, "Search", false, false, 20);
            Check(
                w.Observe(6, new DoorPoint(.5, 0, 0), "RushEnemy", false, false, 20) != "",
                "half metre and action changes do not reset shared watch"
            );
            w = new();
            w.Observe(0, default, "SeekCover", true, true, 20);
            Check(
                w.Observe(99, default, "SeekCover", true, true, 20) == "",
                "occupied cover is not stalled fallback"
            );
        }
        Check(
            SourceSearchRules.QuietHeard(false, true, 15, true, false, 0),
            "quiet heard fallback begins at 15 seconds"
        );
        Check(
            !SourceSearchRules.QuietHeard(false, true, 14.99, true, false, 20)
                && !SourceSearchRules.QuietHeard(true, true, 20, true, false, 20)
                && !SourceSearchRules.QuietHeard(false, false, 20, true, false, 20)
                && !SourceSearchRules.QuietHeard(false, true, 20, false, false, 20),
            "quiet fallback preserves source contact/personality checks"
        );
        Check(
            !SourceSearchRules.QuietHeard(false, true, 15, true, true, 15)
                && SourceSearchRules.QuietHeard(false, true, 15, true, true, 15.01),
            "normal audio search exception uses exact wait threshold"
        );
        Check(
            SourceSearchRules.PartialEnd(true, 1, 4)
                && !SourceSearchRules.PartialEnd(true, 1, 4.01)
                && !SourceSearchRules.PartialEnd(false, 1, 0)
                && !SourceSearchRules.PartialEnd(true, 0, 0),
            "partial search finishes only within two metres of an existing partial end"
        );
        Console.WriteLine($"Back4Streetz hidden source parity: {checks} checks passed.");
        return checks;
    }
}
