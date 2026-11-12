using System;
using Comet.Core;
using EFT;
using Newtonsoft.Json;
using SAIN.Components;
using SAIN.Models.Enums;
using SAIN.SAINComponent.Classes.Mover;
using UnityEngine;

namespace Comet.Client;

public sealed partial class Plugin
{
    // Three nearby solid-cover samples against remembered contact only. No hidden live transform.
    private static double HoldProtection(Vector3 point,Vector3 known)
    {
        if(!DP(point).Finite||!DP(known).Finite)return 0;
        int protectedSamples=0;
        foreach(float height in new[]{.55f,1f,1.4f})
        {
            var origin=point+Vector3.up*height;var toward=known+Vector3.up*1.2f-origin;
            if(Physics.Raycast(origin,toward.normalized,out var hit,Math.Min(2,toward.magnitude),LayersMaskController.HighPolyWithTerrainMask,QueryTriggerInteraction.Ignore)
                &&hit.collider!=null&&hit.collider.attachedRigidbody==null&&hit.collider.GetComponentInParent<EFT.Interactive.Door>()==null)protectedSamples++;
        }
        return protectedSamples/3d;
    }
    private DoorHoldTrace EvaluateDoorHold(BotComponent b,BotState s,Vector3 point,double length,bool safe,bool angle)
    {
        var t=s.Ambush.Policy.Trace!;var known=b.GoalEnemy.KnownPlaces.LastKnownPosition!.Value;
        return DoorHoldSelection.Evaluate(angle,HoldProtection(b.Position,known),safe,HoldProtection(point,known),length,
            t.Remaining,t.WaitDuration,config!.DoorAmbush.MinimumUsefulWait,config.DoorApproach.ApproachLimit);
    }
    private bool TryRetainDoorHold(BotComponent b,BotState s,DoorWork w)
    {
        var r=s.Ambush;var point=b.Position;
        string why=ValidateAmbushPoint(b,w,point,false,out _,out _,out var aim,out _,false);
        bool stationary=b.Mover.ActivePath==null||b.Mover.ActivePath.Status==EBotMoveStatus.Complete;
        bool eyes=ClearAmbushRay(b.Transform.EyePosition,aim),muzzle=ClearAmbushRay(b.Transform.WeaponRoot,aim);
        bool valid=why.Length==0;
        r.Policy.Trace!.Candidates.Add(new(){Door=w.Geometry.Id,Side="current",X=point.x,Y=point.y,Z=point.z,Eligible=valid,
            Reason=valid?"hold-door-current-angle-good":why.Length>0?why:"hold-door-current-angle-unusable"});
        if(!valid)return true;
        var choice=EvaluateDoorHold(b,s,point,0,true,true);choice.Door=w.Geometry.Id;
        if(choice.Outcome!="stay")return true;
        if(!AcquireDoorReservation(b,s,w,"ambush:holding",ReservationDeadline(b,s,true)))return true;
        var candidate=new DoorCandidate{Door=w.Geometry.Id,Side="current",X=point.x,Y=point.y,Z=point.z,Eligible=true};
        r.Pose=b.Mover.Pose.PoseValue.TargetValue;r.OldPose=r.Pose;r.OwnPose=true;r.AppliedPose=r.Pose;r.Policy.Prepare(candidate,DP(aim),r.Pose,w.Geometry.State);
        if(!r.Policy.Retain(DP(point),eyes,muzzle,Time.time)){EndDoorAmbush(s,"hold-door-retain-not-confirmed");return true;}
        r.Selection=choice;r.Chosen=w;r.Aim=aim;r.Work.Clear();r.ArrivedAt=Time.time;r.LastCheck=Time.time;r.NextCheck=Time.time+.25f;
        StopQuiet(b,s,"quiet-door-hold-retained");RecordDoorHold(s);RecordDoorAmbush(s,true);return false;
    }
    private void FinishDoorHoldChoice(BotComponent b,BotState s,string reason)
    {
        var r=s.Ambush;var previous=r.Selection;
        var choice=EvaluateDoorHold(b,s,b.Position,0,false,false);
        // Preserve rejected route estimates for diagnostics, while rechecking whether native Hold is safe now.
        if(previous!=null)
        {choice.PathLength=previous.PathLength;choice.EstimatedTravel=previous.EstimatedTravel;choice.UsefulWait=previous.UsefulWait;choice.CandidateProtection=previous.CandidateProtection;choice.Door=previous.Door;}
        choice.Reason=reason=="hold-door-no-safe-angle"&&previous?.Outcome!="move"&&previous!=null?previous.Reason:reason;
        r.Selection=choice;
        EndDoorAmbush(s,choice.Reason,choice.Outcome!="native-hold");
    }
    private void RecordDoorHold(BotState s)
    {
        var t=s.Ambush.Selection;if(t==null)return;
        var json=Diagnostics.Serialize(new{schema=1,session,sequence=++sequence,feature="D10",stage="evaluated",decision="DoorHold",reason=t.Reason,
            realtime=Time.realtimeSinceStartup,utc=DateTime.UtcNow.ToString("O"),configRevision=config?.Revision??0,profileId=s.ProfileId,name=s.Name,botInstance=s.Id,
            doorHold=t});
        diagnostics?.Write(json);Api.Decisions.Publish(json,ex=>{if(!subscriberWarned){subscriberWarned=true;Logger.LogWarning(ex.Message);}});
    }
}
