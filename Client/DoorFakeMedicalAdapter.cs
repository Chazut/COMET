using System;
using Comet.Core;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using EFT.HealthSystem;
using HarmonyLib;
using System.Runtime.CompilerServices;
using SAIN.Components;
using SAIN.Preset.Shared.Enums;
using UnityEngine;

namespace Comet.Client;

public sealed partial class Plugin
{
    // Weak item keys, weak runtime values, token-scoped observation. No raid/player retained.
    private ConditionalWeakTable<Item,MedicalConsumptionObserver> medicalConsumption=new();
    private sealed class MedicalConsumptionObserver
    {
        internal readonly WeakReference<DoorFakeRuntime> Runtime;
        internal readonly int Token;
        internal MedicalConsumptionObserver(DoorFakeRuntime r){Runtime=new(r);Token=r.Token;}
    }
    private static void PatchDoorFakeMedical(Harmony h)=>h.Patch(
        AccessTools.Method(typeof(MedEffectHelper),nameof(MedEffectHelper.RemoveItem),new[]{typeof(Item)})??throw new MissingMethodException("D06 medical consumption"),
        postfix:new HarmonyMethod(typeof(Plugin),nameof(FakeMedicalConsumed)));
    private static void FakeMedicalConsumed(Item item,bool __result)
    {
        var p=Instance;
        if(__result&&p!=null&&p.medicalConsumption.TryGetValue(item,out var observer)&&observer.Runtime.TryGetTarget(out var r)
            &&r.Token==observer.Token&&r.MedicalObserving&&r.Policy.Trace?.Medical is DoorMedicalTrace med)
            med.Sample(null,false,false,true);
    }
    private static bool FakeMedicalPriority(BotComponent b)
    {
        var med=b.BotOwner.Medecine;
        var status=b.Memory.Health.HealthStatus;
        return med==null||b.Player.HealthController.FindExistingEffect<IHeavyBleeding>()!=null||b.Player.HealthController.FindExistingEffect<ILightBleeding>()!=null
            ||med.FirstAid?.IsBleeding==true||status is ETagStatus.BadlyInjured or ETagStatus.Dying
            ||b.Decision.CurrentSelfDecision is ESelfActionType.FirstAid or ESelfActionType.Surgery or ESelfActionType.Stims;
    }
    private string FindFakeMedical(BotComponent b,out Meds? item,out EBodyPart part)
    {
        item=null;part=EBodyPart.Chest;
        if(config?.DoorFakeMedical.Enabled!=true||FakeMedicalPriority(b)||!b.BotOwner.Settings.FileSettings.Mind.CAN_USE_MEDS)return "";
        var meds=b.BotOwner.Medecine;
        if(meds==null||meds.Using||b.Player.HandsController is IMedsController)return "";
        var first=meds.FirstAid;
        if(first!=null&&first.ShallStartUse()&&!first.IsBleeding&&first._bodyPartToHeal is EBodyPart injured
            &&first.CurUsingMeds is Meds aid&&b.Player.HealthController.CanApplyItem(aid,injured))
        {item=aid;part=injured;return "heal";}
        var stims=meds.Stimulators;
        if(config!.DoorFakeMedical.Stimulators&&stims?.Using==false&&stims.CanUseNow()&&stims._stimulator is Stimulator stim)
        {item=stim;return "stim";}
        return "";
    }
    private void DrawDoorFakeMedical(BotComponent b,BotState s)
    {
        var r=s.Fake;var t=r.Policy.Trace!;var med=t.Medical!;
        if(FindFakeMedical(b,out var item,out var part)!=med.Kind||item==null||!FakeWeaponReady(b))
        {r.Policy.Abort("fake-medical-unavailable",Time.time);return;}
        med.Item=item.Id.ToString();med.BodyPart=part.ToString();med.ResourceBefore=ReadFakeMedicalResource(item);med.ResourceAfter=med.ResourceBefore;
        r.MedicalItem=new WeakReference<Meds>(item);r.MedicalUseFinished=r.MedicalUseSucceeded=false;
        r.MedicalObserving=true;r.MedicalCooldownChecked=false;medicalConsumption.Remove(item);medicalConsumption.Add(item,new(r));
        r.Policy.Draw(med.Item,b.Player.HandsController.Item.Id.ToString(),Time.time);t.Grenade="";t.Guard=false;
        b.Mover.Stop();var weak=new WeakReference<DoorFakeRuntime>(r);int token=r.Token;
        if(med.Kind=="heal")
        {
            var first=b.BotOwner.Medecine.FirstAid;r.MedicalLastEnd=first.LastEndTime;r.MedicalNextBefore=first._nextPosibleUseTime;
            // This is a completion notification, not a successful draw or proof of consumption.
            first.TryApplyToCurrentPart(callback:()=>{if(weak.TryGetTarget(out var current)&&current.Token==token){current.MedicalUseFinished=true;current.MedicalUseAt=Time.time;}});
            if(!first.Using)
            {
                ObserveFakeMedical(r);
                // TryApply can refuse synchronously. A still-ready weapon with no active use is
                // not a lost draw callback and must not fault all future feints for this bot.
                if(FakeWeaponReady(b)){r.Policy.End("fake-medical-start-not-active");r.MedicalObserving=false;}
                else r.Policy.Abort("fake-medical-start-not-active",Time.time);
            }
        }
        else
        {
            // No synthetic medical flags/cooldowns: use the native item/hands operation, as in zzap.
            Callback<IMedsController> callback=value=>
            {
                if(!weak.TryGetTarget(out var current)||current.Token!=token)return;
                current.CallbackSuccess=value.Succeed&&value.Value!=null;current.CallbackPending=true;
                if(current.CallbackSuccess)value.Value!.SetOnUsedCallback(result=>
                {if(weak.TryGetTarget(out var live)&&live.Token==token){live.MedicalUseFinished=true;live.MedicalUseSucceeded|=result.Succeed;}});
            };
            b.Player.SetInHands(item,part,item.GetRandomAnimationVariant(),callback);
        }
    }
    private static double? ReadFakeMedicalResource(Meds item)
    {
        var value=item.MedKitComponent?.HpResource;
        return value.HasValue&&DoorPolicy.Finite(value.Value)&&value.Value>=0?value:null;
    }
    private static void ObserveFakeMedical(DoorFakeRuntime r)
    {
        var t=r.Policy.Trace;var med=t?.Medical;if(med==null)return;
        double? resource=r.MedicalObserving&&r.MedicalItem!=null&&r.MedicalItem.TryGetTarget(out var item)?ReadFakeMedicalResource(item):null;
        med.Sample(resource,r.MedicalUseFinished,r.MedicalUseSucceeded,false);
        if(med.ConsumptionObserved&&t!.State=="completed")r.Policy.End("fake-real-medical-use-observed");
    }
    private bool CancelDoorFakeMedical(BotComponent b,BotState s)
    {
        var r=s.Fake;var med=r.Policy.Trace!.Medical!;
        if(FakeMedicalPriority(b)){r.Policy.YieldToMedical();r.MedicalObserving=false;return false;}
        var first=b.BotOwner.Medecine?.FirstAid;
        if(med.Kind=="heal"&&!med.NativeStopRequested&&first?.Using==true)
        {
            if(first.CurUsingMeds?.Id.ToString()!=med.Item||first.LastEndTime!=r.MedicalLastEnd)
            {r.Policy.YieldToMedical();r.MedicalObserving=false;return false;}
            float before=first._nextPosibleUseTime,now=Time.time;
            med.NativeStopRequested=true;first.StopUse();ObserveFakeMedical(r);
            double? restored=DoorMedicalTrace.RestoreCooldown(before,first._nextPosibleUseTime,now,b.BotOwner.Settings.FileSettings.Mind.HEAL_DELAY_SEC,
                first.CurUsingMeds?.Id.ToString()==med.Item&&!first.Using,FakeMedicalPriority(b),med.ConsumptionObserved,med.UseCallbackCompleted);
            if(restored.HasValue){first._nextPosibleUseTime=(float)restored.Value;med.CooldownRestored=true;}
            // StopUse has no return value. Observe the actual ready weapon; do not invent acceptance.
            return false;
        }
        return b.BotOwner.WeaponManager.Selector.TakePrevWeapon();
    }
    private static void RestoreFakeMedicalCompletionCooldown(BotComponent b,DoorFakeRuntime r)
    {
        var med=r.Policy.Trace?.Medical;
        if(med?.Kind!="heal"||!med.NativeStopRequested||!r.MedicalUseFinished||r.MedicalCooldownChecked)return;
        r.MedicalCooldownChecked=true;
        var first=b.BotOwner.Medecine?.FirstAid;
        // The native completion callback can write its delay after StopUse. Repair that write once,
        // only with this operation's callback timestamp, unchanged item and no actual use/priority.
        if(first==null||first.Using||first.CurUsingMeds?.Id.ToString()!=med.Item||first.LastEndTime!=r.MedicalUseAt)return;
        double? restored=DoorMedicalTrace.RestoreCooldown(r.MedicalNextBefore,first._nextPosibleUseTime,r.MedicalUseAt,
            b.BotOwner.Settings.FileSettings.Mind.HEAL_DELAY_SEC,!med.MedicalHandoff,FakeMedicalPriority(b),med.ConsumptionObserved,false);
        if(restored.HasValue){first._nextPosibleUseTime=(float)restored.Value;med.CooldownRestored=true;}
    }
}
