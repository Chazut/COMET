using System;
using Comet.Core;
using EFT;
using EFT.Ballistics;
using HarmonyLib;
using Squad=SAIN.BotController.Classes.Squad;
using SAIN.Components;
using UnityEngine;
namespace Comet.Client;

public sealed partial class Plugin
{
    private static void PatchSquadTrade(Harmony h)
    {
        var method=AccessTools.Method(typeof(Squad),"memberWasKilled",new[]{typeof(Player),typeof(IPlayer),typeof(DamageInfo),typeof(EBodyPart)});
        if(method==null)throw new MissingMethodException("S05 confirmed squad death event");
        h.Patch(method,prefix:new HarmonyMethod(typeof(Plugin),nameof(ObserveTradeDeath)));
    }
    private void TradeWarning(Exception ex)=>Logger.LogWarning("S05 death observation yielded: "+ex.GetBaseException().Message);
    private static bool TradeDead(Player player)
    {
        if(player==null||!player.IsAI||player.HealthController==null||player.HealthController.IsAlive)return false;
        // Optional Fika contract. Never interpret a downed player as a kill, or guess after an API error.
        try{return player.GetType().GetProperty("Downed",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic)?.GetValue(player) is not true;}
        catch{return false;}
    }
    private static int TradeGroup(BotComponent b)=>System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(b.Squad.Members);
    private static void ObserveTradeDeath(Squad __instance,Player __0,IPlayer __1)
    {
        var p=Instance;if(p?.config?.Enabled!=true||!p.config.SquadTrade.Enabled||!p.Authority||p.world==null||!TradeDead(__0)||p.Ghost(__0.ProfileId))return;
        if(__instance.Members.Count>16)return;
        foreach(var b in __instance.Members.Values)
        {
            try
            {
                if(b==null||b.ProfileId==__0.ProfileId||!p.CrossfireScope(b)||!Witnesses(b,__0.Position+Vector3.up,40))continue;
                if(!p.states.TryGetValue(b,out var s))
                {if(p.states.Count>=512)continue;s=new BotState(b,p.config,p.session);p.states.Add(b,s);}
                string associated="";int scanned=0;
                foreach(var e in b.EnemyController.KnownEnemies)
                {
                    if(++scanned>16)break;
                    // The event's aggressor identity is only useful if this observer already sees that enemy.
                    // No killer transform is read, nor any contact inserted into SAIN's knowledge.
                    if(e!=null&&e.EnemyKnown&&e.IsVisible&&e.EnemyProfileId==__1?.ProfileId){associated=e.EnemyProfileId;break;}
                }
                var r=s.Crossfire.Trade;int group=TradeGroup(b);
                r.Memory.Observe(__0.ProfileId,__0.Profile.Nickname,Time.time,true,false,true,associated,p.jumpRandom.NextDouble(),p.config.SquadTrade,group);
            }
            catch(Exception ex){p.TradeWarning(ex);}
        }
    }
}
