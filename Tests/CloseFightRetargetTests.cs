using Comet.Client;
using SAIN.SAINComponent.Classes.Decision;
using SAIN.SAINComponent.Classes.EnemyClasses;

internal static class CloseFightRetargetTests
{
    internal static int Run()
    {
        var current = new Enemy("held", 5);
        var close = new Enemy("closer", 2);
        var tied = new Enemy("same path", 2);
        var invisible = new Enemy("hidden", 1);
        invisible.Contact.Visible = false;
        var ineligible = new Enemy("ineligible", 0.5f);
        ineligible.Contact.Valid = false;
        var boundary = new Enemy("60 percent", 3);
        var known = new EnemyList { current, invisible, ineligible, boundary, null! };
        var decision = new DogFightDecisionClass(known);
        var policy = new CloseFightRetarget();
        var tested = new List<Enemy>();
        bool Eligible(DogFightDecisionClass owner, Enemy candidate)
        {
            if (!ReferenceEquals(owner, decision))
                throw new Exception("Native eligibility must use the owning decision's personality.");
            tested.Add(candidate);
            return candidate.Contact.Valid;
        }

        int count = 0;
        void Expect(float now, Enemy? expected, params Enemy[] eligibilityCalls)
        {
            tested.Clear();
            var result = policy.Find(decision, known, current, now, Eligible);
            if (!ReferenceEquals(result, expected) || !tested.SequenceEqual(eligibilityCalls))
                throw new Exception($"Unexpected dogfight target or eligibility checks at {now:R}.");
            count++;
        }

        // At 5 m, exactly 3 m is excluded. A miss still starts the per-bot interval.
        Expect(10, null, ineligible);
        known.Add(close);
        Expect(MathF.BitDecrement(10.25f), null);
        Expect(10.25f, close, ineligible, close);
        known.Add(tied);
        // Another bot instance can scan in the same frame. A scan does not reorder SAIN's list.
        var other = new CloseFightRetarget();
        var otherDecision = new DogFightDecisionClass(known);
        if (other.Find(otherDecision, known, current, 10.25f,
            (owner, e) => owner == otherDecision && e.Contact.Valid) != close)
            throw new Exception("Expected an independent dogfight timer per instance.");
        Expect(10.4f, null);
        Expect(10.5f, close, ineligible, close, tied);

        // A longer navigation path rules out a candidate even at a short straight-line distance.
        close.PathLength = 8;
        tied.PathLength = 9;
        boundary.PathLength = MathF.BitDecrement(3f);
        Expect(10.75f, boundary, ineligible, boundary);
        var closerStill = new Enemy("shortest", 1.5f);
        known.Add(closerStill);
        Expect(11, closerStill, ineligible, boundary, closerStill);
        known.Clear();
        known.Add(current);
        Expect(11.25f, null);
        known.Add(close);
        close.PathLength = 2;
        Expect(11.49f, null);
        Expect(11.5f, close, close);
        return count + 1;
    }
}
