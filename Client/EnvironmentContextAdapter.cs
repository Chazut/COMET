using System;
using Comet.Core;
using EFT;
using Newtonsoft.Json;
using SAIN.Components;
using UnityEngine;

namespace Comet.Client;

public sealed partial class Plugin
{
    private readonly EnvironmentProbeBudget environmentBudget=new();
    private readonly RaycastHit[] environmentHits=new RaycastHit[16];
    private readonly System.Diagnostics.Stopwatch environmentClock=new();

    // A consumer can run after Update or after movement invalidated the cached sample.
    // Refresh the cheap native flags before reading; this does not request a ceiling raycast.
    private EnvironmentTrace ReadEnvironment(BotComponent bot,BotState s,string consumer)
    {
        RefreshEnvironment(bot,s);
        var p=bot.Position;
        return s.Environment.Read(Time.time,p.x,p.y,p.z,config!.EnvironmentContext,consumer);
    }

    // Called only from existing combat state updates or an actual grenade preparation.
    // P10 observes context; it never changes native indoor flags, grenade arcs or action scores.
    internal void RefreshEnvironment(BotComponent bot,BotState s,bool ceiling=false)
    {
        if(config==null||!config.Enabled||!config.EnvironmentContext.Enabled||s.EnvironmentFault
            ||!NativeAdapter.IsSupportedAi(bot)||!bot.BotActive||bot.GameEnding||!Authority||Ghost(bot.ProfileId)||bot.GoalEnemy==null)
        {s.Environment.Clear();return;}
        try
        {
            var c=config.EnvironmentContext;var p=bot.Position;double now=Time.time;
            bool changed=s.Environment.NeedsSample(now,p.x,p.y,p.z,c);
            if(changed)
            {
                var data=bot.Player.AIData;
                bool? zone=data==null?null:data.EnvironmentId!=0;
                bool? eft=bot.Player.Environment==EnvironmentType.Indoor?true:bot.Player.Environment==EnvironmentType.Outdoor?false:null;
                s.Environment.Observe(now,p.x,p.y,p.z,zone,eft);
            }
            if(ceiling&&s.Environment.NeedsCeiling(now,p.x,p.y,p.z,c))
            {
                var eye=bot.Transform.EyePosition;
                CeilingResult result;
                if(!ImpactReaction.Finite(eye.x)||!ImpactReaction.Finite(eye.y)||!ImpactReaction.Finite(eye.z))
                    result=new("uncertain","invalid-probe-origin");
                else if(!environmentBudget.TryBegin(Time.frameCount))result=new("deferred","probe-budget-deferred");
                else
                {
                    environmentClock.Restart();
                    try
                    {
                        int n=Physics.RaycastNonAlloc(eye,Vector3.up,environmentHits,(float)c.CeilingDistance,
                            LayersMaskController.HighPolyWithTerrainMask,QueryTriggerInteraction.Ignore);
                        int nearest=-1;float distance=float.PositiveInfinity;
                        for(int i=0;i<n;i++)if(environmentHits[i].distance<distance){nearest=i;distance=environmentHits[i].distance;}
                        var h=nearest<0?default:environmentHits[nearest];var col=h.collider;
                        bool structural=col!=null&&col.attachedRigidbody==null&&(col is MeshCollider||col is BoxCollider);
                        result=CeilingClassifier.Classify(n>=environmentHits.Length,nearest>=0,structural,h.distance,h.normal.y,c.CeilingDistance);
                    }
                    finally
                    {
                        Array.Clear(environmentHits,0,environmentHits.Length);
                        environmentClock.Stop();environmentBudget.Charge(environmentClock.Elapsed.TotalMilliseconds);
                    }
                }
                s.Environment.ObserveCeiling(now,p.x,p.y,p.z,c,result);changed=true;
            }
            if(changed)RecordEnvironment(s,s.Environment.Read(now,p.x,p.y,p.z,c,ceiling?"grenade-preparation":"combat-context"));
        }
        catch(Exception ex)
        {
            s.Environment.Clear();s.EnvironmentFault=true;
            Logger.LogWarning("COMET P10 context unavailable for "+s.ProfileId+": "+ex.Message);
        }
    }
    private void RecordEnvironment(BotState s,EnvironmentTrace? trace)
    {
        if(trace==null)return;
        // Preserve every refreshed observation. Suppress only identical same-frame duplicates;
        // a later refresh must renew RR's validity even when the classification is unchanged.
        string key=trace.Reported+":"+trace.Ceiling+":"+trace.ProbeReason;
        if(key==s.EnvironmentLogKey&&Time.time==s.EnvironmentLogAt)return;
        s.EnvironmentLogKey=key;s.EnvironmentLogAt=Time.time;
        var json=Diagnostics.Serialize(new{schema=1,session,sequence=++sequence,feature="P10",stage="evaluated",
            decision="EnvironmentContext",reason=trace.Conflict?"environment-conflicting-reports":"environment-observed",
            realtime=Time.realtimeSinceStartup,utc=DateTime.UtcNow.ToString("O"),configRevision=config?.Revision??0,
            environmentConfig=config?.EnvironmentContext,profileId=s.ProfileId,name=s.Name,botInstance=s.Id,environmentContext=trace});
        diagnostics?.Write(json);Api.Decisions.Publish(json,ex=>{if(!subscriberWarned){subscriberWarned=true;Logger.LogWarning(ex.Message);}});
    }
    private void ClearEnvironment()
    { environmentBudget.Clear();Array.Clear(environmentHits,0,environmentHits.Length);environmentClock.Reset(); }
}
