<p align="center"><img src="branding/comet-logo-transparent.svg" alt="COMET" width="150"></p>
<p align="center">
  <img src="https://img.shields.io/github/stars/Chazut/COMET?style=flat-square&label=STARS&color=007ec6" />
  <img src="https://img.shields.io/github/issues/Chazut/COMET?style=flat-square&label=ISSUES&color=44cc11" />
  <img src="https://img.shields.io/github/downloads/Chazut/COMET/total?style=flat-square&label=DOWNLOADS&color=44cc11" />
</p>

# COMET

**Coordinated Opponents, Movement & Engagement Tactics**

Lose sight of a PMC in Dorms and you might hear a reload at the next doorway. It could be a feint. If you keep watching that door, their teammate might be the one you meet at the other entrance.

**COMET 1.0.0** builds on **official SAIN 4.5.1** to make PMCs fight more like real players. Its tactics apply to PMCs and AI player scavs, including when they fight each other. Ordinary scavs and bosses keep their usual combat behavior. [Full feature list](MODPAGE.md#features)

## What you'll see in a raid

| In the raid | What changes |
| --- | --- |
| A squad sees an enemy enter a building | **Siege.** If squadmates outside can cover every identified exit from their positions, each watches an exit for a limited time. A lone PMC watching a second entrance is a separate tactic. |
| You open fire on a squad | **Grenade suppression.** A member out of your sight can throw a grenade while a teammate holds an angle on the same contact. |
| A PMC spots a guard early in the raid | **Boss and faction recognition.** A guard can be enough to make a cautious PMC expect company and reconsider a rush. This also applies to factions such as RUAF, UNTAR, ISB and Black Division. The expectation fades later in the raid. The PMC still has to find the escorts. |
| A squad decides to push | **Individual commitment.** One member can stay back while the others go. Personality, nearby support and the kind of enemy they spotted influence that choice. |
| You or another bot step into shadow | **Local lighting.** Shade can slow first detection. A PMC that saw the target in the last 10 seconds uses normal SAIN timing on a repeat peek. Close range, recent hits, active night vision or the target's white light also preserve normal timing. Bots looking at the same AI target share a reading, with at most one new AI lighting measurement per frame. |
| A PMC takes fire in a bush | **Seasonal and exposed cover.** In autumn and winter, bots skip bare foliage when choosing cover. A solid trunk or wall still counts if it shields the bot from the threat. In any season, once incoming hits make SAIN flag a position as exposed, COMET releases it so the bot can choose another. |
| A bot drops its magazine in a hurry | **Magazine recovery.** After combat the owner can go back for the actual magazine. A pickup only counts as recovered once that item is back in its inventory. Recovery is enabled by default under **Quick reload**. |

## The combat tactics

- **Doors and corners.** A bot can run past an opening for a look, jump-peek, watch a doorway from the side, or fake a grenade pull or a heal to draw you out. Injuries, ammo and signs of someone in the room influence whether it goes in.
- **Squad roles.** When a squad loses sight of its target, one member can break off to flank while the others cover. A larger group can leave a rear guard. A survivor can react to the loss of a teammate by pushing or watching the attacker's angle.
- **Reading an opponent.** A PMC can notice gear it has actually seen and learn that you keep peeking the same corner. That adaptation lasts for the encounter. Personality and occasional mistakes keep two bots from always making the same choice.
- **Following sound.** PMCs can hear other bots walk, jump and land. Footsteps on another floor are harder to hear. A PMC following a sound can crouch for the last few metres, check the spot and call off the search if it finds nothing.
- **Moving under fire.** A PMC can lean out, strafe, bait a shot and peek from a different height. Outdoors it can change cover after contact, keep its weapon on the threat while backing away or sprint in a zigzag.
- **Weapons and survival.** Bots weigh an emergency reload against drawing a sidearm, choose grenades for the space they're fighting in, and decide when there's time to treat an injury. After contact, one can watch the last threat while reloading or catch up with its leader.

## Installation and settings

1. Install **SPT 4.1.6**, the **official SAIN 4.5.1** and its dependencies. COMET checks the official SAIN files at startup and disables itself on a modified fork, including zzap. [Download SAIN 4.5.1](https://github.com/ArchangelWTF/SAIN/releases/tag/v4.5.1).
2. Extract **both folders** from the [release ZIP](https://github.com/Chazut/COMET/releases) into the SPT root. The client goes in `BepInEx/plugins/COMET` and the server mod in `SPT_Runtime/user/mods/COMET`.
3. Start the SPT server and open the [COMET web settings](https://127.0.0.1:6969/comet). For a remote server, use its address and port followed by `/comet`.

**Hard** is the default COMET preset. Choose **Normal** or **Easy** to dial the tactics back, or tune individual features and save your own preset. You can import and export presets in the web editor. Saved changes apply next raid. SAIN's preset has its own controls.

## Playing with other mods

**Recommended mods**

- [Bot Healing Fixes](https://sp-mod.com/mod/3140/bot-healing-fixes) helps bots finish treatment after interruptions.
- [Separate Hostility](https://sp-mod.com/mod/2248/separate-hostility) fixes cases where bots that should be enemies ignore each other.
- [Twitch Players](https://sp-mod.com/mod/1895/sain-twitch-players) is a SAIN preset for tougher fights. A GigaChad PMC can line up a shot faster than a Normal PMC.
- [SAIN Personality Mixer](https://sp-mod.com/index.php/addon/177/sain-personality-mixer) lets you choose how often each PMC personality appears, so you can still run into Rats and Timmies at higher levels.

| Setup | What to expect |
| --- | --- |
| [ORBIT](https://sp-mod.com/mod/2706/orbit-30), [Questing Bots](https://sp-mod.com/mod/1109/questing-bots), [Looting Bots](https://sp-mod.com/mod/812/looting-bots) | Your existing mods handle quests, loot and travel. COMET yields for ORBIT Ghost Mode and Questing Bots sleep. |
| [Fika](https://sp-mod.com/mod/2326/project-fika) | The client DLL detects Fika automatically. Use matching client builds on the AI host or headless and each playing client. Install the server folder on the SPT server. When upgrading from the split plugin, remove the old `COMET.Fika.dll`. |
| [Winter Outbreak](https://sp-mod.com/mod/3392/winter-outbreak) | COMET preserves seasonal footstep sounds. |
| [Manimal's CS Gas](https://sp-mod.com/mod/2864/manimals-cs-gas) 2.0.0 or 2.0.1 | Time spent in the gas and the bot's protective gear affect its vision. |
| [Raid Review](https://sp-mod.com/mod/1479/raid-review) | The replay can show squad assignments, cover changes and magazine recovery. A chosen tactic and a completed action can appear as separate events. |
| [HORIZON](https://sp-mod.com/mod/3362/horizon) | After you die, Bot Info shows what each bot is doing while you spectate. |

## FAQ

**Which diagnostics should I enable?** **Combat journal** is on by default so Raid Review can show contacts, damage and time spent on each action. You can turn it off and still replay the raid, including squad tactics and magazine pickups. The other four options are off by default. Enable **Decision counters** for tactic statistics, or **Weapon changes and loops**, **Control transitions** or **Performance capture** to investigate a specific issue. Changes apply next raid.

On upgrade, COMET applies these five settings once and backs up the previous config if it changes. You can then switch any of them on or off for future raids.

**Will COMET cost me FPS?** Keeping the performance impact as low as possible is a top priority. COMET spreads work across frames to limit stutters. When several bots look at the same AI target, they reuse its lighting measurement. The extra combat decisions still take CPU time, so the impact depends on your setup and how many bots are fighting.

**Can I keep my SAIN preset?** Yes. Use SAIN for your usual AI settings. COMET's own web editor lets you adjust individual tactics or change how often PMCs try them.

**How should I set SAIN's AI vs AI limits?** In SAIN's F6 menu, I recommend turning all three AI vs AI limits **OFF** (Global Toggle, Vision and Hearing). Distant squads can then spot and hear each other at your preset's usual ranges, which is especially noticeable in freecam. COMET also works with the limits on.

## AI

Yep, I used AI to make the AI feel less like AI 😂

We spent hundreds of hours testing and tweaking COMET across hundreds of raids. If you want to see how it plays, give it a shot. And if you're looking for SLOP, there might be something in there for you 👀

## Credits

**R_F** designed most of the combat tactics in his SAIN fork and gave permission to port them. His developer kit also helped with testing. **Chazut** created the standalone port, the COMET additions, the web editor and the integrations. [R_F](https://sp-mod.com/user/15954/r-f) · [Chazut](https://sp-mod.com/user/53567/chazut)

Thanks to **Solarint**, the SAIN contributors and fork maintainers for their work, and to everyone who tested COMET, reported a bug or sent a clip. [Solarint](https://sp-mod.com/user/27463/solarint)

Thanks to **[liquidwarp](https://github.com/liquidwarp/SPT-SAIN)** for identifying and fixing native SAIN issues with raycasts, enemy checks, surgery, recoil and animation timing. COMET adapts these fixes while keeping Bot Healing Fixes compatible.

## Support

**Found a bug?** Open the **Issues** tab on Forge or GitHub. Include the map, bot name and approximate raid time. For detailed logs, turn **Diagnostics > Quiet logging** off before the raid and attach the relevant files from `BepInEx/COMET/diagnostics`. With Fika, collect the AI host's logs. Quiet mode still allows Raid Review events. [GitHub issues](https://github.com/Chazut/COMET/issues)

Local storage keeps the latest 20 raids by default and archives older logs within a 5 GB budget. You can adjust this under **F12 > COMET > Diagnostics storage**. [Storage details](DIAGNOSTIC_STORAGE.md)

If you enjoy COMET and want to fuel our next late-night test raid, you can buy either of us a coffee ☕

- [Chazut on Ko-fi](https://ko-fi.com/chazut)
- [R_F on Buy Me a Coffee](https://buymeacoffee.com/real_feels)

---

[Build from source](BUILDING.md) · [MIT license](LICENSE) · [Attribution](THIRD_PARTY_NOTICES.md)
