using Comet.Api;
using Comet.Client;
using Comet.Core;
using Newtonsoft.Json.Linq;

internal static class DeferredDiagnosticTests
{
    private sealed class EncoderProbe
    {
        public int Thread => Environment.CurrentManagedThreadId;
    }

    public static int Run()
    {
        int checks = 0;
        void Check(bool value, string reason)
        {
            if (!value) throw new Exception(reason);
            checks++;
        }

        var stamp = new ReplayStamp("raid", 1, 1.5f, "utc", 5, "id", "bot", "instance");
        var trace = new TacticalDevicesTrace { Accessories = [new() { Mode = 1 }] };
        var config = new TacticalDevicesConfig();
        var frozen = stamp.Capture(trace, config);
        trace.Accessories[0].Mode = 99;
        trace.Alternatives[0] = "changed";
        config.CommandInterval = 1;
        var data = JObject.Parse(Diagnostics.Serialize(frozen));
        Check((int)data["tacticalDevices"]!["accessories"]![0]!["mode"]! == 1
            && (string?)data["tacticalDevices"]!["alternatives"]![0] != "changed"
            && (double)data["tacticalDevicesConfig"]!["commandInterval"]! == .25,
            "L02 freezes accessory details, alternatives and the configuration");
        var next = new ReplayStamp("raid", 2, 2, "utc", 6, "id", "bot", "instance");
        Check((double)JObject.Parse(Diagnostics.Serialize(next.Capture(trace, config)))
            ["tacticalDevicesConfig"]!["commandInterval"]! == 1, "new revision captures changed configuration");

        var weapon = new WeaponRangeTrace { Weapons = [new() { Loaded = 7 }], Alternatives = ["a"] };
        var cover = new CoverTravelTrace { Alternatives = ["b"], Length = 9 };
        var gun = stamp.Capture(weapon, null);
        var route = stamp.Capture(cover, null);
        weapon.Weapons[0].Loaded = 0;
        weapon.Alternatives[0] = cover.Alternatives[0] = "changed";
        cover.Length = 0;
        var w = JObject.Parse(Diagnostics.Serialize(gun))["weaponRange"]!;
        var m = JObject.Parse(Diagnostics.Serialize(route))["coverTravel"]!;
        Check((int)w["weapons"]![0]!["loaded"]! == 7 && (string?)w["alternatives"]![0] == "a", "W05 freezes weapons and alternatives");
        Check((int)m["length"]! == 9 && (string?)m["alternatives"]![0] == "b", "M09 freezes movement and alternatives");

        var first = new List<string>();
        var second = new List<string>();
        var errors = new List<Exception>();
        var main = Environment.CurrentManagedThreadId;
        int wrongThread = 0;
        void Receive(string s)
        {
            if (Environment.CurrentManagedThreadId != main) wrongThread++;
            first.Add(s);
        }
        Decisions.Clear();
        Decisions.RaidClosed = false;
        Decisions.Published += Receive;
        try
        {
            var barrier = new TaskCompletionSource<string>();
            Decisions.Publish(new DiagnosticJson(barrier.Task), Decisions.CaptureSubscribers(), errors.Add);
            Decisions.Published += second.Add;
            Diagnostics.PublishSnapshot(null, frozen, errors.Add); // subscribers without a file
            Decisions.Publish("tail", errors.Add);
            Decisions.Pump(500);
            Check(first.Count == 0 && second.Count == 0, "a pending head holds both streams in order without blocking a frame");
            barrier.SetResult("head");
            Decisions.FlushPending();
            Check(first.SequenceEqual(new[] { "head", Diagnostics.Serialize(frozen), "tail" })
                && second.SequenceEqual(first.Skip(1)) && wrongThread == 0 && errors.Count == 0,
                "flush preserves order, event-time subscriber membership and callback thread");
            Diagnostics.PublishSnapshot(null, new EncoderProbe(), errors.Add);
            Decisions.FlushPending();
            Check((int)JObject.Parse(first.Last())["thread"]! != main && wrongThread == 0,
                "even an immediate final flush serializes in the background and delivers on the caller");
            Decisions.Publish(new DiagnosticJson(Task.FromException<string>(new Exception("format"))),
                Decisions.CaptureSubscribers(), errors.Add);
            Decisions.Publish("survived", errors.Add);
            Decisions.FlushPending();
            Check(errors.Count == 1 && first.Last() == "survived" && second.Last() == "survived",
                "one failed serialization never disables subscribers or strands the queue");
        }
        finally
        {
            Decisions.Published -= Receive;
            Decisions.Published -= second.Add;
            Decisions.Clear();
        }
        return checks;
    }
}
