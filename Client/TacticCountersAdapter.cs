using SAIN.Components;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes;

namespace Comet.Client;

public sealed partial class Plugin
{
    // Reuse the existing bot update and native-action observer. No new scan/raycast.
    private void SampleCounterExecution(BotComponent bot,BotState state)
    {
        if(state.CounterObserverFailed)return;
        try { ObserveCounterExecution(bot,state); }
        catch(System.Exception ex)
        {
            // A diagnostic read must never release a combat action or trip its circuit breaker.
            state.CounterObserverFailed=true;
            try { Logger.LogWarning("COMET counter observer unavailable for " + state.ProfileId + ": " + ex.Message);
                diagnostics?.Write(Newtonsoft.Json.JsonConvert.SerializeObject(new{schema=1,session,feature="T02",stage="observer-unavailable"},Diagnostics.Json)); } catch { }
        }
    }
    private void ObserveCounterExecution(BotComponent bot,BotState state)
    {
        if(config?.Enabled!=true||!config.TacticCounters.Enabled||!Authority||state.Feature is not ("A01" or "A02")
            ||state.Mixing==null||state.ActiveLabel.Length==0||!NativeAdapter.IsPmcAi(bot)||!bot.BotActive
            ||bot.IsDead||bot.GameEnding||Ghost(bot.ProfileId))return;
        if((state.CounterFeature==state.Feature&&state.CounterEncounter==state.Mixing.Encounter&&state.CounterOpportunity==state.Mixing.Opportunity)
            ||bot.Decision.CurrentCombatDecision!=state.Expected||!bot.SAINLayersActive
            ||bot.Decision.CurrentSelfDecision!=ESelfActionType.None||bot.Decision.CurrentSquadDecision!=ESquadDecision.None
            ||!OutcomeAction(bot,state.Decision))return;
        state.CounterFeature=state.Feature;state.CounterEncounter=state.Mixing.Encounter;state.CounterOpportunity=state.Mixing.Opportunity;
        Emit(state,"started","native-action-observed");
    }
}
