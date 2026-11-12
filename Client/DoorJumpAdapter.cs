using System;
using System.Linq;
using Comet.Core;
using EFT;
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

internal sealed class DoorJumpRuntime
{
    internal readonly DoorJumpPeek Policy=new();
    internal BotComponent? Owner;
    internal DoorWork? Door;
    internal IBotPathData? OwnedPath;
    internal NavMeshPath? Outward,Return;
    internal Vector3 Stack,Landing,Look;
    internal int TargetInstance,ValidationStep;
    internal float NextCheck,LastCheck,LastCallback,OpenedAt,LogAt=-1000;
    internal string LogKey="";
    internal bool Fault;
    internal void DropReferences(){Owner=null;Door=null;OwnedPath=null;Outward=null;Return=null;}
}

public sealed partial class Plugin
{
    private readonly RaycastHit[] jumpHits=new RaycastHit[24];
    private readonly System.Random jumpRandom=new();

    private void TryStartDoorJump(BotComponent b,Enemy e,BotState s,DoorWork w)
    {
        TryStartDoorRecon(b,e,s,w);
    }
    private string JumpGate(BotComponent b,Enemy? e,BotState s)
    {
        if(config?.Enabled!=true||!config.DoorJumpPeek.Enabled||!config.DoorApproach.Enabled||s.Jump.Fault
            ||!NativeAdapter.IsSupportedAi(b)||!b.BotActive||b.IsDead||b.GameEnding||!Authority||Ghost(b.ProfileId))return "peek-out-of-scope";
        if(MedicalPostureGuard(b))return "medical-posture-protected";
        if(e==null||!ReferenceEquals(e,b.GoalEnemy)||!e.EnemyKnown||e.KnownPlaces.LastKnownPosition is not Vector3 known
            ||!s.Jump.Policy.SameContact(e.EnemyProfileId,DP(known))||e.TimeSinceLastKnownUpdated>config.DoorApproach.ContactLifetime)return "peek-contact-changed-or-expired";
        if(e.IsVisible){s.Jump.Policy.Trace!.VisualAcquired=true;return "peek-native-visual-acquired";}
        if(!TacticalDoorSearch(b,s))return "peek-search-not-owned";
        if(b.EnemyController.VisibleEnemies.Count>0||b.Flashed.IsFlashed||b.BotOwner.Memory.IsUnderFire||b.Suppression.IsSuppressed
            ||s.Fear.Current.Panic||b.Medical.TimeSinceShot<3||(!VisibleAdapter.WeaponReady(b)&&!JumpOwnOpening(b,s))
            ||b.Decision.CurrentSelfDecision!=ESelfActionType.None||b.Decision.CurrentSquadDecision!=ESquadDecision.None)return "peek-native-priority";
        if((b.DoorOpener.Interacting||b.BotOwner.DoorOpener.Interacting)&&s.Jump.Policy.Trace!.State!="opening")return "peek-other-door-interaction";
        return "";
    }
    private bool StepDoorJump(BotComponent b,Enemy e,BotState s)
    {
        var r=s.Jump;r.LastCallback=Time.time;
        string gate=JumpGate(b,e,s);if(gate.Length>0){EndDoorJump(s,gate);return true;}
        var c=config!.DoorJumpPeek;var t=r.Policy.Trace!;
        bool returning=t.State.StartsWith("return",StringComparison.Ordinal);
        Vector3 destination=returning?r.Stack:r.Landing,direction=destination-b.Position;direction.y=0;
        var velocity=b.Player.Velocity;var flatVelocity=velocity;flatVelocity.y=0;
        bool arrived=Vector3.Distance(b.Position,destination)<=.4f;
        bool grounded=b.Player.MovementContext.IsGrounded;
        // Native completion clears ActivePath. Accept that only for our completed route at its actual endpoint.
        if(t.MovementAccepted&&t.State!="return-pending"&&!OwnsJumpPath(r)
            &&!(b.Mover.ActivePath==null&&r.OwnedPath?.Status==EBotMoveStatus.Complete&&direction.magnitude<=.5f))
        {EndDoorJump(s,"peek-path-replaced");return true;}
        if(t.MovementAccepted&&!OnJumpLane(r,b.Position)){EndDoorJump(s,"peek-left-verified-lane");return true;}
        if(!r.Policy.Observe(DP(b.Position),Vector3.Dot(flatVelocity,direction.normalized),b.Player.Physical.Stamina.NormalValue*100,
            grounded,arrived,e.IsVisible,e.TimeSinceLastKnownUpdated,Time.time,c,velocity.y))
        {FinishDoorJump(s);return !s.StepPeek.Policy.Active;}
        RecordDoorJump(s);
        if(t.State=="opening")
        {
            // The animation belongs to the game. Do not change the leaf or cancel the interaction on release.
            if(r.Door!.Data.Door.DoorState==EDoorState.Open&&Time.time-r.OpenedAt>=1.25f
                &&!JumpInteractionState(b)&&!b.DoorOpener.Interacting&&!b.BotOwner.DoorOpener.Interacting)
            {r.Policy.Opened(Time.time);r.ValidationStep=0;RecordDoorJump(s,true);}
            return false;
        }
        if(t.State=="validating")
        {
            if(!doorBudget.TryBegin(Time.frameCount,b))return false;
            doorClock.Restart();
            try
            {
                if(r.ValidationStep==0)
                {
                    string invalid=PrepareJumpRoutes(b,r);
                    if(invalid.Length>0){EndDoorJump(s,invalid);return true;}
                    if(r.Door!.Data.Door.DoorState==EDoorState.Shut)
                    {
                        if(!RequestJumpDoorOpen(b,r)){EndDoorJump(s,"peek-native-open-refused");return true;}
                        r.OpenedAt=Time.time;r.Policy.Opening(Time.time);RecordDoorJump(s,true);return false;
                    }
                    r.Policy.Trace!.DoorOpenObserved=true;r.ValidationStep=1;return false;
                }
                string why=JumpLane(b,r,true);
                if(why.Length>0){EndDoorJump(s,why);return true;}
                if(!grounded||!b.Player.MovementContext.CanJump||t.Stamina<c.MinimumStamina){EndDoorJump(s,"peek-jump-resources-unavailable");return true;}
                // A verified walking route supplies momentum. No teleport, speed multiplier or forced jump animation.
                bool accepted=b.Mover.WalkToPointByWay(r.Outward!,true,.2f,false);
                r.OwnedPath=b.Mover.ActivePath;
                if(!accepted||!OwnsJumpPath(r)){EndDoorJump(s,"peek-native-movement-refused");return true;}
                r.Policy.Start(DP(b.Position),Time.time);r.NextCheck=Time.time+.2f;r.LastCheck=Time.time;
                RecordDoorJump(s,true);return false;
            }
            finally{doorClock.Stop();doorBudget.Charge(doorClock.Elapsed.TotalMilliseconds);}
        }
        if(r.Door!.Data.Door.DoorState!=EDoorState.Open){EndDoorJump(s,"peek-door-state-changed");return true;}
        bool wants=r.Policy.MayJump(b.Player.MovementContext.CanJump,grounded,direction.magnitude,Time.time,c);
        bool needsCheck=Time.time>=r.NextCheck||wants||t.State=="return-pending";
        if(needsCheck)
        {
            if(!doorBudget.TryBegin(Time.frameCount,b))
            {if(Time.time-r.LastCheck>.4){EndDoorJump(s,"peek-validation-budget-expired");return true;}return false;}
            doorClock.Restart();string invalid;
            try{invalid=JumpLane(b,r,t.State=="run-up"||t.State=="returning"&&c.ReturnJump||t.State=="return-pending"&&c.ReturnJump);}
            finally{doorClock.Stop();doorBudget.Charge(doorClock.Elapsed.TotalMilliseconds);}
            if(invalid.Length>0){EndDoorJump(s,invalid);return true;}
            r.NextCheck=Time.time+.2f;r.LastCheck=Time.time;
        }
        if(wants)
        {
            r.Policy.JumpAccepted(ControlledJump(b,s,"D02",returning?"door-return":"door-peek",direction,destination,c.MinimumSpeed,c.MinimumStamina,true,Time.time-r.LastCheck),Time.time);RecordDoorJump(s,true);
            if(!r.Policy.Active){FinishDoorJump(s);return !s.StepPeek.Policy.Active;}
        }
        if(t.State=="return-pending")
        {
            // Rebuild the return from the observed landing, not the planned position.
            doorClock.Restart();
            try {
            if(!b.Mover.CanGoToPoint(r.Stack,out var path,true,.1f)
                ||DoorJumpGeometry.Route(r.Door.Geometry,DP(b.Position),DP(r.Stack),DP(r.Stack),path.corners.Select(DP).ToArray(),path.status==NavMeshPathStatus.PathComplete).Length>0)
            {EndDoorJump(s,"peek-return-route-invalidated");return true;}
            if(!b.Mover.WalkToPointByWay(path,true,.2f,false)){EndDoorJump(s,"peek-return-movement-refused");return true;}
            r.Policy.ReturnStarted(Time.time);r.OwnedPath=b.Mover.ActivePath;
            if(!OwnsJumpPath(r)){EndDoorJump(s,"peek-return-path-not-observed");return true;}
            RecordDoorJump(s,true);
            } finally {doorClock.Stop();doorBudget.Charge(doorClock.Elapsed.TotalMilliseconds);}
        }
        // Look only at the dated contact. SAIN's normal vision must independently acquire any enemy.
        b.Steering.LookToPoint(r.Look);return false;
    }
    private string PrepareJumpRoutes(BotComponent b,DoorJumpRuntime r)
    {
        if(r.Door==null||!TryDoorGeometry(r.Door.Data,out var g)||g.State!="Open"&&g.State!="Shut")return "peek-door-not-openable";
        if((g.Center-r.Door.Geometry.Center).Length>.1||Math.Abs(g.Axis.Dot(r.Door.Geometry.Axis))<.99)return "peek-door-frame-changed";
        if(Vector3.Distance(b.Position,r.Stack)>.35f)return "peek-stack-position-changed";
        var raw=DoorJumpGeometry.Landing(g,DP(r.Stack));
        if(!NavMesh.SamplePosition(VP(raw),out var sample,.2f,NavMesh.AllAreas))return "peek-no-landing-navmesh";
        r.Landing=sample.position;
        if(!b.Mover.CanGoToPoint(r.Landing,out var outward,true,.1f))return "peek-no-outward-route";
        string why=DoorJumpGeometry.Route(g,DP(b.Position),raw,DP(r.Landing),outward.corners.Select(DP).ToArray(),outward.status==NavMeshPathStatus.PathComplete);
        if(why.Length>0)return why;
        var back=new NavMeshPath();NavMesh.CalculatePath(r.Landing,r.Stack,NavMesh.AllAreas,back);
        why=DoorJumpGeometry.Route(g,DP(r.Landing),DP(r.Stack),DP(r.Stack),back.corners.Select(DP).ToArray(),back.status==NavMeshPathStatus.PathComplete);
        if(why.Length>0)return "peek-no-return-route:"+why;
        r.Outward=outward;r.Return=back;
        var t=r.Policy.Trace;if(t!=null){t.StackX=r.Stack.x;t.StackY=r.Stack.y;t.StackZ=r.Stack.z;t.LandingX=r.Landing.x;t.LandingY=r.Landing.y;t.LandingZ=r.Landing.z;}
        return "";
    }
    private static bool RequestJumpDoorOpen(BotComponent b,DoorJumpRuntime r)
    {
        var data=r.Door!.Data;var door=data.Door;data.ManualUpdate(b.Position);
        if(door.DoorState!=EDoorState.Shut||!door.Operatable||!door.enabled||!door.gameObject.activeInHierarchy
            ||!data.Link.ShallInteract()||!data.InRangeToInteract(door)||!data.CanInteractByTime(Time.time))return false;
        // The official SAIN helper assumes its private selected-door list is populated. Use the same
        // public native interaction API, without touching that list or forcing door/network state.
        b.Player.MovementContext.ResetCanUsePropState();
        var result=Door.Interact(b.Player,EInteractionType.Open);
        if(!result.Succeeded)return false;
        b.Player.ExecuteInteraction(door,result.Value);return true;
    }
    private static bool JumpInteractionState(BotComponent b)=>b.Player.CurrentState.Name is EPlayerState.Open or EPlayerState.DoorInteraction or EPlayerState.Approach;
    private static bool JumpOwnOpening(BotComponent b,BotState s)=>s.Jump.Policy.Active&&s.Jump.Policy.Trace?.State=="opening"
        &&ReferenceEquals(s.Jump.Owner,b)&&s.Jump.Door?.Data.Door!=null&&Time.time-s.Jump.OpenedAt<=4.5f;
    private string JumpLane(BotComponent b,DoorJumpRuntime r,bool overhead)
    {
        if(r.Door?.Data.Door==null||r.Door.Data.Door.DoorState!=EDoorState.Open)return "peek-door-not-open";
        // Conservative continuous volume: standing body plus 1 m of possible vertical travel,
        // along the entire corridor lane. Reject uncertainty; never infer clearance from P10's roof ray.
        return TacticalVolume(b,r.Stack,r.Landing,overhead,1).Replace("run-by-","peek-");
    }
    private static bool JumpObstacle(Collider col,BotComponent b)=>col!=null&&!ReferenceEquals(col.GetComponentInParent<Player>(),b.Player);
    private static bool OnJumpLane(DoorJumpRuntime r,Vector3 position)
    {
        return DoorJumpGeometry.ContainsMotion(r.Door!.Geometry,DP(r.Stack),DP(r.Landing),DP(position));
    }
    private static bool OwnsJumpPath(DoorJumpRuntime r)
    {
        if(r.Owner==null||r.OwnedPath==null||!ReferenceEquals(r.Owner.Mover.ActivePath,r.OwnedPath))return false;
        bool back=r.Policy.Trace?.State.StartsWith("return",StringComparison.Ordinal)==true;
        return Vector3.Distance(r.OwnedPath.Destination,back?r.Stack:r.Landing)<.3f;
    }
    private void EndDoorJump(BotState s,string reason)
    {
        if(!s.Jump.Policy.Active)return;
        // Capture a real acquisition even when the decision-manager release precedes the next Search tick.
        var enemy=s.Jump.Owner?.GoalEnemy;
        if(enemy?.IsVisible==true&&enemy.EnemyProfileId==s.Jump.Policy.Trace!.Target)
            s.Jump.Policy.Trace.VisualAcquired=true;
        s.Jump.Policy.End(reason,Time.time);FinishDoorJump(s);
    }
    private void FinishDoorJump(BotState s)
    {
        var r=s.Jump;
        // Physical jump/interaction is never cancelled. Only stop our path if no other action replaced it.
        if(r.Owner!=null&&r.OwnedPath!=null&&ReferenceEquals(r.Owner.Mover.ActivePath,r.OwnedPath)
            &&!(s.MedicalPosture.Policy.Blocking&&!r.Owner.Player.MovementContext.IsGrounded))
        {r.OwnedPath.Cancel();if(r.Owner.CurrentAction?.GetType().Name=="SearchAction")r.Owner.Search.Reset();}
        RecordDoorJump(s,true);
        if(r.Policy.Trace?.State=="completed"&&r.Policy.Trace.Returned)TryStartDoorStep(s,r);
        r.DropReferences();
    }
    private void RefreshDoorJump(BotComponent b,BotState s)
    {
        if(!s.Jump.Policy.Active)return;
        string gate=JumpGate(b,b.GoalEnemy,s);
        if(gate.Length>0)EndDoorJump(s,gate);
        else if(Time.time-s.Jump.LastCallback>.6f)EndDoorJump(s,"peek-action-callback-stopped");
    }
    private void RecordDoorJump(BotState s,bool force=false)
    {
        var r=s.Jump;var t=r.Policy.Trace;if(t==null)return;
        string key=t.State+":"+t.Reason;
        if(!force&&key==r.LogKey&&Time.time-r.LogAt<.5)return;
        r.LogAt=Time.time;r.LogKey=key;
        var json=Diagnostics.Serialize(new{schema=1,session,sequence=++sequence,feature="D02",stage="evaluated",decision="DoorJumpPeek",
            reason=t.Reason,realtime=Time.realtimeSinceStartup,utc=DateTime.UtcNow.ToString("O"),configRevision=config?.Revision??0,
            doorJumpPeekConfig=config?.DoorJumpPeek,profileId=s.ProfileId,name=s.Name,botInstance=s.Id,doorJumpPeek=t});
        diagnostics?.Write(json);Api.Decisions.Publish(json,ex=>{if(!subscriberWarned){subscriberWarned=true;Logger.LogWarning(ex.Message);}});
    }
}
