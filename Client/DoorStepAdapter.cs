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

internal sealed class DoorStepRuntime
{
    internal readonly DoorStepPeek Policy=new();
    internal BotComponent? Owner;
    internal DoorWork? Door;
    internal IBotPathData? OwnedPath;
    internal NavMeshPath? Outward;
    internal Vector3 Stack,Point,Look,PreviousEye,PreviousLook,SightEye;
    internal int TargetInstance,Candidate,PreparedPeek;
    internal float OriginalPose,Pose,LastCallback,NextCheck,LastCheck,LogAt=-1000;
    internal bool Fault,Sight,PoseRequested;
    internal string LogKey="";
    internal void DropReferences(){Owner=null;Door=null;OwnedPath=null;Outward=null;PoseRequested=false;}
}

public sealed partial class Plugin
{
    private void TryStartDoorStep(BotState s,DoorJumpRuntime parent)
    {
        var r=s.StepPeek;var b=parent.Owner;var e=b?.GoalEnemy;
        if(config==null||r.Fault||b==null||e==null||parent.Door==null||parent.Policy.Trace==null)return;
        // Consume exactly one continuation roll. D02/D03's opportunity and cooldown are untouched.
        if(!r.Policy.Begin(parent.Policy.Trace,Time.time,jumpRandom.NextDouble(),config.DoorStepPeek))
        {if(r.Policy.Trace?.Reason=="step-continuation-declined")RecordDoorStep(s,true);return;}
        r.Owner=b;r.Door=parent.Door;r.Stack=b.Position;r.TargetInstance=parent.TargetInstance;
        r.OriginalPose=b.Mover.Pose.PoseValue.TargetValue;r.PreparedPeek=0;r.Candidate=0;r.LastCallback=Time.time;r.LastCheck=Time.time;r.Sight=false;
        string gate=StepGate(b,e,s);
        if(gate.Length>0){EndDoorStep(s,gate);return;}
        StopQuiet(b,s,"quiet-door-step-plan");EndAbandonment(s,"search-check-door-step");EndSilentSearch(s,"silent-search-door-step");
        RecordDoorStep(s,true);
    }
    private string StepGate(BotComponent b,Enemy? e,BotState s)
    {
        var r=s.StepPeek;var t=r.Policy.Trace!;
        if(config?.Enabled!=true||!config.DoorApproach.Enabled||!config.DoorStepPeek.Enabled||r.Fault
            ||!NativeAdapter.IsSupportedAi(b)||!b.BotActive||b.IsDead||b.GameEnding||!Authority||Ghost(b.ProfileId))return "step-out-of-scope";
        if(MedicalPostureGuard(b))return "medical-posture-protected";
        if(e==null||!ReferenceEquals(e,b.GoalEnemy)||!e.EnemyKnown||e.KnownPlaces.LastKnownPosition is not Vector3 known
            ||!r.Policy.SameContact(e.EnemyProfileId,DP(known))||e.TimeSinceLastKnownUpdated>config.DoorApproach.ContactLifetime
            ||System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(e)!=r.TargetInstance)return "step-contact-changed-or-expired";
        if(e.IsVisible){t.VisualAcquired=true;return "step-native-visual-acquired";}
        if(s.ActiveLabel!="COMET:Search"||s.Feature!="A01"||s.Decision!="Search"||!b.SAINLayersActive
            ||b.Decision.CurrentCombatDecision!=ECombatDecision.Search||b.CurrentAction?.GetType().Name!="SearchAction")return "step-search-not-owned";
        if(b.EnemyController.VisibleEnemies.Count>0||b.Flashed.IsFlashed||b.BotOwner.Memory.IsUnderFire||b.Suppression.IsSuppressed
            ||s.Fear.Current.Panic||b.Medical.TimeSinceShot<3||!VisibleAdapter.WeaponReady(b)
            ||b.Decision.CurrentSelfDecision!=ESelfActionType.None||b.Decision.CurrentSquadDecision!=ESquadDecision.None
            ||b.DoorOpener.Interacting||b.BotOwner.DoorOpener.Interacting)return "step-native-priority";
        if(r.Door?.Data.Door==null||r.Door.Data.Door.DoorState!=EDoorState.Open)return "step-door-not-open";
        int count=0;
        foreach(var other in b.EnemyController.KnownEnemies)
        {
            if(++count>32)return "step-threat-list-truncated";
            var place=other?.KnownPlaces.LastHeardPlace;if(place==null)continue;
            double age=Time.time-silentHeardAt(place);if(age<0||age>1.2)continue;
            if(place.SoundType is SAINSoundType.FootStep or SAINSoundType.Sprint or SAINSoundType.Jump or SAINSoundType.Land or SAINSoundType.Door or SAINSoundType.DoorBreach
                &&Vector3.Distance(place.Position,VP(r.Door.Geometry.Center))<=4)return "step-heard-threat-at-door";
        }
        return "";
    }
    private bool StepDoorPeek(BotComponent b,Enemy e,BotState s)
    {
        var r=s.StepPeek;r.LastCallback=Time.time;var t=r.Policy.Trace!;
        string gate=StepGate(b,e,s);if(gate.Length>0){EndDoorStep(s,gate);return true;}
        bool atFrame=t.State is "planning" or "preparing" or "outward-pending" or "returning" or "resting";
        bool arrived=Vector3.Distance(b.Position,atFrame?r.Stack:r.Point)<=.12f;
        if(t.State is "outward" or "observing" or "return-pending" or "returning" or "resting")
        {
            if(!DoorStepGeometry.Contains(r.Door!.Geometry,DP(r.Stack),DP(r.Point),DP(b.Position)))
            {EndDoorStep(s,"step-left-verified-lane");return true;}
            if(!OwnsStepPath(r)&&!(b.Mover.ActivePath==null&&r.OwnedPath?.Status==EBotMoveStatus.Complete&&arrived))
            {EndDoorStep(s,"step-path-replaced");return true;}
        }
        if(t.Completed>0&&t.State is "observing" or "return-pending"
            &&!DoorStepGeometry.Different(DP(r.PreviousEye),DP(r.PreviousLook),DP(b.Transform.EyePosition),DP(r.Look)))
        {EndDoorStep(s,"step-actual-angle-already-checked");return true;}
        bool looking=r.Sight&&Time.time-r.LastCheck<=.4f&&Vector3.Distance(r.SightEye,b.Transform.EyePosition)<=.1f&&Vector3.Angle(b.Transform.LookDirection,r.Look-b.Transform.EyePosition)<=20;
        if(!r.Policy.Observe(DP(b.Position),b.Player.PoseLevel,b.Player.MovementContext.IsGrounded,b.Player.IsSprintEnabled||b.Mover.Running,
            arrived,VisibleAdapter.WeaponReady(b),looking,e.IsVisible,e.TimeSinceLastKnownUpdated,Time.time,config!.DoorStepPeek))
        {FinishDoorStep(s);return true;}
        if(t.State=="planning")
        {
            if(r.PreparedPeek!=t.Peek){r.Candidate=0;r.PreparedPeek=t.Peek;r.OwnedPath=null;r.Outward=null;}
            if(!doorBudget.TryBegin(Time.frameCount))return false;
            doorClock.Restart();try{return PrepareDoorStep(b,s);}finally{doorClock.Stop();doorBudget.Charge(doorClock.Elapsed.TotalMilliseconds);}
        }
        if(Time.time>=r.NextCheck||t.State is "outward-pending" or "return-pending")
        {
            if(!doorBudget.TryBegin(Time.frameCount))
            {if(Time.time-r.LastCheck>.4f){EndDoorStep(s,"step-validation-budget-expired");return true;}return false;}
            doorClock.Restart();
            try
            {
                string why=RunVolume(b,r.Stack,r.Point);
                if(why.Length>0){EndDoorStep(s,"step-"+why);return true;}
                r.SightEye=b.Transform.EyePosition;r.Sight=StepSight(b,r.SightEye,r.Look);
                if(t.State=="observing"&&!r.Sight){EndDoorStep(s,"step-view-obstructed");return true;}
                if(t.State is "outward-pending" or "return-pending")
                {
                    bool back=t.State=="return-pending";Vector3 goal=back?r.Stack:r.Point;
                    if(!b.Mover.CanGoToPoint(goal,out var path,true,.05f)
                        ||DoorStepGeometry.Route(r.Door!.Geometry,DP(r.Stack),DP(b.Position),DP(goal),path.corners.Select(DP).ToArray(),path.status==NavMeshPathStatus.PathComplete,config.DoorStepPeek.Distance).Length>0)
                    {EndDoorStep(s,"step-route-invalidated");return true;}
                    if(!b.Mover.WalkToPointByWay(path,true,.08f,false)){EndDoorStep(s,"step-movement-refused");return true;}
                    r.OwnedPath=b.Mover.ActivePath;
                    if(back){r.PreviousEye=b.Transform.EyePosition;r.PreviousLook=r.Look;r.Policy.ReturnStarted(DP(b.Position),Time.time);}
                    else r.Policy.Start(DP(b.Position),Time.time);
                    if(!OwnsStepPath(r)){EndDoorStep(s,"step-path-not-observed");return true;}
                }
                r.LastCheck=Time.time;r.NextCheck=Time.time+.2f;
            }
            finally{doorClock.Stop();doorBudget.Charge(doorClock.Elapsed.TotalMilliseconds);}
        }
        b.Steering.LookToPoint(r.Look);RecordDoorStep(s);return false;
    }
    private bool PrepareDoorStep(BotComponent b,BotState s)
    {
        var r=s.StepPeek;var t=r.Policy.Trace!;var c=config!.DoorStepPeek;var g=r.Door!.Geometry;
        if(Vector3.Distance(b.Position,r.Stack)>.12f||!TryDoorGeometry(r.Door.Data,out var current)
            ||current.State!="Open"||(current.Center-g.Center).Length>.1||Math.Abs(current.Axis.Dot(g.Axis))<.99)
        {EndDoorStep(s,"step-frame-or-door-changed");return true;}
        // Three bounded local candidates, one per call. The second sortie needs a different view.
        if(r.Candidate>=3)
        {if(t.Completed>0)r.Policy.NoUsefulNext(Time.time);else r.Policy.End("step-no-safe-view",Time.time);FinishDoorStep(s);return true;}
        double distance=c.Distance*(1-r.Candidate++*.2);
        var candidate=DoorStepGeometry.Candidate(g,DP(r.Stack),distance);
        double contactSide=Math.Sign((new DoorPoint(t.KnownX,t.KnownY,t.KnownZ)-g.Center).Dot(g.Axis));if(contactSide==0)contactSide=1;
        double lookSide=t.Peek==1?contactSide:-contactSide;
        r.Look=VP(g.Center+g.Normal*(-Math.Sign((DP(r.Stack)-g.Center).Dot(g.Normal))*.6)+g.Axis*(lookSide*Math.Min(.3,g.HalfWidth*.6)))+Vector3.up*1.3f;
        r.Pose=t.Peek>1&&c.VaryPosture?.6f:1f;
        string why="step-no-local-navmesh";
        if(NavMesh.SamplePosition(VP(candidate),out var sample,.12f,NavMesh.AllAreas))
        {
            r.Point=sample.position;
            if(b.Mover.CanGoToPoint(r.Point,out var outward,true,.05f))
            {
                why=DoorStepGeometry.Route(g,DP(r.Stack),DP(b.Position),DP(r.Point),outward.corners.Select(DP).ToArray(),outward.status==NavMeshPathStatus.PathComplete,c.Distance);
                if(why.Length==0)
                {
                    var back=new NavMeshPath();NavMesh.CalculatePath(r.Point,r.Stack,NavMesh.AllAreas,back);
                    why=DoorStepGeometry.Route(g,DP(r.Stack),DP(r.Point),DP(r.Stack),back.corners.Select(DP).ToArray(),back.status==NavMeshPathStatus.PathComplete,c.Distance);
                }
                if(why.Length==0)why=RunVolume(b,r.Stack,r.Point);
                var eye=r.Point+Vector3.up*(r.Pose<1?1.15f:1.55f);
                if(why.Length==0&&!StepSight(b,eye,r.Look)&&r.Pose<1){r.Pose=1;eye=r.Point+Vector3.up*1.55f;}
                if(why.Length==0&&!StepSight(b,eye,r.Look))why="step-no-door-sightline";
                if(why.Length==0&&t.Completed>0&&!DoorStepGeometry.Different(DP(r.PreviousEye),DP(r.PreviousLook),DP(eye),DP(r.Look)))why="step-angle-already-checked";
                if(why.Length==0)
                {
                    r.Outward=outward;
                    r.Policy.Prepared(DP(r.Point),DP(r.Look),r.Pose,c.ObservationMin+jumpRandom.NextDouble()*(c.ObservationMax-c.ObservationMin),
                        c.RestMin+jumpRandom.NextDouble()*(c.RestMax-c.RestMin),t.Peek==1?"check-known-side-of-opening":"check-other-doorway-angle",Time.time);
                    b.Mover.SetTargetPose(r.Pose);r.PoseRequested=true;r.Sight=false;r.LastCheck=Time.time;r.NextCheck=Time.time;
                    RecordDoorStep(s,true);return false;
                }
            }
            else why="step-no-outward-route";
        }
        t.Alternatives=t.Alternatives.Concat(new[]{"Candidate "+r.Candidate+": "+why}).ToArray();RecordDoorStep(s,true);return false;
    }
    private bool StepSight(BotComponent b,Vector3 eye,Vector3 look)
    {
        var delta=look-eye;if(delta.sqrMagnitude<.01f)return false;
        try{
            int n=Physics.RaycastNonAlloc(eye,delta.normalized,jumpHits,delta.magnitude,~0,QueryTriggerInteraction.Ignore);
            if(n>=jumpHits.Length)return false;
            for(int i=0;i<n;i++)if(JumpObstacle(jumpHits[i].collider,b))return false;
            return true;
        }finally{Array.Clear(jumpHits,0,jumpHits.Length);}
    }
    private static bool OwnsStepPath(DoorStepRuntime r)=>r.Owner!=null&&r.OwnedPath!=null&&ReferenceEquals(r.Owner.Mover.ActivePath,r.OwnedPath)
        &&Vector3.Distance(r.OwnedPath.Destination,r.Policy.Trace?.ReturnAccepted==true?r.Stack:r.Point)<.15f;
    private void EndDoorStep(BotState s,string reason)
    {
        var r=s.StepPeek;if(!r.Policy.Active)return;var e=r.Owner?.GoalEnemy;
        if(e?.IsVisible==true&&e.EnemyProfileId==r.Policy.Trace!.Target&&System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(e)==r.TargetInstance)r.Policy.Trace.VisualAcquired=true;
        r.Policy.End(reason,Time.time);FinishDoorStep(s);
    }
    private void FinishDoorStep(BotState s)
    {
        var r=s.StepPeek;var b=r.Owner;
        bool ownSearch=b!=null&&b.CurrentAction?.GetType().Name=="SearchAction"&&s.ActiveLabel=="COMET:Search";
        if(b!=null&&r.OwnedPath!=null&&ReferenceEquals(b.Mover.ActivePath,r.OwnedPath))
        {r.OwnedPath.Cancel();if(ownSearch)b.Search.Reset();}
        // Do not overwrite a pose already selected by a new SAIN action.
        if(ownSearch&&r.PoseRequested&&r.Policy.Trace?.VisualAcquired!=true&&b!.Decision.CurrentSelfDecision==ESelfActionType.None
            &&Math.Abs(b.Mover.Pose.PoseValue.TargetValue-r.Pose)<.01f)
            b.Mover.SetTargetPose(r.OriginalPose);
        RecordDoorStep(s,true);r.DropReferences();
    }
    private void RefreshDoorStep(BotComponent b,BotState s)
    {
        if(!s.StepPeek.Policy.Active)return;string why=StepGate(b,b.GoalEnemy,s);
        if(why.Length>0)EndDoorStep(s,why);else if(Time.time-s.StepPeek.LastCallback>.6f)EndDoorStep(s,"step-action-callback-stopped");
    }
    private void RecordDoorStep(BotState s,bool force=false)
    {
        var r=s.StepPeek;var t=r.Policy.Trace;if(t==null)return;
        string key=t.State+":"+t.Reason+":"+t.Peek;
        if(!force&&key==r.LogKey&&Time.time-r.LogAt<.5f)return;
        r.LogAt=Time.time;r.LogKey=key;
        var json=Diagnostics.Serialize(new{schema=1,session,sequence=++sequence,feature="D04",stage="evaluated",decision="DoorStepPeek",
            reason=t.Reason,realtime=Time.realtimeSinceStartup,utc=DateTime.UtcNow.ToString("O"),configRevision=config?.Revision??0,
            doorStepPeekConfig=config?.DoorStepPeek,profileId=s.ProfileId,name=s.Name,botInstance=s.Id,doorStepPeek=t});
        diagnostics?.Write(json);Api.Decisions.Publish(json,ex=>{if(!subscriberWarned){subscriberWarned=true;Logger.LogWarning(ex.Message);}});
    }
}
