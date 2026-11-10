using System;
using Comet.Core;
using SAIN.Components;
using SAIN.SAINComponent.Classes.Mover;
using UnityEngine;
using UnityEngine.AI;
namespace Comet.Client;
public sealed partial class Plugin
{
    private void BuildCrossfireCandidates(BotComponent b,CrossfireRuntime r)
    {
        r.Candidates.Clear();r.Samples.Clear();
        var forward=r.Known-r.Origin;forward.y=0;forward=forward.normalized;
        var side=new Vector3(-forward.z,0,forward.x);float sign=jumpRandom.Next(2)==0?1:-1;
        // Local, bounded search; short routes first. No global cover scan or hidden enemy transform.
        for(int ring=1;ring<=4;ring++)for(int i=0;i<7;i++)
        {
            float a=(i+1)*Mathf.PI/4*sign,rad=Math.Min(ring*6,(float)config!.Crossfire.MaxPath*.7f);
            r.Candidates.Add(r.Origin+(side*Mathf.Sin(a)+forward*Mathf.Cos(a))*rad);
        }
    }
    private static float CrossfireAngle(Vector3 a,Vector3 b,Vector3 target)
    {a-=target;b-=target;a.y=b.y=0;return a.sqrMagnitude<.01f||b.sqrMagnitude<.01f?0:Vector3.Angle(a,b);}
    private string CrossfireSpace(BotComponent b,BotState s,Vector3 point,bool goal)
    {
        var r=s.Crossfire;var c=r.Policy.Trace?.Storm!=null?StormMotionConfig():r.Policy.Trace?.Trade!=null?TradeMotionConfig():config!.Crossfire;
        if(r.Policy.Trace?.CoverMate!=null)return CoverMateSpace(b,s,point,goal);
        if(!crossfireClaims.Free(s.Id,point.x,point.y,point.z,goal?c.Spacing:1.2,Time.time,r.Group))return "reserved position";
        foreach(var mate in b.Squad.Members.Values)
        {
            if(mate==null||ReferenceEquals(mate,b)||mate.IsDead)continue;
            if(Vector3.Distance(point,mate.Position)<(goal?c.Spacing:1.2))return "teammate spacing";
            if(mate.GoalEnemy?.EnemyProfileId==r.Contact&&(mate.GoalEnemy.IsVisible||r.Maneuver?.Member(mate.ProfileId)?.Role=="support")
                &&CrossfireGeometry.InLane(point.x,point.y,point.z,mate.Position.x,mate.Position.y,mate.Position.z,r.Known.x,r.Known.y,r.Known.z))return "allied firing lane";
            if(goal&&states.TryGetValue(mate,out var other)&&other.Crossfire.Policy.Trace?.Reserved==true
                &&other.Crossfire.Contact==r.Contact&&CrossfireAngle(point,other.Crossfire.Goal,r.Known)<10)return "angle already reserved";
        }
        return "";
    }
    private string CrossfireThreatSafety(BotComponent b,CrossfireRuntime r,Vector3 point)
    {
        int count=0,checkedThreats=0;
        foreach(var enemy in b.EnemyController.KnownEnemies)
        {
            if(++count>16)return "threat list over budget";
            if(enemy==null||!enemy.EnemyKnown||enemy.EnemyProfileId==r.Contact||enemy.TimeSinceLastKnownUpdated>config!.Crossfire.ContactAge)continue;
            if(++checkedThreats>4)return "too many nearby threats";
            if(enemy.KnownPlaces.LastKnownPosition is not Vector3 known||!ValidPoint(known))continue;
            if(Vector3.Distance(point,known)<4)return "near another known threat";
            if(CombatProtection(r.Origin,known)==7&&CombatProtection(point,known)!=7)return "exposes another known threat";
        }
        return "";
    }
    private static bool CrossfireSight(Vector3 point,Vector3 known)=>
        !Physics.Linecast(point+Vector3.up*1.4f,known+Vector3.up*1.2f,LayersMaskController.HighPolyWithTerrainMask,QueryTriggerInteraction.Ignore);
    private void RejectCrossfire(CrossfireRuntime r,string why)
    {
        if(!r.Rejects.Contains(why)&&r.Rejects.Count<8)r.Rejects.Add(why);
        r.Policy.Trace!.Alternatives=r.Rejects.ToArray();r.Route=null;r.Samples.Clear();r.Sample=0;
    }
    private void ProbeCrossfire(BotComponent b,BotState s)
    {
        var r=s.Crossfire;var c=config!.Crossfire;
        if(r.Route==null)
        {
            if(r.Candidate>=r.Candidates.Count){EndCrossfire(b,s,"crossfire-no-safe-angle");return;}
            var candidate=r.Candidates[r.Candidate++];r.Policy.Trace!.Candidates=r.Candidate;
            if(!NavMesh.SamplePosition(candidate,out var nav,.75f,NavMesh.AllAreas)||Math.Abs(nav.position.y-r.Origin.y)>2.5)
            {RejectCrossfire(r,"no same-floor navigation");return;}
            r.Goal=nav.position;r.Angle=CrossfireAngle(r.Reference,r.Goal,r.Known);
            var initialAngle=CrossfireAngle(r.Reference,r.Origin,r.Known);
            r.Policy.Trace!.RequiredAngle=CrossfireGeometry.RequiredAngle(r.Indoor?c.IndoorAngle:c.OutdoorAngle,
                initialAngle,Vector3.Distance(r.Origin,r.Known),Math.Min(24,c.MaxPath*.7));
            if(r.Angle<r.Policy.Trace.RequiredAngle)
            {RejectCrossfire(r,"angle gain too small");return;}
            string deny=CrossfireSpace(b,s,r.Goal,true);if(deny.Length>0){RejectCrossfire(r,deny);return;}
            if(!CrossfireSight(r.Goal,r.Known)){RejectCrossfire(r,"no firing angle to known position");return;}
            if(!b.Mover.CanGoToPoint(r.Goal,out var route,true,.2f)||route.status!=NavMeshPathStatus.PathComplete||route.corners.Length<2||route.corners.Length>32)
            {RejectCrossfire(r,"no complete bounded route");return;}
            var corners=route.corners;float length=0;
            for(int i=1;i<corners.Length;i++)length+=Vector3.Distance(corners[i-1],corners[i]);
            if(!float.IsFinite(length)||length>c.MaxPath||Vector3.Distance(corners[corners.Length-1],r.Goal)>1||Vector3.Distance(corners[0],b.Position)>1)
            {RejectCrossfire(r,"route too long or wrong endpoint");return;}
            r.Route=route;r.Length=length;r.Samples.Clear();r.Sample=0;
            for(int i=1;i<corners.Length;i++)
            {int steps=Math.Max(1,(int)Math.Ceiling(Vector3.Distance(corners[i-1],corners[i])/2));for(int j=1;j<=steps;j++)r.Samples.Add(corners[i-1]+(corners[i]-corners[i-1])*(j/(float)steps));}
            return;
        }
        if(r.Sample<r.Samples.Count)
        {
            var point=r.Samples[r.Sample++];string deny=CrossfireSpace(b,s,point,false);
            if(deny.Length==0)deny=CrossfireThreatSafety(b,r,point);
            if(deny.Length>0)RejectCrossfire(r,deny);
            return;
        }
        string occupied=CrossfireSpace(b,s,r.Goal,true);
        if(occupied.Length>0||!crossfireClaims.Reserve(s.Id,r.Goal.x,r.Goal.y,r.Goal.z,c.Spacing,Time.time,r.Group))
        {RejectCrossfire(r,occupied.Length>0?occupied:"position just reserved");return;}
        r.Policy.Ready(r.Goal.x,r.Goal.y,r.Goal.z,r.Angle,r.Length);
    }
    private static bool OwnCrossfirePath(BotComponent b,CrossfireRuntime r)=>r.Path!=null&&ReferenceEquals(b.Mover.ActivePath,r.Path)
        &&r.Path.TimeStarted==r.PathStarted&&Vector3.Distance(r.Path.Destination,r.Goal)<.5f;
    private static void StopCrossfireRoute(BotComponent b,CrossfireRuntime r)
    {
        try{if(b!=null&&OwnCrossfirePath(b,r)&&r.Path is BotPathDataManual path&&b.Player.MovementContext.IsGrounded)
            combatStopPath.Invoke(path,new object[]{false,"COMET crossfire released"});}
        finally{r.Path=null;}
    }
    private void ExecuteCrossfire(BotComponent b,BotState s)
    {
        var r=s.Crossfire;var c=r.Policy.Trace?.Storm!=null?StormMotionConfig():r.Policy.Trace?.Trade!=null?TradeMotionConfig():r.Policy.Trace?.CoverMate!=null?CoverMateMotionConfig():config!.Crossfire;var t=r.Policy.Trace!;
        if(t.State=="ready")
        {
            if(b.Mover.ActivePath!=null||QueuedCombatMovement(b))
            {if(Time.time-r.StartWait>=1)EndCrossfire(b,s,"crossfire-native-route-busy");return;}
            if(!doorBudget.TryBegin(Time.frameCount))return;doorClock.Restart();
            try
            {
                if(r.Route==null||Vector3.Distance(b.Position,r.Origin)>1||CrossfireSpace(b,s,r.Goal,true).Length>0||(t.Trade!=null?!TradeUseful(r,r.Goal):t.CoverMate!=null?!CoverMateUseful(r.Goal,r.Known):t.Entrance==null?!CrossfireSight(r.Goal,r.Known):EntranceApproachSafety(b,s,r.Goal).Length>0))
                {EndCrossfire(b,s,"crossfire-start-invalidated");return;}
                if(!b.Mover.WalkToPointByWay(r.Route,true,.3f,false)||b.Mover.ActivePath==null)
                {EndCrossfire(b,s,"crossfire-native-route-refused");return;}
                r.Path=b.Mover.ActivePath;r.PathStarted=r.Path.TimeStarted;
                if(!OwnCrossfirePath(b,r)){r.Path=null;EndCrossfire(b,s,"crossfire-native-route-unconfirmed");return;}
                r.LastSafety=Time.time;r.Policy.Moving(Time.time,Vector3.Distance(b.Position,r.Goal));
            }
            finally{doorClock.Stop();doorBudget.Charge(doorClock.Elapsed.TotalMilliseconds);}
        }
        if(t.State=="moving")
        {
            // A completed route can disappear on arrival; a replaced route belongs to its new owner.
            if(b.Mover.ActivePath!=null&&!OwnCrossfirePath(b,r)){r.Path=null;EndCrossfire(b,s,"crossfire-route-replaced");return;}
            if(!r.Policy.Progress(Time.time,Vector3.Distance(b.Position,r.Goal),r.Maneuver!=null?new CrossfireConfig{StallTimeout=config!.SquadManeuver.StallTimeout,Retry=c.Retry}:c)){EndCrossfire(b,s,t.Reason);return;}
            if(t.State=="holding"){StopCrossfireRoute(b,r);if(t.Entrance!=null){r.Entrance.Policy.Arrived(Time.time);t.Reason="secondary-entrance-watch-position-reached";t.Execution="watching verified secondary entrance";}}
            else if(b.Mover.ActivePath==null){EndCrossfire(b,s,"crossfire-route-ended-early");return;}
        }
        if(Time.time-r.LastSafety>=c.Interval&&doorBudget.TryBegin(Time.frameCount))
        {
            r.LastSafety=Time.time;doorClock.Restart();
            try
            {
                string deny=CrossfireSpace(b,s,r.Goal,true);
                if(deny.Length==0)deny=CrossfireThreatSafety(b,r,b.Position);
                if(deny.Length==0&&t.Trade!=null&&!TradeUseful(r,r.Goal))deny="trade sector occluded";
                if(deny.Length==0&&t.CoverMate!=null&&!CoverMateUseful(r.Goal,r.Known))deny="cover sector occluded";
                if(deny.Length==0&&t.Entrance!=null&&!r.Entrance.Entering)deny=EntranceApproachSafety(b,s,b.Position);
                // Check the next route sample as well as the current location, before advancing into it.
                if(deny.Length==0&&t.State=="moving")
                {
                    Vector3 next=r.Goal;float nearest=float.MaxValue;int index=-1;
                    for(int i=0;i<r.Samples.Count;i++){float d=Vector3.Distance(b.Position,r.Samples[i]);if(d<nearest){nearest=d;index=i;}}
                    if(index>=0)next=r.Samples[Math.Min(index+1,r.Samples.Count-1)];
                    deny=CrossfireSpace(b,s,next,false);if(deny.Length==0)deny=CrossfireThreatSafety(b,r,next);
                    if(deny.Length==0&&t.Trade!=null&&CombatProtection(r.Origin,r.Known)==7&&CombatProtection(next,r.Known)==0)deny="trade route leaves protection";
                    if(deny.Length==0&&(t.Entrance!=null||t.CoverMate!=null||t.Trade!=null||t.Storm!=null))deny=RunVolume(b,b.Position,next);
                }
                if(deny.Length>0)EndCrossfire(b,s,"crossfire-safety-changed: "+deny);
            }
            finally{doorClock.Stop();doorBudget.Charge(doorClock.Elapsed.TotalMilliseconds);}
        }
        if(r.Policy.Active&&Time.time-r.LastSafety>2)EndCrossfire(b,s,"crossfire-safety-budget-timeout");
    }
}
