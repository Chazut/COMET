using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using SAIN;
using SAIN.SAINComponent;
using SAIN.SAINComponent.Classes.Mover;
using SAIN.SAINComponent.Classes.WeaponFunction;
using UnityEngine;

namespace Comet.Client;

public sealed partial class Plugin
{
    private sealed class NativeClock
    {
        internal double Last = double.NaN;
    }

    private ConditionalWeakTable<Recoil, NativeClock> recoilClocks = new();
    private ConditionalWeakTable<BotBase, NativeClock> smoothingClocks = new();

    private static void PatchNativeTiming(Harmony h)
    {
        h.Patch(
            AccessTools.Method(typeof(Recoil), nameof(Recoil.WeaponShot)),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(StartNativeRecoilClock))
        );
        // W09 may replace this step for its optional DMR/sniper multiplier. Its
        // successful prefix clears __runOriginal; never apply two decay steps.
        h.Patch(
            AccessTools.Method(typeof(Recoil), nameof(Recoil.CalcRecoilDecay)),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(FixNativeRecoil))
            {
                priority = Priority.Last,
            },
            postfix: new HarmonyMethod(typeof(Plugin), nameof(AdvanceNativeRecoilClock))
        );
        h.Patch(
            AccessTools.Method(typeof(PoseClass), nameof(PoseClass.ManualUpdate)),
            transpiler: new HarmonyMethod(typeof(Plugin), nameof(FixSmoothingClock))
        );
        h.Patch(
            AccessTools.Method(typeof(LeanClass), "SetTilt"),
            transpiler: new HarmonyMethod(typeof(Plugin), nameof(FixSmoothingClock))
        );
    }

    private static void StartNativeRecoilClock(Recoil __instance)
    {
        var p = Instance;
        if (p?.NativeFixScope(__instance.Bot) == true && !__instance.Bot.IsCheater)
            p.recoilClocks.GetOrCreateValue(__instance).Last = Time.timeAsDouble;
    }

    private static void AdvanceNativeRecoilClock(Recoil __instance)
    {
        var p = Instance;
        if (p != null && p.recoilClocks.TryGetValue(__instance, out var clock))
            clock.Last = Time.timeAsDouble;
    }

    private static bool FixNativeRecoil(
        Recoil __instance,
        bool __runOriginal,
        ref float ____currentRecoilHorizAngle,
        ref float ____currentRecoilVertAngle,
        ref bool ____recoilFinished
    )
    {
        // HarmonyX runs later prefixes even after an earlier prefix returned false.
        if (!__runOriginal)
            return false;
        var p = Instance;
        if (p == null || !p.NativeFixScope(__instance.Bot) || __instance.Bot.IsCheater)
            return true;
        if (____recoilFinished)
            return false;
        var clock = p.recoilClocks.GetOrCreateValue(__instance);
        double now = Time.timeAsDouble;
        double elapsed = double.IsNaN(clock.Last) ? 0 : Math.Max(0, now - clock.Last);
        clock.Last = now;
        double step = Time.fixedDeltaTime;
        double retention = Mathf.Clamp01(
            1 - (float)step * SAINPlugin.LoadedPreset.GlobalSettings.Shoot.BOT_RECOIL_DECAY_COEF
        );
        float factor = elapsed == 0 ? 1 : (float)Math.Pow(retention, elapsed / step);
        ____currentRecoilHorizAngle *= factor;
        ____currentRecoilVertAngle *= factor;
        bool finished =
            Math.Abs(____currentRecoilHorizAngle) <= .001f
            && Math.Abs(____currentRecoilVertAngle) <= .001f;
        if (!finished && ____currentRecoilHorizAngle <= .001f && ____currentRecoilVertAngle < .001f)
            p.CountNativeFix(NativeFix.SignedRecoil, __instance.Bot);
        ____recoilFinished = finished;
        if (finished)
        {
            ____currentRecoilHorizAngle = 0;
            ____currentRecoilVertAngle = 0;
        }
        if (elapsed > 0)
            p.CountNativeFix(NativeFix.RecoilStep, __instance.Bot);
        return false;
    }

    private static IEnumerable<CodeInstruction> FixSmoothingClock(
        IEnumerable<CodeInstruction> instructions
    )
    {
        int count = 0;
        var fixedTime = AccessTools.PropertyGetter(typeof(Time), nameof(Time.fixedDeltaTime));
        foreach (var instruction in instructions)
        {
            if (!instruction.Calls(fixedTime))
            {
                yield return instruction;
                continue;
            }
            count++;
            yield return new CodeInstruction(instruction)
            {
                opcode = OpCodes.Ldarg_0,
                operand = null,
            };
            yield return new CodeInstruction(
                OpCodes.Call,
                AccessTools.Method(typeof(Plugin), nameof(SmoothingElapsed))
            );
        }
        if (count == 0)
            throw new InvalidOperationException("SAIN pose/lean clock changed");
    }

    private static float SmoothingElapsed(BotBase owner)
    {
        var p = Instance;
        if (p == null || !p.NativeFixScope(owner.Bot))
        {
            p?.smoothingClocks.Remove(owner);
            return Time.fixedDeltaTime;
        }
        var clock = p.smoothingClocks.GetOrCreateValue(owner);
        double now = Time.timeAsDouble;
        float elapsed = double.IsNaN(clock.Last)
            ? 0
            : (float)Math.Min(.25, Math.Max(0, now - clock.Last));
        clock.Last = now;
        p.CountNativeFix(owner is PoseClass ? NativeFix.PoseStep : NativeFix.LeanStep, owner.Bot);
        return elapsed;
    }
}
