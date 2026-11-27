using Comet.Core;
using UnityEngine;

namespace Comet.Client;

public sealed partial class Plugin
{
    private static bool RecordingGestures => ReplayCapture.Essential && Api.Decisions.CaptureSubscribers().Length != 0;

    private void PublishGesture(BotState bot, string feature, string decision)
        => PublishReplayAction(bot, feature, "performed", decision);

    private void ReplayActivity(BotState bot, string feature, ref string? active, string? next)
    {
        if (active == next) return;
        var previous = active;
        active = next;
        if (previous != null) PublishReplayAction(bot, feature, "activity-end", previous);
        if (next != null) PublishReplayAction(bot, feature, "activity-start", next);
    }

    private void PublishReplayAction(BotState bot, string feature, string stage, string decision)
    {
        try
        {
            var json = EssentialReplay.ActionEvent(session, ++sequence, feature, stage, decision, Time.realtimeSinceStartup, bot.ProfileId, bot.Name, bot.Id);
            Api.Decisions.Publish(new DiagnosticJson(json), Api.Decisions.CaptureSubscribers(), WarnFaultSink);
        }
        catch (System.Exception error) { WarnFaultSink(error); }
    }
}
