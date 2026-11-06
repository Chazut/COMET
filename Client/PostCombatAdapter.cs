using System;
using System.Linq;
using Comet.Core;
using DrakiaXYZ.BigBrain.Brains;
using EFT;
using HarmonyLib;
using Newtonsoft.Json;
using SAIN;
using SAIN.Components;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.Decision;
using SAIN.SAINComponent.Classes.Mover;
using UnityEngine;
using UnityEngine.AI;
namespace Comet.Client;

internal sealed class PostCombatRuntime
{
    internal readonly PostCombat Policy=new();
    internal float Next,LogAt,ContactAt,MedicalNext,MoveAt,ProgressAt,BestDistance;
    internal Vector3? Threat;
    internal string Contact="",Key="",RouteStage="";
    internal IBotPathData? Path;
    internal Vector3 Goal,ProtectedAt;
    internal int CoverCandidate,MedicalFailures;
    internal bool Protected,Layer,Fault,MedicalOwner,MedicalUseful,MedicalUrgent,MedicalDisabled;
}
public sealed partial class Plugin
{
    partial void ObserveControlRelease(BotState s,string reason);
    private static void PatchPostCombat(Harmony h)
    {
        h.Patch(AccessTools.Method(typeof(BotDecisionManager),nameof(BotDecisionManager.ManualUpdate)),
            postfix:new HarmonyMethod(typeof(Plugin),nameof(ObservePostCombat)));
        BrainManager.AddCustomLayer(typeof(PostCombatLayer),new(){"PMC","PmcUsec","PmcBear","Assault","ExUsec","Knight","BigPipe","BirdEye"},21);
    }
    private bool PostScope(BotComponent b)=>config?.Enabled==true&&config.PostCombat.Enabled&&world!=null&&Authority
        &&b!=null&&NativeAdapter.IsPmcAi(b)&&b.BotActive&&!b.IsDead&&!b.GameEnding&&!Ghost(b.ProfileId);
    private bool PostCombatMedicalScope(BotComponent b)=>states.TryGetValue(b,out var s)
        &&(s.PostCombat.Layer&&s.PostCombat.Policy.Active||s.PostCombat.MedicalOwner&&(s.Medical.Policy.Active||s.Medical.Interruption.Active));
    private bool PostSurgeryTooSoon(BotComponent b,BotState s)=>PostCombatMedicalScope(b)&&Time.time-s.PostCombat.Policy.LastFight<config!.MedicalChoice.SurgeryQuiet;
    private bool PostReachedCover(BotComponent b,BotState s)=>s.PostCombat.Layer&&s.PostCombat.Protected&&Vector3.Distance(b.Position,s.PostCombat.ProtectedAt)<1.5f;
    private static void ObservePostCombat(BotDecisionManager __instance)
    {
        var p=Instance;var b=__instance.Bot;if(p==null)return;
        try
        {
            if(!p.PostScope(b)){if(p.states.TryGetValue(b,out var old))p.EndPostCombat(b,old,"post-combat-out-of-scope",true);return;}
            var e=b.GoalEnemy;
            // Personal visible contact / actually shooting / taking fire, not merely a search layer.
            bool fight=b.BotOwner.Memory.IsUnderFire||b.BotOwner.ShootData.Shooting||e?.IsVisible==true;
            if(!p.states.TryGetValue(b,out var s))
            {if(!fight||p.states.Count>=512)return;s=new BotState(b,p.config!,p.session);p.states.Add(b,s);}
            if(s.PostCombat.Fault)return;
            if(fight)
            {
                if(s.PostCombat.Policy.Active)p.EndPostCombat(b,s,"post-combat-fight-resumed",false);
                s.PostCombat.Policy.ObserveFight(Time.time);
                var place=e?.KnownPlaces.LastSeenPlace;var heard=e?.KnownPlaces.LastHeardPlace;
                if(heard!=null&&(place==null||heard.TimeSincePositionUpdated<place.TimeSincePositionUpdated))place=heard;
                if(place!=null&&ValidPoint(place.Position)&&place.TimeSincePositionUpdated>=0)
                {s.PostCombat.Threat=place.Position;s.PostCombat.Contact=e!.EnemyProfileId;s.PostCombat.ContactAt=Time.time-place.TimeSincePositionUpdated;}
            }
        }
        catch(Exception ex){if(p.states.TryGetValue(b,out var s))p.PostError(b,s,ex);}
    }
    private bool PostDanger(BotComponent b)
    {
        if(b.BotOwner.Memory.IsUnderFire||b.BotOwner.ShootData.Shooting||b.Grenade.GrenadeDangerPoint!=null||b.Flashed.IsFlashed)return true;
        if(b.GoalEnemy?.EnemyKnown==true)return true;
        int count=0;foreach(var e in b.EnemyController.KnownEnemies)
        {if(++count>16)return true;if(e?.EnemyKnown==true)return true;}
        return false;
    }
    internal static bool PostCombatActive(BotOwner owner)
    {
        var p=Instance;if(p==null||!SAINEnableClass.GetSAIN(owner.ProfileId,out var b)||b==null||!p.states.TryGetValue(b,out var s))return false;
        try{return p.RefreshPostCombat(b,s);}catch(Exception ex){p.PostError(b,s,ex);return false;}
    }
    private bool RefreshPostCombat(BotComponent b,BotState s)
    {
        var r=s.PostCombat;
        if(r.Fault||!PostScope(b)){EndPostCombat(b,s,"post-combat-out-of-scope",true);return false;}
        if(!r.Policy.WantsRecovery(Time.time))return false;
        bool danger=PostDanger(b);
        bool priority=b.Decision.CurrentCombatDecision!=ECombatDecision.None||b.Decision.CurrentSquadDecision!=ESquadDecision.None
            ||b.Decision.CurrentSelfDecision!=ESelfActionType.None||b.BotOwner.DoorOpener.Interacting;
        if(danger||priority)
        {if(r.Policy.Active)EndPostCombat(b,s,danger?"post-combat-threat-returned":"post-combat-native-priority",false);return false;}
        if(Time.time<r.Next)return r.Policy.Active;
        r.Next=Time.time+(float)config!.PostCombat.Interval;
        if(!r.Policy.Active)
        {
            // A previous episode's reached cover or failed supply scan is not evidence for this fight.
            // Reset before building the frame: otherwise Tick can mark cover complete using stale protection.
            r.Protected=b.Cover.CoverInUse?.BotInThisCover==true;r.ProtectedAt=b.Position;r.CoverCandidate=0;
            r.MedicalFailures=0;r.MedicalDisabled=false;r.MedicalUseful=false;r.MedicalUrgent=false;r.MedicalNext=0;
        }
        InspectPostMedical(b,s);
        var w=(b.Player.HandsController as Player.FirearmController)?.Item;
        var mag=w?.GetCurrentMagazine();
        int capacity=mag?.MaxCount??w?.Chambers.Length??0;
        double ammo=capacity>0?100d*(mag?.Count??w?.ChamberAmmoCount??0)/capacity:100;
        var leader=b.Squad.LeaderComponent;
        double distance=r.Threat.HasValue?Vector3.Distance(b.Position,r.Threat.Value):0;
        var f=new PostCombatFrame{Now=Time.time,AmmoPercent=ammo,Health=b.Player.HealthController.GetBodyPartHealth(EBodyPart.Common,false).Normalized,
            Personality=b.Info.Personality.ToString(),MedicalBusy=b.BotOwner.Medecine.Using||b.Player.HandsController is IMedsController||s.Medical.Policy.Active,
            MedicalUseful=r.MedicalUseful,MedicalUrgent=r.MedicalUrgent,Reloading=b.BotOwner.WeaponManager.Reload.Reloading,
            HandsReady=WeaponHandsReady(b,b.Player.HandsController as Player.FirearmController)&&!MedicalPostureGuard(b),
            NeedsCover=r.Threat.HasValue&&!r.Protected,
            Regroup=!b.Squad.IAmLeader&&leader!=null&&!leader.IsDead&&Vector3.Distance(b.Position,leader.Position)>config.PostCombat.RegroupDistance,
            CanAdvance=r.Threat.HasValue&&distance>config.PostCombat.AdvanceMin&&distance<config.PostCombat.AdvanceMax};
        bool was=r.Policy.Active;
        bool active=r.Policy.Tick(f,config.PostCombat,jumpRandom.NextDouble(),jumpRandom.NextDouble());
        if(!active){if(was){ObserveControlRelease(s,"post-combat-finished");PostStopRoute(b,r);}r.Layer=false;}
        var t=r.Policy.Trace;
        if(t!=null){t.Contact=r.Contact;t.ContactAge=Math.Max(0,Time.time-r.ContactAt);t.X=r.Threat?.x;t.Y=r.Threat?.y;t.Z=r.Threat?.z;}
        r.Policy.Applied(r.Layer);RecordPostCombat(s,false);return active;
    }
    internal static void RunPostCombat(BotOwner owner,bool active)
    {
        var p=Instance;if(p==null||!SAINEnableClass.GetSAIN(owner.ProfileId,out var b)||b==null||!p.states.TryGetValue(b,out var s))return;
        try
        {
            var r=s.PostCombat;
            if(!active){p.EndPostCombat(b,s,"post-combat-layer-released",false);return;}
            if(!p.RefreshPostCombat(b,s))return;
            if(!r.Layer){r.Layer=true;r.Next=0;owner.PatrollingData.Pause();}
            r.Policy.Applied(true);
            if(Time.time<r.MoveAt)return;r.MoveAt=Time.time+(float)p.config!.PostCombat.Interval;
            p.ExecutePostCombat(b,s);p.RecordPostCombat(s,false);
        }
        catch(Exception ex){p.PostError(b,s,ex);}
    }
    internal static bool SteerPostCombat(BotOwner owner)
    {
        var p=Instance;
        if(p==null||!SAINEnableClass.GetSAIN(owner.ProfileId,out var b)||b==null||!p.states.TryGetValue(b,out var s)
            ||!s.PostCombat.Layer||!s.PostCombat.Policy.Active)return false;
        try {
            if(!p.PostScope(b)||p.PostDanger(b))return false;
            if(s.PostCombat.Path!=null)b.Steering.LookToMovingDirection();
            else if(s.PostCombat.Threat.HasValue)b.Steering.LookToPoint(s.PostCombat.Threat.Value+Vector3.up);
            else b.Steering.LookToRandomPosition();
            return true;
        } catch(Exception ex){p.PostError(b,s,ex);return false;}
    }
    private void EndPostCombat(BotComponent b,BotState s,string why,bool clear)
    {
        var r=s.PostCombat;bool active=r.Policy.Active||r.Layer;
        if(active)ObserveControlRelease(s,why);
        if(clear)r.Policy.Clear(why);else if(active)r.Policy.End(why);
        r.Layer=false;if(active){try{PostStopRoute(b,r);}finally{r.Path=null;RecordPostCombat(s,true);}}
        if(clear){r.Threat=null;r.Contact="";r.MedicalOwner=false;r.MedicalUseful=false;}
    }
    private void PostError(BotComponent b,BotState s,Exception ex)
    {if(s.PostCombat.Fault)return;s.PostCombat.Fault=true;EndPostCombat(b,s,"post-combat-adapter-error",true);Logger.LogWarning("H04 yielded: "+ex.GetBaseException().Message);}
    private void RecordPostCombat(BotState s,bool force)
    {
        var r=s.PostCombat;var t=r.Policy.Trace;if(t==null)return;
        Api.Decisions.SetPostCombat(s.ProfileId,t.Applied?"COMET:PostCombat"+char.ToUpperInvariant(t.State[0])+t.State.Substring(1):"");
        string key=t.Episode+":"+t.State+":"+t.Reason+":"+t.Execution+":"+t.Applied;
        if(!force&&key==r.Key&&(!t.Applied||Time.time<r.LogAt))return;r.Key=key;r.LogAt=Time.time+.5f;
        var json=Diagnostics.Serialize(new{schema=1,session,sequence=++sequence,feature="H04",stage="evaluated",decision="PostCombat",reason=t.Reason,
            realtime=Time.realtimeSinceStartup,utc=DateTime.UtcNow.ToString("O"),configRevision=config?.Revision??0,profileId=s.ProfileId,name=s.Name,botInstance=s.Id,
            postCombat=t,postCombatConfig=config?.PostCombat});
        diagnostics?.Write(json);Api.Decisions.Publish(json,ex=>{if(!subscriberWarned){subscriberWarned=true;Logger.LogWarning(ex.Message);}});
    }
}
