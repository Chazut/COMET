using System;
using System.Reflection;
using Comet.Core;
using EFT;
using HarmonyLib;
using Newtonsoft.Json;
using SAIN.Components;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.Decision;
using SAIN.SAINComponent.Classes.EnemyClasses;
using SAIN.SAINComponent.Classes.Mover;
using UnityEngine;
namespace Comet.Client;

internal sealed class ReadyStanceRuntime
{
    internal readonly ReadyStance Policy=new();
    internal float Next,LogAt;
    internal bool NativePatrol,NativeKnown,Fault;
    internal string Key="";
}
public sealed partial class Plugin
{
    private static FieldInfo readyPatrol=null!;
    private static void PatchReadyStance(Harmony h)
    {
        readyPatrol=AccessTools.Field(typeof(MovementContext),"_isInPatrol")??throw new MissingFieldException("H05 native patrol posture");
        h.Patch(AccessTools.Method(typeof(BotDecisionManager),nameof(BotDecisionManager.ManualUpdate)),postfix:new HarmonyMethod(typeof(Plugin),nameof(ReadyFight)));
        h.Patch(AccessTools.Method(typeof(EnemyEvents),nameof(EnemyEvents.EnemyHeard),new[]{typeof(SAINSoundType),typeof(bool),typeof(EnemyPlace)}),postfix:new HarmonyMethod(typeof(Plugin),nameof(ReadyHeard)));
        h.Patch(AccessTools.Method(typeof(SAINMoverClass),"WantsPatrolStance")??throw new MissingMethodException("H05 native patrol choice"),postfix:new HarmonyMethod(typeof(Plugin),nameof(ReadyNativeWish)));
        h.Patch(AccessTools.Method(typeof(SAINMoverClass),nameof(SAINMoverClass.ManualUpdate)),postfix:new HarmonyMethod(typeof(Plugin),nameof(ReadyAfterMover)));
    }
    private bool ReadyScope(BotComponent b)=>config?.Enabled==true&&config.ReadyStance.Enabled&&world!=null&&Authority
        &&b!=null&&NativeAdapter.IsPmcAi(b)&&b.BotActive&&!b.IsDead&&!b.GameEnding&&!Ghost(b.ProfileId);
    private static void ReadyFight(BotDecisionManager __instance)
    {
        var p=Instance;var b=__instance.Bot;if(p==null)return;
        try
        {
            if(!p.ReadyScope(b)){if(p.states.TryGetValue(b,out var old))p.EndReadyStance(old,"ready-stance-out-of-scope");return;}
            bool firing=b.BotOwner.ShootData.Shooting,hit=b.BotOwner.Memory.IsUnderFire,visible=b.GoalEnemy?.IsVisible==true;
            if(!firing&&!hit&&!visible)return;
            if(!p.states.TryGetValue(b,out var s)){if(p.states.Count>=512)return;s=new BotState(b,p.config!,p.session);p.states.Add(b,s);}
            if(s.ReadyStance.Fault)return;
            var e=b.GoalEnemy;var place=e?.KnownPlaces.LastSeenPlace;var heard=e?.KnownPlaces.LastHeardPlace;
            if(heard!=null&&(place==null||heard.TimeSincePositionUpdated<place.TimeSincePositionUpdated))place=heard;
            bool usable=place!=null&&ValidPoint(place.Position)&&place.TimeSincePositionUpdated>=0&&float.IsFinite(place.TimeSincePositionUpdated);
            s.ReadyStance.Policy.Observe(Time.time,usable?Time.time-place!.TimeSincePositionUpdated:double.NaN,
                hit?"incoming fire":firing?"firing":"visible contact",e?.EnemyProfileId??"",usable?Vector3.Distance(b.Position,place!.Position):null,true,p.config!.ReadyStance);
        }
        catch(Exception ex){if(p.states.TryGetValue(b,out var s))p.ReadyError(s,ex);}
    }
    private static void ReadyHeard(Enemy ___Enemy,SAINSoundType __0,EnemyPlace __2)
    {
        var p=Instance;var b=___Enemy?.Bot;
        if(p==null||b==null||!p.states.TryGetValue(b,out var s)||s.ReadyStance.Fault)return;
        try
        {
            if(!p.ReadyScope(b)){p.EndReadyStance(s,"ready-stance-out-of-scope");return;}
            if(__0!=SAINSoundType.FootStep&&__0!=SAINSoundType.Sprint&&__0!=SAINSoundType.Shot)return;
            if(__2==null||!___Enemy!.EnemyKnown||!ValidPoint(__2.Position))return;
            s.ReadyStance.Policy.Observe(Time.time,Time.time-__2.TimeSincePositionUpdated,"heard "+__0,___Enemy.EnemyProfileId,
                Vector3.Distance(b.Position,__2.Position),false,p.config!.ReadyStance);
        }catch(Exception ex){p.ReadyError(s,ex);}
    }
    private string ReadyBlock(BotComponent b,BotState s)
    {
        if(b.Player.IsSprintEnabled||b.Mover.Running)return "ready-stance-sprint";
        if(b.BotOwner.DoorOpener.Interacting||b.BotOwner.Mover.CurrentState==EBotMoverState.NearDoor)return "ready-stance-door";
        if(b.BotOwner.Medecine.Using||b.Player.HandsController is IMedsController||s.MedicalPosture.Policy.Blocking)return "ready-stance-medicine";
        if(b.BotOwner.WeaponManager.Reload.Reloading)return "ready-stance-reload";
        if(b.Player.HandsController is not Player.FirearmController f||!WeaponHandsReady(b,f)||f.IsInRemoveOperation()||f.IsInReloadOperation()
            ||f.IsInventoryOpen()||f.IsInInteractionStrictCheck()||f.IsInSpawnOperation()||f.IsHandsProcessing())return "ready-stance-hands";
        return "";
    }
    private static void ReadyNativeWish(SAINMoverClass __instance,ref bool __result)
    {
        var p=Instance;var b=__instance.Bot;if(p==null||!p.states.TryGetValue(b,out var s)||s.ReadyStance.Fault)return;
        try
        {
            if(!p.ReadyScope(b)){p.EndReadyStance(s,"ready-stance-out-of-scope");return;}
            var r=s.ReadyStance;r.NativePatrol=__result;r.NativeKnown=true;
            if(r.Policy.Evaluate(Time.time,__result,p.ReadyBlock(b,s),p.config!.ReadyStance))__result=false;
            // Observation is recorded after native UpdateStance applied (or refused) the request.
            r.Next=0;
        }catch(Exception ex){p.ReadyError(s,ex);}
    }
    private static void ReadyAfterMover(SAINMoverClass __instance)
    {var p=Instance;if(p!=null&&p.states.TryGetValue(__instance.Bot,out var s))p.SampleReadyStance(__instance.Bot,s);}
    private void SampleReadyStance(BotComponent b,BotState s)
    {
        var r=s.ReadyStance;if(r.Fault||r.Policy.Trace==null)return;
        try
        {
            if(!ReadyScope(b)){EndReadyStance(s,"ready-stance-out-of-scope");return;}
            string block=ReadyBlock(b,s);
            if(Time.time<r.Next&&block.Length==0&&r.Policy.Pending(Time.time))return;
            r.Next=Time.time+(float)config!.ReadyStance.Interval;
            r.Policy.Evaluate(Time.time,r.NativeKnown&&r.NativePatrol,block,config.ReadyStance);
            var patrol=readyPatrol.GetValue(b.Player.MovementContext);
            if(patrol is not bool lowered)throw new InvalidOperationException("H05 native patrol state unavailable");
            r.Policy.ObservePosture(b.Player.HandsController is Player.FirearmController&&!lowered);
            RecordReadyStance(s,false);
        }catch(Exception ex){ReadyError(s,ex);}
    }
    private void EndReadyStance(BotState s,string reason)
    {
        var r=s.ReadyStance;if(r.Policy.Trace==null||r.Policy.Trace.State=="released")return;
        r.Policy.Clear(reason);r.NativeKnown=false;RecordReadyStance(s,true);
        // Release only our veto. The next native stance evaluation chooses its current posture.
    }
    private void ReadyError(BotState s,Exception ex)
    {if(s.ReadyStance.Fault)return;s.ReadyStance.Fault=true;EndReadyStance(s,"ready-stance-adapter-error");Logger.LogWarning("H05 yielded: "+ex.GetBaseException().Message);}
    private void RecordReadyStance(BotState s,bool force)
    {
        var r=s.ReadyStance;var t=r.Policy.Trace;if(t==null)return;
        string key=t.State+":"+t.Reason+":"+t.Evidence+":"+t.Contact+":"+t.Requested+":"+t.ObservedRaised;
        if(!force&&key==r.Key&&(!t.Requested||Time.time<r.LogAt))return;r.Key=key;r.LogAt=Time.time+.5f;
        var json=Diagnostics.Serialize(new{schema=1,session,sequence=++sequence,feature="H05",stage="evaluated",decision="ReadyStance",reason=t.Reason,
            realtime=Time.realtimeSinceStartup,utc=DateTime.UtcNow.ToString("O"),configRevision=config?.Revision??0,profileId=s.ProfileId,name=s.Name,botInstance=s.Id,
            readyStance=t,readyStanceConfig=config?.ReadyStance});
        diagnostics?.Write(json);Api.Decisions.Publish(json,ex=>{if(!subscriberWarned){subscriberWarned=true;Logger.LogWarning(ex.Message);}});
    }
}
