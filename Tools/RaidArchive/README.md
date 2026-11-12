# COMET Raid Observatory

A local archive and dashboard for real raid evidence. It watches the normal SPT installation, waits for a finished COMET journal, and keeps a separate copy of the available COMET and Raid Review data. Nothing is uploaded.

## Use

Run `setup.ps1` once with Python 3.12 or newer installed. It creates a desktop shortcut and a Windows Startup shortcut, then starts the collector without a console window. Open **http://127.0.0.1:8791**. After that, play normally. No button is needed after each raid.

Defaults are `C:\Games\SPT-4.1` for the game and `C:\Users\<you>\Documents\COMET-RaidArchive` for the archive. `settings.json` is local and ignored by Git. No headless installation is accessed.

- **All raids:** search, map/build/configuration filters, coverage across 132 feature cards, comparison between raids.
- **Coverage:** inspect a feature or filter by actor. Decisions, requests, acceptance, observed starts and reported completions are separate.
- **Review:** evidence to investigate and observed versus expected choices from recorded random draws.
- **Events:** filter by feature, actor or reason. Each record retains its source and line. Exact RR timestamps are preserved when available.
- **Performance:** recorded COMET costs, longest frames and nearby events. Performance capture must have been enabled in COMET for this data to exist.
- **Sources:** download compressed originals or export the raid as a ZIP, including configuration snapshots and raw evidence.

For a one-off collection use `python app.py --once`. To rebuild reports from the indexed events use `python app.py --reanalyze`. The dashboard offers the same operations. These commands use the configured archive and do not modify game files. Only one collector can run for an archive at a time.

## What the numbers mean

An accepted command does not prove the bot moved. A completed policy does not prove its animation looked right or that the tactic was effective. Interruptions can be legitimate, and absent events do not prove that a feature had an opportunity to run.

Distinct attempts are counted only where the journal supplies a stable occurrence identity. Otherwise the dashboard shows observations and state/reason counts. Repeated snapshots are not turned into separate actions. Attached context is linked to its parent event, but does not inherit the parent's execution status. Cards that were excluded, deferred or folded into another feature remain explicitly labelled.

Changing settings during a raid is recorded by revision. Mixed enable/disable states are not presented as disabled for the entire raid. Build identity comes from the raid's header; installed DLL hashes captured later are labelled as collection-time context.

COMET and RR copies are deduplicated by session and sequence. The local record wins, while RR provides replay timestamps. The RR database is read in a consistent SQLite transaction, including its WAL. Raid position files are archived separately.

Logs with missing footers, missing segments, dropped events, write errors, malformed records or unsupported schemas are marked partial. Older RR-only raids remain available without pretending they contain COMET diagnostics. Old evidence already deleted from the source cannot be recovered by this tool.

## Collection and storage

Heavy collection waits while the latest COMET journal is open and the game is running. Finished files settle for 30 seconds. A crash is imported as partial after the game closes. The worker retries failures and revisits recent raids for RR's late flush. Historical RR raids are backfilled once; currently retained RR history may be limited by its own retention setting.

Raw files are gzip-compressed and stored by SHA-256 in `blobs`. A file that changes while being copied is retried. Each raid has a manifest and a report under `raids`. `archive.sqlite` contains the searchable index; `collector.log` records collection failures. Archive data is never automatically deleted. Collection pauses below 1 GiB of free disk space. Back up the whole archive directory, including SQLite's WAL when present, or copy it while the collector is stopped.

The dashboard listens only on localhost and does not require the SPT server to be running. Removing its Windows Startup shortcut disables automatic launch. The desktop shortcut can start it again. No game or server process is started or stopped by the tool.

## Separate test collections

An archived collection can be kept separately while the normal dashboard starts fresh.
Stop the collector before moving its archive, checkpoint SQLite, and keep the whole
directory (including blobs, raid manifests, general logs and analysis backups).
Set `collectAfterUtc` in the new collection's settings to an ISO timestamp with a
timezone. Import uses the raid's start time, not its file modification time, so old
logs and late RR writes cannot refill the new dashboard. `excludedSessions` and
`excludedRrRaids` can additionally pin the identities present at the boundary.
General logs predating the boundary are retained in the old collection.

For browsing old evidence, use separate settings pointing to the old archive with
`readOnly: true` and a different local port. This mode neither collects nor
re-analyzes data, and opens the database read-only. `collectionLabel` labels the
dashboard; `archiveUrl` can link the active collection to the local archive viewer.
Cross-collection comparison is done by opening the two dashboards side by side;
the existing Compare raids button compares raids within its own collection.

## COMET journal changes

COMET keeps its bounded asynchronous writer. Detailed files now rotate into `.part0001.jsonl`, `.part0002.jsonl`, etc. instead of discarding detail at 64 MiB. The final footer reports segment count and losses. New raid headers record version, assembly identity, map, authority and diagnostic mode. These changes take effect on the next game launch.

A future Quiet mode should filter expensive diagnostics before serialization while retaining useful warnings, errors and compact summaries. It is not implemented by this collector; detailed logging remains enabled for current testing. A recording mode must always be recorded so reduced detail cannot be mistaken for failed features.

## Validation

`python -m unittest -v test_archive` tests archive integrity, duplicate handling, WAL reads, incomplete journals, occurrence counting, mixed configuration, schema handling, the background watcher and local HTTP boundaries. COMET's C# tests also verify asynchronous segment rotation without missing or reordered events. These tests do not simulate Unity animations or replace raid validation.
