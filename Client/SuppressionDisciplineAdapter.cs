using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Comet.Core;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using Newtonsoft.Json;
using SAIN.Components;
using SAIN.Components.PlayerComponentSpace;
using SAIN.Models.Enums;
using SAIN.Preset.Shared.Enums;
using SAIN.Preset.Shared.GlobalSettings;
using SAIN.SAINComponent.Classes;
using SAIN.SAINComponent.Classes.EnemyClasses;
using SAIN.SAINComponent.Classes.WeaponFunction;
using UnityEngine;

namespace Comet.Client;

internal sealed class SuppressionDisciplineRuntime
{
    internal readonly SuppressionDisciplinePolicy Policy=new();
    internal BotComponent? Owner;
    internal Enemy? Enemy;
    internal Weapon? Weapon;
    internal bool InRequest,Owned,Stopping,Fault;
    internal float NativeStarted,LogAt=-1000;
    internal int SafetyFrame=-1;
    internal SuppressionDisciplineFrame? Safety;
    internal string LogKey="";
    internal readonly RaycastHit[] Hits=new RaycastHit[32];
    internal readonly Collider[] Overlaps=new Collider[32];
}
public sealed partial class Plugin
{
    // Harmony stores __state per declaring type: keep this module independent of other hooks.
    private static class I01DisciplineHooks
    {
        internal static bool DisciplineRequest(ManualShootClass __instance,Enemy Enemy,ref Vector3 targetPos,EShootReason reason,ref bool __result,ref BotState? __state) => Plugin.DisciplineRequest(__instance,Enemy,ref targetPos,reason,ref __result,ref __state);
        internal static void DisciplineResult(ManualShootClass __instance,bool __result,BotState? __state) => Plugin.DisciplineResult(__instance,__result,__state);
        internal static Exception? DisciplineException(Exception? __exception,BotState? __state) => Plugin.DisciplineException(__exception,__state);
    }

    private static FieldInfo disciplineEnemy=null!;
    private static void PatchSuppressionDiscipline(Harmony h)
    {
        disciplineEnemy=AccessTools.Field(typeof(SAINBotSuppressClass),"EnemyBeingSuppressed")??throw new MissingFieldException("W07 suppression target");
        h.Patch(AccessTools.Method(typeof(ManualShootClass),nameof(ManualShootClass.TryShoot)),
            prefix:new HarmonyMethod(typeof(I01DisciplineHooks),nameof(I01DisciplineHooks.DisciplineRequest)){priority=Priority.Last},
            postfix:new HarmonyMethod(typeof(I01DisciplineHooks),nameof(I01DisciplineHooks.DisciplineResult)),finalizer:new HarmonyMethod(typeof(I01DisciplineHooks),nameof(I01DisciplineHooks.DisciplineException)));
        h.Patch(AccessTools.Method(typeof(ManualShootClass),nameof(ManualShootClass.ManualUpdate)),prefix:new HarmonyMethod(typeof(Plugin),nameof(DisciplineUpdate)));
        h.Patch(AccessTools.Method(typeof(ManualShootClass),nameof(ManualShootClass.Reset)),postfix:new HarmonyMethod(typeof(Plugin),nameof(DisciplineReset)));
        h.Patch(AccessTools.Method(typeof(SAINBotSuppressClass),nameof(SAINBotSuppressClass.TrySuppressAnyEnemy)),prefix:new HarmonyMethod(typeof(Plugin),nameof(DisciplineAmmoGuard)));
        h.Patch(AccessTools.Method(typeof(SAINBotSuppressClass),"UpdateSuppressionVector"),prefix:new HarmonyMethod(typeof(Plugin),nameof(DisciplineVector)));
        h.Patch(AccessTools.Method(typeof(SAINBotSuppressClass),nameof(SAINBotSuppressClass.SuppressPosition)),postfix:new HarmonyMethod(typeof(Plugin),nameof(DisciplinePositionResult)));
        h.Patch(AccessTools.Method(typeof(SAINBotSuppressClass),nameof(SAINBotSuppressClass.ResetSuppressing)),transpiler:new HarmonyMethod(typeof(Plugin),nameof(DisciplineResetCalls)));
        h.Patch(AccessTools.Method(typeof(PlayerComponent),nameof(PlayerComponent.OnMakingShot)),postfix:new HarmonyMethod(typeof(Plugin),nameof(DisciplineShot)));
        h.Patch(AccessTools.Method(typeof(SAINShootData),"AimAndShootAtEnemy"),prefix:new HarmonyMethod(typeof(Plugin),nameof(DisciplineVisibleFire)));
    }
    private bool DisciplineScope(BotComponent b)=>ContactSafety.AllowsGoal(b)&&config?.Enabled==true&&config.SuppressionDiscipline.Enabled&&NativeAdapter.IsPmcAi(b)
        &&b.BotActive&&!b.IsDead&&!b.GameEnding&&Authority&&!Ghost(b.ProfileId)&&b.SAINLayersActive;
    private SuppressionDisciplineFrame DisciplineFrame(BotComponent b,BotState s,Enemy? e)
    {
        var r=s.SuppressionDiscipline;var firearm=b.Player.HandsController as Player.FirearmController;var w=firearm?.Item;
        var f=new SuppressionDisciplineFrame{Now=Time.time,Target=e?.EnemyProfileId??"",Identity=e==null?"":e.EnemyProfileId+":"+RuntimeHelpers.GetHashCode(e),
            Weapon=w?.Id.ToString()??"",Mode=w?.SelectedFireMode.ToString()??"",Action=b.Decision.CurrentCombatDecision.ToString(),
            KnownContact=e?.EnemyKnown==true,Visible=e?.IsVisible==true};
        if(s.Crossfire.ManeuverFire&&s.Crossfire.Maneuver!=null)
        {f.SupportPlan=s.Crossfire.Maneuver.Id;f.SupportUseful=s.Crossfire.Maneuver.MaySuppress&&s.Crossfire.Policy.Active&&s.Crossfire.Layer;}
        var seen=e?.KnownPlaces.LastSeenPlace;var heard=e?.KnownPlaces.LastHeardPlace;
        if(seen!=null){f.ObservedAt=Time.time-seen.TimeSincePositionUpdated;f.Known=DP(seen.Position);f.Source="personal-vision";}
        // Incoming fire is usable only when it also produced a recent personal localized observation.
        if(e!=null&&heard!=null)
        {
            double heardAt=Time.time-heard.TimeSincePositionUpdated;
            double incoming=Math.Max(e.Status.ShotAtMe?e.Status.TimeLastShotAtMe:-1,e.Status.ShotMe?e.Status.TimeLastShotMe:-1);
            if(incoming>=0&&heardAt>=incoming-.25&&Time.time-incoming<=config!.SuppressionDiscipline.ContactAge&&heardAt>f.ObservedAt)
            {f.ObservedAt=heardAt;f.Known=DP(heard.Position);f.Source="personal-incoming";}
        }
        if(w!=null)
        {
            // Preserve a fraction of the magazine itself. A chambered round is an additional buffer.
            var mag=w.GetCurrentMagazine();f.Loaded=w.ReloadMode==Weapon.EReloadMode.OnlyBarrel?w.ChamberAmmoCount:mag?.Count??0;
            f.Capacity=w.ReloadMode==Weapon.EReloadMode.OnlyBarrel?w.Chambers.Length:mag?.MaxCount??0;
        }
        if(!DisciplineScope(b))f.Block="suppression-out-of-scope";
        else if(r.Fault)f.Block="suppression-adapter-error";
        else if(!SainPresetAccess.AllowsSuppression||!b.Info.PersonalitySettings.General.TARGET_SUPPRESS_TOGGLE)f.Block="suppression-sain-disabled";
        else if(s.CornerPrefire.Policy.Active||s.Sight.Current!=null)f.Block="suppression-other-tactic";
        else if(w==null||firearm==null||!b.BotOwner.WeaponManager.IsWeaponReady||b.BotOwner.WeaponManager.Selector.IsChanging
            ||b.BotOwner.WeaponManager.Reload.Reloading||firearm.IsInReloadOperation()||firearm.IsInInteraction()||w.IsUnderBarrelDeviceActive
            ||b.Decision.CurrentSelfDecision!=ESelfActionType.None||b.BotOwner.Medecine?.Using==true||FakeMedicalPriority(b)
            ||b.Grenade.GrenadeDangerPoint!=null||b.Flashed.IsFlashed||b.Player.IsSprintEnabled||b.Mover.Running
            ||s.ReloadTiming.Policy.Active||s.PistolSwap.Policy.Active||s.WeaponReturn.Policy.Active||s.QuickReload.Policy.Active||s.WeaponRange.Policy.Pending)
            f.Block="suppression-native-priority";
        else if(w.SelectedFireMode is not (Weapon.EFireMode.fullauto or Weapon.EFireMode.single or Weapon.EFireMode.doubleaction))f.Block="suppression-unsupported-fire-mode";
        // Any personally visible target wins immediately over an unseen suppression target.
        int count=0;foreach(var contact in b.EnemyController.KnownEnemies)
        {if(++count>16){f.Block="suppression-contact-scan-incomplete";break;}if(contact?.IsVisible==true){f.Visible=true;break;}}
        return f;
    }
    private bool DisciplineGeometry(BotComponent b,BotState s,SuppressionDisciplineFrame f)
    {
        var r=s.SuppressionDiscipline;
        var geometry=CheckShotGeometry(b,s,VP(r.Policy.Aim),"W07",f.Target);
        if(geometry!=null)
        {
            f.FreshGeometry=geometry.Fresh;f.Clear=geometry.Allowed;f.AllySafe=geometry.AllySafe;
            f.Aligned=Vector3.Angle(b.Transform.WeaponData.PointDirection,VP(r.Policy.Aim)-b.Transform.WeaponData.FirePort)<=8&&b.Steering.AngleToPointFromLookDir(VP(r.Policy.Aim))<=8;
            r.SafetyFrame=Time.frameCount;r.Safety=f;return f.FreshGeometry;
        }
        if(r.SafetyFrame==Time.frameCount&&r.Safety!=null)
        {f.FreshGeometry=r.Safety.FreshGeometry;f.Clear=r.Safety.Clear;f.AllySafe=r.Safety.AllySafe;f.Aligned=r.Safety.Aligned;return f.FreshGeometry;}
        if(!doorBudget.TryBegin(Time.frameCount))return false;
        var watch=System.Diagnostics.Stopwatch.StartNew();
        try {
            var aim=VP(r.Policy.Aim);var muzzle=b.Transform.WeaponData.FirePort;var ray=aim-muzzle;float length=ray.magnitude;
            if(length<.1f||!CornerChase.Point(DP(muzzle)))return false;
            var dir=ray.normalized;var actual=b.Transform.WeaponData.PointDirection;
            f.FreshGeometry=true;f.Aligned=Vector3.Angle(actual,dir)<=8&&b.Steering.AngleToPointFromLookDir(aim)<=8;
            f.Clear=!Physics.CheckSphere(muzzle,.04f,LayersMaskController.HighPolyWithTerrainMask,QueryTriggerInteraction.Ignore)
                &&!Physics.SphereCast(muzzle,.04f,dir,out _,length,LayersMaskController.HighPolyWithTerrainMask,QueryTriggerInteraction.Ignore)
                &&!Physics.SphereCast(muzzle,.04f,actual,out _,length,LayersMaskController.HighPolyWithTerrainMask,QueryTriggerInteraction.Ignore);
            f.AllySafe=DisciplineAllies(b,r,muzzle,dir,length)&&DisciplineAllies(b,r,muzzle,actual,length);
            r.SafetyFrame=Time.frameCount;r.Safety=f;return true;
        } finally {watch.Stop();doorBudget.Charge(watch.Elapsed.TotalMilliseconds);}
    }
    private static bool DisciplineAllies(BotComponent b,SuppressionDisciplineRuntime r,Vector3 muzzle,Vector3 dir,float length)
    {
        var world=GameWorldComponent.Instance?.GameWorld;if(world==null)return false;
        int n=Physics.OverlapSphereNonAlloc(muzzle,.2f,r.Overlaps,LayersMaskController.PlayerMask,QueryTriggerInteraction.Ignore);
        if(n==r.Overlaps.Length)return false;
        for(int i=0;i<n;i++){var p=world.GetPlayerByCollider(r.Overlaps[i]);if(p!=null&&p.ProfileId!=b.ProfileId&&!b.EnemyController.IsPlayerAnEnemy(p.ProfileId))return false;}
        n=Physics.SphereCastNonAlloc(muzzle,.2f,dir,r.Hits,length,LayersMaskController.PlayerMask,QueryTriggerInteraction.Ignore);
        if(n==r.Hits.Length)return false;
        for(int i=0;i<n;i++){var p=world.GetPlayerByCollider(r.Hits[i].collider);if(p!=null&&p.ProfileId!=b.ProfileId&&!b.EnemyController.IsPlayerAnEnemy(p.ProfileId))return false;}
        return true;
    }
    private static bool OwnDiscipline(SuppressionDisciplineRuntime r)
    {
        var m=r.Owner?.ManualShoot;
        return r.Owned&&m!=null&&m.Reason==EShootReason.Suppress&&ReferenceEquals(prefireEnemy.GetValue(m),r.Enemy)
            &&(m.ShootPosition-VP(r.Policy.Aim)).sqrMagnitude<.0001f&&(float)prefireStarted.GetValue(m)==r.NativeStarted;
    }
    private static bool DisciplineRequest(ManualShootClass __instance,Enemy Enemy,ref Vector3 targetPos,EShootReason reason,ref bool __result,ref BotState? __state)
    {
        var p=Instance;var b=__instance.Bot;
        if(p==null||!p.DisciplineScope(b))return true;
        if(!p.states.TryGetValue(b,out var s)){s=new BotState(b,p.config!,p.session);p.states.Add(b,s);}
        if(s.CornerPrefire.InRequest||reason!=EShootReason.Suppress){p.EndDiscipline(s,"suppression-other-shooter");return true;}
        var r=s.SuppressionDiscipline;if(r.Fault)return true;
        try {
            var f=p.DisciplineFrame(b,s,Enemy);
            if(r.Policy.Active&&(!ReferenceEquals(r.Enemy,Enemy)||!ReferenceEquals(r.Weapon,b.Player.HandsController?.Item)||r.Policy.Trace!.Action!=f.Action))p.EndDiscipline(s,"suppression-context-changed");
            if(!r.Policy.Active){r.Owner=b;r.Enemy=Enemy;r.Weapon=b.Player.HandsController?.Item as Weapon;}
            var before=r.Policy.Aim;
            r.Policy.Evaluate(f,p.config!.SuppressionDiscipline);
            if(!r.Policy.Active||r.Policy.Trace!.State=="paused") {p.StopDisciplineFire(s);p.RecordDiscipline(s);__result=false;return false;}
            if((r.Policy.Aim-before).Length>0)r.SafetyFrame=-1;
            p.DisciplineGeometry(b,s,f);
            if(!r.Policy.Evaluate(f,p.config.SuppressionDiscipline)){p.StopDisciplineFire(s);p.RecordDiscipline(s);__result=false;return false;}
            if(__instance.Reason!=EShootReason.None&&!OwnDiscipline(r)) {p.EndDiscipline(s,"suppression-native-shooter-busy");__result=false;return false;}
            r.Owner=b;r.Enemy=Enemy;r.Weapon=b.Player.HandsController?.Item as Weapon;r.InRequest=true;__state=s;
            targetPos=VP(r.Policy.Aim);r.Policy.Request();return true;
        }catch(Exception ex){p.DisciplineError(s,ex);__result=false;return false;}
    }
    private static void DisciplineResult(ManualShootClass __instance,bool __result,BotState? __state)
    {
        var p=Instance;if(p==null||__state==null)return;var r=__state.SuppressionDiscipline;
        try {r.InRequest=false;r.Owned=__result;r.NativeStarted=(float)prefireStarted.GetValue(__instance);r.Policy.Result(__result);
            if(!r.Policy.MayFire)p.StopDisciplineFire(__state);p.RecordDiscipline(__state);}
        catch(Exception ex){p.DisciplineError(__state,ex);}
    }
    private static Exception? DisciplineException(Exception? __exception,BotState? __state)
    {if(__exception!=null&&__state!=null){__state.SuppressionDiscipline.InRequest=false;Instance?.DisciplineError(__state,__exception);}return __exception;}
    private void SampleDiscipline(BotComponent b,BotState s)
    {
        var r=s.SuppressionDiscipline;if(r.InRequest)return;
        if(!r.Policy.Active){ClearDisciplineTarget(s);ClearDisciplineReferences(r);return;}
        var f=DisciplineFrame(b,s,r.Enemy);
        if(!ReferenceEquals(r.Weapon,b.Player.HandsController?.Item)||r.Policy.Trace!.Action!=f.Action){EndDiscipline(s,"suppression-context-changed");return;}
        var aim=r.Policy.Aim;r.Policy.Evaluate(f,config!.SuppressionDiscipline);
        if(!r.Policy.Active||r.Policy.Trace!.State=="paused"){StopDisciplineFire(s);RecordDiscipline(s);return;}
        if((aim-r.Policy.Aim).Length>0)r.SafetyFrame=-1;
        DisciplineGeometry(b,s,f);r.Policy.Evaluate(f,config.SuppressionDiscipline);
        if(!r.Policy.MayFire)StopDisciplineFire(s);RecordDiscipline(s);
    }
    private static void DisciplineUpdate(ManualShootClass __instance)
    {var p=Instance;if(p==null||!p.states.TryGetValue(__instance.Bot,out var s))return;try {p.SampleDiscipline(__instance.Bot,s);}catch(Exception ex){p.DisciplineError(s,ex);}}
    private static void DisciplineShot(PlayerComponent __instance,IWeapon __0)
    {
        var p=Instance;var b=__instance.BotComponent;if(p?.config==null||b==null||!p.states.TryGetValue(b,out var s))return;
        var r=s.SuppressionDiscipline;if(!r.Policy.Active||!ReferenceEquals(__0,r.Weapon)||(!r.InRequest&&!OwnDiscipline(r))||s.CornerPrefire.InRequest)return;
        try {
            var f=p.DisciplineFrame(b,s,r.Enemy);var safe=r.Safety;
            f.FreshGeometry=safe?.FreshGeometry==true&&Time.frameCount-r.SafetyFrame<=1;f.Clear=safe?.Clear==true;f.AllySafe=safe?.AllySafe==true;f.Aligned=safe?.Aligned==true;
            r.Policy.Shot(f,p.config.SuppressionDiscipline,p.jumpRandom.NextDouble());p.ManeuverShot(s);
            if(SuppressionDisciplinePolicy.Gate(f,p.config.SuppressionDiscipline).Length>0)r.Policy.End(SuppressionDisciplinePolicy.Gate(f,p.config.SuppressionDiscipline));
            if(!f.FreshGeometry||!f.Clear||!f.AllySafe||!f.Aligned)r.Policy.End("suppression-shot-safety-expired");
            if(!r.Policy.MayFire&&!r.InRequest)p.StopDisciplineFire(s);p.RecordDiscipline(s,true);
        }catch(Exception ex){p.DisciplineError(s,ex);}
    }
    private static void DisciplineVisibleFire(Enemy Enemy,BotComponent bot)
    {var p=Instance;if(Enemy?.IsVisible==true&&p!=null&&p.states.TryGetValue(bot,out var s))p.EndDiscipline(s,"suppression-visible-target");}
    private static void DisciplineReset(ManualShootClass __instance)
    {var p=Instance;if(p!=null&&p.states.TryGetValue(__instance.Bot,out var s)&&!s.SuppressionDiscipline.InRequest)s.SuppressionDiscipline.Owned=false;}
    private static bool DisciplineAmmoGuard(SAINBotSuppressClass __instance,ref float minimumAmmoRatio,ref bool __result)
    {
        var p=Instance;var b=__instance.Bot;if(p==null||!p.DisciplineScope(b))return true;
        if(p.states.TryGetValue(b,out var existing)&&existing.SuppressionDiscipline.Fault)return true;
        var w=(b.Player.HandsController as Player.FirearmController)?.Item;
        if(w==null||b.BotOwner.WeaponManager.Reload?.Reloading!=false||w.ReloadMode!=Weapon.EReloadMode.OnlyBarrel&&w.GetCurrentMagazine()==null
            ||p.states.TryGetValue(b,out var s)&&s.CornerPrefire.Policy.Active){__result=false;return false;}
        minimumAmmoRatio=Math.Max(minimumAmmoRatio,(float)p.config!.SuppressionDiscipline.ReservePercent/100);return true;
    }
    private static bool DisciplineVector(SAINBotSuppressClass __instance)
    {
        var p=Instance;if(p==null||!p.states.TryGetValue(__instance.Bot,out var s)||!p.DisciplineScope(__instance.Bot))return true;
        var r=s.SuppressionDiscipline;if(r.Fault)return true;
        // Never refresh a hidden enemy's live suppression vector; use this episode's perceived point.
        if(OwnDiscipline(r)){__instance.Bot.ManualShoot.ShootPosition=VP(r.Policy.Aim);return false;}
        p.ClearDisciplineTarget(s);__instance.SuppressingTarget=false;return false;
    }
    private static void DisciplinePositionResult(SAINBotSuppressClass __instance,ref bool __result)
    {
        var p=Instance;if(p==null||!p.states.TryGetValue(__instance.Bot,out var s)||!p.DisciplineScope(__instance.Bot)||s.SuppressionDiscipline.Fault)return;
        if(!s.SuppressionDiscipline.Policy.MayFire){__result=false;p.ClearDisciplineTarget(s);}
    }
    private static IEnumerable<CodeInstruction> DisciplineResetCalls(IEnumerable<CodeInstruction> input)
    {
        int n=0;var native=AccessTools.Method(typeof(ManualShootClass),nameof(ManualShootClass.Reset));
        foreach(var i in input){if(i.Calls(native)){i.opcode=OpCodes.Call;i.operand=AccessTools.Method(typeof(Plugin),nameof(DisciplineResetNative));n++;}yield return i;}
        if(n!=1)throw new InvalidOperationException("W07 unexpected native suppression reset contract");
    }
    private static void DisciplineResetNative(ManualShootClass m)
    {
        var p=Instance;
        if(p!=null&&p.DisciplineScope(m.Bot)&&p.states.TryGetValue(m.Bot,out var s)&&!s.SuppressionDiscipline.Fault
            &&(s.CornerPrefire.Policy.Active||m.Reason!=EShootReason.None&&!OwnDiscipline(s.SuppressionDiscipline)))return;
        m.Reset();
    }
    private void ClearDisciplineTarget(BotState s)
    {
        var r=s.SuppressionDiscipline;var suppression=r.Owner?.Suppression;
        if(suppression!=null&&ReferenceEquals(disciplineEnemy.GetValue(suppression),r.Enemy))
        {if(r.Enemy!=null)r.Enemy.Status.EnemyIsSuppressed=false;suppression.SuppressingTarget=false;disciplineEnemy.SetValue(suppression,null);}
    }
    private void StopDisciplineFire(BotState s)
    {
        var r=s.SuppressionDiscipline;if(r.InRequest)return;
        try {if(OwnDiscipline(r)){r.Stopping=true;r.Owner!.ManualShoot.Reset();}ClearDisciplineTarget(s);}
        finally {r.Stopping=false;r.Owned=false;}
    }
    private void EndDiscipline(BotState s,string reason)
    {
        var r=s.SuppressionDiscipline;r.Policy.End(reason);StopDisciplineFire(s);RecordDiscipline(s);
        if(!r.InRequest)ClearDisciplineReferences(r);
    }
    private static void ClearDisciplineReferences(SuppressionDisciplineRuntime r)
    {r.Owner=null;r.Enemy=null;r.Weapon=null;r.Safety=null;r.SafetyFrame=-1;Array.Clear(r.Hits,0,r.Hits.Length);Array.Clear(r.Overlaps,0,r.Overlaps.Length);}
    private void DisciplineError(BotState s,Exception ex)
    {var r=s.SuppressionDiscipline;bool warn=!r.Fault;r.Fault=true;EndDiscipline(s,"suppression-adapter-error");if(warn)Logger.LogWarning("W07 yielded: "+ex.GetBaseException().Message);}
    private void RecordDiscipline(BotState s,bool force=false)
    {
        var r=s.SuppressionDiscipline;var t=r.Policy.Trace;if(t==null)return;
        string key=t.Episode+":"+t.State+":"+t.Reason+":"+t.Accepted+":"+t.ActualShots;
        if(!force&&key==r.LogKey&&(!r.Policy.Active||Time.time-r.LogAt<.2f))return;r.LogKey=key;r.LogAt=Time.time;
        var json=JsonConvert.SerializeObject(new{schema=1,session,sequence=++sequence,feature="W07",stage="evaluated",decision="SuppressionDiscipline",reason=t.Reason,
            realtime=Time.realtimeSinceStartup,utc=DateTime.UtcNow.ToString("O"),configRevision=config?.Revision??0,profileId=s.ProfileId,name=s.Name,botInstance=s.Id,
            suppressionDiscipline=t,suppressionDisciplineConfig=config?.SuppressionDiscipline},Diagnostics.Json);
        diagnostics?.Write(json);Api.Decisions.Publish(json,ex=>{if(!subscriberWarned){subscriberWarned=true;Logger.LogWarning(ex.Message);}});
    }
}
