using System;
using System.Reflection;
using Comet.Core;
using EFT;
using HarmonyLib;
using Newtonsoft.Json;
using SAIN.Components;
using SAIN.Models.Enums;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.Decision;
using SAIN.SAINComponent.Classes.EnemyClasses;
using SAIN.SAINComponent.Classes.Mover;
using UnityEngine;

namespace Comet.Client;

internal sealed class ReadyStanceRuntime
{
    internal readonly ReadyStance Policy = new();
    internal float Next,
        LogAt,
        LastCombatLayer = -100;
    internal bool NativePatrol,
        NativeKnown,
        Fault;
    internal (
        string State,
        string Reason,
        string Evidence,
        string Contact,
        bool Requested,
        bool ObservedRaised
    ) Key;
}

public sealed partial class Plugin
{
    private static FieldInfo readyPatrol = null!;

    private static void PatchReadyStance(Harmony h)
    {
        readyPatrol = AccessTools.Field(typeof(MovementContext), "_isInPatrol")
            ?? throw new MissingFieldException("H05 native patrol posture");
        h.Patch(
            AccessTools.Method(typeof(BotDecisionManager), nameof(BotDecisionManager.ManualUpdate)),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(ReadyFight))
        );
        h.Patch(
            AccessTools.Method(typeof(SAINMoverClass), "WantsPatrolStance")
                ?? throw new MissingMethodException("H05 native patrol choice"),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(ReadyNativeWish))
        );
        h.Patch(
            AccessTools.Method(typeof(SAINMoverClass), nameof(SAINMoverClass.ManualUpdate)),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(ReadyAfterMover))
        );
    }

    private bool ReadyScope(BotComponent b) =>
        config?.Enabled == true
        && config.ReadyStance.Enabled
        && world != null
        && Authority
        && b != null
        && NativeAdapter.IsSupportedAi(b)
        && b.BotActive
        && !b.IsDead
        && !b.GameEnding
        && !Ghost(b.ProfileId);

    private string ReadyBlock(BotComponent b, BotState s) => ""; // Native CanSetPatrol retains the hands/sprint checks.

    private static void ReadyFight(BotDecisionManager __instance)
    {
        var p = Instance;
        var b = __instance.Bot;
        if (p == null || !p.ReadyScope(b))
            return;
        if (
            b.ActiveLayer != ESAINLayer.Combat
            && b.ActiveLayer != ESAINLayer.AvoidThreat
            && b.ActiveLayer != ESAINLayer.Flashed
        )
            return;
        if (!p.states.TryGetValue(b, out var s))
        {
            s = new BotState(b, p.config!, p.session);
            p.states.Add(b, s);
        }
        s.ReadyStance.LastCombatLayer = Time.time;
    }

    private static void ReadyNativeWish(SAINMoverClass __instance, ref bool __result)
    {
        var p = Instance;
        var b = __instance.Bot;
        if (p == null)
            return;
        try
        {
            if (!p.ReadyScope(b))
                return;
            if (!p.states.TryGetValue(b, out var s))
            {
                s = new BotState(b, p.config!, p.session);
                p.states.Add(b, s);
            }
            var r = s.ReadyStance;
            if (r.Fault)
                return;
            r.NativePatrol = __result;
            r.NativeKnown = true;
            r.Policy.ObserveCombatAge(
                Time.time,
                Time.time - r.LastCombatLayer,
                p.config!.ReadyStance
            );
            if (r.Policy.Evaluate(Time.time, __result, "", p.config.ReadyStance))
                __result = false;
            r.Next = 0;
        }
        catch (Exception ex)
        {
            if (p.states.TryGetValue(b, out var s))
                p.ReadyError(s, ex);
        }
    }

    private static void ReadyAfterMover(SAINMoverClass __instance)
    {
        var p = Instance;
        if (p != null && p.states.TryGetValue(__instance.Bot, out var s))
            p.SampleReadyStance(__instance.Bot, s);
    }

    private void SampleReadyStance(BotComponent b, BotState s)
    {
        var r = s.ReadyStance;
        if (r.Fault || r.Policy.Trace == null)
            return;
        try
        {
            if (!ReadyScope(b))
            {
                EndReadyStance(s, "ready-stance-out-of-scope");
                return;
            }
            string block = ReadyBlock(b, s);
            if (Time.time < r.Next && block.Length == 0 && r.Policy.Pending(Time.time))
                return;
            r.Next = Time.time + (float)config!.ReadyStance.Interval;
            r.Policy.Evaluate(
                Time.time,
                r.NativeKnown && r.NativePatrol,
                block,
                config.ReadyStance
            );
            var patrol = readyPatrol.GetValue(b.Player.MovementContext);
            if (patrol is not bool lowered)
                throw new InvalidOperationException("H05 native patrol state unavailable");
            r.Policy.ObservePosture(
                b.Player.HandsController is Player.FirearmController && !lowered
            );
            RecordReadyStance(s, false);
        }
        catch (Exception ex)
        {
            ReadyError(s, ex);
        }
    }

    private void EndReadyStance(BotState s, string reason)
    {
        var r = s.ReadyStance;
        if (r.Policy.Trace == null || r.Policy.Trace.State == "released")
            return;
        r.Policy.Clear(reason);
        r.NativeKnown = false;
        RecordReadyStance(s, true);
        // Release only our veto. The next native stance evaluation chooses its current posture.
    }

    private void ReadyError(BotState s, Exception ex)
    {
        if (s.ReadyStance.Fault)
            return;
        s.ReadyStance.Fault = true;
        EndReadyStance(s, "ready-stance-adapter-error");
        Logger.LogWarning("H05 yielded: " + ex.GetBaseException().Message);
    }

    private void RecordReadyStance(BotState s, bool force)
    {
        var r = s.ReadyStance;
        var t = r.Policy.Trace;
        if (t == null)
            return;
        var key = (t.State, t.Reason, t.Evidence, t.Contact, t.Requested, t.ObservedRaised);
        if (!force && key == r.Key && (!t.Requested || Time.time < r.LogAt))
            return;
        r.Key = key;
        r.LogAt = Time.time + .5f;
        var json = Diagnostics.Serialize(
            new
            {
                schema = 1,
                session,
                sequence = ++sequence,
                feature = "H05",
                stage = "evaluated",
                decision = "ReadyStance",
                reason = t.Reason,
                realtime = Time.realtimeSinceStartup,
                utc = DateTime.UtcNow.ToString("O"),
                configRevision = config?.Revision ?? 0,
                profileId = s.ProfileId,
                name = s.Name,
                botInstance = s.Id,
                readyStance = t,
                readyStanceConfig = config?.ReadyStance,
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
