using System;
using System.Collections.Generic;
using System.Linq;
using Comet.Core;
using EFT;
using EFT.Interactive;
using SAIN.Components;
using SAIN.SAINComponent.Classes.Mover;
using SAIN.Preset.Shared.Enums;
using UnityEngine;
using UnityEngine.AI;
namespace Comet.Client;

internal sealed class EntranceRuntime
{
    internal readonly SecondaryEntrance Policy=new();
    internal readonly List<DoorWork> Doors=new();
    internal readonly List<Vector3> Radial=new();
    internal int Index,Stage;
    internal DoorWork? Chosen;
    internal bool Virtual,Entering,Preparing,Opening;
    internal Vector3 Primary,Center,Normal,Stack;
    internal string PrimaryId="";
    internal DoorGeometry? PrimaryGeometry;
    internal float OpenAt,Started;
    internal NavMeshPath? PrimaryPath;
    internal DoorReservationLease? Lease;
    internal double NextRenew;
    internal void Drop(){Doors.Clear();Radial.Clear();Chosen=null;PrimaryGeometry=null;PrimaryPath=null;Lease=null;Entering=Preparing=Opening=Virtual=false;Stage=Index=0;}
}
public sealed partial class Plugin
{
    private static bool EntranceOwnOpening(BotComponent b,BotState s)=>s.Crossfire.Policy.Active&&s.Crossfire.Entrance.Opening
        &&s.Crossfire.Entrance.Chosen?.Data.Door!=null&&Time.time-s.Crossfire.Entrance.OpenAt<8;
    private bool BeginEntrance(BotComponent b,BotState s)
    {
        var r=s.Crossfire;var a=r.Entrance;var c=config!.SecondaryEntrance;
        a.Drop();a.Policy.ClearCurrent();r.Policy.Trace!.Entrance=null;
        if(!r.Indoor||!a.Policy.CanTry(r.Contact,Time.time,r.ContactAt,c)||!a.Policy.Begin(r.Contact,Time.time,r.ContactAt,jumpRandom.NextDouble(),c))return false;
        r.Policy.Trace.Entrance=a.Policy.Trace;a.Started=Time.time;
        // Local door links only. Ordinary open passages are also checked from bounded NavMesh paths below.
        CollectEntranceDoors(b,a,r.Known);
        if(b.Squad.Members.TryGetValue(r.Support,out var mate))CollectEntranceDoors(mate,a,r.Known);
        for(int ring=0;ring<3;ring++)for(int i=0;i<8;i++)
        {float angle=i*Mathf.PI/4;float radius=5+ring*3;a.Radial.Add(r.Known+new Vector3(Mathf.Cos(angle),0,Mathf.Sin(angle))*radius);}
        return true;
    }
    private static void CollectEntranceDoors(BotComponent b,EntranceRuntime a,Vector3 known)
    {
        var links=b.BotOwner.VoxelesPersonalData.CurVoxel?.DoorLinks;if(links==null)return;
        int count=0;foreach(var link in links)
        {
            if(++count>48||a.Doors.Count>=24)break;if(link?.Door==null)continue;
            var data=new DoorDataStruct(link);if(!TryDoorGeometry(data,out var g)||Vector3.Distance(VP(g.Center),known)>12||a.Doors.Any(x=>x.Geometry.Id==g.Id))continue;
            a.Doors.Add(new(){Data=data,Geometry=g});
        }
    }
    private static bool EntrancePath(NavMeshPath p,Vector3 from,Vector3 to,float limit)
    {
        var cs=p.corners;return p.status==NavMeshPathStatus.PathComplete&&cs.Length>=2&&cs.Length<=24
            &&Vector3.Distance(cs[0],from)<1&&Vector3.Distance(cs[cs.Length-1],to)<1&&Length(cs)<=limit;
    }
    private static bool CrossesEntrance(Vector3[] path,DoorGeometry g)
    {
        var center=VP(g.Center);var normal=VP(g.Normal);var axis=VP(g.Axis);
        for(int i=1;i<path.Length;i++)
        {
            float x=Vector3.Dot(path[i-1]-center,normal),y=Vector3.Dot(path[i]-center,normal);
            if(x*y>=0||Math.Abs(x-y)<.01)continue;
            var hit=path[i-1]+(path[i]-path[i-1])*(x/(x-y));
            if(Math.Abs(hit.y-center.y)<.8&&Math.Abs(Vector3.Dot(hit-center,axis))<g.HalfWidth+.2)return true;
        }
        return false;
    }
    // A bend alone is not an entrance: require a narrow physical throat with two static sides,
    // a traversable standing lane and the same floor. Unknown/ambiguous geometry falls back to S01.
    private bool EntranceThroat(BotComponent b,Vector3 from,Vector3 toward,out DoorGeometry g)
    {
        g=new();var dir=toward-from;dir.y=0;if(dir.magnitude<1)return false;dir=dir.normalized;
        var side=new Vector3(-dir.z,0,dir.x);var eye=from+Vector3.up*1.2f;
        if(!Physics.Raycast(eye,side,out var left,1.8f,LayersMaskController.HighPolyWithTerrainMask,QueryTriggerInteraction.Ignore)
            ||!Physics.Raycast(eye,side*-1,out var right,1.8f,LayersMaskController.HighPolyWithTerrainMask,QueryTriggerInteraction.Ignore))return false;
        if(left.collider==null||right.collider==null||left.collider.attachedRigidbody!=null||right.collider.attachedRigidbody!=null
            ||left.collider.GetComponentInParent<Door>()!=null||right.collider.GetComponentInParent<Door>()!=null)return false;
        float width=left.distance+right.distance;if(width<.8||width>3.4)return false;
        var center=from+side*((left.distance-right.distance)*.5f);
        if(RunVolume(b,center-dir*.8f,center+dir*.8f).Length>0)return false;
        var path=new NavMeshPath();if(!NavMesh.CalculatePath(center-dir*.8f,center+dir*.8f,NavMesh.AllAreas,path)
            ||!EntrancePath(path,center-dir*.8f,center+dir*.8f,2.2f))return false;
        g=new(){Id=$"passage:{Math.Round(center.x)}:{Math.Round(center.y)}:{Math.Round(center.z)}",State="Open",Center=DP(center),Axis=DP(side),Normal=DP(dir),HalfWidth=width*.5};return true;
    }
    private bool PathThroat(BotComponent b,NavMeshPath path,out DoorGeometry g)
    {
        var cs=path.corners;g=new();if(cs.Length<3)return false;
        // A bounded probe just before the last bend. It must pass real geometry checks.
        var corner=cs[cs.Length-2];var previous=cs[cs.Length-3];var dir=(corner-previous).normalized;
        return EntranceThroat(b,corner-dir*.5f,cs[cs.Length-1],out g);
    }
    private void FallbackEntrance(BotComponent b,BotState s,string why)
    {
        var r=s.Crossfire;var a=r.Entrance;a.Policy.Trace!.EntryReason=why;a.Policy.End(Time.time,r.ContactAt);RecordCrossfire(s,true);
        a.Drop();r.Policy.Trace!.Entrance=null;r.Policy.Trace.Candidates=0;r.Route=null;r.Candidate=r.Sample=0;r.Samples.Clear();
        if(!r.Rejects.Contains(why)&&r.Rejects.Count<8)r.Rejects.Add(why);r.Policy.Trace.Alternatives=r.Rejects.ToArray();BuildCrossfireCandidates(b,r);
    }
    private void ProbeEntrance(BotComponent b,BotState s)
    {
        var r=s.Crossfire;var a=r.Entrance;var c=config!.SecondaryEntrance;
        if(a.Stage==0)
        {
            var path=new NavMeshPath();if(!NavMesh.CalculatePath(r.Reference,r.Known,NavMesh.AllAreas,path)||!EntrancePath(path,r.Reference,r.Known,80))
            {FallbackEntrance(b,s,"no complete partner route");return;}
            a.PrimaryPath=path;DoorGeometry? primary=null;
            foreach(var w in a.Doors)if(Vector3.Distance(VP(w.Geometry.Center),r.Known)<=12&&CrossesEntrance(path.corners,w.Geometry)
                &&(primary==null||Vector3.Distance(VP(w.Geometry.Center),r.Known)<Vector3.Distance(VP(primary.Center),r.Known)))primary=w.Geometry;
            if(primary==null&&PathThroat(b,path,out var inferred))primary=inferred;
            if(primary==null){FallbackEntrance(b,s,"primary access cannot be verified");return;}
            a.PrimaryGeometry=primary;a.Primary=VP(primary.Center);a.PrimaryId=primary.Id;a.Policy.Trace!.Primary=primary.Id;a.Stage=1;return;
        }
        if(r.Route!=null)
        {
            if(r.Sample<r.Samples.Count)
            {
                var previous=r.Sample==0?r.Origin:r.Samples[r.Sample-1];var point=r.Samples[r.Sample++];string why=EntranceApproachSafety(b,s,point);
                if(why.Length==0)why=RunVolume(b,previous,point);
                if(why.Length>0){RejectCrossfire(r,why);a.Chosen=null;}return;
            }
            string occupied=CrossfireSpace(b,s,r.Goal,true);
            if(occupied.Length>0||!ReserveEntrance(b,s)){RejectCrossfire(r,occupied.Length>0?occupied:"passage already reserved");a.Chosen=null;return;}
            if(!crossfireClaims.Reserve(s.Id,r.Goal.x,r.Goal.y,r.Goal.z,config.Crossfire.Spacing,Time.time,r.Group))
            {ReleaseEntrance(s);RejectCrossfire(r,"watch position already reserved");a.Chosen=null;return;}
            r.Policy.Ready(r.Goal.x,r.Goal.y,r.Goal.z,r.Angle,r.Length);r.Policy.Trace!.Reason="secondary-entrance-verified";
            a.Policy.Trace!.State="moving";return;
        }
        DoorWork? chosen=null;bool virtualPassage=false;r.Policy.Trace!.Candidates=Math.Min(48,a.Index+1);
        if(a.Index<a.Doors.Count)chosen=a.Doors[a.Index++];
        else
        {
            int radial=a.Index++-a.Doors.Count;
            if(radial>=a.Radial.Count){FallbackEntrance(b,s,"no distinct safe secondary access");return;}
            if(!NavMesh.SamplePosition(a.Radial[radial],out var nav,.75f,NavMesh.AllAreas))return;
            var path=new NavMeshPath();if(!NavMesh.CalculatePath(nav.position,r.Known,NavMesh.AllAreas,path)||!EntrancePath(path,nav.position,r.Known,15)
                ||!PathThroat(b,path,out var opening))return;
            chosen=new(){Geometry=opening};virtualPassage=true;
        }
        var g=chosen.Geometry;var center=VP(g.Center);
        if(g.Id==a.PrimaryId||Vector3.Distance(center,a.Primary)<3||CrossesEntrance(a.PrimaryPath!.corners,g))
        {RejectCrossfire(r,"access duplicates the supporting teammate's route");return;}
        if(Vector3.Distance(center,r.Known)>12||Math.Abs(center.y-r.Known.y)>.8)
        {RejectCrossfire(r,"access outside remembered area or on another floor");return;}
        if(g.State is not ("Open" or "Shut")){RejectCrossfire(r,"access locked or unavailable");return;}
        var normal=VP(g.Normal);if(Vector3.Dot(r.Known-center,normal)<0)normal*= -1;
        if(Vector3.Dot(r.Known-center,normal)<1.5)return;
        // Confirm both distinct routes really lead into the remembered area.
        var inside=center+normal*.8f;var toTarget=new NavMeshPath();
        if(!NavMesh.CalculatePath(inside,r.Known,NavMesh.AllAreas,toTarget)||!EntrancePath(toTarget,inside,r.Known,15)
            ||CrossesEntrance(toTarget.corners,g)||CrossesEntrance(toTarget.corners,a.PrimaryGeometry!)||CrossfireAngle(a.Primary,center,r.Known)<20)return;
        // Try the requested distance, then two bounded closer positions when space is tight.
        NavMeshHit hit=default;bool stackFound=false;
        for(int step=0;step<3;step++)
        {
            float distance=(float)(step==0?c.StandOff:step==1?Math.Max(1,c.StandOff*.75):1);
            var raw=center-normal*distance;
            if(NavMesh.SamplePosition(raw,out hit,.5f,NavMesh.AllAreas)&&Math.Abs(hit.position.y-center.y)<=.65
                &&Vector3.Dot(hit.position-center,normal)<=-1&&EntranceApproachSafety(b,s,hit.position).Length==0)
            {stackFound=true;break;}
        }
        if(!stackFound)return;
        a.Chosen=chosen;a.Virtual=virtualPassage;a.Center=center;a.Normal=normal;a.Stack=hit.position;r.Goal=hit.position;
        string deny=CrossfireSpace(b,s,r.Goal,true);if(deny.Length==0)deny=EntranceApproachSafety(b,s,r.Goal);
        if(deny.Length>0){RejectCrossfire(r,deny);a.Chosen=null;return;}
        if(!b.Mover.CanGoToPoint(r.Goal,out var route,true,.2f)||!EntrancePath(route,b.Position,r.Goal,(float)c.MaxPath)||CrossesEntrance(route.corners,g)||CrossesEntrance(route.corners,a.PrimaryGeometry!))
        {RejectCrossfire(r,"approach crosses target access or lacks complete route");a.Chosen=null;return;}
        r.Route=route;r.Length=(float)Length(route.corners);r.Angle=CrossfireAngle(a.Primary,center,r.Known);r.Samples.Clear();r.Sample=0;
        var cs=route.corners;for(int i=1;i<cs.Length;i++){int count=Math.Max(1,(int)Math.Ceiling(Vector3.Distance(cs[i-1],cs[i])));for(int j=1;j<=count;j++)r.Samples.Add(cs[i-1]+(cs[i]-cs[i-1])*(j/(float)count));}
        var t=a.Policy.Trace!;t.Passage=g.Id;t.PassageX=center.x;t.PassageY=center.y;t.PassageZ=center.z;
    }
    private string EntranceApproachSafety(BotComponent b,BotState s,Vector3 point)
    {
        var r=s.Crossfire;string why=CrossfireSpace(b,s,point,false);if(why.Length==0)why=CrossfireThreatSafety(b,r,point);
        if(why.Length==0&&CrossfireSight(point,r.Known))why="approach exposes the remembered enemy position";
        if(why.Length==0)why=RunVolume(b,point,point+Vector3.up*.01f);return why;
    }
    private bool ReserveEntrance(BotComponent b,BotState s)
    {
        var a=s.Crossfire.Entrance;var c=config!;if(!c.DoorReservation.Enabled)return true;
        return doorReservations.Acquire(ReservationSquad(b,s),a.Chosen!.Geometry.Id,s.ProfileId,s.Id,"secondary-entrance",
            Time.time,Time.time+c.Crossfire.MoveTimeout*2+c.SecondaryEntrance.WatchMax+c.SecondaryEntrance.Extension+8,c.DoorReservation,out a.Lease,out _);
    }
    private void ReleaseEntrance(BotState s)
    {var a=s.Crossfire.Entrance;if(a.Lease!=null)doorReservations.Release(a.Lease);a.Lease=null;}
    private string EntranceSupportStatus(BotComponent b,BotState s)
    {
        var r=s.Crossfire;
        if(!b.Squad.Members.TryGetValue(r.Support,out var mate)||mate==null||!CrossfireScope(mate))return "unavailable";
        if(mate.GoalEnemy?.EnemyProfileId!=r.Contact||!mate.GoalEnemy.EnemyKnown||mate.GoalEnemy.TimeSinceLastKnownUpdated>config!.SecondaryEntrance.ContactAge)return "contact lost";
        if(Vector3.Distance(mate.Position,r.Reference)>4||Vector3.Distance(mate.Position,b.Position)>config.Crossfire.SupportDistance)return "left primary access";
        if(mate.BotOwner.Medecine.Using||mate.Decision.CurrentSelfDecision!=ESelfActionType.None)return "healing or occupied";
        if(mate.BotOwner.WeaponManager.Reload.Reloading)return "reloading";
        if(mate.Decision.CurrentCombatDecision.ToString().Contains("Retreat")||mate.Decision.CurrentCombatDecision.ToString().Contains("RunAway")
            ||mate.Decision.CurrentSquadDecision!=ESquadDecision.None)return "withdrawing or squad priority";
        if(mate.BotOwner.Memory.IsUnderFire||mate.Suppression.IsSuppressed||mate.Flashed.IsFlashed||mate.Grenade.GrenadeDangerPoint!=null)return "under pressure";
        if(mate.Memory.Health.HealthStatus is ETagStatus.BadlyInjured or ETagStatus.Dying)return "injured";
        if(!WeaponHandsReady(mate,mate.Player.HandsController as Player.FirearmController)||!mate.BotOwner.WeaponManager.HaveBullets)return "weapon unavailable";
        var primary=r.Entrance.Doors.Find(x=>x.Geometry.Id==r.Entrance.PrimaryId);
        if(primary!=null&&primary.Data.Door.DoorState!=EDoorState.Open)return "primary access blocked";
        if(mate.GoalEnemy.IsVisible)return "ready";
        if(mate.GoalEnemy.KnownPlaces.LastKnownPosition is not Vector3 known||Vector3.Distance(known,r.Known)>2)return "contact changed";
        // A covering partner must actually face the same approach, not merely retain an enemy ID.
        if(Vector3.Angle(mate.Player.LookDirection,r.Entrance.Primary-mate.Position)>35)return "not covering primary access";
        return "ready";
    }
    private static double EntranceAmmo(BotComponent b)
    {var m=b.BotOwner.WeaponManager?.CurrentWeapon?.GetCurrentMagazine();return m==null||m.MaxCount<=0?0:m.Count*100d/m.MaxCount;}
    private bool ValidateEntranceActive(BotComponent b,BotState s,bool callback)
    {
        var r=s.Crossfire;var a=r.Entrance;var t=a.Policy.Trace!;var c=config!.SecondaryEntrance;
        var ids=new List<string>(b.Squad.Members.Keys);ids.Sort(StringComparer.Ordinal);
        if(string.Join("|",ids)!=r.Group){EndCrossfire(b,s,"secondary-entrance-squad-changed");return false;}
        if(!c.Enabled||Time.time-r.ContactAt>c.ContactAge){EndCrossfire(b,s,"secondary-entrance-disabled-or-stale");return false;}
        if(r.Policy.Trace!.State=="planning")return true;
        if(a.Lease!=null)
        {
            if(!doorReservations.Owns(a.Lease,ReservationSquad(b,s),s.Id,Time.time))
            {EndCrossfire(b,s,"secondary-entrance-reservation-lost");return false;}
            if(callback&&Time.time>=a.NextRenew)
            {if(!doorReservations.Renew(a.Lease,ReservationSquad(b,s),s.Id,t.State,Time.time,true,config.DoorReservation)){EndCrossfire(b,s,"secondary-entrance-renewal-failed");return false;}a.NextRenew=Time.time+config.DoorReservation.RenewInterval;}
        }
        if(a.Chosen==null){EndCrossfire(b,s,"secondary-entrance-missing-passage");return false;}
        if(!a.Virtual&&(!TryDoorGeometry(a.Chosen.Data,out var g)||g.State is not ("Open" or "Shut")
            ||Vector3.Distance(VP(g.Center),a.Center)>.1))
        {EndCrossfire(b,s,"secondary-entrance-door-unavailable");return false;}
        t.SupportStatus=EntranceSupportStatus(b,s);t.Ammo=EntranceAmmo(b);
        if(a.Entering||a.Preparing||a.Opening)
        {
            if(t.SupportStatus!="ready"||EntranceAmmo(b)<c.MinimumAmmo){EndCrossfire(b,s,"secondary-entrance-entry-support-or-ammo-lost");return false;}
        }
        return true;
    }
    private void ExecuteEntrance(BotComponent b,BotState s)
    {
        var r=s.Crossfire;var a=r.Entrance;var t=a.Policy.Trace!;var c=config!.SecondaryEntrance;
        if(!ValidateEntranceActive(b,s,true))return;
        if(a.Opening)
        {
            if(Time.time-a.OpenAt>=8){EndCrossfire(b,s,"secondary-entrance-opening-timeout");return;}
            if(a.Chosen!.Data.Door.DoorState!=EDoorState.Open||JumpInteractionState(b)||b.BotOwner.DoorOpener.Interacting)return;
            a.Opening=false;t.OpenObserved=true;a.Preparing=true;
        }
        if(a.Entering)
        {
            if(Vector3.Dot(b.Position-a.Center,a.Normal)>.25)t.Crossed=true;
            if(Vector3.Distance(b.Position,r.Goal)<=.8)
            {EndCrossfire(b,s,t.Crossed?"secondary-entrance-crossed-and-reached":"secondary-entrance-arrival-without-crossing");return;}
        }
        if(r.Policy.Trace!.State=="holding"&&!a.Entering&&!a.Preparing)
        {
            a.Policy.Arrived(Time.time);t.State="watching";
            string choice=a.Policy.Decide(Time.time,Time.time-r.ContactAt<=c.ContactAge,t.SupportStatus,EntranceAmmo(b),b.Info.Personality.ToString(),
                t.Rolled?t.EntryRoll/100:Time.time>=a.Policy.WatchAt+t.WatchDuration?jumpRandom.NextDouble():0,c);
            if(choice=="end"){EndCrossfire(b,s,"secondary-entrance-"+t.EntryReason.Replace(' ','-'));return;}
            if(choice!="ready")return;a.Preparing=true;t.State="preparing-entry";
        }
        if(!a.Preparing||!doorBudget.TryBegin(Time.frameCount))return;
        doorClock.Restart();try
        {
            if(!a.Virtual&&a.Chosen!.Data.Door.DoorState==EDoorState.Shut)
            {
                if(!RequestJumpDoorOpen(b,new DoorJumpRuntime{Door=a.Chosen})){EndCrossfire(b,s,"secondary-entrance-native-open-refused");return;}
                a.Opening=true;a.Preparing=false;a.OpenAt=Time.time;t.OpenRequested=true;t.State="opening";return;
            }
            t.OpenObserved=true;var raw=a.Center+a.Normal*2.5f;
            if(!NavMesh.SamplePosition(raw,out var nav,.5f,NavMesh.AllAreas)||Math.Abs(nav.position.y-a.Center.y)>.65
                ||!b.Mover.CanGoToPoint(nav.position,out var route,true,.2f)||!EntrancePath(route,b.Position,nav.position,8)
                ||!CrossesEntrance(route.corners,a.Chosen!.Geometry))
            {EndCrossfire(b,s,"secondary-entrance-no-verified-crossing-route");return;}
            var cs=route.corners;
            for(int i=1;i<cs.Length;i++)
            {
                if(RunVolume(b,cs[i-1],cs[i]).Length>0){EndCrossfire(b,s,"secondary-entrance-crossing-blocked");return;}
                int samples=Math.Max(1,(int)Math.Ceiling(Vector3.Distance(cs[i-1],cs[i])));
                for(int j=1;j<=samples;j++)
                {var point=cs[i-1]+(cs[i]-cs[i-1])*(j/(float)samples);if(CrossfireSpace(b,s,point,false).Length>0||CrossfireThreatSafety(b,r,point).Length>0){EndCrossfire(b,s,"secondary-entrance-crossing-unsafe");return;}}
            }
            if(b.Mover.ActivePath!=null||QueuedCombatMovement(b)){EndCrossfire(b,s,"secondary-entrance-foreign-route");return;}
            if(!crossfireClaims.Free(s.Id,nav.position.x,nav.position.y,nav.position.z,config.Crossfire.Spacing,Time.time,r.Group))
            {EndCrossfire(b,s,"secondary-entrance-entry-position-busy");return;}
            crossfireClaims.Release(s.Id);
            if(!crossfireClaims.Reserve(s.Id,nav.position.x,nav.position.y,nav.position.z,config.Crossfire.Spacing,Time.time,r.Group)){EndCrossfire(b,s,"secondary-entrance-entry-reservation-failed");return;}
            r.Goal=nav.position;r.Route=route;r.Length=(float)Length(cs);r.Samples.Clear();r.Samples.AddRange(cs);
            if(!b.Mover.WalkToPointByWay(route,true,.3f,false)||b.Mover.ActivePath==null){EndCrossfire(b,s,"secondary-entrance-native-entry-refused");return;}
            r.Path=b.Mover.ActivePath;r.PathStarted=r.Path.TimeStarted;
            if(!OwnCrossfirePath(b,r)){r.Path=null;EndCrossfire(b,s,"secondary-entrance-native-entry-unobserved");return;}
            r.Policy.Ready(r.Goal.x,r.Goal.y,r.Goal.z,r.Angle,r.Length);r.Policy.Moving(Time.time,Vector3.Distance(b.Position,r.Goal));
            a.Preparing=false;a.Entering=true;t.State="entering";r.LastSafety=Time.time;r.Policy.Trace!.Reason="secondary-entrance-entry-approved";r.Policy.Trace.Execution="native secondary entry route observed";
        }finally{doorClock.Stop();doorBudget.Charge(doorClock.Elapsed.TotalMilliseconds);}
    }
}
