using System;
using System.Collections.Generic;
using Comet.Core;
using EFT;
using HarmonyLib;
using SAIN.Components;
using SAIN.Models.Enums;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.Mover;
using SAIN.SAINComponent.SubComponents.CoverFinder;
using UnityEngine;
using UnityEngine.AI;

namespace Comet.Client;

internal static class ProgressAdapter
{
    // Destination identity is independent of target, tactic and path recalculation.
    internal static string Destination(Vector3 p) => "destination:" + Math.Round(p.x*2) + ":" + Math.Round(p.y*2) + ":" + Math.Round(p.z*2);
    internal static string CoverKey(CoverPoint? p) => p==null?"":Destination(p.Position);
    internal static string GoalKey(BotComponent bot) => bot.GoalEnemy?.KnownPlaces.LastKnownPosition is Vector3 p ? Destination(p) : "";
    internal static void Candidates(BotComponent bot, BotState state, Situation s)
    {
        if(!state.Progress.Enabled || state.ProgressUnavailable)return;
        s.Progress=state.Progress;
        var keys=state.Progress.CandidateAttempts; keys.Clear();
        keys["Push"]=keys["Search"]=GoalKey(bot);
        keys["Cover"]=keys["FallBack"]=CoverKey(state.SelectedCover);
        keys["Flank"]=CoverKey(state.Flank.Point);
    }
    internal static bool RetireFailedCover(BotComponent bot, PlanProgress progress, double now)
    {
        // SAIN UpdateCover ignores GoToCoverPoint's false result for CoverInUse and
        // keeps holding it. Release only a destination already rejected by A12.
        // Do not mark it permanently bad: it becomes usable after the retry delay.
        if (!progress.Blocked(CoverKey(bot.Cover.CoverInUse), now)
            && !progress.Blocked(CoverKey(bot.Cover.CoverPoint_MovingTo), now)) return false;
        bot.Cover.StopSeekingCover();
        return true;
    }
    internal static ProgressFrame Capture(BotComponent bot,BotState state,bool authority,bool ghost,CometConfig config)
    {
        var p=bot.Position;var d=bot.Decision;var path=bot.Mover.ActivePath;
        var enemy=bot.GoalEnemy;var known=enemy?.KnownPlaces.LastKnownPosition;
        var f=new ProgressFrame {Now=Time.time,X=p.x,Y=p.y,Z=p.z,Scoped=NativeAdapter.IsPmcAi(bot)&&bot.BotActive&&authority&&!ghost,
            Action=state.Decision,Owned=state.ActiveLabel.Length>0 && d.CurrentCombatDecision==state.Expected && bot.SAINLayersActive && bot.CurrentAction!=null,
            Target=enemy?.EnemyProfileId??"",CueAt=enemy==null?-1:Time.time-enemy.TimeSinceLastKnownUpdated,
            CueX=known?.x??0,CueY=known?.y??0,CueZ=known?.z??0};
        int count=0;
        foreach(var e in bot.EnemyController.KnownEnemies)
        {if(++count>32){f.HasThreat=true;break;}if(e!=null && e.KnownPlaces.LastKnownPosition.HasValue && e.TimeSinceLastKnownUpdated<=config.ContactLifetime){f.HasThreat=true;break;}}
        f.HasThreat |= d.CurrentCombatDecision!=ECombatDecision.None || d.CurrentSelfDecision!=ESelfActionType.None || d.CurrentSquadDecision!=ESquadDecision.None;
        var w=bot.BotOwner.WeaponManager;
        if(d.CurrentSelfDecision!=ESelfActionType.None || d.CurrentSquadDecision!=ESquadDecision.None) f.PauseReason="progress-native-priority";
        else if(state.GrenadeEscape.Trace?.State is "planning" or "moving" or "prone")f.PauseReason="progress-grenade-escape";
        else if(state.OwnGrenade.Trace?.State is "waiting" or "fleeing")f.PauseReason="progress-own-grenade-safety";
        else if(state.Entry.Policy.Active&&state.Entry.Policy.Trace?.State=="interacting")f.PauseReason="progress-native-kick-animation";
        else if(bot.BotOwner.DoorOpener.Interacting || path?.Status==EBotMoveStatus.DoorInteraction)f.PauseReason="progress-door-pause";
        else if(state.RoomClear.Policy.Active&&state.RoomClear.Policy.Trace?.State!="entering")f.PauseReason="progress-room-clear-preparation";
        else if(state.Fake.Policy.Active)f.PauseReason="progress-door-feint";
        else if(w==null || w.Reload.Reloading || w.Selector.IsChanging || bot.Player.HandsController.IsInInteractionStrictCheck())f.PauseReason="progress-hands-pause";
        else if(path?.Status==EBotMoveStatus.Paused || (state.Decision=="Search" && bot.Search.CurrentState.ToString()=="Wait"))f.PauseReason="progress-search-pause";
        // An actual shooting action from a static cover is purposeful, even if its parent decision is Cover/Search.
        else if(bot.BotOwner.ShootData.Shooting || bot.ManualShoot.Shooting)f.PauseReason="progress-shooting";
        f.Paused=f.PauseReason.Length>0;
        // D09 is still the same Hold, including its approach and optional bait. Never reset or pause its shared wait budget.
        if(state.Ambush.Policy.Active&&f.Action=="Hold"&&d.CurrentSelfDecision==ESelfActionType.None&&d.CurrentSquadDecision==ESquadDecision.None)
        {f.Paused=false;f.PauseReason="";}
        if(PlanProgress.IsMove(f.Action))
        {
            var cover=f.Action=="Flank"?state.Flank.Point:bot.Cover.CoverPoint_MovingTo ?? state.SelectedCover;
            f.Attempt=f.Action=="Cover"||f.Action=="FallBack"||f.Action=="Flank"?CoverKey(cover):GoalKey(bot);
            f.Intent=f.Attempt;
            if(path!=null && path.PathStatus==NavMeshPathStatus.PathComplete && path.PathPoints!=null && path.PathPoints.Count>=2 && path.PathPoints.Count<=128)
            {
                // Active native path is the route actually being executed, including search corners and detours.
                f.Attempt=Destination(path.Destination);
                double remaining=0,best=double.MaxValue,bestRemaining=0;
                unchecked {
                    int hash=17;
                    for(int i=path.PathPoints.Count-1;i>0;i--)
                    {
                        var a=path.PathPoints[i-1];var b=path.PathPoints[i];var edge=b-a;
                        float length=edge.magnitude;float t=length>.001f?Mathf.Clamp01(Vector3.Dot(p-a,edge)/(length*length)):0;
                        float dist=(p-(a+edge*t)).sqrMagnitude;
                        if(dist<best){best=dist;bestRemaining=remaining+length*(1-t);}
                        remaining+=length;
                        hash=hash*31+(int)Math.Round(b.x*2);hash=hash*31+(int)Math.Round(b.y*2);hash=hash*31+(int)Math.Round(b.z*2);
                    }
                    f.Route=hash.ToString();
                }
                if(best<=9)f.Remaining=bestRemaining;
                // Being close alone is not arrival. Require the native completion state too.
                f.Arrived=path.Status==EBotMoveStatus.Complete && Vector3.Distance(p,path.Destination)<=Math.Max(.25f,path.DestinationReachDistance);
            }
            if((f.Action=="Cover"||f.Action=="FallBack") && bot.Cover.CoverInUse is CoverPoint used
                && Vector3.Distance(p,used.Position)<=1.5f)
            {f.Arrived=true;f.Attempt=CoverKey(used);}
            if(f.Action=="Flank")f.Arrived=FlankAdapter.Arrived(bot,state);
        }
        return f;
    }
}

public sealed partial class Plugin
{
    private void RefreshProgress(BotComponent bot,BotState state)
    {
        if(config==null || !config.Enabled || !config.Progress.Enabled || state.ProgressUnavailable || Time.time<state.NextProgressSample)return;
        state.NextProgressSample=Time.time+.25f;
        try
        {
            if(!NativeAdapter.IsPmcAi(bot) || !bot.BotActive || !Authority || Ghost(bot.ProfileId))
            { state.Progress.Clear();state.ProgressAtSelection=null;return; }
            var frame=ProgressAdapter.Capture(bot,state,Authority,Ghost(bot.ProfileId),config);
            if(RecoveryOwnsStall(state)&&PlanProgress.IsMove(frame.Action)&&!frame.Arrived&&!frame.Paused)
            {frame.Paused=true;frame.PauseReason="progress-C06-recovery-in-progress";}
            bool replan=state.Progress.Observe(frame);
            var trace=state.Progress.Current;state.ProgressAtSelection=trace;
            string key=trace.State+":"+trace.Reason+":"+trace.Attempt;
            if(replan || key!=state.ProgressLogKey && Time.time-state.ProgressLogAt>=1)
            {state.ProgressLogKey=key;state.ProgressLogAt=Time.time;Emit(state,"evaluated",trace.Reason);}
            if(replan && state.ActiveLabel.Length>0)
            {
                if(frame.Action is "Cover" or "FallBack" or "Flank"
                    && RetireProgressCover(bot,state)) Emit(state,"evaluated","progress-native-cover-retired","A12");
                Release(state,trace.Reason);
            }
        }
        catch(Exception ex)
        {
            state.Progress.Clear();
            if(!state.ProgressWarned){state.ProgressWarned=true;Logger.LogWarning("COMET progress observation unavailable; legacy timers retained: "+ex.Message);}
            state.ProgressUnavailable=true;
        }
    }
    private bool RetireProgressCover(BotComponent bot,BotState state)
        => ProgressAdapter.RetireFailedCover(bot,state.Progress,Time.time);
    private static bool ProgressCoverMove(SAINMoverClass __instance,CoverPoint point,ref bool __result)
    {
        var plugin=Instance;var bot=__instance.Bot;
        if(plugin?.config?.Progress.Enabled!=true || !plugin.states.TryGetValue(bot,out var state) || state.ProgressUnavailable || state.ActiveLabel.Length==0
            || !NativeAdapter.IsPmcAi(bot) || !bot.BotActive || !plugin.Authority || plugin.Ghost(bot.ProfileId)
            || bot.Decision.CurrentCombatDecision!=state.Expected || bot.Decision.CurrentSelfDecision!=ESelfActionType.None || bot.Decision.CurrentSquadDecision!=ESquadDecision.None)return true;
        if(!state.Progress.Blocked(ProgressAdapter.CoverKey(point),Time.time))return true;
        plugin.RetireProgressCover(bot,state);
        __result=false;return false;
    }
}
