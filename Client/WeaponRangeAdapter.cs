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
    private WeaponReturnConfig RangeArbiterConfig()=>config!.WeaponReturn;
    private static WeaponRangeGun ReadRangeGun(Weapon w,WeaponReturnGun ammo)
    {
        var g=new WeaponRangeGun{Id=ammo.Id,Slot=ammo.Slot,Name=w.ShortName.Localized(),Class=w.WeapClass,
            Loaded=ammo.Loaded,Reserve=ammo.Reserve,Usable=true};WeaponRangeModel.Classify(g);return g;
    }
    private WeaponRangeFrame WeaponRangeFrameFor(BotComponent b,BotState s,WeaponReturnFrame f)
    {
        var frame=new WeaponRangeFrame{Now=Time.time,Current=f.Current.Id,Main=f.Main.Id,Ready=f.Ready,Reloading=f.Reloading,Goal=b.GoalEnemy?.EnemyProfileId??""};
        var guns=new List<WeaponRangeGun>();
        foreach(var ammo in new[]{f.Main,f.Other}){
            if(Enum.TryParse(ammo.Slot,out EquipmentSlot slot)&&SlotGun(b,slot) is Weapon w)guns.Add(ReadRangeGun(w,ammo));
        }
        frame.Weapons=guns.ToArray();var contacts=new List<WeaponRangeContact>();
        foreach(var e in b.EnemyController.KnownEnemies){if(e==null)continue;
            contacts.Add(new(){Id=e.EnemyProfileId,Age=e.IsVisible?0:e.Seen?e.TimeSinceSeen:999,Distance=e.RealDistance});}
        var goal=b.GoalEnemy;if(goal!=null&&!contacts.Any(c=>c.Id==goal.EnemyProfileId))
            contacts.Add(new(){Id=goal.EnemyProfileId,Age=goal.IsVisible?0:goal.Seen?goal.TimeSinceSeen:999,Distance=goal.RealDistance});
        frame.Contacts=contacts.ToArray();return frame;
    }
    private bool KeepRangeChoice(BotComponent b,BotState s,WeaponReturnFrame f,WeaponReturnGun target,string origin)=>
        origin=="native-return"&&config!.WeaponRange.Enabled&&s.WeaponRange.Policy.Holds(f.Current.Id)&&f.Current.Loaded+f.Current.Reserve>0;
    private void SampleWeaponRange(BotComponent b,BotState s)
    {
        var r=s.WeaponRange;if(r.Fault)return;
        if(!WeaponRangeScope(b)){EndWeaponRange(s,"range-out-of-scope");return;}
        if(!WeaponHandsReady(b,b.Player.HandsController as Player.FirearmController)||b.BotOwner.WeaponManager.Reload.Reloading)return;
        var f=WeaponReturnFrameFor(b,s);if(f==null)return;
        string next=r.Policy.Evaluate(WeaponRangeFrameFor(b,s,f),config!.WeaponRange);
        if(next.Length>0){NativeWeaponRequest(b,s,ReturnTarget(f,next),"W05");r.Policy.Arbitration(s.WeaponReturn.Policy.Trace,Time.time);}
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
