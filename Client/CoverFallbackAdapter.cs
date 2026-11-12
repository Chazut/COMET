using System;
using System.Collections.Generic;
using Comet.Core;
using SAIN.Components;
using SAIN.SAINComponent.SubComponents.CoverFinder;
using UnityEngine;
using UnityEngine.AI;
namespace Comet.Client;

internal sealed class CoverFallbackRuntime
{
    internal readonly Collider[] Colliders=new Collider[24];
    internal readonly float[] Distances=new float[24];
    internal readonly Dictionary<string,int> Rejected=new();
    internal PathData? Path;
    internal Collider? Obstacle;
    internal Vector3 Candidate;
    internal bool Partial;
    internal int Segment,Step;
    internal float Started;
    internal int Count,Cursor,Frame=-1;
    internal float Retry,CheckedAt=-100;
    internal string Contact="",Tier="";
    internal Vector3 Origin,Known;
    internal CoverPoint? Point;
    internal bool Requested;
}
public sealed partial class Plugin
{
    private readonly Dictionary<BotComponent,CoverFallbackRuntime> coverFallbacks=new();
    partial void TickCoverFallbacks(){foreach(var pair in coverFallbacks)SampleCoverFallback(pair.Key,pair.Value);}
    partial void ClearCoverFallbacks(){foreach(var r in coverFallbacks.Values)DiscardFallback(r);coverFallbacks.Clear();}
    private static void DiscardFallback(CoverFallbackRuntime r)
    {if(r.Point!=null)r.Point.CoverData.IsBad=true;r.Point=null;}
    partial void CheckFallbackCover(BotComponent b,CoverPoint point,ref bool handled,ref bool usable,ref string reason)
    {
        if(!coverFallbacks.TryGetValue(b,out var r)||!ReferenceEquals(r.Point,point))return;
        handled=true;usable=ValidateFallback(b,point,out _);reason=usable?"cover-fallback-"+r.Tier+"-rechecked":"cover-fallback-invalidated";
    }
    internal static bool TryFallbackCover(BotComponent b,CoverPoint point,out bool usable)
    {usable=false;if(Instance==null)return false;usable=Instance.ValidateFallback(b,point,out bool recognized);return recognized;}
    internal static CoverPoint? RequestCoverFallback(BotComponent b,PlanProgress? progress)
    {
        var p=Instance;
        if(p?.config?.Enabled!=true||!p.config.CoverScan.Enabled||!p.Authority||!NativeAdapter.IsSupportedAi(b)||!b.BotActive||b.IsDead||b.GameEnding||p.Ghost(b.ProfileId)||!ContactSafety.AllowsGoal(b))return null;
        if(!p.coverFallbacks.TryGetValue(b,out var r))
        {if(p.coverFallbacks.Count>=512)return null;p.coverFallbacks.Add(b,r=new());}
        r.Requested=true;
        if(r.Point==null)return null;
        if(progress?.Blocked(ProgressAdapter.CoverKey(r.Point),Time.time)==true)
        {DiscardFallback(r);r.Count=0;return null;}
        return p.ValidateFallback(b,r.Point,out var recognized)&&recognized?r.Point:null;
    }
    private bool ValidateFallback(BotComponent b,CoverPoint point,out bool recognized)
    {
        recognized=coverFallbacks.TryGetValue(b,out var r)&&ReferenceEquals(r.Point,point);
        if(!recognized)return false;
        var e=b.GoalEnemy;
        if(config?.Enabled!=true||!config.CoverScan.Enabled||!Authority||!b.BotActive||b.IsDead||b.GameEnding||Ghost(b.ProfileId)||!NativeAdapter.IsSupportedAi(b)||!ContactSafety.AllowsGoal(b)
            ||e?.EnemyKnown!=true||e.TimeSinceLastKnownUpdated>config.ContactLifetime||e.EnemyProfileId!=r!.Contact||e.KnownPlaces.LastKnownPosition is not Vector3 known
            ||!FiniteFallback(known)||!FiniteFallback(b.Position)||Vector3.Distance(known,r.Known)>2||point.Spotted||point.Collider==null)
        {point.CoverData.IsBad=true;return false;}
        // Validation is reused by selection and movement, never bypassed because
        // the native chooser has no alternative. Limit repeated geometry to 4 Hz.
        if(Time.time-r.CheckedAt<.25f)return !point.CoverData.IsBad;
        if(!doorBudget.TryBegin(Time.frameCount,b))return !point.CoverData.IsBad&&Time.time-r.CheckedAt<1;
        doorClock.Restart();
        try
        {
            r.CheckedAt=Time.time;
            bool owned=ReferenceEquals(b.Cover.CoverInUse,point)||ReferenceEquals(b.Cover.CoverPoint_MovingTo,point);
            int protection=FallbackProtection(point.Position,known);
            bool valid=(r.Tier=="full"?protection==7:(protection&3)==3)
                &&FallbackSpace(b,point.Position)&&FallbackOtherThreats(b,point.Position)
                &&(owned||VisibleAdapter.ValidPath(point.PathToPoint,b.Position,point.Position));
            point.CoverData.IsBad=!valid;return valid;
        }
        finally{doorClock.Stop();doorBudget.Charge(doorClock.Elapsed.TotalMilliseconds);}
    }

    private static bool FiniteFallback(Vector3 v)=>!float.IsNaN(v.x)&&!float.IsInfinity(v.x)&&!float.IsNaN(v.y)&&!float.IsInfinity(v.y)&&!float.IsNaN(v.z)&&!float.IsInfinity(v.z);
    private static int FallbackProtection(Vector3 point,Vector3 known)
    {
        int mask=0;var target=known+Vector3.up*1.3f;
        for(int i=0;i<3;i++)
            if(Physics.Linecast(point+Vector3.up*(i==0?.55f:i==1?.95f:1.5f),target,LayersMaskController.HighPolyWithTerrainMask,QueryTriggerInteraction.Ignore))mask|=1<<i;
        return mask;
    }
    private bool FallbackOtherThreats(BotComponent b,Vector3 point)
    {
        int count=0,visited=0;
        foreach(var enemy in b.EnemyController.KnownEnemies)
        {
            if(++visited>16)return false;
            if(enemy==null||!enemy.EnemyKnown||enemy.EnemyProfileId==b.GoalEnemy?.EnemyProfileId||enemy.TimeSinceLastKnownUpdated>config!.ContactLifetime)continue;
            if(++count>4)return false;
            if(enemy.KnownPlaces.LastKnownPosition is not Vector3 known)continue;
            if(Vector3.Distance(point,known)<3||FallbackProtection(b.Position,known)==7&&FallbackProtection(point,known)!=7)return false;
        }
        return true;
    }
    private static bool FallbackSpace(BotComponent b,Vector3 point)
    {
        if(b.Squad.Members==null||b.Squad.Members.Count>16)return false;
        foreach(var mate in b.Squad.Members.Values)
        {
            if(mate==null||ReferenceEquals(mate,b)||mate.IsDead)continue;
            if(Vector3.Distance(point,mate.Position)<1.2)return false;
            var cover=mate.Cover.CoverPoint_MovingTo??mate.Cover.CoverInUse;
            if(cover!=null&&Vector3.Distance(point,cover.Position)<1.2)return false;
            if(mate.GoalEnemy?.IsVisible==true&&mate.GoalEnemy.KnownPlaces.LastKnownPosition is Vector3 target
                &&CrossfireGeometry.InLane(point.x,point.y,point.z,mate.Position.x,mate.Position.y,mate.Position.z,target.x,target.y,target.z))return false;
        }
        return true;
    }
    private void SampleCoverFallback(BotComponent b,CoverFallbackRuntime r)
    {
        if(!r.Requested||Time.time<r.Retry||r.Frame==Time.frameCount)return;
        r.Frame=Time.frameCount;
        var e=b.GoalEnemy;
        if(config?.Enabled!=true||!config.CoverScan.Enabled||!Authority||!NativeAdapter.IsSupportedAi(b)||!b.BotActive||b.IsDead||b.GameEnding||Ghost(b.ProfileId)
            ||!ContactSafety.AllowsGoal(b)||e?.EnemyKnown!=true||e.TimeSinceLastKnownUpdated>config.ContactLifetime||e.KnownPlaces.LastKnownPosition is not Vector3 known||!FiniteFallback(known)||!FiniteFallback(b.Position))
        {DiscardFallback(r);r.Path=null;r.Obstacle=null;r.Count=0;r.Requested=false;Array.Clear(r.Colliders,0,r.Colliders.Length);return;}
        if(r.Point!=null&&ValidateFallback(b,r.Point,out _)){r.Requested=false;return;}
        if(!doorBudget.TryBegin(Time.frameCount,b))return;
        doorClock.Restart();
        try
        {
            if(r.Count==0&&r.Path==null||r.Contact!=e.EnemyProfileId||Vector3.Distance(known,r.Known)>2||Vector3.Distance(b.Position,r.Origin)>2)
            {
                DiscardFallback(r);r.Path=null;r.Obstacle=null;r.Cursor=0;r.Tier="";r.Rejected.Clear();r.Started=Time.time;
                r.Contact=e.EnemyProfileId;r.Known=known;r.Origin=b.Position;
                Array.Clear(r.Colliders,0,r.Colliders.Length);
                int found=Physics.OverlapSphereNonAlloc(b.Position,8,r.Colliders,LayersMaskController.HighPolyWithTerrainMask,QueryTriggerInteraction.Ignore);
                // At most eight nearby obstacles, without retaining a scene-wide list.
                for(int i=0;i<found;i++)r.Distances[i]=(r.Colliders[i].bounds.center-b.Position).sqrMagnitude;
                for(int i=0;i<found;i++)for(int j=i+1;j<found;j++)
                    if(r.Distances[j]<r.Distances[i])
                    {var swap=r.Colliders[i];r.Colliders[i]=r.Colliders[j];r.Colliders[j]=swap;var d=r.Distances[i];r.Distances[i]=r.Distances[j];r.Distances[j]=d;}
                r.Count=Math.Min(found,8);
                RecordCoverFallback(b,"planning","cover-fallback-local-search",r);
            }
            if(r.Path!=null){ProbeFallbackRoute(b,r);return;}
            // First try full protection at each obstacle, then low/partial cover.
            if(r.Count==0||r.Cursor>=r.Count*6)
            {RecordCoverFallback(b,"ended","cover-fallback-no-safe-position",r);r.Count=0;r.Retry=Time.time+2;r.Requested=false;Array.Clear(r.Colliders,0,r.Colliders.Length);return;}
            int attempt=r.Cursor++;var collider=r.Colliders[attempt%r.Count];
            if(collider==null||collider.attachedRigidbody!=null){RejectFallback(r,"moving-or-missing-obstacle");return;}
            int variant=attempt/r.Count;bool partial=variant>=3;int offset=variant%3;
            var bounds=collider.bounds;var away=bounds.center-known;away.y=0;away=away.normalized;
            var side=new Vector3(-away.z,0,away.x);
            float extent=Math.Abs(away.x)*bounds.extents.x+Math.Abs(away.z)*bounds.extents.z;
            var candidate=bounds.center+away*(extent+.6f)+side*(offset==0?0:offset==1?.8f:-.8f);candidate.y=b.Position.y;
            if(!NavMesh.SamplePosition(candidate,out var nav,.5f,NavMesh.AllAreas)||Math.Abs(nav.position.y-b.Position.y)>1.5f)
            {RejectFallback(r,"navigation-or-floor");return;}
            candidate=nav.position;
            int protection=FallbackProtection(candidate,known);
            if(Vector3.Distance(candidate,r.Origin)>8||Vector3.Distance(candidate,known)<6){RejectFallback(r,"distance");return;}
            if(!FallbackSpace(b,candidate)){RejectFallback(r,"squad-spacing-or-fire-lane");return;}
            if(partial?(protection&3)!=3:protection!=7){RejectFallback(r,"insufficient-protection");return;}
            if(!FallbackOtherThreats(b,candidate)){RejectFallback(r,"other-known-threat");return;}
            if(states.TryGetValue(b,out var state)&&state.Progress.Blocked(ProgressAdapter.Destination(candidate),Time.time))
            {RejectFallback(r,"previously-failed-destination");return;}
            var path=new PathData();path.CalcPath(b.Position,candidate);
            if(!VisibleAdapter.ValidPath(path.Path,b.Position,candidate)||path.PathLength>15||path.Path.corners.Length>32)
            {RejectFallback(r,"incomplete-or-long-route");return;}
            r.Path=path;r.Candidate=candidate;r.Obstacle=collider;r.Partial=partial;r.Segment=1;r.Step=1;

        }
        finally{doorClock.Stop();doorBudget.Charge(doorClock.Elapsed.TotalMilliseconds);}
    }
    private static void RejectFallback(CoverFallbackRuntime r,string reason)
    {r.Rejected.TryGetValue(reason,out int n);r.Rejected[reason]=n+1;r.Path=null;r.Obstacle=null;}
    private void ProbeFallbackRoute(BotComponent b,CoverFallbackRuntime r)
    {
        var corners=r.Path!.Path.corners;
        if(r.Segment<corners.Length)
        {
            var a=corners[r.Segment-1];var z=corners[r.Segment];
            int steps=Math.Max(1,(int)Math.Ceiling(Vector3.Distance(a,z)));
            var point=a+(z-a)*(r.Step/(float)steps);
            if(Vector3.Distance(point,r.Known)<3||Math.Abs(point.y-r.Origin.y)>1.5f||!FallbackSpace(b,point)||!FallbackOtherThreats(b,point))
            {RejectFallback(r,"unsafe-route-sample");return;}
            if(++r.Step>steps){r.Step=1;r.Segment++;}
            return;
        }
        if(r.Obstacle==null||Vector3.Distance(b.Position,r.Origin)>1||!FallbackSpace(b,r.Candidate))
        {RejectFallback(r,"candidate-changed");return;}
        int protection=FallbackProtection(r.Candidate,r.Known);
        if(r.Partial?(protection&3)!=3:protection!=7){RejectFallback(r,"protection-changed");return;}
        // Geometry was verified along the actual route, one sample per budgeted
        // frame. Consumers receive a real cover point, never a raw random location.
        r.Point=new CoverPoint(b,r.Obstacle,r.Path,r.Candidate);r.Tier=r.Partial?"partial":"full";r.CheckedAt=Time.time;
        r.Requested=false;RecordCoverFallback(b,"ready","cover-fallback-"+r.Tier+"-candidate",r);
        r.Path=null;r.Obstacle=null;Array.Clear(r.Colliders,0,r.Colliders.Length);r.Count=0;
    }
    private void RecordCoverFallback(BotComponent b,string state,string reason,CoverFallbackRuntime r)
    {
        var json=Diagnostics.Serialize(new {schema=1,session,sequence=++sequence,feature="C07",stage="evaluated",decision="CoverScan",reason,
            realtime=Time.realtimeSinceStartup,utc=DateTime.UtcNow.ToString("O"),configRevision=config?.Revision??0,profileId=b.ProfileId,name=b.Player.Profile.Nickname,
            botInstance=states.TryGetValue(b,out var s)?s.Id:"",
            coverScan=new {state=state=="planning"?"scanning":state=="ready"?"published":state,reason,
                contact=r.Contact,selected=r.Point==null?"":ProgressAdapter.CoverKey(r.Point),scan=0,published=r.Point==null?0:1,pending=r.Path==null?0:1,
                elapsed=Math.Max(0,Time.time-r.Started),validFor=0,fallback=r.Tier,candidates=r.Cursor,rejected=r.Rejected,
                goalX=r.Point?.Position.x,goalY=r.Point?.Position.y,goalZ=r.Point?.Position.z}});
        diagnostics?.Write(json);Api.Decisions.Publish(json,_=>{});
    }
}
