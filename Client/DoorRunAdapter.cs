using System;
using System.Linq;
using Comet.Core;
using EFT.Interactive;
using Newtonsoft.Json;
using SAIN.Components;
using SAIN.Models.Enums;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.EnemyClasses;
using SAIN.SAINComponent.Classes.Mover;
using UnityEngine;
using UnityEngine.AI;

namespace Comet.Client;

internal sealed class DoorRunRuntime
{
    internal readonly DoorReconOpportunity Opportunity=new();
    internal readonly DoorRunBy Policy=new();
    internal BotComponent? Owner;
    internal DoorWork? Door;
    internal DoorJumpRuntime? JumpCandidate;
    internal IBotPathData? OwnedPath;
    internal NavMeshPath? Outward,Return;
    internal Vector3 Stack,Far,Angle,ReturnFrom,Look;
    internal int TargetInstance,ValidationStep;
    internal float LastCallback,NextCheck,LastCheck,OpenedAt,LogAt=-1000;
    internal bool Fault;
    internal string LogKey="";
    internal void DropReferences(){Owner=null;Door=null;JumpCandidate?.DropReferences();JumpCandidate=null;OwnedPath=null;Outward=null;Return=null;}
}

public sealed partial class Plugin
{
    private void TryStartDoorRecon(BotComponent b,Enemy e,BotState s,DoorWork w)
    {
        var r=s.RunBy;
        if(s.Fake.Policy.Active||r.Fault||s.Jump.Fault||s.StepPeek.Policy.Active||r.Policy.Active||s.Jump.Policy.Active||DoorGate(b,e,s).Length>0||e.KnownPlaces.LastKnownPosition is not Vector3 known)return;
        var t=r.Opportunity.TryBegin(w.Geometry.Id,e.EnemyProfileId,s.Situation?.ContactSource??"sain-last-known",b.Info.Personality.ToString(),
            DP(known),e.TimeSinceLastKnownUpdated,Time.time,jumpRandom.NextDouble(),jumpRandom.NextDouble(),config!.DoorJumpPeek,config.DoorRunBy);
        if(t==null)return;
        r.Policy.Begin(t,Time.time);
        if(r.Policy.Active)
        {
            r.Owner=b;r.Door=w;r.Stack=b.Position;r.Look=VP(w.Geometry.Center)+Vector3.up*1.3f;
            r.TargetInstance=System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(e);r.ValidationStep=0;r.LastCallback=Time.time;
            r.JumpCandidate=new(){Owner=b,Door=w,Stack=b.Position};
            StopQuiet(b,s,"quiet-door-recon-plan");EndAbandonment(s,"search-check-door-recon");EndSilentSearch(s,"silent-search-door-recon");
        }
        RecordDoorRun(s,true);
    }
    private string RunGate(BotComponent b,Enemy? e,BotState s)
    {
        var r=s.RunBy;var t=r.Policy.Trace!;
        if(config?.Enabled!=true||!config.DoorApproach.Enabled||(!config.DoorJumpPeek.Enabled&&!config.DoorRunBy.Enabled)
            ||t.Style=="run-by"&&!config.DoorRunBy.Enabled||r.Fault||!NativeAdapter.IsPmcAi(b)||!b.BotActive||b.IsDead||b.GameEnding||!Authority||Ghost(b.ProfileId))return "run-by-out-of-scope";
        if(MedicalPostureGuard(b))return "medical-posture-protected";
        if(e==null||!ReferenceEquals(e,b.GoalEnemy)||!e.EnemyKnown||e.KnownPlaces.LastKnownPosition is not Vector3 known
            ||!r.Policy.SameContact(e.EnemyProfileId,DP(known))||e.TimeSinceLastKnownUpdated>config.DoorApproach.ContactLifetime
            ||System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(e)!=r.TargetInstance)return "run-by-contact-changed-or-expired";
        if(e.IsVisible){t.VisualAcquired=true;return "run-by-native-visual-acquired";}
        if(s.ActiveLabel!="COMET:Search"||s.Feature!="A01"||s.Decision!="Search"||!b.SAINLayersActive
            ||b.Decision.CurrentCombatDecision!=ECombatDecision.Search||b.CurrentAction?.GetType().Name!="SearchAction")return "run-by-search-not-owned";
        if(b.EnemyController.VisibleEnemies.Count>0||b.Flashed.IsFlashed||b.BotOwner.Memory.IsUnderFire||b.Suppression.IsSuppressed
            ||s.Fear.Current.Panic||b.Medical.TimeSinceShot<3||(!VisibleAdapter.WeaponReady(b)&&!RunOwnOpening(b,s))
            ||b.Decision.CurrentSelfDecision!=ESelfActionType.None||b.Decision.CurrentSquadDecision!=ESquadDecision.None)return "run-by-native-priority";
        if((b.DoorOpener.Interacting||b.BotOwner.DoorOpener.Interacting)&&t.State!="opening")return "run-by-other-door-interaction";
        // Only personally perceived, dated places. Never inspect an unseen enemy's live transform.
        int count=0;
        foreach(var other in b.EnemyController.KnownEnemies)
        {
            if(++count>32)return "run-by-threat-list-truncated";
            var place=other?.KnownPlaces.LastHeardPlace;if(place==null)continue;
            double age=Time.time-silentHeardAt(place);if(age<0||age>1.2)continue;
            if(place.SoundType is SAINSoundType.FootStep or SAINSoundType.Sprint or SAINSoundType.Jump or SAINSoundType.Land or SAINSoundType.Door or SAINSoundType.DoorBreach
                &&Vector3.Distance(place.Position,VP(r.Door!.Geometry.Center))<=4)return "run-by-heard-threat-at-door";
        }
        return "";
    }
    private static bool RunOwnOpening(BotComponent b,BotState s)=>s.RunBy.Policy.Active&&s.RunBy.Policy.Trace?.State=="opening"
        &&ReferenceEquals(s.RunBy.Owner,b)&&s.RunBy.Door?.Data.Door!=null&&Time.time-s.RunBy.OpenedAt<=4.5f;
    private bool StepDoorRun(BotComponent b,Enemy e,BotState s)
    {
        var r=s.RunBy;r.LastCallback=Time.time;
        string gate=RunGate(b,e,s);if(gate.Length>0){EndDoorRun(s,gate);return true;}
        var t=r.Policy.Trace!;var c=config!.DoorRunBy;
        bool back=t.State is "returning" or "watching";
        Vector3 destination=back?r.Angle:r.Far;var direction=destination-b.Position;direction.y=0;
        var velocity=b.Player.Velocity;velocity.y=0;
        bool arrived=Vector3.Distance(b.Position,destination)<=.35f;
        if(t.MovementAccepted)
        {
            if(!OwnsRunPath(r)&&!(b.Mover.ActivePath==null&&r.OwnedPath?.Status==EBotMoveStatus.Complete&&arrived))
            {EndDoorRun(s,"run-by-path-replaced");return true;}
            if(!DoorRunGeometry.Contains(r.Door!.Geometry,DP(r.Stack),DP(back?r.ReturnFrom:r.Stack),DP(destination),DP(b.Position)))
            {EndDoorRun(s,"run-by-left-verified-lane");return true;}
        }
        if(!r.Policy.Observe(DP(b.Position),Vector3.Dot(velocity,direction.normalized),b.Player.Physical.Stamina.NormalValue*100,
            b.Player.MovementContext.IsGrounded,b.Player.IsSprintEnabled||b.Mover.Running,arrived,VisibleAdapter.WeaponReady(b),e.IsVisible,e.TimeSinceLastKnownUpdated,Time.time,c))
        {FinishDoorRun(s);return true;}
        RecordDoorRun(s);
        if(t.State=="opening")
        {
            if(r.Door!.Data.Door.DoorState==EDoorState.Open&&Time.time-r.OpenedAt>=1.25f&&!JumpInteractionState(b)&&!b.DoorOpener.Interacting&&!b.BotOwner.DoorOpener.Interacting)
            {r.Policy.Opened(Time.time);r.ValidationStep=0;RecordDoorRun(s,true);}
            return false;
        }
        if(t.State=="validating")
        {
            if(!doorBudget.TryBegin(Time.frameCount))return false;
            doorClock.Restart();
            try{return PrepareDoorRecon(b,e,s);}
            finally{doorClock.Stop();doorBudget.Charge(doorClock.Elapsed.TotalMilliseconds);}
        }
        if(r.Door!.Data.Door.DoorState!=EDoorState.Open){EndDoorRun(s,"run-by-door-state-changed");return true;}
        if(t.State=="turning")r.OwnedPath?.RequestEndSprint(ESprintUrgency.None,"COMET run-by walking return");
        if(Time.time>=r.NextCheck||t.State=="return-pending")
        {
            if(!doorBudget.TryBegin(Time.frameCount))
            {if(Time.time-r.LastCheck>.4){EndDoorRun(s,"run-by-validation-budget-expired");return true;}return false;}
            doorClock.Restart();
            try
            {
                string why=RunLane(b,r,back);
                if(why.Length>0){EndDoorRun(s,why);return true;}
                if(t.State=="return-pending")
                {
                    // Use the observed far arrival as the start, with both route and full body volume rechecked.
                    if(!b.Mover.CanGoToPoint(r.Angle,out var path,true,.1f)
                        ||DoorRunGeometry.Route(r.Door.Geometry,DP(r.Stack),DP(b.Position),DP(r.Angle),DP(r.Angle),path.corners.Select(DP).ToArray(),path.status==NavMeshPathStatus.PathComplete).Length>0)
                    {EndDoorRun(s,"run-by-return-route-invalidated");return true;}
                    why=RunVolume(b,b.Position,r.Angle);
                    if(why.Length>0||!RunAngleSight(b,r)) {EndDoorRun(s,why.Length>0?why:"run-by-angle-no-door-sightline");return true;}
                    if(!b.Mover.WalkToPointByWay(path,true,.2f,false)){EndDoorRun(s,"run-by-return-movement-refused");return true;}
                    r.ReturnFrom=b.Position;r.Return=path;r.OwnedPath=b.Mover.ActivePath;r.Policy.ReturnStarted(DP(b.Position),Time.time);
                    if(!OwnsRunPath(r)){EndDoorRun(s,"run-by-return-path-not-observed");return true;}
                    RecordDoorRun(s,true);
                }
                r.LastCheck=Time.time;r.NextCheck=Time.time+.2f;
            }
            finally{doorClock.Stop();doorBudget.Charge(doorClock.Elapsed.TotalMilliseconds);}
        }
        b.Steering.LookToPoint(r.Look);return false;
    }
    // One bounded planning unit per Search call, all sharing D01/D02's frame budget.
    private bool PrepareDoorRecon(BotComponent b,Enemy e,BotState s)
    {
        var r=s.RunBy;var t=r.Policy.Trace!;var j=config!.DoorJumpPeek;var c=config.DoorRunBy;
        if(r.Door==null||!TryDoorGeometry(r.Door.Data,out var g)||g.State!="Open"&&g.State!="Shut"
            ||(g.Center-r.Door.Geometry.Center).Length>.1||Math.Abs(g.Axis.Dot(r.Door.Geometry.Axis))<.99||Vector3.Distance(b.Position,r.Stack)>.35f)
        {EndDoorRun(s,"recon-door-or-stack-changed");return true;}
        if(g.State=="Shut")
        {
            if(!RequestJumpDoorOpen(b,r.JumpCandidate!)){EndDoorRun(s,"recon-native-open-refused");return true;}
            r.OpenedAt=Time.time;r.Policy.Opening(Time.time);RecordDoorRun(s,true);return false;
        }
        t.DoorOpenObserved=true;
        switch(r.ValidationStep++)
        {
            case 0:
                t.JumpReason=!j.Enabled?"jump-disabled":!b.Player.MovementContext.CanJump||b.Player.Physical.Stamina.NormalValue*100<j.MinimumStamina?"jump-resources-unavailable":PrepareJumpRoutes(b,r.JumpCandidate!);
                return false;
            case 1:
                if(t.JumpReason.Length==0)t.JumpReason=JumpLane(b,r.JumpCandidate!,true);
                t.JumpEligible=t.JumpReason.Length==0;return false;
            case 2:
                t.RunReason=!c.Enabled?"run-by-disabled":!b.Player.MovementContext.CanSprint||!b.BotOwner.CanSprintPlayer
                    ||b.Player.Physical.Stamina.NormalValue*100<c.MinimumStamina?"run-by-resources-unavailable":PrepareRunRoutes(b,r);
                return false;
            case 3:
                if(t.RunReason.Length==0){t.RunReason=RunLane(b,r,false);if(t.RunReason.Length==0)t.RunReason=RunVolume(b,r.Far,r.Angle);}
                t.RunEligible=t.RunReason.Length==0;
                t.Style=DoorReconOpportunity.Choose(t.JumpEligible,t.RunEligible,t.StyleRoll/100,j,c);
                t.Alternatives=new[]{"Jump: "+(t.JumpEligible?"eligible":t.JumpReason),"Run-by: "+(t.RunEligible?"eligible":t.RunReason),
                    "Normal SAIN search if neither is safe; no second maneuver in this opportunity."};
                if(t.Style=="none"){EndDoorRun(s,"recon-no-eligible-style");return true;}
                if(t.Style=="jump")
                {
                    var p=s.Jump;var candidate=r.JumpCandidate!;
                    p.Owner=b;p.Door=r.Door;p.Stack=r.Stack;p.Look=VP(new(t.KnownX,t.KnownY,t.KnownZ))+Vector3.up*1.2f;
                    p.Landing=candidate.Landing;p.Outward=candidate.Outward;p.Return=candidate.Return;p.TargetInstance=r.TargetInstance;p.ValidationStep=0;p.LastCallback=Time.time;
                    p.Policy.BeginSelected(t,Time.time);r.Policy.Delegate(Time.time);RecordDoorRun(s,true);r.DropReferences();RecordDoorJump(s,true);return false;
                }
                // Recheck the chosen outward volume immediately before requesting sprint.
                string why=RunLane(b,r,false);
                if(why.Length>0||!b.Player.MovementContext.IsGrounded||b.Player.Physical.Stamina.NormalValue*100<c.MinimumStamina)
                {EndDoorRun(s,why.Length>0?why:"run-by-resources-changed");return true;}
                if(!b.Mover.RunToPointByWay(r.Outward!,true,.2f,ESprintUrgency.High,false)){EndDoorRun(s,"run-by-native-movement-refused");return true;}
                r.OwnedPath=b.Mover.ActivePath;r.Policy.Start(DP(b.Position),Time.time);
                if(!OwnsRunPath(r)){EndDoorRun(s,"run-by-path-not-observed");return true;}
                r.LastCheck=Time.time;r.NextCheck=Time.time+.2f;RecordDoorRun(s,true);return false;
        }
        EndDoorRun(s,"recon-invalid-planning-stage");return true;
    }
    private string PrepareRunRoutes(BotComponent b,DoorRunRuntime r)
    {
        var g=r.Door!.Geometry;var far=DoorRunGeometry.Far(g,DP(r.Stack));var angle=DoorRunGeometry.Angle(g,DP(r.Stack));
        if(!NavMesh.SamplePosition(VP(far),out var f,.2f,NavMesh.AllAreas)||!NavMesh.SamplePosition(VP(angle),out var a,.2f,NavMesh.AllAreas))return "run-by-no-local-navmesh";
        r.Far=f.position;r.Angle=a.position;r.ReturnFrom=r.Far;
        if(!DoorRunGeometry.ProtectedFar(g,DP(r.Stack),DP(r.Far)))return "run-by-far-end-still-exposed";
        if(!b.Mover.CanGoToPoint(r.Far,out var outward,true,.1f))return "run-by-no-outward-route";
        string why=DoorRunGeometry.Route(g,DP(r.Stack),DP(b.Position),far,DP(r.Far),outward.corners.Select(DP).ToArray(),outward.status==NavMeshPathStatus.PathComplete);
        if(why.Length>0)return why;
        var back=new NavMeshPath();NavMesh.CalculatePath(r.Far,r.Angle,NavMesh.AllAreas,back);
        why=DoorRunGeometry.Route(g,DP(r.Stack),DP(r.Far),angle,DP(r.Angle),back.corners.Select(DP).ToArray(),back.status==NavMeshPathStatus.PathComplete);
        if(why.Length>0)return "run-by-no-return-route:"+why;
        if(!RunAngleSight(b,r))return "run-by-angle-no-door-sightline";
        r.Outward=outward;r.Return=back;
        var t=r.Policy.Trace!;t.StackX=r.Stack.x;t.StackY=r.Stack.y;t.StackZ=r.Stack.z;t.FarX=r.Far.x;t.FarY=r.Far.y;t.FarZ=r.Far.z;
        t.AngleX=r.Angle.x;t.AngleY=r.Angle.y;t.AngleZ=r.Angle.z;return "";
    }
    private bool RunAngleSight(BotComponent b,DoorRunRuntime r)
    {
        var g=r.Door!.Geometry;
        var mouth=VP(g.Center+g.Normal*(-Math.Sign((DP(r.Stack)-g.Center).Dot(g.Normal))*.4))+Vector3.up*1.3f;
        var start=r.Angle+Vector3.up*1.4f;var delta=mouth-start;
        try {
            int count=Physics.RaycastNonAlloc(start,delta.normalized,jumpHits,delta.magnitude,~0,QueryTriggerInteraction.Ignore);
            if(count>=jumpHits.Length)return false;
            for(int i=0;i<count;i++)if(JumpObstacle(jumpHits[i].collider,b))return false;
            return true;
        } finally {Array.Clear(jumpHits,0,jumpHits.Length);}
    }
    private string RunLane(BotComponent b,DoorRunRuntime r,bool back)
    {
        if(r.Door?.Data.Door==null||r.Door.Data.Door.DoorState!=EDoorState.Open)return "run-by-door-not-open";
        if(back&&!RunAngleSight(b,r))return "run-by-angle-sightline-lost";
        return RunVolume(b,back?r.ReturnFrom:r.Stack,back?r.Angle:r.Far);
    }
    private string RunVolume(BotComponent b,Vector3 start,Vector3 end,bool overhead=false)
    {
        var delta=end-start;
        try
        {
            foreach(var point in new[]{start,end})
            {
                int count=Physics.OverlapCapsuleNonAlloc(point+Vector3.up*.35f,point+Vector3.up*(overhead?2.55f:1.55f),.25f,doorOccupants,~0,QueryTriggerInteraction.Ignore);
                if(count>=doorOccupants.Length)return "run-by-clearance-uncertain";
                for(int i=0;i<count;i++)if(JumpObstacle(doorOccupants[i],b))return "run-by-body-leaf-or-occupant-blocked";
                Array.Clear(doorOccupants,0,doorOccupants.Length);
            }
            int hits=Physics.CapsuleCastNonAlloc(start+Vector3.up*.35f,start+Vector3.up*(overhead?2.55f:1.55f),.25f,delta.normalized,jumpHits,delta.magnitude,~0,QueryTriggerInteraction.Ignore);
            if(hits>=jumpHits.Length)return "run-by-clearance-uncertain";
            for(int i=0;i<hits;i++)if(JumpObstacle(jumpHits[i].collider,b))return "run-by-lane-obstructed";
            return NavMesh.Raycast(start,end,out _,NavMesh.AllAreas)?"run-by-lane-navmesh-edge":"";
        }
        finally{Array.Clear(doorOccupants,0,doorOccupants.Length);Array.Clear(jumpHits,0,jumpHits.Length);}
    }
    private static bool OwnsRunPath(DoorRunRuntime r)=>r.Owner!=null&&r.OwnedPath!=null&&ReferenceEquals(r.Owner.Mover.ActivePath,r.OwnedPath)
        &&Vector3.Distance(r.OwnedPath.Destination,r.Policy.Trace?.ReturnAccepted==true?r.Angle:r.Far)<.3f;
    private void EndDoorRun(BotState s,string reason)
    {
        var r=s.RunBy;if(!r.Policy.Active)return;var enemy=r.Owner?.GoalEnemy;
        if(enemy?.IsVisible==true&&enemy.EnemyProfileId==r.Policy.Trace!.Target&&System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(enemy)==r.TargetInstance)r.Policy.Trace.VisualAcquired=true;
        r.Policy.End(reason,Time.time);FinishDoorRun(s);
    }
    private void FinishDoorRun(BotState s)
    {
        var r=s.RunBy;
        if(r.Owner!=null&&r.OwnedPath!=null&&ReferenceEquals(r.Owner.Mover.ActivePath,r.OwnedPath))
        {r.OwnedPath.RequestEndSprint(ESprintUrgency.None,"COMET run-by released");r.OwnedPath.Cancel();if(r.Owner.CurrentAction?.GetType().Name=="SearchAction")r.Owner.Search.Reset();}
        RecordDoorRun(s,true);r.DropReferences();
    }
    private void RefreshDoorRun(BotComponent b,BotState s)
    {
        if(!s.RunBy.Policy.Active)return;
        string gate=RunGate(b,b.GoalEnemy,s);
        if(gate.Length>0)EndDoorRun(s,gate);
        else if(Time.time-s.RunBy.LastCallback>.6f)EndDoorRun(s,"run-by-action-callback-stopped");
    }
    private void RecordDoorRun(BotState s,bool force=false)
    {
        var r=s.RunBy;var t=r.Policy.Trace;if(t==null)return;
        string key=t.State+":"+t.Reason;
        if(!force&&key==r.LogKey&&Time.time-r.LogAt<.5)return;
        r.LogAt=Time.time;r.LogKey=key;
        var json=JsonConvert.SerializeObject(new{schema=1,session,sequence=++sequence,feature="D03",stage="evaluated",decision="DoorRunBy",
            reason=t.Reason,realtime=Time.realtimeSinceStartup,utc=DateTime.UtcNow.ToString("O"),configRevision=config?.Revision??0,
            doorReconConfig=new{chances=config?.DoorJumpPeek.Chances,cooldown=config?.DoorJumpPeek.Cooldown},doorRunByConfig=config?.DoorRunBy,
            profileId=s.ProfileId,name=s.Name,botInstance=s.Id,doorRunBy=t},Diagnostics.Json);
        diagnostics?.Write(json);Api.Decisions.Publish(json,ex=>{if(!subscriberWarned){subscriberWarned=true;Logger.LogWarning(ex.Message);}});
    }
}
