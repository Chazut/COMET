## COMET 1.0.0 RC1

Changes since Beta 4.

### Squad tactics

- **Flanking has clearer roles.** One PMC can break off after the squad loses sight of an enemy. Teammates cover, and larger squads can leave a rear guard. The squad can push together once the flanker reaches position or spots the target.
- **Crossfire and assaults.** An expired flank opportunity still leaves crossfire available. Flanks also work with crossfire disabled. Before calling a rush, the PMC checks its route and loaded ammo.
- **Alternate entrances.** A PMC at a second entrance checks whether it still has reason to believe the enemy is inside. Fresh contact can extend the watch once. If it commits, it sprints through.
- **Losing a teammate.** Before retaliating, a survivor checks that its target is linked to the loss. Even the last squad member can push or hold an angle depending on personality.

### Doors, cover and deception

- **Fighting at a doorway.** Nearby teammates, sounds at the door, injuries and low ammo influence whether a PMC clears the room, peeks or sets an ambush. A room clear can include a fake grenade pull before entry.
- **Getting past doors.** Bots can route around open door panels and follow approaches already queued by SAIN. They can retry quickly after a brief interruption and back off longer after a failed opening.
- **Changing cover outdoors.** A bot can finish relocating even if SAIN recalculates its route. Once there, it holds the new cover for 5 to 10 seconds. Relocation and cover feints share a short cooldown so the bot doesn't immediately swap from one to the other.
- **Bait peeks and fake reloads.** A bold PMC can sprint out on the first peek, pull back on contact, then try the opposite side at a different height. A PMC faking a reload watches the last visible point on the enemy's route. If the first feint can't start, it can try the other.

### Combat decisions, grenades and healing

- **Choosing the next move.** PMCs use the enemy's current distance and distinguish a blocked shot from a target outside their weapon's effective range. Glimpsing the same enemy again can leave the previous plan in place.
- **Grenade throws.** Before throwing at an unseen enemy, bots consider what they have on hand, their injuries, the distance and how recently they located the target. They try lower arcs through doorways and on short indoor throws. By default, they prefer shorter fuses up close or under a roof, and longer ones for distant outdoor throws.
- **Healing under pressure.** Personality can make a bot hesitate over routine treatment. Urgent care keeps priority. Medical decisions account for the last sound heard from each known enemy, and retreating to heal only gets extra priority if the bot has supplies.

### After a firefight

- **Keeping watch.** A PMC can stop, lower its stance and watch the last threat while reloading. On the move, it can look where it's going, turn towards a sound or watch the direction incoming fire was last reported from.
- **Reloading and regrouping.** Followers can reload on the way to cover or while catching up with their current leader. If the leader moves or changes, they can update the regroup order. A pending treatment can keep a bot on watch. If SAIN declines it, the bot can carry on moving.
- **Checking the last known position.** A PMC's injury status affects whether it goes to investigate. If it does, it has to get within three metres of the remembered contact to finish the check.

### Other fixes and settings

- Ammo checks now include the chambered round for squad pushes, reload feints, post-fight reloads and firing around corners.
- Bots respect refused peek movements. A bot that has lost sight of its enemy and has neither cover nor a route can briefly hold position when it isn't under fire.
- Fixed an interaction between COMET's footstep reporting and seasonal footstep sounds. The web editor also hides obsolete movement and assault settings. Existing presets remain compatible.
- **Raid Observatory** now tracks sieges, grenade suppression, squad members staying back, boss and guard recognition, exposed or seasonal cover, lighting and dropped magazines separately. Recovery is confirmed when the original magazine is back in the bot's inventory.

A few more improvements and small fixes are planned for the official 1.0.0 release if RC1 testing goes well.
