# Building COMET


Use the **.NET 10 SDK** and your own local game/mod references. The development projects currently use Windows paths under `C:/Games/SPT-4.1` and `C:/Dev`; adjust those paths for your setup.

Place the official SAIN 4.5.1 `SAIN.dll` and `SAIN.Preset.Shared.dll` in `references/sain-4.5.1/`. Game and dependency binaries are not included in this repository. Building the client also requires the Fika.Core build reference specified in `Fika/Comet.Fika.csproj`; Fika remains optional at runtime.

The client build first compiles a reference pass, builds the private Fika transport against it, then embeds that transport in the final `COMET.dll`. Ship only the main client DLL. Do not ship `COMET.Fika.Integration.dll` or the old `COMET.Fika.dll` separately.

Run from the repository root:

```powershell
New-Item -ItemType Directory -Path artifacts -Force | Out-Null
dotnet build Client/Comet.Client.csproj -c Release
dotnet build Server/Comet.Server.csproj -c Release
dotnet run --project Tests/Comet.Tests.csproj -c Release
dotnet run --project Tests/IntegrationHarness/IntegrationHarness.csproj -c Release
dotnet run --project Tests/EmbeddedFikaHarness/EmbeddedFikaHarness.csproj -c Release -- Client/bin/Release/netstandard2.1/COMET.dll Fika/bin/Release/netstandard2.1/COMET.Fika.Integration.dll
```

These commands do not install the mod. `Tools/verify.ps1` runs the broader test suites and Raid Review integration checks; it requires the matching Raid Review checkout on its `comet-integration` branch and the Fika build reference.

Sources, test fixtures, the local web preview and build/verification tools are included. Internal planning documents, generated reports, local configuration, binaries and one-time migration/preview scripts stay outside Git.


See [SOURCE_BASELINE.md](SOURCE_BASELINE.md) for the behavior baseline.
