using System.Text.Json;
using Comet.Core;

static class HandsRecoveryTests
{
    public static int Run()
    {
        int n = 0;
        void Check(bool v, string why)
        {
            n++;
            if (!v)
                throw new Exception("W10 source: " + why);
        }
        var c = new HandsRecoveryConfig();
        var traces = new List<HandsRecoveryTrace>();
        HandsRecoveryTrace Copy(HandsRecoveryTrace t) =>
            JsonSerializer.Deserialize<HandsRecoveryTrace>(
                JsonSerializer.Serialize(t, new JsonSerializerOptions { IncludeFields = true }),
                new JsonSerializerOptions { IncludeFields = true }
            )!;
        var p = new HandsRecoveryPolicy();
        var f = new HandsRecoveryFrame
        {
            Now = 10,
            Kind = "reload",
            ReloadFlag = true,
            Held = "gun",
        };
        Check(p.Observe(f, c) == "", "initial observation");
        f.Now = 15.99;
        Check(p.Observe(f, c) == "", "source six seconds");
        f.Now = 16;
        Check(p.Observe(f, c) == "clear-reload", "source resets stale flag at six seconds");
        traces.Add(Copy(p.Trace!));
        p.Request(16);
        p.Result(true, 16, c);
        Check(
            p.Trace!.State == "recovered" && !p.BlocksSwitch && !p.Pending,
            "no extra execution lease"
        );
        Check(!p.Trace.ReloadFlag, "successful reset telemetry records cleared flag");
        traces.Add(Copy(p.Trace));
        f.ReloadFlag = false;
        f.Now = 17;
        p.Observe(f, c);
        Check(p.Trace.ValidFor == 0 && !p.Trace.ReloadFlag, "flag recovery observed");
        // Keep counting while the actual reload runs; never interrupt it. Once it
        // finishes, the source watchdog can immediately clear its stale flag.
        p = new();
        f = new()
        {
            Now = 20,
            ReloadFlag = true,
            NativeReload = true,
            Held = "gun",
        };
        p.Observe(f, c);
        for (int i = 1; i < 30; i++)
        {
            f.Now = 20 + i;
            Check(p.Observe(f, c) == "", "native reload owns operation");
        }
        f.NativeReload = false;
        Check(
            p.Observe(f, c) == "clear-reload",
            "native finish after six seconds needs no new stability period"
        );
        // COMET's former operation-generation, target, health and attempt gates
        // must not veto the source stale-flag reset.
        p = new();
        f = new()
        {
            Now = 0,
            ReloadFlag = true,
            Held = "gun",
        };
        p.Observe(f, c);
        for (int i = 1; i <= 20; i++)
        {
            f.Now = i * 6;
            f.Generation++;
            f.Held = "other" + i;
            f.Block = "medical/weapon-change";
            Check(p.Observe(f, c) == "clear-reload", "no extra recovery veto");
            p.Request(f.Now);
            p.Result(false, f.Now, c);
            Check(!p.BlocksSwitch, "no blocking native weapon switch");
        }
        foreach (string kind in new[] { "grenade", "switch", "reload-idle" })
        {
            p = new();
            f = new()
            {
                Kind = kind,
                Held = "gun",
                NativeReload = kind == "reload-idle",
                SelectorChanging = true,
                ReloadIdle = true,
                ReloadStranded = true,
            };
            for (int i = 0; i < 100; i++)
            {
                f.Now = i;
                Check(p.Observe(f, c) == "", "no additional repair outside zzap watchdog: " + kind);
            }
        }
        p.End("raid-ended");
        Check(!p.Pending && !p.BlocksSwitch, "cleanup owns no weapon-operation lock");
        f.Now = double.NaN;
        Check(p.Observe(f, c) == "", "bad clock safe");
        var options = new JsonSerializerOptions
        {
            IncludeFields = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        };
        var dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../artifacts"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            Path.Combine(dir, "w10-wire-traces.json"),
            JsonSerializer.Serialize(traces, options)
        );
        File.WriteAllText(
            Path.Combine(dir, "w10-hands-recovery.json"),
            JsonSerializer.Serialize(
                new { handsRecovery = traces[0], handsRecoveryConfig = c },
                options
            )
        );
        Console.WriteLine($"W10 source: {n} checks passed.");
        return n;
    }
}
