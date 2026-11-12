# zzap behavior baseline

This development baseline replaces COMET's additional tactical eligibility and
continuation rules with the corresponding zzap rules. New restrictions require
a recorded raid problem, a source comparison and a focused regression case.
Do not add a defensive tactical veto merely because it seems reasonable.

Reference: R_F's SAIN zzap snapshot
`fbb86b838e0fa692b759b2c0b11fc30158a53876`.
Configuration marker: `zzap-fbb86b83-1`.
The host remains official SAIN 4.5.1; the fork DLL is not distributed or installed.

## Behavioral changes

| Area | Baseline restored | Additional COMET restrictions removed |
| --- | --- | --- |
| Decisions | Source hidden/visible utility, ranking, selection windows and retained-choice margin; RR displays scores multiplied by 100 | Progress penalties, generic execution retry bans, additional plan validity/snapshot/commitment rules |
| Cover and movement | Source candidate geometry, path budgets, peek spots, movement timings and cover continuation | Extra height/path/exposure checks, stalled-progress proof gates, forced periodic restarts and invented fallback ranking |
| Squad tactics | Source crossfire/storm/support selection, reservations and execution windows | Ongoing support-loss, role-change, visibility-blip, target-shift and progress vetoes |
| Doors | Source tactical geometry, timings, session lifetime and interrupted-session handling | Resume-count cap, quiet reset window, extra stamina/weapon/squad/visibility requirements and inherited plan/contact deadlines |
| Peeks, feints and pursuit | Source eligibility, chance, movement cycles and retry timing | Requirement for new evidence before retrying; route revalidation, stability/progress proofs and extra encounter-history bans |
| Shooting and equipment | Source ADS, light, suppression and squad fire-lane behavior | Additional velocity, occlusion, swept prediction, operation-age, firing-clear delay and ongoing sidestep-lease gates |
| Grenades | Source chance, target selection, native feasibility and full trajectory candidate search/cache | Duplicate evidence windows, stable-target delay, extra collision sweeps/landing-navmesh test, walking-route-to-impact test and additional fuse margin |
| Own grenades | Source own-projectile tracking, 0.7 s reaction delay, 6/10 m danger/escape radii, eight-second lifetime and one solid cover ray | Whole-route sampling, global movement interception, extra material tests and shared throw-awareness vetoes |
| Medicine | Source medical choice, personality-based relaxed surgery, posture and post-combat behavior | Additional quiet/danger/stability and action-completion evidence gates; custom surgical cancellation radii and eight-second retry ban |
| Reloads and weapon swaps | Source reload utility, loaded pistol fallback, range-class selection, five-second return holdoff and quick-reload spare count | Optics requirements, stable evidence, ready-mag caps, aggregate switch arbiter and additional no-fire/under-fire/cover vetoes |
| Search abandonment | Native visited marker and 20 seconds without refreshed information | Inspection duration, facing, floor, repeated ray checks and revisit blacklist |
| Idle equipment | Source two-second idle darkness, ten-second reapply interval and 25-second post-combat ready stance | Owner/objective whitelist, one-shot darkness lease and nearby-sound duration extension |
| Squad voice | Source on/off switch, muted by default | Additional chance roll and per-bot/per-squad cooldowns |
| Hands recovery | Source six-second stale `Reloading` flag watchdog; a real ongoing reload is never interrupted | Additional weapon-switch lock, attempt cap, inventory reconciliation and forced animation completion |
| Perception and context | Source hearing, visual memory, threat, fear and equipment modifiers | Additional age/speed limits, smoothed fear threshold and COMET threat-switch margin |
| Observation | Async logs, performance counters, RR journal and outcome records | Observers no longer veto an action because they lack proof that it started or progressed |

The source's own checks remain. Examples: contact expiry, native grenade
feasibility, squad-friendly-fire checks, action-specific time limits and the
three-second stalled-cover rule. Removing these too would not be a source port.

## Integration boundaries

- COMET still scopes itself to standard PMCs and AI Player Scavs. Ordinary scavs,
  bosses, custom roles and human players remain excluded.
- Only the authoritative AI host acts in Fika. Ghost bots and externally owned
  movement remain outside COMET control. Raid cleanup, object validity, fault
  isolation and compatibility checks remain.
- The server UI, RR telemetry, async logging and data collector remain COMET
  integrations, not changes to tactical eligibility.
- Features previously excluded or disabled by the user are not silently enabled.
  Cross-raid player learning remains excluded.
- The optional door-mod code previously excluded by the user remains excluded.
  The existing native kick transport is an integration exception: it waits for
  the game's actual kick animation instead of copying the fork's embedded ram
  implementation. It no longer borrows a deadline from an unrelated COMET plan.

This is a behavioral port through original SAIN hooks, not a byte-identical fork.
Automated source and integration checks do not establish identical results in
Unity, Fika or every raid situation.

## Saved settings

On first load of an older behavior baseline, the server creates a timestamped
`config.json.before-zzap-*.bak` containing the exact original bytes. It then resets
behavior parameters to this baseline, retaining the root enable flag, module
enable flags and diagnostic settings. Migration is atomic and runs once per
configuration marker. Saved presets receive the same baseline migration when
used. Retired knobs remain readable for old JSON but are hidden from the editor
and difficulty-profile overrides.

## Validation and future changes

- Build client, Fika and server; run the core policy suites and production-adapter
  harnesses with `Tools/verify.ps1`.
- Validate hook signatures and transpilers against the pinned official SAIN/EFT
  DLLs, and verify the RR frontend and recording contracts.
- Keep historical RR traces readable. A rejected attempt remains diagnostic
  evidence; it must not become an implicit cooldown or action ban.
- For a future deviation: record the raid/bot/time, source behavior, actual failure,
  smallest proposed change and regression test. Compare with a new raid before
  broadening the change.
