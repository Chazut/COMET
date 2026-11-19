using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Comet.Core;
using EFT;
using HarmonyLib;
using Newtonsoft.Json;
using SAIN.Classes;
using SAIN.Components.PlayerComponentSpace;
using UnityEngine;

namespace Comet.Client;

// Explicit fail-closed guards on known supported-bot calls. C08's normal prefix fallback (true)
// would execute the invalid native method, so this file is not rewritten by C08.
public sealed partial class Plugin
{
    private sealed class ValidityOwner
    {
        internal WeakReference<Player> Player = null!;
        internal string Profile = "",
            Name = "",
            Instance = "c10-" + Guid.NewGuid().ToString("N");
        internal readonly Dictionary<string, ValidityCount> Counts = new();
    }

    private sealed class ValidityCount
    {
        internal ValidityTrace Trace = new();
        internal int Published;
        internal double Next;
    }

    private sealed class ValidityContacts
    {
        internal readonly ConditionalWeakTable<PlayerComponent, object> Retired = new();
    }

    private ConditionalWeakTable<object, ValidityOwner> validityOwners = new();
    private ConditionalWeakTable<OtherPlayersData, ValidityContacts> validityContacts = new();
    private ConditionalWeakTable<PlayerComponent, WeakReference<Player>> validitySources = new();

    // Owners contain only metadata and weak Unity references; keep counters until raid-end.
    private readonly List<ValidityOwner> validityLogs = new();

    private static void PatchValidity(Harmony h)
    {
        h.Patch(
            AccessTools.Method(
                typeof(PlayerMovementController),
                nameof(PlayerMovementController.SetTargetMoveDirection)
            ) ?? throw new MissingMethodException("C10 SetTargetMoveDirection"),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(ValidityMove))
            {
                priority = Priority.First,
            }
        );
        h.Patch(
            AccessTools.Method(
                typeof(OtherPlayersData),
                nameof(OtherPlayersData.GetOrAddData),
                new[] { typeof(PlayerComponent) }
            ) ?? throw new MissingMethodException("C10 GetOrAddData"),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(ValidityAdd))
            {
                priority = Priority.First,
            },
            postfix: new HarmonyMethod(typeof(Plugin), nameof(ValidityAdded))
        );
        h.Patch(
            AccessTools.Method(
                typeof(OtherPlayersData),
                "PlayerRemoved",
                new[] { typeof(string), typeof(PlayerComponent) }
            ) ?? throw new MissingMethodException("C10 PlayerRemoved"),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(ValidityRemove))
            {
                priority = Priority.First,
            }
        );
        h.Patch(
            AccessTools.Method(
                typeof(BotMemory),
                nameof(BotMemory.SetUnderFire),
                new[] { typeof(IPlayer) }
            ) ?? throw new MissingMethodException("C10 SetUnderFire"),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(ValidityUnderFire))
            {
                priority = Priority.First,
            }
        );
    }

    private bool ValidityScope(object? key, Player? player, out ValidityOwner? owner)
    {
        owner = null;
        if (key == null || config?.Enabled != true || !Authority)
            return false;
        if (validityOwners.TryGetValue(key, out owner))
            return true;
        if (player == null || !BotEligibility.Supports(player))
            return false;
        owner = new()
        {
            Player = new(player),
            Profile = player.ProfileId,
            Name = player.Profile?.Nickname ?? "PMC",
        };
        validityOwners.Add(key, owner);
        validityLogs.Add(owner);
        return true;
    }

    private static string ValidityBody(Player? player)
    {
        if (player == null)
            return "player-destroyed";
        if (player.Transform?.Original == null)
            return "transform-destroyed";
        if (player.PlayerBones == null)
            return "bones-unavailable";
        return "";
    }

    private static string ValidityIdentity(ValidityOwner owner, Player? player)
    {
        if (!owner.Player.TryGetTarget(out var original) || !ReferenceEquals(original, player))
            return "owner-replaced";
        string body = ValidityBody(player);
        if (body.Length > 0)
            return body;
        return player!.ProfileId == owner.Profile ? "" : "owner-profile-changed";
    }

    private static bool ValidityMove(PlayerComponent playerComp)
    {
        var p = Instance;
        ValidityOwner? owner = null;
        try
        {
            if (p == null || !p.ValidityScope(playerComp, playerComp?.Player, out owner))
                return true;
            var player = playerComp!.Player;
            string reason =
                playerComp == null ? "component-destroyed" : ValidityIdentity(owner!, player);
            if (reason.Length == 0 && ReadDowned(player))
                reason = "owner-downed";
            if (reason.Length == 0 && player.HealthController?.IsAlive != true)
                reason = "owner-not-alive";
            if (reason.Length == 0 && playerComp!.BotComponent?.IsDead == true)
                reason = "owner-dead";
            if (
                reason.Length == 0
                && (
                    player.MovementContext == null
                    || player.CharacterController == null
                    || playerComp!.BotOwner == null
                )
            )
                reason = "movement-unavailable";
            if (reason.Length == 0)
                return true;
            p.RecordValidity(owner!, "movement", reason, playerComp);
            return false;
        }
        catch (Exception)
        {
            if (owner == null)
                return true;
            p!.RecordValidity(owner, "movement", "state-unavailable", null);
            return false;
        }
    }

    private static bool ValidityAdd(
        OtherPlayersData __instance,
        PlayerComponent __0,
        ref OtherPlayerData __result,
        out PlayerComponent? __state
    )
    {
        __state = null;
        var p = Instance;
        ValidityOwner? owner = null;
        try
        {
            if (p == null || !p.ValidityScope(__instance, __instance.Player, out owner))
                return true;
            string reason = ValidityIdentity(owner!, __instance.Player);
            if (reason.Length == 0 && __0 == null)
                reason = "source-component-destroyed";
            if (reason.Length == 0)
                reason = ValidityBody(__0!.Player);
            var contacts = p.validityContacts.GetValue(__instance, _ => new());
            if (reason.Length == 0 && contacts.Retired.TryGetValue(__0!, out _))
                reason = "source-retired";
            if (
                reason.Length == 0
                && p.validitySources.TryGetValue(__0!, out var saved)
                && (
                    !saved.TryGetTarget(out var original) || !ReferenceEquals(original, __0!.Player)
                )
            )
                reason = "source-replaced";
            if (reason.Length > 0)
            {
                __result = null!;
                p.RecordValidity(owner!, "register-player", reason, __0);
                return false;
            }
            if (!p.validitySources.TryGetValue(__0!, out _))
                p.validitySources.Add(__0!, new(__0!.Player));
            if (
                !string.IsNullOrEmpty(__0!.ProfileId)
                && __instance.DataDictionary.TryGetValue(__0.ProfileId, out var existing)
                && !ReferenceEquals(existing.OtherPlayerComponent, __0)
            )
                __state = existing.OtherPlayerComponent;
            return true;
        }
        catch (Exception)
        {
            if (owner == null)
                return true;
            __result = null!;
            p!.RecordValidity(owner, "register-player", "state-unavailable", null);
            return false;
        }
    }

    private static void ValidityAdded(
        OtherPlayersData __instance,
        PlayerComponent __0,
        OtherPlayerData __result,
        PlayerComponent? __state
    )
    {
        try
        {
            var p = Instance;
            if (
                p == null
                || ReferenceEquals(__state, null)
                || __result == null
                || !ReferenceEquals(__result.OtherPlayerComponent, __0)
                || !p.ValidityScope(__instance, __instance.Player, out _)
            )
                return;
            // Retire only after the replacement was actually installed by SAIN.
            if (
                __instance.DataDictionary.TryGetValue(__0.ProfileId, out var now)
                && ReferenceEquals(now, __result)
            )
                p.validityContacts.GetValue(__instance, _ => new())
                    .Retired.GetValue(__state!, _ => new object());
        }
        catch
        { /* Observation cannot change a successful native registration. */
        }
    }

    private static bool ValidityRemove(OtherPlayersData __instance, string __0, PlayerComponent __1)
    {
        var p = Instance;
        ValidityOwner? owner = null;
        try
        {
            if (p == null || !p.ValidityScope(__instance, __instance.Player, out owner))
                return true;
            if (
                !string.IsNullOrEmpty(__0)
                && __instance.DataDictionary.TryGetValue(__0, out var current)
                && !ReferenceEquals(current.OtherPlayerComponent, __1)
            )
            {
                p.RecordValidity(owner!, "remove-player", "stale-removal", __1);
                return false;
            }
            return true; // Correct removal must still run, including for a destroyed source.
        }
        catch (Exception)
        {
            if (owner == null)
                return true;
            p!.RecordValidity(owner, "remove-player", "state-unavailable", null);
            return false;
        }
    }

    private static bool ValidityUnderFire(BotMemory __instance, IPlayer __0)
    {
        var p = Instance;
        ValidityOwner? owner = null;
        try
        {
            if (p == null || !p.ValidityScope(__instance, __instance._owner?.GetPlayer, out owner))
                return true;
            // Never test source health: bullets/grenades from a dead but valid player still count.
            // Other IPlayer implementations retain the original game behavior.
            string ownerReason = ValidityIdentity(owner!, __instance._owner?.GetPlayer);
            if (ownerReason.Length > 0)
            {
                p.RecordValidity(owner!, "under-fire", ownerReason, null);
                return false;
            }
            if (__0 is not Player source)
                return true;
            string reason =
                source == null ? "source-destroyed"
                : source.Transform?.Original == null ? "source-transform-destroyed"
                : "";
            if (reason.Length == 0)
                return true;
            p.RecordValidity(owner!, "under-fire", reason, null, source);
            return false;
        }
        catch (Exception)
        {
            if (owner == null)
                return true;
            p!.RecordValidity(owner, "under-fire", "state-unavailable", null);
            return false;
        }
    }

    private void RecordValidity(
        ValidityOwner owner,
        string operation,
        string reason,
        PlayerComponent? source,
        Player? player = null
    )
    {
        try
        {
            string key = operation + ":" + reason;
            if (!owner.Counts.TryGetValue(key, out var count))
            {
                if (owner.Counts.Count >= 32)
                    return;
                owner.Counts.Add(
                    key,
                    count = new()
                    {
                        Trace = new() { Operation = operation, Reason = reason },
                    }
                );
            }
            count.Trace.Blocked = Math.Min(int.MaxValue - 1, count.Trace.Blocked) + 1;
            // Metadata only, no destroyed object's position and no strong source references.
            count.Trace.Source = "";
            count.Trace.SourceInstance = "";
            if (!ReferenceEquals(source, null))
            {
                count.Trace.SourceInstance = RuntimeHelpers.GetHashCode(source).ToString();
                try
                {
                    count.Trace.Source = ValidityText(source.ProfileId);
                }
                catch { }
            }
            else if (!ReferenceEquals(player, null))
            {
                count.Trace.SourceInstance = RuntimeHelpers.GetHashCode(player).ToString();
                try
                {
                    count.Trace.Source = ValidityText(player.ProfileId);
                }
                catch { }
            }
            if (count.Published == 0 || Time.unscaledTimeAsDouble >= count.Next)
                PublishValidity(owner, count, false);
        }
        catch (Exception ex)
        {
            WarnFaultSink(ex);
        }
    }

    private void PublishValidity(ValidityOwner owner, ValidityCount count, bool final)
    {
        count.Trace.Summary = final;
        count.Trace.ValidFor = final ? 0 : 2;
        count.Published = count.Trace.Blocked;
        count.Next = Time.unscaledTimeAsDouble + 5;
        if (owner.Player.TryGetTarget(out var player) && player != null)
        {
            var b = player.GetComponent<SAIN.Components.BotComponent>();
            if (b != null && states.TryGetValue(b, out var s))
                owner.Instance = s.Id;
        }
        var json = Diagnostics.Serialize(
            new
            {
                schema = 1,
                session,
                sequence = ++sequence,
                feature = "C10",
                stage = "evaluated",
                decision = "Validity",
                reason = count.Trace.Reason,
                realtime = Time.realtimeSinceStartup,
                utc = DateTime.UtcNow.ToString("O"),
                configRevision = config?.Revision ?? 0,
                profileId = owner.Profile,
                name = owner.Name,
                botInstance = owner.Instance,
                validity = count.Trace,
            }
        );
        if (final)
            diagnostics?.WriteEssential(json);
        else
            diagnostics?.Write(json);
        Api.Decisions.Publish(json, WarnFaultSink);
    }

    private void ClearValidity()
    {
        foreach (var owner in validityLogs)
        foreach (var count in owner.Counts.Values)
            if (count.Published < count.Trace.Blocked)
                try
                {
                    PublishValidity(owner, count, true);
                }
                catch (Exception ex)
                {
                    WarnFaultSink(ex);
                }
        validityOwners = new();
        validityContacts = new();
        validitySources = new();
        validityLogs.Clear();
    }

    private static string ValidityText(string? text) =>
        string.IsNullOrEmpty(text) ? ""
        : text!.Length <= 512 ? text
        : text.Substring(0, 512);
}
