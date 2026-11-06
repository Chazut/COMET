using System;
using Comet.Core;
using EFT;
using HarmonyLib;
using Newtonsoft.Json;
using SAIN.Components;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.Mover;
using UnityEngine;
namespace Comet.Client;

internal sealed class MedicalPostureRuntime
{
    internal readonly MedicalPosture Policy=new();
    internal float Next, LogAt;
    internal bool Fault, Sampling;
    internal string Key="";
}
public sealed partial class Plugin
{
    private static void PatchMedicalPosture(Harmony h)
    {
        h.Patch(AccessTools.Method(typeof(LeanClass),"UpdateLeanSetting",new[]{typeof(float)})
            ??throw new MissingMethodException("H03 SAIN lean selection"),
            prefix:new HarmonyMethod(typeof(Plugin),nameof(MedicalLeanSelection)){priority=Priority.First});
        // Cover/held lean can request a side outside UpdateLeanSetting as well.
        h.Patch(AccessTools.Method(typeof(LeanClass),nameof(LeanClass.FastLean)),
            prefix:new HarmonyMethod(typeof(Plugin),nameof(MedicalLeanRequest)){priority=Priority.First});
    }
    private bool MedicalPostureScope(BotComponent b)=>config?.Enabled==true&&config.MedicalPosture.Enabled&&world!=null&&Authority
        &&b!=null&&NativeAdapter.IsPmcAi(b)&&b.BotActive&&!b.IsDead&&!b.GameEnding&&!Ghost(b.ProfileId);
    private static bool MedicalLeanSelection(LeanClass __instance)=>!MedicalPostureGuard(__instance.Bot);
    private static void MedicalLeanRequest(LeanClass __instance,ref LeanSetting value)
    {if(value!=LeanSetting.None&&MedicalPostureGuard(__instance.Bot))value=LeanSetting.None;}
    // Also used at COMET tactic boundaries; medicine entering hands bypasses the sample interval.
    private static bool MedicalPostureGuard(BotComponent b)
    {
        var p=Instance;if(p==null)return false;
        try
        {
            if(!p.MedicalPostureScope(b))
            {if(b!=null&&p.states.TryGetValue(b,out var old))p.EndMedicalPosture(old,"medical-posture-out-of-scope");return false;}
            if(!p.states.TryGetValue(b,out var s))
            {
                if(b.BotOwner.Medecine?.Using!=true&&b.Player.HandsController is not IMedsController||p.states.Count>=512)return false;
                s=new BotState(b,p.config!,p.session);p.states.Add(b,s);
            }
            p.SampleMedicalPosture(b,s);return s.MedicalPosture.Policy.Blocking;
        }
        catch(Exception ex)
        {if(b!=null&&p.states.TryGetValue(b,out var s))p.MedicalPostureError(s,ex);return false;}
    }
    private void SampleMedicalPosture(BotComponent b,BotState s)
    {
        var r=s.MedicalPosture;if(r.Fault||r.Sampling)return;
        if(!MedicalPostureScope(b)){EndMedicalPosture(s,"medical-posture-out-of-scope");return;}
        var m=b.BotOwner.Medecine;bool use=m?.Using==true,hands=b.Player.HandsController is IMedsController;
        if(!use&&!hands&&!r.Policy.Blocking)return;
        var previous=r.Policy.Trace;
        bool changed=previous==null||previous.Using!=use||previous.MedicineHands!=hands;
        if(!changed&&Time.time<r.Next)return;
        r.Next=Time.time+(float)config!.MedicalPosture.Interval;r.Sampling=true;
        try
        {
            var lean=b.Mover.Lean;
            string treatment=m?.SurgicalKit.Using==true?"Surgery":m?.Stimulators.Using==true?"Stims":m?.FirstAid.Using==true?"FirstAid":"Native medicine";
            bool blocked=r.Policy.Tick(new(){Now=Time.time,Using=use,MedicineHands=hands,
                WeaponReady=WeaponHandsReady(b,b.Player.HandsController as Player.FirearmController),
                Tilt=lean.LeanAngleValue.LastSmoothedValue,NeutralTarget=lean.LeanDirection==LeanSetting.None,
                Grounded=b.Player.MovementContext.IsGrounded,Treatment=treatment,
                Item=hands?b.Player.HandsController?.Item?.Id.ToString()??"":""},config.MedicalPosture);
            if(blocked)
            {
                // Requested side only. ManualUpdate's original SetTilt performs the smooth return.
                lean.ResetHoldLean();lean.FastLean(LeanSetting.None);
                SuspendMedicalTactics(b,s);
            }
            RecordMedicalPosture(s,false);
        }
        finally{r.Sampling=false;}
    }
    private void EndMedicalPosture(BotState s,string reason)
    {if(!s.MedicalPosture.Policy.Blocking)return;s.MedicalPosture.Policy.End(reason);RecordMedicalPosture(s,true);}
    private void MedicalPostureError(BotState s,Exception ex)
    {if(s.MedicalPosture.Fault)return;s.MedicalPosture.Fault=true;EndMedicalPosture(s,"medical-posture-adapter-error");Logger.LogWarning("H03 yielded: "+ex.GetBaseException().Message);}
    private void RecordMedicalPosture(BotState s,bool force)
    {
        var r=s.MedicalPosture;var t=r.Policy.Trace;if(t==null)return;
        string key=t.State+":"+t.Reason+":"+t.Treatment+":"+t.Item+":"+t.Neutral+":"+t.WeaponReady+":"+t.Grounded;
        if(!force&&key==r.Key&&(!t.Applied||Time.time<r.LogAt))return;
        r.Key=key;r.LogAt=Time.time+.5f;
        var json=Diagnostics.Serialize(new{schema=1,session,sequence=++sequence,feature="H03",stage="evaluated",decision="MedicalPosture",reason=t.Reason,
            realtime=Time.realtimeSinceStartup,utc=DateTime.UtcNow.ToString("O"),configRevision=config?.Revision??0,profileId=s.ProfileId,name=s.Name,botInstance=s.Id,
            medicalPosture=t,medicalPostureConfig=config?.MedicalPosture});
        diagnostics?.Write(json);Api.Decisions.Publish(json,ex=>{if(!subscriberWarned){subscriberWarned=true;Logger.LogWarning(ex.Message);}});
    }
}
