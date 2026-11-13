using System;
using System.Collections.Generic;
using Comet.Core;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using Newtonsoft.Json;
using SAIN;
using SAIN.Components;
using SAIN.Preset.Shared.Enums;
using UnityEngine;

namespace Comet.Client;

internal sealed class WeaponReturnRuntime
{
    internal readonly WeaponReturnPolicy Policy=new();
    internal WeaponReturnFrame? Frame;
    internal float Next,SampleAt,LogAt=-1000;
    internal string Intent="",ReturnId="";
    internal (string State,string Choice,string Reason,string From,string To,int Loaded,int TargetLoaded,bool InHands,bool Ready) LogKey;
    internal bool Fault;
}
internal sealed class WeaponSwitchAttempt
{
    internal BotComponent Bot=null!;
    internal BotState State=null!;
    internal float PreviousTimer,TemporaryTimer;
}
public sealed partial class Plugin
{
    private static void PatchWeaponReturn(Harmony h)
    {
        h.Patch(AccessTools.Method(typeof(BotWeaponSelector),nameof(BotWeaponSelector.TryChangeToSlot)),
            prefix:new HarmonyMethod(typeof(Plugin),nameof(AuthorizeWeaponSwitch)),postfix:new HarmonyMethod(typeof(Plugin),nameof(WeaponSwitchResult)),
            finalizer:new HarmonyMethod(typeof(Plugin),nameof(WeaponSwitchException)));
        h.Patch(AccessTools.Method(typeof(BotWeaponSelector),nameof(BotWeaponSelector.TryChangeWeapon)),prefix:new HarmonyMethod(typeof(Plugin),nameof(ChooseWeaponFallback)));
        h.Patch(AccessTools.Method(typeof(BotWeaponSelector),nameof(BotWeaponSelector.TryChangeToMain)),prefix:new HarmonyMethod(typeof(Plugin),nameof(ChooseWeaponReturn)));
        h.Patch(AccessTools.Method(typeof(BotWeaponSelector),nameof(BotWeaponSelector.ManualUpdate)),prefix:new HarmonyMethod(typeof(Plugin),nameof(KeepOffEmptyMain)));
        h.Patch(AccessTools.Method(typeof(BotWeaponSelector),nameof(BotWeaponSelector.ShallChangeIfNoAmmo)),prefix:new HarmonyMethod(typeof(Plugin),nameof(SuppressRandomWeaponSwap)));
    }
    private bool WeaponReturnScope(BotComponent b)=>config?.Enabled==true&&(config.WeaponReturn.Enabled||config.WeaponRange.Enabled)&&NativeAdapter.IsSupportedAi(b)
        &&b.BotActive&&!b.IsDead&&!b.GameEnding&&Authority&&!Ghost(b.ProfileId)&&b.SAINLayersActive;
    private bool WeaponReturnState(BotWeaponSelector selector,out BotComponent b,out BotState s)
    {
        b=null!;s=null!;
        if(selector?._owner==null||!SAINEnableClass.GetSAIN(selector._owner.ProfileId,out b)||b==null||!WeaponReturnScope(b))return false;
        if(!states.TryGetValue(b,out s)){s=new BotState(b,config!,session);states.Add(b,s);}
        return !s.WeaponReturn.Fault;
    }
    private static bool GunSlot(EquipmentSlot slot)=>slot is EquipmentSlot.FirstPrimaryWeapon or EquipmentSlot.SecondPrimaryWeapon or EquipmentSlot.Holster;
    private static Weapon? SlotGun(BotComponent b,EquipmentSlot slot)=>b.Player.Inventory?.Equipment?.GetSlot(slot)?.ContainedItem as Weapon;
    private static string ActualGunSlot(BotComponent b,Weapon w)
    {
        if(ReferenceEquals(SlotGun(b,EquipmentSlot.FirstPrimaryWeapon),w))return nameof(EquipmentSlot.FirstPrimaryWeapon);
        if(ReferenceEquals(SlotGun(b,EquipmentSlot.SecondPrimaryWeapon),w))return nameof(EquipmentSlot.SecondPrimaryWeapon);
        if(ReferenceEquals(SlotGun(b,EquipmentSlot.Holster),w))return nameof(EquipmentSlot.Holster);
        return "";
    }
    private static bool WeaponHandsReady(BotComponent b,Player.FirearmController? firearm)
        =>b!=null&&!b.IsDead&&!b.GameEnding&&firearm!=null&&b.BotOwner?.WeaponManager?.Selector?.IsWeaponReady==true;
    private WeaponReturnGun ReadReturnGun(BotComponent b,BotState s,EquipmentSlot slot)
    {
        var w=SlotGun(b,slot);if(w==null)return new();
        int loaded=ReloadLoaded(w);
        var g=new WeaponReturnGun{Id=w.Id.ToString(),Slot=slot.ToString(),Loaded=loaded,Usable=true};
        if(loaded>0)return g;
        var inventory=b.Player.InventoryController;if(inventory==null)return g;
        var r=s.ReloadTiming;var magSlot=w.GetMagazineSlot();var chamber=w.Chambers.Length>0?w.Chambers[0]:null;
        bool emptyMag=false;
        try{
            if(magSlot!=null){r.Magazines.Clear();inventory.GetReachableItemsOfTypeNonAlloc(r.Magazines);
                foreach(var m in r.Magazines)if(magSlot.CanAccept(m)){if(m.Count>0){g.Reserve=m.Count;g.Resource="magazine";return g;}emptyMag=true;}}
            if(chamber==null||w.ReloadMode==Weapon.EReloadMode.ExternalMagazine&&!emptyMag)return g;
            r.Ammo.Clear();inventory.GetAcceptableItemsNonAlloc(BotReload._availableEquipmentSlots,r.Ammo);
            foreach(var a in r.Ammo)if(a.StackObjectsCount>0&&chamber.CanAccept(a)){g.Reserve=a.StackObjectsCount;g.Resource="loose-ammo";return g;}
            return g;
        }finally{r.Magazines.Clear();r.Ammo.Clear();}
    }
    private WeaponReturnFrame? WeaponReturnFrameFor(BotComponent b,BotState s)
    {
        var firearm=b.Player.HandsController as Player.FirearmController;if(firearm==null)return null;
        var wm=b.BotOwner.WeaponManager;var selector=wm.Selector;
        var first=ReadReturnGun(b,s,EquipmentSlot.FirstPrimaryWeapon);var second=ReadReturnGun(b,s,EquipmentSlot.SecondPrimaryWeapon);var pistol=ReadReturnGun(b,s,EquipmentSlot.Holster);
        var e=b.GoalEnemy;var f=new WeaponReturnFrame{Now=Time.time,Main=selector._mainWeapon==EquipmentSlot.SecondPrimaryWeapon?second:first,
            Other=selector._mainWeapon==EquipmentSlot.SecondPrimaryWeapon?first:second,Pistol=pistol,
            Current=firearm.Item.Id.ToString()==first.Id?first:firearm.Item.Id.ToString()==second.Id?second:pistol,
            Ready=WeaponHandsReady(b,firearm),Reloading=wm.Reload.Reloading,Target=e?.EnemyProfileId??"",
            ContactAge=e==null?999:e.IsVisible?0:e.Seen?e.TimeSinceSeen:999,Distance=e?.RealDistance??0,
            Incoming=e!=null&&e.Status.ShotAtMe&&e.Status.TimeLastShotAtMe>0&&Time.time-e.Status.TimeLastShotAtMe<6,
            Far=e!=null&&e.RealDistance>config!.PistolSwap.MaxDistance+config.WeaponReturn.DistanceMargin};
        s.WeaponReturn.Frame=f;return f;
    }
    private static WeaponReturnGun ReturnTarget(WeaponReturnFrame f,string id)
    {
        if(id.Length>0){if(f.Main.Id==id)return f.Main;if(f.Other.Id==id)return f.Other;if(f.Pistol.Id==id)return f.Pistol;}
        return new();
    }
    private void ObserveWeaponReturn(BotComponent b,BotState s)
    {
        var firearm=b.Player.HandsController as Player.FirearmController;
        s.WeaponReturn.Policy.Observe(Time.time,firearm?.Item?.Id.ToString()??"",b.BotOwner.WeaponManager.Selector.EquipmentSlot.ToString(),WeaponHandsReady(b,firearm),RangeArbiterConfig());
    }
    private bool NativeWeaponRequest(BotComponent b,BotState s,WeaponReturnGun target,string origin)
    {
        if(target.Id.Length==0||!Enum.TryParse(target.Slot,out EquipmentSlot slot))return false;
        var r=s.WeaponReturn;string old=r.Intent;r.Intent=origin;
        try{return b.BotOwner.WeaponManager.Selector.TryChangeToSlot(slot,true);}
        finally{r.Intent=old;}
    }
    private static bool AuthorizeWeaponSwitch(BotWeaponSelector __instance,EquipmentSlot slot,ref bool __result,out WeaponSwitchAttempt? __state)
    {
        __state=null;var p=Instance;BotState? s=null;
        try{
            if(p==null||!GunSlot(slot)||!p.WeaponReturnState(__instance,out var b,out s)||s.WeaponReturn.Intent.Length==0)return true;
            var f=p.WeaponReturnFrameFor(b,s);if(f==null)return true;
            var target=ReturnTarget(f,SlotGun(b,slot)?.Id.ToString()??"");
            s.WeaponReturn.Policy.Evaluate(f,target,s.WeaponReturn.Intent,p.RangeArbiterConfig());s.WeaponReturn.Policy.Request(Time.time);
            __state=new(){Bot=b,State=s,PreviousTimer=__instance._nextChangeTime,TemporaryTimer=0};__instance._nextChangeTime=0;
            return true;
        }catch(Exception ex){if(s!=null)p?.WeaponReturnError(s,ex);return true;}
    }
    private static void WeaponSwitchResult(BotWeaponSelector __instance,bool __result,WeaponSwitchAttempt? __state)
    {
        if(__state==null||Instance==null)return;var p=Instance;var s=__state.State;
        try {
            s.WeaponReturn.Policy.Result(__result,Time.time,p.RangeArbiterConfig());s.WeaponReturn.Frame=null;
            p.ObserveWeaponReturn(__state.Bot,s);p.RecordWeaponReturn(__state.Bot,s,true);
        }catch(Exception ex){p.WeaponReturnError(s,ex);}
    }
    private static void WeaponSwitchException(BotWeaponSelector __instance,Exception? __exception,WeaponSwitchAttempt? __state)
    {
        if(__exception==null||__state==null||Instance==null)return;
        Instance.WeaponReturnError(__state.State,__exception);
    }
    private static bool ChooseWeaponFallback(BotWeaponSelector __instance,ref bool __result)
    {
        var p=Instance;BotState? s=null;
        try{
            if(p==null||!p.config!.WeaponReturn.Enabled||!p.WeaponReturnState(__instance,out var b,out s))return true;
            var f=p.WeaponReturnFrameFor(b,s);if(f==null)return true;
            string id=s.WeaponReturn.Policy.Fallback(f);__result=id.Length>0&&p.NativeWeaponRequest(b,s,ReturnTarget(f,id),"native-toggle");return false;
        }catch(Exception ex){if(s!=null)p?.WeaponReturnError(s,ex);return true;}
    }
    private static bool ChooseWeaponReturn(BotWeaponSelector __instance,ref bool __result)
    {
        var p=Instance;BotState? s=null;
        try{
            if(p==null||!p.config!.WeaponReturn.Enabled||!p.WeaponReturnState(__instance,out var b,out s))return true;
            var f=p.WeaponReturnFrameFor(b,s);if(f==null||f.Main.Id==f.Current.Id)return true;
            if(p.KeepRangeChoice(b,s,f,f.Main,"native-return")||!s.WeaponReturn.Policy.Evaluate(f,f.Main,"native-return",p.RangeArbiterConfig()))
            {__result=false;p.RecordWeaponReturn(b,s);return false;}
            return true; // Source leaves the native return intact when the main has ammo.
        }catch(Exception ex){if(s!=null)p?.WeaponReturnError(s,ex);return true;}
    }
    private static bool KeepOffEmptyMain(BotWeaponSelector __instance)
    {
        var p=Instance;if(p==null||!p.config!.WeaponReturn.Enabled||!p.WeaponReturnState(__instance,out var b,out var s))return true;
        if(__instance.EquipmentSlot==__instance._mainWeapon)return true;
        var g=p.ReadReturnGun(b,s,__instance._mainWeapon);return g.Id.Length==0||g.Loaded>0||g.Reloadable;
    }
    private static bool SuppressRandomWeaponSwap(BotWeaponSelector __instance,ref bool __result)
    {
        var p=Instance;
        try {
            if(p==null||!p.WeaponReturnState(__instance,out _,out _)||!(p.config!.ReloadTiming.Enabled||p.config.PistolSwap.Enabled))return true;
            if(__instance._owner.WeaponManager.UnderbarrelLauncherController?.CanUseInsteadOfReload()==true)return true;
            __result=false;return false;
        }catch{return true;}
    }
    private void SampleWeaponReturn(BotComponent b,BotState s)
    {
        var r=s.WeaponReturn;if(r.Fault)return;
        if(!WeaponReturnScope(b)){EndWeaponReturn(s,"weapon-out-of-scope");return;}
        ObserveWeaponReturn(b,s);
        if(!config!.WeaponReturn.Enabled||!r.Policy.PistolByUs||!WeaponHandsReady(b,b.Player.HandsController as Player.FirearmController))return;
        var f=WeaponReturnFrameFor(b,s);if(f==null)return;
        string id=r.Policy.Recommend(f,config.WeaponReturn);
        if(id.Length>0)NativeWeaponRequest(b,s,ReturnTarget(f,id),"return-policy");
        RecordWeaponReturn(b,s);
    }
    private void EndWeaponReturn(BotState s,string why)
    {var r=s.WeaponReturn;r.Policy.Clear(why);r.Frame=null;r.ReturnId="";RecordWeaponReturn(null,s,true);}
    private void WeaponReturnError(BotState s,Exception ex)
    {if(s.WeaponReturn.Fault)return;s.WeaponReturn.Fault=true;EndWeaponReturn(s,"weapon-adapter-error");Logger.LogWarning("W03 yielded to SAIN: "+ex.GetBaseException().Message);}
    private void RecordWeaponReturn(BotComponent? b,BotState s,bool force=false)
    {
        var r=s.WeaponReturn;var t=r.Policy.Trace;if(t==null)return;
        if(b!=null)t.Action=b.Decision.CurrentSelfDecision==ESelfActionType.Reload?"Reload":b.Decision.CurrentCombatDecision.ToString();
        var key=(t.State,t.Choice,t.Reason,t.From,t.To,t.Loaded,t.TargetLoaded,t.InHands,t.Ready);
        if(!force&&key==r.LogKey&&Time.time-r.LogAt<.25f)return;r.LogKey=key;r.LogAt=Time.time;
        var json=Diagnostics.Serialize(new{schema=1,session,sequence=++sequence,feature="W03",stage="evaluated",decision="WeaponReturn",reason=t.Reason,
            realtime=Time.realtimeSinceStartup,utc=DateTime.UtcNow.ToString("O"),configRevision=config?.Revision??0,profileId=s.ProfileId,name=s.Name,botInstance=s.Id,
            weaponReturn=t,weaponReturnConfig=config?.WeaponReturn});
        diagnostics?.Write(json);Api.Decisions.Publish(json,ex=>{if(!subscriberWarned){subscriberWarned=true;Logger.LogWarning(ex.Message);}});
    }
}
