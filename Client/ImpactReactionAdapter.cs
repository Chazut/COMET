using System;
using System.Collections.Generic;
using Comet.Core;
using EFT;
using EFT.Ballistics;
using HarmonyLib;
using Newtonsoft.Json;
using SAIN.Components;
using SAIN.Helpers;
using SAIN.Layers;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes;
using SAIN.SAINComponent.Classes.Decision;
using SAIN.SAINComponent.Classes.Mover;
using UnityEngine;
using UnityEngine.AI;

namespace Comet.Client;

internal sealed class ImpactRuntime
{
    internal readonly ImpactReaction Memory;
    internal bool Owned, Requested, ActionSeen, Warned, InDecisionPoll;
    internal float FaultUntil, NextLog, NextProbe, LastHeartbeat, PreviousPose, AppliedPose, MoveStartDistance;
    internal bool OwnPose;
    internal int Coalesced;
    internal Vector3 Start, Destination;
    internal NavMeshPath? Path;
    internal IBotPathData? AppliedPath;
    internal string LogKey="";
    internal ImpactRuntime(ImpactReactionConfig config) { Memory=new ImpactReaction(config); }
}

public sealed partial class Plugin
{
    private static int impactProbeFrame=-1;
    private readonly Dictionary<string,int> impactCounters=new();
    private float nextImpactSummary;
    private static void PatchImpactReaction(Harmony h)
    {
        var hit=AccessTools.Method(typeof(SAINBotMedicalClass),nameof(SAINBotMedicalClass.GetHit),new[]{typeof(DamageInfo),typeof(EBodyPart),typeof(float)});
        var freeze=typeof(BotComponent).Assembly.GetType("SAIN.Layers.Combat.Solo.FreezeAction",true);
        if(hit==null||freeze==null)throw new MissingMethodException("P07 native impact/action contract");
        h.Patch(AccessTools.Method(typeof(BotDecisionManager),"getDecision"),prefix:new HarmonyMethod(typeof(Plugin),nameof(ImpactPollBegin)),finalizer:new HarmonyMethod(typeof(Plugin),nameof(ImpactPollEnd)));
        h.Patch(hit,postfix:new HarmonyMethod(typeof(Plugin),nameof(ImpactHit)));
        h.Patch(AccessTools.Method(typeof(BotDecisionManager),"SetDecisions"),prefix:new HarmonyMethod(typeof(Plugin),nameof(ImpactDecisionRequest)));
        h.Patch(AccessTools.Method(freeze,"Update"),prefix:new HarmonyMethod(typeof(Plugin),nameof(ImpactActionUpdate)));
        h.Patch(AccessTools.Method(typeof(BotAction),"OnSteeringTicked"),prefix:new HarmonyMethod(typeof(Plugin),nameof(ImpactActionSteer)));
    }
    private static void ImpactPollBegin(BotDecisionManager __instance)
    { if(Instance?.states.TryGetValue(__instance.Bot,out var s)==true)s.Impact.InDecisionPoll=true; }
    private static void ImpactPollEnd(BotDecisionManager __instance)
    { if(Instance?.states.TryGetValue(__instance.Bot,out var s)==true)s.Impact.InDecisionPoll=false; }
    private bool ImpactScoped(BotComponent b) => config?.Enabled==true&&config.ImpactReaction.Enabled&&world!=null
        &&NativeAdapter.IsSupportedAi(b)&&b.BotActive&&!b.IsDead&&Authority&&!Ghost(b.ProfileId);
    private string ImpactPriority(BotComponent b,ImpactRuntime r)
    {
        return ImpactPolicy.Priority(ImpactScoped(b),Time.time<r.FaultUntil,
            b.GoalEnemy!=null||b.EnemyController.VisibleEnemies.Count>0,
            b.Decision.CurrentSelfDecision!=ESelfActionType.None||b.Decision.CurrentSquadDecision!=ESquadDecision.None
            ||b.Decision.CurrentCombatDecision==ECombatDecision.AvoidGrenade||b.BotOwner.DoorOpener.Interacting
            ||b.Player.HandsController.IsInInteractionStrictCheck()||b.BotOwner.WeaponManager.Reload.Reloading,
            r.Owned,b.Decision.CurrentCombatDecision!=ECombatDecision.None);
    }

    private static void ImpactHit(SAINBotMedicalClass __instance,DamageInfo __0)
    {
        var p=Instance;var b=__instance.Bot;
        if(p==null||!NativeAdapter.IsSupportedAi(b))return;
        try
        {
            if(!p.ImpactScoped(b))return;
            if(__0.DamageType!=EDamageType.Bullet||__0.DelayedDamage) {p.CountImpact("impact-not-direct-bullet");return;}
            var shooter=__0.Player?.iPlayer;
            var group=b.BotOwner.BotsGroup;
            if(shooter!=null&&(ReferenceEquals(shooter,b.Player)||p.Ghost(shooter.ProfileId)||group==null
                ||group.IsAlly(shooter)||!group.IsPlayerEnemy(shooter))) {p.CountImpact("impact-friendly-or-out-of-scope-source");return;}
            if(!p.states.TryGetValue(b,out var s))
            {if(p.states.Count>=512){p.CountImpact("impact-state-cap");return;}s=new BotState(b,p.config!,p.session);p.states.Add(b,s);}
            if(Time.time<s.Impact.FaultUntil)return;
            var f=new ImpactFrame{Now=Time.time,DirectionX=__0.Direction.x,DirectionY=__0.Direction.y,DirectionZ=__0.Direction.z};
            // The internal damage owner is used ONLY to qualify an independently perceived contact.
            // Never read shooter.Position, MasterOrigin, EnemyPosition or an Injury place.
            var enemy=shooter==null?null:b.EnemyController.GetEnemy(shooter.ProfileId,true);
            if(enemy!=null&&enemy.EnemyKnown)
            {
                var seen=enemy.KnownPlaces.LastSeenPlace;var heard=enemy.KnownPlaces.LastHeardPlace;
                if(enemy.IsVisible&&seen!=null&&seen.TimeSincePositionUpdated>=0&&seen.TimeSincePositionUpdated<=.5f)
                {f.Contact=enemy.EnemyProfileId;f.Evidence="seen";}
                else if(heard!=null&&heard.SoundType.HasValue&&heard.SoundType.Value.IsGunShot()
                    &&heard.TimeSincePositionUpdated>=0&&heard.TimeSincePositionUpdated<=.75f)
                {f.Contact=enemy.EnemyProfileId;f.Evidence="heard-shot";}
            }
            double? previousSector=s.Impact.Memory.Current.SectorYaw;
            var t=s.Impact.Memory.Receive(f);p.CountImpact(t.Reason);p.RecordImpact(s,t);
            if(previousSector!=s.Impact.Memory.Current.SectorYaw&&s.Impact.Owned)
            {
                if(s.Impact.AppliedPath!=null&&ReferenceEquals(s.Impact.AppliedPath,b.Mover.ActivePath))b.Mover.Stop();
                s.Impact.Path=null;s.Impact.AppliedPath=null;s.Impact.NextProbe=0;
            }
            p.StopGunfire(s,"gunfire-bullet-impact");
            // A07 rechecks its existing contacts; no new enemy, timestamp or position is injected.
            if(f.Contact.Length>0)s.Threat.Next=0;
        }
        catch(Exception ex){p.ImpactError(b,ex);}
    }
    private static void ImpactDecisionRequest(BotDecisionManager __instance,ref ECombatDecision solo,ESquadDecision squad,ESelfActionType self,
        SAIN.SAINComponent.Classes.EnemyClasses.Enemy enemy)
    {
        var p=Instance;var b=__instance.Bot;
        if(p==null||!p.states.TryGetValue(b,out var s))return;
        var r=s.Impact;r.Requested=false;
        try
        {
            if(!r.Memory.Current.Active)return;
            // Reset/dispose uses SetDecisions too. Never turn a native reset back into a combat action.
            if(!r.InDecisionPoll){p.EndImpact(b,s,"impact-native-reset");return;}
            string priority=p.ImpactPriority(b,r);
            if(enemy!=null||solo!=ECombatDecision.None||squad!=ESquadDecision.None||self!=ESelfActionType.None||priority.Length>0)
            {
                var t=r.Memory.Current;t.State="native";t.Reason=priority.Length>0?priority:"impact-native-response";
                t.Observed=solo+"/"+self+"/"+squad;p.RecordImpact(s,r.Memory.Snapshot(Time.time),true);
                p.EndImpact(b,s,t.Reason);return;
            }
            if(!r.Memory.Active(Time.time)){p.EndImpact(b,s,"impact-memory-expired");return;}
            if(!r.Owned&&!r.Memory.Due(Time.time))return;
            if(r.Memory.Due(Time.time)&&r.Memory.Reviewed(Time.time))
            {
                if(r.AppliedPath!=null&&ReferenceEquals(r.AppliedPath,b.Mover.ActivePath))b.Mover.Stop();
                r.Path=null;r.AppliedPath=null;r.NextProbe=0;
            }
            r.Requested=true;solo=ECombatDecision.Freeze;
            if(!r.ActionSeen&&r.Memory.Current.Requested.Length==0)
            {
                var request=r.Memory.Current;request.State="requested";request.Reason="impact-defensive-request";request.Requested="DefendUnknown";
                p.RecordImpact(s,r.Memory.Snapshot(Time.time),true);
            }
        }
        catch(Exception ex){p.ImpactError(b,ex);}
    }
    // Called before the common A01-A13 decision observer. This lane owns only its anonymous Freeze request.
    private bool ImpactDecisionObserved(BotDecisionManager d,BotState s)
    {
        var r=s.Impact;
        if(!r.Requested)return false;
        r.Requested=false;
        if(d.CurrentCombatDecision!=ECombatDecision.Freeze||d.CurrentSelfDecision!=ESelfActionType.None||d.CurrentSquadDecision!=ESquadDecision.None)
        {EndImpact(d.Bot,s,"impact-native-refused");return false;}
        if(!r.Owned){Release(s,"impact-defensive-handoff");r.Owned=true;r.Start=d.Bot.Position;}
        if(!r.Memory.Current.NativeAccepted)
        {
            r.Memory.Current.NativeAccepted=true;r.Memory.Current.State="accepted";
            r.Memory.Current.Reason="impact-native-decision-accepted";
            RecordImpact(s,r.Memory.Snapshot(Time.time),true);
        }
        // Acceptance is not proof that BigBrain actually ran the action. Label only in its Update.
        return true;
    }
    private static bool ImpactActionUpdate(BotAction __instance)
    {
        var p=Instance;var b=__instance.Bot;
        if(p==null||!p.states.TryGetValue(b,out var s)||!s.Impact.Owned)return true;
        try
        {
            if(!p.ImpactCanAct(b,s))return true;
            var r=s.Impact;var t=r.Memory.Current;
            bool first=!r.ActionSeen;
            r.ActionSeen=true;t.ActionObserved=true;t.State="observed";t.Reason="impact-defensive-action-observed";
            Api.Decisions.Set(s.ProfileId,"COMET:DefendUnknown");
            if(r.Path==null&&Time.time>=r.NextProbe&&impactProbeFrame!=Time.frameCount)
            {
                impactProbeFrame=Time.frameCount;r.NextProbe=Time.time+(float)p.config!.ImpactReaction.MinimumInterval;
                p.FindImpactProtection(b,r);
            }
            if(r.Path!=null)
            {
                if((b.Position-r.Destination).sqrMagnitude<.5f)
                {t.MovementObserved|=r.AppliedPath!=null&&ImpactPolicy.Moved(r.MoveStartDistance,Vector3.Distance(b.Position,r.Destination),(b.Position-r.Start).magnitude);if(ReferenceEquals(r.AppliedPath,b.Mover.ActivePath))b.Mover.Stop();t.Observed="holding-protection";}
                else if(r.AppliedPath==null)
                {
                    if(b.Mover.WalkToPointByWay(r.Path,true,.5f)) {r.AppliedPath=b.Mover.ActivePath;r.Start=b.Position;r.MoveStartDistance=Vector3.Distance(b.Position,r.Destination);t.Observed="move-request-accepted";}
                    else {r.Path=null;t.CoverReason="native-move-refused";}
                }
                else if(ReferenceEquals(r.AppliedPath,b.Mover.ActivePath))
                {
                    t.MovementObserved|=ImpactPolicy.Moved(r.MoveStartDistance,Vector3.Distance(b.Position,r.Destination),(b.Position-r.Start).magnitude);
                    t.Observed=t.MovementObserved?"moving-to-protection":"move-request-accepted";
                }
                else {p.EndImpact(b,s,"impact-movement-taken-over");return true;}
            }
            if(r.Path==null)
            {
                // SAIN's PoseClass skips application without HasEnemy. Use its native player pose operations,
                // without supplying a fake enemy. Restore only our unchanged request when yielding.
                if(!b.Player.IsInPronePose)
                {
                    if(!r.OwnPose){r.PreviousPose=b.Player.PoseLevel;r.AppliedPose=Math.Min(.5f,r.PreviousPose);r.OwnPose=true;}
                    b.BotOwner.SetPose(r.AppliedPose);b.Player.ChangePose(r.AppliedPose-b.Player.PoseLevel);
                    t.Observed=Math.Abs(b.Player.PoseLevel-r.AppliedPose)<.05f?"defensive-posture-observed":"posture-requested";
                }
                else t.Observed="native-prone-preserved";
            }
            p.RecordImpact(s,r.Memory.Snapshot(Time.time),first);return false;
        }
        catch(Exception ex){p.ImpactError(b,ex);return true;}
    }
    private static bool ImpactActionSteer(BotAction __instance)
    {
        var p=Instance;var b=__instance.Bot;
        if(p==null||!p.states.TryGetValue(b,out var s)||!s.Impact.Owned||!s.Impact.ActionSeen
            ||((object)__instance).GetType().FullName!="SAIN.Layers.Combat.Solo.FreezeAction")return true;
        try
        {
            if(!p.ImpactCanAct(b,s))return true;
            var t=s.Impact.Memory.Current;
            // Never shoot/suppress from an impact. Real contact immediately returns to native combat.
            if(t.SectorYaw.HasValue)
            {b.Steering.LookToPoint(b.Position+Vector3.up*1.3f+ImpactDirection(t.SectorYaw.Value)*8);t.LookApplied=true;}
            p.RecordImpact(s,s.Impact.Memory.Snapshot(Time.time));return false;
        }
        catch(Exception ex){p.ImpactError(b,ex);return true;}
    }
    private bool ImpactCanAct(BotComponent b,BotState s)
    {
        var reason=ImpactPriority(b,s.Impact);
        if(reason.Length==0&&!s.Impact.Memory.Active(Time.time))reason="impact-memory-expired";
        if(reason.Length==0&&b.Decision.CurrentCombatDecision!=ECombatDecision.Freeze)reason="impact-native-action-changed";
        if(reason.Length==0)return true;
        EndImpact(b,s,reason);return false;
    }
    private static Vector3 ImpactDirection(double yaw) => new Vector3((float)Math.Sin(yaw*Math.PI/180),0,(float)Math.Cos(yaw*Math.PI/180));
    private void FindImpactProtection(BotComponent b,ImpactRuntime r)
    {
        var t=r.Memory.Current;var origin=b.Position;
        Vector3 incoming=t.SectorYaw.HasValue?ImpactDirection(t.SectorYaw.Value):Vector3.forward;
        Vector3 side=new Vector3(incoming.z,0,-incoming.x);
        // Four nearby alternatives, at most four complete paths and 20 local rays per global probe.
        var offsets=t.SectorYaw.HasValue?new[]{-incoming*2,-incoming*3,side*2-incoming, -side*2-incoming}
            :new[]{Vector3.forward*2,Vector3.back*2,Vector3.left*2,Vector3.right*2};
        int baseline=ImpactShield(origin,t.SectorYaw);t.CoverReason="no-better-local-protection";t.Probes=0;
        for(int i=0;i<offsets.Length;i++)
        {
            t.Probes++;
            if(!NavMesh.SamplePosition(origin+offsets[i],out var hit,.6f,NavMesh.AllAreas)||Math.Abs(hit.position.y-origin.y)>.75f)continue;
            int shield=ImpactShield(hit.position,t.SectorYaw);
            if(shield<=baseline||shield<(t.SectorYaw.HasValue?2:1))continue;
            var path=new NavMeshPath();
            if(!NavMesh.CalculatePath(origin,hit.position,NavMesh.AllAreas,path)||!VisibleAdapter.ValidPath(path,origin,hit.position))continue;
            var corners=path.corners;if(corners.Length>8)continue;
            float length=0,maxHeight=0,maxAdvance=0;
            for(int j=1;j<corners.Length;j++)
            {
                length+=Vector3.Distance(corners[j-1],corners[j]);
                maxHeight=Math.Max(maxHeight,Math.Abs(corners[j].y-origin.y));
                maxAdvance=Math.Max(maxAdvance,Vector3.Dot(corners[j]-origin,incoming));
            }
            if(!ImpactPolicy.Protection(baseline,shield,t.SectorYaw.HasValue,corners.Length,length,maxHeight,maxAdvance))continue;
            r.Path=path;r.Destination=hit.position;t.CoverReason="nearby-geometry-improves-protection";return;
        }
    }
    private static int ImpactShield(Vector3 pos,double? sector)
    {
        int blocked=0;int count=sector.HasValue?3:4;
        for(int i=0;i<count;i++)
        {
            double yaw=sector.HasValue?sector.Value+(i-1)*45:i*90;
            if(Physics.Raycast(pos+Vector3.up*.9f,ImpactDirection(yaw),2.5f,LayersMaskController.HighPolyWithTerrainMask,QueryTriggerInteraction.Ignore))blocked++;
        }
        return blocked;
    }
    private void EndImpact(BotComponent b,BotState s,string reason)
    {
        var r=s.Impact;bool active=r.Memory.Current.Active||r.Owned;
        if(r.Owned)
        {
            // Stop only our own path, before another action acquires the mover. Never cancel someone else's path.
            if(b!=null&&!b.IsDead&&r.AppliedPath!=null&&ReferenceEquals(r.AppliedPath,b.Mover.ActivePath))b.Mover.Stop();
            if(Api.Decisions.GetCurrentDecision(s.ProfileId)=="COMET:DefendUnknown")Api.Decisions.Set(s.ProfileId,"");
        }
        if(r.OwnPose&&b!=null&&!b.IsDead&&!b.Player.IsInPronePose&&Math.Abs(b.Player.PoseLevel-r.AppliedPose)<.05f)
        {b.BotOwner.SetPose(r.PreviousPose);b.Player.ChangePose(r.PreviousPose-b.Player.PoseLevel);}
        r.OwnPose=false;
        r.Owned=r.Requested=r.ActionSeen=false;r.Path=null;r.AppliedPath=null;r.NextProbe=0;
        var t=r.Memory.End(Time.time,reason);if(active){CountImpact(reason);RecordImpact(s,t,true);}
    }
    private void RefreshImpact(BotComponent b,BotState s)
    {
        var r=s.Impact;if(!r.Memory.Current.Active)return;
        try
        {
            if(!ImpactScoped(b)){EndImpact(b,s,"impact-out-of-scope");return;}
            if(!r.Memory.Active(Time.time)){EndImpact(b,s,"impact-memory-expired");return;}
            if(r.Owned&&!ImpactCanAct(b,s))return;
            if(Time.time-r.LastHeartbeat>=1){r.LastHeartbeat=Time.time;RecordImpact(s,r.Memory.Snapshot(Time.time));}
        }
        catch(Exception ex){ImpactError(b,ex);}
    }
    private void ImpactError(BotComponent b,Exception ex)
    {
        CountImpact("impact-adapter-error");
        if(b==null||!states.TryGetValue(b,out var s))return;
        try{EndImpact(b,s,"impact-adapter-error");}catch
        {
            s.Impact.Owned=s.Impact.Requested=s.Impact.ActionSeen=s.Impact.OwnPose=false;
            s.Impact.Path=null;s.Impact.AppliedPath=null;s.Impact.Memory.End(Time.time,"impact-adapter-error");
            if(Api.Decisions.GetCurrentDecision(s.ProfileId)=="COMET:DefendUnknown")Api.Decisions.Set(s.ProfileId,"");
        }
        s.Impact.FaultUntil=Time.time+10;
        if(!s.Impact.Warned){s.Impact.Warned=true;Logger.LogWarning("COMET P07 yielded to native SAIN: "+ex.Message);}
    }
    private void CountImpact(string reason){impactCounters.TryGetValue(reason,out int n);impactCounters[reason]=n+1;}
    private void RecordImpact(BotState s,ImpactTrace t,bool force=false)
    {
        var r=s.Impact;string key=t.State+":"+t.Reason+":"+t.Observed;
        if(!force&&Time.time<r.NextLog){r.Coalesced++;return;}
        if(!force&&key==r.LogKey&&Time.time-r.LastHeartbeat<.5f&&t.State=="requested")return;
        r.NextLog=Time.time+.5f;r.LogKey=key;
        var json=Diagnostics.Serialize(new{schema=1,session,sequence=++sequence,feature="P07",stage="evaluated",decision="ImpactReaction",
            reason=t.Reason,realtime=Time.realtimeSinceStartup,utc=DateTime.UtcNow.ToString("O"),cometVersion="0.1.0",configRevision=config?.Revision??0,
            impactConfig=config?.ImpactReaction,profileId=s.ProfileId,name=s.Name,botInstance=s.Id,impact=t,coalesced=r.Coalesced});
        r.Coalesced=0;diagnostics?.Write(json);Api.Decisions.Publish(json,ex=>{if(!subscriberWarned){subscriberWarned=true;Logger.LogWarning(ex.Message);}});
    }
    private void FlushImpactCounters(bool force=false)
    {
        if(!force&&Time.time<nextImpactSummary)return;nextImpactSummary=Time.time+10;
        if(impactCounters.Count==0)return;
        diagnostics?.Write(Diagnostics.Serialize(new{schema=1,session,feature="P07",stage="summary",realtime=Time.realtimeSinceStartup,counters=impactCounters}));impactCounters.Clear();
    }
}
