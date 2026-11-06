using System;
using System.Collections.Generic;
using System.Linq;
using Comet.Core;
using EFT;
using HarmonyLib;
using Newtonsoft.Json;
using SAIN.Components;
using SAIN.Models.Enums;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes;
using SAIN.SAINComponent.Classes.EnemyClasses;
using SAIN.SAINComponent.Classes.Decision;
using SAIN.SAINComponent.Classes.Mover;
using SAIN.SAINComponent.Classes.WeaponFunction;
using SAIN.SAINComponent.SubComponents.CoverFinder;
using UnityEngine;
using UnityEngine.AI;

namespace Comet.Client;

internal sealed class RelocationRuntime
{
    internal readonly OutdoorRelocation Policy=new();
    internal BotComponent? Owner;
    internal Enemy? Enemy;
    internal CoverPoint? BestCover,Selected;
    internal RelocationCandidate? Best;
    internal readonly List<CoverPoint> Cached=new();
    internal readonly List<Vector3> Threats=new();
    internal readonly Dictionary<IBotPathData,NavMeshPath> Routes=new();
    internal float Next,NextProbe,LastSafety=-1000,RequestedAt,LogAt=-1000,ReleaseAt=-1;
    internal int Candidate,RequestFrame=-1,ProbeFrame=-1;
    internal ECombatDecision Expected;
    internal string LogKey="",GrenadeItem="";
    internal bool Starting,Fault,ThrowCall,GuardPassed,NativeMoveStarted,CoverCall;
    internal int ProjectileId=-1;
    internal WeakReference<Grenade>? Projectile;
    internal Vector3 Blast;
    internal double BlastRadius;
    internal void Drop(){Owner=null;Enemy=null;BestCover=Selected=null;Best=null;Cached.Clear();Threats.Clear();Routes.Clear();Projectile=null;ProjectileId=-1;ReleaseAt=-1;BlastRadius=0;GrenadeItem="";RequestFrame=-1;NativeMoveStarted=CoverCall=false;}
}

public sealed partial class Plugin
{
    private static System.Reflection.MethodInfo relocationClearPath=null!,relocationPathStatus=null!;
    private static void PatchOutdoorRelocation(Harmony h)
    {
        h.Patch(AccessTools.Method(typeof(GrenadeThrowDecider),"TryThrowGrenade",Type.EmptyTypes)
            ??throw new MissingMethodException("M13 native grenade throw"),prefix:new HarmonyMethod(typeof(Plugin),nameof(RelocationThrowGuard)){priority=Priority.First});
        relocationClearPath=AccessTools.Method(typeof(BotPathDataManual),"ClearCachedData")??throw new MissingMethodException("M13 queued path cleanup");
        relocationPathStatus=AccessTools.PropertySetter(typeof(BotPathDataManual),"Status")??throw new MissingMethodException("M13 queued path state");
        h.Patch(AccessTools.Method(typeof(SAINMoverClass),"GoToCoverPoint",new[]{typeof(CoverPoint),typeof(bool),typeof(ESprintUrgency)}),
            prefix:new HarmonyMethod(typeof(Plugin),nameof(RelocationCoverCall)),finalizer:new HarmonyMethod(typeof(Plugin),nameof(RelocationCoverFinally)));
        h.Patch(AccessTools.Method(typeof(BotPathDataManual),"Initialize"),postfix:new HarmonyMethod(typeof(Plugin),nameof(RelocationPathInitialized)));
    }
    private static void RelocationCoverCall(SAINMoverClass __instance,CoverPoint point,out bool __state)
    {
        __state=false;var p=Instance;
        if(p==null||!p.states.TryGetValue(__instance.Bot,out var s))return;
        var r=s.Relocation;__state=r.CoverCall;
        r.CoverCall=r.Policy.Active&&r.Policy.Trace?.State=="moving"&&ReferenceEquals(point,r.Selected);
    }
    private static void RelocationCoverFinally(SAINMoverClass __instance,bool __state)
    {if(Instance?.states.TryGetValue(__instance.Bot,out var s)==true)s.Relocation.CoverCall=__state;}
    private static void RelocationPathInitialized(BotPathDataManual __instance,NavMeshPath path)
    {
        var p=Instance;
        if(__instance.PathFinder is not SAINMoverClass mover||p==null||!p.states.TryGetValue(mover.Bot,out var s))return;
        var r=s.Relocation;
        // Native paths are reused. Every initialization revokes the previous ownership, even at the same destination.
        r.Routes.Remove(__instance);
        if(r.CoverCall&&r.Policy.Active&&ReferenceEquals(path,r.Selected?.PathToPoint)&&r.Routes.Count<2)r.Routes[__instance]=path;
    }
    private static bool OwnRelocationRoute(RelocationRuntime r,IBotPathData? path)=>path!=null&&r.Routes.TryGetValue(path,out var mesh)
        &&ReferenceEquals(mesh,path.NavMeshPath)&&Vector3.Distance(path.Destination,VP(r.Policy.Goal))<.35f;
    private bool RelocationScope(BotComponent b,BotState s)=>ContactSafety.AllowsGoal(b)&&config?.Enabled==true&&config.OutdoorRelocation.Enabled
        &&NativeAdapter.IsPmcAi(b)&&b.BotActive&&!b.GameEnding&&Authority&&!Ghost(s.ProfileId)&&b.SAINLayersActive;
    private void ObserveRelocationContact(BotComponent b,BotState s)
    {
        var e=b.GoalEnemy;if(e==null)return;
        string key=RelocationKey(e);double seen=e.Seen?e.Vision.TimeFirstSeen:-1;
        // Local incoming-fire/impact timestamps, never the opponent's perception or current hidden transform.
        s.Relocation.Policy.History.Observe(key,e.Status.TimeFirstShotAtMe,seen,Time.time);
        s.Relocation.Policy.History.Observe(key,e.Status.TimeLastShotMe,seen,Time.time);
        s.Relocation.Policy.History.Observe(key,e.Status.TimeLastShotAtMe,seen,Time.time);
    }
    private static string RelocationKey(Enemy e)=>e.EnemyProfileId+":"+System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(e);
    private RelocationFrame RelocationFrame(BotComponent b,BotState s)
    {
        var r=s.Relocation;var e=b.GoalEnemy;var known=e?.KnownPlaces.LastKnownPosition;
        var f=new RelocationFrame{Now=Time.time,Target=e?.EnemyProfileId??"",Key=e==null?"":RelocationKey(e),Position=DP(b.Position),Known=DP(known??b.Position),
            ContactAge=e?.TimeSinceLastKnownUpdated??999,SightAge=e?.Seen==true?e.TimeSinceSeen:999,Visible=e?.IsVisible==true,
            Source=known.HasValue?RelocationContactSource(e!):"",InCover=b.Cover.CoverInUse!=null,
            Speed=b.Player.Velocity.magnitude,HandsReady=VisibleAdapter.WeaponReady(b),Distance=known.HasValue?Vector3.Distance(b.Position,known.Value):0,
            Hurt=b.Memory.Health.HealthStatus!=ETagStatus.Healthy,
            HealthyPusher=b.Memory.Health.HealthStatus==ETagStatus.Healthy&&b.Info.PersonalitySettings.Rush.CanRushEnemyReloadHeal};
        var env=s.Environment.Read(Time.time,b.Position.x,b.Position.y,b.Position.z,config!.EnvironmentContext,"outdoor-relocation");
        // Disabled P10 keeps a conservative local EFT fallback; a roof alone is not an indoor classification.
        f.Outdoor=config.EnvironmentContext.Enabled?env.Reported=="outdoor"&&!env.Conflict
            :b.Player.Environment==EnvironmentType.Outdoor&&b.Player.AIData?.EnvironmentId==0;
        var opponent=s.Situation?.Opponent;
        f.LongWeapon=opponent?.Target==f.Target&&opponent.Evidence.Any(x=>x.Active&&x.Kind=="weapon"
            &&(x.Value=="sniperRifle"||x.Value=="marksmanRifle")&&Time.time-x.ObservedAt>=0&&Time.time-x.ObservedAt<=config.Opponent.WeaponMemory)==true;
        int mates=0;
        if(b.Squad?.Members!=null)foreach(var mate in b.Squad.Members.Values) {
            if(++mates>16)break;
            if(mate!=null&&!ReferenceEquals(mate,b)&&!mate.IsDead&&mate.GoalEnemy?.EnemyProfileId==f.Target
                &&(mate.GoalEnemy.IsVisible||mate.GoalEnemy.Seen&&mate.GoalEnemy.TimeSinceSeen<5)){f.MatesEngaging=true;break;}
        }
        if(!RelocationScope(b,s))f.Block="relocate-ineligible";
        else if(e==null||!known.HasValue||!e.EnemyKnown)f.Block="relocate-contact-unavailable";
        else if(r.Policy.Active&&!ReferenceEquals(e,r.Enemy))f.Block="relocate-contact-instance-changed";
        else if(b.Decision.CurrentSelfDecision!=ESelfActionType.None||b.Decision.CurrentSquadDecision!=ESquadDecision.None
            ||FakeMedicalPriority(b)||b.Grenade.GrenadeDangerPoint!=null||b.Flashed.IsFlashed||b.Suppression.IsHeavySuppressed
            ||s.Fear.Current.Panic||b.DoorOpener.Interacting||b.BotOwner.DoorOpener.Interacting||b.Player.IsInPronePose)
            f.Block="relocate-native-emergency-or-interaction";
        else if(s.CoverFeint.Policy.Active||s.Entry.Policy.Active||s.Door.Policy.Active||s.Jump.Policy.Active||s.RunBy.Policy.Active||s.StepPeek.Policy.Active
            ||s.Fake.Policy.Active||s.RoomClear.Policy.Active||s.Emergency.Policy.Active||s.Support!=null||s.Reservation.Lease!=null
            ||s.CornerPeek.Policy.Active||s.CornerChase.Policy.Active||s.CombatStep.Policy.Active||s.HoldAngle.Policy.Active||s.Ambush.Policy.Active)
            f.Block="relocate-other-tactic";
        else if(r.Policy.Trace?.State!="throwing"&&(!f.HandsReady||b.BotOwner.WeaponManager.Grenades.ThrowindNow))f.Block="relocate-hands-busy";
        else if(b.Medical.TimeSinceShot<1||b.Suppression.IsSuppressed&&r.Policy.Trace?.State!="moving")f.Block="relocate-under-immediate-fire";
        int count=0;
        foreach(var other in b.EnemyController.KnownEnemies) {
            if(++count>32){f.Block="relocate-threat-list-truncated";break;}
            if(other!=null&&!ReferenceEquals(other,e)&&(other.IsVisible||other.Status.ShotAtMeRecently)){f.Block="relocate-other-immediate-threat";break;}
        }
        if(r.Policy.Trace?.State=="moving") {
            var path=b.Mover.ActivePath;var cover=b.Cover.CoverInUse;
            f.Arrived=ReferenceEquals(cover,r.Selected)&&Vector3.Distance(b.Position,VP(r.Policy.Goal))<=.8f;
            // WalkToPointByWay does not populate IBotPathData.PathStatus in official SAIN. Check its actual mesh.
            f.RouteOwned=f.Arrived||!r.NativeMoveStarted||OwnRelocationRoute(r,path)&&path!.NavMeshPath.status==NavMeshPathStatus.PathComplete
                &&ReferenceEquals(b.Cover.CoverPoint_MovingTo,r.Selected);
            if(r.NativeMoveStarted&&Time.time-r.RequestedAt<.5f&&r.Routes.Keys.Any(x=>OwnRelocationRoute(r,x)&&x.Status==EBotMoveStatus.ReadyToMove))f.RouteOwned=true;
            // Native arrival can finish the path one tick before CoverInUse is updated; this does not declare arrival.
            if(path==null&&ReferenceEquals(b.Cover.CoverPoint_MovingTo,r.Selected)&&Vector3.Distance(b.Position,VP(r.Policy.Goal))<=.8f)f.RouteOwned=true;
        }
        return f;
    }
    private static string RelocationContactSource(Enemy e)
    {
        var k=e.KnownPlaces;
        return ReferenceEquals(k.LastKnownPlace,k.LastSeenPlace)?"seen":ReferenceEquals(k.LastKnownPlace,k.LastHeardPlace)?"heard"
            :ReferenceEquals(k.LastKnownPlace,k.LastSquadSeenPlace)?"shared-seen":ReferenceEquals(k.LastKnownPlace,k.LastSquadHeardPlace)?"shared-heard":"sain-last-known";
    }
    private bool ChooseRelocation(BotComponent b,Enemy e,BotState s,ref ECombatDecision result,ref bool succeeded)
    {
        var r=s.Relocation;if(config==null||r.Fault)return false;
        try {
            ObserveRelocationContact(b,s);
            if(!r.Policy.Active) {
                if(Time.time<r.Next||s.Sight.Current!=null)return false;r.Next=Time.time+.4f;
                var f=RelocationFrame(b,s);var previous=r.Policy.Trace;
                if(!r.Policy.Begin(f,config.OutdoorRelocation,jumpRandom.NextDouble)){if(!ReferenceEquals(previous,r.Policy.Trace))RecordRelocation(s,true);return false;}
                // Release the prior COMET selection without destroying this newly created relocation policy.
                r.Starting=true;try{Release(s,"relocate-start");}finally{r.Starting=false;}
                r.Owner=b;r.Enemy=e;r.Candidate=0;r.Best=null;r.BestCover=null;r.Selected=null;r.NextProbe=0;r.LastSafety=-1000;r.NativeMoveStarted=false;
                r.Cached.Clear();foreach(var cp in b.Cover.CoverPoints){if(r.Cached.Count>=24)break;if(cp!=null&&!cp.CoverData.IsBad)r.Cached.Add(cp);}
                EnsureClearEvents();RecordRelocation(s,true);
            }
            RefreshRelocation(b,s,true);
            if(!r.Policy.Active)return false;
            r.Expected=r.Policy.Trace!.State=="moving"?ECombatDecision.SeekCover:r.Policy.Trace.State=="throwing"?ECombatDecision.ThrowGrenade:ECombatDecision.Freeze;
            r.RequestFrame=Time.frameCount;result=r.Expected;succeeded=true;return true;
        }catch(Exception ex){RelocationError(s,ex);return false;}
    }
    private bool RelocationAccepted(BotDecisionManager manager,BotState s)
    {
        var r=s.Relocation;if(!r.Policy.Active)return false;
        bool ours=(r.RequestFrame==Time.frameCount||r.Policy.Trace?.State=="throwing"&&manager.Bot.BotOwner.WeaponManager.Grenades.ThrowindNow)
            &&manager.CurrentCombatDecision==r.Expected
            &&manager.CurrentSelfDecision==ESelfActionType.None&&manager.CurrentSquadDecision==ESquadDecision.None;
        r.RequestFrame=-1;r.Policy.NativeAccepted(ours);
        if(!ours){EndRelocation(s,"relocate-native-priority");return false;}
        RecordRelocation(s);return true; // Preserve RR's native behavior label; M13 appears as contextual telemetry.
    }
    private void SampleRelocation(BotComponent b,BotState s)
    {
        var r=s.Relocation;if(r.Fault)return;
        if(!RelocationScope(b,s)){EndRelocation(s,"relocate-ineligible",true);return;}
        ObserveRelocationContact(b,s);
        if(r.Policy.Active)RefreshRelocation(b,s,false);
    }
    private void RefreshRelocation(BotComponent b,BotState s,bool decisionTick)
    {
        var r=s.Relocation;if(!r.Policy.Active||config==null)return;
        var f=RelocationFrame(b,s);
        if(!r.Policy.Observe(f,config.OutdoorRelocation)){FinishRelocation(s);return;}
        var t=r.Policy.Trace!;
        if(t.State=="planning"&&r.ProbeFrame!=Time.frameCount&&doorBudget.TryBegin(Time.frameCount)) {
            r.ProbeFrame=Time.frameCount;var clock=System.Diagnostics.Stopwatch.StartNew();
            try{PlanRelocation(b,s);}finally{clock.Stop();doorBudget.Charge(clock.Elapsed.TotalMilliseconds);}
        }
        if(!r.Policy.Active)return;
        // The chooser initiates a native throw only after a fresh complete destination check.
        if(t.State=="ready"&&decisionTick&&Time.time>=r.NextProbe&&doorBudget.TryBegin(Time.frameCount)) {
            var clock=System.Diagnostics.Stopwatch.StartNew();
            try {
                if(!RecheckRelocation(b,s)){EndRelocation(s,"relocate-selected-cover-no-longer-safe");return;}
                string why="disabled";
                if(config.OutdoorRelocation.Grenade&&f.Distance>8&&f.Distance<45)why=PrepareRelocationGrenade(b,s);
                if(why.Length==0) {
                    var manager=b.BotOwner.WeaponManager.Grenades;r.GrenadeItem=manager.grenade.Id.ToString();r.ThrowCall=true;r.GuardPassed=false;
                    try {bool ok=NativeAdapter.Throw(b,b.GoalEnemy,out var reason);r.Policy.Throw(ok&&r.GuardPassed,f.Now,ok&&r.GuardPassed?"native-accepted":reason);
                        if(ok&&r.GuardPassed&&r.ReleaseAt>=f.Now)r.Policy.Projectile(r.ReleaseAt);}
                    finally{r.ThrowCall=false;}
                }else t.GrenadeOutcome=why;
                if(t.State=="ready"){r.BlastRadius=0;StartRelocationMove(b,s);}
            }finally{clock.Stop();doorBudget.Charge(clock.Elapsed.TotalMilliseconds);}
        }else if(t.State=="throwing"&&r.Policy.CanMove(f)&&decisionTick&&doorBudget.TryBegin(Time.frameCount)) {
            var clock=System.Diagnostics.Stopwatch.StartNew();
            try{if(RecheckRelocation(b,s)&&RelocationBlastSafe(b,s))StartRelocationMove(b,s);else EndRelocation(s,"relocate-post-throw-route-unsafe");}
            finally{clock.Stop();doorBudget.Charge(clock.Elapsed.TotalMilliseconds);}
        }else if(t.State=="moving"&&Time.time>=r.NextProbe) {
            if(doorBudget.TryBegin(Time.frameCount)) {
                var clock=System.Diagnostics.Stopwatch.StartNew();
                try{if(!RecheckRelocation(b,s)||!RelocationBlastSafe(b,s)){EndRelocation(s,"relocate-route-safety-lost");return;}}
                finally{clock.Stop();doorBudget.Charge(clock.Elapsed.TotalMilliseconds);}
            }else if(Time.time-r.LastSafety>.75f){EndRelocation(s,"relocate-safety-budget-expired");return;}
        }
        RecordRelocation(s);
    }
    private void PlanRelocation(BotComponent b,BotState s)
    {
        var r=s.Relocation;var c=config!.OutdoorRelocation;int i=r.Candidate++;
        int total=r.Cached.Count+48;
        if(i>=total) {
            if(r.Best==null||r.BestCover==null){EndRelocation(s,"relocate-no-better-cover");return;}
            r.Selected=r.BestCover;
            if(!RecheckRelocation(b,s)||!r.Policy.Select(r.Best,c)){EndRelocation(s,"relocate-selected-cover-expired");return;}
            return;
        }
        CoverPoint? point=i<r.Cached.Count?r.Cached[i]:null;Vector3 raw;
        string id;
        if(point!=null){raw=point.Position;id="cached-"+i;}
        else {int index=i-r.Cached.Count;float distance=(float)(c.MinDistance+(c.MaxDistance-c.MinDistance)*(index/12)/3d);
            raw=VP(r.Policy.Origin)+Quaternion.Euler(0,(index%12)*30,0)*Vector3.forward*distance;id="sample-"+index;}
        var v=EvaluateRelocationPoint(b,s,raw,id,out var native);
        r.Policy.Candidate(v,c);
        if(OutdoorRelocation.Reject(v,c).Length==0&&(r.Best==null||v.Score(c)>r.Best.Score(c))) {r.Best=v;r.BestCover=native;}
    }
    private RelocationCandidate EvaluateRelocationPoint(BotComponent b,BotState s,Vector3 raw,string id,out CoverPoint? native)
    {
        native=null;var r=s.Relocation;var c=config!.OutdoorRelocation;var origin=VP(r.Policy.Origin);var known=VP(r.Policy.Known);
        var v=new RelocationCandidate{Id=id};
        if(!NavMesh.SamplePosition(raw,out var hit,.6f,NavMesh.AllAreas)||Math.Abs(hit.position.y-origin.y)>1.2f||Vector3.Distance(raw,hit.position)>1)
        {v.Refusal="relocate-navmesh-or-floor";return v;}
        var goal=hit.position;v.Position=DP(goal);v.Distance=Vector3.Distance(origin,goal);
        if(v.Distance<c.MinDistance||v.Distance>c.MaxDistance+.001){v.Refusal="relocate-distance";return v;}
        if(Vector3.Distance(goal,known)<Math.Max(4,Vector3.Distance(origin,known)*.7f)){v.Refusal="relocate-would-rush-threat";return v;}
        int before=HoldProtection(origin,known,1),after=HoldProtection(goal,known,1);
        v.OriginalProtection=Bits(before);v.Protection=Bits(after);
        if(v.Protection<2||v.Protection<v.OriginalProtection){v.Refusal="relocate-weaker-cover";return v;}
        if(!Physics.Linecast(known+Vector3.up*1.5f,goal+Vector3.up*1.35f,out var obstacle,LayersMaskController.HighPolyWithTerrainMask,QueryTriggerInteraction.Ignore)
            ||obstacle.collider==null){v.Refusal="relocate-no-cover-surface";return v;}
        var route=new PathData();route.CalcPath(b.Position,goal);v.PathLength=route.PathLength;
        var corners=route.Path?.corners;
        if(route.Path?.status!=NavMeshPathStatus.PathComplete||corners==null||corners.Length<2||corners.Length>32||v.PathLength>c.MaxPath
            ||Vector3.Distance(corners[corners.Length-1],goal)>.3f){v.Refusal="relocate-no-complete-bounded-route";return v;}
        if(!RelocationRouteSafety(b,s,corners,goal,out var exposure,out var refusal)){v.Refusal=refusal;return v;}
        v.Exposure=exposure;
        var from=origin-known;var to=goal-known;from.y=to.y=0;v.Angle=Vector3.Angle(from,to);v.Safe=true;
        native=new CoverPoint(b,obstacle.collider,route,goal);return v;
    }
    private static int Bits(int n)=>(n&1)+((n>>1)&1)+((n>>2)&1);
    private bool RelocationRouteSafety(BotComponent b,BotState s,Vector3[] corners,Vector3 goal,out double exposure,out string why)
    {
        exposure=0;why="";var r=s.Relocation;var origin=VP(r.Policy.Origin);var known=VP(r.Policy.Known);
        // Bounded route segments and threats. Endpoint body clearance plus the next physical segment cover doors and allies.
        if(RunVolume(b,b.Position,corners[1]).Length>0||RunVolume(b,goal,goal).Length>0){why="relocate-body-door-or-ally-blocked";return false;}
        r.Threats.Clear();int scanned=0;
        foreach(var enemy in b.EnemyController.KnownEnemies) {
            if(++scanned>32){why="relocate-threat-list-truncated";return false;}
            if(enemy==null||ReferenceEquals(enemy,b.GoalEnemy)||!enemy.EnemyKnown||enemy.TimeSinceLastKnownUpdated>5||enemy.KnownPlaces.LastKnownPosition is not Vector3 other)continue;
            if(r.Threats.Count>=3){why="relocate-too-many-known-threats";return false;}r.Threats.Add(other);
        }
        float length=0;for(int i=1;i<corners.Length;i++)length+=Vector3.Distance(corners[i-1],corners[i]);
        if(length>config!.OutdoorRelocation.MaxPath){why="relocate-route-too-long";return false;}
        for(int step=1;step<=3;step++) {
            var sample=RelocationRoutePoint(corners,length*step/4);
            exposure+=(3-Bits(HoldProtection(sample,known,1)))/9d;
            foreach(var threat in r.Threats) {
                int protection=HoldProtection(origin,threat,1);
                if(!CombatStep.PreservesProtection(protection,HoldProtection(sample,threat,1),HoldProtection(goal,threat,1)))
                {why="relocate-exposes-other-known-threat";return false;}
            }
        }
        if(exposure>1d/3+.001){why="relocate-exposed-route";return false;}
        return true;
    }
    private static Vector3 RelocationRoutePoint(Vector3[] path,float distance)
    {for(int i=1;i<path.Length;i++){float n=Vector3.Distance(path[i-1],path[i]);if(n>=distance)return Vector3.Lerp(path[i-1],path[i],n>0?distance/n:0);distance-=n;}return path[path.Length-1];}
    private bool RecheckRelocation(BotComponent b,BotState s)
    {
        var r=s.Relocation;if(r.Best==null)return false;
        var v=EvaluateRelocationPoint(b,s,VP(r.Best.Position),r.Best.Id,out var point);
        if(OutdoorRelocation.Reject(v,config!.OutdoorRelocation).Length>0||point==null)return false;
        if(r.Policy.Trace?.State!="moving"){r.Best=v;r.Selected=point;}
        else {
            var active=b.Mover.ActivePath;
            if(OwnRelocationRoute(r,active)) {
                var actual=RemainingRelocationRoute(b,active!);
                if(actual==null||!RelocationRouteSafety(b,s,actual,r.Selected!.Position,out _,out _))return false;
            }
            r.Selected!.PathData.CalcPath(b.Position,r.Selected.Position);if(r.Selected.PathToPoint.status!=NavMeshPathStatus.PathComplete)return false;
        }
        r.LastSafety=Time.time;r.NextProbe=Time.time+.25f;return true;
    }
    private void StartRelocationMove(BotComponent b,BotState s)
    {
        var r=s.Relocation;if(r.Selected==null||Time.time-r.LastSafety>.3f)return;
        // Native SeekCover will request this point through FindCover on its next action update.
        b.Cover.StopSeekingCover();r.Routes.Clear();r.NativeMoveStarted=false;r.RequestedAt=Time.time;r.Policy.MoveRequested(Time.time);
    }
    private bool FindRelocationCover(SAINCoverClass cover,BotState s,ref CoverPoint result)
    {
        var r=s.Relocation;if(r.Policy.Trace?.State!="moving"||!r.Policy.Active)return false;
        var b=cover.Bot;
        try {
            if(RelocationFrame(b,s).Block.Length>0||r.Selected==null||Time.time-r.LastSafety>.75f
                ||!b.Mover.GoToCoverPoint(r.Selected,false,ESprintUrgency.High)) {EndRelocation(s,"relocate-native-cover-refused");return false;}
            result=r.Selected;r.NativeMoveStarted=true;RecordRelocation(s,true);return true;
        }catch(Exception ex){RelocationError(s,ex);return false;}
    }
    private string PrepareRelocationGrenade(BotComponent b,BotState s)
    {
        if(clearEvents==null)return "projectile-events-unavailable";
        string why=NativeAdapter.PrepareRoomGrenade(b,b.GoalEnemy,s,config!);if(why.Length>0)return why;
        return RelocationThrowSafety(b,s);
    }
    private string RelocationThrowSafety(BotComponent b,BotState s)
    {
        var r=s.Relocation;var g=b.BotOwner.WeaponManager.Grenades;var data=g.AIGreanageThrowData;var item=g.grenade;
        if(item==null||item.IsDummy||item.ThrowType.ToString().IndexOf("smoke",StringComparison.OrdinalIgnoreCase)>=0)return "no-suitable-grenade";
        if(data==null||!data.CanThrow||!data.IsUpToDate()||!DP(data.Target).Finite||Vector3.Distance(data.Position,b.Transform.WeaponData.WeaponRoot)>.5f
            ||Vector3.Distance(data.Target,VP(r.Policy.Known))>3||b.GoalEnemy.TimeSinceLastKnownUpdated>config!.GrenadeContactLifetime)return "stale-or-unrelated-throw-arc";
        if(!OutdoorRelocation.Valid(item.GetExplDelay)||item.GetExplDelay<2.5)return "fuse-too-short-or-unknown";
        r.Blast=data.Target;r.BlastRadius=ClearDangerRadius(item);
        if(!OutdoorRelocation.Valid(r.BlastRadius)||r.BlastRadius>100||!RelocationBlastSafe(b,s))return "own-blast-exposure";
        int count=0;foreach(var player in world!.RegisteredPlayers) {
            if(++count>128)return "friendly-check-truncated";
            if(player==null||ReferenceEquals(player,b.Player)||!player.HealthController.IsAlive||b.BotOwner.BotsGroup?.IsEnemy(player)==true)continue;
            if(!ClearBlastProtected(player.Position,r.Blast,r.BlastRadius+1))return "friendly-blast-exposure";
        }
        return "";
    }
    private bool RelocationBlastSafe(BotComponent b,BotState s)
    {
        var r=s.Relocation;if(r.BlastRadius<=0)return true;
        var danger=r.Blast;
        if(r.Projectile?.TryGetTarget(out var projectile)==true&&projectile!=null)danger=projectile.transform.position;
        if(!ClearBlastProtected(b.Position,danger,r.BlastRadius))return false;
        var points=OwnRelocationRoute(r,b.Mover.ActivePath)?RemainingRelocationRoute(b,b.Mover.ActivePath):r.Selected?.PathToPoint?.corners;
        if(points==null||points.Length>32)return false;
        // Check every corner and midpoint, rather than only the final cover.
        for(int i=1;i<points.Length;i++)if(!ClearBlastProtected(points[i],danger,r.BlastRadius)
            ||!ClearBlastProtected((points[i-1]+points[i])*.5f,danger,r.BlastRadius))return false;
        return true;
    }
    private static Vector3[]? RemainingRelocationRoute(BotComponent b,IBotPathData path)
    {
        // PathPoints is empty in official SAIN's manual cover mover; use the actual steering corners.
        int index=path.CurrentIndex,count=path.PathCorners.Count;
        if(index<0||index>=count||count>32)return null;
        var points=new Vector3[count-index+1];points[0]=b.Position;
        for(int i=index;i<count;i++)points[i-index+1]=path.PathCorners[i].Position;
        return points;
    }
    private static bool RelocationThrowGuard(GrenadeThrowDecider __instance,ref bool __result)
    {
        var p=Instance;var b=__instance.Bot;if(p==null||!p.states.TryGetValue(b,out var s)||!s.Relocation.ThrowCall)return true;
        try {var r=s.Relocation;string why=p.RelocationFrame(b,s).Block;
            if(why.Length==0)why=p.RelocationThrowSafety(b,s);
            if(why.Length==0&&r.GrenadeItem==b.BotOwner.WeaponManager.Grenades.grenade.Id.ToString()){r.GuardPassed=true;return true;}
        }catch(Exception){}__result=false;return false;
    }
    private void RelocationProjectile(Grenade grenade)
    {
        foreach(var pair in states) {
            var r=pair.Value.Relocation;
            if(!r.Policy.Active||!(r.Policy.Trace?.State=="throwing"||r.ThrowCall&&r.GuardPassed)||pair.Key.ProfileId!=grenade.ProfileId||grenade.WeaponSource?.Id.ToString()!=r.GrenadeItem)continue;
            r.Projectile=new(grenade);r.ProjectileId=grenade.Id;r.ReleaseAt=Time.time;
            r.Policy.Projectile(Time.time);RecordRelocation(pair.Value,true);break;
        }
    }
    private void RelocationExplosion(string profile,int id)
    {
        foreach(var pair in states)if(pair.Key.ProfileId==profile&&pair.Value.Relocation.ProjectileId==id) {
            pair.Value.Relocation.BlastRadius=0;pair.Value.Relocation.Projectile=null;break;
        }
    }
    private void EndRelocation(BotState s,string why,bool clear=false)
    {
        var r=s.Relocation;if(r.Starting)return;
        bool active=r.Policy.Active;if(!active&&!clear)return;if(clear)r.Policy.Clear(why);else r.Policy.End(why);
        if(active)FinishRelocation(s);else if(clear)r.Drop();
    }
    private void FinishRelocation(BotState s)
    {
        var r=s.Relocation;
        try {
            if(r.Owner!=null&&!r.Owner.IsDead&&!r.Policy.Trace!.Arrived) {
                // Clear only our queued generation without PathComplete (which would stop a different active route).
                foreach(var path in r.Routes.Keys.ToArray())if(OwnRelocationRoute(r,path)&&!ReferenceEquals(r.Owner.Mover.ActivePath,path)&&path.Status==EBotMoveStatus.ReadyToMove) {
                    relocationPathStatus.Invoke(path,new object[]{EBotMoveStatus.Complete});relocationClearPath.Invoke(path,null);
                }
                var active=r.Owner.Mover.ActivePath;
                if(OwnRelocationRoute(r,active)&&active is BotPathDataManual manual)combatStopPath.Invoke(manual,new object[]{false,"COMET relocation ended"});
                if(ReferenceEquals(r.Owner.Cover.CoverPoint_MovingTo,r.Selected))r.Owner.Cover.StopSeekingCover();
            }
            RecordRelocation(s,true);
        }finally{r.Drop();}
    }
    private void RelocationError(BotState s,Exception ex)
    {if(s.Relocation.Fault)return;s.Relocation.Fault=true;EndRelocation(s,"relocate-adapter-error",true);Logger.LogWarning("M13 yielded: "+ex.GetBaseException().Message);}
    private void RecordRelocation(BotState s,bool force=false)
    {
        var r=s.Relocation;var t=r.Policy.Trace;if(t==null)return;
        string key=t.Episode+":"+t.State+":"+t.Reason+":"+t.Accepted+":"+t.GrenadeOutcome;
        if(!force&&key==r.LogKey&&(!r.Policy.Active||Time.time-r.LogAt<.1f))return;r.LogKey=key;r.LogAt=Time.time;
        var json=Diagnostics.Serialize(new{schema=1,session,sequence=++sequence,feature="M13",stage="evaluated",decision="OutdoorRelocation",reason=t.Reason,
            realtime=Time.realtimeSinceStartup,utc=DateTime.UtcNow.ToString("O"),configRevision=config?.Revision??0,profileId=s.ProfileId,name=s.Name,botInstance=s.Id,
            outdoorRelocation=t,outdoorRelocationConfig=config?.OutdoorRelocation});
        diagnostics?.Write(json);Api.Decisions.Publish(json,ex=>{if(!subscriberWarned){subscriberWarned=true;Logger.LogWarning(ex.Message);}});
    }
}
