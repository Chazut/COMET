using System;
using System.Collections.Generic;
using System.Linq;
using Comet.Core;
using Newtonsoft.Json;
using SAIN.Components;
using SAIN.Models.Enums;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.Mover;
using UnityEngine;
using UnityEngine.AI;

namespace Comet.Client;

internal sealed class DoorSupportOperation
{
    internal string Id="",Target="";
    internal DoorWork Door=new();
    internal BotComponent Leader=null!;
    internal double Until;
    internal int TargetInstance,Cursor;
    internal bool Ended,Suspended;
    internal readonly List<string> Refusals=new();
}
internal sealed class DoorSupportRuntime
{
    internal DoorSupportOperation Operation=null!;
    internal DoorSupportTrace Trace=null!;
    internal BotComponent Owner=null!;
    internal IBotPathData? Path;
    internal int TargetInstance;
    internal float LastCheck,LastCallback,NextCheck,LogAt=-1000;
    internal string LogKey="";
}

public sealed partial class Plugin
{
    private readonly DoorSupportBoard supportBoard=new();
    private readonly Dictionary<BotComponent,DoorSupportOperation> supportOperations=new();
    private static DoorWork? SupportingDoor(BotState s)
        =>s.Entry.Policy.Active?s.Entry.Door:s.RoomClear.Policy.Active?s.RoomClear.Door:s.Fake.Policy.Active?s.Fake.Door:s.StepPeek.Policy.Active?s.StepPeek.Door
            :s.RunBy.Policy.Active?s.RunBy.Door:s.Jump.Policy.Active?s.Jump.Door:s.Ambush.Policy.Active?s.Ambush.Chosen
            :s.Door.Policy.Active?s.Door.Chosen:null;
    private double SupportBudget(BotComponent b,BotState s)
    {
        var age=b.GoalEnemy?.TimeSinceLastKnownUpdated??double.PositiveInfinity;
        double available=Math.Min(config!.DoorSupport.Duration,Math.Min(config.ContactLifetime,config.DoorApproach.ContactLifetime)-age);
        if(s.Entry.Policy.Active)return Math.Min(available,s.Entry.Policy.Deadline-Time.time);
        if(s.Ambush.Policy.Active)available=Math.Min(available,s.Ambush.Policy.Trace!.Remaining);
        if(config.Progress.Enabled&&!s.ProgressUnavailable)
            available=Math.Min(available,s.Decision=="Hold"?config.Progress.HoldLimit-s.Progress.Current.HoldSeconds:config.PlanLimit-Math.Max(0,Time.time-s.LastSelection));
        return available;
    }
    private string SupportGate(BotComponent b,BotState s,DoorSupportOperation o,bool owner=false)
    {
        if(config?.Enabled!=true||!config.DoorSupport.Enabled||!config.DoorApproach.Enabled||!FakeScope(b)||b.IsDead||b.GameEnding)return "support-out-of-scope";
        var leader=o.Leader;
        if(leader==null||leader.IsDead||!FakeScope(leader)||!states.TryGetValue(leader,out var ls)||o.Ended
            ||SupportingDoor(ls)?.Geometry.Id!=o.Door.Geometry.Id)return "support-leader-ended";
        if(!leader.Squad.Members.Values.Any(m=>ReferenceEquals(m,b)))return "support-squad-changed";
        var e=b.GoalEnemy;var le=leader.GoalEnemy;
        if(e==null||le==null||e.EnemyProfileId!=o.Target||le.EnemyProfileId!=o.Target||!e.EnemyKnown||!e.KnownPlaces.LastKnownPosition.HasValue
            ||e.TimeSinceLastKnownUpdated>Math.Min(config.ContactLifetime,config.DoorApproach.ContactLifetime)
            ||System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(le)!=o.TargetInstance)return "support-contact-not-shared";
        if(s.Support!=null&&System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(e)!=s.Support.TargetInstance)return "support-target-instance-changed";
        if(e.IsVisible||b.EnemyController.VisibleEnemies.Count>0)return "support-visible-threat";
        if(Vector3.Distance(b.Position,VP(o.Door.Geometry.Center))>config.DoorSupport.MaxDistance)return "support-too-far";
        if(b.Flashed.IsFlashed||b.BotOwner.Memory.IsUnderFire||b.Suppression.IsSuppressed||s.Fear.Current.Panic||b.Medical.TimeSinceShot<3
            ||b.Grenade.GrenadeDangerPoint!=null||b.Decision.CurrentSelfDecision!=ESelfActionType.None||b.Decision.CurrentSquadDecision!=ESquadDecision.None
            ||b.BotOwner.DoorOpener.Interacting||b.DoorOpener.Interacting||!FakeWeaponReady(b)||b.Player.IsInPronePose||!b.Player.MovementContext.IsGrounded)
            return "support-native-priority";
        if(SupportingDoor(s)!=null||s.Door.Policy.Planning||s.Interruption.Policy.Pending||s.Impact.Owned)return "support-busy";
        if(owner&&(!b.SAINLayersActive||b.CurrentAction?.GetType().Name!="FreezeAction"||b.Decision.CurrentCombatDecision!=ECombatDecision.Freeze
            ||s.Feature!="D11"||s.ActiveLabel!="COMET:Hold"))return "support-native-ownership-lost";
        return "";
    }
    private void UpdateDoorSupports()
    {
        foreach(var pair in supportOperations.ToArray())
        {
            var o=pair.Value;
            if(o.Suspended&&pair.Key!=null&&states.TryGetValue(pair.Key,out var paused)&&paused.Interruption.Policy.Pending&&FakeScope(pair.Key)&&!pair.Key.IsDead)
            {if(Time.time>=o.Until)EndSupportOperation(o,"support-budget-ended-during-interruption");continue;}
            if(pair.Key==null||pair.Key.IsDead||!states.TryGetValue(pair.Key,out var s)||SupportingDoor(s)?.Geometry.Id!=o.Door.Geometry.Id
                ||!FakeScope(pair.Key)||pair.Key.GoalEnemy?.EnemyProfileId!=o.Target)
            {EndSupportOperation(o,"support-leader-ended");supportOperations.Remove(pair.Key!);continue;}
            o.Until=Math.Min(o.Until,Time.time+SupportBudget(pair.Key,s));
            if(Time.time>=o.Until||config?.DoorSupport.Enabled!=true){EndSupportOperation(o,"support-budget-ended");continue;}
        }
        foreach(var pair in states.ToArray())
        {
            if(pair.Value.Support!=null)RefreshDoorSupport(pair.Key,pair.Value);
            var b=pair.Key;var s=pair.Value;var w=SupportingDoor(s);
            if(config?.Enabled!=true||!config.DoorSupport.Enabled||w==null||!FakeScope(b)||b.IsDead||b.GoalEnemy==null||s.Support!=null)continue;
            if(!supportOperations.TryGetValue(b,out var o))
            {
                double available=SupportBudget(b,s);if(available<=0)continue;
                o=new(){Id=Guid.NewGuid().ToString("N"),Leader=b,Door=w,Target=b.GoalEnemy.EnemyProfileId,
                    TargetInstance=System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(b.GoalEnemy),Until=Time.time+available};
                if(!supportBoard.Begin(o.Id,b.ProfileId,w.Geometry.Id,o.Target,Time.time,available,config.DoorSupport))continue;
                supportOperations.Add(b,o);
            }
            if(config!.DoorReservation.Enabled&&(s.Reservation.Lease==null||!doorReservations.Owns(s.Reservation.Lease,ReservationSquad(b,s),s.Id,Time.time)))
            {EndSupportOperation(o,"support-door-reservation-ended");continue;}
            if(!o.Ended&&!o.Suspended)PlanDoorSupport(o);
        }
    }
    private void PlanDoorSupport(DoorSupportOperation o)
    {
        // One candidate per operation/tick; all casts and path calculations share D01's work budget.
        var members=o.Leader.Squad.Members.Values.Where(b=>b!=null&&!ReferenceEquals(b,o.Leader)&&NativeAdapter.IsSupportedAi(b)&&b.BotActive&&!b.IsDead)
            .OrderBy(b=>Vector3.Distance(b.Position,VP(o.Door.Geometry.Center))).Take(8).ToArray();
        if(members.Length==0)return;
        int index=o.Cursor++;int variant=index%5;int member=(index/5)%members.Length;
        string role=(index/(5*members.Length))%config!.DoorSupport.MaxSupports==0?"overwatch":"rear-guard";
        var b=members[member];if(!supportBoard.CanOffer(o.Id,role,b.ProfileId,Time.time))return;
        if(!states.TryGetValue(b,out var s)){s=new BotState(b,config,session);states.Add(b,s);}
        string gate=SupportGate(b,s,o);if(gate.Length>0){SupportRefusal(o,s,role,gate);return;}
        if(s.Support!=null)return;
        if(!doorBudget.TryBegin(Time.frameCount))return;
        doorClock.Restart();
        try
        {
            var g=o.Door.Geometry;var center=VP(g.Center);float sign=Math.Sign(Vector3.Dot(o.Leader.Position-center,VP(g.Normal)));
            float lateral=Math.Sign(Vector3.Dot(o.Leader.Position-center,VP(g.Axis)));if(lateral==0)lateral=1;
            Vector3 raw=variant==0?b.Position:center+VP(g.Normal)*sign*(variant<3?5.5f:7f)
                +VP(g.Axis)*(float)(g.HalfWidth+(variant%2==0?1.5:.8))*-lateral;
            string why=SupportPoint(b,o,role,raw,true,out var path,out var point,out var aim,out double protection);
            if(why.Length>0){SupportRefusal(o,s,role,why);return;}
            double length=variant==0?0:Length(path!.corners);
            if(variant==0&&(Vector3.Distance(b.Position,point)>.15||b.Mover.ActivePath!=null&&b.Mover.ActivePath.Status!=EBotMoveStatus.Complete))return;
            var t=supportBoard.Offer(o.Id,b.ProfileId,role,DP(point),DP(aim),length,protection,Time.time,config.DoorSupport);
            if(t==null)return;
            Release(s,"support-role-offered");t.Alternatives=o.Refusals.ToList();
            s.Support=new(){Operation=o,Trace=t,Owner=b,TargetInstance=System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(b.GoalEnemy),LastCheck=Time.time,LastCallback=Time.time};RecordDoorSupport(s,true);
        }
        finally{doorClock.Stop();doorBudget.Charge(doorClock.Elapsed.TotalMilliseconds);}
    }
    private void SupportRefusal(DoorSupportOperation o,BotState s,string role,string why)
    {
        string entry=s.ProfileId+" / "+role+" / "+why;if(o.Refusals.Contains(entry)||o.Refusals.Count>=12)return;
        o.Refusals.Add(entry);
        PublishDoorSupport(s,new(){Operation=o.Id,Leader=o.Leader.ProfileId,Support=s.ProfileId,Door=o.Door.Geometry.Id,Target=o.Target,
            Role=role,State="rejected",Reason=why,Alternatives=o.Refusals.ToList()});
    }
    private string SupportPoint(BotComponent b,DoorSupportOperation o,string role,Vector3 raw,bool calculate,
        out NavMeshPath? path,out Vector3 point,out Vector3 aim,out double protection)
    {
        var cfg=config!.DoorApproach;
        var approach=new DoorApproachConfig{DoorDistance=config.DoorSupport.MaxDistance,ContactDistance=cfg.ContactDistance,ContactLifetime=cfg.ContactLifetime,ApproachLimit=cfg.ApproachLimit};
        string why=ValidateDoor(b,o.Door,DP(raw),calculate,out path,out point,approach,true);
        aim=VP(o.Door.Geometry.Center)+Vector3.up*1.3f;protection=0;if(why.Length>0)return why;
        if(!supportBoard.Available(DP(point),config.DoorSupport.Spacing,b.ProfileId))return "support-position-reserved";
        // Keep the entrance and leader's firing/entry lane free, including the recipient's complete path.
        var center=VP(o.Door.Geometry.Center);var leader=o.Leader.Position;
        foreach(var other in states.Values)
        {var mate=other.Support;if(mate==null||mate.Owner==b)continue;
            var mt=mate.Trace;var start=new Vector3((float)mt.X,(float)mt.Y,(float)mt.Z);
            var end=new Vector3((float)mt.LookX,(float)mt.LookY,(float)mt.LookZ);end.y=start.y;
            if(SegmentDistance(point,start,end)<.8f)return "support-other-firing-lane";
            if(RouteCrossesSupportLane(path,start,end))return "support-route-crosses-support-lane";}
        if(SegmentDistance(point,leader,center)<.8f)return "support-leader-lane-blocked";
        if(RouteCrossesSupportLane(path,leader,center))return "support-route-crosses-leader-lane";
        protection=HoldProtection(point,b.GoalEnemy.KnownPlaces.LastKnownPosition!.Value);
        if(protection<2d/3)return "support-no-protected-position";
        if(role=="rear-guard")
        {
            // Choose a locally open, walkable approach. No rear enemy lookup and no point aimed into a wall.
            var away=(point-center);away.y=0;away.Normalize();bool found=false;float best=0;
            foreach(float angle in new[]{0f,-45f,45f,-90f,90f})
            {
                var direction=Quaternion.AngleAxis(angle,Vector3.up)*away;var end=point+direction*6;
                if(!NavMesh.SamplePosition(end,out var sample,.5f,NavMesh.AllAreas)||Math.Abs(sample.position.y-point.y)>.5f
                    ||NavMesh.Raycast(point,sample.position,out _,NavMesh.AllAreas))continue;
                var look=sample.position+Vector3.up*1.3f;
                if(!ClearAmbushRay(point+Vector3.up*1.5f,look))continue;
                float score=Vector3.Distance(point,sample.position);if(score<=best)continue;best=score;aim=look;found=true;
            }
            if(!found)return "support-no-rear-access-angle";
        }
        if(!ClearAmbushRay(point+Vector3.up*1.5f,aim)||!ClearAmbushRay(point+Vector3.up*1.2f,aim))return "support-projected-angle-blocked";
        if(states.TryGetValue(o.Leader,out var ls)&&ls.RoomClear.Policy.Trace is DoorClearTrace ct&&ct.ThrowRequested&&!ct.ExplosionConfirmed)
            return "support-leader-grenade-in-flight";
        return "";
    }
    private static float SegmentDistance(Vector3 p,Vector3 a,Vector3 b)
    {var d=b-a;return Vector3.Distance(p,a+d*(d.sqrMagnitude<.001f?0:Mathf.Clamp01(Vector3.Dot(p-a,d)/d.sqrMagnitude)));}
    private static bool RouteCrossesSupportLane(NavMeshPath? path,Vector3 start,Vector3 end)
    {
        if(path==null)return false;
        var corners=path.corners;
        for(int i=1;i<corners.Length;i++)
        {
            var a=corners[i-1];var z=corners[i];int samples=Math.Min(40,(int)Math.Ceiling(Vector3.Distance(a,z)/.4f));
            for(int j=0;j<=samples;j++)
                if(SegmentDistance(Vector3.Lerp(a,z,samples==0?0:j/(float)samples),start,end)<.8f)return true;
        }
        return false;
    }
    private bool KeepDoorSupport(BotComponent b,BotState s,ref ECombatDecision decision,ref bool result)
    {
        if(s.Support==null)return false;RefreshFear(b,s);RefreshProgress(b,s);RefreshDoorSupport(b,s);
        if(s.Support==null)return false;
        if(s.Feature!="D11"||s.Decision!="Hold")
        {
            s.Feature="D11";s.Decision="Hold";s.Candidates=new List<Candidate>{new(){Decision="Hold",Eligible=true,Reason="support-role-selected"},new(){Decision="Search",Eligible=false,Reason="support-temporary-role"}};
            Emit(s,"selected","support-role-selected");
        }
        s.Expected=ECombatDecision.Freeze;s.RequestedFrame=Time.frameCount;
        decision=s.Expected;result=true;return true;
    }
    private void SupportAccepted(BotState s,bool applied)
    {
        var r=s.Support;if(r==null)return;
        if(!applied){EndDoorSupport(s,"support-native-refused");return;}
        if(!r.Trace.Accepted){supportBoard.Accept(s.ProfileId,Time.time);RecordDoorSupport(s,true);}
    }
    private void RefreshDoorSupport(BotComponent b,BotState s)
    {
        var r=s.Support;if(r==null)return;string gate=SupportGate(b,s,r.Operation,r.Trace.Accepted);
        if(gate.Length>0){EndDoorSupport(s,gate);return;}
        double remaining=Math.Min(r.Operation.Until-Time.time,Math.Min(config!.ContactLifetime,config.DoorApproach.ContactLifetime)-b.GoalEnemy.TimeSinceLastKnownUpdated);
        if(config.Progress.Enabled&&!s.ProgressUnavailable)remaining=Math.Min(remaining,config.Progress.HoldLimit-s.Progress.Current.HoldSeconds);
        if(!supportBoard.Observe(s.ProfileId,Time.time,remaining)){EndDoorSupport(s,r.Trace.Reason);return;}
        if(r.Trace.Accepted&&Time.time-r.LastCallback>.6){EndDoorSupport(s,"support-action-callback-stopped");return;}
        RecordDoorSupport(s);
    }
    private bool StepDoorSupport(BotComponent b,BotState s)
    {
        var r=s.Support;if(r==null)return true;r.LastCallback=Time.time;RefreshDoorSupport(b,s);if(s.Support==null)return true;
        var t=r.Trace;if(!t.Accepted)return true;
        if(Time.time<r.NextCheck)return false;
        if(!doorBudget.TryBegin(Time.frameCount))
        {if(Time.time-r.LastCheck>.5)EndDoorSupport(s,"support-validation-budget-expired");return s.Support==null;}
        doorClock.Restart();
        try
        {
            var expected=new Vector3((float)t.X,(float)t.Y,(float)t.Z);bool first=!t.MovementRequested&&!t.Retained;
            string why=SupportPoint(b,r.Operation,t.Role,expected,first,out var path,out var point,out var aim,out _);
            if(why.Length>0){EndDoorSupport(s,why);return true;}
            if(Vector3.Distance(expected,point)>.1f){EndDoorSupport(s,"support-reserved-point-moved");return true;}
            // The rear approach is fixed for this offer, not silently replaced on each validation.
            aim=new((float)t.LookX,(float)t.LookY,(float)t.LookZ);
            if(first&&Vector3.Distance(b.Position,point)>.15)
            {
                t.MovementRequested=true;
                bool accepted=b.Mover.WalkToPointByWay(path!,true,.4f,false);r.Path=b.Mover.ActivePath;
                supportBoard.RequestMove(s.ProfileId,accepted&&r.Path!=null&&r.Path.PathStatus==NavMeshPathStatus.PathComplete
                    &&Vector3.Distance(r.Path.Destination,point)<=.3f);
                if(!t.MovementAccepted){EndDoorSupport(s,"support-move-refused");return true;}
            }
            if(r.Path!=null&&(!ReferenceEquals(r.Path,b.Mover.ActivePath)&&!(b.Mover.ActivePath==null&&r.Path.Status==EBotMoveStatus.Complete)
                ||r.Path.PathStatus!=NavMeshPathStatus.PathComplete))
            {EndDoorSupport(s,"support-path-ownership-lost");return true;}
            bool stationary=r.Path==null||r.Path.Status==EBotMoveStatus.Complete;
            if(stationary)
            {
                bool eyes=ClearAmbushRay(b.Transform.EyePosition,aim),muzzle=ClearAmbushRay(b.Transform.WeaponRoot,aim);
                if(!supportBoard.Arrive(s.ProfileId,DP(b.Position),stationary,eyes,muzzle)){EndDoorSupport(s,"support-arrival-or-angle-invalid");return true;}
                if(b.Mover.ActivePath!=null&&!ReferenceEquals(b.Mover.ActivePath,r.Path)&&b.Mover.ActivePath.Status!=EBotMoveStatus.Complete)
                {EndDoorSupport(s,"support-native-movement-started");return true;}
            }
            r.LastCheck=Time.time;r.NextCheck=Time.time+.2f;RecordDoorSupport(s);return false;
        }
        finally{doorClock.Stop();doorBudget.Charge(doorClock.Elapsed.TotalMilliseconds);}
    }
    private bool SupportSteer(BotComponent b,BotState s)
    {
        var r=s.Support;if(r?.Trace.AngleVerified!=true||SupportGate(b,s,r.Operation,true).Length>0)return false;
        b.Steering.LookToPoint(new((float)r.Trace.LookX,(float)r.Trace.LookY,(float)r.Trace.LookZ));return true;
    }
    private void EndDoorSupport(BotState s,string reason)
    {
        var r=s.Support;if(r==null)return;
        supportBoard.End(s.ProfileId,reason);r.Trace.State="released";r.Trace.Reason=reason;r.Trace.AngleVerified=false;
        if(r.Owner!=null&&r.Path!=null&&ReferenceEquals(r.Owner.Mover.ActivePath,r.Path))r.Path.Cancel();
        RecordDoorSupport(s,true);s.Support=null;
        if(s.Feature=="D11"){s.Decision="";s.ActiveLabel="";s.RequestedFrame=-1;Api.Decisions.Set(s.ProfileId,"");}
    }
    private void EndSupportOperation(DoorSupportOperation o,string why)
    {
        if(o.Ended)return;o.Ended=true;
        foreach(var s in states.Values.ToArray())if(s.Support?.Operation==o)EndDoorSupport(s,why);
        supportBoard.EndOperation(o.Id,why);
    }
    private void ClearDoorSupports(string reason)
    {foreach(var o in supportOperations.Values.ToArray())EndSupportOperation(o,reason);supportOperations.Clear();supportBoard.Clear();}
    private void RecordDoorSupport(BotState s,bool force=false)
    {
        var r=s.Support;if(r==null)return;var t=r.Trace;string key=t.State+":"+t.Reason+":"+t.Accepted+":"+t.AngleVerified;
        if(!force&&key==r.LogKey&&Time.time-r.LogAt<.5)return;r.LogKey=key;r.LogAt=Time.time;PublishDoorSupport(s,t);
    }
    private void PublishDoorSupport(BotState s,DoorSupportTrace t)
    {
        t.SupportName=s.Name; var leader=supportOperations.Values.FirstOrDefault(o=>o.Id==t.Operation)?.Leader; if(leader!=null&&states.TryGetValue(leader,out var leaderState))t.LeaderName=leaderState.Name;
        string json=Diagnostics.Serialize(new{schema=1,session,sequence=++sequence,feature="D11",stage="evaluated",decision="DoorSupport",reason=t.Reason,
            realtime=Time.realtimeSinceStartup,utc=DateTime.UtcNow.ToString("O"),configRevision=config?.Revision??0,profileId=s.ProfileId,name=s.Name,botInstance=s.Id,
            doorSupport=t});
        diagnostics?.Write(json);Api.Decisions.Publish(json,ex=>{if(!subscriberWarned){subscriberWarned=true;Logger.LogWarning(ex.Message);}});
    }
}
