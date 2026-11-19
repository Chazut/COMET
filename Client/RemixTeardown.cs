using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using EFT;
using EFT.Ballistics;
using HarmonyLib;
using SAIN.Classes.Bot.Sense.Hearing;
using SAIN.Components;
using SAIN.Components.BotController;
using SAIN.Components.BotControllerSpace.Classes;
using SAIN.SAINComponent;
using SAIN.SAINComponent.Classes.Talk;

namespace Comet.Client;

// Official SAIN has Dispose but no OnDestroy. Unity can unload the GameObject
// independently, so also observe its destruction without polling each frame.
internal sealed class RemixManagerLifetime : UnityEngine.MonoBehaviour
{
    internal BotManagerComponent? Owner;

    private void OnDestroy()
    {
        var manager = Owner;
        Owner = null;
        if (!ReferenceEquals(manager, null))
            Plugin.RemixManagerDisposed(manager!);
    }
}

public sealed partial class Plugin
{
    private readonly ConditionalWeakTable<BotManagerComponent, RemixManagerLifetime> remixManagers =
        new();
    private static readonly ConditionalWeakTable<BotBase, BotHearingClass> hearingSubscriptions =
        new();

    private static void PatchRemixTeardown(Harmony h)
    {
        h.Patch(
            AccessTools.Method(typeof(BotManagerComponent), "Activate"),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(RemixManagerStarted))
        );
        h.Patch(
            AccessTools.Method(typeof(BotManagerComponent), "Dispose"),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(RemixManagerDisposed))
        );
        foreach (var listener in new[] { typeof(HearingInput), typeof(EnemyTalk) })
        {
            h.Patch(
                AccessTools.Method(listener, "Init"),
                postfix: new HarmonyMethod(typeof(Plugin), nameof(RememberHearingSource))
            );
            h.Patch(
                AccessTools.Method(listener, "Dispose"),
                transpiler: new HarmonyMethod(typeof(Plugin), nameof(RemixHearingTeardown))
            );
        }
    }

    private static void RemixManagerStarted(BotManagerComponent __instance) =>
        Instance?.RememberRemixManager(__instance);

    private void RememberRemixManager(BotManagerComponent manager)
    {
        if (
            config?.Enabled != true
            || !Authority
            || !ReferenceEquals(manager.GameWorld, world)
            || remixManagers.TryGetValue(manager, out _)
        )
            return;
        var lifetime = manager.gameObject.AddComponent<RemixManagerLifetime>();
        lifetime.Owner = manager;
        remixManagers.Add(manager, lifetime);
    }

    internal static void RemixManagerDisposed(BotManagerComponent __instance)
    {
        var p = Instance;
        if (p == null || !p.remixManagers.TryGetValue(__instance, out var lifetime))
            return;
        p.remixManagers.Remove(__instance);
        if (lifetime != null && !ReferenceEquals(lifetime.Owner, null))
        {
            lifetime.Owner = null;
            UnityEngine.Object.Destroy(lifetime);
        }
        SourceSoundWatch.Detach(__instance.BotHearing);
        var spawn = BotSpawnController.Instance;
        var weather = SAINWeatherClass.Instance;
        bool sameManager = ReferenceEquals(BotManagerComponent.Instance, __instance);
        bool sameSpawn = ReferenceEquals(spawn?.BotController, __instance);
        bool sameWeather = ReferenceEquals(weather?.BotController, __instance);
        if (sameSpawn)
            AccessTools
                .PropertySetter(typeof(BotSpawnController), "Instance")
                .Invoke(null, new object?[] { null });
        if (sameWeather)
            AccessTools
                .PropertySetter(typeof(SAINWeatherClass), "Instance")
                .Invoke(null, new object?[] { null });
        if (sameManager)
            AccessTools
                .PropertySetter(typeof(BotManagerComponent), "Instance")
                .Invoke(null, new object?[] { null });
        p.Logger.LogDebug(
            $"SAIN raid references released: manager={sameManager}, spawn={sameSpawn}, weather={sameWeather}"
        );
    }

    // Native subscriptions include all SAIN bots. Even a late or duplicate
    // Dispose must leave the new raid's publisher and other handlers intact.
    private static void RememberHearingSource(BotBase __instance)
    {
        var hearing = BotManagerComponent.Instance?.BotHearing;
        if (hearing == null)
            return;
        hearingSubscriptions.Remove(__instance);
        hearingSubscriptions.Add(__instance, hearing);
    }

    private static BotHearingClass? HearingForDisposal(BotManagerComponent? _, BotBase listener)
    {
        if (!hearingSubscriptions.TryGetValue(listener, out var original))
            return null;
        hearingSubscriptions.Remove(listener);
        return original;
    }

    private static void RemoveBulletListener(BotHearingClass? source, Action<Shot> handler)
    {
        if (source != null)
            source.BulletImpact -= handler;
    }

    private static void RemoveTalkListener(
        BotHearingClass? source,
        Action<EPhraseTrigger, ETagStatus, Player> handler
    )
    {
        if (source != null)
            source.PlayerTalk -= handler;
    }

    private static IEnumerable<CodeInstruction> RemixHearingTeardown(
        IEnumerable<CodeInstruction> instructions
    )
    {
        int getters = 0,
            removals = 0;
        foreach (var ins in instructions)
        {
            if (
                ins.operand is MethodInfo call
                && call.DeclaringType == typeof(BotManagerComponent)
                && call.Name == "get_BotHearing"
            )
            {
                getters++;
                ins.opcode = OpCodes.Ldarg_0;
                ins.operand = null;
                yield return ins;
                yield return new CodeInstruction(
                    OpCodes.Call,
                    AccessTools.Method(typeof(Plugin), nameof(HearingForDisposal))
                );
                continue;
            }
            if (
                ins.operand is MethodInfo remove
                && remove.DeclaringType == typeof(BotHearingClass)
                && (remove.Name == "remove_PlayerTalk" || remove.Name == "remove_BulletImpact")
            )
            {
                removals++;
                ins.opcode = OpCodes.Call;
                ins.operand = AccessTools.Method(
                    typeof(Plugin),
                    remove.Name == "remove_PlayerTalk"
                        ? nameof(RemoveTalkListener)
                        : nameof(RemoveBulletListener)
                );
            }
            yield return ins;
        }
        if (getters != 1 || removals != 1)
            throw new InvalidOperationException("Unknown SAIN hearing unsubscribe sequence");
    }
}
