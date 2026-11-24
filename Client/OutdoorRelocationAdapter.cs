using System;
using System.Collections.Generic;
using System.Linq;
using Comet.Core;
using EFT;
using HarmonyLib;
using Newtonsoft.Json;
using SAIN.Components;
using SAIN.Layers;
using SAIN.Models.Enums;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes;
using SAIN.SAINComponent.Classes.Decision;
using SAIN.SAINComponent.Classes.EnemyClasses;
using SAIN.SAINComponent.Classes.Mover;
using SAIN.SAINComponent.Classes.WeaponFunction;
using SAIN.SAINComponent.SubComponents.CoverFinder;
using UnityEngine;
using UnityEngine.AI;

namespace Comet.Client;

internal sealed class RelocationRuntime(RepositionCooldown cooldown)
{
    internal readonly OutdoorRelocation Policy = new(cooldown);
    internal BotComponent? Owner;
    internal CoverPoint? BestCover,
        Selected;
    internal RelocationCandidate? Best;
    internal readonly List<CoverPoint> Cached = new();
    internal readonly List<Vector3> SourceCandidates = new();
    internal readonly List<Vector3> Threats = new();
    internal readonly Dictionary<IBotPathData, NavMeshPath> Routes = new();
    internal float Next,
        NextProbe,
        LastSafety = -1000,
        ReleaseAt = -1;
    internal int Candidate,
        RequestFrame = -1,
        ProbeFrame = -1;
    internal ECombatDecision Expected;
    internal (int, string, string, bool, string, bool, bool) LogKey;
    internal string GrenadeItem = "";
    internal bool Starting,
        Fault,
        ThrowCall,
        GuardPassed,
        NativeMoveStarted,
        IssuingMove;
    internal int ProjectileId = -1;
    internal WeakReference<Grenade>? Projectile;
    internal Vector3 Blast;
    internal double BlastRadius;

    internal void Drop()
    {
        Owner = null;
        BestCover = Selected = null;
        Best = null;
        Cached.Clear();
        SourceCandidates.Clear();
        Threats.Clear();
        Routes.Clear();
        Projectile = null;
        ProjectileId = -1;
        ReleaseAt = -1;
        BlastRadius = 0;
        GrenadeItem = "";
        RequestFrame = -1;
        NativeMoveStarted = IssuingMove = false;
    }
}

public sealed partial class Plugin
{
    private static System.Reflection.MethodInfo relocationClearPath = null!,
        relocationPathStatus = null!;

    private static void PatchOutdoorRelocation(Harmony h)
    {
        h.Patch(
            AccessTools.Method(typeof(GrenadeThrowDecider), "TryThrowGrenade", Type.EmptyTypes)
                ?? throw new MissingMethodException("M13 native grenade throw"),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(RelocationThrowGuard))
            {
                priority = Priority.First,
            }
        );
        relocationClearPath = AccessTools.Method(typeof(BotPathDataManual), "ClearCachedData")
            ?? throw new MissingMethodException("M13 queued path cleanup");
        relocationPathStatus = AccessTools.PropertySetter(typeof(BotPathDataManual), "Status")
            ?? throw new MissingMethodException("M13 queued path state");
        h.Patch(
            AccessTools.Method(typeof(BotPathDataManual), "Initialize"),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(RelocationPathInitialized))
        );
        var seekCover = typeof(BotComponent).Assembly.GetType(SeekCoverType, true);
        h.Patch(AccessTools.DeclaredMethod(seekCover, "Update"),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(UpdateRelocationHold)) { priority = Priority.First });
        h.Patch(AccessTools.DeclaredMethod(seekCover, "OnSteeringTicked"),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(SteerRelocationHold)) { priority = Priority.First });
    }

    private bool OwnsRelocationHold(BotAction action, out BotState s)
    {
        var b = action.Bot;
        return states.TryGetValue(b, out s!) && s.Relocation.Policy.Holding
            && s.Relocation.Policy.Trace!.Accepted && ReferenceEquals(b.CurrentAction, action)
            && RelocationControlsMovement(b) && RelocationScope(b, s);
    }

    private static bool UpdateRelocationHold(BotAction __instance, bool __runOriginal)
    {
        var p = Instance;
        if (!__runOriginal || p == null || !p.OwnsRelocationHold(__instance, out var s))
            return __runOriginal;
        try
        {
            var b = __instance.Bot;
            var policy = s.Relocation.Policy;
            if (!policy.Observe(p.RelocationFrame(b, s), p.config!.OutdoorRelocation))
            {
                p.FinishRelocation(s);
                return true;
            }
            b.Mover.Stop();
            b.Mover.SetTargetPose(.8f);
            if (!policy.Trace!.HoldApplied)
            {
                policy.Trace.HoldApplied = true;
                p.RecordRelocation(s, true);
            }
            return false;
        }
        catch (Exception ex)
        {
            p.RelocationError(s, ex);
            return true;
        }
    }

    private static bool SteerRelocationHold(BotAction __instance, bool __runOriginal)
    {
        var p = Instance;
        if (!__runOriginal || p == null || !p.OwnsRelocationHold(__instance, out var s))
            return __runOriginal;
        try
        {
            var b = __instance.Bot;
            if (!TacticalSteeringFire.TryShoot(b, TacticalSteeringKind.OutdoorRelocation))
                b.Steering.LookToPoint(VP(s.Relocation.Policy.Known) + Vector3.up * 1.3f);
            return false;
        }
        catch (Exception ex)
        {
            p.RelocationError(s, ex);
            return true;
        }
    }

    private static void RelocationPathInitialized(BotPathDataManual __instance, NavMeshPath path)
    {
        var p = Instance;
        if (
            __instance.PathFinder is not SAINMoverClass mover
            || p == null
            || !p.states.TryGetValue(mover.Bot, out var s)
        )
            return;
        var r = s.Relocation;
        // Native paths are reused. Every initialization revokes the previous ownership, even at the same destination.
        r.Routes.Remove(__instance);
        if (r.IssuingMove && r.Policy.Active)
            r.Routes[__instance] = path;
    }

    private static bool OwnRelocationRoute(RelocationRuntime r, IBotPathData? path) =>
        path != null
        && r.Routes.TryGetValue(path, out var mesh)
        && ReferenceEquals(mesh, path.NavMeshPath)
        && Vector3.Distance(path.Destination, VP(r.Policy.Goal)) < .35f;

    private bool RelocationScope(BotComponent b, BotState s) =>
        ContactSafety.AllowsGoal(b)
        && config?.Enabled == true
        && config.OutdoorRelocation.Enabled
        && NativeAdapter.IsSupportedAi(b)
        && b.BotActive
        && !b.GameEnding
        && Authority
        && !Ghost(s.ProfileId)
        && b.SAINLayersActive;

    private void ObserveRelocationContact(BotComponent b, BotState s)
    {
        var e = b.GoalEnemy;
        if (e == null)
            return;
        string key = RelocationKey(e);
        s.Relocation.Policy.History.Observe(
            key, e.Status.ShotAtMe || e.Status.ShotMe, e.Seen, e.TimeSinceSeen
        );
    }

    private static string RelocationKey(Enemy e) => e.EnemyProfileId;

    private RelocationFrame RelocationFrame(BotComponent b, BotState s)
    {
        var r = s.Relocation;
        var e = b.GoalEnemy;
        var known = e?.KnownPlaces.LastKnownPosition;
        var f = new RelocationFrame
        {
            Now = Time.time,
            Target = e?.EnemyProfileId ?? "",
            Key = e == null ? "" : RelocationKey(e),
            Position = DP(b.Position),
            Known = DP(known ?? b.Position),
            ContactAge = e?.TimeSinceLastKnownUpdated ?? 999,
            SightAge = e?.Seen == true ? e.TimeSinceSeen : 999,
            Visible = e?.IsVisible == true,
            Source = known.HasValue ? RelocationContactSource(e!) : "",
            InCover = b.Cover.CoverInUse != null,
            Speed = b.Player.Velocity.magnitude,
            HandsReady = VisibleAdapter.WeaponReady(b),
            Distance = e?.IsVisible == true ? e.RealDistance
                : known.HasValue ? Vector3.Distance(b.Position, known.Value) : 0,
            Hurt = b.Memory.Health.HealthStatus != ETagStatus.Healthy,
            HealthyPusher =
                b.Memory.Health.HealthStatus == ETagStatus.Healthy
                && b.Info.PersonalitySettings.Rush.CanRushEnemyReloadHeal,
        };
        f.Outdoor = !(
            b.Memory.Location.IsIndoors || b.Player.Environment == EnvironmentType.Indoor
        );
        var weapon = e?.EnemyPlayerComponent?.Equipment?.CurrentWeaponInfo;
        f.LongWeapon =
            weapon != null
            && (
                weapon.WeaponClass.ToString() == "marksmanRifle"
                || weapon.WeaponClass.ToString() == "sniperRifle"
                || weapon.HasOptic
            );
        if (b.Squad?.Members != null)
            foreach (var mate in b.Squad.Members.Values)
            {
                if (
                    mate != null
                    && !ReferenceEquals(mate, b)
                    && !mate.IsDead
                    && mate.GoalEnemy?.EnemyProfileId == f.Target
                    && (
                        mate.GoalEnemy.IsVisible
                        || mate.GoalEnemy.Seen && mate.GoalEnemy.TimeSinceSeen < 5
                    )
                )
                {
                    f.MatesEngaging = true;
                    break;
                }
            }
        if (!RelocationScope(b, s))
            f.Block = "relocate-ineligible";
        else if (e == null || !r.Policy.Active && !known.HasValue)
            f.Block = "relocate-contact-unavailable";
        else if (!r.Policy.Active && b.Decision.CurrentSelfDecision != ESelfActionType.None)
            f.Block = "relocate-native-self-action";
        else if (!r.Policy.Active && b.BotOwner.WeaponManager.Grenades.ThrowindNow)
            f.Block = "relocate-hands-busy";
        else if (b.BotOwner.Memory.IsUnderFire && r.Policy.Trace?.State != "moving")
            f.Block = "relocate-under-immediate-fire";
        f.OtherTactic = s.CoverFeint.Policy.Active
            || s.Entry.Policy.Active || s.Door.Policy.Active || s.Jump.Policy.Active
            || s.RunBy.Policy.Active || s.StepPeek.Policy.Active || s.Fake.Policy.Active
            || s.RoomClear.Policy.Active || s.Emergency.Policy.Active || s.Ambush.Policy.Active
            || s.Support != null || s.Reservation.Lease != null;
        foreach (var other in b.EnemyController.KnownEnemies)
        {
            if (other != null && !SameTacticalContact(other, e) && other.IsVisible)
            {
                f.Block = "relocate-other-immediate-threat";
                break;
            }
        }
        if (f.Block.Length == 0
            && (r.Policy.Holding && r.Policy.Trace!.Accepted || r.Policy.Trace?.State == "moving" && r.NativeMoveStarted)
            && !RelocationControlsMovement(b))
            f.Block = "relocate-native-priority";
        if (r.Policy.Trace?.State == "moving")
        {
            f.NativeMoveStarted = r.NativeMoveStarted;
            if (r.NativeMoveStarted)
            {
                var cover = b.Cover.CoverPoint_MovingTo ?? b.Cover.CoverInUse;
                var active = b.Mover.ActivePath;
                if (active?.Moving != true || active is BotPathDataManual { CancelRequested: true })
                    active = null;
                var queued = QueuedDoorPath(b);
                // By-way movement does not populate PathStatus. Use the actual mesh for travel evidence.
                f.Route = r.Policy.ClassifyRoute(
                    cover != null ? DP(cover.Position) : null,
                    active != null ? DP(active.Destination) : null,
                    queued != null ? DP(queued.Destination) : null,
                    active?.NavMeshPath?.status == NavMeshPathStatus.PathComplete
                );
                if (f.Route == RelocationRoute.ToSelectedCover && !OwnRelocationRoute(r, active))
                    r.Policy.Trace.NativePathReused = true;
            }
        }
        return f;
    }

    private static bool RelocationControlsMovement(BotComponent b) =>
        b.Decision.CurrentCombatDecision == ECombatDecision.SeekCover
        && b.Decision.CurrentSelfDecision == ESelfActionType.None
        && b.Decision.CurrentSquadDecision == ESquadDecision.None
        && (b.CurrentAction == null || b.CurrentAction.GetType().FullName == SeekCoverType);

    private static string RelocationContactSource(Enemy e)
    {
        var k = e.KnownPlaces;
        return ReferenceEquals(k.LastKnownPlace, k.LastSeenPlace) ? "seen"
            : ReferenceEquals(k.LastKnownPlace, k.LastHeardPlace) ? "heard"
            : ReferenceEquals(k.LastKnownPlace, k.LastSquadSeenPlace) ? "shared-seen"
            : ReferenceEquals(k.LastKnownPlace, k.LastSquadHeardPlace) ? "shared-heard"
            : "sain-last-known";
    }

    private bool ChooseRelocation(
        BotComponent b,
        Enemy e,
        BotState s,
        ref ECombatDecision result,
        ref bool succeeded
    )
    {
        var r = s.Relocation;
        if (config == null || r.Fault)
            return false;
        try
        {
            ObserveRelocationContact(b, s);
            if (!r.Policy.Active)
            {
                if (Time.time < r.Next)
                    return false;
                r.Next = Time.time + .4f;
                if (Time.time < s.RepositionCooldown.Until)
                    return false;
                var f = RelocationFrame(b, s);
                var previous = r.Policy.Trace;
                if (!r.Policy.Begin(f, config.OutdoorRelocation, jumpRandom.NextDouble))
                {
                    if (!ReferenceEquals(previous, r.Policy.Trace))
                        RecordRelocation(s, true);
                    return false;
                }
                // Release the prior COMET selection without destroying this newly created relocation policy.
                r.Starting = true;
                try
                {
                    Release(s, "relocate-start");
                }
                finally
                {
                    r.Starting = false;
                }
                r.Owner = b;
                r.Candidate = 0;
                r.Best = null;
                r.BestCover = null;
                r.Selected = null;
                r.NextProbe = 0;
                r.LastSafety = -1000;
                r.NativeMoveStarted = false;
                r.Cached.Clear();
                r.SourceCandidates.Clear();
                EnsureClearEvents();
                RecordRelocation(s, true);
            }
            RefreshRelocation(b, s, true);
            if (!r.Policy.Active)
                return false;
            r.Expected =
                r.Policy.Trace!.State is "moving" or "holding" ? ECombatDecision.SeekCover
                : r.Policy.Trace.State == "throwing" ? ECombatDecision.ThrowGrenade
                : ECombatDecision.Freeze;
            r.RequestFrame = Time.frameCount;
            result = r.Expected;
            succeeded = true;
            return true;
        }
        catch (Exception ex)
        {
            RelocationError(s, ex);
            return false;
        }
    }

    private bool RelocationAccepted(BotDecisionManager manager, BotState s)
    {
        var r = s.Relocation;
        if (!r.Policy.Active)
            return false;
        bool ours =
            (
                r.RequestFrame == Time.frameCount
                || r.Policy.Trace?.State == "throwing"
                    && manager.Bot.BotOwner.WeaponManager.Grenades.ThrowindNow
            )
            && manager.CurrentCombatDecision == r.Expected
            && manager.CurrentSelfDecision == ESelfActionType.None
            && manager.CurrentSquadDecision == ESquadDecision.None;
        r.RequestFrame = -1;
        r.Policy.NativeAccepted(ours);
        if (!ours)
        {
            EndRelocation(s, "relocate-native-priority");
            return false;
        }
        RecordRelocation(s);
        return true; // Preserve RR's native behavior label; M13 appears as contextual telemetry.
    }

    private void SampleRelocation(BotComponent b, BotState s)
    {
        var r = s.Relocation;
        if (r.Fault)
            return;
        if (!RelocationScope(b, s))
        {
            EndRelocation(s, "relocate-ineligible", b == null || b.IsDead || b.GameEnding);
            return;
        }
        ObserveRelocationContact(b, s);
        if (r.Policy.Active)
            RefreshRelocation(b, s, false);
    }

    private void RefreshRelocation(BotComponent b, BotState s, bool decisionTick)
    {
        var r = s.Relocation;
        if (!r.Policy.Active || config == null)
            return;
        var f = RelocationFrame(b, s);
        if (!r.Policy.Observe(f, config.OutdoorRelocation))
        {
            FinishRelocation(s);
            return;
        }
        var t = r.Policy.Trace!;
        if (
            t.State == "planning"
            && r.ProbeFrame != Time.frameCount
            && doorBudget.TryBegin(Time.frameCount, b)
        )
        {
            r.ProbeFrame = Time.frameCount;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                // Consume radial samples in order within the existing shared physics budget.
                // A skipped radius or rejected sample need not cost a whole rendered frame.
                do
                {
                    PlanRelocation(b, s);
                } while (r.Policy.Trace?.State == "planning" && clock.Elapsed.TotalMilliseconds < .4);
            }
            finally
            {
                clock.Stop();
                doorBudget.Charge(clock.Elapsed.TotalMilliseconds);
            }
        }
        if (!r.Policy.Active)
            return;
        // The chooser initiates a native throw only after a fresh complete destination check.
        if (
            t.State == "ready"
            && decisionTick
            && Time.time >= r.NextProbe
            && doorBudget.TryBegin(Time.frameCount, b)
        )
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                if (!RecheckRelocation(b, s))
                {
                    EndRelocation(s, "relocate-selected-cover-no-longer-safe");
                    return;
                }
                string why = "disabled";
                if (config.OutdoorRelocation.Grenade && f.Distance > 8 && f.Distance < 45)
                    why = PrepareRelocationGrenade(b, s);
                if (why.Length == 0)
                {
                    var manager = b.BotOwner.WeaponManager.Grenades;
                    r.GrenadeItem = manager.grenade.Id.ToString();
                    r.ThrowCall = true;
                    r.GuardPassed = false;
                    try
                    {
                        bool ok = NativeAdapter.Throw(b, b.GoalEnemy, out var reason);
                        r.Policy.Throw(
                            ok && r.GuardPassed,
                            f.Now,
                            ok && r.GuardPassed ? "native-accepted" : reason
                        );
                        if (ok && r.GuardPassed && r.ReleaseAt >= f.Now)
                            r.Policy.Projectile(r.ReleaseAt);
                    }
                    finally
                    {
                        r.ThrowCall = false;
                    }
                }
                else
                    t.GrenadeOutcome = why;
                if (t.State == "ready")
                {
                    r.BlastRadius = 0;
                    StartRelocationMove(b, s);
                }
            }
            finally
            {
                clock.Stop();
                doorBudget.Charge(clock.Elapsed.TotalMilliseconds);
            }
        }
        else if (
            t.State == "throwing"
            && r.Policy.CanMove(f)
            && decisionTick
            && doorBudget.TryBegin(Time.frameCount, b)
        )
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                StartRelocationMove(b, s);
            }
            finally
            {
                clock.Stop();
                doorBudget.Charge(clock.Elapsed.TotalMilliseconds);
            }
        }
        if (t.State == "moving" && t.Accepted
            && (r.NativeMoveStarted || b.CurrentAction != null && RelocationControlsMovement(b))
            && !OrderRelocationMove(b, s, f))
            return;
        RecordRelocation(s);
    }

    private static readonly float[] relocationRadii = { 5f, 8f, 12f, 17f, 23f };

    private void PlanRelocation(BotComponent b, BotState s)
    {
        var r = s.Relocation;
        var c = config!.OutdoorRelocation;
        // Same radial samples, bearing checks and first eight paths as RepositionClass.FindPoint.
        // Work is spread over frames without changing the candidate order or expiring the plan.
        if (r.Candidate < 60)
        {
            int i = r.Candidate++;
            float radius = relocationRadii[i / 12];
            if (radius < c.MinDistance || radius > c.MaxDistance)
                return;
            r.Policy.Trace!.Samples++;
            float angle = ((i % 12) * 30 + radius * 7) * Mathf.Deg2Rad;
            var origin = VP(r.Policy.Origin);
            var known = VP(r.Policy.Known);
            var raw = origin + new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * radius;
            if (!NavMesh.SamplePosition(raw, out var hit, 1.2f, NavMesh.AllAreas))
                return;
            var offset = hit.position - raw;
            offset.y = 0;
            if (Math.Abs(hit.position.y - origin.y) > 1.2f || offset.magnitude > 1)
                return;
            var from = origin - known;
            var to = hit.position - known;
            from.y = to.y = 0;
            if (
                Vector3.Angle(from, to) < c.PreferredAngle
                || to.magnitude < 4
                || to.magnitude < from.magnitude * .7f
            )
                return;
            if (
                !Physics.Linecast(
                    known + Vector3.up * 1.5f,
                    hit.position + Vector3.up * 1.3f,
                    LayersMaskController.HighPolyWithTerrainMask,
                    QueryTriggerInteraction.Ignore
                )
            )
                return;
            r.SourceCandidates.Add(hit.position);
            r.Policy.Trace.ConcealedPoints++;
            return;
        }
        if (r.Candidate == 60)
        {
            var origin = VP(r.Policy.Origin);
            float Distance(Vector3 v)
            {
                v -= origin;
                v.y = 0;
                return v.sqrMagnitude;
            }
            r.SourceCandidates.Sort((a, b) => Distance(a).CompareTo(Distance(b)));
        }
        int index = r.Candidate++ - 60;
        if (index >= 8 || index >= r.SourceCandidates.Count)
        {
            EndRelocation(s, "relocate-no-usable-source-candidate");
            return;
        }
        var v = EvaluateRelocationPoint(
            b,
            s,
            r.SourceCandidates[index],
            "radial-" + index,
            out var native
        );
        r.Policy.Candidate(v, c);
        if (OutdoorRelocation.Reject(v, c).Length > 0 || native == null)
            return;
        r.Best = v;
        r.BestCover = r.Selected = native;
        r.Policy.Select(v, c);
    }

    private RelocationCandidate EvaluateRelocationPoint(
        BotComponent b,
        BotState s,
        Vector3 raw,
        string id,
        out CoverPoint? native
    )
    {
        native = null;
        var r = s.Relocation;
        var c = config!.OutdoorRelocation;
        var origin = VP(r.Policy.Origin);
        var known = VP(r.Policy.Known);
        var v = new RelocationCandidate { Id = id };
        if (
            !NavMesh.SamplePosition(raw, out var hit, 1.2f, NavMesh.AllAreas)
            || Math.Abs(hit.position.y - origin.y) > 1.2f
            || new Vector2(raw.x - hit.position.x, raw.z - hit.position.z).magnitude > 1
        )
        {
            v.Refusal = "relocate-navmesh-or-floor";
            return v;
        }
        var goal = hit.position;
        v.Position = DP(goal);
        v.Distance = new Vector2(origin.x - goal.x, origin.z - goal.z).magnitude;
        v.OriginalProtection = 0;
        v.Protection = 0;
        if (
            !Physics.Linecast(
                known + Vector3.up * 1.5f,
                goal + Vector3.up * 1.3f,
                out var obstacle,
                LayersMaskController.HighPolyWithTerrainMask,
                QueryTriggerInteraction.Ignore
            )
            || obstacle.collider == null
        )
        {
            v.Refusal = "relocate-no-cover-surface";
            return v;
        }
        if (IsDoorCover(obstacle.collider))
        {
            v.Refusal = "door-leaf-or-frame";
            CountNativeFix(NativeFix.DoorCoverRejected, b);
            return v;
        }
        if (SeasonalCoverScope(b) && BareFoliageAt(goal, known))
        {
            v.Refusal = "seasonal-cover-position-inside-foliage";
            RecordSeasonalCover(b, obstacle.collider, v.Refusal);
            return v;
        }
        var route = new PathData();
        route.CalcPath(RemixMovementOrigin(b, b.Position, preserveOnMesh: true), goal);
        v.PathLength = route.PathLength;
        var corners = route.Path?.corners;
        if (
            route.Path?.status != NavMeshPathStatus.PathComplete
            || corners == null
            || corners.Length < 2
            || v.PathLength > c.MaxPath
        )
        {
            v.Refusal = "relocate-no-complete-bounded-route";
            return v;
        }
        v.Exposure = 0;
        var from = origin - known;
        var to = goal - known;
        from.y = to.y = 0;
        v.Angle = Vector3.Angle(from, to);
        v.Safe = true;
        native = new CoverPoint(b, obstacle.collider, route, goal);
        return v;
    }

    private static int Bits(int n) => (n & 1) + ((n >> 1) & 1) + ((n >> 2) & 1);

    private bool RelocationRouteSafety(
        BotComponent b,
        BotState s,
        Vector3[] corners,
        Vector3 goal,
        out double exposure,
        out string why
    )
    {
        exposure = 0;
        why = "";
        return true;
    }

    private static Vector3 RelocationRoutePoint(Vector3[] path, float distance)
    {
        for (int i = 1; i < path.Length; i++)
        {
            float n = Vector3.Distance(path[i - 1], path[i]);
            if (n >= distance)
                return Vector3.Lerp(path[i - 1], path[i], n > 0 ? distance / n : 0);
            distance -= n;
        }
        return path[path.Length - 1];
    }

    private bool RecheckRelocation(BotComponent b, BotState s)
    {
        var r = s.Relocation;
        if (r.Best == null)
            return false;
        var v = EvaluateRelocationPoint(b, s, VP(r.Best.Position), r.Best.Id, out var point);
        if (OutdoorRelocation.Reject(v, config!.OutdoorRelocation).Length > 0 || point == null)
            return false;
        if (r.Policy.Trace?.State != "moving")
        {
            r.Best = v;
            r.Selected = point;
        }
        else
        {
            var active = b.Mover.ActivePath;
            if (OwnRelocationRoute(r, active))
            {
                var actual = RemainingRelocationRoute(b, active!);
                if (
                    actual == null
                    || !RelocationRouteSafety(b, s, actual, r.Selected!.Position, out _, out _)
                )
                    return false;
            }
            r.Selected!.PathData.CalcPath(
                RemixMovementOrigin(b, b.Position, preserveOnMesh: true),
                r.Selected.Position
            );
            if (r.Selected.PathToPoint.status != NavMeshPathStatus.PathComplete)
                return false;
        }
        r.LastSafety = Time.time;
        r.NextProbe = Time.time + .25f;
        return true;
    }

    private void StartRelocationMove(BotComponent b, BotState s)
    {
        var r = s.Relocation;
        if (r.Selected == null)
            return;
        // Native SeekCover can start via FindCover or the next accepted movement update.
        b.Cover.StopSeekingCover();
        r.Routes.Clear();
        r.NativeMoveStarted = false;
        r.Policy.MoveRequested(Time.time, DP(b.Position));
    }

    private bool FindRelocationCover(SAINCoverClass cover, BotState s, ref CoverPoint result)
    {
        var r = s.Relocation;
        if (r.Policy.Trace?.State != "moving" || !r.Policy.Trace.Accepted)
            return false;
        var b = cover.Bot;
        try
        {
            if (!RelocationControlsMovement(b))
                return false;
            var f = RelocationFrame(b, s);
            if (!r.Policy.Observe(f, config!.OutdoorRelocation))
            {
                FinishRelocation(s);
                return false;
            }
            if (!r.Policy.Holding && !OrderRelocationMove(b, s, f))
                return false;
            result = r.Selected!;
            RecordRelocation(s);
            return true;
        }
        catch (Exception ex)
        {
            RelocationError(s, ex);
            return false;
        }
    }

    private bool OrderRelocationMove(BotComponent b, BotState s, RelocationFrame f)
    {
        var r = s.Relocation;
        if (!r.Policy.TryBeginMoveOrder(f.Now, f.Route))
            return r.NativeMoveStarted;
        r.IssuingMove = true;
        bool accepted;
        try
        {
            var goal = VP(r.Policy.Goal);
            const float reach = .7f * .7f;
            accepted = !f.Visible
                ? b.Mover.RunToPoint(goal, true, reach, ESprintUrgency.High)
                : b.Mover.WalkToPoint(goal, true, reach);
        }
        finally
        {
            r.IssuingMove = false;
        }
        if (!accepted)
        {
            EndRelocation(s, "relocate-native-cover-refused");
            return false;
        }
        r.NativeMoveStarted = true;
        return true;
    }

    private string PrepareRelocationGrenade(BotComponent b, BotState s)
    {
        string why = NativeAdapter.PrepareRoomGrenade(b, b.GoalEnemy, s, config!);
        if (why.Length > 0)
            return why;
        return RelocationThrowSafety(b, s);
    }

    private string RelocationThrowSafety(BotComponent b, BotState s)
    {
        var r = s.Relocation;
        var g = b.BotOwner.WeaponManager.Grenades;
        var data = g.AIGreanageThrowData;
        var item = g.grenade;
        if (
            item == null
            || item.IsDummy
            || item.ThrowType.ToString().IndexOf("smoke", StringComparison.OrdinalIgnoreCase) >= 0
        )
            return "no-suitable-grenade";
        if (data == null || !data.CanThrow || !data.IsUpToDate())
            return "no-native-throw-arc";
        if (item.GetExplDelay < 2.5)
            return "fuse-too-short";
        r.Blast = data.Target;
        r.BlastRadius = 6;
        return ClearBlastProtected(b.Position, data.Target, 6) ? "" : "own-blast-exposure";
    }

    private bool RelocationBlastSafe(BotComponent b, BotState s)
    {
        var r = s.Relocation;
        if (r.BlastRadius <= 0)
            return true;
        var danger = r.Blast;
        if (r.Projectile?.TryGetTarget(out var projectile) == true && projectile != null)
            danger = projectile.transform.position;
        return ClearBlastProtected(b.Position, danger, 6);
    }

    private static Vector3[]? RemainingRelocationRoute(BotComponent b, IBotPathData path)
    {
        // PathPoints is empty in official SAIN's manual cover mover; use the actual steering corners.
        int index = path.CurrentIndex,
            count = path.PathCorners.Count;
        if (index < 0 || index >= count)
            return null;
        var points = new Vector3[count - index + 1];
        points[0] = b.Position;
        for (int i = index; i < count; i++)
            points[i - index + 1] = path.PathCorners[i].Position;
        return points;
    }

    private static bool RelocationThrowGuard(GrenadeThrowDecider __instance, ref bool __result)
    {
        var p = Instance;
        var b = __instance.Bot;
        if (p == null || !p.states.TryGetValue(b, out var s) || !s.Relocation.ThrowCall)
            return true;
        try
        {
            var r = s.Relocation;
            string why = p.RelocationFrame(b, s).Block;
            if (why.Length == 0)
                why = p.RelocationThrowSafety(b, s);
            if (
                why.Length == 0
                && r.GrenadeItem == b.BotOwner.WeaponManager.Grenades.grenade.Id.ToString()
            )
            {
                r.GuardPassed = true;
                return true;
            }
        }
        catch (Exception) { }
        __result = false;
        return false;
    }

    private void RelocationProjectile(Grenade grenade)
    {
        foreach (var pair in states)
        {
            var r = pair.Value.Relocation;
            if (
                !r.Policy.Active
                || !(r.Policy.Trace?.State == "throwing" || r.ThrowCall && r.GuardPassed)
                || pair.Key.ProfileId != grenade.ProfileId
                || grenade.WeaponSource?.Id.ToString() != r.GrenadeItem
            )
                continue;
            r.Projectile = new(grenade);
            r.ProjectileId = grenade.Id;
            r.ReleaseAt = Time.time;
            r.Policy.Projectile(Time.time);
            RecordRelocation(pair.Value, true);
            break;
        }
    }

    private void RelocationExplosion(string profile, int id)
    {
        foreach (var pair in states)
            if (pair.Key.ProfileId == profile && pair.Value.Relocation.ProjectileId == id)
            {
                pair.Value.Relocation.BlastRadius = 0;
                pair.Value.Relocation.Projectile = null;
                break;
            }
    }

    private void EndRelocation(BotState s, string why, bool clear = false)
    {
        var r = s.Relocation;
        if (r.Starting)
            return;
        bool active = r.Policy.Active;
        if (!active && !clear)
            return;
        if (clear)
            r.Policy.Clear(why, Time.time);
        else
            r.Policy.End(why, Time.time);
        if (active)
            FinishRelocation(s);
        else if (clear)
            r.Drop();
    }

    private void FinishRelocation(BotState s)
    {
        var r = s.Relocation;
        try
        {
            if (r.Owner != null && !r.Owner.IsDead && r.Policy.Trace!.HoldApplied
                && Math.Abs(r.Owner.Mover.Pose.PoseValue.TargetValue - .8f) < .01f)
                r.Owner.Mover.SetTargetPose(1f);
            if (r.Owner != null && !r.Owner.IsDead && !r.Policy.Trace!.Arrived
                && !(r.Policy.Trace.Reason == "relocate-native-priority"
                    && r.NativeMoveStarted && !RelocationControlsMovement(r.Owner)))
            {
                // Clear only our queued generation without PathComplete (which would stop a different active route).
                foreach (var path in r.Routes.Keys.ToArray())
                    if (
                        OwnRelocationRoute(r, path)
                        && !ReferenceEquals(r.Owner.Mover.ActivePath, path)
                        && path.Status == EBotMoveStatus.ReadyToMove
                    )
                    {
                        relocationPathStatus.Invoke(path, new object[] { EBotMoveStatus.Complete });
                        relocationClearPath.Invoke(path, null);
                    }
                var active = r.Owner.Mover.ActivePath;
                if (OwnRelocationRoute(r, active) && active is BotPathDataManual manual)
                    combatStopPath.Invoke(manual, new object[] { false, "COMET relocation ended" });
                if (r.Selected != null && ReferenceEquals(r.Owner.Cover.CoverPoint_MovingTo, r.Selected))
                    r.Owner.Cover.StopSeekingCover();
            }
            RecordRelocation(s, true);
        }
        finally
        {
            r.Drop();
        }
    }

    private void RelocationError(BotState s, Exception ex)
    {
        if (s.Relocation.Fault)
            return;
        s.Relocation.Fault = true;
        EndRelocation(s, "relocate-adapter-error", true);
        Logger.LogWarning("M13 yielded: " + ex.GetBaseException().Message);
    }

    private void RecordRelocation(BotState s, bool force = false)
    {
        var r = s.Relocation;
        var t = r.Policy.Trace;
        if (t == null)
            return;
        var key = (t.Episode, t.State, t.Reason, t.Accepted, t.GrenadeOutcome,
            t.MovementObserved, r.NativeMoveStarted);
        if (!force && key == r.LogKey)
            return;
        r.LogKey = key;
        if (!Comet.Core.ReplayCapture.Detailed)
            return;
        var json = Diagnostics.Serialize(
            new
            {
                schema = 1,
                session,
                sequence = ++sequence,
                feature = "M13",
                stage = "evaluated",
                decision = "OutdoorRelocation",
                reason = t.Reason,
                realtime = Time.realtimeSinceStartup,
                utc = DateTime.UtcNow.ToString("O"),
                configRevision = config?.Revision ?? 0,
                profileId = s.ProfileId,
                name = s.Name,
                botInstance = s.Id,
                outdoorRelocation = t,
                nativeMoveStarted = r.NativeMoveStarted,
                outdoorRelocationConfig = config?.OutdoorRelocation,
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
