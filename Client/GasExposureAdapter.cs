using System;
using System.Collections.Generic;
using System.Reflection;
using Comet.Core;
using EFT;
using HarmonyLib;
using Newtonsoft.Json;
using SAIN.Components;
using SAIN.Helpers;
using SAIN.Layers;
using SAIN.Models.Enums;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.Decision;
using SAIN.SAINComponent.Classes.Mover;
using SAIN.SAINComponent.Classes.WeaponFunction;
using SAIN.SAINComponent.SubComponents;
using UnityEngine;
using UnityEngine.AI;
namespace Comet.Client;
internal sealed class GasRuntime
{
 internal double Intensity;
 internal float Next,Last,NextCough,NextFlash,AttemptUntil,RetryAt,ProbeAt,LogAt=-1000;
 internal bool Inside,Owned,Fault;
 internal int Candidate,ModifierStep=-1;
 internal string Protection="None",ModifierProtection="",Key="";
 internal Vector3 Center,Goal;
 internal IBotPathData? Path;
 internal NavMeshPath? Mesh;
 internal TemporaryStatModifiers? Modifier;
 internal GasExposureTrace? Trace;
 internal readonly List<string> Alternatives=new();
}
public sealed partial class Plugin
{
 private readonly ManimalGasBridge gasBridge=new();
 private static MethodInfo gasSetDecision=null!,gasNativeReaction=null!;
 private int gasProbeFrame=-1,gasProbes;
 private void PatchGasExposure(Harmony h)
 {
  if(!gasBridge.Discover()){Logger.LogInfo("G08 inactive: "+gasBridge.Status);return;}
  try{
   gasSetDecision=AccessTools.Method(typeof(BotDecisionManager),"SetDecisions")??throw new MissingMethodException("SAIN decisions");
   gasNativeReaction=AccessTools.Method(typeof(GrenadeReactionClass),"GetReaction")??throw new MissingMethodException("SAIN grenade reaction");
   gasBridge.Hook(h);
   h.Patch(AccessTools.Method(typeof(BotComponent),nameof(BotComponent.ManualUpdate)),postfix:new HarmonyMethod(typeof(Plugin),nameof(GasBotUpdate)));
   Logger.LogInfo("G08 ready: "+gasBridge.Status+"; awake authoritative PMCs and AI Player Scavs only");
  }catch(Exception ex){DisableGas(ex);}
 }
 internal static void GasCloudCreated(Component __instance){try{Instance?.gasBridge.Add(__instance,false);}catch(Exception e){Instance?.DisableGas(e);}}
 internal static void GasEmitterCreated(Component __result,SmokeGrenade __0){try{Instance?.gasBridge.Add(__result,true,__0);}catch(Exception e){Instance?.DisableGas(e);}}
 private bool GasScope(BotComponent b)=>gasBridge.Ready&&world!=null&&config?.Enabled==true&&config.GasExposure.Enabled&&b!=null&&!b.IsDead
  &&NativeAdapter.IsSupportedAi(b)&&b.BotActive&&!b.GameEnding&&Authority&&!Ghost(b.ProfileId);
 private void DisableGas(Exception ex)
 {gasBridge.Ready=false;gasBridge.Clear();Logger.LogWarning("G08 inactive, native AI retained: "+ex.GetBaseException().Message);foreach(var pair in states)DropGas(pair.Key,pair.Value,"gas-provider-unavailable");}
 // Runs before the existing G04/G05 arbitration. Only a verified Manimal grenade is removed
 // from native explosive selection; known explosive tracks remain in their original dictionary.
 private void FilterNativeGas(GrenadeReactionClass native)
 {
  if(!GasScope(native.Bot))return;
  try {
   var selected=native.DangerGrenade;
   if(selected!=null&&gasBridge.Owns(selected.Grenade)&&selected.TimeSinceThrown>=5)selected=null;
   float nearest=selected==null?324:(selected.DangerPoint-native.Bot.Position).sqrMagnitude;
   foreach(var t in native.EnemyGrenadesList.Values){
    if(t?.Grenade==null||!t.CanReact)continue;
    bool gas=gasBridge.Owns(t.Grenade);
    if(gas?t.TimeSinceThrown>=5:t.Grenade is SmokeGrenade)continue;
    float d=(t.DangerPoint-native.Bot.Position).sqrMagnitude;
    if(d<nearest){nearest=d;selected=t;}
   }
   ownDangerSetter.Invoke(native,new object?[]{selected});
   var reaction=selected==null?EGrenadeReaction.None:gasBridge.Owns(selected.Grenade)?EGrenadeReaction.Scatter:(EGrenadeReaction)gasNativeReaction.Invoke(native,null)!;
   ownReactionSetter.Invoke(native,new object[]{reaction});
  }catch(Exception ex){DisableGas(ex);}
 }
 private static void GasBotUpdate(BotComponent __instance)
 {
  var p=Instance;if(p==null)return;
  try{
   if(!p.GasScope(__instance)){if(p.states.TryGetValue(__instance,out var old))p.DropGas(__instance,old,"gas-out-of-scope");return;}
   var s=p.GrenadeState(__instance);if(s.Gas.Fault)return;p.SampleGas(__instance,s);
  }catch(Exception ex){p.GasError(__instance,ex);}
 }
 private void SampleGas(BotComponent b,BotState s)
 {
  var r=s.Gas;var c=config!.GasExposure;if(Time.time<r.Next)return;
  float dt=r.Last>0?Time.time-r.Last:0;r.Last=Time.time;r.Next=Time.time+(float)c.Refresh;
  bool inside=gasBridge.Inside(b.Transform.EyePosition,out var cloud,out var center,out int active,out var variant);
  // Inventory inspection is only relevant to exposure/recovery. Most frames have
  // no gas at this bot, including when the optional provider is installed.
  r.Protection=inside||r.Intensity>0?gasBridge.Protection(b.Player):"None";r.Inside=inside;r.Center=center;
  r.Intensity=GasExposureRules.Exposure(r.Intensity,inside && gasBridge.ExposureReady(cloud),r.Protection,dt,c);
  // Audited 2.0.0/2.0.1 irritation targets MainPlayer. The provider owns RDA infected buffs.
  // Keep independent intensity modifiers; source blindness does not duplicate flashbang modifiers.
  int step=Mathf.RoundToInt((float)r.Intensity*10);
  if(step!=r.ModifierStep||r.Protection!=r.ModifierProtection){ClearGasModifier(b,r);r.ModifierStep=step;r.ModifierProtection=r.Protection;
   if(step>0){var f=b.BotOwner.Settings.FileSettings.Change;float t=step/10f;
    r.Modifier=new TemporaryStatModifiers(Mathf.Lerp(1,f.FLASH_PRECICING,t),Mathf.Lerp(1,f.FLASH_ACCURATY,t),Mathf.Lerp(1,f.FLASH_GAIN_SIGHT,t),Mathf.Lerp(1,f.FLASH_SCATTERING,t),Mathf.Lerp(1,f.FLASH_SCATTERING,t),Mathf.Lerp(1,f.FLASH_VISION_DIST,t),r.Protection=="None"?Mathf.Lerp(1,f.FLASH_HEARING,t):1);
    b.BotOwner.Settings.Current.Apply(r.Modifier.Modifiers);
   }
  }
  bool cough=GasExposureRules.Cough(inside,r.Protection,r.Intensity)&&Time.time>=r.NextCough;
  if(cough){r.NextCough=Time.time+(float)c.CoughInterval;b.Talk.Say(EPhraseTrigger.OnBreath,ETagStatus.Dying,false,false);}
  if(inside && r.Intensity>=.35 && Time.time>=r.NextFlash){
   r.NextFlash=Time.time+2; ApplyGasFlash(b,center);
  }
  if(!inside&&r.Intensity<=0){DropGas(b,s,"gas-recovered",false);return;}
  r.Trace=new(){State=inside?"exposed":"recovering",Reason=inside?"gas-source-exposure":"gas-left-cloud",Cloud=cloud,Clouds=active,Provider=gasBridge.Provider,Variant=variant,
   Protection=r.Protection,Inside=inside,Intensity=r.Intensity,X=center.x,Y=center.y,Z=center.z,Cough=cough,Flashed=b.Flashed.IsFlashed};
  RecordGas(s);
 }
 // Gas uses SAIN's initial grenade scatter, then its Flashed layer. No custom escape path/lease.
 private bool TryGasAction(BotComponent b)=>false;
 private static void ClearGasModifier(BotComponent b,GasRuntime r){if(r.Modifier!=null){b.BotOwner.Settings.Current.Dismiss(r.Modifier.Modifiers);r.Modifier=null;}r.ModifierStep=-1;}
 private void StopGasPath(BotComponent? b,GasRuntime r){if(b!=null&&r.Path!=null&&ReferenceEquals(b.Mover.ActivePath,r.Path)&&ReferenceEquals(r.Path.NavMeshPath,r.Mesh))combatStopPath.Invoke(r.Path,new object[]{false,"COMET G08 escape ended"});r.Path=null;r.Mesh=null;}
 private void DropGas(BotComponent? b,BotState s,string reason,bool resetSampling=true)
 {var r=s.Gas;StopGasPath(b,r);if(b!=null)ClearGasModifier(b,r);r.Intensity=0;r.Inside=r.Owned=false;r.AttemptUntil=r.RetryAt=r.NextFlash=0;if(resetSampling)r.Next=r.Last=0;r.Candidate=0;if(r.Trace!=null&&r.Trace.State!="ended")GasState(s,"ended",reason);}
 private void GasError(BotComponent b,Exception ex){if(!states.TryGetValue(b,out var s))return;if(!s.Gas.Fault)Logger.LogWarning("G08 yielded: "+ex.GetBaseException().Message);s.Gas.Fault=true;DropGas(b,s,"gas-adapter-error");}
 private void GasState(BotState s,string state,string reason){if(s.Gas.Trace==null)return;s.Gas.Trace.State=state;s.Gas.Trace.Reason=reason;RecordGas(s);}
 private void RecordGas(BotState s)
 {
  var r=s.Gas;var t=r.Trace;if(t==null)return;t.HasGoal=r.Path!=null;t.GoalX=r.Goal.x;t.GoalY=r.Goal.y;t.GoalZ=r.Goal.z;t.Alternatives=r.Alternatives.ToArray();t.ValidFor=t.State=="ended"?0:1;
  string key=t.State+"|"+t.Reason+"|"+t.Cloud+"|"+t.Protection;
  if(key==r.Key&&Time.time-r.LogAt<.5f&&!t.Cough)return;r.Key=key;r.LogAt=Time.time;
  var json=Diagnostics.Serialize(new{schema=1,session,sequence=++sequence,feature="G08",stage="evaluated",decision="GasExposure",reason=t.Reason,realtime=Time.realtimeSinceStartup,utc=DateTime.UtcNow.ToString("O"),configRevision=config?.Revision??0,profileId=s.ProfileId,name=s.Name,botInstance=s.Id,gasExposure=t,gasExposureConfig=config?.GasExposure});
  diagnostics?.Write(json);Api.Decisions.Publish(json,ex=>{if(!subscriberWarned){subscriberWarned=true;Logger.LogWarning(ex.Message);}});
 }
}
