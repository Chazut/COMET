using System.Text;
using Comet.Api;
using Comet.Client;
using Comet.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

internal static class EssentialReplayTests
{
    internal static int Run()
    {
        int count = 0;
        foreach (var (feature, stage, decision, part, rr, sat) in new[]
        {
            ("S10", "accepted", "SquadGrenade", "squadGrenade", true, true),
            ("S11", "released", "Siege", "siege", true, true),
            ("S03", "evaluated", "Crossfire", "crossfire", true, true),
            ("W04", "completed", "RecoverMagazine", "magazineRecovery", true, true),
            ("W04", "evaluated", "QuickReload", "quickReload", false, false),
            ("A13", "released", "Hold", "handoff", false, false),
            ("A02", "accepted", "Flank", "contact", true, false),
            ("A02", "evaluated", "Flank", "candidates", false, false),
            ("W05", "evaluated", "WeaponRange", "weaponRange", false, false),
            ("H04", "evaluated", "PostCombat", "postCombat", true, false)
        })
        {
            var nested = new JObject { ["state"] = "active", ["contact"] = "enemy", ["goalX"] = 12 };
            var input = new JObject { ["schema"] = 1, ["feature"] = feature, ["decision"] = decision, ["stage"] = stage,
                ["config"] = new JObject(), ["name"] = "Bot", ["realtime"] = 3.5, ["candidates"] = new JArray(2, 1), [part] = nested };
            foreach (bool review in new[] { false, true })
            {
                var json = EssentialReplay.Project(input.ToString(Formatting.None), review);
                bool keep = review ? rr : sat;
                if ((json != null) != keep) throw new Exception("Incorrect replay routing: " + feature);
                if (json != null)
                {
                    var output = JObject.Parse(json);
                    if (!JToken.DeepEquals(nested, output[part]) || output["config"] != null || output["candidates"] != null)
                        throw new Exception("Replay detail mismatch: " + feature);
                }
                count++;
            }
        }
        return count;
    }

    internal static void Replay(string directory, IEnumerable<string> files)
    {
        Directory.CreateDirectory(directory);
        foreach (var (name, extended, rr) in new[] { ("rr", false, true), ("satellite", false, false), ("extended", true, true) })
        {
            using var output = new StreamWriter(Path.Combine(directory, name + ".jsonl"), false, new UTF8Encoding(false));
            ReplayCapture.Configure(true, extended, false, rr);
            Decisions.RaidClosed = false;
            Decisions.Clear();
            int inputs = 0, rows = 0;
            long bytes = 0;
            string? expected = null;
            void Receive(string json)
            {
                if (expected == null) throw new Exception("Unexpected replay event");
                if (extended && json != expected) throw new Exception("Extended payload changed");
                var source = Parse(expected);
                var copy = Parse(json);
                foreach (string key in new[] { "sequence", "session", "feature", "stage", "profileId", "botInstance",
                    "name", "decision", "reason", "realtime", "contact", "squadGrenade", "siege", "crossfire", "magazineRecovery", "postCombat", "lifecycle" })
                    if (!JToken.DeepEquals(source[key], copy[key]))
                        throw new Exception($"Replay field {key} changed at {source["sequence"]}");
                if (!extended && (copy["config"] != null || copy["candidates"] != null || copy["mixing"] != null))
                    throw new Exception("Diagnostic data in essential replay.");
                output.WriteLine(json);
                bytes += Encoding.UTF8.GetByteCount(json);
                rows++;
            }
            Decisions.Published += Receive;
            var errors = new List<Exception>();
            try
            {
                foreach (var file in files)
                foreach (var line in File.ReadLines(file))
                {
                    var item = Parse(line);
                    string? feature = (string?)item["feature"], stage = (string?)item["stage"];
                    if ((long?)item["sequence"] is not > 0 || stage == "eligibility" ||
                        feature == "S07" && stage == "diagnostic" || feature == "T01" && item["steeringFire"] != null) continue;
                    inputs++;
                    expected = line;
                    bool squad = item["crossfire"] is JObject || feature is "S10" or "S11";
                    bool magazine = feature == "W04" && (string?)item["decision"] == "RecoverMagazine";
                    bool action = stage is "accepted" or "released" or "rejected" or "selected" or "started";
                    bool expect = extended || squad || magazine || rr && feature != "A13" && (action || feature is "C09" or "C13" or "H04");
                    int previous = rows;
                    Decisions.Publish(line, errors.Add);
                    Decisions.FlushPending();
                    if (errors.Count > 0) throw errors[0];
                    if (rows - previous != (expect ? 1 : 0)) throw new Exception($"Unexpected {name} delivery at {item["sequence"]}");
                }
                Console.WriteLine($"{name}: {rows} / {inputs} events, {bytes} UTF-8 bytes, saved in archive order.");
            }
            finally
            {
                Decisions.Published -= Receive;
                Decisions.Clear();
                ReplayCapture.Configure(true, true, true, true);
            }
        }
    }

    private static JObject Parse(string json)
    {
        using var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None };
        return JObject.Load(reader);
    }
}
