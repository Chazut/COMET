using System;
using System.Collections.Generic;
using System.Linq;
using Comet.Core;
using EFT;
using EFT.Ballistics;
using EFT.InventoryLogic;
using SAIN.Components;
using SAIN.SAINComponent.Classes.Mover;
using UnityEngine;
using UnityEngine.AI;
namespace Comet.Client;
public sealed partial class Plugin
{
    private void InspectPostMedical(BotComponent b,BotState s)
    {
        var r=s.PostCombat;
        if(!config!.MedicalChoice.Enabled||r.MedicalDisabled||s.Medical.Fault){r.MedicalUseful=false;return;}
        if(s.Medical.Policy.Active||b.BotOwner.Medecine.Using||b.Player.HandsController is IMedsController)return;
        if(Time.time<r.MedicalNext||!doorBudget.TryBegin(Time.frameCount,b))return;
        r.MedicalNext=Time.time+(float)config.MedicalChoice.Interval;doorClock.Restart();
        try
        {
            var f=MedicalFrame(b,s);var items=new Dictionary<string,(Meds item,EBodyPart part)>();
            // Work means a carried, usable item for a real need, not merely HaveWork.
            if(f.Priority.Length>0){r.MedicalUseful=false;return;}
            MedicalSupplies(b,s,f,items);
            r.MedicalUseful=f.Options.Any(o=>o.Usable&&(o.Action!="Stims"||f.Health<=config.MedicalChoice.StimHealth||f.Vital));
            r.MedicalUrgent=f.HeavyBleed||f.Vital;
        }
        finally{doorClock.Stop();doorBudget.Charge(doorClock.Elapsed.TotalMilliseconds);s.Medical.Items.Clear();}
    }
    private void ExecutePostCombat(BotComponent b,BotState s)
    {
        var r=s.PostCombat;var t=r.Policy.Trace!;var c=config!.PostCombat;
        if(r.Path!=null&&t.State!=r.RouteStage)PostStopRoute(b,r);
        if(r.Path!=null)
        {
            if(b.Mover.ActivePath==null)
            {if(r.RouteStage=="cover"&&Vector3.Distance(b.Position,r.Goal)<1.2f){r.Protected=true;r.ProtectedAt=b.Position;}r.Policy.Complete(r.RouteStage,Vector3.Distance(b.Position,r.Goal)<1.2f?"destination reached":"route ended before arrival");r.Path=null;return;}
            else if(!ReferenceEquals(b.Mover.ActivePath,r.Path))
            {EndPostCombat(b,s,"post-combat-route-replaced",false);return;}
            else if(Vector3.Distance(b.Position,r.Goal)<1.2f)
            {if(r.RouteStage=="cover"){r.Protected=true;r.ProtectedAt=b.Position;}r.Policy.Complete(r.RouteStage,"destination reached");PostStopRoute(b,r);return;}
            else
            {
                float d=Vector3.Distance(b.Position,r.Goal);
                if(d<r.BestDistance-.4f){r.BestDistance=d;r.ProgressAt=Time.time;}
                if(Time.time-r.ProgressAt>3){r.Policy.Complete(r.RouteStage,"route stalled; no repeat");PostStopRoute(b,r);return;}
                else {b.Steering.LookToMovingDirection();t.Execution="native route observed";return;}
            }
        }
        if(t.State=="cover")
        {
            if(!doorBudget.TryBegin(Time.frameCount,b))return;doorClock.Restart();
            try
            {
                if(!r.Threat.HasValue||CombatProtection(b.Position,r.Threat.Value)==7)
                {r.Protected=true;r.ProtectedAt=b.Position;r.Policy.Complete("cover","already protected");return;}
                Vector3 candidate=default;bool found=false;
                var covers=b.Cover.CoverPoints;
                int nativeCount=Math.Min(16,covers.Count),index=r.CoverCandidate++;
                if(index<nativeCount){var cp=covers[index];if(cp!=null&&!cp.CoverData.IsBad){candidate=cp.Position;found=true;}}
                else if(index<nativeCount+8)
                {
                    float angle=(index-nativeCount)*Mathf.PI/4;var dir=new Vector3(Mathf.Cos(angle),0,Mathf.Sin(angle));
                    if(Physics.Raycast(b.Position+Vector3.up,dir,out var hit,(float)c.CoverRadius,LayersMaskController.HighPolyWithTerrainMask,QueryTriggerInteraction.Ignore))
                    {candidate=hit.point+hit.normal*.7f;candidate.y=b.Position.y;found=true;}
                }
                else {r.Policy.Complete("cover","no reachable protective cover in bounded search");return;}
                if(found&&Vector3.Distance(candidate,b.Position)<=c.CoverRadius&&NavMesh.SamplePosition(candidate,out var sample,.75f,NavMesh.AllAreas)
                    &&CombatProtection(sample.position,r.Threat.Value)==7)
                    PostMove(b,s,sample.position,"cover",c.CoverRadius*1.8);
            }
            finally{doorClock.Stop();doorBudget.Charge(doorClock.Elapsed.TotalMilliseconds);}
            return;
        }
        if(t.State=="medical")
        {
            if(s.Medical.Policy.Trace?.State=="selected"&&s.Medical.Policy.Active)
            {r.MedicalOwner=true;StartMedical(b,s);t.Execution="selected native treatment rechecked";return;}
            if(s.Medical.Policy.Active||b.BotOwner.Medecine.Using||b.Player.HandsController is IMedsController)
            {t.Execution="observing native medicine and callbacks";return;}
            if(t.Elapsed>=c.MedicalDuration){r.MedicalUseful=false;return;}
            if(!doorBudget.TryBegin(Time.frameCount,b))return;doorClock.Restart();
            try
            {
                var f=MedicalFrame(b,s);var items=new Dictionary<string,(Meds item,EBodyPart part)>();
                if(f.Priority.Length==0)MedicalSupplies(b,s,f,items);
                // A recently ended fight still counts as recent information, even if SAIN removed its enemy.
                foreach(var o in f.Options)if(o.Action=="Surgery"&&Time.time-r.Policy.LastFight<config.MedicalChoice.SurgeryQuiet)o.Usable=false;
                var chosen=s.Medical.Policy.Evaluate(f,config.MedicalChoice);RecordMedical(s,false);
                if(chosen.Choice=="Wait"){t.Execution=chosen.Reason;return;}
                if(items.TryGetValue(chosen.Choice,out var item))
                {
                    r.MedicalOwner=true;s.Medical.Item=new(item.item);s.Medical.Part=item.part;s.Medical.Callback=false;s.Medical.Token++;
                    StartMedical(b,s);t.Execution=s.Medical.Policy.Trace?.State=="requested"?"native medicine requested":"native medicine start rechecked";
                    if(!s.Medical.Policy.Active&&++r.MedicalFailures>=2){r.MedicalDisabled=true;r.MedicalUseful=false;}
                }
            }
            finally{doorClock.Stop();doorBudget.Charge(doorClock.Elapsed.TotalMilliseconds);s.Medical.Items.Clear();}
            return;
        }
        if(t.State=="reload")
        {
            if(b.BotOwner.WeaponManager.Reload.Reloading){t.Execution="observing native reload";return;}
            if(!doorBudget.TryBegin(Time.frameCount,b))return;doorClock.Restart();
            try
            {
                var wm=b.BotOwner.WeaponManager;var firearm=b.Player.HandsController as Player.FirearmController;
                r.Policy.Complete("reload","reload unavailable; no repeat");
                if(firearm==null||!WeaponHandsReady(b,firearm)||!wm.Reload.CanReload(true,out var mag,out var ammo))return;
                if(mag!=null&&mag.Count<=(firearm.Item.GetCurrentMagazine()?.Count??0)||mag==null&&(ammo==null||ammo.Count==0))return;
                b.BotOwner.ShootData.EndShoot();wm.Reload.Reloading=true;
                if(mag!=null)wm.Reload.ReloadMagazine(mag);else wm.Reload.ReloadAmmo(ammo);
                t.Execution="native reload requested";
            }
            finally{doorClock.Stop();doorBudget.Charge(doorClock.Elapsed.TotalMilliseconds);}
            return;
        }
        if(t.State is "regroup" or "check")
        {
            if(!doorBudget.TryBegin(Time.frameCount,b))return;doorClock.Restart();
            try
            {
                var leader=b.Squad.LeaderComponent;var target=t.State=="check"?r.Threat:leader!=null&&!leader.IsDead?(Vector3?)leader.Position:null;
                if(!target.HasValue||!PostMove(b,s,target.Value,t.State,t.State=="check"?c.AdvanceMax*1.8:80))r.Policy.Complete(t.State,"destination unavailable; no repeat");
            }
            finally{doorClock.Stop();doorBudget.Charge(doorClock.Elapsed.TotalMilliseconds);}
            return;
        }
        t.Execution=t.State=="watch"?"watching remembered threat":"waiting for native hands";
        if(r.Threat.HasValue)b.Steering.LookToPoint(r.Threat.Value+Vector3.up);
    }
    private bool PostMove(BotComponent b,BotState s,Vector3 target,string stage,double maxLength)
    {
        var r=s.PostCombat;
        if(b.Mover.ActivePath!=null||QueuedCombatMovement(b)||!b.Mover.CanGoToPoint(target,out var route,true,.2f)
            ||route.status!=NavMeshPathStatus.PathComplete||route.corners.Length<2)return false;
        double length=0;for(int i=1;i<route.corners.Length;i++)length+=Vector3.Distance(route.corners[i-1],route.corners[i]);
        if(length>maxLength||Vector3.Distance(route.corners[route.corners.Length-1],target)>1)return false;
        if(!b.Mover.WalkToPointByWay(route,true,.3f,false))return false;
        r.Path=b.Mover.ActivePath;r.RouteStage=stage;r.Goal=target;r.ProgressAt=Time.time;r.BestDistance=Vector3.Distance(b.Position,target);
        r.Policy.Trace!.Execution=r.Path!=null?"native route observed":"route request not observed";
        return r.Path!=null;
    }
    private static void PostStopRoute(BotComponent b,PostCombatRuntime r)
    {
        // Never cancel a route installed by another layer, or an actual airborne movement.
        if(r.Path is BotPathDataManual path&&ReferenceEquals(b?.Mover.ActivePath,path)&&b.Player.MovementContext.IsGrounded)
            combatStopPath.Invoke(path,new object[]{false,"COMET post-combat route released"});
        r.Path=null;r.RouteStage="";
    }
}
