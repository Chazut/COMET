using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using Comet.Core;
using Comet.Server;

internal static class AdsTravelTests
{
    public static int Run()
    {
        int n = 0;
        void Check(bool v, string why)
        {
            if (!v)
                throw new Exception("M10: " + why);
            n++;
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
            throw new Exception("M10 invalid setting accepted");
        }
        var c = new AdsTravelConfig();
        c.Validate();
        AdsTravelFrame F(double now = 10) =>
            new()
            {
                Now = now,
                SinceShot = 1,
                Speed = 1,
                Action = "SeekCover",
                Target = "enemy",
                NativeADS = true,
                ObservedADS = true,
                Moving = true,
                Ready = true,
            };
        Check(
            c.Enabled
                && c.Cover
                && c.Retreat
                && c.Flee
                && c.Push
                && c.SinceShot == .3
                && c.Stability == .4
                && c.Refresh == .1,
            "defaults"
        );
        foreach (string action in new[] { "SeekCover", "Retreat", "RunAway", "RushEnemy" })
        {
            var p = new AdsTravelPolicy();
            var f = F();
            f.Action = action;
            Check(p.Tick(f, c), "eligible native action " + action);
            Check(
                !p.Trace!.RequestedOff && p.Trace.ObservedADS && p.Trace.ValidFor == 0,
                "decision is not actual ADS release"
            );
            p.Observe(true, true, 1, c);
            Check(
                p.Trace.RequestedOff && p.Trace.ObservedADS,
                "request does not invent observed off"
            );
            p.Observe(false, true, 1, c);
            Check(
                !p.Trace.ObservedADS && p.Trace.NativeADS && p.Trace.ValidFor == .2,
                "observed off separate from native wish"
            );
        }
        foreach (
            var mutate in new Action<AdsTravelFrame>[]
            {
                f => f.Block = "ghost",
                f => f.Block = "non-pmc",
                f => f.Block = "remote",
                f => f.Block = "medical",
                f => f.Block = "door",
                f => f.Block = "prefire",
                f => f.Firing = true,
                f => f.Preparing = true,
                f => f.SinceShot = .299,
                f => f.Ready = false,
                f => f.Moving = false,
                f => f.Speed = .14,
                f => f.InCover = true,
                f => f.Sprinting = true,
                f => f.Action = "StandAndShoot",
                f => f.Action = "Search",
                f => f.NativeADS = false,
                f => f.Now = double.NaN,
                f => f.SinceShot = -1,
                f => f.Speed = double.PositiveInfinity,
            }
        )
        {
            var p = new AdsTravelPolicy();
            p.Tick(F(), c);
            var f = F(10.05);
            mutate(f);
            Check(!p.Tick(f, c) && !p.Active, "priority beats stability immediately");
            Check(!p.Trace!.RequestedOff && p.Trace.ValidFor == 0, "rejected context expires");
            Check(
                double.IsFinite(p.Trace.RetryIn)
                    && double.IsFinite(p.Trace.Speed)
                    && double.IsFinite(p.Trace.SinceShot),
                "finite rejection telemetry"
            );
        }
        var policy = new AdsTravelPolicy();
        policy.Tick(F(), c);
        var fire = F(10.05);
        fire.Preparing = true;
        policy.Tick(fire, c);
        Check(
            !policy.Tick(F(10.2), c) && policy.Trace!.Reason == "ads-stability-wait",
            "avoid reapplying between bursts"
        );
        Check(policy.Tick(F(10.46), c), "resume after stable safe window");
        var change = F(10.5);
        change.Target = "new";
        Check(!policy.Tick(change, c), "target change releases old context");
        Check(!policy.Tick(F(10.6), c), "changing context does not bypass cooldown");
        policy = new();
        policy.Tick(F(), c);
        policy.End("ads-action-ended", 10.1, true, true);
        Check(
            policy.Trace!.State == "released"
                && policy.Trace.ObservedADS
                && policy.Trace.NativeADS
                && !policy.Trace.RequestedOff
                && policy.Trace.ValidFor == 0,
            "fresh SAIN wish is restored, not old snapshot"
        );
        Check(!policy.Tick(F(10.2), c), "external release also applies stability");
        policy = new();
        var boundary = F();
        boundary.SinceShot = .3;
        Check(policy.Tick(boundary, c), "shot delay boundary");
        policy.End("ads-stopped", 10.1, false, false);
        Check(
            !policy.Trace!.NativeADS && !policy.Trace.ObservedADS,
            "native may choose to remain off after stop"
        );
        var json = Newtonsoft.Json.JsonConvert.SerializeObject(
            policy.Trace,
            Comet.Client.Diagnostics.Json
        );
        using (var payload = JsonDocument.Parse(json))
        {
            Check(
                payload.RootElement.GetProperty("nativeADS").ValueKind == JsonValueKind.False,
                "wire nativeADS spelling"
            );
            Check(
                payload.RootElement.GetProperty("observedADS").ValueKind == JsonValueKind.False,
                "wire observedADS spelling"
            );
        }
        foreach (
            var prop in typeof(AdsTravelConfig)
                .GetProperties()
                .Where(p => p.PropertyType == typeof(bool))
        )
        {
            var cfg = new AdsTravelConfig();
            prop.SetValue(cfg, false);
            var f = F();
            f.Action = prop.Name switch
            {
                "Retreat" => "Retreat",
                "Flee" => "RunAway",
                "Push" => "RushEnemy",
                _ => "SeekCover",
            };
            Check(!new AdsTravelPolicy().Tick(f, cfg), "per-action toggle " + prop.Name);
        }
        foreach (
            var prop in typeof(AdsTravelConfig)
                .GetProperties()
                .Where(p => p.PropertyType == typeof(double))
        )
        foreach (var bad in new[] { double.NaN, double.PositiveInfinity, -1, 999 })
        {
            var cfg = new AdsTravelConfig();
            prop.SetValue(cfg, bad);
            Invalid(cfg.Validate);
        }
        Invalid(() => new CometConfig { AdsTravel = null! }.Validate());
        Check(
            JsonSerializer.Deserialize<CometConfig>("{}")!.AdsTravel.SinceShot == .3,
            "old config defaults"
        );
        var dir = Path.Combine(Path.GetTempPath(), "comet-m10-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "config.json");
        try
        {
            var store = new ConfigStore(file);
            var saved = store.Read();
            saved.AdsTravel.SinceShot = .5;
            saved.AdsTravel.Push = false;
            store.Save(saved, saved.Revision);
            var read = new ConfigStore(file).Read();
            Check(read.AdsTravel.SinceShot == .5 && !read.AdsTravel.Push, "server persistence");
            var before = File.ReadAllText(file);
            read.AdsTravel.Refresh = 0;
            Invalid(() => store.Save(read, read.Revision));
            Check(File.ReadAllText(file) == before, "invalid save atomic");
        }
        finally
        {
            foreach (var path in new[] { file, file + ".bak" })
                if (File.Exists(path))
                    File.Delete(path);
            File.Delete(file + ConfigStore.ReportDefaultsReceipt);
            Directory.Delete(dir);
        }
        using (var pe = new PEReader(File.OpenRead("references/sain-4.5.1/SAIN.dll")))
        {
            var md = pe.GetMetadataReader();
            var types = md.TypeDefinitions.Select(h => md.GetTypeDefinition(h)).ToArray();
            foreach (
                var pair in new[]
                {
                    ("AimDownSightsController", "ShallAimDownSights"),
                    ("AimDownSightsController", "ManualUpdate"),
                    ("AimDownSightsController", "SetADS"),
                    ("SAINShootData", "AimAndShootAtEnemy"),
                    ("ManualShootClass", "TryShoot"),
                    ("PlayerComponent", "OnMakingShot"),
                }
            )
                Check(
                    types
                        .Single(t => md.GetString(t.Name) == pair.Item1)
                        .GetMethods()
                        .Any(h => md.GetString(md.GetMethodDefinition(h).Name) == pair.Item2),
                    "official hook " + pair
                );
        }
        var rng = new Random(1010);
        policy = new();
        for (int i = 0; i < 2000; i++)
        {
            var f = F(20 + i * .05);
            f.SinceShot = rng.NextDouble();
            f.Speed = rng.NextDouble() * 3;
            f.Firing = i % 7 == 0;
            f.Preparing = i % 11 == 0;
            f.Moving = i % 13 != 0;
            f.InCover = i % 17 == 0;
            if (i % 23 == 0)
                f.Block = "ghost";
            bool active = policy.Tick(f, c);
            Check(!active || AdsTravelPolicy.Gate(f, c) == "", "mixed stream safety");
            policy.Observe(i % 3 == 0, active, f.Speed, c);
            Check(policy.Trace!.RequestedOff == active, "request ownership");
            Check(policy.Trace.ObservedADS == (i % 3 == 0), "observations independent");
        }
        Console.WriteLine($"M10: {n} assertions passed.");
        return n;
    }
}
