using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using Comet.Core;
using EFT;
using HarmonyLib;
using SAIN.Components;
using SAIN.Components.BotComponentSpace.Classes.EnemyClasses;
using SAIN.SAINComponent.Classes.EnemyClasses;
using UnityEngine;

namespace Comet.Client;

internal sealed class BlastRuntime
{
    internal float NextLog;
}

internal sealed class BlastSignal
{
    internal Player? Player;
    internal Vector3 Position;
    internal float Range;
}

public sealed partial class Plugin
{
    private BlastSignal? blastCall;
    private bool blastWarned;

    private static void PatchExplosionAwareness(Harmony h)
    {
        h.Patch(
            AccessTools.Method(typeof(GrenadeController), "RegisterGrenadeExplosionForSAINBots"),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(BlastBegin)),
            transpiler: new HarmonyMethod(typeof(Plugin), nameof(BlastReportTranspiler)),
            finalizer: new HarmonyMethod(typeof(Plugin), nameof(BlastFinally))
        );
        // Observe the actual native report, after dispersion and contact update. Do not suppress shared hearing.
        h.Patch(
            AccessTools.Method(typeof(EnemyHearing), nameof(EnemyHearing.SetHeard)),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(BlastHeard))
        );
    }

    private bool BlastScope(BotComponent b) =>
        world != null
        && config?.Enabled == true
        && config.ExplosionAwareness.Enabled
        && Authority
        && NativeAdapter.IsSupportedAi(b)
        && b.BotActive
        && !b.IsDead
        && !b.GameEnding
        && !Ghost(b.ProfileId);

    private static void BlastBegin(
        Vector3 explosionPosition,
        Player player,
        float range,
        out BlastSignal? __state
    )
    {
        var p = Instance;
        __state = p?.blastCall;
        if (p != null)
            p.blastCall = new BlastSignal
            {
                Player = player,
                Position = explosionPosition,
                Range = range,
            };
    }

    private static Exception? BlastFinally(BlastSignal? __state, Exception? __exception)
    {
        if (Instance != null)
            Instance.blastCall = __state;
        return __exception;
    }

    private static IEnumerable<CodeInstruction> BlastReportTranspiler(
        IEnumerable<CodeInstruction> instructions
    )
    {
        var original = AccessTools.Method(
            typeof(SAINEnemyController),
            nameof(SAINEnemyController.GetEnemy),
            new[] { typeof(string), typeof(bool) }
        );
        int count = 0;
        foreach (var code in instructions)
        {
            if (code.Calls(original))
            {
                count++;
                code.opcode = OpCodes.Call;
                code.operand = AccessTools.Method(typeof(Plugin), nameof(BlastNativeEnemy));
            }
            yield return code;
        }
        if (count != 1)
            throw new InvalidOperationException("G10 native explosion report changed.");
    }

    private static Enemy? BlastNativeEnemy(SAINEnemyController controller, string id, bool active)
    {
        var enemy = controller.GetEnemy(id, active);
        var p = Instance;
        var call = p?.blastCall;
        if (enemy != null || p == null || call?.Player == null || !p.BlastScope(controller.Bot))
            return enemy;
        var b = controller.Bot;
        var player = call.Player;
        if (
            Vector3.Distance(b.Position, call.Position) >= p.config!.ExplosionAwareness.NearDistance
            || b.Squad?.Members?.ContainsKey(player.ProfileId) == true
        )
            return null;
        try
        {
            enemy = controller.CheckAddEnemy(player);
            var group = b.BotOwner.BotsGroup;
            bool listed = false;
            if (group != null)
                foreach (var entry in group.Enemies.Keys)
                    if (entry.ProfileId == player.ProfileId)
                    {
                        listed = true;
                        break;
                    }
            if (enemy == null && group != null && !listed && group.IsPlayerEnemy(player))
            {
                group.AddEnemy(player, EBotEnemyCause.checkAddTODO);
                enemy = controller.CheckAddEnemy(player);
            }
            return enemy; // Native SAIN applies source dispersion distance/10, hearing and squad reporting.
        }
        catch (Exception ex)
        {
            p.BlastWarning(ex.Message);
            return enemy;
        }
    }

    private static void BlastHeard(
        EnemyHearing __instance,
        SAIN.Models.Structs.SAINHearingReport report
    )
    {
        var p = Instance;
        if (
            p?.blastCall == null
            || report.soundType != SAIN.Preset.Shared.Enums.SAINSoundType.GrenadeExplosion
        )
            return;
        try
        {
            var b = __instance.Bot;
            if (!p.BlastScope(b))
                return;
            var s = p.GrenadeState(b);
            var call = p.blastCall;
            p.RecordBlast(
                s,
                new ExplosionTrace
                {
                    State = "heard",
                    Reason = "explosion-source-owner-heard",
                    Evidence = "zzap-owner-position-with-dispersion",
                    Target = call.Player?.ProfileId ?? "",
                    X = call.Position.x,
                    Y = call.Position.y,
                    Z = call.Position.z,
                    ContactX = report.position.x,
                    ContactY = report.position.y,
                    ContactZ = report.position.z,
                    Distance = Vector3.Distance(b.Position, call.Position),
                    Audible = true,
                    Applied = true,
                },
                false
            );
        }
        catch (Exception ex)
        {
            p.BlastWarning(ex.Message);
        }
    }

    private void UpdateBlasts() { }

    private void LateBlasts() { }

    private void StopBlast(BotState s, string reason) { }

    private void ClearBlasts()
    {
        blastCall = null;
        blastWarned = false;
    }

    private void BlastWarning(string message)
    {
        if (!blastWarned)
        {
            blastWarned = true;
            Logger.LogWarning("COMET G10: " + message);
        }
    }

    private void RecordBlast(BotState s, ExplosionTrace t, bool force)
    {
        if (!Comet.Core.ReplayCapture.Detailed)
            return;
        if (!force && Time.time < s.Blast.NextLog)
            return;
        s.Blast.NextLog = Time.time + .5f;
        var json = Diagnostics.Serialize(
            new
            {
                schema = 1,
                session,
                sequence = ++sequence,
                feature = "G10",
                stage = "evaluated",
                decision = "ExplosionAwareness",
                reason = t.Reason,
                realtime = Time.realtimeSinceStartup,
                utc = DateTime.UtcNow.ToString("O"),
                configRevision = config?.Revision ?? 0,
                profileId = s.ProfileId,
                name = s.Name,
                botInstance = s.Id,
                explosionAwareness = t,
                explosionAwarenessConfig = config?.ExplosionAwareness,
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
