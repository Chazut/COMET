using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using Comet.Core;
using HarmonyLib;
using Newtonsoft.Json;
using SAIN.Classes.Bot.Sense.Hearing;
using SAIN.Components;
using SAIN.Components.PlayerComponentSpace;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.EnemyClasses;
using SAIN.SAINComponent.Classes.Mover;
using UnityEngine;

namespace Comet.Client;

public sealed partial class Plugin
{
    // Harmony stores __state per declaring type: keep this module independent of other hooks.
    private static class I01GunfireHooks
    {
        internal static void GunfireBegin(
            HearingSensor __instance,
            AISoundData sound,
            ref GunfireCall? __state
        ) => Plugin.GunfireBegin(__instance, sound, ref __state);

        internal static Exception? GunfireEnd(GunfireCall? __state, Exception? __exception) =>
            Plugin.GunfireEnd(__state, __exception);
    }

    private sealed class GunfireCall
    {
        internal HearingSensor Sensor = null!;
        internal AISoundData Sound;
        internal GunfireTrace? Trace;
        internal BotState? State;
    }

    private GunfireCall? gunfireCall;
    private static Func<HearingSensor, float, bool> nativeGunfireGate = null!;

    private static void PatchGunfire(Harmony h)
    {
        var chase = AccessTools.Method(
            typeof(HearingSensor),
            "ShallChaseGunshot",
            new[] { typeof(float) }
        );
        if (chase == null || chase.ReturnType != typeof(bool))
            throw new MissingMethodException("P03 native gunshot gate");
        nativeGunfireGate =
            (Func<HearingSensor, float, bool>)
                Delegate.CreateDelegate(typeof(Func<HearingSensor, float, bool>), chase);
        h.Patch(
            AccessTools.Method(typeof(HearingSensor), nameof(HearingSensor.ReactToHeardSound)),
            prefix: new HarmonyMethod(
                typeof(I01GunfireHooks),
                nameof(I01GunfireHooks.GunfireBegin)
            ),
            finalizer: new HarmonyMethod(
                typeof(I01GunfireHooks),
                nameof(I01GunfireHooks.GunfireEnd)
            ),
            transpiler: new HarmonyMethod(typeof(Plugin), nameof(GunfireGateTranspiler))
        );
        // Preserve the native ballistic path completely. Merely stop a cosmetic glance after a fly-by.
        h.Patch(
            AccessTools.Method(
                typeof(HearingSensor),
                nameof(HearingSensor.ReactToBulletFlyBy),
                new[] { typeof(AISoundData), typeof(float) }
            ),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(GunfireFlyBy))
        );
        h.Patch(
            AccessTools.Method(
                typeof(EnemyEvents),
                nameof(EnemyEvents.EnemyHeard),
                new[] { typeof(SAINSoundType), typeof(bool), typeof(EnemyPlace) }
            ),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(GunfireHeard))
        );
    }

    private static IEnumerable<CodeInstruction> GunfireGateTranspiler(
        IEnumerable<CodeInstruction> instructions
    )
    {
        var original = AccessTools.Method(
            typeof(HearingSensor),
            "ShallChaseGunshot",
            new[] { typeof(float) }
        );
        var wrapper = AccessTools.Method(typeof(Plugin), nameof(GunfireDispatch));
        int count = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(original))
            {
                count++;
                instruction.opcode = OpCodes.Call;
                instruction.operand = wrapper;
            }
            yield return instruction; // Same stack shape; retain labels and exception boundaries.
        }
        if (count != 1)
            throw new InvalidOperationException("P03 native gunfire gate call changed.");
    }

    private static bool GunfireDispatch(HearingSensor sensor, float distance)
    {
        bool result = false;
        return GunfireGate(sensor, distance, ref result)
            ? nativeGunfireGate(sensor, distance)
            : result;
    }

    private bool GunfireScoped(BotComponent b) =>
        world != null
        && config?.Enabled == true
        && config.Gunfire.Enabled
        && NativeAdapter.IsSupportedAi(b)
        && b.BotActive
        && !b.GameEnding
        && Authority
        && !Ghost(b.ProfileId);

    private static void GunfireBegin(
        HearingSensor __instance,
        AISoundData sound,
        ref GunfireCall? __state
    )
    {
        var p = Instance;
        if (p == null)
            return;
        __state = p.gunfireCall;
        // Scope every nested invocation, including non-gunshots. A stale outer sound must never be reused.
        p.gunfireCall =
            sound.IsGunShot && p.config?.Enabled == true && p.config.Gunfire.Enabled
                ? new GunfireCall { Sensor = __instance, Sound = sound }
                : null;
    }

    private static Exception? GunfireEnd(GunfireCall? __state, Exception? __exception)
    {
        var p = Instance;
        if (p == null)
            return __exception;
        var call = p.gunfireCall;
        p.gunfireCall = __state;
        if (call?.Trace != null && call.State != null)
        {
            try
            {
                if (__exception != null)
                {
                    call.Trace.SearchObserved = false;
                    call.Trace.Reason = "gunfire-native-error";
                }
                p.RecordGunfire(call.State, call.Trace);
            }
            catch (Exception ex)
            {
                p.GunfireError(call.Sensor.Bot, ex);
            }
        }
        return __exception; // Never swallow a native hearing exception.
    }

    private static bool GunfireGate(HearingSensor __instance, float Distance, ref bool __result)
    {
        var p = Instance;
        var call = p?.gunfireCall;
        if (
            p == null
            || call == null
            || !ReferenceEquals(call.Sensor, __instance)
            || !call.Sound.IsGunShot
        )
            return true;
        var b = __instance.Bot;
        try
        {
            if (!p.GunfireScoped(b))
                return true;
            if (!p.states.TryGetValue(b, out var s))
            {
                if (p.states.Count >= 512)
                    return true;
                s = new BotState(b, p.config!, p.session);
                p.states.Add(b, s);
            }
            if (s.GunfireFault)
                return true;
            var e = call.Sound.Enemy;
            if (e == null)
                return true;
            string owner = GunfireOwner(b);
            bool incoming =
                GunfireReaction.Recent(
                    Time.time,
                    e.Status.TimeLastShotAtMe,
                    p.config!.Gunfire.IncomingMemory
                )
                || GunfireReaction.Recent(
                    Time.time,
                    e.Status.TimeLastShotMe,
                    p.config.Gunfire.IncomingMemory
                );
            bool fighting =
                b.HasEnemy && b.GoalEnemy != null && b.GoalEnemy.EnemyProfileId != e.EnemyProfileId;
            var frame = new GunfireFrame
            {
                Now = Time.time,
                EmittedAt = call.Sound.Sound.TimeCreated,
                Distance = Distance,
                NearDistance = b.Info.PersonalitySettings.Search.AudioStraightDistanceToIgnore,
                Source = e.EnemyProfileId,
                Personality = b.Info.Personality.ToString(),
                Owner = owner,
                Incoming = incoming,
                Busy = fighting,
                Unsafe = "gunfire-fighting-another-enemy",
                CanLook = !b.Player.IsSprintEnabled,
                PersonalityChases = b.Info.PersonalitySettings.Search.WillChaseDistantGunshots,
            };
            var t = s.Gunfire.Evaluate(frame);
            call.State = s;
            call.Trace = t;
            if (t.PassToSain)
                p.StopGunfire(s, "gunfire-native-contact-priority");
            if (t.State == "look")
            {
                var position = call.Sound.Sound.Position + Vector3.up * 1.2f;
                b.BotOwner.Steering?.LookToPoint(position);
                t.X = position.x;
                t.Y = position.y;
                t.Z = position.z;
                t.Applied = b.BotOwner.Steering != null;
                t.ValidFor = 0;
                t.Reason = "gunfire-source-single-glance";
                s.Gunfire.Stop(Time.time, "gunfire-single-glance-complete");
            }
            __result = t.PassToSain;
            return false;
        }
        catch (Exception ex)
        {
            p.GunfireError(b, ex);
            return true;
        }
    }

    private static string GunfireOwner(BotComponent b)
    {
        string layer = b.BotOwner.Brain.ActiveLayerName() ?? "";
        if (layer.IndexOf("ORBIT", StringComparison.OrdinalIgnoreCase) >= 0)
            return "ORBIT";
        if (b.SAINLayersActive)
            return "SAIN";
        // The unmodified SAIN patrol layer can receive sound contacts while its combat layer is inactive.
        return layer == "" || layer.IndexOf("patrol", StringComparison.OrdinalIgnoreCase) >= 0
            ? "native"
            : "other";
    }

    private static void GunfireFlyBy(HearingSensor __instance)
    {
        var p = Instance;
        if (p == null || !p.states.TryGetValue(__instance.Bot, out var s))
            return;
        p.StopGunfire(s, "gunfire-incoming-fire");
    }

    private static void GunfireHeard(Enemy ___Enemy, SAINSoundType __0, EnemyPlace __2)
    {
        var call = Instance?.gunfireCall;
        if (
            call?.Trace == null
            || !call.Trace.PassToSain
            || !ReferenceEquals(call.Sound.Enemy, ___Enemy)
            || !ReferenceEquals(call.Sensor.Bot, ___Enemy.Bot)
            || __2 == null
            || !ReferenceEquals(__2, ___Enemy.KnownPlaces.LastHeardPlace)
        )
            return;
        call.Trace.SearchObserved = true; // Personal contact registered, not proof that the bot started moving.
        call.Trace.X = __2.Position.x;
        call.Trace.Y = __2.Position.y;
        call.Trace.Z = __2.Position.z;
    }

    private void LateUpdate()
    {
        LateBlasts();
    }

    private void StopGunfire(BotState s, string reason)
    {
        var t = s.Gunfire.Stop(Time.time, reason);
        if (t != null)
            RecordGunfire(s, t, true);
    }

    private void GunfireError(BotComponent b, Exception ex)
    {
        if (b == null || !states.TryGetValue(b, out var s))
            return;
        s.GunfireFault = true;
        try
        {
            StopGunfire(s, "gunfire-adapter-error");
        }
        catch { }
        if (!s.GunfireWarned)
        {
            s.GunfireWarned = true;
            Logger.LogWarning("COMET P03 inactive for this bot: " + ex.Message);
        }
    }

    private void RecordGunfire(BotState s, GunfireTrace t, bool force = false)
    {
        // Refused bullets cannot erase an ongoing glance in RR. They remain diagnostic records.
        if (!s.GunfireLog.ShouldWrite(Time.time, t, force))
            return;
        int coalesced = s.GunfireLog.TakeCoalesced();
        var json = Diagnostics.Serialize(
            new
            {
                schema = 1,
                session,
                sequence = ++sequence,
                feature = "P03",
                stage = "evaluated",
                decision = "GunfireReaction",
                reason = t.Reason,
                realtime = Time.realtimeSinceStartup,
                utc = DateTime.UtcNow.ToString("O"),
                cometVersion = CometVersion.Release,
                configRevision = config?.Revision ?? 0,
                gunfireConfig = config?.Gunfire,
                profileId = s.ProfileId,
                name = s.Name,
                botInstance = s.Id,
                gunfire = t,
                coalesced,
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
    }
}
