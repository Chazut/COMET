using System;
using Comet.Core;
using EFT;
using HarmonyLib;
using Newtonsoft.Json;
using SAIN.Classes;
using SAIN.Components;
using SAIN.Components.PlayerComponentSpace;
using SAIN.Models.Enums;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.Mover;
using UnityEngine;
using UnityEngine.AI;

namespace Comet.Client;

internal sealed class RetreatWeaveRuntime
{
    internal readonly RetreatWeavePolicy Policy=new();
    internal object? Enemy;
    internal IBotPathData? ProbePath;
    internal Vector3 Seen,HeardFire,Known,Heading,Native,Goal,RollGoal,ProbePosition,ProbeDirection,ProbeGoal;
    internal float SeenAt=-1000,FireAt=-1000,ShotStamp=-1000,GeometryAt=-1000,NextGeometry,CallbackAt=-1000,LogAt=-1000,SampleAt=-1000;
    internal int ProbeIndex;
    internal bool Left,Right,Straight,JumpSafe,LandingSafe,Fault;
    internal string Context="",LogKey="",Source="";
}
public sealed partial class Plugin
{
    private static void PatchRetreatWeave(Harmony h)
    {
        h.Patch(AccessTools.Method(typeof(PlayerMovementController),nameof(PlayerMovementController.SetTargetMoveDirection))
            ??throw new MissingMethodException("M11 native movement"),prefix:new HarmonyMethod(typeof(Plugin),nameof(WeaveMovement)));
    }
    private string WeaveBlock(BotComponent b,BotState s)
    {
        if(config?.Enabled!=true||!config.RetreatWeave.Enabled||!Authority||Ghost(s.ProfileId)||!NativeAdapter.IsPmcAi(b)
            ||b.IsDead||!b.BotActive||b.GameEnding||!b.SAINLayersActive)return "weave-ineligible";
        if(MedicalPostureGuard(b))return "medical-posture-protected";
        var a=b.CurrentAction?.GetType();string d=b.Decision.CurrentCombatDecision.ToString();
        if(a?.Assembly!=typeof(BotComponent).Assembly||!(a.FullName==SeekCoverType&&(d=="SeekCover"||d=="Retreat")
            ||a.FullName=="SAIN.Layers.Combat.Solo.AvoidGrenadeAction"))return "weave-native-action-priority";
        if(b.Decision.CurrentSelfDecision!=ESelfActionType.None||!VisibleAdapter.WeaponReady(b)||b.Player.IsInPronePose
            ||b.ManualShoot.Shooting||b.ManualShoot.Reason!=EShootReason.None||b.BotOwner.ShootData.Shooting||b.Aim.AimStatus!=AimStatus.NoTarget)
            return "weave-firing-medical-or-weapon-busy";
        if(s.CoverTravel.Policy.Trace?.State=="face-walk"&&s.CoverTravel.Owned||s.CornerPrefire.Policy.Active||s.CornerChase.Policy.Active
            ||s.CornerPeek.Policy.Active||s.CombatStep.Policy.Active||s.HoldAngle.Policy.Active||s.Entry.Policy.Active||s.RoomClear.Policy.Active
            ||s.Ambush.Policy.Active||s.Reservation.Lease!=null)return "weave-other-tactic-priority";
        var path=b.Mover.ActivePath;
        if(path==null||!path.Moving||path.Crawling||path.Status==EBotMoveStatus.DoorInteraction||path.PathStatus!=NavMeshPathStatus.PathComplete)
            return "weave-no-active-native-route";
        return "";
    }
    private void SampleWeave(BotComponent b,BotState s)
    {
        var r=s.RetreatWeave;if(r.Fault)return;
        if(!b.IsDead&&r.Policy.ObserveFlight(b.Player.MovementContext.IsGrounded,Time.time))RecordWeave(s,true);
        if(config?.Enabled!=true||!Authority||Ghost(s.ProfileId)||!NativeAdapter.IsPmcAi(b)||b.IsDead||!b.BotActive||b.GameEnding)
        {EndWeave(s,"weave-ineligible");r.Enemy=null;r.SeenAt=r.FireAt=-1000;return;}
        var e=b.GoalEnemy;
        if(!ReferenceEquals(r.Enemy,e)){EndWeave(s,"weave-contact-changed");r.Enemy=e;r.SeenAt=r.FireAt=r.SampleAt=r.ShotStamp=-1000;r.Context="";}
        if(e?.IsVisible==true&&Time.time-r.SampleAt>=.05f&&e.KnownPlaces.LastSeenPlace is {} seen)
        {r.Seen=seen.Position;r.SeenAt=Time.time;r.SampleAt=Time.time;}
        // ShotAtMe is a personal threat signal; its location must also come from personal perception.
        if(e!=null&&e.Status.TimeLastShotAtMe>0&&e.Status.TimeLastShotAtMe>r.ShotStamp) {
            r.ShotStamp=e.Status.TimeLastShotAtMe;
            var heard=e.KnownPlaces.LastHeardPlace;
            if(heard!=null&&heard.TimeSincePositionUpdated>=0&&heard.TimeSincePositionUpdated<1) {
                r.HeardFire=heard.Position;r.FireAt=r.ShotStamp;
            }else if(Time.time-r.SeenAt<3){r.HeardFire=r.Seen;r.FireAt=r.ShotStamp;}
        }
        if(r.Policy.Active&&(Time.time-r.CallbackAt>.3f||WeaveBlock(b,s).Length>0))EndWeave(s,"weave-owner-or-callback-ended");
    }
    private RetreatWeaveFrame WeaveFrame(BotComponent b,BotState s)
    {
        var r=s.RetreatWeave;var velocity=b.Player.Velocity;velocity.y=0;
        var look=b.PlayerComponent.CharacterController.CurrentControlLookDirection;
        return new(){Now=Time.time,VisionAge=Math.Max(0,Time.time-r.SeenAt),FireAge=Math.Max(0,Time.time-r.FireAt),
            Distance=Vector3.Distance(b.Position,r.Known),RearAngle=Vector3.Angle(r.Heading,new Vector3(r.Known.x-b.Position.x,0,r.Known.z-b.Position.z)),
            Speed=velocity.magnitude,ForwardDot=velocity.sqrMagnitude>.001f?Vector3.Dot(velocity.normalized,r.Native):0,
            GoalDistance=Vector3.Distance(b.Position,r.Goal),GoalHeight=Math.Abs(r.Goal.y-b.Position.y),GeometryAge=Math.Max(0,Time.time-r.GeometryAt),
            ObservedYaw=velocity.sqrMagnitude>.001f?Vector3.SignedAngle(r.Native,velocity,Vector3.up):0,
            ObservedPitch=look.sqrMagnitude>.001f?-Mathf.Asin(Mathf.Clamp(look.normalized.y,-1,1))*Mathf.Rad2Deg:0,
            Action=b.CurrentAction?.GetType().FullName=="SAIN.Layers.Combat.Solo.AvoidGrenadeAction"?"RunAway":b.Decision.CurrentCombatDecision.ToString(),
            Target=b.GoalEnemy?.EnemyProfileId??"",Context=r.Context,Source=r.Source,Block=WeaveBlock(b,s),Position=DP(b.Position),Known=DP(r.Known),Goal=DP(r.Goal),
            Sprinting=b.Player.IsSprintEnabled,Grounded=b.Player.MovementContext.IsGrounded,CanJump=ChaseCanJump(b),
            Left=r.Left,Right=r.Right,Straight=r.Straight,JumpSafe=r.JumpSafe,LandingSafe=r.LandingSafe,AtCover=b.Cover.CoverInUse!=null};
    }
    private static void WeaveMovement(ref Vector3 direction,Vector3 finalMoveDestination,PlayerComponent playerComp)
    {
        var p=Instance;var b=playerComp?.BotComponent;
        if(p?.config==null||b==null||!p.states.TryGetValue(b,out var s)||s.RetreatWeave.Fault)return;
        try {
            p.SampleWeave(b,s);var r=s.RetreatWeave;
            if(p.WeaveBlock(b,s).Length>0){p.EndWeave(s,p.WeaveBlock(b,s));return;}
            var path=b.Mover.ActivePath!;int index=path.CurrentIndex;var pts=path.PathCorners;
            if(index<0||index>=pts.Count||pts.Count>64||direction.sqrMagnitude<.01){p.EndWeave(s,"weave-route-not-usable");return;}
            r.CallbackAt=Time.time;
            // Stable leg heading, not a bearing that swings as the PMC moves laterally.
            var leg=pts[index].Position-(index>0?pts[index-1].Position:b.Position);leg.y=0;
            var native=new Vector3(direction.x,0,direction.z).normalized;
            if(leg.sqrMagnitude<.01f)leg=native;
            r.Heading=leg.normalized;r.Native=native;
            if(r.Context.Length==0||Vector3.Distance(path.Destination,r.RollGoal)>2) {
                r.Context=Guid.NewGuid().ToString("N");r.RollGoal=path.Destination;r.GeometryAt=-1000;
            }
            r.Goal=path.Destination;
            if(r.SeenAt>=r.FireAt){r.Known=r.Seen;r.Source=r.SeenAt>=0?"personal-vision":"";}
            else {r.Known=r.HeardFire;r.Source=r.FireAt>=0?"personal-fire":"";}
            // Cheap gates before any physics work; no hidden enemy transform is read.
            var f=p.WeaveFrame(b,s);
            if(RetreatWeavePolicy.Gate(f,p.config.RetreatWeave).Length==0&&(f.Sprinting||r.Policy.Active)&&f.RearAngle>80
                &&Math.Min(f.VisionAge,f.FireAge)<10)p.WeaveGeometry(b,s,path,native,false);
            f.GeometryAge=Math.Max(0,Time.time-r.GeometryAt);f.Left=r.Left;f.Right=r.Right;f.Straight=r.Straight;f.JumpSafe=r.JumpSafe;f.LandingSafe=r.LandingSafe;
            if(!r.Policy.Tick(f,p.config.RetreatWeave,p.jumpRandom.NextDouble)){p.RecordWeave(s);return;}
            var t=r.Policy.Trace!;
            if(t.Jump.Length>0) {
                // Jump input has fresh clearance, velocity and native permission checks of its own.
                bool checkedNow=p.WeaveGeometry(b,s,path,native,true);
                bool safe=checkedNow&&(t.Jump=="landing"?r.LandingSafe:r.JumpSafe)&&ChaseCanJump(b)&&b.Player.MovementContext.IsGrounded;
                var v=b.Player.Velocity;v.y=0;
                bool ok=safe&&v.magnitude>=p.config.RetreatWeave.JumpMinSpeed&&Vector3.Dot(v.normalized,native)>.85f
                    &&p.ControlledJump(b,s,"M11","retreat-"+t.Jump,native,b.Position+native*Mathf.Clamp(v.magnitude*.7f,3,6),p.config.RetreatWeave.JumpMinSpeed,0,true);
                r.Policy.JumpResult(ok,Time.time);
            }
            if(t.Yaw!=0)direction=Quaternion.AngleAxis((float)t.Yaw,Vector3.up)*native;
            r.Policy.Requested(t.Yaw!=0,false,p.config.RetreatWeave);p.RecordWeave(s);
        }catch(Exception ex){p.WeaveError(s,ex);}
    }
    private bool WeaveGeometry(BotComponent b,BotState s,IBotPathData path,Vector3 native,bool force)
    {
        var r=s.RetreatWeave;var c=config!.RetreatWeave;
        bool moved=!ReferenceEquals(path,r.ProbePath)||path.CurrentIndex!=r.ProbeIndex||Vector3.Distance(b.Position,r.ProbePosition)>.3f
            ||Vector3.Angle(native,r.ProbeDirection)>10||Vector3.Distance(path.Destination,r.ProbeGoal)>.1f;
        if(moved)r.GeometryAt=-1000;
        if(!force&&!moved&&Time.time<r.NextGeometry)return false;
        if(force&&r.GeometryAt==Time.time)return true;
        if(!doorBudget.TryBegin(Time.frameCount))return false;
        var clock=System.Diagnostics.Stopwatch.StartNew();
        try {
            r.GeometryAt=-1000;r.Left=r.Right=r.Straight=r.JumpSafe=r.LandingSafe=false;
            float horizon=Mathf.Max(1.5f,b.Player.Velocity.magnitude*(float)(c.Refresh+.1)+.5f);
            var left=Quaternion.AngleAxis(-(float)c.Angle,Vector3.up)*native;
            var right=Quaternion.AngleAxis((float)c.Angle,Vector3.up)*native;
            r.Left=WeaveLane(b,left,horizon,false);r.Right=WeaveLane(b,right,horizon,false);r.Straight=WeaveLane(b,native,horizon,false);
            float length=Mathf.Clamp(b.Player.Velocity.magnitude*.7f,3,6);
            if(c.Jumps&&b.Player.MovementContext.IsGrounded&&ChaseCanJump(b)&&b.Player.Velocity.magnitude>=c.JumpMinSpeed) {
                r.JumpSafe=WeaveLane(b,native,length,true);
                var to=r.Goal-b.Position;float distance=to.magnitude;to.y=0;
                r.LandingSafe=r.JumpSafe&&distance>=3&&distance<=5&&Math.Abs(r.Goal.y-b.Position.y)<=.4f&&Vector3.Dot(to.normalized,native)>.98f
                    &&WeaveLane(b,to.normalized,distance,true);
            }
            r.ProbePath=path;r.ProbeIndex=path.CurrentIndex;r.ProbePosition=b.Position;r.ProbeDirection=native;r.ProbeGoal=path.Destination;
            r.GeometryAt=Time.time;r.NextGeometry=Time.time+(float)c.Refresh;return true;
        }finally{clock.Stop();doorBudget.Charge(clock.Elapsed.TotalMilliseconds);}
    }
    private bool WeaveLane(BotComponent b,Vector3 direction,float length,bool jump)
    {
        var from=b.Position;var end=from+direction*length;
        // Sample the surface along the whole local lane; a clear chest ray alone can miss a drop or stairs.
        for(int i=1;i<=3;i++) {
            var q=Vector3.Lerp(from,end,i/3f);
            if(!NavMesh.SamplePosition(q,out var nav,.25f,NavMesh.AllAreas)||Math.Abs(nav.position.y-from.y)>(jump?.4f:.3f)
                ||!Physics.Raycast(q+Vector3.up*.4f,Vector3.down,out var ground,.8f,LayersMaskController.HighPolyWithTerrainMask,QueryTriggerInteraction.Ignore)
                ||ground.normal.y<.9f)return false;
        }
        return RunVolume(b,from,end,jump).Length==0;
    }
    private bool WeaveGaze(BotComponent b,BotState s,out Vector3 direction)
    {
        direction=default;var r=s.RetreatWeave;var t=r.Policy.Trace;
        if(config==null||r.Fault||!r.Policy.Active||t==null||Time.time-r.CallbackAt>.15f||WeaveBlock(b,s).Length>0||t.Pitch==0&&t.Yaw==0)return false;
        var flat=Quaternion.AngleAxis((float)t.Yaw,Vector3.up)*r.Native;
        float pitch=(float)t.Pitch*Mathf.Deg2Rad;direction=flat*Mathf.Cos(pitch)+Vector3.down*Mathf.Sin(pitch);
        r.Policy.Requested(t.MoveRequested,true,config.RetreatWeave);RecordWeave(s);return true;
    }
    private void EndWeave(BotState s,string reason)
    {var r=s.RetreatWeave;if(r.Policy.Active){r.Policy.End(reason);RecordWeave(s,true);}r.ProbePath=null;r.GeometryAt=-1000;}
    private void WeaveError(BotState s,Exception ex)
    {s.RetreatWeave.Fault=true;EndWeave(s,"weave-adapter-error");Logger.LogWarning("M11 yielded: "+ex.GetBaseException().Message);}
    private void RecordWeave(BotState s,bool force=false)
    {
        var r=s.RetreatWeave;var t=r.Policy.Trace;if(t==null||t.Target.Length==0||t.Context.Length==0)return;
        string key=t.State+":"+t.Reason+":"+t.Context+":"+t.Yaw+":"+t.Jump+":"+t.JumpOutcome;
        if(!force&&key==r.LogKey&&(!r.Policy.Active||Time.time-r.LogAt<.1f))return;r.LogKey=key;r.LogAt=Time.time;
        var json=Diagnostics.Serialize(new{schema=1,session,sequence=++sequence,feature="M11",stage="evaluated",decision="RetreatWeave",reason=t.Reason,
            realtime=Time.realtimeSinceStartup,utc=DateTime.UtcNow.ToString("O"),configRevision=config?.Revision??0,profileId=s.ProfileId,name=s.Name,botInstance=s.Id,
            retreatWeave=t,retreatWeaveConfig=config?.RetreatWeave});
        diagnostics?.Write(json);Api.Decisions.Publish(json,ex=>{if(!subscriberWarned){subscriberWarned=true;Logger.LogWarning(ex.Message);}});
    }
}
