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
    internal float Callback=-100,GeometryAt=-100,LogAt=-100,StepInputAt=-100,OpportunityAt=-100;
    internal Vector3 StepInputFrom,StepInputDirection;
    internal Vector3 At,Input,From,To,Left,Right;
    internal bool LeftSafe,RightSafe,Fresh,Fault;
    internal string GeometryKey="",LogKey="";
    internal string[] Rejected=Array.Empty<string>();
    internal FireLaneRay? Checked;
    internal float NextCheck;
    internal bool CachedOverride,CachedHold;
    internal Vector3 CachedDirection;
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
        // Run after other input prefixes and do nothing if one has already taken ownership.
        h.Patch(AccessTools.Method(typeof(PlayerMovementController),nameof(PlayerMovementController.SetTargetMoveDirection))
            ??throw new MissingMethodException("S07 native movement"),prefix:new HarmonyMethod(typeof(Plugin),nameof(LaneMovement)){priority=Priority.Last});
    }
    private string LaneBlock(BotComponent b,BotState s)
    {
        if(config?.Enabled!=true||!config.FireLane.Enabled||!Authority||world==null||!NativeAdapter.IsSupportedAi(b)||b.IsDead||!b.BotActive||b.GameEnding||Ghost(b.ProfileId))return "lane-ineligible";
        if(b.BotOwner.Memory.IsUnderFire||b.GoalEnemy?.IsVisible==true)return "lane-direct-combat-priority";
        if(b.Decision.CurrentCombatDecision is ECombatDecision.AvoidGrenade or ECombatDecision.SeekCover or ECombatDecision.Retreat or ECombatDecision.RunAway)return "lane-escape-priority";
        if(b.Squad.Members==null||b.Squad.Members.Count<2)return "lane-no-squad";
        return "";
    }
    private List<FireLaneRay> LaneRays(BotComponent b,FireLaneConfig c)
    {
        var result=new List<FireLaneRay>();
        foreach(var mate in b.Squad.Members.Values){
            if(mate==null||ReferenceEquals(mate,b)||mate.IsDead||mate.Player?.HealthController?.IsAlive!=true)continue;
            var from=mate.Transform.WeaponData.FirePort;Vector3 to;
            if(mate.ManualShoot.Reason!=EShootReason.None&&mate.ManualShoot.ShootPosition!=Vector3.zero)to=mate.ManualShoot.ShootPosition;
            else if(mate.BotOwner.ShootData.Shooting&&mate.GoalEnemy?.IsVisible==true){to=mate.GoalEnemy.EnemyPosition+Vector3.up*1.2f;if((to-from).sqrMagnitude<=1)continue;}
            else continue;
            result.Add(new(){Shooter=mate.ProfileId,Name=mate.Player.Profile.Nickname,From=DP(from),To=DP(to),Age=0});
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
            if(block.Length>0||!ValidPoint(direction)||direction.sqrMagnitude<.01){
                string why=block.Length>0?block:"lane-no-input";
                p.RecordLaneOpportunity(b,s,why);p.EndLane(s,why);return true;
            }
            var c=p.config.FireLane;var input=new Vector3(direction.x,0,direction.z).normalized;
            if(Time.time<r.NextCheck)
            {
                r.Callback=Time.time;
                if(r.CachedHold){b.Player.Move(Vector2.zero);return false;}
                if(r.CachedOverride)direction=r.CachedDirection;
                return true;
            }
            r.NextCheck=Time.time+.1f;r.CachedHold=r.CachedOverride=false;
            var position=b.Position;float speed=b.Player.Velocity.magnitude;
            var ahead=position+input;
            var rays=p.LaneRays(b,c);var threat=FireLaneGeometry.Threat(DP(position),DP(ahead),rays,c);
            if(threat==null)p.RecordLaneOpportunity(b,s,rays.Count==0?"lane-no-current-verified-shot":"lane-route-clear-of-squad-fire");
            var route=b.Mover.ActivePath;var destination=route?.Destination??finalMoveDestination;
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
                r.CachedHold=true;r.StepInputAt=-100;b.Player.Move(Vector2.zero);
                r.Policy.Applied(speed<.15f,c);p.RecordLane(s);return false;
            }
            var goal=new Vector3((float)t.GoalX,(float)t.GoalY,(float)t.GoalZ);var side=goal-position;side.y=0;
            if(side.sqrMagnitude<.01){p.EndLane(s,"lane-detour-reached");return true;}
            // Only this input call is changed. The stored native route and final destination remain intact.
            direction=side.normalized;
            r.CachedDirection=direction;r.CachedOverride=true;
            // Keep the native final destination and sprint distance.
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
        var axis=VP(threat!.To)-VP(threat.From);axis.y=0;
        var side=Vector3.Cross(Vector3.up,axis.normalized);
        FireLaneGeometry.PointDistance(DP(b.Position+Vector3.up*1.2f),threat,out var t);
        var offset=b.Position+Vector3.up*1.2f-(VP(threat.From)+ (VP(threat.To)-VP(threat.From))*(float)t);offset.y=0;
        if(Vector3.Dot(offset,side)<0)side=-side;
        r.Left=b.Position+side*(float)c.SideDistance;r.Right=b.Position-side*(float)c.SideDistance;
        r.LeftSafe=LaneSideOpen(b,side,c);r.RightSafe=!r.LeftSafe&&LaneSideOpen(b,-side,c);
        r.Rejected=Array.Empty<string>();return true;
    }
    private static bool LaneSideOpen(BotComponent b,Vector3 side,FireLaneConfig c)
        =>!NavMesh.Raycast(RemixMovementOrigin(b,b.Position,preserveOnMesh:true),b.Position+side*(float)c.SideDistance,out _,NavMesh.AllAreas)
          &&!Physics.Raycast(b.Position+Vector3.up*1.1f,side,.9f,LayersMaskController.HighPolyWithTerrainMask);
    private void SampleLane(BotComponent b,BotState s)
    {
        if(s.FireLane.Policy.Active&&(Time.time-s.FireLane.Callback>.25||LaneBlock(b,s).Length>0))EndLane(s,"lane-owner-or-callback-ended");
    }
    private void RecordLaneOpportunity(BotComponent b,BotState s,string reason)
    {
        var r=s.FireLane;
        if(r.Policy.Active||Time.time-r.OpportunityAt<5||config?.Enabled!=true||!config.FireLane.Enabled
            ||!Authority||!NativeAdapter.IsSupportedAi(b)||b.IsDead||!b.BotActive||b.GameEnding||Ghost(b.ProfileId))return;
        r.OpportunityAt=Time.time;
        var members=b.Squad?.Members;if(members==null||members.Count<2||members.Count>16)return;
        int shots=LaneRays(b,config.FireLane).Count;
        if(shots==0)return;
        // Diagnostic only: never publish an intervention/badge for a gate that did not act.
        diagnostics?.Write(Diagnostics.Serialize(new{schema=1,session,sequence=++sequence,feature="S07",stage="diagnostic",
            decision="FireLane",reason,realtime=Time.realtimeSinceStartup,utc=DateTime.UtcNow.ToString("O"),
            configRevision=config.Revision,profileId=s.ProfileId,name=s.Name,botInstance=s.Id,
            fireLane=new{state="evaluated",reason,validFor=0,opportunityOnly=true,recentSquadShooters=shots}}));
    }
    private void EndLane(BotState s,string reason)
    {s.FireLane.CachedOverride=s.FireLane.CachedHold=false;s.FireLane.NextCheck=0;if(!s.FireLane.Policy.Active)return;s.FireLane.Policy.End(reason);s.FireLane.Fresh=false;s.FireLane.StepInputAt=-100;RecordLane(s,true);}
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
