using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Comet.Core;

namespace Comet.Client;

public sealed partial class Plugin
{
    private void StartPerformance(CometConfig c)
    {
        if(!c.Enabled)return;
        var log=Logger;
        var sink=PerformanceCapture.Start(c.Performance,Path.Combine(BepInEx.Paths.PluginPath,"..","COMET","performance"),session,c.Revision,world?.LocationId??"unknown",Authority,message=>log.LogWarning(message));
        if(sink==null)return;
        if(diagnostics!=null)
        {
            diagnostics.PerformanceAllocation=sink.Allocated;
            Volatile.Write(ref diagnostics.PerformanceSample,sink.Add);
        }
        Logger.LogInfo("COMET performance capture enabled for this raid. Reports: BepInEx/COMET/performance");
    }
    partial void StopPerformance(Task? diagnosticsDone) => PerformanceCapture.Stop(diagnosticsDone);
}
