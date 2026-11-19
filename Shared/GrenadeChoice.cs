using System;
using System.Collections.Generic;

namespace Comet.Core;

public sealed class GrenadeChoiceConfig
{
    public bool Enabled { get; set; } = true;
    public bool PreferShortIndoors { get; set; } = true;
    public double ShortFuseDistance { get; set; } = 20;
    public double MinimumFuse { get; set; } = 2.5;
    public double ArrivalMargin { get; set; } = .75;

    public void Validate()
    {
        CometConfig.Range(ShortFuseDistance, 5, 40, nameof(ShortFuseDistance));
        CometConfig.Range(MinimumFuse, 2.5, 5, nameof(MinimumFuse));
        CometConfig.Range(ArrivalMargin, .5, 3, nameof(ArrivalMargin));
    }
}

public sealed class GrenadeChoiceAlternative
{
    public string Item = "",
        Template = "",
        Name = "",
        Reason = "",
        Category = "fragmentation";
    public double? Fuse;
    public bool Eligible;
}

public sealed class GrenadeChoiceTrace
{
    public string State = "blocked",
        Reason = "choice-no-safe-item",
        Target = "",
        Source = "",
        Item = "",
        Template = "",
        Name = "",
        Preference = "";
    public double KnownX,
        KnownY,
        KnownZ,
        Distance,
        Flight,
        Fuse,
        Margin,
        RequiredMargin,
        ValidFor = .75;
    public List<GrenadeChoiceAlternative> Alternatives = new();
}

public static class GrenadeChoiceRules
{
    // The inspected native helper stores a 1.3-scaled force. This is its arc's
    // estimated flight time, not a measurement of the released PhysX projectile.
    public static double? Flight(double horizontal, double force, double angle)
    {
        if (
            !Finite(horizontal)
            || horizontal < .1
            || horizontal > 80
            || !Finite(force)
            || force <= 0
            || !Finite(angle)
            || angle <= 0
            || angle >= 90
        )
            return null;
        double t = horizontal / ((force / 1.3) * Math.Cos(angle * Math.PI / 180));
        return Finite(t) && t > 0 && t <= 30 ? t : null;
    }

    public static string Reject(
        double fuse,
        double contactDelay,
        bool dummy,
        bool fragmentation,
        double flight,
        GrenadeChoiceConfig c
    )
    {
        if (dummy)
            return "dummy-item";
        if (!fragmentation)
            return "not-timed-fragmentation";
        if (!Finite(contactDelay) || contactDelay >= 0)
            return "contact-explosion";
        if (!Finite(fuse) || fuse < c.MinimumFuse)
            return "fuse-outside-safe-range";
        // Flight estimates are diagnostic; zzap selects by fuse, distance and room context.
        return "";
    }

    public static GrenadeChoiceAlternative? Choose(
        IReadOnlyList<GrenadeChoiceAlternative> choices,
        bool shortest,
        string current
    )
    {
        GrenadeChoiceAlternative? best = null;
        foreach (var a in choices)
        {
            if (!a.Eligible || !a.Fuse.HasValue || !Finite(a.Fuse.Value))
                continue;
            if (
                best == null
                || (shortest ? a.Fuse < best.Fuse : a.Fuse > best.Fuse)
                || a.Fuse == best.Fuse
                    && (
                        a.Item == current && best.Item != current
                        || a.Item != current
                            && best.Item != current
                            && string.CompareOrdinal(a.Item, best.Item) < 0
                    )
            )
                best = a;
        }
        return best;
    }

    public static bool Finite(double n) => !double.IsNaN(n) && !double.IsInfinity(n);
}
