using System;
using System.Linq;
using Comet.Core;

// Behavioral contract: zzap fbb86b83 (see SOURCE_BASELINE.md).
internal static class TacticalDevicesTests
{
    public static int Run()
    {
        int n = 0;
        void Check(bool ok, string why)
        {
            n++;
            if (!ok)
                throw new Exception("TacticalDevices: " + why);
        }

        var c = new TacticalDevicesConfig();
        c.Validate();
        Check(c.InitialPause == 60 && c.MaximumPause == 600, "source failed-ON backoff");
        var p = new TacticalDeviceAttempt();
        p.SetTarget(true, 2);
        Check(p.Observe(0, false, 2, c), "off lamp needs command");
        p.Request(0, c);
        Check(!p.Observe(.1, false, 2, c), "do not issue duplicate pending command");
        Check(p.Observe(1, false, 2, c), "retry first failed command");
        p.Request(1, c);
        Check(
            !p.Observe(2, false, 2, c) && p.RetryAt == 62,
            "failed accessory pauses independently"
        );
        Check(p.Observe(62, false, 2, c), "retry after source pause");
        p.Request(62, c);
        Check(!p.Observe(62.1, true, 2, c) && p.Confirmed, "actual state confirms success");
        p.Yield();
        Check(p.Observe(63, false, 2, c), "external change is not a permanent veto");
        p.SetTarget(false, 2);
        Check(!p.Observe(64, false, 2, c), "already off needs no command");

        return n;
    }
}
