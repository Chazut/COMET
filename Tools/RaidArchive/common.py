"""Local-only raid archive primitives. Never modify a game file."""

from __future__ import annotations
import contextlib, datetime as dt, gzip, hashlib, json, os, pathlib, sqlite3, tempfile, zlib

HERE = pathlib.Path(__file__).resolve().parent
VERSION = 1


def utc():
    return dt.datetime.now(dt.timezone.utc).isoformat()


def dumps(value):
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"), allow_nan=False)


def load(path, default=None):
    try:
        return json.loads(pathlib.Path(path).read_text(encoding="utf-8-sig"))
    except FileNotFoundError:
        return default


def atomic_json(path, value):
    path = pathlib.Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.NamedTemporaryFile(
        "w", encoding="utf-8", dir=path.parent, delete=False
    ) as f:
        json.dump(value, f, ensure_ascii=False, indent=2, allow_nan=False)
        temp = f.name
    os.replace(temp, path)


def fingerprint(path):
    p = pathlib.Path(path)
    s = p.stat()
    return f"{s.st_size}:{s.st_mtime_ns}"


def digest(path):
    h = hashlib.sha256()
    with pathlib.Path(path).open("rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def pack(value):
    return zlib.compress(dumps(value).encode("utf-8"))


def unpack(value):
    return json.loads(zlib.decompress(value))


def timestamp(value):
    try:
        stamp = dt.datetime.fromisoformat(str(value).replace("Z", "+00:00"))
        return stamp.timestamp() if stamp.tzinfo is not None else None
    except (ValueError, TypeError, OverflowError):
        return None


def collection_start(settings):
    value = settings.get("collectAfterUtc")
    if value is None:
        return None
    result = timestamp(value)
    if result is None:
        raise ValueError("collectAfterUtc must be an ISO timestamp with a timezone")
    return result


def connect(root, read_only=False):
    if read_only:
        path = (pathlib.Path(root) / "archive.sqlite").resolve()
        c = sqlite3.connect(path.as_uri() + "?mode=ro", uri=True, timeout=30)
        c.row_factory = sqlite3.Row
        c.execute("PRAGMA query_only=ON")
        return c
    root = pathlib.Path(root)
    root.mkdir(parents=True, exist_ok=True)
    c = sqlite3.connect(root / "archive.sqlite", timeout=30)
    c.row_factory = sqlite3.Row
    c.execute("PRAGMA journal_mode=WAL")
    c.execute("PRAGMA foreign_keys=ON")
    if c.execute(
        "SELECT 1 FROM sqlite_master WHERE type='table' AND name='event_features'"
    ).fetchone():
        return c
    c.executescript("""
    CREATE TABLE IF NOT EXISTS raids(id TEXT PRIMARY KEY, updated TEXT, metadata TEXT, report TEXT);
    CREATE TABLE IF NOT EXISTS artifacts(id INTEGER PRIMARY KEY, raid TEXT, kind TEXT, original TEXT,
      hash TEXT, size INTEGER, captured TEXT, attribution TEXT,
      UNIQUE(raid,kind,original,hash), FOREIGN KEY(raid) REFERENCES raids(id));
    CREATE TABLE IF NOT EXISTS events(id INTEGER PRIMARY KEY, raid TEXT, event_key TEXT,
      feature TEXT, profile TEXT, bot TEXT, name TEXT, personality TEXT, stage TEXT, reason TEXT,
      clock REAL, utc TEXT, source TEXT, line INTEGER, payload BLOB, UNIQUE(raid,event_key));
    CREATE INDEX IF NOT EXISTS event_lookup ON events(raid,feature,profile,clock);
    CREATE TABLE IF NOT EXISTS features(raid TEXT, feature TEXT, profile TEXT, metrics TEXT,
      PRIMARY KEY(raid,feature,profile));
    CREATE TABLE IF NOT EXISTS alerts(id INTEGER PRIMARY KEY,raid TEXT,feature TEXT,profile TEXT,
      kind TEXT,message TEXT,event_key TEXT,clock REAL);
    CREATE TABLE IF NOT EXISTS imports(path TEXT PRIMARY KEY,fingerprint TEXT,raid TEXT);
    CREATE TABLE IF NOT EXISTS file_cache(path TEXT PRIMARY KEY,fingerprint TEXT,hash TEXT,size INTEGER);
    CREATE TABLE IF NOT EXISTS event_features(raid TEXT,event_key TEXT,feature TEXT,PRIMARY KEY(raid,feature,event_key));
    """)
    return c


def backup_analysis(c, root, version):
    """Coherent WAL-aware snapshot once before a new analyzer replaces reports."""
    folder = pathlib.Path(root) / "analysis-backups"
    folder.mkdir(exist_ok=True)
    target = folder / ("before-" + version + ".sqlite")
    if target.exists():
        return target
    if c.in_transaction:
        raise RuntimeError("Backup must precede analysis/import writes")
    if not c.execute(
        "SELECT 1 FROM raids WHERE report IS NOT NULL AND report!='{}' LIMIT 1"
    ).fetchone():
        return None
    temporary = target.with_suffix(".pending")
    try:
        with contextlib.closing(sqlite3.connect(temporary)) as dest:
            c.backup(dest, pages=512)
        os.replace(temporary, target)
    finally:
        with contextlib.suppress(FileNotFoundError):
            temporary.unlink()
    return target


def store_file(root, path):
    """Content-addressed originals, compressed; copy only once, detect a concurrent write."""
    root = pathlib.Path(root)
    path = pathlib.Path(path)
    before = fingerprint(path)
    blobdir = root / "blobs"
    blobdir.mkdir(exist_ok=True)
    h = hashlib.sha256()
    size = 0
    fd, temporary = tempfile.mkstemp(dir=blobdir, suffix=".pending")
    os.close(fd)
    try:
        with path.open("rb") as src, gzip.open(temporary, "wb", compresslevel=3) as dst:
            for chunk in iter(lambda: src.read(1024 * 1024), b""):
                h.update(chunk)
                size += len(chunk)
                dst.write(chunk)
        if before != fingerprint(path):
            raise RuntimeError(f"Source still changing: {path.name}")
        sha = h.hexdigest()
        dest = blobdir / (sha + ".gz")
        if dest.exists():
            os.unlink(temporary)
        else:
            os.replace(temporary, dest)
        return sha, size
    except BaseException:
        with contextlib.suppress(FileNotFoundError):
            os.unlink(temporary)
        raise


def store_value(root, value):
    raw = dumps(value).encode("utf-8")
    sha = hashlib.sha256(raw).hexdigest()
    blobdir = pathlib.Path(root) / "blobs"
    blobdir.mkdir(exist_ok=True)
    path = blobdir / (sha + ".gz")
    if not path.exists():
        with tempfile.NamedTemporaryFile(dir=blobdir, delete=False) as f:
            temp = f.name
        with gzip.open(temp, "wb", compresslevel=3) as f:
            f.write(raw)
        os.replace(temp, path)
    return sha, len(raw)


def artifact(c, raid, kind, original, sha, size, attribution="exact"):
    c.execute(
        "INSERT OR IGNORE INTO artifacts(raid,kind,original,hash,size,captured,attribution) VALUES(?,?,?,?,?,?,?)",
        (raid, kind, str(original), sha, size, utc(), attribution),
    )


def read_blob(root, sha):
    if len(sha) != 64 or any(x not in "0123456789abcdef" for x in sha):
        raise ValueError("Invalid hash")
    return gzip.open(pathlib.Path(root) / "blobs" / (sha + ".gz"), "rb")


def number(value):
    import math

    return (
        float(value)
        if isinstance(value, (int, float))
        and not isinstance(value, bool)
        and math.isfinite(value)
        else None
    )


def at(value, path):
    for key in path.split("."):
        if not isinstance(value, dict):
            return None
        value = value.get(key)
    return value


def insert_event(c, raid, e, source, line):
    if not isinstance(e, dict):
        return False
    seq = e.get("sequence")
    session = e.get("session", "")
    key = (
        f"{session}:{seq}"
        if isinstance(seq, int)
        else hashlib.sha256(dumps(e).encode()).hexdigest()
    )
    # The local stream wins over the RR copy, which may include transport metadata.
    existing = c.execute(
        "SELECT source,payload FROM events WHERE raid=? AND event_key=?", (raid, key)
    ).fetchone()
    if existing:
        previous = unpack(existing["payload"])
        if source.startswith("rr:") or not existing["source"].startswith("rr:"):
            # Local evidence wins, while RR supplies the exact replay clock.
            if source.startswith("rr:") and not existing["source"].startswith("rr:"):
                for k in ("_rrTimeMs", "_rrRaidId"):
                    if k in e:
                        previous[k] = e[k]
                c.execute(
                    "UPDATE events SET payload=? WHERE raid=? AND event_key=?",
                    (pack(previous), raid, key),
                )
            return False
        for k in ("_rrTimeMs", "_rrRaidId"):
            if k in previous:
                e[k] = previous[k]
    c.execute(
        """INSERT INTO events(raid,event_key,feature,profile,bot,name,personality,stage,reason,clock,utc,source,line,payload)
      VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?) ON CONFLICT(raid,event_key) DO UPDATE SET
      source=excluded.source,line=excluded.line,payload=excluded.payload,
      feature=excluded.feature,profile=excluded.profile,bot=excluded.bot,name=excluded.name,
      personality=excluded.personality,stage=excluded.stage,reason=excluded.reason,clock=excluded.clock,utc=excluded.utc""",
        (
            raid,
            key,
            str(e.get("feature", "")),
            str(e.get("profileId", "")),
            str(e.get("botInstance", "")),
            str(e.get("name", "")),
            str(e.get("personality") or ""),
            str(e.get("stage", "")),
            str(e.get("reason", "")),
            number(e.get("realtime")),
            str(e.get("utc", "")),
            source,
            line,
            pack(e),
        ),
    )
    return True
