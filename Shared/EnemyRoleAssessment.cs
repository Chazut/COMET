using System;
using System.Collections.Generic;

namespace Comet.Core;

public enum ContactRole
{
    Unknown,
    Scav,
    Boss,
    Guard,
    Soldier,
}

public enum ContactFaction
{
    None,
    RUAF,
    UNTAR,
    ISB,
    BlackDivision,
    ChaosInsurgency,
    Combine,
    Rogue,
    Raider,
    Bloodhound,
    Cultist,
    OtherFaction,
}

public readonly struct EnemyIdentity
{
    public ContactRole Role { get; }
    public ContactFaction Faction { get; }
    public string? RegisteredFaction { get; }
    public bool ExpectsGroup =>
        Role is ContactRole.Boss or ContactRole.Guard or ContactRole.Soldier;

    public EnemyIdentity(
        ContactRole role,
        ContactFaction faction = ContactFaction.None,
        string? registeredFaction = null
    )
    {
        Role = role;
        Faction = faction;
        RegisteredFaction = registeredFaction;
    }

    public static EnemyIdentity Identify(
        string role,
        string? section = null,
        IReadOnlyList<string>? memberships = null
    )
    {
        if (role is "pmcBEAR" or "pmcUSEC" or "sptBear" or "sptUsec")
            return default;
        if (
            role
            is "assault"
                or "assaultGroup"
                or "marksman"
                or "cursedAssault"
                or "crazyAssaultEvent"
        )
            return new(ContactRole.Scav);
        var faction = FactionOf(role);
        if (faction == ContactFaction.None && !string.IsNullOrEmpty(section))
            faction = FactionOf(section!);
        string? registered = null;
        if (memberships != null)
        {
            foreach (var name in memberships)
            {
                if (string.IsNullOrWhiteSpace(name))
                    continue;
                registered ??= name;
                var named = FactionOf(name);
                if (
                    named != ContactFaction.None
                    && (faction == ContactFaction.None || faction == named)
                )
                {
                    faction = named;
                    registered = name;
                    break;
                }
            }
            if (registered != null && faction == ContactFaction.None)
                faction = ContactFaction.OtherFaction;
        }
        ContactRole rank = role switch
        {
            "bossBoarSniper" or "tagillaHelperAgro" or "wedgeguard" or "sectantWarrior" =>
                ContactRole.Guard,
            "wedge" or "sectantPriest" => ContactRole.Boss,
            _ when Starts(role, "boss") || Starts(role, "ISBBoss") => ContactRole.Boss,
            _ when Starts(role, "follower") || Starts(role, "ISBFollower") => ContactRole.Guard,
            _ when string.Equals(section, "Bosses", StringComparison.OrdinalIgnoreCase) =>
                ContactRole.Boss,
            _ when string.Equals(section, "Followers", StringComparison.OrdinalIgnoreCase) =>
                ContactRole.Guard,
            _ when faction != ContactFaction.None => ContactRole.Soldier,
            _ => ContactRole.Unknown,
        };
        return new EnemyIdentity(rank, faction, registered);
    }

    private static ContactFaction FactionOf(string role)
    {
        if (Starts(role, "ruaf") || Starts(role, "remnant"))
            return ContactFaction.RUAF;
        if (Starts(role, "untar") || Starts(role, "bossuntar") || Starts(role, "followeruntar"))
            return ContactFaction.UNTAR;
        if (Starts(role, "ISB"))
            return ContactFaction.ISB;
        if (Starts(role, "blackDiv") || Starts(role, "Black Division"))
            return ContactFaction.BlackDivision;
        if (Starts(role, "Combine"))
            return ContactFaction.Combine;
        if (Starts(role, "sectant"))
            return ContactFaction.Cultist;
        if (Starts(role, "chaosInsurgency") || Starts(role, "Chaos Insurgency"))
            return ContactFaction.ChaosInsurgency;
        return role switch
        {
            "exUsec" => ContactFaction.Rogue,
            "pmcBot" => ContactFaction.Raider,
            "arenaFighterEvent" => ContactFaction.Bloodhound,
            "Faction" or "Factions" => ContactFaction.OtherFaction,
            _ => ContactFaction.None,
        };
    }

    private static bool Starts(string role, string prefix) =>
        role.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
}

// A sighting belongs to this PMC. Changing targets or killing the only visible guard
// does not immediately erase what the PMC learned about that spot.
public sealed class EscortSighting
{
    private EnemyIdentity identity;
    private DoorPoint position;
    private double lastSeen = double.NegativeInfinity;

    public void Observe(EnemyIdentity observed, DoorPoint point, double when)
    {
        if (!observed.ExpectsGroup || !double.IsFinite(when) || when < lastSeen || !point.Finite)
            return;
        identity = observed;
        position = point;
        lastSeen = when;
    }

    public EnemyIdentity Near(DoorPoint contact, double now)
    {
        if (now < lastSeen || now - lastSeen > 30)
            return default;
        var offset = contact - position;
        return offset.Dot(offset) <= 35 * 35 ? identity : default;
    }
}

public sealed class EnemyRoleAssessment
{
    public EnemyIdentity Target { get; }
    public EnemyIdentity GroupCue { get; }
    public double? RaidProgress { get; }
    public double ExpectedSupport { get; }
    public int OtherContacts { get; }
    public int NearbyAllies { get; }

    public EnemyRoleAssessment(
        EnemyIdentity target,
        EnemyIdentity localCue,
        double? raidProgress,
        int otherContacts,
        int nearbyAllies
    )
    {
        Target = target;
        GroupCue = target.ExpectsGroup ? target : localCue;
        RaidProgress =
            raidProgress.HasValue && double.IsFinite(raidProgress.Value)
                ? ZzapUtility.Unit(raidProgress.Value)
                : null;
        OtherContacts = otherContacts;
        NearbyAllies = nearbyAllies;
        // The first 15% of a raid carries the full escort expectation. By 85%
        // a lone survivor is quite plausible. A missing raid clock is inconclusive.
        ExpectedSupport =
            !GroupCue.ExpectsGroup ? 0
            : RaidProgress.HasValue ? 1 - .85 * ZzapUtility.Unit((RaidProgress.Value - .15) / .70)
            : .5;
    }

    public double Caution(double aggression)
    {
        if (!GroupCue.ExpectsGroup)
            return 0;
        double support = Math.Max(ExpectedSupport, Math.Min(2, OtherContacts) * .5);
        double danger =
            GroupCue.Role == ContactRole.Boss ? .16 + .84 * support : .08 + .85 * support;
        return danger
            * (1 - .55 * ZzapUtility.Unit(aggression))
            * (1 - .15 * Math.Min(2, NearbyAllies));
    }

    public double StormChance(double baseChance, double aggression) =>
        baseChance * (1 - .75 * Caution(aggression));
}
