using System;
using System.Collections.Generic;

namespace Comet.Core;

public sealed class DoorRoomClearConfig
{
    public bool Enabled { get; set; } = true;
    public double RoomClearChance { get; set; } = 45;
    public Dictionary<string, double> Chances { get; set; } =
        new()
        {
            ["GigaChad"] = 40,
            ["Chad"] = 30,
            ["Wreckless"] = 35,
            ["Normal"] = 15,
            ["Rat"] = 0,
            ["SnappingTurtle"] = 0,
            ["Coward"] = 0,
            ["Timmy"] = 0,
        };
    public double ContactLifetime { get; set; } = 60;
    public const double DefaultGrenadePreference = 55;
    public double GrenadePreference { get; set; } = DefaultGrenadePreference;
    public bool AllowFeint { get; set; } = true;
    public double MinimumAmmo { get; set; } = 50;
    public double MinimumStamina { get; set; } = 35;
    public double DepthMin { get; set; } = 2;
    public double DepthMax { get; set; } = 3;
    public double BlastPauseMin { get; set; } = .3;
    public double BlastPauseMax { get; set; } = .7;
    public double TargetCooldown { get; set; } = 60;

    public void Validate()
    {
        CometConfig.Range(RoomClearChance, 0, 100, nameof(RoomClearChance));
        if (Chances == null || Chances.Count != 8)
            throw new ArgumentException("Missing room entry personality chances.");
        foreach (
            var p in new[]
            {
                "GigaChad",
                "Chad",
                "Wreckless",
                "Normal",
                "Rat",
                "SnappingTurtle",
                "Coward",
                "Timmy",
            }
        )
            if (!Chances.TryGetValue(p, out var v))
                throw new ArgumentException("Missing room entry chance: " + p);
            else
                CometConfig.Range(v, 0, 100, p);
        CometConfig.Range(ContactLifetime, 3, 120, nameof(ContactLifetime));
        CometConfig.Range(GrenadePreference, 0, 100, nameof(GrenadePreference));
        CometConfig.Range(MinimumAmmo, 25, 100, nameof(MinimumAmmo));
        CometConfig.Range(MinimumStamina, 20, 80, nameof(MinimumStamina));
        CometConfig.Range(DepthMin, 1.5, 3, nameof(DepthMin));
        CometConfig.Range(DepthMax, DepthMin, 4, nameof(DepthMax));
        CometConfig.Range(BlastPauseMin, .2, 1, nameof(BlastPauseMin));
        CometConfig.Range(BlastPauseMax, BlastPauseMin, 1.5, nameof(BlastPauseMax));
        CometConfig.Range(TargetCooldown, 30, 180, nameof(TargetCooldown));
    }
}

// Snapshot taken when the door is selected, before the bot moves to it.
public sealed class DoorClearPlan
{
    public double GrenadeWeight { get; }
    public double DirectWeight { get; }
    public bool Moving { get; }
    public bool Camping { get; }
    public double EnemyDepth { get; }
    public double Weakness { get; }
    public bool HealthyRusher { get; }
    public double Roll { get; }
    public bool RealGrenade { get; }
    public int Feints { get; }

    public DoorClearPlan(bool haveGrenade, double preference, bool allowFeints, bool moving,
        double contactAge, double enemyDepth, double weakness, bool healthyRusher,
        double variantRoll, double feintRoll)
    {
        Moving = moving;
        Camping = !moving && contactAge > 3;
        EnemyDepth = enemyDepth;
        Weakness = weakness;
        HealthyRusher = healthyRusher;
        GrenadeWeight = haveGrenade ? preference / 55 : 0;
        DirectWeight = 1;
        if (haveGrenade && preference > 0)
        {
            if (Camping) GrenadeWeight += 1.5;
            if (enemyDepth >= 2.5) GrenadeWeight += 1;
            else if (enemyDepth < 1.5 && Camping)
            {
                GrenadeWeight += 1;
                DirectWeight *= .3;
            }
        }
        if (moving) DirectWeight += 1.5;
        if (weakness >= .5) DirectWeight += 1;
        if (healthyRusher) DirectWeight += .5;
        Roll = variantRoll;
        RealGrenade = variantRoll * (GrenadeWeight + DirectWeight) < GrenadeWeight;
        if (haveGrenade && allowFeints && !moving)
            Feints = Camping ? (feintRoll < .2 ? 0 : feintRoll < .65 ? 1 : 2)
                : (feintRoll < .5 ? 0 : 1);
    }
}

public sealed class DoorClearTrace
{
    public string Mode { get; set; } = "recent";
    public double BaseChance { get; set; }
    public double AgeFactor { get; set; } = 1;
    public double SelectionContactAge { get; set; }
    public double EvidenceAt { get; set; }
    public double ContactLimit { get; set; }
    public double FadeFrom { get; set; }
    public double FadeUntil { get; set; }
    public string State { get; set; } = "preparing";
    public string Reason { get; set; } = "clear-selected";
    public string Door { get; set; } = "";
    public string Target { get; set; } = "";
    public string Source { get; set; } = "";
    public string Personality { get; set; } = "";
    public double Chance { get; set; }
    public double Roll { get; set; }
    public DoorClearPlan? Plan { get; set; }
    public double VariantRoll => (Plan?.Roll ?? 0) * 100;
    public string Variant { get; set; } = "undecided";
    public double ContactAge { get; set; }
    public double KnownX { get; set; }
    public double KnownY { get; set; }
    public double KnownZ { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public bool CoveredDestination { get; set; }
    public bool DoorOpenObserved { get; set; }
    public bool FeintAttempted => FeintsStarted > 0;
    public int FeintsStarted { get; set; }
    public int FeintsCompleted { get; set; }
    public string Feint { get; set; } = "none";
    public string FeintOutcome { get; set; } = "not-attempted";
    public bool ThrowRequested { get; set; }
    public bool ThrowAccepted { get; set; }
    public string GrenadeItem { get; set; } = "";
    public int? Projectile { get; set; }
    public bool ProjectileObserved { get; set; }
    public bool ExplosionConfirmed { get; set; }
    public string GrenadeOutcome { get; set; } = "not-thrown";
    public double DangerRadius { get; set; }
    public double BlastPause { get; set; }
    public double FuseEstimate { get; set; }
    public bool MovementRequested { get; set; }
    public bool MovementAccepted { get; set; }
    public bool MovementObserved { get; set; }
    public bool ThresholdCrossed { get; set; }
    public bool Arrived { get; set; }
    public bool WeaponReady { get; set; }
    public double Elapsed { get; set; }
    public double ValidFor { get; set; } = 1.5;
    public string[] Alternatives { get; set; } = Array.Empty<string>();
}

// zzap DoorTacticClass sequence: callbacks are observed, with the source fuse and phase fallbacks.
public sealed class DoorRoomClear
{
    private readonly Dictionary<string, (double at, double evidence)> history = new();
    private double started,
        phase,
        last,
        blastAt;
    private DoorPoint moveStart;
    public DoorClearTrace? Trace { get; private set; }
    private string suspendedPhase = "";
    public bool Active =>
        Trace != null
        && Trace.State != "suspended"
        && Trace.State != "ended"
        && Trace.State != "completed";
    public bool SafeToSuspend =>
        Active
        && Trace!.State is "preparing" or "planning" or "ready" or "throw-ready" or "entry-ready"
        && !Trace.ThrowRequested;
    public double Deadline => started + 150;

    public void Suspend()
    {
        if (SafeToSuspend)
        {
            suspendedPhase = Trace!.State;
            Trace.State = "suspended";
            Trace.ValidFor = 0;
        }
    }

    public bool Resume(double now)
    {
        if (Trace?.State != "suspended" || !Valid(now) || now < last)
            return false;
        Trace.State = suspendedPhase;
        Trace.ValidFor = 1.5;
        started = phase = last = now;
        return true;
    }

    // The door session owns the admission roll and both cooldowns for all its variants.
    public bool BeginSelected(string door, string target, string source, string personality,
        DoorPoint known, double age, double now, double chance, double roll, double limit,
        DoorRoomClearConfig config, bool search, DoorClearPlan plan) =>
        !Active && config.Enabled && known.Finite && BeginCore(door, target, source, personality,
            known, age, now, roll / 100, config, chance, search ? "search" : "recent", 1, limit,
            config.ContactLifetime, limit, true, plan);

    public bool Begin(
        string door,
        string target,
        string source,
        string personality,
        DoorPoint known,
        double age,
        double now,
        double roll,
        DoorRoomClearConfig c
    )
    {
        if (
            Active
            || !c.Enabled
            || !known.Finite
            || !Valid(now)
            || !Valid(age)
            || age > c.ContactLifetime
            || !Roll(roll)
            || string.IsNullOrEmpty(door)
            || string.IsNullOrEmpty(target)
        )
            return false;
        double chance = c.Chances.TryGetValue(personality, out var v) ? v : 0;
        return BeginCore(
            door,
            target,
            source,
            personality,
            known,
            age,
            now,
            roll,
            c,
            chance,
            "recent",
            1,
            c.ContactLifetime,
            0,
            0
        );
    }

    public bool BeginSearch(
        string door,
        string target,
        string source,
        string personality,
        DoorPoint known,
        double age,
        double now,
        double roll,
        DoorRoomClearConfig c,
        DoorSearchClearConfig search,
        double memoryLimit,
        double approachLimit
    )
    {
        if (
            Active
            || !known.Finite
            || !Valid(now)
            || !Roll(roll)
            || string.IsNullOrEmpty(door)
            || string.IsNullOrEmpty(target)
            || DoorSearchClear.Gate(age, c, search, memoryLimit, approachLimit).Length > 0
        )
            return false;
        double factor = DoorSearchClear.Factor(age, c.ContactLifetime, search.ContactLifetime);
        return BeginCore(
            door,
            target,
            source,
            personality,
            known,
            age,
            now,
            roll,
            c,
            search.Chance,
            "search",
            factor,
            DoorSearchClear.Limit(search, memoryLimit, approachLimit),
            c.ContactLifetime,
            search.ContactLifetime
        );
    }

    private bool BeginCore(
        string door,
        string target,
        string source,
        string personality,
        DoorPoint known,
        double age,
        double now,
        double roll,
        DoorRoomClearConfig c,
        double baseChance,
        string mode,
        double factor,
        double limit,
        double fadeFrom,
        double fadeUntil,
        bool selected = false,
        DoorClearPlan? plan = null
    )
    {
        double evidence = now - age;
        if (!selected && history.TryGetValue(target, out var h) && now - h.at < c.TargetCooldown)
            return false;
        history[target] = (now, evidence);
        started = phase = last = now;
        double chance = baseChance * factor;
        Trace = new()
        {
            Mode = mode,
            Plan = plan,
            BaseChance = baseChance,
            AgeFactor = factor,
            SelectionContactAge = age,
            EvidenceAt = evidence,
            ContactLimit = limit,
            FadeFrom = fadeFrom,
            FadeUntil = fadeUntil,
            Door = door,
            Target = target,
            Source = source,
            Personality = personality,
            KnownX = known.X,
            KnownY = known.Y,
            KnownZ = known.Z,
            ContactAge = age,
            Chance = chance,
            Roll = roll * 100,
            Reason = mode == "search" ? "search-clear-selected" : "clear-selected",
        };
        if (!selected && roll * 100 >= chance)
            End(mode == "search" ? "search-clear-roll-declined" : "clear-roll-declined");
        return Active;
    }

    // Search preparation cannot refresh the original evidence or extend the decision engine's memory.
    public string SearchContact(
        double age,
        double now,
        bool known,
        double memoryLimit,
        double approachLimit
    )
    {
        if (Trace?.Mode != "search")
            return "";
        Trace.ContactAge = age;
        return ""; // The selected session uses its own phase/deadline, not the contact selection window.
    }

    public static bool Valid(double v) => DoorPolicy.Finite(v) && v >= 0;

    private static bool Roll(double v) => Valid(v) && v < 1;

    private void Phase(string name, string reason, double now)
    {
        Trace!.State = name;
        Trace.Reason = reason;
        phase = now;
    }

    public void Opening(double now)
    {
        if (Active && Trace!.State == "preparing")
            Phase("opening", "clear-opening-requested", now);
    }

    public void Opened(double now)
    {
        if (Active && Trace!.State is "preparing" or "opening")
        {
            Trace.DoorOpenObserved = true;
            Phase("planning", "clear-door-open-observed", now);
        }
    }

    public void Route(DoorPoint point, bool covered)
    {
        if (Active && point.Finite)
        {
            Trace!.X = point.X;
            Trace.Y = point.Y;
            Trace.Z = point.Z;
            Trace.CoveredDestination = covered;
        }
    }

    public bool HasPendingFeint => Active && Trace!.State == "planning"
        && Trace.FeintsStarted < Trace.Plan?.Feints;

    public void FeintStart(double now)
    {
        if (HasPendingFeint)
        {
            Trace!.FeintsStarted++;
            Trace.Feint = "grenade";
            Trace.FeintOutcome = "running";
            Phase("feint", "clear-feint-started", now);
        }
    }

    public void FeintEnd(bool completed, string reason, double now, bool weaponReady)
    {
        if (Active && Trace!.State == "feint")
        {
            Trace.FeintOutcome = reason;
            if (completed)
            {
                Trace.FeintsCompleted++;
                Phase(Trace.FeintsStarted < Trace.Plan!.Feints ? "planning" : "ready",
                    "clear-feint-weapon-recovered", now);
            }
            else if (weaponReady && reason is ("fake-item-or-hands-unavailable" or "fake-no-accessible-grenade"
                or "fake-missing-item" or "fake-draw-not-observed" or "fake-draw-call-failed"
                or "fake-draw-callback-failed"))
                Phase("ready", "clear-feint-draw-unavailable", now);
            else
                End("clear-feint-ended:" + reason);
        }
    }

    public void Ready(double now)
    {
        if (Active && Trace!.State == "planning")
        {
            if (HasPendingFeint) Trace.FeintOutcome = "draw-unavailable";
            Phase("ready", "clear-route-ready", now);
        }
    }

    public bool Choose(
        bool direct,
        bool grenade,
        string directWhy,
        string grenadeWhy,
        double pauseRoll,
        double now,
        DoorRoomClearConfig c
    )
    {
        if (!Active || Trace!.State != "ready" || Trace.Plan is not { } plan || !Roll(pauseRoll))
            return false;
        if (c.GrenadePreference <= 0)
        {
            grenade = false;
            grenadeWhy = "disabled by preset";
        }
        else if (plan.GrenadeWeight == 0)
        {
            grenade = false;
            grenadeWhy = "no real grenade in initial plan";
        }
        Trace.Alternatives = new[]
        {
            "Direct entry: " + (direct ? "eligible" : directWhy),
            "Grenade entry: " + (grenade ? "eligible" : grenadeWhy),
            "Resume native combat/search if the selected plan becomes invalid",
        };
        if (!direct && !grenade)
        {
            End("clear-no-safe-variant");
            return false;
        }
        Trace.Variant = grenade && (!direct || plan.RealGrenade) ? "grenade" : "direct";
        Trace.BlastPause = c.BlastPauseMin + (c.BlastPauseMax - c.BlastPauseMin) * pauseRoll;
        Phase(
            Trace.Variant == "grenade" ? "throw-ready" : "entry-ready",
            "clear-variant-selected",
            now
        );
        return true;
    }

    public void ThrowRequest(string item, double fuse, double radius, double now)
    {
        if (!Active || Trace!.State != "throw-ready")
            return;
        if (string.IsNullOrEmpty(item) || !Valid(fuse) || !Valid(radius))
        {
            End("clear-unknown-grenade-properties");
            return;
        }
        Trace.GrenadeItem = item;
        Trace.FuseEstimate = fuse;
        Trace.DangerRadius = radius;
        Trace.ThrowRequested = true;
        Trace.GrenadeOutcome = "requested";
        Phase("throwing", "clear-native-throw-requested", now);
    }

    public void ThrowResult(bool accepted, double now)
    {
        if (Trace?.ThrowRequested != true)
            return;
        Trace.ThrowAccepted = accepted;
        if (!accepted && Active)
        {
            Trace.Variant = "direct";
            Trace.GrenadeOutcome = "native-throw-refused";
            Phase("entry-ready", "clear-native-throw-refused-dash-anyway", now);
        }
        else if (accepted)
            blastAt = now + Trace.FuseEstimate + .3;
    }

    public void Projectile(string item, int id, double now)
    {
        if (
            !Active
            || Trace!.State != "throwing"
            || !Trace.ThrowRequested
            || Trace.GrenadeItem != item
        )
            return;
        Trace.Projectile = id;
        Trace.ProjectileObserved = true;
        Trace.GrenadeOutcome = "in-flight";
        Phase("waiting-blast", "clear-projectile-observed", now);
    }

    public void Explosion(int id, double now)
    {
        if (
            !Active
            || Trace!.State != "waiting-blast"
            || !Trace.ProjectileObserved
            || Trace.Projectile != id
        )
            return;
        Trace.ExplosionConfirmed = true;
        Trace.GrenadeOutcome = "explosion-confirmed";
        blastAt = now;
        Phase("post-blast", "clear-explosion-confirmed", now);
    }

    public void MissingProjectile()
    {
        if (Active && Trace!.ProjectileObserved && !Trace.ExplosionConfirmed)
            Trace.GrenadeOutcome = "not-live-using-fuse-fallback";
    }

    public void MoveRequest(DoorPoint start, double now)
    {
        if (Active && Trace!.State == "entry-ready" && start.Finite)
        {
            Trace.MovementRequested = true;
            moveStart = start;
            Phase("entering", "clear-entry-requested", now);
        }
    }

    public void MoveResult(bool accepted)
    {
        if (Trace?.MovementRequested == true)
        {
            Trace.MovementAccepted = accepted;
            if (!accepted)
                End("clear-native-move-refused");
        }
    }

    public void Movement(
        DoorPoint point,
        bool crossed,
        bool nativeComplete,
        bool weaponReady,
        double now
    )
    {
        if (!Active || Trace!.State != "entering" || !Trace.MovementAccepted)
            return;
        if (!point.Finite)
        {
            End("clear-position-invalid");
            return;
        }
        Trace.MovementObserved |= (point - moveStart).Length > .15;
        Trace.ThresholdCrossed |= crossed;
        Trace.WeaponReady = weaponReady;
        if ((point - new DoorPoint(Trace.X, Trace.Y, Trace.Z)).Length <= .5)
        {
            Trace.Arrived = true;
            Phase("completed", "clear-entry-arrival-confirmed", now);
        }
    }

    public void Tick(double now, bool weaponReady)
    {
        if (!Active)
            return;
        if (!Valid(now) || now < last)
        {
            End("clear-invalid-clock");
            return;
        }
        last = now;
        Trace!.Elapsed = now - started;
        Trace.WeaponReady = weaponReady;
        double duration = now - phase;
        if (Trace.State == "post-blast" && now - blastAt >= .3)
            Phase("entry-ready", "clear-explosion-observed", now);
        else if (Trace.State is "throwing" or "waiting-blast" && (now >= blastAt || duration > 8))
            Phase("entry-ready", "clear-fuse-fallback", now);
        else if (Trace.State == "opening" && duration > 5)
            End("clear-open-timeout");
        else if (Trace.State == "entering" && duration > 3)
            Phase("completed", "clear-dash-time-complete", now);
        else if (now - started > 150)
            End("clear-session-timeout");
    }

    public void End(string reason)
    {
        if (Trace != null && Trace.State != "completed")
        {
            Trace.State = "ended";
            Trace.Reason = reason;
        }
    }

    public void Clear()
    {
        Trace = null;
        history.Clear();
    }
}

public static class DoorClearGeometry
{
    public static double Side(DoorGeometry g, DoorPoint start) =>
        (start - g.Center).Dot(g.Normal) >= 0 ? 1 : -1;

    public static DoorPoint Destination(
        DoorGeometry g,
        DoorPoint start,
        double depth,
        double lateral
    ) => g.Center - g.Normal * (Side(g, start) * depth) + g.Axis * lateral;

    public static double Depth(DoorGeometry g, DoorPoint start, DoorPoint point) =>
        -(point - g.Center).Dot(g.Normal) * Side(g, start);

    public static string Route(
        DoorGeometry g,
        DoorPoint start,
        DoorPoint end,
        DoorPoint[] route,
        bool complete
    )
    {
        if (!complete || route == null || route.Length < 2 || !start.Finite || !end.Finite)
            return "clear-incomplete-route";
        double length = 0;
        for (int i = 0; i < route.Length; i++)
        {
            if (!route[i].Finite)
                return "clear-invalid-route";
            if (i > 0)
                length += (route[i] - route[i - 1]).Length;
        }
        return length >= 9 ? "clear-route-too-long" : "";
    }
}
