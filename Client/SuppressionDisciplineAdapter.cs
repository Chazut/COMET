using System;
using System.Runtime.CompilerServices;
using Comet.Core;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using SAIN.Components;
using SAIN.Components.PlayerComponentSpace;
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
    internal bool Fault;
    internal float LogAt=-1000;
    internal string LogKey="";
}
public sealed partial class Plugin
{
    // Source: zzap SAINBotSuppressClass. Do not intercept the generic shooter:
    // burst accounting belongs to successful native SuppressPosition calls.
    private static void PatchSuppressionDiscipline(Harmony h)
    {
        h.Patch(AccessTools.Method(typeof(SAINBotSuppressClass),nameof(SAINBotSuppressClass.TrySuppressAnyEnemy)),prefix:new HarmonyMethod(typeof(Plugin),nameof(DisciplineAmmoGuard)));
        h.Patch(AccessTools.Method(typeof(SAINBotSuppressClass),nameof(SAINBotSuppressClass.TrySuppressEnemy)),prefix:new HarmonyMethod(typeof(Plugin),nameof(DisciplinePauseGuard)));
        h.Patch(AccessTools.Method(typeof(SAINBotSuppressClass),"CanSuppressEnemy"),postfix:new HarmonyMethod(typeof(Plugin),nameof(DisciplineContactGuard)));
        h.Patch(AccessTools.Method(typeof(SAINBotSuppressClass),nameof(SAINBotSuppressClass.SuppressPosition)),postfix:new HarmonyMethod(typeof(Plugin),nameof(DisciplinePositionResult)));
        h.Patch(AccessTools.Method(typeof(PlayerComponent),nameof(PlayerComponent.OnMakingShot)),postfix:new HarmonyMethod(typeof(Plugin),nameof(DisciplineShot)));
    }
    private bool DisciplineScope(BotComponent b)=>ContactSafety.AllowsGoal(b)&&config?.Enabled==true&&config.SuppressionDiscipline.Enabled&&NativeAdapter.IsSupportedAi(b)
        &&b.BotActive&&!b.IsDead&&!b.GameEnding&&Authority&&!Ghost(b.ProfileId)&&b.SAINLayersActive;
    private SuppressionDisciplineFrame DisciplineFrame(BotComponent b,BotState s,Enemy? e)
    {
        var r=s.SuppressionDiscipline;var firearm=b.Player.HandsController as Player.FirearmController;var w=firearm?.Item;
        var f=new SuppressionDisciplineFrame{Now=Time.time,Target=e?.EnemyProfileId??"",Identity=e==null?"":e.EnemyProfileId+":"+RuntimeHelpers.GetHashCode(e),
            Weapon=w?.Id.ToString()??"",Mode=w?.SelectedFireMode.ToString()??"",Action=b.Decision.CurrentCombatDecision.ToString(),
            KnownContact=e?.EnemyKnown==true,Visible=e?.IsVisible==true};
        if(s.Crossfire.ManeuverFire&&s.Crossfire.Maneuver!=null)
        {f.SupportPlan=s.Crossfire.Maneuver.Id;f.SupportUseful=s.Crossfire.Maneuver.MaySuppress&&s.Crossfire.Policy.Active&&s.Crossfire.Layer;}
        if(e!=null)
        {
            var known=e.KnownPlaces.LastKnownPlace;
            if(known!=null)f.Known=DP(known.Position);
            if(e.Seen) { f.ObservedAt=Time.time-e.TimeSinceSeen; f.Source="personal-vision"; }
            double incoming=Math.Max(e.Status.ShotAtMe?e.Status.TimeLastShotAtMe:-1,e.Status.ShotMe?e.Status.TimeLastShotMe:-1);
            if(incoming>f.ObservedAt) { f.ObservedAt=incoming; f.Source="personal-incoming"; }
        }
        if(w!=null && (w.ReloadMode==Weapon.EReloadMode.OnlyBarrel || w.GetCurrentMagazine()!=null))
        {f.Loaded=b.BotOwner.WeaponManager.Reload.BulletCount;f.Capacity=b.BotOwner.WeaponManager.Reload.MaxBulletCount;}
        if(!DisciplineScope(b))f.Block="suppression-out-of-scope";
        else if(r.Fault)f.Block="suppression-adapter-error";
        else if(!SainPresetAccess.AllowsSuppression||!b.Info.PersonalitySettings.General.TARGET_SUPPRESS_TOGGLE)f.Block="suppression-sain-disabled";
        return f;
    }

    private static bool DisciplinePauseGuard(SAINBotSuppressClass __instance,ref bool __result)
    {
        var p=Instance;
        if(p!=null&&p.DisciplineScope(__instance.Bot)&&p.states.TryGetValue(__instance.Bot,out var s)&&!s.SuppressionDiscipline.Fault&&s.SuppressionDiscipline.Policy.NativePaused(Time.time))
        {__result=false;return false;}return true;
    }
    private static bool DisciplineAmmoGuard(SAINBotSuppressClass __instance,ref float minimumAmmoRatio,ref bool __result)
    {
        var p=Instance;var b=__instance.Bot;if(p==null||!p.DisciplineScope(b))return true;
        if(!DisciplinePauseGuard(__instance,ref __result))return false;
        var w=b.BotOwner.WeaponManager.ShootController?.Item;
        if(w==null||w.ReloadMode!=Weapon.EReloadMode.OnlyBarrel&&w.GetCurrentMagazine()==null){__result=false;__instance.ResetSuppressing();return false;}
        minimumAmmoRatio=Math.Max(minimumAmmoRatio,(float)p.config!.SuppressionDiscipline.ReservePercent/100);return true;
    }
    private static void DisciplineContactGuard(SAINBotSuppressClass __instance,Enemy Enemy,ref bool __result)
    {
        var p=Instance;if(p==null||!p.DisciplineScope(__instance.Bot)||!__result)return;
        double cap=p.config!.SuppressionDiscipline.ContactAge;
        __result=Enemy.Seen&&Enemy.TimeSinceSeen<=Math.Min(__instance.TimeSinceSeenToSuppress,cap)
            ||Enemy.Status.ShotAtMe&&Time.time-Enemy.Status.TimeLastShotAtMe<=Math.Min(__instance.TimeSinceShotAtToSuppress,cap)
            ||Enemy.Status.ShotMe&&Time.time-Enemy.Status.TimeLastShotMe<=Math.Min(__instance.TimeSinceShotToSuppress,cap);
    }
    private static void DisciplinePositionResult(SAINBotSuppressClass __instance,Vector3 position,Enemy Enemy,ref bool __result)
    {
        var p=Instance;var b=__instance.Bot;if(p==null||!p.DisciplineScope(b))return;
        if(!p.states.TryGetValue(b,out var s)){s=new BotState(b,p.config!,p.session);p.states.Add(b,s);}
        var r=s.SuppressionDiscipline;if(r.Fault)return;
        try {
            r.Owner=b;r.Enemy=Enemy;r.Weapon=b.Player.HandsController?.Item as Weapon;
            r.Policy.NativeCall(p.DisciplineFrame(b,s,Enemy),p.config!.SuppressionDiscipline,__result,DP(position),p.jumpRandom.NextDouble());
            if(r.Policy.NativePaused(Time.time)){__instance.ResetSuppressing();__result=false;}
            p.RecordDiscipline(s);
        }catch(Exception ex){p.DisciplineError(s,ex);}
    }
    private static void DisciplineShot(PlayerComponent __instance,IWeapon __0)
    {
        var p=Instance;var b=__instance.BotComponent;
        if(p==null||b==null||!p.states.TryGetValue(b,out var s))return;
        var r=s.SuppressionDiscipline;
        if(!r.Policy.Active||!ReferenceEquals(__0,r.Weapon)||b.ManualShoot.Reason!=SAIN.Models.Enums.EShootReason.Suppress)return;
        // Observation only. Ammo deltas in SuppressPosition own burst timing.
        r.Policy.Trace!.ActualShots++;p.ManeuverShot(s);p.RecordDiscipline(s,true);
    }
    private void SampleDiscipline(BotComponent b,BotState s)
    {
        if(s.SuppressionDiscipline.Policy.Active&&!DisciplineScope(b))EndDiscipline(s,"suppression-out-of-scope");
    }
    private void EndDiscipline(BotState s,string reason)
    {
        var r=s.SuppressionDiscipline;r.Policy.End(reason);RecordDiscipline(s);
        // Never reset a different native manual shooter during observational cleanup.
        if(r.Owner?.Suppression.SuppressingTarget==true)r.Owner.Suppression.ResetSuppressing();
        r.Owner=null;r.Enemy=null;r.Weapon=null;
    }
    private void DisciplineError(BotState s,Exception ex)
    {var r=s.SuppressionDiscipline;bool warn=!r.Fault;r.Fault=true;EndDiscipline(s,"suppression-adapter-error");if(warn)Logger.LogWarning("W07 yielded: "+ex.GetBaseException().Message);}
    private void RecordDiscipline(BotState s,bool force=false)
    {
        var r=s.SuppressionDiscipline;var t=r.Policy.Trace;if(t==null)return;
        string key=t.Episode+":"+t.State+":"+t.Reason+":"+t.Accepted+":"+t.ActualShots;
        if(!force&&key==r.LogKey&&(!r.Policy.Active||Time.time-r.LogAt<.2f))return;r.LogKey=key;r.LogAt=Time.time;
        var json=Diagnostics.Serialize(new{schema=1,session,sequence=++sequence,feature="W07",stage="evaluated",decision="SuppressionDiscipline",reason=t.Reason,
            realtime=Time.realtimeSinceStartup,utc=DateTime.UtcNow.ToString("O"),configRevision=config?.Revision??0,profileId=s.ProfileId,name=s.Name,botInstance=s.Id,
            suppressionDiscipline=t,suppressionDisciplineConfig=config?.SuppressionDiscipline});
        diagnostics?.Write(json);Api.Decisions.Publish(json,ex=>{if(!subscriberWarned){subscriberWarned=true;Logger.LogWarning(ex.Message);}});
    }
}
