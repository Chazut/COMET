# zzap behavior baseline

This development baseline replaces COMET's additional tactical eligibility and
continuation rules with the corresponding zzap rules. New restrictions require
a recorded raid problem, a source comparison and a focused regression case.
Do not add a defensive tactical veto merely because it seems reasonable.

Reference: R_F's SAIN zzap snapshot
`fbb86b838e0fa692b759b2c0b11fc30158a53876`.
Configuration marker: `zzap-fbb86b83-2`.
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
  bosses, custom roles and human players remain excluded from its tactics.
  P07 is a narrow source exception: registered custom roles with BrainsToApply or
  BaseBrain receive zzap's exact injury contact and under-fire update when hit.
  Standard roles retain native SAIN hit handling; this exception does not enable
  other COMET tactics for custom roles.
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

## Follow-up source alignment

- D08 uses its full 120-second search contact window, constant source chance and
  the same clear-session grenade preparation as D07. Native Search selects this
  variant even with fresh information; D07 retains its 60-second window. No
  ongoing search-only veto.
- S01 starts from the supporting squadmate's visible enemy position and treats
  either participant being indoors as the indoor layout.
- P03 uses the native personality near-shot distance, a one-shot look command,
  eight-second look and 60-second distant-search intervals. No extra age/lease gate.
- P04 applies source height bands (2.5/5.5 m) indoors without line of sight,
  multiplying the native range instead of crediting its existing attenuation.
- P05 removes third-party/unknown modifiers within 6 m and caps the corrected
  modifier at 1 within 3 m, before native repeat-contact bonuses.
- G10 adds a nearby explosion owner contact and retains native position dispersion
  and squad reporting. No throw-witness proof or anonymous-glance replacement.
- G09 leaves real flashes to native SAIN (including live memory). Continuous gas
  extends blindness without repeating the initial flash side effects.
- G08 keeps initial scatter for five seconds, then exposure/blindness. The custom
  escape path and retry lease are removed. Manimal 2.0.0/2.0.1 real volumes, variants
  and protection remain the previously requested provider compatibility boundary,
  rather than copying the old provider's fixed-radius cloud approximation.

## Saved settings

On first load of an older behavior baseline, the server creates a timestamped
`config.json.before-zzap-*.bak` containing the exact original bytes. It then resets
behavior parameters to this baseline, retaining the root enable flag, module
enable flags and diagnostic settings. Migration is atomic and runs once per
configuration marker. Saved presets receive the same baseline migration when
used. Retired knobs remain readable for old JSON but are hidden from the editor
and difficulty-profile overrides.

## Validation and future changes

### Foliage and flashlight report: native-shot observations

- Compare official SAIN 4.5.1 with the pinned zzap source before changing foliage
  behavior. Native flashlight detection also runs during daytime: an observer
  can investigate a visible light projection near an approximate carrier
  position. This does not directly set the carrier visible or authorize a shot.
  White light also removes the native weather visibility penalty in its distance
  band; it is not a universal daytime sight-speed multiplier.
- SAIN separates cached eye visibility from muzzle clearance. Their aggregate
  flags can come from different body parts. Native suppression and COMET's
  source-aligned prefire can shoot toward a known position without current
  visibility. The inspected COMET gain-sight, shot geometry, prefire and steering
  paths do not establish an added foliage bypass. A clip alone cannot identify
  which of these paths fired; ordinary Scavs and boss guards are outside COMET's
  supported tactical scope.
- T01 now observes `PlayerComponent.OnMakingShot`, separately from requests to
  start firing. It samples target identity/source, native cached visibility,
  muzzle-clear/visible body-part counts, manual-fire reason, contact age and
  target white light at most once per bot per second. Cumulative counts are
  emitted at most once per five seconds, with a final lifecycle flush.
- These are read-only observations: no extra raycasts, aim-point selection,
  inventory scans or firing vetoes. A failed shot observer disables only that
  observer. A native last-shot target is labeled as such, and other-layer or
  unknown manual targets remain unknown. Cached flags do not prove a foliage
  collider blocked the shot. Record a new raid and correlate an actual clip
  before making a behavioral change.

### Raid 443: steering fire and grenade preparation

- Evidence: `8070d276-b0be-4e95-993e-f1e30380cd06`, skylo123, angle reached
  at 16:47.463, first shot at 17:02.254 after taking damage at 17:01.707.
  Squad steering replaced the native callback without its fire attempt.
- Restore the source fire attempt before custom look commands in all four
  steering replacements: squad angles, corner pursuit, cover feints and
  post-combat movement. Keep each source action's suppression and steering
  order. A hidden primary enemy must not prevent shooting another visible enemy.
- DustAndPellets had a prepared grenade trajectory after the initial refusal,
  while hidden utility continued replaying that refusal. Hidden/visible utility
  now caches the preference ranking and refreshes execution availability on each
  call, as the source's decision loop does. Native refusal does not erase the
  ranking, redraw the choice or ban the action from subsequent calls.
- Prepare grenade arcs through native `SetThrowData`, including group
  notification, instead of writing only `AIGreanageThrowData`. Reusing the same
  cached arc does not repeat preparation. This repairs a separate missing native
  side effect; it is not evidence that every unthrown grenade was blocked by it.
- Regression coverage includes the actual compiled DLL's four callback call
  sites, secondary-target firing, suppression/look ordering, blocked-to-ready
  capability changes within a cached ranking, and native grenade preparation.
  `T01/native-fire-check` aggregates checks/acceptance/suppression per bot at most
  once per five seconds. Grenade selection, native progress and confirmed
  projectile events remain distinct. Confirm real firing/throws in the next raid;
  detached harnesses do not execute Unity animations or prove complete port parity.

### Flashlight and laser detection: inherited native defects

- Report: Scootis described SAIN's light/laser detection as still broken after
  a partial merge. Source comparison and installed official SAIN 4.5.1 IL
  confirm two defects; the system itself is registered and is not entirely off.
  `FlashlightRaycastJob`, `RaycastJob` and `LightDetectionClass` have the same
  relevant implementation in the pinned zzap reference.
- The observer-to-light `List<Vector3>` ray constructor uses a non-normalized
  direction and a constant 1 m range. COMET replaces only its light-job call
  site with normalized, full-distance rays, matching the correct native array
  overload. Other consumers of the constructor remain untouched.
- The native `AddLaserPathPoints` helper has no caller. COMET calls that existing
  helper after native source ray completion, using pooled private point lists.
  This restores its 5 m beam sampling, 75 m cap and first-hit truncation, including
  lasers without a surface impact. Native lists remain unchanged for other bots.
- This accepted fix intentionally differs from both baselines. It applies only
  to COMET-supported observers on the AI authority. Native enemy selection,
  125 m range, IR/NVG gate, ray mask and approximate search reaction remain.
  It grants neither visual identification nor shooting permission. It does not
  address every possible device-state issue or prove foliage detection in game.
- T01 records bounded `light-detection` observations and ten-second counters,
  distinguishing an occluded job from a native investigation request. A request
  is not proof the bot completed a search. Quiet logging leaves RR events intact.
  Stop and raid cleanup release cached references; completed native arrays are
  never retained. No additional cooldown or gameplay veto is introduced.
- `LightDetectionHarness` executes the production Harmony transpiler and adapter
  against detached native-shaped jobs: occlusion beyond 1 m, surface-free lasers,
  beam truncation, IR vision, authority/scope, cleanup and diagnostic behavior.
  The real-DLL integration audit separately checks actual SAIN hook signatures
  and IL matching. Unity physics and the resulting raid behavior still require
  an in-game test.

- Build client, Fika and server; run the core policy suites and production-adapter
  harnesses with `Tools/verify.ps1`.
- Validate hook signatures and transpilers against the pinned official SAIN/EFT
  DLLs, and verify the RR frontend and recording contracts.
- Keep historical RR traces readable. A rejected attempt remains diagnostic
  evidence; it must not become an implicit cooldown or action ban.
- For a future deviation: record the raid/bot/time, source behavior, actual failure,
  smallest proposed change and regression test. Compare with a new raid before
  broadening the change.

### Raid 445: stale grenade reaction and non-GC decision spike

- Customs, 2026-11-14: AK_kN stayed stationary from about 85 s until death at
  641.53 s, with 545.88 s recorded as SAIN AvoidGrenade and no shots. The loot
  transaction had already released its lock when combat interrupted it. The old
  diagnostics did not record the native danger identity; the exact projectile
  responsible for this raid cannot be established retrospectively.
- Restore the pinned source's smoke classification (runtime SmokeGrenade or
  smoke collision sound), nearest danger within 18 m, and rebuilding from live
  trackers instead of retaining an old DangerGrenade. Verified Manimal CS gas
  keeps the source's first five seconds of native dodge. Ordinary smoke must not
  preserve Scatter. Own-grenade tracking uses the same smoke classification.
- AvoidGrenadeAction is owned only with an actual selected hazard. Losing it
  releases COMET's movement/prone state and yields to native behavior. Enemy
  lifetime still belongs to SAIN; no new expiry or tactical gate is introduced.
  Transition-only `G05/native-reaction` events expose old/new danger identity,
  reaction, range and live tracker count in the async diagnostics and RR stream.
- Frame 6371 took 162.918 ms, with 102.8774 ms inside COMET and no GC collection.
  Choose had 30.116 ms self time; the previous instrumentation cannot establish
  what caused all of it. Cache the exact sorted squad-member identity using a
  weak dictionary key, checking membership every read without a refresh delay.
  Reuse grenade selection scratch collections/configuration and avoid duplicate
  lookup closures. These remove repeated work without altering action selection.
- Add nested DecisionEvidence scopes for utility capture and native/visible
  evidence preparation. This measures the formerly opaque decision work rather
  than adding throttling, cancellation or eligibility rules. A new raid is still
  required to assess the residual spike and confirm AK_kN's failure is absent.
- Regression coverage: smoke with misleading frag ThrowType, removed/inactive/
  distant trackers, lost hazard between selection and action, movement/prone
  release, repeated diagnostic deduplication, and squad member arrival/departure/
  same-count replacement with zero allocations over 10,000 unchanged reads.
- Validation: core 110,361 checks; gas 326, crossfire 285, grenade reaction 63,
  dispersion 7, own grenade 18, performance 134, embedded Fika 28. Release build
  and actual-DLL integration audit pass (766 contracts; four known CoreCLR/native
  delegate limitations). Engine doubles and build tests are not a Unity raid.
