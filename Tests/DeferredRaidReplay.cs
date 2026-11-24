using System.Diagnostics;
using Comet.Api;
using Comet.Client;
using Comet.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

// Opt-in offline replay. Archive parsing, disk verification and comparisons
// stay outside the producer benchmark. Never open an archive for writing.
internal static class DeferredRaidReplay
{
    private sealed record Event(string Expected, bool Published, Func<object>? Capture, object? Reference);

    internal static void Run(string[] files)
    {
        var records = new List<Event>();
        var configs = new Dictionary<(string, long, string), object>();
        foreach (string file in files)
        foreach (string line in File.ReadLines(file))
        {
            var e = Parse(line);
            string family = (string?)e["feature"] ?? "";
            string stage = (string?)e["stage"] ?? "";
            bool published = (long?)e["sequence"] > 0 && stage != "eligibility"
                && !(family == "S07" && stage == "diagnostic")
                && !(family == "T01" && e["steeringFire"] != null);
            if (stage != "evaluated" || family is not ("L02" or "W05" or "M09"))
            {
                records.Add(new(line, published, null, null));
                continue;
            }
            long revision = (long)e["configRevision"]!;
            var stamp = new ReplayStamp((string)e["session"]!, (long)e["sequence"]!, (float)e["realtime"]!,
                (string)e["utc"]!, revision, (string)e["profileId"]!, (string)e["name"]!, (string)e["botInstance"]!);
            T? Config<T>(string field) where T : class
            {
                if (e[field] is not JObject section) return null;
                var key = (family, revision, section.ToString(Formatting.None));
                if (!configs.TryGetValue(key, out var result))
                    configs.Add(key, result = section.ToObject<T>()!);
                return (T)result;
            }
            Func<object> capture;
            switch (family)
            {
                case "L02":
                    var devices = e["tacticalDevices"]!.ToObject<TacticalDevicesTrace>()!;
                    var dc = Config<TacticalDevicesConfig>("tacticalDevicesConfig");
                    capture = () => stamp.Capture(devices, dc);
                    break;
                case "W05":
                    var weapons = e["weaponRange"]!.ToObject<WeaponRangeTrace>()!;
                    var wc = Config<WeaponRangeConfig>("weaponRangeConfig");
                    capture = () => stamp.Capture(weapons, wc);
                    break;
                default:
                    var cover = e["coverTravel"]!.ToObject<CoverTravelTrace>()!;
                    var cc = Config<CoverTravelConfig>("coverTravelConfig");
                    capture = () => stamp.Capture(cover, cc);
                    break;
            }
            var reference = capture();
            string normalized = Diagnostics.Serialize(reference);
            var actual = Parse(normalized);
            // Unity/Mono and desktop .NET print Single with different decimal
            // lengths. Require the exact same Single, never relax other fields.
            if ((float)actual["realtime"]! != (float)e["realtime"]!)
                throw new Exception("Timestamp mismatch: " + e["sequence"]);
            actual["realtime"] = e["realtime"]!.DeepClone();
            if (!JToken.DeepEquals(actual, e))
                throw new Exception($"Schema or value mismatch at {family}/{e["sequence"]}: " +
                    string.Join(",", e.Properties().Where(p => !JToken.DeepEquals(p.Value, actual[p.Name])).Select(p => p.Name)));
            records.Add(new(normalized, published, capture, reference));
        }
        VerifyDelivery(records);
        var changed = records.Where(e => e.Capture != null).ToArray();
        Measure(changed.Take(512).ToArray(), false);
        Measure(changed.Take(512).ToArray(), true);
        for (int pass = 0; pass < 3; pass++)
        foreach (bool deferred in pass % 2 == 0 ? new[] { false, true } : new[] { true, false })
        {
            var (producer, elapsed, bytes) = Measure(changed, deferred);
            Console.WriteLine($"{(deferred ? "Deferred" : "Synchronous")}: producer {producer:F2} ms, " +
                $"producer allocations {bytes} B, elapsed including final flush {elapsed:F2} ms, {changed.Length} events.");
        }
    }

    private static JObject Parse(string line)
    {
        using var reader = new JsonTextReader(new StringReader(line)) { DateParseHandling = DateParseHandling.None };
        return JObject.Load(reader);
    }

    private static void VerifyDelivery(List<Event> records)
    {
        int main = Environment.CurrentManagedThreadId;
        var expected = records.Where(e => e.Published).ToArray();
        int rr = 0, satellite = 0;
        var errors = new List<string>();
        void Receive(string json, ref int position)
        {
            if (Environment.CurrentManagedThreadId != main || expected[position++].Expected != json)
                throw new Exception($"Published event or callback thread changed at {position}");
        }
        void RR(string json) => Receive(json, ref rr);
        void Satellite(string json) => Receive(json, ref satellite);
        string dir = Directory.CreateTempSubdirectory("comet-deferred-replay-").FullName;
        using var sink = new Diagnostics(dir, "offline", s => { lock (errors) errors.Add(s); }, reportReceiverAvailable: false);
        Decisions.Clear();
        Decisions.RaidClosed = false;
        Decisions.Published += RR;
        Decisions.Published += Satellite;
        try
        {
            sink.ConfigureLogging(false, 1, "offline");
            int index = 0;
            foreach (var record in records)
            {
                if (record.Capture != null)
                    Diagnostics.PublishSnapshot(sink, record.Capture(), ex => errors.Add(ex.Message));
                else
                {
                    sink.Write(record.Expected);
                    if (record.Published) Decisions.Publish(record.Expected, ex => errors.Add(ex.Message));
                }
                if (++index % 64 == 0)
                {
                    Decisions.Pump(index);
                    Thread.Sleep(2); // replay in bursts, without saturating the bounded disk queue
                }
            }
            Decisions.FlushPending();
            sink.Dispose();
            sink.Completion.GetAwaiter().GetResult();
            var lines = Directory.GetFiles(dir, "*.jsonl").OrderBy(f => f, StringComparer.Ordinal)
                .SelectMany(File.ReadLines).ToArray();
            if (rr != expected.Length || satellite != rr || errors.Count != 0 ||
                lines.Length != records.Count + 2 ||
                !lines.Skip(1).Take(records.Count).SequenceEqual(records.Select(e => e.Expected)))
                throw new Exception($"Replay mismatch: file {lines.Length}, RR {rr}, Satellite {satellite}. " + string.Join("; ", errors));
            Console.WriteLine($"Verified {records.Count} archived records, {rr} ordered public callbacks per listener, same bytes for both sinks.");
        }
        finally
        {
            Decisions.Published -= RR;
            Decisions.Published -= Satellite;
            Decisions.Clear();
            sink.Dispose();
            sink.Completion.GetAwaiter().GetResult();
            Directory.Delete(dir, true);
        }
    }

    private static (double Producer, double Elapsed, long Bytes) Measure(Event[] records, bool deferred)
    {
        int received = 0;
        void Count(string json) => received++;
        Decisions.Clear();
        Decisions.Published += Count;
        var wall = Stopwatch.StartNew();
        long ticks = 0, bytes = 0;
        try
        {
            const int batch = 32;
            for (int start = 0; start < records.Length; start += batch)
            {
                Decisions.Pump(start);
                long allocated = GC.GetAllocatedBytesForCurrentThread();
                long before = Stopwatch.GetTimestamp();
                for (int i = start; i < Math.Min(start + batch, records.Length); i++)
                {
                    if (deferred) Diagnostics.PublishSnapshot(null, records[i].Capture!(), Fail);
                    else Decisions.Publish(Diagnostics.Serialize(records[i].Reference!), Fail);
                }
                ticks += Stopwatch.GetTimestamp() - before;
                bytes += GC.GetAllocatedBytesForCurrentThread() - allocated;
                Decisions.FlushPending(); // account for all remaining work in elapsed time
            }
            if (received != records.Length) throw new Exception("Benchmark lost events");
            return (ticks * 1000d / Stopwatch.Frequency, wall.Elapsed.TotalMilliseconds, bytes);
        }
        finally { Decisions.Published -= Count; Decisions.Clear(); }
    }

    private static void Fail(Exception error) => throw error;
}
