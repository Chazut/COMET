using System.Text.Json;
using Comet.Core;

static class SuppressionDisciplineTests
{
    public static int Run()
    {
        int n = 0;
        void C(bool ok, string why)
        {
            n++;
            if (!ok)
                throw new Exception("W07 source: " + why);
        }
        var c = new SuppressionDisciplineConfig();
        c.Validate();
        C(
            c.ContactAge == 3
                && c.BurstRounds == 5
                && c.PauseSeconds == 3
                && c.ReservePercent == 60,
            "zzap suppression defaults"
        );
        var p = new SuppressionDisciplinePolicy();
        var f = new SuppressionDisciplineFrame
        {
            Now = 10,
            Loaded = 30,
            Capacity = 30,
            Target = "a",
            Weapon = "rifle",
            Identity = "a:1",
            Known = new(0, 0, 20),
        };
        var wire = new List<JsonElement>();
        void Save() =>
            wire.Add(
                JsonSerializer.SerializeToElement(
                    p.Trace,
                    new JsonSerializerOptions
                    {
                        IncludeFields = true,
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    }
                )
            );
        p.NativeCall(f, c, true, f.Known, .5);
        Save();
        for (int i = 1; i <= 5; i++)
        {
            f.Now += .1;
            f.Loaded--;
            p.NativeCall(f, c, true, f.Known, .5);
            C(p.NativePaused(f.Now) == (i == 5), "five consumed rounds before source burst pause");
        }
        Save();
        C(p.NativePaused(13.49) && !p.NativePaused(13.51), "three second pause");
        f.Now = 13.6;
        f.Target = "b";
        f.Identity = "b:1";
        f.Weapon = "other";
        f.Loaded = 30;
        p.NativeCall(f, c, true, f.Known, .5);
        C(
            p.Trace!.Episode == 1 && !p.NativePaused(f.Now),
            "native suppressor survives target/weapon change"
        );
        f.Now = 20;
        p.NativeCall(f, c, true, f.Known, .5);
        C(p.Active && p.MayFire, "no COMET six-second sequence expiry or consumed-cue requirement");
        f.Source = "shared";
        f.FreshGeometry = false;
        f.SupportUseful = false;
        f.Aligned = false;
        f.AllySafe = false;
        p.NativeCall(f, c, true, f.Known, .5);
        C(p.MayFire, "native eligibility and shooter own geometry; telemetry adds no veto");
        p.NativeCall(f, c, false, f.Known, .5);
        C(!p.MayFire && p.Trace.ActualShots == 0, "refused request is not reported as a shot");
        Save();
        foreach (double roll in new[] { 0d, .5d, 1d })
        {
            p = new();
            f.Now = 0;
            f.Loaded = 30;
            p.NativeCall(f, c, true, f.Known, roll);
            f.Now = .1;
            f.Loaded = 25;
            p.NativeCall(f, c, true, f.Known, roll);
            double end = .1 + 3 * (.7 + .6 * roll);
            C(
                p.NativePaused(end - .001) && !p.NativePaused(end + .001),
                "source pause randomization"
            );
        }
        p.End("raid-ended");
        C(!p.Active, "cleanup releases observation");
        File.WriteAllText("artifacts/w07-wire-traces.json", JsonSerializer.Serialize(wire));
        File.WriteAllText(
            "artifacts/w07-suppression-discipline.json",
            JsonSerializer.Serialize(
                new
                {
                    botInstance = "instance",
                    suppressionDiscipline = wire[1],
                    suppressionDisciplineConfig = c,
                }
            )
        );
        return n;
    }
}
