using System.Text.Json;
using Comet.Core;

static class GrenadeTrajectoryTests
{
    public static int Run()
    {
        int n = 0;
        void Check(bool ok, string why)
        {
            n++;
            if (!ok)
                throw new Exception("G02: " + why);
        }
        var c = new GrenadeTrajectoryConfig();
        c.Validate();
        Check(
            c.Enabled
                && c.Corners
                && c.Doors
                && c.Distance == 45
                && c.CornerDistance == 5
                && c.DoorDepthFirst == 1.8
                && c.DoorDepthSecond == 1.2
                && c.DoorDepthThird == 2.6
                && c.PreparedLifetime == .5,
            "approved defaults"
        );
        Check(
            JsonSerializer.Deserialize<CometConfig>("{}")!.GrenadeTrajectory.Enabled,
            "old configs get defaults"
        );
        foreach (
            var prop in typeof(GrenadeTrajectoryConfig)
                .GetProperties()
                .Where(p => p.PropertyType == typeof(double))
        )
        foreach (var value in new[] { double.NaN, double.PositiveInfinity, -1d, 1000d })
        {
            var x = new GrenadeTrajectoryConfig();
            prop.SetValue(x, value);
            bool threw = false;
            try
            {
                x.Validate();
            }
            catch (ArgumentException)
            {
                threw = true;
            }
            Check(threw, "invalid " + prop.Name);
        }
        bool missing = false;
        try
        {
            new CometConfig { GrenadeTrajectory = null! }.Validate();
        }
        catch (ArgumentException)
        {
            missing = true;
        }
        Check(missing, "missing config rejected");
        var budget = new GrenadeArcBudget();
        Check(
            budget.TryBegin(1) && budget.TryBegin(1) && !budget.TryBegin(1),
            "two work units maximum"
        );
        Check(budget.TryBegin(2), "next frame");
        budget.Charge(.5);
        Check(!budget.TryBegin(2), "soft time budget");
        Check(budget.TryBegin(3), "reset elapsed");
        budget.Charge(double.NaN);
        Check(!budget.TryBegin(3), "invalid cost closes budget");
        budget.Clear();
        Check(budget.TryBegin(3), "raid reset");
        Check(
            GrenadeTrajectoryRules.Angles(false, true).SequenceEqual(new[] { 15, 25, 35, 45 }),
            "local ceiling prioritizes low angles without forbidding a verified high arc"
        );
        Check(
            GrenadeTrajectoryRules.Angles(true, false).SequenceEqual(new[] { 15, 25 }),
            "source flat angles for corner alternatives"
        );
        Check(
            GrenadeTrajectoryRules.Dispersion(0) == .5
                && GrenadeTrajectoryRules.Dispersion(50) == 5
                && GrenadeTrajectoryRules.Dispersion(80) == 5,
            "distance dispersion bounded"
        );
        Check(
            GrenadeTrajectoryRules.Fresh(1.24, 1, .25)
                && !GrenadeTrajectoryRules.Fresh(1.25, 1, .25)
                && !GrenadeTrajectoryRules.Fresh(.9, 1, .25),
            "exact expiration and clock rollback"
        );
        Console.WriteLine($"G02 policy/config: {n} checks passed");
        return n;
    }
}
