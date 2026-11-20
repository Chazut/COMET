using System;
using Comet.Core;
using SAIN.Components;
using SAIN.Models.Enums;
using SAIN.Preset.Shared.Enums;
using UnityEngine;
using UnityEngine.AI;

namespace Comet.Client;

public sealed partial class Plugin
{
    private static HandoffCue HandoffContact(Situation? s) =>
        s == null
            ? new HandoffCue()
            : new HandoffCue
            {
                Target = s.Target,
                Visible = s.Visible,
                At = s.HasContact ? s.Now - s.ContactAge : -1,
                X = s.KnownX,
                Y = s.KnownY,
                Z = s.KnownZ,
            };

    private void YieldToSain(
        BotState state,
        string reason,
        string kind = "unavailable",
        bool preserveVisiblePlan = false
    )
    {
        bool changed = state.Handoff.Begin(
            Time.time,
            reason,
            kind,
            HandoffContact(state.Situation)
        );
        if (kind == "error")
            state.FaultUntil = Time.time + (float)state.Handoff.Current.RetryRemaining;
        // Only COMET references are released. Native movement, hands and animations are left intact.
        // Native fallback does not discard zzap's cached utility ranking.
        try
        {
            Release(
                state,
                reason,
                preserveVisiblePlan: preserveVisiblePlan,
                preserveHiddenPreference: state.Feature == "A01" && reason == "no-feasible-option"
            );
        }
        finally
        {
            state.GrenadeReady = false;
            state.GrenadeTarget = default;
            state.NextGrenadeProbe = 0;
        }
        if (changed)
            Emit(state, "released", reason, "A13");
    }
}
