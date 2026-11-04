using System;
using Comet.Core;
using EFT;
using HarmonyLib;
using Newtonsoft.Json;
using SAIN;
using SAIN.Components;
using SAIN.SAINComponent.Classes.Info;
using SAIN.SAINComponent.Classes.WeaponFunction;
using UnityEngine;

namespace Comet.Client;

internal sealed class RecoilRecoveryRuntime
{
    internal RecoilRecoveryTrace? Trace;
    internal bool Active,Fault;
    internal double LastAt,StartedAt,LogAt=-1000;
    internal string Weapon="",WeaponClass="";
}
public sealed partial class Plugin
{
    private static void PatchRecoilRecovery(Harmony h)
    {
        h.Patch(AccessTools.Method(typeof(Recoil),nameof(Recoil.WeaponShot)),postfix:new HarmonyMethod(typeof(Plugin),nameof(RecoveryShot)));
        h.Patch(AccessTools.Method(typeof(Recoil),nameof(Recoil.CalcRecoilDecay)),prefix:new HarmonyMethod(typeof(Plugin),nameof(RecoveryDecay)));
    }
    private bool RecoveryScope(BotComponent b)=>config?.Enabled==true&&config.RecoilRecovery.Enabled
        &&NativeAdapter.IsPmcAi(b)&&Authority&&b.BotActive&&!b.IsDead&&!b.GameEnding&&!b.IsCheater&&!Ghost(b.ProfileId)&&b.SAINLayersActive;
    private static string RecoveryWeapon(BotComponent b)=>(b.Player.HandsController as Player.FirearmController)?.Item?.Id.ToString()??"";
    private static string RecoveryClass(BotComponent b)=>b.Info.WeaponInfo?.EWeaponClass.ToString()??"";

    // Postfix: SAIN has already computed the real initial recoil, including gear, stance and injuries.
    private static void RecoveryShot(Recoil __instance,WeaponInfo WeaponInfo,float ____currentRecoilHorizAngle,float ____currentRecoilVertAngle)
    {
        var p=Instance;if(p==null)return;BotState? s=null;
        try {
            var b=__instance.Bot;
            if(!p.RecoveryScope(b)){if(p.states.TryGetValue(b,out s))p.EndRecovery(s,"recoil-out-of-scope");return;}
            string weapon=RecoveryWeapon(b),kind=RecoveryClass(b);double multiplier=RecoilRecoveryPolicy.Multiplier(p.config!.RecoilRecovery,kind);
            if(multiplier<=1||weapon==""||WeaponInfo?.Weapon?.Id.ToString()!=weapon)
            {if(p.states.TryGetValue(b,out s))p.EndRecovery(s,"recoil-native-weapon");return;}
            if(!p.states.TryGetValue(b,out s)){s=new BotState(b,p.config!,p.session);p.states.Add(b,s);}
            var r=s.RecoilRecovery;if(r.Fault)return;
            if(r.Active&&(r.Weapon!=weapon||r.WeaponClass!=kind))p.EndRecovery(s,"recoil-weapon-changed");
            if(!RecoilRecoveryPolicy.TryStep(____currentRecoilHorizAngle,____currentRecoilVertAngle,0,Time.fixedDeltaTime,
                SAINPlugin.LoadedPreset.GlobalSettings.Shoot.BOT_RECOIL_DECAY_COEF,multiplier,out _,out _,out _))
            {p.EndRecovery(s,"recoil-invalid-native-data");return;}
            bool start=!r.Active;double now=Time.timeAsDouble;
            if(start){r.StartedAt=now;r.Trace=new(){Weapon=weapon,WeaponClass=kind,Multiplier=multiplier};}
            r.Active=true;r.Weapon=weapon;r.WeaponClass=kind;r.LastAt=now;
            var t=r.Trace!;t.State="started";t.Reason=kind=="marksmanRifle"?"recoil-marksman":"recoil-sniper";t.Applied=false;t.Step=0;
            t.Multiplier=multiplier;t.Coefficient=SAINPlugin.LoadedPreset.GlobalSettings.Shoot.BOT_RECOIL_DECAY_COEF;
            t.BeforeHorizontal=t.AfterHorizontal=____currentRecoilHorizAngle;t.BeforeVertical=t.AfterVertical=____currentRecoilVertAngle;
            t.Shots++;t.Elapsed=now-r.StartedAt;t.Action=b.Decision.CurrentCombatDecision.ToString();t.Target=s.Situation?.Target??"";
            p.RecordRecovery(s,start);
        }catch(Exception ex){p.RecoveryError(s,ex);}
    }
    private static bool RecoveryDecay(Recoil __instance,ref float ____currentRecoilHorizAngle,ref float ____currentRecoilVertAngle,ref bool ____recoilFinished)
    {
        var p=Instance;if(p==null)return true;BotState? s=null;bool handled=false;
        try {
            var b=__instance.Bot;
            if(!p.states.TryGetValue(b,out s))return true;
            var r=s.RecoilRecovery;if(r.Fault||!r.Active)return true;
            if(!p.RecoveryScope(b)){p.EndRecovery(s,"recoil-out-of-scope");return true;}
            string kind=RecoveryClass(b);double multiplier=RecoilRecoveryPolicy.Multiplier(p.config!.RecoilRecovery,kind);
            if(RecoveryWeapon(b)!=r.Weapon||kind!=r.WeaponClass||multiplier<=1)
            {p.EndRecovery(s,"recoil-weapon-or-setting-changed");return true;}
            if(____recoilFinished){p.EndRecovery(s,"recoil-native-finished");return true;}
            double now=Time.timeAsDouble,dt=now-r.LastAt;
            double coefficient=SAINPlugin.LoadedPreset.GlobalSettings.Shoot.BOT_RECOIL_DECAY_COEF;
            if(!RecoilRecoveryPolicy.TryStep(____currentRecoilHorizAngle,____currentRecoilVertAngle,dt,Time.fixedDeltaTime,coefficient,multiplier,
                out var h,out var v,out var finished)){p.EndRecovery(s,"recoil-stale-or-invalid-step");return true;}
            var t=r.Trace!;t.BeforeHorizontal=____currentRecoilHorizAngle;t.BeforeVertical=____currentRecoilVertAngle;
            // Replace this one native decay step, never decay once natively and then again in COMET.
            ____currentRecoilHorizAngle=h;____currentRecoilVertAngle=v;____recoilFinished=finished;handled=true;r.LastAt=now;
            t.AfterHorizontal=h;t.AfterVertical=v;t.Coefficient=coefficient;t.Multiplier=multiplier;t.Step=dt;t.Elapsed=now-r.StartedAt;
            t.Applied=dt>0&&coefficient>0;t.Steps+=t.Applied?1:0;t.State=finished?"settled":"recovering";
            t.Reason=finished?"recoil-settled":"recoil-time-based-recovery";t.ValidFor=finished?0:.75;
            t.Action=b.Decision.CurrentCombatDecision.ToString();
            if(finished)r.Active=false;
            if(dt>0)p.RecordRecovery(s,finished);
            return false;
        }catch(Exception ex){p.RecoveryError(s,ex);return !handled;}
    }
    private void EndRecovery(BotState s,string reason)
    {
        var r=s.RecoilRecovery;if(!r.Active)return;r.Active=false;r.Weapon="";r.WeaponClass="";
        var t=r.Trace;if(t==null)return;t.State="ended";t.Reason=reason;t.Applied=false;t.Step=0;t.ValidFor=0;
        RecordRecovery(s,true);
    }
    private void RecoveryError(BotState? s,Exception ex)
    {
        if(s==null)return;var r=s.RecoilRecovery;bool warn=!r.Fault;r.Fault=true;
        try{EndRecovery(s,"recoil-adapter-error");}catch{r.Active=false;r.Weapon="";r.WeaponClass="";}
        if(warn)Logger.LogWarning("W09 yielded to native SAIN: "+ex.GetBaseException().Message);
    }
    private void RecordRecovery(BotState s,bool force=false)
    {
        var r=s.RecoilRecovery;var t=r.Trace;if(t==null)return;
        double now=Time.timeAsDouble;if(!force&&now-r.LogAt<.5)return;r.LogAt=now;
        var json=JsonConvert.SerializeObject(new{schema=1,session,sequence=++sequence,feature="W09",stage="evaluated",decision="RecoilRecovery",reason=t.Reason,
            realtime=Time.realtimeSinceStartup,utc=DateTime.UtcNow.ToString("O"),configRevision=config?.Revision??0,profileId=s.ProfileId,name=s.Name,botInstance=s.Id,
            recoilRecovery=t,recoilRecoveryConfig=config?.RecoilRecovery},Diagnostics.Json);
        diagnostics?.Write(json);Api.Decisions.Publish(json,ex=>{if(!subscriberWarned){subscriberWarned=true;Logger.LogWarning(ex.Message);}});
    }
}
