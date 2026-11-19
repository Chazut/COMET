"""Feature telemetry contracts. Commands and settings are not physical outcomes."""

from common import at


def extra_phases(feature, t, event=None):
    result = set()
    if feature == "G01":
        if t.get("nativeStarted") is True:
            result.add("started")
        if t.get("released") is True:
            result.update(("started", "released", "completed"))
        if t.get("state") == "unconfirmed":
            result.add("unconfirmed")
    if feature in {"W04", "H01"} and t.get("started") is True:
        result.add("started")
    if feature == "M04" and (
        t.get("tiltObserved") is True or t.get("headMotionObserved") is True
    ):
        result.add("started")
    if feature == "D01":
        if t.get("moved") is True:
            result.add("started")
        if t.get("arrived") is True:
            result.add("completed")
    # A firing angle acquired during the route completes positioning, not the
    # firefight. Shared support members must not inherit the flanker outcome.
    crossfire = (event or {}).get("crossfire") or {}
    if (
        feature in {"S01", "S03"}
        and (event or {}).get("feature") == feature
        and crossfire.get("outcome") == "engaged-from-new-angle"
        and (feature == "S01" or t.get("role") in (None, "", "flank"))
    ):
        result.add("completed")
    # Confirmed means a measured angle/device state; not a completed combat plan.
    return result


def effect_facts(feature, t, event=None):
    fields = {
        "P02": {"applied": "Speed cap applied"},
        "M04": {
            "tiltObserved": "Tilt observed",
            "headMotionObserved": "Head motion observed",
            "animatorApplied": "Animator command applied",
        },
        "M11": {"applied": "Retreat posture applied"},
        "H04": {"applied": "Recovery setting applied"},
        "L02": {"observed": "Device state observed"},
        "S08": {"applied": "Squad fire guard applied", "stopped": "Shot stopped"},
        "S09": {"played": "Voice played"},
    }.get(feature, {})
    facts = {label for key, label in fields.items() if t.get(key) is True}
    # Positioning and engagement are distinct observations. Attribute only to
    # the actor's own tactic, never to a support snapshot carrying shared context.
    if feature in {"S01", "S03"} and (event or {}).get("feature") == feature:
        crossfire = (event or {}).get("crossfire") or {}
        if feature == "S01" or t.get("role") in (None, "", "flank"):
            if (
                crossfire.get("reason") == "crossfire-angle-reached"
                and crossfire.get("state") == "holding"
            ):
                facts.add("Position reached")
                facts.add("Angle held")
            if crossfire.get("outcome") == "engaged-from-new-angle":
                facts.add("Enemy engaged from new angle")
    if feature == "C04" and t.get("state") == "confirmed":
        facts.add("Door angle confirmed")
    if feature == "L02" and t.get("state") == "confirmed":
        facts.add("Device state confirmed")
    return facts


def feature_identity(e, t, spec):
    # Cleanup has one final report per raid. No fabricated IDs for repeated
    # snapshots of medical, device or voice effects in older recordings.
    if spec["id"] == "C13" and t.get("state") == "completed":
        return ("raid-cleanup",)
    if spec["id"] == "S03" and t.get("plan") not in (None, "", 0):
        episode = at(e, "crossfire.episode")
        if episode not in (None, "", 0):
            return ("maneuver", str(t["plan"]), str(episode))
    return None


def evidence_note(feature):
    if feature in {"S01", "S03"}:
        return "Position reached and Angle held record arrival and holding separately from Enemy engaged from new angle. Completion records the latter outcome, not hits or a won firefight. Counts are per identified tactic, not per snapshot."
    if feature in {
        "D02",
        "D03",
        "D04",
        "D05",
        "D06",
        "D07",
        "D08",
        "D11",
        "D12",
        "D13",
        "D14",
    }:
        return "Requires a usable door plan. Check D01 and D09 first; no events do not prove this follow-up is broken."
    if feature == "M06":
        return "Requires an accepted corner chase (M05) and a safe prefire opportunity."
    if feature == "S07":
        return "Requires a recent squad shot crossing the movement corridor. No trace is not proof that this opportunity occurred."
    if feature == "G08":
        return "Requires the Manimal gas provider and an actual exposure. Provider detection alone does not validate the reaction."
    if feature == "C10":
        return "Safety guard: emits when it blocks invalid references. No events can be normal."
    if feature in {"W09", "C05"}:
        return "Off by default. Check the configuration recorded in each raid before expecting execution."
    if feature == "C13":
        return "One final cleanup report per recorded raid, not a combat action."
    return "No evidence is not zero opportunities. Effects and observed starts do not prove tactical success."
