using System;
using System.Collections.Generic;
using System.Linq;

namespace Comet.Core;

public sealed class SquadManeuverConfig
{
    public bool Enabled { get; set; } = true;
    public bool GrenadeSuppression { get; set; } = true;
    public double Chance { get; set; } = 60;
    public double SquadDistance { get; set; } = 35;
    public double MinDistance { get; set; } = 15;
    public double MaxDistance { get; set; } = 80;
    public double ContactAge { get; set; } = 10;
    public double ConfirmTimeout { get; set; } = 3;
    public double Duration { get; set; } = 25;
    public double StallTimeout { get; set; } = 3;
    public double Retry { get; set; } = 25;
    public double MinimumAmmo { get; set; } = 50;
    public bool CautiousFlank { get; set; } = false;
    public bool RearWatch { get; set; } = true;

    public void Validate()
    {
        CometConfig.Range(Chance, 0, 100, nameof(Chance));
        CometConfig.Range(SquadDistance, 5, 80, nameof(SquadDistance));
        CometConfig.Range(MinDistance, 5, 80, nameof(MinDistance));
        CometConfig.Range(MaxDistance, MinDistance, 150, nameof(MaxDistance));
        CometConfig.Range(ContactAge, 1, 20, nameof(ContactAge));
        CometConfig.Range(ConfirmTimeout, 1, 8, nameof(ConfirmTimeout));
        CometConfig.Range(Duration, 5, 40, nameof(Duration));
        CometConfig.Range(StallTimeout, 1, 10, nameof(StallTimeout));
        CometConfig.Range(Retry, 5, 120, nameof(Retry));
        CometConfig.Range(MinimumAmmo, 1, 100, nameof(MinimumAmmo));
    }
}

public sealed class ManeuverMember
{
    public string Id = "",
        Name = "",
        Personality = "",
        Role = "",
        Status = "assigned",
        Reason = "",
        SelectionReason = "",
        MovementBlock = "";
    public double Ammo,
        Score,
        ContactDistanceSquared,
        ReadyAt = -1,
        LastSeen,
        ProgressAt,
        BestDistance = double.MaxValue,
        Shots,
        LastShot = -100;
    public bool Confirmed,
        Applied,
        VisibleContact;
}

public sealed class ManeuverTrace
{
    public string Plan = "",
        SquadId = "",
        State = "preparing",
        Role = "",
        Reason = "",
        Support = "",
        SupportName = "",
        FireState = "holding";
    public bool SupportReady,
        Confirmed;
    public double Remaining,
        Chance,
        Roll,
        RollValidUntil,
        Shots;
    public string[] Members = Array.Empty<string>();
    public ManeuverSelection[] Selection = Array.Empty<ManeuverSelection>();
}

public sealed class ManeuverSelection
{
    public string Id = "",
        Name = "",
        Role = "",
        Reason = "";
    public double Ammo,
        MinimumAmmo,
        MinimumSupportAmmo;
}

// Detached, raid-local plan: no Unity/SAIN references and no hidden enemy state.
public sealed class SquadManeuverPlan
{
    public string Id = "",
        Group = "",
        Enemy = "",
        Support = "",
        State = "preparing",
        Reason = "roles-selected";
    public double Started,
        Until,
        Evidence,
        Roll,
        RollChance,
        RollValidUntil,
        EndedAt = -1;
    public double MinimumAmmo;
    public readonly List<ManeuverMember> Members = new();
    public bool Active => State != "ended";

    public ManeuverMember? Member(string id) => Members.Find(x => x.Id == id);

    public bool MaySuppress =>
        Active
        && State == "active"
        && Members.Any(x => x.Role == "flank" && x.Status == "moving")
        && !Members.Any(x =>
            x.Role == "flank"
            && (x.Status is "holding" or "engaging" || x.Reason.Contains("enemy-visible"))
        );

    public bool SupportReady(double now)
    {
        foreach (var member in Members)
            if (member.Role == "support" && member.Confirmed && member.Applied
                && member.Status != "ended" && now - member.LastSeen < 1)
                return true;
        return false;
    }

    public bool HasProgressingFlanker(double now, SquadManeuverConfig c) =>
        Active
        && now >= Started
        && now < Until
        && Members.Any(m =>
            m.Role == "flank"
            && m.Status == "moving"
            && m.Confirmed
            && m.Applied
            && now >= m.LastSeen
            && now - m.LastSeen < 1
            && now >= m.ProgressAt
            && now - m.ProgressAt < c.StallTimeout
        );

    public void End(string why)
    {
        State = "ended";
        Reason = why;
        foreach (var m in Members)
        {
            m.Applied = false;
            m.Status = "ended";
            m.Reason = why;
        }
    }

    public void Drop(string id, string why)
    {
        var m = Member(id);
        if (m == null || !Active)
            return;
        m.Status = "ended";
        m.Applied = false;
        m.Reason = why;
        if (!Members.Any(x => x.Role == "flank" && x.Status != "ended"))
            End("maneuver-no-flanker: " + why);
    }

    public void Engage(string id, double now)
    {
        var m = Member(id);
        if (!Active || m?.Role != "flank")
            return;
        m.Status = "engaging";
        m.Reason = "crossfire-angle-engaged";
        m.Applied = false;
        m.LastSeen = now;
        // Hand the flanker back to native combat and let the squad converge.
        State = "converging";
        Reason = "maneuver-angle-engaged";
        Until = Math.Min(Until, now + 3);
    }

    public bool Tick(double now, SquadManeuverConfig c)
    {
        if (!Active)
            return false;
        if (!MedicalChoice.Finite(now) || now < Started || !c.Enabled)
        {
            End("maneuver-disabled-or-clock-reset");
            return false;
        }
        if (now >= Until)
        {
            End(State == "converging" ? "maneuver-engagement-complete" : "maneuver-timeout");
            return false;
        }
        foreach (var m in Members)
        {
            if (m.Status is "ended" or "engaging")
                continue;
            // zzap's no-show timeout concerns a flanker who never took the role.
            // A diagnostic callback gap or a missing supporter is not a failed maneuver.
            if (
                m.Role == "flank"
                && !m.Confirmed
                && now - (m.ReadyAt >= 0 ? m.ReadyAt : Started) >= c.ConfirmTimeout
                && m.Status != "planning"
            )
                Drop(m.Id, "role-not-confirmed");
        }
        if (
            Active
            && State != "converging"
            && Members.Any(x => x.Role == "flank" && x.Confirmed && x.Status != "ended")
        )
            State = "active";
        return Active;
    }

    public ManeuverTrace Trace(string id, double now, SquadManeuverConfig c)
    {
        var m = Member(id)!;
        var support = Member(Support);
        return new()
        {
            Plan = Id,
            SquadId = Group,
            State = State,
            Role = m.Role,
            Reason = m.Reason.Length > 0 ? m.Reason : Reason,
            Support = Support,
            SupportName = support?.Name ?? "",
            Confirmed = m.Confirmed,
            SupportReady = SupportReady(now),
            Remaining = Math.Max(0, Until - now),
            Chance = RollChance,
            Roll = Roll * 100,
            RollValidUntil = RollValidUntil,
            Shots = m.Shots,
            FireState =
                m.Role == "unassigned" ? "no role assigned"
                : m.Role == "rear" ? "watching away from the last known contact"
                : now - m.LastShot < .3 ? "observed support fire"
                : "holding",
            Members = Members
                .Select(x =>
                    $"{x.Name} [{x.Role}]: {x.Status}{(x.Reason.Length > 0 ? " - " + x.Reason : "")}"
                )
                .ToArray(),
            Selection = Members
                .Select(x => new ManeuverSelection
                {
                    Id = x.Id,
                    Name = x.Name,
                    Role = x.Role,
                    Reason = x.SelectionReason,
                    Ammo = x.Ammo,
                    MinimumAmmo = MinimumAmmo,
                    MinimumSupportAmmo = SquadManeuverBoard.SupportAmmo,
                })
                .ToArray(),
        };
    }
}

public sealed class SquadManeuverBoard
{
    public const string SomeoneSees = "maneuver-squad-member-sees-target";
    public const double SupportAmmo = 33;
    private readonly Dictionary<string, SquadManeuverPlan> plans = new();
    private readonly Dictionary<(string squad, string enemy), double> cooldowns = new();
    private readonly Dictionary<(string squad, string enemy), (double value, double chance, double until)> rolls = new();
    private int serial;
    private double clock = -1;
    public SquadManeuverPlan? LastAttempt { get; private set; }
    public string LastReason { get; private set; } = "not-evaluated";
    public int Count => plans.Count;

    public SquadManeuverPlan? Get(string group) => plans.TryGetValue(group, out var p) ? p : null;

    public SquadManeuverPlan? TryStart(
        string group,
        string enemy,
        double now,
        double evidence,
        double roll,
        List<ManeuverMember> candidates,
        SquadManeuverConfig c
    ) => TryStart(group, enemy, now, evidence, () => roll, candidates, c);

    public SquadManeuverPlan? TryStart(
        string group,
        string enemy,
        double now,
        double evidence,
        Func<double> draw,
        List<ManeuverMember> candidates,
        SquadManeuverConfig c
    )
    {
        LastReason = "maneuver-invalid-or-stale-evidence";
        if (
            !MedicalChoice.Finite(now)
            || !MedicalChoice.Finite(evidence)
            || now < 0
            || evidence > now
            || !c.Enabled
        )
            return null;
        if (now < clock)
            Clear();
        clock = now;
        if (plans.TryGetValue(group, out var old) && old.Active)
        {
            LastReason =
                old.Enemy == enemy ? "maneuver-existing-plan" : "maneuver-other-contact-active";
            return old.Enemy == enemy ? old : null;
        }
        if (now - evidence > c.ContactAge)
            return null;
        var key = (group, enemy);
        if (old != null)
        {
            if (old.EndedAt < 0)
                Finish(old, now, c, old.Reason);
            plans.Remove(group);
        }
        if (cooldowns.TryGetValue(key, out var cooldownUntil) && now < cooldownUntil)
        {
            LastReason = "maneuver-retry-cooldown";
            return null;
        }
        if (candidates.Exists(member => member.VisibleContact))
        {
            LastReason = SomeoneSees;
            return null;
        }
        if (candidates.Count < 2)
        {
            LastReason = "maneuver-insufficient-eligible-members";
            return null;
        }
        if (!rolls.TryGetValue(key, out var verdict) || now > verdict.until)
        {
            double value = draw();
            if (!MedicalChoice.Finite(value) || value < 0 || value >= 1)
            {
                LastReason = "maneuver-invalid-roll";
                return null;
            }
            verdict = (value, c.Chance, now + 30);
            rolls[key] = verdict;
        }
        else if (verdict.value * 100 >= verdict.chance)
        {
            LastReason = "maneuver-cached-roll-declined";
            return null;
        }
        var plan = new SquadManeuverPlan
        {
            Id = "maneuver-" + (++serial),
            Group = group,
            Enemy = enemy,
            Started = now,
            Until = now + c.Duration,
            Evidence = evidence,
            Roll = verdict.value,
            RollChance = verdict.chance,
            RollValidUntil = verdict.until,
            MinimumAmmo = c.MinimumAmmo,
        };
        foreach (var m in candidates)
        {
            m.LastSeen = now;
            m.Role = "unassigned";
            m.SelectionReason = "squad-roll-declined";
            plan.Members.Add(m);
        }
        LastAttempt = plan;
        if (verdict.value * 100 >= verdict.chance)
        {
            LastReason = "maneuver-keep-current-plan";
            plan.End(LastReason);
            return null;
        }
        string failure = AssignRoles(plan, c);
        if (failure.Length > 0)
        {
            cooldowns[key] = now + 10;
            LastReason = failure;
            plan.End(LastReason);
            return null;
        }
        LastReason = "maneuver-roles-selected";
        plans[group] = plan;
        return plan;
    }

    private static string AssignRoles(SquadManeuverPlan plan, SquadManeuverConfig c)
    {
        ManeuverMember? flanker = null;
        foreach (var m in plan.Members)
        {
            m.SelectionReason = m.MovementBlock.Length > 0 ? m.MovementBlock
                : m.Score < 0 ? "personality-holds-support"
                : !MedicalChoice.Finite(m.Ammo) || m.Ammo < c.MinimumAmmo ? "ammo-below-flank-threshold"
                : "flank-eligible";
            if (m.SelectionReason == "flank-eligible" && (flanker == null || m.Score > flanker.Score))
                flanker = m;
        }
        if (flanker == null)
            return "maneuver-no-willing-flanker";
        flanker.Role = "flank";
        flanker.SelectionReason = "highest-flank-score";

        ManeuverMember? rear = null;
        if (c.RearWatch && plan.Members.Count >= 3)
        {
            foreach (var m in plan.Members)
                if (m != flanker && (rear == null || m.ContactDistanceSquared > rear.ContactDistanceSquared))
                    rear = m;
        }
        foreach (var m in plan.Members)
        {
            if (m == flanker || m == rear)
                continue;
            if (!MedicalChoice.Finite(m.Ammo) || m.Ammo < SupportAmmo)
            {
                m.SelectionReason = "ammo-below-support-threshold";
                m.Status = "not-participating";
                continue;
            }
            m.Role = "support";
            m.SelectionReason = "base-of-fire";
            if (plan.Support.Length == 0)
                plan.Support = m.Id;
        }
        if (rear != null)
        {
            if (plan.Support.Length == 0 && MedicalChoice.Finite(rear.Ammo) && rear.Ammo >= SupportAmmo)
            {
                rear.Role = "support";
                rear.SelectionReason = "rear-reassigned-to-fire";
                plan.Support = rear.Id;
            }
            else
            {
                rear.Role = "rear";
                rear.SelectionReason = "farthest-from-contact";
            }
        }
        return plan.Support.Length == 0 ? "maneuver-no-suppressor" : "";
    }

    public void Finish(SquadManeuverPlan p, double now, SquadManeuverConfig c, string why)
    {
        if (p.EndedAt >= 0)
            return;
        if (p.Active)
            p.End(why);
        p.EndedAt = now;
        cooldowns[(p.Group, p.Enemy)] = now + c.Retry;
    }

    public static double Score(string personality, bool healthy, double ammo, SquadManeuverConfig c)
    {
        double rank =
            personality is "GigaChad" or "Chad" or "Wreckless" ? 2
            : personality == "Normal" ? 1
            : c.CautiousFlank ? 0
            : -10;
        return rank + (healthy ? .5 : 0) + ammo * .003;
    }

    public void Clear()
    {
        plans.Clear();
        rolls.Clear();
        cooldowns.Clear();
        clock = -1;
        serial = 0;
        LastAttempt = null;
        LastReason = "not-evaluated";
    }
}
