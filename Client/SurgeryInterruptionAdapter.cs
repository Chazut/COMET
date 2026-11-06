using System;
using Comet.Core;
using EFT;
using Newtonsoft.Json;
using SAIN.Components;
using SAIN.Preset.Shared.Enums;
using UnityEngine;

namespace Comet.Client;

public sealed partial class Plugin
{
    private void SampleSurgeryInterruption(BotComponent b,BotState s,bool force)
    {
        var r=s.Medical;var p=r.Interruption;var c=config!.SurgeryInterruption;
        if(!p.Active)return;
        if(!c.Enabled||!MedicalScope(b)){EndSurgeryInterruption(s,"surgery-out-of-scope");return;}
        var kit=b.BotOwner.Medecine.SurgicalKit;
        bool hit=MedicalChoice.Finite(b.Medical.TimeSinceShot)&&b.Medical.TimeSinceShot>=0&&Time.time-b.Medical.TimeSinceShot>p.BeganAt;
        bool grenade=b.Grenade.GrenadeDangerPoint!=null||b.Decision.CurrentCombatDecision==ECombatDecision.AvoidGrenade;
        if(!force&&!hit&&!grenade&&!kit._lastIsCanceled&&Time.time<r.InterruptionAt)return;
        r.InterruptionAt=Time.time+(float)c.Interval;
        var t=p.Trace!;
        bool matching=b.Player.HandsController is IMedsController&&b.Player.HandsController.Item?.Id.ToString()==t.Item;
        if(!p.Cancelling&&kit._using&&!kit._lastIsCanceled&&!r.Callback
            &&(kit.CurUsingMeds?.Id.ToString()!=t.Item||b.Player.HandsController is IMedsController&&!matching))
        {EndSurgeryInterruption(s,"surgery-operation-replaced");EndMedical(s,"medical-ownership-lost");return;}
        var f=new SurgeryFrame{Now=Time.time,Hit=hit,Grenade=grenade,NativeCancelled=kit._lastIsCanceled,
            Hands=matching,OperationPending=kit._using&&!r.Callback,
            Completed=!b.Player.HealthController.IsBodyPartDestroyed(r.Part),
            WeaponReady=WeaponHandsReady(b,b.Player.HandsController as Player.FirearmController)};
        // Only personal remembered positions. Never inspect an invisible enemy's live transform.
        if(!p.Cancelling&&!hit&&!grenade&&!f.Completed)
        {
            int count=0;
            foreach(var e in b.EnemyController.KnownEnemies)
            {
                if(++count>16)break;
                if(e==null||!e.EnemyKnown)continue;
                var place=e.KnownPlaces.LastSeenPlace;var heard=e.KnownPlaces.LastHeardPlace;
                if(!e.IsVisible&&heard!=null&&(place==null||heard.TimeSincePositionUpdated<place.TimeSincePositionUpdated))place=heard;
                if(place==null||!ValidPoint(place.Position))continue;
                var pos=place.Position;
                f.Evidence.Add(new(){Kind=e.IsVisible?"visible":"known",Contact=e.EnemyProfileId,
                    Age=place.TimeSincePositionUpdated,Distance=Vector3.Distance(b.Position,pos),X=pos.x,Y=pos.y,Z=pos.z});
            }
            foreach(var step in r.Steps)
                if(step.at>=p.BeganAt)f.Evidence.Add(new(){Kind="footstep",Age=Time.time-step.at,
                    Distance=Vector3.Distance(b.Position,step.position),X=step.position.x,Y=step.position.y,Z=step.position.z});
        }
        bool cancel=p.Tick(f,c);
        // CancelCurrent is native, guarded by _lastIsCanceled, and requests TakePrevWeapon.
        // It does not mean hands have changed yet. Native hit/BHF cancellation is not repeated.
        if(cancel)kit.CancelCurrent();
        if(p.Trace!=null)p.Trace.NativeCancelled|=kit._lastIsCanceled;
        RecordSurgeryInterruption(s,false);
    }
    private void EndSurgeryInterruption(BotState s,string reason)
    {if(!s.Medical.Interruption.Active)return;s.Medical.Interruption.End(reason);RecordSurgeryInterruption(s,true);}
    private void RecordSurgeryInterruption(BotState s,bool force)
    {
        var r=s.Medical;var t=r.Interruption.Trace;if(t==null)return;
        string key=t.State+":"+t.Reason+":"+t.Completed+":"+t.NativeCancelled;
        if(!force&&key==r.InterruptionKey&&Time.time<r.InterruptionLogAt)return;
        r.InterruptionKey=key;r.InterruptionLogAt=Time.time+(t.Applied?.5f:4);
        var json=Diagnostics.Serialize(new{schema=1,session,sequence=++sequence,feature="H02",stage="evaluated",decision="SurgeryInterruption",reason=t.Reason,
            realtime=Time.realtimeSinceStartup,utc=DateTime.UtcNow.ToString("O"),configRevision=config?.Revision??0,profileId=s.ProfileId,name=s.Name,botInstance=s.Id,
            surgeryInterruption=t,surgeryInterruptionConfig=config?.SurgeryInterruption});
        diagnostics?.Write(json);Api.Decisions.Publish(json,ex=>{if(!subscriberWarned){subscriberWarned=true;Logger.LogWarning(ex.Message);}});
    }
}
