using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Comet.Core;
using EFT;
using Newtonsoft.Json;
using SAIN.Components;
using SAIN.SAINComponent.Classes.EnemyClasses;
using SAIN.SAINComponent.Classes.Mover;
using UnityEngine;
using UnityEngine.AI;

namespace Comet.Client;

internal sealed class GrenadeAim
{
    internal string Kind = "last-known";
    internal Vector3 Point, Nominal;
    internal DoorWork? Door;
    internal int[] Angles = Array.Empty<int>();
    internal bool Checked;
    internal int Next;
}

internal sealed class GrenadeArc
{
    internal AIGreanageThrowData Data = null!;
    internal GrenadeTrajectoryAlternative Alternative = null!;
    internal GrenadeAim Aim = null!;
}

internal sealed class GrenadeTrajectoryRuntime
{
    internal readonly List<GrenadeAim> Targets = new();
    internal readonly List<GrenadeArc> Arcs = new();
    internal GrenadeTrajectoryTrace? Trace;
    internal GrenadeArc? Selected;
    internal Vector3 Origin,
        Known;
    internal int Enemy,
        Index;
    internal long Revision;
    internal string Item = "",
        LogKey = "";
    internal double Started,
        Prepared = -1,
        RetryAt,
        LogAt = -1000;
    internal bool Pending,
        Fault;

    internal void Drop()
    {
        Targets.Clear();
        Arcs.Clear();
        Selected = null;
        Pending = false;
        Prepared = -1;
    }
}

public sealed partial class Plugin
{
    private readonly GrenadeArcBudget grenadeArcBudget = new();
    private readonly System.Diagnostics.Stopwatch grenadeArcClock = new();

    private bool GrenadeTrajectoryScope(BotComponent b) =>
        config?.Enabled == true
        && config.GrenadeTrajectory.Enabled
        && NativeAdapter.IsSupportedAi(b)
        && b.BotActive
        && !b.GameEnding
        && Authority
        && !Ghost(b.ProfileId);

    // Called only after SAIN's throw permission/cooldown checks and the personal-evidence gate.
    // null means unfinished work, not a failed trajectory or a new G01 roll.
    private bool? PrepareGrenadeTrajectory(
        BotComponent b,
        Enemy e,
        BotState s,
        GrenadeJudgmentFrame f
    )
    {
        var r = s.GrenadeTrajectory;
        var c = config!.GrenadeTrajectory;
        double now = Time.timeAsDouble;
        if (r.Fault)
            return false;
        string gate = TrajectoryEvidence(b, e, f);
        if (gate.Length > 0)
        {
            StopGrenadeTrajectory(s, gate);
            return false;
        }
        if (now < r.RetryAt)
            return null;
        if (r.Pending || r.Selected != null)
        {
            string changed = TrajectoryChanged(b, e, s, f);
            if (changed.Length > 0)
            {
                FailGrenadeTrajectory(s, changed);
                return false;
            }
            if (r.Selected != null)
            {
                if (GrenadeTrajectoryRules.Fresh(now, r.Prepared, c.PreparedLifetime))
                {
                    if (
                        !ReferenceEquals(
                            b.BotOwner.WeaponManager.Grenades.AIGreanageThrowData,
                            r.Selected.Data
                        )
                    )
                        b.BotOwner.WeaponManager.Grenades.SetThrowData(r.Selected.Data);
                    return true;
                }
                // No fresh random offset: recompute the same selected aim after the short preparation expires.
                r.Targets.Clear();
                r.Targets.Add(r.Selected.Aim);
                r.Selected.Aim.Next = 0;
                r.Selected.Aim.Checked = false;
                r.Selected.Aim.Angles = new[] { r.Selected.Alternative.Angle };
                r.Arcs.Clear();
                r.Selected = null;
                r.Index = 0;
                r.Pending = true;
                r.Started = now;
                r.Trace!.State = "searching";
                r.Trace.Reason = "trajectory-refreshing-fixed-aim";
                r.Trace.Attempts = 0;
                r.Trace.Alternatives.Clear();
                r.Trace.ValidFor = .5;
                r.Trace.Kind = "";
                r.Trace.Angle = 0;
            }
        }
        else
        {
            if (!grenadeArcBudget.TryBegin(Time.frameCount))
                return null;
            grenadeArcClock.Restart();
            try
            {
                BeginGrenadeTrajectory(b, e, s, f);
            }
            finally
            {
                grenadeArcClock.Stop();
                grenadeArcBudget.Charge(grenadeArcClock.Elapsed.TotalMilliseconds);
            }
        }
        if (!r.Pending)
            return false;
        if (!grenadeArcBudget.TryBegin(Time.frameCount))
        {
            RecordGrenadeTrajectory(s);
            return null;
        }
        grenadeArcClock.Restart();
        try
        {
            var aim = r.Targets[r.Index];
            if (!aim.Checked)
            {
                aim.Checked = true;
                string why = UsefulGrenadeAim(b, r, aim);
                if (why.Length > 0)
                {
                    r.Trace!.Alternatives.Add(
                        new()
                        {
                            Kind = aim.Kind,
                            X = aim.Point.x,
                            Y = aim.Point.y,
                            Z = aim.Point.z,
                            Reason = why,
                        }
                    );
                    aim.Next = aim.Angles.Length;
                }
            }
            if (aim.Next < aim.Angles.Length)
            {
                int angle = aim.Angles[aim.Next++];
                r.Trace.Attempts++;
                var alt = new GrenadeTrajectoryAlternative
                {
                    Kind = aim.Kind,
                    X = aim.Point.x,
                    Y = aim.Point.y,
                    Z = aim.Point.z,
                    Angle = angle,
                };
                r.Trace.Alternatives.Add(alt);
                var native = AIGrenadeHelper.CanThrowGrenade2(
                    r.Origin,
                    aim.Point,
                    b.BotOwner.WeaponManager.Grenades.MaxPower * .9f,
                    (AIGreandeAng)Enum.Parse(typeof(AIGreandeAng), "ang" + angle),
                    -1f,
                    0f
                );
                alt.Reason = !ValidGrenadeArc(native) ? "native-arc-blocked-or-out-of-range" : "";
                if (alt.Reason.Length == 0)
                {
                    // Margin only ranks already safe arcs. It cannot authorize an obstructed throw.
                    alt.Clearance = 0;
                    alt.Accepted = true;
                    alt.Reason = "trajectory-clear";
                    r.Arcs.Add(
                        new()
                        {
                            Data = native,
                            Alternative = alt,
                            Aim = aim,
                        }
                    );
                }
            }
            if (aim.Next >= aim.Angles.Length)
            {
                if (r.Arcs.Count > 0)
                {
                    int chosenIndex = 0;
                    if (jumpRandom.NextDouble() < Mathf.Lerp(.4f, .8f, b.Info.Profile.DifficultyModifier))
                    {
                        for (int i = 1; i < r.Arcs.Count; i++)
                            if (r.Arcs[i].Data.Force < r.Arcs[chosenIndex].Data.Force)
                                chosenIndex = i;
                    }
                    else
                        chosenIndex = (int)(jumpRandom.NextDouble() * r.Arcs.Count);
                    var chosen = r.Arcs[chosenIndex];
                    // Selection can span frames. Execution recomputes this aim from the current origin.
                    r.Selected = chosen;
                    r.Pending = false;
                    r.Prepared = now;
                    // Match zzap: native preparation also notifies the group's grenade coordinator.
                    b.BotOwner.WeaponManager.Grenades.SetThrowData(chosen.Data);
                    var t = r.Trace!;
                    t.State = "prepared";
                    t.Reason = "trajectory-prepared";
                    t.Kind = chosen.Aim.Kind;
                    t.Door = chosen.Aim.Door?.Geometry.Id ?? "";
                    t.X = chosen.Data.Target.x;
                    t.Y = chosen.Data.Target.y;
                    t.Z = chosen.Data.Target.z;
                    t.Angle = chosen.Alternative.Angle;
                    t.Clearance = chosen.Alternative.Clearance;
                    // Age starts at the native calculation, not at the later choice.
                    r.Prepared = chosen.Data.CreatedTime;
                    t.ValidFor = Math.Max(0, c.PreparedLifetime - (now - r.Prepared));
                    RecordGrenadeTrajectory(s, true);
                    // A winning early arc may have aged out while comparing. Refresh its fixed target next call.
                    return GrenadeTrajectoryRules.Fresh(now, r.Prepared, c.PreparedLifetime)
                        ? true
                        : null;
                }
                r.Index++;
                if (r.Index >= r.Targets.Count)
                {
                    FailGrenadeTrajectory(s, "trajectory-no-safe-arc");
                    return false;
                }
            }
            RecordGrenadeTrajectory(s);
            return null;
        }
        catch (Exception ex)
        {
            r.Fault = true;
            FailGrenadeTrajectory(s, "trajectory-adapter-error");
            Logger.LogWarning("G02 blocked grenade preparation: " + ex.GetBaseException().Message);
            return false;
        }
        finally
        {
            grenadeArcClock.Stop();
            grenadeArcBudget.Charge(grenadeArcClock.Elapsed.TotalMilliseconds);
        }
    }

    private string TrajectoryEvidence(BotComponent b, Enemy e, GrenadeJudgmentFrame f)
    {
        if (!ReferenceEquals(e, b.GoalEnemy) || !DP(new((float)f.X, (float)f.Y, (float)f.Z)).Finite)
            return "trajectory-no-personal-contact";
        if (f.Now < f.EvidenceAt || f.Now - f.EvidenceAt > config!.GrenadeContactLifetime)
            return "trajectory-stale-contact";
        if (f.Block.Length > 0)
            return f.Block;
        if (f.Item.Length == 0)
            return "trajectory-no-item";
        if (
            Vector3.Distance(b.Position, new((float)f.X, (float)f.Y, (float)f.Z))
            > config!.GrenadeTrajectory.Distance
        )
            return "trajectory-target-too-far";
        return "";
    }

    private string TrajectoryChanged(BotComponent b, Enemy e, BotState s, GrenadeJudgmentFrame f)
    {
        var r = s.GrenadeTrajectory;
        if (r.Revision != config!.Revision)
            return "trajectory-config-changed";
        if (r.Enemy != RuntimeHelpers.GetHashCode(e) || r.Item != f.Item)
            return "trajectory-contact-or-item-changed";

        if (r.Selected?.Aim.Door is DoorWork door && !CurrentGrenadeDoor(door))
            return "trajectory-door-changed";
        return "";
    }

    private void BeginGrenadeTrajectory(BotComponent b, Enemy e, BotState s, GrenadeJudgmentFrame f)
    {
        var r = s.GrenadeTrajectory;
        var c = config!.GrenadeTrajectory;
        r.Drop();
        r.Origin = b.Transform.WeaponData.WeaponRoot;
        r.Known = new((float)f.X, (float)f.Y, (float)f.Z);
        r.Enemy = f.Instance;
        r.Item = f.Item;
        r.Revision = config.Revision;
        r.Started = f.Now;
        r.Index = 0;
        r.Trace = new()
        {
            Target = f.Target,
            Source = f.Source,
            KnownX = f.X,
            KnownY = f.Y,
            KnownZ = f.Z,
        };
        if (!NativeAdapter.GrenadeFriendsClear(b, r.Known))
        {
            FailGrenadeTrajectory(s, "sain-friendly-distance");
            return;
        }
        float range = Vector3.Distance(b.Position, r.Known);
        AddGrenadeAim(b, r, r.Known, range, "last-known", null);
        if (c.Corners && e.VisiblePathPoint is Vector3 corner)
        {
            float separation = Vector3.Distance(corner, r.Known);
            if (separation <= c.CornerDistance && NativeAdapter.GrenadeFriendsClear(b, corner))
                AddGrenadeAim(b, r, corner, separation, "blind-corner", null);
        }
        if (c.Doors)
        {
            // Inspect only the current navigation voxel, just like the door tactics. No scene/global scan.
            var links = b.BotOwner.VoxelesPersonalData.CurVoxel?.DoorLinks;
            DoorWork? best = null;
            float distance = float.MaxValue;
            if (links != null)
                foreach (var link in links)
                {
                    if (link?.Door == null)
                        continue;
                    var w = new DoorWork { Data = new DoorDataStruct(link) };
                    if (!TryDoorGeometry(w.Data, out var g))
                        continue;
                    w.Geometry = g;
                    if (DoorPolicy.Geometry(g, DP(b.Position), DP(r.Known), Array.Empty<DoorPoint>(), config!.DoorApproach).Length != 0)
                        continue;
                    var offset = VP(g.Center) - b.Position;
                    offset.y = 0;
                    float d = offset.sqrMagnitude;
                    if (d >= distance)
                        continue;
                    best = w;
                    distance = d;
                }
            if (best?.Geometry.State == "Open")
            {
                var center = VP(best.Geometry.Center);
                var normal = VP(best.Geometry.Normal);
                if (Vector3.Dot(r.Known - center, normal) < 0)
                    normal = -normal;
                foreach (
                    double depth in new[] { c.DoorDepthFirst, c.DoorDepthSecond, c.DoorDepthThird }
                )
                    AddGrenadeAim(b, r, center + normal * (float)depth, 0f, "open-door", best);
            }
        }
        r.Pending = true;
        RecordGrenadeTrajectory(s, true);
    }

    // The fork's UnderRoof combines SAIN building areas and EFT's indoor environment.
    private static bool GrenadeThrowerUnderRoof(BotComponent b) =>
        b.Memory.Location.IsIndoors || b.Player.Environment == EnvironmentType.Indoor;

    private void AddGrenadeAim(
        BotComponent b,
        GrenadeTrajectoryRuntime r,
        Vector3 raw,
        float distance,
        string kind,
        DoorWork? door
    )
    {
        bool indoors = GrenadeThrowerUnderRoof(b);
        Vector3 nominal = raw;
        raw.y += .25f;
        if (door == null)
        {
            var along = (raw - b.Position).normalized;
            var side = Vector3.Cross(along, Vector3.up).normalized;
            float spread = (float)GrenadeTrajectoryRules.Dispersion(distance);
            float jitter = Mathf.Lerp(.05f, .4f, Mathf.InverseLerp(5f, 30f, distance))
                * (indoors ? .5f : 1f) * Mathf.Lerp(1.1f, .85f, b.Info.Profile.DifficultyModifier);
            raw += along * (float)((2 * jumpRandom.NextDouble() - 1) * spread)
                + side * (float)((jumpRandom.NextDouble() - .5) * spread)
                + Vector3.up * (float)((2 * jumpRandom.NextDouble() - 1) * jitter);
        }
        r.Targets.Add(
            new()
            {
                Kind = kind,
                Point = raw,
                Nominal = nominal,
                Door = door,
                Angles =
                    kind == "blind-corner"
                        ? new[] { 15, 5 }
                        : GrenadeTrajectoryRules.Angles(door != null, indoors, distance),
            }
        );
    }

    private static bool CurrentGrenadeDoor(DoorWork w) =>
        TryDoorGeometry(w.Data, out var g)
        && g.State == "Open"
        && (g.Center - w.Geometry.Center).Length < .1
        && Math.Abs(g.Axis.Dot(w.Geometry.Axis)) > .99;

    private string UsefulGrenadeAim(BotComponent b, GrenadeTrajectoryRuntime r, GrenadeAim a)
    {
        if (!DP(a.Point).Finite)
            return "trajectory-invalid-aim";
        if (!NativeAdapter.GrenadeFriendsClear(b, a.Point))
            return "sain-friendly-distance";
        if (a.Door != null)
        {
            if (!CurrentGrenadeDoor(a.Door))
                return "trajectory-door-changed";
            var center = VP(a.Door.Geometry.Center);
            var normal = VP(a.Door.Geometry.Normal);
            if (Vector3.Dot(a.Point - center, normal) * Vector3.Dot(r.Known - center, normal) <= 0)
                return "trajectory-wrong-door-side";
        }
        // No NavMesh landing, path-to-target, or extra swept-volume veto in zzap.
        if (
            a.Door != null
            && Vector3.Distance(b.Position, a.Nominal) < 6f
            && !Physics.Linecast(
                a.Nominal + Vector3.up * .3f,
                b.Position + Vector3.up * 1.2f,
                LayersMaskController.HighPolyWithTerrainMask
            )
        )
            return "trajectory-door-self-blast";
        return "";
    }

    private static bool ValidGrenadeArc(AIGreanageThrowData? d) =>
        d != null
        && d.CanThrow
        && DP(d.Target).Finite
        && DP(d.Position).Finite
        && ImpactReaction.Finite(d.Force)
        && d.Force > 0
        && ImpactReaction.Finite(d.Ang)
        && d.Ang > 0
        && d.Ang < 90;

    private bool ValidateGrenadeTrajectory(BotComponent b, Enemy e, BotState s)
    {
        if (!GrenadeTrajectoryScope(b))
            return true;
        var r = s.GrenadeTrajectory;
        var f = GrenadeFrame(b, e, s);
        string why = TrajectoryEvidence(b, e, f);
        if (why.Length == 0)
            why = TrajectoryChanged(b, e, s, f);
        var d = b.BotOwner.WeaponManager.Grenades.AIGreanageThrowData;
        if (
            why.Length == 0
            && (
                r.Selected == null
                || !ReferenceEquals(d, r.Selected.Data)
                || !ValidGrenadeArc(d)
                || !GrenadeTrajectoryRules.Fresh(
                    Time.timeAsDouble,
                    r.Prepared,
                    config!.GrenadeTrajectory.PreparedLifetime
                )
            )
        )
            why = "trajectory-preparation-expired";
        if (why.Length > 0)
        {
            FailGrenadeTrajectory(s, why);
            return false;
        }
        if (!grenadeArcBudget.TryBegin(Time.frameCount))
        {
            r.Trace!.Reason = "trajectory-execution-budget-deferred";
            RecordGrenadeTrajectory(s);
            return false;
        }
        grenadeArcClock.Restart();
        try
        {
            // A small allowed origin change must not reuse a path calculated from the old hand position.
            // Keep the selected point and angle, then ask the native calculator again; never reroll the aim.
            if (!NativeAdapter.GrenadeFriendsClear(b, r.Selected!.Aim.Point))
                why = "sain-friendly-distance";
            if (why.Length == 0)
            {
                var fresh = AIGrenadeHelper.CanThrowGrenade2(
                    b.Transform.WeaponData.WeaponRoot,
                    r.Selected.Aim.Point,
                    b.BotOwner.WeaponManager.Grenades.MaxPower * .9f,
                    (AIGreandeAng)
                        Enum.Parse(typeof(AIGreandeAng), "ang" + r.Selected.Alternative.Angle),
                    -1f,
                    0f
                );
                if (!ValidGrenadeArc(fresh))
                    why = "trajectory-execution-native-refused";
                else
                {
                    why = "";
                    if (why.Length == 0)
                    {
                        d = fresh;
                        r.Selected.Data = fresh;
                        b.BotOwner.WeaponManager.Grenades.SetThrowData(fresh);
                        r.Prepared = fresh.CreatedTime;
                        r.Trace!.Clearance = 0;
                    }
                }
            }
            if (why.Length == 0)
                why = GrenadeSafety(b, s, f);
            if (why.Length > 0)
            {
                FailGrenadeTrajectory(s, why);
                return false;
            }
            r.Trace!.State = "validated";
            r.Trace.Reason = "trajectory-execution-validated";
            r.Trace.ValidFor = Math.Max(
                0,
                config!.GrenadeTrajectory.PreparedLifetime - (Time.timeAsDouble - r.Prepared)
            );
            RecordGrenadeTrajectory(s, true);
            return true;
        }
        finally
        {
            grenadeArcClock.Stop();
            grenadeArcBudget.Charge(grenadeArcClock.Elapsed.TotalMilliseconds);
        }
    }

    private void FailGrenadeTrajectory(BotState s, string why)
    {
        s.GrenadeTrajectory.RetryAt =
            Time.timeAsDouble + (config?.GrenadeJudgment.NoArcRetry ?? 1.5);
        StopGrenadeTrajectory(s, why, "blocked");
    }

    private void StopGrenadeTrajectory(BotState s, string why, string state = "ended")
    {
        var r = s.GrenadeTrajectory;
        r.Drop();
        s.GrenadeJudgment.Arc = false;
        if (r.Trace == null)
            return;
        r.Trace.State = state;
        r.Trace.Reason = why;
        r.Trace.ValidFor = state == "ended" ? 0 : .75;
        RecordGrenadeTrajectory(s, true);
    }

    private void RecordGrenadeTrajectory(BotState s, bool force = false)
    {
        var r = s.GrenadeTrajectory;
        var t = r.Trace;
        if (t == null)
            return;
        string key = t.State + "|" + t.Reason + "|" + t.Attempts;
        if (!force && r.LogKey == key && Time.timeAsDouble - r.LogAt < .25)
            return;
        r.LogKey = key;
        r.LogAt = Time.timeAsDouble;
        if (!Comet.Core.ReplayCapture.Detailed)
            return;
        var json = Diagnostics.Serialize(
            new
            {
                schema = 1,
                session,
                sequence = ++sequence,
                feature = "G02",
                stage = "evaluated",
                decision = "GrenadeTrajectory",
                reason = t.Reason,
                realtime = Time.realtimeSinceStartup,
                utc = DateTime.UtcNow.ToString("O"),
                configRevision = config?.Revision ?? 0,
                profileId = s.ProfileId,
                name = s.Name,
                botInstance = s.Id,
                grenadeTrajectory = t,
                maxContactAgeSeconds = config?.GrenadeContactLifetime,
                grenadeTrajectoryConfig = config?.GrenadeTrajectory,
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
