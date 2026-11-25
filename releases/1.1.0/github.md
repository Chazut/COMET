## COMET 1.1.0

### Features

- **A more deliberate first shot.** A PMC who catches an enemy looking away can take one to two seconds to line up a single shot. A visible head is a possible target if the SAIN preset allows it.
- **An escape from bad cover.** If a PMC keeps taking hits and SAIN cannot find another cover spot, it can break away from the exposed position.
- **Choose who uses COMET.** Enable PMCs and AI player scavs independently. You can also switch off light-based detection if you use another vision mod.

### Changes

- **Lighter combat AI.** Weapon selection, cover searches, footstep hearing and grenade checks reuse information that bots were repeatedly looking up. Mixed magazines still account for the rounds they contain, and bots can react to the first grenade thrown.
- **Simpler replay settings.** Essential is on by default for Raid Review and Satellite. It keeps actions, squad roles and magazine recoveries. Turn on Extended for decision scores and detailed reports. COMET skips replay recording when neither mod is present. Local logging is quiet by default.

### Fixes

- **Fewer cover loops.** PMCs can move on when an enemy is unreachable and SAIN cannot start a search. Switching cover also preserves the destination during the handoff.
- **Bleeding on blacked limbs.** Bots can treat the bleed with a suitable item and stop trying to restore HP to a destroyed limb with a medkit.
- **Late-loading doors.** COMET now picks up door links that appear after its first scan, so open door panels can still be accounted for when bots move.
