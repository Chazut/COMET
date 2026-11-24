using System;
using System.Runtime.CompilerServices;
using SAIN.Components;
using UnityEngine;

namespace Comet.Client;

public sealed partial class Plugin
{
    private sealed class SteeringFireObservation
    {
        internal double Next;
        internal int Checks,
            Accepted,
            Suppressed;
    }

    private readonly ConditionalWeakTable<
        BotComponent,
        SteeringFireObservation
    > steeringFireObservations = new();

    internal static void ObserveSteeringFire(
        BotComponent bot,
        TacticalSteeringKind kind,
        bool fired,
        bool suppressed
    )
    {
        if (!Comet.Core.ReplayCapture.Detailed)
            return;
        // Observation cannot interrupt native firing or change its return value.
        try
        {
            var p = Instance;
            if (p?.diagnostics == null || !p.states.TryGetValue(bot, out var state))
                return;
            var o = p.steeringFireObservations.GetValue(bot, _ => new SteeringFireObservation());
            o.Checks++;
            if (fired)
                o.Accepted++;
            if (suppressed)
                o.Suppressed++;
            double now = Time.realtimeSinceStartup;
            if (now < o.Next)
                return;
            o.Next = now + 5;
            p.diagnostics.Write(
                Diagnostics.Serialize(
                    new
                    {
                        schema = 1,
                        p.session,
                        sequence = ++p.sequence,
                        feature = "T01",
                        stage = "observed",
                        decision = "Tactical steering",
                        reason = "native-fire-check",
                        realtime = now,
                        utc = DateTime.UtcNow.ToString("O"),
                        profileId = state.ProfileId,
                        name = state.Name,
                        botInstance = state.Id,
                        steeringFire = new
                        {
                            lastAction = kind.ToString(),
                            checks = o.Checks,
                            accepted = o.Accepted,
                            suppressed = o.Suppressed,
                            target = bot.GoalEnemy?.EnemyProfileId,
                            goalVisible = bot.GoalEnemy?.IsVisible ?? false,
                            firing = bot.BotOwner.ShootData.Shooting,
                            note = "Counters cover this bot's steering callbacks since the previous sample. Native acceptance is not proof of a projectile; correlate with shots.",
                        },
                    }
                )
            );
            o.Checks = o.Accepted = o.Suppressed = 0;
        }
        catch
        { /* Diagnostics never veto gameplay. */
        }
    }
}
