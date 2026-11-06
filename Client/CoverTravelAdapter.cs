using System;
using Comet.Core;
using EFT;
using HarmonyLib;
using Newtonsoft.Json;
using SAIN.Components;
using SAIN.Layers;
using SAIN.Models.Enums;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes;
using SAIN.SAINComponent.Classes.Mover;
using UnityEngine;
using UnityEngine.AI;

namespace Comet.Client;

internal sealed class CoverTravelRuntime
{
    internal readonly CoverTravelPolicy Policy=new();
    internal BotComponent? Owner;
    internal object? Enemy;
    internal IBotPathData? Path,ProbePath;
    internal Vector3 Seen,ProbeKnown,ProbePosition,ProbeGoal;
    internal float SeenAt=-1000,SampleAt=-1000,GeometryAt=-1000,NextGeometry,CallbackAt,LogAt=-1000,GazeAt=-1000;
    internal int ProbeIndex;
    internal double Length,Turn,Exposure;
    internal bool Protected,NativeSprint,NativeCaptured,Owned,Fault;
    internal string Marker="",LogKey="";
}
public sealed partial class Plugin
{
    private static System.Reflection.MethodInfo travelNativeRun = null!;
    private const string SeekCoverType="SAIN.Layers.Combat.Solo.Cover.SeekCoverAction";
    private static void PatchCoverTravel(Harmony h)
    {
        travelNativeRun=AccessTools.Method(typeof(SAINCoverClass),"CheckStartRun")??throw new MissingMethodException("M09 native cover sprint");
        h.Patch(travelNativeRun,
            postfix:new HarmonyMethod(typeof(Plugin),nameof(CoverTravelNativeSprint)));
        h.Patch(AccessTools.Method(typeof(SAINCoverClass),"UpdateCover")??throw new MissingMethodException("M09 native cover update"),
            postfix:new HarmonyMethod(typeof(Plugin),nameof(CoverTravelUpdate)));
        var type=typeof(BotComponent).Assembly.GetType(SeekCoverType,true);
        h.Patch(AccessTools.Method(type,"Stop")??throw new MissingMethodException("M09 cover stop"),
            prefix:new HarmonyMethod(typeof(Plugin),nameof(CoverTravelStopped)));
    }
    private static void CoverTravelNativeSprint(SAINCoverClass __instance,bool __result)
    {
        var p=Instance;if(p!=null&&p.states.TryGetValue(__instance.Bot,out var s))
        {s.CoverTravel.NativeSprint=__result;s.CoverTravel.NativeCaptured=true;}
    }
    private void SampleCoverTravel(BotComponent b,BotState s)
    {
        var r=s.CoverTravel;if(r.Fault)return;
        if(config?.Enabled!=true||!config.CoverTravel.Enabled||b.IsDead||!b.BotActive||!Authority||Ghost(s.ProfileId)||!NativeAdapter.IsPmcAi(b))
        {EndCoverTravel(s,"travel-ineligible");r.Enemy=null;r.SeenAt=-1000;return;}
        var e=b.GoalEnemy;
        if(!ReferenceEquals(e,r.Enemy)){EndCoverTravel(s,"travel-contact-changed");r.Enemy=e;r.SeenAt=r.GeometryAt=-1000;r.SampleAt=-1000;}
        if(e?.IsVisible==true&&Time.time-r.SampleAt>=.1f&&e.KnownPlaces.LastSeenPlace is {} seen)
        {r.Seen=seen.Position;r.SeenAt=Time.time;r.SampleAt=Time.time;}
        if(r.Owned&&(Time.time-r.CallbackAt>.3f||CoverTravelBlock(b,s).Length>0))EndCoverTravel(s,"travel-native-priority-or-callback-expired");
    }
    private string CoverTravelBlock(BotComponent b,BotState s)
    {
        if(config?.Enabled!=true||!config.CoverTravel.Enabled||!Authority||Ghost(s.ProfileId)||!NativeAdapter.IsPmcAi(b)
            ||!b.BotActive||b.IsDead||b.GameEnding||!b.SAINLayersActive)return "travel-ineligible";
        if(b.CurrentAction?.GetType().FullName!=SeekCoverType||b.CurrentAction.GetType().Assembly!=typeof(BotComponent).Assembly)
            return "travel-native-action-priority";
        if(b.Decision.CurrentSelfDecision!=ESelfActionType.None||b.Decision.CurrentSquadDecision!=ESquadDecision.None
            ||!VisibleAdapter.WeaponReady(b)||b.Medical.TimeSinceShot<.5||b.Suppression.IsSuppressed)
            return "travel-urgent-medical-reload-or-fire";
        var combat=CombatStepFrame(b,s);
        if(combat.Urgent||combat.Busy||!combat.Grounded||combat.Immobile||b.Cover.CoverInUse!=null
            ||s.HoldAngle.Policy.Active||s.CornerChase.Policy.Active||s.CornerPeek.Policy.Active||s.CombatStep.Policy.Active
            ||s.CornerPrefire.Policy.Active||s.Ambush.Policy.Active||s.Reservation.Lease!=null)return "travel-other-tactic-or-urgency";
        if(b.GoalEnemy==null||!ReferenceEquals(b.GoalEnemy,s.CoverTravel.Enemy))return "travel-contact-changed";
        int count=0;
        foreach(var other in b.EnemyController.KnownEnemies)
            if(++count>32||other!=null&&!ReferenceEquals(other,b.GoalEnemy)&&(other.IsVisible||other.Status.ShotAtMeRecently||other.Seen&&other.TimeSinceSeen<config!.CoverTravel.VisionAge))
                return "travel-other-immediate-threat";
        return "";
    }
    private CoverTravelFrame TravelFrame(BotComponent b,BotState s)
    {
        var r=s.CoverTravel;var cover=b.Cover.CoverPoint_MovingTo;var path=b.Mover.ActivePath;
        string block=CoverTravelBlock(b,s);
        if(block.Length==0&&(cover==null||cover.CoverData.IsBad||cover.Spotted||path==null||!path.Moving||path.Crawling
            ||path.PathStatus!=NavMeshPathStatus.PathComplete||Vector3.Distance(path.Destination,cover.Position)>.35f))block="travel-no-native-cover-route";
        var velocity=b.Player.Velocity;var facing=b.PlayerComponent.CharacterController.CurrentControlLookDirection;
        velocity.y=facing.y=0;
        return new(){Now=Time.time,Target=b.GoalEnemy?.EnemyProfileId??"",Context=cover==null?"":ProgressAdapter.CoverKey(cover),
            Position=DP(b.Position),Known=DP(r.Seen),Goal=DP(cover?.Position??b.Position),ObservedAt=r.SeenAt,Block=block,
            Geometry=r.GeometryAt>=0,GeometryAt=r.GeometryAt,Length=r.Length,Turn=r.Turn,Exposure=r.Exposure,Protected=r.Protected,
            Ready=VisibleAdapter.WeaponReady(b),CanSprint=b.Player.MovementContext.CanSprint,NativeSprint=r.NativeSprint,
            ActualSprint=b.Player.IsSprintEnabled,Speed=velocity.magnitude,FacingDot=velocity.sqrMagnitude<.001f?0:Vector3.Dot(velocity.normalized,facing.normalized)};
    }
    private static void CoverTravelUpdate(SAINCoverClass __instance)
    {
        var p=Instance;var b=__instance.Bot;
        if(p?.config==null||!p.states.TryGetValue(b,out var s)||s.CoverTravel.Fault)return;
        try {
            var r=s.CoverTravel;
            if(b.CurrentAction?.GetType().FullName!=SeekCoverType){p.EndCoverTravel(s,"travel-native-action-ended");return;}
            p.SampleCoverTravel(b,s);r.CallbackAt=Time.time;r.Owner=b;
            var f=p.TravelFrame(b,s);
            if(f.Block.Length==0&&r.SeenAt>=0&&Time.time-r.SeenAt<p.config.CoverTravel.VisionAge
                &&Vector3.Distance(b.Position,r.Seen)<=p.config.CoverTravel.CloseDistance)p.TravelGeometry(b,s);
            f=p.TravelFrame(b,s);string mode=r.Policy.Tick(f,p.config.CoverTravel);
            if(mode=="native"||!r.NativeCaptured)p.ReleaseTravelRequest(s);
            else {
                var path=b.Mover.ActivePath!;
                if(r.Owned&&!ReferenceEquals(path,r.Path))p.ReleaseTravelRequest(s);
                r.Path=path;r.Marker="COMET M09 "+mode;r.Owned=true;
                if(path.SprintReason!=r.Marker) {
                    if(mode=="dash")path.RequestStartSprint(ESprintUrgency.High,r.Marker);
                    else path.RequestEndSprint(ESprintUrgency.High,r.Marker);
                }
                r.Policy.Requested(mode=="face-walk",mode=="dash",mode=="face-walk"&&Time.time-r.GazeAt<.15f);
            }
            p.RecordCoverTravel(s);
        }catch(Exception ex){p.CoverTravelError(s,ex);}
    }
    private void TravelGeometry(BotComponent b,BotState s)
    {
        var r=s.CoverTravel;var path=b.Mover.ActivePath!;var goal=b.Cover.CoverPoint_MovingTo.Position;
        bool changed=!ReferenceEquals(path,r.ProbePath)||path.CurrentIndex!=r.ProbeIndex
            ||Vector3.Distance(b.Position,r.ProbePosition)>.75f||Vector3.Distance(r.Seen,r.ProbeKnown)>.5f||Vector3.Distance(goal,r.ProbeGoal)>.05f;
        if(changed)r.GeometryAt=-1000;
        if(!changed&&Time.time<r.NextGeometry)return;
        if(!doorBudget.TryBegin(Time.frameCount))return;
        var clock=System.Diagnostics.Stopwatch.StartNew();
        try {
            r.NextGeometry=Time.time+(float)config!.CoverTravel.Refresh;r.GeometryAt=-1000;
            // Use remaining corners of the route SAIN is actually following, never a new shortcut.
            var corners=path.PathCorners;int start=path.CurrentIndex;
            if(start<0||start>=corners.Count||corners.Count>64||Vector3.Distance(corners[corners.Count-1].Position,goal)>.35f)return;
            double length=0,turn=0;var from=b.Position;Vector3 previous=default;
            for(int i=start;i<corners.Count;i++) {
                var to=corners[i].Position;if(!CornerChase.Point(DP(to)))return;
                var direction=to-from;float segment=direction.magnitude;
                if(segment>.05f){if(previous.sqrMagnitude>.001f)turn=Math.Max(turn,Vector3.Angle(previous,direction));previous=direction;}
                length+=segment;from=to;
            }
            // Bounded spatial sampling: 3 route points x 3 body rays, plus 3 at the endpoint.
            // This is an exposure estimate, not proof that all geometry is safe.
            int exposed=0;
            for(int sample=1;sample<=3;sample++) {
                double distance=length*sample/4;from=b.Position;Vector3 point=from;
                for(int i=start;i<corners.Count;i++) {
                    var to=corners[i].Position;double segment=Vector3.Distance(from,to);
                    if(distance<=segment){point=Vector3.Lerp(from,to,segment>.001?(float)(distance/segment):0);break;}
                    distance-=segment;from=point=to;
                }
                exposed+=TravelExposure(point,r.Seen);
            }
            r.Length=length;r.Turn=turn;r.Exposure=exposed/9d;r.Protected=TravelExposure(goal,r.Seen)<=1;
            r.ProbePath=path;r.ProbeIndex=start;r.ProbePosition=b.Position;r.ProbeKnown=r.Seen;r.ProbeGoal=goal;r.GeometryAt=Time.time;
        }finally{clock.Stop();doorBudget.Charge(clock.Elapsed.TotalMilliseconds);}
    }
    private static int TravelExposure(Vector3 position,Vector3 known)
    {
        int count=0;var source=known+Vector3.up*1.4f;
        for(int sample=1;sample<=3;sample++)
            if(!Physics.Linecast(source,position+Vector3.up*(sample*.5f),LayersMaskController.HighPolyWithTerrainMask,QueryTriggerInteraction.Ignore))count++;
        return count;
    }
    private bool CoverTravelGaze(BotComponent b,BotState s,out Vector3 direction)
    {
        direction=default;var r=s.CoverTravel;
        if(r.Policy.Trace?.State!="face-walk"||!r.Owned||!ReferenceEquals(b.Mover.ActivePath,r.Path)||Time.time-r.CallbackAt>.2f
            ||b.Player.IsSprintEnabled||CoverTravelPolicy.Gate(TravelFrame(b,s),config!.CoverTravel).Length>0
            ||b.ManualShoot.Shooting||b.ManualShoot.Reason!=EShootReason.None||b.Aim.AimStatus!=AimStatus.NoTarget)return false;
        direction=(r.Seen+Vector3.up*1.3f-b.Transform.WeaponRoot).normalized;
        r.GazeAt=Time.time;r.Policy.Requested(true,false,true);RecordCoverTravel(s);return true;
    }
    private void ReleaseTravelRequest(BotState s)
    {
        var r=s.CoverTravel;
        try {
            // Restore only our own last request on the same still-active native path.
            if(r.Owned&&r.Owner!=null&&!r.Owner.IsDead&&ReferenceEquals(r.Owner.Mover.ActivePath,r.Path)&&r.Path?.SprintReason==r.Marker) {
                // Native cover refresh is normally only once per second. Re-evaluate its
                // original choice now so a new medical/urgent state never inherits an old veto.
                object[] args={r.Owner.GoalEnemy,null!};
                bool native=(bool)travelNativeRun.Invoke(r.Owner.Cover,args);
                if(native)r.Path.RequestStartSprint(ESprintUrgency.High,"COMET M09 released to native cover");
                else r.Path.RequestEndSprint(ESprintUrgency.High,"COMET M09 released to native cover");
            }
        }catch(Exception ex) {
            // A destroyed native object or another patch must not prevent SAIN.Stop
            // (or the rest of raid cleanup) from running. Its next update owns travel.
            r.Fault=true;Logger.LogWarning("M09 release yielded: "+ex.GetBaseException().Message);
        }finally{r.Owned=false;r.Path=null;}
    }
    private static void CoverTravelStopped(BotAction __instance)
    {var p=Instance;if(p!=null&&p.states.TryGetValue(__instance.Bot,out var s))p.EndCoverTravel(s,"travel-native-action-ended");}
    private void EndCoverTravel(BotState s,string reason)
    {
        var r=s.CoverTravel;ReleaseTravelRequest(s);
        if(r.Policy.Trace!=null&&r.Policy.Trace.State!="ended"){r.Policy.End(reason);RecordCoverTravel(s,true);}
        r.Owner=null;r.ProbePath=null;r.GeometryAt=-1000;
    }
    private void CoverTravelError(BotState s,Exception ex)
    {s.CoverTravel.Fault=true;EndCoverTravel(s,"travel-adapter-error");Logger.LogWarning("M09 yielded: "+ex.GetBaseException().Message);}
    private void RecordCoverTravel(BotState s,bool force=false)
    {
        var r=s.CoverTravel;var t=r.Policy.Trace;if(t==null||t.Target.Length==0||t.Context.Length==0)return;
        string key=t.State+":"+t.Reason+":"+t.Context+":"+t.Target+":"+t.Observed;
        if(!force&&key==r.LogKey&&(t.State=="native"||t.State=="ended"||Time.time-r.LogAt<.1f))return;
        r.LogKey=key;r.LogAt=Time.time;
        var json=Diagnostics.Serialize(new{schema=1,session,sequence=++sequence,feature="M09",stage="evaluated",decision="CoverTravel",reason=t.Reason,
            realtime=Time.realtimeSinceStartup,utc=DateTime.UtcNow.ToString("O"),configRevision=config?.Revision??0,profileId=s.ProfileId,name=s.Name,botInstance=s.Id,
            coverTravel=t,coverTravelConfig=config?.CoverTravel});
        diagnostics?.Write(json);Api.Decisions.Publish(json,ex=>{if(!subscriberWarned){subscriberWarned=true;Logger.LogWarning(ex.Message);}});
    }
}
