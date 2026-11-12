using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using Comet.Core;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using Newtonsoft.Json;
using SAIN.Components;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.Decision;
using SAIN.SAINComponent.Classes.EnemyClasses;
using UnityEngine;

namespace Comet.Client;

internal sealed class ReloadTimingRuntime
{
    internal readonly ReloadTimingPolicy Policy=new();
    internal readonly List<Magazine> Magazines=new();
    internal readonly List<Ammo> Ammo=new();
    internal readonly List<Magazine> BeltMagazines=new();
    internal float BeltRefillAt;
    internal bool InventoryFault;
    internal ReloadTimingFrame? LastFrame;
    internal Player.FirearmController? Firearm;
    internal float Next,SampleAt,LogAt=-1000;
    internal string Key="",LogKey="";
    internal bool Fault;
    internal void Drop(){Firearm=null;Magazines.Clear();Ammo.Clear();BeltMagazines.Clear();LastFrame=null;}
}
public sealed partial class Plugin
{
    private static System.Reflection.FieldInfo reloadLastTime=null!;
    private static Weapon? barrelReloadRequest;
    private static void PatchReloadTiming(Harmony h)
    {
        reloadLastTime=AccessTools.Field(typeof(SelfActionDecisionClass),"_lastReloadTime")??throw new MissingFieldException("W01 SAIN reload clock");
        h.Patch(AccessTools.Method(typeof(SelfActionDecisionClass),"CheckDoReload",new[]{typeof(Enemy),typeof(BotComponent)}),
            prefix:new HarmonyMethod(typeof(Plugin),nameof(ChooseReloadTiming)));
        h.Patch(AccessTools.Method(typeof(BotReloadOnlyBarrel),nameof(BotReloadOnlyBarrel.ReloadAmmo)),
            transpiler:new HarmonyMethod(typeof(Plugin),nameof(PartialBarrelReload)));
    }
    private static IEnumerable<CodeInstruction> PartialBarrelReload(IEnumerable<CodeInstruction> instructions)
    {
        var code=instructions.ToList();
        var getter=AccessTools.PropertyGetter(typeof(Weapon),nameof(Weapon.ChamberAmmoCount));
        var hits=code.Select((x,i)=>(x,i)).Where(x=>x.x.Calls(getter)).ToArray();
        // Harmony can widen short branches before invoking the transpiler. Both sizes
        // express the same predicate; keep checking the condition and constant values.
        if(hits.Length!=1||hits[0].i+4>=code.Count
            ||(code[hits[0].i+1].opcode!=OpCodes.Brfalse_S&&code[hits[0].i+1].opcode!=OpCodes.Brfalse)
            ||code[hits[0].i+2].opcode!=OpCodes.Ldc_I4_0
            ||(code[hits[0].i+3].opcode!=OpCodes.Br_S&&code[hits[0].i+3].opcode!=OpCodes.Br)
            ||code[hits[0].i+4].opcode!=OpCodes.Ldc_I4_1)
            throw new InvalidOperationException("W01 partial barrel reload contract changed: "+string.Join("; ",hits.SelectMany(hit=>code.Skip(hit.i).Take(5))));
        // Only the empty-chamber predicate changes during our request. Native ammo
        // selection, animation, inventory transactions and completion callback stay intact.
        code[hits[0].i].opcode=OpCodes.Call;
        code[hits[0].i].operand=AccessTools.Method(typeof(Plugin),nameof(BarrelReloadChamberPredicate));
        return code;
    }
    private static int BarrelReloadChamberPredicate(Weapon weapon)=>ReferenceEquals(weapon,barrelReloadRequest)
        &&weapon.ReloadMode==Weapon.EReloadMode.OnlyBarrel&&weapon.ChamberAmmoCount<weapon.Chambers.Length
            ?0:weapon.ChamberAmmoCount;
    private bool ReloadScope(BotComponent b)=>config?.Enabled==true&&(config.ReloadTiming.Enabled||config.PistolSwap.Enabled)&&NativeAdapter.IsSupportedAi(b)
        &&b.BotActive&&!b.IsDead&&!b.GameEnding&&Authority&&!Ghost(b.ProfileId)&&b.SAINLayersActive;
    private static int ReloadLoaded(Weapon w)=>w.ChamberAmmoCount+(w.ReloadMode==Weapon.EReloadMode.OnlyBarrel?0:w.GetCurrentMagazine()?.Count??0);
    private static bool ChooseReloadTiming(SelfActionDecisionClass __instance,Enemy enemy,BotComponent bot,ref bool __result)
    {
        var p=Instance;
        if(p==null||bot==null||!p.ReloadScope(bot)||enemy==null)return true;
        BotState? s=null;bool invoked=false;
        try{
            if(!p.states.TryGetValue(bot,out s)){s=new BotState(bot,p.config!,p.session);p.states.Add(bot,s);}
            var r=s.ReloadTiming;var wm=bot.BotOwner.WeaponManager;
            if(r.Fault||wm==null||wm.IsMelee)return true;
            var reload=wm.Reload;var firearm=bot.Player.HandsController as Player.FirearmController;
            if(reload==null||firearm==null)return true;
            __result=false;
            // Native continuation (including its own cancellation rules) runs before this hook.
            if(s.HandsRecovery.Policy.BlocksSwitch||r.Policy.Active||s.QuickReload.Policy.Active||s.PistolSwap.Policy.Active||s.WeaponReturn.Policy.Active||reload.Reloading||firearm.IsInReloadOperation()||firearm.IsInInteraction()
                ||bot.BotOwner.Medecine?.Using==true||!wm.IsReady||wm.ShootController?.CanStartReload()!=true
                ||wm.Selector.IsChanging||Time.time-(float)reloadLastTime.GetValue(__instance)<1)return false;
            if(wm.Malfunctions.HaveMalfunction()&&wm.Malfunctions.MalfunctionType()!=Weapon.EMalfunctionState.Misfire)return true;
            // Let GetDecision continue into SAIN's healing decision when a medical emergency exists.
            if(FakeMedicalPriority(bot)||bot.Grenade.GrenadeDangerPoint!=null||bot.Flashed.IsFlashed)return false;
            var weapon=firearm.Item;var id=weapon.Id.ToString();int loaded=ReloadLoaded(weapon);
            string key=id+":"+loaded;
            if(Time.time<r.Next&&key==r.Key)return false;
            if(!p.doorBudget.TryBegin(Time.frameCount,bot))return false;
            p.doorClock.Restart();
            ReloadTimingFrame f;
            try{f=p.ReloadFrame(bot,enemy,s,weapon);}
            finally{p.doorClock.Stop();p.doorBudget.Charge(p.doorClock.Elapsed.TotalMilliseconds);r.Magazines.Clear();r.Ammo.Clear();}
            r.Next=Time.time+.25f;r.Key=key;r.LastFrame=f;
            string choice=r.Policy.Evaluate(f,p.config!.ReloadTiming);
            if(p.config.ReloadTiming.Enabled)p.RecordReloadTiming(bot,s);
            try{if(p.TryPistolSwap(bot,s,f,ref invoked))return false;}
            catch(Exception ex){p.PistolSwapError(s,ex);if(invoked)return false;}
            if(!p.config.ReloadTiming.Enabled)return true;
            if(choice=="Native"){
                // Preserve SAIN's empty-weapon / equipment fallback, but do not spam its inventory queries.
                if(r.Policy.RetryBlocked(f))return false;
                r.Policy.Refuse(f,p.config.ReloadTiming);r.Next=Time.time+(float)p.config.ReloadTiming.RetryDelay;
                return true;
            }
            if(choice!="Reload")return false;
            r.Policy.Request(f);r.Firearm=firearm;
            // CanReload is not a read-only query in EFT. Call it only for an actual attempt.
            bool available=reload.CanReload(true,out var mag,out var ammo);
            bool same=ReferenceEquals(bot.Player.HandsController,firearm)&&ReferenceEquals(wm.CurrentWeapon,weapon);
            bool improves=mag!=null?mag.Count>(weapon.GetCurrentMagazine()?.Count??0):ammo!=null&&ammo.Count>0;
            if(!available||!same||!improves||!firearm.CanStartReload()){
                r.Policy.Result(false,f,p.config.ReloadTiming);r.Drop();p.RecordReloadTiming(bot,s,true);return false;
            }
            // W04 selects normal/quick inside the native magazine route; no synthetic ammo, speed changes or cancellation.
            invoked=true;bot.BotOwner.ShootData.EndShoot();reload.Reloading=true;reloadLastTime.SetValue(__instance,Time.time);
            if(mag!=null)reload.ReloadMagazine(mag);
            else {
                var previous=barrelReloadRequest;barrelReloadRequest=weapon;
                try{reload.ReloadAmmo(ammo);}finally{barrelReloadRequest=previous;}
            }
            r.Policy.Result(true,f,p.config.ReloadTiming);__result=true;
            r.Policy.Observe(Time.time,firearm.IsInReloadOperation(),reload.Reloading,ReloadLoaded(weapon),true);
            p.RecordReloadTiming(bot,s,true);return false;
        }catch(Exception ex){if(s!=null)p.ReloadTimingError(s,ex);return !invoked;}
    }
    private ReloadTimingFrame ReloadFrame(BotComponent b,Enemy e,BotState s,Weapon w)
    {
        var r=s.ReloadTiming;var mag=w.GetCurrentMagazine();
        var place=e.KnownPlaces.LastSeenPlace;var heard=e.KnownPlaces.LastHeardPlace;
        // LastSeenPlace is personal; choose a newer personal heard place, never RealDistance when hidden.
        if(heard!=null&&(place==null||heard.TimeSincePositionUpdated<place.TimeSincePositionUpdated))place=heard;
        var known=place?.Position??b.Position;
        var f=new ReloadTimingFrame{Now=Time.time,Weapon=w.Id.ToString(),WeaponClass=w.WeapClass,Target=e.EnemyProfileId,
            Source=ReferenceEquals(place,heard)?"personal-hearing":"personal-vision",Distance=Vector3.Distance(b.Position,known),
            ContactAge=place?.TimeSincePositionUpdated??999,SightAge=e.Seen?e.TimeSinceSeen:999,Visible=e.IsVisible,
            Looking=e.IsVisible&&e.EnemyLookingAtMe,Incoming=b.BotOwner.Memory.IsUnderFire||b.Medical.TimeSinceShot<1.5,
            Chamber=w.ChamberAmmoCount,Magazine=w.ReloadMode==Weapon.EReloadMode.OnlyBarrel?0:mag?.Count??0,
            Capacity=w.ReloadMode==Weapon.EReloadMode.OnlyBarrel?w.Chambers.Length:(mag?.MaxCount??0)+w.Chambers.Length};
        f.Loaded=f.Chamber+f.Magazine;
        if(place==null||!e.EnemyKnown){f.Block="reload-no-personal-contact";return f;}
        if(config!.Aggression.TryGetValue(b.Info.Personality.ToString(),out var aggression))f.Aggression=aggression;
        var cover=b.Cover.CoverInUse;
        f.Protected=cover?.BotInThisCover==true&&b.Player.Velocity.magnitude<.35f&&CombatProtection(b.Position,known)==7;
        int count=0;
        foreach(var other in b.EnemyController.KnownEnemies){
            if(++count>16){f.ThreatsTruncated=true;f.Protected=false;break;}
            if(other==null||ReferenceEquals(other,e)||!other.EnemyKnown)continue;
            var p=other.KnownPlaces.LastSeenPlace;var h=other.KnownPlaces.LastHeardPlace;
            if(h!=null&&(p==null||h.TimeSincePositionUpdated<p.TimeSincePositionUpdated))p=h;
            if(other.IsVisible||other.Status.ShotAtMeRecently||p!=null&&p.TimeSincePositionUpdated<config.ReloadTiming.LostContactDelay
                &&Vector3.Distance(b.Position,p.Position)<config.ReloadTiming.CloseDistance){f.Threats++;f.Protected=false;}
        }
        // A personal cue is an estimate, not access to the opponent's hidden action status.
        var opportunity=s.Opportunity.Snapshot(e.EnemyProfileId,System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(e),Time.time,config.Opportunity);
        f.BusyObserved=e.IsVisible&&opportunity.State=="assess"&&opportunity.Cue is "reload" or "heal" or "surgery";
        return ReloadResources(b,s,w,f);
    }
    private ReloadTimingFrame ReloadResources(BotComponent b,BotState s,Weapon w,ReloadTimingFrame f,bool force=false)
    {
        var r=s.ReloadTiming;var mag=w.GetCurrentMagazine();
        if(f.Capacity<=0||!force&&f.Loaded>=f.Capacity*config!.ReloadTiming.ConsiderBelow/100){f.Resource="not-needed";return f;}
        // Read only reachable compatible inventory. These lists are cleared in the caller's finally block.
        Magazine? best=null;var slot=w.GetMagazineSlot();
        if(w.ReloadMode is Weapon.EReloadMode.ExternalMagazine or Weapon.EReloadMode.ExternalMagazineWithInternalReloadSupport){
            if(!ReloadInventory.ReadMagazines(b.Player.InventoryController,r.Magazines)){f.Block="reload-inventory-limit";return f;}
            f.ResourcesChecked=true;
            foreach(var m in r.Magazines){
                if(ReferenceEquals(m,mag))continue;
                f.MagazineCandidates++;
                if(ReloadInventory.Location(m,b.Player.Inventory.Equipment)=="ArmBand")f.BeltMagazines++;
                if(m.Count<=f.Magazine){f.NonImprovingMagazines++;continue;}
                if(slot==null){f.ResourceRejection="no-magazine-slot";continue;}
                var check=ItemManipulator.CheckMoveIgnoringTargetItem(m,slot,b.Player.InventoryController);
                if(!check.Succeeded){f.RejectedMagazines++;if(f.ResourceRejection.Length==0){var why=check.Error?.ToString()??"inventory-move-refused";f.ResourceRejection=why.Substring(0,Math.Min(160,why.Length));}continue;}
                if(best==null||m.Count>best.Count)best=m;
            }
        }
        if(best!=null){f.Resource="magazine";f.Reserve=best.Count;f.ResourceKey=best.Id+":"+best.Count;
            f.ResourceLocation=ReloadInventory.Location(best,b.Player.Inventory.Equipment);f.ResourceItem=best.ShortName.Localized();}
        else if(w is Revolver&&mag!=null){
            b.Player.InventoryController.GetReachableItemsOfTypeNonAlloc(r.Ammo,
                (Ammo a)=>a.StackObjectsCount>0&&b.Player.InventoryController.Examined(a)&&a.CheckAction(null).Succeeded&&mag.CheckCompatibility(a));
            if(r.Ammo.Count>128){f.Block="reload-inventory-limit";return f;}
            foreach(var a in r.Ammo)f.Reserve+=a.StackObjectsCount;
            if(f.Reserve>0){f.Resource="loose-ammo";f.ResourceKey=f.Resource+":"+f.Reserve;}
        }
        else if(w.ReloadMode!=Weapon.EReloadMode.ExternalMagazine&&w.Chambers.Length>0){
            b.Player.InventoryController.GetAcceptableItemsNonAlloc(BotReload._availableEquipmentSlots,r.Ammo,
                (Ammo a)=>a.StackObjectsCount>0&&w.Chambers[0].CanAccept(a)&&a.CheckAction(null).Succeeded);
            if(r.Ammo.Count>128){f.Block="reload-inventory-limit";return f;}
            foreach(var a in r.Ammo)f.Reserve+=a.StackObjectsCount;
            if(f.Reserve>0){f.Resource="loose-ammo";f.ResourceKey=f.Resource+":"+f.Reserve;}
        }
        return f;
    }
    private void SampleReloadTiming(BotComponent b,BotState s)
    {
        var r=s.ReloadTiming;if(!r.Policy.Active)return;
        if(!ReloadScope(b)){EndReloadTiming(s,"reload-out-of-scope");return;}
        if(Time.time<r.SampleAt)return;r.SampleAt=Time.time+.1f;
        var f=r.Firearm;bool same=f!=null&&ReferenceEquals(b.Player.HandsController,f);
        if(r.LastFrame!=null&&r.Policy.Trace!=null)r.Policy.Trace.ContactAge=r.LastFrame.ContactAge+Math.Max(0,Time.time-r.LastFrame.Now);
        r.Policy.Observe(Time.time,same&&f!.IsInReloadOperation(),b.BotOwner.WeaponManager?.Reload?.Reloading==true,
            same?ReloadLoaded(f!.Item):0,same);
        if(!r.Policy.Active){if(r.LastFrame!=null){r.LastFrame.Now=Time.time;r.Policy.CompleteObservation(r.LastFrame,config!.ReloadTiming);}r.Drop();}
        RecordReloadTiming(b,s);
    }
    private void EndReloadTiming(BotState s,string why)
    {var r=s.ReloadTiming;r.Policy.Clear(why);r.Drop();RecordReloadTiming(null,s,true);}
    private void ReloadTimingError(BotState s,Exception ex)
    {if(s.ReloadTiming.Fault)return;s.ReloadTiming.Fault=true;EndReloadTiming(s,"reload-adapter-error");Logger.LogWarning("W01 yielded to SAIN: "+ex.GetBaseException().Message);}
    private void RecordReloadTiming(BotComponent? b,BotState s,bool force=false)
    {
        var r=s.ReloadTiming;var t=r.Policy.Trace;if(t==null)return;
        if(b!=null)t.Action=b.Decision.CurrentSelfDecision==ESelfActionType.Reload?"Reload":b.Decision.CurrentCombatDecision.ToString();
        string key=t.State+":"+t.Choice+":"+t.Reason+":"+t.Loaded+":"+t.Reserve+":"+t.Started;
        if(!force&&key==r.LogKey&&Time.time-r.LogAt<.25f)return;r.LogKey=key;r.LogAt=Time.time;
        var json=Diagnostics.Serialize(new{schema=1,session,sequence=++sequence,feature="W01",stage="evaluated",decision="ReloadTiming",reason=t.Reason,
            realtime=Time.realtimeSinceStartup,utc=DateTime.UtcNow.ToString("O"),configRevision=config?.Revision??0,profileId=s.ProfileId,name=s.Name,botInstance=s.Id,
            reloadTiming=t,reloadTimingConfig=config?.ReloadTiming});
        diagnostics?.Write(json);Api.Decisions.Publish(json,ex=>{if(!subscriberWarned){subscriberWarned=true;Logger.LogWarning(ex.Message);}});
    }
}
