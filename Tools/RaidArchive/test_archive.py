import contextlib, gzip, json, pathlib, sqlite3, tempfile, threading, time, unittest, urllib.request, urllib.error
from unittest.mock import patch
from common import *
from analyze import analyze, identity, phases, CATALOG
from collector import (
    collect,
    sessions,
    parse_diagnostic,
    capture_rr,
    ensure,
    performance_report,
)
from app import State, Handler, query_settings
import http.server

SESSION = "a" * 32


def event(seq, stage="evaluated", feature="A01", **more):
    return dict(
        schema=1,
        session=SESSION,
        sequence=seq,
        feature=feature,
        stage=stage,
        profileId="p",
        botInstance="instance",
        name="Tester",
        realtime=10 + seq,
        **more,
    )


class ArchiveTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = pathlib.Path(self.tmp.name)
        self.archive = self.root / "archive"
        self.game = self.root / "game"
        self.game.mkdir()
        self.diag = self.game / "BepInEx/COMET/diagnostics"
        self.diag.mkdir(parents=True)
        self.path = self.diag / f"20261101-120000-{SESSION}.jsonl"
        self.settings = {
            "game": str(self.game),
            "archive": str(self.archive),
            "settleSeconds": 0,
            "minimumFreeBytes": 0,
            "port": 8791,
            "intervalSeconds": 30,
            "rrUrl": "http://127.0.0.1:7829",
        }
        self.c = connect(self.archive)
        ensure(self.c, "r", {"session": SESSION})
        self.c.commit()

    def tearDown(self):
        self.c.close()
        self.tmp.cleanup()

    def add(self, *events):
        for i, e in enumerate(events):
            insert_event(self.c, "r", e, "local", i + 1)
        self.c.commit()

    def finish(self):
        self.add(
            {
                "schema": 1,
                "session": SESSION,
                "stage": "diagnostics-end",
                "queueDropped": 0,
                "fileLimitDropped": 0,
            }
        )

    def m(self, f="A01", profile=""):
        return json.loads(
            self.c.execute(
                "SELECT metrics FROM features WHERE raid=? AND feature=? AND profile=?",
                ("r", f, profile),
            ).fetchone()[0]
        )

    def choice(self, seq, stage, op=1, **kwargs):
        return event(
            seq,
            stage,
            decision="Hold",
            mixing={"opportunity": op, "encounter": 1},
            candidates=[{"decision": "Hold", "eligible": True, "score": 50}],
            **kwargs,
        )

    def test_accepted_is_not_started(self):
        self.add(
            self.choice(1, "selected"),
            self.choice(2, "accepted"),
            self.choice(3, "released"),
        )
        self.finish()
        analyze(self.c, "r")
        self.assertEqual(
            (self.m()["selected"], self.m()["started"], self.m()["unconfirmed"]),
            (1, 0, 1),
        )

    def test_selection_alone_is_not_a_command(self):
        self.add(self.choice(1, "selected"))
        self.finish()
        analyze(self.c, "r")
        self.assertEqual(
            (self.m()["selected"], self.m()["requested"], self.m()["unconfirmed"]),
            (1, 0, 0),
        )

    def test_reported_completion_is_not_flagged_as_unconfirmed(self):
        self.add(
            event(1, feature="W04", quickReload={"episode": 1, "requested": True}),
            event(2, feature="W04", quickReload={"episode": 1, "state": "completed"}),
        )
        self.finish()
        analyze(self.c, "r")
        self.assertEqual(self.m("W04")["unconfirmed"], 0)

    def test_duplicate_snapshots_are_one_attempt(self):
        self.add(
            *[self.choice(i, "accepted") for i in range(1, 9)],
            self.choice(9, "started"),
        )
        self.finish()
        analyze(self.c, "r")
        self.assertEqual(self.m()["accepted"], 1)
        self.assertEqual(self.m()["started"], 1)
        self.assertEqual(self.m()["completed"], 0)

    def test_belt_reload_resources_are_preserved_but_not_execution(self):
        trace = dict(
            episode=1,
            state="assess",
            resourcesChecked=True,
            magazineCandidates=3,
            beltMagazines=3,
            resource="magazine",
            reserve=20,
            resourceLocation="ArmBand",
            resourceItem="GEN1 20",
            rejectedMagazines=0,
            resourceRejection="",
        )
        self.add(event(1, feature="W01", reloadTiming=trace))
        self.finish()
        analyze(self.c, "r")
        payload = unpack(
            self.c.execute("SELECT payload FROM events WHERE feature='W01'").fetchone()[
                0
            ]
        )
        self.assertEqual(payload["reloadTiming"], trace)
        self.assertEqual((self.m("W01")["started"], self.m("W01")["completed"]), (0, 0))
        self.add(
            event(
                2,
                feature="W01",
                reloadTiming=dict(
                    trace,
                    state="reloading",
                    requested=True,
                    accepted=True,
                    started=True,
                ),
            )
        )
        analyze(self.c, "r")
        self.assertEqual((self.m("W01")["started"], self.m("W01")["completed"]), (1, 0))
        self.add(
            event(
                3,
                feature="W01",
                reloadTiming=dict(
                    trace,
                    state="ended",
                    started=True,
                    finished=True,
                    improved=True,
                    after=20,
                ),
            )
        )
        analyze(self.c, "r")
        self.assertEqual((self.m("W01")["started"], self.m("W01")["completed"]), (1, 1))

    def test_reload_finish_without_ammo_gain_is_not_success(self):
        self.add(
            event(
                1,
                feature="W01",
                reloadTiming=dict(
                    episode=1,
                    state="ended",
                    started=True,
                    finished=True,
                    improved=False,
                ),
            )
        )
        self.finish()
        analyze(self.c, "r")
        self.assertEqual((self.m("W01")["started"], self.m("W01")["completed"]), (1, 0))

    def test_reborn_bot_does_not_share_identity(self):
        a = self.choice(1, "started")
        b = self.choice(2, "started")
        b["botInstance"] = "new"
        self.add(a, b)
        self.finish()
        analyze(self.c, "r")
        self.assertEqual(self.m()["started"], 2)

    def test_missing_identity_not_counted_as_trial(self):
        self.add(event(1, "accepted"))
        self.finish()
        analyze(self.c, "r")
        self.assertEqual(self.m()["opportunities"], 0)
        self.assertEqual(self.m()["unknownIdentityEvents"], 1)

    def test_explicit_complete_and_observed_motion(self):
        self.add(
            event(
                1,
                feature="M01",
                combatStep={"episode": 1, "state": "moving", "movementObserved": True},
            ),
            event(2, feature="M01", combatStep={"episode": 1, "state": "completed"}),
        )
        self.finish()
        analyze(self.c, "r")
        self.assertEqual(self.m("M01")["started"], 1)
        self.assertEqual(self.m("M01")["completed"], 1)

    def test_applied_does_not_prove_motion(self):
        self.add(
            event(
                1,
                feature="M01",
                combatStep={"episode": 1, "state": "moving", "applied": True},
            )
        )
        self.finish()
        analyze(self.c, "r")
        self.assertEqual(self.m("M01")["started"], 0)

    def test_squad_eligibility_diagnostics_are_not_tactical_execution(self):
        self.add(
            event(1, "eligibility", "S03", reason="crossfire-native-priority"),
            event(2, "eligibility", "S03", reason="squad-tactical-route-handoff"),
        )
        self.finish()
        analyze(self.c, "r")
        m = self.m("S03")
        self.assertEqual(m["events"], 2)
        for key in (
            "opportunities",
            "selected",
            "requested",
            "started",
            "completed",
            "unconfirmed",
        ):
            self.assertEqual(m[key], 0, key)

    def test_attached_context_does_not_inherit_parent_start(self):
        self.add(self.choice(1, "started", opportunity={"signal": 1}))
        self.finish()
        analyze(self.c, "r")
        self.assertEqual(self.m("A01")["started"], 1)
        self.assertEqual(self.m("A10")["started"], 0)

    def test_state_ended_is_not_failure(self):
        self.add(
            event(
                1,
                feature="M01",
                combatStep={"episode": 1, "state": "ended", "reason": "combat-ended"},
            )
        )
        self.finish()
        analyze(self.c, "r")
        self.assertEqual(self.m("M01")["interrupted"], 0)

    def test_log_loss_and_missing_footer(self):
        self.add(self.choice(1, "selected"))
        self.assertTrue(analyze(self.c, "r")["partial"])
        self.add({"stage": "diagnostics-end", "fileLimitDropped": 4})
        self.assertTrue(analyze(self.c, "r")["partial"])

    def test_disabled_never_called_feature(self):
        self.add(
            {
                "stage": "configuration",
                "feature": "U01",
                "configRevision": 1,
                "config": {"enabled": True, "recoilRecovery": {"enabled": False}},
            }
        )
        self.finish()
        analyze(self.c, "r")
        self.assertEqual(self.m("W09")["coverage"], "disabled")
        self.assertEqual(self.m("D02")["coverage"], "no-evidence")

    def test_catalog_has_legacy_and_recent_cards_without_duplicates(self):
        self.finish()
        analyze(self.c, "r")
        ids = {card["id"] for card in CATALOG}
        self.assertEqual(len(ids), len(CATALOG))
        self.assertTrue({"S10", "S11", "W11", "C15", "C16", "C17", "A14", "S12", "L05"} <= ids)
        self.assertTrue({"A01", "D01", "G01", "S01", "W04", "U01"} <= ids)
        self.assertEqual(self.m("C11")["coverage"], "excluded")
        self.assertEqual(self.m("E02")["coverage"], "deferred")

    def test_discarded_magazine_cycles_and_original_item_confirmation(self):
        def rec(n, stage, reason="", magazine="mag"):
            return event(n, stage, "W04", decision="RecoverMagazine", reason=reason,
                         magazineRecovery={"magazine": magazine})
        self.add(event(1, "evaluated", "W04", decision="QuickReload"),
                 rec(2, "queued"), rec(3, "started"), rec(4, "interrupted"), rec(5, "started"),
                 rec(6, "arrived"), rec(7, "pickup-ready"),
                 rec(8, "completed", "original magazine confirmed in inventory"),
                 rec(9, "queued"), rec(10, "started"), rec(11, "failed"),
                 rec(12, "queued", magazine="other"), rec(13, "completed", "callback only", "other"),
                 rec(14, "started", magazine=None))
        report = analyze(self.c, "r")
        m = self.m("W11")
        self.assertEqual((m["opportunities"], m["started"], m["completed"], m["refused"]), (3, 2, 1, 1))
        self.assertEqual(m["effects"]["Reached magazine"], 1)
        self.assertEqual(m["observationFacts"]["started"], 1)
        self.assertEqual(self.m("W04")["events"], 1)
        self.assertEqual(self.c.execute("SELECT count(*) FROM event_features WHERE feature='W11'").fetchone()[0], 13)
        self.assertEqual(analyze(self.c, "r")["attempts"], report["attempts"])
        self.assertEqual(self.m("W11"), m)

    def test_squad_checks_roles_and_acceptance(self):
        siege = {"target": "contact", "exit": "door", "until": 40.5}
        pair = {"holder": "h", "thrower": "p", "contact": "c", "startFrame": 50}
        self.add(event(1, "eligibility", "S11", reason="siege-unverified-exits"),
                 event(2, "selected", "S11", siege=siege), event(3, "accepted", "S11", siege=siege),
                 event(4, "released", "S11", siege=siege, reason="siege-watch-expired"),
                 event(5, "selected", "S10", squadGrenade=pair),
                 event(6, "accepted", "S10", squadGrenade=pair),
                 dict(event(7, "selected", "S10", squadGrenade=pair), profileId="h"),
                 event(8, "accepted", "A13", squadGrenade=pair, siege=siege),
                 event(9, "selected", "S11"))
        analyze(self.c, "r")
        for f, selected, events in (("S11", 1, 5), ("S10", 2, 3)):
            m = self.m(f)
            self.assertEqual((m["events"], m["selected"], m["accepted"], m["started"], m["completed"]),
                             (events, selected, 1, 0, 0))
        self.assertEqual(self.m("S11")["observationFacts"]["Eligibility check"], 1)
        self.assertEqual(self.m("S11")["released"], 1)

    def test_recent_observation_channels_and_repeated_counters(self):
        self.add(event(1, "evaluated", "C07", decision="CoverExposure", reason="hit-below-native-spotted-threshold",
                       coverExposure={"hits": 12, "released": 9}),
                 event(2, "evaluated", "C07", decision="CoverExposure", reason="hit-cover-released",
                       coverExposure={"foliage": True, "released": 10}),
                 event(3, "evaluated", "C07", decision="SeasonalCover", reason="seasonal-foliage-is-not-cover",
                       seasonalCover={"rejected": 93}), event(4, "evaluated", "C07", decision="CoverScan"),
                 event(5, "evaluated", "A02", enemyRole={"role": "Guard", "groupCue": "Guard", "expectedSupport": .75}),
                 event(6, "evaluated", "A01", enemyRole={"role": "Scav", "groupCue": "Unknown"}),
                 event(7, "eligibility", "S06", reason="storm-expected-support"),
                 event(8, "summary", "local-exposure", shadow=12, aiShadow=4, recentSight=6),
                 event(9, "summary", "local-exposure", shadow=3, aiShadow=1),
                 event(10, "evaluated", "local-exposure", reason="Shadow"),
                 event(11, "native-fix-summary", "C11", labels=["DoorCoverRejected"], counts=[5]),
                 event(12, "native-fix-summary", "C11", labels=["DoorCoverRejected"], counts=[7]),
                 event(13, "native-fix-summary", "C11", labels=["DoorCoverRejected"], counts=[7]))
        analyze(self.c, "r")
        self.assertEqual(self.m("C07")["events"], 1)
        self.assertEqual(self.m("A14")["events"], 2)
        self.assertEqual(self.m("S12")["events"], 1)
        self.assertEqual(self.m("C15")["observationFacts"]["Foliage cover rejected"], 1)
        self.assertEqual(self.m("C16")["observationFacts"]["Exposed foliage cover released"], 1)
        self.assertEqual(self.m("L05")["observationFacts"], {"Shadow checks": 15, "AI shadow checks": 5,
                         "Recent contact checks": 6, "Sampled decision: Shadow": 1})
        self.assertEqual(self.m("C17")["observationFacts"]["Native fix: DoorCoverRejected"], 7)
        self.assertEqual(self.m("L05")["started"], 0)

    def test_rr_duplicate_local_preferred(self):
        e = self.choice(1, "accepted")
        insert_event(self.c, "r", dict(e, reason="rr"), "rr:x", 1)
        insert_event(self.c, "r", dict(e, reason="local"), "blob:y", 2)
        rows = self.c.execute("SELECT payload FROM events").fetchall()
        self.assertEqual(len(rows), 1)
        self.assertEqual(unpack(rows[0][0])["reason"], "local")

    def test_rr_import_does_not_overwrite_local(self):
        self.add(self.choice(1, "started"))
        self.assertFalse(
            insert_event(self.c, "r", self.choice(1, "accepted"), "rr:x", 1)
        )

    def test_rr_clock_survives_both_import_orders(self):
        for seq, reverse in ((1, False), (2, True)):
            local = self.choice(seq, "started")
            rr = dict(local, reason="rr", _rrTimeMs=1234, _rrRaidId="rrid")
            pairs = [(local, "blob:x"), (rr, "rr:rrid")]
            if reverse:
                pairs.reverse()
            for e, source in pairs:
                insert_event(self.c, "r", e, source, 1)
            row = self.c.execute(
                "SELECT * FROM events WHERE event_key=?", (SESSION + ":" + str(seq),)
            ).fetchone()
            self.assertEqual(unpack(row["payload"])["_rrTimeMs"], 1234)
            self.assertEqual(row["reason"], "")
            self.assertEqual(row["stage"], "started")

    def test_unsupported_schema_preserved_but_not_interpreted(self):
        e = self.choice(1, "started")
        e["schema"] = 99
        self.add(e)
        self.finish()
        r = analyze(self.c, "r")
        self.assertEqual(r["unsupportedSchemas"], 1)
        self.assertTrue(r["partial"])
        self.assertEqual(self.m()["started"], 0)

    def test_config_enabled_then_disabled_is_mixed(self):
        for revision, enabled in ((1, True), (2, False)):
            self.add(
                {
                    "stage": "configuration",
                    "configRevision": revision,
                    "config": {"recoilRecovery": {"enabled": enabled}},
                }
            )
        self.finish()
        analyze(self.c, "r")
        self.assertTrue(self.m("W09")["configChanged"])
        self.assertIsNone(self.m("W09")["configEnabled"])

    def test_missing_segment_is_partial(self):
        self.path.write_text(dumps(self.choice(1, "started")) + "\n", encoding="utf-8")
        last = self.path.with_name(self.path.stem + ".part0002.jsonl")
        last.write_text(
            dumps(
                {
                    "schema": 1,
                    "session": SESSION,
                    "stage": "diagnostics-end",
                    "segments": 3,
                }
            )
            + "\n",
            encoding="utf-8",
        )
        with patch("collector.game_running", return_value=False):
            collect(self.settings)
        row = self.c.execute(
            "SELECT metadata,report FROM raids WHERE id=?", ("comet-" + SESSION,)
        ).fetchone()
        self.assertEqual(json.loads(row[0])["missingSegments"], [1])
        self.assertTrue(json.loads(row[1])["partial"])

    def test_no_identity_preserves_observations_not_attempts(self):
        self.add(
            event(1, feature="D02", doorJump={"takeoffObserved": True}),
            event(2, feature="D02", doorJump={"takeoffObserved": True}),
        )
        self.finish()
        analyze(self.c, "r")
        spec = next(s for s in CATALOG if s["id"] == "D02")
        # Use the actual registered root, independent of emitter naming.
        self.c.execute("DELETE FROM events WHERE feature='D02'")
        self.add(event(3, feature="D02", **{spec["trace"]: {"takeoffObserved": True}}))
        analyze(self.c, "r")
        self.assertEqual(self.m("D02")["started"], 0)
        self.assertEqual(self.m("D02")["observationFacts"]["started"], 1)

    def test_background_watcher_collects_after_raid_closed(self):
        self.path.write_text(dumps(self.choice(1, "selected")) + "\n", encoding="utf-8")
        with patch("collector.game_running", return_value=True):
            state = State(self.settings)
            state.start()
            try:
                deadline = time.monotonic() + 3
                while (
                    state.status.get("state") != "waiting-for-raid-end"
                    and time.monotonic() < deadline
                ):
                    time.sleep(0.02)
                self.assertEqual(state.status["state"], "waiting-for-raid-end")
                with self.path.open("a", encoding="utf-8") as f:
                    f.write(
                        dumps(
                            {
                                "schema": 1,
                                "session": SESSION,
                                "stage": "diagnostics-end",
                            }
                        )
                        + "\n"
                    )
                # Windows filesystem timestamps can be slightly ahead of time.time().
                # This test concerns automatic collection, not the settling delay.
                closed = time.time() - 1
                os.utime(self.path, (closed, closed))
                state.wake.set()
                deadline = time.monotonic() + 4
                while (
                    state.status.get("state") != "ready" and time.monotonic() < deadline
                ):
                    time.sleep(0.02)
                self.assertEqual(state.status["state"], "ready")
                self.assertTrue(
                    (
                        self.archive / "raids" / ("comet-" + SESSION) / "report.json"
                    ).is_file()
                )
            finally:
                state.stop.set()
                state.wake.set()
                state.thread.join(3)

    def test_random_expected_sum_uses_real_draws(self):
        for i, p in enumerate((0.1, 0.7)):
            e = self.choice(i + 1, "selected", i + 1)
            e["mixing"].update(
                roll=0.2,
                selected="Hold",
                options=[
                    {"decision": "Hold", "probability": p},
                    {"decision": "Search", "probability": 1 - p},
                ],
            )
            self.add(e)
        self.finish()
        r = analyze(self.c, "r")
        hold = next(x for x in r["draws"] if x["action"] == "Hold")
        self.assertAlmostEqual(hold["expected"], 0.8)
        self.assertEqual(hold["observed"], 2)

    def test_visible_and_hidden_probability_streams_do_not_collide(self):
        a = self.choice(1, "selected")
        b = self.choice(2, "selected")
        b["feature"] = "A02"
        for e in (a, b):
            e["mixing"].update(
                roll=0.2,
                selected="Hold",
                options=[{"decision": "Hold", "probability": 0.5}],
            )
        self.add(a, b)
        self.finish()
        report = analyze(self.c, "r")
        self.assertEqual(
            next(x for x in report["draws"] if x["feature"] == "A04")["trials"], 2
        )

    def test_summary_is_evidence_not_execution(self):
        self.add(event(1, "summary", feature="T02"))
        self.finish()
        analyze(self.c, "r")
        self.assertEqual(self.m("T02")["events"], 1)
        self.assertEqual(self.m("T02")["started"], 0)

    def test_tactical_probe_counters_are_retained_without_inventing_actions(self):
        self.add(
            event(
                1,
                "summary",
                kind="tactical-probes",
                probes={"clearanceRetries": 12, "deferred": 24},
                indexedDoorLinks=80,
                doorIndexComplete=True,
            )
        )
        self.finish()
        report = analyze(self.c, "r")
        summary = next(
            x for x in report["summaries"] if x.get("kind") == "tactical-probes"
        )
        self.assertEqual(summary["probes"]["clearanceRetries"], 12)
        self.assertEqual(summary["probes"]["deferred"], 24)
        self.assertEqual(report["attempts"], 0)
        self.assertEqual(self.m("D01")["started"], 0)

    def test_blob_roundtrip_and_integrity(self):
        p = self.root / "original"
        p.write_bytes(b"abc\x00" * 100)
        sha, n = store_file(self.archive, p)
        with read_blob(self.archive, sha) as f:
            self.assertEqual(f.read(), p.read_bytes())
        self.assertEqual(digest(p), sha)
        self.assertEqual(n, 400)
        self.assertEqual(store_file(self.archive, p)[0], sha)

    def test_path_traversal_rejected(self):
        with self.assertRaises(ValueError):
            read_blob(self.archive, "../../secret")

    def test_malformed_lines_preserved(self):
        self.path.write_text(
            json.dumps(self.choice(1, "selected")) + "\nnot json\n", encoding="utf-8"
        )
        parse_diagnostic(self.c, self.archive, "r", self.path)
        r = analyze(self.c, "r")
        self.assertEqual(r["malformed"], 1)
        self.assertTrue(r["partial"])

    def test_source_changes_during_copy_retried(self):
        p = self.root / "source"
        p.write_text("x")
        with patch("common.fingerprint", side_effect=["1", "2"]):
            with self.assertRaises(RuntimeError):
                store_file(self.archive, p)
        self.assertEqual(list((self.archive / "blobs").glob("*.pending")), [])

    def test_split_files_grouped_and_idempotent(self):
        self.path.write_text(json.dumps(self.choice(1, "selected")) + "\n")
        part = self.path.with_name(self.path.stem + ".part0001.jsonl")
        part.write_text(
            json.dumps(self.choice(2, "started"))
            + "\n"
            + json.dumps({"stage": "diagnostics-end", "segments": 2})
            + "\n"
        )
        self.assertEqual(len(sessions(self.game)[SESSION]), 2)
        with patch("collector.game_running", return_value=False):
            collect(self.settings)
            collect(self.settings)
        c = connect(self.archive)
        try:
            self.assertEqual(
                c.execute(
                    "SELECT count(*) FROM events WHERE raid=?", ("comet-" + SESSION,)
                ).fetchone()[0],
                3,
            )
        finally:
            c.close()

    def test_active_raid_is_not_imported(self):
        self.path.write_text(json.dumps(self.choice(1, "selected")) + "\n")
        with patch("collector.game_running", return_value=True):
            r = collect(self.settings)
        self.assertEqual(r["state"], "waiting-for-raid-end")

    def test_crash_imported_as_partial(self):
        self.path.write_text(json.dumps(self.choice(1, "selected")) + "\n")
        # Keep the crash log settled even when filesystem timestamp precision is
        # slightly ahead of time.time() on Windows.
        settled = time.time() - 2
        os.utime(self.path, (settled, settled))
        with patch("collector.game_running", return_value=False):
            collect(self.settings)
        r = self.c.execute(
            "SELECT report FROM raids WHERE id=?", ("comet-" + SESSION,)
        ).fetchone()
        self.assertTrue(json.loads(r[0])["partial"])

    def test_rr_wal_snapshot_and_exact_mapping(self):
        folder = self.game / "SPT_Runtime/user/mods/RaidReview/data"
        folder.mkdir(parents=True)
        rr = sqlite3.connect(folder / "raid_review_mod.db")
        rr.execute("PRAGMA journal_mode=WAL")
        rr.executescript(
            "CREATE TABLE raid(id INTEGER,raidId TEXT,location TEXT,time TEXT,exitStatus TEXT);CREATE TABLE comet_decision(id INTEGER,raidId TEXT,cometSession TEXT,sequence INTEGER,time INTEGER,detail TEXT);"
        )
        rr.execute(
            "INSERT INTO raid VALUES(1,?,?,?,?)",
            ("raid-1", "Woods", "2026-11-01T12:00:00Z", "Survived"),
        )
        rr.execute(
            "INSERT INTO comet_decision VALUES(1,?,?,1,1000,?)",
            ("raid-1", SESSION, json.dumps(self.choice(1, "started"))),
        )
        rr.commit()
        self.path.write_text(
            json.dumps({"schema": 1, "session": SESSION, "stage": "diagnostics-end"})
            + "\n"
        )
        try:
            with patch("collector.game_running", return_value=False):
                collect(self.settings)
            m = json.loads(
                self.c.execute(
                    "SELECT metadata FROM raids WHERE id=?", ("comet-" + SESSION,)
                ).fetchone()[0]
            )
            self.assertEqual(m["rrId"], "raid-1")
            self.assertEqual(
                self.c.execute(
                    "SELECT count(*) FROM events WHERE raid=? AND feature=?",
                    ("comet-" + SESSION, "A01"),
                ).fetchone()[0],
                1,
            )
        finally:
            rr.close()

    def test_reanalysis_is_idempotent(self):
        self.add(
            self.choice(1, "selected"),
            self.choice(2, "accepted"),
            self.choice(3, "released"),
        )
        self.finish()
        a = analyze(self.c, "r")
        b = analyze(self.c, "r")
        self.assertEqual(a["alerts"], b["alerts"])
        self.assertEqual(a["attempts"], b["attempts"])

    def test_http_local_host_and_csrf(self):
        state = State(self.settings)
        server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        server.state = state
        t = threading.Thread(target=server.serve_forever, daemon=True)
        t.start()
        url = f"http://127.0.0.1:{server.server_port}"
        try:
            self.assertEqual(
                json.load(urllib.request.urlopen(url + "/api/status"))["state"],
                "starting",
            )
            with self.assertRaises(urllib.error.HTTPError) as err:
                urllib.request.urlopen(
                    urllib.request.Request(url + "/api/collect", method="POST")
                )
            self.assertEqual(err.exception.code, 403)
            with self.assertRaises(urllib.error.HTTPError):
                urllib.request.urlopen(
                    urllib.request.Request(
                        url + "/api/status", headers={"Host": "evil.example"}
                    )
                )
        finally:
            server.shutdown()
            server.server_close()
            t.join()


class NullMapRegression(unittest.TestCase):
    def test_null_location_uses_header(self):
        from pathlib import Path
        import ast

        tree = ast.parse(Path("collector.py").read_text(encoding="utf-8"))
        expr = next(
            n
            for n in ast.walk(tree)
            if isinstance(n, ast.Dict)
            and any(
                isinstance(k, ast.Constant) and k.value == "missingSegments"
                for k in n.keys
            )
        )
        value = expr.values[
            next(
                i
                for i, k in enumerate(expr.keys)
                if isinstance(k, ast.Constant) and k.value == "map"
            )
        ]
        for rr, header, expected in [
            ({"location": None}, {"map": "Sandbox"}, "Sandbox"),
            ({"location": None}, {"map": None}, "Unknown"),
            ({"location": "woods"}, {}, "woods"),
        ]:
            self.assertEqual(
                eval(
                    compile(ast.Expression(value), "map", "eval"),
                    {"rrmeta": rr, "header": header},
                ),
                expected,
            )


if __name__ == "__main__":
    unittest.main(verbosity=2)
