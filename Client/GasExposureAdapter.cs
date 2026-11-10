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
 internal float Next,Last,NextCough,AttemptUntil,RetryAt,ProbeAt,LogAt=-1000;
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
   var action=typeof(BotComponent).Assembly.GetType("SAIN.Layers.Combat.Solo.AvoidGrenadeAction",true)!;
   h.Patch(AccessTools.Method(action,"Stop"),postfix:new HarmonyMethod(typeof(Plugin),nameof(GasActionStopped)));
   h.Patch(AccessTools.Method(typeof(BotComponent),nameof(BotComponent.ManualUpdate)),postfix:new HarmonyMethod(typeof(Plugin),nameof(GasBotUpdate)));
   h.Patch(AccessTools.Method(typeof(BotDecisionManager),"getDecision"),prefix:new HarmonyMethod(typeof(Plugin),nameof(GasDecision)){priority=Priority.First});
   Logger.LogInfo("G08 ready: "+gasBridge.Status+"; awake authoritative PMCs and AI Player Scavs only");
  }catch(Exception ex){DisableGas(ex);}
 }
 internal static void GasCloudCreated(Component __instance){try{Instance?.gasBridge.Add(__instance,false);}catch(Exception e){Instance?.DisableGas(e);}}
 internal static void GasEmitterCreated(Component __result){try{Instance?.gasBridge.Add(__result,true);}catch(Exception e){Instance?.DisableGas(e);}}
 private bool GasScope(BotComponent b)=>gasBridge.Ready&&world!=null&&config?.Enabled==true&&config.GasExposure.Enabled&&b!=null&&!b.IsDead
  &&NativeAdapter.IsSupportedAi(b)&&b.BotActive&&!b.GameEnding&&Authority&&!Ghost(b.ProfileId);
 private void DisableGas(Exception ex)
 {gasBridge.Ready=false;gasBridge.Clear();Logger.LogWarning("G08 inactive, native AI retained: "+ex.GetBaseException().Message);foreach(var pair in states)DropGas(pair.Key,pair.Value,"gas-provider-unavailable");}
 // Runs before the existing G04/G05 arbitration. Only a verified Manimal grenade is removed
 // from native explosive selection; known explosive tracks remain in their original dictionary.
 private void FilterNativeGas(GrenadeReactionClass native)
 {
  if(!GasScope(native.Bot)||native.DangerGrenade?.Grenade is not { } gas||!gasBridge.Owns(gas))return;
  try{
   GrenadeTrackerClass? selected=null;float nearest=324;
   foreach(var t in native.EnemyGrenadesList.Values){
    if(t?.Grenade==null||!t.CanReact||gasBridge.Owns(t.Grenade)||t.Grenade is SmokeGrenade)continue;
    float d=(t.DangerPoint-native.Bot.Position).sqrMagnitude;if(d<nearest){nearest=d;selected=t;}
   }
   ownDangerSetter.Invoke(native,new object?[]{selected});
   var reaction=selected==null?EGrenadeReaction.None:(EGrenadeReaction)gasNativeReaction.Invoke(native,null)!;
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
  bool inside=gasBridge.Inside(b.Transform.EyePosition,out var cloud,out var center,out int active);
  // Inventory inspection is only relevant to exposure/recovery. Most frames have
  // no gas at this bot, including when the optional provider is installed.
  r.Protection=inside||r.Intensity>0?gasBridge.Protection(b.Player):"None";r.Inside=inside;r.Center=center;
  r.Intensity=GasExposureRules.Exposure(r.Intensity,inside,r.Protection,dt,c);
  // Manimal 2.0.0 affects MainPlayer only. Apply only reversible AI modifiers, no extra damage/buffs/flash state.
  int step=(int)Math.Ceiling(r.Intensity*10);
  if(step!=r.ModifierStep||r.Protection!=r.ModifierProtection){ClearGasModifier(b,r);r.ModifierStep=step;r.ModifierProtection=r.Protection;
   if(step>0){var f=b.BotOwner.Settings.FileSettings.Change;float t=step/10f;
    r.Modifier=new TemporaryStatModifiers(Mathf.Lerp(1,f.FLASH_PRECICING,t),Mathf.Lerp(1,f.FLASH_ACCURATY,t),Mathf.Lerp(1,f.FLASH_GAIN_SIGHT,t),Mathf.Lerp(1,f.FLASH_SCATTERING,t),Mathf.Lerp(1,f.FLASH_SCATTERING,t),Mathf.Lerp(1,f.FLASH_VISION_DIST,t),r.Protection=="None"?Mathf.Lerp(1,f.FLASH_HEARING,t):1);
    b.BotOwner.Settings.Current.Apply(r.Modifier.Modifiers);
   }
  }
  bool cough=GasExposureRules.Cough(inside,r.Protection,r.Intensity)&&Time.time>=r.NextCough;
  if(cough){r.NextCough=Time.time+(float)c.CoughInterval;b.Talk.Say(EPhraseTrigger.OnBreath,ETagStatus.Dying,false,false);}
  bool escape=GasExposureRules.Escape(inside,r.Protection);
  if(!escape){StopGasPath(b,r);r.Owned=false;r.Candidate=0;r.AttemptUntil=0;r.RetryAt=0;}
  if(!inside&&r.Intensity<=0){DropGas(b,s,"gas-recovered",false);return;}
  r.Trace=new(){State=escape?"exposed":r.Intensity>0?"recovering":"protected",Reason=escape?"gas-exposure":inside?"gas-mask-protection":"gas-left-cloud",Cloud=cloud,Clouds=active,
   Protection=r.Protection,Inside=inside,Intensity=r.Intensity,X=center.x,Y=center.y,Z=center.z,Cough=cough};
  RecordGas(s);
 }
 private static bool GasDecision(BotDecisionManager __instance)
 {
  var p=Instance;var b=__instance.Bot;
  if(p==null||!p.GasScope(b)||!p.states.TryGetValue(b,out var s)||s.Gas.Fault)return true;
  try{
   var r=s.Gas;p.SampleGas(b,s);p.FilterNativeGas(b.Grenade.GrenadeReactionClass);
   if(!GasExposureRules.Escape(r.Inside,r.Protection)||Time.time>=r.Next+.5f)return true;
   // Native frag danger always wins. Do not invent a GrenadeTracker or alter its ownership.
   if(b.Grenade.GrenadeReactionClass.ShallAvoidGrenade()||b.Flashed.IsFlashed||b.BotOwner.WeaponManager.Grenades.ThrowindNow
    ||b.BotOwner.DoorOpener.Interacting||b.DoorOpener.Interacting||b.Player.HandsController.IsInInteractionStrictCheck())
   {p.StopGasPath(b,r);r.Owned=false;p.GasState(s,"yielded","gas-native-emergency-priority");return true;}
   if(r.AttemptUntil>0&&Time.time>=r.AttemptUntil){p.StopGasPath(b,r);r.Owned=false;r.AttemptUntil=0;r.RetryAt=Time.time+(float)p.config!.GasExposure.RetryDelay;r.Candidate=0;}
   if(Time.time<r.RetryAt){p.GasState(s,"retry","gas-retry-delay");return true;}
   if(!r.Owned){p.Release(s,"gas-escape-priority");r.Owned=true;r.AttemptUntil=Time.time+(float)p.config!.GasExposure.AttemptDuration;r.Alternatives.Clear();}
   gasSetDecision.Invoke(__instance,new object?[]{ECombatDecision.AvoidGrenade,ESquadDecision.None,ESelfActionType.None,b.GoalEnemy});
   return false;
  }catch(Exception ex){p.GasError(b,ex);return true;}
 }
 private static void GasActionStopped(BotAction __instance)
 {var p=Instance;if(p!=null&&p.states.TryGetValue(__instance.Bot,out var s)&&s.Gas.Owned){p.StopGasPath(__instance.Bot,s.Gas);s.Gas.Owned=false;p.GasState(s,"yielded","gas-native-action-ended");}}
 private bool TryGasAction(BotComponent b)
 {
  if(!states.TryGetValue(b,out var s)||!s.Gas.Owned)return false;
  try{
   var r=s.Gas;
   if(!GasScope(b)||s.Gas.Fault){DropGas(b,s,"gas-out-of-scope");return false;}
   if(b.Grenade.GrenadeReactionClass.ShallAvoidGrenade()){StopGasPath(b,r);r.Owned=false;GasState(s,"yielded","gas-explosive-priority");return false;}
   if(Time.time>=r.AttemptUntil||!GasExposureRules.Escape(r.Inside,r.Protection)){StopGasPath(b,r);return true;}
   if(Time.time<r.ProbeAt)return true;r.ProbeAt=Time.time+(float)config!.GasExposure.Refresh;
   if(r.Path!=null&&ReferenceEquals(r.Path,b.Mover.ActivePath)&&ReferenceEquals(r.Mesh,r.Path.NavMeshPath)
    &&Vector3.Distance(b.Position,r.Goal)>1&&!GasAtFeet(b,r.Goal)&&GasRoute(b,r.Mesh!.corners)){GasState(s,"escaping","gas-safe-route-held");return true;}
   StopGasPath(b,r);
   if(gasProbeFrame!=Time.frameCount){gasProbeFrame=Time.frameCount;gasProbes=0;}
   Vector3 away=b.Position-r.Center;away.y=0;if(away.sqrMagnitude<.1f)away=b.LookDirection*-1;away.y=0;if(away.sqrMagnitude<.1f)away=new(1,0,0);
   float[] angles={0,45,-45,90,-90,135,-135,180};int local=0;
   while(r.Candidate<24&&local++<2&&gasProbes<8){gasProbes++;int i=r.Candidate++;var goal=b.Position+Quaternion.Euler(0,angles[i%8],0)*away.normalized*(8+8*(i/8));string why="no-navmesh";
    if(NavMesh.SamplePosition(goal,out var hit,2,NavMesh.AllAreas)){
     if(GasAtFeet(b,hit.position))why="destination-in-gas";
     else{var path=new NavMeshPath();if(!NavMesh.CalculatePath(b.Position,hit.position,NavMesh.AllAreas,path)||path.status!=NavMeshPathStatus.PathComplete||path.corners.Length<2||Vector3.Distance(path.corners[0],b.Position)>2||Vector3.Distance(path.corners[path.corners.Length-1],hit.position)>.5f)why="incomplete-path";
      else if(!GasRoute(b,path.corners))why="route-reenters-gas";
      else if(b.Mover.RunToPointByWay(path,true,.5f,ESprintUrgency.High,false)){
       var applied=b.Mover.ActivePath;
       if(applied!=null&&ReferenceEquals(applied.NavMeshPath,path)){r.Path=applied;r.Mesh=path;r.Goal=hit.position;GasState(s,"escaping","gas-safe-route-started");return true;}
       r.Owned=false;r.RetryAt=Time.time+(float)config.GasExposure.RetryDelay;GasState(s,"yielded","gas-native-route-not-owned");return false;
      }else why="native-movement-refused";
     }
    }
    if(r.Alternatives.Count<8)r.Alternatives.Add("Candidate "+(i+1)+": "+why);
   }
   if(r.Candidate>=24){r.Owned=false;r.Candidate=0;r.AttemptUntil=0;r.RetryAt=Time.time+(float)config.GasExposure.RetryDelay;GasState(s,"retry","gas-no-reachable-exit");}
   else GasState(s,"planning","gas-search-budget-yield");
   return true;
  }catch(Exception ex){GasError(b,ex);return false;}
 }
 private bool GasAtFeet(BotComponent b,Vector3 feet)=>gasBridge.Inside(feet+(b.Transform.EyePosition-b.Position),out _,out _,out _);
 private bool GasRoute(BotComponent b,Vector3[] points)
 {
  // Sample bounded route segments, rejecting a detour through another cloud after reaching clear air.
  bool clear=false;int probes=0;
  for(int i=1;i<points.Length;i++){float length=Vector3.Distance(points[i-1],points[i]);int n=Math.Max(1,(int)Math.Ceiling(length/2));
   for(int j=1;j<=n;j++){if(++probes>32)return false;bool inside=GasAtFeet(b,Vector3.Lerp(points[i-1],points[i],j/(float)n));if(clear&&inside)return false;if(!inside)clear=true;}}
  return clear;
 }
 private static void ClearGasModifier(BotComponent b,GasRuntime r){if(r.Modifier!=null){b.BotOwner.Settings.Current.Dismiss(r.Modifier.Modifiers);r.Modifier=null;}r.ModifierStep=-1;}
 private void StopGasPath(BotComponent? b,GasRuntime r){if(b!=null&&r.Path!=null&&ReferenceEquals(b.Mover.ActivePath,r.Path)&&ReferenceEquals(r.Path.NavMeshPath,r.Mesh))combatStopPath.Invoke(r.Path,new object[]{false,"COMET G08 escape ended"});r.Path=null;r.Mesh=null;}
 private void DropGas(BotComponent? b,BotState s,string reason,bool resetSampling=true)
 {var r=s.Gas;StopGasPath(b,r);if(b!=null)ClearGasModifier(b,r);r.Intensity=0;r.Inside=r.Owned=false;r.AttemptUntil=r.RetryAt=0;if(resetSampling)r.Next=r.Last=0;r.Candidate=0;if(r.Trace!=null&&r.Trace.State!="ended")GasState(s,"ended",reason);}
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
