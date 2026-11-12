using System;
using Comet.Core;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using Newtonsoft.Json;
using SAIN.Components;
using SAIN.Models.Enums;
using SAIN.SAINComponent.Classes.WeaponFunction;
using UnityEngine;

namespace Comet.Client;

internal sealed class CornerPrefireRuntime
{
    internal readonly CornerPrefire Policy=new();
    internal BotComponent? Owner;
    internal object? Enemy;
    internal object? Weapon;
    internal Vector3 Aim;
    internal bool InRequest,Stopping,Owned,Fault;
    internal float NativeStarted,LogAt=-1000;
    internal int SafetyFrame=-1;
    internal CornerPrefireFrame? Safety;
    internal string LogKey="";
    internal readonly RaycastHit[] Hits=new RaycastHit[32];
    internal readonly Collider[] Overlaps=new Collider[32];
}
public sealed partial class Plugin
{
    private static readonly System.Reflection.FieldInfo prefireEnemy=AccessTools.Field(typeof(ManualShootClass),"ManualShootEnemy");
    private static readonly System.Reflection.FieldInfo prefireStarted=AccessTools.Field(typeof(ManualShootClass),"_timeStartManualShoot");
    private static void PatchCornerPrefire(Harmony h)
    {
        if(prefireEnemy==null||prefireStarted==null)throw new MissingFieldException("M06 official SAIN manual shooting contract");
        h.Patch(AccessTools.Method(typeof(ManualShootClass),nameof(ManualShootClass.TryShoot)),prefix:new HarmonyMethod(typeof(Plugin),nameof(PrefireNativeRequest)){priority=Priority.First});
        h.Patch(AccessTools.Method(typeof(ManualShootClass),nameof(ManualShootClass.Reset)),prefix:new HarmonyMethod(typeof(Plugin),nameof(PrefireNativeReset)));
    }
    private void BeginPrefire(BotComponent b,BotState s)
    {
        var r=s.CornerPrefire;if(r.Fault)return;r.Owner=b;r.Enemy=b.GoalEnemy;r.Weapon=b.Player.HandsController?.Item;
        // Frozen plausible torso-height point beyond this verified corner, not a hidden target transform.
        r.Aim=s.CornerChase.Exit+Vector3.up*1.2f;
        var f=PrefireFrame(b,s,false);
        if(b.ManualShoot.Reason!=EShootReason.None||b.ManualShoot.Shooting)f.Block="prefire-native-shooting-busy";
        r.Policy.Begin(f,config!.CornerPrefire,jumpRandom.NextDouble());RecordPrefire(s,true);
        if(!r.Policy.Active){r.Owner=null;r.Enemy=r.Weapon=null;}
    }
    private CornerPrefireFrame PrefireFrame(BotComponent b,BotState s,bool safety)
    {
        var r=s.CornerPrefire;var chase=s.CornerChase;var cf=ChaseFrame(b,s);
        var weapon=b.Player.HandsController?.Item as Weapon;var mag=weapon?.GetCurrentMagazine();
        var f=new CornerPrefireFrame {Now=Time.time,SeenAt=chase.Memory.SeenAt,ParentDeadline=chase.Policy.Deadline,
            ParentActive=chase.Policy.Active,ParentEpisode=chase.Policy.Trace?.Episode??0,ParentStyle=chase.Policy.Trace?.Style??"",
            Target=b.GoalEnemy?.EnemyProfileId??"",Weapon=weapon?.Id.ToString()??"",Mode=weapon?.SelectedFireMode.ToString()??"",
            Automatic=weapon?.SelectedFireMode==Weapon.EFireMode.fullauto,Ammo=mag?.Count??0,Capacity=mag?.MaxCount??0,
            Known=chase.Policy.Known,Aim=DP(r.Aim),CornerDistance=Vector3.Distance(b.Position,chase.Corner),
            KnownCornerDistance=Vector3.Distance(VP(chase.Policy.Known),chase.Corner),Fresh=false,
            Block=CornerChase.Gate(cf,config!.CornerChase,false)};
        if(!ReferenceEquals(r.Enemy,b.GoalEnemy)||!ReferenceEquals(b.CurrentAction,chase.Action)||!OwnChase(chase)
            ||QueuedCombatMovement(b)||Time.time-chase.CallbackAt>.4f)f.Block="prefire-parent-ownership-lost";
        if(!cf.Combat.Grounded||cf.Combat.Sprinting||weapon==null||!b.BotOwner.WeaponManager.IsWeaponReady
            ||b.BotOwner.WeaponManager.Reload.Reloading||r.Policy.Trace?.Requested!=true&&!b.ManualShoot.CanShoot(false))f.Block="prefire-native-priority";
        // Native burst modes cannot guarantee one cartridge per trigger: leave them to SAIN.
        if(weapon!=null&&weapon.SelectedFireMode!=Weapon.EFireMode.fullauto&&weapon.SelectedFireMode!=Weapon.EFireMode.single
            &&weapon.SelectedFireMode!=Weapon.EFireMode.doubleaction)f.Block="prefire-unsupported-fire-mode";
        if(!safety||f.Block.Length>0)return f;
        var geometry=CheckShotGeometry(b,s,r.Aim,"M06",f.Target);
        if(geometry!=null)
        {
            f.Fresh=geometry.Fresh;f.Clear=geometry.Allowed;f.AllySafe=geometry.AllySafe;
            f.Aligned=Vector3.Angle(b.Transform.WeaponData.PointDirection,r.Aim-b.Transform.WeaponData.FirePort)<=8&&b.Steering.AngleToPointFromLookDir(r.Aim)<=8;
            return f;
        }
        if(!doorBudget.TryBegin(Time.frameCount,b))return f;
        var watch=System.Diagnostics.Stopwatch.StartNew();
        try {
            var muzzle=b.Transform.WeaponData.FirePort;var ray=r.Aim-muzzle;float length=ray.magnitude;
            f.Fresh=true;
            if(length<.1f||!CornerChase.Point(DP(muzzle))){f.Block="prefire-invalid-muzzle";return f;}
            var dir=ray.normalized;var actual=b.Transform.WeaponData.PointDirection;
            f.Aligned=Vector3.Angle(actual,dir)<=8&&b.Steering.AngleToPointFromLookDir(r.Aim)<=8;
            // Casts do not detect a collider that already contains their origin.
            f.Clear=!Physics.CheckSphere(muzzle,.04f,LayersMaskController.HighPolyWithTerrainMask,QueryTriggerInteraction.Ignore)
                &&!Physics.SphereCast(muzzle,.04f,dir,out _,length,LayersMaskController.HighPolyWithTerrainMask,QueryTriggerInteraction.Ignore)
                &&!Physics.SphereCast(muzzle,.04f,actual,out _,length,LayersMaskController.HighPolyWithTerrainMask,QueryTriggerInteraction.Ignore);
            // SAIN's wrapper skips the ally test for solo bots; check physical bystanders ourselves too.
            f.AllySafe=PrefireAllies(b,r,muzzle,dir,length)&&PrefireAllies(b,r,muzzle,actual,length);
        }finally{watch.Stop();doorBudget.Charge(watch.Elapsed.TotalMilliseconds);}
        return f;
    }
    private static bool PrefireAllies(BotComponent b,CornerPrefireRuntime r,Vector3 muzzle,Vector3 dir,float length)
    {
        int count=Physics.SphereCastNonAlloc(muzzle,.2f,dir,r.Hits,length,LayersMaskController.PlayerMask,QueryTriggerInteraction.Ignore);
        if(count==r.Hits.Length)return false;
        var world=GameWorldComponent.Instance?.GameWorld;if(world==null)return false;
        int overlapCount=Physics.OverlapSphereNonAlloc(muzzle,.2f,r.Overlaps,LayersMaskController.PlayerMask,QueryTriggerInteraction.Ignore);
        if(overlapCount==r.Overlaps.Length)return false;
        for(int i=0;i<overlapCount;i++) {
            var p=world.GetPlayerByCollider(r.Overlaps[i]);
            if(p!=null&&p.ProfileId!=b.ProfileId&&!b.EnemyController.IsPlayerAnEnemy(p.ProfileId))return false;
        }
        for(int i=0;i<count;i++) {
            var p=world.GetPlayerByCollider(r.Hits[i].collider);
            if(p!=null&&p.ProfileId!=b.ProfileId&&!b.EnemyController.IsPlayerAnEnemy(p.ProfileId))return false;
        }
        return true;
    }
    private static bool OwnPrefire(CornerPrefireRuntime r)
    {
        var m=r.Owner?.ManualShoot;
        return r.Owned&&m!=null&&m.Reason==EShootReason.Suppress&&ReferenceEquals(prefireEnemy.GetValue(m),r.Enemy)
            &&(m.ShootPosition-r.Aim).sqrMagnitude<.0001f&&(float)prefireStarted.GetValue(m)==r.NativeStarted;
    }
    private bool PermitPrefireRequest(ManualShootClass m,object? enemy)
    {
        if(config==null||!states.TryGetValue(m.Bot,out var s))return false;
        var r=s.CornerPrefire;
        if(!r.InRequest||!ReferenceEquals(m.Bot,r.Owner)||!ReferenceEquals(enemy,r.Enemy))return false;
        var f=PrefireFrame(m.Bot,s,false);var safe=r.Safety;
        f.Fresh=safe?.Fresh==true&&r.SafetyFrame==Time.frameCount;
        f.Clear=safe?.Clear==true;f.AllySafe=safe?.AllySafe==true;f.Aligned=safe?.Aligned==true;
        return r.Policy.AuthorizeRequest(f,config.CornerPrefire);
    }
    private void RefreshPrefire(BotComponent b,BotState s,bool steering=false)
    {
        var r=s.CornerPrefire;if(!r.Policy.Active)return;
        try {
            if(config==null||!config.Enabled||b==null||b.IsDead){EndPrefire(s,"prefire-out-of-scope");return;}
            if(r.Policy.Trace!.Requested&&!OwnPrefire(r)){EndPrefire(s,"prefire-native-ownership-lost");return;}
            var f=PrefireFrame(b,s,true);
            r.Safety=f;r.SafetyFrame=Time.frameCount;
            if(!r.Policy.Observe(f,config.CornerPrefire)){FinishPrefire(s);return;}
            if(r.Policy.Trace.Requested) {
                if(!b.ManualShoot.Shooting)EndPrefire(s,"prefire-native-fire-ended");
                else RecordPrefire(s);
                return;
            }
            if(b.ManualShoot.Reason!=EShootReason.None||b.ManualShoot.Shooting){EndPrefire(s,"prefire-native-shooting-busy");return;}
            if(steering) {
                b.Steering.LookToPoint(r.Aim);
                if(r.Policy.Request(f,config.CornerPrefire)) {
                    r.InRequest=true;bool ok=false;
                    try {ok=b.ManualShoot.TryShoot(b.GoalEnemy,r.Aim,true,EShootReason.Suppress);}
                    finally {
                        r.InRequest=false;r.Policy.Result(ok);
                        // TryShoot writes its owner AFTER a possible synchronous shot callback.
                        r.Owned=ok;r.NativeStarted=(float)prefireStarted.GetValue(b.ManualShoot);
                        if(!r.Policy.Active)FinishPrefire(s);
                    }
                }
            }
            RecordPrefire(s);
        }catch(Exception ex){PrefireError(s,ex);}
    }
    private void PrefireShot(BotComponent b,BotState s,object weapon)
    {
        var r=s.CornerPrefire;
        if(!r.Policy.Active||!r.InRequest&&!OwnPrefire(r))return;
        try {
            var f=PrefireFrame(b,s,false);var safe=r.Safety;
            if(!ReferenceEquals(weapon,r.Weapon)||f.Block.Length>0){EndPrefire(s,f.Block.Length>0?f.Block:"prefire-shot-weapon-changed");return;}
            f.Fresh=safe?.Fresh==true&&Time.frameCount-r.SafetyFrame<=1;
            f.Clear=safe?.Clear==true;f.AllySafe=safe?.AllySafe==true;f.Aligned=safe?.Aligned==true;
            r.Policy.Shot(f,config!.CornerPrefire);
            // Evidence for this callback comes from the preceding guarded request/update, not a second physics query.
            if(!r.Policy.Active&&!r.InRequest)FinishPrefire(s);
            RecordPrefire(s,true);
        }catch(Exception ex){PrefireError(s,ex);}
    }
    private static bool PrefireNativeRequest(ManualShootClass __instance,object Enemy,ref bool __result)
    {
        var p=Instance;if(p==null||!p.states.TryGetValue(__instance.Bot,out var s))return true;
        if(s.CornerPrefire.InRequest) {
            try {if(p.PermitPrefireRequest(__instance,Enemy))return true;}
            catch(Exception ex){p.PrefireError(s,ex);}
            __result=false;return false;
        }
        p.EndPrefire(s,"prefire-native-shooter-takeover");return true;
    }
    private static void PrefireNativeReset(ManualShootClass __instance)
    {
        var p=Instance;if(p==null||!p.states.TryGetValue(__instance.Bot,out var s))return;var r=s.CornerPrefire;
        if(r.Stopping||r.InRequest||!r.Policy.Active)return;
        r.Owned=false;r.Policy.End("prefire-native-reset");p.RecordPrefire(s,true);r.Owner=null;r.Enemy=r.Weapon=null;r.Safety=null;
    }
    private void EndPrefire(BotState s,string reason)
    {
        var r=s.CornerPrefire;if(!r.Policy.Active&&!r.Owned)return;r.Policy.End(reason);FinishPrefire(s);
    }
    private void FinishPrefire(BotState s)
    {
        var r=s.CornerPrefire;
        try {if(!r.InRequest&&OwnPrefire(r)){r.Stopping=true;r.Owner!.ManualShoot.Reset();}}
        finally{r.Stopping=false;r.Owned=false;RecordPrefire(s,true);if(!r.InRequest){r.Owner=null;r.Enemy=r.Weapon=null;r.Safety=null;}}
    }
    private void PrefireError(BotState s,Exception ex)
    {
        s.CornerPrefire.Fault=true;EndPrefire(s,"prefire-adapter-error");Logger.LogWarning("M06 yielded: "+ex.GetBaseException().Message);
    }
    private void RecordPrefire(BotState s,bool force=false)
    {
        var r=s.CornerPrefire;var t=r.Policy.Trace;if(t==null)return;
        string key=t.Episode+":"+t.State+":"+t.Reason+":"+t.Accepted+":"+t.ActualShots;
        if(!force&&key==r.LogKey&&(!r.Policy.Active||Time.time-r.LogAt<.1f))return;r.LogKey=key;r.LogAt=Time.time;
        var json=Diagnostics.Serialize(new{schema=1,session,sequence=++sequence,feature="M06",stage="evaluated",decision="CornerPrefire",reason=t.Reason,
            realtime=Time.realtimeSinceStartup,utc=DateTime.UtcNow.ToString("O"),configRevision=config?.Revision??0,profileId=s.ProfileId,name=s.Name,botInstance=s.Id,
            cornerPrefire=t,cornerPrefireConfig=config?.CornerPrefire});
        diagnostics?.Write(json);Api.Decisions.Publish(json,ex=>{if(!subscriberWarned){subscriberWarned=true;Logger.LogWarning(ex.Message);}});
    }
}
