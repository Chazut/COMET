using System;
using System.Reflection;
using Comet.Core;
using HarmonyLib;
using Newtonsoft.Json;
using SAIN.Components;
using SAIN.Helpers;
using SAIN.SAINComponent.Classes.Sense;
using UnityEngine;

namespace Comet.Client;

internal sealed class FlashCall
{
    internal float Now,
        OldEnd,
        OldStrong,
        Duration;
    internal bool WasBlind,
        Applied;
    internal Vector3? Remembered;
    internal string Target = "";
}

internal sealed class FlashRuntime
{
    internal FlashCall? Pending;
    internal BotFlashedClass? Native;
    internal TemporaryStatModifiers? First,
        Second;
    internal float End,
        StrongEnd,
        NextLog;
    internal bool Active,
        Fault;
    internal Vector3? Remembered;
    internal string Target = "";
    internal FlashContinuityTrace? Trace;
}

public sealed partial class Plugin
{
    private static FieldInfo flashEnd = null!,
        flashBlindDelay = null!,
        flashFirst = null!,
        flashSecond = null!;
    private static MethodInfo flashMemorySetter = null!,
        flashApplyModifiers = null!;

    private static void PatchFlashContinuity(Harmony h)
    {
        var t = typeof(BotFlashedClass);
        FieldInfo Field(string name, Type type)
        {
            var f = AccessTools.Field(t, name);
            if (f == null || f.FieldType != type)
                throw new MissingMemberException("G09 SAIN " + name);
            return f;
        }
        flashEnd = Field("_flashEndTime", typeof(float));
        flashBlindDelay = Field("_blindFireTime", typeof(float));
        flashFirst = Field("_firstStage", typeof(TemporaryStatModifiers));
        flashSecond = Field("_secondStage", typeof(TemporaryStatModifiers));
        flashMemorySetter = AccessTools.PropertySetter(
            t,
            nameof(BotFlashedClass.LastSeenEnemyPoint)
        ) ?? throw new MissingMemberException("G09 flash memory");
        flashApplyModifiers = AccessTools.Method(
            t,
            "ApplyModifiers",
            new[] { typeof(float), typeof(float) }
        ) ?? throw new MissingMethodException("G09 flash modifiers");
        h.Patch(
            AccessTools.Method(
                t,
                nameof(BotFlashedClass.ApplyFlash),
                new[] { typeof(float), typeof(Vector3) }
            ),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeRealFlash))
            {
                priority = Priority.Last,
            },
            postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterRealFlash)),
            finalizer: new HarmonyMethod(typeof(Plugin), nameof(FlashException))
        );

        h.Patch(
            flashApplyModifiers,
            prefix: new HarmonyMethod(typeof(Plugin), nameof(FlashModifiersAllowed))
        );
        h.Patch(
            AccessTools.Method(t, nameof(BotFlashedClass.ManualUpdate)),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(ObserveFlash))
        );
        h.Patch(
            AccessTools.Method(t, nameof(BotFlashedClass.Dispose)),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(DisposedFlash))
        );
    }

    private bool FlashScope(BotComponent b) =>
        world != null
        && config?.Enabled == true
        && config.FlashContinuity.Enabled
        && b != null
        && !b.IsDead
        && NativeAdapter.IsSupportedAi(b)
        && b.BotActive
        && !b.GameEnding
        && Authority
        && !Ghost(b.ProfileId);

    private static bool ValidPoint(Vector3 v) =>
        FlashContinuityRules.Finite(v.x)
        && FlashContinuityRules.Finite(v.y)
        && FlashContinuityRules.Finite(v.z);

    private static float FlashDuration(BotComponent b, float baseTime)
    {
        var c = SAIN.SAINPlugin.LoadedPreset.GlobalSettings.Look.Flashbang;
        var duration =
            baseTime * c.DurationMultiplier * b.Info.FileSettings.Look.FlashDurationMulti;
        if (b.BotOwner.NightVision.UsingNow)
            duration *= c.NightVisionMultiplier;
        return Math.Min(duration, c.MaxDuration);
    }

    private static bool BeforeRealFlash(
        BotFlashedClass __instance,
        float baseTime,
        Vector3 position,
        out FlashCall? __state
    )
    {
        __state = null;
        var p = Instance;
        if (p == null || !(p.FlashScope(__instance.Bot) || p.gasFlashCall))
            return true;
        try
        {
            __state = new FlashCall
            {
                Now = Time.time,
                Duration = FlashDuration(__instance.Bot, baseTime),
                WasBlind = __instance.IsFlashed,
            };
        }
        catch (Exception ex)
        {
            p.Logger.LogWarning("G09 observation: " + ex.Message);
        }
        return true; // Real flashes keep native zzap memory, search point, delay and duration.
    }

    private static bool DeadFlashOwner(BotComponent? b) => b == null || b.IsDead;

    private bool gasFlashCall;

    private static bool FlashModifiersAllowed() => Instance?.gasFlashCall != true;

    private static void AfterRealFlash(
        BotFlashedClass __instance,
        FlashCall? __state,
        bool __runOriginal
    )
    {
        var p = Instance;
        if (p == null || __state == null || !__runOriginal || __state.Duration <= 0)
            return;
        try
        {
            var s = p.GrenadeState(__instance.Bot);
            var r = s.Flash;
            r.Native = __instance;
            r.End = (float)flashEnd.GetValue(__instance)!;
            r.StrongEnd =
                Time.time
                + __state.Duration
                    * SAIN.SAINPlugin.LoadedPreset.GlobalSettings.Look.Flashbang.RecoveryPoint;
            r.Remembered = __instance.LastSeenEnemyPoint;
            r.Target = __instance.Bot.GoalEnemy?.EnemyProfileId ?? "";
            r.First = (TemporaryStatModifiers?)flashFirst.GetValue(__instance);
            r.Second = (TemporaryStatModifiers?)flashSecond.GetValue(__instance);
            r.Active = true;
            r.Trace = new FlashContinuityTrace
            {
                Source = p.gasFlashCall ? "gas-start" : "native-flash",
                RequestedDuration = __state.Duration,
            };
            p.RecordFlash(
                __instance.Bot,
                s,
                __state.WasBlind ? "new-flash" : "started",
                "flash-source-live-memory",
                true
            );
        }
        catch (Exception ex)
        {
            p.Logger.LogWarning("G09 observation: " + ex.Message);
        }
    }

    private static Exception? FlashException(BotFlashedClass __instance, Exception? __exception)
    {
        var p = Instance;
        if (__exception != null && p != null && p.states.TryGetValue(__instance.Bot, out var s))
            p.FlashError(__instance.Bot, s, __exception);
        return __exception; // Never hide native errors.
    }

    // Port of zzap RefreshFlash: duration only. No memory/search/blind-fire restart.
    internal bool RefreshOngoingFlash(BotComponent b, float baseTime)
    {
        if (!GasScope(b) && !FlashScope(b))
            return false;
        if (!b.Flashed.IsFlashed)
            return false;
        float duration = FlashDuration(b, baseTime),
            old = (float)flashEnd.GetValue(b.Flashed)!;
        if (duration <= 0 || Time.time + duration <= old)
            return false;
        flashEnd.SetValue(b.Flashed, Time.time + duration);
        var s = GrenadeState(b);
        var r = s.Flash;
        r.Native = b.Flashed;
        r.End = Time.time + duration;
        r.Remembered = b.Flashed.LastSeenEnemyPoint;
        r.Active = true;
        r.Trace = new FlashContinuityTrace
        {
            Source = "gas-continuation",
            RequestedDuration = duration,
        };
        RecordFlash(b, s, "extended", "flash-duration-only-refresh", true);
        return true;
    }

    private void ApplyGasFlash(BotComponent b, Vector3 position)
    {
        if (b.Flashed.IsFlashed)
        {
            RefreshOngoingFlash(b, 3);
            return;
        }
        gasFlashCall = true;
        try
        {
            b.Flashed.ApplyFlash(3, position);
        }
        finally
        {
            gasFlashCall = false;
        }
    }

    private static void ObserveFlash(BotFlashedClass __instance)
    {
        var p = Instance;
        if (p == null || !p.states.TryGetValue(__instance.Bot, out var s) || !s.Flash.Active)
            return;
        try
        {
            if (!p.FlashScope(__instance.Bot) && !p.GasScope(__instance.Bot))
            {
                p.DropFlash(__instance.Bot, s, "flash-out-of-scope", false);
                return;
            }
            if (!__instance.IsFlashed)
            {
                p.DropFlash(__instance.Bot, s, "flash-recovered", false);
                return;
            }
            p.RecordFlash(__instance.Bot, s, "active", "flash-in-progress", false);
        }
        catch (Exception ex)
        {
            p.FlashError(__instance.Bot, s, ex);
        }
    }

    private static void DisposedFlash(BotFlashedClass __instance)
    {
        var p = Instance;
        if (p != null && p.states.TryGetValue(__instance.Bot, out var s))
            p.DropFlash(__instance.Bot, s, "flash-disposed", true);
    }

    private void DropFlash(BotComponent? b, BotState s, string reason, bool cleanup)
    {
        var r = s.Flash;
        r.Pending = null;
        try
        {
            if (cleanup && b != null && r.Native != null)
            {
                // Do not dismiss a replacement installed by another source, or G08's separate modifiers.
                if (r.First != null)
                {
                    b.BotOwner.Settings.Current.Dismiss(r.First.Modifiers);
                    if (ReferenceEquals(flashFirst.GetValue(r.Native), r.First))
                        flashFirst.SetValue(r.Native, null);
                }
                if (r.Second != null)
                {
                    b.BotOwner.Settings.Current.Dismiss(r.Second.Modifiers);
                    if (ReferenceEquals(flashSecond.GetValue(r.Native), r.Second))
                        flashSecond.SetValue(r.Native, null);
                }
                if ((float)flashEnd.GetValue(r.Native)! == r.End)
                {
                    flashEnd.SetValue(r.Native, 0f);
                    flashMemorySetter.Invoke(r.Native, new object?[] { null });
                }
            }
            if (r.Active)
                RecordFlash(b, s, "ended", reason, true);
        }
        finally
        {
            r.Active = false;
            r.Native = null;
            r.First = r.Second = null;
            r.Remembered = null;
            r.Target = "";
        }
    }

    private void FlashError(BotComponent b, BotState s, Exception ex)
    {
        if (!s.Flash.Fault)
            Logger.LogWarning("G09 yielded: " + ex.GetBaseException().Message);
        s.Flash.Fault = true;
        DropFlash(b, s, "flash-adapter-error", false);
    }

    private void RecordFlash(BotComponent? b, BotState s, string state, string reason, bool force)
    {
        var r = s.Flash;
        if (!force && Time.time < r.NextLog)
            return;
        r.NextLog = Time.time + .5f;
        var t = r.Trace ?? new FlashContinuityTrace();
        r.Trace = t;
        t.State = state;
        t.Reason = reason;
        t.ValidFor = state == "ended" ? 0 : 1;
        t.Remaining = state == "ended" ? 0 : Math.Max(0, r.End - Time.time);
        t.StrongRemaining = state == "ended" ? 0 : Math.Max(0, r.StrongEnd - Time.time);
        t.BlindFireRemaining =
            state == "ended" || r.Native == null
                ? 0
                : Math.Max(0, (float)flashBlindDelay.GetValue(r.Native)! - Time.time);
        t.HasRemembered = state != "ended" && r.Remembered.HasValue;
        t.Target = t.HasRemembered ? r.Target : "";
        t.X = t.HasRemembered ? r.Remembered!.Value.x : 0;
        t.Y = t.HasRemembered ? r.Remembered!.Value.y : 0;
        t.Z = t.HasRemembered ? r.Remembered!.Value.z : 0;
        t.GasActive = s.Gas.Intensity > 0;
        t.Alternatives = new[]
        {
            "Real flash: native live enemy memory and new duration",
            "Continuous gas: extend blindness without refreshing memory or blind-fire delay",
            "Gas modifiers stay separate",
        };
        if (!Comet.Core.ReplayCapture.Detailed)
            return;
        var json = Diagnostics.Serialize(
            new
            {
                schema = 1,
                session,
                sequence = ++sequence,
                feature = "G09",
                stage = "evaluated",
                decision = "FlashContinuity",
                reason,
                realtime = Time.realtimeSinceStartup,
                utc = DateTime.UtcNow.ToString("O"),
                configRevision = config?.Revision ?? 0,
                profileId = s.ProfileId,
                name = s.Name,
                botInstance = s.Id,
                flashContinuity = t,
                flashContinuityConfig = config?.FlashContinuity,
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
