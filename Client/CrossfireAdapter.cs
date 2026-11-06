using System;
using System.Collections.Generic;
using Comet.Core;
using DrakiaXYZ.BigBrain.Brains;
using EFT;
using HarmonyLib;
using Newtonsoft.Json;
using SAIN;
using SAIN.Components;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.Decision;
using SAIN.SAINComponent.Classes.EnemyClasses;
using SAIN.SAINComponent.Classes.Mover;
using UnityEngine;
using UnityEngine.AI;
namespace Comet.Client;

internal sealed class CrossfireRuntime
{
    internal readonly Crossfire Policy=new();
    internal readonly CoverMateRuntime Care=new();
    internal readonly SquadTradeRuntime Trade=new();
    internal readonly SquadStormRuntime Storm=new();
    internal int ManeuverAttempts;internal bool ManeuverFire;internal SquadManeuverPlan? Maneuver;internal ManeuverMember? ManeuverRole;internal float ManeuverStarted,ManeuverNext;internal Vector3 Watch;
    internal readonly EntranceRuntime Entrance=new();
    internal float Next,LogAt,ContactAt,LastSafety,StartWait;
    internal int ProbeFrame=-1,Candidate,Sample,FinalizedEpisode,ContactInstance;
    internal bool Layer,Fault,Indoor;
    internal string Key="",Support="",Contact="",Group="";
    internal Vector3 Known,Origin,Reference,Goal;
    internal readonly List<Vector3> Candidates=new(),Samples=new();
    internal readonly List<string> Rejects=new();
    internal NavMeshPath? Route;
    internal IBotPathData? Path;
    internal float PathStarted,Angle,Length;
    internal IBotPathData? HandoffPath;
    internal float HandoffStarted, NextEligibility, GateLogAt;
    internal string GateKey="";
}
public sealed partial class Plugin
{
    private readonly CrossfireClaims crossfireClaims=new();
    private static void PatchCrossfire(Harmony h)
    {
        h.Patch(AccessTools.Method(typeof(BotDecisionManager),nameof(BotDecisionManager.ManualUpdate)),postfix:new HarmonyMethod(typeof(Plugin),nameof(ObserveCrossfire)));
        BrainManager.AddCustomLayer(typeof(CrossfireLayer),new(){"PMC","PmcUsec","PmcBear","Assault","ExUsec","Knight","BigPipe","BirdEye"},21);
    }
    private bool CrossfireScope(BotComponent b)=>ContactSafety.AllowsGoal(b)&&config?.Enabled==true&&(config.Crossfire.Enabled||config.CoverMate.Enabled||config.SquadTrade.Enabled||config.SquadStorm.Enabled)&&world!=null&&Authority
        &&b!=null&&NativeAdapter.IsPmcAi(b)&&b.BotActive&&!b.IsDead&&!b.GameEnding&&!Ghost(b.ProfileId);
    private string CrossfireBlock(BotComponent b,BotState s,bool maneuverCandidate=false)
    {
        if(!CrossfireScope(b)||!config!.Crossfire.Enabled)return "crossfire-out-of-scope";
        var e=b.GoalEnemy;
        if(e==null||!e.EnemyKnown||e.KnownPlaces.LastKnownPosition is not Vector3 known||!ValidPoint(known))return "crossfire-no-known-contact";
        if(e.IsVisible)return "crossfire-enemy-visible";
        bool maneuver= maneuverCandidate||s.Crossfire.Maneuver?.Active==true;
        double contactAge=maneuver?config!.SquadManeuver.ContactAge:s.Crossfire.Policy.Active&&s.Crossfire.Policy.Trace?.Entrance!=null?config!.SecondaryEntrance.ContactAge:config!.Crossfire.ContactAge;
        if(!float.IsFinite(e.TimeSinceLastKnownUpdated)||e.TimeSinceLastKnownUpdated<0||e.TimeSinceLastKnownUpdated>contactAge)return "crossfire-contact-stale";
        if(Vector3.Distance(b.Position,known)>(maneuver?config.SquadManeuver.MaxDistance:config.Crossfire.EnemyDistance))return "crossfire-enemy-too-far";
        if(s.Crossfire.Policy.Active&&Time.time-s.Crossfire.ContactAt>contactAge)return "crossfire-snapshot-stale";
        if(s.Crossfire.Policy.Active&&(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(e)!=s.Crossfire.ContactInstance||e.EnemyProfileId!=s.Crossfire.Contact||Vector3.Distance(known,s.Crossfire.Known)>4))return "crossfire-contact-changed";
        return CrossfireCommonBlock(b,s);
    }
    private string CrossfireCommonBlock(BotComponent b,BotState s,bool trade=false)
    {
        if(b.BotOwner.Memory.IsUnderFire||b.Suppression.IsSuppressed||b.Grenade.GrenadeDangerPoint!=null||b.Flashed.IsFlashed)return "crossfire-urgent-threat";
        if(b.Decision.CurrentSelfDecision!=ESelfActionType.None
            ||b.Decision.CurrentSquadDecision!=ESquadDecision.None&&!TacticalPriority.OrdinarySquad(b.Decision.CurrentSquadDecision.ToString()))return "crossfire-native-priority";
        if(b.BotOwner.DoorOpener.Interacting&&!EntranceOwnOpening(b,s)||b.BotOwner.Medecine.Using||MedicalPostureGuard(b)
            ||b.BotOwner.WeaponManager.Reload.Reloading||b.BotOwner.WeaponManager.Grenades.ThrowindNow
            ||!WeaponHandsReady(b,b.Player.HandsController as Player.FirearmController)&&!EntranceOwnOpening(b,s)||!b.BotOwner.WeaponManager.HaveBullets)return "crossfire-hands-or-supplies";
        if(!b.Player.MovementContext.IsGrounded
            ||b.Player.IsSprintEnabled&&!OwnCrossfirePath(b,s.Crossfire)&&!CrossfireCanHandoff(b)
                &&!PendingCrossfireHandoff(b,s.Crossfire))return "crossfire-native-movement";
        if(b.Memory.Health.HealthStatus is ETagStatus.BadlyInjured or ETagStatus.Dying)return "crossfire-injured";
        if(s.PostCombat.Policy.Active||s.Relocation.Policy.Active||s.CoverFeint.Policy.Active||s.Feature.StartsWith("D")&&s.Decision.Length>0)return "crossfire-other-tactic";
        if(!CrossfireTacticalDecision(b))return "crossfire-native-combat-priority";
        if((!trade&&!b.Squad.BotInGroup)||b.Squad.Members==null||b.Squad.Members.Count<(trade?1:2)||b.Squad.Members.Count>16)return "crossfire-no-bounded-squad";
        int count=0;foreach(var other in b.EnemyController.KnownEnemies)
        {if(++count>16)return "crossfire-threat-list-over-budget";if(other?.IsVisible==true)return "crossfire-visible-threat";}
        return "";
    }
    private BotComponent? CrossfireSupport(BotComponent b,string id="")
    {
        if(b.Squad.Members==null)return null;
        BotComponent? best=null;float distance=float.MaxValue;
        foreach(var mate in b.Squad.Members.Values)
        {
            if(mate==null||ReferenceEquals(b,mate)||id.Length>0&&mate.ProfileId!=id||!CrossfireScope(mate)
                ||mate.GoalEnemy?.EnemyProfileId!=b.GoalEnemy?.EnemyProfileId||mate.GoalEnemy?.IsVisible!=true
                ||mate.BotOwner.Medecine.Using||mate.BotOwner.WeaponManager.Reload.Reloading||!mate.BotOwner.WeaponManager.HaveBullets)continue;
            float d=Vector3.Distance(b.Position,mate.Position);if(d<=config!.Crossfire.SupportDistance&&d<distance){best=mate;distance=d;}
        }
        return best;
    }
    private static void ObserveCrossfire(BotDecisionManager __instance)
    {
        var p=Instance;var b=__instance.Bot;if(p==null||!p.CrossfireScope(b))return;
        try
        {
            if(!p.states.TryGetValue(b,out var s)){if(p.states.Count>=512)return;s=new BotState(b,p.config!,p.session);p.states.Add(b,s);}
            if(!s.Crossfire.Policy.Active)
            {
                if(Time.time<s.Crossfire.NextEligibility)return;
                s.Crossfire.NextEligibility=Time.time+(float)p.config!.Crossfire.Interval;
            }
            p.ObserveStormEquipment(b,s);
            p.RefreshCrossfire(b,s,true);
        }catch(Exception ex){if(p.states.TryGetValue(b,out var s))p.CrossfireError(b,s,ex);}
    }
    private void SampleCrossfire(BotComponent b,BotState s)
    {if(!s.Crossfire.Policy.Active)return;try{RefreshCrossfire(b,s,false);}catch(Exception ex){CrossfireError(b,s,ex);}}
    private bool RefreshCrossfire(BotComponent b,BotState s,bool mayStart)
    {
        var r=s.Crossfire;if(r.Fault)return false;
        if(!r.Policy.Active&&mayStart)
        {
            // All variants share these safety gates. Do not rebuild several squad
            // candidates on every tick when a native emergency already excludes them.
            string common=CrossfireCommonBlock(b,s,true);
            if(common.Length>0){RecordCrossfireGate(s,common);return false;}
        }
        if(r.Policy.Active&&r.Policy.Trace?.Storm!=null)return RefreshStorm(b,s);
        if(r.Policy.Active&&r.Policy.Trace?.Trade!=null)return RefreshSquadTrade(b,s);
        if(!r.Policy.Active&&mayStart&&config!.SquadStorm.Enabled&&b.Squad.Members!=null&&stormBoard.Get(ManeuverGroup(b))?.Active==true)
        {return TryBeginStorm(b,s)&&RefreshStorm(b,s);}
        if(!r.Policy.Active&&mayStart&&TryBeginSquadTrade(b,s))return RefreshSquadTrade(b,s);
        if(r.Policy.Active&&r.Policy.Trace?.CoverMate!=null)return RefreshCoverMate(b,s);
        if(!r.Policy.Active&&mayStart&&TryBeginCoverMate(b,s))return RefreshCoverMate(b,s);
        if(!r.Policy.Active&&mayStart&&TryBeginStorm(b,s))return RefreshStorm(b,s);
        var observed=b.GoalEnemy;
        if(r.Policy.Active&&observed?.EnemyKnown==true&&observed.EnemyProfileId==r.Contact
            &&observed.KnownPlaces.LastKnownPosition is Vector3 remembered&&Vector3.Distance(remembered,r.Known)<.5f
            &&float.IsFinite(observed.TimeSinceLastKnownUpdated)&&observed.TimeSinceLastKnownUpdated>=0)
            r.ContactAt=Math.Max(r.ContactAt,Time.time-observed.TimeSinceLastKnownUpdated);
        string block=CrossfireBlock(b,s,!r.Policy.Active&&mayStart&&config!.SquadManeuver.Enabled);
        if(block.Length>0){if(r.Policy.Active)EndCrossfire(b,s,block);else if(mayStart)RecordCrossfireGate(s,block);return false;}
        var c=config!.Crossfire;
        if(!r.Policy.Active&&mayStart&&config.SquadManeuver.Enabled)TryBeginManeuver(b,s);
        // A declined S03 plan must not relax the separate S01 eligibility limits.
        if(!r.Policy.Active&&CrossfireBlock(b,s).Length>0)return false;
        if(r.Maneuver!=null&&r.Policy.Active&&!ValidateManeuver(b,s))return false;
        var support=r.Maneuver!=null&&r.Policy.Active&&b.Squad.Members!.TryGetValue(r.Support,out var plannedSupport)?plannedSupport:CrossfireSupport(b,r.Policy.Active?r.Support:"");
        if(r.Policy.Active)
        {
            if(!b.Squad.Members!.TryGetValue(r.Support,out var original)||original==null||original.IsDead||!original.BotActive||Ghost(original.ProfileId))
            {EndCrossfire(b,s,"crossfire-support-inactive");return false;}
            if(r.Policy.Trace!.Entrance!=null&&!ValidateEntranceActive(b,s,false))return false;
            if(!r.Policy.Check(Time.time,c,"",support!=null||r.Policy.Trace.Entrance!=null,r.Policy.Trace.Entrance!=null?config.SecondaryEntrance.WatchMax+config.SecondaryEntrance.Extension+8:null)){EndCrossfire(b,s,r.Policy.Trace!.Reason);return false;}
            if(r.Policy.Trace!.Reserved&&!crossfireClaims.Renew(s.Id,Time.time)){EndCrossfire(b,s,"crossfire-reservation-expired");return false;}
            r.Policy.Trace.ContactAge=Math.Max(0,Time.time-r.ContactAt);
            if(support!=null&&Vector3.Distance(support.Position,r.Reference)>4){EndCrossfire(b,s,"crossfire-support-moved");return false;}
        }
        else
        {
            if(!mayStart||Time.time<r.Next||Time.time<r.Policy.RetryAt||support==null)return false;
            r.Next=Time.time+(float)c.Interval;
            r.Maneuver=null;r.ManeuverRole=null;
            bool selected=r.Policy.Begin(Time.time,c,jumpRandom.NextDouble());
            var e=b.GoalEnemy!;var situation=NativeAdapter.Capture(b,e,true,false);
            r.ContactInstance=System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(e);r.Known=e.KnownPlaces.LastKnownPosition!.Value;r.Contact=e.EnemyProfileId;r.ContactAt=Time.time-e.TimeSinceLastKnownUpdated;
            r.Support=support.ProfileId;r.Reference=support.Position;r.Origin=b.Position;r.Candidate=r.Sample=0;r.Route=null;r.Rejects.Clear();
            RememberCrossfireHandoff(b,r);
            // Detached squad identity: reservations must not expose other groups' hidden positions.
            var memberIds=new List<string>(b.Squad.Members!.Keys);memberIds.Sort(StringComparer.Ordinal);r.Group=string.Join("|",memberIds);
            r.Indoor=b.Player.Environment==EnvironmentType.Indoor;
            var t=r.Policy.Trace!;t.Contact=r.Contact;t.Source=situation.ContactSource;t.Support=r.Support;t.SupportName=support.Player.Profile.Nickname;
            t.X=r.Known.x;t.Y=r.Known.y;t.Z=r.Known.z;t.ContactAge=e.TimeSinceLastKnownUpdated;
            if(!selected){RecordCrossfire(s,true);return false;}
            if(!BeginEntrance(b,s))BuildCrossfireCandidates(b,r);RecordCrossfire(s,true);
        }
        if(r.Policy.Trace!.State=="planning")
        {
            if(Vector3.Distance(b.Position,r.Origin)>2){EndCrossfire(b,s,"crossfire-origin-moved");return false;}
            if(r.ProbeFrame!=Time.frameCount&&doorBudget.TryBegin(Time.frameCount))
            {r.ProbeFrame=Time.frameCount;doorClock.Restart();try{if(r.Policy.Trace.Entrance!=null)ProbeEntrance(b,s);else ProbeCrossfire(b,s);}finally{doorClock.Stop();doorBudget.Charge(doorClock.Elapsed.TotalMilliseconds);}}
        }
        r.Policy.Applied(r.Layer&&(r.ManeuverRole==null||r.ManeuverRole.Confirmed));RecordCrossfire(s,false);
        return r.Policy.Active&&r.Policy.Trace.State!="planning";
    }
    private bool ContinueCrossfireDecision(BotComponent b,BotState s,ref ECombatDecision result,ref bool succeeded)
    {
        if(!s.Crossfire.Policy.Active||s.Crossfire.Policy.Trace?.State=="planning")return false;
        if(!RefreshCrossfire(b,s,false))return false;
        Release(s,"crossfire-layer-request",false,true);result=ECombatDecision.Freeze;succeeded=true;return true;
    }
    internal static bool CrossfireActive(BotOwner owner)
    {
        var p=Instance;if(p==null||!SAINEnableClass.GetSAIN(owner.ProfileId,out var b)||b==null||!p.states.TryGetValue(b,out var s))return false;
        try{return p.RefreshCrossfire(b,s,false);}catch(Exception ex){p.CrossfireError(b,s,ex);return false;}
    }
    internal static void RunCrossfire(BotOwner owner,bool active)
    {
        var p=Instance;if(p==null||!SAINEnableClass.GetSAIN(owner.ProfileId,out var b)||b==null||!p.states.TryGetValue(b,out var s))return;
        try
        {
            var r=s.Crossfire;if(!active){if(r.Layer)p.EndCrossfire(b,s,"crossfire-layer-released");return;}
            if(!p.RefreshCrossfire(b,s,false))return;
            if(r.ManeuverRole?.Role=="flank"&&!r.Maneuver!.SupportReady(Time.time))return;
            if(!r.Layer)
            {
                if(!p.TakeCrossfireHandoff(b,s)){p.EndCrossfire(b,s,"crossfire-handoff-route-changed");return;}
                r.Layer=true;r.StartWait=Time.time;owner.PatrollingData.Pause();p.Release(s,"crossfire-layer-active",false,true);
            }
            if(r.Policy.Trace!.Storm!=null){p.ExecuteStorm(b,s);if(r.Policy.Active)p.RefreshStorm(b,s);return;}
            if(r.Policy.Trace!.Trade!=null){p.ExecuteSquadTrade(b,s);if(r.Policy.Active)p.RefreshSquadTrade(b,s);return;}
            if(r.Policy.Trace!.CoverMate!=null){p.ExecuteCoverMate(b,s);if(r.Policy.Active)p.RefreshCoverMate(b,s);return;}
            if(p.ExecuteManeuverSupport(b,s)){r.Policy.Applied(r.Layer&&(r.ManeuverRole==null||r.ManeuverRole.Confirmed));p.RecordCrossfire(s,false);return;}
            if(r.Policy.Trace!.Entrance!=null){p.ExecuteEntrance(b,s);if(!r.Policy.Active)return;}
            if(!r.Entrance.Opening)p.ExecuteCrossfire(b,s);p.ConfirmManeuver(b,s);r.Policy.Applied(r.Layer&&(r.ManeuverRole==null||r.ManeuverRole.Confirmed));p.RecordCrossfire(s,false);
        }catch(Exception ex){p.CrossfireError(b,s,ex);}
    }
    internal static bool SteerCrossfire(BotOwner owner)
    {
        var p=Instance;if(p==null||!SAINEnableClass.GetSAIN(owner.ProfileId,out var b)||b==null||!p.states.TryGetValue(b,out var s)||!s.Crossfire.Layer)return false;
        try
        {
            if((s.Crossfire.Policy.Trace?.Storm!=null?p.StormBlock(b,s):s.Crossfire.Policy.Trace?.Trade!=null?p.SquadTradeBlock(b,s):s.Crossfire.Policy.Trace?.CoverMate!=null?p.CoverMateBlock(b,s):p.CrossfireBlock(b,s)).Length>0)return false;
            if(s.Crossfire.Policy.Trace?.State=="moving")b.Steering.LookToMovingDirection();else b.Steering.LookToPoint((s.Crossfire.Policy.Trace?.Entrance!=null?s.Crossfire.Entrance.Center:s.Crossfire.ManeuverRole?.Role=="rear"?s.Crossfire.Watch:s.Crossfire.Known)+Vector3.up*1.3f);
            p.ConfirmManeuver(b,s,true);p.ConfirmCoverMate(b,s);p.ConfirmSquadTrade(b,s);
            return true;
        }catch(Exception ex){p.CrossfireError(b,s,ex);return false;}
    }
    private void EndCrossfire(BotComponent b,BotState s,string reason)
    {
        var r=s.Crossfire;
        if(r.Policy.Trace==null||r.Policy.Trace.Episode==r.FinalizedEpisode&&!r.Layer&&r.Path==null)return;
        EndManeuver(s,reason);EndCoverMate(s,reason);EndSquadTrade(s,reason);EndStorm(s,reason);
        if(r.Policy.Trace.Entrance!=null){r.Entrance.Policy.End(Time.time,r.ContactAt);ReleaseEntrance(s);}
        r.Policy.End(Time.time,config?.Crossfire??new(),reason);r.Layer=false;crossfireClaims.Release(s.Id);
        try{StopCrossfireRoute(b,r);}catch(Exception ex){Logger.LogWarning("S01 route release: "+ex.GetBaseException().Message);}
        finally{r.Route=null;r.HandoffPath=null;r.Samples.Clear();r.Candidates.Clear();RecordCrossfire(s,true);r.Entrance.Drop();r.Maneuver=null;r.ManeuverRole=null;r.Storm.Plan=null;r.Storm.Member=null;}
    }
    private void CrossfireError(BotComponent b,BotState s,Exception ex)
    {if(s.Crossfire.Fault)return;s.Crossfire.Fault=true;EndCrossfire(b,s,"crossfire-adapter-error");Logger.LogWarning("S01 yielded: "+ex.GetBaseException().Message);}
    private void RecordCrossfire(BotState s,bool force)
    {
        var r=s.Crossfire;var t=r.Policy.Trace;if(t==null)return;
        if(t.State=="ended")r.FinalizedEpisode=t.Episode;
        if(r.Maneuver!=null&&r.ManeuverRole!=null)t.Maneuver=r.Maneuver.Trace(s.ProfileId,Time.time,config!.SquadManeuver);
        if(r.Storm.Plan!=null&&r.Storm.Member!=null)t.Storm=r.Storm.Plan.Trace(s.ProfileId,Time.time,config!.SquadStorm);
        string label=t.Storm!=null?"StormMoving":t.Trade!=null?"Trade"+(t.State=="moving"?(t.Trade.Choice=="push"?"Pushing":"Moving"):"Watching"):t.CoverMate!=null?"CoverMate"+(t.State=="moving"?"Moving":"Holding"):t.Maneuver!=null?"Maneuver"+(r.ManeuverRole?.Role=="rear"?"Rear":r.ManeuverRole?.Role=="support"?(Time.time-r.ManeuverRole.LastShot<.3?"Suppressing":"Supporting"):t.State=="moving"?"Moving":"Holding"):t.Entrance==null?char.ToUpperInvariant(t.State[0])+t.State.Substring(1):r.Entrance.Entering?"EntranceEntering":t.State=="moving"?"EntranceMoving":"EntranceWatching";
        Api.Decisions.SetCrossfire(s.ProfileId,t.Applied?"COMET:Crossfire"+label:"");
        string key=t.Episode+":"+t.State+":"+t.Reason+":"+t.Execution+":"+t.Applied+":"+t.Entrance?.State+":"+t.Entrance?.SupportStatus+":"+t.Entrance?.EntryReason+":"+t.Maneuver?.State+":"+t.Maneuver?.SupportReady+":"+t.Maneuver?.FireState+":"+t.CoverMate?.Status+":"+t.CoverMate?.Busy+":"+t.CoverMate?.WeaponReady+":"+t.CoverMate?.Confirmed+":"+t.Trade?.Choice+":"+t.Trade?.Status+":"+t.Trade?.Confirmed+":"+t.Storm?.State+":"+t.Storm?.Status+":"+t.Storm?.Ready+":"+t.Storm?.Reason;
        if(!force&&key==r.Key&&(!t.Applied||Time.time<r.LogAt))return;r.Key=key;r.LogAt=Time.time+.5f;
        var json=Diagnostics.Serialize(new{schema=1,session,sequence=++sequence,feature=t.Storm!=null?"S06":t.Trade!=null?"S05":t.CoverMate!=null?"S04":t.Maneuver!=null?"S03":t.Entrance==null?"S01":"S02",stage="evaluated",decision="Crossfire",reason=t.Reason,
            realtime=Time.realtimeSinceStartup,utc=DateTime.UtcNow.ToString("O"),configRevision=config?.Revision??0,profileId=s.ProfileId,name=s.Name,botInstance=s.Id,
            crossfire=t,squadStormConfig=t.Storm!=null?config?.SquadStorm:null,squadTradeConfig=t.Trade!=null?config?.SquadTrade:null,coverMateConfig=t.CoverMate!=null?config?.CoverMate:null,squadManeuverConfig=t.Maneuver!=null?config?.SquadManeuver:null,crossfireConfig=config?.Crossfire,secondaryEntranceConfig=t.Entrance!=null?config?.SecondaryEntrance:null});
        diagnostics?.Write(json);Api.Decisions.Publish(json,ex=>{if(!subscriberWarned){subscriberWarned=true;Logger.LogWarning(ex.Message);}});
    }
}
