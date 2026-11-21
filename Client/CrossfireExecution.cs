using System;
using Comet.Core;
using SAIN.Components;
using SAIN.SAINComponent.Classes.Mover;
using UnityEngine;
using UnityEngine.AI;

namespace Comet.Client;

public sealed partial class Plugin
{
    private void BuildCrossfireCandidates(BotComponent b, CrossfireRuntime r)
    {
        r.Candidates.Clear();
        r.Samples.Clear();
        r.FallbackPass = false;
        r.Candidate = 0;
        r.Route = null;
        r.Length = float.MaxValue;
        var from = r.Reference - r.Known;
        from.y = 0;
        if (from.sqrMagnitude < 1)
        {
            from = r.Origin - r.Known;
            from.y = 0;
        }
        float range = Mathf.Clamp((r.Origin - r.Known).magnitude, 10, 40);
        float baseAngle = Mathf.Atan2(from.z, from.x) * Mathf.Rad2Deg;
        float minAngle = (float)(
            r.Indoor
                ? Math.Min(config!.Crossfire.OutdoorAngle, config.Crossfire.IndoorAngle)
                : config!.Crossfire.OutdoorAngle
        );
        var radii = r.Indoor
            ? new[] { 4f, 6f, 8f, Mathf.Clamp(range * .7f, 8, 14) }
            : new[] { Mathf.Max(range, 8), Mathf.Max(range * .7f, 8), Mathf.Max(range * .45f, 8) };
        foreach (float radius in radii)
            for (float offset = minAngle; offset <= 120; offset += 15)
            for (int sign = -1; sign <= 1; sign += 2)
            {
                float angle = (baseAngle + offset * sign) * Mathf.Deg2Rad;
                r.Candidates.Add(
                    r.Known + new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * radius
                );
            }
        var direction = r.Known - r.Origin;
        direction.y = 0;
        if (direction.sqrMagnitude > 1)
            foreach (float step in new[] { 3f, 6f, 9f, 12f })
            {
                var lateral = Vector3.Cross(Vector3.up, direction.normalized) * step;
                r.Candidates.Add(r.Origin + lateral);
                r.Candidates.Add(r.Origin - lateral);
            }
        r.Policy.Trace!.RequiredAngle = minAngle;
        r.Policy.Trace.SearchPass = "zzap-angle-search";
    }

    private static float CrossfireAngle(Vector3 a, Vector3 b, Vector3 target)
    {
        a -= target;
        b -= target;
        a.y = b.y = 0;
        return a.sqrMagnitude < .01f || b.sqrMagnitude < .01f ? 0 : Vector3.Angle(a, b);
    }

    private string CrossfireSpace(BotComponent b, BotState s, Vector3 point, bool goal)
    {
        var r = s.Crossfire;
        var c = config!.Crossfire;
        if (!goal)
            return "";
        if (!crossfireClaims.Free(s.Id, point.x, point.y, point.z, c.Spacing, Time.time, r.Group))
            return "reserved position";
        foreach (var mate in b.Squad.Members.Values)
        {
            if (mate == null || ReferenceEquals(mate, b) || mate.IsDead)
                continue;
            var delta = point - mate.Position;
            delta.y = 0;
            if (delta.magnitude < c.Spacing)
                return "teammate spacing";
        }
        return "";
    }

    private static bool CrossfireSight(Vector3 point, Vector3 known) =>
        CrossfireSightPose(point, known).Length > 0;

    private static string CrossfireSightPose(Vector3 point, Vector3 known)
    {
        if (
            !Physics.Linecast(
                point + Vector3.up * 1.5f,
                known + Vector3.up * 1.3f,
                LayersMaskController.HighPolyWithTerrainMask,
                QueryTriggerInteraction.Ignore
            )
        )
            return "standing";
        return !Physics.Linecast(
            point + Vector3.up * .95f,
            known + Vector3.up * 1.3f,
            LayersMaskController.HighPolyWithTerrainMask,
            QueryTriggerInteraction.Ignore
        )
            ? "crouched"
            : "";
    }

    private bool CrossfireFiringPoint(BotComponent b, BotState s)
    {
        var r = s.Crossfire;
        var t = r.Policy.Trace!;
        t.SightPose = CrossfireSightPose(r.Goal, r.Known);
        if (t.SightPose.Length > 0)
            return true;
        var direction = r.Known - r.Goal;
        direction.y = 0;
        if (direction.sqrMagnitude > .01f)
            for (int sign = -1; sign <= 1; sign += 2)
            {
                var step = r.Goal + Vector3.Cross(Vector3.up, direction.normalized) * sign;
                if (
                    NavMesh.SamplePosition(step, out var nav, .4f, NavMesh.AllAreas)
                    && !Physics.Linecast(
                        nav.position + Vector3.up * 1.5f,
                        r.Known + Vector3.up * 1.3f,
                        LayersMaskController.HighPolyWithTerrainMask
                    )
                )
                {
                    t.SightPose = "standing-sidestep";
                    return true;
                }
            }
        return false;
    }

    // The source session owns its acquisition geometry. Moving support is not a
    // reason to rebuild the search or cancel a route already chosen by the bot.
    private bool RevalidateCrossfireContext(BotComponent b, BotState s) => true;

    private void RejectCrossfire(CrossfireRuntime r, string why)
    {
        var trace = r.Policy.Trace!;
        if (trace.RejectionCounts.TryGetValue(why, out var count))
            trace.RejectionCounts[why] = count + 1;
        else if (trace.RejectionCounts.Count < 24)
            trace.RejectionCounts[why] = 1;
        if (!r.Rejects.Contains(why) && r.Rejects.Count < 8)
        {
            r.Rejects.Add(why);
            trace.Alternatives = r.Rejects.ToArray();
        }
        r.Route = null;
        r.Samples.Clear();
        r.Sample = 0;
    }

    private void ProbeCrossfire(BotComponent b, BotState s)
    {
        var r = s.Crossfire;
        var c = config!.Crossfire;
        var trace = r.Policy.Trace!;
        // Evaluate the same complete candidate set as zzap, spread over frames.
        // A scheduling budget changes latency, never candidate eligibility.
        if (r.Candidate < r.Candidates.Count)
        {
            var raw = r.Candidates[r.Candidate++];
            trace.Candidates++;
            trace.CandidateX = raw.x;
            trace.CandidateY = raw.y;
            trace.CandidateZ = raw.z;
            void Reject(string why)
            {
                trace.RejectionCounts.TryGetValue(why, out int n);
                trace.RejectionCounts[why] = n + 1;
            }
            if (!NavMesh.SamplePosition(raw, out var nav, 2f, NavMesh.AllAreas))
            {
                Reject("no nearby navigation");
                return;
            }
            var delta = nav.position - r.Known;
            delta.y = 0;
            float angle = CrossfireAngle(r.Reference, nav.position, r.Known);
            trace.CandidateAngle = angle;
            if (delta.magnitude < 5 || angle < trace.RequiredAngle)
            {
                Reject("angle gain too small");
                return;
            }
            var previousGoal = r.Goal;
            var previousPose = trace.SightPose;
            r.Goal = nav.position;
            string deny = CrossfireSpace(b, s, r.Goal, true);
            if (deny.Length == 0 && !CrossfireFiringPoint(b, s))
                deny = "no firing angle";
            if (deny.Length == 0 && b.Mover.CanGoToPoint(r.Goal, out var route, true))
            {
                float length = 0;
                var corners = route.corners;
                for (int i = 1; i < corners.Length; i++)
                    length += Vector3.Distance(corners[i - 1], corners[i]);
                if (length <= c.MaxPath && length < r.Length)
                {
                    r.Route = route;
                    r.Length = length;
                    r.Angle = angle;
                    return;
                }
                deny = "route too long or not shortest";
            }
            else if (deny.Length == 0)
                deny = "no usable route";
            r.Goal = previousGoal;
            trace.SightPose = previousPose;
            Reject(deny);
            return;
        }
        if (r.Route == null)
        {
            EndCrossfire(b, s, "crossfire-no-safe-angle");
            return;
        }
        crossfireClaims.Reserve(s.Id, r.Goal.x, r.Goal.y, r.Goal.z, c.Spacing, Time.time, r.Group);
        r.Policy.Ready(r.Goal.x, r.Goal.y, r.Goal.z, r.Angle, r.Length);
    }

    private static bool OwnCrossfirePath(BotComponent b, CrossfireRuntime r) =>
        r.Path != null
        && ReferenceEquals(b.Mover.ActivePath, r.Path)
        && r.Path.TimeStarted == r.PathStarted
        && Vector3.Distance(r.Path.Destination, r.Goal) < .5f;

    private static void StopCrossfireRoute(BotComponent b, CrossfireRuntime r)
    {
        try
        {
            if (
                b != null
                && OwnCrossfirePath(b, r)
                && r.Path is BotPathDataManual path
                && b.Player.MovementContext.IsGrounded
            )
                combatStopPath.Invoke(path, new object[] { false, "COMET crossfire released" });
        }
        finally
        {
            r.Path = null;
        }
    }

    private void ExecuteCrossfire(BotComponent b, BotState s)
    {
        var r = s.Crossfire;
        var c =
            r.Policy.Trace?.Trade != null ? TradeMotionConfig()
            : r.Policy.Trace?.CoverMate != null ? CoverMateMotionConfig()
            : config!.Crossfire;
        var t = r.Policy.Trace!;
        if (t.State == "ready")
        {
            r.NextMoveOrder = 0;
            r.Peek?.Reset();
            r.Policy.Moving(
                Time.time,
                Vector3.ProjectOnPlane(b.Position - r.Goal, Vector3.up).magnitude
            );
        }
        if (t.State == "moving")
        {
            // Observations only: retain the source's 12-second move timeout.
            t.SprintObserved = b.Player.IsSprintEnabled;
            t.MoveSpeed = b.Player.Velocity.magnitude;
            t.DistanceToGoal = Vector3.ProjectOnPlane(r.Goal - b.Position, Vector3.up).magnitude;
            if (
                !r.Policy.Progress(
                    Time.time,
                    Vector3.ProjectOnPlane(b.Position - r.Goal, Vector3.up).magnitude,
                    c
                )
            )
            {
                EndCrossfire(b, s, t.Reason);
                return;
            }
            if (t.State == "moving" && Time.time >= r.NextMoveOrder)
            {
                r.NextMoveOrder = Time.time + .75f;
                bool sprint =
                    r.Policy.Trace?.Trade?.Choice == "push"
                    || Vector3.ProjectOnPlane(r.Goal - b.Position, Vector3.up).magnitude > 8;
                t.MoveOrders++;
                t.SprintRequested = sprint;
                long moveStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                bool accepted = sprint
                    ? b.Mover.RunToPoint(
                        r.Goal,
                        r.Policy.Trace?.Trade?.Choice != "push",
                        .56f,
                        SAIN.Models.Enums.ESprintUrgency.High
                    )
                    : b.Mover.WalkToPoint(r.Goal, true, .56f);
                t.MoveAccepted = accepted;
                t.MoveOrderMilliseconds =
                    (System.Diagnostics.Stopwatch.GetTimestamp() - moveStarted)
                    * 1000d
                    / System.Diagnostics.Stopwatch.Frequency;
                if (!accepted)
                {
                    EndCrossfire(b, s, "crossfire-noPath");
                    return;
                }
                r.Path = b.Mover.ActivePath;
                if (r.Path != null)
                    r.PathStarted = r.Path.TimeStarted;
            }
            if (t.State == "holding" && t.Entrance != null)
            {
                r.Entrance.Policy.Arrived(Time.time);
                t.Reason = "secondary-entrance-watch-position-reached";
            }
            if (t.State == "holding" && r.ManeuverRole?.Role == "flank")
                r.Maneuver!.BeginConvergence(Time.time, "maneuver-angle-reached");
        }
        if (t.State == "holding")
        {
            r.Peek ??= new SourcePeekSpot(b, "squad hold");
            bool moving =
                config!.HoldAngle.Enabled
                && r.Peek.Tick(
                    r.Known + Vector3.up * 1.3f,
                    r.Known,
                    (float)config.HoldAngle.MaxStep
                );
            if (!moving)
                b.Mover.Stop();
            r.OldPose = r.OwnPose ? r.OldPose : b.Mover.Pose.PoseValue.TargetValue;
            r.AppliedPose =
                r.Peek.StandToSee ? 1
                : t.SightPose == "crouched" ? .55f
                : t.SightPose == "standing" ? 1
                : .8f;
            r.OwnPose = true;
            b.Mover.SetTargetPose(r.AppliedPose);
        }
    }
}
