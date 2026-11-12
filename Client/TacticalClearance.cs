using System;
using Comet.Core;
using EFT;
using EFT.Interactive;
using SAIN.Components;
using UnityEngine;
using UnityEngine.AI;

namespace Comet.Client;

public sealed partial class Plugin
{
    private readonly Collider[] clearanceSmall=new Collider[24],clearanceLarge=new Collider[128];
    private readonly RaycastHit[] clearanceCastSmall=new RaycastHit[24],clearanceCastLarge=new RaycastHit[128];
    private long clearanceQueries,clearanceRetries,clearanceUnknown,clearanceBlocked,clearanceIgnored;
    private static int TacticalCollisionMask=>(int)LayersMaskController.PlayerStaticCollisionsMask
        |LayersMaskController.HighPolyWithTerrainMask|LayersMaskController.PlayerMask|LayersMaskController.PlayerStaticDoorMask;

    private string TacticalVolume(BotComponent b,Vector3 start,Vector3 end,bool overhead,float pose)
    {
        if(!ValidPoint(start)||!ValidPoint(end)||!float.IsFinite(pose))return "run-by-invalid-volume";
        var delta=end-start;
        if(delta.sqrMagnitude>.0001f && NavMesh.Raycast(start,end,out _,NavMesh.AllAreas))
            return "run-by-lane-navmesh-edge";
        if(overhead && (Physics.Raycast(start+Vector3.up*1.7f,Vector3.up,.7f,LayersMaskController.HighPolyWithTerrainMask)
            || Physics.Raycast(end+Vector3.up*1.7f,Vector3.up,.7f,LayersMaskController.HighPolyWithTerrainMask)))
            return "run-by-no-headroom";
        return "";
    }
    private bool TacticalSightline(BotComponent b,Vector3 start,Vector3 end)
    {
        if(!ValidPoint(start)||!ValidPoint(end))return false;
        clearanceQueries++;
        return !Physics.Linecast(start,end,LayersMaskController.HighPolyWithTerrainMask);
    }
    private string TacticalDoorClearance(BotComponent b,Vector3 lower,Vector3 upper,float radius,Door door,Vector3 travel)
    {
        if(!ValidPoint(lower)||!ValidPoint(upper)||!ValidPoint(travel)||!float.IsFinite(radius)||radius<=0)return "invalid-volume";
        bool sweep=travel.sqrMagnitude>.0001f;
        try
        {
            clearanceQueries++;var overlaps=clearanceSmall;var casts=clearanceCastSmall;
            int Query()=>sweep?Physics.CapsuleCastNonAlloc(lower,upper,radius,travel.normalized,casts,travel.magnitude,TacticalCollisionMask,QueryTriggerInteraction.Ignore)
                :Physics.OverlapCapsuleNonAlloc(lower,upper,radius,overlaps,TacticalCollisionMask,QueryTriggerInteraction.Ignore);
            int count=Query();
            if(count==24){clearanceRetries++;overlaps=clearanceLarge;casts=clearanceCastLarge;count=Query();}
            for(int i=0;i<count;i++)
            {
                var collider=sweep?casts[i].collider:overlaps[i];
                if(JumpObstacle(collider,b)&&!ReferenceEquals(collider.GetComponentInParent<Door>(),door)){clearanceBlocked++;return "blocked";}
                clearanceIgnored++;
            }
            if(count==overlaps.Length){clearanceUnknown++;return "incomplete-after-retry";}
            return "";
        }
        finally
        {
            Array.Clear(clearanceSmall,0,clearanceSmall.Length);Array.Clear(clearanceLarge,0,clearanceLarge.Length);
            Array.Clear(clearanceCastSmall,0,clearanceCastSmall.Length);Array.Clear(clearanceCastLarge,0,clearanceCastLarge.Length);
        }
    }
    private object TacticalProbeSummary()=>new{clearanceQueries,clearanceRetries,clearanceUnknown,clearanceBlocked,clearanceIgnored,
        queued=doorBudget.PendingCount,deferred=doorBudget.Deferred,smallCapacity=24,retryCapacity=128,mask=TacticalCollisionMask};
}
