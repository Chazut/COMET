using System.Text.Json;
using Comet.Core;

internal static class SecondaryEntranceTests
{
    public static int Run()
    {
        int n = 0;
        void C(bool v, string why)
        {
            n++;
            if (!v)
                throw new Exception("S02: " + why);
        }
        var c = new SecondaryEntranceConfig();
        c.Validate();
        C(
            c.Enabled
                && c.AllowEntry
                && c.WatchMin == 10
                && c.WatchMax == 15
                && c.Extension == 15
                && c.StandOff == 2.5
                && c.MaxPath == 35
                && c.ContactAge == 15
                && c.MinimumAmmo == 50
                && c.Retry == 30,
            "defaults"
        );
        C(
            c.Chance("GigaChad") == 100
                && c.Chance("Chad") == 100
                && c.Chance("Wreckless") == 100
                && c.Chance("Normal") == 60
                && c.Chance("Rat") == 0
                && c.Chance("SnappingTurtle") == 0
                && c.Chance("Coward") == 0
                && c.Chance("Timmy") == 0
                && c.Chance("unknown") == 0,
            "personalities"
        );
        foreach (
            var prop in typeof(SecondaryEntranceConfig)
                .GetProperties()
                .Where(x => x.PropertyType == typeof(double))
        )
        foreach (var bad in new[] { double.NaN, double.PositiveInfinity, -1, 1000 })
        {
            var cfg = new SecondaryEntranceConfig();
            prop.SetValue(cfg, bad);
            bool fail = false;
            try
            {
                cfg.Validate();
            }
            catch (ArgumentException)
            {
                fail = true;
            }
            C(fail, "invalid " + prop.Name);
        }
        C(
            JsonSerializer.Deserialize<CometConfig>("{}")!.SecondaryEntrance.WatchMax == 15,
            "legacy migration"
        );
        var conf = new CometConfig();
        conf.SecondaryEntrance.Chad = 42;
        C(
            JsonSerializer
                .Deserialize<CometConfig>(JsonSerializer.Serialize(conf))!
                .SecondaryEntrance.Chad == 42,
            "roundtrip"
        );
        {
            var p = new SecondaryEntrance();
            C(p.Begin("enemy", 10, 9, c), "begin");
            p.Arrived(12);
            C(
                p.Trace!.WatchDuration == 15
                    && p.Decide(13, false, "stale", 100, "Chad", 0, c) == "wait"
                    && p.Trace.WatchRemaining == 14
                    && p.Decide(27, false, "ready", 100, "Chad", 0, c) == "wait"
                    && !p.Trace.Rolled,
                "contact evidence does not end the initial watch, including at exactly its deadline"
            );
            C(
                p.Decide(27.01, true, "ready", 100, "Chad", 0, c) == "ready" && p.Trace.Rolled,
                "first update past the deadline uses the current evidence"
            );
            p.End(28);
            C(
                !p.CanTry("enemy", 57, 40, c)
                    && p.CanTry("enemy", 100, 9, c)
                    && p.CanTry("enemy", 58, 10, c),
                "cooldown without new-evidence veto"
            );
        }
        foreach (double oldRoll in new[] { 0d, .5d, 1d })
        {
            var p = new SecondaryEntrance();
            var preset = JsonSerializer.Deserialize<SecondaryEntranceConfig>("""{"WatchMin":4,"WatchMax":18}""")!;
            preset.Validate();
            C(p.Begin("e", 1, 1, oldRoll, preset) && p.Trace!.WatchDuration == 18,
                "existing presets and callers use their configured duration for every watch");
        }
        foreach (
            var why in new[]
            {
                "reloading",
                "healing or occupied",
                "contact lost",
                "left primary access",
                "under pressure",
            }
        )
        {
            var p = new SecondaryEntrance();
            p.Begin("e", 1, 1, 1, c);
            p.Arrived(2);
            C(
                p.Decide(17.01, true, why, 100, "GigaChad", 0, c) == "ready",
                "busy partner does not veto entry"
            );
        }
        {
            var p = new SecondaryEntrance();
            p.Begin("e", 1, 1, 0, c);
            p.Arrived(2);
            C(p.Decide(18, true, "ready", 40, "Chad", 0, c) == "wait" && p.Trace!.Extended,
                "low ammunition starts one extra watch at the first decision");
            C(
                p.Decide(19, false, "stale", 50, "Chad", 1, c) == "wait"
                    && p.Decide(33, false, "ready", 50, "Chad", 1, c) == "wait"
                    && p.Decide(33.01, true, "ready", 50, "Chad", 1, c) == "ready",
                "only reconsider at the end of the extra watch, including a late first update"
            );
        }
        foreach (
            string personality in new[]
            {
                "GigaChad",
                "Chad",
                "Wreckless",
                "Normal",
                "Rat",
                "SnappingTurtle",
                "Coward",
                "Timmy",
            }
        )
        foreach (double roll in new[] { 0d, .3999, .4, .7499, .75, .8499, .85, 1 })
        {
            var p = new SecondaryEntrance();
            p.Begin("e", 1, 1, 0, c);
            p.Arrived(2);
            C(
                (p.Decide(17.01, true, "ready", 50, personality, roll, c) == "ready")
                    == (roll * 100 < c.Chance(personality)),
                "exact chance boundary " + personality
            );
        }
        foreach (string mode in new[] { "stale", "clock", "off", "ammo", "toggle" })
        {
            var p = new SecondaryEntrance();
            var cfg = new SecondaryEntranceConfig();
            p.Begin("e", 1, 1, 0, cfg);
            p.Arrived(2);
            if (mode == "off")
                cfg.Enabled = false;
            if (mode == "toggle")
                cfg.AllowEntry = false;
            var result = p.Decide(
                mode == "clock" ? 1 : 17.01,
                mode != "stale",
                "ready",
                mode == "ammo" ? 49 : 50,
                "Chad",
                0,
                cfg
            );
            C(result != "ready", "unsafe " + mode);
        }
        {
            var p = new SecondaryEntrance();
            for (int i = 0; i < 32; i++)
                C(p.Begin("e" + i, 1, 1, 0, c), "bounded history slot");
            C(p.CanTry("overflow", 2, 2, c), "history does not cap eligible enemies");
        }
        {
            var p = new SecondaryEntrance();
            p.Begin("e", 10, 9, 0, c);
            p.End(23, 22);
            C(
                p.CanTry("e", 53, 22, c) && p.CanTry("e", 53, 23, c),
                "retry requires newer evidence than latest information used during the attempt"
            );
        }
        Console.WriteLine($"{n} S02 policy/config checks passed");
        return n;
    }
}
