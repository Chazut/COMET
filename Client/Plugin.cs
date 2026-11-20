using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Bootstrap;
using Comet.Core;
using Comfort.Common;
using EFT;
using HarmonyLib;
using Newtonsoft.Json;
using SAIN.Components;
using SAIN.Models.Enums;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes;
using SAIN.SAINComponent.Classes.Decision;
using SAIN.SAINComponent.Classes.EnemyClasses;
using SAIN.SAINComponent.Classes.Mover;
using SAIN.SAINComponent.Classes.WeaponFunction;
using SAIN.SAINComponent.SubComponents.CoverFinder;
using SPT.Common.Http;
using UnityEngine;

namespace Comet.Client;

[BepInPlugin("com.chazut.comet", "COMET", CometVersion.Plugin)]
[BepInDependency("me.sol.sain", "4.5.1")]
[BepInDependency("com.manimal.csgas", BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency("com.fika.core", BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency("com.chazut.orbit", BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency("com.danw.questingbots", BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency("me.skwizzy.lootingbots", BepInDependency.DependencyFlags.SoftDependency)]
[BepInProcess("EscapeFromTarkov.exe")]
public sealed partial class Plugin : BaseUnityPlugin
{
    internal static Plugin? Instance;
    private readonly EmbeddedFika embeddedFika = new();
    private bool fikaIntegrationReady;
    private readonly Dictionary<BotComponent, BotState> states =
        new Dictionary<BotComponent, BotState>();
    private Harmony? harmony;
    private GameWorld? world;
    private readonly RaidConfigLoad<CometConfig> loading = new();
    private CometConfig? config;
    private Diagnostics? diagnostics;
    private string session = "";
    private long sequence;
    private float nextSweep;
    private readonly List<KeyValuePair<BotComponent, BotState>> maintenanceSnapshot = new(64);
    private bool subscriberWarned;
    private PropertyInfo? fikaServer;
    private Func<bool>? readFikaServer;
    private bool fikaPresent;
    private Func<string, bool>? isGhost;
    private bool orbitUnavailable;
    private RaidDiagnostics.DiagnosticStorage? diagnosticStorage;

    private void Awake()
    {
        Logger.LogInfo("COMET " + CometVersion.Release);
        try
        {
            diagnosticStorage = new(
                Config,
                Path.Combine(BepInEx.Paths.BepInExRootPath, "COMET"),
                "COMET",
                message => Logger.LogInfo(message),
                message => Logger.LogWarning(message)
            );
        }
        catch (Exception e)
        {
            Logger.LogWarning("Diagnostic storage configuration: " + e.Message);
        }
        try
        {
            RunIntegration("official-sain-contracts", VerifyIntegrationFiles);
            integration.Report.Phase = "optional-integrations";
            fikaPresent = Chainloader.PluginInfos.ContainsKey("com.fika.core");
            fikaServer = AccessTools
                .TypeByName("Fika.Core.Main.Utils.FikaBackendUtils")
                ?.GetProperty("IsServer", BindingFlags.Public | BindingFlags.Static);
            if (fikaPresent && fikaServer?.PropertyType != typeof(bool))
                throw new InvalidOperationException(
                    "Unknown Fika authority API. COMET is inactive."
                );
            if (fikaPresent)
                readFikaServer =
                    (Func<bool>)
                        Delegate.CreateDelegate(typeof(Func<bool>), fikaServer!.GetGetMethod()!);
            var orbit = AccessTools.TypeByName("Orbit.Api.OrbitTelemetry");
            if (orbit != null)
            {
                var method = orbit.GetMethod("IsBotDormant", new[] { typeof(string) });
                if (method == null)
                    orbitUnavailable = true;
                else
                    isGhost =
                        (Func<string, bool>)
                            Delegate.CreateDelegate(typeof(Func<string, bool>), method);
            }
            // The player may choose host/replica later, when creating or joining a raid.
            integration.Report.Role = fikaPresent ? "Fika (authority checked in raid)" : "solo";
            if (orbitUnavailable)
                integration.Report.Notices = new[]
                {
                    "ORBIT is installed but its Ghost API is unavailable. COMET will not control bots until this is resolved.",
                };
            harmony = new Harmony("com.chazut.comet");
            ConnectControlIntegrations(harmony);
            integration.Report.Phase = "core-decision-hooks";
            harmony.Patch(
                AccessTools.Method(typeof(EnemyDecisionClass), "GetDecision"),
                prefix: new HarmonyMethod(typeof(Plugin), nameof(Choose))
            );
            harmony.Patch(
                AccessTools.Method(typeof(BotDecisionManager), "SetDecisions"),
                prefix: new HarmonyMethod(typeof(Plugin), nameof(ArbitrateSquad)),
                postfix: new HarmonyMethod(typeof(Plugin), nameof(Accepted))
            );
            harmony.Patch(
                AccessTools.Method(typeof(SAINCoverClass), "FindCoverPoint"),
                prefix: new HarmonyMethod(typeof(Plugin), nameof(FindCover)),
                postfix: new HarmonyMethod(typeof(Plugin), nameof(ObserveNativeCover))
            );
            harmony.Patch(
                AccessTools.Method(typeof(BotDecisionManager), "ContinueMoveToCover"),
                postfix: new HarmonyMethod(typeof(Plugin), nameof(ContinueCover))
            );

            RunIntegration("PatchSightContinuity", () => PatchSightContinuity(harmony));

            RunIntegration("PatchFear", () => PatchFear(harmony));

            RunIntegration("PatchSquadTrade", () => PatchSquadTrade(harmony));

            RunIntegration("PatchSquadStorm", () => PatchSquadStorm(harmony));

            RunIntegration("PatchFireLane", () => PatchFireLane(harmony));

            RunIntegration("PatchValidity", () => PatchValidity(harmony));

            RunIntegration("PatchRaidCleanup", () => PatchRaidCleanup(harmony));

            RunIntegration("PatchSquadFire", () => PatchSquadFire(harmony));

            RunIntegration("PatchSquadVoice", () => PatchSquadVoice(harmony));

            RunIntegration("PatchLightDiscipline", () => PatchLightDiscipline(harmony));

            RunIntegration("PatchTacticalDevices", () => PatchTacticalDevices(harmony));

            RunIntegration("PatchRestLights", () => PatchRestLights(harmony));

            RunIntegration("PatchThreatSelection", () => PatchThreatSelection(harmony));

            RunIntegration("PatchLoadout", () => PatchLoadout(harmony));

            RunIntegration("PatchOpponent", () => PatchOpponent(harmony));

            RunIntegration("PatchOpportunity", () => PatchOpportunity(harmony));

            RunIntegration("PatchMovementHearing", () => PatchMovementHearing(harmony));

            RunIntegration("PatchQuietApproach", () => PatchQuietApproach(harmony));

            RunIntegration("PatchGunfire", () => PatchGunfire(harmony));

            RunIntegration("PatchFloorHearing", () => PatchFloorHearing(harmony));

            RunIntegration("PatchCloseDetection", () => PatchCloseDetection(harmony));
            RunIntegration("PatchLocalExposure", () => PatchLocalExposure(harmony));
            RunIntegration("PatchLightDetection", () => PatchLightDetection(harmony));
            RunIntegration("PatchLiquidwarpFixes", () => PatchLiquidwarpFixes(harmony));
            RunIntegration("PatchRemixDecisions", () => PatchRemixDecisions(harmony));
            RunIntegration("PatchRemixNavigation", () => PatchRemixNavigation(harmony));
            RunIntegration("PatchRemixTeardown", () => PatchRemixTeardown(harmony));

            RunIntegration("PatchImpactReaction", () => PatchImpactReaction(harmony));

            RunIntegration("PatchOutcomes", () => PatchOutcomes(harmony));

            RunIntegration("PatchDoorTransit", () => PatchDoorTransit(harmony));

            RunIntegration("PatchDoorRecovery", () => PatchDoorRecovery(harmony));

            RunIntegration("PatchDoorMotion", () => PatchDoorMotion(harmony));

            RunIntegration("PatchDoorClosing", () => PatchDoorClosing(harmony));

            RunIntegration("PatchMovementRecovery", () => PatchMovementRecovery(harmony));

            RunIntegration("PatchCoverScan", () => PatchCoverScan(harmony));
            RunIntegration("PatchSeasonalCover", () => PatchSeasonalCover(harmony));
            RunIntegration("PatchCoverExposure", () => PatchCoverExposure(harmony));

            RunIntegration("PatchSilentSearch", () => PatchSilentSearch(harmony));
            RunIntegration("PatchSearchExecution", () => PatchSearchExecution(harmony));
            RunIntegration("PatchHiddenExecution", () => PatchHiddenExecution(harmony));
            RunIntegration("PatchSourceSearch", () => PatchSourceSearch(harmony));

            RunIntegration("PatchSearchAbandonment", () => PatchSearchAbandonment(harmony));

            RunIntegration("PatchDoorApproach", () => PatchDoorApproach(harmony));

            RunIntegration("PatchDoorAmbush", () => PatchDoorAmbush(harmony));

            RunIntegration("PatchDoorFake", () => PatchDoorFake(harmony));

            RunIntegration("PatchDoorRoomClear", () => PatchDoorRoomClear(harmony));

            RunIntegration("PatchDoorEmergency", () => PatchDoorEmergency(harmony));

            RunIntegration("PatchCombatStep", () => PatchCombatStep(harmony));

            RunIntegration("PatchCombatLean", () => PatchCombatLean(harmony));

            RunIntegration("PatchCornerChase", () => PatchCornerChase(harmony));

            RunIntegration("PatchCornerPrefire", () => PatchCornerPrefire(harmony));

            RunIntegration("PatchAngleWatch", () => PatchAngleWatch(harmony));

            RunIntegration("PatchHoldAngle", () => PatchHoldAngle(harmony));

            RunIntegration("PatchCoverTravel", () => PatchCoverTravel(harmony));

            RunIntegration("PatchAdsTravel", () => PatchAdsTravel(harmony));

            RunIntegration("PatchRetreatWeave", () => PatchRetreatWeave(harmony));

            RunIntegration("PatchTacticalJump", () => PatchTacticalJump(harmony));

            RunIntegration("PatchOutdoorRelocation", () => PatchOutdoorRelocation(harmony));

            RunIntegration("PatchCoverFeint", () => PatchCoverFeint(harmony));

            RunIntegration("PatchReloadTiming", () => PatchReloadTiming(harmony));
            RunIntegration("PatchReloadInventory", () => PatchReloadInventory(harmony));

            RunIntegration("PatchCombatJournal", () => PatchCombatJournal(harmony));
            RunIntegration("PatchWeaponReturn", () => PatchWeaponReturn(harmony));

            RunIntegration("PatchWeaponSync", () => PatchWeaponSync(harmony));

            RunIntegration("PatchQuickReload", () => PatchQuickReload(harmony));

            RunIntegration("PatchSuppressionDiscipline", () => PatchSuppressionDiscipline(harmony));

            RunIntegration("PatchShotGeometry", () => PatchShotGeometry(harmony));

            RunIntegration("PatchRecoilRecovery", () => PatchRecoilRecovery(harmony));

            RunIntegration("PatchHandsRecovery", () => PatchHandsRecovery(harmony));

            RunIntegration("PatchGrenadeJudgment", () => PatchGrenadeJudgment(harmony));

            RunIntegration("PatchGrenadeFilter", () => PatchGrenadeFilter(harmony));

            RunIntegration("PatchOwnGrenadeSafety", () => PatchOwnGrenadeSafety(harmony));

            RunIntegration("PatchGrenadeReaction", () => PatchGrenadeReaction(harmony));

            RunIntegration("PatchGasExposure", () => PatchGasExposure(harmony));

            RunIntegration("PatchFlashContinuity", () => PatchFlashContinuity(harmony));

            RunIntegration("PatchExplosionAwareness", () => PatchExplosionAwareness(harmony));

            RunIntegration("PatchMedicalChoice", () => PatchMedicalChoice(harmony));

            RunIntegration("PatchMedicalPosture", () => PatchMedicalPosture(harmony));

            RunIntegration("PatchPostCombat", () => PatchPostCombat(harmony));

            RunIntegration("PatchReadyStance", () => PatchReadyStance(harmony));

            RunIntegration("PatchCrossfire", () => PatchCrossfire(harmony));
            RunIntegration("flank-execution", () => PatchFlank(harmony));
            RunIntegration("fault-owner-contracts", PrepareFaultOwners);
            RunIntegration("PatchDoorEntry", () => PatchDoorEntry(harmony!));
            RunIntegration("installed-hook-manifest", VerifyInstalledIntegration);
            FinishIntegration();
            try
            {
                var warm = DiagnosticWarmup.Prepare(
                    typeof(Plugin).Assembly,
                    Diagnostics.SnapshotJson.ContractResolver!
                );
                var readers =
                    Diagnostics.SnapshotJson.ContractResolver as PreparedDiagnosticResolver;
                Logger.LogInfo(
                    $"Diagnostics prepared before raids: {warm.Contracts} contracts, {warm.Methods} methods, {warm.Failed} unavailable, {readers?.ReadersPrepared ?? 0} readers, {readers?.ReadersFallback ?? 0} reader fallbacks, {(Diagnostics.SnapshotJson.ContractResolver as SnapshotDiagnosticResolver)?.WritersPrepared ?? 0} typed writers, {warm.Milliseconds:F1} ms."
                );
                if (warm.Failed > 0)
                    Logger.LogWarning(
                        "Diagnostic preparation details: " + string.Join("; ", warm.Unavailable)
                    );
                Logger.LogInfo(
                    "Diagnostics use isolated schema; backend converters cannot disable typed snapshots."
                );
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Diagnostic warmup unavailable: " + ex.Message);
            }
        }
        catch (Exception ex)
        {
            FailIntegration(ex);
            return;
        }
        fikaIntegrationReady = true;
    }

    private void Start()
    {
        // Start runs after the chainloader has registered every plugin, including
        // legacy addons that depend on COMET and therefore awaken after us.
        if (!fikaIntegrationReady)
            return;
        embeddedFika.Start(
            fikaPresent,
            Chainloader.PluginInfos.ContainsKey("com.chazut.comet.fika"),
            () =>
                EmbeddedFika.Load(
                    typeof(Plugin).Assembly,
                    "COMET.Fika.Integration.dll",
                    "Comet.Fika.DoorBridge"
                ),
            Logger,
            message => Logger.LogInfo(message),
            message => Logger.LogError(message)
        );
    }

    private bool Authority => !fikaPresent || readFikaServer!();

    private bool Ghost(string profileId) => Sleeping(profileId);

    private void Update()
    {
        embeddedFika.Update();
        diagnosticStorage?.Tick(Singleton<GameWorld>.Instantiated, Time.realtimeSinceStartup);
        PerformanceCapture.Tick();
        Api.Decisions.Pump(Time.frameCount);
        if (world != null && Time.realtimeSinceStartup >= nextDeliveryReport)
        {
            nextDeliveryReport = Time.realtimeSinceStartup + 5;
            diagnostics?.Write(
                Diagnostics.Serialize(
                    new
                    {
                        schema = 1,
                        session,
                        stage = "diagnostic-delivery",
                        realtime = Time.realtimeSinceStartup,
                        delivery = Api.Decisions.DeliveryStats(),
                    }
                )
            );
        }
        FlushFloorCounters();
        FlushCloseCounters();
        FlushLightDetection();
        FlushNativeFixes();
        FlushImpactCounters();
        var current = Singleton<GameWorld>.Instance;
        if (current == null)
            current = null; // Normalize destroyed Unity objects.
        if (current != null && current.LocationId == "hideout")
            current = null;
        if (!ReferenceEquals(world, current))
        {
            EndRaid();
            if (current != null && raidLifetime.Begin(current))
            {
                world = current;
                session = Guid.NewGuid().ToString("N");
                sequence = 0;
                Api.Decisions.RaidSession = session;
                Api.Decisions.RaidClosed = false;
                var log = Logger; // Writer holds the logger, never the Plugin/world.
                try
                {
                    diagnostics = new Diagnostics(
                        Path.Combine(BepInEx.Paths.PluginPath, "..", "COMET", "diagnostics"),
                        session,
                        text => log.LogWarning(text)
                    );
                }
                catch (Exception ex)
                {
                    Logger.LogError(
                        "COMET cannot open diagnostic file; RR events remain available. "
                            + ex.Message
                    );
                }
                diagnostics?.WriteEssential(
                    Diagnostics.Serialize(
                        new
                        {
                            schema = 1,
                            session,
                            stage = "raid-start",
                            map = current.LocationId,
                            botScope = "pmc-and-ai-player-scav",
                            utc = DateTime.UtcNow.ToString("O"),
                            realtime = Time.realtimeSinceStartup,
                            cometVersion = CometVersion.Release,
                            assemblyMvid = typeof(Plugin).Assembly.ManifestModule.ModuleVersionId.ToString(),
                            sainMvid = typeof(BotComponent).Assembly.ManifestModule.ModuleVersionId.ToString(),
                            authority = Authority,
                            fika = fikaPresent,
                            diagnosticMode = "awaiting-config",
                            segmentBytes = 64L * 1024 * 1024,
                        }
                    )
                );
                loading.Start(raidLifetime.Generation, static () =>
                {
                    var c = JsonConvert.DeserializeObject<CometConfig>(
                        RequestHandler.GetJson("/comet/config")
                    ) ?? throw new InvalidDataException("Missing COMET config.");
                    c.Validate();
                    return c;
                });
            }
        }
        if (!raidLifetime.Active)
            return;
        if (loading.Take(raidLifetime.Generation, out var loaded, out var loadError))
        {
            if (loaded != null)
                diagnostics?.ConfigureLogging(loaded.QuietLogging, loaded.Revision, session);
            if (loaded != null)
                StartPerformance(loaded);
            if (loaded != null)
                diagnostics?.ConfigureSnapshot(loaded, session);
            if (loaded != null)
            {
                config = loaded;
                diagnostics?.ConfigureJournal(
                    config.Enabled && config.CombatJournal.Enabled && Authority,
                    config.Enabled && config.WeaponJournal.Enabled && Authority,
                    config.Revision,
                    session
                );
                diagnostics?.ConfigureControlJournal(
                    config.Enabled && config.ControlJournal.Enabled && Authority,
                    config.Revision,
                    session
                );
                diagnostics?.ConfigureCounters(
                    config.Enabled && config.TacticCounters.Enabled && Authority,
                    config.TacticCounters.MaxOpportunities,
                    config.Revision,
                    session
                );
                Logger.LogInfo(
                    "COMET raid config revision " + config.Revision + ", enabled=" + config.Enabled
                );
                ReportDifficultyPreset();
            }
            else
                Logger.LogError(
                    "COMET config unavailable. SAIN retains control for this raid. "
                        + loadError?.Message
                );
            if (loaded?.Enabled == true)
            {
                if (loaded.SecondaryEntrance.Enabled && loaded.SecondaryEntrance.Siege && Authority)
                    RefreshSiegeRooms();
                ConfigureExistingRemixBots();
            }
        }
        UpdateLocalDoors();
        UpdateExposureDiagnostics();
        FlushTacticalProbes();
        SampleSainPreset();
        SampleControlHandoffs();
        SampleLifecycles();
        ResetFaultedBots();
        if (config != null && config.Enabled)
            foreach (var pair in states)
            {
                SampleDecisionExecution(pair.Key, pair.Value);
                SampleCombatJournal(pair.Key, pair.Value);
                SampleCounterExecution(pair.Key, pair.Value);
                SampleOutcome(pair.Key, pair.Value);
                SampleEncounter(pair.Key, pair.Value);
                try
                {
                    SampleMedicalPosture(pair.Key, pair.Value);
                }
                catch (Exception ex)
                {
                    MedicalPostureError(pair.Value, ex);
                }
                RefreshFear(pair.Key, pair.Value);
                if (config.SightContinuity.Enabled)
                    RefreshSight(pair.Key, pair.Value);
                try
                {
                    SampleCoverTravel(pair.Key, pair.Value);
                }
                catch (Exception ex)
                {
                    CoverTravelError(pair.Value, ex);
                }
                if (pair.Value.Siege != null)
                    SampleSiege(pair.Key, pair.Value);
                try
                {
                    SampleHoldAngle(pair.Key, pair.Value);
                }
                catch (Exception ex)
                {
                    HoldAngleError(pair.Value, ex);
                }
                try
                {
                    SampleAngle(pair.Key, pair.Value);
                }
                catch (Exception ex)
                {
                    AngleError(pair.Value, ex);
                }
                try
                {
                    SampleChase(pair.Key, pair.Value);
                    RefreshPrefire(pair.Key, pair.Value);
                }
                catch (Exception ex)
                {
                    ChaseError(pair.Value, ex);
                }
            }
        CheckCoverScans();
        foreach (var pair in states)
            try
            {
                SampleMovementRecovery(pair.Key, pair.Value);
            }
            catch (Exception ex)
            {
                MovementRecoveryError(pair.Value, ex);
            }
        UpdateDoorMotion();
        UpdateDoorClosing();
        foreach (var pair in states)
        {
            WatchDoorRecovery(pair.Key, pair.Value);
            WatchDoorTransit(pair.Key, pair.Value);
        }
        foreach (var pair in states)
            if (pair.Value.Entry.Policy.Active)
                RefreshDoorEntry(pair.Key, pair.Value);
        foreach (var pair in states)
        {
            SampleLightDiscipline(pair.Key, pair.Value);
            SampleTacticalDevices(pair.Key, pair.Value);
            SampleRestLights(pair.Key, pair.Value);
            SampleCrossfire(pair.Key, pair.Value);
            SampleLane(pair.Key, pair.Value);
            SampleSquadFire(pair.Key, pair.Value);
            SampleReadyStance(pair.Key, pair.Value);
            WatchAdsTravel(pair.Key, pair.Value);
            try
            {
                SampleWeave(pair.Key, pair.Value);
            }
            catch (Exception ex)
            {
                WeaveError(pair.Value, ex);
            }
            try
            {
                SampleMedical(pair.Key, pair.Value);
            }
            catch (Exception ex)
            {
                MedicalError(pair.Value, ex);
            }
            try
            {
                SampleHandsRecovery(pair.Key, pair.Value);
            }
            catch (Exception ex)
            {
                HandsRecoveryError(pair.Value, ex);
            }
            try
            {
                SampleWeaponSync(pair.Key, pair.Value);
            }
            catch (Exception ex)
            {
                WeaponSyncError(pair.Value, ex);
            }
            try
            {
                SampleDiscipline(pair.Key, pair.Value);
            }
            catch (Exception ex)
            {
                DisciplineError(pair.Value, ex);
            }
            try
            {
                SampleReloadTiming(pair.Key, pair.Value);
            }
            catch (Exception ex)
            {
                ReloadTimingError(pair.Value, ex);
            }
            try
            {
                SamplePistolSwap(pair.Key, pair.Value);
            }
            catch (Exception ex)
            {
                PistolSwapError(pair.Value, ex);
            }
            try
            {
                SampleQuickReload(pair.Key, pair.Value);
            }
            catch (Exception ex)
            {
                QuickReloadError(pair.Value, ex);
            }
            try
            {
                SampleWeaponReturn(pair.Key, pair.Value);
            }
            catch (Exception ex)
            {
                WeaponReturnError(pair.Value, ex);
            }
            try
            {
                SampleWeaponRange(pair.Key, pair.Value);
            }
            catch (Exception ex)
            {
                WeaponRangeError(pair.Value, ex);
            }
            try
            {
                SampleFeint(pair.Key, pair.Value);
            }
            catch (Exception ex)
            {
                FeintError(pair.Value, ex);
            }
            try
            {
                SampleRelocation(pair.Key, pair.Value);
            }
            catch (Exception ex)
            {
                RelocationError(pair.Value, ex);
            }
            try
            {
                SampleTacticalJump(pair.Key, pair.Value);
            }
            catch (Exception ex)
            {
                TacticalJumpError(pair.Value, ex);
            }
        }
        UpdateMovementHearing();
        UpdateBlasts();
        if (Time.unscaledTime >= nextSweep)
        {
            nextSweep = Time.unscaledTime + .25f;
            SweepClearGrenades();
            doorReservations.Sweep(Time.time);
            try
            {
                UpdateDoorSupports();
            }
            catch (Exception ex)
            {
                ClearDoorSupports("support-adapter-error");
                Logger.LogWarning("COMET support yielded: " + ex.Message);
            }
        }
        maintenanceSnapshot.Clear();
        foreach (var pair in states)
            maintenanceSnapshot.Add(pair);
        foreach (var pair in maintenanceSnapshot)
        {
            if (!pair.Value.Maintenance.Due(Time.unscaledTime))
                continue;
            var bot = pair.Key;
            try
            {
                if (bot == null)
                    continue;
                if (bot.GoalEnemy != null && !ContactSafety.AllowsGoal(bot))
                {
                    ReleaseThreat(bot, pair.Value, "contact-ended-own-squad");
                    Release(pair.Value, "contact-ended-own-squad");
                    continue;
                }
                if (!BlastScope(bot))
                    StopBlast(pair.Value, "explosion-out-of-scope");
                if (!GasScope(bot))
                    DropGas(bot, pair.Value, "gas-out-of-scope");
                if (!FlashScope(bot))
                    DropFlash(bot, pair.Value, "flash-out-of-scope", DeadFlashOwner(bot));
                GuardDoorReservation(bot, pair.Value);
                RefreshCombatStep(bot, pair.Value, false);
                RefreshCornerPeek(bot, pair.Value, false);
                GuardCombatLean(bot, pair.Value);
                RefreshChase(bot, pair.Value, false);
                RefreshDoorInterruption(bot, pair.Value);
                RefreshImpact(bot, pair.Value);
                if (pair.Value.Retired || bot == null || bot.IsDead)
                    continue; // C09 owns retirement and independent cleanup.
                else if (!bot.BotActive || Ghost(bot.ProfileId) || !Authority)
                {
                    EndCrossfire(bot, pair.Value, "crossfire-inactive");
                    EndReadyStance(pair.Value, "ready-stance-inactive");
                    EndPostCombat(bot, pair.Value, "post-combat-inactive", true);
                    DropGrenadeEscape(bot, pair.Value, "grenade-bot-inactive");
                    DropOwnSafety(bot, pair.Value, "own-grenade-inactive");
                    pair.Value.CloseEvidence.Clear();
                    pair.Value.Progress.Clear();
                    ReleaseThreat(bot, pair.Value, "threat-inactive-ghost-or-non-authority");
                    Release(pair.Value, "inactive-ghost-or-non-authority");
                    pair.Value.Handoff.Clear();
                }
                else if (bot.GoalEnemy == null && !pair.Value.Impact.Owned)
                {
                    ReleaseThreat(bot, pair.Value, "threat-contact-ended");
                    Release(pair.Value, "contact-ended");
                    pair.Value.Handoff.Clear();
                }
                // Visibility changes are reconciled inside Choose, in the same decision tick.
                // Eager cleanup here erased the old plan before its replacement was evaluated.
                if (bot != null && !bot.IsDead)
                {
                    RefreshDoorAmbush(bot, pair.Value);
                    RefreshDoorClear(bot, pair.Value);
                    RefreshDoorFake(bot, pair.Value);
                    RefreshDoorStep(bot, pair.Value);
                    RefreshDoorRun(bot, pair.Value);
                    RefreshDoorJump(bot, pair.Value);
                    RefreshDoor(bot, pair.Value);
                    if (!GasScope(bot))
                        DropGas(bot, pair.Value, "gas-out-of-scope");
                    if (!FlashScope(bot))
                        DropFlash(bot, pair.Value, "flash-out-of-scope", DeadFlashOwner(bot));
                    GuardDoorReservation(bot, pair.Value);
                    RefreshEnvironment(bot, pair.Value);
                    RefreshProgress(bot, pair.Value);
                    if (config != null)
                        RefreshQuiet(bot, pair.Value);
                }
            }
            catch (Exception ex)
            {
                DropFlash(bot, pair.Value, "flash-state-discarded", false);
                DropGas(bot, pair.Value, "gas-state-discarded");
                try
                {
                    StopQuiet(bot!, pair.Value, "quiet-adapter-error");
                }
                catch { }
                ReleaseThreat(bot!, pair.Value, "threat-adapter-error");
                Release(pair.Value, "adapter-error");
                DisposeDoorFake(pair.Value, "fake-state-discarded");
                motionWatches.Remove(bot!);
                states.Remove(bot!);
                Logger.LogWarning(ex.Message);
            }
        }
    }

    // A failed adapter call returns the default result and leaves the native decision in control.
    private enum VisibleHandoff { Sain, Selected, HiddenRanking }

    private VisibleHandoff ChooseVisible(
        BotComponent bot, Enemy enemy, BotState state, Situation situation,
        ref ECombatDecision result, ref bool __result
    )
    {
        bool keepHiddenRanking = state.Engine.HasUnexpiredPreference(situation.Now);
        bool canResumeHidden = state.Engine.CanResumeOnSight(situation);
        if (!config!.VisibleCombat.Enabled)
        {
            if (canResumeHidden && HiddenSightMayTakeOver(bot, enemy, null))
                return VisibleHandoff.HiddenRanking;
            YieldToSain(state, "visible-combat-disabled", preserveHiddenPreference: keepHiddenRanking);
            return VisibleHandoff.Sain;
        }
        state.Situation = situation;
        if (!canResumeHidden && state.VisibleEngine.NeedsEvidence(situation, bot.Medical.TimeSinceShot < 1.5f))
            ZzapUtilityCapture.Fill(bot, enemy, situation);
        var visible = VisibleAdapter.Capture(bot, enemy, situation, state, config);
        state.VisibleSituation = visible;
        VisibleChoice? choice = null;
        if (canResumeHidden)
        {
            // With a blocked shot A02 can only reuse a ranking, so no score inputs are needed yet.
            choice = state.VisibleEngine.Evaluate(visible);
            if (HiddenSightMayTakeOver(bot, enemy, choice))
                return VisibleHandoff.HiddenRanking;
        }
        ProgressAdapter.Candidates(bot, state, situation);
        situation.Opportunity = ReadOpportunity(
            bot,
            enemy,
            state,
            situation,
            visible
        );
        situation.Outcome = ReviewOutcome(bot, state);
        choice ??= state.VisibleEngine.Evaluate(visible);
        if (state.Feature != "A02")
        {
            Release(state, "visible-contact-handoff", false, preserveVisiblePlan: true,
                preserveHiddenPreference: keepHiddenRanking);
            state.Feature = "A02";
            choice.Changed |= choice.Decision.HasValue;
        }
        if (choice.Reason == "plan-kept-pending-exposure")
            ContinuityEvent(state, "kept", choice.Reason);
        state.CoverResponseAtSelection = visible.CoverResponse;
        state.OpponentAtSelection = situation.Opponent?.Copy();
        state.Mixing = choice.Mixing;
        state.Decision = choice.Decision?.ToString() ?? "";
        state.Candidates = choice.Candidates;
        RecordOutcomeChoice(state, situation);
        RecordOpportunity(state, situation);
        string responseKey =
            visible.CoverResponse?.State
            + ":"
            + visible.CoverResponse?.Reason
            + ":"
            + visible.CoverId;
        if (
            config.CoverResponse.Enabled
            && responseKey != state.CoverResponseLogKey
            && Time.time - state.CoverResponseLogAt >= 1
        )
        {
            state.CoverResponseLogAt = Time.time;
            state.CoverResponseLogKey = responseKey;
            Emit(
                state,
                "evaluated",
                visible.CoverResponse?.Reason ?? "cover-response-unavailable"
            );
        }
        if (choice.Changed || choice.Reconsidered)
        {
            state.FearAtSelection = state.Fear.Current;
            state.LoadoutAtSelection = situation.Loadout?.Copy();
        }
        if (choice.Changed)
        {
            Emit(
                state,
                choice.Decision == null ? "released" : "selected",
                choice.Reason
            );
            state.ActiveLabel = "";
            Api.Decisions.Set(state.ProfileId, "");
        }
        else if (choice.Reconsidered)
            Emit(state, "evaluated", "cover-plan-retained:" + choice.Reason);
        if (!choice.Decision.HasValue)
        {
            if (
                !choice.Changed
                && (
                    state.LastRefusal != choice.Reason
                    || Time.time - state.LastRefusalAt > 5
                )
            )
            {
                Emit(state, "rejected", choice.Reason);
                state.LastRefusal = choice.Reason;
                state.LastRefusalAt = Time.time;
            }
            YieldToSain(
                state,
                choice.Reason,
                preserveVisiblePlan: choice.KeepRankingOnHandoff,
                preserveHiddenPreference: keepHiddenRanking
            );
            return VisibleHandoff.Sain;
        }
        state.LastRefusal = "";
        state.Expected =
            choice.Decision == VisibleTactic.Shoot ? ECombatDecision.StandAndShoot
            : choice.Decision == VisibleTactic.Cover ? ECombatDecision.SeekCover
            : ECombatDecision.RushEnemy;
        state.RequestedFrame = Time.frameCount;
        result = state.Expected;
        __result = true;
        return VisibleHandoff.Selected;
    }

    private static bool Choose(
        EnemyDecisionClass __instance,
        ref ECombatDecision result,
        Enemy enemy,
        EnemyList knownEnemies,
        ref bool __result
    )
    {
        var plugin = Instance;
        if (plugin == null || plugin.config == null)
            return true;
        var bot = __instance.Bot;
        if (!NativeAdapter.IsSupportedAi(bot) || enemy == null)
        {
            if (bot != null && plugin.states.TryGetValue(bot, out var old))
            {
                plugin.Release(old, "contact-ended-or-out-of-scope");
                old.Handoff.Clear();
            }
            return true;
        }
        try
        {
            if (!plugin.states.TryGetValue(bot, out var state))
            {
                state = new BotState(bot, plugin.config, plugin.session);
                plugin.states.Add(bot, state);
            }
            state.Door.NativeFallback = state.Door.NativeSelected = false;
            if (!ContactSafety.Allows(bot, enemy))
            {
                plugin.ReleaseThreat(bot, state, "contact-ended-own-squad");
                plugin.Release(state, "contact-ended-own-squad");
                if (state.RejectedContact != enemy.EnemyProfileId)
                {
                    state.RejectedContact = enemy.EnemyProfileId;
                    state.Situation = new Situation { Target = enemy.EnemyProfileId };
                    plugin.Emit(state, "rejected", "contact-own-squad-rejected", "A13");
                }
                return true;
            }
            state.RejectedContact = "";
            var situation = NativeAdapter.Capture(
                bot,
                enemy,
                plugin.Authority,
                plugin.Ghost(bot.ProfileId)
            );
            if (
                !plugin.config.Enabled
                || !situation.IsAuthority
                || !situation.IsActive
                || situation.Ghost
            )
            {
                plugin.Release(state, "disabled-inactive-ghost-or-non-authority");
                state.Handoff.Clear();
                return true;
            }
            EnemyRoleCapture.Observe(enemy, situation, state);
            plugin.SampleOutcome(bot, state);
            state.Situation = situation;
            if (plugin.ObservePlanService(bot, state, false))
                return true;
            if (NativeChoiceHasPriority(bot, state))
            {
                plugin.YieldToSain(state, "handoff-native-priority", "priority");
                return true;
            }
            if (!plugin.GuardDoorReservation(bot, state))
                return true;
            plugin.RefreshEnvironment(bot, state);
            if (Time.time < state.FaultUntil)
                return true;
            plugin.RefreshDoorInterruption(bot, state);
            if (plugin.RequestDoorEmergency(bot, state, ref result, ref __result))
                return false;
            if (plugin.RequestDoorResume(bot, state, ref result, ref __result))
                return false;
            if (plugin.KeepDoorSupport(bot, state, ref result, ref __result))
                return false;
            if (plugin.KeepDoorEntry(bot, state, ref result, ref __result))
                return false;
            if (plugin.KeepDoorClear(bot, state, ref result, ref __result))
                return false;
            if (plugin.KeepCombatDoor(bot, enemy, state, ref result, ref __result))
                return false;
            if (plugin.KeepDoorAmbush(bot, state, ref result, ref __result))
                return false;
            if (plugin.KeepDoorFake(bot, state, ref result, ref __result))
                return false;
            if (
                state.CoverFeint.Policy.Active
                && plugin.ChooseFeint(bot, enemy, state, ref result, ref __result)
            )
                return false;
            if (
                state.Relocation.Policy.Active
                && plugin.ChooseRelocation(bot, enemy, state, ref result, ref __result)
            )
                return false;
            if (
                !VisibleAdapter.WeaponReady(bot)
                && !JumpOwnOpening(bot, state)
                && !RunOwnOpening(bot, state)
                && !EntranceOwnOpening(bot, state)
            )
            {
                plugin.YieldToSain(state, "handoff-native-priority", "priority");
                return true;
            }
            if (MedicalPostureGuard(bot))
                return true;
            if (plugin.HoldForSquadGrenade(bot, state, enemy, ref result, ref __result))
                return false;
            if (plugin.HoldForSiege(bot, state, ref result, ref __result))
                return false;
            if (plugin.ChooseNativeStorm(bot, state, ref result, ref __result))
                return false;
            if (plugin.ContinueCrossfireDecision(bot, state, ref result, ref __result))
                return false;
            if (state.Handoff.Blocked(Time.time, HandoffContact(situation)))
                return true;
            plugin.RefreshFear(bot, state);
            situation.Fear = state.Fear.Current;
            if (plugin.KeepCornerPeek(bot, state, enemy, ref result, ref __result))
                return false;
            situation.Loadout = plugin.ReadLoadout(bot, state);
            situation.Opponent = plugin.ReadOpponent(bot, enemy, state, situation);
            plugin.RefreshSight(bot, state);
            if (plugin.ChooseRelocation(bot, enemy, state, ref result, ref __result))
                return false;
            if (plugin.ChooseFeint(bot, enemy, state, ref result, ref __result))
                return false;
            plugin.RefreshProgress(bot, state);
            if (state.Sight.Current is SightHold sight)
            {
                // A brief visible sample can release A03 between two native decision ticks.
                // The next occlusion still needs a fresh selection/acceptance, even if the feature name has not changed.
                if (state.Feature != "A03" || state.Decision.Length == 0
                    || state.SightEvent?.Target != sight.Target || state.SightEvent.Action != sight.Action)
                {
                    plugin.Release(state, "brief-sight-loss", false, true,
                        preserveHiddenPreference: state.Engine.HasUnexpiredPreference(situation.Now));
                    state.Feature = "A03";
                    state.SightEpisode++;
                    state.Decision = sight.Action == "Push" ? "ContinuePush" : "KeepAngle";
                    state.SightEvent = sight;
                    state.FearAtSelection = state.Fear.Current;
                    state.LoadoutAtSelection = situation.Loadout?.Copy();
                    state.Situation = SightSituation(situation, sight);
                    state.Candidates = new List<Candidate>
                    {
                        new Candidate
                        {
                            Decision = state.Decision,
                            Eligible = true,
                            Reason = state.Sight.Reason,
                        },
                        new Candidate
                        {
                            Decision = "Search",
                            Eligible = false,
                            Reason = "sight-wait-for-window",
                        },
                    };
                    plugin.Emit(state, "selected", state.Sight.Reason);
                }
                state.SightEvent = sight;
                state.Situation = SightSituation(situation, sight);
                state.Expected =
                    sight.Action == "Push" ? ECombatDecision.RushEnemy
                    : sight.Action == "DogFight" ? ECombatDecision.DogFight
                    : ECombatDecision.StandAndShoot;
                state.RequestedFrame = Time.frameCount;
                result = state.Expected;
                __result = true;
                return false;
            }
            if (enemy.IsVisible)
            {
                var visibleResult = plugin.ChooseVisible(bot, enemy, state, situation, ref result, ref __result);
                if (visibleResult != VisibleHandoff.HiddenRanking)
                    return visibleResult == VisibleHandoff.Sain;
            }
            if (state.Feature != "A01")
            {
                plugin.Release(state, "hidden-contact-handoff", false, preserveVisiblePlan: true,
                    preserveHiddenPreference: state.Engine.HasUnexpiredPreference(situation.Now));
                state.Feature = "A01";
            }
            // A hidden primary target must not override SAIN's response to another visible threat.
            if (!situation.Visible && knownEnemies != null && knownEnemies.Any(e => e != null && e.IsVisible))
                situation.Visible = true;
            state.Situation = situation;
            var gate = state.Engine.Ineligible(situation);
            if (gate != null)
                plugin.EndSilentSearch(state, gate);
            if (gate != null)
            {
                plugin.YieldToSain(state, gate);
                return true;
            }
            if (state.Engine.NeedsEvidence(situation))
                ZzapUtilityCapture.Fill(bot, enemy, situation);
            NativeAdapter.Capabilities(bot, enemy, situation, state, plugin.config);
            if (
                !situation.Visible && !situation.Unavailable.ContainsKey(Tactic.Grenade)
                && plugin.StartSquadGrenade(bot, state, enemy, situation, ref result, ref __result)
            )
                return false;
            ProgressAdapter.Candidates(bot, state, situation);
            state.Intent.Apply(situation);
            situation.SilentSearch = plugin.ReadSilentSearch(bot, enemy, state, situation);
            situation.Encounter = plugin.ReadEncounter(bot, enemy, state, situation);
            situation.Outcome = plugin.ReviewOutcome(bot, state);
            situation.Opportunity = plugin.ReadOpportunity(bot, enemy, state, situation);
            state.HiddenExecution.Refusals.Clear();
            plugin.WatchHiddenMovement(bot, enemy, state);
            bool beingShot = HiddenBeingShot(bot);
            {
                Choice choice = state.Engine.Evaluate(situation, tactic =>
                    plugin.HiddenCanRun(bot, enemy, state, tactic, beingShot)
                );
                choice = plugin.KeepHiddenChoice(bot, enemy, state, choice, beingShot);
                state.OpponentAtSelection = situation.Opponent?.Copy();
                state.Choice = choice;
                state.Mixing = choice.Mixing;
                state.Decision = choice.Decision?.ToString() ?? "";
                state.Candidates = choice.Candidates;
                if (state.HiddenExecution.Refusals.Count > 0)
                    plugin.RecordHiddenExecution(
                        bot,
                        state,
                        choice.Decision.HasValue ? "hidden-native-fallback" : "hidden-none-runnable"
                    );
                RecordOutcomeChoice(state, situation);
                plugin.RecordOpportunity(state, situation);
                plugin.RecordSilentChoice(state, situation);
                plugin.RecordEncounterChoice(state, situation);
                if (choice.Changed)
                {
                    state.FearAtSelection = state.Fear.Current;
                    state.LoadoutAtSelection = situation.Loadout?.Copy();
                    plugin.Emit(
                        state,
                        choice.Decision == null ? "released" : "selected",
                        choice.Reason
                    );
                    state.ActiveLabel = "";
                    Api.Decisions.Set(state.ProfileId, "");
                }
                if (!choice.Decision.HasValue)
                {
                    plugin.YieldToSain(state, "no-feasible-option");
                    state.Door.NativeFallback = true;
                    return true;
                }
                var tactic = choice.Decision.Value;
                state.Expected = NativeAdapter.Map(tactic);
                if (tactic == Tactic.Push && state.Door.SessionPlan.Length > 0 && !state.Door.FromSearch)
                {
                    if (state.Door.SessionPlan == "trap")
                    {
                        state.Expected = ECombatDecision.Freeze;
                        state.Decision = "Hold";
                    }
                    else
                        state.Expected = ECombatDecision.Search;
                }
                state.RequestedFrame = Time.frameCount;
                result = state.Expected;
                __result = true;
                return false;
            }
        }
        catch (Exception ex)
        {
            if (plugin.states.TryGetValue(bot, out var state))
                plugin.YieldToSain(state, "adapter-exception", "error");
            plugin.Logger.LogWarning("COMET yielded to SAIN: " + ex);
            return true;
        }
    }

    private static void Accepted(BotDecisionManager __instance)
    {
        var plugin = Instance;
        if (plugin == null || !plugin.states.TryGetValue(__instance.Bot, out var state))
            return;
        try
        {
            if (
                plugin.config?.Enabled != true
                || state.Retired
                || !NativeAdapter.IsSupportedAi(__instance.Bot)
                || !__instance.Bot.BotActive
                || !plugin.Authority
                || plugin.Ghost(state.ProfileId)
            )
            {
                plugin.Release(state, "disabled-inactive-ghost-or-non-authority");
                return;
            }
            if (plugin.ObservePlanService(__instance.Bot, state, true))
                return;
            if (plugin.FeintAccepted(__instance, state))
            {
                state.RequestedFrame = -1;
                return;
            }
            if (plugin.RelocationAccepted(__instance, state))
            {
                state.RequestedFrame = -1;
                return;
            }
            if (plugin.ImpactDecisionObserved(__instance, state))
            {
                state.RequestedFrame = -1;
                return;
            }
            plugin.SilentDecisionObserved(__instance, state);
            bool applied = IntegrationContract.Accept(
                plugin.config?.Enabled == true
                    && !state.Retired
                    && NativeAdapter.IsSupportedAi(__instance.Bot)
                    && __instance.Bot.BotActive
                    && plugin.Authority
                    && !plugin.Ghost(state.ProfileId),
                state.Decision.Length > 0,
                state.RequestedFrame,
                Time.frameCount,
                (int)state.Expected,
                (int)__instance.CurrentCombatDecision,
                __instance.CurrentSelfDecision != ESelfActionType.None,
                __instance.CurrentSquadDecision != ESquadDecision.None
            );
            // Consume this request. A later native decision in the same frame is not ours.
            state.RequestedFrame = -1;
            plugin.SupportAccepted(state, applied);
            plugin.DoorResumeAccepted(state, applied);
            plugin.DoorEmergencyAccepted(state, applied);
            if (applied)
            {
                state.Handoff.Accepted(
                    Time.time,
                    state.Feature,
                    state.Decision,
                    HandoffContact(state.Situation)
                );
                if (state.Feature is "A01" or "A02" or "A03")
                    state.Execution.Accept(
                        Time.time,
                        state.Feature,
                        state.Decision,
                        state.Situation?.Target ?? ""
                    );
                else
                    state.Execution.Clear();
            }
            string label = applied ? "COMET:" + state.Decision : "";
            if (
                applied
                && state.Feature == "A02"
                && state.Decision == "Cover"
                && state.Situation?.Visible == true
            )
                state.Intent.Accepted(
                    state.Situation.Target,
                    Time.time,
                    plugin.config!.VisibleCombat.CoverMemory
                );
            else if (applied && state.Feature == "A02" && state.Decision != "Cover")
                state.Intent.Clear();
            // A selected-but-rejected request has no active label yet. It still needs an explicit release.
            if (!applied && state.Decision.Length > 0 && state.ActiveLabel.Length == 0)
            {
                plugin.Emit(
                    state,
                    "rejected",
                    "sain-request-not-applied:"
                        + __instance.CurrentCombatDecision
                        + "/"
                        + __instance.CurrentSelfDecision
                        + "/"
                        + __instance.CurrentSquadDecision
                );
                plugin.YieldToSain(state, "sain-request-not-applied", "priority");
            }
            if (label != state.ActiveLabel)
            {
                if (applied)
                {
                    state.ActiveLabel = label;
                    Api.Decisions.Set(state.ProfileId, label);
                    if (state.Feature == "A03")
                    {
                        __instance.Bot.Shoot.EndShoot();
                        __instance.Bot.ManualShoot.Reset();
                        __instance.Bot.Aim.LoseAimTarget();
                    }
                    plugin.Emit(state, "accepted", "sain-decision-accepted");
                }
                else
                    plugin.YieldToSain(
                        state,
                        __instance.CurrentCombatDecision == ECombatDecision.None
                        && __instance.CurrentSelfDecision == ESelfActionType.None
                        && __instance.CurrentSquadDecision == ESquadDecision.None
                            ? "native-decision-cleared"
                            : "sain-priority:"
                                + __instance.CurrentCombatDecision
                                + "/"
                                + __instance.CurrentSelfDecision
                                + "/"
                                + __instance.CurrentSquadDecision,
                        "priority"
                    );
            }
            if (
                !applied
                && state.Handoff.Yielded
                && NativeAdapter.IsSupportedAi(__instance.Bot)
                && __instance.Bot.BotActive
                && plugin.Authority
                && !plugin.Ghost(state.ProfileId)
                && state.Handoff.ObserveNative(
                    Time.time,
                    __instance.CurrentCombatDecision.ToString(),
                    __instance.CurrentSelfDecision.ToString(),
                    __instance.CurrentSquadDecision.ToString()
                )
            )
                plugin.Emit(state, "evaluated", "handoff-native-observed", "A13");
        }
        catch (Exception ex)
        {
            plugin.Logger.LogWarning("COMET decision observer: " + ex.Message);
        }
    }

    private static bool FindCover(SAINCoverClass __instance, ref CoverPoint __result)
    {
        var plugin = Instance;
        if (!ContactSafety.AllowsGoal(__instance.Bot))
            return true;
        if (
            plugin != null
            && plugin.states.TryGetValue(__instance.Bot, out var relocation)
            && plugin.FindRelocationCover(__instance, relocation, ref __result)
        )
            return false;
        // A01/A02 select a tactical intent, not a second cover execution layer.
        // Native FindCoverPoint owns picking and moving, including an empty or
        // stale cache. The old prefix canceled the plan before that search ran.
        return true;
    }

    private static void ObserveNativeCover(SAINCoverClass __instance, CoverPoint __result)
    {
        var p = Instance;
        if (
            p == null
            || !p.states.TryGetValue(__instance.Bot, out var s)
            || !(
                s.Feature == "A01" && s.ActiveLabel == "COMET:FallBack"
                || s.Feature == "A02" && s.ActiveLabel == "COMET:Cover"
            )
        )
            return;
        bool changed = !ReferenceEquals(s.SelectedCover, __result);
        s.SelectedCover = __result;
        if (changed)
            p.Emit(
                s,
                "observed",
                __result == null ? "native-cover-search-pending" : "native-cover-selected"
            );
    }

    private static void ContinueCover(BotDecisionManager __instance, ref bool __result)
    {
        var plugin = Instance;
        if (plugin?.config == null || !plugin.states.TryGetValue(__instance.Bot, out var state))
            return;
        var bot = __instance.Bot;
        if (!plugin.TacticalScope(bot, bot.GoalEnemy))
            return;
        // A source custom movement layer owns its own route. Native cover continuation
        // cannot bypass that layer; otherwise preserve SAIN's result verbatim.
        if (
            state.CoverFeint.Policy.Active
            || state.Relocation.Policy.Active
            || state.Emergency.Policy.Active
        )
        {
            __result = false;
            return;
        }
        if (!__result)
            return;
        if (!plugin.SourceContinueCover(bot, state))
        {
            __result = false;
            return;
        }
        bool hidden = state.Feature == "A01" && state.ActiveLabel == "COMET:FallBack";
        if (!hidden && !(state.Feature == "A02" && state.ActiveLabel == "COMET:Cover"))
            return;
        // Observability only: no second eligibility, exposure, snapshot-change,
        // progress or periodic-review veto over an accepted native continuation.
        state.SelectedCover = bot.Cover.CoverPoint_MovingTo;
        if (hidden && state.Expected != ECombatDecision.SeekCover)
            plugin.ArbitrationEvent(state, "fallback-cover-continuation", "SeekCover");
        state.Expected = ECombatDecision.SeekCover;
        state.RequestedFrame = Time.frameCount;
    }

    private readonly DecisionConfiguration decisionConfiguration = new();

    private void Emit(BotState state, string stage, string reason, string? featureOverride = null)
    {
        var role = (featureOverride ?? state.Feature) switch
        {
            "A01" => state.Engine.RankedEnemy,
            "A02" => state.VisibleEngine.RankedEnemy,
            _ => null,
        };
        string json = Diagnostics.Serialize(
            new
            {
                schema = 1,
                session,
                sequence = ++sequence,
                feature = featureOverride ?? state.Feature,
                stage,
                reason,
                handoff = featureOverride == "A13"
                    ? state.ContinuityTrace ?? state.Handoff.Current
                    : null,
                execution = state.Execution.Active || state.Execution.Phase == "timed-out"
                    ? state.Execution
                    : null,
                arbitration = featureOverride == "A13" && state.ArbitrationReason == reason
                    ? new
                    {
                        reason = state.ArbitrationReason,
                        native = state.ArbitrationNative,
                        expected = state.Expected.ToString(),
                        decision = state.Decision,
                    }
                    : null,
                realtime = Time.realtimeSinceStartup,
                utc = DateTime.UtcNow.ToString("O"),
                configRevision = config?.Revision ?? 0,
                cometVersion = CometVersion.Release,
                config = decisionConfiguration.Take(config, session),
                profileId = state.ProfileId,
                name = state.Name,
                botInstance = state.Id,
                botKind = state.BotKind,
                decision = state.Decision,
                candidates = state.Candidates,
                hiddenHold = (featureOverride ?? state.Feature) == "A01" && state.Engine.RankedHoldSeconds is double held
                    ? new { secondsAtRank = held, cappedAtRank = state.Engine.RankedHoldCapped }
                    : null,
                mixing = state.Mixing,
                tacticObservationVersion = 1,
                squadGrenade = state.Feature == "S10" && state.SquadGrenade is { } pair
                    ? new
                    {
                        pair.Thrower,
                        pair.Holder,
                        pair.Contact,
                        pair.StartFrame,
                    }
                    : null,
                siege = state.Feature == "S11" && state.Siege is { } watch
                    ? new
                    {
                        target = watch.Contact,
                        exit = state.SiegeExit?.Id,
                        exits = watch.Watches.Count,
                        until = watch.Until,
                    }
                    : null,
                flank = state.Decision == "Flank"
                    ? new
                    {
                        accepted = state.Flank.MoveAccepted,
                        reason = state.Flank.Reason,
                        destination = state.Flank.Point == null
                            ? null
                            : new
                            {
                                x = state.Flank.Point.Position.x,
                                y = state.Flank.Point.Position.y,
                                z = state.Flank.Point.Position.z,
                            },
                    }
                    : null,
                doorEmergency = state.Feature == "D13" ? state.Emergency.Policy.Trace : null,
                hearing = state.Situation?.Target == state.MovementSource
                && Time.time - state.MovementHeardAt <= 5
                    ? state.MovementEvidence
                    : null,
                quiet = state.Quiet.Current.Applied ? state.Quiet.Current : null,
                silentSearch = state.Feature == "A01" ? state.SilentTrace : null,
                encounter = state.Feature == "A01" ? state.Situation?.Encounter : null,
                outcome = state.Feature is "A01" or "A02" ? state.Situation?.Outcome : null,
                opponent = state.Feature == "A03" ? null : state.OpponentAtSelection,
                enemyRole = role == null
                    ? null
                    : new
                    {
                        role = role.Target.Role.ToString(),
                        faction = role.Target.Faction.ToString(),
                        membership = role.Target.RegisteredFaction,
                        groupCue = role.GroupCue.Role.ToString(),
                        groupFaction = role.GroupCue.Faction.ToString(),
                        groupMembership = role.GroupCue.RegisteredFaction,
                        role.RaidProgress,
                        role.ExpectedSupport,
                        role.OtherContacts,
                        role.NearbyAllies,
                    },
                opportunity = state.Feature == "A03" && state.Decision != "ContinuePush"
                    ? null
                    : state.OpportunityAtSelection,
                coverResponse = state.Feature == "A02" ? state.CoverResponseAtSelection : null,
                progress = config?.Progress.Enabled == true && !state.ProgressUnavailable
                    ? state.ProgressAtSelection ?? state.Progress.Current
                    : null,
                loadout = state.Feature == "A03" ? null : state.LoadoutAtSelection,
                fear = reason == "panic-reassessment"
                    ? state.Fear.Current
                    : state.FearAtSelection ?? state.Fear.Current,
                continuity = state.Feature == "A03" && state.SightEvent != null
                    ? new
                    {
                        episode = state.SightEpisode,
                        target = state.SightEvent.Target,
                        previousAction = state.SightEvent.Action,
                        remaining = Math.Max(0, state.SightEvent.Until - Time.time),
                        windowEnd = state.SightEvent.Until,
                        lostFor = Math.Max(0, Time.time - state.SightEvent.SeenAt),
                        x = state.SightEvent.X,
                        y = state.SightEvent.Y,
                        z = state.SightEvent.Z,
                        aimHeight = state.SightAimHeight,
                        frozenRoute = state.SightEvent.Action == "Push",
                        fireAuthorized = false,
                    }
                    : null,
                visible = state.Feature == "A02"
                    ? new
                    {
                        enemyDistance = state.VisibleSituation?.EnemyDistance,
                        clearShot = state.VisibleSituation?.CanShoot,
                        effectiveWeaponDistance = state.VisibleSituation?.EffectiveWeaponDistance,
                        currentExposure = state.VisibleSituation?.CurrentExposure,
                        coverExposure = state.VisibleSituation?.CoverExposure,
                        coverRouteRisk = state.VisibleSituation?.CoverRouteRisk,
                        pushRouteRisk = state.VisibleSituation?.PushRouteRisk,
                        pushPathLength = state.VisibleSituation?.PushPathLength,
                        knownThreats = state.VisibleSituation?.ThreatCount,
                        threatsTruncated = state.VisibleSituation?.ThreatsTruncated,
                        coverId = state.VisibleSituation?.CoverId,
                        probeAge = state.Probe == null ? (float?)null : Time.time - state.Probe.At,
                        probeMilliseconds = state.Probe?.Milliseconds,
                        probeRays = state.Probe?.Rays,
                        probeReady = state.VisibleSituation?.ProbeReady,
                        pendingReason = state.VisibleSituation?.ProbePendingReason,
                        continuationReason = state.VisibleSituation?.ContinuationReason,
                        coverPosition = state.SelectedCover == null
                            ? null
                            : new
                            {
                                x = state.SelectedCover.Position.x,
                                y = state.SelectedCover.Position.y,
                                z = state.SelectedCover.Position.z,
                            },
                    }
                    : null,
                contact = new
                {
                    target = state.Situation?.Target,
                    age = state.Feature == "A03" && state.SightEvent != null
                        ? Math.Max(0, Time.time - state.SightEvent.SeenAt)
                        : state.Situation?.ContactAge,
                    source = state.Situation?.ContactSource,
                    distance = state.Situation?.KnownDistance,
                    x = state.Situation?.KnownX,
                    y = state.Situation?.KnownY,
                    z = state.Situation?.KnownZ,
                },
                personality = state.Situation?.Personality,
            }
        );
        if (stage == "selected")
            state.LastSelection = Time.time;
        diagnostics?.Write(json);
        Api.Decisions.Publish(json, ex =>
        {
            if (!subscriberWarned)
            {
                subscriberWarned = true;
                Logger.LogWarning("COMET telemetry subscriber failed: " + ex.Message);
            }
        });
    }

    private void Release(
        BotState state,
        string reason,
        bool clearIntent = true,
        bool preserveVisiblePlan = false,
        bool suspendPlan = false,
        bool preserveHiddenPreference = false
    )
    {
        if (state.Siege != null && state.Feature == "S11" && state.Decision.Length > 0)
            EndSiege(state.Siege, reason);
        if (
            reason.Contains("ghost")
            || reason.Contains("inactive")
            || reason.Contains("authority")
            || reason.Contains("ended")
            || reason.Contains("error")
            || reason.Contains("exception")
        )
        {
            state.HiddenExecution.Movement.Pause();
            state.HiddenExecution.Pick = null;
            state.HiddenExecution.PickEnemy = null;
            state.HiddenExecution.LastReason = "";
        }
        if (MagazineRecovery<object>.ShouldPause(reason))
            PauseMagazineRecovery(state, reason, MagazineRecovery<object>.ShouldClear(reason));
        if (!suspendPlan)
            state.PlanService.Clear();
        if (state.ActiveLabel.Length > 0 && !suspendPlan)
            ObserveControlRelease(state, reason);
        if (state.Door.SessionPlan.Length > 0)
            CloseDoorSession(state, DoorSessionReleaseReason(state, reason));
        EndDoorSpatial(state, reason);
        EndDoorRecovery(state, reason);
        EndDoorTransit(state, reason);
        if (
            reason.Contains("ghost")
            || reason.Contains("inactive")
            || reason.Contains("authority")
            || reason.Contains("ended")
            || reason.Contains("error")
            || reason.Contains("exception")
        )
        {
            EndEncounter(state, reason);
            EndOutcome(state, reason);
        }
        try
        {
            if (
                reason.Contains("ghost")
                || reason.Contains("inactive")
                || reason.Contains("authority")
                || reason.Contains("ended")
                || reason.Contains("error")
            )
            {
                EndAngle(state, reason);
                state.AngleWatch.Enemy = null;
                state.AngleWatch.SeenAt = -1000;
                EndCombatStep(state, reason);
                EndCornerPeek(state, reason);
                EndCombatLean(state, reason);
                EndChase(state, reason);
                state.CornerChase.Memory.Clear();
            }
            EndRelocation(state, reason, reason.Contains("bot-ended") || reason.Contains("raid-ended"));
            if (
                reason.Contains("ghost")
                || reason.Contains("inactive")
                || reason.Contains("authority")
                || reason.Contains("bot-ended")
                || reason.Contains("raid-ended")
            )
                EndReloadTiming(state, reason);
            if (
                reason.Contains("ghost")
                || reason.Contains("inactive")
                || reason.Contains("authority")
                || reason.Contains("bot-ended")
                || reason.Contains("raid-ended")
            )
            {
                EndPistolSwap(state, reason);
                EndWeaponReturn(state, reason);
                EndQuickReload(state, reason);
                EndWeaponRange(state, reason);
                EndWeaponSync(state, reason);
                EndDiscipline(state, reason);
            }
            if (
                reason.Contains("ghost")
                || reason.Contains("inactive")
                || reason.Contains("authority")
                || reason.Contains("bot-ended")
                || reason.Contains("raid-ended")
            )
            {
                EndGrenadeJudgment(state, reason);
                try
                {
                    EndHandsRecovery(state, reason);
                }
                catch (Exception ex)
                {
                    HandsRecoveryError(state, ex);
                }
                try
                {
                    EndRecovery(state, reason);
                }
                catch (Exception ex)
                {
                    RecoveryError(state, ex);
                }
                try
                {
                    StopGeometryOwned(state);
                    ReleaseGeometry(state, reason);
                }
                catch (Exception ex)
                {
                    GeometryError(state, ex);
                }
            }
            EndFeint(
                state,
                reason,
                reason.Contains("ghost")
                    || reason.Contains("inactive")
                    || reason.Contains("authority")
                    || reason.Contains("bot-ended")
                    || reason.Contains("raid-ended")
                    || reason.Contains("error")
            );
            EndHoldAngle(state, reason);
            EndCoverTravel(state, reason);
            if (
                reason.Contains("ghost")
                || reason.Contains("inactive")
                || reason.Contains("authority")
                || reason.Contains("bot-ended")
                || reason.Contains("raid-ended")
                || reason.Contains("error")
                || reason.Contains("exception")
            )
            {
                EndAdsTravel(state, reason);
                EndWeave(state, reason);
                EndLane(state, reason);
                EndSquadFire(state, reason);
                EndSquadVoice(state, reason);
                EndLightDiscipline(state, reason);
                EndTacticalDevices(state, reason);
                EndRestLights(state, reason);
                EndTacticalJump(state, reason);
            }
            EndDoorEntry(state, reason);
            ReleaseDoorReservation(state, reason);
            ReleaseDoorEmergency(state, reason);
            ReleaseDoorInterruption(state, reason);
            EndDoorSupport(state, reason);
            EndSilentSearch(state, reason);
            EndDoorAmbush(state, reason);
            EndDoorClear(state, reason);
            EndDoorFake(state, reason);
            EndDoorStep(state, reason);
            EndDoorRun(state, reason);
            EndDoorJump(state, reason);
            EndDoor(state, reason);
            if (
                reason.Contains("ghost")
                || reason.Contains("inactive")
                || reason.Contains("authority")
                || reason.Contains("bot-ended")
                || reason.Contains("raid-ended")
                || reason.Contains("error")
            )
                EndAbandonment(state, reason, true);
            else if (reason == "contact-ended" || reason == "enemy-visible")
                EndAbandonment(state, reason);
            if (
                state.Engine.Current.HasValue
                || state.VisibleEngine.Current.HasValue
                || state.ActiveLabel.Length > 0
                || (state.Feature == "A03" && state.Decision.Length > 0)
            )
                Emit(state, "released", reason);
        }
        finally
        {
            if (!suspendPlan)
            {
                if (preserveHiddenPreference)
                    state.Engine.SuspendSelection();
                else
                    state.Engine.Release(reason);
            }
            state.ActiveLabel = "";
            state.RequestedFrame = -1;
            state.Execution.Clear();
            if (preserveVisiblePlan || suspendPlan)
            {
                state.VisibleEngine.SuspendForSight();
                state.Mixing = state.VisibleEngine.Mixing;
            }
            else
            {
                state.VisibleEngine.Release(reason);
                state.Mixing = null;
            }
            state.Decision = "";
            state.SightEvent = null;
            if (
                reason.IndexOf("ghost", StringComparison.Ordinal) >= 0
                || reason.IndexOf("inactive", StringComparison.Ordinal) >= 0
                || reason.IndexOf("authority", StringComparison.Ordinal) >= 0
                || reason.IndexOf("ended", StringComparison.Ordinal) >= 0
                || reason.IndexOf("exception", StringComparison.Ordinal) >= 0
                || reason.IndexOf("error", StringComparison.Ordinal) >= 0
            )
            {
                state.Crossfire.Storm.Observations.Clear();
                state.Crossfire.Storm.Sounds.Clear();
                state.Crossfire.Storm.DangerAt = -100;
                state.Opponent.Clear();
                state.OpponentAtSelection = null;
                state.Opportunity.Clear();
                state.OpportunityAtSelection = null;
                state.OpportunityLogKey = "";
                state.MovementEvidence = null;
                state.MovementSource = "";
                state.MovementHeardAt = float.NegativeInfinity;
                state.Environment.Clear();
            }
            if (clearIntent)
            {
                state.Intent.Clear();
                state.Probe = null;
                state.SelectedCover = null;
                state.Sight.Reset(reason);
                state.SightPath = null;
            }
            Api.Decisions.Set(state.ProfileId, "");
        }
    }

    private void OnDestroy()
    {
        try
        {
            diagnosticStorage?.Dispose();
            EndRaid("plugin-destroyed");
        }
        finally
        {
            try
            {
                embeddedFika.Dispose();
                harmony?.UnpatchSelf();
            }
            finally
            {
                if (Instance == this)
                    Instance = null;
            }
        }
    }
}

internal sealed class BotState
{
    internal SquadGrenadePlan? SquadGrenade;
    internal SiegeSquad? Siege;
    internal SiegeExit? SiegeExit;
    internal readonly EscortSighting EscortSighting = new();
    public float CoverRunAnchorAt = -1f,
        CoverRunBreakCountedAt;
    public Vector3 CoverRunAnchor;

    private static int maintenanceOrdinal;
    internal readonly PeriodicWorkSlot Maintenance = new(
        System.Threading.Interlocked.Increment(ref maintenanceOrdinal)
    );
    internal readonly FlankRuntime Flank = new();
    internal readonly PlanServiceRuntime PlanService = new();
    internal HandoffTrace? ContinuityTrace;
    internal string ContinuityKey = "";
    internal float ContinuityAt = -100;
    internal string RejectedContact = "";
    internal string CounterFeature = "";
    internal int CounterEncounter,
        CounterOpportunity;
    internal bool CounterObserverFailed;
    internal readonly CombatJournalRuntime Journal = new();
    internal bool Retired;
    internal readonly BotComponent Owner;
    internal readonly MagazineRecoveryRuntime MagazineRecovery = new();
    internal readonly MovementRecoveryRuntime MovementRecovery = new();
    internal readonly EncounterRuntime Encounter = new();
    internal readonly DoorTransitRuntime Transit = new();
    internal readonly DoorRecoveryRuntime Recovery = new();
    internal DoorSpatialTrace? SpatialTrace;
    internal string SpatialKey = "";
    internal float SpatialAt = -1000;
    internal readonly OutcomeRuntime Outcomes = new();
    internal readonly GasRuntime Gas = new();
    internal readonly FlashRuntime Flash = new();
    internal readonly MedicalPostureRuntime MedicalPosture = new();
    internal readonly CrossfireRuntime Crossfire = new();
    internal readonly PostCombatRuntime PostCombat = new();
    internal readonly ReadyStanceRuntime ReadyStance = new();
    internal readonly MedicalRuntime Medical = new();
    internal readonly BlastRuntime Blast = new();
    internal readonly GrenadeEscapeRuntime GrenadeEscape = new();
    internal readonly OwnGrenadeRuntime OwnGrenade = new();
    internal readonly GrenadeChoiceRuntime GrenadeChoice = new();
    internal readonly GrenadeFilterRuntime GrenadeFilter = new();
    internal readonly GrenadeTrajectoryRuntime GrenadeTrajectory = new();
    internal readonly GrenadeJudgmentRuntime GrenadeJudgment = new();
    internal readonly CombatStepRuntime CombatStep = new();
    internal readonly CornerPeekRuntime CornerPeek = new();
    internal readonly CombatLeanRuntime CombatLean = new();
    internal readonly AngleWatchRuntime AngleWatch = new();
    internal readonly CoverTravelRuntime CoverTravel = new();
    internal readonly AdsTravelRuntime AdsTravel = new();
    internal readonly RetreatWeaveRuntime RetreatWeave = new();
    internal readonly RestLightsRuntime RestLights = new();
    internal readonly TacticalDevicesRuntime TacticalDevices = new();
    internal readonly LightDisciplineRuntime LightDiscipline = new();
    internal readonly SquadVoiceRuntime SquadVoice = new();
    internal readonly SquadFireRuntime SquadFire = new();
    internal readonly FireLaneRuntime FireLane = new();
    internal readonly TacticalJumpRuntime TacticalJump = new();
    internal readonly ReloadTimingRuntime ReloadTiming = new();
    internal readonly PistolSwapRuntime PistolSwap = new();
    internal readonly WeaponSyncRuntime WeaponSync = new();
    internal readonly SuppressionDisciplineRuntime SuppressionDiscipline = new();
    internal readonly HandsRecoveryRuntime HandsRecovery = new();
    internal readonly RecoilRecoveryRuntime RecoilRecovery = new();
    internal readonly ShotGeometryRuntime ShotGeometry = new();
    internal readonly WeaponRangeRuntime WeaponRange = new();
    internal readonly QuickReloadRuntime QuickReload = new();
    internal readonly WeaponReturnRuntime WeaponReturn = new();
    internal readonly CoverFeintRuntime CoverFeint = new();
    internal readonly SeasonalCoverRuntime SeasonalCover = new();
    internal readonly CoverExposureRuntime CoverExposure = new();
    internal readonly RelocationRuntime Relocation = new();
    internal readonly HoldAngleRuntime HoldAngle = new();
    internal readonly CornerPrefireRuntime CornerPrefire = new();
    internal readonly CornerChaseRuntime CornerChase = new();
    internal readonly DoorEntryRuntime Entry = new();
    internal readonly DoorReservationRuntime Reservation = new();
    internal readonly DoorEmergencyRuntime Emergency = new();
    internal readonly DoorInterruptionRuntime Interruption = new();
    internal DoorSupportRuntime? Support;
    internal readonly DoorAmbushRuntime Ambush = new();
    internal readonly DoorClearRuntime RoomClear = new();
    internal readonly DoorFakeRuntime Fake = new();
    internal readonly DoorStepRuntime StepPeek = new();
    internal readonly DoorRunRuntime RunBy = new();
    internal readonly DoorJumpRuntime Jump = new();
    internal readonly DoorRuntime Door = new();
    internal readonly Comet.Core.EnvironmentContext Environment = new();
    internal bool EnvironmentFault;
    internal float EnvironmentLogAt = -1000;
    internal string EnvironmentLogKey = "";
    internal readonly SearchAbandonment Abandonment = new();
    internal SearchAbandonmentFrame? AbandonPending;
    internal float AbandonProbeAt;
    internal bool AbandonFault;
    internal readonly ContactDiagnosticGate AbandonLogs = new();
    internal SilentSearchTrace? SilentTrace;
    internal float SilentLogAt = -1000;
    internal bool SilentAccepted,
        SilentObserved,
        SilentMoved,
        SilentWarned,
        SilentFault;
    internal Vector3 SilentStart;
    internal readonly ImpactRuntime Impact;
    internal readonly CloseDetectionEvidence CloseEvidence = new();
    internal float CloseNextLog,
        CloseNextAcquired;
    internal int CloseCoalesced;
    internal float FloorNextLog;
    internal int FloorCoalesced;
    internal readonly GunfireReaction Gunfire;
    internal bool GunfireFault,
        GunfireWarned;
    internal float GunfireAppliedAt;
    internal readonly GunfireLogThrottle GunfireLog = new();
    internal readonly QuietApproach Quiet;
    internal readonly OpportunityMemory QuietLoot = new OpportunityMemory();
    internal bool QuietOwnSpeed,
        QuietOwnPose,
        QuietRestoring,
        QuietFault,
        QuietWarned;
    internal float QuietNativeSpeed = 1,
        QuietNativePose = 1,
        NextQuietSample,
        QuietLogAt = -1000;
    internal string QuietLogKey = "";
    internal int QuietTargetInstance;
    internal object? MovementEvidence;
    internal string MovementSource = "";
    internal float MovementHeardAt = float.NegativeInfinity;
    internal readonly string BotKind,
        ProfileId,
        Name,
        Id = Guid.NewGuid().ToString("N");
    internal readonly HiddenEnemyDecision Engine;
    internal readonly HiddenExecutionRuntime HiddenExecution = new();
    internal readonly VisibleEnemyDecision VisibleEngine;
    internal readonly CoverIntent Intent = new CoverIntent();
    internal readonly SightContinuity Sight;
    internal readonly FearModel Fear;
    internal readonly ThreatRuntime Threat;
    internal FearTrace? FearAtSelection;
    internal readonly LoadoutRuntime Loadout = new LoadoutRuntime();
    internal LoadoutTrace? LoadoutAtSelection;
    internal bool LoadoutWarned;
    internal readonly OpponentMemory Opponent = new OpponentMemory();
    internal OpponentTrace? OpponentAtSelection;
    internal float NextOpponentSample;
    internal bool OpponentWarned;
    internal readonly OpportunityMemory Opportunity = new OpportunityMemory();
    internal OpportunityTrace? OpportunityAtSelection;
    internal string OpportunityLogKey = "";
    internal bool OpportunityWarned;
    internal string CoverResponseLogKey = "";
    internal CoverResponseTrace? CoverResponseAtSelection;
    internal float CoverResponseLogAt = -1000;
    internal readonly PlanProgress Progress;
    internal ProgressTrace? ProgressAtSelection;
    internal float NextProgressSample,
        ProgressLogAt = -1000;
    internal string ProgressLogKey = "";
    internal bool ProgressWarned,
        ProgressUnavailable;
    internal float NextFearSample;
    internal readonly SightFrame SightFrame = new SightFrame();
    internal int SightEpisode;
    internal SightHold? SightEvent;
    internal SAIN.SAINComponent.Classes.Mover.IBotPathData? SightPath;
    internal Vector3 SightDestination;
    internal float SightAimHeight;
    internal string Feature = "A01",
        Decision = "";
    internal List<Candidate> Candidates = new List<Candidate>();
    internal VisibleSituation? VisibleSituation;
    internal ExposureProbe? Probe;
    internal CoverPoint? SelectedCover;
    internal float NextExposureAttempt,
        LastSelection;
    internal string LastRefusal = "";
    internal float LastRefusalAt;
    internal Situation? Situation;
    internal Choice? Choice;
    internal MixTrace? Mixing;
    internal string ActiveLabel = "";
    internal ECombatDecision Expected;
    internal int RequestedFrame = -1;
    internal readonly DecisionExecution Execution = new();
    internal bool ArbitratingSquad;
    internal float ExecutionSampleAt,
        ArbitrationAt = -100;
    internal string ArbitrationKey = "",
        ArbitrationReason = "",
        ArbitrationNative = "";
    internal float FaultUntil,
        NextGrenadeProbe;
    internal readonly FrameProbeGate GrenadeProbeGate = new();
    internal readonly SainHandoff Handoff;
    internal bool GrenadeReady;
    internal Vector3 GrenadeTarget;

    internal BotState(BotComponent bot, CometConfig config, string session, string? identity = null)
    {
        Owner = bot;
        BotKind = BotEligibility.Kind(bot.Player);
        if (identity != null)
            Id = identity;
        ProfileId = bot.ProfileId;
        Name = bot.Player.Profile.Nickname;
        Handoff = new SainHandoff(config.Handoff);
        Impact = new ImpactRuntime(config.ImpactReaction);
        Gunfire = new GunfireReaction(config.Gunfire);
        uint quietSeed = 2166136261;
        foreach (char ch in session + ":" + ProfileId + ":P02")
            quietSeed = unchecked((quietSeed ^ ch) * 16777619);
        if (quietSeed == 0)
            quietSeed = 1;
        Quiet = new QuietApproach(config.QuietApproach, () =>
        {
            quietSeed ^= quietSeed << 13;
            quietSeed ^= quietSeed >> 17;
            quietSeed ^= quietSeed << 5;
            return quietSeed / 4294967296.0;
        });
        Fear = new FearModel(config.Fear);
        Threat = new ThreatRuntime(config);
        Progress = new PlanProgress(config.Progress);
        var mistakes = new TacticalMistakes(config.TacticalMistakes, session + ":" + ProfileId);
        Engine = new HiddenEnemyDecision(config, session + ":" + ProfileId, mistakes);
        VisibleEngine = new VisibleEnemyDecision(config, session + ":" + ProfileId, mistakes);
        Sight = new SightContinuity(config);
    }
}
