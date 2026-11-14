using Comet.Client;
using Comet.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Diagnostics;

internal static class DiagnosticEventReaderTests
{
    public static int Run()
    {
        int checks = 0;
        void Check(bool value, string why)
        {
            checks++;
            if (!value) throw new Exception("Diagnostic projection: " + why);
        }
        string Json(object value) => Diagnostics.Serialize(value);
        var events = new List<string>
        {
            Json(new {schema=1, session="raid", feature="T02", stage="configuration", enabled=true, configRevision=3, maxOpportunities=16384}),
            Json(new {schema=1, session="raid", feature="T01", stage="configuration", combatEnabled=true, weaponEnabled=true, configRevision=3}),
            Json(new {schema=1, session="raid", feature="T04", stage="configuration", enabled=true, configRevision=3}),
            Json(new {schema=1, session="raid", feature="configuration", stage="configuration", config=new CometConfig()})
        };
        foreach (string stage in new[] {"selected", "accepted", "started", "released"})
            events.Add(Json(new {schema=1, session="raid", feature="A01", stage, profileId="pmc", botInstance="bot", personality="Normal", decision="Hold", reason="test", tacticObservationVersion=1,
                candidates=new[] {new {decision="Hold", eligible=true, reason="available", score=46}},
                mixing=new {opportunity=1, encounter=1, selected="Hold", roll=.2, state="weighted-roll", options=new[] {new {decision="Hold", probability=1d}}},
                unneeded=new {feature="T04", schema=99, stage="configuration", config=new CometConfig()}}));
        long sequence = 0;
        foreach (double time in new[] {1d, 2d})
            events.Add(Json(new {schema=1, session="raid", sequence=++sequence, feature="T01", stage="evaluated", realtime=time, botInstance="bot", profileId="pmc", name="PMC", personality="Normal",
                combatJournal=new {kind="context", context=new {action="Hold", feature="A01", target="enemy"}}}));
        events.Add(Json(new {schema=1, session="raid", sequence=++sequence, feature="T03", stage="evaluated", botInstance="bot", profileId="pmc", name="PMC",
            weaponJournal=new {change=new WeaponJournalEvent {State="request", Reason="reload"}}}));
        events.Add(Json(new {schema=1, session="raid", sequence=++sequence, feature="T04", stage="evaluated", botInstance="bot", profileId="pmc", name="PMC",
            controlJournal=new ControlJournalEvent {Kind="takeover", Owner="orbit", Blocker="selected-layer-active", Elapsed=15}}));
        foreach (string feature in new[] {"D05", "D06"})
            events.Add(Json(new {schema=1, session="raid", feature, stage="evaluated", profileId="pmc", botInstance="bot",
                doorFakeGrenade=new {state="idle", reason="test"}, doorFakeMedical=new {state="idle", reason="test"}}));
        events.Add(Json(new {schema=1, session="raid", feature="C13", stage="cleanup", realtime=10}));

        Check(Equivalent(events, "raid"), "same counters, combat, weapon and control summaries");
        var reverse = events.Select(line => new JObject(JObject.Parse(line).Properties().Reverse()
            .Select(p => new JProperty(p.Name, p.Value.DeepClone()))).ToString(Formatting.None)).ToList();
        Check(Equivalent(reverse, "raid"), "root property order does not affect summaries");
        var special = new List<string>(events);
        special.Insert(4, "/* comment */ {\"ignored\":{\"feature\":\"T04\",\"enabled\":false},\"schema\":1,\"session\":\"raid\",\"feature\":\"wrong\",\"featu\\u0072e\":\"S01\",\"stage\":/* value */\"evaluated\"}");
        Check(Equivalent(special, "raid"), "comments, escaped keys, duplicates and nested lookalikes");
        foreach (string damaged in new[]
        {
            "{\"feature\":\"T04\",", "[]", "null", "{\"ignored\":[1,2}",
            "{\"feature\":\"T01\",\"ignored\":" + new string('[', 50) + "0" + new string(']', 50) + "}",
            "{\"feature\":\"T04\",\"ignored\":\"" + new string('x', 262144) + "\"}"
        })
        {
            var damagedEvents = new List<string>(events);
            damagedEvents.Insert(4, damaged);
            Check(Equivalent(damagedEvents, "raid"), "malformed input preserves accounting and later events");
        }
        var reader = new DiagnosticEventReader();
        var counters = new TacticCounters("raid");
        var journal = new CombatReport("raid");
        foreach (string line in events) reader.Observe(line, counters, journal);
        Check(reader.ParsedEvents == events.Count && reader.ProjectedEvents > 0 && reader.SkippedProperties > 0 && reader.LegacyFallbacks == 0,
            "normal input uses projection without fallback");
        reader.Observe("{", counters, journal);
        Check(reader.LegacyFallbacks == 1, "invalid input is separately observable");

        // The complete source string is never rewritten. Summary-only projection
        // avoids allocating the large snapshot trees for unconsumed feature data.
        var repeated = Enumerable.Repeat(events[3], 100).ToArray();
        Measure(repeated, "raid", false); Measure(repeated, "raid", true);
        var oldCost = Measure(repeated, "raid", false);
        var newCost = Measure(repeated, "raid", true);
        Check(newCost.Bytes < oldCost.Bytes * .7, "configuration snapshots allocate substantially less");
        Console.WriteLine($"Diagnostic projection snapshot allocations: {oldCost.Bytes:N0} -> {newCost.Bytes:N0} bytes (.NET test runtime).");

        // Optional replay of the original, immutable diagnostic JSONL. This is
        // intentionally separate from checked-in tests and carries no player data.
        string? path = Environment.GetEnvironmentVariable("COMET_DIAGNOSTIC_REPLAY");
        if (!string.IsNullOrEmpty(path))
        {
            var lines = File.ReadAllLines(path).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
            string session = (string)JObject.Parse(lines.First(l => JObject.Parse(l)["session"] != null))["session"]!;
            Check(Equivalent(lines, session), "archived raid produces identical complete summaries");
            oldCost = Measure(lines, session, false);
            newCost = Measure(lines, session, true);
            Check(newCost.Bytes < oldCost.Bytes, "archived raid allocations decreased");
            Console.WriteLine($"Diagnostic projection replay: {lines.Length:N0} events; allocations {oldCost.Bytes:N0} -> {newCost.Bytes:N0} bytes; elapsed {oldCost.Ms:F1} -> {newCost.Ms:F1} ms (.NET, not an in-game frame benchmark).");
        }
        Console.WriteLine($"Diagnostic projection: {checks} checks passed.");
        return checks;
    }

    private static bool Equivalent(IEnumerable<string> lines, string session)
    {
        var oldCounters = new TacticCounters(session);
        var oldJournal = new CombatReport(session);
        var newCounters = new TacticCounters(session);
        var newJournal = new CombatReport(session);
        var reader = new DiagnosticEventReader();
        foreach (string line in lines)
        {
            oldCounters.Observe(line); oldJournal.Observe(line);
            reader.Observe(line, newCounters, newJournal);
        }
        string Before() => Diagnostics.Serialize(oldCounters.Finish(0).Concat(oldJournal.Finish(0)).ToArray());
        string After() => Diagnostics.Serialize(newCounters.Finish(0).Concat(newJournal.Finish(0)).ToArray());
        return Before() == After();
    }

    private static (long Bytes, double Ms) Measure(IEnumerable<string> lines, string session, bool projected)
    {
        var counters = new TacticCounters(session);
        var journal = new CombatReport(session);
        var reader = new DiagnosticEventReader();
        var clock = Stopwatch.StartNew();
        long start = GC.GetAllocatedBytesForCurrentThread();
        foreach (string line in lines)
            if (projected) reader.Observe(line, counters, journal);
            else { counters.Observe(line); journal.Observe(line); }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - start;
        clock.Stop();
        return (allocated, clock.Elapsed.TotalMilliseconds);
    }
}
