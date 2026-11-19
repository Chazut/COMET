using Comet.Core;
using EFT;
using SAIN.Components;
using UnityEngine;

namespace Comet.Client;

public sealed partial class Plugin
{
    // liquidwarp f2c4db5: adopt a surgery already started by the native brain.
    // Keep zzap's existing SourceSurgery policy and BHF's real operation/callbacks.
    private void AdoptExternalSurgery(BotComponent bot, BotState state)
    {
        var runtime = state.Medical;
        var kit = bot.BotOwner.Medecine.SurgicalKit;
        if (
            runtime.ExternalSurgery
            || runtime.Policy.Active
            || runtime.Interruption.Active
            || !kit.Using
        )
            return;
        runtime.ExternalSurgery = true;
        runtime.SurgeryOwned = true; // Prevent native Stop's restore-all-limbs shortcut.
        runtime.ExternalSurgeryItem = kit.CurUsingMeds?.Id.ToString()
            ?? bot.Player.HandsController?.Item?.Id.ToString()
            ?? "";
        runtime.ExternalSurgeryPart = kit._bodyPartToHeal;
        runtime.Interruption.Begin(
            Time.time,
            runtime.ExternalSurgeryItem,
            kit._bodyPartToHeal?.ToString() ?? ""
        );
        runtime.Interruption.Trace!.Origin = "native";
        runtime.Interruption.Trace.Reason = "surgery-native-operation-adopted";
        RecordSurgeryInterruption(state, true);
    }

    private void SampleExternalSurgery(BotComponent bot, BotState state)
    {
        var runtime = state.Medical;
        if (!runtime.ExternalSurgery)
            return;
        var kit = bot.BotOwner.Medecine.SurgicalKit;
        if (
            kit.Using
            && (
                kit._bodyPartToHeal != runtime.ExternalSurgeryPart
                || kit.CurUsingMeds != null
                    && kit.CurUsingMeds.Id.ToString() != runtime.ExternalSurgeryItem
            )
        )
        {
            EndSurgeryInterruption(state, "surgery-native-operation-changed");
            AdoptExternalSurgery(bot, state);
        }
        bool hands =
            bot.Player.HandsController is IMedsController
            && (
                runtime.ExternalSurgeryItem.Length == 0
                || bot.Player.HandsController.Item?.Id.ToString() == runtime.ExternalSurgeryItem
            );
        if (!kit.Using && !hands)
        {
            EndSurgeryInterruption(state, "surgery-native-operation-ended");
            return;
        }
        var frame = new SurgeryFrame
        {
            Now = Time.time,
            NativeCancelled = kit._lastIsCanceled,
            Hands = hands,
            OperationPending = kit.Using,
            Completed =
                runtime.ExternalSurgeryPart.HasValue
                && !bot.Player.HealthController.IsBodyPartDestroyed(
                    runtime.ExternalSurgeryPart.Value
                ),
            WeaponReady = WeaponHandsReady(
                bot,
                bot.Player.HandsController as Player.FirearmController
            ),
            SourceAllowed =
                !config!.SurgeryInterruption.Enabled || SourceSurgeryAllowed(bot, state),
        };
        if (runtime.Interruption.Tick(frame, config.SurgeryInterruption))
            kit.CancelCurrent(); // BHF patches this method; do not replace it or force the hands state.
        if (runtime.Interruption.Trace != null)
            runtime.Interruption.Trace.NativeCancelled |= kit._lastIsCanceled;
        RecordSurgeryInterruption(state, false);
    }
}
