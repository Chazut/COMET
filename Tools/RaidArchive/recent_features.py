"""Evidence for COMET additions which share or extend older diagnostic channels."""

# ID, display name, runtime payload, config path, interpretation.
CARDS = {
    "A14": ("Boss, guard and faction recognition", "enemyRole", None,
            "A decision snapshot records a recognized contact or nearby escort cue. Expected support is the PMC's estimate."),
    "C15": ("Autumn and winter bush cover", "seasonalCover", None,
            "Records bare foliage rejected or a cached cover point removed. A missing record does not identify a solid trunk."),
    "C16": ("Exposed cover after taking fire", "coverExposure", None,
            "A hit, a released cover point and a refused move are separate observations. Release alone does not establish arrival at new cover."),
    "C17": ("Native SAIN corrections and doorframe cover", "", None,
            "Counts are increases in labeled runtime counters, including rejected doorframe cover and following a searching leader."),
    "L05": ("Local lighting, shadow and contact memory", "", None,
            "Summary checks are counted once per reporting interval. Sampled decisions are listed separately. AI shadow checks are a subset of all shadow checks."),
    "S10": ("Squad grenade suppression", "squadGrenade", "squadManeuver.grenadeSuppression",
            "Counts each member's role per plan. A native decision acceptance does not confirm a grenade left the thrower's hand."),
    "S11": ("Squad siege - watch building exits", "siege", "secondaryEntrance.siege",
            "Counts each assigned watcher per siege. Eligibility checks and a released watch do not establish a successful siege."),
    "S12": ("Squad storm - members staying back", "crossfire.storm", "squadStorm",
            "Records a member declining the rush or a storm roster listing a member staying back. It does not establish that anyone left the squad."),
    "W11": ("Discarded magazine recovery", "magazineRecovery", None,
            "One recovery per dropped magazine and owner. Start means the bot returned for it. Completion requires the original magazine confirmed in inventory."),
}
PARENT_CONFIG = {"S10": "squadManeuver", "S11": "secondaryEntrance"}
ALIASES = {
    ("W04", "RecoverMagazine"): "W11",
    ("C07", "SeasonalCover"): "C15",
    ("C07", "CoverExposure"): "C16",
}
GROUP_ROLES = {"Boss", "Guard", "Soldier"}


def channel(event):
    feature = event.get("feature")
    if feature == "local-exposure":
        return "L05"
    if feature == "C11" and event.get("stage") == "native-fix-summary":
        return "C17"
    if feature in ("W04", "C07"):
        return ALIASES.get((feature, event.get("decision")), feature)
    return feature


def additional(event):
    feature = event.get("feature")
    if feature in ("A01", "A02"):
        role = event.get("enemyRole")
        if isinstance(role, dict) and (role.get("role") in GROUP_ROLES or role.get("groupCue") in GROUP_ROLES):
            yield "A14", role
    elif feature == "S06":
        reason = event.get("reason")
        if event.get("stage") == "eligibility" and reason in {"storm-expected-support", "storm-personality-choice"}:
            yield "S12", {}
            if reason == "storm-expected-support":
                yield "A14", {}
        else:
            trace = (event.get("crossfire") or {}).get("storm") or {}
            if any(isinstance(m, str) and m.endswith(": stay back") for m in trace.get("members") or []):
                yield "S12", trace


def squad_identity(feature, t, e):
    if not e.get("profileId"):
        return None
    if feature == "S11" and t.get("target") and t.get("exit") and isinstance(t.get("until"), (int, float)):
        return ("siege-watch", t["target"], t["until"], t["exit"])
    if feature == "S10" and t.get("thrower") and t.get("holder") and t.get("contact") and isinstance(t.get("startFrame"), int):
        return ("grenade-pair", t["thrower"], t["holder"], t["contact"], t["startFrame"])


def phases(feature, t, e):
    stage, reason = e.get("stage"), e.get("reason")
    if feature in {"S10", "S11"}:
        return {"selected": {"selected"}, "accepted": {"requested", "accepted"},
                "rejected": {"requested", "refused"}, "released": {"released"}}.get(stage, set())
    if feature == "W11":
        if stage == "completed" and reason == "original magazine confirmed in inventory":
            return {"completed"}
        return {"queued": {"opportunities"}, "started": {"started"}, "interrupted": {"interrupted"},
                "failed": {"refused"}, "unavailable": {"refused"}}.get(stage, set())
    return set()


def observations(feature, t, e):
    stage, reason = e.get("stage"), e.get("reason")
    if feature in {"S10", "S11"} and stage == "eligibility":
        return {"Eligibility check"}
    if feature == "W11":
        label = {"queued": "Magazine drop recorded", "arrived": "Reached magazine",
                 "pickup-started": "Pickup state entered", "pickup-ready": "Pickup callback received"}.get(stage)
        return {label} if label else set()
    if feature == "C15":
        if reason in {"seasonal-held-foliage-cover-released", "seasonal-moving-foliage-cover-released"}:
            return {"Foliage cover released"}
        if reason in {"seasonal-foliage-is-not-cover", "seasonal-cover-position-inside-foliage",
                      "seasonal-cached-foliage-cover-removed", "seasonal-foliage-cover-move-refused"}:
            return {"Foliage cover rejected"}
    if feature == "C16":
        facts = set()
        if reason in {"hit-cover-released", "hit-below-native-spotted-threshold"}:
            facts.add("Hit in cover")
        if reason in {"hit-cover-released", "spotted-held-cover-released", "spotted-moving-cover-released"}:
            facts.add("Exposed cover released")
            if t.get("foliage") is True:
                facts.add("Exposed foliage cover released")
        if reason == "spotted-cover-move-refused":
            facts.add("Exposed cover move refused")
        return facts
    if feature == "L05" and stage == "evaluated" and reason in {"Shadow", "Bright", "Contact", "Hit", "Close", "NightVision", "Light", "Unknown"}:
        return {"Sampled decision: " + reason}
    if feature == "A14":
        facts = set()
        if t.get("role") in GROUP_ROLES:
            facts.add(t["role"] + " recognized")
        if t.get("groupCue") in GROUP_ROLES and isinstance(t.get("expectedSupport"), (float, int)) and t["expectedSupport"] > 0:
            facts.add("Escort expected")
        if reason == "storm-expected-support":
            facts.add("Storm declined due to expected support")
        return facts
    if feature == "S12":
        return {"Storm member stayed back"} if not t else {"Storm roster includes a member staying back"}
    return set()


def light_interval(e):
    fields = {"shadow": "Shadow checks", "aiShadow": "AI shadow checks", "recentSight": "Recent contact checks",
              "recentHit": "Recent hit checks", "bright": "Bright checks", "remoteChecks": "Remote player checks"}
    return {label: e[key] for key, label in fields.items() if type(e.get(key)) is int and e[key] > 0}


class RaidState:
    def __init__(self):
        self.magazines = {}
        self.native_totals = {}

    def magazine(self, e, t, event_key):
        magazine = t.get("magazine")
        if not magazine or not e.get("profileId"):
            return None
        key = (e.get("session"), e["profileId"], e.get("botInstance"), magazine)
        previous = self.magazines.get(key)
        stage = e.get("stage")
        if previous is None or (stage == "queued" and previous[1]):
            previous = (("drop", magazine, event_key), False)
        terminal = stage in {"completed", "failed", "unavailable"}
        self.magazines[key] = previous[0], previous[1] or terminal
        return previous[0]

    def native_changes(self, e):
        # These counters grow throughout a raid. The final report repeats them.
        changes = {}
        for label, count in zip(e.get("labels") or (), e.get("counts") or ()):
            if not isinstance(label, str) or type(count) is not int or count < 0:
                continue
            key = (e.get("session"), label)
            previous = self.native_totals.get(key, 0)
            if count > previous:
                changes["Native fix: " + label] = count - previous
                self.native_totals[key] = count
        return changes
