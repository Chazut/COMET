using System;
using System.Collections.Generic;
using System.Linq;
using Comet.Core;
using EFT;
using EFT.InventoryLogic;
using Newtonsoft.Json;
using SAIN.Components;
using SAIN.Preset.Shared.Enums;
using UnityEngine;

namespace Comet.Client;

internal sealed class WeaponRangeRuntime
{
    internal readonly WeaponRangePolicy Policy=new();
    internal float Next,LogAt=-1000;
    internal (string,string,string,string,string,bool) LogKey;
    internal bool Fault;
}
public sealed partial class Plugin
{
    private bool WeaponRangeScope(BotComponent b)=>config?.Enabled==true&&config.WeaponRange.Enabled&&NativeAdapter.IsSupportedAi(b)
        &&b.BotActive&&!b.IsDead&&!b.GameEnding&&Authority&&!Ghost(b.ProfileId)&&b.SAINLayersActive;
    private WeaponReturnConfig RangeArbiterConfig()=>config!.WeaponReturn.Enabled?config.WeaponReturn:new WeaponReturnConfig
    {
        // W05 still uses one arbiter when W03's autonomous return feature is disabled.
        Enabled=true,MinimumHold=config.WeaponReturn.MinimumHold,RetryDelay=config.WeaponReturn.RetryDelay,
        CalmDelay=config.WeaponReturn.CalmDelay,DistanceMargin=config.WeaponReturn.DistanceMargin,PistolReturnDelay=config.WeaponReturn.PistolReturnDelay
    };
    private static readonly string[] RangeOpticTypes={"55818add4bdc2d5b648b456f","55818ae44bdc2dde698b456c","55818aeb4bdc2ddc698b456a"};
    private static readonly string[] RangeDotTypes={"55818ad54bdc2ddc698b4569","55818acf4bdc2dde698b456b"};
    private static readonly string[] RangeIronTypes={"55818ac54bdc2d5b648b456e"};
    private static bool RangeAttachmentType(Mod mod,string[] ids)
    {
        foreach(string id in ids)
            if(JsonTypes.TypeTable.TryGetValue(id,out var a)&&a.IsInstanceOfType(mod)
                ||JsonTypes.TemplateTypeTable.TryGetValue(id,out var b)&&b.IsInstanceOfType(mod.Template))return true;
        return false;
    }
    private static WeaponRangeGun ReadRangeGun(Weapon w,WeaponReturnGun ammo)
    {
        var g=new WeaponRangeGun{Id=ammo.Id,Slot=ammo.Slot,Name=w.ShortName.Localized(),Class=w.WeapClass,
            Loaded=ammo.Loaded,Reserve=ammo.Reserve,Usable=ammo.Usable,Bolt=w.BoltAction,
            Automatic=w.WeapFireType?.Contains(Weapon.EFireMode.fullauto)==true};
        bool optic=false,dot=false,unknown=false;var ids=new List<string>();int count=0;
        // Read actual attachments on EACH weapon, including guns never drawn or just looted.
        // SAIN's CurrentWeaponInfo and uninitialized secondary WeaponInfo are not reliable for this comparison.
        foreach(var mod in w.Mods)
        {
            if(++count>64){unknown=true;g.OpticEvidence="attachment-budget";break;}
            ids.Add(mod.Id.ToString());
            bool isOptic=RangeAttachmentType(mod,RangeOpticTypes),isDot=RangeAttachmentType(mod,RangeDotTypes);
            optic|=isOptic;dot|=isDot;
            // Iron sights also carry SightComponent. They remain usable with or
            // without a scope, including secondary weapons that were never drawn.
            if(!isOptic&&!isDot&&!RangeAttachmentType(mod,RangeIronTypes)&&mod.GetItemComponent<SightComponent>()!=null)
            {unknown=true;if(g.OpticEvidence.Length==0)g.OpticEvidence="unrecognized-sight: "+mod.TemplateId+" ("+mod.GetType().Name+")";}
        }
        g.Optic=unknown?"unknown":optic?(dot?"scope-and-dot":"scope"):dot?"dot":"iron";
        if(g.OpticEvidence.Length==0)g.OpticEvidence="attachment-families: "+g.Optic;
        g.Fingerprint=g.Class+":"+g.Optic+":"+g.Automatic+":"+g.Bolt+":"+string.Join(",",ids);
        WeaponRangeModel.Classify(g);return g;
    }
    private WeaponRangeFrame? WeaponRangeFrameFor(BotComponent b,BotState s,WeaponReturnFrame f)
    {
        if(!doorBudget.TryBegin(Time.frameCount,b))return null;
        doorClock.Restart();
        try {
        var first=SlotGun(b,EquipmentSlot.FirstPrimaryWeapon);var second=SlotGun(b,EquipmentSlot.SecondPrimaryWeapon);
        if(first==null||second==null||!WeaponReturnPolicy.IsPrimary(f.Current.Slot))return null;
        var a=ReturnTarget(f,first.Id.ToString());var z=ReturnTarget(f,second.Id.ToString());
        if(a.Id.Length==0||z.Id.Length==0||a.Loaded!=ReloadLoaded(first)||z.Loaded!=ReloadLoaded(second))return null;
        var frame=new WeaponRangeFrame{Now=Time.time,Current=f.Current.Id,Weapons=new[]{ReadRangeGun(first,a),ReadRangeGun(second,z)},
            SinceShot=Math.Max(0,Time.time-s.AdsTravel.LastShot),Ready=WeaponHandsReady(b,b.Player.HandsController as Player.FirearmController),
            Busy=f.Busy||Time.time-s.AdsTravel.LastFirePreparation<.25f||s.PistolSwap.Policy.Active,
            Reloading=f.Reloading,Incoming=f.Incoming,Protected=f.Protected};
        var nativeInfo=b.BotOwner.WeaponManager.info;
        // W06 owns general native cache resynchronization. Do not draw a replacement whose native
        // reload entry still belongs to the discarded gun. A never-drawn slot is initialized natively.
        if(nativeInfo.TryGetValue(EquipmentSlot.FirstPrimaryWeapon,out var firstInfo)&&firstInfo!=null&&!ReferenceEquals(firstInfo.weapon,first)
            ||nativeInfo.TryGetValue(EquipmentSlot.SecondPrimaryWeapon,out var secondInfo)&&secondInfo!=null&&!ReferenceEquals(secondInfo.weapon,second))
            frame.Block="range-native-weapon-cache-stale";
        var contacts=new List<WeaponRangeContact>();int count=0;
        foreach(var e in b.EnemyController.KnownEnemies)
        {
            if(++count>16){frame.Truncated=true;break;}
            if(e==null||!e.EnemyKnown)continue;
            frame.Incoming|=e.Status.ShotAtMeRecently;
            var seen=e.KnownPlaces.LastSeenPlace;var heard=e.KnownPlaces.LastHeardPlace;
            // Both are personal evidence; a fresh sound must not erase a still-valid visual observation.
            if(seen!=null)contacts.Add(new(){Id=e.EnemyProfileId,Source="personal-vision",Age=Math.Max(0,seen.TimeSincePositionUpdated),Distance=Vector3.Distance(b.Position,seen.Position)});
            if(heard!=null&&(seen==null||heard.TimeSincePositionUpdated<seen.TimeSincePositionUpdated))
                contacts.Add(new(){Id=e.EnemyProfileId,Source="personal-hearing",Age=Math.Max(0,heard.TimeSincePositionUpdated),Distance=Vector3.Distance(b.Position,heard.Position)});
            if(e.IsVisible&&seen==null)frame.Truncated=true;
        }
        if(contacts.Count>16)frame.Truncated=true;
        frame.Contacts=contacts.Take(16).ToArray();return frame;
        } finally {doorClock.Stop();doorBudget.Charge(doorClock.Elapsed.TotalMilliseconds);}
    }
    private bool KeepRangeChoice(BotComponent b,BotState s,WeaponReturnFrame f,WeaponReturnGun target,string origin)
    {
        if(origin=="W05"||origin=="W02"||!WeaponRangeScope(b)||s.WeaponRange.Fault||!f.Current.LoadedUseful
            ||!WeaponReturnPolicy.IsPrimary(target.Slot)||target.Id==f.Current.Id)return false;
        var frame=WeaponRangeFrameFor(b,s,f);
        // Defer this non-urgent native switch rather than bypassing W05 without a fresh comparison.
        // Empty/broken current weapons and W02 emergencies have already been excluded above.
        if(frame==null)return SlotGun(b,EquipmentSlot.FirstPrimaryWeapon)!=null&&SlotGun(b,EquipmentSlot.SecondPrimaryWeapon)!=null;
        s.WeaponRange.Policy.Evaluate(frame,config!.WeaponRange);
        bool keep=s.WeaponRange.Policy.ProtectCurrent;
        if(keep)RecordWeaponRange(b,s);
        return keep;
    }
    private void SampleWeaponRange(BotComponent b,BotState s)
    {
        var r=s.WeaponRange;if(r.Fault)return;
        if(!WeaponRangeScope(b)){EndWeaponRange(s,"range-out-of-scope");return;}
        if(r.Policy.Pending)
        {
            ObserveWeaponReturn(b,s);r.Policy.Arbitration(s.WeaponReturn.Policy.Trace,Time.time);RecordWeaponRange(b,s);
            if(r.Policy.Pending)return;
        }
        if(Time.time<r.Next)return;r.Next=Time.time+.25f+(s.Id.GetHashCode()&15)*.003f;
        if(s.WeaponReturn.Fault||s.WeaponReturn.Policy.Active)return;
        // No inventory walk for the common case with only one primary, or a pistol emergency.
        var firearm=b.Player.HandsController as Player.FirearmController;
        if(firearm==null||!WeaponReturnPolicy.IsPrimary(ActualGunSlot(b,firearm.Item))
            ||SlotGun(b,EquipmentSlot.FirstPrimaryWeapon)==null||SlotGun(b,EquipmentSlot.SecondPrimaryWeapon)==null)
        {EndWeaponRange(s,"range-no-primary-pair");return;}
        var f=WeaponReturnFrameFor(b,s);if(f==null){r.Policy.Defer("range-sample-deferred");RecordWeaponRange(b,s);return;}
        var frame=WeaponRangeFrameFor(b,s,f);if(frame==null){r.Policy.Defer("range-comparison-deferred");RecordWeaponRange(b,s);return;}
        string next=r.Policy.Evaluate(frame,config!.WeaponRange);
        if(next.Length>0)
        {
            var before=s.WeaponReturn.Policy.Trace;
            NativeWeaponRequest(b,s,ReturnTarget(f,next),"W05");
            if(ReferenceEquals(before,s.WeaponReturn.Policy.Trace))r.Policy.Decline("range-arbiter-unavailable");
            else r.Policy.Arbitration(s.WeaponReturn.Policy.Trace,Time.time);
        }
        RecordWeaponRange(b,s);
    }
    private void EndWeaponRange(BotState s,string why)
    {if(s.WeaponRange.Policy.Trace==null||s.WeaponRange.Policy.Trace.State=="ended")return;s.WeaponRange.Policy.Clear(why);RecordWeaponRange(null,s,true);}
    private void WeaponRangeError(BotState s,Exception ex)
    {if(s.WeaponRange.Fault)return;s.WeaponRange.Fault=true;EndWeaponRange(s,"range-adapter-error");Logger.LogWarning("W05 yielded to SAIN: "+ex.GetBaseException().Message);}
    private void RecordWeaponRange(BotComponent? b,BotState s,bool force=false)
    {
        var r=s.WeaponRange;var t=r.Policy.Trace;if(t==null)return;
        var key=(t.State,t.Reason,t.From,t.To,t.Target,t.Ready);
        if(!force&&key==r.LogKey&&Time.time-r.LogAt<.25f)return;r.LogKey=key;r.LogAt=Time.time;
        if(b!=null)t.Action=b.Decision.CurrentSelfDecision==ESelfActionType.Reload?"Reload":b.Decision.CurrentCombatDecision.ToString();
        var json=Diagnostics.Serialize(new{schema=1,session,sequence=++sequence,feature="W05",stage="evaluated",decision="WeaponRange",reason=t.Reason,
            realtime=Time.realtimeSinceStartup,utc=DateTime.UtcNow.ToString("O"),configRevision=config?.Revision??0,profileId=s.ProfileId,name=s.Name,botInstance=s.Id,
            weaponRange=t,weaponRangeConfig=config?.WeaponRange});
        diagnostics?.Write(json);Api.Decisions.Publish(json,ex=>{if(!subscriberWarned){subscriberWarned=true;Logger.LogWarning(ex.Message);}});
    }
}
