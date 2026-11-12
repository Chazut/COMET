using System;
using System.Collections.Generic;
using Comet.Core;
using EFT;
using HarmonyLib;
using Newtonsoft.Json;
using SAIN.Components;
using SAIN.Models.Enums;
using SAIN.Preset.Shared.Enums;
using SAIN.Preset.Shared.GlobalSettings;
using SAIN.SAINComponent.Classes.Mover;
using UnityEngine;
using UnityEngine.AI;

namespace Comet.Client;

internal sealed class AngleWatchRuntime
{
    internal readonly AngleWatch Policy=new();
    internal object? Enemy;
    internal Vector3 Seen,ProbePosition,ProbeDirection,ProbeKnown;
    internal float SeenAt=-1000,GeometryAt=-1000,NextGeometry,CallbackAt,LogAt=-1000,SampleAt=-1000;
    internal bool Wall,Fault,OwnLean;
    internal LeanClass? LeanOwner;
    internal LeanSetting Applied;
    internal AngleCandidate[] Candidates=Array.Empty<AngleCandidate>();
    internal string LogKey="",ProbeSource="";
}
public sealed partial class Plugin
{
    private struct AngleRestore {internal bool Changed;internal Vector3 Original;}
    private static void PatchAngleWatch(Harmony h)
    {
        var method=AccessTools.Method(typeof(SAINSteeringClass),"TickPlayerSteering")??throw new MissingMethodException("M07 native steering tick");
        if(AccessTools.Field(typeof(SAINSteeringClass),"_targetLookDirection")?.FieldType!=typeof(Vector3))throw new MissingFieldException("M07 native gaze direction");
        h.Patch(method,prefix:new HarmonyMethod(typeof(Plugin),nameof(AngleSteering)),postfix:new HarmonyMethod(typeof(Plugin),nameof(RestoreAngleSteering)));
    }
    private void SampleAngle(BotComponent b,BotState s)
    {
        var r=s.AngleWatch;
        if(r.Fault)return;
        if(config?.Enabled!=true||!config.AngleWatch.Enabled||b==null||b.IsDead||!b.BotActive||!Authority||Ghost(s.ProfileId)||!NativeAdapter.IsSupportedAi(b))
        {r.Enemy=null;r.SeenAt=-1000;EndAngle(s,"angle-ineligible");return;}
        var e=b.GoalEnemy;
        if(!SameTacticalContact(r.Enemy,e)){EndAngle(s,"angle-contact-changed");r.Enemy=e;r.SeenAt=r.GeometryAt=-1000;r.NextGeometry=0;r.Candidates=Array.Empty<AngleCandidate>();}
        if(e?.IsVisible==true&&Time.time-r.SampleAt>=.1f&&e.KnownPlaces.LastSeenPlace is {} seen)
        {r.Seen=seen.Position;r.SeenAt=Time.time;r.SampleAt=Time.time;}
        if(r.Policy.Active&&Time.time-r.CallbackAt>.4f)EndAngle(s,"angle-steering-callback-expired");
    }
    private AngleWatchFrame AngleFrame(BotComponent b,BotState s,Vector3 native)
    {
        var r=s.AngleWatch;var c=config!.AngleWatch;var e=b.GoalEnemy;var combat=CombatStepFrame(b,s);
        var f=new AngleWatchFrame {Now=Time.time,Target=e?.EnemyProfileId??"",Action=b.Decision.CurrentCombatDecision.ToString(),
            Position=DP(b.Position),Origin=DP(b.Transform.WeaponRoot),NativeDirection=DP(native),
            ObservedDirection=DP(b.PlayerComponent.CharacterController.CurrentControlLookDirection),Moving=b.Player.Velocity.magnitude>=.5f};
        if(r.SeenAt>=0&&Time.time-r.SeenAt<c.VisionAge){f.Source="personal-vision";f.Known=DP(r.Seen);f.ObservedAt=r.SeenAt;}
        // LastHeardPlace is populated by UpdatePersonalHeardPosition, separately from squad reports.
        var heard=e?.KnownPlaces.LastHeardPlace;
        if(heard!=null&&heard.TimeSincePositionUpdated>=0&&heard.TimeSincePositionUpdated<c.HearingAge
            &&(f.Source.Length==0||Time.time-heard.TimeSincePositionUpdated>f.ObservedAt))
        {f.Source="personal-hearing";f.Known=DP(heard.Position);f.ObservedAt=Time.time-heard.TimeSincePositionUpdated;}
        if(!combat.Enabled||!combat.Pmc||!combat.Authority||combat.Ghost||!combat.Active)f.Block="angle-ineligible";
        else if(e==null||e.IsVisible||e.InLineOfSight)f.Block="angle-visible-or-no-enemy";
        else if(combat.Urgent||combat.Busy||!combat.Grounded||!combat.Ready||b.Medical.TimeSinceShot<.5)f.Block="angle-urgent-or-busy";
        else if(s.CornerChase.Policy.Active||s.CornerPrefire.Policy.Active||s.CornerPeek.Policy.Active||s.Sight.Current!=null
            ||s.Ambush.Policy.Active||s.RoomClear.Policy.Active||s.Entry.Policy.Active)f.Block="angle-preserve-tactical-aim";
        else if(b.ManualShoot.Shooting||b.ManualShoot.Reason!=EShootReason.None||b.Aim.AimStatus!=AimStatus.NoTarget)f.Block="angle-native-fire-or-aim";
        else if(f.Action!="Search"&&f.Action!="RushEnemy"&&f.Action!="HoldInCover"&&f.Action!="StandAndShoot")f.Block="angle-native-action-priority";
        else if(b.CurrentAction?.GetType().Assembly!=typeof(BotComponent).Assembly
            ||b.CurrentAction.GetType().FullName!="SAIN.Layers.Combat.Solo."+(f.Action=="HoldInCover"?"StandAndShoot":f.Action)+"Action")f.Block="angle-native-action-owner";
        int count=0;
        if(f.Block.Length==0)foreach(var other in b.EnemyController.KnownEnemies) {
            if(++count>32||other!=null&&!SameTacticalContact(other,e)&&(other.IsVisible||other.Status.ShotAtMeRecently)){f.Block="angle-other-threat";break;}
        }
        return f;
    }
    private static void AngleSteering(SAINSteeringClass __instance,ref Vector3 ____targetLookDirection,out AngleRestore __state)
    {
        __state=default;var p=Instance;var b=__instance.Bot;
        if(p?.config==null||!p.states.TryGetValue(b,out var s) )return;
        try {
            if(p.WeaveGaze(b,s,out var weaveDirection)) {
                __state=new(){Changed=true,Original=____targetLookDirection};____targetLookDirection=weaveDirection;return;
            }
        }catch(Exception ex){p.WeaveError(s,ex);}
        try {
            if(p.CoverTravelGaze(b,s,out var travelDirection)) {
                __state=new(){Changed=true,Original=____targetLookDirection};____targetLookDirection=travelDirection;return;
            }
        }catch(Exception ex){p.CoverTravelError(s,ex);}
        try {
            if(p.HoldAngleGaze(b,s,out var holdDirection)) {
                __state=new(){Changed=true,Original=____targetLookDirection};____targetLookDirection=holdDirection;return;
            }
        }catch(Exception ex){p.HoldAngleError(s,ex);}
        if(s.AngleWatch.Fault)return;
        var r=s.AngleWatch;
        try {
            p.SampleAngle(b,s);r.CallbackAt=Time.time;
            var f=p.AngleFrame(b,s,____targetLookDirection);
            if(f.Block.Length>0||f.Source.Length==0||!p.config.AngleWatch.Enabled){p.EndAngle(s,f.Block.Length>0?f.Block:"angle-no-evidence-or-disabled");return;}
            p.AngleGeometry(b,s,f);
            if(!r.Policy.Tick(f,p.config.AngleWatch)){p.ReleaseAngleLean(s);p.RecordAngle(s);return;}
            // Only this invocation uses the prepared direction. Do not leave a stale gaze in SAIN's backing field.
            __state=new(){Changed=true,Original=____targetLookDirection};
            ____targetLookDirection=(VP(r.Policy.Aim)-b.Transform.WeaponRoot).normalized;
            if(r.Policy.Trace!.Kind=="corner")b.Mover.ActivePath?.RequestEndSprint(ESprintUrgency.None,"COMET prepare nearby known corner");
            if(r.Policy.Side==0)p.ReleaseAngleLean(s);
            p.RecordAngle(s);
        }catch(Exception ex){p.AngleError(s,ex);}
    }
    private static void RestoreAngleSteering(ref Vector3 ____targetLookDirection,AngleRestore __state)
    {if(__state.Changed)____targetLookDirection=__state.Original;}
    private void AngleGeometry(BotComponent b,BotState s,AngleWatchFrame f)
    {
        var r=s.AngleWatch;var c=config!.AngleWatch;var native=VP(f.NativeDirection);
        bool moved=Vector3.Distance(b.Position,r.ProbePosition)>.75f||Vector3.Angle(native,r.ProbeDirection)>25
            ||Vector3.Distance(VP(f.Known),r.ProbeKnown)>.3f||r.ProbeSource!=f.Source;
        if(moved||Time.time-r.GeometryAt>=c.Refresh) {
            if(moved)r.GeometryAt=-1000;
            if(Time.time>=r.NextGeometry&&doorBudget.TryBegin(Time.frameCount,b)) {
                r.NextGeometry=Time.time+(float)c.Refresh;
                var watch=System.Diagnostics.Stopwatch.StartNew();
                try {
                    var options=new List<AngleCandidate>(8);var root=b.Transform.WeaponRoot;var eyes=b.Player.PlayerBones.Head.position;
                    var muzzle=b.Transform.WeaponData.FirePort;var known=VP(f.Known);float height=Mathf.Clamp(root.y-b.Position.y,.6f,1.7f);
                    float wall=(float)(f.Moving?c.MovingWall:c.StillWall);
                    r.Wall=!AngleLane(eyes,eyes+native.normalized*wall,.03f)||!AngleLane(muzzle,muzzle+native.normalized*wall,.04f);
                    // Route is calculated to frozen personal evidence, never enemy.VisiblePathPoint or a hidden transform.
                    var route=new NavMeshPath();Vector3 anchor=known+Vector3.up*height,lip=anchor;bool corner=false,stairs=false;int side=0;
                    if(Vector3.Distance(b.Position,known)<=60&&NavMesh.SamplePosition(known,out var end,.5f,NavMesh.AllAreas)
                        &&NavMesh.CalculatePath(b.Position,end.position,NavMesh.AllAreas,route)&&route.status==NavMeshPathStatus.PathComplete) {
                        var points=route.corners;
                        if(points.Length>=2) {
                            stairs=Math.Abs(points[1].y-b.Position.y)>.7&&Vector3.Distance(b.Position,points[1])<=c.CornerRange;
                            if(points.Length>=3&&Vector3.Distance(b.Position,points[1])>=.6f&&Vector3.Distance(b.Position,points[1])<=c.CornerRange) {
                                var incoming=points[1]-b.Position;var outgoing=points[2]-points[1];
                                float turn=Vector3.Angle(new(incoming.x,0,incoming.z),new(outgoing.x,0,outgoing.z));
                                if(turn>=20&&turn<=150&&outgoing.magnitude>.5f) {
                                    anchor=points[1]+Vector3.up*height+outgoing.normalized*Mathf.Min(.6f,outgoing.magnitude);corner=true;
                                    lip=points[1]+Vector3.up*height-incoming.normalized*.2f;
                                    side=Vector3.Dot(Vector3.Cross(Vector3.up,incoming),outgoing)<0?-1:1;
                                }
                            }
                            if(stairs&&!corner)anchor=points[1]+Vector3.up*height;
                        }
                    }
                    bool elevated=Math.Abs(known.y-b.Position.y)>.7||stairs;
                    void Add(string id,string kind,Vector3 point,int lean=0) {
                        var delta=point-root;
                        if(!elevated)point=root+VP(AngleWatch.ClampPitch(DP(delta),c.PitchLimit));
                        bool safe=AngleLane(eyes,point,.03f)&&AngleLane(muzzle,point,.04f);
                        options.Add(new(){Id=id,Kind=kind,Aim=DP(point),Clear=safe,Elevation=elevated,Side=lean,
                            LeanSafe=lean!=0&&safe&&c.SafeLean&&LeanSweep(eyes,eyes+b.Player.Transform.right*(lean*.3f))});
                    }
                    if(corner){Add("near-corner","corner",anchor,side);Add("corner-lip","corner",lip,side);}
                    if(c.WallCorrection&&r.Wall) {
                        // Search only around the evidence-bearing approach, not arbitrary open directions behind the bot.
                        var dir=anchor-root;if(dir.magnitude>.5f) {
                            foreach(float yaw in new[]{0f,20f,-20f,40f,-40f}) {
                                var point=root+(Quaternion.AngleAxis(yaw,Vector3.up)*dir.normalized)*Mathf.Min(dir.magnitude,3.5f);
                                Add("opening-"+yaw,"wall",point);
                            }
                        }
                    }
                    if(!elevated&&Math.Abs(AngleWatch.Pitch(f.NativeDirection))>c.PitchLimit
                        &&Vector3.Angle(new(native.x,0,native.z),new(known.x-root.x,0,known.z-root.z))<=60)
                        Add("vertical-guard","pitch",root+VP(AngleWatch.ClampPitch(DP(native),c.PitchLimit)).normalized*3);
                    r.Candidates=options.ToArray();r.GeometryAt=Time.time;r.ProbePosition=b.Position;r.ProbeDirection=native;r.ProbeKnown=known;r.ProbeSource=f.Source;
                }finally{watch.Stop();doorBudget.Charge(watch.Elapsed.TotalMilliseconds);}
            }
        }
        f.GeometryAt=r.GeometryAt;f.Candidates=r.Candidates;f.WallBlocked=r.Wall;
    }
    private static bool AngleLane(Vector3 origin,Vector3 point,float radius)
    {
        var delta=point-origin;if(!CornerChase.Point(DP(origin))||!CornerChase.Point(DP(point))||delta.magnitude<.1f)return false;
        int mask=LayersMaskController.HighPolyWithTerrainMask;
        return !Physics.CheckSphere(origin,radius,mask,QueryTriggerInteraction.Ignore)
            &&!Physics.SphereCast(origin,radius,delta.normalized,out _,delta.magnitude,mask,QueryTriggerInteraction.Ignore);
    }
    private bool WatchLean(LeanClass lean,BotState s)
    {
        var r=s.AngleWatch;
        if(!r.Policy.Active||r.Policy.Side==0)return false;
        var f=AngleFrame(lean.Bot,s,lean.Bot.LookDirection);
        if(f.Block.Length>0||Time.time-r.CallbackAt>.2f||Time.time-r.GeometryAt>config!.AngleWatch.Refresh+.1
            ||lean.IsHoldingLean||lean.Bot.Cover.CoverInUse!=null||!lean.Bot.Info.FileSettings.Move.LEAN_TOGGLE||!SainPresetAccess.AllowsLean)
        {ReleaseAngleLean(s);return false;}
        r.Applied=r.Policy.Side<0?LeanSetting.Left:LeanSetting.Right;r.OwnLean=true;r.LeanOwner=lean;lean.FastLean(r.Applied);
        r.Policy.Trace!.LeanRequested=true;return true;
    }
    private void ReleaseAngleLean(BotState s)
    {
        var r=s.AngleWatch;var lean=r.LeanOwner;
        try{if(r.OwnLean&&lean!=null&&Authority&&!lean.IsHoldingLean&&lean.Bot.Cover.CoverInUse==null&&lean.LeanDirection==r.Applied)lean.FastLean(LeanSetting.None);}
        finally{r.OwnLean=false;r.LeanOwner=null;}
    }
    private void EndAngle(BotState s,string reason){s.AngleWatch.Policy.End(reason);ReleaseAngleLean(s);RecordAngle(s);}
    private void AngleError(BotState s,Exception ex)
    {
        s.AngleWatch.Fault=true;s.AngleWatch.Enemy=null;s.AngleWatch.Candidates=Array.Empty<AngleCandidate>();
        try{EndAngle(s,"angle-adapter-error");}finally{Logger.LogWarning("M07 yielded: "+ex.GetBaseException().Message);}
    }
    private void RecordAngle(BotState s)
    {
        var r=s.AngleWatch;var t=r.Policy.Trace;if(t==null)return;t.LeanRequested=r.OwnLean;
        string key=t.Episode+":"+t.State+":"+t.Reason+":"+t.Candidate+":"+t.Aligned;
        if(key==r.LogKey&&(!r.Policy.Active||Time.time-r.LogAt<.1f))return;r.LogKey=key;r.LogAt=Time.time;
        var json=Diagnostics.Serialize(new{schema=1,session,sequence=++sequence,feature="M07",stage="evaluated",decision="AngleWatch",reason=t.Reason,
            realtime=Time.realtimeSinceStartup,utc=DateTime.UtcNow.ToString("O"),configRevision=config?.Revision??0,profileId=s.ProfileId,name=s.Name,botInstance=s.Id,
            angleWatch=t,angleWatchConfig=config?.AngleWatch});
        diagnostics?.Write(json);Api.Decisions.Publish(json,ex=>{if(!subscriberWarned){subscriberWarned=true;Logger.LogWarning(ex.Message);}});
    }
}
