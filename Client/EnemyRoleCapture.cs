using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx.Bootstrap;
using Comet.Core;
using Comfort.Common;
using EFT;
using SAIN.Components;
using SAIN.Preset;
using SAIN.SAINComponent.Classes.EnemyClasses;

namespace Comet.Client;

internal static class EnemyRoleCapture
{
    private sealed class Identification
    {
        internal readonly EnemyIdentity Identity;
        internal Identification(Player player)
        {
            var settings = player.Profile?.Info?.Settings;
            if (settings == null) return;
            var role = settings.Role;
            BotTypeDefinitions.BotTypes.TryGetValue(role, out var definition);
            Identity = EnemyIdentity.Identify(role.ToString(), definition?.Section, FactionsFor(role));
        }
    }

    private static readonly ConditionalWeakTable<Player, Identification> identifications = new();
    private static FieldInfo? factionIndex;
    private static Type? managerType;
    private static bool bridgeChecked;

    // MoreBotsAPI attaches the manager to its plugin. Read its already-loaded raid index,
    // never its server endpoint or another bot's live group. No hard dependency for solo installs.
    private static IReadOnlyList<string>? FactionsFor(WildSpawnType role)
    {
        if (!Chainloader.PluginInfos.TryGetValue("com.morebotsapi.tacticaltoaster", out var plugin) || plugin.Instance == null) return null;
        if (!bridgeChecked)
        {
            managerType = plugin.Instance.GetType().Assembly.GetType("MoreBotsAPI.Components.FactionManager");
            factionIndex = managerType?.GetField("WildSpawnTypeFactionDict");
            bridgeChecked = true;
        }
        if (factionIndex == null || managerType == null) return null;
        var manager = plugin.Instance.GetComponent(managerType);
        return manager != null && factionIndex.GetValue(manager) is IDictionary<WildSpawnType, List<string>> byRole
            && byRole.TryGetValue(role, out var factions) ? factions : null;
    }

    internal static void Observe(Enemy enemy, Situation s, BotState state)
    {
        if (state.BotKind != "pmc" || !s.HasContact) return;
        var seen = enemy.KnownPlaces.LastSeenPlace;
        var shared = enemy.KnownPlaces.LastSquadSeenPlace;
        var sight = seen;
        if (shared != null && (sight == null || shared.TimeSincePositionUpdated < sight.TimeSincePositionUpdated)) sight = shared;
        if ((enemy.IsVisible || enemy.Seen || sight != null) && enemy.IsAI && enemy.EnemyPlayer is Player player)
        {
            s.RecognizedEnemy = identifications.GetValue(player, p => new Identification(p)).Identity;
            if (enemy.IsVisible || sight != null)
            {
                var position = enemy.IsVisible ? enemy.EnemyPosition : sight!.Position;
                double at = enemy.IsVisible ? s.Now : s.Now - sight!.TimeSincePositionUpdated;
                state.EscortSighting.Observe(s.RecognizedEnemy, new DoorPoint(position.x, position.y, position.z), at);
            }
        }
        s.LocalGroupCue = state.EscortSighting.Near(new DoorPoint(s.KnownX, s.KnownY, s.KnownZ), s.Now);
    }

    internal static double? RaidProgress()
    {
        var timer = Singleton<AbstractGame>.Instance?.GameTimer;
        if (timer?.SessionTime is not System.TimeSpan duration || duration.TotalSeconds <= 0) return null;
        return timer.PastTime.TotalSeconds / duration.TotalSeconds;
    }
}

public sealed partial class Plugin
{
    private double AdjustStormChance(BotComponent bot, Enemy enemy, BotState state, double chance)
    {
        var s = state.Situation;
        if (chance <= 0 || s == null || s.Target != enemy.EnemyProfileId || !(s.RecognizedEnemy.ExpectsGroup || s.LocalGroupCue.ExpectsGroup)) return chance;
        ZzapUtilityCapture.FillNumbers(bot, enemy, s);
        double aggression = config!.Aggression.TryGetValue(s.Personality, out var value) ? value : .5;
        return s.EnemyRole!.StormChance(chance, aggression);
    }
}
