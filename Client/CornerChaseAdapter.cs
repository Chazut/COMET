using System;
using System.Linq;
using Comet.Core;
using EFT;
using HarmonyLib;
using Newtonsoft.Json;
using SAIN.Components;
using SAIN.Layers;
using SAIN.Models.Enums;
using SAIN.Preset.Shared.Enums;
using SAIN.Preset.Shared.GlobalSettings;
using SAIN.SAINComponent.Classes.Mover;
using UnityEngine;
using UnityEngine.AI;

namespace Comet.Client;

internal sealed class CornerChaseRuntime
{
    internal readonly ChaseObservation Memory=new();
    internal readonly CornerChase Policy=new();
    internal BotComponent? Owner;
    internal object? Action;
    internal IBotPathData? Path;
    internal Vector3 Goal,Corner,Exit,Wide,Origin;
    internal Vector3[] Lane=Array.Empty<Vector3>();
    internal float PathAt,CallbackAt,SampleAt=-1000,SafetyAt=-1000,LogAt=-1000;
    internal double PathLength;
    internal int TargetInstance,ObservationInstance,Stage,Side;
    internal bool Fault,OwnLean,OwnEmpty,WideSafe,JumpSafe;
    internal LeanSetting Applied;
    internal string LogKey="";
}
public sealed partial class Plugin
{
    private static readonly Type chaseAction=typeof(BotAction).Assembly.GetType("SAIN.Layers.Combat.Solo.RushEnemyAction",true)!;
    private static readonly System.Reflection.FieldInfo chaseEnemy=AccessTools.Field(chaseAction,"_enemy");
    private static readonly System.Reflection.FieldInfo chaseMoveAt=AccessTools.Field(chaseAction,"_updateMoveTime");
    private static void PatchCornerChase(Harmony h)
    {
        if(chaseEnemy==null||chaseMoveAt==null)throw new MissingFieldException("M05 official SAIN rush fields");
        // A valid bounded M05 route must run before A03's frozen-route prefix can skip native movement.
        h.Patch(AccessTools.Method(chaseAction,"updateRushBehavior"),prefix:new HarmonyMethod(typeof(Plugin),nameof(ChaseTick)){priority=Priority.First});
        h.Patch(AccessTools.Method(chaseAction,"OnSteeringTicked"),prefix:new HarmonyMethod(typeof(Plugin),nameof(ChaseSteering)));
        h.Patch(AccessTools.Method(chaseAction,"Stop"),prefix:new HarmonyMethod(typeof(Plugin),nameof(ChaseStopped)));
    }
    private void SampleChase(BotComponent b,BotState s)
    {
        var r=s.CornerChase;
        if(config?.CornerChase.Enabled!=true||r.Fault||b==null||b.IsDead||!b.BotActive||!Authority||Ghost(b.ProfileId)||!NativeAdapter.IsSupportedAi(b)) {r.Memory.Clear();return;}
        if(Time.time-r.SampleAt<.1f)return;r.SampleAt=Time.time;
        var e=b.GoalEnemy;int instance=e==null?0:System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(e);
        if(instance!=r.ObservationInstance){r.Memory.Clear();r.ObservationInstance=instance;}
        var seen=e?.KnownPlaces.LastSeenPlace;
        bool visible=e?.IsVisible==true&&seen!=null;
        // Never read EnemyPlayer.Velocity / Position, nor any hidden shared or heard update.
        r.Memory.Sample(e?.EnemyProfileId??"",visible,visible?DP(seen!.Position):default,Time.time);
    }
    private CornerChaseFrame ChaseFrame(BotComponent b,BotState s)
    {
        var r=s.CornerChase;var f=CombatStepFrame(b,s);
        f.Action=b.CurrentAction?.GetType()==chaseAction&&b.Decision.CurrentCombatDecision==ECombatDecision.RushEnemy?"RushEnemy":"other";
        f.Visible=b.GoalEnemy?.IsVisible==true||b.GoalEnemy?.InLineOfSight==true;
        f.Source=r.Memory.Lost?"personal-vision":"";f.Known=r.Policy.Active?r.Policy.Known:r.Memory.Known;
        f.ContactAge=Time.time-r.Memory.SeenAt;f.Distance=(f.Position-f.Known).Length;
        int scanned=0;
        foreach(var e in b.EnemyController.KnownEnemies) {
            if(++scanned>32){f.Urgent=true;break;}
            if(e!=null&&!ReferenceEquals(e,b.GoalEnemy)&&(e.IsVisible||e.Status.ShotAtMeRecently)){f.Urgent=true;break;}
        }
        f.Urgent|=b.Medical.TimeSinceShot<.5;
        var velocity=b.Player.Velocity;velocity.y=0;
        return new(){Combat=f,ObservationEpisode=r.Memory.Episode,SeenAt=r.Memory.SeenAt,Speed=velocity.magnitude,
            Stamina=b.Player.Physical.Stamina.NormalValue*100,NativeLeanOwned=b.Mover.Lean.IsHoldingLean,
            RealCorner=true,PathLength=r.PathLength,CanJump=ChaseCanJump(b)&&r.JumpSafe,
            Fleeing=f.ContactAge<=1.5&&r.Memory.Speed>=1&&r.Memory.Direction.Length>.5
                &&Vector3.Dot(VP(r.Memory.Direction),(VP(r.Memory.Known)-b.Position).normalized)>.3f};
    }
    private static bool ChaseCanJump(BotComponent b)=>b.Info.FileSettings.Move.JUMP_TOGGLE&&SainPresetAccess.AllowsJump
        &&b.Player.MovementContext.CanJump&&b.Player.MovementContext.CanSprint&&b.BotOwner.CanSprintPlayer;
    private static bool ChaseTick(BotAction __instance)
    {
        var p=Instance;var b=__instance.Bot;
        if(p?.config==null||!p.states.TryGetValue(b,out var s))return true;
        var r=s.CornerChase;if(r.Fault)return true;
        try {
            p.SampleChase(b,s);r.CallbackAt=Time.time;
            if(!r.Policy.Active) {
                var f=p.ChaseFrame(b,s);
                if(!r.Policy.Eligible(f,p.config.CornerChase)||QueuedCombatMovement(b))return true;
                if(!p.doorBudget.TryBegin(Time.frameCount))return true;
                p.doorClock.Restart();
                try {
                    f.RealCorner=p.PlanChase(b,s,out f.GeometryReason);f.PathLength=r.PathLength;f.CanJump=ChaseCanJump(b)&&r.JumpSafe&&f.Stamina>=p.config.CornerChase.JumpMinStamina;
                    if(!r.Policy.Begin(f,p.config.CornerChase,p.jumpRandom.NextDouble(),p.jumpRandom.NextDouble(),r.Side)){p.RecordChase(s);return true;}
                    r.Owner=b;r.Action=__instance;r.TargetInstance=System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(b.GoalEnemy);
                    r.Stage=r.Policy.Trace!.Style=="pie"&&r.WideSafe?0:1;
                    if(r.Policy.Trace.Style=="pie"&&!r.WideSafe)r.Policy.Fallback("No safe wide route; use the verified direct angle");
                    p.EndCombatLean(s,"lean-preserve-corner-chase");p.EndCornerPeek(s,"corner-native-action-changed");p.EndCombatStep(s,"movement-native-action-changed");
                    if(!p.RequestChaseRoute(b,s,true)){p.EndChase(s,"chase-route-request-refused");return true;}
                    p.BeginPrefire(b,s);
                }finally{p.doorClock.Stop();p.doorBudget.Charge(p.doorClock.Elapsed.TotalMilliseconds);}
            }
            p.RefreshChase(b,s,true);
            if(!r.Policy.Active)return true;
            chaseEnemy.SetValue(__instance,b.GoalEnemy); // Preserve SAIN's native private enemy bookkeeping.
            return false;
        }catch(Exception ex){p.ChaseError(s,ex);return true;}
    }
    private bool PlanChase(BotComponent b,BotState s,out string reason)
    {
        var r=s.CornerChase;var c=config!.CornerChase;r.PathLength=0;r.Side=0;r.WideSafe=r.JumpSafe=false;
        var known=VP(r.Memory.Known);var route=new NavMeshPath();var end=known;
        reason="chase-contact-no-navmesh";
        if(!NavMesh.SamplePosition(end,out var sample,.35f,NavMesh.AllAreas))return false;end=sample.position;
        NavMesh.CalculatePath(b.Position,end,NavMesh.AllAreas,route);
        reason="chase-contact-route-incomplete-or-too-long";
        if(!CornerChase.Route(DP(b.Position),DP(end),route.corners.Select(DP).ToArray(),route.status==NavMeshPathStatus.PathComplete,c.PathLimit,out r.PathLength))return false;
        // Last seen can be just before the turn. A short projection uses only direction observed while visible.
        if(route.corners.Length<3&&Time.time-r.Memory.SeenAt<=1&&r.Memory.Speed>=.5&&r.Memory.Direction.Length>.5) {
            var projected=known+VP(r.Memory.Direction)*Mathf.Min(1,(float)r.Memory.Speed*.25f);
            reason="chase-projected-contact-no-navmesh";
            if(!NavMesh.SamplePosition(projected,out sample,.25f,NavMesh.AllAreas))return false;end=sample.position;
            NavMesh.CalculatePath(b.Position,end,NavMesh.AllAreas,route);
            reason="chase-projected-route-incomplete-or-too-long";
            if(!CornerChase.Route(DP(b.Position),DP(end),route.corners.Select(DP).ToArray(),route.status==NavMeshPathStatus.PathComplete,c.PathLimit,out r.PathLength))return false;
        }
        reason="chase-route-has-no-bend";
        var pts=route.corners;if(pts.Length<3)return false;
        var incoming=pts[1]-pts[0];incoming.y=0;var outgoing=pts[2]-pts[1];outgoing.y=0;
        reason="chase-first-bend-segments-too-short";
        if(incoming.magnitude<.6||outgoing.magnitude<.5)return false;
        reason="chase-bend-angle-outside-range";
        float angle=Vector3.Angle(incoming,outgoing);if(angle<25||angle>145)return false;
        r.Corner=pts[1];r.Side=Vector3.Dot(Vector3.Cross(Vector3.up,incoming.normalized),outgoing)>0?1:-1;
        r.Exit=pts[1]+outgoing.normalized*Mathf.Min(1.5f,outgoing.magnitude);
        var before=pts[1]-incoming.normalized*Mathf.Min(1.2f,incoming.magnitude);
        // Solid, vertical obstruction + navigation bend. Foliage-only loss does not qualify.
        reason="chase-bend-has-no-vertical-obstruction";
        if(!Physics.Linecast(before+Vector3.up*1.2f,r.Exit+Vector3.up*1.2f,out var hit,LayersMaskController.HighPolyWithTerrainMask,QueryTriggerInteraction.Ignore)
            ||Math.Abs(hit.normal.y)>.4f)return false;
        reason="chase-exit-route-unsafe-or-too-long";
        if(!ChaseRoute(b,b.Position,r.Exit,c.PathLimit,out _,out _))return false;
        var away=-Vector3.Cross(Vector3.up,incoming.normalized)*r.Side;
        var raw=before+away*(float)c.WideOffset;
        if(NavMesh.SamplePosition(raw,out sample,.2f,NavMesh.AllAreas)) {
            r.Wide=sample.position;
            r.WideSafe=Vector3.Distance(r.Wide,before)<=c.WideOffset+.2
                &&ChaseRoute(b,b.Position,r.Wide,c.PathLimit,out _,out double a)
                &&ChaseRoute(b,r.Wide,r.Exit,c.PathLimit,out _,out double z)&&a+z<=c.PathLimit;
        }
        r.JumpSafe=RunVolume(b,before,r.Corner,true).Length==0&&RunVolume(b,r.Corner,r.Exit,true).Length==0;
        r.Origin=b.Position;reason="chase-verified-corner";return true;
    }
    private bool ChaseRoute(BotComponent b,Vector3 from,Vector3 goal,double limit,out NavMeshPath route,out double length)
    {
        route=new();NavMesh.CalculatePath(from,goal,NavMesh.AllAreas,route);
        if(!CornerChase.Route(DP(from),DP(goal),route.corners.Select(DP).ToArray(),route.status==NavMeshPathStatus.PathComplete,limit,out length))return false;
        var pts=route.corners;
        for(int i=1;i<pts.Length;i++)if(RunVolume(b,pts[i-1],pts[i]).Length>0||CornerOtherThreats(b,pts[i-1],pts[i]).Length>0)return false;
        return true;
    }
    private static bool OwnChase(CornerChaseRuntime r)=>r.Owner!=null&&r.Path!=null&&ReferenceEquals(r.Owner.Mover.ActivePath,r.Path)
        &&r.Path.TimeStarted==r.PathAt&&Vector3.Distance(r.Path.Destination,r.Goal)<.02;
    private bool RequestChaseRoute(BotComponent b,BotState s,bool initial=false)
    {
        var r=s.CornerChase;var goal=r.Stage==0?r.Wide:r.Exit;
        if(QueuedCombatMovement(b)||!ChaseRoute(b,b.Position,goal,config!.CornerChase.PathLimit,out var route,out _))return false;
        if(b.Mover.ActivePath is {} active) {
            if(!initial&&!OwnChase(r)||active is not BotPathDataManual)return false;
            active.RequestEndSprint(ESprintUrgency.None,"COMET chase route");combatStopPath.Invoke(active,new object[]{false,"COMET chase route"});
            if(b.Mover.ActivePath!=null)return false;
        }
        r.OwnEmpty=true;
        bool jump=r.Policy.Trace!.Style=="jump";
        bool ok=jump?b.Mover.RunToPointByWay(route,true,.1f,ESprintUrgency.High,false):b.Mover.WalkToPointByWay(route,true,.1f,false);
        r.Path=b.Mover.ActivePath;r.PathAt=r.Path?.TimeStarted??-1;r.Goal=goal;r.Lane=route.corners;r.OwnEmpty=false;
        if(!ok||!OwnChase(r))return false;
        r.SafetyAt=Time.time;r.Policy.Request(DP(goal));RecordChase(s,true);return true;
    }
    private void RefreshChase(BotComponent b,BotState s,bool fromAction)
    {
        var r=s.CornerChase;if(!r.Policy.Active)return;
        if(config==null||b==null||b.IsDead){EndChase(s,"chase-bot-ended");return;}
        var f=ChaseFrame(b,s);
        bool arrived=Vector3.Distance(b.Position,r.Goal)<=.25f;
        f.RouteValid=OwnChase(r)||b.Mover.ActivePath==null&&r.Path?.Status==EBotMoveStatus.Complete&&arrived;
        if(b.GoalEnemy==null||System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(b.GoalEnemy)!=r.TargetInstance||!ReferenceEquals(b.CurrentAction,r.Action)
            ||QueuedCombatMovement(b)||Time.time-r.CallbackAt>.4f){EndChase(s,"chase-owner-or-callback-changed");return;}
        // Priorities, contact reacquisition and hard duration are checked before expensive geometry.
        string gate=CornerChase.Gate(f,config.CornerChase,false);
        if(gate.Length>0||Time.time>=r.Policy.Deadline){EndChase(s,gate.Length>0?gate:"chase-time-limit");return;}
        if(Time.time-r.SafetyAt>=.1f) {
            if(!doorBudget.TryBegin(Time.frameCount)){if(Time.time-r.SafetyAt>.2f)EndChase(s,"chase-safety-budget-expired");return;}
            doorClock.Restart();try {
                bool onLane=false;
                for(int i=1;i<r.Lane.Length;i++) {
                    if(!CornerPeek.Lane(DP(r.Lane[i-1]),DP(r.Lane[i]),DP(b.Position),r.Policy.Trace!.JumpAccepted))continue;
                    onLane=true;
                }
                f.LaneSafe=onLane;
                for(int i=1;i<r.Lane.Length&&f.LaneSafe;i++)f.LaneSafe=RunVolume(b,r.Lane[i-1],r.Lane[i]).Length==0&&CornerOtherThreats(b,r.Lane[i-1],r.Lane[i]).Length==0;
                r.SafetyAt=Time.time;
            }finally{doorClock.Stop();doorBudget.Charge(doorClock.Elapsed.TotalMilliseconds);}
        }
        if(!r.Policy.Observe(f,config.CornerChase)){FinishChase(s);return;}
        if(arrived&&f.Combat.Grounded) {
            if(r.Stage==0&&fromAction){r.Stage=1;if(!RequestChaseRoute(b,s))EndChase(s,"chase-next-leg-unavailable");}
            else if(r.Stage==1)EndChase(s,"chase-corner-checked");
            return;
        }
        var t=r.Policy.Trace!;
        b.Mover.SetTargetMoveSpeed(t.Style=="pie"?.35f:t.Style=="jump"?1:.7f);
        if(t.Style!="jump")r.Path?.RequestEndSprint(ESprintUrgency.None,"COMET chase walking angle");
        if(fromAction&&t.Style=="jump"&&!t.JumpRequested) {
            var toward=r.Corner-b.Position;toward.y=0;var velocity=b.Player.Velocity;velocity.y=0;
            if(r.Policy.MayJump(Time.time,Vector3.Dot(velocity,toward.normalized),toward.magnitude,f.Combat.Grounded,ChaseCanJump(b),f.Stamina,config.CornerChase)) {
                if(doorBudget.TryBegin(Time.frameCount)) {
                    doorClock.Restart();try {
                        if(RunVolume(b,b.Position,r.Corner,true).Length==0&&RunVolume(b,r.Corner,r.Exit,true).Length==0)r.Policy.JumpResult(ControlledJump(b,s,"M05","corner-chase",toward,r.Exit,config.CornerChase.JumpMinSpeed,config.CornerChase.JumpMinStamina,true),Time.time);
                        else r.Policy.Fallback("Jump headroom or landing corridor blocked");
                    }finally{doorClock.Stop();doorBudget.Charge(doorClock.Elapsed.TotalMilliseconds);}
                }
            }else if(toward.magnitude<.6||f.Stamina<config.CornerChase.JumpMinStamina||!ChaseCanJump(b))r.Policy.Fallback("Insufficient observed run-up or stamina");
        }
        RecordChase(s);
    }
    private static bool ChaseSteering(BotAction __instance)
    {
        var p=Instance;if(p==null||!p.states.TryGetValue(__instance.Bot,out var s)||!s.CornerChase.Policy.Active)return true;
        try {p.RefreshChase(__instance.Bot,s,false);if(!s.CornerChase.Policy.Active)return true;
            if(s.CornerPrefire.Policy.Active)p.RefreshPrefire(__instance.Bot,s,true);
            else __instance.Bot.Steering.LookToPoint(VP(s.CornerChase.Policy.Known)+Vector3.up*1.3f);return false;
        }catch(Exception ex){p.ChaseError(s,ex);return true;}
    }
    private static void ChaseStopped(BotAction __instance){var p=Instance;if(p!=null&&p.states.TryGetValue(__instance.Bot,out var s))p.EndChase(s,"chase-native-action-ended");}
    private bool ChaseLean(LeanClass lean,BotState s)
    {
        var r=s.CornerChase;if(!r.Policy.Active)return false;
        RefreshChase(lean.Bot,s,false);if(!r.Policy.Active)return false;
        if(r.Policy.Trace!.Style=="jump"||!lean.Bot.Info.FileSettings.Move.LEAN_TOGGLE||!SainPresetAccess.AllowsLean)return true;
        r.Applied=r.Side<0?LeanSetting.Left:LeanSetting.Right;r.OwnLean=true;lean.FastLean(r.Applied);return true;
    }
    private void EndChase(BotState s,string reason){if(!s.CornerChase.Policy.Active)return;s.CornerChase.Policy.End(reason,Time.time);FinishChase(s);}
    private void FinishChase(BotState s)
    {
        EndPrefire(s,"prefire-parent-ended");
        var r=s.CornerChase;var b=r.Owner;
        try {
            if(Authority&&OwnChase(r)&&r.Path is BotPathDataManual path&&!(s.MedicalPosture.Policy.Blocking&&b!=null&&!b.Player.MovementContext.IsGrounded)){path.RequestEndSprint(ESprintUrgency.None,"COMET chase ended");combatStopPath.Invoke(path,new object[]{false,"COMET chase ended"});}
            if(Authority&&b!=null&&!b.IsDead&&r.OwnLean&&!b.Mover.Lean.IsHoldingLean&&b.Mover.Lean.LeanDirection==r.Applied)b.Mover.Lean.FastLean(LeanSetting.None);
            if(Authority&&b!=null&&ReferenceEquals(b.CurrentAction,r.Action)){chaseMoveAt.SetValue(r.Action,0f);b.Mover.SetTargetMoveSpeed(1);}
        }finally{RecordChase(s,true);r.Owner=null;r.Action=null;r.Path=null;r.Lane=Array.Empty<Vector3>();r.OwnLean=r.OwnEmpty=false;}
    }
    private void ChaseError(BotState s,Exception ex){s.CornerChase.Fault=true;EndChase(s,"chase-adapter-error");Logger.LogWarning("M05 yielded: "+ex.GetBaseException().Message);}
    private void RecordChase(BotState s,bool force=false)
    {
        var r=s.CornerChase;var t=r.Policy.Trace;if(t==null)return;
        string key=t.Episode+":"+t.State+":"+t.Reason+":"+t.Requests+":"+t.JumpRequested+":"+t.TakeoffObserved+":"+t.LandingObserved+":"+t.Alternatives.Length;
        if(!force&&key==r.LogKey&&(!r.Policy.Active||Time.time-r.LogAt<.1f))return;r.LogKey=key;r.LogAt=Time.time;
        var json=Diagnostics.Serialize(new{schema=1,session,sequence=++sequence,feature="M05",stage="evaluated",decision="CornerChase",reason=t.Reason,
            realtime=Time.realtimeSinceStartup,utc=DateTime.UtcNow.ToString("O"),configRevision=config?.Revision??0,profileId=s.ProfileId,name=s.Name,botInstance=s.Id,
            cornerChase=t,cornerChaseConfig=config?.CornerChase});
        diagnostics?.Write(json);Api.Decisions.Publish(json,ex=>{if(!subscriberWarned){subscriberWarned=true;Logger.LogWarning(ex.Message);}});
    }
}
