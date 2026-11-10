using System;
using System.Collections.Generic;
using Comet.Core;
using EFT;
using HarmonyLib;
using Newtonsoft.Json;
using SAIN.Components;
using SAIN.Layers;
using SAIN.Models.Enums;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.Mover;
using UnityEngine;
using UnityEngine.AI;

namespace Comet.Client;

internal sealed class HoldAngleRuntime
{
    internal readonly HoldAngle Policy=new();
    internal BotComponent? Owner;
    internal object? Enemy;
    internal IBotPathData? Path;
    internal Vector3 Seen,Known,Aim,Goal;
    internal float SeenAt=-1000,SampleAt=-1000,ObservedAt,PathAt,CallbackAt,GeometryAt=-1000,NextGeometry,LogAt=-1000,BasePose,OldPose,AppliedPose;
    internal bool Fault,OwnPose,Baseline,ClearEyes,ClearMuzzle;
    internal int Candidate,PlanFrame=-1;
    internal string Source="",LogKey="";
    internal HoldAngleCandidate? Best;
    internal HoldAngleFrame? LastFrame;
    internal readonly List<string> Alternatives=new();
    internal void Drop(){Owner=null;Path=null;OwnPose=false;Best=null;LastFrame=null;Alternatives.Clear();}
}
public sealed partial class Plugin
{
    private static void PatchHoldAngle(Harmony h)
    {
        var type=typeof(BotComponent).Assembly.GetType("SAIN.Layers.Combat.Solo.FreezeAction",true);
        h.Patch(AccessTools.Method(type,"Update")??throw new MissingMethodException("M08 Freeze.Update"),
            postfix:new HarmonyMethod(typeof(Plugin),nameof(HoldAngleStep)));
        // Freeze inherits Stop; filter in the callback so other native actions remain untouched.
        h.Patch(AccessTools.Method(type,"Stop")??throw new MissingMethodException("M08 Freeze.Stop"),
            prefix:new HarmonyMethod(typeof(Plugin),nameof(HoldAngleStop)));
    }
    private void SampleHoldAngle(BotComponent b,BotState s)
    {
        var r=s.HoldAngle;if(r.Fault)return;
        if(config?.Enabled!=true||!config.HoldAngle.Enabled||b.IsDead||!b.BotActive||!Authority||Ghost(s.ProfileId)||!NativeAdapter.IsSupportedAi(b))
        {EndHoldAngle(s,"hold-angle-ineligible");r.Enemy=null;r.SeenAt=-1000;return;}
        var e=b.GoalEnemy;
        if(!ReferenceEquals(r.Enemy,e)){EndHoldAngle(s,"hold-angle-contact-changed");r.Enemy=e;r.SeenAt=-1000;}
        if(e?.IsVisible==true&&Time.time-r.SampleAt>=.1f&&e.KnownPlaces.LastSeenPlace is {} seen)
        {r.Seen=seen.Position;r.SeenAt=Time.time;r.SampleAt=Time.time;}
        if(r.Policy.Active) {
            if(Time.time-r.CallbackAt>.4f)EndHoldAngle(s,"hold-angle-callback-expired");
            else RefreshHoldAngle(b,s,false);
        }
    }
    private HoldAngleFrame HoldFrame(BotComponent b,BotState s)
    {
        var r=s.HoldAngle;var combat=CombatStepFrame(b,s);var e=b.GoalEnemy;
        var f=new HoldAngleFrame{Now=Time.time,HoldStarted=s.LastSelection,Target=e?.EnemyProfileId??"",Position=DP(b.Position),
            Pose=b.Player.PoseLevel,Speed=b.Player.Velocity.magnitude,GeometryAt=r.GeometryAt,EyesClear=r.ClearEyes,MuzzleClear=r.ClearMuzzle};
        if(r.SeenAt>=0&&Time.time-r.SeenAt<10){f.Source="personal-vision";f.Known=DP(r.Seen);f.ObservedAt=r.SeenAt;}
        var heard=e?.KnownPlaces.LastHeardPlace;
        if(heard!=null&&heard.TimeSincePositionUpdated>=0&&heard.TimeSincePositionUpdated<5
            &&(f.Source.Length==0||Time.time-heard.TimeSincePositionUpdated>f.ObservedAt))
        {f.Source="personal-hearing";f.Known=DP(heard.Position);f.ObservedAt=Time.time-heard.TimeSincePositionUpdated;}
        if(r.Policy.Active) {
            if(f.Source.Length>0&&Vector3.Distance(VP(f.Known),r.Known)>2)f.Block="hold-angle-contact-moved";
            // Freeze the personal evidence for this opportunity; repeated sounds cannot extend it.
            f.Source=r.Source;f.Known=DP(r.Known);f.ObservedAt=r.ObservedAt;
            f.RouteOwned=!QueuedCombatMovement(b)&&(r.Path==null?b.Mover.ActivePath==null:OwnHoldPath(r)
                ||b.Mover.ActivePath==null&&r.Path.Status==EBotMoveStatus.Complete&&Vector3.Distance(b.Position,r.Goal)<.2f);
        }
        if(!combat.Enabled||!combat.Pmc||!combat.Authority||combat.Ghost||!combat.Active)f.Block="hold-angle-ineligible";
        else if(e==null||e.IsVisible||e.InLineOfSight)f.Block="hold-angle-visible-or-no-enemy";
        else if(s.ActiveLabel!="COMET:Hold"||b.Decision.CurrentCombatDecision!=ECombatDecision.Freeze
            ||b.CurrentAction?.GetType().FullName!="SAIN.Layers.Combat.Solo.FreezeAction"||b.CurrentAction.GetType().Assembly!=typeof(BotComponent).Assembly)
            f.Block="hold-angle-native-action-priority";
        else if(combat.Urgent||combat.Busy||!combat.Grounded||combat.Sprinting||combat.Immobile||!combat.Ready||b.Medical.TimeSinceShot<.5)
            f.Block="hold-angle-urgent-or-busy";
        else if(combat.ProtectedCover||s.Sight.Current!=null||s.Ambush.Policy.Active||s.CornerPeek.Policy.Active||s.CombatStep.Policy.Active
            ||s.CornerChase.Policy.Active||s.CornerPrefire.Policy.Active||s.CombatLean.Policy.Active||s.Reservation.Lease!=null)
            f.Block="hold-angle-preserve-cover-or-tactic";
        else if(b.ManualShoot.Shooting||b.ManualShoot.Reason!=EShootReason.None||b.Aim.AimStatus!=AimStatus.NoTarget)
            f.Block="hold-angle-native-fire-or-aim";
        if(f.Block.Length==0) {
            int scanned=0;
            foreach(var other in b.EnemyController.KnownEnemies)
                if(++scanned>32||other!=null&&!ReferenceEquals(other,e)&&(other.IsVisible||other.Status.ShotAtMeRecently))
                {f.Block="hold-angle-other-immediate-threat";break;}
        }
        r.LastFrame=f;
        return f;
    }
    private static void HoldAngleStep(BotAction __instance)
    {
        var p=Instance;var b=__instance.Bot;if(p?.config==null||!p.states.TryGetValue(b,out var s)||s.HoldAngle.Fault)return;
        try {
            var r=s.HoldAngle;r.CallbackAt=Time.time;
            if(!r.Policy.Active) {
                if(b.Mover.ActivePath!=null||QueuedCombatMovement(b))return;
                var f=p.HoldFrame(b,s);if(!r.Policy.Begin(f,p.config.HoldAngle))return;
                r.Owner=b;r.Known=VP(f.Known);r.Source=f.Source;r.ObservedAt=(float)f.ObservedAt;r.Candidate=0;r.Baseline=false;
                r.BasePose=b.Player.PoseLevel;r.Best=null;r.Alternatives.Clear();r.NextGeometry=0;r.GeometryAt=-1000;
            }
            p.RefreshHoldAngle(b,s,true);
            // Original Freeze.Update has just chosen its native pose; override only for this accepted correction.
            if(r.Policy.Active&&r.Policy.Trace!.State!="planning") {
                float nativePose=b.Mover.Pose.PoseValue.TargetValue;
                if(!r.OwnPose||Math.Abs(nativePose-r.AppliedPose)>.01)r.OldPose=nativePose;
                r.AppliedPose=(float)r.Policy.Pose;
                if(p.config.HoldAngle.AdjustPose&&Math.Abs(nativePose-r.AppliedPose)>.01) {
                    b.Mover.SetTargetPose(r.AppliedPose);r.OwnPose=true;r.Policy.Requested(true,false);
                }
                p.RecordHoldAngle(s);
            }
        }catch(Exception ex){p.HoldAngleError(s,ex);}
    }
    private static void HoldAngleStop(BotAction __instance)
    {var p=Instance;if(((object)__instance).GetType().FullName=="SAIN.Layers.Combat.Solo.FreezeAction"&&p!=null&&p.states.TryGetValue(__instance.Bot,out var s))p.EndHoldAngle(s,"hold-angle-native-action-ended");}
    private void RefreshHoldAngle(BotComponent b,BotState s,bool mayPlan)
    {
        var r=s.HoldAngle;if(!r.Policy.Active||config==null)return;var c=config.HoldAngle;var f=HoldFrame(b,s);
        if(HoldAngle.Gate(f,c).Length>0){EndHoldAngle(s,HoldAngle.Gate(f,c));return;}
        if(!r.Policy.Observe(f,c)){FinishHoldAngle(s,r.Policy.Trace!.Reason!="hold-angle-context-changed");return;}
        if(r.Policy.Trace!.State=="planning") {
            if((b.Position-VP(r.Policy.Origin)).magnitude>.15){EndHoldAngle(s,"hold-angle-planning-origin-moved",true);return;}
            if(mayPlan&&r.PlanFrame!=Time.frameCount&&doorBudget.TryBegin(Time.frameCount)) {
                r.PlanFrame=Time.frameCount;var clock=System.Diagnostics.Stopwatch.StartNew();
                try {PlanHoldAngle(b,s,f);}finally{clock.Stop();doorBudget.Charge(clock.Elapsed.TotalMilliseconds);}
            }
        }else if(Time.time>=r.NextGeometry&&doorBudget.TryBegin(Time.frameCount)) {
            var clock=System.Diagnostics.Stopwatch.StartNew();
            try {
                var why=HoldSafety(b,s,b.Position,r.Goal,(float)r.Policy.Pose,true);
                if(why.Length>0){EndHoldAngle(s,why,true);return;}
                r.GeometryAt=Time.time;r.NextGeometry=Time.time+(float)c.Refresh;
                r.ClearEyes=AngleLane(b.Transform.EyePosition,r.Aim,.03f);r.ClearMuzzle=AngleLane(b.Transform.WeaponData.FirePort,r.Aim,.04f);
                f=HoldFrame(b,s);
                if(!r.Policy.Observe(f,c)){FinishHoldAngle(s,true);return;}
                if(r.Policy.Trace!.State=="watching")StopHoldPath(s);
            }finally{clock.Stop();doorBudget.Charge(clock.Elapsed.TotalMilliseconds);}
        }
        RecordHoldAngle(s);
    }
    private void PlanHoldAngle(BotComponent b,BotState s,HoldAngleFrame f)
    {
        var r=s.HoldAngle;var c=config!.HoldAngle;var origin=VP(r.Policy.Origin);
        if(!r.Baseline) {
            var route=new NavMeshPath();
            if(Vector3.Distance(origin,r.Known)>40||!NavMesh.SamplePosition(r.Known,out var end,.5f,NavMesh.AllAreas)
                ||!NavMesh.CalculatePath(origin,end.position,NavMesh.AllAreas,route)||route.status!=NavMeshPathStatus.PathComplete)
            {EndHoldAngle(s,"hold-angle-no-known-approach",true);return;}
            r.Aim=r.Known+Vector3.up*1.3f;var pts=route.corners;
            if(pts.Length>=3&&Vector3.Distance(origin,pts[1])<=6&&(pts[2]-pts[1]).magnitude>.5f)
                r.Aim=pts[1]+Vector3.up*1.3f+(pts[2]-pts[1]).normalized*.4f;
            r.ClearEyes=AngleLane(b.Transform.EyePosition,r.Aim,.03f);r.ClearMuzzle=AngleLane(b.Transform.WeaponData.FirePort,r.Aim,.04f);
            r.Baseline=true;
            if(r.ClearEyes&&r.ClearMuzzle){EndHoldAngle(s,"hold-angle-already-clear");return;}
        }
        // Do not spend separate frames on duplicate postures (e.g. an already standing bot).
        float[] postures=!c.AdjustPose||r.BasePose>=.95f?new[]{r.BasePose}
            :r.BasePose>=.6f?new[]{r.BasePose,1f}:new[]{r.BasePose,.65f,1f};
        int poses=postures.Length;int rings=(int)Math.Floor(c.MaxStep/c.Spacing+1e-6);int index=r.Candidate++;
        if(index>=(1+rings*5)*poses) {
            if(r.Best==null){EndHoldAngle(s,"hold-angle-no-safe-correction",true);return;}
            var best=r.Best;var goal=VP(best.Position);
            // Revalidate the selected candidate after the incremental search, before requesting anything.
            string why=HoldSafety(b,s,origin,goal,(float)best.Pose,true);
            if(why.Length>0){EndHoldAngle(s,why,true);return;}
            r.GeometryAt=Time.time;r.NextGeometry=Time.time+(float)c.Refresh;
            if(!r.Policy.Choose(best,DP(r.Aim),HoldFrame(b,s),c)){EndHoldAngle(s,"hold-angle-choice-expired",true);return;}
            r.Policy.Trace!.Alternatives=r.Alternatives.ToArray();r.Goal=goal;
            if(Vector3.Distance(origin,goal)>.1f) {
                if(!CornerRoute(b,b.Position,goal,c.MaxStep,out var endpoint,out var path,out _)
                    ||Vector3.Distance(endpoint,goal)>.02f||QueuedCombatMovement(b)||b.Mover.ActivePath!=null
                    ||!b.Mover.WalkToPointByWay(path,true,.1f,false)) {EndHoldAngle(s,"hold-angle-route-refused",true);return;}
                r.Path=b.Mover.ActivePath;r.PathAt=r.Path?.TimeStarted??-1;
                if(!OwnHoldPath(r)){EndHoldAngle(s,"hold-angle-route-unconfirmed",true);return;}
                r.Policy.Requested(false,true);
            }
            RecordHoldAngle(s,true);return;
        }
        int cell=index/poses;int poseIndex=index%poses;
        float pose=postures[poseIndex];
        Vector3 raw=origin;
        if(cell>0) {
            int ring=(cell-1)/5+1;int side=(cell-1)%5;
            var dir=r.Aim-origin;dir.y=0;if(dir.sqrMagnitude<.01f){EndHoldAngle(s,"hold-angle-no-direction",true);return;}
            float yaw=side switch{0=>0,1=>45,2=>-45,3=>90,_=>-90};
            raw+=Quaternion.AngleAxis(yaw,Vector3.up)*dir.normalized*(float)(ring*c.Spacing);
        }
        Vector3 point=raw;string reason="";
        if(cell>0&&!CornerRoute(b,origin,raw,c.MaxStep,out point,out _,out reason)) {AddHoldAlternative(r,"step-"+cell,reason);return;}
        if(Vector3.Distance(origin,point)>c.MaxStep+.001){AddHoldAlternative(r,"step-"+cell,"origin limit after navmesh snap");return;}
        reason=HoldSafety(b,s,origin,point,pose,true);
        string id=(cell==0?"posture":"step-"+cell)+"-"+poseIndex;
        if(reason.Length>0){AddHoldAlternative(r,id,reason);return;}
        // Exposure to the watched threat dominates distance/pose cost; other known threats may not gain a lane at all.
        int mask=HoldProtection(point,r.Known,pose);int exposed=3-((mask&1)!=0?1:0)-((mask&2)!=0?1:0)-((mask&4)!=0?1:0);
        double cost=exposed*10+Vector3.Distance(point,origin)+Math.Abs(pose-r.BasePose)*.4;
        if(r.Best==null||cost<r.Best.Cost)r.Best=new(){Id=id,Position=DP(point),Pose=pose,Cost=cost,Safe=true};
        AddHoldAlternative(r,id,"safe; exposure "+exposed+"; distance "+Vector3.Distance(point,origin).ToString("F2",System.Globalization.CultureInfo.InvariantCulture));
    }
    private static void AddHoldAlternative(HoldAngleRuntime r,string id,string reason)
    {if(r.Alternatives.Count==7)r.Alternatives.RemoveAt(1);r.Alternatives.Add(id+": "+reason);if(r.Policy.Trace!=null)r.Policy.Trace.Alternatives=r.Alternatives.ToArray();}
    private string HoldSafety(BotComponent b,BotState s,Vector3 from,Vector3 to,float pose,bool requireOpen)
    {
        var r=s.HoldAngle;var origin=VP(r.Policy.Origin);
        if(Vector3.Distance(origin,to)>config!.HoldAngle.MaxStep+.001f)return "hold-angle-origin-limit";
        string body=RunVolume(b,from,to);if(body.Length>0)return "hold-angle-"+body;
        // Conservative standing capsule checks headroom even for the lower candidate pose.
        int seen=0,count=0;
        foreach(var e in b.EnemyController.KnownEnemies) {
            if(++seen>32)return "hold-angle-threat-list-truncated";
            if(e==null||ReferenceEquals(e,b.GoalEnemy)||!e.EnemyKnown||e.TimeSinceLastKnownUpdated>3||e.KnownPlaces.LastKnownPosition is not Vector3 known)continue;
            if(++count>4)return "hold-angle-threat-budget-exceeded";
            int before=HoldProtection(origin,known,r.BasePose);
            if(!CombatStep.PreservesProtection(before,HoldProtection(from,known,pose),HoldProtection(to,known,pose)))return "hold-angle-exposes-other-threat";
            int steps=Math.Max(1,(int)Math.Ceiling(Vector3.Distance(from,to)/.3f));
            for(int i=1;i<steps;i++)if(!CombatStep.PreservesProtection(before,HoldProtection(Vector3.Lerp(from,to,(float)i/steps),known,pose),before))return "hold-angle-exposes-other-threat-on-route";
        }
        if(requireOpen) {
            // Predicted height is only a candidate estimate; success requires actual eye/muzzle rays after arrival.
            var offset=to-b.Position+Vector3.up*((pose-b.Player.PoseLevel)*.55f);
            if(!AngleLane(b.Transform.EyePosition+offset,r.Aim,.03f)||!AngleLane(b.Transform.WeaponData.FirePort+offset,r.Aim,.04f))return "hold-angle-eyes-or-muzzle-blocked";
        }
        return "";
    }
    private static int HoldProtection(Vector3 at,Vector3 threat,float pose)
    {
        int mask=0;float rise=Mathf.Clamp01(pose)*.55f;
        if(!ClearAmbushRay(threat+Vector3.up*1.5f,at+Vector3.up*(1.05f+rise)))mask|=1;
        if(!ClearAmbushRay(threat+Vector3.up*1.5f,at+Vector3.up*(.8f+rise)))mask|=2;
        if(!ClearAmbushRay(threat+Vector3.up*1.5f,at+Vector3.up*(.5f+rise)))mask|=4;
        return mask;
    }
    private static bool OwnHoldPath(HoldAngleRuntime r)=>r.Owner!=null&&r.Path!=null&&ReferenceEquals(r.Owner.Mover.ActivePath,r.Path)
        &&r.Path.TimeStarted==r.PathAt&&Vector3.Distance(r.Path.Destination,r.Goal)<.02f;
    private void StopHoldPath(BotState s)
    {
        var r=s.HoldAngle;
        if(OwnHoldPath(r)&&r.Path is BotPathDataManual path){path.RequestEndSprint(ESprintUrgency.None,"COMET hold angle ended");combatStopPath.Invoke(path,new object[]{false,"COMET hold angle ended"});}
        r.Path=null;
    }
    private bool HoldAngleGaze(BotComponent b,BotState s,out Vector3 direction)
    {
        direction=default;var r=s.HoldAngle;if(!r.Policy.Active||r.Policy.Trace!.State=="planning")return false;
        RefreshHoldAngle(b,s,false);if(!r.Policy.Active)return false;
        direction=(r.Aim-b.Transform.WeaponRoot).normalized;return true;
    }
    private void EndHoldAngle(BotState s,string reason,bool reconsider=false)
    {
        var r=s.HoldAngle;if(!r.Policy.Active||config==null)return;
        // Cleanup must not query a destroyed Unity actor or a replacement enemy.
        var f=r.LastFrame??new HoldAngleFrame{Source=r.Source,Known=DP(r.Known),ObservedAt=r.ObservedAt};f.Now=Time.time;
        r.Policy.End(reason,f,config.HoldAngle,reconsider);FinishHoldAngle(s,reconsider);
    }
    private void FinishHoldAngle(BotState s,bool reconsider)
    {
        var r=s.HoldAngle;
        try {
            StopHoldPath(s);
            if(r.Owner!=null&&!r.Owner.IsDead&&r.OwnPose&&Math.Abs(r.Owner.Mover.Pose.PoseValue.TargetValue-r.AppliedPose)<.01)
                r.Owner.Mover.SetTargetPose(r.OldPose);
            if(reconsider&&s.ActiveLabel=="COMET:Hold")s.Engine.Fail(Tactic.Hold,Time.time,r.Policy.Trace!.Reason);
            RecordHoldAngle(s,true);
        }finally{r.Drop();}
    }
    private void HoldAngleError(BotState s,Exception ex){s.HoldAngle.Fault=true;EndHoldAngle(s,"hold-angle-adapter-error");Logger.LogWarning("M08 yielded: "+ex.GetBaseException().Message);}
    private void RecordHoldAngle(BotState s,bool force=false)
    {
        var r=s.HoldAngle;var t=r.Policy.Trace;if(t==null)return;
        string key=t.Episode+":"+t.State+":"+t.Reason+":"+t.PoseRequested+":"+t.MoveRequested;
        if(!force&&key==r.LogKey&&(!r.Policy.Active||Time.time-r.LogAt<.1f))return;
        r.LogKey=key;r.LogAt=Time.time;
        var json=Diagnostics.Serialize(new{schema=1,session,sequence=++sequence,feature="M08",stage="evaluated",decision="HoldAngle",reason=t.Reason,
            realtime=Time.realtimeSinceStartup,utc=DateTime.UtcNow.ToString("O"),configRevision=config?.Revision??0,profileId=s.ProfileId,name=s.Name,botInstance=s.Id,
            holdAngle=t,holdAngleConfig=config?.HoldAngle});
        diagnostics?.Write(json);Api.Decisions.Publish(json,ex=>{if(!subscriberWarned){subscriberWarned=true;Logger.LogWarning(ex.Message);}});
    }
}
