"""Regenerate the committed coverage registry from the local feature cards/source.
Not required on the player's machine. Inspect the generated diff when adding cards.
"""

import pathlib, re, json
from recent_features import CARDS

ROOT = pathlib.Path(__file__).resolve().parents[2]

TRACE = {
    "A01": "",
    "A02": "visible",
    "A03": "continuity",
    "A04": "mixing",
    "A05": "mixing.mistake",
    "A06": "fear",
    "A07": "threat",
    "A08": "loadout",
    "A09": "opponent",
    "A10": "opportunity",
    "A11": "coverResponse",
    "A12": "progress",
    "A13": "handoff",
    "P01": "hearing",
    "P02": "quiet",
    "P03": "gunfire",
    "P04": "floorHearing",
    "P05": "closeDetection",
    "P07": "impact",
    "P08": "silentSearch",
    "P09": "searchAbandonment",
    "P10": "environmentContext",
    "D01": "doorApproach",
    "D02": "doorJumpPeek",
    "D03": "doorRunBy",
    "D04": "doorStepPeek",
    "D05": "doorFakeGrenade",
    "D06": "doorFakeMedical",
    "D07": "doorRoomClear",
    "D08": "doorSearchClear",
    "D09": "doorAmbush",
    "D10": "doorHold",
    "D11": "doorSupport",
    "D12": "doorInterruption",
    "D13": "doorEmergency",
    "D14": "doorReservation",
    "D15": "doorEntry",
    "M01": "combatStep",
    "M02": "firePause",
    "M03": "cornerPeek",
    "M04": "combatLean",
    "M05": "cornerChase",
    "M06": "cornerPrefire",
    "M07": "angleWatch",
    "M08": "holdAngle",
    "M09": "coverTravel",
    "M10": "adsTravel",
    "M11": "retreatWeave",
    "M12": "tacticalJump",
    "M13": "outdoorRelocation",
    "M14": "coverFeint",
    "W01": "reloadTiming",
    "W02": "pistolSwap",
    "W03": "weaponReturn",
    "W04": "quickReload",
    "W05": "weaponRange",
    "W06": "weaponSync",
    "W07": "suppressionDiscipline",
    "W08": "shotGeometry",
    "W09": "recoilRecovery",
    "W10": "handsRecovery",
    "G01": "grenadeJudgment",
    "G02": "grenadeTrajectory",
    "G03": "grenadeChoice",
    "G04": "ownGrenadeSafety",
    "G05": "grenadeReaction",
    "G06": "grenadeDispersion",
    "G07": "grenadeFilter",
    "G08": "gasExposure",
    "G09": "flashContinuity",
    "G10": "explosionAwareness",
    "H01": "medicalChoice",
    "H02": "surgeryInterruption",
    "H03": "medicalPosture",
    "H04": "postCombat",
    "H05": "readyStance",
    "S01": "crossfire",
    "S02": "crossfire.entrance",
    "S03": "squadManeuver",
    "S04": "crossfire.coverMate",
    "S05": "crossfire.trade",
    "S06": "squadStorm",
    "S07": "fireLane",
    "S08": "squadFire",
    "S09": "squadVoice",
    "L01": "lightDiscipline",
    "L02": "tacticalDevices",
    "L03": "restLights",
    "E03": "encounter",
    "E04": "outcome",
    "C01": "doorTransit",
    "C02": "doorRecovery",
    "C03": "doorSpatial",
    "C04": "doorMotion",
    "C05": "doorClosing",
    "C06": "movementRecovery",
    "C07": "coverScan",
    "C08": "faultIsolation",
    "C09": "lifecycle",
    "C10": "validity",
    "C13": "raidCleanup",
    "C14": "sainPreset",
    "I02": "controlHandoff",
    "T01": "combatJournal",
    "T02": "tacticCounters",
    "T03": "weaponJournal",
    "T04": "controlJournal",
    "U01": "cometPreset",
}
EXCLUDED = set("C11 C12 E01 E05 U02 U03 U04 U06 U09 U10 T05".split())
DEFERRED = set("E02 I04 I05 I06 U11".split())
INTEGRATED = set("P06 E06 I03 L04".split())
INFRA = set("I01 U05 U07 U08".split())


def build():
    config = (ROOT / "Shared/CometConfig.cs").read_text(encoding="utf-8-sig")
    props = re.findall(r"public\s+\w+Config\s+(\w+)\s*\{", config)
    byname = {p.lower(): p[0].lower() + p[1:] for p in props}
    overrides = {
        "A01": "enabled",
        "A02": "visibleCombat",
        "A03": "sightContinuity",
        "A04": "decisionVariety",
        "A05": "tacticalMistakes",
        "A07": "threatSelection",
        "P01": "movementHearing",
        "P02": "quietApproach",
        "D02": "doorJumpPeek",
        "D03": "doorRunBy",
        "D04": "doorStepPeek",
        "D10": "holdAngle",
        "S02": "secondaryEntrance",
        "S04": "coverMate",
        "S05": "squadTrade",
        "E03": "encounter",
        "E04": "outcomes",
        "P07": "impactReaction",
        "S03": "squadManeuver",
        "S06": "squadStorm",
        "T01": "combatJournal",
        "T02": "tacticCounters",
        "T03": "weaponJournal",
        "T04": "controlJournal",
    }
    cards = {
        p.stem: p.read_text(encoding="utf-8-sig")
        for p in (ROOT / "docs").glob("[A-Z][0-9][0-9].md")
    }
    cards["E02"] = "# E02 - Player-action diagnostic tool (deferred)"
    result = []
    # Infer the root trace field only from an explicit emitter for that feature.
    # Hand mappings retain nested/attached features which do not own an emitter.
    for p in (ROOT / "Client").glob("*.cs"):
        s = p.read_text(encoding="utf-8-sig")
        for m in re.finditer(r'feature\s*=\s*"([A-Z]\d{2})"', s):
            end = s.find("Diagnostics.Json", m.end())
            if end < 0 or end - m.end() > 5000:
                continue
            part = s[m.end() : end]
            names = re.findall(r"\b(\w+)\s*=\s*t\s*[,}]", part)
            if names and m[1] in TRACE and "." not in TRACE[m[1]]:
                TRACE[m[1]] = names[-1]
    TRACE.update(E03="encounter", S03="crossfire.maneuver", S06="crossfire.storm")
    for f, doc in sorted(cards.items()):
        name = re.sub(r"^#\s*" + f + r"\s*[-:]\s*", "", doc.splitlines()[0])
        status = (
            "excluded"
            if f in EXCLUDED
            else (
                "deferred"
                if f in DEFERRED
                else (
                    "integrated"
                    if f in INTEGRATED
                    else "interface" if f in INFRA else "runtime"
                )
            )
        )
        path = TRACE.get(f, "")
        key = overrides.get(f, byname.get(path.replace(".", "").lower()))
        result.append(
            dict(
                id=f,
                name=name,
                status=status,
                trace=path,
                config=key,
                counting=(
                    "choice"
                    if f in ("A01", "A02")
                    else "draw" if f in ("A04", "A05") else "trace"
                ),
                note="Recorded evidence only. Missing events do not prove zero opportunities. Completion means the feature reported completion, not tactical success.",
            )
        )
    catalog_path = pathlib.Path(__file__).parent / "catalog.json"
    # Reference design cards are local maintainer files. Keep the tracked
    # legacy entries when regenerating from a public checkout without them.
    previous = json.loads(catalog_path.read_text(encoding="utf-8"))
    entries = {entry["id"]: entry for entry in previous if entry["id"] not in CARDS}
    entries.update({entry["id"]: entry for entry in result})
    for feature, (name, trace, config_key, note) in CARDS.items():
        entries[feature] = dict(id=feature, name=name, status="runtime", trace=trace,
                                config=config_key, counting="trace", note=note)
    result = [entries[key] for key in sorted(entries)]
    catalog_path.write_text(
        json.dumps(result, indent=2, ensure_ascii=False) + "\n", encoding="utf-8"
    )
    print(f"{len(result)} feature cards written.")


if __name__ == "__main__":
    build()
