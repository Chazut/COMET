using Comet.Core;

internal static class GasExposureTests
{
    public static int Run()
    {
        int n = 0;
        void C(bool v, string w)
        {
            n++;
            if (!v)
                throw new Exception(w);
        }
        var c = new GasExposureConfig();
        c.Validate();
        C(
            c.Enabled
                && c.RampIn == 4
                && c.Recovery == 8
                && c.Refresh == .25
                && c.AttemptDuration == 5
                && c.RetryDelay == 2,
            "G08 defaults"
        );
        double t = 0;
        for (int i = 0; i < 16; i++)
            t = GasExposureRules.Exposure(t, true, "None", .25, c);
        C(t == 1, "ramp 4s");
        for (int i = 0; i < 32; i++)
            t = GasExposureRules.Exposure(t, false, "None", .25, c);
        C(t == 0, "recovery 8s");
        C(GasExposureRules.Exposure(0, true, "Full", .5, c) == 0, "full protection");
        C(GasExposureRules.Exposure(0, true, "Respirator", .5, c) == .125, "respirator eyes");
        C(!GasExposureRules.Cough(true, "Respirator", 1), "respirator lungs");
        C(!GasExposureRules.Cough(false, "None", 1), "cough stops outside");
        C(GasExposureRules.Exposure(0, true, "None", 60, c) == .125, "no catchup");
        foreach (double v in new[] { double.NaN, double.PositiveInfinity, -1, 0d, 31 })
        {
            c.Recovery = v;
            try
            {
                c.Validate();
                throw new Exception("accepted invalid");
            }
            catch (ArgumentException)
            {
                n++;
            }
        }
        c.Recovery = 8;
        var old = Newtonsoft.Json.JsonConvert.DeserializeObject<CometConfig>("{}")!;
        old.Validate();
        C(old.GasExposure.Enabled, "old settings migration");
        old.GasExposure.RampIn = 6;
        var copy = Newtonsoft.Json.JsonConvert.DeserializeObject<CometConfig>(
            Newtonsoft.Json.JsonConvert.SerializeObject(old)
        )!;
        C(copy.GasExposure.RampIn == 6, "roundtrip");
        Console.WriteLine($"G08 policy: {n} checks passed.");
        return n;
    }
}
