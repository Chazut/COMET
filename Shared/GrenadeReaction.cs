using System;
using System.Collections.Generic;

namespace Comet.Core;

public sealed class GrenadeReactionConfig
{
    public bool Enabled { get; set; } = true;
    public double DangerMargin { get; set; } = 1;
    public double ExitPadding { get; set; } = 4;
    public double ScatterDistance { get; set; } = 14;
    public double RetreatDistance { get; set; } = 22;
    public double Refresh { get; set; } = .1;
    public double Commitment { get; set; } = 1.5;

    public void Validate()
    {
        CometConfig.Range(DangerMargin, 0, 5, nameof(DangerMargin));
        CometConfig.Range(ExitPadding, 1, 8, nameof(ExitPadding));
        CometConfig.Range(ScatterDistance, 4, 30, nameof(ScatterDistance));
        CometConfig.Range(RetreatDistance, 4, 40, nameof(RetreatDistance));
        CometConfig.Range(Refresh, .05, .2, nameof(Refresh));
        CometConfig.Range(Commitment, .2, 2, nameof(Commitment));
    }
}

public sealed class GrenadeReactionTrace
{
    public string State = "planning",
        Reason = "grenade-escape-planning",
        Reaction = "Scatter",
        Personality = "Normal",
        Owner = "",
        Item = "",
        Source = "sain-detected",
        Contact = "",
        ContactSource = "";
    public int Projectile,
        Active;
    public double X,
        Y,
        Z,
        Radius,
        Distance,
        GoalX,
        GoalY,
        GoalZ,
        PathLength,
        ValidFor = .5;
    public bool HasGoal;
    public string[] Alternatives = Array.Empty<string>();
}

public static class GrenadeReactionRules
{
    public static string Preference(string personality, bool personalContact) =>
        personality switch
        {
            "Chad" or "GigaChad" or "Wreckless" => personalContact ? "Push" : "Scatter",
            "Rat" or "Coward" or "Timmy" => "Retreat",
            "SnappingTurtle" => "Relocate",
            _ => "Scatter",
        };

    public static bool PersonalContact(bool visible, string source, double age) =>
        age >= 0 && age <= 3 && (visible || source is "seen" or "heard");

    public static DoorPoint Direction(
        DoorPoint position,
        DoorPoint grenade,
        DoorPoint fallback,
        DoorPoint? contact,
        string reaction
    )
    {
        var away = new DoorPoint(position.X - grenade.X, 0, position.Z - grenade.Z);
        if (away.Length < .01)
            away = new(fallback.X, 0, fallback.Z);
        if (away.Length < .01)
            away = new(1, 0, 0);
        away = away * (1 / away.Length);
        if (reaction == "Push" && contact.HasValue)
        {
            var toward = new DoorPoint(
                contact.Value.X - position.X,
                0,
                contact.Value.Z - position.Z
            );
            // No push toward the grenade, including an opponent on its far side.
            if (toward.Length > .01 && away.Dot(toward) > 0)
            {
                toward = toward * (1 / toward.Length);
                var blend = away + toward;
                return blend * (1 / blend.Length);
            }
        }
        return away;
    }

    public static bool DestinationSafe(
        DoorPoint point,
        IReadOnlyList<OwnGrenadeEvidence> dangers,
        double padding,
        Func<DoorPoint, DoorPoint, bool> cover
    )
    {
        if (!point.Finite)
            return false;
        foreach (var g in dangers)
            if (
                !g.Exploded
                && (point - g.Position).Length < g.Radius + padding
                && !cover(point, g.Position)
            )
                return false;
        return true;
    }
}
