using System.Text.Json;
using Comet.Core;

static class RecoilRecoveryTests
{
    public static int Run()
    {
        int n = 0;
        void Check(bool ok, string why)
        {
            n++;
            if (!ok)
                throw new Exception("W09: " + why);
        }
        void Invalid(Action a)
        {
            try
            {
                a();
            }
            catch (ArgumentException)
            {
                n++;
                return;
            }
            throw new Exception("W09 invalid settings accepted");
        }
        var c = new RecoilRecoveryConfig();
        c.Validate();
        Check(!c.Enabled && c.DmrMultiplier == 3 && c.SniperMultiplier == 3, "approved defaults");
        var old = JsonSerializer.Deserialize<CometConfig>("{}")!;
        old.Validate();
        Check(!old.RecoilRecovery.Enabled, "old config defaults off");
        Invalid(() => new CometConfig { RecoilRecovery = null! }.Validate());
        foreach (
            double bad in new[]
            {
                double.NaN,
                double.PositiveInfinity,
                double.NegativeInfinity,
                0,
                .99,
                3.01,
            }
        )
        {
            Invalid(() => new RecoilRecoveryConfig { DmrMultiplier = bad }.Validate());
            Invalid(() => new RecoilRecoveryConfig { SniperMultiplier = bad }.Validate());
        }
        c = new()
        {
            Enabled = true,
            DmrMultiplier = 1.7,
            SniperMultiplier = 2.4,
        };
        var saved = JsonSerializer.Deserialize<RecoilRecoveryConfig>(JsonSerializer.Serialize(c))!;
        saved.Validate();
        Check(
            saved.Enabled && saved.DmrMultiplier == 1.7 && saved.SniperMultiplier == 2.4,
            "independent persistence"
        );
        foreach (
            string kind in new[]
            {
                "assaultRifle",
                "pistol",
                "shotgun",
                "smg",
                "machinegun",
                "unknown",
                "",
            }
        )
            Check(RecoilRecoveryPolicy.Multiplier(c, kind) == 1, "excluded class");
        Check(
            RecoilRecoveryPolicy.Multiplier(c, "marksmanRifle") == 1.7
                && RecoilRecoveryPolicy.Multiplier(c, "sniperRifle") == 2.4,
            "class mapping"
        );
        Check(RecoilRecoveryPolicy.Multiplier(new(), "marksmanRifle") == 1, "off native");
        bool Step(
            double h,
            double v,
            double dt,
            double fixedDt,
            double coef,
            double multi,
            out float nh,
            out float nv,
            out bool done
        ) => RecoilRecoveryPolicy.TryStep(h, v, dt, fixedDt, coef, multi, out nh, out nv, out done);
        foreach (double multi in new[] { 1.1, 1.7, 2, 3 })
        foreach (double coef in new[] { 0d, .2, 2, 20, 100 })
        foreach (double h in new[] { -10d, -.01, 0, .01, 10 })
        {
            Check(
                Step(h, -h, .02, .02, coef, multi, out var nh, out var nv, out var done),
                "accepted finite native step"
            );
            double expected = h * Math.Max(0, 1 - .02 * coef * multi);
            if (Math.Abs(expected) <= .001)
                expected = 0;
            Check(
                Math.Abs(nh - expected) < 1e-5 && Math.Abs(nv + expected) < 1e-5,
                "one fixed tick equals zzap decay with absolute stop"
            );
            Check(
                Math.Abs(nh) <= Math.Abs(h) && Math.Abs(nv) <= Math.Abs(h),
                "no added recoil or sign flip"
            );
        }
        foreach (int fps in new[] { 20, 30, 50, 60, 120, 144, 240 })
        {
            float h = 8,
                v = -4;
            for (int i = 0; i < fps; i++)
            {
                Check(Step(h, v, 1d / fps, .02, 1, 3, out h, out v, out _), "cadence accepted");
            }
            double expected = 8 * Math.Pow(.94, 50);
            Check(
                Math.Abs(h - expected) < 2e-5 && Math.Abs(v + expected / 2) < 2e-5,
                "same elapsed time independent of frequency"
            );
        }
        Check(
            Step(-4, -2, .02, .02, 1, 3, out var x, out var y, out var finished)
                && !finished
                && x < 0
                && y < 0,
            "negative offsets do not prematurely finish"
        );
        Check(
            Step(.0009, -.0009, .02, .02, 1, 3, out x, out y, out finished)
                && finished
                && x == 0
                && y == 0,
            "absolute stop threshold"
        );
        Check(
            Step(4, -2, 0, .02, 1, 3, out x, out y, out finished) && x == 4 && y == -2 && !finished,
            "same timestamp no double decay"
        );
        foreach (
            var input in new[]
            {
                (double.NaN, 1d, .02, .02, 1d, 3d),
                (181d, 1d, .02, .02, 1d, 3d),
                (1d, 1d, -.01, .02, 1d, 3d),
                (1d, 1d, .251, .02, 1d, 3d),
                (1d, 1d, .02, 0d, 1d, 3d),
                (1d, 1d, .02, .2, 1d, 3d),
                (1d, 1d, .02, .02, -1d, 3d),
                (1d, 1d, .02, .02, 1d, 1d),
                (1d, 1d, .02, .02, 1d, 4d),
                (1d, double.PositiveInfinity, .02, .02, 1d, 3d),
            }
        )
            Check(
                !Step(
                    input.Item1,
                    input.Item2,
                    input.Item3,
                    input.Item4,
                    input.Item5,
                    input.Item6,
                    out _,
                    out _,
                    out _
                ),
                "bad/paused/x1 yields native"
            );
        var wire = new List<RecoilRecoveryTrace>();
        var t = new RecoilRecoveryTrace
        {
            Weapon = "test-dmr",
            Action = "StandAndShoot",
            Target = "known-contact",
            Shots = 1,
            BeforeHorizontal = 4,
            BeforeVertical = -2,
            AfterHorizontal = 4,
            AfterVertical = -2,
            Coefficient = 1,
        };
        wire.Add(t);
        Step(4, -2, .02, .02, 1, 3, out x, out y, out _);
        wire.Add(
            new()
            {
                State = "recovering",
                Reason = "recoil-time-based-recovery",
                Weapon = "test-dmr",
                Action = "StandAndShoot",
                Target = "known-contact",
                Shots = 1,
                Steps = 1,
                Applied = true,
                Multiplier = 3,
                BeforeHorizontal = 4,
                BeforeVertical = -2,
                AfterHorizontal = x,
                AfterVertical = y,
                Coefficient = 1,
                Step = .02,
                Elapsed = .02,
            }
        );
        wire.Add(
            new()
            {
                State = "ended",
                Reason = "recoil-weapon-changed",
                Weapon = "test-dmr",
                Action = "StandAndShoot",
                Shots = 1,
                ValidFor = 0,
            }
        );
        wire.Add(
            new()
            {
                State = "settled",
                Reason = "recoil-settled",
                Weapon = "test-dmr",
                Action = "StandAndShoot",
                Shots = 1,
                Steps = 1,
                Applied = true,
                ValidFor = 0,
                Coefficient = 1,
                Step = .02,
                BeforeHorizontal = .0009,
                BeforeVertical = -.0009,
            }
        );
        var opts = new JsonSerializerOptions
        {
            IncludeFields = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        };
        var dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../artifacts"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            Path.Combine(dir, "w09-wire-traces.json"),
            JsonSerializer.Serialize(wire, opts)
        );
        File.WriteAllText(
            Path.Combine(dir, "w09-recoil-recovery.json"),
            JsonSerializer.Serialize(
                new
                {
                    recoilRecovery = wire[1],
                    recoilRecoveryConfig = new RecoilRecoveryConfig { Enabled = true },
                },
                opts
            )
        );
        Console.WriteLine($"W09: {n} checks passed");
        return n;
    }
}
