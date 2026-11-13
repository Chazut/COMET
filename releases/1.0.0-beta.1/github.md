# COMET Beta 1

A very early beta for weekend testing. Not all planned features are included, some still need work, and bugs are expected.

COMET adds combat decisions, movement, door tactics, squad coordination and equipment management for **PMCs and AI Player Scavs**. It requires SAIN; ORBIT is optional.

The features in this beta come from **R_F's work**, adapted and tweaked to my taste. More original features are planned. **A huge thank you to R_F - this mod would not exist without him.**

## Getting started

- Requires **SPT 4.1.6**, **original SAIN 4.5.1** and SAIN's dependencies.
- Extract `COMET-1.0.0-beta.1.zip` into your SPT folder.
- Configure it at [COMET settings](https://127.0.0.1:6969/comet). Changes apply next raid.
- **Fika support is included in the same ZIP.** Install the matching build on the AI host/headless and playing clients, with the server files on your SPT server. I haven't tested this beta with Fika yet, so multiplayer support is experimental.

## Reporting bugs

Keep **Diagnostics > Quiet logging OFF**. Send the files from `BepInEx/COMET/diagnostics` and `BepInEx/LogOutput.log`, with the bot's name, approximate time and what went wrong.

The optional `RaidReview-1.10.0-COMET-beta.1.zip` lets you follow COMET decisions and squad tactics in the replay. Install both its client and server files, then restart the game and server. Play a new raid to record COMET events.
