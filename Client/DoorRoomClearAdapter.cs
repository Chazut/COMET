using System;
using System.Linq;
using Comet.Core;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using EFT.InventoryLogic;
using HarmonyLib;
using Newtonsoft.Json;
using SAIN.Components;
using SAIN.Models.Enums;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.EnemyClasses;
using SAIN.SAINComponent.Classes.Mover;
using SAIN.SAINComponent.Classes.WeaponFunction;
using SAIN.SAINComponent.SubComponents;
using UnityEngine;
using UnityEngine.AI;

namespace Comet.Client;

internal sealed class DoorClearRuntime
{
    internal readonly DoorRoomClear Policy=new();
    internal BotComponent? Owner;
    internal DoorWork? Door;
    internal Vector3 Stack,Destination;
    internal NavMeshPath? Route;
    internal IBotPathData? OwnedPath;
    internal WeakReference<Grenade>? Projectile;
    internal int TargetInstance,Candidate;
    internal float OpenedAt,ProjectileAt,LastCheck,NextCheck,LastCallback,LogAt=-1000;
    internal double BestScore=double.MaxValue;
    internal bool EntryChecked,FeintChecked,NativeThrowCall,ThrowGuardPassed,Fault;
    internal string LogKey="",ThrowRefusal="";
    internal void Drop(){Owner=null;Door=null;Route=null;OwnedPath=null;Projectile=null;NativeThrowCall=false;}
}

// Only our real D07 projectile is added to SAIN's ordinary danger tracking. Never invent an enemy.
internal sealed class ClearOwnGrenade : IDisposable
{
    private readonly WeakReference<Grenade> grenade;
    private readonly WeakReference<GrenadeReactionClass> reaction;
    private GrenadeTrackerClass? tracker;
    private readonly WeakReference<BotComponent> bot;
    internal ClearOwnGrenade(BotComponent bot,Grenade item,Vector3 point)
    {
        grenade=new(item);reaction=new(bot.Grenade.GrenadeReactionClass);this.bot=new(bot);
        tracker=new GrenadeTrackerClass(bot,item,point,0,true);
        bot.Grenade.GrenadeReactionClass.EnemyGrenadesList.Add(item,tracker);item.DestroyEvent+=Destroyed;
    }
    private void Destroyed(Throwable item)=>Dispose();
    internal bool Alive=>tracker!=null&&grenade.TryGetTarget(out var g)&&g!=null&&bot.TryGetTarget(out var b)&&b!=null&&!b.IsDead;
    public void Dispose()
    {
        if(!grenade.TryGetTarget(out var g)){tracker=null;return;}
        // Unity destroyed objects can still carry managed delegates/keys.
        if(!ReferenceEquals(g,null))
        {
            g.DestroyEvent-=Destroyed;
            if(reaction.TryGetTarget(out var r)&&r.EnemyGrenadesList.TryGetValue(g,out var entry)&&ReferenceEquals(entry,tracker))r.EnemyGrenadesList.Remove(g);
        }
        tracker=null;
    }
}

public sealed partial class Plugin
{
    private GlobalEventDispatcher? clearEvents;
    private readonly System.Collections.Generic.List<ClearOwnGrenade> clearOwnGrenades=new();
    private static void PatchDoorRoomClear(Harmony h)
    {
        // Guard the actual SAIN-selected arc immediately before its native throw, without replacing SAIN's cooldown.
        h.Patch(AccessTools.Method(typeof(GrenadeThrowDecider),"TryThrowGrenade",Type.EmptyTypes)??throw new MissingMethodException("D07 TryThrowGrenade"),
            prefix:new HarmonyMethod(typeof(Plugin),nameof(ClearThrowGuard)){priority=Priority.First});
    }
    private void EnsureClearEvents()
    {
        var current=Singleton<GlobalEventDispatcher>.Instance;
        if(ReferenceEquals(clearEvents,current))return;
        ClearRoomEvents();clearEvents=current;
        if(clearEvents!=null){clearEvents.OnGrenadeThrow+=ClearProjectileThrown;clearEvents.OnGrenadeExplosive+=ClearProjectileExplosion;}
    }
    private void ClearRoomEvents()
    {
        if(clearEvents!=null){clearEvents.OnGrenadeThrow-=ClearProjectileThrown;clearEvents.OnGrenadeExplosive-=ClearProjectileExplosion;}
        clearEvents=null;
        foreach(var link in clearOwnGrenades)link.Dispose();clearOwnGrenades.Clear();
    }
    private void SweepClearGrenades()
    {
        for(int i=clearOwnGrenades.Count-1;i>=0;i--)if(!clearOwnGrenades[i].Alive){clearOwnGrenades[i].Dispose();clearOwnGrenades.RemoveAt(i);}
    }
    private void ClearProjectileThrown(Grenade grenade,Vector3 position,Vector3 force,float mass)
    {
        if(grenade==null||world==null)return;
        RelocationProjectile(grenade);
        JudgmentProjectile(grenade);
        try{OwnProjectileThrown(grenade);}catch(Exception ex){Logger.LogWarning("G04 throw observation failed: "+ex.GetBaseException().Message);}
        foreach(var pair in states)
        {
            var r=pair.Value.RoomClear;var t=r.Policy.Trace;
            if(!r.Policy.Active||t?.State!="throwing"||pair.Key.ProfileId!=grenade.ProfileId||grenade.WeaponSource?.Id.ToString()!=t.GrenadeItem)continue;
            r.Projectile=new(grenade);r.ProjectileAt=Time.time;r.Policy.Projectile(t.GrenadeItem,grenade.Id,Time.time);
            RecordDoorClear(pair.Value,true);break;
        }
    }
    private void ClearProjectileExplosion(Vector3 position,string profile,bool smoke,float radius,float life,int id)
    {
        if(smoke||world==null)return;
        RelocationExplosion(profile,id);
        try{OwnExplosion(profile,id);}catch(Exception ex){Logger.LogWarning("G04 blast observation failed: "+ex.GetBaseException().Message);}
        foreach(var pair in states)
        {
            var r=pair.Value.RoomClear;
            if(pair.Key.ProfileId!=profile||!r.Policy.Active||r.Policy.Trace?.Projectile!=id)continue;
            r.Policy.Explosion(id,Time.time);r.Projectile=null;RecordDoorClear(pair.Value,true);break;
        }
    }
    private bool TryStartDoorClear(BotComponent b,Enemy e,BotState s,DoorWork door)
    {
        var r=s.RoomClear;
        if(config?.DoorRoomClear.Enabled!=true||r.Fault||r.Policy.Active||DoorGate(b,e,s).Length>0
            ||e.KnownPlaces.LastKnownPosition is not Vector3 known||ClearResources(b).Length>0)return false;
        if(Vector3.Distance(b.Position,VP(door.Geometry.Center))>3)return false;
        bool searching=e.TimeSinceLastKnownUpdated>config.DoorRoomClear.ContactLifetime;
        if(searching&&(!b.Info.PersonalitySettings.Search.WillSearchForEnemy||!e.Seen&&!b.Info.PersonalitySettings.Search.WillSearchFromAudio))return false;
        var old=r.Policy.Trace;
        // Exactly one variant gets a roll. A declined D07 attempt cannot fall through into D08.
        bool selected=searching
            ?r.Policy.BeginSearch(door.Geometry.Id,e.EnemyProfileId,s.Situation?.ContactSource??"sain-last-known",b.Info.Personality.ToString(),DP(known),e.TimeSinceLastKnownUpdated,Time.time,jumpRandom.NextDouble(),config.DoorRoomClear,config.DoorSearchClear,config.ContactLifetime,config.DoorApproach.ContactLifetime)
            :r.Policy.Begin(door.Geometry.Id,e.EnemyProfileId,s.Situation?.ContactSource??"sain-last-known",b.Info.Personality.ToString(),DP(known),e.TimeSinceLastKnownUpdated,Time.time,jumpRandom.NextDouble(),config.DoorRoomClear);
        if(ReferenceEquals(old,r.Policy.Trace))return false;
        if(!selected){RecordDoorClear(s,true);return false;}
        r.Owner=b;r.Door=door;r.Stack=b.Position;r.TargetInstance=System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(e);
        r.EntryChecked=false;r.Route=null;r.OwnedPath=null;r.Projectile=null;r.Candidate=0;r.BestScore=double.MaxValue;r.FeintChecked=false;
        r.LastCallback=r.LastCheck=Time.time;r.NextCheck=0;r.ThrowGuardPassed=false;r.ThrowRefusal="";
        if(!searching)EnsureClearEvents();StopQuiet(b,s,"quiet-room-clear");EndSilentSearch(s,"silent-search-room-clear");EndAbandonment(s,"search-check-room-clear");
        RecordDoorClear(s,true);return true;
    }
    private string ClearResources(BotComponent b)
    {
        if(MedicalPostureGuard(b)||FakeMedicalPriority(b))return "clear-real-medical-priority";
        var weapon=b.BotOwner.WeaponManager?.CurrentWeapon;
        if(weapon==null||weapon.GetCurrentMagazine()==null)return "clear-magazine-unavailable";
        var mag=weapon.GetCurrentMagazine();
        if(mag.MaxCount<=0||mag.Count*100d/mag.MaxCount<config!.DoorRoomClear.MinimumAmmo)return "clear-insufficient-loaded-ammo";
        if(b.Player.Physical.Stamina.NormalValue*100<config!.DoorRoomClear.MinimumStamina)return "clear-insufficient-stamina";
        return "";
    }
    private string ClearGate(BotComponent b,BotState s)
    {
        var r=s.RoomClear;var t=r.Policy.Trace!;var e=b.GoalEnemy;
        if(config?.Enabled!=true||!config.DoorRoomClear.Enabled||!config.DoorApproach.Enabled||!FakeScope(b))return "clear-out-of-scope";
        if(!TacticalDoorSearch(b,s))return "clear-search-not-owned";
        if(t.Mode=="search")
        {
            if(!config.DoorSearchClear.Enabled)return "search-clear-disabled";
            if(!b.Info.PersonalitySettings.Search.WillSearchForEnemy||e?.Seen==false&&!b.Info.PersonalitySettings.Search.WillSearchFromAudio)return "search-clear-native-search-disabled";
            string search=r.Policy.SearchContact(e?.TimeSinceLastKnownUpdated??-1,Time.time,e?.EnemyKnown==true,config.ContactLifetime,config.DoorApproach.ContactLifetime);
            if(search.Length>0)return search;
        }
        if(e==null||!e.EnemyKnown||e.EnemyProfileId!=t.Target
            ||e.KnownPlaces.LastKnownPosition is not Vector3 known||(DP(known)-new DoorPoint(t.KnownX,t.KnownY,t.KnownZ)).Length>2)return "clear-contact-changed";
        t.ContactAge=e.TimeSinceLastKnownUpdated;
        if(e.IsVisible||b.EnemyController.VisibleEnemies.Count>0)return "clear-visible-contact-native-combat";
        if(b.Grenade.GrenadeDangerPoint!=null)return "clear-grenade-danger-native-escape";
        // Once the grenade is out, keep its safety wait even if the tactical contact has become stale.
        if(t.Mode!="search"&&t.State is not ("throwing" or "waiting-blast" or "post-blast")&&t.ContactAge>config.DoorRoomClear.ContactLifetime)return "clear-contact-expired";
        if(b.Flashed.IsFlashed||b.BotOwner.Memory.IsUnderFire||b.Suppression.IsSuppressed||s.Fear.Current.Panic||b.Medical.TimeSinceShot<3
            ||b.Decision.CurrentSelfDecision!=ESelfActionType.None||b.Decision.CurrentSquadDecision!=ESquadDecision.None)return "clear-native-priority";
        if(r.Door==null||!TryDoorGeometry(r.Door.Data,out var g)||(g.Center-r.Door.Geometry.Center).Length>.1||Math.Abs(g.Axis.Dot(r.Door.Geometry.Axis))<.99)return "clear-frame-changed";
        bool ownOpen=t.State=="opening";
        if(!ownOpen&&(b.DoorOpener.Interacting||b.BotOwner.DoorOpener.Interacting||JumpInteractionState(b)))return "clear-foreign-door-interaction";
        if(!ownOpen&&t.DoorOpenObserved&&g.State!="Open")return "clear-door-no-longer-open";
        if(!ownOpen&&g.State!="Open"&&g.State!="Shut")return "clear-door-unavailable";
        bool handsBusy=t.State is "feint" or "throwing" or "waiting-blast" or "post-blast";
        if(!ownOpen&&!handsBusy&&!FakeWeaponReady(b))return "clear-weapon-not-ready";
        if(!handsBusy){string resources=ClearResources(b);if(resources.Length>0)return resources;}
        if(t.State!="entering"&&Vector3.Distance(b.Position,r.Stack)>.5f)return "clear-protected-frame-lost";
        return "";
    }
    private bool KeepDoorClear(BotComponent b,BotState s,ref ECombatDecision decision,ref bool result)
    {
        if(!s.RoomClear.Policy.Active)return false;
        RefreshDoorClear(b,s);
        if(!s.RoomClear.Policy.Active)return false;
        s.Expected=ECombatDecision.Search;s.RequestedFrame=Time.frameCount;decision=ECombatDecision.Search;result=true;return true;
    }
    private void RefreshDoorClear(BotComponent b,BotState s)
    {
        if(!s.RoomClear.Policy.Active||s.Entry.Policy.Active)return;
        try
        {
            string why=ClearGate(b,s);
            if(why.Length>0)EndDoorClear(s,why);
            else if(Time.time-s.RoomClear.LastCallback>.6f)EndDoorClear(s,"clear-search-callback-stopped");
        }
        catch(Exception ex){s.RoomClear.Fault=true;EndDoorClear(s,"clear-adapter-error");Logger.LogWarning("COMET room entry yielded: "+ex.Message);}
    }
    private bool StepDoorClear(BotComponent b,BotState s)
    {
        var r=s.RoomClear;var t=r.Policy.Trace!;var e=b.GoalEnemy;r.LastCallback=Time.time;
        if(s.Entry.Policy.Active){RefreshDoorEntry(b,s);return !s.Entry.Policy.Active;}
        if(t.State=="preparing"&&!r.EntryChecked)
        {
            r.EntryChecked=true;
            // A direct entry variant is chosen before opening the door; no grenade was requested.
            if(t.Mode=="recent"&&r.Door!=null&&r.Door.Data.Door.DoorState==EDoorState.Shut
                &&ClearGate(b,s).Length==0&&TryStartDoorEntry(b,s,r.Door,"prepared",true))return false;
        }
        string gate=ClearGate(b,s);if(gate.Length>0){EndDoorClear(s,gate);return true;}
        r.Policy.Tick(Time.time,FakeWeaponReady(b));
        if(!r.Policy.Active){FinishDoorClear(s);return true;}
        RecordDoorClear(s);
        if(t.State=="feint")
        {
            RefreshDoorFake(b,s);
            if(!s.Fake.Policy.Active){var f=s.Fake.Policy.Trace;r.Policy.FeintEnd(f?.State=="completed"&&f.WeaponReady,f?.Reason??"feint-not-confirmed",Time.time);RecordDoorClear(s,true);}
            if(!r.Policy.Active){FinishDoorClear(s);return true;}return false;
        }
        if(t.State=="opening")
        {
            if(r.Door!.Data.Door.DoorState==EDoorState.Open&&!JumpInteractionState(b)&&!b.DoorOpener.Interacting&&!b.BotOwner.DoorOpener.Interacting)
            {r.Policy.Opened(Time.time);RecordDoorClear(s,true);}return false;
        }
        if(t.State=="throwing")return false;
        if(t.State=="waiting-blast"&&(r.Projectile==null||!r.Projectile.TryGetTarget(out var projectile)||projectile==null))
        {r.Policy.MissingProjectile();FinishDoorClear(s);return true;}
        if(t.State=="entering")
        {
            bool near=Vector3.Distance(b.Position,r.Destination)<=.5f;
            bool owns=OwnsClearPath(r)||b.Mover.ActivePath==null&&r.OwnedPath?.Status==EBotMoveStatus.Complete&&near;
            if(!owns){EndDoorClear(s,"clear-route-replaced");return true;}
            r.Policy.Movement(DP(b.Position),DoorClearGeometry.Depth(r.Door!.Geometry,DP(r.Stack),DP(b.Position))>.4,r.OwnedPath?.Status==EBotMoveStatus.Complete,FakeWeaponReady(b),Time.time);
            if(!r.Policy.Active){FinishDoorClear(s);return true;}
        }
        if(Time.time<r.NextCheck){b.Steering.LookToPoint(VP(new(t.KnownX,t.KnownY,t.KnownZ))+Vector3.up*1.2f);return false;}
        if(!doorBudget.TryBegin(Time.frameCount,b))
        {if(Time.time-r.LastCheck>.5f){EndDoorClear(s,"clear-safety-budget-expired");return true;}return false;}
        doorClock.Restart();
        try
        {
            r.LastCheck=Time.time;r.NextCheck=Time.time+.25f;
            if(t.State!="entering"&&!ClearCover(b,r)){EndDoorClear(s,"clear-frame-cover-lost");return true;}
            if(t.State=="preparing")
            {
                if(r.Door!.Data.Door.DoorState==EDoorState.Shut)
                {
                    if(!RequestJumpDoorOpen(b,new DoorJumpRuntime{Door=r.Door})){EndDoorClear(s,"clear-native-open-refused");return true;}
                    r.OpenedAt=Time.time;r.Policy.Opening(Time.time);
                }
                else r.Policy.Opened(Time.time);
            }
            else if(t.State=="planning")
            {
                if(r.Candidate<6){PlanClearCandidate(b,s,r.Candidate++);r.NextCheck=Time.time;return false;}
                if(r.Route==null){EndDoorClear(s,"clear-no-interior-route");return true;}
                if(!r.FeintChecked)
                {
                    r.FeintChecked=true;
                    if(config!.DoorRoomClear.AllowFeint&&(t.Mode!="search"||config.DoorSearchClear.AllowFeint)&&TryStartDoorFake(b,e!,s,r.Door!))
                    {r.Policy.FeintStart(s.Fake.Policy.Trace!.Kind,Time.time);RecordDoorClear(s,true);return false;}
                }
                r.Policy.Ready(Time.time);
            }
            else if(t.State=="ready")
            {
                string route=ValidateClearRoute(b,r,true);if(route.Length>0){EndDoorClear(s,route);return true;}
                string direct=DirectClearSafety(b,r);
                string nade=t.Mode=="search"?"search-clear-no-real-grenade":PrepareClearGrenade(b,e!,s);
                r.Policy.Choose(direct.Length==0,nade.Length==0,direct,nade,config!.GrenadeJudgment.Enabled?0:jumpRandom.NextDouble(),jumpRandom.NextDouble(),Time.time,config.DoorRoomClear,config.GrenadeJudgment.Enabled);
            }
            else if(t.State=="throw-ready")
            {
                string why=PrepareClearGrenade(b,e!,s);
                if(why.Length>0){EndDoorClear(s,"clear-grenade-plan-invalid:"+why);return true;}
                var nade=b.BotOwner.WeaponManager.Grenades.grenade;
                r.Policy.ThrowRequest(nade.Id.ToString(),nade.GetExplDelay,ClearDangerRadius(nade),Time.time);
                if(r.Policy.Active)
                {
                    r.NativeThrowCall=true;r.ThrowGuardPassed=false;r.ThrowRefusal="";
                    try{bool accepted=NativeAdapter.Throw(b,e!,out var reason);r.Policy.ThrowResult(accepted&&r.ThrowGuardPassed,Time.time);if(!accepted)t.Alternatives=t.Alternatives.Concat(new[]{"Native throw refused: "+reason+" "+r.ThrowRefusal}).ToArray();}
                    finally{r.NativeThrowCall=false;}
                }
            }
            else if(t.State=="waiting-blast")
            {
                // A projectile necessarily starts at the thrower's hands. Allow only its initial departure,
                // then hand a dangerous own projectile to native avoidance; safe throws need no tracker.
                if(Time.time-r.ProjectileAt>=.35f&&r.Projectile!=null&&r.Projectile.TryGetTarget(out var nade)&&nade!=null&&!ClearBlastProtected(b.Position,nade.transform.position,t.DangerRadius))
                {
                    SweepClearGrenades();
                    if(config?.OwnGrenadeSafety.Enabled!=true&&!b.Grenade.GrenadeReactionClass.EnemyGrenadesList.ContainsKey(nade))clearOwnGrenades.Add(new ClearOwnGrenade(b,nade,nade.transform.position));
                    EndDoorClear(s,"clear-own-grenade-threat-native-escape");return true;
                }
            }
            else if(t.State=="entry-ready")
            {
                string why=ValidateClearRoute(b,r,true);
                if(why.Length==0)why=DirectClearSafety(b,r);
                if(why.Length>0){EndDoorClear(s,"clear-entry-recheck:"+why);return true;}
                r.Policy.MoveRequest(DP(b.Position),Time.time);
                bool accepted=b.Mover.WalkToPointByWay(r.Route!,true,.35f,false);r.Policy.MoveResult(accepted);r.OwnedPath=accepted?b.Mover.ActivePath:null;
                if(accepted&&!OwnsClearPath(r)){EndDoorClear(s,"clear-native-path-not-observed");return true;}
            }
            else if(t.State=="entering")
            {
                string why=ValidateClearRoute(b,r,false);if(why.Length>0){EndDoorClear(s,why);return true;}
            }
            if(!r.Policy.Active){FinishDoorClear(s);return true;}
            b.Steering.LookToPoint(VP(new(t.KnownX,t.KnownY,t.KnownZ))+Vector3.up*1.2f);RecordDoorClear(s);return false;
        }
        finally{doorClock.Stop();doorBudget.Charge(doorClock.Elapsed.TotalMilliseconds);}
    }
    private void PlanClearCandidate(BotComponent b,BotState s,int index)
    {
        var r=s.RoomClear;var t=r.Policy.Trace!;var c=config!.DoorRoomClear;double depth=index<3?c.DepthMin:c.DepthMax;
        double lateral=index%3==0?-.8:index%3==1?.8:0;
        var raw=DoorClearGeometry.Destination(r.Door!.Geometry,DP(r.Stack),depth,lateral);
        if(!NavMesh.SamplePosition(VP(raw),out var hit,.2f,NavMesh.AllAreas)||Math.Abs(hit.position.y-r.Stack.y)>.65
            ||Physics.Linecast(VP(raw)+Vector3.up,hit.position+Vector3.up,LayersMaskController.HighPolyWithTerrainMask,QueryTriggerInteraction.Ignore))return;
        if(!b.Mover.CanGoToPoint(hit.position,out var path,true,.1f)||DoorClearGeometry.Route(r.Door.Geometry,DP(r.Stack),DP(hit.position),path.corners.Select(DP).ToArray(),path.status==NavMeshPathStatus.PathComplete).Length>0)return;
        var previous=r.Destination;var previousPath=r.Route;r.Destination=hit.position;r.Route=path;
        string why=ValidateClearRoute(b,r,false);
        if(why.Length>0||!s.ProgressUnavailable&&s.Progress.Blocked(ProgressAdapter.Destination(hit.position),Time.time)){r.Destination=previous;r.Route=previousPath;return;}
        var known=new Vector3((float)t.KnownX,(float)t.KnownY,(float)t.KnownZ);
        if(Vector3.Distance(hit.position,known)<1.5f){r.Destination=previous;r.Route=previousPath;return;}
        bool cover=ClearBlastProtected(hit.position,known,30);
        double score=Length(path.corners)+(cover?0:10);
        if(score>=r.BestScore){r.Destination=previous;r.Route=previousPath;return;}
        r.BestScore=score;r.Policy.Route(DP(hit.position),cover);
    }
    private string ValidateClearRoute(BotComponent b,DoorClearRuntime r,bool refresh)
    {
        if(r.Door?.Data.Door==null||r.Door.Data.Door.DoorState!=EDoorState.Open)return "clear-door-not-open";
        if(refresh)
        {
            if(!b.Mover.CanGoToPoint(r.Destination,out var path,true,.1f))return "clear-no-current-route";
            string why=DoorClearGeometry.Route(r.Door.Geometry,DP(r.Stack),DP(r.Destination),path.corners.Select(DP).ToArray(),path.status==NavMeshPathStatus.PathComplete);
            if(why.Length>0)return why;r.Route=path;
        }
        if(r.Route==null||r.Route.status!=NavMeshPathStatus.PathComplete)return "clear-incomplete-route";
        var corners=r.Route.corners;
        if(corners.Length<2||corners.Length>16)return "clear-route-too-complex";
        // Check each actual segment, including the threshold, rather than a shortcut through its wall.
        for(int i=1;i<corners.Length;i++){string why=RunVolume(b,corners[i-1],corners[i]);if(why.Length>0)return "clear-"+why;}
        if(r.Policy.Trace?.State=="entering"&&Math.Abs(b.Position.y-r.Stack.y)>.65)return "clear-left-verified-floor";
        return "";
    }
    private string DirectClearSafety(BotComponent b,DoorClearRuntime r)
    {
        var t=r.Policy.Trace!;
        if(t.Mode=="search")
        {
            string search=r.Policy.SearchContact(b.GoalEnemy?.TimeSinceLastKnownUpdated??-1,Time.time,b.GoalEnemy?.EnemyKnown==true,config!.ContactLifetime,config.DoorApproach.ContactLifetime);
            if(search.Length>0)return search;
        }
        else if(t.ContactAge>config!.DoorRoomClear.ContactLifetime)return "clear-contact-too-old-to-enter";
        if(!FakeWeaponReady(b))return "clear-weapon-not-ready";
        string resources=ClearResources(b);if(resources.Length>0)return resources;
        var known=new DoorPoint(t.KnownX,t.KnownY,t.KnownZ);
        if(DoorClearGeometry.Depth(r.Door!.Geometry,DP(r.Stack),known)<1.5)return "clear-known-contact-at-threshold";
        if(b.Grenade.GrenadeDangerPoint!=null)return "clear-native-grenade-danger";
        return "";
    }
    private bool ClearCover(BotComponent b,DoorClearRuntime r)
    {
        int count=0;bool primary=false;
        foreach(var e in b.EnemyController.KnownEnemies)
        {
            if(++count>8)return false;
            if(e==null||e.TimeSinceLastKnownUpdated>30||e.KnownPlaces.LastKnownPosition is not Vector3 known)continue;
            if(e.EnemyProfileId==r.Policy.Trace!.Target)primary=true;
            if(e.IsVisible||!ClearBlastProtected(b.Position,known,30))return false;
        }
        return primary;
    }
    private static bool ClearBlastProtected(Vector3 point,Vector3 danger,double radius)
    {
        if(!DP(point).Finite||!DP(danger).Finite||!DoorRoomClear.Valid(radius))return false;
        if(Vector3.Distance(point,danger)>radius)return true;
        return Physics.Linecast(danger+Vector3.up*.3f,point+Vector3.up*1.3f,LayersMaskController.HighPolyWithTerrainMask,QueryTriggerInteraction.Ignore)
            &&Physics.Linecast(danger+Vector3.up*.3f,point+Vector3.up*1.65f,LayersMaskController.HighPolyWithTerrainMask,QueryTriggerInteraction.Ignore);
    }
    private static double ClearDangerRadius(ThrowWeap item)=>Math.Max(6,Math.Max(item.MaxExplosionDistance,Math.Max(item.Blindness.y,item.Contusion.y)))+1;
    private string PrepareClearGrenade(BotComponent b,Enemy e,BotState s)
    {
        if(s.RoomClear.Policy.Trace?.Mode=="search")return "search-clear-no-real-grenade";
        if(clearEvents==null)return "clear-explosion-events-unavailable";
        string why=NativeAdapter.PrepareRoomGrenade(b,e,s,config!);if(why.Length>0)return why;
        var manager=b.BotOwner.WeaponManager.Grenades;var item=manager.grenade;
        if(item==null||item.ThrowType.ToString().IndexOf("smoke",StringComparison.OrdinalIgnoreCase)>=0||item.IsDummy)return "clear-no-suitable-grenade";
        return ClearThrowSafety(b,s,manager.AIGreanageThrowData,item);
    }
    private string ClearThrowSafety(BotComponent b,BotState s,AIGreanageThrowData data,ThrowWeap item)
    {
        var r=s.RoomClear;var t=r.Policy.Trace!;
        if(t.Mode=="search")return "search-clear-no-real-grenade";
        if(data==null||!data.CanThrow||!data.IsUpToDate()||!DP(data.Target).Finite||!DP(data.Position).Finite
            ||Vector3.Distance(data.Position,b.Transform.WeaponData.WeaponRoot)>.5f)return "clear-throw-arc-invalid";
        double radius=ClearDangerRadius(item);
        if(!DoorRoomClear.Valid(radius)||radius>100||!DoorRoomClear.Valid(item.GetExplDelay)||item.GetExplDelay<.75||item.GetExplDelay>30)return "clear-grenade-properties-unknown-or-fuse-too-short";
        if(r.Door?.Data.Door==null||r.Door.Data.Door.DoorState!=EDoorState.Open||DoorClearGeometry.Depth(r.Door.Geometry,DP(r.Stack),DP(data.Target))<.5)return "clear-throw-not-into-room";
        if(!ClearBlastProtected(b.Position,data.Target,radius))return "clear-own-blast-exposure";
        int count=0;
        // Existing player registry, bounded. Unknown/non-hostile participants are protected too, not just squadmates.
        foreach(var player in world!.RegisteredPlayers)
        {
            if(++count>128)return "clear-friendly-check-truncated";
            if(player==null||ReferenceEquals(player,b.Player)||!player.HealthController.IsAlive)continue;
            if(b.BotOwner.BotsGroup?.IsEnemy(player)==true)continue;
            if(!ClearBlastProtected(player.Position,data.Target,radius+1))return "clear-friendly-blast-exposure";
        }
        if(t.ContactAge>config!.GrenadeContactLifetime)return "clear-grenade-contact-expired";
        return "";
    }
    private static bool ClearThrowGuard(GrenadeThrowDecider __instance,ref bool __result)
    {
        var p=Instance;var b=__instance.Bot;
        if(p==null||!p.states.TryGetValue(b,out var s)||!s.RoomClear.NativeThrowCall)return true;
        var r=s.RoomClear;
        try
        {
            string why=p.ClearGate(b,s);
            if(why.Length==0)why=p.ClearThrowSafety(b,s,b.BotOwner.WeaponManager.Grenades.AIGreanageThrowData,b.BotOwner.WeaponManager.Grenades.grenade);
            if(why.Length==0&&b.BotOwner.WeaponManager.Grenades.grenade.Id.ToString()!=r.Policy.Trace!.GrenadeItem)why="clear-selected-grenade-changed";
            if(why.Length==0){r.ThrowGuardPassed=true;return true;}r.ThrowRefusal=why;
        }
        catch(Exception){r.ThrowRefusal="clear-throw-validation-failed";}
        __result=false;return false;
    }
    private static bool OwnsClearPath(DoorClearRuntime r)=>r.Owner!=null&&r.OwnedPath!=null&&ReferenceEquals(r.Owner.Mover.ActivePath,r.OwnedPath)&&Vector3.Distance(r.OwnedPath.Destination,r.Destination)<.3f;
    private void EndDoorClear(BotState s,string reason)
    {
        var r=s.RoomClear;if(!r.Policy.Active)return;
        r.Policy.End(reason);
        if(s.Fake.Policy.Active)EndDoorFake(s,reason);
        FinishDoorClear(s);
    }
    private void FinishDoorClear(BotState s)
    {
        var r=s.RoomClear;
        if(OwnsClearPath(r)){r.OwnedPath!.Cancel();if(r.Owner!.CurrentAction?.GetType().Name=="SearchAction")r.Owner.Search.Reset();}
        RecordDoorClear(s,true);r.Drop();
    }
    private void RecordDoorClear(BotState s,bool force=false)
    {
        var r=s.RoomClear;var t=r.Policy.Trace;if(t==null)return;
        string key=t.State+":"+t.Reason+":"+t.GrenadeOutcome+":"+t.ThresholdCrossed+":"+t.MovementObserved+":"+t.ThrowAccepted;
        if(!force&&key==r.LogKey&&(!r.Policy.Active||Time.time-r.LogAt<.5f))return;r.LogKey=key;r.LogAt=Time.time;
        var json=Diagnostics.Serialize(new{schema=1,session,sequence=++sequence,feature=t.Mode=="search"?"D08":"D07",stage="evaluated",decision=t.Mode=="search"?"DoorSearchClear":"DoorRoomClear",reason=t.Reason,
            realtime=Time.realtimeSinceStartup,utc=DateTime.UtcNow.ToString("O"),configRevision=config?.Revision??0,profileId=s.ProfileId,name=s.Name,botInstance=s.Id,
            doorRoomClear=t,doorRoomClearConfig=config?.DoorRoomClear,doorSearchClearConfig=t.Mode=="search"?config?.DoorSearchClear:null});
        diagnostics?.Write(json);Api.Decisions.Publish(json,ex=>{if(!subscriberWarned){subscriberWarned=true;Logger.LogWarning(ex.Message);}});
    }
}
