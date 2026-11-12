using System;
using System.Collections.Generic;
using Comet.Core;
using EFT;
using EFT.Interactive;
using SAIN.Components;
using SAIN.SAINComponent.Classes.Mover;
using UnityEngine;
using UnityEngine.AI;

namespace Comet.Client;

public sealed partial class Plugin
{
    private readonly Dictionary<(int,int),List<NavMeshDoorLink>> localDoors=new();
    private BotDoorsController? localDoorSource;
    private int localDoorCursor;
    private float nextDoorDiscovery,nextProbeSummary,doorIndexRetryAt;
    private bool doorIndexWarned;
    private readonly System.Diagnostics.Stopwatch localDoorClock=new();
    private long localDoorRequests,localDoorCandidates;
    private static (int,int) DoorCell(Vector3 point)=>((int)Math.Floor(point.x/16),(int)Math.Floor(point.z/16));
    // Build once per world in bounded slices. Voxel links remain available while the index fills.
    private void UpdateLocalDoors()
    {
        if(Time.time<doorIndexRetryAt)return;
        try{BuildLocalDoors();}
        catch(Exception ex){doorIndexRetryAt=Time.time+5;if(!doorIndexWarned){doorIndexWarned=true;Logger.LogWarning("COMET local door index deferred; voxel doors remain available: "+ex.Message);}}
    }
    private void BuildLocalDoors()
    {
        if(config?.Enabled!=true||!Authority)return;
        if(localDoorSource==null){if(Time.time<nextDoorDiscovery)return;nextDoorDiscovery=Time.time+5;
            localDoorSource=UnityEngine.Object.FindObjectOfType<BotDoorsController>();if(localDoorSource==null)return;}
        var links=localDoorSource._navMeshDoorLinks;
        if(links==null||localDoorCursor>=links.Count||!doorBudget.TryBegin(Time.frameCount,this))return;
        localDoorClock.Restart();
        try {for(int i=0;i<24&&localDoorCursor<links.Count;i++){
            var link=links[localDoorCursor++];if(link==null||link.Door==null)continue;
            if(!TryDoorGeometry(new DoorDataStruct(link),out var geometry))continue;
            var cell=DoorCell(VP(geometry.Center));if(!localDoors.TryGetValue(cell,out var bucket))localDoors[cell]=bucket=new();
            bucket.Add(link);if(localDoorClock.Elapsed.TotalMilliseconds>=.25)break;
        }}finally{localDoorClock.Stop();doorBudget.Charge(localDoorClock.Elapsed.TotalMilliseconds);}
    }
    private IEnumerable<NavMeshDoorLink> NearbyDoors(BotComponent bot)
    {
        localDoorRequests++;var seen=new HashSet<NavMeshDoorLink>();int count=0;
        var voxel=bot.BotOwner.VoxelesPersonalData.CurVoxel;
        if(voxel?.DoorLinks!=null)foreach(var link in voxel.DoorLinks){
            if(link==null||!seen.Add(link))continue;localDoorCandidates++;yield return link;if(++count>=48)yield break;
        }
        var cell=DoorCell(bot.Position);
        for(int x=-1;x<=1;x++)for(int z=-1;z<=1;z++)if(localDoors.TryGetValue((cell.Item1+x,cell.Item2+z),out var bucket))
            foreach(var link in bucket){if(link==null||!seen.Add(link))continue;localDoorCandidates++;yield return link;if(++count>=48)yield break;}
    }
    private static DoorPoint[] RememberedDoorRoute(BotComponent bot,Vector3 known)
    {
        var path=bot.GoalEnemy?.Path.PathToEnemy;
        if(path?.status==NavMeshPathStatus.PathComplete){var points=path.corners;
            if(points.Length>=2&&points.Length<=128&&Vector3.Distance(points[0],bot.Position)<=4&&Vector3.Distance(points[points.Length-1],known)<=2)
            {var route=new DoorPoint[points.Length];for(int i=0;i<route.Length;i++)route[i]=DP(points[i]);return route;}}
        // Only a bearing to remembered information. The actual stack route is still separately checked.
        return new[]{DP(bot.Position),DP(known)};
    }
    private void FlushTacticalProbes(bool force=false)
    {
        if(!force&&Time.time<nextProbeSummary)return;nextProbeSummary=Time.time+15;
        diagnostics?.Write(Diagnostics.Serialize(new{schema=1,session,stage="summary",kind="tactical-probes",time=Time.time,
            probes=TacticalProbeSummary(),localDoorRequests,localDoorCandidates,indexedDoorLinks=localDoorCursor,
            doorIndexComplete=localDoorSource!=null&&localDoorCursor>=(localDoorSource._navMeshDoorLinks?.Count??0)}));
    }
    private void ClearLocalDoors()
    {
        localDoors.Clear();localDoorSource=null;localDoorCursor=0;nextDoorDiscovery=nextProbeSummary=doorIndexRetryAt=0;doorIndexWarned=false;
        localDoorRequests=localDoorCandidates=0;clearanceQueries=clearanceRetries=clearanceUnknown=clearanceBlocked=clearanceIgnored=0;
    }
}
