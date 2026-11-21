# zzap behavior baseline

This development baseline replaces COMET's additional tactical eligibility and
continuation rules with the corresponding zzap rules. New restrictions require
a recorded raid problem, a source comparison and a focused regression case.
Do not add a defensive tactical veto merely because it seems reasonable.

Reference: R_F's SAIN zzap snapshot
`fbb86b838e0fa692b759b2c0b11fc30158a53876`.
The audited door, grenade, cover and squad changes also track remix 7.6
(`d8655293f607161fa0c7c0cf9958af4ffcfbae8f`).
Configuration marker: `zzap-d8655293-3`.
The host remains official SAIN 4.5.1; the fork DLL is not distributed or installed.

## Behavioral changes

### Chambered rounds in tactical decisions

S02 entry, S03 flank and support roles, the S06 squad ammo threshold, the
M14 magazine feint and the H04 post-combat reload use EFT's loaded-round count
and capacity like remix 7.6. M05 uses the same reading when selecting a corner
pre-fire as M06 uses for its start threshold and ammunition reserve.
A round in the chamber counts toward the threshold. Weapons loaded directly
into their chambers use their chamber capacity. A missing detachable magazine
or zero capacity gives a ratio of zero. A full magazine plus one chambered
round can exceed 100% and remains eligible for M14.

### S06 squad assault admission

In remix 7.6, `shallRushEnemy` checks for a complete path to the enemy and
calls SAIN's `LowOnAmmo(0.5f)` before asking the squad to storm. COMET applies
both checks to each member, including when reusing a cached join decision.
The squad's own ammo setting and individual participation roll still apply.
As in `SquadCombatClass.AssessManeuver`, a favorable S06 verdict for the
group and contact blocks planning a new S03 flank. Members can still take
their roles in an existing maneuver or choose another squad tactic.

S03 stores its plan and retry history under SAIN's squad ID. S06 keeps one
assessment per EFT group and enemy, with each participant's join decision
cached until that assessment expires. Arrivals and departures update the
available members without resetting either tactic. A transfer to a different
SAIN squad releases the old S03 role. S03 traces include the SAIN squad ID.

For S06, personalities without an individual rate (including SAIN's Custom1
through Custom5 and None) use a 50% base chance to join. Named personalities
use their configured rates, including a deliberate zero. The existing
expected-escort adjustment applies before the personal roll.

A new S03 requires the nearby squad members on that enemy to have lost sight
of it. Someone still watching it can enable ordinary crossfire (S01). Members
busy with a door tactic are excluded from this check. The sight check precedes
the squad roll and records `maneuver-squad-member-sees-target`. Once the
plan exists, its support can regain sight and continue in that role.

Indoors, an S03 flanker searches for an angle on its last known enemy position
using the center of the supporting members as its reference. Only ordinary
crossfire searches for a secondary entrance (S02) when a teammate sees the target.

S02 starts its initial watch on arrival, for the preset's `WatchMax` (15 seconds
by default). The first update past the watch deadline checks whether the last
known enemy position is less than 15 seconds old and less than 12 m horizontally
from the doorway. The next room check after an extension is at its expiry.
If the bot decides against entering, a sign from the target less than eight
seconds old allows one extra watch. At the next decision, an entry-enabled
bot makes a fresh personality roll. The trace retains both entry rolls.
Ordinary combat interruptions still release the bot during the watch.
`WatchMin` remains the lower bound for S11 squad siege duration.

Incoming fire blocks a PMC's own attempt to start S03. A teammate forming the
plan can still assign that PMC a role. The PMC can take the role once the fire
lets up. During an active role, support and rear guards hold under fire and
flankers yield. The personal admission check logs `maneuver-under-fire`.

Only the proposer checks the horizontal distance to its last known enemy position
and the age of that observation (defaults 15-80 m and up to 10 s, inclusive).
The squad radius uses 3D distance between teammates. A nearby member tracking the
same enemy can be assigned a role even with older information or a different
distance to the contact. It takes that role using its own remembered point.
The throttled eligibility log distinguishes `maneuver-proposer-horizontal-range`
from `maneuver-proposer-contact-stale`.

S03 selects the highest-scoring eligible flanker, preserving SAIN's member
order for ties. With rear watch enabled and at least three candidates, the
remaining member farthest horizontally from the remembered contact looks away
from it. Other teammates with at least 33% loaded ammunition provide the base of
fire. If none qualify, the rear guard takes that duty when sufficiently loaded.
A missing flanker or shooter delays the next attempt by ten seconds. Diagnostics
record each role and the reason for the selection, alongside the single squad roll.

S03 remembers its roll for each squad and enemy through 30 seconds after the draw.
Both a refusal and an acceptance keep their original probability. Missing roles
can be reassessed after 10 seconds using a still-valid acceptance. Ending a plan
starts the separately configured same-enemy cooldown (25 seconds by default).
The maneuver trace records the original roll, chance and deadline in game time.

When the flanker reaches the chosen angle or reacquires the squad's target on
the way, coordinated blind suppression stops. Existing roles have five seconds
to converge, capped by the original plan deadline. A later sighting does not
restart the window. Telemetry distinguishes spotting the target en route from
engaging it after reaching the angle.

### M14 second bait peek (2026-11-21)

During each outward and return leg, the accepted action renews its native
move on the first update more than 0.6 seconds after the last order. Arrival
within 0.7 m horizontally needs no order. SAIN refusing the first move or a
renewal ends the session with `feint-no-path`, even on an update that crosses
the leg's timeout. The trace records the attempted leg and total move orders.
Renewals preserve the leg timer and allow SAIN to reuse its path.

Once the first peek has a point, the bot has a 40% chance to lower its stance
to 0.55. On the second outward leg it uses the opposite stance. At the first
return, it tries the reflection of the first opening across its starting cover.
The check samples within 0.6 m on the NavMesh and tests the sightline from the
sampled point to the remembered enemy. A failed check reuses the first opening.
The M14 trace records the first point, initial stance, side choice and last
pose requested from SAIN. The first outward and return orders request a
high-urgency sprint. Subsequent peeks use walking orders. The jump setting
controls hopping independently. Each leg of the first peek can jump once.
Blocked headroom or a refused jump leaves the bot free to retry on the next
action update. The return checks after 0.05 s.
Takeoff requires 1.2 m/s toward the destination, headroom at both ends and
at most 0.25 m between their elevations. M14 records its jump requests and
which legs received an acceptance.

During each active action update in the exposed phase, M14 draws a fresh
threshold (0.2 to 0.35 seconds by default). Between peeks it does the same
with 0.8 to 2.2 seconds. Elapsed time must exceed the draw. Entering a phase
starts its clock. Decision and telemetry callbacks do not sample either timer.
If the pause expires before the opposite point is checked, the bot proceeds
as soon as that check finishes. The existing M14 trace carries the latest
threshold and the total checks for each phase.

### A02 visible combat scores (2026-11-20)

A02 ranks Shoot, Cover and Push using the full weighted scores, including
encounter outcome adjustments. Tactical mistakes use those same scores.
The cached preference order is kept when capabilities change. Candidate
and mixing diagnostics display scores to two decimal places after ranking.

### H01 hearing during medical decisions (2026-11-20)

Ordinary medical choices use the last sound SAIN accepted for each known
enemy with a known position updated within 120 seconds. A footstep or sprint
counts for less than 5 seconds and strictly within 30 m by default, including
elevation. A subsequent sound replaces the cue for that enemy alone. H01
and M14 read the same per-contact report. Medical traces identify a contact
that contributed footsteps in `footstepContact`.

### M13 outdoor relocation route renewal (2026-11-20)

Once SAIN accepts the relocation and starts its cover action, COMET reissues
the native move to the selected position every 0.6 seconds, as in remix 7.6
`RepositionClass.MoveTo`. SAIN can reuse or rebuild its path. A gap before
the next path starts keeps the move pending. A different destination, a
replacement action, or a refused move ends the relocation. Cleanup only
cancels path generations created during COMET's own orders.

On arrival within 0.7 m horizontally, the bot holds the selected cover for
5 to 10 seconds. The duration is drawn once per destination and starts on
arrival. The native action stops movement, sets pose to 0.8 and watches the
last known contact. Threats, a change of action and the session timeout can
interrupt the hold. M13 records arrival, applied hold and hold expiry separately.

M13 and M14 share the five-second post-session cooldown from remix 7.6
`RepositionClass.End`. It belongs to the bot across targets. Relocation starts
when a throw is accepted or the move is requested. A peek starts once its point
is found. A fake reload starts at the firearm's magazine-check request, which
can itself end the session if refused. A declined roll or failed initial search
leaves the shared clock alone. Terminal traces record the expiry in scaled
game seconds as `sharedCooldownUntil`.

M14 uses three source cues when ordering the cover feints. A shot directed at
the bot within 10 seconds and a living squadmate engaging the same enemy
(visible or seen within 5 seconds) favor a peek. A footstep or sprint favors
the magazine check when it is the last accepted sound from that enemy, less
than 5 seconds old, and closer to the listener than the enemy's last known
position. Each observer/contact pair retains one sound. The selection trace
records all three cues alongside the two weights.

M14 keeps the unused feint available during initial preparation. If the first
peek has no usable point, it tries the magazine check. A missing firearm or
a refused check can lead to a peek. The alternate uses its own eligibility
and the roll from the same decision. An accepted variant consumes the choice.
The trace keeps the episode ID and records the switch in `fallbackReason`.

At the native magazine-check request, M14 saves SAIN's visible path point
for the current enemy in `magazineVisiblePathPoint`. The bot watches that
point through the check and hold, falling back to the encounter's last known
position if none was available. A bait peek uses the last known position.
Both look 1.3 metres above their point and give native firing priority.

During the angle hold after a fake reload, each accepted action update requests
pose 0.8 from SAIN. The magazine-check animation keeps its own stance. On
expiry or interruption, M14 returns the bot to standing if its last lowered
pose is still the mover's target. The M14 event records the requested pose in
the existing `appliedPeekPose` field.

When a started bait peek ends on sight of its enemy or on a native switch to
`StandAndShoot`, M14 leaves ordinary decisions available for 0.9 s. For the
next 1.6 s it requests native `Retreat` when the enemy chooser runs, after
emergencies and before ordinary tactics. The window belongs to the bot.
The feint trace records its start, expiry and whether a retreat was requested.

### A05/H01 ordinary medical decisions (2026-11-20)

With a known enemy, ordinary medical choices use the remix 7.6 `HealUtility`
personality roll. Each bot keeps a separate five-second medical verdict while
the same option ranks first. The alternative must still be usable, score above
zero and be within 35 points of the leader. Heavy bleeding, a critical head or
chest injury, and the BadlyInjured or Dying health states bypass this roll.
H01 records the natural first choice, the selected action and the verdict expiry.
At execution, COMET checks the selected item, actual injury, native cooldowns
and ownership of the hands. A new emergency returns the bot to medical selection.
External surgery continues to use its native kit and callback.

### D07/D08 room entry choice (2026-11-20)

Both clear modes use the remix 7.6 `BuildClear` weights and feint count when
the door session is selected. The stock grenade preference is 55. Custom profiles
retain their saved preference. G01 still checks a planned throw, including its
trajectory. A preference of zero disables real throws. Recent movement
rules out feints. A quiet contact can lead to two grenade feints,
with weapon recovery between them. The D07/D08 trace records the original plan,
the executed variant and the number of feints started and completed.

### G01/G02 grenade contact age and throw range (2026-11-20)

An ordinary throw can use a contact up to 20 s old and 45 m away by default.
Information older than 10 s costs 20 percentage points. The range applies with
COMET's arc search or with SAIN's search while grenade judgment is enabled.
The G01 event records both effective limits alongside the observed age and penalty.
Stock difficulty profiles adopt the new defaults. Personal presets and customized
active profiles retain their saved grenade settings.

### A01 ambush radius for a hidden contact (2026-11-20)

The advanced Lost contact setting `AmbushRadius` uses the remix 7.6 default
of 45 m and accepts 10 to 100 m. The Hold score begins to fall beyond this
distance from the last known enemy position. The penalty reaches its maximum
at twice the radius and is smaller for long-range weapons. Each fresh A01
ranking records the radius in `hiddenHold.ambushRadiusAtRank`.

### A01 medical retreat score for a hidden contact (2026-11-20)

A badly injured or dying bot outside cover gets 20 extra FallBack points when
EFT reports first aid or surgery work and the bot carries a medical item.
The hidden ranking reuses the equipment count. When the count is unavailable,
it checks the carried equipment only if the other conditions apply and stops at
the first medical item. The A01 decision log records `medicalRetreatBonusAtRank`.

### A01/G01 grenade score for a hidden contact (2026-11-20)

On a fresh hidden ranking, COMET reads EFT's grenade availability and SAIN's
path length, distance to the last known position, contact age and health status.
The base score is zero unless a grenade is available, the path is at least
10 m, the last known position is strictly between 8 and 45 m, the contact is at
most 20 s old, and the bot is neither badly injured nor dying. The path and age
thresholds use the remix 7.6 defaults and ranges in COMET's advanced grenade
settings because official SAIN 4.5.1 lacks the fork's CloseCombat fields.

A01 decision events retain `hiddenGrenadeAtRank` from the last scoring pass.
Native execution checks still handle grenade permissions, a pending arc and
throw safety when the bot tries the cached ranking.

### A04/A05 fallback after a deliberate mistake (2026-11-20)

Visible combat, hidden combat and reload decisions swap the original best option
with the option chosen by the mistake verdict. If the third choice becomes
unavailable, the original runner-up is tried next. For hidden enemies, an ordinary
weighted draw and retention of a previous close-scoring pick still move that pick
to the front in their original order. Combat decision traces record the swapped
option's one-based rank in `mixing.mistake.swappedFromRank`.

### A01/A12 hold time across hidden contacts (2026-11-20)

The hidden-enemy ranking carries its Hold clock across target changes while
SAIN reports Freeze. On a fresh ranking, a top Hold preference or native Freeze
maintains the clock. Cached rankings and execution refusals leave it alone.
The existing 40-second cap uses this elapsed time. A01 decision events record
`hiddenHold.secondsAtRank` and `hiddenHold.cappedAtRank` with the candidate scores.

### H04 recovery after combat (2026-11-20)

H04 samples SAIN's active layer at the start of each decision update. It can
start once the first and last Combat, AvoidThreat or Flashed samples span at
least 1.5 seconds. Squad preserves those timestamps during handoff. Other
layers reset eligibility. The H04 event records this span as `combatLayerSeconds`.
The recovery window ends 60 seconds after the last combat-layer observation, even
with repeated sounds, active treatment or a reload. H04 releases control and lets
native operations finish. Its end event records the reason and `secondsSinceCombat`.

H04 can request one native top-up on the same update that orders a move to
cover. An existing reload can continue on the way. Active treatment suspends
that movement. The H04 trace records `Reloading` alongside the cover action.

During regrouping, each movement order targets the live squad leader's current
position. Orders are at least one second apart. Regrouping ends within the
configured distance (25 m by default). The optional enemy search still uses
the remembered contact position.

### Door recovery at Ground Zero (2026-11-20)

In raid 485 a PMC held the same doorway for 37 seconds. C04 selected a reversed
leaf on the floor below nine times. C01 recorded the doorway on the route as
open, and both knee-level probes in C06 missed the obstruction. C04 now uses
the nearest physical open leaf, following `DoorLeafCheck.OpenLeafDoorNear` in
remix 7.6. C06 also checks a short chest-height capsule along the stalled
route. A confirmed leaf enters the existing repath and route-release sequence.

### Door decisions and engagement scoring (2026-11-20)

Combat door selection follows a refused rush. Search has its own room-clear roll.
A decline blocks another door for eight seconds and the same door for 25 seconds.
An ordinary session end blocks that door for 60 seconds and other doors for eight.
If the target changes, the enemy is spotted, a rush takes over or SAIN interrupts
the action before 1.5 seconds, the delays are eight and three seconds. An armed
third-party resume on a target change takes precedence (five and three seconds).
Conditional peeks, clears and
traps share the selection. Existing installations migrate the previous default
personality chances while retaining individually tuned values.

COMET applies repetition and outcome scores within the current encounter. It
continues to use equipment when estimating human target weakness. The latter
is an intentional difference from the fork.

### Brief sight loss and outdoor relocation (2026-11-20)

Across 47 recorded raids, A03 had 443 accepted decisions. SAIN's execution
observer records the native action even during a brief occlusion. The archive
links that observation to the same bot, contact and selection. A normal end
requires reacquisition or expiry after an observed start. The jiggle case
observes DogFight, and another threat must be identified by the known-enemy
check in `EnemyDecisionClass.KeepCloseFight`.

Of 72 M13 episodes, 39 ended on a competing tactic flag. In the pinned
`RepositionClass.ShallUse`, the door and squad flags apply before a session
begins. The active session still yields to a different goal, another visible
enemy, incoming fire while stationary, or a visible goal within 20 m while
moving. First contact uses the source's half-second rule and one roll per
enemy. The radial search uses the shared frame budget and retains the source
sample and path order. Planning displacement and an accepted Freeze cannot
confirm movement. The mover and arrival have separate diagnostic evidence.

### Squad siege (S11)

S11 adds a collective watch to COMET. S02 still follows its original secondary
access decision. S11 uses the squad's latest SAIN visual contact inside an EFT
room and the map's connected indoor rooms. Exterior door IDs refer to physical
doors. Open passages require a direct NavMesh crossing between the two room
volumes. An unresolved exterior portal makes the building ineligible.

At least two different exits must be visible from different available members'
current outdoor positions. Only assigned members request native Freeze and look
at their exit. The leader's S02 personality settings determine the maximum wait.
Fresh visual evidence that the contact left, lost coverage, self-care or stale
contact releases the squad. A new attempt requires a new sighting. Decisions
run on raid authority and emit S11 selection, acceptance and release events.

### Grenade suppression

This COMET squad choice extends the baseline's individual hidden-contact grenade
decision. A recent SAIN shot-at or hit recorded by a squad member identifies the
attacking human, including a remote Fika player. A bot out of sight can request
the existing grenade action when a nearby squadmate has the same contact. The
holder uses Freeze while the player is hidden and StandAndShoot on visual
contact. Both roles use the host's decisions and ordinary SAIN actions.

The existing grenade settings, equipment checks and native throw approval apply.
The pair lasts for one native throw. A rejected throw leaves the ordinary
decision ranking available. S10 decision events identify the thrower, holder and
contact. The squad maneuver settings include the Grenade suppression switch.

### Recognizing hostile groups (2026-11-18)

Raid Review recorded Rook killing ToastyTater 48 seconds into the Customs raid
starting at 19:14. The kill record does not establish which decision led to it.
The pinned visible utility rewards pushing a vulnerable target and counts allies
on that target. Its hidden utility also counts known enemies. The squad storm
uses target weakness and rolls each member's personality chance. None estimates
escorts from the identity of a single contact. The requested COMET change applies
to PMC scoring for real combat after personal or shared visual identification.

A recognized boss, guard or faction soldier implies support throughout the first
15% of raid time. That expectation falls to 15% by the 85% mark. Nearby known
opponents can sustain caution late in the raid. SAIN personality aggression and
nearby engaged squadmates scale the score adjustment. A recently seen group
leaves a local cue for 30 seconds within 35 m even if the original target dies.
SAIN's existing action capabilities and contact selection still apply. A01/A02
diagnostics record the role, faction, group cue, raid progress, escort expectation
and observed counts alongside the candidates used for that decision. Squad storm
uses the same assessment when a member first rolls to join. The effective chance
is recorded, and a refusal attributable to expected support has a distinct reason.

### Selected fixes from R_F's zzap Remix7.6 (2026-11-18)

- `93f81272` fixes GroupSearch. Followers can join their leader's search for a shared enemy. The leader continues its own decision.
- `8a5c7d7d` uses SAIN's existing successful NavMesh sample as the path start beyond 0.35 m of horizontal displacement. The bot returns to its usual start at 0.25 m. Enemy, cover, mover and strafe queries use this origin. The same correction covers COMET routes starting at the bot. Aiming and target coordinates still use the physical positions. COMET already defers the enemy path cache update until recalculation.
- `8a63bf9c` releases SAIN's three raid statics by owner at disposal or scene destruction. Hearing and post-combat sound listeners detach from the publisher they subscribed to, including when disposal overlaps the next raid.
- `d0d031c1` applies the installed SAIN voice, squad talk and grenade settings at bot initialization and on preset reload. Fake-death voice rolls once per eligible wound. A room-entry grenade preference of zero disables real throws even with shared grenade judgment enabled. The native grenade arc cutoff remains zero as in Remix7.6.
- `902cd039` excludes a door leaf and its descendants from cover, as well as a sibling frame whose bounds center is strictly within 1.5 m of a door pivot. COMET applies the same test when creating its own outdoor cover. Collider classifications are cached per raid. The separate seasonal foliage filter still applies.

Open door leaves now use the remix 7.6 carver on the AI host (default on).
The shared navigation mesh reflects the open leaf when no living actor is within
2 m. SAIN paths retained by supported bots are checked against recent cuts.
The existing local obstruction recovery still handles a bot already stuck.
At the native SeekCover fallback, a supported bot with no cover in use, no
cover candidates and an invalid path to a hidden enemy holds its angle with
Freeze. Incoming fire or a hit less than five seconds ago leaves SeekCover in
control. A continuous hold ends at 30 seconds, followed by 20 seconds before
this fallback can hold again. A gap over one second restarts the hold clock.
New fork-specific preset switches remain outside this port.
C11 batches the new counters with the existing native-fix summary.

### Hidden-action execution after Back4Streetz raid 459 (2026-11-17)

The recorded 03:10.603 candidate list selected a negative Hold after Search
failed. Selection now follows `EnemyDecisionClass.TryUtility`: stop at scores
at or below 0.05 (5 in COMET telemetry), allow Hold/FallBack only at the top or
within 0.12, and retain the source's danger exception for FallBack. Failed
execution probes do not reroll cached preferences. The last native action may
survive a transient refusal for the source constant of **2 seconds**, without
renewing its deadline or logging another selection on every tick.

ShiftCover uses native `shallShiftCover` and native point selection, including
personality, suppression, movement, completion and reset timers. The COMET
observer records that route without submitting a replacement. FallBack maps to
SeekCover. Search uses the full native decider rather than a direct path probe,
with zzap's 15-second quiet heard-only exception and walking behavior restored.
Reached partial path ends use the source two-metre arrival rule.

The related movement watchdog uses the source thresholds: 0.5 m progress,
5 seconds for utility FallBack, 2 seconds for Search near the last known spot,
6 seconds for other Search/Push stalls, then source 15/12-second bans. It does
not classify Hold, Flank or ordinary native SeekCover as stalled utility work.
Ghost/inactive lifetime pauses do not count as movement stalls.

Diagnostics group native refusals, path endpoints, mover and cover state into
at most one A01 `execution-check` per bot every three seconds. Quiet logging
still preserves RR publication. Tests replay the recorded candidate list and
exercise native acceptance/refusal, retention, search completion and authority
boundaries. These checks do not prove that the original Unity geometry will
produce a valid route or that native SeekCover cannot stall after COMET yields.

### Discarded-magazine travel facing (2026-11-17)

The user's raid observation showed bots repeatedly walking backwards during
"Returning for a discarded magazine". This custom post-combat feature passed
the retained enemy to native priority steering, which can face the old contact
instead of the path. Recovery now looks along its own native route, then at the
magazine during pickup. Shooting and new combat/movement owners retain priority;
zzap combat selection and the existing recovery eligibility are unchanged.
W04 events report the requested facing mode without adding per-frame logging.
MagazineRecoveryHarness covers route facing, pickup, danger, Ghost and Fika replicas.

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

### Autumn and winter foliage cover: requested seasonal correction

The user and Discord clips report bots treating bare autumn bushes as cover,
while native foliage vision still obstructs their view. Official SAIN 4.5.1 and
the pinned zzap CoverAnalyzer both accept Foliage colliders through the same
geometry test, without season awareness. Native Autumn/AutumnLate state changes
materials; SAIN's cover and vision masks still include the Foliage layer.

COMET now excludes foliage-only cover candidates in Autumn, AutumnLate and Winter.
Winter applies the same user-requested cover policy as autumn, without changing
perception or adding a new tactical condition. C07 diagnostics identify the
actual season; refusal reasons use the shared `seasonal-` prefix.
Successful native candidate positions are also checked for foliage occupancy
one metre above the standing point. Inside foliage, a short ray from that point
towards the known threat checks solid geometry (HighPolyCollider or Terrain).
It ends at the threat or SAIN's three-metre local cover range, whichever is
closer. A trunk, wall or rock shielding the bot can qualify even when its
branches overlap the standing point. Retained, custom, flank,
directional and relocation cover paths use the same seasonal exclusion. Native
cover selection/ranking and alternative movement remain responsible for the
replacement. Invalid retained pointers are cleared individually, not marked
permanently bad. Other seasons retain the source behavior, including evergreen
concealment; no inference from the real-world date or a mod's name is made.

This is a deliberately conservative cover choice, not a leaf-density model:
autumn/winter foliage volumes are excluded even if a particular bush retains leaves.
Grass has its own layer and is absent from both the foliage and solid tests.
Unknown seasons and external bot ownership retain native behavior.
Only supported, active, authoritative COMET bots with an enemy are affected.

No vision/raycast mask, collider, detection preset, target position, accuracy or
suppression rule is changed. Do not call this a fix for every reported shot
through bushes. Native raycasts, origin-inside-collider behavior, stale contacts
and suppression must be distinguished with actual shot evidence before changing
visibility. In particular, removing all foliage from vision would also expose
players hidden behind dense bushes and evergreen vegetation.

Occupancy uses a shared non-allocating buffer, cleared after use. Retained/custom
point occupancy is cached by weak point identity, position and season. Inside
foliage, repeated checks of the same point share the solid ray for that frame
and known threat position. No physics
scan runs for a foliage-layer rejection or outside the affected seasons/scope.
Aggregated C07 SeasonalCover events are limited to one per bot per five seconds
and observer failure cannot veto gameplay. Regression tests cover all seasons,
solid/foliage/inside-foliage positions, unchanged native failures, retained and
custom destinations, authority/role/ghost/handoff boundaries, cache invalidation
and diagnostic faults. Actual scene geometry and movement still need raid QA.

### Discord foliage reports: protection, exposed cover and shot evidence

The user requested these three follow-ups after the autumn correction. Reports
describe bots staying behind bushes while taking hits, and fire through foliage
whose intent cannot be established from the clips alone. These are explicit,
localized differences from pinned zzap, not new general tactical vetoes.

Both official 4.5.1 (also verified in the pinned DLL) and zzap count damage in
CoverPoint.GetHit and expose a temporary Spotted flag. However, the main
SAINCoverClass.FindCoverPoint/UpdateCover route accepts/retains points using
IsBad alone. FindPointInDirection already excludes Spotted. OnSpottedInCover
has no decision subscriber in either source tree; official AvoidThreat only
uses it to label an already-selected DogFight action. Geometry rescans do not
repair this discrepancy: their IsBad result is separate from the damage flag.

COMET now releases an exposed held/moving point after native hit accounting,
before retained-cover updates and before move requests. A refused move lets
the existing native picker try its next candidate. Retained COMET selection
and the source's near-point stalled-arrival fallback respect the same flag.
There is no permanent blacklist, replacement ranking, new hit threshold,
expiry extension, forced DogFight, forced sprint or medicine cancellation.
The native two-second Spotted lifetime remains authoritative. Unrelated routes
are preserved. Only authoritative supported COMET combat bots are affected;
ghosts, Fika replicas, ordinary scavs/bosses and other layers retain native flow.

Medical scoring distinguishes arrival from protection. Foliage, native bad or
spotted points do not earn the existing FirstAid/Surgery protection bonus or
the protected stimulant terms. Their source weights, eligibility, emergency
choices and treatment execution remain unchanged, including BHF callbacks.
Arrival still informs travel separately. Post-combat episode setup also checks
arrived, protective native cover before falling back to the existing solid
enclosure/last-known-threat geometry. A bush destination is not evidence of
protection. Summer camouflage destinations remain available; autumn exclusion
continues independently. This is collider classification, not a bullet
penetration or leaf-density simulation, and cannot guarantee invulnerable cover.

C07 CoverExposure events report native Spotted/IsBad, collider, position and
cumulative hits/releases/refusals. Details are bounded to five seconds, with
the first release allowed through even after a below-threshold hit. H01 exposes
ReachedCover separately from Cover. T01 counts and samples actual shots with
separate intent (direct, suppression, blindfire, other/unknown) and cached native
visibility; flashlight/laser state is contextual evidence, not proof of detection.
No visibility flag, shooting gate, ray mask or SAIN preset is changed here.
No additional physics query runs for exposure or medical classification.
Quiet disk logging does not gate the event API. Observer failure cannot veto
movement. Tests cover native-state release/expiry ownership, retained/custom
paths, medical scores/adapters, post-combat stale cover, scope, observer faults
and actual-shot classifications. Actual foliage geometry still needs raid QA.

### Raid457: physically compatible magazines with the wrong ammunition

Woods_Keep3r_01 collected a .300-loaded STANAG magazine for a 5.56 M4, then
stopped firing after its chambered round despite a loaded secondary. The pinned
fork counts magazine rounds and tests the magazine body, just like the native
reload selector. This accepted correction uses EFT's IsAmmoCompatible chamber
check for reload candidates, resources, weapon fallback and quick-reload spares.
An incompatible magazine no longer keeps HaveBullets true after the chamber is
empty. The native critical-bullet threshold, last chambered shot, mixed-magazine
next-cartridge semantics, transaction callbacks and fallback ranking remain.
Wrong-caliber rejection records include weapon/magazine IDs and ammunition;
observer failure cannot change the selection. Regression coverage reproduces
the M4/.300 case, a loaded ISO secondary, empty/mixed magazines and host scope.

Raid457 also contains long native Search episodes. Diagnostic reasons now
distinguish an unvisited place from insufficient silence, with cached native
search/path state. No search timeout or movement constraint was added.

Fault-owner warmup now includes native classes from the game's assembly even
when they have no namespace. This moves first-use expression compilation to
startup; it does not change tactics or sampling frequency. Raid457's measured
spikes alone do not prove how much time came from this cold path. The integration
audit checks the actual native types; frame-time gains still need a new raid.

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

### liquidwarp audit: native calculation and surgery fixes

- User-authorized corrections after comparison with official SAIN 4.5.1,
  installed DLLs, pinned zzap and liquidwarp's source. Exact upstream commits and
  attribution are in THIRD_PARTY_NOTICES.md. These correct inherited errors;
  they do not add tactical eligibility, delays, cancellation conditions or rolls.
- Both PathVisionJob constructors now cast normalized rays over the full segment.
  The existing masks and query parameters remain. Deferred path calculations no
  longer consume changed positions, and Clear invalidates the old enemy position.
  Native path range, knowledge and update interval checks remain unchanged.
- Grenade landing prediction uses midPoint-to-target for the second ray. The
  corrected prediction is computed once per throw and supplied only to supported
  observers. Ordinary scavs, bosses and other callers keep native predictions.
  Nested calls and exceptions restore the temporary prediction context.
- Enemy activity reads the target component, and the existing self-target check
  is made reachable. Native alive, AI owner, active and standby checks remain.
- Surgery started outside SAIN is adopted without starting another kit or
  replacing its animation, item, health effects or callbacks. Existing zzap
  SourceSurgery rules decide whether it continues. Cancellation calls the native
  CancelCurrent method, allowing BHF to finish the real hands operation. Native
  instant-heal/restore-all shortcuts are suppressed for the adopted operation,
  as they already were for COMET-started surgery. BHF remains optional and intact.
- Baseline recoil decay now uses elapsed time and absolute residual angles.
  W09 stays optional and unchanged; an explicit HarmonyX __runOriginal check
  prevents applying both decay steps. Pose and lean use elapsed update time with
  liquidwarp's 0.25 s resume cap. No recoil coefficients or personality values
  change, and cheater recoil remains native.
- All corrections retain COMET's supported-bot/authority/lifetime boundaries.
  Existing G05 smoke/CS-gas and T01 light/laser corrections are not duplicated.
  C11 native-fix-summary emits cumulative counters at most every five seconds,
  plus a final dirty flush. H02 marks adopted surgery with origin=native. Quiet
  logging leaves the RR event stream intact; neither observer gates gameplay.
- NativeFixHarness exercises production Harmony patches on native-shaped
  fixtures, including 30/60/144 Hz updates, negative recoil, W09 ordering,
  nested/failed grenade events and excluded observers. MedicalHarness covers
  native surgery adoption, cancellation and both BHF callback orders. Real-DLL
  registration/IL checks complement these tests. Unity animations, physics and
  a multiplayer raid still need in-game validation.

## Raid 454: native magazine pickup and observer work

The discarded-magazine feature is a COMET integration, not an extra tactical
constraint on the pinned zzap decisions. The installed EFT MovePlayerState
inherits an empty Pickup method; IdlePlayerState enters the actual pickup state,
which invokes its callback from the animation. Stop movement and retry when the
native state changes. Confirm success only from the existing item transaction
and inventory membership. Combat handoff, authority, supported bots and Ghost
behavior retain their existing rules. Interrupted callbacks cannot transfer an
item later or cancel an unrelated pickup. Arrival is no longer logged as proof
that the animation started.

RR receives the same W04 recovery lifecycle in Quiet mode, including the actual
dropped item's identity and position. The compact map projection, replay parser,
action labels and ground-loot timeline now consume it. Failed/interrupted recovery
does not remove the item marker. Confirmed pickup, including by another bot, does.
Historical raid 454 remains a failed pickup, not a retroactive success.

Medical evaluation reads the seven body parts once within each synchronous call,
reuses private scratch dictionaries and creates options only for winning candidates.
Ranks, tie breaking, native eligibility, surgery policy, random draws and evaluation
frequency are unchanged. No cross-frame health cache or new cooldown is introduced.
Separate performance scopes expose medical sampling, inventory evaluation, equipment,
environment, sight and diagnostic serialization costs. The recorded non-GC spikes
are not proof that every millisecond came from these repeated reads; the next raid
must establish the actual gain and any remaining scheduling/serialization spikes.

Regression coverage includes production pickup adapters, real installed EFT IL,
medical inventory sizes 1-32, previous trace immutability, Ghost/Fika ownership,
RR historical event replay, rewind and repeated drop/pickup, and compact projection.

### Local light and visual contact

On the AI authority, supported bots use local light at a human or AI target's eye position
to scale SAIN's acquisition modifier by up to 1.35. The effect starts beyond
6 metres and reaches full strength at 30 metres. Each target's scene sample
is reused across observers for up to 0.35 seconds, with an early refresh after
1.5 metres of movement. This includes remote players on a Fika host.
Human samples are immediate. AI requests share a FIFO queue and at most one new
scene measurement per Unity frame. During contention a stationary target can reuse
its reading for another 0.35 seconds. A moved target or an older reading uses the
native preset until refreshed. Unrequested stale entries expire from the queue.

Contact history comes from SAIN and EFT for each observer and target. A confirmed
sighting in the last 10 seconds or an exchanged hit in the last 8 seconds removes
the extra delay. Active NVGs, a target's white light, bright conditions and an
unavailable measurement also preserve the native value. The adjustment never sets
visibility or changes a preset. Local-exposure diagnostics record outcomes and
remote human observations. Each summary includes AI checks, shadow outcomes,
samples, cache hits, deferred checks, queue depth and total and peak AI measurement
time in milliseconds. Detailed samples identify AI targets when quiet logging is off.
