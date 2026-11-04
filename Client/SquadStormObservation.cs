using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Comet.Core;
using HarmonyLib;
using Newtonsoft.Json;
using SAIN.Components;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.EnemyClasses;
using UnityEngine;
namespace Comet.Client;

internal sealed partial class SquadStormRuntime
{
    internal readonly OpponentMemory Observations=new();
    internal readonly OpportunityMemory Sounds=new();
    internal float NextObservation,DangerAt=-100;
    internal string DangerTarget="";internal int DangerInstance;
}
public sealed partial class Plugin
{
    private OpponentConfig? stormObservationConfig;
    private OpportunityConfig? stormOpportunityConfig;
    private int stormObservationFrame=-1;
    private OpponentConfig StormObservationConfig=>stormObservationConfig??=StormCopy(config!.Opponent);
    private OpportunityConfig StormOpportunityConfig=>stormOpportunityConfig??=StormCopy(config!.Opportunity);
    private static OpponentConfig StormCopy(OpponentConfig c){var clone=JsonConvert.DeserializeObject<OpponentConfig>(JsonConvert.SerializeObject(c))!;clone.Enabled=true;return clone;}
    private static OpportunityConfig StormCopy(OpportunityConfig c){var clone=JsonConvert.DeserializeObject<OpportunityConfig>(JsonConvert.SerializeObject(c))!;clone.Enabled=true;return clone;}
    private static void PatchSquadStorm(Harmony h)
    {
        var method=AccessTools.Method(typeof(EnemyEvents),nameof(EnemyEvents.EnemyHeard),new[]{typeof(SAINSoundType),typeof(bool),typeof(EnemyPlace)});
        if(method==null)throw new MissingMethodException("S06 personal sound hook");
        h.Patch(method,postfix:new HarmonyMethod(typeof(Plugin),nameof(StormHeard)));
    }
    private static void StormHeard(Enemy ___Enemy,SAINSoundType __0,EnemyPlace __2)
    {
        var p=Instance;var b=___Enemy.Bot;
        if(p?.config?.SquadStorm.Enabled!=true||!p.CrossfireScope(b)||__2==null||!ReferenceEquals(__2,___Enemy.KnownPlaces.LastHeardPlace))return;
        string cue=__0 switch {SAINSoundType.Reload=>"reload",SAINSoundType.Heal=>"heal",SAINSoundType.Surgery=>"surgery",SAINSoundType.Looting=>"looting",SAINSoundType.DryFire=>"dry-fire",SAINSoundType.Shot=>"shot",SAINSoundType.SuppressedShot=>"shot",SAINSoundType.Sprint=>"sprint",SAINSoundType.GrenadeDraw=>"grenade",SAINSoundType.GrenadePin=>"grenade",_=>""};
        if(cue.Length==0)return;
        try
        {
            if(!p.states.TryGetValue(b,out var s)){if(p.states.Count>=512)return;s=new BotState(b,p.config,p.session);p.states.Add(b,s);}
            var r=s.Crossfire.Storm;int instance=RuntimeHelpers.GetHashCode(___Enemy);var k=__2.Position;
            if(cue is "shot" or "sprint" or "grenade"){r.DangerAt=Time.time;r.DangerTarget=___Enemy.EnemyProfileId;r.DangerInstance=instance;}
            r.Sounds.Hear(___Enemy.EnemyProfileId,instance,Time.time,cue,true,k.x,k.y,k.z,p.StormOpportunityConfig);
            if(cue is "heal" or "surgery")r.Observations.InjuryCue(___Enemy.EnemyProfileId,instance,Time.time,"heard-"+cue,true);
        }catch(Exception ex){if(p.states.TryGetValue(b,out var s))p.CrossfireError(b,s,ex);}
    }
    private void ObserveStormEquipment(BotComponent b,BotState s)
    {
        if(!config!.SquadStorm.Enabled||b.GoalEnemy is not Enemy e||!CrossfireScope(b)||b.Flashed.IsFlashed)return;
        var r=s.Crossfire.Storm;if(Time.time<r.NextObservation||stormObservationFrame==Time.frameCount)return;
        var situation=NativeAdapter.Capture(b,e,true,false);var view=OpponentAdapter.View(e,situation);var c=StormObservationConfig;
        if(!OpponentMemory.CanObserve(view,c))return;
        stormObservationFrame=Time.frameCount;r.NextObservation=Time.time+.5f;
        r.Observations.Observe(e.EnemyProfileId,RuntimeHelpers.GetHashCode(e),Time.time,view,c,o=>{
            OpponentAdapter.ObserveVisibleParts(e,o,c);
            // Only inspect visible hands. A grenade/knife/unknown controller is never scored as unarmed.
            if(o.WeaponVisibleFor>=c.ObservationSeconds&&e.EnemyPlayer.HandsController is EFT.IGrenadeController)
            {r.DangerAt=Time.time;r.DangerTarget=e.EnemyProfileId;r.DangerInstance=RuntimeHelpers.GetHashCode(e);}
        });
    }
    private StormEvidence ReadStormEvidence(BotComponent b,BotState s)
    {
        var e=b.GoalEnemy!;var r=s.Crossfire.Storm;int instance=RuntimeHelpers.GetHashCode(e);
        var observed=r.Observations.Snapshot(e.EnemyProfileId,instance,Time.time,StormObservationConfig);
        var observations=new List<(string kind,string value,double at)>();foreach(var o in observed.Evidence)if(o.Active)observations.Add((o.Kind,o.Value,o.ObservedAt));
        var cue=r.Sounds.Snapshot(e.EnemyProfileId,instance,Time.time,StormOpportunityConfig);
        bool busy=cue.State=="assess";bool danger=r.DangerTarget==e.EnemyProfileId&&r.DangerInstance==instance&&Time.time-r.DangerAt<=config!.SquadStorm.ContactAge;
        // No nearby observed enemy is only an impression of isolation, not knowledge of the enemy's squad.
        bool alone=true;int count=0;
        foreach(var other in b.EnemyController.KnownEnemies)
        {if(++count>16){alone=false;break;}if(other==null||other.EnemyProfileId==e.EnemyProfileId||!other.EnemyKnown||!float.IsFinite(other.TimeSinceLastKnownUpdated)||other.TimeSinceLastKnownUpdated<0||other.TimeSinceLastKnownUpdated>config!.SquadStorm.ContactAge)continue;
         if(other.KnownPlaces.LastKnownPosition is Vector3 pos&&e.KnownPlaces.LastKnownPosition is Vector3 known&&Vector3.Distance(pos,known)<20)alone=false;}
        if(busy&&e.KnownPlaces.LastKnownPosition is Vector3 k&&Vector3.Distance(k,new((float)cue.X,(float)cue.Y,(float)cue.Z))>3)busy=false;
        return StormEvidence.Assess(Time.time,b.ProfileId,observations,alone,busy?cue.Cue:"",cue.HeardAt,busy?cue.Remaining-StormOpportunityConfig.SafetyMargin:0,danger,config!.SquadStorm);
    }
    private double StormRouteEta(BotComponent b,CrossfireRuntime r)
    {
        double speed=b.Player.MovementContext.MaxSpeed;
        if(!double.IsFinite(speed)||speed<=0)return 100000;
        double vertical=0;var corners=r.Route!.corners;for(int i=1;i<corners.Length;i++)vertical+=Math.Abs(corners[i].y-corners[i-1].y);
        // This layer walks the actual native path: no sprint credit that execution cannot deliver.
        return r.Length/(Math.Min(3,speed)*.8)+Math.Max(0,corners.Length-2)*.3+vertical*.25+.25;
    }
}
