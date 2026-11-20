using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Audio.AmbientSubsystem;
using Audio.AudioWeatherSystem;
using Comet.Core;
using CommonAssets.Scripts.Audio;
using EFT;
using EFT.EnvironmentEffect;
using HarmonyLib;
using Newtonsoft.Json;
using SAIN;
using SAIN.Classes.Bot.Sense.Hearing;
using SAIN.Components;
using SAIN.Components.BotController;
using SAIN.Components.PlayerComponentSpace;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.EnemyClasses;
using UnityEngine;

namespace Comet.Client;

public sealed partial class Plugin
{
    private const int MovementQueueLimit = 128,
        MovementRecipientsLimit = 256,
        MovementPairsPerFrame = 128;
    private readonly MovementHearingGate movementSources = new MovementHearingGate(),
        movementListeners = new MovementHearingGate();
    private readonly Queue<MovementSignal> movementQueue = new Queue<MovementSignal>();
    private readonly Dictionary<(BotComponent, SoundEvent), MovementDelivery> movementPending =
        new Dictionary<(BotComponent, SoundEvent), MovementDelivery>();
    private readonly Dictionary<string, int> movementCounts = new Dictionary<string, int>();
    private readonly Dictionary<string, (float At, int Suppressed)> movementLog =
        new Dictionary<string, (float, int)>();
    private MovementDelivery? movementInjection,
        movementReaction;
    private long movementSequence;
    private float nextMovementCleanup,
        nextMovementSummary;
    private bool movementWarned;
    private float movementFaultUntil;

    private sealed class MovementSignal
    {
        public long Id;
        public SoundEvent Sound;
        public PlayerComponent[]? Recipients;
        public int Cursor;
    }

    private sealed class MovementDelivery
    {
        public MovementSignal Signal = null!;
        public BotComponent Bot = null!;
        public float Distance;
        public bool Heard;
        public Vector3? Perceived;
        public float? AnalysisDistance, NativeRange, FinalRange, Environment, Occlusion;
    }

    private sealed class MovementCacheState
    {
        public int Count;
        public string Key = "";
        public bool Added;
    }

    private bool MovementEnabled =>
        config?.Enabled == true
        && config.MovementHearing.Enabled
        && world != null
        && Time.time >= movementFaultUntil
        && Authority;

    private bool MovementListener(BotComponent bot) =>
        bot != null
        && MovementHearingScope.Listener(
            Authority,
            NativeAdapter.IsSupportedAi(bot),
            bot.BotActive && !bot.IsDead,
            bot.GameEnding,
            Ghost(bot.ProfileId)
        );

    private bool MovementSource(PlayerComponent source) =>
        source != null
        && MovementHearingScope.Source(source.IsAI, source.IsActive, Ghost(source.ProfileId));

    private static string MovementFamily(SAINSoundType type) =>
        type switch
        {
            SAINSoundType.FootStep => "step",
            SAINSoundType.Sprint => "step",
            SAINSoundType.Jump => "jump",
            SAINSoundType.Land => "land",
            _ => "",
        };

    private bool MovementTypeEnabled(SAINSoundType type) =>
        MovementFamily(type) switch
        {
            "step" => config!.MovementHearing.Footsteps,
            "jump" or "land" => config!.MovementHearing.JumpsAndLandings,
            _ => false,
        };

    // Runtime object identity prevents a reused profile ID inheriting another actor's deduplication history.
    private static string MovementIdentity(PlayerComponent source) =>
        source.ProfileId + ":" + RuntimeHelpers.GetHashCode(source);

    private static string MovementKey(BotComponent bot, SoundEvent sound) =>
        bot.ProfileId
        + ":"
        + RuntimeHelpers.GetHashCode(bot)
        + ":"
        + MovementIdentity(sound.PlayerComponent)
        + ":"
        + MovementFamily(sound.SoundType);

    private void CountMovement(string reason)
    {
        movementCounts.TryGetValue(reason, out var n);
        movementCounts[reason] = n + 1;
    }

    private static void PatchMovementHearing(Harmony h)
    {
        // Observe the exact volume at the common audio-layer call AFTER the base sound bank played.
        // This also works with EFT's no-op seasonal layer and captures genuine landings, not airborne guesses.
        foreach (var name in new[] { nameof(Player.DefaultPlay), nameof(Player.PlayStepSound) })
            h.Patch(
                AccessTools.Method(typeof(Player), name),
                transpiler: new HarmonyMethod(typeof(Plugin), nameof(MovementAudioTranspiler))
            );
        var sprint = AccessTools.EnumeratorMoveNext(
            AccessTools.Method(typeof(Player), nameof(Player.Sprint))
        );
        if (sprint == null)
            throw new MissingMethodException("P01 sprint audio iterator");
        h.Patch(
            sprint,
            transpiler: new HarmonyMethod(typeof(Plugin), nameof(MovementAudioTranspiler))
        );
        h.Patch(
            AccessTools.Method(typeof(HearingInput), nameof(HearingInput.CheckAddSoundToCache)),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(MovementCachePrefix)),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(MovementCachePostfix))
        );
        h.Patch(
            AccessTools.Method(typeof(HearingSensor), nameof(HearingSensor.ReactToHeardSound)),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(MovementReactPrefix)),
            finalizer: new HarmonyMethod(typeof(Plugin), nameof(MovementReactFinally))
        );
        h.Patch(
            AccessTools.Method(typeof(EnemyEvents), nameof(EnemyEvents.EnemyHeard)),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(MovementHeard))
        );
        // Native squad sharing remains native, but cannot give a P01 contact to a non-PMC or a ghost.
        h.Patch(
            AccessTools.Method(typeof(Enemy), nameof(Enemy.EnemyPositionReported)),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(MovementSharedContact))
        );
    }

    private static IEnumerable<CodeInstruction> MovementAudioTranspiler(
        IEnumerable<CodeInstruction> instructions,
        MethodBase __originalMethod
    )
    {
        var call = AccessTools.Method(typeof(IPlayerStepAudioController), "Play");
        var wrapper = AccessTools.Method(typeof(Plugin), nameof(MovementAudioPlayed));
        var owner =
            __originalMethod.DeclaringType == typeof(Player)
                ? null
                : __originalMethod
                    .DeclaringType!.GetFields(
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                    )
                    .Single(f => f.FieldType == typeof(Player));
        int matches = 0;
        foreach (var instruction in instructions)
        {
            if (!instruction.Calls(call))
            {
                yield return instruction;
                continue;
            }
            matches++;
            instruction.opcode = OpCodes.Ldarg_0;
            instruction.operand = null; // Preserve branch labels / exception boundaries.
            yield return instruction;
            if (owner != null)
                yield return new CodeInstruction(OpCodes.Ldfld, owner);
            yield return new CodeInstruction(OpCodes.Call, wrapper);
        }
        if (matches != 1)
            throw new InvalidOperationException(
                "P01 audio hook changed: " + __originalMethod + ", matches=" + matches
            );
    }

    private static void MovementAudioPlayed(
        IPlayerStepAudioController controller,
        EAudioMovementState movement,
        EnvironmentType environment,
        float distance,
        float volume,
        float speed,
        bool firstPerson,
        Player player
    )
    {
        controller.Play(movement, environment, distance, volume, speed, firstPerson);
        var p = Instance;
        if (p == null)
            return;
        try
        {
            p.CaptureMovement(player, movement, volume);
        }
        catch (Exception ex)
        {
            p.MovementError(ex);
        } // Never break EFT audio if diagnostics fail.
    }

    private void CaptureMovement(Player player, EAudioMovementState movement, float volume)
    {
        if (!MovementEnabled || !player.IsAI)
            return;
        var type = movement switch
        {
            EAudioMovementState.Run or EAudioMovementState.Duck => SAINSoundType.FootStep,
            EAudioMovementState.Sprint => SAINSoundType.Sprint,
            EAudioMovementState.Jump => SAINSoundType.Jump,
            EAudioMovementState.Land => SAINSoundType.Land,
            _ => SAINSoundType.None,
        };
        if (!MovementTypeEnabled(type))
            return;
        CountMovement("emitted");
        if (
            !GameWorldComponent.TryGetPlayerComponent(player, out var source)
            || !MovementSource(source)
            || !source.AIData.AISoundPlayer.ShallPlayAISound()
        )
        {
            CountMovement("source-ineligible");
            return;
        }
        string key = MovementIdentity(source) + ":" + MovementFamily(type);
        if (
            !movementSources.Allows(
                key,
                Time.time,
                Time.time,
                config!.MovementHearing.MinimumInterval,
                true,
                out var reason
            )
        )
        {
            CountMovement(reason);
            return;
        }
        if (movementQueue.Count >= MovementQueueLimit)
        {
            CountMovement("source-queue-full");
            return;
        }
        var settings = SAINPlugin.LoadedPreset.GlobalSettings.Hearing;
        float range = type switch
        {
            SAINSoundType.FootStep => settings.BaseSoundRange_Footstep,
            SAINSoundType.Sprint => settings.BaseSoundRange_Sprint,
            SAINSoundType.Jump => settings.BaseSoundRange_Jump,
            _ => settings.MaxSoundRange_FallLanding,
        };
        // Same emitter modifiers as PlayerComponent.AddCachedAISoundEvent. Do not use PlayAISound's
        // global cache or BotHearingClass, which would distribute this added signal to non-PMCs too.
        volume *= settings.FootstepAudioMultiplier;
        var weather = SAINWeatherClass.Instance;
        if (!source.AIData.PlayerLocation.InBunker && weather != null)
            volume *=
                player.AIData.EnvironmentId == 0
                    ? weather.RainSoundModifierOutdoor
                    : weather.RainSoundModifierIndoor;
        range *= (float)config.MovementHearing.RangeMultiplier;
        if (!(range > 0 && volume > 0) || float.IsInfinity(range) || float.IsInfinity(volume))
        {
            CountMovement("invalid-volume");
            return;
        }
        movementSources.Record(key, Time.time, Time.time, true);
        movementQueue.Enqueue(
            new MovementSignal
            {
                Id = ++movementSequence,
                Sound = new SoundEvent(type, source.Position, source, range, volume, 343),
            }
        );
    }

    private void UpdateMovementHearing()
    {
        try
        {
            if (!MovementEnabled)
            {
                if (movementQueue.Count > 0 || movementPending.Count > 0)
                    ClearMovementHearing();
                return;
            }
            if (Time.time >= nextMovementCleanup)
            {
                nextMovementCleanup = Time.time + .25f;
                foreach (var pair in movementPending.ToArray())
                {
                    var d = pair.Value;
                    if (
                        !MovementHearingGate.Fresh(Time.time, d.Signal.Sound.TimeCreated)
                        || !MovementListener(d.Bot)
                        || !MovementSource(d.Signal.Sound.PlayerComponent)
                    )
                        RemoveMovement(d, "movement-expired-or-inactive");
                }
                movementSources.Prune(Time.time);
                movementListeners.Prune(Time.time);
                foreach (
                    var key in movementLog
                        .Where(x => Time.time - x.Value.At > 10)
                        .Select(x => x.Key)
                        .ToArray()
                )
                    movementLog.Remove(key);
            }
            int budget = MovementPairsPerFrame,
                snapshots = 0;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (movementQueue.Count > 0 && budget > 0 && clock.Elapsed.TotalMilliseconds < 1)
            {
                var signal = movementQueue.Peek();
                var sound = signal.Sound;
                if (
                    !MovementSource(sound.PlayerComponent)
                    || !MovementHearingGate.Fresh(Time.time, sound.TimeCreated)
                )
                {
                    movementQueue.Dequeue();
                    CountMovement("source-expired-or-inactive");
                    continue;
                }
                if (signal.Recipients == null)
                {
                    if (++snapshots > 8)
                        break;
                    // Existing SAIN distance cache, never a scene scan or a new raycast. Snapshot once;
                    // do not hold a live collection enumerator across frames/spawns/despawns.
                    var cache = sound.PlayerComponent.OtherPlayersData.DataHashSet;
                    if (cache.Count > MovementRecipientsLimit)
                        CountMovement("recipient-snapshot-truncated");
                    signal.Recipients = cache
                        .Take(MovementRecipientsLimit)
                        .Where(x =>
                            x?.OtherPlayerComponent?.BotComponent != null
                            && x.IsInHearingRadius_Footsteps
                            && x.DistanceData.Distance <= sound.BaseRangeWithVolume
                            && MovementListener(x.OtherPlayerComponent.BotComponent)
                        )
                        .Select(x => x.OtherPlayerComponent)
                        .ToArray();
                }
                if (signal.Cursor >= signal.Recipients.Length)
                {
                    movementQueue.Dequeue();
                    continue;
                }
                var listener = signal.Recipients[signal.Cursor++];
                budget--;
                var bot = listener?.BotComponent;
                if (
                    bot == null
                    || !MovementListener(bot)
                    || ReferenceEquals(listener, sound.PlayerComponent)
                )
                    continue;
                float distance = Vector3.Distance(bot.Position, sound.Position); // Frozen emission position, never a moving hidden destination.
                if (distance > sound.BaseRangeWithVolume)
                    continue;
                if (movementPending.Count >= MovementHearingGate.Capacity)
                {
                    CountMovement("listener-queue-full");
                    continue;
                }
                var delivery = new MovementDelivery
                {
                    Signal = signal,
                    Bot = bot,
                    Distance = distance,
                };
                movementInjection = delivery;
                try
                {
                    bot.Hearing.HearingInput.CheckAddSoundToCache(sound, distance);
                }
                finally
                {
                    movementInjection = null;
                }
            }
            if (Time.time >= nextMovementSummary)
            {
                nextMovementSummary = Time.time + 10;
                FlushMovementSummary();
            }
        }
        catch (Exception ex)
        {
            MovementError(ex);
            ClearMovementHearing();
        }
    }

    private static bool MovementCachePrefix(
        HearingInput __instance,
        SoundEvent Sound,
        ref MovementCacheState? __state
    )
    {
        var p = Instance;
        try
        {
            if (
                p == null
                || !p.MovementEnabled
                || !Sound.IsAI
                || !p.MovementTypeEnabled(Sound.SoundType)
                || !p.MovementListener(__instance.Bot)
            )
                return true;
            bool added =
                p.movementInjection != null
                && ReferenceEquals(p.movementInjection.Bot, __instance.Bot);
            // The freshness/capacity policy belongs to our added stream, not another mod's
            // existing hearing delivery. Only an actual duplicate can suppress that stream.
            if (!added && !MovementHearingGate.Fresh(Time.time, Sound.TimeCreated))
                return true;
            string key = MovementKey(__instance.Bot, Sound);
            if (
                !p.movementListeners.Allows(
                    key,
                    Time.time,
                    Sound.TimeCreated,
                    p.config!.MovementHearing.MinimumInterval,
                    added,
                    out var reason
                )
            )
            {
                p.CountMovement(reason);
                return !added && reason != "movement-duplicate";
            }
            __state = new MovementCacheState
            {
                Key = key,
                Added = added,
                Count = __instance.AISoundCachedEvents.Count,
            };
        }
        catch (Exception ex)
        {
            p?.MovementError(ex);
        }
        return true;
    }

    private static void MovementCachePostfix(
        HearingInput __instance,
        SoundEvent Sound,
        MovementCacheState? __state
    )
    {
        var p = Instance;
        if (p == null || __state == null)
            return;
        try
        {
            if (__instance.AISoundCachedEvents.Count <= __state.Count)
            {
                if (__state.Added)
                    p.CountMovement("native-cache-refused");
                return;
            }
            p.movementListeners.Record(__state.Key, Time.time, Sound.TimeCreated, __state.Added);
            if (!__state.Added || p.movementInjection == null)
                return;
            var delivery = p.movementInjection;
            p.movementPending[(__instance.Bot, Sound)] = delivery;
            p.CountMovement("queued");
            p.EmitMovement(delivery, "queued", "movement-queued");
        }
        catch (Exception ex)
        {
            p.MovementError(ex);
        }
    }

    private static bool MovementReactPrefix(
        HearingSensor __instance,
        AISoundData sound,
        ref MovementDelivery? __state
    )
    {
        var p = Instance;
        if (p == null || !p.movementPending.TryGetValue((__instance.Bot, sound.Sound), out var d))
            return true;
        try
        {
            if (
                !p.MovementEnabled
                || !p.MovementListener(d.Bot)
                || !p.MovementSource(d.Signal.Sound.PlayerComponent)
                || !MovementHearingGate.Fresh(Time.time, d.Signal.Sound.TimeCreated)
            )
            {
                p.CountMovement("reaction-expired-or-inactive");
                p.movementPending.Remove((d.Bot, sound.Sound));
                return false;
            }
            __state = p.movementReaction;
            p.movementReaction = d;
            return true; // Native detection roll, delay and dispersion are retained.
        }
        catch (Exception ex)
        {
            p.MovementError(ex);
            return false;
        }
    }

    private static Exception? MovementReactFinally(
        HearingSensor __instance,
        AISoundData sound,
        MovementDelivery? __state,
        Exception? __exception
    )
    {
        var p = Instance;
        if (p == null || !p.movementPending.TryGetValue((__instance.Bot, sound.Sound), out var d))
            return __exception;
        try
        {
            p.CountMovement(d.Heard ? "heard" : "not-heard");
            p.EmitMovement(
                d,
                d.Heard ? "heard" : "not-heard",
                d.Heard ? "movement-personally-heard" : "movement-native-not-heard"
            );
        }
        catch (Exception ex)
        {
            p.MovementError(ex);
        }
        finally
        {
            p.movementReaction = __state;
            p.movementPending.Remove((__instance.Bot, sound.Sound));
        }
        return __exception; // Never swallow a native SAIN exception.
    }

    private static void MovementHeard(Enemy ___Enemy, SAINSoundType __0, EnemyPlace __2)
    {
        var p = Instance;
        var d = p?.movementReaction;
        if (
            d == null
            || __2 == null
            || !ReferenceEquals(___Enemy.Bot, d.Bot)
            || ___Enemy.EnemyProfileId != d.Signal.Sound.ProfileId
            || __0 != d.Signal.Sound.SoundType
            || !ReferenceEquals(__2, ___Enemy.KnownPlaces.LastHeardPlace)
        )
            return;
        d.Heard = true;
        d.Perceived = __2.Position;
    }

    private static bool MovementSharedContact(Enemy __instance)
    {
        var p = Instance;
        try
        {
            return p?.movementReaction == null || p.MovementListener(__instance.Bot);
        }
        catch (Exception ex)
        {
            p?.MovementError(ex);
            return false;
        }
    }

    private void RemoveMovement(MovementDelivery d, string reason)
    {
        var sound = d.Signal.Sound;
        var input = d.Bot?.Hearing?.HearingInput;
        if (input != null)
        {
            input.AISoundCachedEvents.RemoveAll(x => x.Sound.Equals(sound));
            input.SoundDataToReactTo.RemoveAll(x => x.Sound.Equals(sound));
        }
        movementPending.Remove((d.Bot!, sound));
        CountMovement(reason);
    }

    private void ClearMovementHearing(bool endRaid = false)
    {
        foreach (var delivery in movementPending.Values.ToArray())
            RemoveMovement(delivery, "movement-cleared");
        movementQueue.Clear();
        movementPending.Clear();
        movementSources.Clear();
        movementListeners.Clear();
        movementLog.Clear();
        movementInjection = movementReaction = null;
        FlushMovementSummary();
        if (endRaid)
        {
            nextMovementCleanup = nextMovementSummary = movementFaultUntil = 0;
            movementSequence = 0;
            movementWarned = false;
        }
    }

    private void FlushMovementSummary()
    {
        if (movementCounts.Count == 0)
            return;
        diagnostics?.WriteEssential(
            Diagnostics.Serialize(
                new
                {
                    schema = 1,
                    session,
                    feature = "P01",
                    stage = "summary",
                    reason = "movement-counters",
                    realtime = Time.realtimeSinceStartup,
                    counters = movementCounts,
                    queuedSignals = movementQueue.Count,
                    pendingListeners = movementPending.Count,
                }
            )
        );
        movementCounts.Clear();
    }

    private void EmitMovement(MovementDelivery d, string outcome, string reason)
    {
        // Detailed transitions per observer/source/kind/outcome at most once a second; counters retain totals.
        var sound = d.Signal.Sound;
        if (!states.TryGetValue(d.Bot, out var state))
        {
            state = new BotState(d.Bot, config!, session);
            states.Add(d.Bot, state);
        }
        var point = d.Perceived;
        var evidence = new
        {
            signal = d.Signal.Id,
            source = sound.ProfileId,
            kind = sound.SoundType.ToString(),
            outcome,
            emittedAt = sound.TimeCreated,
            age = Time.time - sound.TimeCreated,
            distance = d.Distance,
            range = sound.Range,
            volume = sound.Volume,
            analysisDistance = d.AnalysisDistance,
            nativeRange = d.NativeRange,
            finalRange = d.FinalRange,
            environment = d.Environment,
            occlusion = d.Occlusion,
            x = point?.x,
            y = point?.y,
            z = point?.z,
        };
        if (d.Heard)
        {
            state.MovementEvidence = evidence;
            state.MovementSource = sound.ProfileId;
            state.MovementHeardAt = Time.time;
        }
        string key = MovementKey(d.Bot, sound) + ":" + outcome;
        movementLog.TryGetValue(key, out var last);
        if (last.At > 0 && Time.time - last.At < 1)
        {
            movementLog[key] = (last.At, last.Suppressed + 1);
            return;
        }
        if (movementLog.Count >= MovementHearingGate.Capacity && !movementLog.ContainsKey(key))
            return;
        movementLog[key] = (Time.time, 0);
        string json = Diagnostics.Serialize(
            new
            {
                schema = 1,
                session,
                sequence = ++sequence,
                feature = "P01",
                stage = "evaluated",
                reason,
                decision = "MovementHearing",
                realtime = Time.realtimeSinceStartup,
                utc = DateTime.UtcNow.ToString("O"),
                configRevision = config?.Revision ?? 0,
                hearingConfig = config!.MovementHearing,
                cometVersion = CometVersion.Release,
                profileId = state.ProfileId,
                name = state.Name,
                botInstance = state.Id,
                hearing = evidence,
                suppressed = last.Suppressed,
                contact = point == null
                    ? null
                    : new
                    {
                        target = sound.ProfileId,
                        source = "heard",
                        age = 0,
                        x = point.Value.x,
                        y = point.Value.y,
                        z = point.Value.z,
                    },
            }
        );
        diagnostics?.Write(json);
        Api.Decisions.Publish(json, ex =>
        {
            if (!subscriberWarned)
            {
                subscriberWarned = true;
                Logger.LogWarning(ex.Message);
            }
        });
        // Hearing is evidence, not an accepted combat action. Never write GetCurrentDecision here.
    }

    private void MovementError(Exception ex)
    {
        CountMovement("adapter-error");
        movementFaultUntil = Time.time + 10;
        if (!movementWarned)
        {
            movementWarned = true;
            Logger.LogWarning("COMET P01 movement hearing: " + ex.Message);
        }
    }
}
