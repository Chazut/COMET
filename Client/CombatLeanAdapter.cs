using System;
using Comet.Core;
using EFT;
using HarmonyLib;
using Newtonsoft.Json;
using SAIN.Components;
using SAIN.Preset.Shared.Enums;
using SAIN.Preset.Shared.GlobalSettings;
using SAIN.SAINComponent.Classes.Mover;
using UnityEngine;

namespace Comet.Client;

internal sealed class CombatLeanRuntime
{
    internal readonly CombatLean Policy=new();
    internal LeanClass? Owner;
    internal int TargetInstance;
    internal LeanSetting Applied;
    internal bool Owns,Fault,LeftSafe,RightSafe,AnimatorApplied;
    internal float SafetyAt=-1000,LogAt=-1000,CallbackAt=-1000;
    internal Vector3 SafetyPosition,SafetyRight;
    internal string LogKey="";
}

public sealed partial class Plugin
{
    private static void PatchCombatLean(Harmony h)
    {
        // Replace only side selection. ManualUpdate still calls the original smoothed SetTilt.
        var method=AccessTools.Method(typeof(LeanClass),"UpdateLeanSetting",new[]{typeof(float)})
            ??throw new MissingMethodException("M04 native lean selection");
        h.Patch(method,prefix:new HarmonyMethod(typeof(Plugin),nameof(SelectCombatLean)));
        h.Patch(AccessTools.Method(typeof(LeanClass),"SetTilt"),postfix:new HarmonyMethod(typeof(Plugin),nameof(ApplyCombatLeanAnimation)));
    }
    private static void ApplyCombatLeanAnimation(LeanClass __instance)
    {
        var p=Instance;var b=__instance.Bot;
        if(p?.config==null||!p.states.TryGetValue(b,out var s))return;
        var r=s.CombatLean;
        if(!r.Owns||!r.Policy.Active||!ReferenceEquals(r.Owner,__instance)||__instance.LeanDirection!=r.Applied)return;
        try {
            var f=p.LeanFrame(b,s,__instance);f.GeometryFresh=f.LeftSafe=f.RightSafe=true;
            if(CombatLean.Gate(f,p.config.CombatLean).Length>0){p.EndCombatLean(s,"lean-animation-priority-changed");return;}
            // SAIN has already smoothed this value. Running EFT inertia over that
            // continuously changing target can leave the physical tilt at zero.
            // Preserve SAIN's smooth trajectory and feed it to the native animator.
            b.Player.MovementContext.SetTilt((float)f.SmoothedTilt,true);
            r.AnimatorApplied=true;r.Policy.Trace!.AnimatorApplied=true;
        }catch(Exception ex){r.Fault=true;p.EndCombatLean(s,"lean-animation-adapter-error");p.Logger.LogWarning("M04 animation yielded: "+ex.Message);}
    }
    private static bool SelectCombatLean(LeanClass __instance)
    {
        var p=Instance;var b=__instance.Bot;
        if(MedicalPostureGuard(b))return false;
        if(p?.config==null||!p.states.TryGetValue(b,out var s))return true;
        try { if(p.ChaseLean(__instance,s))return false; }
        catch(Exception ex) {p.ChaseError(s,ex);return true;}
        try { if(p.WatchLean(__instance,s))return false; } catch(Exception ex) {p.AngleError(s,ex);return true;}
        var r=s.CombatLean;
        if(r.Fault)return true;
        try {
            r.CallbackAt=Time.time;
            if(!r.Policy.Active&&Time.time<r.Policy.NextAttempt)return true;
            var f=p.LeanFrame(b,s,__instance);
            if(!r.Policy.Active&&f.FirePause)return true;
            // Cheap scope checks always precede geometry or RNG work.
            f.GeometryFresh=f.LeftSafe=f.RightSafe=true;
            string gate=CombatLean.Gate(f,p.config.CombatLean);
            if(gate.Length>0){p.EndCombatLean(s,gate);return true;}
            if(r.Policy.Active&&(!ReferenceEquals(r.Owner,__instance)
                ||r.TargetInstance!=System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(b.GoalEnemy)
                ||r.Owns&&__instance.LeanDirection!=r.Applied)) {
                p.EndCombatLean(s,"lean-native-owner-or-contact-changed");return true;
            }
            f.GeometryFresh=f.LeftSafe=f.RightSafe=true;
            if(!r.Policy.Active) {
                if(!r.Policy.Begin(f,p.config.CombatLean,p.jumpRandom.NextDouble(),p.jumpRandom.NextDouble(),p.jumpRandom.NextDouble(),
                    p.jumpRandom.NextDouble(),p.jumpRandom.NextDouble(),p.jumpRandom.NextDouble())) {p.RecordCombatLean(s);return true;}
                r.Owner=__instance;r.TargetInstance=System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(b.GoalEnemy);
            }else if(!r.Policy.Observe(f,p.config.CombatLean,p.jumpRandom.NextDouble(),p.jumpRandom.NextDouble())) {
                p.FinishCombatLean(s);return true;
            }
            r.Applied=r.Policy.Trace!.Side<0?LeanSetting.Left:LeanSetting.Right;r.Owns=true;
            __instance.FastLean(r.Applied); // SAIN smooths the desired side; our SetTilt postfix applies that smoothed value.
            p.RecordCombatLean(s);return false;
        }catch(Exception ex) {
            r.Fault=true;
            try{p.EndCombatLean(s,"lean-adapter-error");}catch{r.Owner=null;r.Owns=false;}
            p.Logger.LogWarning("M04 yielded: "+ex.GetBaseException().Message);return true;
        }
    }
    private CombatLeanFrame LeanFrame(BotComponent b,BotState s,LeanClass lean)
    {
        var f=CombatStepFrame(b,s);
        var right=b.Player.Transform.right;
        var head=b.Player.PlayerBones.Head;
        var position=head.position;var source="bone";
        // Prefer the enabled physical hit volume. BifacialTransform can return
        // an imitated/accumulated position instead of the animated head.
        var colliders=b.Player.PlayerBones.BodyPartCollidersDictionary;
        if(colliders!=null && colliders.TryGetValue(EBodyPartColliderType.HeadCommon,out var part)
            && part?.Collider is SphereCollider sphere && sphere.enabled && sphere.gameObject.activeInHierarchy)
        { position=sphere.transform.TransformPoint(sphere.center);source="head-collider"; }
        return new(){Combat=f,NativeLeanEnabled=b.Info.FileSettings.Move.LEAN_TOGGLE&&SainPresetAccess.AllowsLean,
            NativeLeanOwned=lean.IsHoldingLean||b.Cover.CoverInUse!=null,CornerPeek=s.CornerPeek.Policy.Active,
            FirePause=s.CombatStep.Pause.Pausing,SmoothedTilt=lean.LeanAngleValue.LastSmoothedValue,
            HeadLateral=Vector3.Dot(position-b.Position,right),HeadSource=source,
            BoneHeadLateral=Vector3.Dot(head.position-b.Position,right),HeadImitated=head.UseImitation,HeadAccumulated=head.AccumulatePositionAndRotation,
            NativeTilt=b.Player.MovementContext.Tilt,NativeSmoothedTilt=b.Player.MovementContext.SmoothedTilt,
            SimplifiedSkeleton=b.Player.UsedSimplifiedSkeleton,
            // Native EFT may stop updating transforms for distant/off-screen bots.
            // Record the mode, without forcing expensive animation or claiming motion.
            BodyAnimationCulling=b.Player.GetBodyAnimatorCommon()?.cullingMode switch
            {
                AnimatorCullingMode.AlwaysAnimate=>"always-animate",
                AnimatorCullingMode.CullUpdateTransforms=>"cull-update-transforms",
                AnimatorCullingMode.CullCompletely=>"cull-completely",
                _=>"unknown"
            }};
    }
    private void GuardCombatLean(BotComponent b,BotState s)
    {
        if(!s.CombatLean.Policy.Active)return;
        if(b==null||b.IsDead||config==null)EndCombatLean(s,"lean-callback-or-bot-ended");
        else {
            var f=LeanFrame(b,s,b.Mover.Lean);f.GeometryFresh=f.LeftSafe=f.RightSafe=true;
            var gate=CombatLean.Gate(f,config.CombatLean);
            if(gate.Length>0||Time.time>=s.CombatLean.Policy.Deadline)EndCombatLean(s,gate.Length>0?gate:"lean-sequence-completed");
        }
    }
    private void EndCombatLean(BotState s,string why)
    {
        if(!s.CombatLean.Policy.Active)return;
        s.CombatLean.Policy.End(why,Time.time);FinishCombatLean(s);
    }
    private void FinishCombatLean(BotState s)
    {
        var r=s.CombatLean;var lean=r.Owner;
        try {
            // Release our desired side only. Never clear another system's explicit held lean.
            if(r.Owns&&lean!=null&&Authority&&!lean.IsHoldingLean&&lean.Bot.Cover.CoverInUse==null&&lean.LeanDirection==r.Applied)
            {lean.FastLean(LeanSetting.None);if(r.AnimatorApplied)lean.Bot.Player.MovementContext.SetTilt(0,true);}
        }finally{r.Owns=false;r.AnimatorApplied=false;r.Owner=null;RecordCombatLean(s,true);}
    }
    private void RecordCombatLean(BotState s,bool force=false)
    {
        var r=s.CombatLean;var t=r.Policy.Trace;if(t==null)return;
        string key=t.Episode+":"+t.State+":"+t.Reason+":"+t.Requests+":"+t.HeadMotionObserved+":"+t.Alternatives.Length;
        if(!force&&key==r.LogKey&&(!r.Policy.Active||Time.time-r.LogAt<.1f))return;
        r.LogKey=key;r.LogAt=Time.time;
        var json=Diagnostics.Serialize(new{schema=1,session,sequence=++sequence,feature="M04",stage="evaluated",decision="CombatLean",reason=t.Reason,
            realtime=Time.realtimeSinceStartup,utc=DateTime.UtcNow.ToString("O"),configRevision=config?.Revision??0,profileId=s.ProfileId,name=s.Name,botInstance=s.Id,
            combatLean=t,combatLeanConfig=config?.CombatLean});
        diagnostics?.Write(json);Api.Decisions.Publish(json,ex=>{if(!subscriberWarned){subscriberWarned=true;Logger.LogWarning(ex.Message);}});
    }
}
