# COMET beta 2

- Fixed bots not firing during some angle-holding and movement actions.
- Fixed grenade throw preparation and bots staying stuck avoiding old grenades.
- Fixed flashlight/laser detection and squad member identification.
- Reduced repeated calculations and diagnostic memory allocations.
- Fika support is now built into `COMET.dll` and activates automatically when needed.

**Updating from beta 1:** close the game and server, overwrite COMET's client and server files, and remove the old `COMET.Fika.dll`. Your settings are kept.

Requires **SPT 4.1.6** and **original SAIN 4.5.1**. ORBIT is optional. Fika still needs multiplayer raid testing.

Magazine recovery after quick reload is not included in this build; it still needs raid testing. Emergency quick reload itself is unchanged.

Still an early beta. Please include diagnostics with bug reports. Thanks again to **R_F** for the work behind COMET, and everyone testing it!
