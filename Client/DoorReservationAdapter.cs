using System;
using Comet.Core;
using Newtonsoft.Json;
using SAIN.Components;
using UnityEngine;

namespace Comet.Client;

internal sealed class DoorReservationRuntime
{
    internal DoorReservationLease? Lease;
    internal string BusyDoor = "",
        BusySquad = "",
        Target = "";
    internal double RetryAt,
        NextRenew;
}

public sealed partial class Plugin
{
    private readonly DoorReservationBoard doorReservations = new();

    private static string ReservationSquad(BotComponent b, BotState s) =>
        string.IsNullOrEmpty(b.Squad?.SquadInfo?.GUID)
            ? "solo:" + s.Id
            : "squad:" + b.Squad.SquadInfo.GUID;

    private static string ReservationPhase(BotState s) =>
        s.Entry.Policy.Active ? "native-entry:" + s.Entry.Policy.Trace!.State
        : s.Fake.Policy.Active ? "feint:" + s.Fake.Policy.Trace!.State
        : s.RoomClear.Policy.Active ? "entry:" + s.RoomClear.Policy.Trace!.State
        : s.StepPeek.Policy.Active ? "step-peek"
        : s.RunBy.Policy.Active ? "run-by"
        : s.Jump.Policy.Active ? "jump-peek"
        : s.Ambush.Policy.Active ? "ambush:" + s.Ambush.Policy.Trace!.State
        : s.Door.Policy.Active ? "approach"
        : "none";

    private bool DoorBusy(BotComponent b, BotState s, string door)
    {
        if (config?.DoorReservation.Enabled != true)
            return false;
        var r = s.Reservation;
        var squad = ReservationSquad(b, s);
        if (r.BusyDoor == door && r.BusySquad == squad && Time.time < r.RetryAt)
            return true;
        var lease = doorReservations.Find(squad, door, Time.time);
        if (lease == null || ReferenceEquals(lease, r.Lease))
            return false;
        r.BusyDoor = door;
        r.BusySquad = squad;
        r.RetryAt = Time.time + config.DoorReservation.BusyRetry;
        r.Target = b.GoalEnemy?.EnemyProfileId ?? "";
        RecordDoorReservation(s, lease, "refused", "reservation-door-busy", false);
        return true;
    }

    private bool AcquireDoorReservation(
        BotComponent b,
        BotState s,
        DoorWork w,
        string phase,
        double deadline
    )
    {
        if (config?.DoorReservation.Enabled != true)
            return true;
        var r = s.Reservation;
        if (r.Lease != null)
            return r.Lease.Door == w.Geometry.Id
                && doorReservations.Owns(r.Lease, ReservationSquad(b, s), s.Id, Time.time);
        if (DoorBusy(b, s, w.Geometry.Id))
            return false;
        if (
            !doorReservations.Acquire(
                ReservationSquad(b, s),
                w.Geometry.Id,
                s.ProfileId,
                s.Id,
                phase,
                Time.time,
                deadline,
                config.DoorReservation,
                out var lease,
                out var why
            )
        )
        {
            if (lease != null)
                RecordDoorReservation(s, lease, "refused", why, false);
            return false;
        }
        r.Lease = lease;
        r.Target = b.GoalEnemy?.EnemyProfileId ?? "";
        r.NextRenew = Time.time;
        r.BusyDoor = "";
        r.BusySquad = "";
        r.RetryAt = 0;
        RecordDoorReservation(s, lease!, "acquired", "reservation-acquired", false);
        return true;
    }

    private double ReservationDeadline(BotComponent b, BotState s, bool ambush = false)
    {
        // zzap: one raid-wide claim for the whole door session (150 seconds).
        // Individual approach, peek and feint stages keep their own source timers.
        return Time.time + 150;
    }

    // Sweeps may revoke, but only a successful native action callback can renew.
    private bool GuardDoorReservation(BotComponent b, BotState s)
    {
        var r = s.Reservation;
        var lease = r.Lease;
        if (lease == null)
            return true;
        if (config?.DoorReservation.Enabled != true)
        {
            ReleaseDoorReservation(s, "reservation-disabled");
            return true;
        }
        if (b == null || b.IsDead || !FakeScope(b) || b.GameEnding)
        {
            Release(s, "reservation-out-of-scope");
            return false;
        }
        if (!doorReservations.Owns(lease, ReservationSquad(b, s), s.Id, Time.time))
        {
            Release(
                s,
                Time.time >= lease.Until
                    ? "reservation-expired"
                    : "reservation-squad-or-owner-changed"
            );
            return false;
        }
        var activeDoor = SupportingDoor(s)?.Geometry.Id;
        if (activeDoor == null)
        {
            ReleaseDoorReservation(s, "reservation-operation-ended");
            return true;
        }
        if (activeDoor != lease.Door)
        {
            Release(s, "reservation-door-changed");
            return false;
        }
        return true;
    }

    private bool CompleteDoorReservationStep(BotComponent b, BotState s, bool nativeFallback)
    {
        var r = s.Reservation;
        var lease = r.Lease;
        if (lease == null)
            return nativeFallback;
        if (!GuardDoorReservation(b, s))
            return true;
        if (r.Lease == null)
            return nativeFallback;
        if (!nativeFallback && (Time.time >= r.NextRenew || lease.Phase != ReservationPhase(s)))
        {
            if (
                !doorReservations.Renew(
                    lease,
                    ReservationSquad(b, s),
                    s.Id,
                    ReservationPhase(s),
                    Time.time,
                    true,
                    config!.DoorReservation
                )
            )
            {
                Release(s, "reservation-renewal-refused");
                return true;
            }
            r.NextRenew = Time.time + config.DoorReservation.RenewInterval;
            RecordDoorReservation(s, lease, "active", "reservation-validated", true);
        }
        return nativeFallback;
    }

    private void ReleaseDoorReservation(BotState s, string reason)
    {
        var r = s.Reservation;
        var lease = r.Lease;
        if (lease == null)
            return;
        r.Lease = null;
        doorReservations.Release(lease);
        RecordDoorReservation(
            s,
            lease,
            Time.time >= lease.Until ? "expired" : "released",
            reason,
            false
        );
    }

    private void RecordDoorReservation(
        BotState s,
        DoorReservationLease lease,
        string state,
        string reason,
        bool observed
    )
    {
        var t = new DoorReservationTrace
        {
            State = state,
            Reason = reason,
            Door = lease.Door,
            Squad = lease.Squad,
            Owner = lease.Owner,
            Token = lease.Token,
            Phase = lease.Phase,
            Target = s.Reservation.Target,
            Remaining = Math.Max(0, lease.Until - Time.time),
            BudgetRemaining = Math.Max(0, lease.Deadline - Time.time),
            RetryRemaining = Math.Max(0, s.Reservation.RetryAt - Time.time),
            ActionObserved = observed,
            Alternatives =
                state == "refused"
                    ? new[]
                    {
                        "another available door",
                        "D11 support if eligible",
                        "native action; no waiting queue",
                    }
                    : Array.Empty<string>(),
        };
        if (state == "released" || state == "expired")
            t.Remaining = 0;
        if (!Comet.Core.ReplayCapture.Detailed)
            return;
        var json = Diagnostics.Serialize(
            new
            {
                schema = 1,
                session,
                sequence = ++sequence,
                feature = "D14",
                stage = "evaluated",
                decision = "DoorReservation",
                reason,
                realtime = Time.realtimeSinceStartup,
                utc = DateTime.UtcNow.ToString("O"),
                configRevision = config?.Revision ?? 0,
                profileId = s.ProfileId,
                name = s.Name,
                botInstance = s.Id,
                doorReservation = t,
                doorReservationConfig = config?.DoorReservation,
            }
        );
        diagnostics?.Write(json);
        Api.Decisions.Publish(json, ex =>
        {
            if (!subscriberWarned)
            {
                subscriberWarned = true;
                Logger.LogWarning(ex.Message);
            }
        });
    }
}
