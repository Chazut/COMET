using System;
using Comet.Core;
using EFT;
using EFT.Interactive;
using Newtonsoft.Json;
using SAIN.Components;
using SAIN.SAINComponent.Classes.Mover;
using UnityEngine;

namespace Comet.Client;

public sealed partial class Plugin
{
    // Reused locally; never retain colliders or world objects between checks/raids.
    private readonly Collider[] spatialPanels=new Collider[32];
    private static bool TryDoorGeometry(DoorDataStruct d,out DoorGeometry g)
    {
        g=new();if(d.Door==null||d.Link==null)return false;
        double bottom=d.Link.BottomY;
        if(d.Link.name?.StartsWith("DoorLink_Custom_",StringComparison.Ordinal)==true)
        {
            // Waypoints builds world-space endpoints but leaves BottomY at its zero
            // default. Read the physical leaf's foot, independent of its rotation.
            bottom=d.Link.Open1.y;
            var panels=d.Door.CollisionColliders;
            bool found=false;
            if(panels!=null&&panels.Count<=16)foreach(var panel in panels)
                if(panel!=null&&panel.enabled&&panel.gameObject.activeInHierarchy&&!panel.isTrigger)
                { double y=panel.bounds.min.y;if(!double.IsNaN(y)&&!double.IsInfinity(y)){bottom=found?Math.Min(bottom,y):y;found=true;} }
        }
        return DoorSpatial.Decode(d.Door.Id??d.Link.Id.ToString(),d.Door.DoorState.ToString(),d.Link.name??"",
            DP(d.Link.Close1),DP(d.Link.Close2_Normal),DP(d.Link.Open1),DP(d.Link.Open2),bottom,out g);
    }
    // Called inside the existing bounded door planning/revalidation budget. This guards
    // tactical stopping positions; it must not prevent native traversal of the threshold.
    private string ValidateDoorPanels(BotComponent b,DoorWork w,DoorGeometry g,Vector3 point)
    {
        var cfg=config!.DoorSpatial;
        var trace=new DoorSpatialTrace{Door=g.Id,DoorState=g.State,Source=g.Source,Margin=cfg.Clearance,X=point.x,Y=point.y,Z=point.z};
        string reason=DoorSpatial.Candidate(g,DP(point));
        try
        {
            if(reason.Length==0&&!cfg.Enabled)
            {
                reason=DoorSpatial.SegmentDistance(DP(point),g.OpenA,g.OpenB)<cfg.Clearance?"geometry-legacy-leaf-clearance":"";
                trace.Source+="-legacy";
            }
            else if(reason.Length==0)
            {
                var panels=w.Data.Door.CollisionColliders;
                bool available=false;
                if(panels!=null&&panels.Count<=16)foreach(var panel in panels)
                    if(panel!=null&&panel.enabled&&panel.gameObject.activeInHierarchy&&!panel.isTrigger){available=true;break;}
                // Without the selected door's active physical panel, an empty query is not proof of safety.
                if(!available)
                    reason="geometry-panel-unavailable";
                else
                {
                    int count=Physics.OverlapCapsuleNonAlloc(point+Vector3.up*.4f,point+Vector3.up*1.45f,
                        (float)cfg.Clearance,spatialPanels,(int)LayersMaskController.PlayerStaticCollisionsMask,QueryTriggerInteraction.Ignore);
                    if(count>=spatialPanels.Length)reason="geometry-query-saturated";
                    else for(int i=0;i<count;i++)
                    {
                        var col=spatialPanels[i];if(col==null)continue;
                        var door=col.GetComponentInParent<Door>();
                        if(door==null)continue;
                        trace.Blocker=door.Id??"unknown";trace.BlockerState=door.DoorState.ToString();
                        reason="geometry-live-panel-clearance";break;
                    }
                }
            }
        }
        catch(Exception){reason="geometry-query-failed";}
        finally{Array.Clear(spatialPanels,0,spatialPanels.Length);}
        trace.State=reason.Length==0?"clear":"rejected";trace.Reason=reason.Length==0?"geometry-candidate-clear":reason;
        RecordDoorSpatial(b,trace);return reason;
    }
    private string CombatDoorPanel(BotComponent b,Vector3 point)
    {
        if(config?.DoorSpatial.Enabled!=true)return "";
        try
        {
            int n=Physics.OverlapCapsuleNonAlloc(point+Vector3.up*.4f,point+Vector3.up*1.45f,(float)config.DoorSpatial.Clearance,
                spatialPanels,(int)LayersMaskController.PlayerStaticCollisionsMask,QueryTriggerInteraction.Ignore);
            if(n>=spatialPanels.Length)return "geometry-query-saturated";
            for(int i=0;i<n;i++)if(spatialPanels[i]?.GetComponentInParent<Door>() is Door door)
            {
                RecordDoorSpatial(b,new(){State="rejected",Reason="geometry-combat-step-panel",Door=door.Id,DoorState=door.DoorState.ToString(),
                    Source="native",Blocker=door.Id,BlockerState=door.DoorState.ToString(),Margin=config.DoorSpatial.Clearance,X=point.x,Y=point.y,Z=point.z});
                return "geometry-combat-step-panel";
            }
            return "";
        }
        catch{return "geometry-query-failed";}
        finally{Array.Clear(spatialPanels,0,spatialPanels.Length);}
    }
    private void RecordDoorSpatial(BotComponent b,DoorSpatialTrace t)
    {
        if(!states.TryGetValue(b,out var s))return;
        RecordDoorSpatial(s,t);
    }
    private void InvalidDoorGeometry(BotComponent b,DoorDataStruct data,DoorPoint point)
    {
        if(!point.Finite)point=new();
        RecordDoorSpatial(b,new(){Door=string.IsNullOrEmpty(data.Door?.Id)?"unknown":data.Door!.Id,DoorState=data.Door?.DoorState.ToString()??"unknown",
            Source=data.Link?.name??"unknown",Margin=config?.DoorSpatial.Clearance??.55,Reason="geometry-invalid-frame",X=point.X,Y=point.Y,Z=point.Z,
            RawBottom=data.Link?.BottomY,RawClose1=data.Link==null?null:DP(data.Link.Close1),RawClose2=data.Link==null?null:DP(data.Link.Close2_Normal),
            RawOpen1=data.Link==null?null:DP(data.Link.Open1),RawOpen2=data.Link==null?null:DP(data.Link.Open2)});
    }
    private void EndDoorSpatial(BotState s,string reason)
    {
        if(s.SpatialTrace==null)return;
        var t=s.SpatialTrace;t.State="ended";t.ValidFor=0;t.Reason=reason;
        RecordDoorSpatial(s,t,true);s.SpatialTrace=null;s.SpatialKey="";s.SpatialAt=-1000;
    }
    private void RecordDoorSpatial(BotState s,DoorSpatialTrace t,bool force=false)
    {
        // Pure detached observations. Identical results are bounded to two per second.
        string key=t.Door+":"+t.DoorState+":"+t.Reason+":"+t.Blocker+":"+t.Source+":"+t.X.ToString("F1",System.Globalization.CultureInfo.InvariantCulture)+":"+t.Z.ToString("F1",System.Globalization.CultureInfo.InvariantCulture);
        if(!force&&s.SpatialKey==key&&Time.time-s.SpatialAt<.5f)return;
        s.SpatialTrace=t;
        s.SpatialKey=key;s.SpatialAt=Time.time;
        var json=JsonConvert.SerializeObject(new{schema=1,session,sequence=++sequence,feature="C03",stage="evaluated",decision="DoorGeometry",reason=t.Reason,
            realtime=Time.realtimeSinceStartup,utc=DateTime.UtcNow.ToString("O"),configRevision=config?.Revision??0,doorSpatialConfig=config?.DoorSpatial,
            profileId=s.ProfileId,name=s.Name,botInstance=s.Id,doorSpatial=t},Diagnostics.Json);
        diagnostics?.Write(json);Api.Decisions.Publish(json,ex=>{if(!subscriberWarned){subscriberWarned=true;Logger.LogWarning(ex.Message);}});
    }
}
