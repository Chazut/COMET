using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Bootstrap;
using Comet.Core;
using EFT;
using HarmonyLib;
using Newtonsoft.Json;
using SAIN.Components;
using UnityEngine;

namespace Comet.Client;

// Infrastructure boundary: explicit handling, no generated recursive lifecycle guards.
public sealed partial class Plugin
{
    private Func<string,bool>? qbSleeping;
    private bool qbPresent, lbPresent, controlUnavailable;
    private double controlNext;
    private readonly HashSet<string> controlWarnings=new();
    private sealed class ControlWatch
    {
        internal ControlHandoff Policy=new();
        internal Action Detach=()=>{};
        internal BotState State=null!;
        internal bool Cleaning;
        internal ControlJournal Journal=new();
        internal MovementObserver Movement=new();
        internal bool MovementFailed;
        internal bool JournalFailed;
        internal double JournalBudgetAt;
        internal int JournalEvents;
    }
    private readonly Dictionary<BotComponent,ControlWatch> controlWatches=new();

    private void ConnectControlIntegrations(Harmony h)
    {
        var notes=integration.Report.Notices.ToList();
        qbPresent=Chainloader.PluginInfos.ContainsKey("com.danw.questingbots");
        lbPresent=Chainloader.PluginInfos.ContainsKey("me.skwizzy.lootingbots");
        notes.Add("I02: ORBIT "+(isGhost!=null?"detected":"absent")+", Questing Bots "+(qbPresent?"detected":"absent")+", Looting Bots "+(lbPresent?"detected":"absent")+". Detection is not raid validation.");
        if(qbPresent)
        {
            // Resolve from the loaded plugin assembly, not a guessed file or a required DLL reference.
            var assembly=Chainloader.PluginInfos["com.danw.questingbots"].Instance.GetType().Assembly;
            var sleep=assembly.GetType("QuestingBots.Controllers.BotRegistrationManager")?.GetMethod("IsBotSleeping",BindingFlags.Public|BindingFlags.Static,null,new[]{typeof(string)},null);
            var recognizes=assembly.GetType("QuestingBots.BotLogic.ExternalMods.ModInfo.SAINModInfo")?.GetMethod("IsSAINLayer",BindingFlags.Public|BindingFlags.Static,null,new[]{typeof(string)},null);
            if(sleep?.ReturnType!=typeof(bool)||recognizes?.ReturnType!=typeof(bool))
            {controlUnavailable=true;notes.Add("I02: Questing Bots sleep/layer contract unavailable. COMET is suspended; update the matching builds.");}
            else
            {
                qbSleeping=(Func<string,bool>)Delegate.CreateDelegate(typeof(Func<string,bool>),sleep);
                // Optional hook has a separately checked contract. It is not a required SAIN hook.
                h.Patch(recognizes,postfix:new HarmonyMethod(AccessTools.Method(typeof(Plugin),nameof(RecognizeCometLayer))));
                notes.Add("I02: QB sleep reader and COMET layer recognition connected. QB priorities, search delays and loot/extraction settings are unchanged.");
            }
        }
        if(isGhost!=null&&(qbPresent||lbPresent))notes.Add("I02: ORBIT together with QB/LB is not a supported combination. Choose one out-of-combat setup.");
        integration.Report.Notices=notes.ToArray();
        foreach(var note in notes.Where(n=>n.StartsWith("I02:")))Logger.LogInfo(note);
    }
    private static void RecognizeCometLayer(string __0,ref bool __result)
    {
        // Exact COMET-owned names only: no native/QB names, configuration or non-PMC layers are changed.
        if(Instance?.config?.Enabled==true&&ControlHandoff.IsCometLayer(__0))__result=true;
    }
    private bool Sleeping(string profile)
    {
        if(controlUnavailable||orbitUnavailable)return true;
        try{return (isGhost?.Invoke(profile)??false)||(qbSleeping?.Invoke(profile)??false);}
        catch(Exception ex)
        {controlUnavailable=true;Logger.LogWarning("I02 sleep observation failed; COMET suspended: "+ex.GetBaseException().Message);return true;}
    }
    private ControlWatch? WatchControl(BotState s)
    {
        var b=s.Owner;
        if(s.Retired||b==null||!NativeAdapter.IsSupportedAi(b)||!Authority||config?.Enabled!=true||world==null)return null;
        if(controlWatches.TryGetValue(b,out var existing))
        {if(!ReferenceEquals(existing.State,s)){existing.State=s;existing.Policy=new();existing.Journal=new();existing.JournalFailed=false;}return existing;}
        if(controlWatches.Count>=512||b.BotOwner?.Brain?.BaseBrain==null)return null;
        var watch=new ControlWatch{State=s};var brain=b.BotOwner.Brain.BaseBrain;
        Action<AICoreLayer<BotLogicDecision>> changed=layer=>ReadControl(b,watch,layer?.Name()??"");
        brain.OnLayerChangedTo+=changed;
        watch.Detach=()=>brain.OnLayerChangedTo-=changed;
        controlWatches.Add(b,watch);
        ReadControl(b,watch,b.BotOwner.Brain.ActiveLayerName()??"");
        return watch;
    }
    private void ReadControl(BotComponent b,ControlWatch w,string name)
    {
        if(w.Cleaning||world==null||config?.Enabled!=true)return;
        try
        {
            if(!Authority||b==null||b.IsDead||!NativeAdapter.IsSupportedAi(b))return;
            string owner=Sleeping(b.ProfileId)?"sleep":ControlHandoff.Classify(name,isGhost!=null,qbPresent,lbPresent);
            if(w.State.Retired&&owner!="sleep")return;
            var trace=w.Policy.Observe(Time.time,owner,name);
            ObserveControlJournal(b,w,owner,name);
            if(trace==null)return;
            // The event is the selected layer; no claim that its next action has executed yet.
            if(!w.State.Retired&&ControlHandoff.IsOutside(owner)&&trace.PreviousOwner is "combat" or "recovery" or "sain")
            {
                w.Cleaning=true;
                try
                {
                    // Release just COMET's owned paths/intent; do not cancel medicine or native hands.
                    EndCrossfire(b,w.State,"control-owner-changed");
                    EndPostCombat(b,w.State,"control-owner-changed",false);
                    Release(w.State,"control-owner-inactive");
                    w.State.Handoff.Clear();
                }
                finally{w.Cleaning=false;}
            }
            RecordControl(w.State,trace);
        }
        catch(Exception ex){ControlWarning("layer observation",ex);}
    }
    private void SampleControlHandoffs()
    {
        if(Time.unscaledTimeAsDouble<controlNext)return;
        controlNext=Time.unscaledTimeAsDouble+.25;
        foreach(var s in states.Values.ToArray())try{WatchControl(s);}catch(Exception ex){ControlWarning("bot subscription",ex);}
        foreach(var entry in controlWatches.ToArray())
        {
            var b=entry.Key;var w=entry.Value;
            try {
                if(b==null||b.IsDead||world==null)
                {try{w.Detach();}catch(Exception ex){ControlWarning("unsubscribe",ex);}controlWatches.Remove(b!);continue;}
                ReadControl(b,w,b.BotOwner?.Brain?.ActiveLayerName()??"");
            }catch(Exception ex){ControlWarning("layer sampling",ex);}
        }
    }
    partial void ObserveControlRelease(BotState s,string reason)
    {
        try
        {
            var watch=WatchControl(s);if(watch==null||watch.Cleaning)return;
            var trace=watch.Policy.Release(Time.time,s.ActiveLabel.Length>0?s.ActiveLabel:s.PostCombat.Policy.Active||s.PostCombat.Layer||reason=="post-combat-finished"?"PostCombat":s.Decision,reason);
            watch.Journal.Release(reason);
            if(trace!=null)RecordControl(s,trace);
        }
        catch(Exception ex){ControlWarning("release observation",ex);}
    }
    partial void ClearControlHandoffs()
    {
        foreach(var w in controlWatches.Values)try{w.Detach();}catch(Exception ex){ControlWarning("unsubscribe",ex);}
        controlWatches.Clear();controlWarnings.Clear();controlNext=0;
    }
    private void ControlWarning(string step,Exception ex)
    { if(controlWarnings.Add(step)){Logger.LogWarning("I02 "+step+" unavailable: "+ex.GetBaseException().Message);ControlJournalUnavailable(step);} }
    partial void ObserveControlJournal(BotComponent b,ControlWatch watch,string owner,string layer);
    partial void ControlJournalUnavailable(string source);
    private void RecordControl(BotState s,ControlHandoffTrace trace)
    {
        try
        {
            string json=Diagnostics.Serialize(new{schema=1,session,sequence=++sequence,profileId=s.ProfileId,name=s.Name,botInstance=s.Id,
                realtime=Time.realtimeSinceStartup,utc=DateTime.UtcNow.ToString("O"),feature="I02",stage="evaluated",decision="ControlHandoff",
                reason=trace.Reason,controlHandoff=trace,configRevision=config?.Revision??0});
            diagnostics?.Write(json);Api.Decisions.Publish(json,WarnFaultSink);
        }
        catch(Exception ex){ControlWarning("diagnostic",ex);}
    }
}
