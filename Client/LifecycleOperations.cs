using System;
using System.Collections.Generic;
using System.Linq;
using Comet.Core;
using SAIN.Components;

namespace Comet.Client;

public sealed partial class Plugin
{
    private void ResetLifecycleDecisions(BotState s, string reason)
    {
        s.Engine.Release(reason);
        s.VisibleEngine.Release(reason);
        s.Intent.Clear();
        s.Handoff.Clear();
        s.Sight.Reset(reason);
        s.Probe = null;
        s.SelectedCover = null;
        s.SightPath = null;
    }

    private LifecycleTrace ForgetLifecycleOperations(BotComponent b, BotState s)
    {
        // Called only from the top-level lifecycle sweep, never inside a collection's update.
        bool old = faultCleaning,
            oldFailed = faultCleanupFailed;
        var oldError = faultCleanupError;
        faultCleaning = true;
        var trace = new LifecycleTrace
        {
            State = "disabled",
            Reason = "lifecycle-pending-cleanup-failed",
        };
        var failures = new List<string>();
        void Step(string name, Action call)
        {
            trace.CleanupSteps++;
            faultCleanupFailed = false;
            faultCleanupError = null;
            FaultCleanup(call);
            if (faultCleanupFailed)
            {
                trace.FailedSteps++;
                failures.Add(BoundLife(name + ": " + faultCleanupError?.Message));
                SafeFaultLog("C09 " + name + ": " + faultCleanupError);
            }
        }
        try
        {
            Step("CoverScan", () =>
            {
                if (b != null && b.Cover?.CoverFinder != null)
                    StopCoverScan(b.Cover.CoverFinder);
            });
            Step("DoorSwing", () =>
            {
                doorSwings.RemoveAll(x => ReferenceEquals(x.Bot, b));
                motionWatches.Remove(b);
            });
            Step("DoorClosing", () =>
            {
                closingSelected.Remove(b);
                closingPending.RemoveAll(x => ReferenceEquals(x.Bot, b));
                closingLogs.Remove(b);
            });
            Step("SquadClaims", () =>
            {
                crossfireClaims.Release(s.Id);
                coverMateBoard.Release(s.Id);
            });
            foreach (var pair in supportOperations.ToArray())
                if (ReferenceEquals(pair.Value.Leader, b))
                {
                    Step("DoorSupport", () =>
                        EndSupportOperation(pair.Value, "lifecycle-owner-bot-ended")
                    );
                    supportOperations.Remove(pair.Key);
                }
        }
        finally
        {
            faultCleaning = old;
            faultCleanupFailed = oldFailed;
            faultCleanupError = oldError;
        }
        trace.Failures = failures.Take(8).ToArray();
        return trace;
    }
}
