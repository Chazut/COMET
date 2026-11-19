using System;
using Comet.Core;
using SAIN;
using SAIN.Components;
using SAIN.Models.Enums;
using SAIN.Preset.Shared.Enums;
using UnityEngine;
using UnityEngine.AI;

namespace Comet.Client;

internal sealed class CoverMateRuntime
{
    internal string Mate = "",
        Operation = "",
        Sector = "",
        Source = "";
    internal float Started,
        Next,
        LastCallback,
        RetryAt,
        FaceAt = -1,
        MoveAt = -1;
    internal Vector3 MateOrigin;
    internal bool Confirmed;
}

public sealed partial class Plugin
{
    private readonly CoverMateBoard coverMateBoard = new();

    private string CoverMateBlock(BotComponent b, BotState s) =>
        !CrossfireScope(b) || !config!.CoverMate.Enabled
            ? "cover-mate-out-of-scope"
            : CrossfireCommonBlock(b, s, allowGoalVisible: true);

    private static bool CoverMateBusy(BotComponent mate, out string kind)
    {
        kind = "";
        if (mate.BotOwner?.WeaponManager?.Reload?.Reloading == true)
        {
            kind = "Reload";
            return true;
        }
        if (mate.BotOwner?.Medecine?.Using == true)
        {
            kind = "Medicine";
            return true;
        }
        var self = mate.Decision.CurrentSelfDecision;
        if (self is ESelfActionType.Surgery or ESelfActionType.FirstAid or ESelfActionType.Reload)
        {
            kind = self.ToString();
            return true;
        }
        return false;
    }

    private bool TryBeginCoverMate(BotComponent b, BotState s)
    {
        var r = s.Crossfire;
        var a = r.Care;
        var c = config!.CoverMate;
        if (
            !c.Enabled
            || r.Policy.Active
            || Time.time < a.Next
            || Time.time < a.RetryAt
            || !CrossfireScope(b)
        )
            return false;
        if (CrossfireCommonBlock(b, s, allowGoalVisible: true).Length > 0)
            return false;
        var enemy = b.GoalEnemy;
        if (
            enemy == null
            || enemy.IsVisible
            || enemy.TimeSinceLastKnownUpdated > 20
            || enemy.KnownPlaces.LastKnownPosition is not Vector3 last
        )
            return false;
        if (
            Vector3.ProjectOnPlane(last - b.Position, Vector3.up).magnitude
            > config.Crossfire.EnemyDistance
        )
            return false;
        a.Next = Time.time + .5f;
        foreach (var mate in b.Squad.Members.Values)
        {
            if (
                mate == null
                || ReferenceEquals(mate, b)
                || mate.IsDead
                || !CoverMateBusy(mate, out var kind)
            )
                continue;
            if (
                Vector3.ProjectOnPlane(mate.Position - b.Position, Vector3.up).magnitude
                > c.MateDistance
            )
                continue;
            if (!coverMateBoard.Available(s.Id, mate.ProfileId, "", "", 0, 0, Time.time, c))
                continue;
            var threat = mate.GoalEnemy ?? b.GoalEnemy;
            if (threat?.KnownPlaces.LastKnownPosition is not Vector3 known)
                continue;
            var side = Vector3.Cross(
                Vector3.up,
                Vector3.ProjectOnPlane(known - mate.Position, Vector3.up).normalized
            );
            if (
                !NavMesh.SamplePosition(
                    mate.Position + side * 1.6f,
                    out var nav,
                    2,
                    NavMesh.AllAreas
                )
                && !NavMesh.SamplePosition(
                    mate.Position - side * 1.6f,
                    out nav,
                    2,
                    NavMesh.AllAreas
                )
            )
                continue;
            if (!b.Mover.CanGoToPoint(nav.position, out var path, true))
                continue;
            if (!r.Policy.Begin(Time.time, CoverMateMotionConfig(), 0))
                return false;
            coverMateBoard.Reserve(s.Id, mate.ProfileId, "", "", 0, 0, Time.time, c);
            RememberCrossfireHandoff(b, r);
            a.Mate = mate.ProfileId;
            a.Operation = "care-" + r.Policy.Trace!.Episode;
            a.Sector = threat.EnemyProfileId;
            a.Started = Time.time;
            a.MateOrigin = mate.Position;
            a.Confirmed = false;
            a.LastCallback = Time.time;
            a.FaceAt = a.MoveAt = -1;
            a.Source = NativeAdapter.Capture(b, threat, true, false).ContactSource;
            r.Maneuver = null;
            r.ManeuverRole = null;
            r.Entrance.Drop();
            r.Known = known;
            r.Contact = threat.EnemyProfileId;
            r.ContactAt = Time.time - threat.TimeSinceLastKnownUpdated;
            r.Support = mate.ProfileId;
            r.Reference = mate.Position;
            r.Origin = b.Position;
            r.Group = ManeuverGroup(b);
            r.Indoor = b.Player.Environment == EnvironmentType.Indoor;
            r.Goal = nav.position;
            r.Route = path;
            r.Length = path == null ? 0 : (float)Length(path.corners);
            r.Rejects.Clear();
            r.Samples.Clear();
            r.Candidates.Clear();
            var t = r.Policy.Trace!;
            t.CoverMate = new()
            {
                Mate = mate.ProfileId,
                MateName = mate.Player.Profile.Nickname,
                Operation = a.Operation,
                Treatment = kind,
                Sector = threat.EnemyProfileId,
                Busy = true,
                Protectors = 1,
            };
            t.Contact = r.Contact;
            t.Source = a.Source;
            t.Support = r.Support;
            t.SupportName = mate.Player.Profile.Nickname;
            t.X = known.x;
            t.Y = known.y;
            t.Z = known.z;
            r.Policy.Ready(r.Goal.x, r.Goal.y, r.Goal.z, 0, r.Length);
            t.Reason = "cover-mate-busy-teammate";
            t.Execution = "moving beside teammate";
            RecordCrossfire(s, true);
            return true;
        }
        return DeclineSquad(s, "S04", "cover-mate-no-busy-teammate-position");
    }

    private CrossfireConfig CoverMateMotionConfig() =>
        new()
        {
            Enabled = config!.Enabled && config.CoverMate.Enabled,
            Participation = 100,
            MoveTimeout = 12,
            HoldTimeout = config.CoverMate.Duration,
            Retry = config.CoverMate.Retry,
        };

    private bool RefreshCoverMate(BotComponent b, BotState s)
    {
        var r = s.Crossfire;
        var a = r.Care;
        var t = r.Policy.Trace!;
        var d = t.CoverMate!;
        string block =
            !CrossfireScope(b) || !config!.CoverMate.Enabled
                ? "cover-mate-out-of-scope"
                : CrossfireCommonBlock(b, s, allowGoalVisible: true);
        if (block.Length > 0)
        {
            EndCrossfire(b, s, block);
            return false;
        }
        if (!b.Squad.Members.TryGetValue(a.Mate, out var mate) || mate == null || mate.IsDead)
        {
            EndCrossfire(b, s, "cover-mate-gone");
            return false;
        }
        double elapsed = Time.time - a.Started;
        d.Busy = CoverMateBusy(mate, out var kind);
        if (d.Busy)
            d.Treatment = kind;
        d.WeaponReady = !d.Busy;
        d.OperationAge = elapsed;
        if (!d.Busy && (t.State == "holding" || elapsed > 3))
        {
            EndCrossfire(b, s, "cover-mate-done");
            return false;
        }
        if (elapsed > config!.CoverMate.Duration)
        {
            EndCrossfire(b, s, "cover-mate-timeout");
            return false;
        }
        if (
            (mate.Position - a.MateOrigin).sqrMagnitude > 2.25f
            || (mate.Position - b.Position).sqrMagnitude < 1
        )
        {
            EndCrossfire(b, s, "cover-mate-moved");
            return false;
        }
        d.Remaining = Math.Max(0, config.CoverMate.Duration - elapsed);
        d.Separation = Vector3.Distance(b.Position, mate.Position);
        t.Remaining = d.Remaining;
        d.Status = t.State == "holding" ? "covering" : "moving";
        d.Confirmed = a.Confirmed;
        t.Applied = r.Layer && t.State is "moving" or "holding";
        t.ValidFor = t.Applied ? Math.Min(1, d.Remaining) : 0;
        RecordCrossfire(s, false);
        return true;
    }

    private void ExecuteCoverMate(BotComponent b, BotState s)
    {
        s.Crossfire.Care.LastCallback = Time.time;
        ExecuteCrossfire(b, s);
    }

    private void ConfirmCoverMate(BotComponent b, BotState s)
    {
        var r = s.Crossfire;
        if (r.Policy.Trace?.CoverMate == null || !r.Layer)
            return;
        r.Care.Confirmed =
            r.Policy.Trace.State == "holding"
            && Vector3.Angle(b.Player.LookDirection, r.Known - b.Position) <= 30;
        r.Policy.Trace.CoverMate.Confirmed = r.Care.Confirmed;
    }

    private void EndCoverMate(BotState s, string reason)
    {
        var r = s.Crossfire;
        if (r.Policy.Trace?.CoverMate == null)
            return;
        coverMateBoard.Release(s.Id);
        r.Care.RetryAt = Time.time + (float)(config?.CoverMate.Retry ?? 4);
        var t = r.Policy.Trace.CoverMate;
        t.Status = "ended";
        t.Reason = reason;
        t.Confirmed = false;
        r.Care.Confirmed = false;
    }
}
