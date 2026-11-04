param([string]$RaidReview = 'C:\Dev\spt-raid-review-fork')
$ErrorActionPreference = 'Stop'
$cometRoot = Split-Path -Parent $PSScriptRoot
function Run([string]$Program, [string[]]$Arguments) {
    & $Program @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Program failed with exit code $LASTEXITCODE" }
}
Push-Location $cometRoot
try {
    Run dotnet @('build', 'Client/Comet.Client.csproj', '-c', 'Release', '--nologo', '-v', 'quiet')
    Run dotnet @('build', 'Fika/Comet.Fika.csproj', '-c', 'Release', '--nologo', '-v', 'quiet')
    Run dotnet @('build', 'Server/Comet.Server.csproj', '-c', 'Release', '--nologo', '-v', 'quiet')
    Run dotnet @('run', '--project', 'Tests/Comet.Tests.csproj', '-c', 'Release')
    Run dotnet @('run', '--project', 'Tests/IntegrationHarness/IntegrationHarness.csproj', '-c', 'Release')
    Run dotnet @('run', '--project', 'Tests/ObserverHarness/ObserverHarness.csproj', '-c', 'Release')
    Run dotnet @('run', '--project', 'Tests/ControlHarness/ControlHarness.csproj', '-c', 'Release')
    Run dotnet @('run', '--project', 'Tests/FaultHarness/FaultHarness.csproj', '-c', 'Release')
    Run dotnet @('run', '--project', 'Tests/LifecycleHarness/LifecycleHarness.csproj', '-c', 'Release')
    Run dotnet @('run', '--project', 'Tests/ValidityHarness/ValidityHarness.csproj', '-c', 'Release')
    Run dotnet @('run', '--project', 'Tests/RaidCleanupHarness/RaidCleanupHarness.csproj', '-c', 'Release')
    Run dotnet @('run', '--project', 'Tests/PresetHarness/PresetHarness.csproj', '-c', 'Release')
    Run dotnet @('run', '--project', 'Tests/CoverScanHarness/CoverScanHarness.csproj', '-c', 'Release')
    Run dotnet @('run', '--project', 'Tests/MovementRecoveryHarness/MovementRecoveryHarness.csproj', '-c', 'Release')
    Run dotnet @('run', '--project', 'Tests/DoorFikaHarness/DoorFikaHarness.csproj', '-c', 'Release')
    Run dotnet @('run', '--project', 'Tests/DoorMotionHarness/DoorMotionHarness.csproj', '-c', 'Release')
    Run dotnet @('run', '--project', 'Tests/DoorClosingHarness/DoorClosingHarness.csproj', '-c', 'Release')
    Run dotnet @('run', '--project', 'Tests/DoorGeometryHarness/DoorGeometryHarness.csproj', '-c', 'Release')
    Run dotnet @('run', '--project', 'Tests/DoorRecoveryHarness/DoorRecoveryHarness.csproj', '-c', 'Release')
    Run dotnet @('run', '--project', 'Tests/DoorTransitHarness/DoorTransitHarness.csproj', '-c', 'Release')
    Run dotnet @('run', '--project', 'Tests/OutcomeHarness/OutcomeHarness.csproj', '-c', 'Release')
    Run dotnet @('run', '--project', 'Tests/JournalHarness/JournalHarness.csproj', '-c', 'Release')
    Run dotnet @('run', '--project', 'Tests/EncounterHarness/EncounterHarness.csproj', '-c', 'Release')
    Run dotnet @('run', '--project', 'Tests/RecoilHarness/RecoilHarness.csproj', '-c', 'Release')
    Run dotnet @('run', '--project', 'Tests/HandsHarness/HandsHarness.csproj', '-c', 'Release')
    Run dotnet @('run', '--project', 'Tests/GrenadeHarness/GrenadeHarness.csproj', '-c', 'Release')
    Run dotnet @('run', '--project', 'Tests/GrenadeTrajectoryHarness/GrenadeTrajectoryHarness.csproj', '-c', 'Release')
    Run dotnet @('run', '--project', 'Tests/GrenadeChoiceHarness/GrenadeChoiceHarness.csproj', '-c', 'Release')
    Run dotnet @('run', '--project', 'Tests/OwnGrenadeHarness/OwnGrenadeHarness.csproj', '-c', 'Release')
    Run dotnet @('run', '--project', 'Tests/GrenadeReactionHarness/GrenadeReactionHarness.csproj', '-c', 'Release')
    Run dotnet @('run', '--project', 'Tests/GrenadeDispersionHarness/GrenadeDispersionHarness.csproj', '-c', 'Release')
    Run dotnet @('run', '--project', 'Tests/GasHarness/GasHarness.csproj', '-c', 'Release')
    Run dotnet @('run', '--project', 'Tests/FlashHarness/FlashHarness.csproj', '-c', 'Release')
    Run dotnet @('run', '--project', 'Tests/MedicalHarness/MedicalHarness.csproj', '-c', 'Release')
    Run dotnet @('run', '--project', 'Tests/MedicalPostureHarness/MedicalPostureHarness.csproj', '-c', 'Release')
    Run dotnet @('run', '--project', 'Tests/PostCombatHarness/PostCombatHarness.csproj', '-c', 'Release')
    Run dotnet @('run', '--project', 'Tests/SquadVoiceHarness/SquadVoiceHarness.csproj', '-c', 'Release')
    Run dotnet @('run', '--project', 'Tests/LightHarness/LightHarness.csproj', '-c', 'Release')
    Run dotnet @('run', '--project', 'Tests/DevicesHarness/DevicesHarness.csproj', '-c', 'Release')
    Run dotnet @('run', '--project', 'Tests/RestLightsHarness/RestLightsHarness.csproj', '-c', 'Release')
    Run dotnet @('run', '--project', 'Tests/SquadFireHarness/SquadFireHarness.csproj', '-c', 'Release')
    Run dotnet @('run', '--project', 'Tests/FireLaneHarness/FireLaneHarness.csproj', '-c', 'Release')
    Run dotnet @('run', '--project', 'Tests/CrossfireHarness/CrossfireHarness.csproj', '-c', 'Release')
    Run dotnet @('run', '--project', 'Tests/ReadyStanceHarness/ReadyStanceHarness.csproj', '-c', 'Release')
    Run dotnet @('run', '--project', 'Tests/ExplosionHarness/ExplosionHarness.csproj', '-c', 'Release')
    Run dotnet @('run', '--project', 'Tests/GrenadeFilterHarness/GrenadeFilterHarness.csproj', '-c', 'Release')
} finally { Pop-Location }
if ((& git -C $RaidReview branch --show-current) -ne 'comet-integration') { throw 'RR must be on comet-integration.' }
Push-Location (Join-Path $RaidReview 'Private')
try { Run npm.cmd @('run', 'build') } finally { Pop-Location }
# Frontend must finish first: RR embeds its generated files in the server DLL.
Push-Location $RaidReview
try {
    Run node @('Tests/comet-map.cjs')
    Run node @('Tests/comet-counters.cjs')
    Run node @('Tests/comet-journal.cjs')
    Run node @('Tests/participant-visuals.cjs')
    Run dotnet @('build', 'Client/RAID-REVIEW.csproj', '-c', 'Release', "-p:OutputPath=$cometRoot/artifacts/rr-client/", '--nologo', '-v', 'quiet')
    Run dotnet @('build', 'ServerMod/ServerMod.csproj', '-c', 'Release', '-p:SkipDeploy=true', '--nologo', '-v', 'quiet')
    foreach ($suite in @('BehaviorDecisions', 'RecordingPipeline', 'RaidTransfer')) {
        Run dotnet @('run', '--project', "Tests/$suite/$suite.csproj", '-c', 'Release')
    }
} finally { Pop-Location }
