using Comet.Core;

static class PeriodicWorkSlotTests
{
    public static int Run()
    {
        int n = 0;
        void Check(bool ok, string why)
        {
            n++;
            if (!ok)
                throw new Exception(why);
        }
        foreach (int fps in new[] { 20, 30, 60, 144 })
        {
            var slots = Enumerable.Range(0, 17).Select(i => new PeriodicWorkSlot(i)).ToArray();
            var last = Enumerable.Repeat(-1d, 17).ToArray();
            var count = new int[17];
            int max = 0;
            for (int frame = 0; frame < fps * 10; frame++)
            {
                double now = frame / (double)fps;
                int calls = 0;
                for (int i = 0; i < slots.Length; i++)
                    if (slots[i].Due(now))
                    {
                        calls++;
                        count[i]++;
                        Check(
                            last[i] < 0 || now - last[i] <= .25 + 1d / fps + 1e-8,
                            "maintenance deadline bounded to one frame"
                        );
                        last[i] = now;
                        Check(!slots[i].Due(now), "one run per bot/frame");
                    }
                max = Math.Max(max, calls);
            }
            Check(max <= Math.Ceiling(17 * 4d / fps) + 1, "maintenance spread instead of all bots");
            Check(count.All(c => c >= 39 && c <= 40), "4 checks per second retained");
            Check(slots.All(s => s.Due(100)), "long frame services all pending slots once");
            Check(slots.All(s => !s.Due(100)), "no catch-up burst");
        }
        var reset = new PeriodicWorkSlot(3);
        Check(!reset.Due(double.NaN) && !reset.Due(-1), "invalid clock");
        reset.Due(20);
        reset.Due(0);
        Check(reset.Due(.25), "clock reset bounded");
        return n;
    }
}
