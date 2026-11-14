using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using Comfort.Common;
using Comet.Core;
using DrakiaXYZ.BigBrain.Brains;
using EFT;
using EFT.Interactive;
using EFT.InventoryLogic;
using HarmonyLib;
using Newtonsoft.Json;
using SAIN;
using SAIN.Components;
using SAIN.Preset.Shared.Enums;
using UnityEngine;

namespace Comet.Client;

internal sealed class QuickReloadRuntime
{
    internal readonly QuickReloadPolicy Policy=new();
    internal readonly List<Magazine> Magazines=new();
    internal Player.FirearmController? Firearm;
    internal float SampleAt,LogAt=-1000;
    internal int Token;
    internal string LogKey="";
    internal bool Fault;
    internal void Drop(){Firearm=null;Magazines.Clear();Token++;}
}
public sealed partial class Plugin
{
    private static void PatchQuickReload(Harmony h)
    {
        h.Patch(AccessTools.Method(typeof(BotReload),nameof(BotReload.ReloadMagazine)),
            transpiler:new HarmonyMethod(typeof(Plugin),nameof(RouteQuickReload)));
        h.Patch(AccessTools.Method(typeof(GameWorld),nameof(GameWorld.ThrowItem),new[]{typeof(Item),typeof(IPlayer),typeof(Vector3),typeof(Quaternion),typeof(Vector3),typeof(Vector3),typeof(bool),typeof(bool),typeof(float)}),
            postfix:new HarmonyMethod(typeof(Plugin),nameof(ObserveDroppedReloadMagazine)));
        BrainManager.AddCustomLayer(typeof(MagazineRecoveryLayer),new(){"PMC","PmcUsec","PmcBear","Assault","ExUsec","Knight","BigPipe","BirdEye"},20);
    }
    // Redirect only the call inside BotReload. Preserve its original callback, timers, speech and bookkeeping.
    private static IEnumerable<CodeInstruction> RouteQuickReload(IEnumerable<CodeInstruction> instructions)
    {
        // BotReload.ShootController is an interface. Its callvirt operand is NOT the
        // concrete Player.FirearmController method, even when that is the runtime instance.
        var list=instructions.ToList();var original=AccessTools.Method(typeof(IFirearmHandsController),nameof(IFirearmHandsController.ReloadMag),new[]{typeof(Magazine),typeof(ItemAddress),typeof(Callback)});
        int matches=list.Count(i=>i.Calls(original));
        if(matches!=1)throw new InvalidOperationException("W04 expected one IFirearmHandsController.ReloadMag call in BotReload.ReloadMagazine; found "+matches);
        foreach(var i in list)
        {
            if(!i.Calls(original)){yield return i;continue;}
            var owner=new CodeInstruction(i){opcode=OpCodes.Ldarg_0,operand=null};
            yield return owner;
            yield return new CodeInstruction(OpCodes.Call,AccessTools.Method(typeof(Plugin),nameof(DispatchMagazineReload)));
        }
    }
    private bool QuickReloadScope(BotComponent b)=>config?.Enabled==true&&config.QuickReload.Enabled&&NativeAdapter.IsSupportedAi(b)
        &&b.BotActive&&!b.IsDead&&!b.GameEnding&&Authority&&!Ghost(b.ProfileId)&&b.SAINLayersActive;
    private static void DispatchMagazineReload(IFirearmHandsController controller,Magazine magazine,ItemAddress address,Callback callback,BotReload reload)
    {
        var firearm=controller as Player.FirearmController;
        var p=Instance;BotState? s=null;BotComponent? b=null;bool quick=false;
        try
        {
            if(firearm!=null&&p!=null&&reload?._owner!=null&&SAINEnableClass.GetSAIN(reload._owner.ProfileId,out b)&&b!=null&&p.QuickReloadScope(b))
            {
                if(!p.states.TryGetValue(b,out s)){s=new BotState(b,p.config!,p.session);p.states.Add(b,s);}
                var r=s.QuickReload;
                if(!r.Fault)
                {
                    p.doorClock.Restart();
                    try{quick=r.Policy.Evaluate(p.QuickReloadFrameFor(b,s,firearm,magazine),p.config!.QuickReload);}
                    finally{p.doorClock.Stop();p.doorBudget.Charge(p.doorClock.Elapsed.TotalMilliseconds);r.Magazines.Clear();s.ReloadTiming.Magazines.Clear();s.ReloadTiming.Ammo.Clear();}
                    p.RecordQuickReload(b,s);
                }
            }
        }
        catch(Exception ex){if(s!=null)p?.QuickReloadError(s,ex);quick=false;}
        // Preserve interface dispatch for other implementations and all ineligible bots.
        if(!quick||firearm==null||p==null||s==null||b==null){controller.ReloadMag(magazine,address,callback);return;}
        var runtime=s.QuickReload;runtime.Firearm=firearm;runtime.Policy.Request(Time.time);
        int token=++runtime.Token;var weak=new WeakReference<QuickReloadRuntime>(runtime);bool delivered=false;
        Callback completion=result=>
        {
            if(delivered)return;delivered=true;
            if(weak.TryGetTarget(out var live)&&live.Token==token)live.Policy.Callback(result.Succeed);
            callback?.Invoke(result); // Keep the original native result and cleanup exactly once.
        };
        p.RecordQuickReload(b,s,true);
        try{firearm.QuickReloadMag(magazine,completion);}
        catch(Exception ex)
        {
            // Never retry a normal reload after a possibly started native operation.
            try{if(!delivered)completion.Fail("COMET native quick reload threw");}
            finally{p.QuickReloadError(s,ex);}
        }
    }
    private QuickReloadFrame QuickReloadFrameFor(BotComponent b,BotState s,Player.FirearmController firearm,Magazine replacement)
    {
        var w=firearm.Item;var old=w.GetCurrentMagazine();var e=b.GoalEnemy;bool underFire=b.BotOwner.Memory.IsUnderFire;
        var f=new QuickReloadFrame{Now=Time.time,Weapon=w.Id.ToString(),OldMagazine=old?.Id.ToString()??"",Replacement=replacement.Id.ToString(),
            Loaded=ReloadLoaded(w),Capacity=(old?.MaxCount??0)+w.Chambers.Length,OldRounds=old?.Count??0,ReplacementRounds=replacement.Count,
            Target=e?.EnemyProfileId??"",Distance=e?.RealDistance??0,ContactAge=e?.TimeSinceSeen??999,
            InFight=underFire||e!=null&&e.Seen&&e.TimeSinceSeen<10,
            Urgent=underFire||e!=null&&(e.IsVisible||e.Seen&&e.TimeSinceSeen<config!.QuickReload.ContactMemory&&e.RealDistance<config.QuickReload.ThreatDistance),
            Incoming=underFire,UtilityReload=config!.ReloadTiming.Enabled};
        var slot=w.GetMagazineSlot();var equipment=b.Player.Inventory?.Equipment;
        if(slot!=null&&equipment!=null)foreach(var item in equipment.GetAllItems())
            if(item is Magazine m&&!ReferenceEquals(m,old)&&slot.CanAccept(m))f.SpareMagazines++;
        return f;
    }
    private static void ObserveDroppedReloadMagazine(Item item,IPlayer player,LootItem __result)
    {
        var p=Instance;if(p==null||__result==null||item is not Magazine mag||player==null)return;
        BotState? s=null;
        try
        {
            if(!SAINEnableClass.GetSAIN(player.ProfileId,out var b)||b==null||!p.states.TryGetValue(b,out s)||!s.QuickReload.Policy.Active)return;
            if(!ReferenceEquals(__result.Item,item)||s.QuickReload.Policy.Trace?.OldMagazine!=item.Id.ToString())return;
            s.QuickReload.Policy.Drop(item.Id.ToString(),mag.Count);p.RecordQuickReload(b,s,true);
            if(p.config?.QuickReload.RecoverDroppedMagazine==true)
                p.RememberDroppedMagazine(s,__result);
        }
        catch(Exception ex){if(s!=null)p.QuickReloadError(s,ex);}
    }
    private void SampleQuickReload(BotComponent b,BotState s)
    {
        var r=s.QuickReload;if(r.Firearm==null)return;
        if(!QuickReloadScope(b)){EndQuickReload(s,"quick-out-of-scope");return;}
        if(Time.time<r.SampleAt)return;r.SampleAt=Time.time+.1f;
        var f=r.Firearm;bool same=ReferenceEquals(b.Player.HandsController,f);
        r.Policy.Observe(Time.time,same,same&&f.IsInReloadOperation(),b.BotOwner.WeaponManager?.Reload?.Reloading==true,
            same?f.Item.GetCurrentMagazine()?.Id.ToString()??"":"",same?ReloadLoaded(f.Item):0);
        RecordQuickReload(b,s);if(!r.Policy.Active)r.Drop();
    }
    private void EndQuickReload(BotState s,string why)
    {var r=s.QuickReload;if(!r.Policy.Active&&r.Firearm==null)return;r.Policy.End(why);r.Drop();RecordQuickReload(null,s,true);}
    private void QuickReloadError(BotState s,Exception ex)
    {if(s.QuickReload.Fault)return;s.QuickReload.Fault=true;EndQuickReload(s,"quick-adapter-error");Logger.LogWarning("W04 yielded to native reload: "+ex.GetBaseException().Message);}
    private void RecordQuickReload(BotComponent? b,BotState s,bool force=false)
    {
        var r=s.QuickReload;var t=r.Policy.Trace;if(t==null)return;
        if(b!=null)t.Action=b.Decision.CurrentSelfDecision==ESelfActionType.Reload?"Reload":b.Decision.CurrentCombatDecision.ToString();
        string key=t.State+":"+t.Choice+":"+t.Reason+":"+t.Dropped+":"+t.CallbackSeen;
        if(!force&&key==r.LogKey&&Time.time-r.LogAt<.25f)return;r.LogKey=key;r.LogAt=Time.time;
        var json=Diagnostics.Serialize(new{schema=1,session,sequence=++sequence,feature="W04",stage="evaluated",decision="QuickReload",reason=t.Reason,
            realtime=Time.realtimeSinceStartup,utc=DateTime.UtcNow.ToString("O"),configRevision=config?.Revision??0,profileId=s.ProfileId,name=s.Name,botInstance=s.Id,
            quickReload=t,quickReloadConfig=config?.QuickReload});
        diagnostics?.Write(json);Api.Decisions.Publish(json,ex=>{if(!subscriberWarned){subscriberWarned=true;Logger.LogWarning(ex.Message);}});
    }
}
