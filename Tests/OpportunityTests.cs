using System;
using System.Linq;
using Comet.Core;

// Behavioral contract: zzap fbb86b83 (see SOURCE_BASELINE.md).
internal static class OpportunityTests
{
    public static int Run()
    {
        int n = 0;
        void Check(bool ok, string why)
        {
            n++;
            if (!ok)
                throw new Exception("Opportunity: " + why);
        }

        var c = new OpportunityConfig();
        var m = new OpportunityMemory();
        Check(m.Hear("e", 1, 10, "surgery", true, 0, 0, 0, c), "real audio is recorded");
        Check(m.Snapshot("e", 1, 10, c).State == "assess", "no extra reaction delay");
        var t = m.Snapshot("e", 1, 10, c);
        var s = new Situation
        {
            IsAi = true,
            IsSupportedBot = true,
            IsAuthority = true,
            IsActive = true,
            HasContact = true,
            Target = "e",
            Personality = "Normal",
            Opportunity = t,
        };
        var v = new OpportunityTravel
        {
            Ready = true,
            Complete = true,
            PathLength = 10,
        };
        OpportunityModel.Assess(t, s, v, c);
        Check(t.State == "usable" && t.Eta == 2, "source ETA uses five metres per second");
        var choices = new System.Collections.Generic.List<Candidate>
        {
            new()
            {
                Decision = "Push",
                Score = 40,
                Eligible = true,
            },
        };
        OpportunityModel.Adjust(choices, s, c);
        Check(choices[0].Score == 40, "opportunity diagnostic adds no second utility bonus");
        Check(
            m.Hear("e", 1, 11, "shot", true, 0, 0, 0, c)
                && m.Snapshot("e", 1, 11, c).State == "interrupted",
            "actual shot interrupts audio opportunity"
        );
        m.Clear();
        Check(m.Count == 0, "raid cleanup");

        return n;
    }
}
