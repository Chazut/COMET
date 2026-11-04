using System;
using System.Linq;
using Comet.Core;
using EFT;
using Newtonsoft.Json;
using SAIN.Components;
using SAIN.Components.PlayerComponentSpace;
using SAIN.Models.Enums;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.EnemyClasses;
using SAIN.SAINComponent.Classes.Mover;
using UnityEngine;
using UnityEngine.AI;

namespace Comet.Client;

internal sealed class CornerPeekRuntime
{
    internal readonly CornerPeek Policy=new();
    internal BotComponent? Owner;
    internal IBotPathData? Path;
    internal Vector3 Goal,Origin,Near,Far,Direction,Previous,RouteFrom;
    internal float PathAt,LastCallback,NextSafety,LastSafety,LastSpeedAt,Speed,LogAt=-1000;
    internal int TargetInstance,Candidate,PlanStage;
    internal bool Fault,OwnEmpty,OwnSpeed,Hidden,Open,LaneSafe;
    internal float PreviousSpeed,AppliedSpeed;
    internal string LogKey="";
    internal void Drop(){Owner=null;Path=null;OwnEmpty=OwnSpeed=false;}
}

public sealed partial class Plugin
{
    private bool TryCornerPeek(BotComponent b,BotState s,CombatStepFrame f)
    {
        var r=s.CornerPeek;var c=config!.CornerPeek;
        if(r.Fault||!c.Enabled||Time.time<r.Policy.NextAttempt)return false;
        var personality=b.Info.Personality.ToString();
        bool bold=personality is "Chad" or "GigaChad" ||personality=="Normal"&&CornerBold(b.ProfileId);
        if(!r.Policy.Begin(f,s.CombatStep.Policy.Trace!.Episode,c,jumpRandom.NextDouble(),jumpRandom.NextDouble(),jumpRandom.NextDouble(),jumpRandom.NextDouble(),bold))
        {RecordCornerPeek(s);return false;}
        // Take the one already rolled M01 opportunity; M02 is not started for this sequence.
        EndCombatLean(s,"lean-preserve-corner-peek");
        EndCombatStep(s,"movement-delegated-to-corner-peek");
        r.Owner=b;r.Origin=r.Previous=b.Position;r.Candidate=r.PlanStage=0;r.Speed=0;r.LastSpeedAt=r.LastCallback=Time.time;
        r.TargetInstance=System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(b.GoalEnemy);
        r.Path=null;r.OwnEmpty=false;r.LastSafety=-1000;r.NextSafety=0;
        if(!StopCornerRoute(s,true)){EndCornerPeek(s,"corner-native-route-taken-over");return true;}
        RecordCornerPeek(s,true);return true;
    }
    private static bool CornerBold(string id){unchecked {int hash=17;foreach(char c in id)hash=hash*31+c;return (hash&1)==0;}}
    private string CornerGate(BotComponent b,BotState s,CombatStepFrame f)
    {
        var r=s.CornerPeek;var t=r.Policy.Trace!;
        if(config==null||!config.CornerPeek.Enabled||!config.CombatStep.Enabled||!f.Enabled||!f.Pmc||!f.Authority||!f.Active||f.Ghost)return "corner-out-of-scope";
        if(b.GoalEnemy==null||System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(b.GoalEnemy)!=r.TargetInstance||f.Target!=t.Target||f.Action!=t.Action)return "corner-native-action-or-contact-changed";
        if(f.Busy||f.Urgent||!f.Ready||f.Immobile||f.ProtectedCover)return "corner-native-priority";
        if(!f.Grounded&&!(t.State=="peek"&&t.Jumps==1))return "corner-unexpected-airborne";
        if(f.Sprinting&&t.State!="crossing")return "corner-unexpected-sprint";
        if(QueuedCombatMovement(b))return "corner-native-route-queued";
        if(Time.time-r.LastCallback>.4f)return "corner-action-callback-expired";
        int scanned=0;
        foreach(var e in b.EnemyController.KnownEnemies) {
            if(++scanned>32)return "corner-threat-list-truncated";
            if(e!=null&&!ReferenceEquals(e,b.GoalEnemy)&&(e.IsVisible||e.Status.ShotAtMeRecently))return "corner-new-immediate-threat";
        }
        return "";
    }
    private CombatStepFrame CornerFrame(BotComponent b,BotState s)
    {
        var f=CombatStepFrame(b,s);
        // Never use a hidden contact's live transform or refreshed shared position to aim this maneuver.
        if(!f.Visible){f.Known=s.CornerPeek.Policy.Known;f.Distance=(f.Position-f.Known).Length;}
        return f;
    }
    private bool KeepCornerPeek(BotComponent b,BotState s,Enemy enemy,ref ECombatDecision result,ref bool succeeded)
    {
        var r=s.CornerPeek;if(!r.Policy.Active||enemy.IsVisible)return false;
        try {
            RefreshCornerPeek(b,s,false);
            if(!r.Policy.MayKeep(Time.time,config!.CornerPeek)||!r.LaneSafe||Time.time-r.LastSafety>.2f)return false;
            // Only the intentional short occlusion retains the existing native shooting action.
            // Self/squad decisions are still selected by SAIN's decision manager, outside this hook.
            result=r.Policy.Trace!.Action=="DogFight"?ECombatDecision.DogFight:ECombatDecision.StandAndShoot;succeeded=true;
            b.Steering.LookToPoint(VP(r.Policy.Known)+Vector3.up*1.3f);return true;
        }catch(Exception ex){CornerError(s,ex);return false;}
    }
    private void RefreshCornerPeek(BotComponent b,BotState s,bool mayPlan)
    {
        var r=s.CornerPeek;if(!r.Policy.Active)return;
        if(config==null||b==null||b.IsDead){EndCornerPeek(s,"corner-bot-ended");return;}
        var f=CornerFrame(b,s);string why=CornerGate(b,s,f);
        if(why.Length>0){EndCornerPeek(s,why);return;}
        if(Time.time>r.LastSpeedAt){r.Speed=(b.Position-r.Previous).magnitude/(Time.time-r.LastSpeedAt);r.Previous=b.Position;r.LastSpeedAt=Time.time;}
        bool arrived=r.Path!=null&&Vector3.Distance(b.Position,r.Goal)<=.18f;
        bool valid=r.Path==null?(!r.OwnEmpty||b.Mover.ActivePath==null):OwnCornerPath(r)
            ||b.Mover.ActivePath==null&&r.Path.Status==EBotMoveStatus.Complete&&arrived;
        // Ownership and the hard deadline cannot wait for a geometry budget slot.
        if(!valid||Time.time>=r.Policy.Deadline){EndCornerPeek(s,!valid?"corner-native-route-taken-over":"corner-sequence-time-limit");return;}
        if(r.Policy.Moving&&!CornerPeek.Lane(DP(r.RouteFrom),DP(r.Goal),DP(b.Position),r.Policy.Trace!.Jumps>0))
        {EndCornerPeek(s,"corner-left-verified-lane");return;}
        if(r.Policy.Trace!.State!="planning"&&Time.time>=r.NextSafety) {
            if(!doorBudget.TryBegin(Time.frameCount)){if(Time.time-r.LastSafety>.2f)EndCornerPeek(s,"corner-safety-budget-expired");return;}
            doorClock.Restart();
            try {why=CornerSafety(b,s);r.LaneSafe=why.Length==0;r.LastSafety=Time.time;r.NextSafety=Time.time+.1f;
                if(why.Length>0){EndCornerPeek(s,why);return;}}
            finally{doorClock.Stop();doorBudget.Charge(doorClock.Elapsed.TotalMilliseconds);}
        }
        var before=r.Policy.Trace.State;
        if(!r.Policy.Observe(new(){Combat=f,RouteValid=valid,AtGoal=arrived,Speed=r.Speed,Grounded=f.Grounded,SinceHit=b.Medical.TimeSinceShot,
            GeometryFresh=Time.time-r.LastSafety<=.2f,LaneSafe=r.LaneSafe,Hidden=r.Hidden,Open=r.Open},config.CornerPeek,""))
        {FinishCornerPeek(s);return;}
        if(r.Policy.Trace.State=="planning") {
            if(mayPlan&&doorBudget.TryBegin(Time.frameCount)) {
                doorClock.Restart();try{PlanCornerPeek(b,s);}finally{doorClock.Stop();doorBudget.Charge(doorClock.Elapsed.TotalMilliseconds);}
            }
            RecordCornerPeek(s);return;
        }
        if(r.Policy.Holding){if(!StopCornerRoute(s)){EndCornerPeek(s,"corner-route-taken-over");return;}if(r.OwnEmpty)b.Player.Move(Vector2.zero);}
        if(!f.Visible)b.Steering.LookToPoint(VP(r.Policy.Known)+Vector3.up*1.3f);
        if(mayPlan&&r.Policy.WantsRoute) {
            if(!doorBudget.TryBegin(Time.frameCount)){RecordCornerPeek(s);return;}
            doorClock.Restart();try{RequestCornerRoute(b,s);}finally{doorClock.Stop();doorBudget.Charge(doorClock.Elapsed.TotalMilliseconds);}
        }
        // Only a real outward speed can launch the one permitted jump. SAIN retains all firing rules.
        if(r.Policy.Trace.State=="peek"&&r.Policy.Trace.Style=="jump"&&r.Policy.Trace.Jumps==0) {
            var direction=r.Goal-b.Position;direction.y=0;
            var velocity=b.Player.Velocity;velocity.y=0;
            if(r.Policy.MayJump(Time.time,Vector3.Dot(velocity,direction.normalized),direction.magnitude,f.Grounded,b.Player.MovementContext.CanJump,b.Player.Physical.Stamina.NormalValue*100)) {
                if(doorBudget.TryBegin(Time.frameCount)) {
                    doorClock.Restart();try {
                        var lane=RunVolume(b,r.RouteFrom,r.Goal,true);
                        if(lane.Length==0)r.Policy.JumpAccepted(ControlledJump(b,s,"M03","corner-peek",direction,r.Goal,1.1,30,true));else r.Policy.Fallback(lane);
                    }finally{doorClock.Stop();doorBudget.Charge(doorClock.Elapsed.TotalMilliseconds);}
                }
            } else if(direction.magnitude<.35f)r.Policy.Fallback("insufficient observed run-up");
        }
        RecordCornerPeek(s,before!=r.Policy.Trace.State);
    }
    private void PlanCornerPeek(BotComponent b,BotState s)
    {
        var r=s.CornerPeek;var p=r.Policy;var t=p.Trace!;var c=config!.CornerPeek;
        if(Vector3.Distance(b.Position,r.Origin)>.15f){EndCornerPeek(s,"corner-planning-origin-moved");return;}
        // Native Stop only releases the route. Let real inertia settle before starting a firing window.
        if(r.Speed>.35f||!b.Player.MovementContext.IsGrounded)return;
        var known=VP(p.Known);
        if(!CornerOpen(b,r.Origin,known)){EndCornerPeek(s,"corner-initial-angle-blocked");return;}
        if(r.PlanStage==0) {
            if(r.Candidate>=6){EndCornerPeek(s,"corner-no-local-cover");return;}
            var forward=known-r.Origin;forward.y=0;if(forward.sqrMagnitude<.01f){EndCornerPeek(s,"corner-no-bearing");return;}
            int i=r.Candidate++;float distance=(float)c.MaxStep*(.5f+.25f*(i/2));
            var direction=Vector3.Cross(Vector3.up,forward.normalized)*(i%2==0?1:-1);
            var raw=r.Origin+direction*distance;
            if(!CornerRoute(b,r.Origin,raw,c.MaxStep,out var end,out _,out var why)||CombatProtection(end,known)!=7
                ||(why=CornerOtherThreats(b,r.Origin,end)).Length>0) {
                p.Alternative("cover "+i+": "+(why.Length>0?why:"body not protected"));RecordCornerPeek(s);return;
            }
            r.Near=end;r.Direction=direction;r.Candidate=0;r.PlanStage=1;return;
        }
        string style=t.Style;Vector3 hidden=r.Near;
        if(style=="run-by") {
            if(r.Candidate<3) {
                float distance=2.5f+r.Candidate++;var raw=r.Origin-r.Direction*distance;
                if(b.Player.MovementContext.CanSprint&&b.BotOwner.CanSprintPlayer&&b.Player.Physical.Stamina.NormalValue*100>=40
                    &&CornerRoute(b,r.Origin,raw,4.7,out var far,out _,out var why)&&CombatProtection(far,known)==7
                    &&CornerOpen(b,(r.Origin+far)*.5f,known)&&CornerOtherThreats(b,r.Origin,far).Length==0) {
                    hidden=r.Far=far;r.PlanStage=2;
                }else {p.Alternative("run-by candidate "+r.Candidate+": no safe covered crossing");return;}
            } else {p.Fallback("no safe far cover or sprint resources");style="step";}
        }
        if(style=="jump") {
            // Jump only where the upper lane is free and the raised angle actually reduces occlusion.
            bool benefit=CornerOpen(b,(r.Near+r.Origin)*.5f+Vector3.up*.5f,known)&&!CornerOpen(b,(r.Near+r.Origin)*.5f,known);
            if(!benefit||!b.Player.MovementContext.CanJump||b.Player.Physical.Stamina.NormalValue*100<30||RunVolume(b,r.Near,r.Origin,true).Length>0)
            {p.Fallback("no useful safe jump corridor or stamina");style="step";}
        }
        if(!StopCornerRoute(s)){EndCornerPeek(s,"corner-native-route-taken-over");return;}
        if(!p.Plan(DP(hidden),style,Time.time)){EndCornerPeek(s,"corner-plan-expired");return;}
        r.LaneSafe=true;r.Open=true;r.Hidden=false;r.LastSafety=Time.time;r.NextSafety=Time.time;RecordCornerPeek(s,true);
    }
    private bool CornerRoute(BotComponent b,Vector3 start,Vector3 raw,double radius,out Vector3 end,out NavMeshPath path,out string why)
    {
        end=raw;path=new();why="corner-no-local-navmesh";
        if(!NavMesh.SamplePosition(raw,out var sample,.1f,NavMesh.AllAreas))return false;end=sample.position;
        NavMesh.CalculatePath(start,end,NavMesh.AllAreas,path);
        if(!CombatStep.Route(DP(start),DP(start),DP(end),path.corners.Select(DP).ToArray(),path.status==NavMeshPathStatus.PathComplete,radius))
        {why="corner-no-short-complete-route";return false;}
        why=RunVolume(b,start,end);if(why.Length>0)return false;
        // Reverse route is checked too; navmesh links can be directional.
        var back=new NavMeshPath();NavMesh.CalculatePath(end,start,NavMesh.AllAreas,back);
        if(!CombatStep.Route(DP(end),DP(end),DP(start),back.corners.Select(DP).ToArray(),back.status==NavMeshPathStatus.PathComplete,radius))
        {why="corner-no-safe-return-route";return false;}
        return true;
    }
    private static bool CornerOpen(BotComponent b,Vector3 position,Vector3 known)
    {
        var offset=position-b.Position;var aim=known+Vector3.up*1.3f;
        return ClearAmbushRay(b.Transform.EyePosition+offset,aim)&&ClearAmbushRay(b.Transform.WeaponRoot+offset,aim);
    }
    private string CornerOtherThreats(BotComponent b,Vector3 from,Vector3 to)
    {
        int scanned=0,count=0;
        foreach(var e in b.EnemyController.KnownEnemies) {
            if(++scanned>32)return "corner-threat-list-truncated";
            if(e==null||ReferenceEquals(e,b.GoalEnemy)||!e.EnemyKnown||e.TimeSinceLastKnownUpdated>3||e.KnownPlaces.LastKnownPosition is not Vector3 known)continue;
            if(++count>4)return "corner-threat-budget-exceeded";
            if(!CombatStep.PreservesProtection(CombatProtection(from,known),CombatProtection((from+to)*.5f,known),CombatProtection(to,known)))return "corner-exposes-other-known-threat";
        }
        return "";
    }
    private string CornerSafety(BotComponent b,BotState s)
    {
        var r=s.CornerPeek;var p=r.Policy;var hidden=VP(p.HiddenPoint);var exposed=VP(p.Exposed);var known=VP(p.Known);
        // Full ground corridor remains checked during flight; actual motion has its separate vertical leash.
        string why=RunVolume(b,exposed,hidden,p.Trace!.Style=="jump");if(why.Length>0)return why;
        if(CombatProtection(hidden,known)!=7||!CornerOpen(b,exposed,known))return "corner-protection-or-firing-angle-lost";
        why=CornerOtherThreats(b,exposed,hidden);if(why.Length>0)return why;
        r.Hidden=CombatProtection(b.Position,known)==7;r.Open=CornerOpen(b,b.Position,known);return "";
    }
    private void RequestCornerRoute(BotComponent b,BotState s)
    {
        var r=s.CornerPeek;var p=r.Policy;bool outward=p.Trace!.State=="peek-pending";bool run=p.Trace.State=="crossing-pending";
        var goal=VP(outward?p.Exposed:p.HiddenPoint);
        if(!CornerRoute(b,b.Position,goal,run?4.7:config!.CornerPeek.MaxStep+.25,out var endpoint,out var route,out var why)
            ||Vector3.Distance(endpoint,goal)>.1f||CornerSafety(b,s).Length>0) {EndCornerPeek(s,why.Length>0?why:"corner-route-no-longer-safe");return;}
        if(run&&(!b.Player.MovementContext.CanSprint||!b.BotOwner.CanSprintPlayer||b.Player.Physical.Stamina.NormalValue*100<40))
        {EndCornerPeek(s,"corner-sprint-resources-lost");return;}
        if(!StopCornerRoute(s)){EndCornerPeek(s,"corner-native-route-taken-over");return;}
        bool accepted=run?b.Mover.RunToPointByWay(route,true,.1f,ESprintUrgency.High,false):b.Mover.WalkToPointByWay(route,true,.1f,false);
        r.Path=b.Mover.ActivePath;r.Goal=endpoint;r.PathAt=r.Path?.TimeStarted??-1;r.RouteFrom=b.Position;r.OwnEmpty=false;
        if(!accepted||!OwnCornerPath(r)||!p.Request(Time.time)){EndCornerPeek(s,"corner-movement-request-unconfirmed");return;}
        if(!r.OwnSpeed){r.PreviousSpeed=(float)combatNativeSpeed.GetValue(b.PlayerComponent.CharacterController);r.AppliedSpeed=Mathf.Clamp01(b.Info.FileSettings.Move.STRAFE_SPEED);r.OwnSpeed=true;}
        if(!run)b.Mover.SetTargetMoveSpeed(r.AppliedSpeed);
        r.LastSafety=Time.time;r.NextSafety=Time.time+.1f;RecordCornerPeek(s,true);
    }
    private static bool OwnCornerPath(CornerPeekRuntime r)=>r.Owner!=null&&r.Path!=null&&ReferenceEquals(r.Owner.Mover.ActivePath,r.Path)
        &&r.Path.TimeStarted==r.PathAt&&Vector3.Distance(r.Path.Destination,r.Goal)<.02f;
    private bool StopCornerRoute(BotState s,bool initial=false)
    {
        var r=s.CornerPeek;var b=r.Owner;if(b==null||QueuedCombatMovement(b))return false;
        var active=b.Mover.ActivePath;
        if(active!=null) {
            if(!OwnCornerPath(r)&&!initial)return false;
            if(active is not BotPathDataManual path)return false;
            // Initial takeover is only called from the same checked StandAndShoot/DogFight callback.
            path.RequestEndSprint(ESprintUrgency.None,"COMET corner hold");combatStopPath.Invoke(path,new object[]{false,"COMET corner hold"});
            if(b.Mover.ActivePath!=null)return false;
        }
        r.Path=null;r.OwnEmpty=true;return true;
    }
    private void EndCornerPeek(BotState s,string why){if(!s.CornerPeek.Policy.Active)return;s.CornerPeek.Policy.End(why,Time.time);FinishCornerPeek(s);}
    private void FinishCornerPeek(BotState s)
    {
        var r=s.CornerPeek;
        try {
            bool owned=OwnCornerPath(r);var b=r.Owner;
            if(owned&&r.Path is BotPathDataManual path){path.RequestEndSprint(ESprintUrgency.None,"COMET corner ended");combatStopPath.Invoke(path,new object[]{false,"COMET corner ended"});}
            if(b!=null&&!b.IsDead&&(owned||r.OwnEmpty)&&b.Mover.ActivePath==null&&!QueuedCombatMovement(b))b.Player.Move(Vector2.zero);
            if(r.OwnSpeed&&b!=null&&!b.IsDead&&Math.Abs((float)combatNativeSpeed.GetValue(b.PlayerComponent.CharacterController)-r.AppliedSpeed)<.001f)b.Mover.SetTargetMoveSpeed(r.PreviousSpeed);
        }finally{s.CombatStep.Policy.DeferUntil(r.Policy.NextAttempt);RecordCornerPeek(s,true);r.Drop();}
    }
    private void CornerError(BotState s,Exception ex){s.CornerPeek.Fault=true;EndCornerPeek(s,"corner-adapter-error");Logger.LogWarning("M03 yielded: "+ex.GetBaseException().Message);}
    private void RecordCornerPeek(BotState s,bool force=false)
    {
        var r=s.CornerPeek;var t=r.Policy.Trace;if(t==null)return;
        string key=t.Episode+":"+t.State+":"+t.Reason+":"+t.Jumps+":"+t.Alternatives.Length;
        if(!force&&key==r.LogKey&&(!r.Policy.Active||Time.time-r.LogAt<.1f))return;
        r.LogKey=key;r.LogAt=Time.time;
        var json=JsonConvert.SerializeObject(new{schema=1,session,sequence=++sequence,feature="M03",stage="evaluated",decision="CornerPeek",reason=t.Reason,
            realtime=Time.realtimeSinceStartup,utc=DateTime.UtcNow.ToString("O"),configRevision=config?.Revision??0,profileId=s.ProfileId,name=s.Name,botInstance=s.Id,
            cornerPeek=t,cornerPeekConfig=config?.CornerPeek},Diagnostics.Json);
        diagnostics?.Write(json);Api.Decisions.Publish(json,ex=>{if(!subscriberWarned){subscriberWarned=true;Logger.LogWarning(ex.Message);}});
    }
}
