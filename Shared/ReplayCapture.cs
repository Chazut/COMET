namespace Comet.Core;

// Set by the AI host once per raid. The replay boundary holds no game objects.
internal static class ReplayCapture
{
    internal static bool Extended { get; private set; }
    internal static bool Essential { get; private set; }
    internal static bool Detailed { get; private set; }
    internal static bool RaidReview { get; private set; }

    internal static void Configure(bool essential, bool extended, bool detailedFile, bool raidReview)
    {
        Extended = extended;
        Essential = essential || extended;
        Detailed = extended || detailedFile;
        RaidReview = Essential && raidReview;
    }

    internal static bool MainDecision(string feature, string stage) =>
        feature is "S10" or "S11" || (RaidReview && feature != "A13" && IsActionStage(stage));
    internal static bool IsActionStage(string stage) =>
        stage is "selected" or "accepted" or "released" or "rejected" or "started";
}
