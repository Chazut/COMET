import contextlib, json, sqlite3, unittest
from unittest.mock import patch
import test_archive as fixtures

event = fixtures.event
from analyze import analyze, ANALYZER_VERSION, CATALOG_BY_ID, stale_analyses
from common import backup_analysis, insert_event, dumps
from collector import collect
from app import totals


class EvidenceRegression(unittest.TestCase):
    setUp = fixtures.ArchiveTests.setUp
    tearDown = fixtures.ArchiveTests.tearDown
    add = fixtures.ArchiveTests.add
    finish = fixtures.ArchiveTests.finish
    m = fixtures.ArchiveTests.m

    def test_coverage_survives_anonymous_events_and_summaries(self):
        for feature, last_stage in (("D01", "evaluated"), ("G05", "summary")):
            offset = 0 if feature == "D01" else 10
            trace = {CATALOG_BY_ID[feature]["trace"]: {"episode": 1}}
            self.add(
                event(offset + 1, feature=feature, **trace),
                event(offset + 2, last_stage, feature),
                dict(event(offset + 3, feature=feature), profileId="other"),
            )
        self.finish()
        analyze(self.c, "r")
        campaign = {f["id"]: f for f in totals(self.c, {})["features"]}
        for feature in ("D01", "G05"):
            with self.subTest(feature=feature):
                self.assertEqual(self.m(feature, "p")["coverage"], "identified-attempts")
                self.assertEqual(self.m(feature, "other")["coverage"], "events-only")
                self.assertEqual(self.m(feature)["coverage"], "identified-attempts")
                self.assertEqual(campaign[feature]["identifiedRaids"], 1)
                self.assertEqual(campaign[feature]["started"], 0)
                self.assertEqual(campaign[feature]["completed"], 0)

    def test_legacy_door_requires_an_explicit_request_for_the_same_target_and_door(self):
        def step(n, reason, **fields):
            trace = dict(door="d", target="enemy", requested=True, reason=reason)
            trace.update(fields)
            return event(n, feature="D01", doorApproach=trace)
        self.add(step(1, "door-move-requested"), step(2, "door-native-route-not-observed", state="ended", movementAccepted=True),
                 step(3, "other", state="ended", movementAccepted=True),
                 step(4, "door-move-requested"), step(5, "changed", door="other", moved=True),
                 step(6, "accepted", episode=7, movementAccepted=True), step(7, "arrived", episode=7, moved=True, arrived=True))
        self.finish()
        analyze(self.c, "r")
        d = self.m("D01")
        self.assertEqual((d["requested"], d["accepted"], d["started"], d["completed"], d["unconfirmed"]), (3, 2, 1, 1, 2))

    def test_observed_feature_fields_are_distinct_from_intent(self):
        from analyze import phases, CATALOG_BY_ID
        from evidence import effect_facts
        def facts(f, **fields):
            return phases({"feature": f}, fields, CATALOG_BY_ID[f])
        self.assertIn("started", facts("M07", aligned=True))
        self.assertNotIn("started", facts("M07", requested=True))
        self.assertNotIn("started", facts("W07", accepted=True, actualShots=0))
        self.assertIn("started", facts("W07", actualShots=1))
        self.assertIn("started", facts("M03", openObserved=True))
        self.assertNotIn("completed", facts("L03", observedOff=True))
        self.assertIn("completed", facts("L03", requested=True, observedOff=True))
        self.assertFalse({"requested", "accepted"} & facts("M13", requested=True, accepted=True))
        self.assertIn("requested", facts("M13", moveRequested=True, requested=True))
        self.assertFalse(effect_facts("S04", {}, {"feature": "S01", "reason": "crossfire-angle-reached"}))

    def test_weapon_journal_matches_actor_weapon_time_and_latest_request_once(self):
        from evidence import EpisodeEvidence
        e = EpisodeEvidence()
        actor = dict(session="a", profileId="p", botInstance="b")
        def observe(time, weapon="gun", reason="requested-weapon-observed", **overrides):
            row = {**actor, **overrides, "weaponJournal": {"change": {"to": weapon, "reason": reason}}}
            e.weapon_observation(row, time)
        for key, time in [("first", 1), ("second", 20), ("third", 21), ("unseen", 55)]:
            e.weapon_request(actor, {"to": "gun"}, time, key)
        observe(.98)
        observe(19.9)
        observe(20.98)  # Between two plausible commands; it cannot confirm either.
        observe(21.3)
        observe(22, reason="requested-weapon-ready")
        observe(22.2, reason="requested-weapon-ready")
        observe(56, weapon="another")
        observe(56, botInstance="other")
        observe(56, session="other")
        observe(65.1)
        self.assertEqual(dict(e.weapon_results()), {"first": {"started", "Requested weapon in hands"},
            "third": {"started", "completed", "Requested weapon in hands", "Requested weapon ready"}})

    def test_crossfire_positioning_outcome_is_explicit_and_deduplicated(self):
        for n in range(1, 4):
            self.add(
                event(
                    n,
                    feature="S01",
                    crossfire={
                        "episode": 1,
                        "state": "ended",
                        "outcome": "engaged-from-new-angle",
                    },
                ),
                event(
                    n + 10,
                    feature="S03",
                    crossfire={
                        "episode": 2,
                        "outcome": "engaged-from-new-angle",
                        "maneuver": {"plan": "one", "state": "converging"},
                    },
                ),
            )
        self.add(
            event(
                20,
                feature="S01",
                crossfire={
                    "episode": 3,
                    "state": "ended",
                    "reason": "crossfire-enemy-visible",
                },
            ),
            event(
                21,
                feature="S03",
                crossfire={
                    "episode": 4,
                    "maneuver": {"plan": "one", "state": "converging"},
                },
            ),
            event(
                22,
                feature="C07",
                coverScan={"state": "published", "published": 1, "fallback": "full"},
            ),
        )
        self.finish()
        analyze(self.c, "r")
        self.assertEqual(self.m("S01")["completed"], 1)
        self.assertEqual(self.m("S03")["completed"], 1)
        self.assertEqual(self.m("C07")["completed"], 0)
        self.assertEqual(self.m("C07")["started"], 0)

    def test_fragments_preserve_full_config_and_nested_values(self):
        self.add(
            event(
                1,
                "configuration",
                "U01",
                configRevision=2,
                config={
                    "enabled": True,
                    "recoilRecovery": {"enabled": False},
                    "threatSelection": {"enabled": True, "memory": 12},
                },
            ),
            event(
                2,
                feature="A07",
                configRevision=2,
                config={"enabled": True, "threatSelection": {"enabled": True}},
            ),
        )
        self.finish()
        r = analyze(self.c, "r")
        self.assertEqual(r["configs"]["2"]["threatSelection"]["memory"], 12)
        self.assertEqual(self.m("W09")["configEnabled"], False)
        before = r["configHash"]
        self.add(event(3, feature="A07", configRevision=2, config={"enabled": True}))
        self.assertEqual(analyze(self.c, "r")["configHash"], before)
        # Never carry a previous revision's missing settings into a new revision.
        self.add(
            event(
                4,
                "configuration",
                "U01",
                configRevision=3,
                config={"enabled": True, "recoilRecovery": {"enabled": True}},
            )
        )
        r = analyze(self.c, "r")
        self.assertTrue(self.m("W09")["configChanged"])
        self.assertNotIn("threatSelection", r["configs"]["3"])

    def test_shoreline_arrival_is_not_lost_or_promoted_to_engagement(self):
        for n in range(1, 4):
            self.add(
                event(
                    n,
                    feature="S01",
                    crossfire={
                        "episode": 8,
                        "state": "holding",
                        "reason": "crossfire-angle-reached",
                    },
                ),
                event(
                    n + 10,
                    feature="S03",
                    crossfire={
                        "episode": 4,
                        "state": "holding",
                        "reason": "crossfire-angle-reached",
                        "maneuver": {
                            "plan": "maneuver-5",
                            "role": "flank",
                            "state": "active",
                        },
                    },
                ),
                dict(
                    event(
                        n + 20,
                        feature="S03",
                        crossfire={
                            "episode": 4,
                            "state": "holding",
                            "reason": "crossfire-angle-reached",
                            "maneuver": {
                                "plan": "maneuver-5",
                                "role": "support",
                                "state": "active",
                            },
                        },
                    ),
                    profileId="support",
                ),
            )
        self.add(
            event(
                40,
                feature="S01",
                crossfire={
                    "episode": 8,
                    "state": "ended",
                    "reason": "crossfire-hold-timeout",
                },
            ),
            event(
                41,
                feature="S03",
                crossfire={
                    "episode": 4,
                    "state": "ended",
                    "reason": "crossfire-out-of-scope",
                    "maneuver": {
                        "plan": "maneuver-5",
                        "role": "flank",
                        "state": "ended",
                    },
                },
            ),
            event(
                42,
                feature="S01",
                crossfire={
                    "episode": 9,
                    "state": "ready",
                    "reason": "crossfire-angle-ready",
                },
            ),
            event(
                43,
                feature="S04",
                crossfire={
                    "episode": 10,
                    "state": "holding",
                    "reason": "crossfire-angle-reached",
                },
            ),
            event(
                44,
                feature="S03",
                crossfire={
                    "episode": 11,
                    "outcome": "engaged-from-new-angle",
                    "maneuver": {
                        "plan": "support-context",
                        "role": "support",
                        "state": "converging",
                    },
                },
            ),
        )
        self.finish()
        analyze(self.c, "r")
        for feature in ("S01", "S03"):
            m = self.m(feature)
            self.assertEqual(m["effects"]["Position reached"], 1)
            self.assertEqual(m["effects"]["Angle held"], 1)
            self.assertEqual(m["effects"].get("Enemy engaged from new angle", 0), 0)
            self.assertEqual(m["completed"], 0)
        # A later observed engagement is a separate milestone, deduplicated.
        for n in (50, 51):
            self.add(
                event(
                    n,
                    feature="S01",
                    crossfire={
                        "episode": 8,
                        "state": "ended",
                        "outcome": "engaged-from-new-angle",
                    },
                )
            )
        analyze(self.c, "r")
        self.assertEqual(self.m("S01")["effects"]["Enemy engaged from new angle"], 1)
        self.assertEqual(self.m("S01")["effects"]["Position reached"], 1)

    def test_arrival_without_identity_is_only_a_snapshot(self):
        for n in (1, 2):
            self.add(
                event(
                    n,
                    feature="S01",
                    crossfire={"state": "holding", "reason": "crossfire-angle-reached"},
                )
            )
        self.finish()
        analyze(self.c, "r")
        self.assertEqual(self.m("S01")["observationFacts"]["Position reached"], 2)
        self.assertEqual(self.m("S01")["effects"].get("Position reached", 0), 0)

    def test_effects_deduplicate_only_with_identity(self):
        for n in range(1, 5):
            self.add(
                event(
                    n,
                    feature="M04",
                    combatLean={
                        "episode": 1,
                        "tiltObserved": True,
                        "animatorApplied": True,
                    },
                ),
                event(n + 10, feature="P02", quiet={"applied": True}),
                event(n + 20, feature="H01", medicalChoice={"started": True}),
            )
        self.finish()
        analyze(self.c, "r")
        self.assertEqual(self.m("M04")["started"], 1)
        self.assertEqual(self.m("M04")["effects"]["Tilt observed"], 1)
        self.assertEqual(self.m("P02")["observationFacts"]["Speed cap applied"], 4)
        self.assertEqual(self.m("P02")["started"], 0)
        self.assertEqual(self.m("H01")["observationFacts"]["started"], 4)
        self.assertEqual(self.m("H01")["started"], 0)

    def test_quick_reload_and_cleanup(self):
        self.add(
            event(1, feature="W04", quickReload={"episode": 1, "started": True}),
            event(2, feature="W04", quickReload={"episode": 1, "state": "completed"}),
            event(3, feature="C13", raidCleanup={"state": "completed"}),
            event(4, feature="C13", raidCleanup={"state": "completed"}),
        )
        self.finish()
        analyze(self.c, "r")
        self.assertEqual((self.m("W04")["started"], self.m("W04")["completed"]), (1, 1))
        self.assertEqual(self.m("C13")["completed"], 1)

    def test_old_analysis_recomputed_without_source_files_and_backed_up(self):
        self.add(event(1, feature="W04", quickReload={"episode": 1, "started": True}))
        self.finish()
        self.c.execute(
            "UPDATE raids SET report=? WHERE id=?", (dumps({"analyzerVersion": 1}), "r")
        )
        self.c.commit()
        with patch("collector.game_running", return_value=False), patch(
            "collector.active_raid", return_value=False
        ):
            result = collect(self.settings)
        self.assertEqual(result["errors"], 0)
        self.assertNotIn("r", stale_analyses(self.c))
        self.assertEqual(self.m("W04")["started"], 1)
        backup = (
            self.archive
            / "analysis-backups"
            / ("before-" + ANALYZER_VERSION + ".sqlite")
        )
        with contextlib.closing(sqlite3.connect(backup)) as c:
            self.assertEqual(
                json.loads(
                    c.execute("SELECT report FROM raids WHERE id='r'").fetchone()[0]
                )["analyzerVersion"],
                1,
            )
        modified = backup.stat().st_mtime_ns
        backup_analysis(self.c, self.archive, ANALYZER_VERSION)
        self.assertEqual(backup.stat().st_mtime_ns, modified)

    def test_denominator_excludes_menu_and_rr_only(self):
        self.add(event(1, feature="W04", quickReload={"episode": 1, "started": True}))
        self.finish()
        analyze(self.c, "r")
        self.c.execute(
            "INSERT INTO raids VALUES(?,?,?,?)",
            ("menu", "now", "{}", dumps({"sources": {"log": 10}, "actors": {}})),
        )
        self.c.commit()
        result = totals(self.c, {})
        self.assertEqual(result["archiveEntries"], 2)
        self.assertEqual(result["recordedRaids"], 1)
        f = next(f for f in result["features"] if f["id"] == "W04")
        self.assertEqual(f["started"], 1)

    def test_future_feature_does_not_break_overview(self):
        self.add(event(1, feature="W04", quickReload={"episode": 1, "started": True}))
        self.finish()
        analyze(self.c, "r")
        self.c.execute(
            "INSERT INTO features VALUES(?,?,?,?)",
            ("r", "NEW01", "", dumps({"events": 2, "effects": {"Observed action": 1}})),
        )
        self.c.commit()
        f = next(f for f in totals(self.c, {})["features"] if f["id"] == "NEW01")
        self.assertEqual(f["events"], 2)
        self.assertEqual(f["effects"]["Observed action"], 1)
        self.assertEqual(f["unknownConfigRaids"], 1)


if __name__ == "__main__":
    unittest.main()
