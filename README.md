# COMET

**Coordinated Opponents, Movement & Engagement Tactics**

COMET adds tactical combat behavior for PMCs and AI Player Scavs on top of the original SAIN. It has its own client plugin, server configuration and web interface. SAIN remains required; ORBIT and Raid Review are optional.

**Development preview.** Implementation and automated tests are in place, and live raid testing has started. This is not a finished release. Automated checks do not establish reliable behavior in Unity or multiplayer.

## Features

- Tactical choices shaped by personality, equipment, known threats and squad support.
- Doorway approaches, peeks, feints, room entry and ambushes.
- Cover selection, repositioning, movement and recovery from blocked routes.
- Hearing, contact memory and reactions to observed enemy actions.
- Reloading, weapon changes, grenades, healing and tactical equipment.
- Squad coordination and protection against firing through teammates.
- Server-side settings, personal presets and Easy, Normal or Hard profiles. Hard is the default.
- Decision logs and optional Raid Review reports, map badges and hover details.

COMET controls standard PMC bots and AI bots that simulate Player Scavs. Human players (including human Player Scavs), ordinary scavs, bosses and custom bot roles are excluded. SAIN must already control the bot; COMET keeps its personality and SAIN settings. In Fika, only the AI host makes decisions. ORBIT Ghost Mode still pauses COMET until the bot wakes.

## Integrations

Requires **official SAIN 4.5.1** and its dependencies on **SPT 4.1**. Startup checks the SAIN binaries and COMET hooks before activating.

Supported integration targets are SAIN alone, SAIN with ORBIT, or SAIN with Questing Bots and Looting Bots. These combinations still need live testing. ORBIT combined with Questing Bots and Looting Bots is not a supported target.

`Fika/` contains the optional transport plugin for COMET's door corrections. Multiplayer behavior still needs validation.

Configuration is available in the server web interface at `/comet`. Changes apply to the next raid. Local diagnostic files are written to `BepInEx/COMET/diagnostics`. The matching Raid Review integration displays decisions and observed outcomes separately: selecting an action does not prove it completed.

COMET takes priority over ordinary SAIN squad tactics when it has a usable combat plan. Grenade avoidance, active healing/reloading, close combat and safety retreats keep priority. Diagnostics distinguish accepted plans from observed native actions; an unobserved core action yields after 2 seconds of eligible observation, with a 3-second retry delay. Native cover continuation keeps the original COMET plan when its target and destination still match.

For a stutter investigation, search **Performance** in that interface and enable **Record COMET performance**, then save and play a raid. It is off by default. Reports in `BepInEx/COMET/performance` contain feature timings, managed allocations, slow frames and separate diagnostic/subscriber costs. Keep the whole file and turn capture off afterwards. Measurements add overhead and cover COMET, not the entire game or GPU; a GC counter beside a hitch does not prove COMET caused it. Completed raids have a `performance-end` footer; interrupted sessions keep the report windows already written.

## Build and test

Use the **.NET 10 SDK** and your own local game/mod references. The development projects currently use Windows paths under `C:/Games/SPT-4.1` and `C:/Dev`; adjust those paths for your setup.

Place the official SAIN 4.5.1 `SAIN.dll` and `SAIN.Preset.Shared.dll` in `references/sain-4.5.1/`. Game and dependency binaries are not included in this repository. The optional Fika project also needs a local Fika.Core reference.

Run from the repository root:

```powershell
New-Item -ItemType Directory -Path artifacts -Force | Out-Null
dotnet build Client/Comet.Client.csproj -c Release
dotnet build Server/Comet.Server.csproj -c Release
dotnet run --project Tests/Comet.Tests.csproj -c Release
dotnet run --project Tests/IntegrationHarness/IntegrationHarness.csproj -c Release
```

These commands do not install the mod. `Tools/verify.ps1` runs the broader test suites and Raid Review integration checks; it requires the matching Raid Review checkout on its `comet-integration` branch and the Fika build reference.

Sources, test fixtures, the local web preview and build/verification tools are included. Internal planning documents, generated reports, local configuration, binaries and one-time migration/preview scripts stay outside Git.

## Credits

SAIN by Solarint and its contributors provides the foundation. R_F's zzap work and developer kit informed COMET's features and regression scenarios, with permission to reuse his contributions. COMET also includes its own changes and features. No modified SAIN binary is distributed here.
