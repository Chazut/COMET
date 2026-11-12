using System;
using System.Runtime.CompilerServices;
using Comet.Core;
using EFT;
using HarmonyLib;
using SAIN.Components;
using UnityEngine;
namespace Comet.Client;

public sealed partial class Plugin
{
    private readonly FearRaidMemory fearMemory = new();
    private sealed class FearDeath { public bool Recorded; }
    private ConditionalWeakTable<Player,FearDeath> fearDeaths=new();
    private static void PatchFear(Harmony h) => h.Patch(
        AccessTools.Method(typeof(Player),nameof(Player.OnDead)) ?? throw new MissingMethodException("Player.OnDead"),
        postfix:new HarmonyMethod(typeof(Plugin),nameof(FearDeathObserved)));
    private static void FearDeathObserved(Player __instance,IPlayer? ___LastAggressor)
    {
        var p=Instance;
        if(p?.config?.Enabled!=true || !p.config.Fear.Enabled || !p.Authority || p.world==null) return;
        if(__instance.HealthController?.IsAlive!=false || ReadDowned(__instance)) return;
        var seen=p.fearDeaths.GetOrCreateValue(__instance);if(seen.Recorded)return;seen.Recorded=true;
        var group=__instance.AIData?.BotOwner?.BotsGroup;
        if(group!=null)p.fearMemory.Death(RuntimeHelpers.GetHashCode(group),DP(__instance.Position),Time.time);
        if(___LastAggressor!=null && !___LastAggressor.IsAI)p.fearMemory.PlayerKill(Time.time);
    }
    private void RefreshFear(BotComponent bot,BotState state)
    {
        if(config==null)return;
        if(!config.Enabled || !config.Fear.Enabled || !NativeAdapter.IsSupportedAi(bot) || !bot.BotActive || !Authority || Ghost(state.ProfileId))
        {state.Fear.Pause(Time.time);return;}
        try
        {
            var goal=bot.GoalEnemy;
            var group=bot.BotOwner.BotsGroup;
            var input=new FearInput { Now=Time.time,Personality=bot.Info.Personality.ToString(),
                Health=bot.Memory.Health.HealthStatus switch{ETagStatus.Healthy=>1,ETagStatus.Injured=>.7,ETagStatus.BadlyInjured=>.35,_=>.1},
                HeavySuppression=bot.Suppression.IsHeavySuppressed,
                NearbyDeathAt=group==null?null:fearMemory.NearbyDeath(RuntimeHelpers.GetHashCode(group),DP(bot.Position),Time.time),
                PlayerStreak=fearMemory.Streak(goal!=null&&!goal.IsAI,Time.time)};
            if(goal!=null)
            {
                foreach(var e in bot.EnemyController.KnownEnemies)
                    if(e!=null&&!ReferenceEquals(e,goal)&&e.TimeSinceLastKnownUpdated<20
                        &&(e.EnemyPosition-goal.EnemyPosition).sqrMagnitude<400)input.KnownEnemies++;
                foreach(var mate in bot.Squad.Members.Values)
                    if(mate!=null&&mate!=bot&&!mate.IsDead&&mate.GoalEnemy?.EnemyProfileId==goal.EnemyProfileId
                        &&(mate.Position-bot.Position).sqrMagnitude<=900)input.AvailableAllies++;
            }
            state.Fear.Update(input);
        }
        catch(Exception ex){state.Fear.Pause(Time.time);Logger.LogWarning("COMET fear read failed: "+ex.Message);}
    }
}
