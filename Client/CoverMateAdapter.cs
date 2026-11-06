using System;
using System.Collections.Generic;
using Comet.Core;
using EFT;
using EFT.InventoryLogic;
using SAIN.Components;
using SAIN.SAINComponent.Classes.EnemyClasses;
using UnityEngine;
using UnityEngine.AI;
namespace Comet.Client;

internal sealed class CoverMateRuntime
{
    internal string Mate="",Operation="",Sector="",Source="";
    internal float Started,Next,LastCallback,RetryAt,FaceAt=-1,MoveAt=-1;
    internal Vector3 MateOrigin;
    internal bool Confirmed;
}
public sealed partial class Plugin
{
    private readonly CoverMateBoard coverMateBoard=new();
    private CoverOperation? ObserveCoverOperation(BotComponent mate)
    {
        var med=mate.BotOwner.Medecine;bool surgery=med.SurgicalKit.Using,healing=surgery||med.FirstAid.Using||med.Using||mate.Player.HandsController is IMedsController;
        bool reload=mate.BotOwner.WeaponManager.Reload.Reloading;
        var item=surgery?med.SurgicalKit.CurUsingMeds:med.FirstAid.Using?med.FirstAid.CurUsingMeds:null;
        double expected=0,progress=mate.BotOwner.WeaponManager.CurrentWeapon?.GetCurrentMagazine()?.Count??0;
        if(item!=null&&healing)
        {var part=surgery?med.SurgicalKit._bodyPartToHeal:med.FirstAid._bodyPartToHeal;if(part.HasValue)expected=item.HealthEffectsComponent.UseTimeFor(part.Value);progress+=item.MedKitComponent?.HpResource??0;}
        return coverMateBoard.Observe(mate.ProfileId,Time.time,surgery?"Surgery":healing?"Medicine":"Reload",healing?mate.Player.HandsController?.Item?.Id.ToString()??"":"",
            healing||reload,!healing&&!reload&&WeaponHandsReady(mate,mate.Player.HandsController as Player.FirearmController),expected,progress,config!.CoverMate);
    }
    private string CoverMateBlock(BotComponent b,BotState s)
    {
        if(!CrossfireScope(b)||!config!.CoverMate.Enabled)return "cover-mate-out-of-scope";
        if(b.GoalEnemy?.IsVisible==true)return "cover-mate-enemy-visible";
        return CrossfireCommonBlock(b,s);
    }
    private bool TryBeginCoverMate(BotComponent b,BotState s)
    {
        var r=s.Crossfire;var c=config!.CoverMate;var a=r.Care;
        if(!c.Enabled||r.Policy.Active||Time.time<a.Next||Time.time<a.RetryAt||CoverMateBlock(b,s).Length>0)return false;
        a.Next=Time.time+.5f;
        if(b.Mover.ActivePath!=null&&!CrossfireCanHandoff(b)||QueuedCombatMovement(b))return false;
        BotComponent? selected=null;CoverOperation? operation=null;float nearest=float.MaxValue;
        foreach(var mate in b.Squad.Members.Values)
        {
            if(mate==null||ReferenceEquals(b,mate)||!CrossfireScope(mate)||Vector3.Distance(b.Position,mate.Position)>c.MateDistance||Math.Abs(b.Position.y-mate.Position.y)>2.5)continue;
            var op=ObserveCoverOperation(mate);if(op==null||op.Ended||!op.Busy)continue;
            float d=Vector3.Distance(b.Position,mate.Position);if(d<nearest){selected=mate;operation=op;nearest=d;}
        }
        if(selected==null||operation==null)return false;
        // Information must already belong to this PMC's SAIN knowledge (including normal squad sharing).
        // An old snapshot describes a sector to watch; it never authorizes shots or tracks a hidden transform.
        var contacts=new List<Enemy>();if(b.GoalEnemy!=null)contacts.Add(b.GoalEnemy);
        int n=0;foreach(var e in b.EnemyController.KnownEnemies){if(++n>16)break;if(e!=null&&!contacts.Contains(e))contacts.Add(e);}
        Enemy? threat=null;Vector3 known=default;
        foreach(var e in contacts)
        {
            if(!e.EnemyKnown||e.IsVisible||!float.IsFinite(e.TimeSinceLastKnownUpdated)||e.TimeSinceLastKnownUpdated<0||e.TimeSinceLastKnownUpdated>30
                ||e.KnownPlaces.LastKnownPosition is not Vector3 k||!ValidPoint(k)||Vector3.Distance(k,selected.Position)<8||Vector3.Distance(k,selected.Position)>80)continue;
            var sector=k-selected.Position;
            if(!coverMateBoard.Available(s.Id,selected.ProfileId,operation.Id,e.EnemyProfileId,sector.x,sector.z,Time.time,c))continue;
            threat=e;known=k;break;
        }
        if(threat==null||!coverMateBoard.Attempt(s.Id,selected.ProfileId,operation.Id,Time.time))return false;
        var direction=known-selected.Position;
        if(!coverMateBoard.Reserve(s.Id,selected.ProfileId,operation.Id,threat.EnemyProfileId,direction.x,direction.z,Time.time,c))return false;
        if(!r.Policy.Begin(Time.time,CoverMateMotionConfig(),0)){coverMateBoard.Release(s.Id);return false;}
        RememberCrossfireHandoff(b,r);
        a.Mate=selected.ProfileId;a.Operation=operation.Id;a.Sector=threat.EnemyProfileId;a.Started=Time.time;a.MateOrigin=selected.Position;a.Confirmed=false;a.LastCallback=Time.time;a.FaceAt=a.MoveAt=-1;
        a.Source=NativeAdapter.Capture(b,threat,true,false).ContactSource;
        r.Maneuver=null;r.ManeuverRole=null;r.Entrance.Drop();r.Known=known;r.Contact=threat.EnemyProfileId;r.ContactAt=Time.time-threat.TimeSinceLastKnownUpdated;
        r.Support=selected.ProfileId;r.Reference=selected.Position;r.Origin=b.Position;r.Group=ManeuverGroup(b);r.Indoor=b.Player.Environment==EnvironmentType.Indoor;
        r.Candidate=r.Sample=0;r.Route=null;r.Rejects.Clear();r.Samples.Clear();r.Candidates.Clear();
        var t=r.Policy.Trace!;t.CoverMate=new(){Mate=selected.ProfileId,MateName=selected.Player.Profile.Nickname,Operation=operation.Id,Treatment=operation.Kind,Sector=threat.EnemyProfileId,Busy=true};
        t.Contact=r.Contact;t.Source=a.Source;t.Support=r.Support;t.SupportName=selected.Player.Profile.Nickname;t.X=known.x;t.Y=known.y;t.Z=known.z;
        t.Reason="cover-mate-observed-operation";t.Execution="choosing a protected sector";
        r.Candidates.Add(b.Position);
        // Current position first, then bounded rings. Search only local navigation, never all map covers.
        for(int ring=0;ring<2;ring++)for(int i=0;i<8;i++)
        {float angle=i*Mathf.PI/4,radius=(float)(ring==0?c.Near:c.Far);r.Candidates.Add(selected.Position+new Vector3(Mathf.Cos(angle),0,Mathf.Sin(angle))*radius);}
        RecordCrossfire(s,true);return true;
    }
    private CrossfireConfig CoverMateMotionConfig()=>new(){Enabled=config!.Enabled&&config.CoverMate.Enabled,Participation=100,Spacing=2,
        MaxPath=config.CoverMate.MaxPath,StallTimeout=config.CoverMate.StallTimeout,MoveTimeout=12,HoldTimeout=config.CoverMate.SurgeryDuration,Retry=config.CoverMate.Retry};
    private bool RefreshCoverMate(BotComponent b,BotState s)
    {
        var r=s.Crossfire;var a=r.Care;var t=r.Policy.Trace!;var c=config!.CoverMate;var d=t.CoverMate!;
        string block=CoverMateBlock(b,s);
        if(block.Length>0){EndCrossfire(b,s,block);return false;}
        if(!b.Squad.Members.TryGetValue(a.Mate,out var mate)||!CrossfireScope(mate)||ManeuverGroup(b)!=r.Group
            ||Vector3.Distance(mate.Position,a.MateOrigin)>1.5||Vector3.Distance(b.Position,mate.Position)>c.MateDistance)
        {EndCrossfire(b,s,"cover-mate-unavailable-or-moved");return false;}
        var op=ObserveCoverOperation(mate);
        if(op!=null&&op.Id==a.Operation){d.Busy=op.Busy;d.WeaponReady=op.Ready;d.OperationAge=Math.Max(0,Time.time-op.Started);}
        if(op==null||op.Id!=a.Operation||op.Ended){d.Stalled=op?.Stalled??false;EndCrossfire(b,s,d.Stalled?"cover-mate-operation-stalled":"cover-mate-weapon-ready-or-operation-ended");return false;}
        if(!coverMateBoard.Valid(s.Id,Time.time)||a.Confirmed&&Time.time-a.LastCallback>=1)
        {EndCrossfire(b,s,"cover-mate-execution-expired");return false;}
        d.Treatment=op.Kind;d.Busy=op.Busy;d.WeaponReady=op.Ready;d.OperationAge=Math.Max(0,Time.time-op.Started);
        d.Remaining=Math.Max(0,(op.Kind=="Surgery"?c.SurgeryDuration:c.Duration)-(Time.time-a.Started));
        d.Protectors=coverMateBoard.Count(a.Mate,a.Operation,Time.time);d.Separation=Vector3.Distance(b.Position,mate.Position);
        t.Remaining=d.Remaining;t.ContactAge=Math.Max(0,Time.time-r.ContactAt);
        if(d.Remaining<=0||Time.time<a.Started){EndCrossfire(b,s,"cover-mate-timeout");return false;}
        if(t.State=="moving"&&a.MoveAt>=0&&Time.time-a.MoveAt>=12){EndCrossfire(b,s,"cover-mate-move-timeout");return false;}
        if(t.Reserved&&!crossfireClaims.Renew(s.Id,Time.time)){EndCrossfire(b,s,"cover-mate-position-expired");return false;}
        if(t.State=="planning")
        {
            if(Vector3.Distance(b.Position,r.Origin)>1){EndCrossfire(b,s,"cover-mate-origin-moved");return false;}
            if(r.ProbeFrame!=Time.frameCount&&doorBudget.TryBegin(Time.frameCount))
            {r.ProbeFrame=Time.frameCount;doorClock.Restart();try{ProbeCoverMate(b,s);}finally{doorClock.Stop();doorBudget.Charge(doorClock.Elapsed.TotalMilliseconds);}}
        }
        if(!r.Policy.Active)return false;
        d.Status=t.State=="planning"||t.State=="ready"?"selected":t.State=="moving"?"moving":a.Confirmed?"covering":"awaiting facing";
        d.Reason=op.Reason;
        if(t.State=="holding")t.Execution=a.Confirmed?"watching sector while teammate is unavailable":"waiting for observed facing";
        d.Confirmed=a.Confirmed;t.Applied=r.Layer&&(t.State=="moving"||t.State=="holding"&&a.Confirmed);t.ValidFor=t.Applied?Math.Min(1,d.Remaining):0;
        RecordCrossfire(s,false);return r.Policy.Active&&t.State!="planning";
    }
    private string CoverMateSpace(BotComponent b,BotState s,Vector3 point,bool goal)
    {
        var r=s.Crossfire;var c=config!.CoverMate;double separation=Vector3.Distance(point,r.Care.MateOrigin);
        if(goal&&(separation<c.Near-.1||separation>c.Far+.1))return "outside useful teammate spacing";
        if(!crossfireClaims.Free(s.Id,point.x,point.y,point.z,goal?2:1.2,Time.time,r.Group))return "reserved position";
        foreach(var mate in b.Squad.Members.Values)
        {
            if(mate==null||ReferenceEquals(mate,b)||mate.IsDead)continue;
            if(Vector3.Distance(point,mate.Position)<(goal?2:1.2))return "teammate spacing";
            var e=mate.GoalEnemy;
            if(e?.IsVisible==true&&e.KnownPlaces.LastKnownPosition is Vector3 k&&CrossfireGeometry.InLane(point.x,point.y,point.z,mate.Position.x,mate.Position.y,mate.Position.z,k.x,k.y,k.z))return "allied firing lane";
        }
        return "";
    }
    private bool CoverMateUseful(Vector3 point,Vector3 known)=>CombatProtection(point,known)!=0&&CrossfireSight(point,known);
    private void ProbeCoverMate(BotComponent b,BotState s)
    {
        var r=s.Crossfire;var c=config!.CoverMate;var t=r.Policy.Trace!;
        if(r.Route==null)
        {
            if(r.Candidate>=r.Candidates.Count){EndCrossfire(b,s,"cover-mate-no-useful-protected-sector");return;}
            var candidate=r.Candidates[r.Candidate++];t.Candidates=r.Candidate;
            if(!NavMesh.SamplePosition(candidate,out var nav,.75f,NavMesh.AllAreas)||Math.Abs(nav.position.y-r.Origin.y)>1.5){RejectCrossfire(r,"no same-floor navigation");return;}
            r.Goal=nav.position;string why=CoverMateSpace(b,s,r.Goal,true);
            if(why.Length==0&&!CoverMateUseful(r.Goal,r.Known))why="no protected view of known sector";
            if(why.Length==0)why=RunVolume(b,r.Goal,r.Goal+Vector3.up*.01f);
            if(why.Length>0){RejectCrossfire(r,why);return;}
            if(Vector3.Distance(b.Position,r.Goal)<.3){r.Route=new NavMeshPath();r.Samples.Clear();r.Length=0;}
            else
            {
                if(!b.Mover.CanGoToPoint(r.Goal,out var path,true,.2f)||!EntrancePath(path,b.Position,r.Goal,(float)c.MaxPath))
                {RejectCrossfire(r,"no short complete route");return;}
                r.Route=path;r.Length=(float)Length(path.corners);r.Samples.Clear();
                for(int i=1;i<path.corners.Length;i++){var from=path.corners[i-1];var to=path.corners[i];int steps=Math.Max(1,(int)Math.Ceiling(Vector3.Distance(from,to)));for(int j=1;j<=steps;j++)r.Samples.Add(from+(to-from)*(j/(float)steps));}
            }
            r.Sample=0;return;
        }
        if(r.Sample<r.Samples.Count)
        {
            var point=r.Samples[r.Sample];var previous=r.Sample==0?r.Origin:r.Samples[r.Sample-1];r.Sample++;
            string why=CoverMateSpace(b,s,point,false);
            if(why.Length==0)why=CrossfireThreatSafety(b,r,point);
            if(why.Length==0&&CombatProtection(r.Origin,r.Known)==7&&CombatProtection(point,r.Known)==0)why="route leaves protection";
            if(why.Length==0)why=RunVolume(b,previous,point);
            if(why.Length>0)RejectCrossfire(r,why);return;
        }
        if(!crossfireClaims.Reserve(s.Id,r.Goal.x,r.Goal.y,r.Goal.z,2,Time.time,r.Group)){RejectCrossfire(r,"position already reserved");return;}
        r.Policy.Ready(r.Goal.x,r.Goal.y,r.Goal.z,0,r.Length);t.Reason="cover-mate-sector-reserved";t.Execution="selected; awaiting native execution";
    }
    private void ExecuteCoverMate(BotComponent b,BotState s)
    {
        var r=s.Crossfire;
        if(!coverMateBoard.Renew(s.Id,Time.time)){EndCrossfire(b,s,"cover-mate-sector-expired");return;}
        r.Care.LastCallback=Time.time;
        if(r.Policy.Trace!.State=="ready"&&r.Length==0)
        {
            if(b.Mover.ActivePath!=null||QueuedCombatMovement(b)){EndCrossfire(b,s,"cover-mate-foreign-route");return;}
            r.Policy.Moving(Time.time,0);r.Policy.Progress(Time.time,0,CoverMateMotionConfig());r.LastSafety=Time.time;
        }
        ExecuteCrossfire(b,s);
        if(r.Policy.Active&&r.Policy.Trace.State=="moving"&&r.Care.MoveAt<0)r.Care.MoveAt=Time.time;
        if(r.Policy.Active&&r.Policy.Trace.State=="holding")
        {
            if(b.Mover.ActivePath!=null||QueuedCombatMovement(b)){EndCrossfire(b,s,"cover-mate-foreign-route");return;}
            if(Vector3.Distance(b.Position,r.Goal)>1){EndCrossfire(b,s,"cover-mate-left-sector");return;}
            if(!r.Care.Confirmed)
            {
                if(r.Care.FaceAt<0)r.Care.FaceAt=Time.time;
                if(Time.time-r.Care.FaceAt>=config!.CoverMate.StallTimeout){EndCrossfire(b,s,"cover-mate-facing-stalled");return;}
            }
            r.Policy.Trace.Execution=r.Care.Confirmed?"watching sector while teammate is unavailable":"waiting for observed facing";
        }
    }
    private void ConfirmCoverMate(BotComponent b,BotState s)
    {
        var r=s.Crossfire;if(r.Policy.Trace?.CoverMate==null||!r.Layer)return;
        r.Care.Confirmed=r.Policy.Trace.State=="holding"&&Vector3.Angle(b.Player.LookDirection,r.Known-b.Position)<=30;
        if(r.Care.Confirmed)r.Care.FaceAt=-1;
        r.Care.LastCallback=Time.time;r.Policy.Trace.CoverMate.Confirmed=r.Care.Confirmed;
    }
    private void EndCoverMate(BotState s,string reason)
    {
        var r=s.Crossfire;if(r.Policy.Trace?.CoverMate==null)return;
        coverMateBoard.Release(s.Id);r.Care.RetryAt=Time.time+(float)(config?.CoverMate.Retry??4);
        var t=r.Policy.Trace.CoverMate;t.Status="ended";t.Reason=reason;t.Confirmed=false;r.Care.Confirmed=false;
    }
}
