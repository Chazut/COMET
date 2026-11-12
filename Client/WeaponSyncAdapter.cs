using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Comet.Core;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using Newtonsoft.Json;
using SAIN;
using SAIN.Components;
using SAIN.Components.PlayerComponentSpace.Classes.Equipment;
using SAIN.SAINComponent.Classes.Info;
using UnityEngine;

namespace Comet.Client;

internal sealed class WeaponSyncRuntime
{
    internal readonly WeaponSyncSchedule Schedule=new();
    internal readonly string[] Contents=new string[3];
    internal readonly Weapon?[] Observed=new Weapon?[3];
    internal WeaponSyncTrace? Trace;
    internal bool Applying,ResourcesDirty=true,WasInScope,Warned;
    internal float RetryAt,LogAt=-1000;
    internal string LogKey="";
}
internal sealed class WeaponSyncSnapshot
{
    internal WeaponSyncFrame Frame=new();
    internal readonly Weapon?[] Guns=new Weapon?[3];
    internal readonly BotWeaponInfo?[] Native=new BotWeaponInfo?[3];
    internal readonly WeaponInfo?[] Sain=new WeaponInfo?[3];
    internal readonly string[] Contents=new string[3];
    internal Player.AbstractHandsController? Hands;
    internal BotWeaponInfo? MeleeInfo;
    internal int HeldIndex=-1;
}
public sealed partial class Plugin
{
    private static readonly EquipmentSlot[] SyncSlots={EquipmentSlot.FirstPrimaryWeapon,EquipmentSlot.SecondPrimaryWeapon,EquipmentSlot.Holster};
    private static MethodInfo syncCurrentSetter=null!,syncFindMods=null!;
    private static FieldInfo syncForceRecalc=null!;
    private int weaponSyncFrame=-1;
    private bool weaponSyncNotificationWarned;
    private static void PatchWeaponSync(Harmony h)
    {
        syncCurrentSetter=AccessTools.PropertySetter(typeof(SAINEquipmentClass),nameof(SAINEquipmentClass.CurrentWeaponInfo))??throw new MissingMethodException("W06 current equipment setter");
        syncFindMods=AccessTools.Method(typeof(WeaponInfo),"FindModType",new[]{typeof(IEnumerable<Mod>)})??throw new MissingMethodException("W06 sight refresh");
        syncForceRecalc=AccessTools.Field(typeof(BotWeaponInfoClass),"_forceNewCheck")??throw new MissingFieldException("W06 SAIN equipment invalidation");
        h.Patch(AccessTools.Method(typeof(BotWeaponSelector),nameof(BotWeaponSelector.UpdateWeaponsList)),postfix:new HarmonyMethod(typeof(Plugin),nameof(WeaponListChanged)));
        h.Patch(AccessTools.Method(typeof(BotWeaponManager),nameof(BotWeaponManager.UpdateFirearmsController)),postfix:new HarmonyMethod(typeof(Plugin),nameof(WeaponHandsChanged)));
        h.Patch(AccessTools.Method(typeof(BotComponent),nameof(BotComponent.ManualUpdate)),postfix:new HarmonyMethod(typeof(Plugin),nameof(EnsureWeaponSyncTracked)));
    }
    // Maintenance also runs outside SAIN combat layers, e.g. while ORBIT loots.
    private bool WeaponSyncScope(BotComponent b)=>config?.Enabled==true&&config.WeaponSync.Enabled&&NativeAdapter.IsSupportedAi(b)
        &&b.BotActive&&!b.IsDead&&!b.GameEnding&&Authority&&!Ghost(b.ProfileId);
    private static void WeaponListChanged(BotWeaponSelector __instance)=>Instance?.MarkWeaponSync(__instance?._owner?.GetPlayer,"weapon-list-changed");
    private static void WeaponHandsChanged(BotWeaponManager __instance)=>Instance?.MarkWeaponSync(__instance?._owner?.GetPlayer,"weapon-in-hands-changed");
    // Constant-time enrollment, no equipment scan: catches bots that have never had a combat decision,
    // including bots initialized before the asynchronous server configuration arrived.
    private static void EnsureWeaponSyncTracked(BotComponent __instance)
    {
        var p=Instance;if(p?.config?.Enabled!=true||!p.config.WeaponSync.Enabled||p.states.ContainsKey(__instance)
            ||!NativeAdapter.IsSupportedAi(__instance)||!__instance.BotActive||__instance.GameEnding||!p.Authority)return;
        p.MarkWeaponSync(__instance.Player,"initial-check");
    }
    private void MarkWeaponSync(Player? player,string reason)
    {
        try {
            if(player==null||config?.Enabled!=true||!config.WeaponSync.Enabled||!Authority||!player.IsAI
                ||!SAINEnableClass.GetSAIN(player.ProfileId,out var b)||b==null||!WeaponSyncScope(b))return;
            if(!states.TryGetValue(b,out var s)){s=new BotState(b,config,session);states.Add(b,s);}
            var r=s.WeaponSync;if(r.Applying)return;
            r.ResourcesDirty=true;r.Schedule.Mark(Time.time,reason);
        } catch(Exception ex){if(!weaponSyncNotificationWarned){weaponSyncNotificationWarned=true;Logger.LogWarning("W06 equipment notification skipped: "+ex.GetBaseException().Message);}}
    }
    private static string SyncId(Weapon? w)=>w?.Id.ToString()??"";
    private static Item? SelectorGun(BotWeaponSelector selector,int i)=>i==0?selector._firstPrimaryWeaponItem:i==1?selector._secondPrimaryWeaponItem:selector._holsterItem;
    private string SyncBlock(BotComponent b,BotState s)
    {
        var wm=b.BotOwner.WeaponManager;var hand=b.Player.HandsController;
        if(wm?.Selector==null||wm.info==null||b.PlayerComponent?.Equipment==null)return "sync-not-initialized";
        if(!wm.IsReady||!wm.Selector.IsWeaponReady)return "sync-weapon-not-ready";
        if(s.ReloadTiming.Policy.Active||s.QuickReload.Policy.Active||s.PistolSwap.Policy.Active||s.WeaponReturn.Policy.Active||s.WeaponRange.Policy.Pending
            ||wm.Selector.IsChanging||wm._currentWeaponInfo?.Reload?.Reloading==true||wm.info.Values.Any(x=>x?.Reload?.Reloading==true))return "sync-native-operation-pending";
        if(b.BotOwner.Medecine?.Using==true||FakeMedicalPriority(b))return "sync-medical-operation";
        if(hand is Player.FirearmController firearm)
        {
            if(firearm.IsInInteraction()||firearm.IsInReloadOperation()||firearm.Item.IsUnderBarrelDeviceActive)return "sync-hands-busy";
            if(ActualGunSlot(b,firearm.Item).Length==0)return "sync-held-weapon-between-slots";
            if(Time.time-s.AdsTravel.LastShot<.25f||Time.time-s.AdsTravel.LastFirePreparation<.25f)return "sync-firing";
        }
        else if(hand is not Player.KnifeController)return "sync-hands-busy";
        else if(!wm.info.TryGetValue(EquipmentSlot.Scabbard,out var melee)||melee==null)return "sync-melee-not-initialized";
        return "";
    }
    private static string SyncContents(Weapon? w)
    {
        if(w==null)return "";
        var ids=new List<string>();
        foreach(var mod in w.Mods){if(ids.Count==64)throw new InvalidOperationException("W06 attachment limit exceeded");ids.Add(mod.Id.ToString());}
        return SyncId(w)+":"+(w.CurrentAmmoTemplate?._id.ToString()??"")+":"+string.Join(",",ids);
    }
    private WeaponSyncSnapshot ReadWeaponSync(BotComponent b,BotState s)
    {
        var r=s.WeaponSync;var wm=b.BotOwner.WeaponManager;var eq=b.PlayerComponent.Equipment;
        var snap=new WeaponSyncSnapshot{Hands=b.Player.HandsController};
        var held=(snap.Hands as Player.FirearmController)?.Item;
        if(held==null)wm.info.TryGetValue(EquipmentSlot.Scabbard,out snap.MeleeInfo);
        var f=snap.Frame;f.Block=SyncBlock(b,s);f.Safe=f.Block.Length==0;f.Held=SyncId(held);f.ResourcesChanged=r.ResourcesDirty;
        f.NativeCurrent=SyncId(wm._currentWeaponInfo?.weapon);f.SainCurrent=SyncId(eq.CurrentWeaponInfo?.Weapon);
        f.Slots=new WeaponSyncSlot[3];
        for(int i=0;i<3;i++)
        {
            var slot=SyncSlots[i];var gun=snap.Guns[i]=SlotGun(b,slot);
            wm.info.TryGetValue(slot,out var native);eq.WeaponInfos.TryGetValue(slot,out var sain);
            snap.Native[i]=native;snap.Sain[i]=sain;snap.Contents[i]=SyncContents(gun);
            if(held!=null&&ReferenceEquals(held,gun))snap.HeldIndex=i;
            f.Slots[i]=new(){Slot=slot.ToString(),Actual=SyncId(gun),Name=gun?.ShortName.Localized()??"",NativeBefore=SyncId(native?.weapon),SainBefore=SyncId(sain?.Weapon),
                NativePresent=wm.info.ContainsKey(slot),SainPresent=eq.WeaponInfos.ContainsKey(slot),
                NativeMatches=gun==null?!wm.info.ContainsKey(slot):native!=null&&ReferenceEquals(native.weapon,gun),
                SainMatches=gun==null?!eq.WeaponInfos.ContainsKey(slot):sain!=null&&ReferenceEquals(sain.Weapon,gun),
                SelectorMatches=ReferenceEquals(SelectorGun(wm.Selector,i),gun),ContentsChanged=snap.Contents[i]!=r.Contents[i]};
        }
        f.NativeCurrentMatches=snap.HeldIndex<0?ReferenceEquals(wm._currentWeaponInfo,snap.MeleeInfo)&&snap.MeleeInfo!=null:ReferenceEquals(wm._currentWeaponInfo,snap.Native[snap.HeldIndex])&&snap.Native[snap.HeldIndex]!=null&&ReferenceEquals(wm._currentWeaponInfo?.weapon,held);
        f.SainCurrentMatches=snap.HeldIndex<0?eq.CurrentWeaponInfo==null:ReferenceEquals(eq.CurrentWeaponInfo,snap.Sain[snap.HeldIndex])&&snap.Sain[snap.HeldIndex]!=null&&ReferenceEquals(eq.CurrentWeaponInfo?.Weapon,held);
        return snap;
    }
    private static void RefreshSainWeapon(WeaponInfo info,Player player)
    {
        // SAIN Update dereferences the ammo template. Empty/custom guns can have none; refresh their sights safely.
        if(info.Weapon.CurrentAmmoTemplate!=null)info.Update(player);
        else syncFindMods.Invoke(info,new object[]{info.Weapon.Mods});
    }
    private bool ApplyWeaponSync(BotComponent b,BotState s,WeaponSyncSnapshot snap,WeaponSyncTrace trace)
    {
        var wm=b.BotOwner.WeaponManager;var eq=b.PlayerComponent.Equipment;
        var freshNative=new BotWeaponInfo?[3];var freshSain=new WeaponInfo?[3];var retired=new List<WeaponInfo>();
        bool committed=false;
        try {
            for(int i=0;i<3;i++)
            {
                var row=snap.Frame.Slots[i];var gun=snap.Guns[i];
                if(row.NativeOperation is "add" or "replace")freshNative[i]=new BotWeaponInfo(b.BotOwner,gun!,SyncSlots[i],wm.ChangeToMode);
                if(row.SainOperation is "add" or "replace") {freshSain[i]=new WeaponInfo(gun!);RefreshSainWeapon(freshSain[i]!,b.Player);}
            }
            // Constructors/other hooks must not turn an obsolete snapshot into a repair of a different gun.
            if(!WeaponSyncScope(b)||SyncBlock(b,s).Length>0||!ReferenceEquals(snap.Hands,b.Player.HandsController)
                ||Enumerable.Range(0,3).Any(i=>!ReferenceEquals(snap.Guns[i],SlotGun(b,SyncSlots[i]))||snap.Contents[i]!=SyncContents(snap.Guns[i])))return false;
            for(int i=0;i<3;i++)
            {
                var row=snap.Frame.Slots[i];var slot=SyncSlots[i];
                if(row.SainOperation=="refresh")RefreshSainWeapon(snap.Sain[i]!,b.Player);
                if(row.NativeOperation=="remove")wm.info.Remove(slot);else if(freshNative[i]!=null)wm.info[slot]=freshNative[i]!;
                if(row.SainOperation=="remove"||freshSain[i]!=null)
                {
                    if(snap.Sain[i]!=null)retired.Add(snap.Sain[i]!);
                    if(freshSain[i]==null)eq.WeaponInfos.Remove(slot);else {eq.WeaponInfos[slot]=freshSain[i]!;freshSain[i]=null;}
                }
            }
            // Preserve the native melee object when a knife is held; never leave a removed gun active.
            if(!snap.Frame.NativeCurrentMatches){wm._currentWeaponInfo=snap.HeldIndex>=0?wm.info[SyncSlots[snap.HeldIndex]]:snap.MeleeInfo!;trace.NativeCurrentUpdated=true;}
            var current=snap.HeldIndex<0?null:eq.WeaponInfos[SyncSlots[snap.HeldIndex]];
            if(!ReferenceEquals(eq.CurrentWeaponInfo,current)){syncCurrentSetter.Invoke(eq,new object?[]{current});trace.SainCurrentUpdated=true;}
            if(snap.Frame.Slots.Any(x=>!x.SelectorMatches)){wm.Selector.UpdateWeaponsList();trace.SelectorUpdated=true;}
            InvalidateSyncedEquipment(b,s,snap);trace.ResourcesInvalidated=true;
            trace.Changed=true;committed=true;return true;
        } finally {
            // Only our unused candidates or detached old SAIN objects are disposed, never native reload callbacks.
            foreach(var candidate in freshSain)candidate?.Dispose();
            foreach(var old in retired.Distinct())if(!ReferenceEquals(eq.CurrentWeaponInfo,old)&&!eq.WeaponInfos.Values.Contains(old))old.Dispose();
            if(!committed)s.WeaponSync.ResourcesDirty=true;
        }
    }
    private void InvalidateSyncedEquipment(BotComponent b,BotState s,WeaponSyncSnapshot snap)
    {
        bool changed=Enumerable.Range(0,3).Any(i=>!ReferenceEquals(s.WeaponSync.Observed[i],snap.Guns[i])||s.WeaponSync.Contents[i]!=snap.Contents[i]);
        // Keep the last known weapon classification during the bounded refresh.
        // ReadLoadout returns a neutral Pending snapshot (no supplies/ready claim),
        // and only reuses classification when the held weapon id still matches.
        // Clearing it made an unchanged gun look like two tactical weapon swaps.
        s.Loadout.Refresh.Invalidate("weapon-sync");
        s.ReloadTiming.LastFrame=null;s.ReloadTiming.Next=0;s.ReloadTiming.Key="";
        s.WeaponReturn.Frame=null;s.WeaponReturn.Next=0;
        if(!snap.Frame.Slots.Any(x=>x.Actual==s.WeaponReturn.ReturnId))s.WeaponReturn.ReturnId="";
        if(changed){EndWeaponRange(s,"range-equipment-synchronized");s.WeaponRange.Next=0;}
        if(changed&&b.Info?.WeaponInfo!=null)syncForceRecalc.SetValue(b.Info.WeaponInfo,true);
    }
    private void SampleWeaponSync(BotComponent b,BotState s)
    {
        var r=s.WeaponSync;
        if(!WeaponSyncScope(b)){EndWeaponSync(s,"sync-out-of-scope");return;}
        r.WasInScope=true;
        if(r.Applying||Time.time<r.RetryAt||!r.Schedule.Due(Time.time)||weaponSyncFrame==Time.frameCount)return;
        if(!doorBudget.TryBegin(Time.frameCount,b))return;
        weaponSyncFrame=Time.frameCount;doorClock.Restart();r.Applying=true;
        try {
            var blocked=SyncBlock(b,s);
            if(blocked.Length>0)
            {
                // Keep the request pending without traversing equipment during a native operation.
                r.Schedule.Defer(Time.time);
                r.Trace=new WeaponSyncTrace{Trigger=r.Schedule.Trigger,Reason=blocked};
                RecordWeaponSync(b,s);return;
            }
            int revision=r.Schedule.Revision;var snap=ReadWeaponSync(b,s);var f=snap.Frame;
            var t=new WeaponSyncTrace{Trigger=r.Schedule.Trigger,Held=f.Held,NativeCurrentBefore=f.NativeCurrent,SainCurrentBefore=f.SainCurrent,Slots=f.Slots};
            if(!WeaponSyncPlan.Prepare(f))
            {t.Reason=f.Block.Length>0?f.Block:"sync-invalid-equipment";r.Schedule.Defer(Time.time);r.Trace=t;RecordWeaponSync(b,s);return;}
            bool work=WeaponSyncPlan.NeedsWork(f);
            if(work&&!ApplyWeaponSync(b,s,snap,t)){t.Reason="sync-equipment-changed-before-apply";r.Schedule.Defer(Time.time);r.Trace=t;RecordWeaponSync(b,s);return;}
            var after=work?ReadWeaponSync(b,s):snap;t.NativeCurrentAfter=after.Frame.NativeCurrent;t.SainCurrentAfter=after.Frame.SainCurrent;
            for(int i=0;i<3;i++){t.Slots[i].NativeAfter=after.Frame.Slots[i].NativeBefore;t.Slots[i].SainAfter=after.Frame.Slots[i].SainBefore;}
            t.Verified=WeaponSyncPlan.Matches(after.Frame);t.State=t.Verified?"verified":"applied";t.Reason=t.Verified?"sync-equipment-verified":"sync-verification-pending";
            bool report=work||r.Trace?.State=="deferred"||r.Trace?.State=="failed";
            if(t.Verified)
            {
                for(int i=0;i<3;i++){r.Contents[i]=snap.Contents[i];r.Observed[i]=snap.Guns[i];}
                r.ResourcesDirty=false;r.Schedule.Complete(Time.time,config!.WeaponSync.BackupSeconds,revision);
            }
            else r.Schedule.Defer(Time.time);
            if(report){r.Trace=t;RecordWeaponSync(b,s,true);}
        } finally {r.Applying=false;doorClock.Stop();doorBudget.Charge(doorClock.Elapsed.TotalMilliseconds);if(r.Trace!=null)r.Trace.SampleMilliseconds=doorClock.Elapsed.TotalMilliseconds;}
    }
    private void EndWeaponSync(BotState s,string reason)
    {
        var r=s.WeaponSync;if(!r.WasInScope)return;r.WasInScope=false;r.Schedule.Reset();r.ResourcesDirty=true;
        Array.Clear(r.Observed,0,3);Array.Clear(r.Contents,0,3);r.RetryAt=0;
        if(r.Trace!=null){r.Trace.State="ended";r.Trace.Reason=reason;r.Trace.ValidFor=0;RecordWeaponSync(null,s,true);}
    }
    private void WeaponSyncError(BotState s,Exception ex)
    {
        var r=s.WeaponSync;r.RetryAt=Time.time+1;r.ResourcesDirty=true;r.Schedule.Mark(Time.time,"retry-after-error");
        r.Trace=new(){State="failed",Reason="sync-adapter-error",Trigger=r.Schedule.Trigger};RecordWeaponSync(null,s);
        if(!r.Warned){r.Warned=true;Logger.LogWarning("W06 equipment sync deferred: "+ex.GetBaseException().Message);}
    }
    private void RecordWeaponSync(BotComponent? b,BotState s,bool force=false)
    {
        var r=s.WeaponSync;var t=r.Trace;if(t==null)return;
        if(r.Applying)t.SampleMilliseconds=doorClock.Elapsed.TotalMilliseconds;
        if(b?.Decision!=null)t.Action=b.Decision.CurrentSelfDecision==SAIN.Preset.Shared.Enums.ESelfActionType.Reload?"Reload":b.Decision.CurrentCombatDecision.ToString();
        var key=t.State+":"+t.Reason+":"+t.Held+":"+string.Join("|",t.Slots.Select(x=>x.Actual+":"+x.NativeBefore+":"+x.SainBefore));
        if(!force&&key==r.LogKey&&Time.time-r.LogAt<1)return;r.LogKey=key;r.LogAt=Time.time;
        var json=Diagnostics.Serialize(new{schema=1,session,sequence=++sequence,feature="W06",stage="evaluated",decision="WeaponSync",reason=t.Reason,
            realtime=Time.realtimeSinceStartup,utc=DateTime.UtcNow.ToString("O"),configRevision=config?.Revision??0,profileId=s.ProfileId,name=s.Name,botInstance=s.Id,
            weaponSync=t,weaponSyncConfig=config?.WeaponSync});
        diagnostics?.Write(json);Api.Decisions.Publish(json,ex=>{if(!subscriberWarned){subscriberWarned=true;Logger.LogWarning(ex.Message);}});
    }
}
