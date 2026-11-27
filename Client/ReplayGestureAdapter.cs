using Comet.Core;
using UnityEngine;

namespace Comet.Client;

public sealed partial class Plugin
{
    private static bool RecordingGestures => ReplayCapture.Essential && Api.Decisions.CaptureSubscribers().Length != 0;

    private void PublishGesture(BotState bot, string feature, string decision)
    {
        var json = EssentialReplay.Gesture(session, ++sequence, feature, decision, Time.realtimeSinceStartup, bot.ProfileId, bot.Name, bot.Id);
        Api.Decisions.Publish(new DiagnosticJson(json), Api.Decisions.CaptureSubscribers(), WarnFaultSink);
    }
}
