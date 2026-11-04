using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Comet.Core;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using Newtonsoft.Json;
using SAIN.Components;
using SAIN.Helpers;
using SAIN.Layers;
using SAIN.Models.Enums;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.Mover;
using UnityEngine;
using UnityEngine.AI;

namespace Comet.Client;

internal sealed class DoorEmergencyRuntime
{
    internal readonly DoorEmergency Policy=new();
    internal BotComponent? Owner;
    internal NavMeshPath? Route;
    internal IBotPathData? Path;
    internal Vector3 Door,Threat,Destination,Start,Origin;
    internal int InitialProtection;
    internal int Candidate;
    internal readonly List<string> Alternatives=new();
    internal float EvidenceAt,NextCheck,LastCallback,RequestedAt,StartDistance,LogAt=-1000;
    internal bool Planned,Requested,Fault;
    internal string LogKey="";
    internal void Drop(){Owner=null;Route=null;Path=null;Planned=Requested=false;Candidate=0;Alternatives.Clear();}
}

public sealed partial class Plugin
{
    private static void PatchDoorEmergency(Harmony h)
    {
        var type=typeof(BotComponent).Assembly.GetType("SAIN.Layers.Combat.Solo.Cover.SeekCoverAction",true);
        h.Patch(AccessTools.Method(type,"Update")??throw new MissingMethodException("D13 SeekCoverAction.Update"),
            prefix:new HarmonyMethod(typeof(Plugin),nameof(EmergencyCoverStep)));
    }
    private static bool EmergencyCoverStep(BotAction __instance)
    {
        var p=Instance;var b=__instance.Bot;
        if(p==null||!p.states.TryGetValue(b,out var s)||!s.Emergency.Policy.Active)return true;
        try{return p.StepDoorEmergency(b,s,"SeekCoverAction");}
        catch(Exception ex){p.EmergencyError(s,ex);return true;}
    }
    private bool EmergencyPriority(BotComponent b,BotState s)=>config?.Enabled!=true||!FakeScope(b)||b.IsDead
        ||FakeMedicalPriority(b)||b.Decision.CurrentSelfDecision!=ESelfActionType.None||b.Decision.CurrentSquadDecision!=ESquadDecision.None
        ||b.Grenade.GrenadeDangerPoint!=null||b.Decision.CurrentCombatDecision==ECombatDecision.AvoidGrenade
        ||b.BotOwner.WeaponManager?.Grenades.ThrowindNow==true||b.BotOwner.DoorOpener.Interacting||b.DoorOpener.Interacting
        ||b.BotOwner.WeaponManager?.Reload.Reloading==true||EmergencyForeignHands(b,s)
        ||b.Flashed.IsFlashed||!b.Player.MovementContext.IsGrounded||b.Player.IsInPronePose
        ||s.RoomClear.Policy.Trace?.ThrowRequested==true&&s.RoomClear.Policy.Active;
    private static bool EmergencyForeignHands(BotComponent b,BotState s)
    {
        var hands=b.Player.HandsController;var item=hands?.Item;
        if(item is Weapon)return false;
        var f=s.Fake.Policy.Trace;
        if(f?.DrawRequested!=true||f.Medical?.MedicalHandoff==true)return true;
        // Native empty hands during the owned draw/holster callback are not another action.
        return item!=null&&item.Id.ToString()!=(f.Kind=="grenade"?f.Grenade:f.Medical?.Item);
    }

    // Only personal observations are read. No enemy Transform/Position is used to discover an exit.
    private bool EmergencyEvidence(BotComponent b,BotState s,Vector3 door,bool visionOnly,out DoorEmergencyTrace t,out Vector3 known)
    {
        var c=config!.DoorEmergency;t=new();known=default;int count=0;
        foreach(var e in b.EnemyController.KnownEnemies)
        {
            if(++count>32)break;if(e==null||!e.EnemyKnown)continue;
            var seen=e.KnownPlaces.LastSeenPlace;
            if(seen!=null&&DoorEmergencyRules.Visible(e.IsVisible,seen.TimeSincePositionUpdated,Vector3.Distance(b.Position,seen.Position),c))
            {known=seen.Position;t.Target=e.EnemyProfileId;t.Confirmed=true;t.Evidence="personal-vision";t.EvidenceAge=seen.TimeSincePositionUpdated;t.Distance=Vector3.Distance(b.Position,known);return true;}
        }
        if(visionOnly)return false;count=0;
        foreach(var e in b.EnemyController.KnownEnemies)
        {
            if(++count>32)break;if(e==null||!e.EnemyKnown)continue;
            var heard=e.KnownPlaces.LastHeardPlace;if(heard==null||!heard.SoundType.HasValue)continue;
            bool floor=Math.Abs(heard.Position.y-door.y)>2
                ||floorCache.TryGet(b.ProfileId+":"+e.EnemyProfileId,Time.time,Point(b.Transform.EyePosition),Point(heard.Position),out var fg)&&fg.Floors>0;
            double age=Time.time-silentHeardAt(heard);
            if(!DoorEmergencyRules.Heard(heard.SoundType.Value.ToString(),e.Heard,floor,age,Vector3.Distance(door,heard.Position),c))continue;
            known=heard.Position;t.Target=e.EnemyProfileId;t.Evidence="personal-sound:"+heard.SoundType.Value;t.EvidenceAge=age;t.Distance=Vector3.Distance(b.Position,known);return true;
        }
        return false;
    }
    private void RefreshDoorEmergency(BotComponent b,BotState s)
    {
        var r=s.Emergency;if(r.Fault)return;
        try
        {
            if(r.Policy.Active)
            {
                if(!r.Policy.Tick(Time.time,FakeWeaponReady(b),EmergencyPriority(b,s),config!.DoorEmergency))
                {EndDoorEmergency(s,r.Policy.Trace!.Reason);return;}
                var t=r.Policy.Trace!;t.Hands=b.Player.HandsController?.GetType().Name??"none";t.EvidenceAge=Time.time-r.EvidenceAt;
                if(!t.Confirmed&&EmergencyEvidence(b,s,r.Door,true,out var seen,out var point))
                {r.Policy.Confirm(seen.Target,seen.EvidenceAge,seen.Distance);r.Threat=point;r.EvidenceAt=Time.time-(float)seen.EvidenceAge;r.Planned=false;}
                if(r.Requested&&Time.time-(t.ActionObserved?r.LastCallback:r.RequestedAt)>.6f)
                {EndDoorEmergency(s,"emergency-native-callback-stopped");return;}
                RecordDoorEmergency(s);return;
            }
            bool fake=s.Fake.Policy.Active&&s.Fake.Door!=null;
            bool prep=s.RoomClear.Policy.Active&&s.RoomClear.Policy.SafeToSuspend;
            if(!DoorEmergencyRules.CanStart(config?.DoorEmergency.Enabled==true,FakeScope(b),EmergencyPriority(b,s),fake,prep,
                b.BotOwner.WeaponManager?.Grenades.ThrowindNow==true||s.RoomClear.Policy.Active&&s.RoomClear.Policy.Trace?.ThrowRequested==true))return;
            var work=fake?s.Fake.Door:s.RoomClear.Door;if(work==null)return;
            if(!EmergencyEvidence(b,s,VP(work.Geometry.Center),false,out var trace,out var threat))return;
            // Seeing an enemy with a usable weapon needs normal combat, not a retreat episode.
            trace.WeaponReady=FakeWeaponReady(b);if(trace.Confirmed&&trace.WeaponReady)return;
            object identity=fake?(object)s.Fake.Policy.Trace!:s.RoomClear.Policy.Trace!;
            trace.Plan=fake?(s.Fake.Policy.Trace!.Kind=="grenade"?"D05":"D06"):(s.RoomClear.Policy.Trace!.Mode=="search"?"D08":"D07");
            trace.Attempt=trace.Plan+":"+RuntimeHelpers.GetHashCode(identity);trace.Door=work.Geometry.Id;
            trace.Phase=fake?s.Fake.Policy.Trace!.State:s.RoomClear.Policy.Trace!.State;
            trace.Hands=b.Player.HandsController?.GetType().Name??"none";
            if(!r.Policy.Begin(trace,Time.time,config!.DoorEmergency))return;
            r.Owner=b;r.Door=VP(work.Geometry.Center);r.Threat=threat;r.EvidenceAt=Time.time-(float)trace.EvidenceAge;
            r.NextCheck=0;r.Planned=r.Requested=false;r.LastCallback=r.RequestedAt=Time.time;
            // Capture detached evidence first. D05/D06 retain ownership of their real hands recovery.
            AbandonDoorResume(s,"door-emergency-cancel-plan");Release(s,"door-emergency-begin");
            RecordDoorEmergency(s,true);
        }
        catch(Exception ex){EmergencyError(s,ex);}
    }
    private bool RequestDoorEmergency(BotComponent b,BotState s,ref ECombatDecision decision,ref bool result)
    {
        var r=s.Emergency;if(!r.Policy.Active)return false;
        if(EmergencyPriority(b,s)){EndDoorEmergency(s,"emergency-native-priority");return false;}
        if(!r.Planned)
        {
            if(!doorBudget.TryBegin(Time.frameCount))return false;
            doorClock.Restart();try{PlanDoorEmergency(b,s);}finally{doorClock.Stop();doorBudget.Charge(doorClock.Elapsed.TotalMilliseconds);}
            if(!r.Planned||!r.Policy.Active)return false;
        }
        var t=r.Policy.Trace!;
        if(!t.Confirmed){RecordDoorEmergency(s);return false;} // sound cancels the attempt; SAIN remains in control.
        string action=r.Route!=null?"FallBack":"Hold";
        if(s.Feature!="D13"||s.Decision!=action)
        {
            s.Feature="D13";s.Decision=action;s.Mixing=null;
            s.Candidates=new(){new(){Decision=action,Eligible=true,Reason=t.Reason}};
            s.OpponentAtSelection=null;s.OpportunityAtSelection=null;s.LoadoutAtSelection=null;s.ProgressAtSelection=null;
            s.FearAtSelection=s.Fear.Current;Emit(s,"selected",t.Reason);
        }
        if(!r.Requested){r.RequestedAt=Time.time;r.Requested=true;}
        s.Expected=action=="FallBack"?ECombatDecision.Retreat:ECombatDecision.Freeze;
        s.RequestedFrame=Time.frameCount;decision=s.Expected;result=true;RecordDoorEmergency(s);return true;
    }
    private void DoorEmergencyAccepted(BotState s,bool applied)
    {
        if(!s.Emergency.Policy.Active||s.Feature!="D13")return;
        s.Emergency.Policy.Accepted(applied);
        if(!applied)EndDoorEmergency(s,"emergency-native-decision-refused");else RecordDoorEmergency(s);
    }
    private static int EmergencyProtection(Vector3 pos,Vector3 known)
    {
        int n=0;var eye=known+Vector3.up*1.4f;
        foreach(float h in new[]{.6f,1.1f,1.6f})
            if(Physics.Linecast(pos+Vector3.up*h,eye,LayersMaskController.HighPolyWithTerrainMask,QueryTriggerInteraction.Ignore))n++;
        return n;
    }
    private bool EmergencySpace(BotComponent b,Vector3 point)
    {
        if(!supportBoard.Available(DP(point),config!.DoorSupport.Spacing,b.ProfileId))return false;
        try
        {
            int hits=Physics.OverlapCapsuleNonAlloc(point+Vector3.up*.4f,point+Vector3.up*1.45f,.25f,doorOccupants,~0,QueryTriggerInteraction.Ignore);
            if(hits>=doorOccupants.Length)return false;
            for(int i=0;i<hits;i++)
            {var col=doorOccupants[i];if(col==null||ReferenceEquals(col.GetComponentInParent<Player>(),b.Player))continue;return false;}
            return true;
        }
        finally{Array.Clear(doorOccupants,0,doorOccupants.Length);}
    }
    private string EmergencyRoute(BotComponent b,DoorEmergencyRuntime r,Vector3 destination,out NavMeshPath? path)
    {
        path=new();var origin=b.Position;Vector3[] corners;
        if(r.Path!=null)
        {
            // Revalidate the path actually being executed, not a newly calculated alternative.
            if(r.Path.PathStatus!=NavMeshPathStatus.PathComplete||r.Path.PathPoints.Count>8||r.Path.CurrentIndex<0||r.Path.CurrentIndex>=r.Path.PathPoints.Count)return "native-route-invalid";
            var remaining=new List<Vector3>{origin};
            for(int i=r.Path.CurrentIndex;i<r.Path.PathPoints.Count;i++)remaining.Add(r.Path.PathPoints[i]);
            corners=remaining.ToArray();path=r.Route;
        }
        else
        {
            if(!NavMesh.CalculatePath(origin,destination,NavMesh.AllAreas,path)||path.status!=NavMeshPathStatus.PathComplete)return "incomplete-route";
            corners=path.corners;
        }
        if(corners.Length<2||corners.Length>8||Vector3.Distance(corners[0],origin)>.5||Vector3.Distance(corners[corners.Length-1],destination)>.5)return "route-endpoint-mismatch";
        var direction=(r.Threat-r.Origin).normalized;float length=0,height=0,advance=0;
        for(int i=1;i<corners.Length;i++)
        {
            length+=Vector3.Distance(corners[i-1],corners[i]);height=Math.Max(height,Math.Abs(corners[i].y-r.Origin.y));advance=Math.Max(advance,Vector3.Dot(corners[i]-r.Origin,direction));
            if(NavMesh.Raycast(corners[i-1],corners[i],out _,NavMesh.AllAreas))return "route-navmesh-obstacle";
            if(Physics.Linecast(corners[i-1]+Vector3.up*.8f,corners[i]+Vector3.up*.8f,LayersMaskController.HighPolyWithTerrainMask,QueryTriggerInteraction.Ignore))return "route-obstacle";
            if(!EmergencySpace(b,corners[i])||!EmergencySpace(b,(corners[i-1]+corners[i])*.5f))return "route-clearance-or-ally";
        }
        bool exposed=false;int count=0,threats=0;
        foreach(var e in b.EnemyController.KnownEnemies)
        {
            if(++count>16)return "known-contact-validation-budget";if(e==null||!e.EnemyKnown||e.EnemyProfileId==r.Policy.Trace!.Target)continue;
            var place=e.KnownPlaces.LastKnownPlace;if(place==null||place.TimeSincePositionUpdated>3)continue;
            if(++threats>4)return "known-threat-validation-budget";
            int baseline=EmergencyProtection(origin,place.Position);
            for(int i=1;i<corners.Length&&!exposed;i++)exposed=EmergencyProtection(corners[i],place.Position)<baseline
                ||EmergencyProtection((corners[i-1]+corners[i])*.5f,place.Position)<baseline;
            if(exposed)break;
        }
        string why=DoorEmergencyRules.Route(true,EmergencySpace(b,destination),true,r.InitialProtection,EmergencyProtection(destination,r.Threat),length,height,advance,exposed,config!.DoorEmergency);
        if(why.Length==0)r.Policy.Trace!.PathLength=length;
        return why;
    }
    private void PlanDoorEmergency(BotComponent b,BotState s)
    {
        var r=s.Emergency;var t=r.Policy.Trace!;
        if(!t.Confirmed){r.Planned=true;t.Choice="recover-only";t.Alternatives=new[]{"Retreat: refused, sound does not confirm an exit","Recover the weapon and let SAIN handle the contact"};return;}
        var alternatives=r.Alternatives;
        if(r.Candidate==0)
        {
            r.Origin=b.Position;r.InitialProtection=EmergencyProtection(b.Position,r.Threat);t.SafePosition=r.InitialProtection>=2;t.Choice="stay";
            alternatives.Clear();alternatives.Add(t.SafePosition?"Stay: current position is protected":"Stay: no verified better destination yet");
            if(t.SafePosition){r.Planned=true;t.Reason="emergency-keep-current-protection";t.Alternatives=alternatives.ToArray();r.NextCheck=Time.time+.25f;return;}
        }
        if(Vector3.Distance(b.Position,r.Origin)>.5f){EndDoorEmergency(s,"emergency-origin-moved-during-validation");return;}
        var away=b.Position-r.Threat;away.y=0;away.Normalize();var side=new Vector3(away.z,0,-away.x);
        var offsets=new[]{away*2,away*3,away+side*2,away-side*2};
        // One local candidate per shared budget unit; four alternatives maximum for the episode.
        int i=r.Candidate++;
        if(!NavMesh.SamplePosition(r.Origin+offsets[i],out var hit,.6f,NavMesh.AllAreas))alternatives.Add("Local cover "+(i+1)+": no navmesh");
        else
        {
            string why=EmergencyRoute(b,r,hit.position,out var path);alternatives.Add("Local cover "+(i+1)+": "+(why.Length==0?"verified":why));
            if(why.Length==0)
            {
                r.Route=path;r.Destination=hit.position;t.Choice="retreat";t.SafePosition=true;t.X=hit.position.x;t.Y=hit.position.y;t.Z=hit.position.z;
                t.Reason="emergency-short-protected-retreat";
            }
        }
        r.Planned=r.Route!=null||r.Candidate>=offsets.Length;
        if(r.Route==null)t.Reason=r.Planned?"emergency-no-safe-retreat-stay":"emergency-validating-local-protection";
        t.Alternatives=alternatives.ToArray();r.NextCheck=Time.time+.25f;RecordDoorEmergency(s,true);
    }
    private bool StepDoorEmergency(BotComponent b,BotState s,string action)
    {
        var r=s.Emergency;RefreshDoorEmergency(b,s);if(!r.Policy.Active)return true;
        var t=r.Policy.Trace!;
        if(!r.Requested||!t.NativeAccepted)return true;
        string expected=r.Route==null?"FreezeAction":"SeekCoverAction";
        if(action!=expected||b.CurrentAction?.GetType().Name!=expected||!b.SAINLayersActive||s.Feature!="D13"
            ||b.Decision.CurrentCombatDecision!=s.Expected||s.ActiveLabel!="COMET:"+s.Decision)
        {EndDoorEmergency(s,"emergency-action-ownership-lost");return true;}
        r.LastCallback=Time.time;
        if(r.Route!=null)
        {
            bool complete=r.Path?.Status==EBotMoveStatus.Complete,close=Vector3.Distance(b.Position,r.Destination)<=.6f;
            if(r.Path!=null&&!ReferenceEquals(r.Path,b.Mover.ActivePath)&&!(complete&&close))
            {EndDoorEmergency(s,"emergency-path-ownership-lost");return true;}
            if(!complete&&(r.Path==null||Time.time>=r.NextCheck))
            {
                if(!doorBudget.TryBegin(Time.frameCount))
                {if(Time.time-r.NextCheck>.5)EndDoorEmergency(s,"emergency-validation-budget-expired");return false;}
                doorClock.Restart();string why;
                try{why=EmergencyRoute(b,r,r.Destination,out var refreshed);if(r.Path==null&&why.Length==0)r.Route=refreshed;}finally{doorClock.Stop();doorBudget.Charge(doorClock.Elapsed.TotalMilliseconds);}
                if(why.Length>0&&!(close&&why=="no-better-protection")){EndDoorEmergency(s,"emergency-route-changed:"+why);return true;}
                r.NextCheck=Time.time+.25f;
            }
            if(r.Path==null)
            {
                r.Start=b.Position;r.StartDistance=Vector3.Distance(b.Position,r.Destination);
                t.MoveAccepted=b.Mover.WalkToPointByWay(r.Route,true,.4f,false);r.Path=b.Mover.ActivePath;
                if(!t.MoveAccepted||r.Path==null){EndDoorEmergency(s,"emergency-native-move-refused");return true;}
            }
            r.Policy.Observe(Vector3.Distance(b.Position,r.Start),r.StartDistance-Vector3.Distance(b.Position,r.Destination),complete,close);
        }
        else
        {
            if(t.SafePosition&&Time.time>=r.NextCheck)
            {
                if(!doorBudget.TryBegin(Time.frameCount)){if(Time.time-r.NextCheck>.5)EndDoorEmergency(s,"emergency-validation-budget-expired");return false;}
                doorClock.Restart();int protection;
                try{protection=EmergencyProtection(b.Position,r.Threat);}finally{doorClock.Stop();doorBudget.Charge(doorClock.Elapsed.TotalMilliseconds);}
                r.NextCheck=Time.time+.25f;
                if(protection<2){EndDoorEmergency(s,"emergency-current-protection-lost");return true;}
            }
            r.Policy.Observe(0,0,false,false);
        }
        RecordDoorEmergency(s);return false; // Native steering/shooting ticks are untouched.
    }
    private void EndDoorEmergency(BotState s,string why)
    {
        var r=s.Emergency;if(r.Policy.Trace==null)return;
        bool changed=r.Policy.Active||r.Owner!=null;r.Policy.End(why);
        try{if(r.Owner!=null&&!r.Owner.IsDead&&r.Path!=null&&ReferenceEquals(r.Path,r.Owner.Mover.ActivePath))r.Owner.Mover.Stop();}
        finally
        {
            r.Drop();if(changed)RecordDoorEmergency(s,true);
            if(s.Feature=="D13"&&s.Decision.Length>0){Emit(s,"released",why);s.Decision="";s.ActiveLabel="";s.RequestedFrame=-1;Api.Decisions.Set(s.ProfileId,"");}
        }
    }
    private void ReleaseDoorEmergency(BotState s,string why)
    {
        if(!s.Emergency.Policy.Active||why=="door-emergency-begin")return;
        // While only recovering from sound, the normal native handoff is expected.
        if(s.Emergency.Requested&&s.Feature=="D13"||why.Contains("ghost")||why.Contains("inactive")||why.Contains("authority")||why.Contains("ended")||why.Contains("error")||why.Contains("out-of-scope"))EndDoorEmergency(s,why);
    }
    private void EmergencyError(BotState s,Exception ex)
    {try{EndDoorEmergency(s,"emergency-adapter-error");}finally{s.Emergency.Drop();s.Emergency.Fault=true;}Logger.LogWarning("COMET D13 yielded: "+ex.Message);}
    private void RecordDoorEmergency(BotState s,bool force=false)
    {
        var r=s.Emergency;var t=r.Policy.Trace;if(t==null)return;
        string key=t.State+":"+t.Reason+":"+t.Choice+":"+t.NativeAccepted+":"+t.ActionObserved+":"+t.MovementObserved+":"+t.Arrived;
        if(!force&&key==r.LogKey&&(!r.Policy.Active||Time.time-r.LogAt<.5))return;
        r.LogKey=key;r.LogAt=Time.time;
        var json=JsonConvert.SerializeObject(new{schema=1,session,sequence=++sequence,feature="D13",stage="evaluated",decision="DoorEmergency",reason=t.Reason,
            realtime=Time.realtimeSinceStartup,utc=DateTime.UtcNow.ToString("O"),configRevision=config?.Revision??0,profileId=s.ProfileId,name=s.Name,botInstance=s.Id,
            doorEmergency=t,doorEmergencyConfig=config?.DoorEmergency},Diagnostics.Json);
        diagnostics?.Write(json);Api.Decisions.Publish(json,ex=>{if(!subscriberWarned){subscriberWarned=true;Logger.LogWarning(ex.Message);}});
    }
}
