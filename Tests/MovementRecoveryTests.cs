using Comet.Core;
using Comet.Server;

internal static class MovementRecoveryTests
{
    internal static int Run()
    {
        int n = 0;
        void Check(bool v, string why)
        {
            n++;
            if (!v)
                throw new Exception("C06 " + why);
        }
        var c = new MovementRecoveryConfig();
        c.Validate();
        Check(
            c.Enabled
                && !c.Teleport
                && c.SampleInterval == .5
                && c.ProgressDistance == .6
                && c.EarlyJumpAfter == 1
                && c.RepathAfter == 3
                && c.JumpAfter == 5
                && c.TeleportAfter == 8
                && c.StopAfter == 14
                && c.TeleportDistance == 2
                && c.HumanDistance == 50,
            "defaults"
        );
        foreach (
            var edit in new Action<MovementRecoveryConfig>[]
            {
                x => x.SampleInterval = 0,
                x => x.ProgressDistance = double.NaN,
                x => x.EarlyJumpAfter = 4,
                x => x.RepathAfter = 5,
                x => x.JumpAfter = double.PositiveInfinity,
                x => x.TeleportAfter = 16,
                x => x.StopAfter = 8,
                x => x.TeleportDistance = 2.01,
                x => x.HumanDistance = 49,
            }
        )
        {
            var bad = new MovementRecoveryConfig();
            edit(bad);
            bool failed = false;
            try
            {
                bad.Validate();
            }
            catch (ArgumentException)
            {
                failed = true;
            }
            Check(failed, "invalid config");
        }
        foreach (string json in new[] { "{}", "{\"movementRecovery\":null}" })
        {
            var old = Newtonsoft.Json.JsonConvert.DeserializeObject<CometConfig>(json)!;
            old.Validate();
            Check(old.MovementRecovery.Enabled && !old.MovementRecovery.Teleport, "safe migration");
        }
        string path = Path.Combine(Path.GetTempPath(), "comet-c06-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ConfigStore(path);
            var cfg = store.Read();
            cfg.MovementRecovery = new()
            {
                Enabled = false,
                Teleport = true,
                SampleInterval = 1,
                ProgressDistance = .8,
                EarlyJumpAfter = 2,
                RepathAfter = 4,
                JumpAfter = 6,
                TeleportAfter = 10,
                StopAfter = 20,
                TeleportDistance = 1,
                HumanDistance = 70,
            };
            store.Save(cfg, cfg.Revision);
            var saved = new ConfigStore(path).Read();
            Check(
                Newtonsoft.Json.JsonConvert.SerializeObject(saved.MovementRecovery)
                    == Newtonsoft.Json.JsonConvert.SerializeObject(cfg.MovementRecovery),
                "all server settings persist"
            );
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".bak");
        }
        var p = new MovementRecoveryPolicy();
        var f = new MovementRecoveryFrame
        {
            Intent = true,
            Owner = "SAIN",
            Action = "Search",
        };
        var commands = new List<string>();
        for (int i = 0; i <= 60; i++)
        {
            f.Now = i * .5;
            f.Route = "route-" + i;
            f.Owner = i % 2 == 0 ? "SAIN" : "external-mover";
            f.Action = i % 3 == 0 ? "Search" : "Push";
            var a = p.Observe(f, c);
            if (a.Length > 0)
            {
                commands.Add(a);
                p.Result(true, "accepted");
            }
            Check(p.Trace.State != "recovered", "accepted action is not success");
        }
        Check(
            string.Join(",", commands) == "early-vault,repath,vault-or-jump,stop",
            "order, optional hop OFF, one request per incident despite path churn"
        );
        Check(!p.Handling && p.Trace.State == "exhausted", "bounded A12 ownership ends");
        f.X = .3;
        f.Now += .5;
        p.Observe(f, c);
        Check(p.Trace.State == "exhausted", "small jitter cannot reset exhausted incident");
        f.Y = 2;
        f.Now += .5;
        p.Observe(f, c);
        Check(p.Trace.State == "exhausted", "jump height alone is not real route progress");
        f.X = .7;
        f.Now += .5;
        p.Observe(f, c);
        Check(
            p.Trace.State == "recovered" && p.Trace.ValidFor == 0,
            "real progress confirms recovery"
        );
        p = new();
        f = new() { Intent = true };
        for (int i = 0; i <= 4; i++)
        {
            f.Now = i * .5;
            p.Observe(f, c);
        }
        double before = p.Trace.StalledSeconds;
        f.Pause = "medical";
        for (int i = 5; i <= 44; i++)
        {
            f.Now = i * .5;
            Check(
                p.Observe(f, c) == "" && !p.Handling && p.Trace.ValidFor == 0,
                "medical pauses are protected"
            );
        }
        f.Pause = "";
        f.Now += .5;
        p.Observe(f, c);
        Check(p.Trace.StalledSeconds == before, "pause does not count or erase evidence");
        f.Now += 100;
        p.Observe(f, c);
        Check(p.Trace.StalledSeconds == before, "missing samples do not manufacture elapsed stall");
        f.Now = -1;
        p.Observe(f, c);
        Check(p.Trace.StalledSeconds == 0, "clock rollback resets");
        f.Scoped = false;
        p.Observe(f, c);
        Check(
            !p.Handling && p.Trace.State == "ended",
            "authority, ghost, faction exclusion resets"
        );
        c.Teleport = true;
        p = new();
        f = new() { Intent = true };
        bool relocated = false;
        for (int i = 0; i <= 16; i++)
        {
            f.Now = i * .5;
            var a = p.Observe(f, c);
            if (a == "teleport")
            {
                p.Result(true, "relocated", true);
                relocated = true;
            }
        }
        Check(relocated, "optional teleport eligible at 8s");
        f.X = 2;
        f.Now += .5;
        p.Observe(f, c);
        Check(
            p.Trace.State != "recovered" && p.Trace.StalledSeconds >= 8,
            "teleport never masquerades as natural recovery"
        );
        f.X = 2.7;
        f.Now += .5;
        p.Observe(f, c);
        Check(p.Trace.State == "recovered", "movement following teleport confirms");
        foreach (var enabled in new[] { true, false })
            for (int seed = 0; seed < 20; seed++)
            {
                c.Teleport = enabled;
                p = new();
                f = new() { Intent = true };
                var count = new Dictionary<string, int>();
                var random = new Random(seed);
                for (int i = 0; i < 400; i++)
                {
                    f.Now += .5;
                    f.X = random.NextDouble() * .1;
                    f.Z = random.NextDouble() * .1;
                    f.Pause = i % 13 == 0 ? "wait" : "";
                    var a = p.Observe(f, c);
                    if (a.Length > 0)
                    {
                        count[a] = count.GetValueOrDefault(a) + 1;
                        p.Result(false, "blocked");
                    }
                    Check(
                        count.Values.All(x => x <= 1),
                        "refused stages not spammed by jitter or pauses"
                    );
                }
                Check(count.Count == (enabled ? 5 : 4), "complete bounded ladder");
            }
        Console.WriteLine($"C06 policy/config: {n} checks passed");
        return n;
    }
}
