using System;
using System.Collections.Generic;
using Comet.Core;
using Comfort.Common;
using EFT;
using EFT.Ballistics;
using HarmonyLib;
using Newtonsoft.Json;
using SAIN.Components;
using SAIN.SAINComponent.Classes;
using SAIN.SAINComponent.Classes.WeaponFunction;
using UnityEngine;

namespace Comet.Client;
internal sealed class SquadFireRuntime
{
    internal readonly SquadFirePolicy Policy=new();
    internal readonly ShotGeometryRuntime Surfaces=new();
    internal readonly RaycastHit[] Hits=new RaycastHit[32];
    internal readonly Collider[] Near=new Collider[32];
    internal float CheckedAt=-100,Callback=-100,LogAt=-100;
    internal string LogKey="";
    internal bool Fault;
    internal Vector3? ManualTarget;
}
internal sealed class SquadFireAimScope
{
    internal SquadFireRuntime Runtime=null!;internal Vector3? Previous;
}
internal sealed class SquadFireCandidate
{
    internal string Id="",Name="",Kind="prediction";
    internal Vector3 Position,Predicted;internal float Along,Speed;
}
public sealed partial class Plugin
{
    private static void PatchSquadFire(Harmony h)
    {
        // TryShoot publishes its target only after Shoot succeeds. Keep the actual call target scoped to that call.
        h.Patch(AccessTools.Method(typeof(ManualShootClass),nameof(ManualShootClass.TryShoot)),
            prefix:new HarmonyMethod(typeof(Plugin),nameof(SquadFireManualBegin)){priority=Priority.Last},
            finalizer:new HarmonyMethod(typeof(Plugin),nameof(SquadFireManualEnd)));
        // This veto never writes SAIN's FriendlyFireStatus or a global CanShoot flag.
        h.Patch(AccessTools.Method(typeof(ShootData),nameof(ShootData.Shoot),Type.EmptyTypes),
            prefix:new HarmonyMethod(typeof(Plugin),nameof(SquadFireTrigger)){priority=Priority.Last});
        h.Patch(AccessTools.Method(typeof(SAINFriendlyFireClass),nameof(SAINFriendlyFireClass.ManualUpdate)),
            postfix:new HarmonyMethod(typeof(Plugin),nameof(SquadFireUpdate)));
    }
    private static void SquadFireManualBegin(ManualShootClass __instance,Vector3 targetPos,bool __runOriginal,out SquadFireAimScope? __state)
    {
        __state=null;var p=Instance;if(p==null||!__runOriginal)return;
        BotState? state=null;
        try {
            var b=__instance.Bot;if(b==null||!p.SquadFireScope(b)||!ValidPoint(targetPos))return;
            if(!p.states.TryGetValue(b,out state)){state=new BotState(b,p.config!,p.session);p.states.Add(b,state);}
            if(state.SquadFire.Fault)return;
            __state=new(){Runtime=state.SquadFire,Previous=state.SquadFire.ManualTarget};state.SquadFire.ManualTarget=targetPos;
        }catch(Exception ex){if(state!=null)p.SquadFireError(state,ex);}
    }
    private static void SquadFireManualEnd(SquadFireAimScope? __state)
    {if(__state!=null)__state.Runtime.ManualTarget=__state.Previous;}
    private bool SquadFireScope(BotComponent b)=>config?.Enabled==true&&config.SquadFire.Enabled&&Authority&&world!=null
        &&NativeAdapter.IsSupportedAi(b)&&b.BotActive&&!b.IsDead&&!b.GameEnding&&!Ghost(b.ProfileId)
        &&b.Squad.Members!=null&&b.Squad.Members.Count>1&&b.Squad.Members.Count<=16;
    private static bool SquadFireTrigger(ShootData __instance,ref bool __result,bool __runOriginal)
    {
        var p=Instance;if(p==null||!__runOriginal)return true;
        BotState? state=null;
        try {
            var b=__instance._owner?.GetComponent<BotComponent>();if(b==null)return true;
            if(!p.SquadFireScope(b)){if(p.states.TryGetValue(b,out var old))p.EndSquadFire(old,"squad-fire-ineligible");return true;}
            if(!p.states.TryGetValue(b,out state)){state=new BotState(b,p.config!,p.session);p.states.Add(b,state);}
            if(state.SquadFire.Fault)return true;
            // A new trigger request always gets a fresh check, even inside the periodic interval.
            if(!p.CheckSquadFire(b,state))return true;
            bool requested=__instance.Shooting;if(requested)__instance.EndShoot();
            state.SquadFire.Policy.Applied(requested,requested&&!__instance.Shooting,true,p.config!.SquadFire);
            p.RecordSquadFire(state);__result=false;return false;
        }catch(Exception ex){if(state!=null)p.SquadFireError(state,ex);return true;}
    }
    private static void SquadFireUpdate(SAINFriendlyFireClass __instance)
    {
        var p=Instance;var b=__instance.Bot;if(p==null||b==null)return;
        BotState? state=null;
        try {
            if(!p.SquadFireScope(b)){if(p.states.TryGetValue(b,out var old))p.EndSquadFire(old,"squad-fire-ineligible");return;}
            if(!p.states.TryGetValue(b,out state)){
                if(!b.BotOwner.ShootData.Shooting)return;
                state=new BotState(b,p.config!,p.session);p.states.Add(b,state);
            }
            var r=state.SquadFire;if(r.Fault)return;r.Callback=Time.time;
            if(!b.BotOwner.ShootData.Shooting&&!r.Policy.Blocking)return;
            if(Time.time>=r.CheckedAt&&Time.time-r.CheckedAt<p.config!.SquadFire.Interval)return;
            if(!p.CheckSquadFire(b,state))return;
            var shoot=b.BotOwner.ShootData;bool requested=shoot.Shooting;
            if(requested)shoot.EndShoot();
            r.Policy.Applied(requested,requested&&!shoot.Shooting,false,p.config!.SquadFire);p.RecordSquadFire(state);
        }catch(Exception ex){if(state!=null)p.SquadFireError(state,ex);}
    }
    private bool CheckSquadFire(BotComponent b,BotState s)
    {
        var r=s.SquadFire;r.Callback=r.CheckedAt=Time.time;var c=config!.SquadFire;
        var frame=new SquadFireFrame{Now=Time.time,Action=b.Decision.CurrentCombatDecision.ToString()};
        try {
            var start=b.Transform.WeaponData.FirePort;var direction=b.Transform.WeaponData.PointDirection;
            float distance=b.BotOwner.AimingManager?.CurrentAiming?.LastDist2Target??50;
            if(r.ManualTarget is Vector3 target)distance=Vector3.Distance(start,target);
            else if(b.ManualShoot.Reason!=SAIN.Models.Enums.EShootReason.None&&ValidPoint(b.ManualShoot.ShootPosition))distance=Vector3.Distance(start,b.ManualShoot.ShootPosition);
            if(!ValidPoint(start)||!ValidPoint(direction)||direction.sqrMagnitude<.5f||!float.IsFinite(distance)||distance<.05||distance>500)
                return TickSquadFire(s,frame);
            direction.Normalize();var end=start+direction*distance;frame.Muzzle=DP(start);frame.End=DP(end);
            if(!doorBudget.TryBegin(Time.frameCount,b))return TickSquadFire(s,frame);
            doorClock.Restart();try {
                var members=new Dictionary<string,BotComponent>();bool complete=true;
                foreach(var mate in b.Squad.Members.Values)
                    if(mate!=null&&!ReferenceEquals(mate,b)&&!mate.IsDead&&mate.Player?.HealthController?.IsAlive==true&&mate.BotActive&&!Ghost(mate.ProfileId))members[mate.ProfileId]=mate;
                var candidates=new Dictionary<string,SquadFireCandidate>();
                var gw=GameWorldComponent.Instance?.GameWorld;if(gw==null)return TickSquadFire(s,frame);
                void AddBody(Collider collider,float along){
                    if(collider==null)return;var player=gw.GetPlayerByCollider(collider);
                    if(player==null||!members.TryGetValue(player.ProfileId,out var mate))return;
                    if(candidates.TryGetValue(mate.ProfileId,out var prior)&&prior.Along<=along)return;
                    candidates[mate.ProfileId]=new(){Id=mate.ProfileId,Name=mate.Player.Profile.Nickname,Kind="body",Position=mate.Position,Predicted=mate.Position,Along=Math.Max(.05f,along)};
                }
                int near=Physics.OverlapSphereNonAlloc(start,(float)c.BodyRadius,r.Near,LayersMaskController.PlayerMask,QueryTriggerInteraction.Ignore);
                complete&=near<r.Near.Length;for(int i=0;i<near;i++)AddBody(r.Near[i],.05f);
                int hits=Physics.SphereCastNonAlloc(start,(float)c.BodyRadius,direction,r.Hits,distance,LayersMaskController.PlayerMask,QueryTriggerInteraction.Ignore);
                complete&=hits<r.Hits.Length;for(int i=0;i<hits;i++)AddBody(r.Hits[i].collider,r.Hits[i].distance);
                foreach(var mate in members.Values){
                    if(candidates.ContainsKey(mate.ProfileId))continue;
                    var pos=mate.Position;var head=mate.Transform.HeadData.HeadPosition;var velocity=mate.Player.Velocity;
                    if(!ValidPoint(pos)||!ValidPoint(head)||!ValidPoint(velocity)||velocity.magnitude>20){complete=false;continue;}
                    var travel=Vector3.ClampMagnitude(velocity*(float)c.Prediction,3);var foot=pos+Vector3.up*.2f;
                    var center=(foot+head)*.5f;var points=new[]{foot,center,head};
                    foreach(var point in points){
                        if(!SquadFireGeometry.Crosses(DP(start),DP(end),DP(point),DP(point+travel),c.PredictionRadius))continue;
                        float along=Math.Max(.05f,Math.Min(distance,Math.Min(Vector3.Dot(point-start,direction),Vector3.Dot(point+travel-start,direction))));
                        candidates[mate.ProfileId]=new(){Id=mate.ProfileId,Name=mate.Player.Profile.Nickname,Position=pos,Predicted=pos+travel,Along=along,Speed=velocity.magnitude};break;
                    }
                }
                var ordered=new List<SquadFireCandidate>(candidates.Values);ordered.Sort((x,y)=>{int d=x.Along.CompareTo(y.Along);return d!=0?d:string.CompareOrdinal(x.Id,y.Id);});
                var alternatives=new List<string>();int tested=0;
                foreach(var candidate in ordered){
                    if(++tested>4){complete=false;break;}
                    string geometry=SquadFirePath(b,r,start,direction,Math.Max(.01f,candidate.Along-(float)(candidate.Kind=="body"?c.BodyRadius:c.PredictionRadius)));
                    if(geometry=="blocking solid"){alternatives.Add(candidate.Name+": solid cover blocks this path");continue;}
                    frame.Fresh=frame.Risk=true;frame.Ally=candidate.Id;frame.AllyName=candidate.Name;frame.Kind=candidate.Kind;frame.Geometry=geometry;
                    frame.Position=DP(candidate.Position);frame.Predicted=DP(candidate.Predicted);frame.Distance=candidate.Along;frame.Speed=candidate.Speed;
                    alternatives.Add("Continue firing: squadmate intersects current or predicted barrel path");alternatives.Add("Resume: wait for continuous clear checks, then let SAIN decide");
                    frame.Alternatives=alternatives.ToArray();return TickSquadFire(s,frame);
                }
                frame.Fresh=complete;frame.Alternatives=alternatives.ToArray();return TickSquadFire(s,frame);
            }finally {doorClock.Stop();doorBudget.Charge(doorClock.Elapsed.TotalMilliseconds);}
        }finally {Array.Clear(r.Hits,0,r.Hits.Length);Array.Clear(r.Near,0,r.Near.Length);GeometryClearBuffers(r.Surfaces);}
    }
    private string SquadFirePath(BotComponent b,SquadFireRuntime r,Vector3 start,Vector3 direction,float distance)
    {
        var surfaces=new List<ShotSurface>(16);bool stable=false;
        int mask=BallisticsCalculatorConstants.StaticObjectsHitMask|(1<<LayerMask.NameToLayer("Deadbody"));
        bool complete=GeometryLane(r.Surfaces,start,direction,distance,mask,"barrel",surfaces,ref stable);
        surfaces.Sort((a,z)=>a.Distance.CompareTo(z.Distance));
        var weapon=(b.Player.HandsController as Player.FirearmController)?.Item;var ammo=GeometryAmmo(weapon);
        var f=new ShotGeometryFrame{Complete=complete,OriginClear=true,AllySafe=true,Surfaces=surfaces.ToArray(),AmmoKnown=ammo!=null,
            Power=ammo?.PenetrationPower??0,Deviation=ammo?.PenetrationPowerDiviation??0,AmmoChance=ammo?.PenetrationChanceObstacle??0,
            AmmoRicochet=ammo?.RicochetChance??0,Degradation=Singleton<GlobalConfiguration>.Instance?.Ballistic.GlobalDamageDegradationCoefficient??double.NaN};
        var verdict=ShotGeometryPolicy.Evaluate(f,new ShotGeometryConfig{AllowPenetration=true});
        // Unknown/partial geometry cannot prove an ally safe. Doors/glass follow W08's approved pass-through rules.
        return !SquadFireGeometry.CanReach(verdict)?"blocking solid":verdict.Allowed?(verdict.PassThroughSurfaces>0?"door or glass in path":"clear or penetrable path"):"uncertain cover; protect squadmate";
    }
    private bool TickSquadFire(BotState s,SquadFireFrame f)
    {bool block=s.SquadFire.Policy.Tick(f,config!.SquadFire);if(!block)RecordSquadFire(s);return block;}
    private void SampleSquadFire(BotComponent b,BotState s)
    {if(s.SquadFire.Policy.Blocking&&(!SquadFireScope(b)||Time.time-s.SquadFire.Callback>.25f))EndSquadFire(s,"squad-fire-scope-or-callback-ended");}
    private void EndSquadFire(BotState s,string reason)
    {if(!s.SquadFire.Policy.Blocking)return;s.SquadFire.Policy.End(reason);GeometryClearBuffers(s.SquadFire.Surfaces);RecordSquadFire(s,true);}
    private void SquadFireError(BotState s,Exception ex)
    {var r=s.SquadFire;if(!r.Fault)Logger.LogWarning("S08 yielded: "+ex.GetBaseException().Message);r.Fault=true;EndSquadFire(s,"squad-fire-adapter-error");}
    private void RecordSquadFire(BotState s,bool force=false)
    {
        var r=s.SquadFire;var t=r.Policy.Trace;if(t==null)return;
        string key=t.State+":"+t.Reason+":"+t.Ally+":"+t.Execution;
        if(!force&&key==r.LogKey&&(!r.Policy.Blocking||Time.time-r.LogAt<.1f))return;r.LogKey=key;r.LogAt=Time.time;
        var json=Diagnostics.Serialize(new{schema=1,session,sequence=++sequence,feature="S08",stage="evaluated",decision="SquadFire",reason=t.Reason,
            realtime=Time.realtimeSinceStartup,utc=DateTime.UtcNow.ToString("O"),configRevision=config?.Revision??0,profileId=s.ProfileId,name=s.Name,botInstance=s.Id,
            squadFire=t,squadFireConfig=config?.SquadFire});
        diagnostics?.Write(json);Api.Decisions.Publish(json,ex=>{if(!subscriberWarned){subscriberWarned=true;Logger.LogWarning(ex.Message);}});
    }
}
