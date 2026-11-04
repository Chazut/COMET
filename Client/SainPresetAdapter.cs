using System;
using Comet.Core;
using Newtonsoft.Json;
using SAIN;
using UnityEngine;

namespace Comet.Client;

public sealed partial class Plugin
{
    private readonly SainPresetAudit sainPresetAudit = new();
    private float nextSainPresetSample;
    private bool sainPresetReportFailed;
    private void SampleSainPreset()
    {
        if(config == null || !Authority || Time.realtimeSinceStartup < nextSainPresetSample)return;
        nextSainPresetSample = Time.realtimeSinceStartup+1;
        try
        {
            var current = SAINPlugin.LoadedPreset;
            var trace = sainPresetAudit.Observe(current,SainPresetAccess.Describe(current),Time.realtimeSinceStartup);
            if(trace == null)return;
            string json = JsonConvert.SerializeObject(new { schema=1,session,sequence=++sequence,feature="C14",stage="context",
                decision="SainPreset",reason=trace.Reason,realtime=Time.realtimeSinceStartup,utc=DateTime.UtcNow.ToString("O"),
                configRevision=config.Revision,profileId="",name="SAIN preset",sainPreset=trace },Diagnostics.Json);
            diagnostics?.Write(json);Api.Decisions.Publish(json,WarnFaultSink);
            Logger.LogInfo($"C14 selected SAIN preset: {trace.Name}; {trace.Availability}; singleton matches={trace.SingletonMatches}; COMET revision={config.Revision}.");
        }
        catch(Exception ex)
        {
            if(sainPresetReportFailed)return;
            sainPresetReportFailed=true;SafeFaultLog("C14 preset diagnostic failed: "+ex);
        }
    }
    private void ClearSainPreset()
    {
        sainPresetAudit.Clear(); nextSainPresetSample=0; sainPresetReportFailed=false;
    }
}
