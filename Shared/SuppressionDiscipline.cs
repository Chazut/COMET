using System;
using System.Collections.Generic;

namespace Comet.Core;

public sealed class SuppressionDisciplineConfig
{
    public bool Enabled { get; set; } = true;
    public double ContactAge { get; set; } = 3;
    public int BurstRounds { get; set; } = 5;
    public double PauseSeconds { get; set; } = 3;
    public double ReservePercent { get; set; } = 60;
    public double SequenceSeconds { get; set; } = 6;

    public void Validate()
    {
        CometConfig.Range(ContactAge, .5, 10, nameof(ContactAge));
        CometConfig.Range(BurstRounds, 1, 15, nameof(BurstRounds));
        CometConfig.Range(PauseSeconds, .5, 8, nameof(PauseSeconds));
        CometConfig.Range(ReservePercent, 20, 90, nameof(ReservePercent));
        CometConfig.Range(SequenceSeconds, 1, 15, nameof(SequenceSeconds));
    }

    public int Reserve(int capacity) => (int)Math.Ceiling(capacity * ReservePercent / 100);
}

public sealed class SuppressionDisciplineFrame
{
    public double Now,
        ObservedAt = -1;
    public string Target = "",
        Identity = "",
        Weapon = "",
        Mode = "",
        Source = "",
        Action = "",
        Block = "",
        SupportPlan = "";
    public int Loaded,
        Capacity;
    public DoorPoint Known;
    public bool Visible,
        KnownContact,
        FreshGeometry,
        Clear,
        AllySafe,
        Aligned,
        SupportUseful = true;
}

public sealed class SuppressionDisciplineTrace
{
    public string State = "waiting",
        Reason = "suppression-request",
        Target = "",
        Weapon = "",
        Mode = "",
        Source = "",
        Action = "",
        SupportPlan = "";
    public int Episode,
        Loaded,
        Capacity,
        Reserve,
        BurstLimit,
        BurstShots,
        ActualShots;
    public double ContactAge,
        Elapsed,
        Remaining,
        PauseRemaining,
        ValidFor,
        KnownX,
        KnownY,
        KnownZ,
        AimX,
        AimY,
        AimZ;
    public bool Requested,
        Accepted,
        Clear,
        AllySafe,
        Aligned;
    public string[] Alternatives =
    {
        "Normal SAIN fire when a target is visible",
        "Keep ammunition and wait",
        "Continue the native movement or cover action",
    };
}

// Permission for native suppression, never a scheduler that fabricates shots or changes the weapon's fire mode.
public sealed class SuppressionDisciplinePolicy
{
    public SuppressionDisciplineTrace? Trace { get; private set; }
    public bool Active => Trace != null && Trace.State != "ended" && Trace.State != "rejected";
    public bool MayFire => Active && Trace!.State == "firing";
    public DoorPoint Aim { get; private set; }

    private string identity = "";
    private double started,
        last = -1,
        latestCue = -1,
        pauseUntil;
    private int episode;
    private double lastShot = -1000;

    public void SetAim(DoorPoint aim)
    {
        Aim = aim;
    }

    // Observe native SuppressPosition calls, as TrackBurst in zzap does. Native
    // CanSuppressEnemy/TrySuppressAnyEnemy own eligibility; telemetry is not a veto.
    private int burstStartAmmo;

    public void NativeCall(
        SuppressionDisciplineFrame f,
        SuppressionDisciplineConfig c,
        bool accepted,
        DoorPoint aim,
        double roll
    )
    {
        bool same = Trace != null;
        if (!same)
        {
            identity = f.Identity;
            started = f.Now;
            lastShot = -1000;
            pauseUntil = 0;
            burstStartAmmo = f.Loaded;
            Trace = new()
            {
                Episode = ++episode,
                Target = f.Target,
                Weapon = f.Weapon,
                Mode = f.Mode,
                BurstLimit = c.BurstRounds,
            };
        }
        SetAim(aim);
        Trace!.Target = f.Target;
        Trace.Weapon = f.Weapon;
        Trace.Mode = f.Mode;
        Trace.Requested = true;
        Trace.Accepted = accepted;
        Trace.Action = f.Action;
        Trace.SupportPlan = f.SupportPlan;
        Trace.Capacity = f.Capacity;
        Trace.Reserve = c.Reserve(Math.Max(0, f.Capacity));
        Trace.State = accepted ? "firing" : "waiting";
        Trace.Reason = accepted ? "suppression-native-accepted" : "suppression-native-refused";
        if (accepted)
        {
            if (f.Now - lastShot > 1.5 || f.Loaded > burstStartAmmo)
                burstStartAmmo = f.Loaded;
            lastShot = f.Now;
            Trace.BurstShots = Math.Max(0, burstStartAmmo - f.Loaded);
            if (Trace.BurstShots >= c.BurstRounds)
            {
                pauseUntil = f.Now + c.PauseSeconds * (.7 + .6 * Math.Max(0, Math.Min(1, roll)));
                burstStartAmmo = f.Loaded;
            }
        }
        if (f.Now < pauseUntil)
        {
            Trace.State = "paused";
            Trace.Reason = "suppression-burst-pause";
        }
        Trace.ValidFor = .3;
        Snapshot(f);
    }

    public bool NativePaused(double now) => now < pauseUntil;

    private void Snapshot(SuppressionDisciplineFrame f)
    {
        var t = Trace!;
        t.Loaded = Math.Max(0, f.Loaded);
        t.ContactAge =
            DoorPolicy.Finite(f.Now) && DoorPolicy.Finite(f.ObservedAt) && f.ObservedAt >= 0
                ? Math.Max(0, f.Now - f.ObservedAt)
                : 0;
        t.Elapsed = Active && DoorPolicy.Finite(f.Now) ? Math.Max(0, f.Now - started) : 0;
        t.PauseRemaining = Active && DoorPolicy.Finite(f.Now) ? Math.Max(0, pauseUntil - f.Now) : 0;
        t.Clear = f.Clear;
        t.AllySafe = f.AllySafe;
        t.Aligned = f.Aligned;
        t.Source = f.Source;
        var known = CornerChase.Point(f.Known) ? f.Known : default;
        t.KnownX = known.X;
        t.KnownY = known.Y;
        t.KnownZ = known.Z;
        t.AimX = Aim.X;
        t.AimY = Aim.Y;
        t.AimZ = Aim.Z;
    }

    public void End(string reason)
    {
        if (!Active)
            return;
        Trace!.State = "ended";
        Trace.Reason = reason;
        Trace.ValidFor = Trace.Remaining = Trace.PauseRemaining = 0;
    }
}
