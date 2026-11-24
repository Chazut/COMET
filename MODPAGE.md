## COMET {.tabset}

### Overview

**COMET builds on SAIN to make AI PMCs fight more like real players, whether they're running solo or moving as a squad.**

A PMC's kit, personality and what they've actually seen or heard shape their next move. Some will take a chance when they hear you reload. Others would rather change cover, watch a squadmate's flank or let you walk into an ambush. They can panic, miss an opening or get a little too confident, too.

These tactics apply to **PMCs and AI player scavs**, including fights between bots. Ordinary scavs and bosses keep their usual behavior. Bring your favorite SAIN preset.

**Requires official SAIN 4.5.1.**

### Features

**Combat decisions**

- PMCs choose whether to shoot, push, flank, hold, throw a grenade, search or fall back. Their SAIN personality, weapon, ammo, injuries and nearby allies all weigh in.
- They prioritize known threats, remember gear they've seen and listen for openings such as a reload or a heal. A boss, guard or faction soldier can mean backup, especially early in the raid.
- Heavy suppression and squad losses can cause fear or panic. Bots can make mistakes, remember repeated peeks and retry tactics that worked earlier in the same fight.

**Sight, sound & search**

- Local light affects first detection for players and AI alike. Recent contact, close range, active NVGs and weapon lights matter. Bots can switch off lamps and lasers while waiting in ambush or at rest.
- PMCs hear footsteps, jumps and landings, including those of other bots. Floors muffle movement sounds. Distant gunfire, close threats, bullet impacts and nearby explosions get different reactions.
- A bot can keep watching through a brief loss of sight, approach the last sound quietly, check the area and give up an empty search.

**Movement & cover**

- Combat strafing, pauses to steady a shot, head movement, leaning, quick peeks and short prefires. A PMC can chase around a corner, jump-peek or fire during a tactical jump.
- Bots can step out just far enough to see a watched angle, bait a shot with a peek or fake reload, and switch cover outdoors. On the way to safety they can lower their sights, face the threat or sprint away in a zigzag.
- Bots reconsider cover when hits expose it. They skip bare bushes in autumn and winter, still use protective trunks and walls, and check doorframes before trusting them as cover.

**Doors & room clearing**

- Approach from the side, step-peek, jump-peek, run past the opening or ambush the exit. Bots can fake a grenade pull, heal or stim to draw someone out.
- Squadmates take turns at the door and cover the rear. A bot can open or breach, clear a room during a search, or send a grenade in first and wait for the blast. An enemy coming out can interrupt the plan.
- Bots route around open door panels, retry stalled interactions and recover from blocked movement. Closing a door to clear a route is optional.

**Squad tactics**

- Crossfires, a detached flanker with covering fire, rear guards and second-entrance watches. Squads can lay siege to a building when they can cover every identified exit.
- Coordinated pushes against a vulnerable enemy. Each member can join or stay back. When a player engages the squad, a hidden thrower can use a grenade while a teammate holds the angle.
- Cover for teammates who heal or reload, reactions to a witnessed squadmate death, and fire discipline when a friendly crosses the barrel. Squad callouts are configurable.

**Weapons, reloads & grenades**

- Weapon choice accounts for distance, sights, ammo and newly looted guns. PMCs can draw a loaded pistol under pressure, switch back to their primary, fire short suppression bursts and check that their muzzle clears cover. Faster aim recovery for DMRs and sniper rifles is optional.
- Bots time their reloads, drop a mag for an emergency reload, and can return after combat for that same magazine with its remaining rounds.
- Grenade choice considers the target's last position, fuse, range, roof and doorway. PMCs distinguish frags from flashes, avoid their own throws and scatter to separate safe spots according to personality. Flash effects persist, and supported CS gas accounts for exposure and protection.

**Treatment & regrouping**

- First aid, bleeding control, stims and surgery. Bots tuck back into cover for treatment and can stop surgery when a threat closes in.
- After combat they can keep their weapon up, watch the last threat, reload, check the last known position or rejoin their leader.

**Settings, replays & reliability**

- Separate COMET difficulty presets, per-tactic settings and a web editor to save, import or share personal presets. COMET respects your SAIN preset and hands control back when its tactics finish.
- Fika support, ORBIT Ghost Mode and Questing Bots sleep handoffs, Raid Review squad and action events, and live action labels with HORIZON.
- Recovery for stuck hands, stale reloads, cover searches and failed plans. SAIN fixes cover sight checks, grenade paths, surgery, recoil and animation timing. Per-bot recovery handles interrupted actions, downing, revives and raid cleanup.

### How PMCs fight

- **Personality.** When you reload, a Chad might push while a Rat holds the stairs. Wounds, low ammo or a dead squadmate can change either one's mind.
- **Memory.** Keep peeking the same window and a PMC may be ready for your next peek. That memory ends with the fight.
- **Bosses and factions.** A guard or a RUAF, UNTAR, ISB or Black Division patrol means possible backup, especially early in the raid.
- **Movement.** PMCs can strafe, jump-peek, bait a shot, then peek from a different angle or change cover entirely.
- **Doorways.** A PMC might fake a heal or a grenade pull to draw you out, hold the exit, or clear the room while a teammate covers.

### Squad tactics

- **Crossfires and flanks.** While one PMC has eyes on you, another can move for a second angle. If the squad loses you, one member can break off to flank while the others cover. A larger group can leave someone watching the rear.
- **Covering a teammate.** One PMC can watch the approach while another heals or reloads. They give firing teammates room and can hold fire when a friend crosses their sights.
- **Coordinated pushes.** A squad can rush an enemy it thinks is vulnerable. Each PMC decides whether to go. Hurt one member of a duo and you might get one attacker and one still holding the angle.
- **Grenade suppression.** When you open fire on a squad, a member out of your sight can throw a grenade while another holds the angle you're fighting from.
- **Second entrances and sieges.** One PMC can watch a side entrance and decide whether to go in. If a squad outside can cover every exit it's identified, each member can take an exit and wait you out.
- **Losing a squadmate.** A witness can go after the attacker if they've identified them. With only a rough idea of where the shot came from, a survivor can stay behind cover and watch that direction.

### Sight, sound & cover

- **Light.** Shadows can buy you or another bot time on first contact. Ambushing PMCs can switch off their lights and lasers.
- **Sound.** PMCs can follow footsteps, hear less between floors, and crouch for the last few metres of a search.
- **Cover.** Bare bushes lose their value in autumn and winter. Solid trunks still work, and taking hits can make a PMC change cover.

### Gear & survival

- **Weapons.** PMCs choose a gun for the distance, fire short bursts to save ammo, and can draw a pistol when their rifle runs dry.
- **Dropped magazines.** A PMC can ditch a mag for a faster reload, then come back after the fight for that same mag and its remaining rounds.
- **Grenades.** PMCs pick a frag or flash to suit the throw, adjust their arc indoors, and scatter when a grenade lands among them.
- **Field care.** Wounded PMCs prioritize bleeding, use stims, and seek cover for surgery. After the fight they can reload, keep watch or rejoin their squad.

### Install & configure

1. Require **official SAIN 4.5.1** and SAIN's dependencies. COMET disables itself if it detects a modified SAIN build.
2. Extract the COMET ZIP into your SPT root. Let both `BepInEx` and `SPT_Runtime` merge with the existing folders.
3. Start the SPT server and open the editor. On a hosted server, use the host's address and port followed by `/comet`. [Open COMET settings locally](https://127.0.0.1:6969/comet)

**Hard** is the default. **Normal** and **Easy** dial back how often some of the more demanding tactics happen. The web editor lets you change individual behaviors, from doorway jumps to squad callouts, and save or share your own presets. Saved settings take effect next raid.

### Compatibility

#### Recommended mods

- **[SAIN Personality Mixer](https://sp-mod.com/index.php/addon/177/sain-personality-mixer)** lets you choose how often each PMC personality appears, so you can still run into Rats and Timmies at higher levels.
- **[Bot Healing Fixes](https://sp-mod.com/mod/3140/bot-healing-fixes)** helps wounded bots finish healing and recover when treatment gets interrupted.
- **[Separate Hostility](https://sp-mod.com/mod/2248/separate-hostility)** fixes cases where bots that should be enemies ignore each other.
- **[Twitch Players](https://sp-mod.com/mod/1895/sain-twitch-players)** is a SAIN preset for tougher fights. A GigaChad PMC can line up a shot faster than a Normal PMC.
- **[Satellite](https://sp-mod.com/mod/3373/satellite)** for the live raid map and replay.

#### Other supported mods

- **[Fika](https://sp-mod.com/mod/2326/project-fika).** Install the matching COMET client on each player's PC and on the host or headless. Fika support is built in.
- **[ORBIT](https://sp-mod.com/mod/2706/orbit-30), [Questing Bots](https://sp-mod.com/mod/1109/questing-bots) and [Looting Bots](https://sp-mod.com/mod/812/looting-bots).** These are optional. Your usual mods still handle travel, quests and loot. COMET yields when ORBIT puts a bot in Ghost Mode or Questing Bots puts it to sleep.
- **[Winter Outbreak](https://sp-mod.com/mod/3392/winter-outbreak).** COMET keeps the seasonal footstep sounds.
- **[Manimal's CS Gas](https://sp-mod.com/mod/2864/manimals-cs-gas) 2.0.0 or 2.0.1.** Time spent in a gas cloud and the protection a bot is wearing affect its vision.
- **[Raid Review](https://sp-mod.com/mod/1479/raid-review) and [HORIZON](https://sp-mod.com/mod/3362/horizon).** Raid Review shows squad roles, cover changes and magazine pickups in the replay. HORIZON's Bot Info shows each bot's current action live after you die.

#### Incompatible

- **Modified SAIN builds**: incompatible. COMET requires the official SAIN 4.5.1 DLLs and disables itself if they have been modified. Custom SAIN presets are fine.
- **[LiveRaid](https://sp-mod.com/mod/3253/liveraid):** incompatible. Both mods change the same SAIN combat decisions. For example, COMET can send a PMC to cover and LiveRaid can turn that into a push. I recommend picking one.

#### Known overlap

COMET already uses local lighting to decide how quickly bots spot you. If you use either mod alongside COMET, I recommend disabling its lighting effect so darkness doesn't slow detection twice.

- **Ombarella (Kobe's version)**: Kobe recommends turning **Light Level** off. If you're on **v3.0.0**, open **F12 > Ombarella > b - Main Settings** and set **3-Light meter strength** to **0**.
- **[That's Lit](https://sp-mod.com/mod/3450/thats-lit-logical-ai-vision-spt-41-port)**:  Open **F12 > That's Lit > 2. Darkness / Brightness** and turn **Enable** off. You can leave the mod itself enabled under **1. Main**.

I haven't personally tested either mod with COMET. These settings address the lighting overlap; full compatibility is still unconfirmed.

### FAQ

**Will COMET cost me FPS?**

YES. Keeping the impact on FPS as low as possible is a top priority. COMET spreads work across frames to limit stutters. The extra AI still takes some CPU time, so the impact depends on your setup and how many bots are fighting.

**Can I keep my SAIN preset?**

Yes. Keep the SAIN preset you like and use COMET's web editor to tune how often PMCs try its tactics.

**How should I set SAIN's AI vs AI limits?**

In SAIN's F6 menu, I recommend turning all three AI vs AI limits **OFF** (Global Toggle, Vision and Hearing). Distant squads can then spot and hear each other at your preset's usual ranges, which is especially noticeable in freecam. COMET also works with the limits on.

### AI

Yep, I used AI to make the AI feel less like AI 😂

If that's a dealbreaker for you, skip this one. If you judge a mod by what it does in a raid, give COMET a shot.

We spent hundreds of hours of our free time iterating, testing and tweaking it across hundreds of raids. Ask me how many of those hours I spent watching the same PMC try to open a door. Actually, please don't.

And if you're here looking for SLOP... well, I did leave some in there for you to find 👀

### Credits

[**R_F**](https://sp-mod.com/user/15954/r-f) designed most of the combat tactics and gave me permission to bring them to official SAIN. [**Chazut**](https://sp-mod.com/user/53567/chazut) built the standalone mod, added original features and integrations, and made the web editor. Thanks to **[Solarint](https://sp-mod.com/user/27463/solarint) and the SAIN contributors/forkers**, [**liquidwarp**](https://sp-mod.com/user/120411/liquidwarp) for the SAIN fixes, and to everyone who sent clips of PMCs doing brilliant or deeply questionable things.

### Support

**Which replay setting do I need?** Leave **Essential replay data** on for Raid Review or Satellite. It includes actions supported by your viewer, squad tactics and magazine pickups. Turn on **Extended replay data** for decision scores and diagnostic reports. Both settings apply next raid and require a replay plugin on the AI host.

**Found a bug?** Open a report in the **Issues** tab on Forge. For detailed logs, turn off **Diagnostics > Quiet logging** before the raid and attach the files from `BepInEx/COMET/diagnostics`. With Fika, use the AI host's logs.

If you enjoy COMET and want to fuel our next late-night test raid, you can buy either of us a coffee ☕

- [Chazut on Ko-fi](https://ko-fi.com/chazut)
- [R_F on Buy Me a Coffee](https://buymeacoffee.com/real_feels)
