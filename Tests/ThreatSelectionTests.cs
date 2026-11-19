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
        Check(p.Score(b, 10).Score == score.Score + 30, "source PMC bonus restored");
        var ids = f.Contacts.Select(x => x.Id).ToArray();
        p.Evaluate(f);
        Check(
            f.Contacts.Select(x => x.Id).SequenceEqual(ids),
            "never reorder SAIN known enemy list"
        );
        f.Ghost = true;
        Check(p.Evaluate(f).Selected == "", "ghost remains outside active AI");

        return n;
    }
}
