using System;
using System.Collections.Generic;
using Comet.Core;
using SAIN.Components;
using SAIN.SAINComponent.Classes.EnemyClasses;
using UnityEngine;
using UnityEngine.AI;
namespace Comet.Client;

internal sealed class SquadTradeRuntime
{
    internal readonly SquadTradeMemory Memory=new();
    internal int GroupStamp;
    internal float DeathAt,Started,LastCallback,FaceAt=-1;
    internal bool Confirmed,Push;
}
public sealed partial class Plugin
{
    private string SquadTradeBlock(BotComponent b,BotState s)
    {
        if(!CrossfireScope(b)||!config!.SquadTrade.Enabled)return "trade-out-of-scope";
        if(TradeGroup(b)!=s.Crossfire.Trade.GroupStamp)return "trade-squad-changed";
        if(b.GoalEnemy?.IsVisible==true)return "trade-enemy-visible";
        return CrossfireCommonBlock(b,s,true);
    }
    private CrossfireConfig TradeMotionConfig()=>new(){Enabled=config!.Enabled&&config.SquadTrade.Enabled,Participation=100,Spacing=3,
        MaxPath=config.SquadTrade.MaxPath,StallTimeout=config.SquadTrade.StallTimeout,MoveTimeout=config.SquadTrade.Duration,HoldTimeout=config.SquadTrade.Duration,Retry=1};
    private bool TryBeginSquadTrade(BotComponent b,BotState s)
    {
        var r=s.Crossfire;var a=r.Trade;var c=config!.SquadTrade;
        if(!c.Enabled||r.Policy.Active||Time.time<r.Policy.RetryAt)return false;
        var loss=a.Memory.Take(Time.time,c);if(loss==null)return false;
        a.GroupStamp=loss.Group;
        if(!r.Policy.Begin(Time.time,TradeMotionConfig(),0))return false;
        RememberCrossfireHandoff(b,r);
        r.Maneuver=null;r.ManeuverRole=null;r.Entrance.Drop();a.DeathAt=(float)loss.At;a.Started=a.LastCallback=Time.time;a.FaceAt=-1;a.Confirmed=false;
        r.Origin=b.Position;r.Support=loss.Mate;r.Reference=b.Position;r.Group=b.Squad.Members!=null?ManeuverGroup(b):"";r.Contact="";r.Known=b.Position;r.ContactAt=Time.time;
        r.Route=null;r.Candidate=r.Sample=0;r.Rejects.Clear();r.Samples.Clear();r.Candidates.Clear();
        var t=r.Policy.Trace!;var d=t.Trade=new SquadTradeTrace{Death=loss.Id,Mate=loss.Mate,MateName=loss.Name,DeathAge=Time.time-loss.At,Delay=loss.ReadyAt-loss.At,Ammo=Math.Max(0,Math.Min(100,ManeuverAmmo(b)))};
        t.Support=loss.Mate;t.SupportName=loss.Name;
        string block=SquadTradeBlock(b,s);
        if(block.Length>0){d.Reason=block;EndCrossfire(b,s,block);return false;}
        var contacts=new List<Enemy>();if(b.GoalEnemy!=null)contacts.Add(b.GoalEnemy);
        int count=0;foreach(var e in b.EnemyController.KnownEnemies){if(++count>16)break;if(e!=null&&!contacts.Contains(e))contacts.Add(e);}
        Enemy? chosen=null;
        foreach(var e in contacts)
        {
            if(!e.EnemyKnown||e.IsVisible||!float.IsFinite(e.TimeSinceLastKnownUpdated)||e.TimeSinceLastKnownUpdated<0||e.TimeSinceLastKnownUpdated>c.ContactAge
                ||e.KnownPlaces.LastKnownPosition is not Vector3 k||!ValidPoint(k)||Vector3.Distance(b.Position,k)>c.MaxDistance)continue;
            if(chosen==null||e.EnemyProfileId==loss.AssociatedContact||chosen.EnemyProfileId!=loss.AssociatedContact&&e.TimeSinceLastKnownUpdated<chosen.TimeSinceLastKnownUpdated)chosen=e;
        }
        if(chosen==null){EndCrossfire(b,s,"trade-no-fresh-known-sector");return false;}
        bool associated=loss.AssociatedContact.Length>0&&chosen.EnemyProfileId==loss.AssociatedContact;
        RefreshFear(b,s);bool aggressive=b.Info.PersonalitySettings.Rush.CanRushEnemyReloadHeal;
        d.Roll=jumpRandom.NextDouble()*100;d.Chance=aggressive?c.AggressiveChance:c.OtherChance;
        d.Choice=SquadTradeMemory.Choose(associated,aggressive,d.Ammo,s.Fear.Current.Panic,d.Roll/100,c,out var why);d.Reason=why;
        d.Confidence=associated?"witnessed attacker association":"sector only";a.Push=d.Choice=="push";
        r.Known=chosen.KnownPlaces.LastKnownPosition!.Value;r.Contact=chosen.EnemyProfileId;r.ContactAt=Time.time-chosen.TimeSinceLastKnownUpdated;
        t.Contact=r.Contact;t.Source=NativeAdapter.Capture(b,chosen,true,false).ContactSource;t.X=r.Known.x;t.Y=r.Known.y;t.Z=r.Known.z;t.ContactAge=Time.time-r.ContactAt;t.Roll=d.Roll;t.Chance=d.Chance;
        t.Reason="trade-"+d.Choice;t.Execution="selecting a safe position";t.Remaining=d.Remaining=c.Duration;
        r.Rejects.Add(a.Push?"watch: push roll accepted":"push: "+why);t.Alternatives=r.Rejects.ToArray();
        BuildTradeCandidates(b,s);RecordCrossfire(s,true);return true;
    }
    private void BuildTradeCandidates(BotComponent b,BotState s)
    {
        var r=s.Crossfire;var forward=r.Known-r.Origin;forward.y=0;forward=forward.normalized;var side=new Vector3(-forward.z,0,forward.x);
        if(r.Trade.Push)
        {
            var center=r.Known-forward*3;
            // Approach the dated sector; never blindly run onto the target's last position.
            for(int i=0;i<5;i++)r.Candidates.Add(center+side*((i-2)*2));
        }
        else
        {
            r.Candidates.Add(b.Position);
            for(int ring=1;ring<=2;ring++)for(int i=0;i<8;i++)r.Candidates.Add(r.Origin+new Vector3(Mathf.Cos(i*Mathf.PI/4),0,Mathf.Sin(i*Mathf.PI/4))*(ring*3));
        }
    }
    private bool TradeUseful(CrossfireRuntime r,Vector3 point)=>CrossfireSight(point,r.Known)&&(r.Trade.Push||CombatProtection(point,r.Known)>0);
    private void ProbeSquadTrade(BotComponent b,BotState s)
    {
        var r=s.Crossfire;var c=config!.SquadTrade;var t=r.Policy.Trace!;
        if(r.Route==null)
        {
            if(r.Candidate>=r.Candidates.Count)
            {
                if(r.Trade.Push){r.Trade.Push=false;t.Trade!.Choice="watch";t.Trade.Reason="no safe push route";t.Reason="trade-watch-fallback";r.Candidates.Clear();r.Candidate=0;BuildTradeCandidates(b,s);RecordCrossfire(s,true);return;}
                EndCrossfire(b,s,"trade-no-safe-position");return;
            }
            var candidate=r.Candidates[r.Candidate++];t.Candidates++;
            if(!NavMesh.SamplePosition(candidate,out var nav,.75f,NavMesh.AllAreas)||Math.Abs(nav.position.y-r.Origin.y)>1.5){RejectCrossfire(r,"no same-floor navigation");return;}
            r.Goal=nav.position;string why=CrossfireSpace(b,s,r.Goal,true);
            if(why.Length==0&&!TradeUseful(r,r.Goal))why="no useful protected angle";
            if(why.Length==0)why=RunVolume(b,r.Goal,r.Goal+Vector3.up*.01f);
            if(why.Length>0){RejectCrossfire(r,why);return;}
            if(Vector3.Distance(b.Position,r.Goal)<.3){r.Route=new NavMeshPath();r.Length=0;r.Samples.Clear();}
            else
            {
                if(!b.Mover.CanGoToPoint(r.Goal,out var route,true,.2f)||!EntrancePath(route,b.Position,r.Goal,(float)c.MaxPath)){RejectCrossfire(r,"no short complete route");return;}
                r.Route=route;r.Length=(float)Length(route.corners);r.Samples.Clear();
                for(int i=1;i<route.corners.Length;i++){var from=route.corners[i-1];var to=route.corners[i];int steps=Math.Max(1,(int)Math.Ceiling(Vector3.Distance(from,to)));for(int j=1;j<=steps;j++)r.Samples.Add(from+(to-from)*(j/(float)steps));}
            }
            r.Sample=0;return;
        }
        if(r.Sample<r.Samples.Count)
        {
            var point=r.Samples[r.Sample];var previous=r.Sample==0?r.Origin:r.Samples[r.Sample-1];r.Sample++;
            string why=CrossfireSpace(b,s,point,false);if(why.Length==0)why=CrossfireThreatSafety(b,r,point);
            if(why.Length==0&&CombatProtection(r.Origin,r.Known)==7&&CombatProtection(point,r.Known)==0)why="route leaves protection";
            if(why.Length==0)why=RunVolume(b,previous,point);if(why.Length>0)RejectCrossfire(r,why);return;
        }
        if(!crossfireClaims.Reserve(s.Id,r.Goal.x,r.Goal.y,r.Goal.z,3,Time.time,r.Group)){RejectCrossfire(r,"position already reserved");return;}
        r.Policy.Ready(r.Goal.x,r.Goal.y,r.Goal.z,0,r.Length);t.Reason="trade-"+t.Trade!.Choice+"-reserved";
    }
    private bool RefreshSquadTrade(BotComponent b,BotState s)
    {
        var r=s.Crossfire;var a=r.Trade;var t=r.Policy.Trace!;var d=t.Trade!;var c=config!.SquadTrade;
        string block=SquadTradeBlock(b,s);if(block.Length>0){EndCrossfire(b,s,block);return false;}
        d.DeathAge=Math.Max(0,Time.time-a.DeathAt);
        d.Remaining=Math.Max(0,c.Duration-(Time.time-a.Started));t.Remaining=d.Remaining;t.ContactAge=Math.Max(0,Time.time-r.ContactAt);
        if(Time.time<a.Started||d.Remaining<=0){EndCrossfire(b,s,"trade-timeout");return false;}
        RefreshFear(b,s);if(a.Push&&(s.Fear.Current.Panic||ManeuverAmmo(b)<c.MinimumAmmo)){EndCrossfire(b,s,"trade-push-readiness-lost");return false;}
        Enemy? observed=null;int scanned=0;
        foreach(var e in b.EnemyController.KnownEnemies){if(++scanned>16)break;if(e?.EnemyProfileId==r.Contact){observed=e;break;}}
        if(observed==null&&b.GoalEnemy?.EnemyProfileId==r.Contact)observed=b.GoalEnemy;
        if(observed?.EnemyKnown!=true){EndCrossfire(b,s,"trade-contact-lost");return false;}
        if(observed.KnownPlaces.LastKnownPosition is Vector3 known&&ValidPoint(known)&&float.IsFinite(observed.TimeSinceLastKnownUpdated)&&observed.TimeSinceLastKnownUpdated>=0)
        {
            if(Vector3.Distance(known,r.Known)>4){EndCrossfire(b,s,"trade-contact-moved");return false;}
            if(Vector3.Distance(known,r.Known)<.5f)r.ContactAt=Math.Max(r.ContactAt,Time.time-observed.TimeSinceLastKnownUpdated);
            t.ContactAge=Math.Max(0,Time.time-r.ContactAt);
        }
        if(t.State!="holding"&&t.ContactAge>c.ContactAge){EndCrossfire(b,s,"trade-contact-stale");return false;}
        if(a.Confirmed&&Time.time-a.LastCallback>=1){EndCrossfire(b,s,"trade-execution-expired");return false;}
        if(t.Reserved&&!crossfireClaims.Renew(s.Id,Time.time)){EndCrossfire(b,s,"trade-position-expired");return false;}
        if(t.State=="planning")
        {
            if(Vector3.Distance(b.Position,r.Origin)>1){EndCrossfire(b,s,"trade-origin-moved");return false;}
            if(r.ProbeFrame!=Time.frameCount&&doorBudget.TryBegin(Time.frameCount))
            {r.ProbeFrame=Time.frameCount;doorClock.Restart();try{ProbeSquadTrade(b,s);}finally{doorClock.Stop();doorBudget.Charge(doorClock.Elapsed.TotalMilliseconds);}}
        }
        if(!r.Policy.Active)return false;
        d.Status=t.State=="moving"?"moving":t.State=="holding"?(a.Confirmed?"watching":"awaiting facing"):"selected";
        if(t.State=="holding")
        {
            t.Execution=a.Confirmed?"watching the dated known sector":"waiting for observed facing";
            if(t.Reason=="crossfire-angle-reached")t.Reason="trade-position-reached";
        }
        d.Confirmed=a.Confirmed;t.Applied=r.Layer&&(t.State=="moving"||t.State=="holding"&&a.Confirmed);t.ValidFor=t.Applied?Math.Min(1,d.Remaining):0;
        RecordCrossfire(s,false);return t.State!="planning";
    }
    private void ExecuteSquadTrade(BotComponent b,BotState s)
    {
        var r=s.Crossfire;r.Trade.LastCallback=Time.time;
        if(r.Policy.Trace!.State=="ready"&&r.Length==0)
        {
            if(b.Mover.ActivePath!=null||QueuedCombatMovement(b)){EndCrossfire(b,s,"trade-foreign-route");return;}
            r.Policy.Moving(Time.time,0);r.Policy.Progress(Time.time,0,TradeMotionConfig());r.LastSafety=Time.time;
        }
        ExecuteCrossfire(b,s);
        if(r.Policy.Active&&r.Policy.Trace.State=="holding")
        {
            if(b.Mover.ActivePath!=null||QueuedCombatMovement(b)){EndCrossfire(b,s,"trade-foreign-route");return;}
            if(Vector3.Distance(b.Position,r.Goal)>1){EndCrossfire(b,s,"trade-left-position");return;}
            if(!r.Trade.Confirmed){if(r.Trade.FaceAt<0)r.Trade.FaceAt=Time.time;if(Time.time-r.Trade.FaceAt>=config!.SquadTrade.StallTimeout){EndCrossfire(b,s,"trade-facing-stalled");return;}}
            r.Policy.Trace.Execution=r.Trade.Confirmed?"watching the dated known sector":"waiting for observed facing";
        }
    }
    private void ConfirmSquadTrade(BotComponent b,BotState s)
    {
        var r=s.Crossfire;if(r.Policy.Trace?.Trade==null||!r.Layer)return;
        r.Trade.Confirmed=r.Policy.Trace.State=="holding"&&Vector3.Angle(b.Player.LookDirection,r.Known-b.Position)<=30;
        if(r.Trade.Confirmed)r.Trade.FaceAt=-1;r.Trade.LastCallback=Time.time;r.Policy.Trace.Trade.Confirmed=r.Trade.Confirmed;
    }
    private void EndSquadTrade(BotState s,string reason)
    {var r=s.Crossfire;if(r.Policy.Trace?.Trade==null)return;r.Policy.Trace.Trade.Status="ended";r.Policy.Trace.Trade.Reason=reason;r.Policy.Trace.Trade.Confirmed=r.Trade.Confirmed=false;}
}
