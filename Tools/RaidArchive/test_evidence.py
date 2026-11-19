import contextlib, json, sqlite3, unittest
from unittest.mock import patch
import test_archive as fixtures

event = fixtures.event
from analyze import analyze, ANALYZER_VERSION, stale_analyses
from common import backup_analysis, insert_event, dumps
from collector import collect
from app import totals


class EvidenceRegression(unittest.TestCase):
    setUp = fixtures.ArchiveTests.setUp
    tearDown = fixtures.ArchiveTests.tearDown
    add = fixtures.ArchiveTests.add
    finish = fixtures.ArchiveTests.finish
    m = fixtures.ArchiveTests.m

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
