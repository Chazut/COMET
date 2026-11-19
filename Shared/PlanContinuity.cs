using System;

namespace Comet.Core;

// A missing measurement may delay reassessment, never authorize a new action.
// Acceptance and repeated callbacks cannot renew this deadline.
public sealed class PlanContinuity
{
    private double since = double.NaN,
        last = double.NaN;
    private string key = "";

    public bool Keep(double now, double limit, string plan, bool valid)
    {
        if (
            !Finite(now)
            || now < 0
            || !Finite(limit)
            || limit <= 0
            || !valid
            || plan.Length == 0
            || (Finite(last) && now < last)
        )
        {
            Clear();
            return false;
        }
        last = now;
        if (key != plan)
        {
            key = plan;
            since = now;
        }
        return now - since < limit;
    }

    public void Clear()
    {
        key = "";
        since = last = double.NaN;
    }

    private static bool Finite(double n) => !double.IsNaN(n) && !double.IsInfinity(n);
}
