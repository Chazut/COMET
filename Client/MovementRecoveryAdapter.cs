using System;
using Comet.Core;
using Comet.Api;
using Comfort.Common;
using EFT;
using EFT.Ballistics;
using EFT.Interactive;
using HarmonyLib;
using Newtonsoft.Json;
using SAIN.Components;
using SAIN.Models.Enums;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.Mover;
using UnityEngine;
using UnityEngine.AI;

namespace Comet.Client;

internal sealed class MovementRecoveryRuntime
{
    internal readonly MovementRecoveryPolicy Policy=new();
    internal double Next,LogAt=-1000;
    internal string Key="";
    internal bool Fault;
}
public sealed partial class Plugin
{
    private int recoveryFrame=-1,recoverySamples;
    private bool movementEnrollmentWarned;
    private readonly Collider[] recoveryVolumes=new Collider[32];
    private static void PatchMovementRecovery(Harmony h)=>h.Patch(AccessTools.Method(typeof(BotComponent),nameof(BotComponent.ManualUpdate)),
        postfix:new HarmonyMethod(typeof(Plugin),nameof(TrackMovementRecovery)));
    private bool MovementRecoveryScope(BotComponent b)=>config?.Enabled==true&&config.MovementRecovery.Enabled&&Authority
        &&NativeAdapter.IsPmcAi(b)&&b.BotActive&&!b.IsDead&&!b.GameEnding&&!Ghost(b.ProfileId);
    private static void TrackMovementRecovery(BotComponent __instance)
    {
        var p=Instance;if(p==null)return;
        try{if(p.MovementRecoveryScope(__instance)&&!p.states.ContainsKey(__instance))p.states.Add(__instance,new BotState(__instance,p.config!,p.session));}
        catch(Exception ex){if(!p.movementEnrollmentWarned){p.movementEnrollmentWarned=true;p.Logger.LogWarning("C06 enrollment: "+ex.Message);}}
    }
    private bool RecoveryOwnsStall(BotState s)=>config?.Enabled==true&&config.MovementRecovery.Enabled&&!s.MovementRecovery.Fault&&s.MovementRecovery.Policy.Handling;
    private MovementRecoveryFrame ReadMovementRecovery(BotComponent b,BotState s,out Vector3 next)
    {
        var p=b.Position;var f=new MovementRecoveryFrame{Now=Time.timeAsDouble,X=p.x,Y=p.y,Z=p.z,Scoped=MovementRecoveryScope(b),
            Owner=b.SAINLayersActive?"SAIN":"external-mover",Action=b.Decision.CurrentCombatDecision.ToString(),Contact=b.GoalEnemy?.EnemyProfileId??""};
        next=p;var path=b.Mover.ActivePath;
        if(b.SAINLayersActive&&path!=null)
        {
            f.Intent=path.Moving&&!(path is BotPathDataManual manual&&manual.CancelRequested)&&path.Status!=EBotMoveStatus.Complete;
            f.Route=ProgressAdapter.Destination(path.Destination);
            if(path.PathCorners!=null&&path.CurrentIndex>=0&&path.CurrentIndex<path.PathCorners.Count)next=path.PathCorners[path.CurrentIndex].Position;
            if(path.Status==EBotMoveStatus.Paused||path is BotPathDataManual paused&&paused.PausedRequested)f.Pause="recovery-path-paused";
        }
        else
        {
            var mover=b.BotOwner.Mover;var native=mover.ActualPathController?.CurPath;
            f.Intent=mover.IsMoving&&mover.HasPathAndNoComplete;
            if(native!=null&&native.Length>0&&native.CurIndex>=0&&native.CurIndex<native.Length)
            {next=native.GetPoint(native.CurIndex);f.Route=ProgressAdapter.Destination(native.GetPoint(native.Length-1));}
            else f.Intent=false;
        }
        if(s.ActiveLabel.Length>0)f.Owner="COMET";
        var wm=b.BotOwner.WeaponManager;
        if(!b.Player.MovementContext.IsGrounded||b.Player.IsInPronePose)f.Pause="recovery-airborne-or-prone";
        else if(b.BotOwner.Medecine?.Using==true||b.Decision.CurrentSelfDecision!=ESelfActionType.None)f.Pause="recovery-self-action";
        else if(wm==null||wm.Reload.Reloading||wm.Selector.IsChanging||b.Player.HandsController==null||b.Player.HandsController.IsInInteractionStrictCheck())f.Pause="recovery-hands-busy";
        else if(b.BotOwner.DoorOpener.Interacting||b.DoorOpener.Interacting||path?.Status==EBotMoveStatus.DoorInteraction
            ||doorSwings.Exists(x=>x.Bot==b))f.Pause="recovery-door-in-progress";
        else if(s.Fake.Policy.Active||s.Entry.Policy.Active||s.RoomClear.Policy.Active&&s.RoomClear.Policy.Trace?.State!="entering")f.Pause="recovery-tactical-door-action";
        else if(b.BotOwner.ShootData.Shooting||b.ManualShoot.Shooting)f.Pause="recovery-shooting";
        else if(b.SAINLayersActive&&b.Search.CurrentState.ToString()=="Wait")f.Pause="recovery-search-wait";
        else if(s.ActiveLabel.Length>0&&(s.Decision=="Hold"||s.Decision=="KeepAngle"))f.Pause="recovery-intentional-hold";
        return f;
    }
    private void SampleMovementRecovery(BotComponent b,BotState s)
    {
        var r=s.MovementRecovery;
        if(r.Fault)return;
        if(!MovementRecoveryScope(b)){EndMovementRecovery(s,"recovery-out-of-scope");return;}
        double now=Time.timeAsDouble;if(now<r.Next)return;
        // Bound native probes per frame; due bots keep their deadline until serviced.
        if(recoveryFrame!=Time.frameCount){recoveryFrame=Time.frameCount;recoverySamples=0;}
        if(recoverySamples>=8)return;recoverySamples++;r.Next=now+config!.MovementRecovery.SampleInterval;
        var f=ReadMovementRecovery(b,s,out var next);string command=r.Policy.Observe(f,config.MovementRecovery);
        if(command.Length==0){RecordMovementRecovery(s);return;}
        RecordMovementRecovery(s,true);
        var fresh=ReadMovementRecovery(b,s,out next);
        if(!fresh.Scoped||!fresh.Intent||fresh.Pause.Length>0){r.Policy.Result(false,"recovery-state-changed");RecordMovementRecovery(s,true);return;}
        bool accepted=false;string reason="recovery-request-refused";
        switch(command)
        {
            case "repath":b.Mover.RecalcPath();b.BotOwner.Mover.RecalcWay();accepted=true;reason="recovery-repath-requested";break;
            case "early-vault":case "vault-or-jump":
                accepted=TryRecoveryJump(b,next,out reason);break;
            case "teleport":accepted=TryRecoveryTeleport(b,next,out reason);break;
            case "stop":
                s.Progress.Fail(f.Route,Time.time,"recovery-movement-exhausted");
                b.Mover.Stop();b.BotOwner.Mover.Stop();
                if(s.ActiveLabel.Length>0)Release(s,"recovery-movement-exhausted");
                accepted=true;reason="recovery-stopped-for-owner-replan";break;
        }
        r.Policy.Result(accepted,reason,command=="teleport");RecordMovementRecovery(s,true);
    }
    private bool TryRecoveryJump(BotComponent b,Vector3 next,out string reason)
    {
        reason="recovery-no-low-obstacle";
        var dir=next-b.Position;dir.y=0;if(dir.sqrMagnitude<.01f)return false;dir.Normalize();
        if(!RecoveryActorsClear(b,b.Position,b.Position+dir*1.2f,.7f)){reason="recovery-person-ahead";return false;}
        if(!Physics.Raycast(b.Position+Vector3.up*.4f,dir,out var low,.75f,LayersMaskController.PlayerStaticDoorMask,QueryTriggerInteraction.Ignore))return false;
        if(low.collider.GetComponentInParent<Door>()!=null){reason="recovery-door-owned-by-C01-C04";return false;}
        if(Physics.Raycast(b.Position+Vector3.up*1.3f,dir,1.2f,LayersMaskController.PlayerStaticDoorMask,QueryTriggerInteraction.Ignore))
        {reason="recovery-obstacle-too-high";return false;}
        if(b.Mover.TryVault()){reason="recovery-vault-requested";return true;}
        // A native vault refusal does not justify jumping blindly into a wall, actor or a drop.
        var candidate=b.Position+dir*1.2f;
        if(!NavMesh.SamplePosition(candidate,out var hit,.3f,NavMesh.AllAreas)||Math.Abs(hit.position.y-b.Position.y)>.35f
            ||!RecoveryVolumeClear(b,hit.position)){reason="recovery-jump-landing-unsafe";return false;}
        bool jumped=b.Mover.TryJump();reason=jumped?"recovery-jump-requested":"recovery-jump-refused";return jumped;
    }
    private bool RecoveryActorsClear(BotComponent b,Vector3 start,Vector3 end,float radius)
    {
        var actors=Singleton<GameWorld>.Instance?.AllAlivePlayersList;if(actors==null||actors.Count>256)return false;
        var edge=end-start;float sq=edge.sqrMagnitude;
        foreach(var a in actors)
        {
            if(a==null||ReferenceEquals(a,b.Player))continue;
            var closest=start+edge*(sq>.001f?Mathf.Clamp01(Vector3.Dot(a.Position-start,edge)/sq):0);
            var offset=a.Position-closest;
            if(Math.Abs(offset.y)<2&&offset.x*offset.x+offset.z*offset.z<radius*radius)return false;
        }
        return true;
    }
    private bool RecoveryVolumeClear(BotComponent b,Vector3 feet)
    {
        try
        {
            int n=Physics.OverlapCapsuleNonAlloc(feet+Vector3.up*.45f,feet+Vector3.up*1.5f,.28f,recoveryVolumes,
                LayersMaskController.PlayerStaticDoorMask,QueryTriggerInteraction.Ignore);
            if(n>=recoveryVolumes.Length)return false;
            for(int i=0;i<n;i++)if(recoveryVolumes[i]!=null&&recoveryVolumes[i].GetComponentInParent<Player>()!=b.Player)return false;
            return true;
        }finally{Array.Clear(recoveryVolumes,0,recoveryVolumes.Length);}
    }
    private bool TryRecoveryTeleport(BotComponent b,Vector3 next,out string reason)
    {
        var c=config!.MovementRecovery;reason="recovery-teleport-disabled";if(!c.Teleport)return false;
        if(fikaPresent&&MovementRecoveryNetwork.Teleport==null){reason="recovery-fika-transport-unavailable";return false;}
        reason="recovery-teleport-unsafe-route";
        var start=b.Position;var dir=next-start;dir.y=0;float distance=Math.Min((float)c.TeleportDistance,dir.magnitude);
        if(distance<.5f)return false;
        var target=start+dir.normalized*distance;
        if(!NavMesh.SamplePosition(start,out var origin,.3f,NavMesh.AllAreas)
            ||!NavMesh.SamplePosition(target,out var end,.2f,NavMesh.AllAreas)
            ||Vector3.Distance(start,end.position)>c.TeleportDistance||Math.Abs(start.y-end.position.y)>.3f
            ||NavMesh.Raycast(origin.position,end.position,out _,NavMesh.AllAreas))return false;
        target=end.position;dir=target-start;distance=dir.magnitude;
        // Continuous physical sweep prevents crossing walls, doors or a thin obstacle between samples.
        if(!RecoveryVolumeClear(b,start)||Physics.CapsuleCast(start+Vector3.up*.45f,start+Vector3.up*1.5f,.28f,dir.normalized,distance,
            LayersMaskController.PlayerStaticDoorMask,QueryTriggerInteraction.Ignore)||!RecoveryVolumeClear(b,target)
            ||!RecoveryActorsClear(b,start,target,1))return false;
        var actors=Singleton<GameWorld>.Instance?.AllAlivePlayersList;if(actors==null||actors.Count>256)return false;
        int humans=0;
        foreach(var actor in actors)
        {
            if(actor==null||actor.IsAI)continue;
            if(++humans>16){reason="recovery-human-budget-exceeded";return false;}
            for(int i=0;i<=8;i++)
            {
                var point=Vector3.Lerp(start,target,i/8f);
                if(Vector3.Distance(actor.Position,point)<c.HumanDistance){reason="recovery-human-too-close";return false;}
                var eye=actor.PlayerBones?.Head;if(eye==null){reason="recovery-human-view-unknown";return false;}
                // No camera cone assumption: an unobstructed view from any human blocks correction, even looking away/scoped.
                foreach(float height in RecoveryHeights)if(!RecoveryHidden(eye.position,point+Vector3.up*height))
                {reason="recovery-visible-to-human";return false;}
            }
        }
        bool ok;
        if(fikaPresent)ok=MovementRecoveryNetwork.Teleport!.Invoke(b.Player,target);
        else {b.Player.Teleport(target);ok=true;}
        if(ok){b.Mover.RecalcPath();b.BotOwner.Mover.RecalcWay();}
        reason=ok?"recovery-relocated-awaiting-movement":"recovery-fika-transport-refused";return ok;
    }
    private static readonly float[] RecoveryHeights={.4f,1f,1.7f};
    private static bool RecoveryHidden(Vector3 eye,Vector3 point)
    {
        if(!Physics.Linecast(eye,point,out var hit,LayersMaskController.HighPolyWithTerrainMask,QueryTriggerInteraction.Ignore)||hit.collider==null)return false;
        var collider=hit.collider;
        var material=collider.GetComponent<BaseBallistic>()??collider.transform.parent?.GetComponent<BaseBallistic>();
        // Glass and unknown surfaces cannot prove invisibility. Doors may have windows, so remain conservative.
        return collider.GetComponentInParent<Door>()==null&&material is BallisticCollider surface
            &&surface.TypeOfMaterial is not (MaterialType.Glass or MaterialType.GlassShattered);
    }
    private void EndMovementRecovery(BotState s,string reason)
    {var r=s.MovementRecovery;if(r.Policy.Trace.State=="ended")return;r.Policy.End(reason);RecordMovementRecovery(s,true);}
    private void MovementRecoveryError(BotState s,Exception ex)
    {var r=s.MovementRecovery;if(!r.Fault)Logger.LogWarning("C06 yielded: "+ex.GetBaseException().Message);r.Fault=true;EndMovementRecovery(s,"recovery-adapter-error");}
    private void RecordMovementRecovery(BotState s,bool force=false)
    {
        var r=s.MovementRecovery;var t=r.Policy.Trace;
        // Idle walking needs no heartbeat. Active incidents refresh the map context with a short TTL.
        if(!force&&t.StalledSeconds<1&&t.State!="recovered")return;
        string key=t.State+"|"+t.Reason+"|"+t.Incident+"|"+t.Step;
        if(!force&&key==r.Key&&(t.ValidFor==0||Time.timeAsDouble-r.LogAt<config!.MovementRecovery.SampleInterval))return;
        r.Key=key;r.LogAt=Time.timeAsDouble;
        var json=JsonConvert.SerializeObject(new{schema=1,session,sequence=++sequence,feature="C06",stage="evaluated",decision="MovementRecovery",reason=t.Reason,
            realtime=Time.realtimeSinceStartup,utc=DateTime.UtcNow.ToString("O"),configRevision=config?.Revision??0,profileId=s.ProfileId,name=s.Name,botInstance=s.Id,
            movementRecovery=t,movementRecoveryConfig=config?.MovementRecovery},Diagnostics.Json);
        diagnostics?.Write(json);Api.Decisions.Publish(json,ex=>{if(!subscriberWarned){subscriberWarned=true;Logger.LogWarning(ex.Message);}});
    }
}
