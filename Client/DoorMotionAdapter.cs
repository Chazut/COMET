using System;
using System.Collections.Generic;
using Comet.Core;
using Comet.Api;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using HarmonyLib;
using Newtonsoft.Json;
using SAIN.Components;
using UnityEngine;

namespace Comet.Client;

internal sealed class DoorSwing
{
    internal Door Door=null!;
    internal BotComponent Bot=null!;
    internal BotState State=null!;
    internal float From,To,Start,NextLog;
    internal long Generation;
    internal WorldInteractiveObject.InteractionState? Interaction;
    internal string Action="unjam";
}
internal sealed class DoorMotionWatch
{
    internal Vector3 Anchor;
    internal float Since,Next,Attempt;
}

public sealed partial class Plugin
{
    private readonly List<DoorSwing> doorSwings=new();
    private readonly Dictionary<BotComponent,DoorMotionWatch> motionWatches=new();
    private readonly Collider[] swingHits=new Collider[32];
    private readonly Stack<DoorSwing?> pushContexts=new();
    private static void PatchDoorMotion(Harmony h)
    {
        h.Patch(AccessTools.Method(typeof(WorldInteractiveObject),"InitializeSmoothOpenInteraction"),
            prefix:new HarmonyMethod(typeof(Plugin),nameof(PushBegin)),finalizer:new HarmonyMethod(typeof(Plugin),nameof(PushEnd)));
        h.Patch(AccessTools.Method(typeof(WorldInteractiveObject.InteractionState),"InitSmoothOpen",
            new[]{typeof(EDoorState),typeof(float),typeof(float),typeof(AnimationCurve),typeof(bool)}),
            prefix:new HarmonyMethod(typeof(Plugin),nameof(PushAngle)){priority=Priority.Last});
    }
    private bool DoorSyncReady=>!fikaPresent||DoorMotionNetwork.Ready?.Invoke()==true;
    private bool MotionScoped(BotComponent b)=>config?.Enabled==true&&config.DoorMotion.Enabled&&Authority
        &&NativeAdapter.IsSupportedAi(b)&&b.BotActive&&!b.IsDead&&!b.GameEnding&&!Ghost(b.ProfileId);
    private static void PushBegin(WorldInteractiveObject __instance,EDoorState __0,out bool __state)
    {
        __state=false;var p=Instance;if(p==null)return;
        // Stack scope survives nested opens and exceptions; no global flip flag leaking into another actor's door.
        p.pushContexts.Push(null);__state=true;
        try
        {
            if(__0!=EDoorState.Open||p.config?.DoorMotion.PushAway!=true||!p.DoorSyncReady||__instance is not Door door
                ||door.GetType()!=typeof(Door)||door.InteractingPlayer is not EFT.Player player)return;
            if(p.doorSwings.Count+p.pushContexts.Count>16||p.doorSwings.Exists(x=>x.Door==door))return;
            foreach(var pending in p.pushContexts)if(pending?.Door==door)return;
            foreach(var pair in p.states)
            {
                if(pair.Key.Player!=player||!p.MotionScoped(pair.Key))continue;
                if(Math.Abs(DoorMotion.Delta(door.GetAngle(EDoorState.Shut),door.CurrentAngle))>5||p.RecoveryBusy(pair.Key,pair.Value,door,true))return;
                if(!p.LeafFrame(door,out var local,out var half))return;
                var shut=door.GetDoorRotation(door.GetAngle(EDoorState.Shut))*door.transform.parent.rotation*local;
                var open=door.GetDoorRotation(door.GetAngle(EDoorState.Open))*door.transform.parent.rotation*local;
                var normal=new Vector3(-shut.z,0,shut.x).normalized;
                if(Vector3.Dot(open,normal)<0)normal=-normal;
                if(Vector3.Dot(player.Position-door.transform.position,normal)<=0)return;
                float to=(float)DoorMotion.Away(door.GetAngle(EDoorState.Shut),door.GetAngle(EDoorState.Open));
                if(!p.SwingClear(door,door.CurrentAngle,to,pair.Key,false))
                {p.RecordDoorMotion(pair.Value,door,"rejected","motion-opposite-swing-obstructed","push-away",to);return;}
                p.pushContexts.Pop();p.pushContexts.Push(new(){Door=door,Bot=pair.Key,State=pair.Value,From=door.CurrentAngle,To=to,Start=Time.time,Action="push-away"});return;
            }
        }
        catch(Exception ex){p.Logger.LogWarning("COMET C04 push check: "+ex.Message);}
    }
    private static void PushAngle(EDoorState state,ref float resultAngle)
    {
        var p=Instance;if(state==EDoorState.Open&&p?.pushContexts.Count>0&&p.pushContexts.Peek() is { } swing)resultAngle=swing.To;
    }
    private static Exception? PushEnd(bool __state,Exception? __exception)
    {
        var p=Instance;if(!__state||p==null||p.pushContexts.Count==0)return __exception;
        var swing=p.pushContexts.Pop();
        if(swing!=null&&__exception==null)
        {
            // Observe the actual interaction. Fika snapshots are queued until Update, after its original interaction packet.
            swing.Interaction=(WorldInteractiveObject.InteractionState)recoveryInteraction.GetValue(swing.Door)!;
            swing.Generation=p.recoveryEpochs.GetValue(swing.Door,_=>new()).Generation;
            if(p.doorSwings.Count<16&&!p.doorSwings.Exists(x=>x.Door==swing.Door))p.doorSwings.Add(swing);
            p.RecordDoorMotion(swing.State,swing.Door,"requested","motion-push-away-requested",swing.Action,swing.To);
        }
        return __exception;
    }
    private bool LeafFrame(Door door,out Vector3 local,out float half)
    {
        local=default;half=0;if(door.transform.parent==null)return false;
        var panels=door.CollisionColliders;if(panels==null||panels.Count>16)return false;
        foreach(var panel in panels)
        {
            if(panel==null||!panel.enabled||panel.isTrigger||!panel.gameObject.activeInHierarchy)continue;
            local=Quaternion.Inverse(door.transform.rotation)*(panel.bounds.center-door.transform.position);
            var flat=local;flat.y=0;half=flat.magnitude;if(half>=.15f&&half<=2)return true;
        }
        return false;
    }
    // Sweep the whole rotating leaf, not only its destination. Queries ignore its own collider and include actors.
    private bool SwingClear(Door door,float from,float to,BotComponent owner,bool protectHuman)
    {
        if(!LeafFrame(door,out var local,out var half))return false;
        var players=Singleton<GameWorld>.Instance?.AllAlivePlayersList;if(players==null||players.Count>256)return false;
        if(protectHuman)foreach(var player in players)
            if(player!=null&&!player.IsAI&&Vector3.Distance(player.Position,door.transform.position)<config!.DoorMotion.HumanClearance)return false;
        int steps=Math.Max(1,(int)Math.Ceiling(Math.Abs(to-from)/10));
        var q0=door.transform.rotation;var baseRotation=door.transform.parent.rotation;
        try
        {
            for(int i=1;i<=steps;i++)
            {
                var q=door.GetDoorRotation((float)DoorMotion.At(from,to,i,steps))*baseRotation;
                for(int j=1;j<=3;j++)
                {
                    var flat=local;flat.y=0;var offset=q*(flat*(j*.6f));
                    var point=door.transform.position+offset;point.y=door.transform.position.y+.9f;
                    int count=Physics.OverlapCapsuleNonAlloc(point-Vector3.up*.5f,point+Vector3.up*.5f,.12f,swingHits,
                        LayersMaskController.PlayerStaticDoorMask,QueryTriggerInteraction.Ignore);
                    if(count>=swingHits.Length)return false;
                    for(int k=0;k<count;k++)if(swingHits[k]!=null&&swingHits[k].GetComponentInParent<Door>()!=door)return false;
                    foreach(var player in players)
                    {
                        if(player==null||Math.Abs(player.Position.y-point.y) >2)continue;
                        var diff=player.Position-point;diff.y=0;
                        if(diff.sqrMagnitude<.16f)
                        {
                            var before=door.transform.position+q0*(flat*(j*.6f));before.y=point.y;
                            var prior=player.Position-before;prior.y=0;
                            if(player==owner.Player&&diff.sqrMagnitude>=prior.sqrMagnitude+.0001f)continue;
                            return false;
                        }
                    }
                }
            }
            return true;
        }
        finally{Array.Clear(swingHits,0,swingHits.Length);}
    }
    private void UpdateDoorMotion()
    {
        if(doorSwings.Count>1){var first=doorSwings[0];doorSwings.RemoveAt(0);doorSwings.Add(first);}
        for(int i=doorSwings.Count-1;i>=0;i--)
        {
            var swing=doorSwings[i];var door=swing.Door;
            try
            {
                if(door==null||!MotionScoped(swing.Bot)||!DoorSyncReady||RecoveryBusy(swing.Bot,swing.State,door,true)
                    ||door.DoorState is EDoorState.Locked or EDoorState.Shut
                    ||!ReferenceEquals(swing.Interaction,recoveryInteraction.GetValue(door))
                    ||recoveryEpochs.GetValue(door,_=>new()).Generation!=swing.Generation)
                {if(door!=null)RecordDoorMotion(swing.State,door,"ended","motion-ownership-ended",swing.Action,swing.To);doorSwings.RemoveAt(i);continue;}
                bool done;
                if(swing.Action=="unjam")
                {
                    if(!doorBudget.TryBegin(Time.frameCount))continue;
                    float angle=(float)DoorMotion.At(swing.From,swing.To,Time.time-swing.Start,config!.DoorMotion.SwingDuration);
                    var timer=System.Diagnostics.Stopwatch.StartNew();bool clear;
                    try{clear=SwingClear(door,door.CurrentAngle,angle,swing.Bot,true);}
                    finally{timer.Stop();doorBudget.Charge(timer.Elapsed.TotalMilliseconds);}
                    if(!clear)
                    {RecordDoorMotion(swing.State,door,"ended","motion-sweep-became-occupied",swing.Action,swing.To);doorSwings.RemoveAt(i);continue;}
                    if(!DoorMotionNetwork.Align(door,EDoorState.Open,angle))
                    {RecordDoorMotion(swing.State,door,"ended","motion-newer-interaction",swing.Action,swing.To);doorSwings.RemoveAt(i);continue;}
                    done=Time.time-swing.Start>=config.DoorMotion.SwingDuration;
                    if(done)DoorMotionNetwork.Completed(door);
                }
                else done=Math.Abs(DoorMotion.Delta(door.CurrentAngle,swing.To))<=.5;
                if(Time.time>=swing.NextLog||done)
                {
                    DoorMotionNetwork.Publish(door);
                    RecordDoorMotion(swing.State,door,done?"confirmed":"moving",done?"motion-angle-observed":"motion-angle-changing",swing.Action,swing.To);
                    swing.NextLog=Time.time+.1f;
                }
                if(done||Time.time-swing.Start>5)doorSwings.RemoveAt(i);
            }
            catch(Exception ex){Logger.LogWarning("COMET C04 motion stopped: "+ex.Message);doorSwings.RemoveAt(i);}
        }
        foreach(var pair in states)
        {
            var b=pair.Key;if(!MotionScoped(b)||b.Mover.ActivePath==null||!b.Mover.ActivePath.Moving)
            {motionWatches.Remove(b);continue;}
            if(!motionWatches.TryGetValue(b,out var watch))motionWatches[b]=watch=new(){Anchor=b.Position,Since=Time.time};
            if(Vector3.Distance(watch.Anchor,b.Position)>.2f){watch.Anchor=b.Position;watch.Since=Time.time;}
            if(Time.time-watch.Since<config!.DoorMotion.StuckDelay||Time.time<watch.Next||Time.time<watch.Attempt)continue;
            watch.Next=Time.time+.25f;
            if(!DoorSyncReady||doorSwings.Count>=16||!doorBudget.TryBegin(Time.frameCount))continue;
            var clock=System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var links=b.BotOwner.VoxelesPersonalData.CurVoxel?.DoorLinks;if(links==null||links.Count>48)continue;
                foreach(var link in links)
                {
                    var d=link?.Door;if(d==null||d.GetType()!=typeof(Door)||d.DoorState!=EDoorState.Open||!d.Operatable
                        ||!DoorMotion.Reversed(d.GetAngle(EDoorState.Shut),d.GetAngle(EDoorState.Open),d.CurrentAngle)
                        ||doorSwings.Exists(x=>x.Door==d)||RecoveryBusy(b,pair.Value,d))continue;
                    bool near=false;foreach(var collider in d.CollisionColliders)
                        if(collider!=null&&Vector3.Distance(collider.ClosestPoint(b.Position+Vector3.up),b.Position+Vector3.up)<config.DoorMotion.LeafDistance){near=true;break;}
                    if(!near)continue;
                    watch.Attempt=Time.time+3;float to=d.GetAngle(EDoorState.Open);
                    if(!SwingClear(d,d.CurrentAngle,to,b,true))
                    {RecordDoorMotion(pair.Value,d,"rejected","motion-sweep-occupied","unjam",to);break;}
                    doorSwings.Add(new(){Door=d,Bot=b,State=pair.Value,From=d.CurrentAngle,To=to,Start=Time.time,
                        Interaction=(WorldInteractiveObject.InteractionState)recoveryInteraction.GetValue(d)!,Generation=recoveryEpochs.GetValue(d,_=>new()).Generation});
                    RecordDoorMotion(pair.Value,d,"requested","motion-unjam-requested","unjam",to);break;
                }
            }
            catch(Exception ex){Logger.LogWarning("COMET C04 local check: "+ex.Message);watch.Attempt=Time.time+3;}
            finally{clock.Stop();doorBudget.Charge(clock.Elapsed.TotalMilliseconds);}
        }
    }
    private void RecordDoorMotion(BotState s,Door door,string state,string reason,string action,float target)
    {
        var point=door.transform.position;
        var t=new DoorMotionTrace{State=state,Reason=reason,Door=door.Id,DoorState=door.DoorState.ToString(),Action=action,
            Angle=door.CurrentAngle,TargetAngle=target,ValidFor=state is "ended" or "confirmed" or "rejected"?0:.35,X=point.x,Y=point.y,Z=point.z};
        var json=Diagnostics.Serialize(new{schema=1,session,sequence=++sequence,feature="C04",stage="evaluated",decision="DoorMotion",reason,
            realtime=Time.realtimeSinceStartup,utc=DateTime.UtcNow.ToString("O"),configRevision=config?.Revision??0,doorMotionConfig=config?.DoorMotion,
            profileId=s.ProfileId,name=s.Name,botInstance=s.Id,doorMotion=t});
        diagnostics?.Write(json);Api.Decisions.Publish(json,ex=>Logger.LogWarning(ex.Message));
    }
    private void ClearDoorMotion()
    {doorSwings.Clear();motionWatches.Clear();pushContexts.Clear();Array.Clear(swingHits,0,swingHits.Length);DoorMotionNetwork.Clear();}
}
