using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using EFT.Interactive;
using HarmonyLib;
using SAIN.Components;
using SAIN.Extensions;
using SAIN.Layers.Combat.Solo;
using SAIN.Models.PlayerData;
using SAIN.SAINComponent.Classes.EnemyClasses;
using SAIN.SAINComponent.Classes.Mover;
using SAIN.SAINComponent.SubComponents.CoverFinder;
using UnityEngine;
using UnityEngine.AI;

namespace Comet.Client;

public sealed partial class Plugin
{
    private sealed class PathOwner
    {
        internal readonly BotComponent Bot;
        internal bool OffEdge;

        internal PathOwner(BotComponent bot)
        {
            Bot = bot;
        }
    }

    private ConditionalWeakTable<PlayerNavData, PathOwner> pathOwners = new();
    private readonly Dictionary<int, bool> doorCoverParts = new();

    partial void ClearRemixRaidState()
    {
        doorCoverParts.Clear();
        pathOwners = new();
        remixBots = new();
    }

    private static void PatchRemixNavigation(Harmony h)
    {
        h.Patch(
            AccessTools.Method(
                typeof(PlayerDataExtensions),
                "Update",
                new[] { typeof(PlayerNavData), typeof(Vector3), typeof(bool) }
            ),
            transpiler: new HarmonyMethod(typeof(Plugin), nameof(RemixPathInstructions))
        );
        h.Patch(
            AccessTools.Method(typeof(SAINEnemyPath), "CheckCalcPath"),
            transpiler: new HarmonyMethod(typeof(Plugin), nameof(RemixPathInstructions))
        );
        h.Patch(
            AccessTools.Method(typeof(BotPathDataManual), "CheckCornerComplete"),
            transpiler: new HarmonyMethod(typeof(Plugin), nameof(RemixPathInstructions))
        );
        foreach (
            var name in new[] { "CanGoToPoint", "CanGoToCoverPoint", "TryRecalcCoverPointPath" }
        )
            h.Patch(
                AccessTools.Method(typeof(SAINMoverClass), name),
                transpiler: new HarmonyMethod(typeof(Plugin), nameof(RemixPathInstructions))
            );
        h.Patch(
            AccessTools.Method(typeof(StandAndShootAction), "FindSwingMovePosition"),
            transpiler: new HarmonyMethod(typeof(Plugin), nameof(RemixPathInstructions))
        );
        h.Patch(
            AccessTools.Method(typeof(DogFight), "findStrafePoint2"),
            transpiler: new HarmonyMethod(typeof(Plugin), nameof(RemixPathInstructions))
        );
        foreach (var name in new[] { "CheckCreateNewCoverPoint", "RecheckCoverPoint" })
            h.Patch(
                AccessTools.Method(typeof(CoverAnalyzer), name),
                prefix: new HarmonyMethod(typeof(Plugin), nameof(RemixCoverPathOrigin))
            );
        h.Patch(
            AccessTools.Method(typeof(CoverAnalyzer), "CheckColliderForCover"),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(RemixDoorCover))
        );
    }

    // Keep the native position for directions, distances, arrival and targets. Only
    // the ray/path origin (the middle of the three swing-position reads) changes.
    private static IEnumerable<CodeInstruction> RemixPathInstructions(
        IEnumerable<CodeInstruction> instructions,
        MethodBase __originalMethod
    )
    {
        int matched = 0,
            reads = 0;
        bool sample = __originalMethod.DeclaringType == typeof(PlayerDataExtensions);
        bool swing = __originalMethod.Name == "FindSwingMovePosition";
        bool strafe = __originalMethod.DeclaringType == typeof(DogFight);
        foreach (var ins in instructions)
        {
            string? replacement = null;
            if (
                ins.operand is MethodInfo method
                && (ins.opcode == OpCodes.Call || ins.opcode == OpCodes.Callvirt)
            )
            {
                if (method.DeclaringType == typeof(PlayerNavData) && method.Name == "set_Position")
                    replacement = nameof(StoreRemixNavSample);
                else if (
                    !sample
                    && !strafe
                    && method.DeclaringType == typeof(PlayerNavData)
                    && method.Name == "get_Position"
                )
                {
                    reads++;
                    if (!swing || reads == 2)
                        replacement = nameof(RemixDataOrigin);
                }
                else if (
                    method.DeclaringType == typeof(BotComponent)
                    && method.Name == "get_NavMeshPosition"
                )
                    replacement = nameof(RemixBotOrigin);
                else if (strafe && method.DeclaringType == typeof(NavMesh))
                {
                    if (method.Name == "Raycast")
                        replacement = nameof(RemixStrafeRay);
                    if (method.Name == "CalculatePath")
                        replacement = nameof(RemixStrafePath);
                }
            }
            if (replacement == null)
            {
                yield return ins;
                continue;
            }
            var call = AccessTools.Method(typeof(Plugin), replacement);
            matched++;
            if (strafe)
            {
                ins.opcode = OpCodes.Ldarg_0;
                ins.operand = null;
                yield return ins;
                yield return new CodeInstruction(OpCodes.Call, call);
            }
            else
            {
                ins.opcode = OpCodes.Call;
                ins.operand = call;
                yield return ins;
            }
        }
        if (matched != (strafe ? 2 : 1) || swing && reads != 3)
            throw new InvalidOperationException(
                "Unexpected SAIN path origins in " + __originalMethod
            );
    }

    private void RegisterPathOwner(BotComponent bot)
    {
        var data = bot.Transform.NavData;
        var owner = new PathOwner(bot);
        pathOwners.Remove(data);
        pathOwners.Add(data, owner);
        // A bot can already have a valid sample when COMET receives its raid config.
        UpdateRemixEdge(owner, data.LastValidHit.position, data.Position);
    }

    private static void StoreRemixNavSample(PlayerNavData data, Vector3 projected)
    {
        data.Position = projected;
        var p = Instance;
        if (p == null || !p.pathOwners.TryGetValue(data, out var owner))
            return;
        p.UpdateRemixEdge(owner, data.LastValidHit.position, projected);
    }

    private void UpdateRemixEdge(PathOwner owner, Vector3 sampled, Vector3 projected)
    {
        var delta = sampled - projected;
        float threshold = owner.OffEdge ? .25f : .35f;
        bool offEdge = delta.x * delta.x + delta.z * delta.z > threshold * threshold;
        if (offEdge && !owner.OffEdge)
            CountNativeFix(NativeFix.OffMeshStart, owner.Bot);
        owner.OffEdge = offEdge;
    }

    private static Vector3 RemixDataOrigin(PlayerNavData data)
    {
        var p = Instance;
        return
            p != null
            && p.pathOwners.TryGetValue(data, out var owner)
            && owner.OffEdge
            && p.NativeFixScope(owner.Bot)
            ? data.LastValidHit.position
            : data.Position;
    }

    private static Vector3 RemixBotOrigin(BotComponent bot) =>
        RemixDataOrigin(bot.Transform.NavData);

    internal static Vector3 RemixMovementOrigin(
        BotComponent bot,
        Vector3 original,
        bool preserveOnMesh = false
    )
    {
        var p = Instance;
        var data = bot.Transform?.NavData;
        if (p == null || data == null || !p.pathOwners.TryGetValue(data, out var owner))
            return original;
        if ((preserveOnMesh && !owner.OffEdge) || !p.NativeFixScope(bot))
            return original;
        return owner.OffEdge ? data.LastValidHit.position : data.Position;
    }

    private static bool RemixStrafeRay(
        Vector3 start,
        Vector3 end,
        out NavMeshHit hit,
        int mask,
        DogFight strafe
    ) => NavMesh.Raycast(RemixMovementOrigin(strafe.Bot, start), end, out hit, mask);

    private static bool RemixStrafePath(
        Vector3 start,
        Vector3 end,
        int mask,
        NavMeshPath path,
        DogFight strafe
    ) => NavMesh.CalculatePath(RemixMovementOrigin(strafe.Bot, start), end, mask, path);

    private static void RemixCoverPathOrigin(CoverAnalyzer __instance, ref Vector3 botPosition) =>
        botPosition = RemixMovementOrigin(__instance.Bot, botPosition);

    private static bool RemixDoorCover(
        CoverAnalyzer __instance,
        Collider collider,
        ref string reason,
        ref NavMeshHit navMeshHit,
        ref bool __result
    )
    {
        var p = Instance;
        if (p == null || !p.NativeFixScope(__instance.Bot) || collider == null)
            return true;
        if (!p.IsDoorCover(collider))
            return true;
        reason = "door-leaf-or-frame";
        navMeshHit = default;
        __result = false;
        p.CountNativeFix(NativeFix.DoorCoverRejected, __instance.Bot);
        return false;
    }

    private bool IsDoorCover(Collider? collider)
    {
        if (collider == null)
            return false;
        int id = collider.GetInstanceID();
        if (doorCoverParts.TryGetValue(id, out bool doorPart))
            return doorPart;
        return doorCoverParts[id] = IsRemixDoorPart(collider);
    }

    // Bots already active when the raid configuration arrives can have native points in use.
    private void RemoveCachedDoorCover(BotComponent bot)
    {
        var cover = bot.Cover;
        if (cover == null)
            return;
        var points = cover.CoverPoints;
        for (int i = points.Count - 1; i >= 0; i--)
            if (IsDoorCover(points[i]?.Collider))
                points.RemoveAt(i);
        if (cover.CoverInUse is { } held && IsDoorCover(held.Collider))
            SourceCoverAccess.Forget(cover, held);
        if (cover.CoverPoint_MovingTo is { } moving && IsDoorCover(moving.Collider))
            SourceCoverAccess.Forget(cover, moving);
    }

    private static bool IsRemixDoorPart(Collider collider)
    {
        if (collider.GetComponentInParent<Door>() != null)
            return true;
        var root = collider.transform.parent;
        if (root == null)
            return false;
        var center = collider.bounds.center;
        for (int i = 0; i < root.childCount; i++)
        {
            var door = root.GetChild(i).GetComponent<Door>();
            if (door != null && (door.transform.position - center).sqrMagnitude < 1.5f * 1.5f)
                return true;
        }
        return false;
    }
}
