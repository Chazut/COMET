using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Comet.Core;
using EFT;
using HarmonyLib;
using Newtonsoft.Json;
using SAIN.Components;
using SAIN.Classes;
using SAIN.Models.Enums;
using SAIN.Components.PlayerComponentSpace;
using SAIN.SAINComponent.Classes.Mover;
using SAIN.Preset.Shared.Enums;
using UnityEngine;
using UnityEngine.AI;
namespace Comet.Client;

internal sealed class FireLaneRuntime
{
    internal readonly FireLanePolicy Policy=new();
    internal float Callback=-100,GeometryAt=-100,LogAt=-100,StepInputAt=-100;
    internal Vector3 StepInputFrom,StepInputDirection;
    internal Vector3 At,Input,From,To,Left,Right;
    internal bool LeftSafe,RightSafe,Fresh,Fault;
    internal string GeometryKey="",LogKey="";
    internal string[] Rejected=Array.Empty<string>();
    internal FireLaneRay? Checked;
}
internal sealed class FireLaneShot
{
    internal Vector3 From,Direction;internal float At,Length;internal int Instance;
}
public sealed partial class Plugin
{
    private readonly Dictionary<string,FireLaneShot> laneShots=new();
    private bool laneShotWarned;
    private static void PatchFireLane(Harmony h)
    {
        h.Patch(AccessTools.Method(typeof(PlayerComponent),nameof(PlayerComponent.OnMakingShot)),postfix:new HarmonyMethod(typeof(Plugin),nameof(LaneShot)));
        // Run after other input prefixes and do nothing if one has already taken ownership.
        h.Patch(AccessTools.Method(typeof(PlayerMovementController),nameof(PlayerMovementController.SetTargetMoveDirection))
            ??throw new MissingMethodException("S07 native movement"),prefix:new HarmonyMethod(typeof(Plugin),nameof(LaneMovement)){priority=Priority.Last});
    }
    private static void LaneShot(PlayerComponent __instance)
    {
        var p=Instance;var b=__instance?.BotComponent;
        if(p?.config?.Enabled!=true||!p.config.FireLane.Enabled||!p.Authority||b==null||b.IsDead||!b.BotActive||p.Ghost(b.ProfileId))return;
        try {
            var from=b.Transform.WeaponData.FirePort;var dir=b.Transform.WeaponData.PointDirection;
            if(!ValidPoint(from)||!ValidPoint(dir)||dir.sqrMagnitude<.5f)return;
            if(p.laneShots.Count>=512&&!p.laneShots.ContainsKey(b.ProfileId))return;
            float length=60;
            // Dated visible/manual aim data only. No hidden target transforms.
            if(b.ManualShoot.Shooting&&ValidPoint(b.ManualShoot.ShootPosition)&&b.ManualShoot.ShootPosition.sqrMagnitude>.01)
                length=Math.Min(length,Vector3.Distance(from,b.ManualShoot.ShootPosition));
            else if(b.GoalEnemy?.IsVisible==true&&b.GoalEnemy.KnownPlaces.LastSeenPlace is {} seen)
                length=Math.Min(length,Vector3.Distance(from,seen.Position)+1);
            p.laneShots[b.ProfileId]=new(){From=from,Direction=dir.normalized,At=Time.time,Length=length,Instance=RuntimeHelpers.GetHashCode(b)};
        }catch(Exception ex){if(!p.laneShotWarned){p.laneShotWarned=true;p.Logger.LogWarning("S07 shot observation yielded: "+ex.GetBaseException().Message);}}
    }
    private string LaneBlock(BotComponent b,BotState s)
    {
        if(config?.Enabled!=true||!config.FireLane.Enabled||!Authority||world==null||!NativeAdapter.IsSupportedAi(b)||b.IsDead||!b.BotActive||b.GameEnding||Ghost(b.ProfileId))return "lane-ineligible";
        if(b.BotOwner.Memory.IsUnderFire||b.Suppression.IsSuppressed||b.Grenade.GrenadeDangerPoint!=null||b.GoalEnemy?.IsVisible==true||b.BotOwner.ShootData.Shooting||b.ManualShoot.Shooting)return "lane-direct-combat-priority";
        if(b.Decision.CurrentCombatDecision is ECombatDecision.AvoidGrenade or ECombatDecision.SeekCover or ECombatDecision.Retreat or ECombatDecision.RunAway)return "lane-escape-priority";
        if(b.Decision.CurrentSelfDecision!=ESelfActionType.None||b.BotOwner.Medecine.Using||MedicalPostureGuard(b)||b.BotOwner.WeaponManager.Reload.Reloading
            ||b.BotOwner.DoorOpener.Interacting||!b.Player.MovementContext.IsGrounded||b.Player.IsInPronePose)return "lane-urgent-native-action";
        if(s.CombatStep.Policy.Active||s.CornerPeek.Policy.Active||s.CornerChase.Policy.Active||s.CornerPrefire.Policy.Active||s.RetreatWeave.Policy.Active
            ||s.Entry.Policy.Active||s.RoomClear.Policy.Active||s.Ambush.Policy.Active||s.Reservation.Lease!=null)return "lane-other-movement-priority";
        var path=b.Mover.ActivePath;
        if(path==null||!path.Moving||path.Crawling||path.Status==EBotMoveStatus.DoorInteraction||path.PathStatus!=NavMeshPathStatus.PathComplete)return "lane-no-native-route";
        if(b.Squad.Members==null||b.Squad.Members.Count<2||b.Squad.Members.Count>16)return "lane-no-bounded-squad";
        return "";
    }
    private List<FireLaneRay> LaneRays(BotComponent b,FireLaneConfig c)
    {
        var result=new List<FireLaneRay>();
        foreach(var mate in b.Squad.Members.Values){
            if(mate==null||ReferenceEquals(mate,b)||mate.IsDead||!mate.BotActive||Ghost(mate.ProfileId)||!laneShots.TryGetValue(mate.ProfileId,out var shot)
                ||shot.Instance!=RuntimeHelpers.GetHashCode(mate)||Time.time<shot.At||Time.time-shot.At>c.ShotGrace)continue;
            var now=mate.Transform.WeaponData;
            // A cached shot cannot reserve the old lane after the muzzle moves or turns.
            if(!ValidPoint(now.FirePort)||!ValidPoint(now.PointDirection)||Vector3.Distance(now.FirePort,shot.From)>.25||Vector3.Angle(now.PointDirection,shot.Direction)>8)continue;
            result.Add(new(){Shooter=mate.ProfileId,Name=mate.Player.Profile.Nickname,From=DP(shot.From),To=DP(shot.From+shot.Direction*shot.Length),Age=Time.time-shot.At});
        }
        return result;
    }
    private static bool LaneMovement(ref Vector3 direction,ref Vector3 finalMoveDestination,PlayerComponent playerComp,ref float STOP_SPRINT_DIST,bool __runOriginal)
    {
        var p=Instance;var b=playerComp?.BotComponent;
        if(p?.config==null||b==null)return true;
        if(!p.states.TryGetValue(b,out var s))return true;
        var r=s.FireLane;if(r.Fault)return true;
        var originalDirection=direction;var originalDestination=finalMoveDestination;float originalStop=STOP_SPRINT_DIST;
        try {
            string block=!__runOriginal?"lane-other-prefix-owner":p.LaneBlock(b,s);
            if(block.Length>0||!ValidPoint(direction)||direction.sqrMagnitude<.01){p.EndLane(s,block.Length>0?block:"lane-no-input");return true;}
            var c=p.config.FireLane;var input=new Vector3(direction.x,0,direction.z).normalized;
            var position=b.Position;float speed=b.Player.Velocity.magnitude;
            if(!float.IsFinite(speed)||speed>20){p.EndLane(s,"lane-invalid-velocity");return true;}
            var ahead=position+input*(float)Math.Clamp(Math.Max(speed,1)*c.Prediction,.2,3);
            var rays=p.LaneRays(b,c);var threat=FireLaneGeometry.Threat(DP(position),DP(ahead),rays,c);
            // Finish the chosen short exit even once the next native input narrowly clears the lane.
            if(threat==null&&r.Policy.Trace?.State=="stepping")threat=rays.FirstOrDefault(x=>x.Shooter==r.Policy.Trace.Shooter);
            var route=b.Mover.ActivePath!;var destination=route.Destination;
            string context=FormattableString.Invariant($"{RuntimeHelpers.GetHashCode(route)}:{b.Decision.CurrentCombatDecision}:{destination.x:0.0},{destination.y:0.0},{destination.z:0.0}");
            bool fresh=false;
            if(threat!=null)fresh=p.LaneGeometry(b,s,input,ahead,rays,ref threat,context);
            var f=new FireLaneFrame{Now=Time.time,Speed=speed,Action=b.Decision.CurrentCombatDecision.ToString(),Context=context,
                Position=DP(position),Desired=DP(ahead),Threat=threat,Inside=threat!=null&&FireLaneGeometry.Blocks(DP(position),DP(position),threat,c.Width),
                GeometryFresh=fresh,LeftSafe=r.LeftSafe,RightSafe=r.RightSafe,Left=DP(r.Left),Right=DP(r.Right),Rejected=r.Rejected};
            r.Callback=Time.time;
            if(!r.Policy.Tick(f,c)){r.StepInputAt=-100;p.RecordLane(s);return true;}
            var t=r.Policy.Trace!;
            if(t.State=="holding"){
                r.StepInputAt=-100;b.Player.Move(Vector2.zero);b.Player.EnableSprint(false);
                r.Policy.Applied(speed<.15f,c);p.RecordLane(s);return false;
            }
            var goal=new Vector3((float)t.GoalX,(float)t.GoalY,(float)t.GoalZ);var side=goal-position;side.y=0;
            if(side.sqrMagnitude<.01){p.EndLane(s,"lane-detour-reached");return true;}
            // Only this input call is changed. The stored native route and final destination remain intact.
            direction=side.normalized;finalMoveDestination=goal;
            STOP_SPRINT_DIST=Math.Max(STOP_SPRINT_DIST,(float)c.SideDistance+1);
            var travelled=position-r.StepInputFrom;
            bool observed=Time.time>r.StepInputAt&&Time.time-r.StepInputAt<=.25f&&travelled.magnitude>.005f
                &&Vector3.Dot(travelled.normalized,r.StepInputDirection)>.6f&&speed>.1&&Vector3.Dot(b.Player.Velocity.normalized,direction)>.6f;
            r.StepInputAt=Time.time;r.StepInputFrom=position;r.StepInputDirection=direction;
            r.Policy.Applied(observed,c);p.RecordLane(s);return true;
        }catch(Exception ex){direction=originalDirection;finalMoveDestination=originalDestination;STOP_SPRINT_DIST=originalStop;r.Fault=true;p.EndLane(s,"lane-adapter-error");p.Logger.LogWarning("S07 yielded: "+ex.GetBaseException().Message);return true;}
    }
    private bool LaneGeometry(BotComponent b,BotState s,Vector3 input,Vector3 ahead,List<FireLaneRay> rays,ref FireLaneRay? threat,string context)
    {
        var r=s.FireLane;var c=config!.FireLane;
        string key=context+":"+string.Join("|",rays.OrderBy(x=>x.Shooter,StringComparer.Ordinal).Select(x=>FormattableString.Invariant($"{x.Shooter}:{x.From.X:0.0},{x.From.Y:0.0},{x.From.Z:0.0}:{x.To.X:0.0},{x.To.Y:0.0},{x.To.Z:0.0}")));
        bool same=r.GeometryKey==key&&Vector3.Distance(r.At,b.Position)<.15&&Vector3.Angle(input,r.Input)<5
            &&Vector3.Distance(r.From,VP(threat!.From))<.1&&Vector3.Distance(r.To,VP(threat.To))<.2;
        if(same&&Time.time-r.GeometryAt<=c.Refresh){if(r.Checked!=null)r.Checked.Age=rays.First(x=>x.Shooter==r.Checked.Shooter).Age;threat=r.Checked;return r.Fresh;}
        r.Fresh=false;r.LeftSafe=r.RightSafe=false;
        if(!doorBudget.TryBegin(Time.frameCount))return false;
        doorClock.Restart();try{
            r.GeometryAt=Time.time;r.GeometryKey=key;r.At=b.Position;r.Input=input;r.From=VP(threat!.From);r.To=VP(threat.To);
            // Trim every actual firing ray at static cover, so walls do not reserve the space behind them.
            foreach(var ray in rays){var start=VP(ray.From);var delta=VP(ray.To)-start;
                if(Physics.Raycast(start,delta.normalized,out var hit,delta.magnitude,LayersMaskController.HighPolyWithTerrainMask,QueryTriggerInteraction.Ignore))ray.To=DP(hit.point);}
            var actual=FireLaneGeometry.Threat(DP(b.Position),DP(ahead),rays,c);
            if(actual==null&&r.Policy.Trace?.State=="stepping")actual=rays.FirstOrDefault(x=>x.Shooter==r.Policy.Trace.Shooter&&FireLaneGeometry.BodyDistance(DP(b.Position),DP(b.Position),x)<c.SideDistance+c.Width&&Vector3.Distance(VP(x.To),VP(x.From))>.5f);
            threat=r.Checked=actual;
            if(actual==null){r.Rejected=new[]{"Static cover ends the firing lane before this route"};r.Fresh=true;return true;}
            var axis=VP(actual.To)-VP(actual.From);axis.y=0;var side=new Vector3(-axis.z,0,axis.x).normalized;
            r.Left=b.Position+side*(float)c.SideDistance;r.Right=b.Position-side*(float)c.SideDistance;
            if(r.Policy.Trace?.State=="stepping"){
                var t=r.Policy.Trace;var kept=new Vector3((float)t.GoalX,(float)t.GoalY,(float)t.GoalZ);
                if(t.Side=="left")r.Left=kept;else r.Right=kept;
            }
            var rejected=new List<string>();r.LeftSafe=LaneSideSafe(b,r.Left,rays,rejected,"left");r.RightSafe=LaneSideSafe(b,r.Right,rays,rejected,"right");
            r.Rejected=rejected.Take(8).ToArray();r.Fresh=true;return true;
        }finally{doorClock.Stop();doorBudget.Charge(doorClock.Elapsed.TotalMilliseconds);}
    }
    private bool LaneSideSafe(BotComponent b,Vector3 goal,List<FireLaneRay> rays,List<string> rejects,string side)
    {
        string reason="";
        if(b.Player.IsSprintEnabled)reason="sprinting: no abrupt lateral input";
        else if(!ValidPoint(goal)||!NavMesh.SamplePosition(goal,out var nav,.15f,NavMesh.AllAreas)||Vector3.Distance(goal,nav.position)>.15
            ||NavMesh.Raycast(b.Position,goal,out _,NavMesh.AllAreas))reason="no local same-floor navigation";
        else if(!FireLaneGeometry.SafeExit(DP(b.Position),DP(goal),rays,config!.FireLane.Width))reason="crosses another lane or the firing axis";
        else if(RunVolume(b,b.Position,goal).Length>0)reason="body clearance blocked";
        else foreach(var mate in b.Squad.Members.Values)if(mate!=null&&!ReferenceEquals(mate,b)&&!mate.IsDead&&Vector3.Distance(goal,mate.Position)<.85){reason="teammate occupies detour";break;}
        if(reason.Length==0)return true;rejects.Add(side+": "+reason);return false;
    }
    private void SampleLane(BotComponent b,BotState s)
    {
        if(s.FireLane.Policy.Active&&(Time.time-s.FireLane.Callback>.25||LaneBlock(b,s).Length>0))EndLane(s,"lane-owner-or-callback-ended");
    }
    private void EndLane(BotState s,string reason)
    {if(!s.FireLane.Policy.Active)return;s.FireLane.Policy.End(reason);s.FireLane.Fresh=false;s.FireLane.StepInputAt=-100;RecordLane(s,true);}
    private void RecordLane(BotState s,bool force=false)
    {
        var r=s.FireLane;var t=r.Policy.Trace;if(t==null||t.Shooter.Length==0)return;
        string key=t.State+":"+t.Reason+":"+t.Shooter+":"+t.Side+":"+t.Applied+":"+t.Confirmed;
        if(!force&&key==r.LogKey&&(!r.Policy.Active||Time.time-r.LogAt<.1f))return;r.LogKey=key;r.LogAt=Time.time;
        var json=Diagnostics.Serialize(new{schema=1,session,sequence=++sequence,feature="S07",stage="evaluated",decision="FireLane",reason=t.Reason,
            realtime=Time.realtimeSinceStartup,utc=DateTime.UtcNow.ToString("O"),configRevision=config?.Revision??0,profileId=s.ProfileId,name=s.Name,botInstance=s.Id,
            fireLane=t,fireLaneConfig=config?.FireLane});
        diagnostics?.Write(json);Api.Decisions.Publish(json,ex=>{if(!subscriberWarned){subscriberWarned=true;Logger.LogWarning(ex.Message);}});
    }
}
