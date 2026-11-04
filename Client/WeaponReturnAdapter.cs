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
    internal string Intent="",ReturnId="",LogKey="";
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
        h.Patch(AccessTools.Method(typeof(BotWeaponSelector),nameof(BotWeaponSelector.ChangeToMain)),prefix:new HarmonyMethod(typeof(Plugin),nameof(ChooseWeaponReturn)));
        h.Patch(AccessTools.Method(typeof(BotWeaponSelector),nameof(BotWeaponSelector.ShallChangeIfNoAmmo)),prefix:new HarmonyMethod(typeof(Plugin),nameof(SuppressRandomWeaponSwap)));
    }
    private bool WeaponReturnScope(BotComponent b)=>config?.Enabled==true&&(config.WeaponReturn.Enabled||config.WeaponRange.Enabled)&&NativeAdapter.IsPmcAi(b)
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
        foreach(var slot in new[]{EquipmentSlot.FirstPrimaryWeapon,EquipmentSlot.SecondPrimaryWeapon,EquipmentSlot.Holster})
            if(ReferenceEquals(SlotGun(b,slot),w))return slot.ToString();
        return "";
    }
    private static bool WeaponHandsReady(BotComponent b,Player.FirearmController? firearm)
    {
        if(b==null||b.GameEnding||b.IsDead)return false;
        var wm=b.BotOwner?.WeaponManager;var selector=wm?.Selector;
        return firearm!=null&&wm!=null&&selector!=null&&wm.IsReady&&selector.IsWeaponReady&&!selector.IsChanging
            &&!firearm.IsInInteraction()&&!firearm.IsInReloadOperation()&&firearm.CanStartReload();
    }
    private WeaponReturnGun ReadReturnGun(BotComponent b,BotState s,EquipmentSlot slot)
    {
        var w=SlotGun(b,slot);if(w==null)return new();
        var mag=w.GetCurrentMagazine();
        var g=new WeaponReturnGun{Id=w.Id.ToString(),Slot=slot.ToString(),Chamber=w.ChamberAmmoCount,
            Magazine=w.ReloadMode==Weapon.EReloadMode.OnlyBarrel?0:mag?.Count??0,
            Capacity=w.ReloadMode==Weapon.EReloadMode.OnlyBarrel?w.Chambers.Length:(mag?.MaxCount??0)+w.Chambers.Length,
            Usable=w.MalfState.State==Weapon.EMalfunctionState.None};
        g.Loaded=g.Chamber+g.Magazine;
        try {
            var f=ReloadResources(b,s,w,new ReloadTimingFrame{Weapon=g.Id,Loaded=g.Loaded,Chamber=g.Chamber,Magazine=g.Magazine,Capacity=g.Capacity},true);
            // An incomplete inventory check cannot establish a reload resource.
            if(f.Block.Length==0){g.Reserve=f.Reserve;g.Resource=f.Resource;}
        } finally {s.ReloadTiming.Magazines.Clear();s.ReloadTiming.Ammo.Clear();}
        return g;
    }
    private WeaponReturnFrame? WeaponReturnFrameFor(BotComponent b,BotState s)
    {
        var r=s.WeaponReturn;var firearm=b.Player.HandsController as Player.FirearmController;
        if(firearm==null||ActualGunSlot(b,firearm.Item).Length==0)return null;
        if(r.Frame!=null&&Time.time<r.Next&&r.Frame.Current.Id==firearm.Item.Id.ToString()&&r.Frame.Current.Loaded==ReloadLoaded(firearm.Item))
        {
            // Volatile native state is never cached across a reload/interaction.
            r.Frame.ContactAge+=Math.Max(0,Time.time-r.Frame.Now);r.Frame.Now=Time.time;r.Frame.Ready=WeaponHandsReady(b,firearm);
            r.Frame.Incoming|=b.BotOwner.Memory.IsUnderFire||b.Medical.TimeSinceShot<1.5;
            if(r.Frame.Incoming)r.Frame.Far=false;
            r.Frame.Current.Usable=firearm.Item.MalfState.State==Weapon.EMalfunctionState.None;
            r.Frame.Reloading=s.QuickReload.Policy.Active||b.BotOwner.WeaponManager.Reload.Reloading||firearm.IsInReloadOperation();
            r.Frame.Busy=WeaponReturnBusy(b,firearm);return r.Frame;
        }
        if(!doorBudget.TryBegin(Time.frameCount))return null;
        doorClock.Restart();
        try {
            var first=ReadReturnGun(b,s,EquipmentSlot.FirstPrimaryWeapon);var second=ReadReturnGun(b,s,EquipmentSlot.SecondPrimaryWeapon);var pistol=ReadReturnGun(b,s,EquipmentSlot.Holster);
            var main=first;var other=second;var selector=b.BotOwner.WeaponManager.Selector;
            if(second.Id.Length>0&&(second.Id==r.ReturnId||first.Id.Length==0||first.Id!=r.ReturnId&&selector._mainWeapon==EquipmentSlot.SecondPrimaryWeapon)){main=second;other=first;}
            string id=firearm.Item.Id.ToString();var current=id==first.Id?first:id==second.Id?second:pistol;
            var f=new WeaponReturnFrame{Now=Time.time,Current=current,Main=main,Other=other,Pistol=pistol,
                Ready=WeaponHandsReady(b,firearm),Reloading=s.QuickReload.Policy.Active||b.BotOwner.WeaponManager.Reload.Reloading||firearm.IsInReloadOperation(),Busy=WeaponReturnBusy(b,firearm),
                Incoming=b.BotOwner.Memory.IsUnderFire||b.Medical.TimeSinceShot<1.5};
            bool haveFar=false,haveNear=false;int count=0;Vector3 closestKnown=b.Position;double closest=double.MaxValue;
            foreach(var e in b.EnemyController.KnownEnemies)
            {
                if(++count>16){f.Truncated=true;haveNear=true;break;}
                if(e==null||!e.EnemyKnown)continue;
                var place=e.KnownPlaces.LastSeenPlace;var heard=e.KnownPlaces.LastHeardPlace;
                if(heard!=null&&(place==null||heard.TimeSincePositionUpdated<place.TimeSincePositionUpdated))place=heard;
                if(e.Status.ShotAtMeRecently)f.Incoming=true;
                if(place==null){if(e.IsVisible){f.Incoming=true;haveNear=true;}continue;}
                double age=Math.Max(0,place.TimeSincePositionUpdated);double distance=Vector3.Distance(b.Position,place.Position);
                f.LastThreatAt=Math.Max(f.LastThreatAt,e.IsVisible?Time.time:Time.time-age);
                if(age<=config!.WeaponReturn.CalmDelay&&distance<=config.PistolSwap.MaxDistance+config.WeaponReturn.DistanceMargin)haveNear=true;
                if(e.IsVisible&&age>config!.PistolSwap.ContactMemory)haveNear=true;
                if(age<=config!.PistolSwap.ContactMemory)
                {
                    if(distance>config.PistolSwap.MaxDistance+config.WeaponReturn.DistanceMargin)haveFar=true;else haveNear=true;
                }
                if(distance<closest&&age<=config.WeaponReturn.CalmDelay)
                {closest=distance;closestKnown=place.Position;f.Target=e.EnemyProfileId;f.Source=ReferenceEquals(place,heard)?"personal-hearing":"personal-vision";f.Distance=distance;f.ContactAge=age;}
            }
            f.Far=haveFar&&!haveNear&&!f.Incoming&&!f.Truncated;
            // Only reached, stationary cover with a known direction can justify returning to an empty reloadable rifle.
            f.Protected=f.Target.Length>0&&!f.Truncated&&count<=1&&b.Cover.CoverInUse?.BotInThisCover==true&&b.Player.Velocity.magnitude<.35f&&CombatProtection(b.Position,closestKnown)==7;
            r.Frame=f;r.Next=Time.time+.25f;r.Policy.NoteThreat(f);return f;
        } finally {doorClock.Stop();doorBudget.Charge(doorClock.Elapsed.TotalMilliseconds);}
    }
    private static bool WeaponReturnBusy(BotComponent b,Player.FirearmController f)=>f.IsInInteraction()||b.BotOwner.Medecine?.Using==true
        ||f.Item.IsUnderBarrelDeviceActive||FakeMedicalPriority(b)||b.Grenade.GrenadeDangerPoint!=null||b.Flashed.IsFlashed;
    private static WeaponReturnGun ReturnTarget(WeaponReturnFrame f,string id)
    {foreach(var g in new[]{f.Main,f.Other,f.Pistol})if(g.Id==id&&id.Length>0)return g;return new();}
    private void ObserveWeaponReturn(BotComponent b,BotState s)
    {
        var firearm=b.Player.HandsController as Player.FirearmController;
        s.WeaponReturn.Policy.Observe(Time.time,firearm?.Item?.Id.ToString()??"",firearm==null?"":ActualGunSlot(b,firearm.Item),WeaponHandsReady(b,firearm),RangeArbiterConfig());
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
        try {
            if(p==null||!GunSlot(slot)||!p.WeaponReturnState(__instance,out var b,out s))return true;
            // Native restoration after medicine/grenades and equipment recovery keep their normal route.
            if(b.Player.HandsController is not Player.FirearmController)return true;
            __result=false;var r=s.WeaponReturn;p.ObserveWeaponReturn(b,s);
            if(r.Policy.Active)return false;
            var f=p.WeaponReturnFrameFor(b,s);if(f==null)return false;
            var actual=SlotGun(b,slot);var target=ReturnTarget(f,actual?.Id.ToString()??"");
            if(actual!=null&&(target.Id.Length==0||target.Loaded!=ReloadLoaded(actual))){r.Frame=null;return false;}
            string origin=r.Intent.Length>0?r.Intent:"native-slot";
            if(p.KeepRangeChoice(b,s,f,target,origin))return false;
            if(!p.config!.WeaponReturn.Enabled&&origin!="W05")return true;
            if(!r.Policy.Evaluate(f,target,origin,p.RangeArbiterConfig())){p.RecordWeaponReturn(b,s);return false;}
            // Recheck native readiness and equipment just before the only mutating native call.
            if(!WeaponHandsReady(b,b.Player.HandsController as Player.FirearmController)||!ReferenceEquals(SlotGun(b,slot),actual)
                ||actual==null||actual.MalfState.State!=Weapon.EMalfunctionState.None){r.Policy.End("weapon-equipment-changed");p.RecordWeaponReturn(b,s,true);return false;}
            r.Policy.Request(Time.time);p.RecordWeaponReturn(b,s,true);
            __state=new(){Bot=b,State=s,PreviousTimer=__instance._nextChangeTime,TemporaryTimer=Time.time-1};
            __instance._nextChangeTime=__state.TemporaryTimer;
            return true;
        }catch(Exception ex){if(s!=null)p?.WeaponReturnError(s,ex);return true;}
    }
    private static void WeaponSwitchResult(BotWeaponSelector __instance,bool __result,WeaponSwitchAttempt? __state)
    {
        if(__state==null||Instance==null)return;var p=Instance;var s=__state.State;
        try {
            if(!__result&&__instance._nextChangeTime==__state.TemporaryTimer)__instance._nextChangeTime=__state.PreviousTimer;
            s.WeaponReturn.Policy.Result(__result,Time.time,p.RangeArbiterConfig());s.WeaponReturn.Frame=null;
            p.ObserveWeaponReturn(__state.Bot,s);p.RecordWeaponReturn(__state.Bot,s,true);
        }catch(Exception ex){p.WeaponReturnError(s,ex);}
    }
    private static void WeaponSwitchException(BotWeaponSelector __instance,Exception? __exception,WeaponSwitchAttempt? __state)
    {
        if(__exception==null||__state==null||Instance==null)return;
        if(__instance._nextChangeTime==__state.TemporaryTimer)__instance._nextChangeTime=__state.PreviousTimer;
        Instance.WeaponReturnError(__state.State,__exception);
    }
    private static bool ChooseWeaponFallback(BotWeaponSelector __instance,ref bool __result)
    {
        var p=Instance;BotState? s=null;
        try {
            if(p==null||!p.config!.WeaponReturn.Enabled||!p.WeaponReturnState(__instance,out var b,out s)||b.Player.HandsController is not Player.FirearmController)return true;
            __result=false;p.ObserveWeaponReturn(b,s);if(s.WeaponReturn.Policy.Active)return false;
            var f=p.WeaponReturnFrameFor(b,s);if(f==null)return false;
            var id=s.WeaponReturn.Policy.Recommend(f,p.config!.WeaponReturn);
            if(id.Length>0)__result=p.NativeWeaponRequest(b,s,ReturnTarget(f,id),"native-toggle");
            else {s.WeaponReturn.Policy.Evaluate(f,f.Main,"native-toggle",p.config.WeaponReturn);p.RecordWeaponReturn(b,s);}
            return false;
        }catch(Exception ex){if(s!=null)p?.WeaponReturnError(s,ex);return true;}
    }
    private static bool ChooseWeaponReturn(BotWeaponSelector __instance,ref bool __result)
    {
        var p=Instance;BotState? s=null;
        try {
            if(p==null||!p.config!.WeaponReturn.Enabled||!p.WeaponReturnState(__instance,out var b,out s)||b.Player.HandsController is not Player.FirearmController)return true;
            __result=false;p.ObserveWeaponReturn(b,s);if(s.WeaponReturn.Policy.Active)return false;
            var f=p.WeaponReturnFrameFor(b,s);
            // Both native return entry points must consult the arbiter BEFORE
            // issuing a slot request. A rejected empty main is not a failed draw.
            if(f!=null)
            {
                if(s.WeaponReturn.Policy.Evaluate(f,f.Main,"native-return",p.RangeArbiterConfig()))
                    __result=p.NativeWeaponRequest(b,s,f.Main,"native-return");
                else p.RecordWeaponReturn(b,s);
            }
            return false;
        }catch(Exception ex){if(s!=null)p?.WeaponReturnError(s,ex);return true;}
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
        if(!WeaponReturnScope(b)){if(r.Frame!=null||r.Policy.Active)EndWeaponReturn(s,"weapon-out-of-scope");return;}
        if(Time.time<r.SampleAt)return;r.SampleAt=Time.time+.1f;
        bool pending=r.Policy.Active;ObserveWeaponReturn(b,s);
        if(pending)RecordWeaponReturn(b,s,!r.Policy.Active);
        if(r.Policy.Active||!config!.WeaponReturn.Enabled)return;
        if(Time.time<r.Next)return;
        var firearm=b.Player.HandsController as Player.FirearmController;
        if(firearm==null)return;
        // Stable loaded primaries need only cheap hands observations. Inventory scans are for actual selection work.
        if(ActualGunSlot(b,firearm.Item)!="Holster"&&ReloadLoaded(firearm.Item)>0&&firearm.Item.MalfState.State==Weapon.EMalfunctionState.None)return;
        var f=WeaponReturnFrameFor(b,s);if(f==null)return;
        string next=r.Policy.Recommend(f,config!.WeaponReturn);
        if(next.Length>0)NativeWeaponRequest(b,s,ReturnTarget(f,next),"return-policy");
        else if(f.Current.Slot=="Holster") {r.Policy.Evaluate(f,f.Main,"return-policy",config.WeaponReturn);RecordWeaponReturn(b,s);}
    }
    private void EndWeaponReturn(BotState s,string why)
    {var r=s.WeaponReturn;r.Policy.Clear(why);r.Frame=null;r.ReturnId="";RecordWeaponReturn(null,s,true);}
    private void WeaponReturnError(BotState s,Exception ex)
    {if(s.WeaponReturn.Fault)return;s.WeaponReturn.Fault=true;EndWeaponReturn(s,"weapon-adapter-error");Logger.LogWarning("W03 yielded to SAIN: "+ex.GetBaseException().Message);}
    private void RecordWeaponReturn(BotComponent? b,BotState s,bool force=false)
    {
        var r=s.WeaponReturn;var t=r.Policy.Trace;if(t==null)return;
        if(b!=null)t.Action=b.Decision.CurrentSelfDecision==ESelfActionType.Reload?"Reload":b.Decision.CurrentCombatDecision.ToString();
        string key=t.State+":"+t.Choice+":"+t.Reason+":"+t.From+":"+t.To+":"+t.Loaded+":"+t.TargetLoaded+":"+t.InHands+":"+t.Ready;
        if(!force&&key==r.LogKey&&Time.time-r.LogAt<.25f)return;r.LogKey=key;r.LogAt=Time.time;
        var json=JsonConvert.SerializeObject(new{schema=1,session,sequence=++sequence,feature="W03",stage="evaluated",decision="WeaponReturn",reason=t.Reason,
            realtime=Time.realtimeSinceStartup,utc=DateTime.UtcNow.ToString("O"),configRevision=config?.Revision??0,profileId=s.ProfileId,name=s.Name,botInstance=s.Id,
            weaponReturn=t,weaponReturnConfig=config?.WeaponReturn},Diagnostics.Json);
        diagnostics?.Write(json);Api.Decisions.Publish(json,ex=>{if(!subscriberWarned){subscriberWarned=true;Logger.LogWarning(ex.Message);}});
    }
}
