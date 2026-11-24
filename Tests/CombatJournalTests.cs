using Comet.Client;
using Comet.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

static class CombatJournalTests
{
    public static int Run()
    {
        int checks = 0;
        void Check(bool ok, string why)
        {
            checks++;
            if (!ok)
                throw new Exception("T01/T03: " + why);
        }
        string Json(object o) => JsonConvert.SerializeObject(o, Diagnostics.Json);
        var c = new WeaponJournalConfig();
        c.Validate();
        new CombatJournalConfig().Validate();
        Check(
            c.Enabled && new CombatJournalConfig().Enabled && c.Window == 20 && c.ChangeThreshold == 3 && c.ConfirmationTimeout == 5,
            "accepted defaults"
        );
        foreach (
            Action invalid in new Action[]
            {
                () => new WeaponJournalConfig { Window = 0 }.Validate(),
                () => new WeaponJournalConfig { ChangeThreshold = 2 }.Validate(),
                () => new WeaponJournalConfig { ConfirmationTimeout = 31 }.Validate(),
                () => new CombatJournalConfig { SampleInterval = 0 }.Validate(),
            }
        )
        {
            bool threw = false;
            try
            {
                invalid();
            }
            catch
            {
                threw = true;
            }
            Check(threw, "invalid configuration rejected");
        }
        c.Enabled = true;
        var j = new WeaponJournal();
        Check(
            j.Observe(0, "rifle", true, c).Count == 0,
            "initial equipment is a baseline, not a change"
        );
        long id = j.Begin();
        var e = j.Result(id, 1, "rifle", "pistol", "W02", true);
        Check(e.Single().State == "accepted", "acceptance is distinct from readiness");
        Check(j.Observe(2, "", false, c).Count == 0, "temporary empty hands not counted");
        e = j.Observe(3, "pistol", false, c);
        Check(
            e.Any(x => x.State == "observed")
                && e.Any(x => x.State == "in-hands")
                && !e.Any(x => x.State == "ready"),
            "actual in-hands transition without readiness"
        );
        e = j.Observe(4, "pistol", true, c);
        Check(
            e.Single().State == "ready" && e[0].Request == id,
            "native readiness confirms correct request"
        );
        Check(
            j.Observe(4.5, "pistol", true, c).Count == 0,
            "repeated samples do not duplicate changes or ready events"
        );
        id = j.Begin();
        j.Result(id, 5, "pistol", "rifle", "W03", true);
        e = j.Observe(10, "pistol", true, c);
        Check(
            e.Single().State == "unconfirmed",
            "accepted request without transition times out without assumed failure"
        );
        e = j.Observe(11, "rifle", true, c);
        Check(
            e.Single().State == "observed" && e[0].Request == 0,
            "late change is not assigned to expired request"
        );
        e = j.Observe(12, "pistol", true, c);
        Check(
            e.Count(x => x.State == "loop-start") == 1 && e.Last().Changes == 3,
            "three observed changes with reversals start one incident"
        );
        e = j.Observe(13, "rifle", true, c);
        Check(
            e.Count(x => x.State == "loop-update") == 1 && e.Last().Incident == 1,
            "continued alternations share the incident"
        );
        e = j.Observe(34, "rifle", true, c);
        Check(
            e.Single().State == "loop-end" && e[0].Duration == 10,
            "quiet window closes incident using actual first/last change times"
        );
        j = new();
        j.Observe(0, "a", true, c);
        foreach (var pair in new[] { (1d, "b"), (2d, "c"), (3d, "d") })
            Check(
                !j.Observe(pair.Item1, pair.Item2, true, c).Any(x => x.State.StartsWith("loop")),
                "three distinct changes are not an alternation loop"
            );
        j = new();
        j.Observe(0, "a", true, c);
        id = j.Begin();
        j.Result(id, 1, "a", "b", "unknown", true);
        var second = j.Begin();
        e = j.Result(second, 2, "a", "c", "W05", true);
        Check(
            e.Any(x => x.State == "interrupted" && x.Request == id),
            "superseding native request closes previous diagnostic attempt"
        );
        j.Result(j.Begin(), 3, "a", "b", "unknown", false);
        e = j.Observe(4, "c", true, c);
        Check(
            e.Any(x => x.State == "ready" && x.Request == second),
            "refusal does not destroy another pending request"
        );
        j.Result(j.Begin(), 5, "c", "b", "unknown", true);
        e = j.Suspend(6, "ghost");
        Check(e.Single().State == "unconfirmed", "sleep does not invent completion");
        Check(j.Observe(7, "b", true, c).Count == 0, "wake establishes a fresh baseline");
        Check(j.Suspend(8, "raid-ended").Count == 0, "cleanup is idempotent");
        j = new();
        j.Observe(0, "a", true, c);
        j.Result(j.Begin(), 0, "a", "b", "unknown", true);
        j.Observe(1, "b", false, c);
        e = j.Observe(5, "b", false, c);
        Check(
            e.Single().Reason == "weapon-seen-readiness-unconfirmed",
            "held weapon is not automatically ready"
        );

        var report = new CombatReport("raid");
        long seq = 0;
        void Configure(bool enabled = true) =>
            report.Observe(
                Json(
                    new
                    {
                        schema = 1,
                        session = "raid",
                        feature = "T01",
                        stage = "configuration",
                        combatEnabled = enabled,
                        weaponEnabled = enabled,
                        configRevision = 4,
                    }
                )
            );
        void Context(
            double at,
            string action = "Hold",
            string bot = "bot",
            string kind = "context",
            double damage = 0
        ) =>
            report.Observe(
                Json(
                    new
                    {
                        schema = 1,
                        session = "raid",
                        sequence = ++seq,
                        feature = "T01",
                        stage = "evaluated",
                        realtime = at,
                        botInstance = bot,
                        profileId = bot,
                        name = bot,
                        personality = "Normal",
                        combatJournal = new
                        {
                            kind,
                            damage,
                            context = new
                            {
                                action,
                                feature = "A01",
                                target = "enemy",
                            },
                        },
                    }
                )
            );
        JObject[] Finish(long loss = 0, bool cleanup = true)
        {
            if (cleanup)
                report.Observe(
                    Json(
                        new
                        {
                            schema = 1,
                            session = "raid",
                            feature = "C13",
                            stage = "cleanup",
                            realtime = 100,
                        }
                    )
                );
            return report.Finish(loss).Select(p => JObject.Parse(Json(p))).ToArray();
        }
        JToken[] Rows(JObject[] pages, string feature) =>
            pages
                .Where(p => (string?)p["feature"] == feature)
                .SelectMany(p => p["journalReport"]!["rows"]!)
                .ToArray();
        void Detail(double at, object payload) =>
            report.Observe(Json(new
            {
                schema = 1, session = "raid", sequence = ++seq, feature = "T01",
                stage = "evaluated", realtime = at, botInstance = "bot", combatJournal = payload,
            }));
        Configure();
        Context(0);
        Context(5);
        Context(6, "Push");
        Context(7, "Push", kind: "hit-dealt", damage: 42);
        Context(8, "Push", kind: "kill");
        Context(9, "Push", kind: "death");
        var pages = Finish();
        var rows = Rows(pages, "T01");
        Check(
            rows.Length == 2 && (double)rows[0]["seconds"]! == 6,
            "sampled action duration attributed to previous observed action"
        );
        Check(
            (double)rows[1]["damageDealt"]! == 42
                && (int)rows[1]["kills"]! == 1
                && (int)rows[1]["deaths"]! == 1,
            "actual combat facts grouped by native context"
        );
        Check((bool)pages[0]["journalReport"]!["complete"]!, "clean journal marked complete");
        Check(!report.Finish(0).Any(), "report finalization idempotent");
        report = new("raid");
        seq = 0;
        Configure();
        Context(0);
        Detail(2, new { kind = "shot", totalShots = 1, context = new { action = "Hold", feature = "A01" } });
        Detail(3, new { kind = "shot-summary", totalShots = 1 });
        Detail(4, new { kind = "light-detection", clearRay = false });
        Context(100);
        pages = Finish();
        Check(
            (double)Rows(pages, "T01")[0]["seconds"]! == 0
                && !(bool)pages[0]["journalReport"]!["complete"]!,
            "missing observation interval never counted as action duration"
        );
        Check(
            (int)pages[0]["journalReport"]!["invalidEvents"]! == 0
                && (int)pages[0]["journalReport"]!["observationGaps"]! == 1,
            "late sample is an observation gap, not corrupt input"
        );
        Check(
            (bool)pages.First(p => (string?)p["feature"] == "T03")["journalReport"]!["complete"]!,
            "combat sampling gap does not invalidate weapon facts"
        );
        Check((double)pages[0]["journalReport"]!["maxObservationGap"]! == 100
            && Rows(pages, "T01").Sum(row => (int)row["samples"]!) == 2,
            "replay details never shorten a gap or add action samples");
        report = new("raid");
        seq = 0;
        Configure();
        Detail(1, new { kind = "shot", totalShots = 1 });
        Detail(2, new { kind = "shot", context = new { action = "Hold" } });
        Detail(3, new { kind = "shot-summary", totalShots = "unknown" });
        Detail(4, new { kind = "light-detection", clearRay = "false" });
        Detail(5, new { kind = "unknown", context = new { action = "Hold" } });
        pages = Finish();
        Check((int)pages[0]["journalReport"]!["invalidEvents"]! == 5,
            "damaged replay details and unknown event kinds remain invalid");
        report = new("raid");
        seq = 0;
        Configure();
        Context(93.768, bot: "dashu01");
        Context(100.799, bot: "dashu01");
        Context(102, bot: "dashu01");
        pages = Finish();
        Check(
            Math.Abs((double)pages[0]["journalReport"]!["unobservedSeconds"]! - 7.031) < .00001
                && Math.Abs((double)Rows(pages, "T01")[0]["seconds"]! - 1.201) < .00001,
            "raid 431 gap excluded, following observed interval retained"
        );
        Check(
            (string?)pages[0]["journalReport"]!["gapExamples"]![0]!["name"] == "dashu01",
            "gap records bot and time for next raid diagnosis"
        );
        var previousCulture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            foreach (string culture in new[] { "en-US", "fr-FR", "de-DE" })
            {
                System.Globalization.CultureInfo.CurrentCulture =
                    System.Globalization.CultureInfo.GetCultureInfo(culture);
                report = new("raid");
                seq = 0;
                Configure();
                Context(1.25);
                Context(3.75);
                Context(4, kind: "hit-dealt", damage: 42.5);
                pages = Finish();
                Check(
                    (double)Rows(pages, "T01")[0]["seconds"]! == 2.5
                        && (double)Rows(pages, "T01")[0]["damageDealt"]! == 42.5,
                    "fractional journal times and damage independent of OS locale: " + culture
                );
            }
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previousCulture;
        }
        report = new("raid");
        seq = 0;
        Configure();
        Context(10);
        Context(9);
        pages = Finish();
        Check(
            (int)pages[0]["journalReport"]!["invalidEvents"]! == 1
                && (int)pages[0]["journalReport"]!["observationGaps"]! == 0,
            "backwards timestamp remains invalid"
        );
        report = new("raid");
        seq = 0;
        Configure();
        Context(0);
        Context(1, kind: "pause");
        Context(6);
        pages = Finish();
        Check((double)Rows(pages, "T01")[0]["seconds"]! == 0, "sleep pause removes stale timeline");
        report = new("raid");
        seq = 0;
        Configure(false);
        Context(1);
        pages = Finish();
        Check(
            !Rows(pages, "T01").Any() && !(bool)pages[0]["journalReport"]!["enabled"]!,
            "disabled is not zero observed combat"
        );
        report = new("raid");
        seq = 0;
        Configure();
        Context(1);
        pages = Finish(3, false);
        Check(
            !(bool)pages[0]["journalReport"]!["complete"]!
                && (int)pages[0]["journalReport"]!["queueDropped"]! == 3,
            "loss and missing cleanup explicit"
        );
        report = new("raid");
        seq = 0;
        Configure();
        foreach (
            var change in new[]
            {
                new WeaponJournalEvent { State = "accepted" },
                new WeaponJournalEvent { State = "in-hands" },
                new WeaponJournalEvent { State = "ready" },
                new WeaponJournalEvent { State = "loop-start" },
                new WeaponJournalEvent { State = "loop-update" },
            }
        )
        {
            var json = Json(
                new
                {
                    schema = 1,
                    session = "raid",
                    feature = "T03",
                    stage = "evaluated",
                    sequence = ++seq,
                    botInstance = "bot",
                    profileId = "p",
                    name = "PMC",
                    weaponJournal = new { change },
                }
            );
            report.Observe(json);
            report.Observe(json);
        }
        pages = Finish();
        var wr = Rows(pages, "T03").Single();
        Check(
            (int)wr["requests"]! == 1 && (int)wr["incidents"]! == 1 && (int)wr["ready"]! == 1,
            "duplicates do not inflate weapon incidents"
        );
        report = new("raid");
        seq = 0;
        Configure();
        for (int i = 0; i < 300; i++)
            Context(i, "Hold", i.ToString());
        pages = Finish();
        Check(
            Rows(pages, "T01").Length == 256
                && pages.Where(p => (string?)p["feature"] == "T01").Count() == 33,
            "bounded rows and transport pages"
        );
        Check(
            !(bool)pages[0]["journalReport"]!["complete"]!
                && pages.All(p => Json(p).Length < 32768),
            "memory limit is explicit and pages remain bounded"
        );
        // Serialized cross-language fixtures contain real report payloads and real weapon-journal traces.
        report = new("0123456789abcdef0123456789abcdef");
        report.Observe(
            Json(
                new
                {
                    schema = 1,
                    session = "0123456789abcdef0123456789abcdef",
                    feature = "T01",
                    stage = "configuration",
                    combatEnabled = true,
                    weaponEnabled = true,
                }
            )
        );
        report.Observe(
            Json(
                new
                {
                    schema = 1,
                    session = "0123456789abcdef0123456789abcdef",
                    feature = "T01",
                    sequence = 1,
                    botInstance = "pmc",
                    profileId = "p",
                    name = "Test PMC",
                    personality = "Normal",
                    realtime = 10,
                    combatJournal = new
                    {
                        kind = "hit-dealt",
                        damage = 42,
                        context = new { action = "Shoot", feature = "A02" },
                    },
                }
            )
        );
        report.Observe(
            Json(
                new
                {
                    schema = 1,
                    session = "0123456789abcdef0123456789abcdef",
                    feature = "T03",
                    sequence = 2,
                    botInstance = "pmc",
                    profileId = "p",
                    name = "Test PMC",
                    weaponJournal = new
                    {
                        change = new WeaponJournalEvent
                        {
                            State = "loop-start",
                            Reason = "repeated-observed-alternation",
                        },
                    },
                }
            )
        );
        report.Observe(
            Json(
                new
                {
                    schema = 1,
                    session = "0123456789abcdef0123456789abcdef",
                    feature = "C13",
                    stage = "cleanup",
                    realtime = 20,
                }
            )
        );
        string directory = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "../../../../artifacts/t01-t03")
        );
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "journal-summary.json"),
            Json(report.Finish(0).ToArray())
        );
        Console.WriteLine($"T01/T03: {checks} journal/report checks passed.");
        return checks;
    }
}
