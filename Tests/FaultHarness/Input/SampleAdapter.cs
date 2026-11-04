using System;
using SAIN.Components;
namespace Comet.Client;
public sealed partial class Plugin
{
    internal int WorkCalls, DependencyCalls, HealthyCalls;
    internal bool ThrowWork, ThrowCleanup;
    internal BotComponent? FailOnly;
    internal void Sample(BotComponent bot)
    {
        WorkCalls++; states[bot].OwnCommand=true;
        try { if(ThrowWork&&(FailOnly==null||ReferenceEquals(FailOnly,bot)))throw new InvalidOperationException("injected sample failure"); }
        catch(Exception) { throw new Exception("legacy handler must not swallow or relabel"); }
    }
    internal void EndSample(BotState state,string reason)
    { state.OwnCommand=false; if(ThrowCleanup)throw new InvalidOperationException("injected cleanup failure"); }
    internal void RecordSample(BotState state) { _=Newtonsoft.Json.JsonConvert.SerializeObject(new {sample=true}); throw new InvalidOperationException("injected event serialization failure"); }
}
