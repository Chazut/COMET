using System;
using System.Collections.Generic;
using System.Linq;
using Comet.Core;
using Newtonsoft.Json;
using SAIN.Components;
using SAIN.Models.Enums;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.Mover;
using UnityEngine;
using UnityEngine.AI;

namespace Comet.Client;

internal sealed class DoorSupportOperation
{
    internal string Id="",Target="";
    internal DoorWork Door=new();
    internal BotComponent Leader=null!;
    internal double Until;
    internal int TargetInstance,Cursor;
    internal bool Ended,Suspended,Planned;
    internal readonly SourceDoorLeaderLifetime Lifetime=new();
    internal readonly List<string> Refusals=new();
}
internal sealed class DoorSupportRuntime
{
    internal DoorSupportOperation Operation=null!;
    internal DoorSupportTrace Trace=null!;
    internal BotComponent Owner=null!;
    internal IBotPathData? Path;
    internal int TargetInstance;
    internal float LastCheck,LastCallback,NextCheck,LogAt=-1000;
    internal string LogKey="";
    internal float MoveAt=-1,NextMove,ProgressAt;
    internal Vector3 ProgressPosition;
}

public sealed partial class Plugin
{
    private readonly DoorSupportBoard supportBoard=new();
    private readonly Dictionary<BotComponent,DoorSupportOperation> supportOperations=new();
    private static DoorWork? SupportingDoor(BotState s)
        =>s.Entry.Policy.Active?s.Entry.Door:s.RoomClear.Policy.Active?s.RoomClear.Door:s.Fake.Policy.Active?s.Fake.Door:s.StepPeek.Policy.Active?s.StepPeek.Door
            :s.RunBy.Policy.Active?s.RunBy.Door:s.Jump.Policy.Active?s.Jump.Door:s.Ambush.Policy.Active?s.Ambush.Chosen
            :s.Door.Policy.Active?s.Door.Chosen:null;
    private double SupportBudget(BotComponent b,BotState s)
    {return config!.DoorSupport.Duration;}
    private bool SupportLeaderAlive(DoorSupportOperation o)
    {
        var leader=o.Leader;
        return o.Lifetime.Continue(Time.time,leader!=null&&!leader.IsDead&&FakeScope(leader)&&!o.Ended,
            leader!=null&&states.TryGetValue(leader,out var ls)&&SupportingDoor(ls)?.Geometry.Id==o.Door.Geometry.Id);
    }
    private string SupportGate(BotComponent b,BotState s,DoorSupportOperation o,bool owner=false)
    {
        if(config?.Enabled!=true||!config.DoorSupport.Enabled||!config.DoorApproach.Enabled||!FakeScope(b)||b.IsDead||b.GameEnding)return "support-out-of-scope";
        if(!SupportLeaderAlive(o))return "support-leader-ended";
        var e=b.GoalEnemy;
        if(e==null||e.EnemyProfileId!=o.Target)return "support-contact-not-shared";
        // TryStartSupportRole checks visibility when accepting, not while executing the role.
        if(!owner) {
            if(e.IsVisible)return "support-visible-threat";
            var center=VP(o.Door.Geometry.Center);var side=o.Leader.Position-center;side.y=0;
            var delta=b.Position-center;delta.y=0;
            if(delta.magnitude>config.DoorSupport.MaxDistance||Vector3.Dot(delta,side)<0)return "support-too-far-or-other-side";
            if(SupportingDoor(s)!=null)return "support-busy";
        }
        return "";
    }
    private void UpdateDoorSupports()
    {
        foreach(var pair in supportOperations.ToArray())
        {
            var o=pair.Value;
            if(!SupportLeaderAlive(o))
            {EndSupportOperation(o,"support-leader-ended");supportOperations.Remove(pair.Key!);continue;}
            if(!states.TryGetValue(pair.Key,out var s))continue;
            o.Until=Math.Min(o.Until,Time.time+SupportBudget(pair.Key,s));
            if(Time.time>=o.Until||config?.DoorSupport.Enabled!=true){EndSupportOperation(o,"support-budget-ended");continue;}
        }
        foreach(var pair in states.ToArray())
        {
            if(pair.Value.Support!=null)RefreshDoorSupport(pair.Key,pair.Value);
            var b=pair.Key;var s=pair.Value;var w=SupportingDoor(s);
            if(config?.Enabled!=true||!config.DoorSupport.Enabled||w==null||!FakeScope(b)||b.IsDead||b.GoalEnemy==null||s.Support!=null)continue;
            if(!supportOperations.TryGetValue(b,out var o))
            {
                double available=SupportBudget(b,s);if(available<=0)continue;
                o=new(){Id=Guid.NewGuid().ToString("N"),Leader=b,Door=w,Target=b.GoalEnemy.EnemyProfileId,
                    TargetInstance=System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(b.GoalEnemy),Until=Time.time+available};
                if(!supportBoard.Begin(o.Id,b.ProfileId,w.Geometry.Id,o.Target,Time.time,available,config.DoorSupport))continue;
                supportOperations.Add(b,o);
            }
            if(!o.Ended&&!o.Suspended)PlanDoorSupport(o);
        }
    }
    private void PlanDoorSupport(DoorSupportOperation o)
    {
        if(o.Planned||!doorBudget.TryBegin(Time.frameCount,o.Leader))return;
        doorClock.Restart();
        try {
            o.Planned=true;
            var members=o.Leader.Squad.Members.Values.Where(b=>b!=null&&!ReferenceEquals(b,o.Leader)&&NativeAdapter.IsSupportedAi(b)&&b.BotActive&&!b.IsDead)
                .OrderBy(b=>SupportHorizontal(b.Position,VP(o.Door.Geometry.Center))).ToArray();
            var eligible=new List<BotState>();var bots=new List<BotComponent>();
            foreach(var b in members) {
                if(!states.TryGetValue(b,out var state)){state=new BotState(b,config!,session);states.Add(b,state);}
                string why=SupportGate(b,state,o);if(why.Length>0){SupportRefusal(o,state,"candidate",why);continue;}
                if(state.Support==null){eligible.Add(state);bots.Add(b);}
            }
            if(bots.Count==0)return;
            var g=o.Door.Geometry;var center=VP(g.Center);float sign=Math.Sign(Vector3.Dot(o.Leader.Position-center,VP(g.Normal)));
            float lateral=-Math.Sign(Vector3.Dot(o.Leader.Position-center,VP(g.Axis)));if(lateral==0)lateral=-1;
            int index=0;bool found=false;Vector3 point=default,aim=default;
            foreach(float depth in new[]{7f,6.5f,6f,5.5f}) {
                foreach(float offset in new[]{(float)g.HalfWidth+1.5f,(float)g.HalfWidth+.8f,0}) {
                    var raw=center+VP(g.Normal)*sign*depth+VP(g.Axis)*lateral*offset;
                    if(SupportPoint(o.Leader,o,"overwatch",raw,true,out _,out point,out aim,out _).Length==0){found=true;break;}
                }
                if(found)break;
            }
            void Offer(int i,string role,Vector3 at,Vector3 look) {
                var b=bots[i];var state=eligible[i];
                var t=supportBoard.Offer(o.Id,b.ProfileId,role,DP(at),DP(look),0,0,Time.time,config!.DoorSupport);if(t==null)return;
                Release(state,"support-role-offered");t.Alternatives=o.Refusals.ToList();
                state.Support=new(){Operation=o,Trace=t,Owner=b,LastCheck=Time.time,LastCallback=Time.time};RecordDoorSupport(state,true);
            }
            if(found){Offer(index++,"overwatch",point,aim);}
            if(index<bots.Count&&(!found||config!.DoorSupport.MaxSupports>1)) {
                var at=bots[index].Position;var away=at-center;away.y=0;
                away=away.sqrMagnitude>.01f?away.normalized:VP(g.Normal)*sign;
                Offer(index,"rear-guard",at,at+away*10+Vector3.up*1.3f);
            }
        }
        finally{doorClock.Stop();doorBudget.Charge(doorClock.Elapsed.TotalMilliseconds);}
    }
    private static float SupportHorizontal(Vector3 a,Vector3 b){var d=a-b;d.y=0;return d.magnitude;}
    private void SupportRefusal(DoorSupportOperation o,BotState s,string role,string why)
    {
        string entry=s.ProfileId+" / "+role+" / "+why;if(o.Refusals.Contains(entry)||o.Refusals.Count>=12)return;
        o.Refusals.Add(entry);
        PublishDoorSupport(s,new(){Operation=o.Id,Leader=o.Leader.ProfileId,Support=s.ProfileId,Door=o.Door.Geometry.Id,Target=o.Target,
            Role=role,State="rejected",Reason=why,Alternatives=o.Refusals.ToList()});
    }
    private string SupportPoint(BotComponent b,DoorSupportOperation o,string role,Vector3 raw,bool calculate,
        out NavMeshPath? path,out Vector3 point,out Vector3 aim,out double protection)
    {
        path=null;point=raw;protection=0;var center=VP(o.Door.Geometry.Center);
        aim=center+Vector3.up*1.2f;
        if(role=="rear-guard") {
            var away=raw-center;away.y=0;away.Normalize();aim=raw+away*10+Vector3.up*1.3f;return "";
        }
        if(!NavMesh.SamplePosition(raw,out var hit,.7f,NavMesh.AllAreas))return "support-no-navmesh";
        point=hit.position;
        var side=VP(o.Door.Geometry.Normal)*Math.Sign(Vector3.Dot(o.Leader.Position-center,VP(o.Door.Geometry.Normal)));
        if(Vector3.Dot(point-center,side)<=.3f)return "support-other-side";
        if(calculate&&!b.Mover.CanGoToPoint(point,out path,true))return "support-no-path";
        return "";
    }
    private static float SegmentDistance(Vector3 p,Vector3 a,Vector3 b)
    {var d=b-a;return Vector3.Distance(p,a+d*(d.sqrMagnitude<.001f?0:Mathf.Clamp01(Vector3.Dot(p-a,d)/d.sqrMagnitude)));}
    private static bool RouteCrossesSupportLane(NavMeshPath? path,Vector3 start,Vector3 end)
    {
        if(path==null)return false;
        var corners=path.corners;
        for(int i=1;i<corners.Length;i++)
        {
            var a=corners[i-1];var z=corners[i];int samples=Math.Min(40,(int)Math.Ceiling(Vector3.Distance(a,z)/.4f));
            for(int j=0;j<=samples;j++)
                if(SegmentDistance(Vector3.Lerp(a,z,samples==0?0:j/(float)samples),start,end)<.8f)return true;
        }
        return false;
    }
    private bool KeepDoorSupport(BotComponent b,BotState s,ref ECombatDecision decision,ref bool result)
    {
        if(s.Support==null)return false;RefreshFear(b,s);RefreshProgress(b,s);RefreshDoorSupport(b,s);
        if(s.Support==null)return false;
        if(s.Feature!="D11"||s.Decision!="Hold")
        {
            s.Feature="D11";s.Decision="Hold";s.Candidates=new List<Candidate>{new(){Decision="Hold",Eligible=true,Reason="support-role-selected"},new(){Decision="Search",Eligible=false,Reason="support-temporary-role"}};
            Emit(s,"selected","support-role-selected");
        }
        s.Expected=ECombatDecision.Freeze;s.RequestedFrame=Time.frameCount;
        decision=s.Expected;result=true;return true;
    }
    private void SupportAccepted(BotState s,bool applied)
    {
        var r=s.Support;if(r==null)return;
        if(!applied){EndDoorSupport(s,"support-native-refused");return;}
        if(!r.Trace.Accepted){supportBoard.Accept(s.ProfileId,Time.time);RecordDoorSupport(s,true);}
    }
    private void RefreshDoorSupport(BotComponent b,BotState s)
    {
        var r=s.Support;if(r==null)return;string gate=SupportGate(b,s,r.Operation,r.Trace.Accepted);
        if(gate.Length>0){EndDoorSupport(s,gate);return;}
        double remaining=r.Operation.Until-Time.time;
        if(!supportBoard.Observe(s.ProfileId,Time.time,remaining)){EndDoorSupport(s,r.Trace.Reason);return;}
        RecordDoorSupport(s);
    }
    private bool StepDoorSupport(BotComponent b,BotState s)
    {
        var r=s.Support;if(r==null)return true;r.LastCallback=Time.time;RefreshDoorSupport(b,s);if(s.Support==null)return true;
        var t=r.Trace;if(!t.Accepted)return true;
        var point=new Vector3((float)t.X,(float)t.Y,(float)t.Z);
        if(t.Role=="rear-guard"||SupportHorizontal(b.Position,point)<.6f) {
            b.Mover.Stop();b.Mover.SetTargetPose(t.Role=="overwatch"?.6f:.8f);
            supportBoard.Arrive(s.ProfileId,DP(b.Position),true,false,false);RecordDoorSupport(s);return false;
        }
        if(r.MoveAt<0){r.MoveAt=Time.time;r.ProgressAt=Time.time+2.5f;r.ProgressPosition=b.Position;}
        if(Time.time-r.MoveAt>12){EndDoorSupport(s,"support-move-timeout");return true;}
        if(Time.time>r.ProgressAt){if(SupportHorizontal(b.Position,r.ProgressPosition)<.4f){EndDoorSupport(s,"support-move-stuck");return true;}r.ProgressAt=Time.time+2.5f;r.ProgressPosition=b.Position;}
        if(Time.time>r.NextMove) {
            r.NextMove=Time.time+.75f;
            bool accepted=b.Mover.WalkToPoint(point,true,.42f);r.Path=b.Mover.ActivePath;
            supportBoard.RequestMove(s.ProfileId,accepted);
            if(!accepted){EndDoorSupport(s,"support-move-refused");return true;}
        }
        b.Mover.SetTargetMoveSpeed(.75f);RecordDoorSupport(s);return false;
    }
    private bool SupportSteer(BotComponent b,BotState s)
    {
        var r=s.Support;if(r?.Trace.Accepted!=true||SupportGate(b,s,r.Operation,true).Length>0)return false;
        b.Steering.LookToPoint(new((float)r.Trace.LookX,(float)r.Trace.LookY,(float)r.Trace.LookZ));return true;
    }
    private void EndDoorSupport(BotState s,string reason)
    {
        var r=s.Support;if(r==null)return;
        supportBoard.End(s.ProfileId,reason);r.Trace.State="released";r.Trace.Reason=reason;r.Trace.AngleVerified=false;
        if(r.Owner!=null&&r.Path!=null&&ReferenceEquals(r.Owner.Mover.ActivePath,r.Path))r.Path.Cancel();
        RecordDoorSupport(s,true);s.Support=null;
        if(s.Feature=="D11"){s.Decision="";s.ActiveLabel="";s.RequestedFrame=-1;Api.Decisions.Set(s.ProfileId,"");}
    }
    private void EndSupportOperation(DoorSupportOperation o,string why)
    {
        if(o.Ended)return;o.Ended=true;
        foreach(var s in states.Values.ToArray())if(s.Support?.Operation==o)EndDoorSupport(s,why);
        supportBoard.EndOperation(o.Id,why);
    }
    private void ClearDoorSupports(string reason)
    {foreach(var o in supportOperations.Values.ToArray())EndSupportOperation(o,reason);supportOperations.Clear();supportBoard.Clear();}
    private void RecordDoorSupport(BotState s,bool force=false)
    {
        var r=s.Support;if(r==null)return;var t=r.Trace;string key=t.State+":"+t.Reason+":"+t.Accepted+":"+t.AngleVerified;
        if(!force&&key==r.LogKey&&Time.time-r.LogAt<.5)return;r.LogKey=key;r.LogAt=Time.time;PublishDoorSupport(s,t);
    }
    private void PublishDoorSupport(BotState s,DoorSupportTrace t)
    {
        t.SupportName=s.Name; var leader=supportOperations.Values.FirstOrDefault(o=>o.Id==t.Operation)?.Leader; if(leader!=null&&states.TryGetValue(leader,out var leaderState))t.LeaderName=leaderState.Name;
        string json=Diagnostics.Serialize(new{schema=1,session,sequence=++sequence,feature="D11",stage="evaluated",decision="DoorSupport",reason=t.Reason,
            realtime=Time.realtimeSinceStartup,utc=DateTime.UtcNow.ToString("O"),configRevision=config?.Revision??0,profileId=s.ProfileId,name=s.Name,botInstance=s.Id,
            doorSupport=t});
        diagnostics?.Write(json);Api.Decisions.Publish(json,ex=>{if(!subscriberWarned){subscriberWarned=true;Logger.LogWarning(ex.Message);}});
    }
}
