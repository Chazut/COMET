using Comet.Core;
using Comet.Server;

internal static class DoorClosingTests
{
    internal static int Run()
    {
        int n = 0;
        void Check(bool v, string why)
        {
            n++;
            if (!v)
                throw new Exception("C05 " + why);
        }
        var c = new DoorClosingConfig();
        c.Validate();
        Check(
            !c.Enabled && c.OpenGrace == 6 && c.OccupiedRadius == 1.5 && c.FailedRetry == 10,
            "defaults"
        );
        foreach (
            var edit in new Action<DoorClosingConfig>[]
            {
                x => x.OpenGrace = double.NaN,
                x => x.OpenGrace = 0,
                x => x.OccupiedRadius = 0,
                x => x.FailedRetry = double.PositiveInfinity,
                x => x.FailedRetry = 61,
            }
        )
        {
            var bad = new DoorClosingConfig();
            edit(bad);
            bool rejected = false;
            try
            {
                bad.Validate();
            }
            catch (ArgumentException)
            {
                rejected = true;
            }
            Check(rejected, "invalid config");
        }
        var old = Newtonsoft.Json.JsonConvert.DeserializeObject<CometConfig>(
            "{\"doorClosing\":null}"
        )!;
        old.Validate();
        Check(!old.DoorClosing.Enabled, "missing/null config stays OFF");
        var a = new DoorPoint(0, 0, 0);
        var b = new DoorPoint(1, 0, 0);
        Check(DoorClosing.Blocks(new(.5, 0, -1), new(.5, 0, 1), a, b), "crossing shut leaf");
        Check(DoorClosing.Blocks(new(-1, 0, 0), new(2, 0, 0), a, b), "collinear overlap");
        Check(!DoorClosing.Blocks(new(-2, 0, 0), new(-1, 0, 0), a, b), "collinear separated");
        Check(!DoorClosing.Blocks(new(-1, 0, -.8), new(2, 0, -.8), a, b), "parallel route");
        Check(DoorClosing.Blocks(new(0, 0, -.2), new(0, 0, -.2), a, b), "degenerate near hinge");
        Check(DoorClosing.Blocks(new(double.NaN, 0, 0), b, a, b), "uncertain route rejected");
        string path = Path.Combine(Path.GetTempPath(), "comet-c05-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ConfigStore(path);
            var cfg = store.Read();
            cfg.DoorClosing.Enabled = true;
            cfg.DoorClosing.OpenGrace = 8;
            cfg.DoorClosing.OccupiedRadius = 2;
            cfg.DoorClosing.FailedRetry = 15;
            store.Save(cfg, cfg.Revision);
            var saved = new ConfigStore(path).Read();
            Check(
                saved.DoorClosing.Enabled
                    && saved.DoorClosing.OpenGrace == 8
                    && saved.DoorClosing.OccupiedRadius == 2
                    && saved.DoorClosing.FailedRetry == 15,
                "server save/reload all settings"
            );
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".bak");
        }
        Console.WriteLine($"C05 geometry/config: {n} checks passed");
        return n;
    }
}
