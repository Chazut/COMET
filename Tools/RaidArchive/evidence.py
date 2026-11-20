"""Feature telemetry contracts. Commands and settings are not physical outcomes."""

from bisect import bisect_right
from collections import defaultdict
from common import at
from recent_features import CARDS


def sight_executing(e):
    execution = e.get("execution") or {}
    return (
        execution.get("phase") == "executing"
        and e.get("decision") in {"KeepAngle", "ContinuePush"}
        and execution.get("action") == e["decision"]
        and bool(execution.get("target"))
        and execution["target"] == at(e, "contact.target")
    )


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
    if feature == "A03" and event:
        if event.get("feature") == feature and event.get("stage") == "selected":
            result.add("selected")
        if event.get("feature") == feature and event.get("stage") == "accepted":
            result.update(("requested", "accepted"))
        if sight_executing(event):
            result.add("started")
    if feature == "M13":
        if t.get("reason") in {"relocate-searching-better-cover", "relocate-chose-to-stay"}:
            result.add("opportunities")
        if t.get("reason") == "relocate-searching-better-cover":
            result.add("selected")
        if t.get("moveRequested") is True and t.get("arrived") is True:
            result.add("started")
            if "holdSeconds" not in t or (
                t.get("holdApplied") is True and t.get("reason") == "relocate-hold-complete"
            ):
                result.add("completed")
    if feature == "D05" and t.get("reason") == "fake-roll-declined":
        result.add("refused")
    if feature == "D01":
        if t.get("movementAccepted") is True:
            result.add("accepted")
        if t.get("moved") is True:
            result.add("started")
        if t.get("arrived") is True:
            result.add("completed")
    if feature == "M07" and t.get("aligned") is True:
        result.add("started")
    if feature == "M03" and t.get("openObserved") is True:
        result.add("started")
    if feature == "L03" and t.get("requested") is True and t.get("observedOff") is True:
        result.add("completed")
    if feature == "W07" and isinstance(t.get("actualShots"), (int, float)) and t["actualShots"] > 0:
        result.add("started")
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


EFFECT_FIELDS = {
    "M07": {"aligned": "Watch angle aligned"},
    "M03": {"openObserved": "Exposure observed", "hiddenObserved": "Return to cover observed", "arrived": "Peek destination reached"},
    "L03": {"observedOff": "Device observed off"},
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
}

STATE_FACTS = {
    ("C08", "suspended"): "Faulting module suspended",
    ("C08", "module-disabled"): "Faulting module disabled",
    ("G02", "prepared"): "Trajectory prepared",
    ("G03", "selected"): "Fuse selected",
    ("G06", "reserved"): "Dispersion destination reserved",
    ("G07", "selected"): "Grenade type selected",
    ("G10", "heard"): "Explosion heard",
    ("A13", "native-observed"): "Native control observed",
    ("C09", "resumed"): "AI resumed",
    ("M02", "settled"): "Firing pause settled",
    ("H02", "hands-released"): "Hands released",
    ("H02", "weapon-ready"): "Weapon ready",
    ("D14", "acquired"): "Reservation acquired",
}

REASON_FACTS = {
    ("M13", "relocate-chose-to-stay"): "Rolled to stay in cover",
    ("M13", "relocate-better-cover-selected"): "Relocation destination selected",
    ("C06", "recovery-movement-observed"): "Recovered movement observed",
    ("S06", "storm-native-rush"): "Native rush recorded",
    ("C01", "transit-passage-clear"): "Passage clear",
    ("C07", "cover-scan-complete"): "Cover scan published",
    ("C07", "hit-cover-released"): "Exposed cover released",
    ("D09", "ambush-watching-exit"): "Exit watched",
    ("I02", "control-layer-observed"): "Control layer observed",
    ("P01", "movement-personally-heard"): "Footstep heard by bot",
    ("P04", "floor-range-reduced"): "Hearing range reduced",
    ("P04", "floor-range-cut"): "Sound filtered",
    ("P05", "close-native-visual-acquired"): "Native sight acquisition",
    ("W06", "sync-equipment-verified"): "Equipment verified",
    ("W08", "geometry-other-floor"): "Shot blocked by floor check",
    ("W10", "hands-native-reload-recovered"): "Native reload recovered",
    ("S02", "secondary-entrance-watch-position-reached"): "Watch post reached",
    ("S04", "crossfire-angle-reached"): "Cover angle reached",
    ("S05", "trade-position-reached"): "Position reached",
    ("G04", "own-grenade-confirmed-explosion"): "Own grenade explosion observed",
    ("G09", "flash-source-live-memory"): "Flash onset observed",
    ("G09", "flash-recovered"): "Flash recovery observed",
}


def effect_facts(feature, t, event=None):
    fields = EFFECT_FIELDS.get(feature, {})
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
    if (event or {}).get("feature") == feature:
        state = t.get("state")
        reason = (event or {}).get("reason")
        label = STATE_FACTS.get((feature, state)) or REASON_FACTS.get((feature, reason))
        if feature == "A07" and event.get("stage") == "accepted":
            facts.add("Target choice recorded")
        if label:
            facts.add(label)
        if feature == "P09" and state == "abandoned" and reason == "search-check-position-empty":
            facts.add("Empty search abandoned")
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
    if feature in CARDS:
        return CARDS[feature][3]
    if feature == "A03":
        return "Start means the matching native combat action was observed. Completion means contact returned or the memory window expired after an observed start."
    if feature == "M13":
        return "Start records movement to the selected cover. Completion records arrival and a finished hold. Older raids recorded completion on arrival."
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
    if feature == "A11":
        return "Part of visible combat scoring (A02). Older recordings have no independent signal for this contribution."
    if feature == "C03":
        return "Door geometry is checked during D01. A missing dedicated trace does not count successful checks."
    if feature == "W05":
        return "Matches the requested weapon to the same actor in T03. In hands and ready are separate observations within 10 seconds."
    if feature == "D01":
        return "Movement accepted, actual displacement and arrival at the stack are separate observations. Legacy records are joined only to an explicit request for the same door and target."
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

class EpisodeEvidence:
    """Per raid. A later request supersedes the previous one for that actor/weapon."""

    def __init__(self):
        self.sight = {}
        self.doors = {}
        self.requests = defaultdict(dict)
        self.observations = defaultdict(list)

    @staticmethod
    def actor(e):
        return (e.get("session"), e.get("profileId"), e.get("botInstance"))

    def legacy_sight(self, e, event_key):
        actor = self.actor(e)
        target = at(e, "contact.target")
        action = e.get("decision")
        if not all(actor) or not target or action not in {"KeepAngle", "ContinuePush"}:
            return None
        if e.get("feature") == "A03" and e.get("stage") == "selected":
            self.sight[actor] = (target, action, ("legacy-sight-selection", event_key))
        current = self.sight.get(actor)
        if not current or current[:2] != (target, action):
            return None
        if e.get("feature") == "A03" and e.get("stage") in {"released", "rejected"}:
            self.sight.pop(actor)
        return current[2]

    def legacy_door(self, e, t, event_key):
        actor = self.actor(e)
        if not actor[1] or actor[2] is None or not t.get("door") or not t.get("target"):
            return None
        door = (t["door"], t["target"])
        if t.get("reason") == "door-move-requested" and t.get("requested") is True:
            self.doors[actor] = (door, ("legacy-request", event_key))
        current = self.doors.get(actor)
        if not current or current[0] != door or not t.get("requested"):
            self.doors.pop(actor, None)
            return None
        if t.get("state") in {"ended", "arrived"}:
            self.doors.pop(actor, None)
        return current[1]

    def weapon_request(self, e, t, clock, key):
        if clock is not None and t.get("to") and e.get("profileId") and e.get("botInstance") is not None:
            self.requests[self.actor(e), t["to"]].setdefault(key, clock)

    def weapon_observation(self, e, clock):
        change = (e.get("weaponJournal") or {}).get("change") or {}
        reason = change.get("reason")
        if clock is not None and change.get("to") and reason in {"requested-weapon-ready", "requested-weapon-observed"}:
            self.observations[self.actor(e), change["to"]].append((clock, reason))

    def weapon_results(self):
        for actor_weapon, requests in self.requests.items():
            ordered = sorted(requests, key=requests.get)
            times = [requests[k] for k in ordered]
            results = defaultdict(set)
            for clock, reason in self.observations[actor_weapon]:
                i = bisect_right(times, clock) - 1
                preceding = i >= 0 and clock - times[i] <= 10
                next_frame = i + 1 < len(times) and times[i + 1] - clock <= .05
                if next_frame == preceding:  # Both possible, or neither in the correlation window.
                    continue
                if next_frame:
                    i += 1
                if (i > 0 and times[i - 1] == times[i]) or (i + 1 < len(times) and times[i + 1] == times[i]):
                    continue
                result = results[ordered[i]]
                result.update(("started", "Requested weapon in hands"))
                if reason == "requested-weapon-ready":
                    result.update(("completed", "Requested weapon ready"))
            yield from results.items()
