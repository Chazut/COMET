using System.Text.Json.Nodes;
using Comet.Client;
using Comet.Core;
using Comet.Server;
using Comet.Server.Editing;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

static class QuietLoggingTests
{
    public static async Task<int> Run()
    {
        int checks = 0;
        void Check(bool ok, string why)
        {
            checks++;
            if (!ok)
                throw new Exception("Quiet logging: " + why);
        }
        string Json(object value) => Diagnostics.Serialize(value);
        string directory = Path.Combine(
            Path.GetTempPath(),
            "comet-quiet-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(directory);
        try
        {
            string freshPath = Path.Combine(directory, "fresh.json");
            var freshStore = new ConfigStore(freshPath);
            var first = freshStore.Read();
            Check(first.QuietLogging && !File.Exists(freshPath), "new installs start quiet");
            foreach (var profile in new[] { first, DifficultyPresets.Create("Easy"), DifficultyPresets.Create("Normal"), DifficultyPresets.Create("Hard") })
                Check(!profile.TacticCounters.Enabled && !profile.CombatJournal.Enabled && !profile.WeaponJournal.Enabled
                    && !profile.ControlJournal.Enabled && !profile.Performance.Enabled, "player defaults collect no optional reports");
            first.QuietLogging = false;
            freshStore.Save(first, first.Revision);
            Check(!new ConfigStore(freshPath).Read().QuietLogging, "first explicit opt-out survives restart");

            var legacy = new CometConfig { Revision = 42 };
            legacy.TacticCounters.Enabled = legacy.ControlJournal.Enabled =
                legacy.CombatJournal.Enabled = legacy.WeaponJournal.Enabled = true;
            legacy.Performance.Enabled = true;
            var saved = JsonNode
                .Parse(System.Text.Json.JsonSerializer.Serialize(legacy))!
                .AsObject();
            saved.Remove("QuietLogging");
            saved["quietLogging"] = false;
            saved["ExtraSetting"] = 19;
            string path = Path.Combine(directory, "config.json");
            File.WriteAllText(path, saved.ToJsonString());
            var store = new ConfigStore(path);
            var existing = store.Read();
            Check(
                existing.QuietLogging && existing.Revision == 43 && existing.Performance.Enabled,
                "old config enables Quiet once, invalidates stale drafts and retains performance capture"
            );
            string migrationBackup = Directory.GetFiles(directory, "config.json.before-quiet-*.bak").Single();
            string migratedText = File.ReadAllText(path);
            var migrated = JsonNode.Parse(migratedText)!.AsObject();
            Check(
                File.ReadAllText(migrationBackup) == saved.ToJsonString()
                    && migrated["quietLogging"]!.GetValue<bool>() && !migrated.ContainsKey("QuietLogging")
                    && (int?)migrated["ExtraSetting"] == 19,
                "migration backs up the original and preserves casing and unrelated settings"
            );
            var restarted = new ConfigStore(path);
            var game = JObject.Parse(restarted.ForGame());
            Check(new[] { "TacticCounters", "CombatJournal", "WeaponJournal", "ControlJournal", "Performance" }
                .All(name => (bool?)game[name]?["Enabled"] == true), "saved personal opt-ins reach the raid after migration");
            Check((bool?)game["QuietLogging"] == true && game["QuietLoggingDefaultApplied"] == null
                && File.ReadAllText(path) == migratedText, "game receives quiet without metadata or another write");
            bool staleRejected = false;
            try { store.Save(legacy, 42); }
            catch (InvalidOperationException) { staleRejected = true; }
            Check(staleRejected, "an editor opened before the migration cannot undo it");
            foreach (bool alreadyQuiet in new[] { false, true })
            {
                string otherPath = Path.Combine(directory, alreadyQuiet ? "enabled.json" : "missing.json");
                var other = new JsonObject { ["BehaviorBaseline"] = CometConfig.SourceBaseline, ["Revision"] = 7 };
                if (alreadyQuiet)
                    other["QuietLogging"] = true;
                File.WriteAllText(otherPath, other.ToJsonString());
                var otherStore = new ConfigStore(otherPath);
                var upgraded = otherStore.Read();
                Check(upgraded.QuietLogging && upgraded.Revision == (alreadyQuiet ? 7 : 8),
                    "omitted and already enabled settings both migrate");
                var manual = JsonNode.Parse(File.ReadAllText(otherPath))!;
                manual["QuietLogging"] = false;
                File.WriteAllText(otherPath, manual.ToJsonString());
                Check(!new ConfigStore(otherPath).Read().QuietLogging, "manual opt-out survives restart");
            }
            var editor = new ConfigEditor(existing);
            var field = editor.Catalog.Fields[nameof(CometConfig.QuietLogging)];
            Check(
                field.Boolean
                    && field.Simple
                    && editor.Catalog.Sections[field.Section].Category == "Diagnostics",
                "toggle is available in simple Diagnostics UI"
            );
            Check(
                editor.Visible(false, "quiet", false).Contains(field),
                "search finds Quiet without advanced mode"
            );
            Check(
                editor.Set(field, "false") && editor.CanSave && !editor.Snapshot().QuietLogging,
                "UI draft can request detailed logs"
            );
            Check(
                store.Read().QuietLogging && existing.QuietLogging,
                "editing draft does not mutate saved or running configuration"
            );
            editor.AcceptSaved(store.Save(editor.Snapshot(), editor.Revision));
            Check(
                !new ConfigStore(path).Read().QuietLogging
                    && !JsonConvert.DeserializeObject<CometConfig>(store.ForGame())!.QuietLogging
                    && Directory.GetFiles(directory, "config.json.before-quiet-*.bak").Length == 1,
                "opt-out reaches game endpoint and survives restart without another migration"
            );
            Check(
                store.Read().Performance.Enabled,
                "Quiet does not change independent performance setting"
            );
            Check(editor.Set(field, "true"), "UI can re-enable Quiet");
            store.Save(editor.Snapshot(), editor.Revision);
            Check(
                store.Read().QuietLogging && store.Read().Revision == 45,
                "ON survives save/reload"
            );
            var old = JsonNode
                .Parse(
                    System.Text.Json.JsonSerializer.Serialize(
                        new CometConfig { QuietLogging = true }
                    )
                )!
                .AsObject();
            old["BehaviorBaseline"] = "older";
            Check(
                SourceBaselineMigration.Upgrade(old) && old["QuietLogging"]!.GetValue<bool>(),
                "behavior migration preserves logging preference"
            );

            var prior = JsonNode.Parse("""
                {"BehaviorBaseline":"zzap-fbb86b83-2","DifficultyPreset":"Easy",
                 "DoorJumpPeek":{"Chances":{"Rat":0,"Normal":12,"Chad":12.5}},"QuietLogging":true}
                """)!.AsObject();
            Check(SourceBaselineMigration.Upgrade(prior)
                && prior["DoorJumpPeek"]!["Chances"]!["Rat"]!.GetValue<double>() == 25
                && prior["DoorJumpPeek"]!["Chances"]!["Normal"]!.GetValue<double>() == 12
                && prior["DoorJumpPeek"]!["Chances"]!["Chad"]!.GetValue<double>() == 17.5
                && prior["DifficultyPreset"]!.GetValue<string>() == "Easy"
                && prior["QuietLogging"]!.GetValue<bool>() && !SourceBaselineMigration.Upgrade(prior),
                "only the previous default door rates migrate; custom values and unrelated settings survive a second load");
            var events = new List<string>();
            long sequence = 0;
            foreach (var stage in new[] { "selected", "accepted", "started", "released" })
                events.Add(
                    Json(
                        new
                        {
                            schema = 1,
                            session = "raid",
                            sequence = ++sequence,
                            feature = "A01",
                            stage,
                            reason = "test",
                            profileId = "pmc",
                            botInstance = "bot",
                            personality = "Normal",
                            decision = "Hold",
                            tacticObservationVersion = 1,
                            candidates = new[]
                            {
                                new
                                {
                                    decision = "Hold",
                                    eligible = true,
                                    reason = "available",
                                    score = 46,
                                },
                            },
                            mixing = new
                            {
                                opportunity = 1,
                                encounter = 1,
                                selected = "Hold",
                                roll = .2,
                                state = "weighted-roll",
                                options = new[] { new { decision = "Hold", probability = 1d } },
                            },
                            contact = new { target = "known", age = .3 },
                            handoff = new { action = "Hold", reason = "test" },
                        }
                    )
                );
            foreach (double at in new[] { 1d, 2d })
                events.Add(
                    Json(
                        new
                        {
                            schema = 1,
                            session = "raid",
                            sequence = ++sequence,
                            feature = "T01",
                            stage = "evaluated",
                            realtime = at,
                            botInstance = "bot",
                            profileId = "pmc",
                            name = "Test PMC",
                            personality = "Normal",
                            combatJournal = new
                            {
                                kind = "context",
                                context = new
                                {
                                    action = "Hold",
                                    feature = "A01",
                                    target = "enemy",
                                },
                            },
                        }
                    )
                );
            for (int i = 0; i < 40; i++)
                events.Add(
                    Json(
                        new
                        {
                            schema = 1,
                            session = "raid",
                            sequence = ++sequence,
                            feature = "S01",
                            stage = "evaluated",
                            botInstance = "bot",
                            profileId = "pmc",
                            decision = "Flank",
                            squad = "team",
                            index = i,
                        }
                    )
                );
            string fault = Json(
                new
                {
                    schema = 1,
                    session = "raid",
                    feature = "C08",
                    stage = "evaluated",
                    reason = "fault-test",
                    faultIsolation = new { message = "test exception" },
                }
            );
            string cleanup = Json(
                new
                {
                    schema = 1,
                    session = "raid",
                    feature = "C13",
                    stage = "cleanup",
                    realtime = 10,
                }
            );
            var warnings = new List<string>();
            async Task<(string[] Decisions, string[] Reports, JObject[] Files)> Capture(
                bool quiet, bool reportsOn = true, bool authority = true, bool countersOnly = false)
            {
                string folder = Path.Combine(directory, Guid.NewGuid().ToString("N"));
                var decisions = new List<string>();
                var reports = new List<string>();
                Action<string> receive = decisions.Add,
                    summary = reports.Add;
                Comet.Api.Decisions.RaidClosed = false;
                Comet.Api.Decisions.Clear();
                Comet.Api.Decisions.Published += receive;
                Comet.Api.Decisions.SummaryPublished += summary;
                try
                {
                    var journal = new Diagnostics(folder, "raid", warnings.Add, 2048);
                    journal.ConfigureLogging(quiet, 44, "raid");
                    var settings = new CometConfig { QuietLogging = quiet, Revision = 44 };
                    settings.CombatJournal.Enabled = settings.WeaponJournal.Enabled = settings.ControlJournal.Enabled = reportsOn;
                    settings.TacticCounters.Enabled = reportsOn || countersOnly;
                    journal.ConfigureSnapshot(settings, "raid");
                    journal.ConfigureRaidReports(settings, authority, "raid");
                    foreach (string e in events)
                    {
                        journal.Write(e);
                        Comet.Api.Decisions.Publish(e, ex => throw ex);
                    }
                    journal.WriteEssential(fault);
                    Comet.Api.Decisions.Publish(fault, ex => throw ex);
                    journal.WriteEssential(cleanup);
                    Comet.Api.Decisions.Publish(cleanup, ex => throw ex);
                    Comet.Api.Decisions.FlushPending();
                    journal.Dispose();
                    await journal.Completion;
                    var files = Directory
                        .GetFiles(folder, "*.jsonl")
                        .SelectMany(File.ReadLines)
                        .Select(JObject.Parse)
                        .ToArray();
                    return (decisions.ToArray(), reports.ToArray(), files);
                }
                finally
                {
                    Comet.Api.Decisions.Published -= receive;
                    Comet.Api.Decisions.SummaryPublished -= summary;
                    Comet.Api.Decisions.Clear();
                }
            }
            var full = await Capture(false);
            var quiet = await Capture(true);
            foreach (var (silence, authority) in new[] { (true, true), (false, true), (true, false) })
            {
                var off = await Capture(silence, reportsOn: !authority, authority: authority);
                var footer = off.Files.Single(e => (string?)e["stage"] == "diagnostics-end");
                Check((int?)footer["summaryReader"]?["parsedEvents"] == 7
                    && (int?)footer["quietSuppressed"] == (silence ? events.Count : 0),
                    "only configuration, fault and cleanup parsed with all reports off or on a replica");
                Check(off.Files.Count(e => (long?)e["sequence"] > 0) == (silence ? 0 : events.Count)
                    && off.Decisions.SequenceEqual(full.Decisions), "detailed file and ordinary RR delivery remain independent");
                var summaries = off.Reports.Select(JObject.Parse).Select(e => e["tacticCounters"] ?? e["journalReport"]).ToArray();
                Check(summaries.Length == 4 && summaries.All(r => (bool?)r?["enabled"] == false && (bool?)r?["sawCleanup"] == true),
                    "disabled reports still receive cleanup and tell RR that collection was off");
            }
            var countersOnly = await Capture(true, reportsOn: false, countersOnly: true);
            Check(countersOnly.Reports.Select(JObject.Parse).Any(e => e["tacticCounters"]?["rows"]?.Any() == true),
                "one active report keeps receiving ordinary events in quiet mode");
            Check(
                full.Decisions.SequenceEqual(events.Concat(new[] { fault, cleanup })),
                "full RR stream contains every original action, score, contact, squad event and fault"
            );
            Check(
                quiet.Decisions.SequenceEqual(full.Decisions),
                "Quiet RR events are byte-for-byte identical and in the same order"
            );
            Check(
                quiet.Reports.Length > 0 && quiet.Reports.SequenceEqual(full.Reports),
                "RR receives identical non-empty counters and journal reports in both modes"
            );
            foreach (string feature in new[] { "T01", "T02", "T03", "T04" })
                Check(
                    quiet.Reports.Select(JObject.Parse).Any(p => (string?)p["feature"] == feature),
                    "summary retained: " + feature
                );
            var counters = quiet
                .Reports.Select(JObject.Parse)
                .Where(p => (string?)p["feature"] == "T02")
                .ToArray();
            var row = counters
                .SelectMany(p => p["tacticCounters"]!["rows"]!)
                .Single(r => (string?)r["feature"] == "A01");
            Check(
                (int)row["opportunities"]! == 1 && (int)row["started"]! == 1,
                "filtered local events still contribute to observed tactic execution"
            );
            Check(
                (bool)counters[0]["tacticCounters"]!["complete"]!,
                "intentional filtering does not falsely mark summaries incomplete"
            );
            var combat = quiet
                .Reports.Select(JObject.Parse)
                .Where(p => (string?)p["feature"] == "T01")
                .SelectMany(p => p["journalReport"]!["rows"]!)
                .Single();
            Check((double)combat["seconds"]! == 1, "combat duration remains measurable with Quiet");
            Check(
                full.Files.Count(p => p["sequence"] != null && (int)p["sequence"]! > 0)
                    == events.Count,
                "OFF writes all detail across segments"
            );
            Check(
                !quiet.Files.Any(p => p["sequence"] != null && (int)p["sequence"]! > 0),
                "ON omits detailed local events only"
            );
            Check(
                quiet.Files.Any(p => (string?)p["reason"] == "fault-test")
                    && quiet.Files.Any(p => (string?)p["stage"] == "cleanup"),
                "fault and cleanup evidence always persisted"
            );
            Check(
                quiet.Files.Any(p =>
                    (string?)p["stage"] == "configuration" && p["config"] is JObject
                ),
                "full configuration retained for interpreting raid"
            );
            Check(
                quiet.Files.Count(p => (string?)p["stage"] == "summary") == quiet.Reports.Length,
                "every summary also retained locally"
            );
            foreach (var result in new[] { full, quiet })
            {
                var footer = result.Files.Single(p => (string?)p["stage"] == "diagnostics-end");
                Check(
                    (string?)footer["summaryReader"]?["mode"] == "shared-projection-v1"
                        && (long)footer["summaryReader"]!["legacyFallbacks"]! == 0,
                    "projected background reader completed without fallback"
                );
                Check(
                    (long)footer["summaryReader"]!["projectedEvents"]! > 0
                        && (long)footer["summaryReader"]!["skippedProperties"]! > 0,
                    "unused snapshots were skipped while keeping reports and RR intact"
                );
                Check(
                    (int)footer["queueDropped"]! == 0 && (int)footer["writeFailed"]! == 0,
                    "no accidental data loss"
                );
                Check(
                    (int)footer["quietSuppressed"]!
                        == ((bool)footer["quietLogging"]! ? events.Count : 0),
                    "intentional omissions separately counted"
                );
            }
            Check(warnings.Count == 0, "normal filtering generates no warning spam");
            // Multiple game/raid instances must not inherit a previous Quiet mode.
            var fresh = new Diagnostics(Path.Combine(directory, "next"), "next", warnings.Add);
            fresh.Write(Json(new { stage = "next-raid-detail" }));
            fresh.Dispose();
            await fresh.Completion;
            Check(
                Directory
                    .GetFiles(Path.Combine(directory, "next"))
                    .SelectMany(File.ReadLines)
                    .All(l => !l.Contains("next-raid-detail")),
                "new writer stays quiet before receiving the raid configuration"
            );
            // Failed disk IO must remain visible even when detailed file output is suppressed.
            string blocked = Path.Combine(directory, "blocked");
            File.WriteAllText(blocked, "not a directory");
            bool failed = false;
            try
            {
                _ = new Diagnostics(blocked, "raid", warnings.Add);
            }
            catch (IOException)
            {
                failed = true;
            }
            Check(failed, "file initialization failure is not hidden by Quiet");
        }
        finally
        {
            Directory.Delete(directory, true);
        }
        Console.WriteLine($"Quiet logging: {checks} assertions passed.");
        return checks;
    }
}
