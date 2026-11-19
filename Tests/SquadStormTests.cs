using Comet.Core;
using Newtonsoft.Json;

internal static class SquadStormTests
{
    public static int Run()
    {
        int n = 0;
        void C(bool x, string why)
        {
            n++;
            if (!x)
                throw new Exception("S06: " + why);
        }
        var c = new SquadStormConfig();
        c.Validate();
        C(
            c.Enabled
                && c.Weakness == .5
                && c.MaxPath == 40
                && c.SquadDistance == 35
                && c.MinimumAttackers == 2
                && c.MinimumAmmo == 40
                && c.ContactAge == 12
                && c.Duration == 10
                && c.PrepareTimeout == 4
                && c.Retry == 20,
            "approved defaults"
        );
        foreach (
            var (p, v) in new[]
            {
                ("GigaChad", 100d),
                ("Chad", 100d),
                ("Wreckless", 100d),
                ("Normal", 80d),
                ("Timmy", 60d),
                ("Rat", 35d),
                ("SnappingTurtle", 35d),
                ("Coward", 10d),
            }
        )
            C(c.Chance(p) == v, p);
        C(c.Chance("unknown") == 0, "unknown personality does not automatically charge");
        foreach (var v in new[] { double.NaN, double.PositiveInfinity, -1, 101 })
        {
            var bad = new SquadStormConfig { Normal = v };
            bool caught = false;
            try
            {
                bad.Validate();
            }
            catch (ArgumentException)
            {
                caught = true;
            }
            C(caught, "invalid chance");
        }
        var old = JsonConvert.DeserializeObject<CometConfig>("{}")!;
        old.Validate();
        C(old.SquadStorm.Enabled && old.SquadStorm.MinimumAttackers == 2, "older config migration");
        StormEvidence E(double at = 10) =>
            StormEvidence.Assess(
                at,
                "a",
                new() { ("body", "none-visible", at), ("weapon", "pistol", at) },
                true,
                "",
                0,
                0,
                false,
                c
            );
        C(Math.Abs(E().Score - .7) < .001, "perceived weak kit score");
        foreach (
            var value in new[] { "unknown", "visible-protection", "class-1", "empty", "grenade" }
        )
        {
            var e = StormEvidence.Assess(
                10,
                "a",
                new() { ("body", value, 10), ("weapon", value, 10), ("head", value, 10) },
                true,
                "",
                0,
                0,
                false,
                c
            );
            C(e.Score == 0, "no invented weakness " + value);
        }
        C(
            StormEvidence
                .Assess(23, "a", new() { ("body", "none-visible", 10) }, true, "", 0, 0, false, c)
                .Score == 0,
            "stale observations"
        );
        C(
            StormEvidence
                .Assess(10, "a", new() { ("body", "none-visible", 11) }, true, "", 0, 0, false, c)
                .Score == 0,
            "future observations"
        );
        C(
            StormEvidence
                .Assess(
                    10,
                    "a",
                    new() { ("weapon", "pistol", 9), ("weapon", "assaultRifle", 10) },
                    true,
                    "",
                    0,
                    0,
                    false,
                    c
                )
                .Score == 0,
            "new observation replaces old"
        );
        foreach (string cue in new[] { "reload", "heal", "surgery", "looting", "dry-fire" })
        {
            var e = StormEvidence.Assess(
                10,
                "a",
                new() { ("body", "none-visible", 10) },
                true,
                cue,
                9,
                2,
                false,
                c
            );
            C(
                e.Score >= .5 && e.Until == 12 && e.Cue == cue,
                "bounded personally heard opening " + cue
            );
        }
        var heard = StormEvidence.Assess(10, "a", new(), true, "reload", 10, 2, false, c);
        C(
            heard.Score >= c.Weakness,
            "heard reload plus apparently isolated contact suffices without inspected gear"
        );
        var numbers = StormEvidence.Assess(10, "a", new(), false, "surgery", 10, 12, false, c);
        numbers.ApplyNumbers(3, c.Weakness);
        C(
            numbers.Score >= c.Weakness && numbers.Reason == "perceived-weakness",
            "observed opening plus numerical advantage permits assault"
        );
        double once = numbers.Score;
        numbers.ApplyNumbers(3, c.Weakness);
        C(numbers.Score == once, "numerical advantage cannot accumulate on repeated callbacks");
        var none = StormEvidence.Assess(10, "a", new(), true, "", 0, 0, false, c);
        none.ApplyNumbers(4, c.Weakness);
        C(none.Score == 0, "numbers alone never invent vulnerability");
        var danger = StormEvidence.Assess(
            10,
            "a",
            new() { ("body", "none-visible", 10), ("weapon", "pistol", 10) },
            true,
            "grenade",
            10,
            5,
            true,
            c
        );
        C(danger.Danger && danger.Cue == "", "grenade veto even with weak kit");
        List<StormMember> M(int count = 2, string personality = "Chad", double roll = .1) =>
            Enumerable
                .Range(0, count)
                .Select(i => new StormMember
                {
                    Id = i.ToString(),
                    Name = "PMC " + i,
                    Personality = personality,
                    Ammo = 100,
                    Roll = roll,
                })
                .ToList();
        for (int count = 2; count <= 8; count++)
        {
            var board = new SquadStormBoard();
            var p = board.Start("group", "target", 10, E(), M(count), c)!;
            C(p != null, "proposal " + count);
            p!.Members[0].Ready = true;
            p.Members[0].LastCallback = 10.1;
            p.Tick(10.1, c);
            C(p.State == "active", "one volunteer departs without collective readiness barrier");
            foreach (var m in p.Members)
            {
                m.Ready = true;
                m.LastCallback = 10.2;
            }
            C(
                p.Tick(10.2, c) && p.State == "active" && p.Departure == 10.1,
                "first volunteer departure persists"
            );
            C(p.Members.All(x => x.Status != "ended"), "no artificial attacker cap");
            p.Drop("0", "own urgent care");
            C(p.Active, "individual withdrawal");
            p.Tick(11.3, c);
            C(p.Active, "diagnostic callback gaps do not abort source assault");
            p.End("test-ended");
            board.Finish(p, 11.3, c, p.Reason);
            C(board.Start("group", "target", 12, E(12), M(), c) == null, "retry delay");
            C(board.Start("group", "target", 32, E(10), M(), c) == null, "no repeat old evidence");
            C(
                board.Start("group", "target", 32, E(32), M(), c) != null,
                "new evidence after cooldown"
            );
            board.Clear();
            C(board.Get("group") == null, "raid cleanup");
        }
        {
            var p = new SquadStormBoard().Start("g", "e", 10, E(), M(3), c)!;
            foreach (var m in p.Members.Take(2))
            {
                m.Ready = true;
                m.LastCallback = 10.1;
            }
            C(
                p.Tick(10.1, c) && p.State == "active" && p.Members[2].Status != "ended",
                "ready pair departs without discarding third member still planning"
            );
            p.Members[2].Ready = true;
            foreach (var m in p.Members)
                m.LastCallback = 11;
            C(
                p.Tick(11, c) && p.Members.All(x => x.Status != "ended"),
                "late teammate can join within preparation window"
            );
        }
        foreach (string personality in new[] { "Normal", "Timmy", "Rat", "Coward" })
        {
            var board = new SquadStormBoard();
            C(
                board.Start("g", "e", 10, E(), M(4, personality, .99), c) == null,
                "not enough volunteers " + personality
            );
            C(
                board.LastAttempt!.Members.All(x => x.Status == "declined"),
                "retain refusal reasons"
            );
        }
        {
            var board = new SquadStormBoard();
            var members = M();
            members[1].Ammo = 39;
            C(
                board
                    .Start("g", "e", 10, E(), members, c)
                    ?.Members.Count(m => m.Status == "declined") == 1,
                "one member failing ammo does not veto the other volunteer"
            );
        }
        {
            var board = new SquadStormBoard();
            var p = board.Start("g", "e", 10, E(), M(), c)!;
            C(p.Tick(14, c), "no COMET preparation barrier");
        }
        {
            var board = new SquadStormBoard();
            var e = E();
            e.Cue = "reload";
            e.Until = 11;
            var p = board.Start("g", "e", 10, e, M(), c)!;
            foreach (var m in p.Members)
            {
                m.Ready = true;
                m.LastCallback = 10.2;
                m.Eta = 1;
            }
            C(p.Tick(10.2, c), "source assault does not require ETA inside an inferred opening");
        }
        foreach (string why in new[] { "clock", "disabled", "expiry" })
        {
            var local = new SquadStormConfig();
            var p = new SquadStormBoard().Start("g", "e", 10, E(), M(), local)!;
            if (why == "danger")
                p.Evidence.Danger = true;
            if (why == "score")
                p.Evidence.Score = .1;
            if (why == "disabled")
                local.Enabled = false;
            C(
                !p.Tick(
                    why == "clock" ? 9
                        : why == "expiry" ? 25
                        : 10.2,
                    local
                ),
                "cancel " + why
            );
        }
        foreach (
            double bad in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity }
        )
        {
            var e = E();
            e.Score = bad;
            C(
                new SquadStormBoard().Start("g", "e", 10, e, M(), c) == null,
                "invalid evidence score"
            );
            e = E();
            e.Until = bad;
            C(
                new SquadStormBoard().Start("g", "e", 10, e, M(), c) == null,
                "invalid evidence expiry"
            );
            var p = new SquadStormBoard().Start("g", "e", 10, E(), M(), c)!;
            foreach (var m in p.Members)
            {
                m.Ready = true;
                m.LastCallback = 10.2;
                m.Eta = bad;
            }
            C(p.Tick(10.2, c), "ETA telemetry is not a source assault gate");
        }
        {
            var p = new SquadStormBoard().Start("g", "e", 10, E(), M(4), c)!;
            foreach (var m in p.Members.Take(2))
            {
                m.Ready = true;
                m.LastCallback = 10.1;
            }
            C(p.Tick(10.1, c) && p.State == "active", "first ready pair starts");
            C(
                p.Members.Skip(2).All(m => m.Status == "planning"),
                "other volunteers retain initial preparation window"
            );
            foreach (var m in p.Members)
            {
                m.Ready = true;
                m.LastCallback = 10.8;
            }
            C(
                p.Tick(10.8, c) && p.Members.All(m => m.Status != "ended"),
                "all four may take part after preparing"
            );
        }
        Console.WriteLine($"{n} S06 policy checks passed");
        return n;
    }
}
