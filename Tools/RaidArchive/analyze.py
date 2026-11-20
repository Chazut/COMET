"""Conservative, versioned evidence reducer. No inferred physical success."""

from __future__ import annotations
import collections, hashlib, json, logging
from common import HERE, at, dumps, load, number, unpack, utc

# A code/catalog change invalidates derived reports even when the originals did
# not change. Archive/export schema VERSION is deliberately independent.
ANALYZER_VERSION = hashlib.sha256(
    b"".join(
        (HERE / p).read_bytes() for p in ("analyze.py", "catalog.json", "evidence.py")
    )
).hexdigest()[:20]
from evidence import EpisodeEvidence, effect_facts, extra_phases, feature_identity, evidence_note

CATALOG = load(HERE / "catalog.json", [])
COUNTS = (
    "events",
    "opportunities",
    "eligible",
    "selected",
    "requested",
    "accepted",
    "started",
    "completed",
    "interrupted",
    "refused",
    "released",
    "unconfirmed",
)
# These fields explicitly record observation in the runtime traces, not command intent.
OBSERVED = (
    "movementObserved",
    "handsObserved",
    "shotObserved",
    "jumpObserved",
    "reloadObserved",
    "surgeryObserved",
    "takeoffObserved",
)


def new_metrics():
    return {
        **dict.fromkeys(COUNTS, 0),
        "reasons": collections.Counter(),
        "states": collections.Counter(),
        "coverage": "no-evidence",
        "configEnabled": None,
        "durationSamples": 0,
        "durationTotal": 0,
        "randomTrials": 0,
        "expectedSelections": 0.0,
        "randomSelections": 0,
        "unknownIdentityEvents": 0,
        "observationFacts": collections.Counter(),
        "effects": collections.Counter(),
    }


def merge_config(previous, incoming):
    """Merge partial telemetry, including nested fragments, within one revision."""
    result = dict(previous)
    for key, value in incoming.items():
        result[key] = (
            merge_config(result.get(key, {}), value)
            if isinstance(value, dict) and isinstance(result.get(key, {}), dict)
            else value
        )
    return result


def stale_analyses(c):
    return [
        r[0]
        for r in c.execute("SELECT id,report FROM raids")
        if json.loads(r[1] or "{}").get("analyzerVersion") != ANALYZER_VERSION
    ]


# Only these nested traces are independent evidence for a second feature.
CONTEXT = {"A04", "A05", "A06", "A08", "A09", "A10", "A11", "A12", "E03", "E04"}
SQUAD = {"S01", "S02", "S04", "S05", "S06"}
CATALOG_BY_ID = {s["id"]: s for s in CATALOG}
CANDIDATES = {
    own: [s for s in CATALOG if s["id"] == own or s["id"] in CONTEXT
          or own in SQUAD and s["id"] in {"S02", "S04", "S05"}]
    for own in (*CATALOG_BY_ID, None)
}


def payloads(e, catalog=CATALOG):
    own = e.get("feature")
    specs = CANDIDATES.get(own, CANDIDATES[None]) if catalog is CATALOG else catalog
    found = False
    for spec in specs:
        f = spec["id"]
        if f != own and f not in CONTEXT and not (own in SQUAD and f in {"S02", "S04", "S05"}):
            continue
        trace = at(e, spec["trace"]) if spec["trace"] else None
        if f == own or isinstance(trace, dict):
            found |= f == own
            yield spec, trace if isinstance(trace, dict) else {}
    if own and not found:
        yield {"id": own, "status": "unknown", "counting": "trace", "trace": ""}, {}


def identity(e, trace, mode):
    if mode in ("choice", "draw"):
        mix = e.get("mixing") or {}
        if mode == "draw":
            mix = trace
        if mix.get("opportunity") is not None:
            if mode == "draw":
                # A04 has independent visible/hidden streams; A05 is shared.
                if e.get("mixing", {}).get("mistake") is trace:
                    return ("shared-mistake", str(mix["opportunity"]))
                return (
                    "policy",
                    str(e.get("feature", "")),
                    str(mix.get("encounter", "")),
                    str(mix["opportunity"]),
                )
            return (
                "opportunity",
                str((e.get("mixing") or {}).get("encounter", "")),
                str(mix["opportunity"]),
            )
        return None
    for key in (
        "opportunity",
        "episode",
        "requestId",
        "signal",
        "operationId",
        "attemptId",
        "projectile",
    ):
        value = trace.get(key)
        if isinstance(value, (str, int)) and value not in ("", 0):
            return (key, str(value), str(trace.get("target", trace.get("contact", ""))))
    return None


def phases(e, t, spec):
    """Explicit facts only; active/applied/accepted never imply observed execution."""
    result = set()
    mode = spec["counting"]
    stage = e.get("stage", "")
    if mode == "choice":
        if stage in (
            "selected",
            "requested",
            "accepted",
            "started",
            "released",
            "rejected",
        ):
            result.add({"rejected": "refused"}.get(stage, stage))
            if stage in ("accepted", "rejected"):
                result.add("requested")
        return result
    if mode == "draw":
        return result
    state = t.get("state", "")
    if t.get("eligible") is True:
        result.add("eligible")
    if t.get("requested") is True or t.get("drawRequested") is True:
        result.add("requested")
    if t.get("jumpRequested") is True:
        result.add("requested")
    if t.get("accepted") is True:
        result.add("accepted")
    if t.get("callbackSucceeded") is True:
        result.add("accepted")
    if t.get("callbackCompleted") is True and t.get("callbackSucceeded") is False:
        result.add("refused")
    if any(t.get(k) is True for k in OBSERVED):
        result.add("started")
    if spec["id"] in ("W02", "W03", "W05") and t.get("inHands") is True:
        result.add("started")
    if spec["id"] == "W01":
        if t.get("started") is True:
            result.add("started")
        # W01 ends observation with state=ended, including unsuccessful reloads.
        if t.get("finished") is True and t.get("improved") is True:
            result.add("completed")
    if spec["id"] == "M13" and t.get("moveRequested") is not True:
        result.difference_update(("requested", "accepted"))
    result.update(extra_phases(spec["id"], t, e))
    if state == "completed":
        result.add("completed")
    if state == "interrupted":
        result.add("interrupted")
    # Attached context is not the parent tactic: an A01 start does not mean
    # its A10 opportunity was physically executed.
    if e.get("feature") == spec["id"]:
        if stage == "started":
            result.add("started")
        if stage == "rejected":
            result.add("refused")
        if stage == "released":
            result.add("released")
    return result


def analyze(c, raid):
    row = c.execute("SELECT metadata FROM raids WHERE id=?", (raid,)).fetchone()
    if not row:
        raise KeyError(raid)
    meta = json.loads(row[0])
    metrics = collections.defaultdict(new_metrics)
    attempts = {}
    episodes = EpisodeEvidence()
    alerts = []
    summaries = []
    footer = None
    configs = {}
    actors = {}
    malformed = meta.get("malformed", 0)
    first = None
    last = None
    headers = []
    candidates = collections.Counter()
    sources = collections.Counter()
    unsupported = 0
    c.execute("DELETE FROM features WHERE raid=?", (raid,))
    c.execute("DELETE FROM alerts WHERE raid=?", (raid,))
    c.execute("DELETE FROM event_features WHERE raid=?", (raid,))
    for row in c.execute(
        "SELECT * FROM events WHERE raid=? ORDER BY COALESCE(clock,-1),id", (raid,)
    ):
        e = unpack(row["payload"])
        stage = e.get("stage")
        profile = e.get("profileId", "")
        bot = e.get("botInstance", "")
        if e.get("schema", 1) != 1:
            unsupported += 1
            continue
        if stage == "diagnostics-end":
            footer = e
            continue
        if stage == "raid-start":
            headers.append(e)
            continue
        if isinstance(e.get("config"), dict):
            revision = str(e.get("configRevision", 0))
            configs[revision] = merge_config(configs.get(revision, {}), e["config"])
        if stage == "summary":
            summaries.append(e)
            f = e.get("feature")
            if f:
                m = metrics[f, profile]
                m["events"] += 1
                m["states"]["summary"] += 1
                m["coverage"] = "events-only"
                c.execute(
                    "INSERT OR IGNORE INTO event_features VALUES(?,?,?)",
                    (raid, row["event_key"], f),
                )
            continue
        if stage == "configuration":
            continue
        clock = number(e.get("realtime"))
        if clock is not None:
            first = clock if first is None else min(first, clock)
            last = clock if last is None else max(last, clock)
        if profile:
            actor = actors.setdefault(
                profile,
                {
                    "name": e.get("name") or profile,
                    "instances": set(),
                    "personalities": set(),
                    "events": 0,
                },
            )
            actor["instances"].add(bot)
            actor["events"] += 1
            if e.get("personality"):
                actor["personalities"].add(e["personality"])
        sources[row["source"]] += 1
        if e.get("feature") == "T03":
            episodes.weapon_observation(e, clock)
        for spec, t in payloads(e):
            f = spec["id"]
            m = metrics[f, profile]
            m["events"] += 1
            m["coverage"] = "events-only"
            c.execute(
                "INSERT OR IGNORE INTO event_features VALUES(?,?,?)",
                (raid, row["event_key"], f),
            )
            reason = str(t.get("reason") or e.get("reason") or "")
            if reason:
                m["reasons"][reason] += 1
            state = str(t.get("state") or stage or "")
            if state:
                m["states"][state] += 1
            own_config = e.get(spec.get("trace", "") + "Config")
            if isinstance(own_config, dict) and isinstance(
                own_config.get("enabled"), bool
            ):
                m["configEnabled"] = own_config["enabled"]
            ident = feature_identity(e, t, spec) or identity(e, t, spec["counting"])
            if f == "D01" and ident is None:
                ident = episodes.legacy_door(e, t, row["event_key"])
            effects = effect_facts(f, t, e)
            if ident is None:
                m["unknownIdentityEvents"] += 1
                m["observationFacts"].update(phases(e, t, spec))
                m["observationFacts"].update(effects)
            else:
                key = (f, profile, bot, e.get("session"), *ident)
                a = attempts.setdefault(
                    key,
                    {
                        "seen": set(),
                        "first": clock,
                        "last": clock,
                        "event": row["event_key"],
                        "feature": f,
                        "profile": profile,
                        "decision": e.get("decision", ""),
                        "reason": reason,
                    },
                )
                if f == "W05" and t.get("accepted") is True:
                    episodes.weapon_request(e, t, clock, key)
                a["last"] = clock
                a["reason"] = reason
                facts = phases(e, t, spec)
                for effect in effects - a["seen"]:
                    m["effects"][effect] += 1
                a["seen"] |= effects
                if spec["counting"] in ("choice", "draw"):
                    facts.add("opportunities")
                if spec["counting"] == "choice" and "candidates" not in a["seen"]:
                    a["seen"].add("candidates")
                    for opt in e.get("candidates") or []:
                        if not isinstance(opt, dict):
                            continue
                        candidates[
                            (
                                f,
                                str(opt.get("decision", "")),
                                bool(opt.get("eligible")),
                                str(opt.get("reason", "")),
                            )
                        ] += 1
                    if any(
                        x.get("eligible")
                        for x in e.get("candidates", [])
                        if isinstance(x, dict)
                    ):
                        facts.add("eligible")
                if (
                    spec["counting"] == "draw"
                    and "draw" not in a["seen"]
                    and number(t.get("roll")) is not None
                ):
                    a["seen"].add("draw")
                    m["randomTrials"] += 1
                    chosen = t.get("selected", "")
                    prob = next(
                        (
                            number(o.get("probability"))
                            for o in t.get("options", [])
                            if isinstance(o, dict) and o.get("decision") == chosen
                        ),
                        None,
                    )
                    if chosen:
                        m["randomSelections"] += 1
                    # Candidate-specific expected/observed counts, not score percentages.
                    for opt in t.get("options", []):
                        if not isinstance(opt, dict):
                            continue
                        p = number(opt.get("probability"))
                        if p is not None and 0 <= p <= 1:
                            a.setdefault("drawOptions", []).append(
                                {
                                    "action": opt.get("decision"),
                                    "p": p,
                                    "selected": opt.get("decision") == chosen,
                                }
                            )
                for fact in facts - a["seen"]:
                    if fact in m:
                        m[fact] += 1
                a["seen"] |= facts
                if (
                    "completed" in facts
                    or "interrupted" in facts
                    or "released" in facts
                ) and "duration" not in a["seen"]:
                    if (
                        a["first"] is not None
                        and clock is not None
                        and clock >= a["first"]
                    ):
                        m["durationSamples"] += 1
                        m["durationTotal"] += clock - a["first"]
                    a["seen"].add("duration")
            if (
                stage == "observer-unavailable"
                or reason
                in {"accepted-not-observed-timeout", "execution-not-observed-timeout"}
                or ("timed-out" == str(at(e, "execution.phase")))
                or (f == "C08" and t.get("state") == "quarantined")
            ):
                alerts.append(
                    (f, profile, "review", reason or stage, row["event_key"], clock)
                )
    partial = (
        not footer
        or bool(
            footer.get("queueDropped")
            or footer.get("fileLimitDropped")
            or footer.get("writeFailed")
        )
        or bool(malformed or unsupported or meta.get("missingSegments"))
    )
    draws = collections.defaultdict(
        lambda: {"trials": 0, "expected": 0.0, "observed": 0}
    )
    for key, facts in episodes.weapon_results():
        a = attempts[key]
        m = metrics[a["feature"], a["profile"]]
        for fact in facts - a["seen"]:
            if fact in COUNTS:
                m[fact] += 1
            else:
                m["effects"][fact] += 1
        a["seen"] |= facts
    for a in attempts.values():
        seen = a["seen"]
        m = metrics[a["feature"], a["profile"]]
        # An anonymous observation or summary later in the raid cannot erase an identified attempt.
        m["coverage"] = "identified-attempts"
        if ("requested" in seen or "accepted" in seen) and not {
            "started",
            "completed",
        } & seen:
            m["unconfirmed"] += 1
            if "released" in seen and not partial:
                alerts.append(
                    (
                        a["feature"],
                        a["profile"],
                        "unconfirmed",
                        "Command ended without an observed start; inspect urgency/recording.",
                        a["event"],
                        a["first"],
                    )
                )
        for opt in a.get("drawOptions", []):
            d = draws[a["feature"], opt["action"]]
            d["trials"] += 1
            d["expected"] += opt["p"]
            d["observed"] += opt["selected"]
    cfg = configs.get(str(max((int(k) for k in configs if k.isdigit()), default=0)), {})
    for spec in CATALOG:
        f = spec["id"]
        feature_rows = [m for (feature, _), m in metrics.items() if feature == f]
        total = new_metrics()
        for m in feature_rows:
            for key in COUNTS + (
                "durationSamples",
                "durationTotal",
                "randomTrials",
                "randomSelections",
                "expectedSelections",
                "unknownIdentityEvents",
            ):
                total[key] += m[key]
            total["reasons"].update(m["reasons"])
            total["states"].update(m["states"])
            total["observationFacts"].update(m["observationFacts"])
            total["effects"].update(m["effects"])
        enabled_values = set()
        for snapshot in configs.values():
            configured = at(snapshot, spec["config"]) if spec.get("config") else None
            enabled = (
                configured
                if isinstance(configured, bool)
                else configured.get("enabled") if isinstance(configured, dict) else None
            )
            if snapshot.get("enabled") is False and spec["status"] == "runtime":
                enabled = False
            if enabled is not None:
                enabled_values.add(enabled)
        enabled = next(iter(enabled_values)) if len(enabled_values) == 1 else None
        total["configChanged"] = len(enabled_values) > 1
        total["configEnabled"] = enabled
        total["coverage"] = (
            spec["status"]
            if spec["status"] != "runtime"
            else (
                "disabled"
                if enabled is False
                else (
                    "identified-attempts"
                    if any(m["coverage"] == "identified-attempts" for m in feature_rows)
                    else "events-only" if total["events"] else "no-evidence"
                )
            )
        )
        total["name"] = spec["name"]
        total["status"] = spec["status"]
        total["partial"] = partial
        total["counting"] = spec["counting"]
        total["evidenceNote"] = evidence_note(f)
        total["completionMeaning"] = (
            "Feature-reported completion; does not prove animation quality or combat effectiveness."
        )
        for m in [total, *feature_rows]:
            m["counting"] = spec["counting"]
            m["reasons"] = dict(m["reasons"].most_common(30))
            m["states"] = dict(m["states"].most_common(30))
            m["observationFacts"] = dict(m["observationFacts"])
            m["effects"] = dict(m["effects"])
            m["evidenceNote"] = evidence_note(f)
        c.execute("INSERT INTO features VALUES(?,?,?,?)", (raid, f, "", dumps(total)))
    for (f, p), m in metrics.items():
        if p:
            c.execute(
                "INSERT OR REPLACE INTO features VALUES(?,?,?,?)",
                (raid, f, p, dumps(m)),
            )
    for f, p, kind, msg, key, clock in dict.fromkeys(alerts):
        c.execute(
            "INSERT INTO alerts(raid,feature,profile,kind,message,event_key,clock) VALUES(?,?,?,?,?,?,?)",
            (raid, f, p, kind, msg, key, clock),
        )
    for actor in actors.values():
        actor["instances"] = sorted(actor["instances"])
        actor["personalities"] = sorted(actor["personalities"])
    config_hash = (
        hashlib.sha256(
            dumps({k: v for k, v in cfg.items() if k != "revision"}).encode()
        ).hexdigest()
        if cfg
        else None
    )
    report = {
        "analyzerVersion": ANALYZER_VERSION,
        "analyzedAt": utc(),
        "partial": partial,
        "footer": footer,
        "malformed": malformed,
        "unsupportedSchemas": unsupported,
        "recording": (
            "no-comet-evidence" if not sources else "partial" if partial else "closed"
        ),
        "headers": headers,
        "configs": configs,
        "configHash": config_hash,
        "actors": actors,
        "firstClock": first,
        "lastClock": last,
        "summaries": summaries,
        "sources": dict(sources),
        "attempts": len(attempts),
        "alerts": len(set(alerts)),
        "draws": [dict(feature=f, action=a, **v) for (f, a), v in draws.items()],
        "candidateRejections": [
            dict(feature=f, action=a, eligible=eligible, reason=reason, count=n)
            for (f, a, eligible, reason), n in candidates.items()
        ],
        "limitations": [
            "No evidence is not zero opportunities.",
            "Accepted commands are not observed execution.",
            "Alerts are review candidates, not automatic bug verdicts.",
            "Durations cover observed records, not exact animation length.",
        ],
    }
    c.execute(
        "UPDATE raids SET report=?,updated=? WHERE id=?", (dumps(report), utc(), raid)
    )
    c.commit()
    door = [m for (f, p), m in metrics.items() if f == "D01" and p]
    logging.info("Evidence %s raid=%s attempts=%d D01 requested=%d accepted=%d moved=%d arrived=%d",
                 ANALYZER_VERSION, raid, len(attempts),
                 *(sum(m[k] for m in door) for k in ("requested", "accepted", "started", "completed")))
    return report
