using System;
using System.Collections.Generic;
using System.Linq;
using Comet.Core;
using EFT;
using HarmonyLib;
using Newtonsoft.Json;
using SAIN.Components;
using SAIN.Models.Enums;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.Decision;
using SAIN.SAINComponent.Classes.EnemyClasses;
using SAIN.SAINComponent.Classes.Mover;
using UnityEngine;
using UnityEngine.AI;

namespace Comet.Client;

internal sealed class CoverFeintRuntime
{
    internal readonly CoverFeintPolicy Policy=new();
    internal readonly Dictionary<IBotPathData,NavMeshPath> Routes=new();
    internal BotComponent? Owner;internal Enemy? Enemy;internal Player.FirearmController? Firearm;
    internal NavMeshPath? Requesting;internal Vector3 Out,From;
    internal float Next,LastSafety=-1000,NextSafety,SeenAt,LogAt=-1000,HeardAt=-1,HeardDistance;
    internal int Candidate,RequestFrame=-1,ProbeFrame=-1,JumpLeg=-1,FirstSide;
    internal bool Starting,Fault,Protected,Exposed,Coming,OwnSpeed;internal float PreviousSpeed,AppliedSpeed;
    internal string LogKey="";
    internal void Drop(){Owner=null;Enemy=null;Firearm=null;Requesting=null;Routes.Clear();RequestFrame=-1;OwnSpeed=false;}
}

public sealed partial class Plugin
{
    private static System.Reflection.FieldInfo feintBridge=null!,feintClipRelease=null!;
    private static void PatchCoverFeint(Harmony h)
    {
        var freeze=typeof(BotComponent).Assembly.GetType("SAIN.Layers.Combat.Solo.FreezeAction",true)!;
        h.Patch(AccessTools.Method(typeof(SAIN.Layers.BotAction),"OnSteeringTicked"),prefix:new HarmonyMethod(typeof(Plugin),nameof(FeintSteering)));
        h.Patch(AccessTools.Method(freeze,"Update"),prefix:new HarmonyMethod(typeof(Plugin),nameof(FeintAction)));
        h.Patch(AccessTools.Method(typeof(BotPathDataManual),"Initialize"),postfix:new HarmonyMethod(typeof(Plugin),nameof(FeintPathInitialized)));
        feintBridge=AccessTools.Field(typeof(BaseSoundPlayer),"playersBridge")??throw new MissingFieldException("M14 sound bridge");
        feintClipRelease=AccessTools.Field(typeof(BaseSoundPlayer),"_clipSourceReleaseTime")??throw new MissingFieldException("M14 native playback evidence");
        foreach(string name in new[]{"SoundAtPointEventHandler","EventReceiver"})
            h.Patch(AccessTools.Method(typeof(BaseSoundPlayer),name),prefix:new HarmonyMethod(typeof(Plugin),nameof(FeintSoundEvent)));
        h.Patch(AccessTools.Method(typeof(BaseSoundPlayer),"PlayClipInternal"),prefix:new HarmonyMethod(typeof(Plugin),nameof(FeintSoundBefore)),
            postfix:new HarmonyMethod(typeof(Plugin),nameof(FeintSoundAfter)));
    }
    private bool FeintScope(BotComponent b,BotState s)=>ContactSafety.AllowsGoal(b)&&config?.Enabled==true&&config.CoverFeint.Enabled&&NativeAdapter.IsPmcAi(b)
        &&b.BotActive&&!b.IsDead&&!b.GameEnding&&Authority&&!Ghost(s.ProfileId)&&b.SAINLayersActive;
    private static bool MagazineOperation(Player.FirearmController? f)=>f?.CurrentHandsOperation is Player.FirearmController.UtilityOperation op
        &&op._utilityOperationType==Player.FirearmController.UtilityOperation.EUtilityType.CheckMagazine;
    private static bool MagazineSoundAvailable(BotComponent b,double distance)
    {
        // Inspect only this weapon's native sound assets, once per attempted selection.
        // A suitable rolloff is a prerequisite, not proof that another actor hears through occlusion.
        if(b.Player.HandsController is not Player.FirearmController firearm)return false;
        foreach(var sound in firearm.GetComponentsInChildren<BaseSoundPlayer>())
            foreach(var element in (sound.MainSounds??Array.Empty<BaseSoundPlayer.SoundElement>()).Concat(sound.AdditionalSounds??new()))
                if(element!=null&&element.EventName.IndexOf("mag",StringComparison.OrdinalIgnoreCase)>=0&&element.Volume>0&&element.RollOff>=distance
                    &&element.SoundClips?.Any(clip=>clip!=null)==true)return true;
        return false;
    }
    private CoverFeintFrame FeintFrame(BotComponent b,BotState s)
    {
        var r=s.CoverFeint;var e=b.GoalEnemy;var seen=e?.KnownPlaces.LastSeenPlace;var mag=(b.Player.HandsController as Player.FirearmController)?.Item.GetCurrentMagazine();
        var known=r.Policy.Active?VP(r.Policy.Known):seen?.Position??b.Position;
        var f=new CoverFeintFrame{Now=Time.time,Position=DP(b.Position),Known=DP(known),Target=e?.EnemyProfileId??"",Key=e==null?"":RelocationKey(e),
            Age=r.Policy.Active?Time.time-r.SeenAt:seen?.TimeSincePositionUpdated??999,SightAge=e?.Seen==true?e.TimeSinceSeen:999,
            Visible=e?.IsVisible==true,Distance=Vector3.Distance(b.Position,known),Cover=b.Cover.CoverInUse!=null,
            Ready=VisibleAdapter.WeaponReady(b),Healthy=b.Memory.Health.HealthStatus==ETagStatus.Healthy,
            Bold=b.Info.PersonalitySettings.Rush.CanRushEnemyReloadHeal,Speed=b.Player.Velocity.magnitude,
            Grounded=b.Player.MovementContext.IsGrounded,AmmoRatio=mag?.MaxCount>0?(double)mag.Count/mag.MaxCount:0,
            Protected=r.Protected,Exposed=r.Exposed,AtGoal=Vector3.Distance(b.Position,VP(r.Policy.Goal))<=.2f,
            Fire=e==null?-1:Math.Max(e.Status.TimeLastShotAtMe,e.Status.TimeLastShotMe)};
        f.MagazineAudible=r.Policy.Active?r.Policy.Trace!.Kind=="magazine":MagazineSoundAvailable(b,f.Distance);
        var env=s.Environment.Read(Time.time,b.Position.x,b.Position.y,b.Position.z,config!.EnvironmentContext,"cover-feint");
        f.Outdoor=config.EnvironmentContext.Enabled?env.Reported=="outdoor"&&!env.Conflict:b.Player.Environment==EnvironmentType.Outdoor&&b.Player.AIData?.EnvironmentId==0;
        var heard=e?.KnownPlaces.LastHeardPlace;
        if(heard!=null){f.Heard=silentHeardAt(heard);float distance=Vector3.Distance(b.Position,heard.Position);
            if(f.Heard>r.HeardAt+.01){r.Coming=r.HeardAt>=0&&distance<r.HeardDistance-.3f;r.HeardAt=(float)f.Heard;r.HeardDistance=distance;}
            f.Coming=r.Coming&&Time.time-f.Heard<=3;}
        int count=0;if(b.Squad?.Members!=null)foreach(var mate in b.Squad.Members.Values){if(++count>16)break;
            if(mate!=null&&!ReferenceEquals(mate,b)&&!mate.IsDead&&mate.GoalEnemy?.EnemyProfileId==f.Target&&mate.GoalEnemy.IsVisible){f.Mates=true;break;}}
        if(MedicalPostureGuard(b))f.Block="medical-posture-protected";
        else if(!FeintScope(b,s))f.Block="feint-out-of-scope";
        else if(e==null||seen==null||!e.Seen||!e.EnemyKnown)f.Block="feint-no-personally-seen-contact";
        else if(r.Policy.Active&&(!ReferenceEquals(e,r.Enemy)||!ReferenceEquals(b.Player.HandsController,r.Firearm)))f.Block="feint-contact-or-weapon-changed";
        else if(b.Decision.CurrentSelfDecision!=ESelfActionType.None||b.Decision.CurrentSquadDecision!=ESquadDecision.None||FakeMedicalPriority(b)
            ||b.Grenade.GrenadeDangerPoint!=null||b.Flashed.IsFlashed||b.Suppression.IsHeavySuppressed||s.Fear.Current.Panic
            ||b.DoorOpener.Interacting||b.BotOwner.DoorOpener.Interacting||b.Player.IsInPronePose
            ||b.Player.MovementContext.PhysicalConditionIs(EPhysicalCondition.LeftLegDamaged)||b.Player.MovementContext.PhysicalConditionIs(EPhysicalCondition.RightLegDamaged))f.Block="feint-native-emergency-or-interaction";
        else if(s.Entry.Policy.Active||s.Door.Policy.Active||s.Jump.Policy.Active||s.RunBy.Policy.Active||s.StepPeek.Policy.Active||s.Fake.Policy.Active
            ||s.RoomClear.Policy.Active||s.Emergency.Policy.Active||s.Support!=null||s.Reservation.Lease!=null||s.Relocation.Policy.Active
            ||s.CornerPeek.Policy.Active||s.CornerChase.Policy.Active||s.CombatStep.Policy.Active||s.HoldAngle.Policy.Active||s.Ambush.Policy.Active)f.Block="feint-other-tactic";
        else if(r.Policy.Trace?.State!="mag-check"&&!f.Ready||b.BotOwner.WeaponManager.Grenades.ThrowindNow)f.Block="feint-hands-busy";
        if(r.Policy.Active){var path=b.Mover.ActivePath;
            f.RouteValid=!QueuedCombatMovement(b)&&(path==null||OwnFeintRoute(r,path));
            if(r.Policy.Moving&&!CornerPeek.Lane(DP(r.From),r.Policy.Goal,DP(b.Position),r.Policy.Trace!.JumpsAccepted>0))f.RouteValid=false;}
        count=0;foreach(var other in b.EnemyController.KnownEnemies){if(++count>32){f.Block="feint-threat-list-truncated";break;}
            if(other!=null&&!ReferenceEquals(other,e)&&(other.IsVisible||other.Status.ShotAtMeRecently)){f.Block="feint-other-immediate-threat";break;}}
        return f;
    }
    private bool ChooseFeint(BotComponent b,Enemy e,BotState s,ref ECombatDecision result,ref bool succeeded)
    {
        var r=s.CoverFeint;if(config==null||r.Fault)return false;
        try{
            if(!r.Policy.Active){if(Time.time<r.Next||s.Sight.Current!=null||b.Mover.ActivePath!=null||QueuedCombatMovement(b))return false;r.Next=Time.time+.4f;
                var f=FeintFrame(b,s);var old=r.Policy.Trace;
                if(!r.Policy.Begin(f,config.CoverFeint,jumpRandom.NextDouble)){if(!ReferenceEquals(old,r.Policy.Trace))RecordFeint(s,true);return false;}
                r.Starting=true;try{Release(s,"feint-start");}finally{r.Starting=false;}
                r.Owner=b;r.Enemy=e;r.Firearm=b.Player.HandsController as Player.FirearmController;r.SeenAt=Time.time-(float)f.Age;
                r.Candidate=0;r.FirstSide=0;r.JumpLeg=-1;r.LastSafety=-1000;r.NextSafety=0;
            }
            RefreshFeint(b,s,false);if(!r.Policy.Active)return false;
            r.RequestFrame=Time.frameCount;result=ECombatDecision.Freeze;succeeded=true;return true;
        }catch(Exception ex){FeintError(s,ex);return false;}
    }
    private bool FeintAccepted(BotDecisionManager manager,BotState s)
    {
        var r=s.CoverFeint;if(!r.Policy.Active)return false;
        bool ours=r.RequestFrame==Time.frameCount&&manager.CurrentCombatDecision==ECombatDecision.Freeze
            &&manager.CurrentSelfDecision==ESelfActionType.None&&manager.CurrentSquadDecision==ESquadDecision.None;
        r.RequestFrame=-1;r.Policy.Accepted(ours);if(!ours){FinishFeint(s);return false;}RecordFeint(s);return true;
    }
    private static bool FeintAction(SAIN.Layers.BotAction __instance)
    {
        var b=__instance.Bot;var p=Instance;
        if(p==null||b==null||!p.states.TryGetValue(b,out var s)||!s.CoverFeint.Policy.Active)return true;
        try{p.RefreshFeint(b,s,true);return !s.CoverFeint.Policy.Active;}catch(Exception ex){p.FeintError(s,ex);return true;}
    }
    private static bool FeintSteering(SAIN.Layers.BotAction __instance)
    {
        var p=Instance;var b=__instance.Bot;
        if(__instance.Name!="FreezeAction"||p==null||b==null||!ReferenceEquals(b.CurrentAction,__instance)||!p.states.TryGetValue(b,out var s)
            ||!s.CoverFeint.Policy.Active||!p.FeintScope(b,s)||b.GoalEnemy?.IsVisible==true)return true;
        // No suppression shot is invented by this feint. Visible contacts retain native shooting.
        b.Shoot.EndShoot();b.Steering.LookToPoint(VP(s.CoverFeint.Policy.Known)+Vector3.up*1.3f);return false;
    }
    private void SampleFeint(BotComponent b,BotState s)
    {if(s.CoverFeint.Fault)return;if(!FeintScope(b,s)){EndFeint(s,"feint-out-of-scope",true);return;}if(s.CoverFeint.Policy.Active)RefreshFeint(b,s,false);}
    private void RefreshFeint(BotComponent b,BotState s,bool execute)
    {
        var r=s.CoverFeint;var p=r.Policy;if(!p.Active||config==null)return;
        var f=FeintFrame(b,s);
        if(f.Block.Length>0||!f.RouteValid){EndFeint(s,f.Block.Length>0?f.Block:"feint-native-route-taken-over");return;}
        if(Time.time>=r.NextSafety){
            if(doorBudget.TryBegin(Time.frameCount)){doorClock.Restart();try{
                r.Protected=CombatProtection(b.Position,VP(p.Known))==7;r.Exposed=CornerOpen(b,b.Position,VP(p.Known));
                bool safe=CombatProtection(VP(p.Home),VP(p.Known))==7&&CornerOtherThreats(b,VP(p.Home),p.Moving?VP(p.Goal):b.Position).Length==0;
                if(p.Moving){safe&=RunVolume(b,VP(p.Home),r.Out).Length==0;
                    var active=b.Mover.ActivePath;
                    if(active!=null&&OwnFeintRoute(r,active)&&Vector3.Distance(b.Position,VP(p.Goal))>.2f)
                        safe&=active.NavMeshPath.status==NavMeshPathStatus.PathComplete&&FeintRouteClear(b,RemainingRelocationRoute(b,active));}
                if(!safe){EndFeint(s,"feint-cover-or-corridor-unsafe");return;}r.LastSafety=Time.time;r.NextSafety=Time.time+.1f;
            }finally{doorClock.Stop();doorBudget.Charge(doorClock.Elapsed.TotalMilliseconds);}}
            else if(Time.time-r.LastSafety>.2f){EndFeint(s,"feint-safety-budget-expired");return;}else return;
        }
        f.Protected=r.Protected;f.Exposed=r.Exposed;
        if(!p.Observe(f,config.CoverFeint)){FinishFeint(s);return;}
        if(p.Trace!.State=="mag-check")p.MagazineObserved(MagazineOperation(r.Firearm),f.Ready,Time.time);
        if(!p.Active){FinishFeint(s);return;}
        // Native Freeze owns steering and shooting. Only its accepted action callback executes the tactic.
        if(execute&&p.Trace.Accepted&&b.Decision.CurrentCombatDecision==ECombatDecision.Freeze){
            if(p.Trace.State=="planning"&&r.ProbeFrame!=Time.frameCount&&doorBudget.TryBegin(Time.frameCount)){
                r.ProbeFrame=Time.frameCount;doorClock.Restart();try{PlanFeint(b,s);}finally{doorClock.Stop();doorBudget.Charge(doorClock.Elapsed.TotalMilliseconds);}}
            if(p.WantsRoute&&doorBudget.TryBegin(Time.frameCount)){doorClock.Restart();try{MoveFeint(b,s);}finally{doorClock.Stop();doorBudget.Charge(doorClock.Elapsed.TotalMilliseconds);}}
            if(p.Trace.State=="mag-pending"){
                if(!r.Protected||!f.Ready||r.Firearm==null||!MagazineSoundAvailable(b,f.Distance)){EndFeint(s,"feint-magazine-no-longer-safe");return;}
                bool accepted=r.Firearm.CheckAmmo();p.MagazineResult(accepted,MagazineOperation(r.Firearm),Time.time);
            }
            if(p.Moving&&config.CoverFeint.Jump&&p.Trace.CompletedPeeks==0){int leg=p.Trace.State=="out"?0:1;
                var dir=VP(p.Goal)-b.Position;dir.y=0;var v=b.Player.Velocity;v.y=0;
                if(r.JumpLeg!=leg&&dir.magnitude>.45f&&Vector3.Dot(v,dir.normalized)>=1.2f&&b.Player.MovementContext.IsGrounded
                    &&b.Player.MovementContext.CanJump&&b.Player.Physical.Stamina.NormalValue*100>=30&&doorBudget.TryBegin(Time.frameCount)){
                    r.JumpLeg=leg;doorClock.Restart();try{if(RunVolume(b,r.From,VP(p.Goal),true).Length==0)
                        p.Jump(ControlledJump(b,s,"M14",leg==0?"bait-out":"bait-back",dir,VP(p.Goal),1.2,30,true));}
                    finally{doorClock.Stop();doorBudget.Charge(doorClock.Elapsed.TotalMilliseconds);}}
            }
            if(!p.Moving&&!p.WantsRoute){StopFeintRoutes(r);if(b.Mover.ActivePath==null&&!QueuedCombatMovement(b))b.Player.Move(Vector2.zero);}
        }
        RecordFeint(s);
    }
    private void PlanFeint(BotComponent b,BotState s)
    {
        var r=s.CoverFeint;var p=r.Policy;var c=config!.CoverFeint;
        if(!r.Protected||b.Player.Velocity.magnitude>.35f||!b.Player.MovementContext.IsGrounded)return;
        if(p.Trace!.Kind=="magazine"){p.Plan(p.Home,Time.time);return;}
        if(r.Candidate>=12){EndFeint(s,"feint-no-safe-peek");return;}
        int i=r.Candidate++;var forward=VP(p.Known)-VP(p.Home);forward.y=0;
        int side=i%2==0?1:-1;if(p.Trace.CompletedPeeks>0&&r.FirstSide!=0)side*= -r.FirstSide;
        var direction=Vector3.Cross(Vector3.up,forward.normalized)*side;
        if(i>=6)direction=(direction+forward.normalized*.25f).normalized;
        float distance=(float)(c.MinStep+(c.MaxStep-c.MinStep)*((i%6)/2)/2d);
        var raw=VP(p.Home)+direction*distance;
        if(!CornerRoute(b,b.Position,raw,c.MaxStep+.2,out var goal,out var route,out var why)||!FeintRouteClear(b,route.corners)||!FeintReturnClear(b,goal,VP(p.Home))||!CornerOpen(b,goal,VP(p.Known))
            ||CornerOtherThreats(b,VP(p.Home),goal).Length>0){p.Alternative("Peek "+i+": "+(why.Length>0?why:"blocked angle or other threat"));return;}
        r.Out=goal;if(p.Trace.CompletedPeeks==0)r.FirstSide=side;r.Candidate=0;p.Plan(DP(goal),Time.time);
    }
    private bool FeintRouteClear(BotComponent b,Vector3[]? corners)
    {
        if(corners==null||corners.Length<2||corners.Length>5)return false;
        for(int i=1;i<corners.Length;i++)if(RunVolume(b,corners[i-1],corners[i]).Length>0||CornerOtherThreats(b,corners[i-1],corners[i]).Length>0)return false;
        return true;
    }
    private bool FeintReturnClear(BotComponent b,Vector3 from,Vector3 to)
    {var path=new NavMeshPath();NavMesh.CalculatePath(from,to,NavMesh.AllAreas,path);return path.status==NavMeshPathStatus.PathComplete&&FeintRouteClear(b,path.corners);}
    private void MoveFeint(BotComponent b,BotState s)
    {
        var r=s.CoverFeint;var p=r.Policy;var goal=VP(p.Goal);
        // Do not ask NavMesh to start from an airborne coordinate: finish the native flight first.
        if(!b.Player.MovementContext.IsGrounded)return;
        if(!CornerRoute(b,b.Position,goal,config!.CoverFeint.MaxStep+.25,out var end,out var path,out var why)||Vector3.Distance(end,goal)>.1f||!FeintRouteClear(b,path.corners))
        {EndFeint(s,why.Length>0?why:"feint-return-route-changed");return;}
        StopFeintRoutes(r);if(b.Mover.ActivePath!=null||QueuedCombatMovement(b)){EndFeint(s,"feint-route-taken-over");return;}
        r.From=b.Position;r.Requesting=path;bool accepted;
        try{accepted=b.Mover.WalkToPointByWay(path,true,.1f,false);}finally{r.Requesting=null;}
        if(!accepted||!OwnFeintRoute(r,b.Mover.ActivePath)){EndFeint(s,"feint-movement-not-accepted");return;}
        if(!r.OwnSpeed){r.PreviousSpeed=(float)combatNativeSpeed.GetValue(b.PlayerComponent.CharacterController);r.AppliedSpeed=Mathf.Clamp01(b.Info.FileSettings.Move.STRAFE_SPEED);r.OwnSpeed=true;}
        b.Mover.SetTargetMoveSpeed(r.AppliedSpeed);p.RequestMove(Time.time);
    }
    private static void FeintPathInitialized(BotPathDataManual __instance,NavMeshPath path)
    {if(__instance.PathFinder is SAINMoverClass m&&Instance?.states.TryGetValue(m.Bot,out var s)==true){var r=s.CoverFeint;r.Routes.Remove(__instance);
        if(r.Policy.Active&&ReferenceEquals(path,r.Requesting)&&r.Routes.Count<2)r.Routes[__instance]=path;}}
    private static bool OwnFeintRoute(CoverFeintRuntime r,IBotPathData? path)=>path!=null&&r.Routes.TryGetValue(path,out var mesh)&&ReferenceEquals(mesh,path.NavMeshPath);
    private static void StopFeintRoutes(CoverFeintRuntime r)
    {
        if(r.Owner==null)return;foreach(var path in r.Routes.Keys.ToArray())if(OwnFeintRoute(r,path)){
            if(ReferenceEquals(r.Owner.Mover.ActivePath,path)&&path is BotPathDataManual manual)combatStopPath.Invoke(manual,new object[]{false,"COMET cover feint"});
            else if(path.Status==EBotMoveStatus.ReadyToMove){relocationPathStatus.Invoke(path,new object[]{EBotMoveStatus.Complete});relocationClearPath.Invoke(path,null);}}
        r.Routes.Clear();
    }
    private static BotState? FeintSoundOwner(BaseSoundPlayer sound)
    {
        if((feintBridge.GetValue(sound) as BaseSoundPlayer.IObserverToPlayerBridge)?.iPlayer is not Player player)return null;
        var b=player.AIData?.BotOwner?.GetComponent<BotComponent>();var p=Instance;
        return b!=null&&p?.states.TryGetValue(b,out var s)==true&&p.FeintScope(b,s)&&s.CoverFeint.Policy.Active
            &&s.CoverFeint.Policy.Trace?.State=="mag-check"&&ReferenceEquals(player.HandsController,s.CoverFeint.Firearm)&&MagazineOperation(s.CoverFeint.Firearm)?s:null;
    }
    private static void FeintSoundEvent(BaseSoundPlayer __instance,string __0)
    {try{var s=FeintSoundOwner(__instance);s?.CoverFeint.Policy.Sound(__0,false);}catch{/* Observation must never suppress native audio. */}}
    private static void FeintSoundBefore(BaseSoundPlayer __instance,out double __state)
    {__state=-1;try{if(FeintSoundOwner(__instance)!=null)__state=(double)feintClipRelease.GetValue(__instance);}catch{}}
    private static void FeintSoundAfter(BaseSoundPlayer __instance,double __state,bool __runOriginal)
    {try{if(__runOriginal&&__state>=0&&(double)feintClipRelease.GetValue(__instance)>__state)FeintSoundOwner(__instance)?.CoverFeint.Policy.Sound("",true);}catch{}}
    private void EndFeint(BotState s,string why,bool clear=false)
    {var r=s.CoverFeint;if(r.Starting)return;bool active=r.Policy.Active;if(clear)r.Policy.Clear(why);else if(active)r.Policy.End(why);if(active)FinishFeint(s);else if(clear)r.Drop();}
    private void FinishFeint(BotState s){var r=s.CoverFeint;try{if(!(s.MedicalPosture.Policy.Blocking&&r.Owner!=null&&!r.Owner.Player.MovementContext.IsGrounded))StopFeintRoutes(r);
        if(r.OwnSpeed&&r.Owner!=null&&!r.Owner.IsDead&&Math.Abs((float)combatNativeSpeed.GetValue(r.Owner.PlayerComponent.CharacterController)-r.AppliedSpeed)<.001f)r.Owner.Mover.SetTargetMoveSpeed(r.PreviousSpeed);
        RecordFeint(s,true);}finally{r.Drop();}}
    private void FeintError(BotState s,Exception ex){if(s.CoverFeint.Fault)return;s.CoverFeint.Fault=true;EndFeint(s,"feint-adapter-error",true);Logger.LogWarning("M14 yielded: "+ex.GetBaseException().Message);}
    private void RecordFeint(BotState s,bool force=false)
    {
        var r=s.CoverFeint;var t=r.Policy.Trace;if(t==null)return;string key=t.Episode+":"+t.State+":"+t.Reason+":"+t.Accepted+":"+t.SoundEvents;
        if(!force&&key==r.LogKey&&(!r.Policy.Active||Time.time-r.LogAt<.1f))return;r.LogKey=key;r.LogAt=Time.time;
        var json=Diagnostics.Serialize(new{schema=1,session,sequence=++sequence,feature="M14",stage="evaluated",decision="CoverFeint",reason=t.Reason,
            realtime=Time.realtimeSinceStartup,utc=DateTime.UtcNow.ToString("O"),configRevision=config?.Revision??0,profileId=s.ProfileId,name=s.Name,botInstance=s.Id,
            coverFeint=t,coverFeintConfig=config?.CoverFeint});
        diagnostics?.Write(json);Api.Decisions.Publish(json,ex=>{if(!subscriberWarned){subscriberWarned=true;Logger.LogWarning(ex.Message);}});
    }
}
