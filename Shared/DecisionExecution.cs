using System;

namespace Comet.Core;

// An accepted decision is not evidence that its native action has started.
// The timer is independent of PlanProgress.Owned and does not reset on repeated accepts.
public sealed class DecisionExecution
{
    public const double ObservationTimeout = 2;
    public const double RetryDelay = 3;
    public string Phase { get; private set; } = "idle";
    public string Action { get; private set; } = "";
    public string Target { get; private set; } = "";
    public double MissingFor { get; private set; }
    public double RetryAt { get; private set; }
    private string feature = "";
    private double last = -1;
    private bool observed, missingReported;
    public bool Active => Phase is "accepted" or "executing" or "unobserved" or "paused";

    public void Accept(double now, string feature, string action, string target)
    {
        if (Active && this.feature == feature && Action == action && Target == target) return;
        this.feature = feature; Action = action; Target = target;
        MissingFor = 0; last = now; observed = missingReported = false; Phase = "accepted";
    }

    public string Sample(double now, bool executed, bool paused)
    {
        if (!Active) return "";
        if (double.IsNaN(now) || double.IsInfinity(now) || now < last) { Clear(); return ""; }
        double dt = Math.Min(.5, Math.Max(0, now - last)); last = now;
        if (paused) { Phase = "paused"; return ""; }
        if (executed)
        {
            bool resumed = observed && MissingFor > 0;
            bool first = !observed; observed = true; MissingFor = 0; missingReported = false; Phase = "executing";
            return first ? "execution-observed" : resumed ? "execution-observed-again" : "";
        }
        // Unknown/foreign execution never counts as progress or success.
        MissingFor += dt; Phase = observed ? "unobserved" : "accepted";
        if (MissingFor < ObservationTimeout || missingReported) return "";
        missingReported = true; // Telemetry only: no cancellation, blacklist or retry lock.
        return observed ? "execution-observation-lost" : "execution-start-not-observed";
    }

    public bool CoolingDown(double now) => now < RetryAt;
    public void Clear() { Phase = "idle"; feature = Action = Target = ""; MissingFor = 0; last = -1; observed = false; }
}

public static class TacticalPriority
{
    // Retreats are deliberately excluded: squad safety may be using them.
    // Unknown enum values are also left to SAIN rather than being treated as ordinary tactics.
    public static bool OrdinarySquad(string squad) => squad is "Surround" or "Suppress" or "PushSuppressedEnemy"
        or "Regroup" or "SpreadOut" or "HoldPositions" or "Help" or "Search" or "GroupSearch";
}
