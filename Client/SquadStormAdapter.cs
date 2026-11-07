using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Comet.Core;
using Newtonsoft.Json;
using SAIN.Components;
using UnityEngine;
using UnityEngine.AI;
namespace Comet.Client;

internal sealed partial class SquadStormRuntime
{
    internal SquadStormPlan? Plan;
    internal StormMember? Member;
    internal float Next;
}
public sealed partial class Plugin
{
    private readonly SquadStormBoard stormBoard=new();
    private CrossfireConfig StormMotionConfig()=>new(){Enabled=config!.Enabled&&config.SquadStorm.Enabled,Participation=100,Spacing=3,
        MaxPath=config.SquadStorm.MaxPath,MoveTimeout=config.SquadStorm.Duration,HoldTimeout=1,StallTimeout=3,Retry=1};
    private string StormBlock(BotComponent b,BotState s)
    {
        if(!CrossfireScope(b)||!config!.SquadStorm.Enabled)return "storm-out-of-scope";
        var e=b.GoalEnemy;var r=s.Crossfire;
        if(e==null||!e.EnemyKnown||e.KnownPlaces.LastKnownPosition is not Vector3 k||!ValidPoint(k))return "storm-no-known-contact";
        if(e.IsVisible)return "storm-visible-native-combat";
        if(!float.IsFinite(e.TimeSinceLastKnownUpdated)||e.TimeSinceLastKnownUpdated<0||e.TimeSinceLastKnownUpdated>config.SquadStorm.ContactAge)return "storm-stale-contact";
        if(r.Storm.Plan!=null&&r.Policy.Active&&(e.EnemyProfileId!=r.Contact||RuntimeHelpers.GetHashCode(e)!=r.ContactInstance||Vector3.Distance(k,r.Known)>3))return "storm-contact-changed";
        if(ManeuverAmmo(b)<config.SquadStorm.MinimumAmmo)return "storm-low-ammo";
        RefreshFear(b,s);if(s.Fear.Current.Panic)return "storm-fear-veto";
        return CrossfireCommonBlock(b,s);
    }
    private bool TryBeginStorm(BotComponent b,BotState s)
    {
        var r=s.Crossfire;var c=config!.SquadStorm;
        if(!c.Enabled||r.Policy.Active||Time.time<r.Policy.RetryAt)return false;
        string block=StormBlock(b,s);if(block.Length>0)return DeclineSquad(s,"S06",block);
        string group=ManeuverGroup(b);var e=b.GoalEnemy!;var known=e.KnownPlaces.LastKnownPosition!.Value;
        var plan=stormBoard.Get(group);
        if(plan?.Active!=true)
        {
            if(Time.time<r.Storm.Next)return false;r.Storm.Next=Time.time+.5f;
            var candidates=new List<StormMember>();StormEvidence? evidence=null;bool blocked=false;
            foreach(var mate in b.Squad.Members.Values)
            {
                if(mate==null||!CrossfireScope(mate)||Vector3.Distance(b.Position,mate.Position)>c.SquadDistance)continue;
                if(!states.TryGetValue(mate,out var ms)){if(states.Count>=512)continue;ms=new BotState(mate,config,session);states.Add(mate,ms);}
                // Keep the squad's existing coordinated action. No competing S03/S04/S05 plan.
                if(ms.Crossfire.Policy.Active){blocked=true;break;}
                if(ms.Crossfire.Fault||Time.time<ms.Crossfire.Policy.RetryAt||mate.GoalEnemy?.EnemyProfileId!=e.EnemyProfileId
                    ||StormBlock(mate,ms).Length>0||!CrossfireCanAcquire(mate)
                    ||mate.GoalEnemy.KnownPlaces.LastKnownPosition is not Vector3 mk||Vector3.Distance(mk,known)>3)continue;
                var observed=ReadStormEvidence(mate,ms);
                if(observed.Danger){blocked=true;break;}
                if(observed.At>=0&&(evidence==null||observed.Score>evidence.Score))evidence=observed;
                candidates.Add(new(){Id=mate.ProfileId,Name=mate.Player.Profile.Nickname,Personality=mate.Info.Personality.ToString(),Ammo=ManeuverAmmo(mate),Roll=jumpRandom.NextDouble()});
            }
            if(blocked)return DeclineSquad(s,"S06","storm-competing-tactic-or-perceived-danger");
            if(evidence==null)return DeclineSquad(s,"S06","storm-no-observed-vulnerability");
            var previous=stormBoard.LastAttempt;plan=stormBoard.Start(group,e.EnemyProfileId,Time.time,evidence,candidates,c);
            if(plan==null&&!ReferenceEquals(previous,stormBoard.LastAttempt)&&stormBoard.LastAttempt is SquadStormPlan declined)
                foreach(var member in declined.Members)if(b.Squad.Members.TryGetValue(member.Id,out var mate)&&states.TryGetValue(mate,out var ms))RecordStormDeclined(mate,ms,declined);
        }
        if(plan==null||!plan.Active||plan.Enemy!=e.EnemyProfileId||plan.Member(b.ProfileId) is not StormMember role)return false;
        if(role.Status is "declined" or "ended")return false;
        if(Time.time-plan.Started>=c.PrepareTimeout){plan.Drop(b.ProfileId,"storm-missed-departure");return false;}
        if(!r.Policy.Begin(Time.time,StormMotionConfig(),0))return false;
        RememberCrossfireHandoff(b,r);
        r.Storm.Plan=plan;r.Storm.Member=role;r.Maneuver=null;r.ManeuverRole=null;r.Entrance.Drop();
        r.Origin=b.Position;r.Known=known;r.Contact=e.EnemyProfileId;r.ContactInstance=RuntimeHelpers.GetHashCode(e);r.ContactAt=Time.time-e.TimeSinceLastKnownUpdated;
        r.Group=group;r.Support=plan.Observer;r.Reference=b.Position;r.Route=null;r.Candidate=r.Sample=0;r.Rejects.Clear();r.Candidates.Clear();r.Samples.Clear();
        var t=r.Policy.Trace!;t.Storm=plan.Trace(b.ProfileId,Time.time,c);t.Contact=r.Contact;t.Source=NativeAdapter.Capture(b,e,true,false).ContactSource;
        t.X=known.x;t.Y=known.y;t.Z=known.z;t.ContactAge=e.TimeSinceLastKnownUpdated;t.Roll=role.Roll*100;t.Chance=role.Chance;t.Remaining=c.Duration;
        t.Reason="storm-prepare-approach";t.Execution="finding a distinct short approach";
        var forward=known-b.Position;forward.y=0;forward=forward.normalized;var side=new Vector3(-forward.z,0,forward.x);
        foreach(var offset in new[]{0,-3,3,-6,6})r.Candidates.Add(known-forward*3+side*offset);
        RecordCrossfire(s,true);return true;
    }
    private void RecordStormDeclined(BotComponent b,BotState s,SquadStormPlan p)
    {
        var k=b.GoalEnemy!.KnownPlaces.LastKnownPosition!.Value;
        var t=new CrossfireTrace{Episode=++s.Crossfire.ManeuverAttempts,State="ended",Reason=p.Reason,Execution="kept native action",Contact=p.Enemy,
            Source="squad observed evidence",ContactAge=b.GoalEnemy.TimeSinceLastKnownUpdated,X=k.x,Y=k.y,Z=k.z,Storm=p.Trace(b.ProfileId,Time.time,config!.SquadStorm),
            Alternatives=new[]{"Continue native SAIN combat","Individual opportunity remains available"}};
        var json=Diagnostics.Serialize(new{schema=1,session,sequence=++sequence,feature="S06",stage="evaluated",decision="Crossfire",reason=p.Reason,
            realtime=Time.realtimeSinceStartup,utc=DateTime.UtcNow.ToString("O"),profileId=s.ProfileId,name=s.Name,botInstance=s.Id,configRevision=config.Revision,crossfire=t,squadStormConfig=config.SquadStorm});
        diagnostics?.Write(json);Api.Decisions.Publish(json,ex=>{if(!subscriberWarned){subscriberWarned=true;Logger.LogWarning(ex.Message);}});
    }
    private void ProbeStorm(BotComponent b,BotState s)
    {
        var r=s.Crossfire;
        if(r.Route==null)
        {
            if(r.Candidate>=r.Candidates.Count){EndCrossfire(b,s,"storm-no-safe-approach");return;}
            var candidate=r.Candidates[r.Candidate++];r.Policy.Trace!.Candidates++;
            if(!NavMesh.SamplePosition(candidate,out var nav,.75f,NavMesh.AllAreas)||Math.Abs(nav.position.y-r.Origin.y)>1.5){RejectCrossfire(r,"no same-floor navigation");return;}
            r.Goal=nav.position;
            string deny=CrossfireSpace(b,s,r.Goal,true);
            if(deny.Length==0&&!CrossfireSight(r.Goal,r.Known))deny="no useful approach";
            if(deny.Length==0)deny=RunVolume(b,r.Goal,r.Goal+Vector3.up*.01f);
            if(deny.Length>0){RejectCrossfire(r,deny);return;}
            if(!b.Mover.CanGoToPoint(r.Goal,out var route,true,.2f)||!EntrancePath(route,b.Position,r.Goal,(float)config!.SquadStorm.MaxPath)){RejectCrossfire(r,"no complete bounded approach");return;}
            r.Route=route;r.Length=(float)Length(route.corners);r.Samples.Clear();r.Sample=0;
            for(int i=1;i<route.corners.Length;i++){var a=route.corners[i-1];var z=route.corners[i];int steps=Math.Max(1,(int)Math.Ceiling(Vector3.Distance(a,z)/2));for(int j=1;j<=steps;j++)r.Samples.Add(a+(z-a)*(j/(float)steps));}
            return;
        }
        if(r.Sample<r.Samples.Count)
        {
            var point=r.Samples[r.Sample];var from=r.Sample==0?r.Origin:r.Samples[r.Sample-1];r.Sample++;
            string deny=CrossfireSpace(b,s,point,false);if(deny.Length==0)deny=CrossfireThreatSafety(b,r,point);if(deny.Length==0)deny=RunVolume(b,from,point);
            if(deny.Length>0)RejectCrossfire(r,deny);return;
        }
        r.Storm.Member!.Eta=StormRouteEta(b,r);
        if(!double.IsFinite(r.Storm.Member.Eta)||r.Storm.Plan!.Evidence.Cue.Length>0&&Time.time+r.Storm.Member.Eta>=r.Storm.Plan.Evidence.Until)
        {EndCrossfire(b,s,"storm-too-late-for-opening");return;}
        if(CrossfireSpace(b,s,r.Goal,true).Length>0||!crossfireClaims.Reserve(s.Id,r.Goal.x,r.Goal.y,r.Goal.z,3,Time.time,r.Group)){RejectCrossfire(r,"approach already reserved");return;}
        r.Policy.Ready(r.Goal.x,r.Goal.y,r.Goal.z,0,r.Length);r.Policy.Trace!.Reason="storm-approach-ready";r.Storm.Member.Status="ready";r.Storm.Member.ReadyAt=Time.time;
    }
    private bool RefreshStorm(BotComponent b,BotState s)
    {
        var r=s.Crossfire;var p=r.Storm.Plan!;var m=r.Storm.Member!;var c=config!.SquadStorm;var t=r.Policy.Trace!;
        // Share a participant's perceived danger before visible combat hands that participant back.
        // Otherwise its withdrawal could discard the grenade cue while the rest keep approaching.
        if(p.Active&&CrossfireScope(b)&&b.GoalEnemy?.EnemyProfileId==p.Enemy&&ReadStormEvidence(b,s).Danger)
        {p.End("storm-teammate-perceived-danger");EndCrossfire(b,s,p.Reason);return false;}
        string block=StormBlock(b,s);
        if(block.Length>0){EndCrossfire(b,s,block);return false;}
        if(ManeuverGroup(b)!=p.Group){p.End("storm-squad-changed");EndCrossfire(b,s,p.Reason);return false;}
        if(!b.Squad.Members.TryGetValue(p.Observer,out var observer)||!CrossfireScope(observer)||!states.TryGetValue(observer,out var os)
            ||observer.GoalEnemy?.EnemyProfileId!=p.Enemy||observer.GoalEnemy.KnownPlaces.LastKnownPosition is not Vector3 ok||Vector3.Distance(ok,r.Known)>3
            ||Vector3.Distance(b.Position,observer.Position)>c.SquadDistance)
        {p.End("storm-observer-unavailable");EndCrossfire(b,s,p.Reason);return false;}
        var evidence=ReadStormEvidence(observer,os);
        // Evidence can become less convincing; a running plan never extends its original opening.
        evidence.Until=Math.Min(evidence.Until,p.Evidence.Until);p.Evidence=evidence;p.EvidenceAt=Math.Max(p.EvidenceAt,evidence.At);
        foreach(var member in p.Members)
        {
            if(member.Status is "ended" or "declined")continue;
            if(!b.Squad.Members.TryGetValue(member.Id,out var mate)||!states.TryGetValue(mate,out var ms)||StormBlock(mate,ms).Length>0
                ||Vector3.Distance(mate.Position,observer.Position)>c.SquadDistance||mate.GoalEnemy?.EnemyProfileId!=p.Enemy
                ||mate.GoalEnemy.KnownPlaces.LastKnownPosition is not Vector3 mk||Vector3.Distance(mk,r.Known)>3)
            {p.Drop(member.Id,"storm-teammate-unavailable");continue;}
            if(ReadStormEvidence(mate,ms).Danger){p.End("storm-teammate-perceived-danger");break;}
        }
        if(!p.Tick(Time.time,c)||m.Status=="ended"){EndCrossfire(b,s,m.Reason.Length>0?m.Reason:p.Reason);return false;}
        r.ContactAt=Time.time-b.GoalEnemy!.TimeSinceLastKnownUpdated;t.ContactAge=b.GoalEnemy.TimeSinceLastKnownUpdated;
        if(t.Reserved&&!crossfireClaims.Renew(s.Id,Time.time)){EndCrossfire(b,s,"storm-position-expired");return false;}
        if(t.State=="planning")
        {
            if(Vector3.Distance(b.Position,r.Origin)>1){EndCrossfire(b,s,"storm-origin-moved");return false;}
            if(r.ProbeFrame!=Time.frameCount&&doorBudget.TryBegin(Time.frameCount))
            {r.ProbeFrame=Time.frameCount;doorClock.Restart();try{ProbeStorm(b,s);}finally{doorClock.Stop();doorBudget.Charge(doorClock.Elapsed.TotalMilliseconds);}}
        }
        if(!r.Policy.Active)return false;
        t.Remaining=Math.Max(0,Math.Min(p.Until,p.Evidence.Until)-Time.time);t.Applied=r.Layer&&m.Confirmed&&p.State=="active"&&t.State=="moving";
        t.ValidFor=t.Applied?Math.Min(1,t.Remaining):0;RecordCrossfire(s,false);return t.State!="planning";
    }
    private void ExecuteStorm(BotComponent b,BotState s)
    {
        var r=s.Crossfire;var p=r.Storm.Plan!;var m=r.Storm.Member!;
        if(r.Policy.Trace!.State=="ready")
        {
            if(b.Mover.ActivePath!=null||QueuedCombatMovement(b)||Vector3.Distance(b.Position,r.Origin)>1){EndCrossfire(b,s,"storm-native-route-busy");return;}
            if(Time.time-p.Started>=config!.SquadStorm.PrepareTimeout&&!m.Ready){EndCrossfire(b,s,"storm-preparation-timeout");return;}
            if(p.Evidence.Cue.Length>0&&Time.time+m.Eta>=p.Evidence.Until){EndCrossfire(b,s,"storm-too-late-for-opening");return;}
            m.Ready=true;m.LastCallback=Time.time;
            if(!p.Tick(Time.time,config!.SquadStorm)){EndCrossfire(b,s,p.Reason);return;}
            if(p.State!="active"){r.Policy.Trace.Execution="ready; awaiting other attackers";return;}
        }
        ExecuteCrossfire(b,s);
        if(!r.Policy.Active)return;
        m.LastCallback=Time.time;m.Status=r.Policy.Trace.State;m.Confirmed=r.Policy.Trace.State=="moving"&&OwnCrossfirePath(b,r);
        if(m.Confirmed)r.Policy.Trace.Reason="storm-coordinated-approach";
        if(r.Policy.Trace.State=="holding")EndCrossfire(b,s,"storm-known-sector-reached");
    }
    private void EndStorm(BotState s,string why)
    {
        var r=s.Crossfire;var p=r.Storm.Plan;if(r.Policy.Trace?.Storm==null||p==null)return;
        p.Drop(s.ProfileId,why);if(!p.Active)stormBoard.Finish(p,Time.time,config?.SquadStorm??new(),p.Reason);
        r.Policy.Trace.Storm=p.Trace(s.ProfileId,Time.time,config?.SquadStorm??new());
    }
}
