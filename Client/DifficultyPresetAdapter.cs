using System;
using Comet.Core;
using Newtonsoft.Json;
using UnityEngine;

namespace Comet.Client;

public sealed partial class Plugin
{
    // Called only when a detached raid config is accepted. Remote Fika players do
    // not choose AI settings or publish a duplicate authority context.
    private void ReportDifficultyPreset()
    {
        if (config == null || !Authority) return;
        try
        {
            var trace = DifficultyPresets.Describe(config);
            string json = Diagnostics.Serialize(new { schema=1,session,sequence=++sequence,
                feature="U01",stage="context",decision="CometPreset",reason="raid-config-loaded",
                realtime=Time.realtimeSinceStartup,utc=DateTime.UtcNow.ToString("O"),
                configRevision=config.Revision,profileId="",name="COMET difficulty",cometPreset=trace });
            diagnostics?.Write(json); Api.Decisions.Publish(json,WarnFaultSink);
            Logger.LogInfo($"U01/U07 COMET difficulty: {trace.BaseProfile}; customized={trace.Customized} ({trace.ChangedSettings} settings); revision={config.Revision}; personal preset={trace.PresetName ?? "none"}; preset revision={trace.PresetRevision}; further edits={trace.ModifiedSincePreset}. SAIN preset unchanged.");
        }
        catch (Exception ex) { SafeFaultLog("U01 preset diagnostic failed: " + ex); }
    }
}
