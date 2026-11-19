"""COMET Raid Observatory. Python standard library, loopback-only, no upload."""

from __future__ import annotations
import argparse, collections, contextlib, http.server, io, json, logging, logging.handlers, os, pathlib
import shutil, sqlite3, tempfile, threading, time, urllib.parse, webbrowser, zipfile
from common import *
from analyze import analyze, CATALOG, ANALYZER_VERSION, stale_analyses
from evidence import evidence_note
from collector import collect, active_raid


def query_settings(path):
    value = load(path)
    if not isinstance(value, dict):
        raise ValueError("Missing settings file. Run setup.ps1 first.")
    value.setdefault("port", 8791)
    value.setdefault("intervalSeconds", 30)
    value.setdefault("settleSeconds", 30)
    value.setdefault("rrUrl", "http://127.0.0.1:7829")
    collection_start(value)
    for key in ("game", "archive"):
        value[key] = str(pathlib.Path(value[key]).resolve())
    if not pathlib.Path(value["game"]).is_dir():
        raise ValueError("Game directory does not exist")
    if pathlib.Path(value["archive"]).is_relative_to(pathlib.Path(value["game"])):
        raise ValueError("Archive must be outside the game directory")
    return value


class State:
    def __init__(self, settings):
        self.settings = settings
        self.root = pathlib.Path(settings["archive"])
        self.stop = threading.Event()
        self.wake = threading.Event()
        self.lock = threading.Lock()
        self.status = {"state": "starting"}
        self.thread = None
        if settings.get("readOnly"):
            self.status = {"state": "archived"}

    def preserve_logs(self):
        if self.settings.get("readOnly"):
            return
        game = pathlib.Path(self.settings["game"])
        cutoff = collection_start(self.settings)
        with contextlib.closing(connect(self.root)) as c:
            for p in [
                game / "BepInEx/LogOutput.log",
                *(game / "BepInEx/ORBIT/debug/last-raid").glob("*.log"),
            ]:
                if not p.is_file():
                    continue
                if cutoff is not None and p.stat().st_mtime < cutoff:
                    continue
                try:
                    from collector import add_file, ensure

                    ensure(
                        c,
                        "general-logs",
                        {"map": "General logs", "time": utc(), "recording": "context"},
                    )
                    add_file(c, self.root, "general-logs", "context", p, "launch-log")
                    c.commit()
                except (OSError, RuntimeError):
                    c.rollback()

    def cycle(self, force=False):
        if self.settings.get("readOnly"):
            return
        if not self.lock.acquire(blocking=False):
            return
        try:
            self.preserve_logs()
            self.status = {"state": "collecting", "updated": utc()}
            self.status = collect(self.settings, force)
            atomic_json(self.root / "collector-status.json", self.status)
        except Exception as ex:
            logging.exception("Collection failed; automatic retry scheduled")
            self.status = {"state": "retrying", "error": str(ex), "updated": utc()}
            atomic_json(self.root / "collector-status.json", self.status)
        finally:
            self.lock.release()

    def run(self):
        while not self.stop.is_set():
            self.cycle()
            self.wake.wait(max(5, self.settings["intervalSeconds"]))
            self.wake.clear()

    def start(self):
        if self.settings.get("readOnly"):
            return
        self.thread = threading.Thread(
            target=self.run, name="raid-collector", daemon=True
        )
        self.thread.start()

    def reanalyze(self):
        if self.settings.get("readOnly"):
            return
        if not self.lock.acquire(blocking=False):
            return
        try:
            if active_raid(self.settings["game"]):
                self.status = {
                    "state": "waiting-for-raid-end",
                    "error": "Re-analysis deferred during a raid. Try again afterwards.",
                }
                return
            self.status = {"state": "reanalyzing"}
            with contextlib.closing(connect(self.root)) as c:
                if stale_analyses(c):
                    backup_analysis(c, self.root, ANALYZER_VERSION)
                for row in c.execute("SELECT id FROM raids").fetchall():
                    if active_raid(self.settings["game"]):
                        self.status = {
                            "state": "waiting-for-raid-end",
                            "updated": utc(),
                        }
                        return
                    report = analyze(c, row[0])
                    atomic_json(self.root / "raids" / row[0] / "report.json", report)
            self.status = {"state": "ready", "updated": utc()}
        except Exception:
            logging.exception("Re-analysis failed")
            self.status = {"state": "retrying"}
        finally:
            self.lock.release()


def compact_raid(row):
    meta = json.loads(row["metadata"])
    report = json.loads(row["report"])
    return dict(
        id=row["id"],
        updated=row["updated"],
        map=meta.get("map"),
        time=meta.get("time"),
        rrId=meta.get("rrId"),
        session=meta.get("session"),
        partial=report.get("partial"),
        recording=report.get("recording", "no-comet-evidence"),
        events=sum(report.get("sources", {}).values()),
        bots=len(report.get("actors", {})),
        alerts=report.get("alerts", 0),
        configHash=report.get("configHash"),
        build=(report.get("headers") or [{}])[0].get("assemblyMvid", "unknown"),
        result=meta.get("rr", {}).get("exitStatus", "Unknown"),
    )


def totals(c, filters):
    rows = [
        compact_raid(r)
        for r in c.execute(
            "SELECT * FROM raids ORDER BY json_extract(metadata,'$.time') DESC"
        )
    ]
    options = {
        key: sorted({r[key] for r in rows if r.get(key)})
        for key in ("map", "build", "configHash")
    }
    for key in ("map", "build", "configHash"):
        if filters.get(key):
            rows = [r for r in rows if r.get(key) == filters[key][0]]
    # RR-only history, menu-only sessions and general logs are not combat tests.
    ids = {r["id"] for r in rows if r["bots"] > 0}
    features = {
        s["id"]: dict(
            s,
            raids=0,
            raidIds=[],
            events=0,
            started=0,
            completed=0,
            unconfirmed=0,
            selected=0,
            identifiedRaids=0,
            disabledRaids=0,
            enabledRaids=0,
            unknownConfigRaids=0,
            changedConfigRaids=0,
            effects=collections.Counter(),
            observationFacts=collections.Counter(),
            evidenceNote=evidence_note(s["id"]),
        )
        for s in CATALOG
    }
    for row in c.execute("SELECT * FROM features WHERE profile='' "):
        if row["raid"] not in ids:
            continue
        m = json.loads(row["metrics"])
        f = features.get(row["feature"])
        if f is None:
            f = dict(
                id=row["feature"],
                name=row["feature"],
                status="unknown",
                raids=0,
                raidIds=[],
                events=0,
                started=0,
                completed=0,
                unconfirmed=0,
                selected=0,
                identifiedRaids=0,
                disabledRaids=0,
                enabledRaids=0,
                unknownConfigRaids=0,
                changedConfigRaids=0,
                effects=collections.Counter(),
                observationFacts=collections.Counter(),
                evidenceNote=evidence_note(row["feature"]),
            )
            features[row["feature"]] = f
        for key in ("events", "started", "completed", "unconfirmed", "selected"):
            f[key] += m.get(key, 0)
        f["raids"] += bool(m["events"])
        f["identifiedRaids"] += m.get("coverage") == "identified-attempts"
        f["disabledRaids"] += m.get("configEnabled") is False
        f["enabledRaids"] += m.get("configEnabled") is True
        f["unknownConfigRaids"] += m.get("configEnabled") is None
        f["changedConfigRaids"] += bool(m.get("configChanged"))
        f["effects"].update(m.get("effects", {}))
        f["observationFacts"].update(m.get("observationFacts", {}))
        if m["events"]:
            f.setdefault("raidIds", []).append(row["raid"])
    return {
        "raids": rows,
        "features": list(features.values()),
        "totalEvents": sum(r["events"] for r in rows),
        "filterOptions": options,
        "recordedRaids": len(ids),
        "archiveEntries": len(rows),
    }


class Handler(http.server.BaseHTTPRequestHandler):
    server_version = "RaidObservatory/1"

    @property
    def state(self):
        return self.server.state

    def log_message(self, fmt, *args):
        logging.debug(fmt, *args)

    def allowed(self):
        host = self.headers.get("Host", "")
        return host in (
            f"127.0.0.1:{self.server.server_port}",
            f"localhost:{self.server.server_port}",
        )

    def send(self, value, status=200, mime="application/json; charset=utf-8"):
        raw = value if isinstance(value, bytes) else dumps(value).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", mime)
        self.send_header("Content-Length", str(len(raw)))
        self.send_header("Cache-Control", "no-store")
        self.send_header("X-Content-Type-Options", "nosniff")
        self.send_header(
            "Content-Security-Policy",
            "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; frame-ancestors 'none'",
        )
        self.end_headers()
        self.wfile.write(raw)

    def do_GET(self):
        if not self.allowed():
            return self.send({"error": "Loopback host required"}, 403)
        try:
            self.get()
        except (KeyError, ValueError):
            self.send({"error": "Not found or invalid query"}, 404)
        except (BrokenPipeError, ConnectionResetError):
            pass
        except Exception:
            logging.exception("HTTP error")
            self.send({"error": "Request failed; see collector.log"}, 500)

    def get(self):
        url = urllib.parse.urlsplit(self.path)
        path = urllib.parse.unquote(url.path)
        q = urllib.parse.parse_qs(url.query)
        if path == "/":
            return self.send(
                (HERE / "index.html").read_bytes(), mime="text/html; charset=utf-8"
            )
        if path == "/evidence-view.js":
            return self.send(
                (HERE / "evidence-view.js").read_bytes(),
                mime="text/javascript; charset=utf-8",
            )
        if path == "/api/status":
            return self.send(
                dict(
                    self.state.status,
                    archive=str(self.state.root),
                    rrUrl=self.state.settings["rrUrl"],
                    readOnly=bool(self.state.settings.get("readOnly")),
                    collectionLabel=self.state.settings.get("collectionLabel", ""),
                    collectAfterUtc=self.state.settings.get("collectAfterUtc"),
                    archiveUrl=self.state.settings.get("archiveUrl", ""),
                )
            )
        with contextlib.closing(
            connect(
                self.state.root, read_only=bool(self.state.settings.get("readOnly"))
            )
        ) as c:
            if path == "/api/overview":
                return self.send(totals(c, q))
            if path.startswith("/api/raid/"):
                rid = path.removeprefix("/api/raid/")
                row = c.execute("SELECT * FROM raids WHERE id=?", (rid,)).fetchone()
                if not row:
                    raise KeyError(rid)
                return self.send(
                    {
                        "id": rid,
                        "metadata": json.loads(row["metadata"]),
                        "report": json.loads(row["report"]),
                        "features": [
                            dict(feature=r["feature"], **json.loads(r["metrics"]))
                            for r in c.execute(
                                "SELECT * FROM features WHERE raid=? AND profile='' ORDER BY feature",
                                (rid,),
                            )
                        ],
                        "alerts": [
                            dict(r)
                            for r in c.execute(
                                "SELECT * FROM alerts WHERE raid=? ORDER BY clock LIMIT 1000",
                                (rid,),
                            )
                        ],
                        "artifacts": [
                            dict(r)
                            for r in c.execute(
                                "SELECT * FROM artifacts WHERE raid=?", (rid,)
                            )
                        ],
                    }
                )
            if path == "/api/events":
                rid = q.get("raid", [""])[0]
                where = ["raid=?"]
                args = [rid]
                for key in ("feature", "profile", "stage", "event_key"):
                    if not q.get(key):
                        continue
                    if key == "feature":
                        where.append(
                            "EXISTS(SELECT 1 FROM event_features ef WHERE ef.raid=events.raid AND ef.event_key=events.event_key AND ef.feature=?)"
                        )
                    else:
                        where.append(key + "=?")
                    args.append(q[key][0])
                if q.get("search"):
                    where.append("(reason LIKE ? OR name LIKE ? OR stage LIKE ?)")
                    args.extend(["%" + q["search"][0] + "%"] * 3)
                clause = " AND ".join(where)
                offset = max(0, int(q.get("offset", ["0"])[0]))
                limit = min(200, max(1, int(q.get("limit", ["50"])[0])))
                count = c.execute(
                    "SELECT count(*) FROM events WHERE " + clause, args
                ).fetchone()[0]
                rows = []
                for r in c.execute(
                    "SELECT * FROM events WHERE "
                    + clause
                    + " ORDER BY COALESCE(clock,-1),id LIMIT ? OFFSET ?",
                    args + [limit, offset],
                ):
                    d = dict(r)
                    d["detail"] = unpack(d.pop("payload"))
                    rows.append(d)
                return self.send({"total": count, "offset": offset, "events": rows})
            if path == "/api/bot":
                rid = q["raid"][0]
                p = q["profile"][0]
                return self.send(
                    [
                        dict(feature=r["feature"], **json.loads(r["metrics"]))
                        for r in c.execute(
                            "SELECT * FROM features WHERE raid=? AND profile=?",
                            (rid, p),
                        )
                    ]
                )
            if path == "/api/compare":
                a = q["a"][0]
                b = q["b"][0]
                out = []
                for rid in (a, b):
                    row = c.execute("SELECT * FROM raids WHERE id=?", (rid,)).fetchone()
                    if not row:
                        raise KeyError(rid)
                    out.append(
                        {
                            "raid": compact_raid(row),
                            "features": {
                                r["feature"]: json.loads(r["metrics"])
                                for r in c.execute(
                                    "SELECT * FROM features WHERE raid=? AND profile=''",
                                    (rid,),
                                )
                            },
                        }
                    )
                ca = json.loads(
                    c.execute("SELECT report FROM raids WHERE id=?", (a,)).fetchone()[0]
                ).get("configs", {})
                cb = json.loads(
                    c.execute("SELECT report FROM raids WHERE id=?", (b,)).fetchone()[0]
                ).get("configs", {})

                def flat(v, p=""):
                    if isinstance(v, dict):
                        return {
                            k2: v2
                            for k, x in v.items()
                            for k2, v2 in flat(x, p + "." + k if p else k).items()
                        }
                    return {p: v}

                def latest(configs):
                    if not configs:
                        return {}
                    snapshot = dict(
                        configs[
                            max(configs, key=lambda k: int(k) if k.isdigit() else -1)
                        ]
                    )
                    snapshot.pop("revision", None)
                    return snapshot

                fa = flat(latest(ca))
                fb = flat(latest(cb))
                return self.send(
                    {
                        "runs": out,
                        "configChanges": [
                            {"setting": k, "before": fa.get(k), "after": fb.get(k)}
                            for k in sorted(fa.keys() | fb.keys())
                            if fa.get(k) != fb.get(k)
                        ],
                        "note": "Different maps, populations, settings and partial recordings prevent a causal performance or success comparison.",
                    }
                )
            if path.startswith("/api/artifact/"):
                row = c.execute(
                    "SELECT * FROM artifacts WHERE id=?", (int(path.split("/")[-1]),)
                ).fetchone()
                if not row:
                    raise KeyError(path)
                self.send_response(200)
                self.send_header("Content-Type", "application/octet-stream")
                self.send_header(
                    "Content-Disposition",
                    f'attachment; filename="artifact-{row["id"]}.gz"',
                )
                self.end_headers()
                with (self.state.root / "blobs" / (row["hash"] + ".gz")).open(
                    "rb"
                ) as f:
                    shutil.copyfileobj(f, self.wfile)
                return
            if path.startswith("/api/export/"):
                rid = path.removeprefix("/api/export/")
                row = c.execute("SELECT * FROM raids WHERE id=?", (rid,)).fetchone()
                if not row:
                    raise KeyError(rid)
                with tempfile.TemporaryFile() as file:
                    with zipfile.ZipFile(
                        file, "w", compression=zipfile.ZIP_STORED, allowZip64=True
                    ) as z:
                        z.writestr("report.json", row["report"])
                        z.writestr("metadata.json", row["metadata"])
                        items = [
                            dict(r)
                            for r in c.execute(
                                "SELECT * FROM artifacts WHERE raid=?", (rid,)
                            )
                        ]
                        z.writestr("manifest.json", dumps(items))
                        for sha in {r["hash"] for r in items}:
                            z.write(
                                self.state.root / "blobs" / (sha + ".gz"),
                                "blobs/" + sha + ".gz",
                            )
                    size = file.tell()
                    file.seek(0)
                    self.send_response(200)
                    self.send_header("Content-Type", "application/zip")
                    self.send_header("Content-Length", str(size))
                    self.send_header(
                        "Content-Disposition",
                        'attachment; filename="raid-evidence.zip"',
                    )
                    self.end_headers()
                    shutil.copyfileobj(file, self.wfile)
                return
        self.send({"error": "Not found"}, 404)

    def do_POST(self):
        origin = self.headers.get("Origin", "")
        expected = {
            f"http://127.0.0.1:{self.server.server_port}",
            f"http://localhost:{self.server.server_port}",
        }
        if (
            not self.allowed()
            or origin not in expected
            or self.headers.get("X-Raid-Local") != "1"
        ):
            return self.send({"error": "Local browser required"}, 403)
        if self.state.settings.get("readOnly"):
            return self.send({"error": "Archived collection is read-only"}, 403)
        if self.path == "/api/collect":
            self.state.wake.set()
            return self.send({"queued": True}, 202)
        if self.path == "/api/reanalyze":
            threading.Thread(target=self.state.reanalyze, daemon=True).start()
            return self.send({"queued": True}, 202)
        self.send({"error": "Not found"}, 404)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--settings", default=str(HERE / "settings.json"))
    parser.add_argument("--once", action="store_true")
    parser.add_argument("--reanalyze", action="store_true")
    parser.add_argument("--open", action="store_true")
    args = parser.parse_args()
    settings = query_settings(args.settings)
    root = pathlib.Path(settings["archive"])
    if settings.get("readOnly"):
        if args.once or args.reanalyze:
            raise ValueError("Archived collections are read-only")
        with contextlib.closing(connect(root, read_only=True)) as c:
            c.execute("SELECT id FROM raids LIMIT 1").fetchone()
        server = http.server.ThreadingHTTPServer(
            ("127.0.0.1", settings["port"]), Handler
        )
        server.state = State(settings)
        if args.open:
            webbrowser.open(f'http://127.0.0.1:{settings["port"]}')
        try:
            server.serve_forever(poll_interval=0.5)
        finally:
            server.server_close()
        return
    root.mkdir(parents=True, exist_ok=True)
    handler = logging.handlers.RotatingFileHandler(
        root / "collector.log",
        maxBytes=2 * 1024 * 1024,
        backupCount=3,
        encoding="utf-8",
    )
    logging.basicConfig(
        handlers=[handler],
        level=logging.INFO,
        format="%(asctime)s %(levelname)s %(message)s",
    )
    # Serialize all writers across auto-start, shortcut launches and CLI backfills.
    lock = (root / "collector.lock").open("a+b")
    lock.seek(0)
    lock.write(b"0")
    lock.flush()
    lock.seek(0)
    try:
        if os.name == "nt":
            import msvcrt

            msvcrt.locking(lock.fileno(), msvcrt.LK_NBLCK, 1)
        else:
            import fcntl

            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
    except OSError:
        if args.open:
            webbrowser.open(f'http://127.0.0.1:{settings["port"]}')
        return
    state = State(settings)
    if args.once:
        state.cycle()
        print(dumps(state.status))
        return
    if args.reanalyze:
        state.reanalyze()
        print(dumps(state.status))
        return
    server = http.server.ThreadingHTTPServer(("127.0.0.1", settings["port"]), Handler)
    server.state = state
    state.start()
    logging.info("Listening on 127.0.0.1:%s", settings["port"])
    if args.open:
        webbrowser.open(f'http://127.0.0.1:{settings["port"]}')
    try:
        server.serve_forever(poll_interval=0.5)
    finally:
        state.stop.set()
        state.wake.set()
        server.server_close()
        lock.close()


if __name__ == "__main__":
    main()
