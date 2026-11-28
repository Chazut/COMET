# COMET 1.2.0 | Footsteps, firefights and fewer dropped mags

## Features

- **Footsteps by pace.** PMCs and AI player scavs can hear other bots at different ranges depending on how they move. The default base ranges are **5 m** for quiet movement, **20 m** for walking and **30 m** for sprinting. You can tune all three in the web editor. The override is on by default and works independently of your SAIN preset.
- **More of the fight in replays.** Essential replay data now includes confirmed peeks, jumps, feints, emergency weapon swaps, grenade escapes and medical actions. Replay viewers can show those moments with Extended switched off.

## Changes

- **Fewer dropped magazines.** PMCs save the quick-drop reload for emergencies. After a fight, they only go back for a dropped mag within the configured recovery range, **15 m** by default.
- **Squad sieges.** Squads check the building's exits, including walkable openings without doors. They call off the siege if an exit is uncertain or there aren't enough squadmates to watch them all.

## Fixes

- **Repeated reloads.** Fixed PMCs swapping magazines over and over without firing when the replacement wouldn't give them more usable rounds.
- **Untreated fractures.** PMCs can use a splint or a compatible medkit on the fractured limb, both during combat and while recovering afterward.
- **Missing map names.** The Raid Observatory can pick up the map after loading when it was unavailable at the start of the raid.
