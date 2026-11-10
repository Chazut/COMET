using System;
using Comet.Core;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using Newtonsoft.Json;
using SAIN;
using SAIN.Components;
using UnityEngine;

namespace Comet.Client;

internal sealed class HandsRecoveryRuntime
{
    internal readonly HandsRecoveryPolicy Policy=new();
    internal object? Hands,Operation,Reload,Transaction,Magazine;
    internal double Next,LogAt=-1000;
    internal int Generation,Loaded=-1;
    internal bool Issuing,Fault;
    internal string LogKey="";
    internal string NativePhase="";
    internal void Drop(){Hands=Operation=Reload=Transaction=Magazine=null;Loaded=-1;Issuing=false;}
}
public sealed partial class Plugin
{
    private static void PatchHandsRecovery(Harmony h)
    {
        h.Patch(AccessTools.Method(typeof(BotComponent),nameof(BotComponent.ManualUpdate)),postfix:new HarmonyMethod(typeof(Plugin),nameof(TrackHandsRecovery)));
        h.Patch(AccessTools.Method(typeof(BotWeaponSelector),nameof(BotWeaponSelector.TryChangeToSlot)),
            prefix:new HarmonyMethod(typeof(Plugin),nameof(HandsSwitchGuard)){priority=Priority.First});
        // The native selector fills magazines before drawing. Recovery must not grant ammunition.
        h.Patch(AccessTools.Method(typeof(BotReload),nameof(BotReload.TryFillMagazines)),
            prefix:new HarmonyMethod(typeof(Plugin),nameof(HandsFillGuard)){priority=Priority.First});
    }
    private bool HandsRecoveryScope(BotComponent b)=>config?.Enabled==true&&config.HandsRecovery.Enabled&&NativeAdapter.IsSupportedAi(b)
        &&b.BotActive&&!b.IsDead&&!b.GameEnding&&Authority&&!Ghost(b.ProfileId);
    private static void TrackHandsRecovery(BotComponent __instance)
    {
        var p=Instance;if(p==null)return;
        try{if(p.HandsRecoveryScope(__instance)&&!p.states.ContainsKey(__instance))p.states.Add(__instance,new BotState(__instance,p.config!,p.session));}
        catch(Exception ex){if(!p.handsEnrollmentWarned){p.handsEnrollmentWarned=true;p.Logger.LogWarning("W10 enrollment yielded: "+ex.Message);}}
    }
    private bool handsEnrollmentWarned;
    private static bool HandsSwitchGuard(BotWeaponSelector __instance,ref bool __result)
    {
        var p=Instance;
        if(p==null||__instance?._owner==null||!SAINEnableClass.GetSAIN(__instance._owner.ProfileId,out var b)||b==null
            ||!p.states.TryGetValue(b,out var s)||!p.HandsRecoveryScope(b))return true;
        if(!s.HandsRecovery.Issuing&&s.HandsRecovery.Policy.BlocksSwitch){__result=false;return false;}return true;
    }
    private static bool HandsFillGuard(BotReload __instance)
    {
        var p=Instance;var player=__instance?._player;
        return p==null||player==null||!SAINEnableClass.GetSAIN(player.ProfileId,out var b)||b==null
            ||!p.states.TryGetValue(b,out var s)||!s.HandsRecovery.Issuing||!ReferenceEquals(b.BotOwner.WeaponManager.Reload,__instance);
    }
    private HandsRecoveryFrame ReadHandsRecovery(BotComponent b,BotState s)
    {
        var r=s.HandsRecovery;var wm=b.BotOwner.WeaponManager;var hands=b.Player.HandsController;
        var gun=hands as Player.FirearmController;var grenade=hands as Player.GrenadeHandsController;
        object? operation=gun!=null?gun.CurrentOperation:grenade?.CurrentOperation;
        int loaded=gun?.Item==null?-1:ReloadLoaded(gun.Item);
        var external=operation as Player.FirearmController.ReloadExternalMagOperation;
        string phase=external==null?"":$"{external.State}:{external._magPulledOutFromWeapon}:{external._magAppeared}:{external._magInsertedToWeapon}:{external._addAmmoInChamber}";
        if(!ReferenceEquals(r.Hands,hands)||!ReferenceEquals(r.Operation,operation)||!ReferenceEquals(r.Reload,wm.Reload)||loaded!=r.Loaded||phase!=r.NativePhase
            ||!ReferenceEquals(r.Transaction,external?.ReloadExternalMagResult)||!ReferenceEquals(r.Magazine,gun?.Item?.GetCurrentMagazine()))r.Generation++;
        r.Transaction=external?.ReloadExternalMagResult;r.Magazine=gun?.Item?.GetCurrentMagazine();
        r.NativePhase=phase;
        r.Hands=hands;r.Operation=operation;r.Reload=wm.Reload;r.Loaded=loaded;
        var f=new HandsRecoveryFrame{Now=Time.timeAsDouble,Held=hands?.Item?.Id.ToString()??"",Generation=r.Generation,
            ReloadFlag=wm.Reload?.Reloading==true,NativeReload=gun?.IsInReloadOperation()==true,
            Operation=operation?.GetType().Name??hands?.GetType().Name??"no-hands",GunReady=WeaponHandsReady(b,gun),
            Action=b.Decision.CurrentCombatDecision.ToString(),Contact=s.Situation?.Target??"",NativePhase=phase,SelectorChanging=wm.Selector?.IsChanging==true};
        // Replay only a missed idle notification after the real animator and magazine
        // insertion agree. Inventory reconciliation below has a separate, longer confirmation window.
        f.ReloadIdle=f.NativeReload&&external?._magAppeared==true&&external._magInsertedToWeapon
            &&!b.Player.UsedSimplifiedSkeleton&&wm.InIdleState();
        // EFT moves the real inventory before starting the animation. If that animation
        // is already idle but its events never arrived, reconcile the existing native
        // transaction after a long, continuously observed stall, without creating ammo.
        var transaction=external?.ReloadExternalMagResult;
        f.ReloadStranded=f.NativeReload&&!f.ReloadIdle&&transaction!=null
            &&ReferenceEquals(transaction.Weapon,gun!.Item)&&transaction.NextMagazine!=null
            &&ReferenceEquals(gun!.Item!.GetCurrentMagazine(),transaction.NextMagazine)
            &&ReferenceEquals(wm.CurrentWeapon,gun.Item)&&ReferenceEquals(wm.ShootController,gun)
            &&!b.Player.UsedSimplifiedSkeleton&&wm.InIdleState();
        f.SwitchCompleted=f.SelectorChanging&&gun?.CurrentOperation is Player.FirearmController.Idling
            &&wm.Selector!.IsWeaponReady&&!b.Player.InventoryController.IsChangingWeapon
            &&ReferenceEquals(wm.ShootController,gun)&&ReferenceEquals(wm.CurrentWeapon,gun.Item)
            &&ReferenceEquals(SlotGun(b,wm.Selector.EquipmentSlot),gun.Item);
        f.Kind=f.NativeReload?"reload-idle":f.ReloadFlag?"reload":f.SelectorChanging?"switch":grenade!=null?"grenade":"";
        bool idleGrenade=grenade?.CurrentOperation is Player.GrenadeHandsController.Idling&&!grenade.WaitingForHighThrow&&!grenade.WaitingForLowThrow;
        if(wm.Reload==null||wm.Selector==null)f.Block="hands-native-data-unavailable";
        else if(b.BotOwner.Medecine?.Using==true)f.Block="hands-medical-active";
        else if(s.Fake.Policy.Active||s.Fake.CallbackPending||FakeGuarded(b))f.Block="hands-feint-owned";
        else if(s.RoomClear.Policy.Active||wm.Grenades?.ThrowindNow==true||f.Action=="ThrowGrenade")f.Block="hands-grenade-action-active";
        else if(s.WeaponReturn.Policy.Active||s.PistolSwap.Policy.Active||s.QuickReload.Policy.Active)f.Block="hands-other-weapon-request";
        else if(b.Player.InventoryController.IsChangingWeapon)f.Block="hands-inventory-changing";
        else if(wm.Selector.IsChanging&&!f.SwitchCompleted)f.Block="hands-weapon-changing";
        else if(f.NativeReload&&!f.ReloadIdle&&!f.ReloadStranded)f.Block="hands-native-reload-active";
        else if(!f.ReloadIdle&&!f.ReloadStranded&&gun!=null&&(gun.IsInInteraction()||!gun.CanStartReload()))f.Block="hands-firearm-operation-active";
        else if(gun==null&&!idleGrenade)f.Block="hands-unknown-or-committed-operation";
        if(grenade!=null&&wm.Selector!=null)
        {
            var selector=wm.Selector;
            var slot=selector._lastEquipmentSlot;
            if(!GunSlot(slot)||SlotGun(b,slot)==null)slot=selector._mainWeapon;
            if(!GunSlot(slot)||SlotGun(b,slot)==null)
                foreach(var candidate in SyncSlots)if(SlotGun(b,candidate)!=null){slot=candidate;break;}
            var target=GunSlot(slot)?SlotGun(b,slot):null;
            if(target!=null){f.TargetWeapon=target.Id.ToString();f.TargetSlot=slot.ToString();}
        }
        return f;
    }
    private void SampleHandsRecovery(BotComponent b,BotState s)
    {
        var r=s.HandsRecovery;
        if(r.Fault||!HandsRecoveryScope(b)){EndHandsRecovery(s,"hands-out-of-scope");return;}
        double now=Time.timeAsDouble;if(now<r.Next&&now>=r.Next-config!.HandsRecovery.CheckSeconds)return;
        r.Next=now+config!.HandsRecovery.CheckSeconds;
        var f=ReadHandsRecovery(b,s);string command=r.Policy.Observe(f,config.HandsRecovery);
        if(command.Length==0){RecordHandsRecovery(s);return;}
        // Re-read all volatile native state and the target slot immediately before mutating anything.
        var fresh=ReadHandsRecovery(b,s);
        if(fresh.Kind!=f.Kind||fresh.Held!=f.Held||fresh.Generation!=f.Generation||fresh.Block.Length>0
            ||fresh.TargetWeapon!=f.TargetWeapon||!HandsRecoveryScope(b)){r.Policy.Observe(fresh,config.HandsRecovery);RecordHandsRecovery(s,true);return;}
        r.Policy.Request(now);RecordHandsRecovery(s,true);
        bool accepted=false;
        try {
            if(command=="clear-reload")
            {b.BotOwner.WeaponManager.Reload.Reloading=false;accepted=!b.BotOwner.WeaponManager.Reload.Reloading;}
            else if(command=="clear-switch"&&fresh.SwitchCompleted)
            {b.BotOwner.WeaponManager.Selector.IsChanging=false;accepted=!b.BotOwner.WeaponManager.Selector.IsChanging;}
            else if(command=="finish-reload-idle"&&fresh.ReloadIdle
                &&b.Player.HandsController is Player.FirearmController firearm
                &&firearm.CurrentOperation is Player.FirearmController.ReloadExternalMagOperation operation)
            {operation.OnIdleStartEvent();accepted=!firearm.IsInReloadOperation();}
            else if(command=="reconcile-stranded-reload"&&fresh.ReloadStranded
                &&b.Player.HandsController is Player.FirearmController stalledGun
                &&stalledGun.CurrentOperation is Player.FirearmController.ReloadExternalMagOperation stalled)
            {r.Issuing=true;stalled.FastForward();accepted=!stalledGun.IsInReloadOperation();}
            else if(Enum.TryParse(f.TargetSlot,out EquipmentSlot slot)&&SlotGun(b,slot)?.Id.ToString()==f.TargetWeapon)
            {r.Issuing=true;accepted=b.BotOwner.WeaponManager.Selector.TryChangeToSlot(slot,true);}
        } finally {r.Issuing=false;}
        r.Policy.Result(accepted,now,config.HandsRecovery);RecordHandsRecovery(s,true);
    }
    private void EndHandsRecovery(BotState s,string reason)
    {
        var r=s.HandsRecovery;bool active=r.Policy.Trace?.ValidFor>0;r.Policy.End(reason);r.Drop();
        if(active)RecordHandsRecovery(s,true);
    }
    private void HandsRecoveryError(BotState s,Exception ex)
    {
        var r=s.HandsRecovery;bool warn=!r.Fault;r.Fault=true;
        try{EndHandsRecovery(s,"hands-adapter-error");}catch{r.Drop();r.Policy.End("hands-adapter-error");}
        if(warn)Logger.LogWarning("W10 yielded to native hands: "+ex.GetBaseException().Message);
    }
    private void RecordHandsRecovery(BotState s,bool force=false)
    {
        var r=s.HandsRecovery;var t=r.Policy.Trace;if(t==null)return;
        double now=Time.timeAsDouble;string key=t.State+"|"+t.Reason+"|"+t.Attempts+"|"+t.NativePhase+"|"+t.ReloadIdle+"|"+t.SwitchCompleted;
        if(!force&&key==r.LogKey&&(t.ValidFor==0||now-r.LogAt<(r.Policy.Pending?1:5)))return;
        r.LogKey=key;r.LogAt=now;
        var json=Diagnostics.Serialize(new{schema=1,session,sequence=++sequence,feature="W10",stage="evaluated",decision="HandsRecovery",reason=t.Reason,
            realtime=Time.realtimeSinceStartup,utc=DateTime.UtcNow.ToString("O"),configRevision=config?.Revision??0,profileId=s.ProfileId,name=s.Name,botInstance=s.Id,
            handsRecovery=t,handsRecoveryConfig=config?.HandsRecovery});
        diagnostics?.Write(json);Api.Decisions.Publish(json,ex=>{if(!subscriberWarned){subscriberWarned=true;Logger.LogWarning(ex.Message);}});
    }
}
