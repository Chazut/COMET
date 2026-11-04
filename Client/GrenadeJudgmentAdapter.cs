using System;
using System.Runtime.CompilerServices;
using Comet.Core;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using Newtonsoft.Json;
using SAIN.Components;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.EnemyClasses;
using SAIN.SAINComponent.Classes.WeaponFunction;
using UnityEngine;

namespace Comet.Client;

internal sealed class GrenadeJudgmentRuntime
{
    internal readonly GrenadeJudgmentPolicy Policy=new();
    internal double NextSample,LogAt=-1000,RoomAt=-1;
    internal int RoomEnemy,ArcFrame=-1,ArcEnemy;
    internal Vector3 RoomPoint,ArcPoint,ArcOrigin,ArcTarget;
    internal string LogKey="",ArcItem="";
    internal bool Fault,Arc;
}
public sealed partial class Plugin
{
    private int grenadeProbeFrame=-1;
    private static void PatchGrenadeJudgment(Harmony h)
    {
        h.Patch(AccessTools.Method(typeof(BotComponent),nameof(BotComponent.ManualUpdate)),postfix:new HarmonyMethod(typeof(Plugin),nameof(TrackGrenadeJudgment)));
        h.Patch(AccessTools.Method(typeof(GrenadeThrowDecider),"FindThrowTarget",new[]{typeof(Enemy)})??throw new MissingMethodException("G01 FindThrowTarget"),
            prefix:new HarmonyMethod(typeof(Plugin),nameof(GrenadeFindBefore)),postfix:new HarmonyMethod(typeof(Plugin),nameof(GrenadeFindAfter)));
        h.Patch(AccessTools.Method(typeof(GrenadeThrowDecider),"TryThrowGrenade",Type.EmptyTypes)??throw new MissingMethodException("G01 TryThrowGrenade"),
            prefix:new HarmonyMethod(typeof(Plugin),nameof(GrenadeThrowBefore)){priority=Priority.Last},
            postfix:new HarmonyMethod(typeof(Plugin),nameof(GrenadeThrowAfter)));
    }
    private bool GrenadeJudgmentScope(BotComponent b)=>config?.Enabled==true&&config.GrenadeJudgment.Enabled&&NativeAdapter.IsPmcAi(b)
        &&b.BotActive&&!b.GameEnding&&Authority&&!Ghost(b.ProfileId);
    private BotState GrenadeState(BotComponent b)
    {if(!states.TryGetValue(b,out var s))states.Add(b,s=new BotState(b,config!,session));return s;}
    private static void TrackGrenadeJudgment(BotComponent __instance)
    {
        var p=Instance;if(p==null)return;
        if(p.states.TryGetValue(__instance,out var running)&&running.GrenadeTrajectory.Pending)
        {
            if(!p.GrenadeTrajectoryScope(__instance)||__instance.GoalEnemy==null)p.StopGrenadeTrajectory(running,"trajectory-scope-ended");
            else try{NativeAdapter.ContinueGrenadeSearch(__instance,running,p.config!);}
            catch(Exception ex){running.GrenadeTrajectory.Fault=true;p.FailGrenadeTrajectory(running,"trajectory-adapter-error");p.GrenadeJudgmentError(running,ex);}
        }
        if(p.states.TryGetValue(__instance,out var choiceState)&&!p.GrenadeChoiceScope(__instance))p.StopGrenadeChoice(choiceState,"choice-scope-ended");
        if(p.states.TryGetValue(__instance,out var fs)&&!p.GrenadeFilterScope(__instance))p.EndGrenadeFilter(fs,"filter-scope-ended");
        if(!p.GrenadeJudgmentScope(__instance)&&!p.GrenadeChoiceScope(__instance))return;
        var s=p.GrenadeState(__instance);var r=s.GrenadeJudgment;
        if(r.Fault||Time.timeAsDouble<r.NextSample)return;r.NextSample=Time.timeAsDouble+.25;
        try
        {
            bool pending=r.Policy.Pending;
            r.Policy.Tick(Time.timeAsDouble,p.config!.GrenadeJudgment);
            if(pending)p.RecordGrenadeJudgment(s);
            var e=__instance.GoalEnemy;if(e==null)return;
            var f=p.GrenadeFrame(__instance,e,s);
            // Observe room classification only while the target is personally visible. Keep a short-lived location fact.
            if(f.Personal&&e.IsVisible&&e.EnemyPlayer!=null)
            {r.RoomEnemy=f.Instance;r.RoomAt=e.EnemyPlayer.Environment==EnvironmentType.Indoor?f.Now:-1;r.RoomPoint=new Vector3((float)f.X,(float)f.Y,(float)f.Z);}
            if(p.GrenadeJudgmentScope(__instance))r.Policy.Observe(f);
        }catch(Exception ex){p.GrenadeJudgmentError(s,ex);}
    }
    private GrenadeJudgmentFrame GrenadeFrame(BotComponent b,Enemy e,BotState s)
    {
        var places=e.KnownPlaces;var k=places.LastKnownPosition;var place=places.LastKnownPlace;var r=s.GrenadeJudgment;
        string source=place!=null&&ReferenceEquals(place,places.LastSeenPlace)?"seen":place!=null&&ReferenceEquals(place,places.LastHeardPlace)?"heard":"shared-or-unknown";
        var f=new GrenadeJudgmentFrame{Now=Time.timeAsDouble,Target=e.EnemyProfileId,Instance=RuntimeHelpers.GetHashCode(e),
            Personal=k.HasValue&&(source=="seen"||source=="heard"),Source=source,EvidenceAt=places.TimeLastKnownUpdated,
            X=k?.x??0,Y=k?.y??0,Z=k?.z??0,Item=b.BotOwner.WeaponManager.Grenades.grenade?.Id.ToString()??""};
        f.Room=k.HasValue&&r.RoomAt>=0&&f.Now-r.RoomAt<=config!.GrenadeContactLifetime&&r.RoomEnemy==f.Instance&&(k.Value-r.RoomPoint).sqrMagnitude<=2.25f;
        var opportunity=s.Opportunity.Snapshot(f.Target,f.Instance,f.Now,config!.Opportunity);
        f.Vulnerable=opportunity.State=="assess"&&(opportunity.Cue=="reload"||opportunity.Cue=="heal"||opportunity.Cue=="surgery");
        var health=b.Memory.Health.HealthStatus;
        if(r.Fault)f.Block="grenade-adapter-error";
        else if(b.BotOwner.Memory.IsUnderFire||b.Suppression.IsHeavySuppressed||b.Grenade.GrenadeDangerPoint!=null||b.Flashed.IsFlashed)f.Block="grenade-immediate-danger";
        else if(health==ETagStatus.BadlyInjured||health==ETagStatus.Dying||b.BotOwner.Medecine.Using)f.Block="grenade-treatment-priority";
        else if(s.Fake.Policy.Active||s.Fake.CallbackPending||FakeGuarded(b))f.Block="grenade-feint-owned";
        else foreach(var other in b.EnemyController.VisibleEnemies)if(!ReferenceEquals(other,e)){f.Block="grenade-other-visible-threat";break;}
        return f;
    }
    private static bool GrenadeFindBefore(GrenadeThrowDecider __instance,Enemy __0,ref bool __result,out BotState? __state)
    {
        __state=null;var p=Instance;var b=__instance.Bot;if(p==null||(!p.GrenadeJudgmentScope(b)&&!p.GrenadeTrajectoryScope(b)&&!p.GrenadeChoiceScope(b)&&!p.GrenadeFilterScope(b)))return true;
        var s=p.GrenadeState(b);var r=s.GrenadeJudgment;
        try
        {
            if(p.GrenadeChoiceInProgress(b)||!p.PrepareOffensiveGrenade(b,s)){__result=false;return false;}
            if(!p.GrenadeJudgmentScope(b)&&!p.GrenadeTrajectoryScope(b)&&!p.GrenadeChoiceScope(b)){__state=s;return true;}
            var f=p.GrenadeFrame(b,__0,s);
            if(p.GrenadeJudgmentScope(b)&&!r.Policy.Prepare(f,p.config!.GrenadeJudgment,p.config.GrenadeContactLifetime)) {p.RecordGrenadeJudgment(s);__result=false;return false;}
            if(p.GrenadeTrajectoryScope(b))
            {
                var ready=p.PrepareGrenadeTrajectory(b,__0,s,f);
                __result=ready==true;if(ready.HasValue)__state=s;return false;
            }
            var g=b.BotOwner.WeaponManager.Grenades;
            if(r.Arc&&r.ArcFrame==Time.frameCount&&r.ArcEnemy==f.Instance&&r.ArcItem==f.Item
                &&(r.ArcPoint-new Vector3((float)f.X,(float)f.Y,(float)f.Z)).sqrMagnitude<.01f&&(r.ArcOrigin-b.Transform.WeaponData.WeaponRoot).sqrMagnitude<.01f
                &&g.AIGreanageThrowData?.IsUpToDate()==true){__state=s;__result=true;return false;}
            if(p.grenadeProbeFrame==Time.frameCount){__result=false;return false;}
            p.grenadeProbeFrame=Time.frameCount;__state=s;return true;
        }catch(Exception ex){p.GrenadeJudgmentError(s,ex);__result=false;return false;}
    }
    private static void GrenadeFindAfter(GrenadeThrowDecider __instance,Enemy __0,ref bool __result,BotState? __state)
    {
        var p=Instance;if(p==null||__state==null)return;
        try
        {
            var b=__instance.Bot;var r=__state.GrenadeJudgment;
            r.Arc=__result;r.ArcFrame=Time.frameCount;r.ArcEnemy=RuntimeHelpers.GetHashCode(__0);
            r.ArcItem=b.BotOwner.WeaponManager.Grenades.grenade?.Id.ToString()??"";r.ArcPoint=__0.KnownPlaces.LastKnownPosition??Vector3.zero;r.ArcOrigin=b.Transform.WeaponData.WeaponRoot;
            r.ArcTarget=b.BotOwner.WeaponManager.Grenades.AIGreanageThrowData?.Target??Vector3.zero;
            __result=p.ChooseGrenade(b,__0,__state,__result);
            __result=p.BindOffensiveGrenade(b,__state,__result);
            __result=p.AssessGrenade(b,__0,__state,__result);
        }catch(Exception ex){p.GrenadeJudgmentError(__state,ex);__result=false;}
    }
    internal bool AssessGrenade(BotComponent b,Enemy e,BotState s,bool arc)
    {
        if(!GrenadeJudgmentScope(b))return arc;
        var f=GrenadeFrame(b,e,s);
        if(arc&&f.Block.Length==0)f.Block=GrenadeSafety(b,s,f);
        bool ok=s.GrenadeJudgment.Policy.Evaluate(f,config!.GrenadeJudgment,config.GrenadeContactLifetime,arc,jumpRandom.NextDouble);
        RecordGrenadeJudgment(s);return ok;
    }
    private string GrenadeSafety(BotComponent b,BotState s,GrenadeJudgmentFrame f)
    {
        var g=b.BotOwner.WeaponManager.Grenades;var d=g.AIGreanageThrowData;var item=g.grenade;
        string filtered=FilterReject(b,item);if(filtered.Length>0)return filtered;
        if(item==null||item.IsDummy||item.ThrowType.ToString().IndexOf("smoke",StringComparison.OrdinalIgnoreCase)>=0)return "grenade-no-suitable-item";
        if(d==null||!d.CanThrow||!d.IsUpToDate()||!DP(d.Target).Finite||!DP(d.Position).Finite||Vector3.Distance(d.Position,b.Transform.WeaponData.WeaponRoot)>.5f)return "grenade-invalid-current-arc";
        var r=s.GrenadeJudgment;
        if(!r.Arc||r.ArcEnemy!=f.Instance||r.ArcItem!=f.Item||(r.ArcPoint-new Vector3((float)f.X,(float)f.Y,(float)f.Z)).sqrMagnitude>1
            ||(r.ArcTarget-d.Target).sqrMagnitude>.01f)return "grenade-arc-contact-changed";
        double radius=ClearDangerRadius(item);
        if(!DoorRoomClear.Valid(radius)||radius>100||!DoorRoomClear.Valid(item.GetExplDelay)||item.GetExplDelay<.75||item.GetExplDelay>30)return "grenade-unknown-properties";
        if(!ClearBlastProtected(b.Position,d.Target,radius))return "grenade-self-exposed";
        if(world==null||world.RegisteredPlayers==null)return "grenade-participants-unavailable";
        int count=0;foreach(var player in world.RegisteredPlayers)
        {
            if(++count>128)return "grenade-participant-limit";
            if(player==null||ReferenceEquals(player,b.Player)||!player.HealthController.IsAlive||b.BotOwner.BotsGroup?.IsEnemy(player)==true)continue;
            if(!ClearBlastProtected(player.Position,d.Target,radius+1))return "grenade-nonhostile-exposed";
        }
        // Match native CheckGrenade's equipment scope, excluding items G01 cannot safely use.
        f.Count=0;count=0;
        foreach(var carried in b.Player.Inventory.GetPlayerItems(EPlayerItems.Equipment))
        {
            if(++count>256)return "grenade-inventory-limit";
            if(carried is not ThrowWeap candidate||candidate.IsDummy||candidate.ThrowType.ToString().IndexOf("smoke",StringComparison.OrdinalIgnoreCase)>=0)continue;
            if(FilterReject(b,candidate).Length>0)continue;
            if(DoorRoomClear.Valid(candidate.GetExplDelay)&&candidate.GetExplDelay>=.75&&candidate.GetExplDelay<=30)f.Count++;
        }
        return f.Count>0?"":"grenade-no-usable-inventory";
    }
    private static bool GrenadeThrowBefore(GrenadeThrowDecider __instance,ref bool __result,out BotState? __state)
    {
        __state=null;var p=Instance;var b=__instance.Bot;if(p==null||(!p.GrenadeJudgmentScope(b)&&!p.GrenadeTrajectoryScope(b)&&!p.GrenadeChoiceScope(b)&&!p.GrenadeFilterScope(b)))return true;
        var s=p.GrenadeState(b);
        try
        {
            if(b.GoalEnemy==null||!p.ValidateGrenadeTrajectory(b,b.GoalEnemy,s)||!p.ValidateGrenadeChoice(b,b.GoalEnemy,s,true)||!p.AssessGrenade(b,b.GoalEnemy,s,true)||!p.ValidateOffensiveGrenade(b,s)){__result=false;return false;}
            if(!p.GrenadeJudgmentScope(b))return true;
            p.EnsureClearEvents();if(p.clearEvents==null){__result=false;return false;}
            var f=p.GrenadeFrame(b,b.GoalEnemy,s);s.GrenadeJudgment.Policy.Request(f);p.RecordGrenadeJudgment(s,true);__state=s;return true;
        }catch(Exception ex){p.GrenadeJudgmentError(s,ex);__result=false;return false;}
    }
    private static void GrenadeThrowAfter(bool __result,BotState? __state)
    {
        var p=Instance;if(p==null||__state==null)return;
        __state.GrenadeJudgment.Policy.Result(__result,Time.timeAsDouble,p.config!.GrenadeJudgment);p.RecordGrenadeJudgment(__state,true);
    }
    private void JudgmentProjectile(Grenade grenade)
    {foreach(var pair in states)if(pair.Key.ProfileId==grenade.ProfileId&&pair.Value.GrenadeJudgment.Policy.Released(grenade.WeaponSource?.Id.ToString()??"",Time.timeAsDouble))RecordGrenadeJudgment(pair.Value,true);}
    private void EndGrenadeJudgment(BotState s,string reason)
    {EndGrenadeFilter(s,reason);StopGrenadeChoice(s,reason);StopGrenadeTrajectory(s,reason);s.GrenadeJudgment.Policy.Clear(reason);s.GrenadeJudgment.RoomAt=-1;s.GrenadeJudgment.Arc=false;RecordGrenadeJudgment(s);}
    private void GrenadeJudgmentError(BotState s,Exception ex)
    {bool warn=!s.GrenadeJudgment.Fault;s.GrenadeJudgment.Fault=true;EndGrenadeJudgment(s,"grenade-adapter-error");if(warn)Logger.LogWarning("G01 blocked grenade requests: "+ex.GetBaseException().Message);}
    private void RecordGrenadeJudgment(BotState s,bool force=false)
    {
        var r=s.GrenadeJudgment;var t=r.Policy.Trace;if(t==null)return;
        double now=Time.timeAsDouble;string key=t.Target+"|"+t.State+"|"+t.Reason+"|"+t.Roll;
        if(!force&&key==r.LogKey&&(t.ValidFor==0||now-r.LogAt<.75))return;r.LogKey=key;r.LogAt=now;
        var json=JsonConvert.SerializeObject(new{schema=1,session,sequence=++sequence,feature="G01",stage="evaluated",decision="GrenadeJudgment",reason=t.Reason,
            realtime=Time.realtimeSinceStartup,utc=DateTime.UtcNow.ToString("O"),configRevision=config?.Revision??0,profileId=s.ProfileId,name=s.Name,botInstance=s.Id,
            grenadeJudgment=t,grenadeJudgmentConfig=config?.GrenadeJudgment},Diagnostics.Json);
        diagnostics?.Write(json);Api.Decisions.Publish(json,ex=>{if(!subscriberWarned){subscriberWarned=true;Logger.LogWarning(ex.Message);}});
    }
}
