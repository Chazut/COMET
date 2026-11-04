using System;
using System.Collections.Generic;
using System.Linq;
using Comet.Core;
using EFT;
using EFT.Interactive;
using HarmonyLib;
using Newtonsoft.Json;
using SAIN.Classes.Bot.Search;
using SAIN.Components;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.EnemyClasses;
using SAIN.SAINComponent.Classes.Mover;
using UnityEngine;
using UnityEngine.AI;

namespace Comet.Client;

internal sealed class DoorWork
{
    internal DoorDataStruct Data;
    internal DoorGeometry Geometry=new();
    internal int Side,Variant;
}
internal sealed class DoorRuntime
{
    internal readonly DoorApproach Policy=new();
    internal readonly List<DoorWork> Work=new();
    internal DoorWork? Chosen;
    internal IBotPathData? OwnedPath;
    internal BotComponent? Owner;
    internal double EvidenceAt;
    internal float NextCheck,Started,LogAt=-1000,NextPlan;
    internal bool Fault;
    internal string LogKey="",Target="";
    internal Vector3 Contact;
    internal int TargetInstance;
    internal void DropReferences(){Work.Clear();Chosen=null;OwnedPath=null;Owner=null;}
}

public sealed partial class Plugin
{
    private readonly DoorWorkBudget doorBudget=new();
    private readonly System.Diagnostics.Stopwatch doorClock=new();
    private readonly Collider[] doorOccupants=new Collider[24];
    private static DoorPoint DP(Vector3 p)=>new(p.x,p.y,p.z);
    private static Vector3 VP(DoorPoint p)=>new((float)p.X,(float)p.Y,(float)p.Z);

    private static void PatchDoorApproach(Harmony h)
    {
        var m=AccessTools.Method(typeof(SearchClass),nameof(SearchClass.Search),new[]{typeof(bool),typeof(Enemy)})
            ??throw new MissingMethodException("D01 Search(bool, Enemy)");
        h.Patch(m,prefix:new HarmonyMethod(typeof(Plugin),nameof(DoorSearchStep)){priority=Priority.First});
    }
    private string DoorGate(BotComponent b,Enemy? e,BotState s)
    {
        if(MedicalPostureGuard(b))return "medical-posture-protected";
        var c=config!;bool scope=c.Enabled&&!s.Door.Fault&&NativeAdapter.IsPmcAi(b)&&b.BotActive&&!b.GameEnding&&Authority&&!Ghost(b.ProfileId);
        bool search=s.ActiveLabel=="COMET:Search"&&s.Feature=="A01"&&s.Decision=="Search"&&b.Decision.CurrentCombatDecision==ECombatDecision.Search
            &&b.SAINLayersActive&&b.CurrentAction?.GetType().Name=="SearchAction"&&ReferenceEquals(e,b.GoalEnemy);
        bool unsafeNow=b.Flashed.IsFlashed||b.EnemyController.VisibleEnemies.Count>0||b.BotOwner.Memory.IsUnderFire||b.Suppression.IsSuppressed
            ||s.Fear.Current.Panic||b.Medical.TimeSinceShot<3||b.BotOwner.DoorOpener.Interacting||b.DoorOpener.Interacting
            ||b.Decision.CurrentSelfDecision!=ESelfActionType.None||b.Decision.CurrentSquadDecision!=ESquadDecision.None||!VisibleAdapter.WeaponReady(b);
        double unseen=e==null?0:e.Seen?e.TimeSinceSeen:Time.time;
        return DoorPolicy.Gate(scope,search,unsafeNow,e?.EnemyKnown==true&&e.KnownPlaces.LastKnownPosition.HasValue,e?.IsVisible==true,
            e?.TimeSinceLastKnownUpdated??-1,unseen,c.DoorApproach);
    }
    private static bool DoorSearchStep(SearchClass __instance,Enemy enemy)
    {
        var p=Instance;var b=__instance.Bot;
        if(p?.config==null||!p.states.TryGetValue(b,out var s))return true;
        if(s.Entry.Policy.Active){p.RefreshDoorEntry(b,s);return !s.Entry.Policy.Active;}
        try{if(!p.GuardDoorReservation(b,s))return true;p.RefreshDoorInterruption(b,s);if(!p.GuardDoorReservation(b,s))return true;bool fallback=s.Interruption.Policy.Pending?p.StepDoorResume(b,s):s.RoomClear.Policy.Active?p.StepDoorClear(b,s):s.Fake.Policy.Active?p.StepDoorFake(b,s):s.StepPeek.Policy.Active?p.StepDoorPeek(b,enemy,s):s.RunBy.Policy.Active?p.StepDoorRun(b,enemy,s):s.Jump.Policy.Active?p.StepDoorJump(b,enemy,s):p.StepDoor(b,enemy,s);return p.CompleteDoorReservationStep(b,s,fallback);}
        catch(Exception ex){if(s.Interruption.Policy.Pending||s.Interruption.Policy.Resumed)p.AbandonDoorResume(s,"door-resume-adapter-error");if(s.RoomClear.Policy.Active){s.RoomClear.Fault=true;p.EndDoorClear(s,"clear-adapter-error");}if(s.Fake.Policy.Active)p.EndDoorFake(s,"fake-adapter-error");if(s.StepPeek.Policy.Active){s.StepPeek.Fault=true;p.EndDoorStep(s,"step-adapter-error");}if(s.RunBy.Policy.Active){s.RunBy.Fault=true;p.EndDoorRun(s,"run-by-adapter-error");}if(s.Jump.Policy.Active){s.Jump.Fault=true;p.EndDoorJump(s,"peek-adapter-error");}s.Door.Fault=true;p.EndDoor(s,"door-adapter-error");p.ReleaseDoorReservation(s,"reservation-adapter-error");p.Logger.LogWarning("COMET doorway adapter yielded: "+ex.Message);return true;}
    }
    // false means this one Search invocation was replaced by an already accepted, bounded native move.
    private bool StepDoor(BotComponent b,Enemy e,BotState s)
    {
        var r=s.Door;if(s.Interruption.Policy.Pending)return true;string gate=DoorGate(b,e,s);
        if(gate.Length>0){if(r.Policy.Active||r.Policy.Planning)EndDoor(s,gate);return true;}
        var known=e.KnownPlaces.LastKnownPosition!.Value;var c=config!.DoorApproach;
        if(r.Policy.Active)
        {
            if(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(e)!=r.TargetInstance){EndDoor(s,"door-contact-instance-changed");return true;}
            var t=r.Policy.Trace!;
            bool owns=OwnsDoorPath(r)||(b.Mover.ActivePath==null&&r.OwnedPath?.Status==SAIN.Models.Enums.EBotMoveStatus.Complete
                &&Vector3.Distance(b.Position,new Vector3((float)t.X,(float)t.Y,(float)t.Z))<=.6f);
            bool arrived=owns&&r.OwnedPath!.Status==SAIN.Models.Enums.EBotMoveStatus.Complete;
            if(!r.Policy.Observe(e.EnemyProfileId,DP(known),DP(b.Position),e.TimeSinceLastKnownUpdated,Time.time,c.ApproachLimit,owns,arrived))
            {var work=r.Chosen;bool reached=t.Arrived;FinishDoor(s);
                if(reached&&work!=null&&!TryStartDoorClear(b,e,s,work)&&!TryStartDoorFake(b,e,s,work))TryStartDoorJump(b,e,s,work);return !s.RoomClear.Policy.Active&&!s.Fake.Policy.Active&&!s.Jump.Policy.Active&&!s.RunBy.Policy.Active;}
            if(Time.time>=r.NextCheck)
            {
                if(!doorBudget.TryBegin(Time.frameCount))
                {
                    // Briefly wait for validation; never keep walking indefinitely on stale geometry.
                    if(Time.time-r.NextCheck>.5){EndDoor(s,"door-validation-budget-expired");return true;}
                }
                else
                {
                    doorClock.Restart();string invalid;
                    try{invalid=ValidateDoor(b,r.Chosen!,new DoorPoint(t.X,t.Y,t.Z),false,out _,out _);}
                    finally{doorClock.Stop();doorBudget.Charge(doorClock.Elapsed.TotalMilliseconds);}
                    if(invalid.Length>0){EndDoor(s,invalid);return true;}
                    r.NextCheck=Time.time+.25f;
                }
            }
            RecordDoor(s);return false;
        }
        if(!r.Policy.Planning)
        {
            if(Time.time<r.NextPlan||!r.Policy.CanPlan(e.EnemyProfileId,DP(known),Time.time))return true;
            r.NextPlan=Time.time+2;
            StartDoorPlan(b,e,s,known);
            if(!r.Policy.Planning)return true;
        }
        if(r.Target!=e.EnemyProfileId||r.TargetInstance!=System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(e)
            ||Vector3.Distance(r.Contact,known)>2||r.Policy.PlanningExpired(Time.time))
        {EndDoor(s,"door-plan-became-stale");return true;}
        if(!doorBudget.TryBegin(Time.frameCount))return true;
        doorClock.Restart();
        try
        {
            if(r.Work.Count>0)
            {
                var work=r.Work[0];r.Work.RemoveAt(0);
                if(DoorBusy(b,s,work.Geometry.Id)){r.Policy.Trace!.Candidates.Add(new(){Door=work.Geometry.Id,Reason="reservation-door-busy"});return true;}
                var raw=DoorPolicy.Stack(work.Geometry,DP(b.Position),work.Side,work.Variant);
                string why=ValidateDoor(b,work,raw,true,out var path,out var point);
                var candidate=new DoorCandidate{Door=work.Geometry.Id,Side=work.Side<0?"left":"right",X=point.x,Y=point.y,Z=point.z,Reason=why,Eligible=why.Length==0};
                if(candidate.Eligible)
                {
                    candidate.PathLength=Length(path!.corners);candidate.Score=Math.Round(candidate.PathLength+Math.Abs(point.y-b.Position.y)*2,3);
                    candidate.Reason="door-reachable-protected-frame";
                    if(!s.ProgressUnavailable&&s.Progress.Blocked(ProgressAdapter.Destination(point),Time.time))
                    {candidate.Eligible=false;candidate.Reason="door-progress-retry-blocked";}
                }
                r.Policy.Trace!.Candidates.Add(candidate);
                var best=DoorPolicy.Best(r.Policy.Trace.Candidates);
                if(ReferenceEquals(best,candidate))r.Chosen=work;
                // One candidate work unit. Other bots/frames share the same bounded allowance.
                return true;
            }
            var selected=DoorPolicy.Best(r.Policy.Trace!.Candidates);
            if(selected==null||r.Chosen==null){EndDoor(s,"door-no-safe-frame-point");return true;}
            string refusal=ValidateDoor(b,r.Chosen,selected.Point,true,out var finalPath,out var finalPoint);
            if(refusal.Length>0){EndDoor(s,refusal);return true;}
            selected.X=finalPoint.x;selected.Y=finalPoint.y;selected.Z=finalPoint.z;selected.PathLength=Length(finalPath!.corners);
            if(!AcquireDoorReservation(b,s,r.Chosen,"approach",ReservationDeadline(b,s))){EndDoor(s,"reservation-unavailable");return true;}
            r.Policy.Request(selected,DP(b.Position),Time.time);RecordDoor(s,true);
            // Reset search's corner/wait bookkeeping before temporarily supplying our verified route.
            b.Search.Reset();
            bool accepted=b.Mover.WalkToPointByWay(finalPath,true,.5f,false);
            r.Policy.Accept(accepted,Time.time);
            if(!accepted){FinishDoor(s);return true;}
            r.Owner=b;r.OwnedPath=b.Mover.ActivePath;r.NextCheck=Time.time+.25f;r.Started=Time.time;
            if(!OwnsDoorPath(r)){EndDoor(s,"door-native-route-not-observed");return true;}
            RecordDoor(s,true);return false;
        }
        finally{doorClock.Stop();doorBudget.Charge(doorClock.Elapsed.TotalMilliseconds);}
    }
    private void StartDoorPlan(BotComponent b,Enemy e,BotState s,Vector3 known)
    {
        var r=s.Door;var native=e.Path.PathToEnemy;
        if(native==null||native.status!=NavMeshPathStatus.PathComplete)return;
        var corners=native.corners;if(corners.Length<2||corners.Length>128)return;
        var route=corners.Select(DP).ToArray();
        // Native current voxel only. No FindObjectsOfType/global door scan and no mutation of SAIN's cache.
        var voxel=b.BotOwner.VoxelesPersonalData.CurVoxel;if(voxel?.DoorLinks==null)return;
        r.DropReferences();r.Policy.Begin(e.EnemyProfileId,s.Situation?.ContactSource??"sain-last-known",DP(known),e.TimeSinceLastKnownUpdated,Time.time);
        r.Target=e.EnemyProfileId;r.TargetInstance=System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(e);r.Contact=known;r.Started=Time.time;r.EvidenceAt=Time.time-e.TimeSinceLastKnownUpdated;
        var candidates=new List<DoorWork>();int visited=0;
        foreach(var link in voxel.DoorLinks)
        {
            if(++visited>48)break;
            if(link==null||link.Door==null)continue;
            var work=new DoorWork{Data=new DoorDataStruct(link)};
            if(!TryDoorGeometry(work.Data,out var g)){InvalidDoorGeometry(b,work.Data,DP(b.Position));continue;}
            work.Geometry=g;
            string why=DoorPolicy.Geometry(g,DP(b.Position),DP(known),route,config!.DoorApproach);
            if(why.Length==0)candidates.Add(work);
            else if((g.Center-DP(b.Position)).Length<=config!.DoorApproach.DoorDistance&&r.Policy.Trace!.Candidates.Count<12)
                r.Policy.Trace.Candidates.Add(new(){Door=g.Id,Reason=why});
        }
        foreach(var d in candidates.OrderBy(x=>(x.Geometry.Center-DP(b.Position)).Length).ThenBy(x=>x.Geometry.Id,StringComparer.Ordinal).Take(3))
            foreach(int variant in new[]{0,1})foreach(int side in new[]{-1,1})r.Work.Add(new(){Data=d.Data,Geometry=d.Geometry,Side=side,Variant=variant});
        if(r.Work.Count==0){EndDoor(s,"door-no-relevant-local-door");return;}
        RecordDoor(s,true);
    }
    private string ValidateDoor(BotComponent b,DoorWork w,DoorPoint raw,bool calculate,out NavMeshPath? path,out Vector3 point,DoorApproachConfig? approach=null,bool stationary=false)
    {
        path=null;point=VP(raw);
        if(!TryDoorGeometry(w.Data,out var g)){InvalidDoorGeometry(b,w.Data,raw);return "geometry-invalid-frame";}
        if(g.State!="Open"&&g.State!="Shut")return "door-state-changed";
        if((g.Center-w.Geometry.Center).Length>.1||Math.Abs(g.Axis.Dot(w.Geometry.Axis))<.99)return "door-frame-changed";
        // Revalidate route relevance at selection/move request, using only the remembered native path.
        var e=b.GoalEnemy;var known=e?.KnownPlaces.LastKnownPosition;
        if(e==null||known==null)return "door-contact-ended";
        if(calculate)
        {
            var route=e.Path.PathToEnemy;
            if(route==null||route.status!=NavMeshPathStatus.PathComplete)return "door-no-current-contact-route";
            string relevant=DoorPolicy.Geometry(g,DP(b.Position),DP(known.Value),route.corners.Select(DP).ToArray(),approach??config!.DoorApproach);
            if(relevant.Length>0)return relevant;
            if(!NavMesh.SamplePosition(point,out var sample,.3f,NavMesh.AllAreas))return "door-no-local-navmesh";
            point=sample.position;
            if(Physics.Linecast(VP(raw)+Vector3.up*.8f,point+Vector3.up*.8f,LayersMaskController.HighPolyWithTerrainMask,QueryTriggerInteraction.Ignore))return "door-sample-through-wall";
            if(!b.Mover.CanGoToPoint(point,out path,true,.1f))return "door-incomplete-stack-route";
            string placement=DoorPolicy.Placement(g,DP(b.Position),raw,DP(point),path.corners.Select(DP).ToArray(),path.status==NavMeshPathStatus.PathComplete);
            if(placement.Length>0)return placement;
        }
        else if(!stationary&&b.Mover.ActivePath?.PathStatus!=NavMeshPathStatus.PathComplete)return "door-route-invalidated";
        string panels=ValidateDoorPanels(b,w,g,point);if(panels.Length>0)return panels;
        int hits=0;
        try
        {
            hits=Physics.OverlapCapsuleNonAlloc(point+Vector3.up*.4f,point+Vector3.up*1.45f,.25f,doorOccupants,~0,QueryTriggerInteraction.Ignore);
            if(hits>=doorOccupants.Length)return "door-clearance-uncertain";
            for(int i=0;i<hits;i++)
            {
                var col=doorOccupants[i];if(col==null||ReferenceEquals(col.GetComponentInParent<Player>(),b.Player))continue;
                return col.GetComponentInParent<Player>()!=null?"door-point-occupied":"door-standing-volume-blocked";
            }
        }
        finally{Array.Clear(doorOccupants,0,doorOccupants.Length);}
        var origin=point+Vector3.up*1.2f;var toward=known.Value+Vector3.up*1.2f-origin;
        if(!Physics.Raycast(origin,toward.normalized,out var cover,Math.Min(2,toward.magnitude),LayersMaskController.HighPolyWithTerrainMask,QueryTriggerInteraction.Ignore)
            ||cover.collider==null||cover.collider.attachedRigidbody!=null||cover.collider.GetComponentInParent<Door>()!=null)
            return "door-frame-not-protective";
        return "";
    }
    private static double Length(Vector3[] corners){double d=0;for(int i=1;i<corners.Length;i++)d+=Vector3.Distance(corners[i-1],corners[i]);return d;}
    private static bool OwnsDoorPath(DoorRuntime r)
    {
        var t=r.Policy.Trace;var b=r.Owner;
        return t!=null&&b!=null&&r.OwnedPath!=null&&ReferenceEquals(b.Mover.ActivePath,r.OwnedPath)
            &&Vector3.Distance(r.OwnedPath.Destination,new Vector3((float)t.X,(float)t.Y,(float)t.Z))<=.3f;
    }
    private void FinishDoor(BotState s)
    {
        var r=s.Door;
        if(OwnsDoorPath(r))
        {
            // Never stop a replacement action's path. A native Search will rebuild from its real position.
            r.OwnedPath!.Cancel();
            if(r.Owner!.Decision.CurrentCombatDecision==ECombatDecision.Search&&r.Owner.CurrentAction?.GetType().Name=="SearchAction")r.Owner.Search.Reset();
        }
        RecordDoor(s,true);r.DropReferences();
    }
    private void EndDoor(BotState s,string reason)
    {if(!s.Door.Policy.Active&&!s.Door.Policy.Planning)return;s.Door.Policy.End(reason,Time.time);FinishDoor(s);}
    private void RefreshDoor(BotComponent b,BotState s)
    {
        var r=s.Door;if(!r.Policy.Active&&!r.Policy.Planning)return;
        string gate=DoorGate(b,b.GoalEnemy,s);
        if(gate.Length>0){EndDoor(s,gate);return;}
        if(r.Policy.PlanningExpired(Time.time)){EndDoor(s,"door-plan-became-stale");return;}
        if(r.Policy.Active&&Time.time-r.Started>config!.DoorApproach.ApproachLimit+1)EndDoor(s,"door-action-callback-stopped");
    }
    private void RecordDoor(BotState s,bool force=false)
    {
        var r=s.Door;var t=r.Policy.Trace;if(t==null)return;
        string key=t.State+":"+t.Reason;
        if(!force&&key==r.LogKey&&Time.time-r.LogAt<1)return;
        r.LogAt=Time.time;r.LogKey=key;
        var json=JsonConvert.SerializeObject(new{schema=1,session,sequence=++sequence,feature="D01",stage="evaluated",decision="DoorApproach",
            reason=t.Reason,realtime=Time.realtimeSinceStartup,utc=DateTime.UtcNow.ToString("O"),configRevision=config?.Revision??0,
            doorApproachConfig=config?.DoorApproach,profileId=s.ProfileId,name=s.Name,botInstance=s.Id,doorApproach=t},Diagnostics.Json);
        diagnostics?.Write(json);Api.Decisions.Publish(json,ex=>{if(!subscriberWarned){subscriberWarned=true;Logger.LogWarning(ex.Message);}});
    }
}
