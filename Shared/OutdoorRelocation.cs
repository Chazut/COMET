using System;
using System.Collections.Generic;

namespace Comet.Core;

public sealed class OutdoorRelocationConfig
{
    public bool Enabled { get; set; } = true;
    public bool Grenade { get; set; } = true;
    public double Chance { get; set; } = 70;
    public double SightLoss { get; set; } = 1.5;
    public double MinDistance { get; set; } = 5;
    public double MaxDistance { get; set; } = 17;
    public double MaxPath { get; set; } = 26;
    public double PreferredAngle { get; set; } = 25;
    public double MoveTimeout { get; set; } = 12;
    public double SessionTimeout { get; set; } = 30;
    public double RetryDelay { get; set; } = 20;

    public void Validate()
    {
        CometConfig.Range(Chance, 0, 100, nameof(Chance));
        CometConfig.Range(SightLoss, .5, 5, nameof(SightLoss));
        CometConfig.Range(MinDistance, 3, 15, nameof(MinDistance));
        CometConfig.Range(MaxDistance, 5, 25, nameof(MaxDistance));
        CometConfig.Range(MaxPath, 5, 40, nameof(MaxPath));
        CometConfig.Range(PreferredAngle, 5, 60, nameof(PreferredAngle));
        CometConfig.Range(MoveTimeout, 3, 20, nameof(MoveTimeout));
        CometConfig.Range(SessionTimeout, 10, 45, nameof(SessionTimeout));
        CometConfig.Range(RetryDelay, 10, 90, nameof(RetryDelay));
        if (MaxDistance < MinDistance || MaxPath < MaxDistance || SessionTimeout < MoveTimeout)
            throw new ArgumentException("Relocation distance and time limits must be ordered.");
    }
}

// First contact and the single relocation roll belong to this bot/enemy pair for the raid.
public sealed class RelocationHistory
{
    private readonly Dictionary<string, bool> canRoll = new(StringComparer.Ordinal);
    public int Count => canRoll.Count;

    public void Observe(string id, bool incoming, bool seen, double seenAge)
    {
        if (id.Length == 0 || (!incoming && !seen) || canRoll.ContainsKey(id))
            return;
        canRoll.Add(id, incoming && !(seen && seenAge > .5));
    }

    public bool Eligible(string id) => canRoll.TryGetValue(id, out bool eligible) && eligible;
    public void Attempt(string id) => canRoll[id] = false;
    public void Clear() => canRoll.Clear();
}

public sealed class RelocationFrame
{
    public double Now,
        ContactAge,
        SightAge,
        Distance,
        Speed;
    public string Target = "",
        Key = "",
        Source = "",
        Block = "";
    public DoorPoint Position,
        Known;
    public bool Outdoor,
        InCover,
        Visible,
        Hurt,
        LongWeapon,
        HealthyPusher,
        MatesEngaging,
        OtherTactic,
        NativeMoveStarted,
        HandsReady,
        RouteOwned = true;
}

public sealed class RelocationCandidate
{
    public string Id = "",
        Refusal = "";
    public DoorPoint Position;
    public double Distance,
        PathLength,
        Angle,
        Exposure;
    public int Protection,
        OriginalProtection;
    public bool Safe;

    public double Score(OutdoorRelocationConfig c) => -Distance;
}

public sealed class RelocationTrace
{
    public string State = "planning",
        Reason = "",
        Target = "",
        Source = "",
        Destination = "",
        GrenadeOutcome = "not-tried",
        Action = "Freeze";
    public int Episode,
        Samples,
        ConcealedPoints,
        Candidates,
        Rejected,
        Protection,
        OriginalProtection;
    public double ValidFor,
        Chance,
        Roll = -1,
        Elapsed,
        ContactAge,
        SightAge,
        PathLength,
        Angle,
        Exposure,
        Score,
        Speed,
        DistanceMoved;
    public double X,
        Y,
        Z,
        KnownX,
        KnownY,
        KnownZ,
        DestinationX,
        DestinationY,
        DestinationZ;
    public bool Requested,
        Accepted,
        MoveRequested,
        MovementObserved,
        Arrived,
        ThrowAccepted,
        ProjectileObserved;
    public string[] Alternatives = Array.Empty<string>();
}

public sealed class OutdoorRelocation
{
    public readonly RelocationHistory History = new();
    public RelocationTrace? Trace { get; private set; }
    public bool Active => Trace?.State is "planning" or "ready" or "throwing" or "moving";
    public DoorPoint Origin,
        Known,
        Goal;
    private double started,
        movingAt,
        progressAt,
        bestRemaining,
        lastTime,
        throwAt;
    private double releaseAt = -1;
    private int episode;

    public static bool Valid(double v) => DoorPolicy.Finite(v) && v >= 0;

    public static string Gate(RelocationFrame f, OutdoorRelocationConfig c, bool active = false)
    {
        if (!c.Enabled)
            return "relocate-disabled";
        if (f.Block.Length > 0)
            return f.Block;
        if (
            !Valid(f.Now)
            || !Valid(f.ContactAge)
            || !Valid(f.SightAge)
            || !f.Position.Finite
            || !f.Known.Finite
            || !Valid(f.Speed)
        )
            return "relocate-invalid-context";
        if (f.Target.Length == 0 || !active && (f.Source.Length == 0 || f.ContactAge > 30))
            return "relocate-no-fresh-known-contact";
        if (!active && f.OtherTactic)
            return "relocate-other-tactic";
        if (!active && !f.Outdoor)
            return "relocate-outdoor-context-required";
        if (f.Visible && !(active && f.Distance > 20))
            return "relocate-visible-threat";
        if (!active && (!f.InCover || f.SightAge < c.SightLoss))
            return "relocate-wait-for-cover-and-quiet";
        return "";
    }

    public static double Chance(RelocationFrame f, OutdoorRelocationConfig c) =>
        Math.Min(
            100,
            c.Chance
                * (f.Hurt ? 1.3 : 1)
                * (f.LongWeapon ? 1.3 : 1)
                * (f.HealthyPusher ? .8 : 1)
                * (f.MatesEngaging ? .7 : 1)
        );

    public bool Begin(RelocationFrame f, OutdoorRelocationConfig c, Func<double> random)
    {
        if (
            Active
            || Gate(f, c).Length > 0
            || !History.Eligible(f.Key.Length > 0 ? f.Key : f.Target)
        )
            return false;
        var roll = random();
        if (!Valid(roll) || roll >= 1)
            return false;
        History.Attempt(f.Key.Length > 0 ? f.Key : f.Target);
        Origin = f.Position;
        Known = f.Known;
        Goal = Origin;
        started = lastTime = f.Now;
        releaseAt = -1;
        Trace = new()
        {
            Episode = ++episode,
            Target = f.Target,
            Source = f.Source,
            Chance = Chance(f, c),
            Roll = roll * 100,
            ContactAge = f.ContactAge,
            SightAge = f.SightAge,
            X = Origin.X,
            Y = Origin.Y,
            Z = Origin.Z,
            KnownX = Known.X,
            KnownY = Known.Y,
            KnownZ = Known.Z,
            Requested = true,
        };
        if (Trace.Roll >= Trace.Chance)
        {
            End("relocate-chose-to-stay");
            return false;
        }
        Trace.Reason = "relocate-searching-better-cover";
        Trace.ValidFor = .3;
        return true;
    }

    public static string Reject(RelocationCandidate v, OutdoorRelocationConfig c)
    {
        if (!v.Safe || v.Refusal.Length > 0)
            return v.Refusal.Length > 0 ? v.Refusal : "relocate-unsafe-candidate";
        if (
            !v.Position.Finite
            || !Valid(v.Distance)
            || !Valid(v.PathLength)
            || !Valid(v.Angle)
            || !Valid(v.Exposure)
            || v.Angle > 180
            || v.Exposure > 1
            || v.Protection < 0
            || v.Protection > 3
            || v.OriginalProtection < 0
            || v.OriginalProtection > 3
        )
            return "relocate-invalid-candidate";
        // Radius limits apply to the search rings. NavMesh projection can shift a valid point.
        if (v.PathLength > c.MaxPath)
            return "relocate-route-too-long-or-close";
        if (v.Angle < c.PreferredAngle)
            return "relocate-bearing-too-small";
        return "";
    }

    public void Candidate(RelocationCandidate v, OutdoorRelocationConfig c)
    {
        if (!Active)
            return;
        var t = Trace!;
        t.Candidates++;
        string why = Reject(v, c);
        if (why.Length > 0)
            t.Rejected++;
        var list = new List<string>(t.Alternatives);
        if (list.Count >= 8)
            list.RemoveAt(0);
        list.Add(
            v.Id
                + ": "
                + (
                    why.Length > 0
                        ? why
                        : "safe alternative; score "
                            + v.Score(c)
                                .ToString("F2", System.Globalization.CultureInfo.InvariantCulture)
                )
        );
        t.Alternatives = list.ToArray();
    }

    public bool Select(RelocationCandidate v, OutdoorRelocationConfig c)
    {
        if (Trace?.State != "planning" || Reject(v, c).Length > 0)
            return false;
        Goal = v.Position;
        var t = Trace;
        t.State = "ready";
        t.Reason = "relocate-better-cover-selected";
        t.Destination = v.Id;
        t.DestinationX = Goal.X;
        t.DestinationY = Goal.Y;
        t.DestinationZ = Goal.Z;
        t.Protection = v.Protection;
        t.OriginalProtection = v.OriginalProtection;
        t.PathLength = v.PathLength;
        t.Angle = v.Angle;
        t.Exposure = v.Exposure;
        t.Score = v.Score(c);
        return true;
    }

    public void NativeAccepted(bool accepted)
    {
        if (Active)
            Trace!.Accepted = accepted;
    }

    public void Throw(bool accepted, double now, string outcome)
    {
        if (Trace?.State != "ready")
            return;
        Trace.ThrowAccepted = accepted;
        Trace.GrenadeOutcome = outcome;
        if (accepted)
        {
            Trace.State = "throwing";
            Trace.Action = "ThrowGrenade";
            throwAt = now;
            Trace.Reason = "relocate-wait-for-projectile-and-hands";
        }
    }

    public void Projectile(double now)
    {
        if (Trace?.State == "throwing" && now >= throwAt)
        {
            Trace.ProjectileObserved = true;
            releaseAt = now;
            Trace.GrenadeOutcome = "release-observed";
        }
    }

    public bool CanMove(RelocationFrame f) =>
        Trace?.State == "ready"
        || Trace?.State == "throwing"
            && (Trace.ProjectileObserved ? f.Now - releaseAt >= .2 : f.Now - throwAt > 4);

    private DoorPoint moveOrigin;

    public bool AtDestination(DoorPoint position)
    {
        var offset = position - Goal;
        return offset.X * offset.X + offset.Z * offset.Z < .7 * .7;
    }

    public void MoveRequested(double now, DoorPoint position)
    {
        if (!Active)
            return;
        Trace!.State = "moving";
        Trace.Action = "SeekCover";
        Trace.MoveRequested = true;
        Trace.Accepted = false;
        moveOrigin = position;
        Trace.Reason = "relocate-native-cover-move";
        movingAt = progressAt = now;
        bestRemaining = (Goal - Origin).Length;
    }

    public bool Observe(RelocationFrame f, OutdoorRelocationConfig c)
    {
        if (!Active)
            return false;
        var t = Trace!;
        // Capture the last movement sample even if this tick also reveals a threat.
        if (t.State == "moving" && f.RouteOwned && f.Position.Finite)
        {
            t.DistanceMoved = (f.Position - moveOrigin).Length;
            t.MovementObserved |= f.NativeMoveStarted && t.DistanceMoved > .5;
        }
        string why = Gate(f, c, true);
        if (why.Length == 0 && f.Visible && t.State != "moving")
            why = "relocate-visible-threat";
        if (why.Length == 0 && (f.Now < lastTime || f.Target != t.Target))
            why = "relocate-contact-changed";
        if (why.Length == 0 && f.Now - started >= c.SessionTimeout)
            why = "relocate-session-timeout";

        if (why.Length == 0 && t.State == "moving")
        {
            double remaining = (f.Position - Goal).Length;
            if (!f.RouteOwned && !AtDestination(f.Position))
                why = "relocate-native-route-replaced";
            else
            {
                if (AtDestination(f.Position))
                {
                    t.Arrived = f.NativeMoveStarted;
                    why = t.Arrived ? "relocate-arrival-observed" : "relocate-already-at-destination";
                }
                else if (f.Now - movingAt > c.MoveTimeout)
                    why = "relocate-move-timeout";
                else if (remaining < bestRemaining - .25)
                {
                    bestRemaining = remaining;
                    progressAt = f.Now;
                }
            }
        }
        t.Elapsed = Math.Max(0, f.Now - started);
        t.Speed = Valid(f.Speed) ? f.Speed : 0;
        t.ContactAge = Valid(f.ContactAge) ? f.ContactAge : 0;
        lastTime = f.Now;
        if (why.Length > 0)
        {
            End(why);
            return false;
        }
        return true;
    }

    public void End(string reason)
    {
        if (Trace != null)
        {
            Trace.State = Trace.Arrived ? "arrived" : "ended";
            Trace.Reason = reason;
            Trace.ValidFor = 0;
        }
    }

    public void Clear(string reason)
    {
        End(reason);
        History.Clear();
    }
}
