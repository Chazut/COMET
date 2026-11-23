using Comet.Client;
using Comet.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

internal static class ControlJournalTests
{
    public static int Run()
    {
        int n = 0;
        void Check(bool value, string why)
        {
            n++;
            if (!value)
                throw new Exception("T04: " + why);
        }
        string Json(object x) => JsonConvert.SerializeObject(x, Diagnostics.Json);
        var movement = new MovementObserver();
        int samples = 0;
        for (int i = 0; i < 1000; i++)
            if (movement.Due(i * .01, true))
                samples++;
        Check(samples == 10, "locomotion sampling bounded at one per second");
        Check(
            !movement.Due(9.9, false) && !movement.Due(9.91, true),
            "rapid handoff cannot bypass locomotion rate limit"
        );
        Check(movement.Due(1, true), "new clock permits a fresh observation");
        foreach (double bad in new[] { double.NaN, double.PositiveInfinity, -1d })
            Check(!movement.Due(bad, true), "invalid locomotion clock");
        var motion = MovementObserver.Capture(.35, .3, .4, false, false)!;
        Check(
            motion.Pose == .35
                && motion.Speed == .5
                && !motion.Prone
                && !motion.Sprinting
                && motion.ValidFor == 1.5,
            "actual horizontal motion and pose captured without ownership claims"
        );
        Check(
            MovementObserver.Capture(0, 0, 0, true, false)!.Prone,
            "stationary prone is separate from crouching"
        );
        Check(
            MovementObserver.Capture(1, 3, 4, false, true)!.Speed == 5,
            "sprint velocity observed"
        );
        foreach (
            var bad in new[]
            {
                MovementObserver.Capture(double.NaN, 0, 0, false, false),
                MovementObserver.Capture(1, 21, 0, false, false),
                MovementObserver.Capture(1, 0, double.PositiveInfinity, false, false),
                MovementObserver.Capture(2, 0, 0, false, false),
            }
        )
            Check(bad == null, "invalid or teleport motion not presented as locomotion");
        var c = new ControlJournalConfig();
        c.Validate();
        Check(
            !c.Enabled
                && c.LongWait == 30
                && c.RapidReturn == 5
                && c.CycleThreshold == 3
                && c.Window == 20,
            "approved defaults"
        );
        foreach (
            Action bad in new Action[]
            {
                () => new ControlJournalConfig { LongWait = 0 }.Validate(),
                () => new ControlJournalConfig { RapidReturn = double.NaN }.Validate(),
                () => new ControlJournalConfig { CycleThreshold = 13 }.Validate(),
                () => new ControlJournalConfig { Window = 61 }.Validate(),
            }
        )
        {
            bool rejected = false;
            try
            {
                bad();
            }
            catch
            {
                rejected = true;
            }
            Check(rejected, "configuration bounds");
        }
        c.Enabled = true;
        var model = new ControlJournal();
        double time = 0;
        var all = new List<ControlJournalEvent>();
        ControlObservation O(
            string owner = "combat",
            string blocker = "combat-active",
            string target = "enemy",
            double contact = 0,
            string residual = ""
        ) =>
            new()
            {
                Owner = owner,
                Layer = owner,
                Action = "Hold",
                Blocker = blocker,
                Target = target,
                ContactAt = contact,
                Residual = residual,
            };
        void At(double until, ControlObservation o)
        {
            while (time < until)
            {
                time = Math.Min(until, time + .25);
                all.AddRange(model.Observe(time, o, c));
            }
        }
        model.Observe(0, O(), c);
        At(21, O());
        model.Release("post-combat-finished");
        At(22, O("recovery", "medical-in-progress"));
        At(53, O("recovery", "medical-in-progress"));
        Check(
            all.Count(x => x.Kind == "long-wait") == 1
                && all.Single(x => x.Kind == "long-wait").Blocker == "medical-in-progress",
            "long wait reports once with actual explanation"
        );
        At(54, O("orbit", "selected-layer-active"));
        var takeover = all.Single(x => x.Kind == "takeover");
        Check(
            takeover.Elapsed == 32 && takeover.Reason == "outside-layer-observed",
            "delay ends at selected outside layer"
        );
        At(55, O());
        Check(!all.Any(x => x.Kind == "incident-start"), "one rapid return is not an incident");
        At(56, O("orbit"));
        At(57, O());
        At(58, O("orbit"));
        At(59, O());
        Check(
            all.Count(x => x.Kind == "incident-start") == 1
                && all.Single(x => x.Kind == "incident-start").Cycles == 3,
            "three completed round trips form one incident"
        );
        At(60, O("orbit"));
        At(61, O());
        Check(all.Count(x => x.Kind == "incident-update") == 1, "ongoing cycles grouped");
        At(82, O());
        Check(all.Count(x => x.Kind == "incident-end") == 1, "quiet window closes incident");
        Check(
            all.Any(x => x.Kind == "interval" && x.WaitSeconds > 0)
                && all.Where(x => x.Kind == "interval").Sum(x => x.SampledSeconds) <= 82,
            "observed time bounded by samples"
        );
        model = new();
        time = 0;
        all.Clear();
        model.Observe(0, O(), c);
        At(1, O("orbit"));
        At(2, O());
        At(3, O("orbit"));
        At(4, O());
        At(5, O("orbit"));
        At(6, O());
        Check(
            !all.Any(x => x.Kind.StartsWith("incident")),
            "startup grace excludes normal first engagements"
        );
        At(21, O());
        for (int i = 0; i < 3; i++)
        {
            At(22 + i * 2, O("orbit", contact: i));
            At(23 + i * 2, O(contact: i + 1));
        }
        Check(
            all.Count(x => x.Kind == "combat-return" && x.Reason == "fresh-threat-observed") == 3
                && !all.Any(x => x.Kind == "incident-start"),
            "fresh contact excludes repeated returns"
        );
        At(28, O("sleep", "sleep"));
        At(29, O());
        At(30, O("questing"));
        At(31, O());
        Check(
            !all.Any(x => x.Kind == "incident-start"),
            "wake resets pending and incident history"
        );
        At(32, O("other", "selected-layer-active", residual: "intent:Hold"));
        At(34, O("other", "selected-layer-active", residual: "intent:Hold"));
        Check(all.Count(x => x.Kind == "residual") == 1, "sustained record only one warning");
        At(35, O("other"));
        At(36, O("other", residual: "intent:Hold"));
        At(38, O("other", residual: "intent:Hold"));
        Check(
            all.Count(x => x.Kind == "residual") == 2,
            "clearing then recurring record can be observed again"
        );
        var gap = model.Observe(100, O(), c);
        Check(
            gap.Any(x => x.Kind == "gap") && !gap.Any(x => x.Kind == "long-wait"),
            "missed observations do not invent long idle interval"
        );
        Check(
            model.Observe(101, O(), new() { Enabled = false }).Count == 0,
            "toggle affects diagnostics only"
        );
        Check(model.Observe(double.NaN, O(), c).Count == 0, "invalid clock ignored");
        var backward = model.Observe(0, O(), c);
        Check(backward.Any(x => x.Kind == "gap"), "new clock clears history");
        // Same owner, different layers is not a completed combat/outside cycle.
        model = new();
        time = 0;
        all.Clear();
        model.Observe(0, O(), c);
        At(21, O());
        for (int i = 0; i < 20; i++)
        {
            var o = O();
            o.Layer = i % 2 == 0 ? "SAIN : Avoid" : "SAIN : Combat Layer";
            At(22 + i, o);
        }
        Check(
            !all.Any(x => x.Kind == "combat-ended" || x.Kind == "incident-start"),
            "combat layers do not create false exits"
        );
        // Report, disabled/partial states, bounded memory, and real serialized RR fixture.
        string session = "0123456789abcdef0123456789abcdef";
        long sequence = 0;
        var report = new CombatReport(session);
        void Configure(bool enabled = true) =>
            report.Observe(
                Json(
                    new
                    {
                        schema = 1,
                        session,
                        feature = "T04",
                        stage = "configuration",
                        enabled,
                        configRevision = 42,
                    }
                )
            );
        void Event(ControlJournalEvent e, string bot = "pmc") =>
            report.Observe(
                Json(
                    new
                    {
                        schema = 1,
                        session,
                        feature = "T04",
                        stage = "evaluated",
                        sequence = ++sequence,
                        botInstance = bot,
                        profileId = bot,
                        name = "Test PMC",
                        controlJournal = e,
                    }
                )
            );
        JObject[] Finish(long loss = 0)
        {
            report.Observe(
                Json(
                    new
                    {
                        schema = 1,
                        session,
                        feature = "C13",
                        stage = "cleanup",
                        realtime = 100,
                    }
                )
            );
            return report.Finish(loss).Select(x => JObject.Parse(Json(x))).ToArray();
        }
        Configure();
        Event(
            new()
            {
                Kind = "takeover",
                Owner = "orbit",
                Blocker = "selected-layer-active",
                Elapsed = 15,
            }
        );
        Event(
            new()
            {
                Kind = "interval",
                Owner = "combat",
                Blocker = "combat-active",
                SampledSeconds = 4,
            }
        );
        Event(
            new()
            {
                Kind = "incident-start",
                Owner = "combat",
                Blocker = "combat-active",
            }
        );
        var pages = Finish();
        var rows = pages.SelectMany(x => x["journalReport"]!["rows"]!).ToArray();
        Check(
            pages.All(x => (string?)x["feature"] == "T04") && rows.Length == 2,
            "T04 can report independently of T01/T03"
        );
        Check(
            (double)rows[0]["takeoverSeconds"]! == 15 && (long)rows[1]["incidents"]! == 1,
            "correct aggregate outcomes"
        );
        Check((bool)pages[0]["journalReport"]!["complete"]!, "complete report");
        var dir = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "../../../../artifacts/t04")
        );
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "control-summary.json"), Json(pages));
        report = new(session);
        Configure(false);
        Event(new() { Kind = "takeover", Owner = "orbit" });
        pages = Finish();
        Check(
            !(bool)pages[0]["journalReport"]!["enabled"]! && pages.Length == 1,
            "disabled collection is explicit"
        );
        report = new(session);
        Configure();
        Event(new() { Kind = "gap", Owner = "combat" });
        pages = Finish(2);
        Check(!(bool)pages[0]["journalReport"]!["complete"]!, "gaps and queue loss mark partial");
        report = new(session);
        Configure();
        for (int i = 0; i < 300; i++)
            Event(new() { Kind = "context", Owner = "combat" }, i.ToString());
        pages = Finish();
        Check(
            pages.Length == 33 && !(bool)pages[0]["journalReport"]!["complete"]!,
            "bounded 256 rows and 33 pages"
        );
        Console.WriteLine($"T04 control journal: {n} checks passed.");
        return n;
    }
}
