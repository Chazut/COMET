using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Comet.Core;
using EFT;
using EFT.Ballistics;
using EFT.InventoryLogic;
using HarmonyLib;
using Newtonsoft.Json;
using SAIN;
using SAIN.Components;
using SAIN.Helpers;
using UnityEngine;

namespace Comet.Client;

internal sealed class CombatJournalRuntime
{
    internal readonly WeaponJournal Weapons=new();
    internal double SampleAt,WeaponAt,ContextAt=-1000,ResourcesAt=-1000,BudgetAt;
    internal int Events;
    internal long Omitted;
    internal bool Fault,Scoped;
    internal string Context="";
    internal string Personality="";
    internal object? LastContext;
    internal readonly List<Magazine> Magazines=new();
    internal readonly List<Ammo> Ammo=new();
    internal object[] Resources=Array.Empty<object>();
}

// These observers have their own fail-open boundary. A broken diagnostic must
// never trip C08's combat circuit breaker or alter a Harmony result.
public sealed partial class Plugin
{
    private ConditionalWeakTable<Player,JournalDeath> journalDeaths=new();
    private bool journalReadFaultReported;
    private sealed class JournalDeath { internal bool Recorded; }
    private static void PatchCombatJournal(Harmony h)
    {
        h.Patch(AccessTools.Method(typeof(BotComponent),nameof(BotComponent.ManualUpdate)),postfix:new HarmonyMethod(typeof(Plugin),nameof(TrackCombatJournal)));
        h.Patch(AccessTools.Method(typeof(BotWeaponSelector),nameof(BotWeaponSelector.TryChangeToSlot)),
            prefix:new HarmonyMethod(typeof(I01JournalWeaponHook),nameof(I01JournalWeaponHook.Before)){priority=int.MaxValue},
            postfix:new HarmonyMethod(typeof(I01JournalWeaponHook),nameof(I01JournalWeaponHook.After)),
            finalizer:new HarmonyMethod(typeof(I01JournalWeaponHook),nameof(I01JournalWeaponHook.Fault)));
        h.Patch(AccessTools.Method(typeof(Player),nameof(Player.ApplyDamageInfo)),
            prefix:new HarmonyMethod(typeof(I01JournalHitHook),nameof(I01JournalHitHook.Before)){priority=int.MaxValue},
            postfix:new HarmonyMethod(typeof(I01JournalHitHook),nameof(I01JournalHitHook.After)));
        h.Patch(AccessTools.Method(typeof(Player),nameof(Player.OnDead)),prefix:new HarmonyMethod(typeof(Plugin),nameof(JournalDeathObserved)){priority=int.MaxValue});
    }
    private bool JournalScope(BotComponent b)=>world!=null&&config?.Enabled==true&&Authority&&NativeAdapter.IsSupportedAi(b)
        &&b.BotActive&&!b.IsDead&&!b.GameEnding&&!Ghost(b.ProfileId)&&!ReadDowned(b.Player);
    private static void TrackCombatJournal(BotComponent __instance)
    {
        var p=Instance;if(p?.config?.Enabled!=true||(!p.config.CombatJournal.Enabled&&!p.config.WeaponJournal.Enabled&&!p.config.ControlJournal.Enabled))return;
        try
        {
            if(!p.JournalScope(__instance))return;
            if(!p.states.TryGetValue(__instance,out var s)){s=new BotState(__instance,p.config,p.session);p.states.Add(__instance,s);}
        }catch{ /* Eligibility diagnostics must never enroll an uncertain bot. */ }
    }
    private BotState? JournalState(IPlayer? player,bool allowDead=false)
    {
        if(player==null||world==null||config?.Enabled!=true||!Authority||!player.IsAI
            ||!SAINEnableClass.GetSAIN(player.ProfileId,out var b)||b==null||!NativeAdapter.IsSupportedAi(b)||Ghost(b.ProfileId))return null;
        if(!allowDead&&!JournalScope(b))return null;
        if(!states.TryGetValue(b,out var s)){s=new BotState(b,config,session);states.Add(b,s);}return s;
    }
    private object JournalContext(BotState s,string? observedService=null)
    {
        // Death, suspension and disposal can invalidate SAIN members before our final event.
        // Retain a value-only last observation, never dereference that disposed body for diagnostics.
        if(!JournalScope(s.Owner)||s.Owner.Decision==null||s.Owner.Info==null||s.Owner.BotOwner?.Memory==null)
            return s.Journal.LastContext??new { available=false };
        var b=s.Owner;var e=b.GoalEnemy;var known=e?.KnownPlaces.LastKnownPosition;
        s.Journal.Personality=b.Info.Personality.ToString();
        string action=s.Feature=="A03"?s.Decision=="ContinuePush"?"Push":s.Decision=="KeepAngle"?"Shoot":"":s.Decision;
        bool active=s.ActiveLabel.Length>0&&b.SAINLayersActive&&b.Decision.CurrentCombatDecision==s.Expected
            &&b.Decision.CurrentSelfDecision.ToString()=="None"&&b.Decision.CurrentSquadDecision.ToString()=="None"&&OutcomeAction(b,action);
        string service=observedService??OwnedPlanService(b,s,false);
        return s.Journal.LastContext=new { action=b.SAINLayersActive?b.CurrentAction?.GetType().Name??"":"OtherLayer",sainActive=b.SAINLayersActive,combat=b.Decision.CurrentCombatDecision.ToString(),
            self=b.Decision.CurrentSelfDecision.ToString(),squad=b.Decision.CurrentSquadDecision.ToString(),
            feature=service.Length>0?(service=="Reload"?"W01":"H01"):active?s.Feature:"",
            choice=service.Length>0?service:active?s.Decision:"",reason=active&&s.Feature=="A01"?s.Choice?.Reason??"":"",
            target=e?.EnemyProfileId??"",visible=e?.IsVisible==true,contactAge=e?.TimeSinceLastKnownUpdated,
            knownDistance=known.HasValue?(double?)Vector3.Distance(b.Position,known.Value):null,
            underFire=b.BotOwner.Memory.IsUnderFire,personality=b.Info.Personality.ToString() };
    }
    private void JournalWrite(BotState s,string feature,string stage,string reason,object? combat=null,object? weapon=null,bool limited=false)
    {
        var r=s.Journal;if(world==null||r.Fault)return;
        // Repeated native requests can be arbitrarily frequent. Cap expensive snapshots and output.
        if(limited){if(Time.time-r.BudgetAt>=1){r.BudgetAt=Time.time;r.Events=0;}if(++r.Events>12){r.Omitted++;return;}}
        var json=Diagnostics.Serialize(new{schema=1,session,sequence=++sequence,feature,stage="evaluated",eventKind=stage,decision=feature=="T01"?"CombatJournal":"WeaponJournal",reason,
            realtime=Time.realtimeSinceStartup,utc=DateTime.UtcNow.ToString("O"),configRevision=config?.Revision??0,
            profileId=s.ProfileId,name=s.Name,botInstance=s.Id,personality=r.Personality,
            combatJournal=combat,weaponJournal=weapon});
        diagnostics?.Write(json);Api.Decisions.Publish(json,_=>{});
    }
    private void JournalError(BotState s,Exception ex)
    {
        if(s.Journal.Fault)return;s.Journal.Fault=true;
        try{diagnostics?.Write(Diagnostics.Serialize(new{schema=1,session,feature="T01",stage="observer-unavailable",profileId=s.ProfileId}));Logger.LogWarning("COMET journal unavailable for "+s.ProfileId+": "+ex.Message);}catch{}
    }
    private void JournalReadError(string source,Exception ex)
    {
        if(journalReadFaultReported)return;journalReadFaultReported=true;
        try{diagnostics?.Write(Diagnostics.Serialize(new{schema=1,session,feature="T01",stage="observer-unavailable",source}));Logger.LogWarning("COMET journal native observation unavailable: "+source+": "+ex.Message);}catch{}
    }
    private void SampleCombatJournal(BotComponent b,BotState s)
    {
        if(s.Journal.Fault)return;
        try
        {
            var r=s.Journal;
            if(!JournalScope(b))
            {if(r.Scoped){if(config?.CombatJournal.Enabled==true)JournalWrite(s,"T01","pause","observation-suspended",new{kind="pause"});JournalWeaponEvents(s,r.Weapons.Suspend(Time.time,"observation-suspended"),true);r.Scoped=false;r.Context="";}return;}
            r.Scoped=true;
            if(config!.WeaponJournal.Enabled&&Time.time>=r.WeaponAt)
            {
                r.WeaponAt=Time.time+.1;var gun=b.Player.HandsController as Player.FirearmController;
                JournalWeaponEvents(s,r.Weapons.Observe(Time.time,gun?.Item?.Id.ToString()??"",WeaponHandsReady(b,gun),config.WeaponJournal));
            }
            if(!config.CombatJournal.Enabled||Time.time<r.SampleAt)return;r.SampleAt=Time.time+config.CombatJournal.SampleInterval;
            string service=OwnedPlanService(b,s,false);
            string key=(b.CurrentAction?.GetType().Name??"")+":"+b.SAINLayersActive+":"+b.Decision.CurrentCombatDecision+":"+b.Decision.CurrentSelfDecision+":"+b.Decision.CurrentSquadDecision+":"+b.GoalEnemy?.EnemyProfileId+":"+b.GoalEnemy?.IsVisible+":"+s.ActiveLabel+":"+service;
            if(key!=r.Context||Time.time-r.ContextAt>=5)
            {r.Context=key;r.ContextAt=Time.time;JournalWrite(s,"T01","context","observed-native-context",new{kind="context",context=JournalContext(s,service)});}
        }catch(Exception ex){JournalError(s,ex);}
    }
    private object[] JournalWeapons(BotState s)
    {
        var r=s.Journal;if(Time.time-r.ResourcesAt<1)return r.Resources;
        var b=s.Owner;var result=new List<object>(3);var magazines=r.Magazines;var ammo=r.Ammo;
        try
        {
        // All three weapons share the same reachable inventory. Scan it once per snapshot.
        bool inventoryComplete=ReloadInventory.ReadMagazines(b.Player.InventoryController,magazines);
        // Read-only APIs: never BotReload.CanReload, which can change the selected weapon.
        foreach(var slot in new[]{EquipmentSlot.FirstPrimaryWeapon,EquipmentSlot.SecondPrimaryWeapon,EquipmentSlot.Holster})
        {
            var w=SlotGun(b,slot);if(w==null)continue;var mag=w.GetCurrentMagazine();var magSlot=w.GetMagazineSlot();int reserve=0,spares=0;bool complete=true;
            ammo.Clear();
            if(!inventoryComplete||magazines.Count>128)complete=false;
            for(int i=0;i<Math.Min(128,magazines.Count);i++)
            {var m=magazines[i];if(!ReferenceEquals(m,mag)&&m.Count>0&&magSlot!=null
                &&ItemManipulator.CheckMoveIgnoringTargetItem(m,magSlot,b.Player.InventoryController).Succeeded){spares++;reserve+=m.Count;}}
            if(w.Chambers.Length>0)
            {
                b.Player.InventoryController.GetAcceptableItemsNonAlloc(BotReload._availableEquipmentSlots,ammo,
                    (Ammo a)=>a.StackObjectsCount>0&&w.Chambers[0].CanAccept(a)&&a.CheckAction(null).Succeeded);
                if(ammo.Count>128)complete=false;
            }
            int loose=0;for(int i=0;i<Math.Min(128,ammo.Count);i++)loose+=ammo[i].StackObjectsCount;
            result.Add(new{id=w.Id.ToString(),name=w.ShortName.Localized(),slot=slot.ToString(),chamber=w.ChamberAmmoCount,magazine=mag?.Count??0,
                malfunction=w.MalfState.State.ToString(),reachableMagazines=spares,reachableMagazineRounds=reserve,
                looseRounds=loose,resourcesComplete=complete});
        }
        r.ResourcesAt=Time.time;return r.Resources=result.ToArray();
        }
        finally { magazines.Clear();ammo.Clear(); }
    }
    private void JournalWeaponEvents(BotState s,IEnumerable<WeaponJournalEvent> events,bool cached=false)
    {
        foreach(var e in events)
        {
            var r=s.Journal;if(Time.time-r.BudgetAt<1&&r.Events>=12){r.Omitted++;continue;}
            JournalWrite(s,"T03",e.State,e.Reason,weapon:new{change=e,context=cached?r.LastContext:JournalContext(s),weapons=cached?r.Resources:JournalWeapons(s),cachedContext=cached,resourcesAge=Math.Max(0,Time.time-r.ResourcesAt)},limited:true);
        }
    }
    partial void EndJournalBot(BotState s,string reason)
    {
        try
        {
            if(config?.CombatJournal.Enabled==true)JournalWrite(s,"T01","pause",reason,new{kind="pause"});
            JournalWeaponEvents(s,s.Journal.Weapons.Suspend(Time.time,reason),true);
            if(s.Journal.Omitted>0){diagnostics?.Write(Diagnostics.Serialize(new{schema=1,session,feature="T03",stage="omitted",count=s.Journal.Omitted}));s.Journal.Omitted=0;}
            s.Journal.Scoped=false;s.Journal.Context="";
        }catch(Exception ex){JournalError(s,ex);}
    }
    private sealed class JournalRequest { internal BotState State=null!;internal long Id;internal string From="",To="",Origin="";internal bool Done; }
    private static class I01JournalWeaponHook
    {
        internal static void Before(BotWeaponSelector __instance,EquipmentSlot slot,out JournalRequest? __state)
        {
            __state=null;var p=Instance;if(p?.config?.WeaponJournal.Enabled!=true||!GunSlot(slot))return;
            BotState? s=null;
            try
            {
                s=p.JournalState(__instance._owner?.GetPlayer);if(s==null||s.Journal.Fault)return;
                var b=s.Owner;var gun=b.Player.HandsController as Player.FirearmController;
                p.JournalWeaponEvents(s,s.Journal.Weapons.Observe(Time.time,gun?.Item?.Id.ToString()??"",WeaponHandsReady(b,gun),p.config.WeaponJournal));
                string origin=s.HandsRecovery.Issuing?"W10":s.WeaponReturn.Intent;
                __state=new(){State=s,Id=s.Journal.Weapons.Begin(),From=gun?.Item?.Id.ToString()??"",To=SlotGun(b,slot)?.Id.ToString()??"",Origin=origin.Length==0?"unknown":origin.StartsWith("W")?origin:"W03/"+origin};
            }catch(Exception ex){if(s!=null)p.JournalError(s,ex);}
        }
        internal static void After(bool __result,JournalRequest? __state)=>Finish(__state,__result,"");
        internal static void Fault(Exception? __exception,JournalRequest? __state){if(__exception!=null)Finish(__state,false,"native-request-threw");}
        private static void Finish(JournalRequest? r,bool accepted,string reason)
        {
            var p=Instance;if(p==null||r==null||r.Done)return;r.Done=true;
            try{p.JournalWeaponEvents(r.State,r.State.Journal.Weapons.Result(r.Id,Time.time,r.From,r.To,r.Origin,accepted,reason));}
            catch(Exception ex){p.JournalError(r.State,ex);}
        }
    }
    private sealed class JournalHit { internal BotState? Victim,Attacker;internal double Health;internal object? VictimContext,AttackerContext; }
    private static class I01JournalHitHook
    {
        internal static void Before(Player __instance,DamageInfo damageInfo,out JournalHit? __state)
        {
            __state=null;var p=Instance;if(p?.config?.CombatJournal.Enabled!=true||p.world==null||!p.Authority)return;
            try
            {
                if(__instance.HealthController?.IsAlive!=true||ReadDowned(__instance))return;
                var victim=p.JournalState(__instance);var attacker=p.JournalState(damageInfo.Player?.iPlayer);
                if(victim==null&&attacker==null)return;
                __state=new(){Victim=victim,Attacker=attacker,Health=__instance.HealthController.GetBodyPartHealth(EBodyPart.Common,false).Current,
                    VictimContext=victim==null?null:p.JournalContext(victim),AttackerContext=attacker==null?null:p.JournalContext(attacker)};
            }catch(Exception ex){p.JournalReadError("health-before-hit",ex);}
        }
        internal static void After(Player __instance,DamageInfo damageInfo,EBodyPart bodyPartType,JournalHit? __state)
        {
            var p=Instance;var h=__state;if(p==null||h==null)return;
            try
            {
                double damage=Math.Max(0,h.Health-__instance.HealthController.GetBodyPartHealth(EBodyPart.Common,false).Current);
                if(damage<=0||double.IsInfinity(damage)||double.IsNaN(damage))return;
                if(h.Victim!=null)p.JournalWrite(h.Victim,"T01","hit","health-loss-observed",new{kind="hit-taken",damage,bodyPart=bodyPartType.ToString(),damageType=damageInfo.DamageType.ToString(),other=damageInfo.Player?.iPlayer?.ProfileId??"",context=h.VictimContext});
                if(h.Attacker!=null&&!ReferenceEquals(h.Attacker,h.Victim))p.JournalWrite(h.Attacker,"T01","hit","health-loss-observed",new{kind="hit-dealt",damage,bodyPart=bodyPartType.ToString(),damageType=damageInfo.DamageType.ToString(),other=__instance.ProfileId,context=h.AttackerContext});
            }catch(Exception ex){if(h.Victim!=null)p.JournalError(h.Victim,ex);if(h.Attacker!=null)p.JournalError(h.Attacker,ex);}
        }
    }
    private static void JournalDeathObserved(Player __instance,EDamageType damageType,IPlayer? ___LastAggressor)
    {
        var p=Instance;if(p?.config?.CombatJournal.Enabled!=true||p.world==null||!p.Authority)return;
        try
        {
            if(__instance.HealthController?.IsAlive!=false||ReadDowned(__instance))return;
            var seen=p.journalDeaths.GetOrCreateValue(__instance);if(seen.Recorded)return;seen.Recorded=true;
            var victim=p.JournalState(__instance,true);var attacker=p.JournalState(___LastAggressor,true);
            if(victim!=null)p.JournalWrite(victim,"T01","death","native-death-observed",new{kind="death",other=___LastAggressor?.ProfileId??"",damageType=damageType.ToString(),context=p.JournalContext(victim)});
            if(attacker!=null&&!ReferenceEquals(attacker,victim))p.JournalWrite(attacker,"T01","kill","native-death-observed",new{kind="kill",other=__instance.ProfileId,damageType=damageType.ToString(),context=p.JournalContext(attacker)});
        }catch(Exception ex){p.JournalReadError("native-death",ex);}
    }
    partial void FinishCombatJournal()
    {
        foreach(var s in states.Values)try
        {
            JournalWeaponEvents(s,s.Journal.Weapons.Suspend(Time.time,"raid-ended"));
            if(s.Journal.Omitted>0)diagnostics?.Write(Diagnostics.Serialize(new{schema=1,session,feature="T03",stage="omitted",count=s.Journal.Omitted}));
        }catch(Exception ex){JournalError(s,ex);}
        journalDeaths=new();journalReadFaultReported=false;
    }
}
