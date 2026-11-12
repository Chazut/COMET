using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Comet.Core;
using EFT;
using EFT.Interactive;
using EFT.InventoryLogic;
using EFT.Ballistics;
using HarmonyLib;
using Newtonsoft.Json;
using SAIN.Components;
using SAIN.Components.PlayerComponentSpace;
using SAIN.Models.Enums;
using SAIN.SAINComponent.Classes.Mover;
using SAIN.SAINComponent.Classes.WeaponFunction;
using SAIN.SAINComponent.SubComponents;
using UnityEngine;

namespace Comet.Client;

internal sealed class OwnProjectile
{
    internal readonly OwnGrenadeEvidence Evidence=new();
    internal WeakReference<Grenade> Grenade=null!;
    internal WeakReference<BotComponent> Owner=null!;
    internal readonly HashSet<string> Informed=new(StringComparer.Ordinal);
}
internal sealed class OwnGrenadeRuntime
{
    internal readonly HashSet<OwnProjectile> Escaping=new();
    internal GrenadeTrackerClass? Tracker;
    internal OwnProjectile? Selected;
    internal OwnGrenadeSafetyTrace? Trace;
    internal float Next,PathNext,LogAt=-1000;
    internal IBotPathData? CheckedPath;
    internal string Key="";
    internal bool Fault,Phased;
}

public sealed partial class Plugin
{
    private readonly List<OwnProjectile> ownProjectiles=new();
    private float ownSampleAt;
    private static MethodInfo ownDangerSetter=null!,ownReactionSetter=null!;
    private static void PatchOwnGrenadeSafety(Harmony h)
    {
        ownDangerSetter=AccessTools.PropertySetter(typeof(GrenadeReactionClass),nameof(GrenadeReactionClass.DangerGrenade))??throw new MissingMethodException("G04 danger setter");
        ownReactionSetter=AccessTools.PropertySetter(typeof(GrenadeReactionClass),nameof(GrenadeReactionClass.Reaction))??throw new MissingMethodException("G04 reaction setter");
        h.Patch(AccessTools.Method(typeof(BotComponent),nameof(BotComponent.ManualUpdate)),prefix:new HarmonyMethod(typeof(Plugin),nameof(OwnGrenadeUpdate)));
        h.Patch(AccessTools.Method(typeof(GrenadeReactionClass),"UpdateDangerGrenade"),postfix:new HarmonyMethod(typeof(Plugin),nameof(OwnNativeDanger)));
    }
    private bool OwnScope(BotComponent b)=>config?.Enabled==true&&config.OwnGrenadeSafety.Enabled&&b!=null&&!b.IsDead
        &&NativeAdapter.IsSupportedAi(b)&&b.BotActive&&!b.GameEnding&&Authority&&!Ghost(b.ProfileId);
    private static void OwnGrenadeUpdate(BotComponent __instance)
    {
        var p=Instance;if(p==null||p.world==null||p.config==null)return;
        try
        {
            if(!p.OwnScope(__instance)){if(p.states.TryGetValue(__instance,out var old))p.DropOwnSafety(__instance,old,"own-grenade-out-of-scope");return;}
            var s=p.GrenadeState(__instance);if(s.OwnGrenade.Fault)return;
            p.EnsureClearEvents();p.SampleOwnProjectiles();
            if(Time.time<s.OwnGrenade.Next)return;
            s.OwnGrenade.Next=Time.time+(float)p.config.OwnGrenadeSafety.Refresh+(s.OwnGrenade.Phased?0:(Math.Abs((long)__instance.ProfileId.GetHashCode())%8)*(float)p.config.OwnGrenadeSafety.Refresh/8);
            s.OwnGrenade.Phased=true;
            p.RefreshOwnSafety(__instance,s);
        }
        catch(Exception ex){p.OwnSafetyError(__instance,ex);}
    }
    private void OwnProjectileThrown(Grenade grenade)
    {
        if(config?.Enabled!=true||!config.OwnGrenadeSafety.Enabled||!Authority||grenade.WeaponSource is not ThrowWeap item||item.IsDummy
            ||item.ThrowType.ToString() is "smoke_grenade" or "gas_grenade")return;
        var b=states.Keys.FirstOrDefault(x=>x!=null&&x.ProfileId==grenade.ProfileId&&OwnScope(x));if(b==null)return;
        if(ownProjectiles.Any(x=>x.Grenade.TryGetTarget(out var g)&&ReferenceEquals(g,grenade)))return;
        double radius=6; // OwnGrenadeTracker.DANGER_RADIUS
        if(!GrenadeChoiceRules.Finite(radius)||radius<=0||radius>150)return;
        SampleOwnProjectiles();
        // Source reaction list is bounded per thrower, never by a global veto.
        var oldest=ownProjectiles.Where(x=>x.Evidence.Owner==b.ProfileId).OrderBy(x=>x.Evidence.ThrownAt).ToList();
        while(oldest.Count>=4){ownProjectiles.Remove(oldest[0]);oldest.RemoveAt(0);}
        var p=new OwnProjectile{Grenade=new(grenade),Owner=new(b)};var e=p.Evidence;
        e.Owner=b.ProfileId;e.Projectile=grenade.Id;e.Item=item.Id.ToString();e.Radius=radius;e.Position=e.Origin=DP(grenade.transform.position);
        e.ThrownAt=e.SampledAt=Time.time;e.Fuse=GrenadeChoiceRules.Finite(item.GetExplDelay)?Math.Max(0,Math.Min(30,item.GetExplDelay)):8;
        p.Informed.Add(b.ProfileId);
        // Teammates use SAIN's native grenade perception, as in zzap.
        ownProjectiles.Add(p);
        foreach(var pair in states)if(p.Informed.Contains(pair.Key.ProfileId))RecordOwnSafety(pair.Key,pair.Value,p,"tracking","own-grenade-live",false,false,true);
    }
    private void OwnExplosion(string owner,int id)
    {
        foreach(var p in ownProjectiles)if(p.Evidence.Confirm(owner,id))
            foreach(var pair in states)if(p.Informed.Contains(pair.Key.ProfileId))RecordOwnSafety(pair.Key,pair.Value,p,"released","own-grenade-confirmed-explosion",false,false,true);
        // Do not wait for the next sample to retire this projectile; other live grenades remain.
        foreach(var pair in states)if(pair.Value.OwnGrenade.Selected?.Evidence.Exploded==true)DropOwnTracker(pair.Key,pair.Value.OwnGrenade);
    }
    private void SampleOwnProjectiles()
    {
        if(Time.time<ownSampleAt)return;ownSampleAt=Time.time+(float)config!.OwnGrenadeSafety.Refresh;
        for(int i=ownProjectiles.Count-1;i>=0;i--)
        {
            var p=ownProjectiles[i];var e=p.Evidence;
            if(e.Exploded||e.Expired(Time.time))
            {
                if(!e.Exploded)foreach(var pair in states)if(p.Informed.Contains(pair.Key.ProfileId))
                {RecordOwnSafety(pair.Key,pair.Value,p,"waiting","own-grenade-unknown-timeout",false,false,true);EndOwnTrace(pair.Value,"own-grenade-unknown-timeout");}
                ownProjectiles.RemoveAt(i);continue;
            }
            if(p.Grenade.TryGetTarget(out var g)&&g!=null)
            {var owner=p.Owner.TryGetTarget(out var b)&&b!=null?DP(b.Position):e.Origin;e.Sample(DP(g.transform.position),Time.time,owner);}
            else if(e.UnknownAt<0)
            {
                e.Missing(Time.time);
                foreach(var pair in states)if(p.Informed.Contains(pair.Key.ProfileId))
                {RecordOwnSafety(pair.Key,pair.Value,p,"waiting","own-grenade-outcome-unknown",false,false,true);}
            }
        }
    }
    private void RefreshOwnSafety(BotComponent b,BotState s)
    {
        var r=s.OwnGrenade;OwnProjectile? danger=null;double nearest=double.MaxValue;
        r.Escaping.RemoveWhere(x=>!ownProjectiles.Contains(x));
        foreach(var p in ownProjectiles)
        {
            if(!p.Informed.Contains(b.ProfileId))continue;var e=p.Evidence;
            bool threat=OwnGrenadeSafetyRules.Threat(e,DP(b.Position),r.Escaping.Contains(p),Time.time,config!.OwnGrenadeSafety,OwnSolidCover,e.Owner==b.ProfileId);
            if(threat){r.Escaping.Add(p);double d=(e.Position-DP(b.Position)).Length;if(d<nearest){nearest=d;danger=p;}}
            else r.Escaping.Remove(p);
        }
        if(danger==null){DropOwnTracker(b,r);if(r.Trace?.State is "fleeing" or "waiting")EndOwnTrace(s,"own-grenade-safe-position");return;}
        bool live=danger.Grenade.TryGetTarget(out var grenade)&&grenade!=null;
        bool changed=!ReferenceEquals(r.Selected,danger);
        if(changed||!live&&r.Tracker!=null)
        {
            DropOwnTracker(b,r);r.Selected=danger;
            if(live)r.Tracker=new GrenadeTrackerClass(b,grenade!,VP(danger.Evidence.Position),0,true);
            Release(s,live?"own-grenade-native-escape":"own-grenade-outcome-unknown");
        }
        if(r.Tracker!=null)r.Tracker.DangerPoint=VP(danger.Evidence.Position);
        RecordOwnSafety(b,s,danger,live?"fleeing":"waiting",live?"own-grenade-native-escape":"own-grenade-outcome-unknown",false,live);
    }
    private static void OwnNativeDanger(GrenadeReactionClass __instance)
    {
        var p=Instance;var b=__instance.Bot;p?.FilterNativeGas(__instance);if(p!=null&&p.SelectGrenadeEscape(__instance))return;
        if(p==null||!p.OwnScope(b)||!p.states.TryGetValue(b,out var s))return;
        var r=s.OwnGrenade;if(r.Fault||r.Tracker==null||r.Selected==null||r.Selected.Evidence.Exploded)return;
        // The native 18 m selection limit and aggressive Push reaction are inappropriate for an own rebound.
        // Preserve a closer enemy grenade; G05 will cover its richer reactions.
        if(__instance.Reaction==EGrenadeReaction.Scatter&&__instance.DangerGrenade!=null&&!ReferenceEquals(__instance.DangerGrenade,r.Tracker)
            &&Vector3.Distance(b.Position,__instance.DangerGrenade.DangerPoint)<Vector3.Distance(b.Position,r.Tracker.DangerPoint))return;
        try{ownDangerSetter.Invoke(__instance,new object[]{r.Tracker});ownReactionSetter.Invoke(__instance,new object[]{EGrenadeReaction.Scatter});}
        catch(Exception ex){p.OwnSafetyError(b,ex);}
    }
    private bool OwnThreatens(BotComponent b,Vector3 point,out Vector3 newest)
    {
        newest=Vector3.zero;
        if(!OwnScope(b))return false;
        SampleOwnProjectiles();
        bool danger=false;
        foreach(var p in ownProjectiles)
        {
            var e=p.Evidence;
            if(e.Owner!=b.ProfileId||e.Exploded||e.Expired(Time.time))continue;
            newest=VP(e.Position);
            if((DP(point)-e.Position).Length<=6&&!OwnSolidCover(DP(point),e.Position))danger=true;
        }
        return danger;
    }
    private bool OwnSolidCover(DoorPoint point,DoorPoint grenade)
        =>Physics.Linecast(VP(grenade)+Vector3.up*.3f,VP(point)+Vector3.up*1.2f,LayersMaskController.HighPolyWithTerrainMask);
    private void DropOwnTracker(BotComponent? b,OwnGrenadeRuntime r)
    {
        var reaction=b?.Grenade?.GrenadeReactionClass;
        if(reaction!=null&&r.Tracker!=null&&ReferenceEquals(reaction.DangerGrenade,r.Tracker))
        {ownDangerSetter.Invoke(reaction,new object?[]{null});ownReactionSetter.Invoke(reaction,new object[]{EGrenadeReaction.None});}
        r.Tracker=null;r.Selected=null;
    }
    private void DropOwnSafety(BotComponent? b,BotState s,string reason)
    {DropOwnTracker(b,s.OwnGrenade);s.OwnGrenade.Escaping.Clear();s.OwnGrenade.CheckedPath=null;EndOwnTrace(s,reason);}
    private void ClearOwnSafety()
    {foreach(var pair in states)DropOwnSafety(pair.Key,pair.Value,"own-grenade-raid-ended");ownProjectiles.Clear();ownSampleAt=0;}
    private void OwnSafetyError(BotComponent b,Exception ex)
    {if(states.TryGetValue(b,out var s)){if(!s.OwnGrenade.Fault)Logger.LogWarning("G04 yielded to SAIN: "+ex.GetBaseException().Message);s.OwnGrenade.Fault=true;DropOwnSafety(b,s,"own-grenade-adapter-error");}}
    private void EndOwnTrace(BotState s,string reason)
    {if(s.OwnGrenade.Trace is not { } t||t.State=="ended")return;t.State="ended";t.Reason=reason;t.ValidFor=0;WriteOwnTrace(s,true);}
    private void RecordOwnSafety(BotComponent b,BotState s,OwnProjectile p,string state,string reason,bool route,bool escape,bool force=false)
    {
        var e=p.Evidence;s.OwnGrenade.Trace=new(){State=state,Reason=reason,Owner=e.Owner,Item=e.Item,Projectile=e.Projectile,Source=e.Owner==b.ProfileId?"own-throw":"observed-squad-throw",
            Outcome=e.Status,X=e.Position.X,Y=e.Position.Y,Z=e.Position.Z,Radius=e.Radius,Distance=(DP(b.Position)-e.Position).Length,Age=Math.Max(0,Time.time-e.ThrownAt),
            Active=ownProjectiles.Count(x=>!x.Evidence.Exploded&&x.Informed.Contains(b.ProfileId)),RouteBlocked=route,Escape=escape,ValidFor=state=="released"?0:.5,
            Alternatives=new[]{"own projectile: source six-metre danger check","escape: native SAIN until ten metres away","live object ends or expires after eight seconds"}};WriteOwnTrace(s,force);
    }
    private void WriteOwnTrace(BotState s,bool force=false)
    {
        var r=s.OwnGrenade;var t=r.Trace;if(t==null)return;string key=t.State+"|"+t.Reason+"|"+t.Owner+"|"+t.Projectile;
        if(!force&&key==r.Key&&Time.time-r.LogAt<.4f)return;r.Key=key;r.LogAt=Time.time;
        var json=Diagnostics.Serialize(new{schema=1,session,sequence=++sequence,feature="G04",stage="evaluated",decision="OwnGrenadeSafety",reason=t.Reason,
            realtime=Time.realtimeSinceStartup,utc=DateTime.UtcNow.ToString("O"),configRevision=config?.Revision??0,profileId=s.ProfileId,name=s.Name,botInstance=s.Id,
            ownGrenadeSafety=t,ownGrenadeSafetyConfig=config?.OwnGrenadeSafety});
        diagnostics?.Write(json);Api.Decisions.Publish(json,ex=>{if(!subscriberWarned){subscriberWarned=true;Logger.LogWarning(ex.Message);}});
    }
}
