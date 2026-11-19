using Comet.Core;
using Comet.Server;

internal static class DoorSpatialTests
{
    internal static int Run()
    {
        int n = 0;
        void Check(bool ok, string why)
        {
            n++;
            if (!ok)
                throw new Exception("C03: " + why);
        }
        var config = new DoorSpatialConfig();
        config.Validate();
        Check(config.Enabled && config.Clearance == .55, "approved defaults");
        foreach (double v in new[] { double.NaN, double.PositiveInfinity, .249, 1.251 })
        {
            bool rejected = false;
            try
            {
                new DoorSpatialConfig { Clearance = v }.Validate();
            }
            catch (ArgumentException)
            {
                rejected = true;
            }
            Check(rejected, "invalid margin");
        }
        var old = Newtonsoft.Json.JsonConvert.DeserializeObject<CometConfig>(
            "{\"doorSpatial\":null}"
        )!;
        old.Validate();
        Check(old.DoorSpatial.Clearance == .55, "migration");
        string file = Path.Combine(Path.GetTempPath(), "comet-c03-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ConfigStore(file);
            var c = store.Read();
            c.DoorSpatial.Clearance = .7;
            c.DoorSpatial.Enabled = false;
            store.Save(c, c.Revision);
            var read = store.Read();
            Check(
                read.DoorSpatial.Clearance == .7 && !read.DoorSpatial.Enabled,
                "server save/load"
            );
        }
        finally
        {
            File.Delete(file);
            File.Delete(file + ".bak");
        }
        for (int angle = 0; angle < 360; angle += 5)
        {
            double rad = angle * Math.PI / 180;
            var axis = new DoorPoint(Math.Cos(rad), 0, Math.Sin(rad));
            var a = new DoorPoint(10, 3, 20);
            var b = a + axis * 1.2;
            var open = a + new DoorPoint(-axis.Z, 0, axis.X) * 1.2;
            Check(
                DoorSpatial.Decode("a", "Open", "normal", a, b, a, open, 2, out var normal),
                "native rotation"
            );
            Check(
                DoorSpatial.Decode(
                    "a",
                    "Open",
                    "DoorLink_Custom_a",
                    a,
                    open,
                    a,
                    b,
                    2,
                    out var custom
                ),
                "waypoints rotation"
            );
            Check(
                (normal.Center - custom.Center).Length < 1e-8
                    && (normal.Axis - custom.Axis).Length < 1e-8,
                "fixed frame parity"
            );
            Check((normal.OpenB - custom.OpenB).Length < 1e-8, "legacy segment parity");
            Check(
                Math.Abs(normal.Axis.Dot(normal.Normal)) < 1e-8
                    && Math.Abs(normal.HalfWidth - .6) < 1e-8,
                "orthonormal frame"
            );
            Check(
                DoorSpatial.Candidate(normal, new(10, 2.5, 20)) == "",
                "small threshold/stair offset"
            );
            Check(
                DoorSpatial.Candidate(normal, new(10, 5, 20)) == "geometry-wrong-floor",
                "different floor"
            );
        }
        Check(
            !DoorSpatial.Decode(
                "a",
                "Open",
                "",
                new(0, 0, 0),
                new(0, 0, 0),
                new(),
                new(1, 0, 0),
                0,
                out _
            ),
            "zero width"
        );
        Check(
            !DoorSpatial.Decode(
                "a",
                "Open",
                "",
                new(0, 0, 0),
                new(5, 0, 0),
                new(),
                new(1, 0, 0),
                0,
                out _
            ),
            "oversize frame"
        );
        Check(
            !DoorSpatial.Decode(
                "a",
                "Open",
                "",
                new(0, 0, 0),
                new(1, 1, 0),
                new(),
                new(1, 0, 0),
                0,
                out _
            ),
            "slanted cross-floor frame"
        );
        Check(
            !DoorSpatial.Decode(
                "a",
                "Open",
                "",
                new(0, 0, 0),
                new(1, 0, 0),
                new(),
                new(1, 0, 0),
                double.NaN,
                out _
            ),
            "invalid bottom"
        );
        Check(
            !DoorSpatial.Decode(
                "a",
                "Open",
                "",
                new(0, 0, 0),
                new(1, 0, 0),
                new(double.NaN, 0, 0),
                new(1, 0, 0),
                0,
                out _
            ),
            "invalid leaf endpoint"
        );
        Check(
            DoorSpatial.SegmentDistance(new(.5, 100, .55), new(), new(1, 0, 0)) == .55,
            "horizontal legacy distance"
        );
        Console.WriteLine($"C03 geometry policy: {n} checks passed");
        return n;
    }
}
