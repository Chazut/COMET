using System;
using System.Linq;
using Comet.Core;

// Behavioral contract: zzap fbb86b83 (see SOURCE_BASELINE.md).
internal static class ThreatSelectionTests
{
    public static int Run()
    {
        int n = 0;
        void Check(bool ok, string why)
        {
            n++;
            if (!ok)
                throw new Exception("ThreatSelection: " + why);
        }

        var c = new CometConfig();
        ThreatContact Contact(string id, double d) =>
            new()
            {
                Id = id,
                Valid = true,
                Visible = true,
                ObservedAt = 10,
                Distance = d,
                X = d,
                Y = 0,
                Z = 0,
            };
        var a = Contact("a", 55);
        var b = Contact("b", 3);
        var f = new ThreatFrame
        {
            Now = 10,
            Current = "a",
            IsAi = true,
            IsSupportedBot = true,
            IsAuthority = true,
            Active = true,
        };
        f.Contacts.AddRange(new[] { a, b });
        var p = new ThreatSelection(c);
        Check(
            p.Evaluate(f).Selected == "b",
            "source flank interrupts immediately without COMET initial hold"
        );
        b.Distance = 25;
        Check(
            new ThreatSelection(c).Evaluate(f).Selected == "",
            "unengaged alternative yields to native choice"
        );
        b.HitAt = 10;
        Check(
            new ThreatSelection(c).Evaluate(f).Selected == "b",
            "known recent attacker takes priority"
        );
        var score = p.Score(b, 10);
        Check(score.Hit == 100, "source flat recent-hit bonus");
        Check(p.Score(b, 13).Hit == 0, "hit expires at source memory boundary");
        b.IsPmc = true;
        Check(Math.Abs(p.Score(b, 10).Score - score.Score - 30) < .00005, "PMC bonus in RR score units");
        var ids = f.Contacts.Select(x => x.Id).ToArray();
        p.Evaluate(f);
        Check(
            f.Contacts.Select(x => x.Id).SequenceEqual(ids),
            "never reorder SAIN known enemy list"
        );
        f.Ghost = true;
        Check(p.Evaluate(f).Selected == "", "ghost remains outside active AI");

        var current = Contact("current", 25);
        var challenger = Contact("challenger", 15);
        var pair = new ThreatFrame { Now = 10, Current = current.Id, IsAi = true, IsSupportedBot = true, IsAuthority = true, Active = true };
        pair.Contacts.AddRange(new[] { current, challenger });
        ThreatTrace Compare() => new ThreatSelection(c).Evaluate(pair);
        Check(Compare().Selected == challenger.Id, "visible 15 m challenger against a 25 m goal receives the flank bonus");
        challenger.Distance = MathF.BitIncrement(15f);
        Check(Compare().Selected == "", "the next representable distance is outside the visible flank range");

        current.Distance = 10;
        challenger.Distance = 6;
        Check(Compare().Selected == "", "visible flank uses a strict comparison at the 60 percent boundary");
        challenger.Visible = false;
        challenger.Footsteps = true;
        challenger.HeardAge = 1.5;
        challenger.HeardDistance = 6;
        Check(Compare().Selected == challenger.Id, "heard flank includes equality with 60 percent of goal distance");
        challenger.HeardDistance = MathF.BitIncrement(6f);
        Check(Compare().Selected == "", "heard flank excludes the next representable distance");

        current.Distance = 38;
        current.HitAt = 10;
        current.ShotAt = 6;
        current.IsPmc = current.BadlyInjured = true;
        challenger.Distance = 13;
        challenger.HitAt = 6;
        challenger.Visible = challenger.LookingAtMe = challenger.IsPmc = true;
        var closeScores = Compare();
        Check(closeScores.Selected == challenger.Id && closeScores.Candidates[1].Score > closeScores.Candidates[0].Score,
            "fork's 38 m / 13 m near-tie also exposes the winning score to RR");
        var equalChallenger = Contact("equal", 13);
        equalChallenger.HitAt = 6;
        equalChallenger.LookingAtMe = equalChallenger.IsPmc = true;
        pair.Contacts.Insert(1, equalChallenger);
        Check(Compare().Selected == equalChallenger.Id, "equal challengers keep their original SAIN list order");

        foreach (string outcome in new[] { "return", "expired", "other goal", "ghost", "disabled", "authority lost", "reset" })
        {
            var settings = new CometConfig();
            var selector = new ThreatSelection(settings);
            var first = Contact("first", 50);
            var attacker = Contact("attacker", 25);
            attacker.HitAt = 9.9;
            var frame = new ThreatFrame
            {
                Now = 10, Current = first.Id, IsAi = true, IsSupportedBot = true, IsAuthority = true, Active = true
            };
            frame.Contacts.AddRange(new[] { first, attacker });
            Check(selector.Evaluate(frame).Selected == attacker.Id, "initial attack establishes priority");
            frame.Current = attacker.Id;
            frame.Contacts.Remove(first);
            foreach (double time in new[] { 10.5, 11.75 })
            {
                frame.Now = time;
                var gap = selector.Evaluate(frame);
                Check(gap.Selected == "" && gap.Candidates.Count == 0 && gap.Reason == "threat-yield-to-sain",
                    "SAIN selects the sole enemy");
                Check(gap.HoldRemaining == 12 - time, "the original deadline keeps running during the gap");
            }
            if (outcome is "ghost" or "disabled" or "authority lost")
            {
                frame.Ghost = outcome == "ghost";
                settings.ThreatSelection.Enabled = outcome != "disabled";
                frame.IsAuthority = outcome != "authority lost";
                Check(selector.Evaluate(frame).HoldRemaining == 0, "scope cleanup also applies with only one contact");
                frame.Ghost = false;
                settings.ThreatSelection.Enabled = true;
                frame.IsAuthority = true;
            }
            if (outcome == "reset")
                selector.Reset();
            attacker.HitAt = null;
            frame.Contacts.Add(first);
            frame.Now = outcome == "expired" ? 12 : 11.875;
            frame.Current = outcome == "other goal" ? first.Id : attacker.Id;
            var result = selector.Evaluate(frame);
            Check(outcome == "return" ? result.Selected == attacker.Id && result.Reason == "threat-keep-held"
                                      : result.Selected == "",
                $"two contacts after the gap: {outcome}");
        }
        return n;
    }
}
