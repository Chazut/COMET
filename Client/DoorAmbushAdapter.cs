using System;
using System.Collections.Generic;
using System.Linq;
using Comet.Core;
using EFT;
using HarmonyLib;
using Newtonsoft.Json;
using SAIN.Components;
using SAIN.Models.Enums;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.EnemyClasses;
using SAIN.SAINComponent.Classes.Mover;
using UnityEngine;
using UnityEngine.AI;

namespace Comet.Client;

internal sealed class DoorAmbushRuntime
{
    internal readonly DoorAmbush Policy=new();
    internal readonly List<DoorWork> Work=new();
    internal DoorWork? Chosen;
    internal DoorCandidate? Best;
    internal DoorHoldTrace? Selection;
    internal double CurrentProtection;
    internal bool CurrentChecked;
    internal Vector3 PlanningOrigin;
    internal BotComponent? Owner;
    internal IBotPathData? Path;
    internal Vector3 Aim;
    internal float Pose,OldPose,AppliedPose,NextCheck,LastCheck,LastCallback,ArrivedAt,LogAt=-1000,NextPlan;
    internal int TargetInstance;
    internal bool FeintChecked,FeintStarted,OwnPose,Fault;
    internal string LogKey="";
    internal void Drop(){Work.Clear();Chosen=null;Best=null;Owner=null;Path=null;FeintChecked=FeintStarted=OwnPose=false;Selection=null;CurrentChecked=false;}
}

public sealed partial class Plugin
{
    private static void PatchDoorAmbush(Harmony h)
    {
        var type=typeof(BotComponent).Assembly.GetType("SAIN.Layers.Combat.Solo.FreezeAction",true);
        h.Patch(AccessTools.Method(type,"Update")??throw new MissingMethodException("D09 FreezeAction.Update"),
            prefix:new HarmonyMethod(typeof(Plugin),nameof(AmbushHoldStep)));
        h.Patch(AccessTools.Method(typeof(SAINSteeringClass),"SteerByPriority"),
            prefix:new HarmonyMethod(typeof(Plugin),nameof(AmbushSteering)));
    }
    private static bool AmbushHoldStep(object __instance)
    {
        var p=Instance;BotState? state=null;
        try
        {
            var b=actionBot.GetValue(__instance) as BotComponent;
            if(p?.config==null||b==null||!p.states.TryGetValue(b,out state))return true;
            if(!p.GuardDoorReservation(b,state))return true;
            p.RefreshDoorInterruption(b,state);
            if(!p.GuardDoorReservation(b,state))return true;
            if(state.Emergency.Policy.Active)return p.StepDoorEmergency(b,state,"FreezeAction");
            bool fallback=state.Interruption.Policy.Pending?p.StepDoorResume(b,state):state.Support!=null?p.StepDoorSupport(b,state):p.StepDoorAmbush(b,state);
            return p.CompleteDoorReservationStep(b,state,fallback);
        }
        catch(Exception ex)
        {if(state!=null){if(state.Interruption.Policy.Pending||state.Interruption.Policy.Resumed)p?.AbandonDoorResume(state,"door-resume-adapter-error");p?.EndDoorSupport(state,"support-adapter-error");state.Ambush.Fault=true;p?.EndDoorAmbush(state,"ambush-adapter-error");p?.ReleaseDoorReservation(state,"reservation-adapter-error");}p?.Logger.LogWarning("COMET door ambush yielded: "+ex.Message);return true;}
    }
    private static bool AmbushSteering(SAINSteeringClass __instance,ref bool __result)
    {
        var p=Instance;var b=__instance.Bot;
        if(p?.config!=null && p.states.TryGetValue(b,out var guarded) && !p.GuardDoorReservation(b,guarded))return true;
        if(p?.config!=null && p.states.TryGetValue(b,out var support) && p.SupportSteer(b,support)){__result=true;return false;}
        if(p?.config==null||!p.states.TryGetValue(b,out var s)||!s.Ambush.Policy.Active
            ||s.Ambush.Policy.Trace?.AngleVerified!=true||s.Fake.Policy.Active||p.AmbushGate(b,s).Length>0)return true;
        __instance.LookToPoint(s.Ambush.Aim);__result=true;return false;
    }
    private double AmbushHeld(BotState s)=>config!.Progress.Enabled&&!s.ProgressUnavailable?s.Progress.Current.HoldSeconds:0;
    private double AmbushLimit(BotState s)=>config!.Progress.Enabled&&!s.ProgressUnavailable?config.Progress.HoldLimit:config.HoldLimit;
    private DoorApproachConfig AmbushApproach()
    {
        var a=config!.DoorApproach;
        return new(){Enabled=a.Enabled,DoorDistance=config.DoorAmbush.DoorDistance,ContactDistance=a.ContactDistance,
            UnseenDelay=a.UnseenDelay,ContactLifetime=Math.Min(a.ContactLifetime,config.ContactLifetime),ApproachLimit=a.ApproachLimit};
    }
    private string AmbushGate(BotComponent b,BotState s)
    {
        if(config?.Enabled!=true||!config.DoorAmbush.Enabled||!config.DoorApproach.Enabled||s.Ambush.Fault||!FakeScope(b)||b.IsDead)return "ambush-out-of-scope";
        if(s.Feature!="A01"||s.Decision!="Hold"||s.ActiveLabel!="COMET:Hold"||b.Decision.CurrentCombatDecision!=ECombatDecision.Freeze
            ||!b.SAINLayersActive||b.CurrentAction?.GetType().Name!="FreezeAction")return "ambush-hold-not-owned";
        var e=b.GoalEnemy;
        if(e==null||!e.EnemyKnown||e.KnownPlaces.LastKnownPosition==null)return "ambush-contact-forgotten";
        if(e.IsVisible||b.EnemyController.VisibleEnemies.Count>0)return "ambush-visual-contact";
        if(b.Flashed.IsFlashed||b.BotOwner.Memory.IsUnderFire||b.Suppression.IsSuppressed||s.Fear.Current.Panic||b.Medical.TimeSinceShot<3
            ||b.Grenade.GrenadeDangerPoint!=null||b.Decision.CurrentSelfDecision!=ESelfActionType.None||b.Decision.CurrentSquadDecision!=ESquadDecision.None
            ||b.BotOwner.DoorOpener.Interacting||b.DoorOpener.Interacting||b.Player.IsInPronePose||!b.Player.MovementContext.IsGrounded)return "ambush-native-priority";
        if(!s.Fake.Policy.Active&&!FakeWeaponReady(b))return "ambush-weapon-unavailable";
        if(s.Ambush.Policy.Active&&(e.EnemyProfileId!=s.Ambush.Policy.Trace!.Target))return "ambush-target-changed";
        return "";
    }
    private bool KeepDoorAmbush(BotComponent b,BotState s,ref ECombatDecision decision,ref bool result)
    {
        if(!s.Ambush.Policy.Active)return false;
        RefreshFear(b,s);RefreshProgress(b,s);RefreshDoorAmbush(b,s);
        if(!s.Ambush.Policy.Active)return false;
        s.Expected=ECombatDecision.Freeze;s.RequestedFrame=Time.frameCount;decision=s.Expected;result=true;return true;
    }
    private void RefreshDoorAmbush(BotComponent b,BotState s)
    {
        var r=s.Ambush;if(!r.Policy.Active)return;
        string gate=AmbushGate(b,s);var e=b.GoalEnemy;
        if(gate.Length>0){EndDoorAmbush(s,gate);return;}
        if(Time.time-r.LastCallback>.6){EndDoorAmbush(s,"ambush-action-callback-stopped");return;}
        if(!r.Policy.Observe(Time.time,e.TimeSinceLastKnownUpdated,e.EnemyKnown,DP(e.KnownPlaces.LastKnownPosition!.Value),AmbushHeld(s),AmbushLimit(s),config!.DoorApproach.ApproachLimit))
            EndDoorAmbush(s,r.Policy.Trace!.Reason);
    }
    private bool StepDoorAmbush(BotComponent b,BotState s)
    {
        var r=s.Ambush;if(s.Interruption.Policy.Pending)return true;r.LastCallback=Time.time;
        string gate=AmbushGate(b,s);
        if(gate.Length>0){EndDoorAmbush(s,gate);return true;}
        var e=b.GoalEnemy;var known=e.KnownPlaces.LastKnownPosition!.Value;
        if(!r.Policy.Active)
        {
            if(Time.time<r.NextPlan||s.Fake.Policy.Active||s.RoomClear.Policy.Active||s.Door.Policy.Active||s.Door.Policy.Planning||s.Jump.Policy.Active||s.RunBy.Policy.Active||s.StepPeek.Policy.Active)return true;
            r.NextPlan=Time.time+2;
            if((e.Seen?e.TimeSinceSeen:Time.time)<config!.DoorApproach.UnseenDelay)return true;
            if(!r.Policy.Begin(e.EnemyProfileId,s.Situation?.ContactSource??"sain-last-known",b.Info.Personality.ToString(),DP(known),e.TimeSinceLastKnownUpdated,
                Time.time,jumpRandom.NextDouble(),Math.Min(config!.ContactLifetime,config.DoorApproach.ContactLifetime),AmbushHeld(s),AmbushLimit(s),config.DoorAmbush))return true;
            r.Drop();r.Owner=b;r.TargetInstance=System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(e);r.LastCheck=Time.time;r.NextCheck=0;
            r.PlanningOrigin=b.Position;PlanAmbush(b,s,known);RecordDoorAmbush(s,true);
        }
        RefreshDoorAmbush(b,s);if(!r.Policy.Active)return true;
        var t=r.Policy.Trace!;
        if(t.State=="planning")return PlanAmbushStep(b,s);
        if(t.State=="approaching")
        {
            if(r.Path?.PathStatus!=NavMeshPathStatus.PathComplete){EndDoorAmbush(s,"ambush-route-invalidated");return true;}
            bool complete=r.Path?.Status==EBotMoveStatus.Complete;
            bool owns=OwnAmbushPath(r)||(b.Mover.ActivePath==null&&complete);
            r.Policy.Movement(DP(b.Position),complete,owns,Time.time);
            if(!r.Policy.Active){EndDoorAmbush(s,t.Reason);return true;}
            if(t.Arrived){r.ArrivedAt=Time.time;r.OldPose=b.Mover.Pose.PoseValue.TargetValue;r.OwnPose=true;b.Mover.SetTargetPose(r.Pose);r.AppliedPose=r.Pose;}
        }
        if(Time.time>=r.NextCheck)
        {
            if(!doorBudget.TryBegin(Time.frameCount,b))
            {if(Time.time-r.LastCheck>.5){EndDoorAmbush(s,"ambush-validation-budget-expired");return true;}}
            else
            {
                doorClock.Restart();
                try
                {
                    string invalid=ValidateAmbushPoint(b,r.Chosen!,VP(new(t.X,t.Y,t.Z)),false,out _,out _,out _,out _,false);
                    if(invalid.Length>0){EndDoorAmbush(s,invalid);return true;}
                    if((t.Arrived||t.RetainedPosition)&&!s.Fake.Policy.Active)
                    {
                        bool eyes=ClearAmbushRay(b.Transform.EyePosition,r.Aim),gun=ClearAmbushRay(b.Transform.WeaponRoot,r.Aim);
                        // One posture correction, never shuffle or alternate crouch/stand every frame.
                        if((!eyes||!gun)&&Time.time-r.ArrivedAt>.35&&r.Pose<1)
                        {r.Pose=1;t.Pose=1;b.Mover.SetTargetPose(1);r.AppliedPose=1;}
                        r.Policy.Angle(eyes,gun,Time.time);
                        if(!r.Policy.Active){EndDoorAmbush(s,t.Reason);return true;}
                    }
                }
                finally{doorClock.Stop();doorBudget.Charge(doorClock.Elapsed.TotalMilliseconds);}
                r.NextCheck=Time.time+.25f;r.LastCheck=Time.time;
            }
        }
        if(t.Arrived||t.RetainedPosition)
        {
            if(!r.Policy.MaintainPosition(DP(b.Position))){EndDoorAmbush(s,t.Reason);return true;}
            // Do not stomp a path another action supplied after our arrival.
            if(b.Mover.ActivePath!=null&&!OwnAmbushPath(r)&&!(t.RetainedPosition&&b.Mover.ActivePath.Status==EBotMoveStatus.Complete)){EndDoorAmbush(s,"ambush-route-replaced");return true;}
            if(!r.FeintChecked&&t.AngleVerified)
            {
                r.FeintChecked=true;
                if(config!.DoorAmbush.AllowFeint&&TryStartDoorFake(b,e,s,r.Chosen!,true))
                {r.FeintStarted=true;t.Feint=s.Fake.Policy.Trace!.Kind;t.FeintResult="started";
                    t.AngleVerified=t.EyesClear=t.MuzzleClear=false;t.State="settling";t.Reason="ambush-feint-preparation";}
                else t.FeintResult=config!.DoorAmbush.AllowFeint?"not-selected-or-unavailable":"disabled";
            }
            if(r.FeintStarted)
            {
                RefreshDoorFake(b,s);
                if(!s.Fake.Policy.Active)
                {r.FeintStarted=false;t.FeintResult=s.Fake.Policy.Trace?.Reason??"ended";
                    if(s.Fake.Policy.Trace?.State!="completed"||!FakeWeaponReady(b)){EndDoorAmbush(s,"ambush-feint-not-completed");return true;}}
            }
            if(!s.Fake.Policy.Active){b.Mover.SetTargetPose(r.Pose);r.AppliedPose=r.Pose;b.Steering.LookToPoint(r.Aim);}
        }
        RecordDoorAmbush(s);return false;
    }
    private void PlanAmbush(BotComponent b,BotState s,Vector3 known)
    {
        var r=s.Ambush;var t=r.Policy.Trace!;
        var route=RememberedDoorRoute(b,known);var options=new List<DoorWork>();int visited=0;
        foreach(var link in NearbyDoors(b))
        {
            if(++visited>48)break;t.DoorsScanned++;if(link?.Door==null)continue;
            var w=new DoorWork{Data=new(link)};if(!TryDoorGeometry(w.Data,out var g)){t.Candidates.Add(new(){Reason="door geometry unavailable"});continue;}w.Geometry=g;
            string gate=DoorPolicy.Geometry(g,DP(b.Position),DP(known),route,AmbushApproach());
            if(gate.Length==0)options.Add(w);else t.Candidates.Add(new(){Door=g.Id,Reason=gate});
        }
        t.DoorsEligible=options.Count;t.PlanningInput=options.Count>0?"eligible local doors":visited>=48?"no eligible door within scan budget":"no eligible local door on contact route";
        var relevant=options.OrderBy(w=>(w.Geometry.Center-DP(b.Position)).Length).ThenBy(w=>w.Geometry.Id,StringComparer.Ordinal).Take(3).ToArray();
        // Check the actual position first, once per relevant door. Do not request a fake zero-length move.
        foreach(var w in relevant)r.Work.Add(new(){Data=w.Data,Geometry=w.Geometry,Side=0,Variant=-1});
        foreach(var w in relevant)
            foreach(int variant in new[]{0,1,2,3})foreach(int side in new[]{-1,1})r.Work.Add(new(){Data=w.Data,Geometry=w.Geometry,Side=side,Variant=variant});
    }
    private bool PlanAmbushStep(BotComponent b,BotState s)
    {
        var r=s.Ambush;var t=r.Policy.Trace!;
        if(!doorBudget.TryBegin(Time.frameCount,b)){r.Policy.Preparation.Defer(Time.time,Time.deltaTime);return true;}
        doorClock.Restart();
        try
        {
            if(!r.CurrentChecked)
            {r.CurrentProtection=HoldProtection(b.Position,b.GoalEnemy.KnownPlaces.LastKnownPosition!.Value);r.CurrentChecked=true;}
            if(Vector3.Distance(r.PlanningOrigin,b.Position)>.5f)
            {EndDoorAmbush(s,"hold-door-origin-moved-reconsider");return true;}
            if(r.Work.Count>0&&r.Best==null)
            {
                var w=r.Work[0];r.Work.RemoveAt(0);
                if(DoorBusy(b,s,w.Geometry.Id)){t.Candidates.Add(new(){Door=w.Geometry.Id,Reason="reservation-door-busy"});return true;}
                if(w.Variant==-1)return TryRetainDoorHold(b,s,w);
                var raw=VP(DoorPolicy.Stack(w.Geometry,DP(b.Position),w.Side,w.Variant));
                string why=ValidateAmbushPoint(b,w,raw,true,out var path,out var point,out var aim,out var pose,true);
                var candidate=new DoorCandidate{Door=w.Geometry.Id,Side=w.Side<0?"left":"right",X=point.x,Y=point.y,Z=point.z,Eligible=why.Length==0,Reason=why};
                if(candidate.Eligible)
                {
                    candidate.PathLength=Length(path!.corners);
                    var selection=EvaluateDoorHold(b,s,point,candidate.PathLength,true,false);selection.Door=w.Geometry.Id;
                    candidate.Score=DoorHoldSelection.Rank(selection.CandidateProtection,candidate.PathLength);
                    candidate.Eligible=selection.Outcome=="move";candidate.Reason=selection.Reason;
                    if(!s.ProgressUnavailable&&s.Progress.Blocked(ProgressAdapter.Destination(point),Time.time)){candidate.Eligible=false;candidate.Reason="ambush-progress-retry-blocked";}
                    if(candidate.Eligible&&(r.Best==null||candidate.Score<r.Best.Score))
                    {r.Best=candidate;r.Chosen=w;r.Aim=aim;r.Pose=pose;r.Selection=selection;r.Work.Clear();}
                    else if(r.Selection==null)r.Selection=selection;
                }
                t.Candidates.Add(candidate);return true;
            }
            if(r.Chosen==null||r.Best==null)
            {FinishDoorHoldChoice(b,s,"hold-door-no-safe-angle");return true;}
            string refusal=ValidateAmbushPoint(b,r.Chosen,VP(r.Best.Point),true,out var final,out var destination,out var look,out var stance,true);
            if(refusal.Length>0){FinishDoorHoldChoice(b,s,refusal);return true;}
            // Time, current protection and route length may have changed while spreading work over frames.
            r.Selection=EvaluateDoorHold(b,s,destination,Length(final!.corners),true,false);r.Selection.Door=r.Chosen.Geometry.Id;
            if(r.Selection.Outcome!="move"){FinishDoorHoldChoice(b,s,r.Selection.Reason);return true;}
            r.Best.X=destination.x;r.Best.Y=destination.y;r.Best.Z=destination.z;r.Aim=look;r.Pose=stance;
            if(!AcquireDoorReservation(b,s,r.Chosen,"ambush:approaching",ReservationDeadline(b,s,true))){EndDoorAmbush(s,"reservation-unavailable",false);return true;}
            r.Policy.Prepare(r.Best,DP(look),stance,r.Chosen.Geometry.State);RecordDoorHold(s);
            StopQuiet(b,s,"quiet-door-ambush");
            bool accepted=b.Mover.WalkToPointByWay(final!,true,.4f,false);r.Policy.Request(DP(b.Position),accepted);r.Path=b.Mover.ActivePath;
            if(!accepted||!OwnAmbushPath(r)){EndDoorAmbush(s,"ambush-native-route-not-observed");return true;}
            r.NextCheck=Time.time+.25f;r.LastCheck=Time.time;RecordDoorAmbush(s,true);return false;
        }
        finally{doorClock.Stop();doorBudget.Charge(doorClock.Elapsed.TotalMilliseconds);}
    }
    private string ValidateAmbushPoint(BotComponent b,DoorWork w,Vector3 raw,bool calculate,out NavMeshPath? path,out Vector3 point,out Vector3 aim,out float pose,bool projected)
    {
        string why=ValidateDoor(b,w,DP(raw),calculate,out path,out point,AmbushApproach(),true);
        pose=b.Info.Personality.ToString()=="Rat"?.2f:b.Info.Personality.ToString()=="SnappingTurtle"?.4f:.7f;
        var normal=VP(w.Geometry.Normal)*Math.Sign((DP(b.Position)-w.Geometry.Center).Dot(w.Geometry.Normal));
        // Watch the corridor-side mouth, not the hidden enemy or a point through a closed leaf.
        aim=VP(w.Geometry.Center)+normal*.65f+Vector3.up*1.2f;
        if(why.Length>0)return why;
        if(!TryDoorGeometry(w.Data,out var current)||current.State!=w.Geometry.State)return "ambush-door-changed-reconsider";
        if(projected)
        {
            var sampled=point;var target=aim;
            bool View(float p)=>ClearAmbushRay(sampled+Vector3.up*(.65f+p*.8f),target)&&ClearAmbushRay(sampled+Vector3.up*(.8f+p*.8f),target);
            if(!View(pose)){pose=1;if(!View(pose))return "ambush-eyes-or-muzzle-blocked";}
        }
        return "";
    }
    private static bool ClearAmbushRay(Vector3 from,Vector3 to)=>!Physics.Linecast(from,to,LayersMaskController.HighPolyWithTerrainMask,QueryTriggerInteraction.Ignore);
    private static bool OwnAmbushPath(DoorAmbushRuntime r)=>r.Owner!=null&&r.Path!=null&&ReferenceEquals(r.Owner.Mover.ActivePath,r.Path)
        &&r.Policy.Trace is DoorAmbushTrace t&&Vector3.Distance(r.Path.Destination,new((float)t.X,(float)t.Y,(float)t.Z))<=.3f;
    private void EndDoorAmbush(BotState s,string reason,bool reconsider=true)
    {
        var r=s.Ambush;if(r.Policy.Trace==null||!r.Policy.Active&&r.Owner==null)return;
        r.Policy.End(reason,reason=="ambush-visual-contact");
        if(OwnAmbushPath(r))r.Path!.Cancel();
        if(r.Owner!=null&&!r.Owner.IsDead&&r.OwnPose&&Math.Abs(r.Owner.Mover.Pose.PoseValue.TargetValue-r.AppliedPose)<.01)
            r.Owner.Mover.SetTargetPose(r.OldPose);
        if(r.FeintStarted)EndDoorFake(s,"ambush-ended:"+reason);
        // A completed/invalidated ambush asks the existing selector for another feasible action.
        if(reconsider)s.Engine.Fail(Tactic.Hold,Time.time,reason);
        if(r.Selection!=null)r.Selection.ValidFor=reconsider?0:1.5;
        RecordDoorAmbush(s,true);RecordDoorHold(s);r.Drop();
    }
    private void RecordDoorAmbush(BotState s,bool force=false)
    {
        var r=s.Ambush;var t=r.Policy.Trace;if(t==null)return;string key=t.State+":"+t.Reason+":"+t.Feint+":"+t.FeintResult+":"+t.AngleVerified;
        if(!force&&key==r.LogKey&&(!r.Policy.Active||Time.time-r.LogAt<.5f))return;r.LogKey=key;r.LogAt=Time.time;
        var json=Diagnostics.Serialize(new{schema=1,session,sequence=++sequence,feature="D09",stage="evaluated",decision="DoorAmbush",reason=t.Reason,
            realtime=Time.realtimeSinceStartup,utc=DateTime.UtcNow.ToString("O"),configRevision=config?.Revision??0,planningWaitSeconds=r.Policy.Preparation.Seconds,profileId=s.ProfileId,name=s.Name,botInstance=s.Id,
            doorAmbush=t,doorHold=r.Selection,doorAmbushConfig=config?.DoorAmbush});
        diagnostics?.Write(json);Api.Decisions.Publish(json,ex=>{if(!subscriberWarned){subscriberWarned=true;Logger.LogWarning(ex.Message);}});
    }
}
