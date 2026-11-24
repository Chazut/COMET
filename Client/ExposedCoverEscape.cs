using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using EFT.Ballistics;
using HarmonyLib;
using SAIN.Components;
using SAIN.SAINComponent.Classes;
using SAIN.SAINComponent.Classes.EnemyClasses;
using SAIN.SAINComponent.Classes.Mover;
using SAIN.SAINComponent.SubComponents.CoverFinder;
using UnityEngine;
using UnityEngine.AI;

namespace Comet.Client;

public sealed partial class Plugin
{
    private sealed class CoverEscape(BotComponent bot, CoverPoint point)
    {
        internal readonly Collider OriginCollider = point.Collider;
        internal readonly Vector3 CoverPosition = point.Position;
        internal readonly Vector3 Anchor = bot.Position;
        internal readonly Enemy Contact = bot.GoalEnemy!;
        internal int ArmedFrame = Time.frameCount;
        internal float Until = Time.time + 6f;
        internal bool Repeated, Running;
        internal int Candidate;
        internal NavMeshPath? FirstOpenRoute;
        internal Vector3 Destination, Away;
        internal Vector3? KnownShooter;
        internal float Started;

        internal bool AtOrigin(CoverPoint p) =>
            ReferenceEquals(p.Collider, OriginCollider) && (p.Position - CoverPosition).sqrMagnitude < 1f;
        internal bool Rejects(CoverPoint p) => Repeated && AtOrigin(p);
        internal bool IsOtherCover(CoverPoint? p) =>
            p != null && !AtOrigin(p) && !p.Spotted && !p.CoverData.IsBad;
    }

    private readonly ConditionalWeakTable<BotComponent, CoverEscape> coverEscapes = new();
    private int lastEscapeProbeFrame = -1;
    private bool escapeFaultLogged;

    private CoverEscape? CurrentEscape(BotComponent b)
    {
        if (!coverEscapes.TryGetValue(b, out var e))
            return null;
        if (ReferenceEquals(e.Contact, b.GoalEnemy) && Time.time <= e.Until
            && (e.Running || (b.Position - e.Anchor).sqrMagnitude <= 4f)
            && !e.IsOtherCover(b.Cover.CoverInUse) && !e.IsOtherCover(b.Cover.CoverPoint_MovingTo))
            return e;
        coverEscapes.Remove(b);
        return null;
    }

    private void ObserveEscapeHit(BotComponent b, CoverPoint? held, DamageInfo hit)
    {
        var e = CurrentEscape(b);
        if (e != null)
        {
            if (!e.Running && Time.frameCount != e.ArmedFrame)
            {
                if (!e.Repeated)
                {
                    // The damage event identifies the shooter, but their actual transform
                    // is not evidence. Use SAIN's remembered position or the impact direction.
                    var shooter = hit.Player?.iPlayer?.ProfileId;
                    if (shooter != null && b.EnemyController.Enemies.TryGetValue(shooter, out var known))
                        e.KnownShooter = known.LastKnownPosition;
                    e.Away = e.KnownShooter.HasValue ? e.Anchor - e.KnownShooter.Value : hit.Direction;
                    e.Away.y = 0;
                    if (e.Away.sqrMagnitude < .001f)
                    {
                        e.Away = -b.Transform.LookDirection;
                        e.Away.y = 0;
                    }
                    e.Away = e.Away.sqrMagnitude > .001f ? e.Away.normalized : Vector3.forward;
                    e.Repeated = true;
                }
                e.Until = Time.time + 6f;
            }
        }
        else if (held?.Spotted == true)
        {
            var first = new CoverEscape(b, held);
            if (!first.IsOtherCover(b.Cover.CoverPoint_MovingTo))
                coverEscapes.Add(b, first);
        }
    }


    // Replace only the fallback call after FindCoverPoint actually failed. Each
    // later UpdateCover still tries native cover before it can reach our sprint.
    private static IEnumerable<CodeInstruction> InsertExposedCoverEscape(IEnumerable<CodeInstruction> instructions)
    {
        var native = AccessTools.Method(typeof(DogFight), nameof(DogFight.DogFightMove),
            new[] { typeof(bool), typeof(Enemy) });
        var replacement = AccessTools.Method(typeof(Plugin), nameof(MoveAfterCoverSearch));
        int calls = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(native))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = replacement;
                calls++;
            }
            yield return instruction;
        }
        if (calls != 1)
            throw new InvalidOperationException("Unexpected SAIN no-cover movement call count: " + calls);
    }

    private static void MoveAfterCoverSearch(DogFight mover, bool aggressive, Enemy enemy)
    {
        var p = Instance;
        if (p != null)
        {
            try
            {
                var b = mover.Bot;
                if (p.CurrentEscape(b) is { Repeated: true } e && ReferenceEquals(enemy, e.Contact)
                    && CoverExposureScope(b) && p.TryEscapeExposedCover(b, e))
                    return;
            }
            catch (Exception ex)
            {
                p.coverEscapes.Remove(mover.Bot);
                if (!p.escapeFaultLogged)
                {
                    p.escapeFaultLogged = true;
                    p.Logger.LogWarning("Exposed-cover escape unavailable: " + ex.GetBaseException().Message);
                }
            }
        }
        mover.DogFightMove(aggressive, enemy);
    }

    private bool TryEscapeExposedCover(BotComponent b, CoverEscape e)
    {
        if (e.Running)
        {
            // SAIN queues a new path behind the one it is cancelling. Give it
            // one handoff before testing ownership, never restart a lost path.
            if ((b.Position - e.Destination).sqrMagnitude > 1f
                && (Time.time - e.Started < .35f || b.Mover.ActivePath is { Moving: true } active
                    && (active.Destination - e.Destination).sqrMagnitude < 1f))
                return true;
            coverEscapes.Remove(b);
            return false;
        }
        if (lastEscapeProbeFrame == Time.frameCount)
            return false;
        lastEscapeProbeFrame = Time.frameCount;

        // Try a shorter step if the wider points are all unreachable.
        Vector3? threat = e.KnownShooter;
        Vector3 away = e.Away;
        Vector3 side = new(-away.z, 0, away.x);
        if ((b.GetInstanceID() & 1) != 0)
            side = -side;
        Vector3 offset = (e.Candidate % 3) switch { 0 => side, 1 => -side, _ => away };
        float distance = e.Candidate < 3 ? 9f : 4f;
        e.Candidate++;
        NavMeshPath? route = null;
        if (b.Mover.CanGoToPoint(e.Anchor + offset * distance, out var candidate, true, 1f)
            && candidate.status == NavMeshPathStatus.PathComplete)
        {
            var corners = candidate.corners;
            if (corners.Length >= 2)
            {
                var end = corners[corners.Length - 1];
                if (threat.HasValue && Physics.Linecast(end + Vector3.up, threat.Value + Vector3.up,
                    LayersMaskController.HighPolyWithTerrainMask, QueryTriggerInteraction.Ignore))
                    route = candidate;
                else
                    e.FirstOpenRoute ??= candidate;
            }
        }
        bool finishedRing = e.Candidate == 3 || e.Candidate == 6;
        if (route == null && (!finishedRing || (e.Candidate == 3 && e.FirstOpenRoute == null)))
            return false;
        route ??= e.FirstOpenRoute;
        if (route != null && b.Mover.RunToPointByWay(route, true, .75f, SAIN.Models.Enums.ESprintUrgency.High, false))
        {
            var corners = route.corners;
            e.Destination = corners[corners.Length - 1];
            e.Running = true;
            e.Started = Time.time;
            e.Until = Time.time + 6f;
            TraceEscape(b, "started", e);
            return true;
        }
        TraceEscape(b, route == null ? "no-reachable-point" : "move-refused", e);
        // Retry only on fresh damage at this spot, including when SAIN never
        // reacquired the original point. Native movement runs in the meantime.
        e.Repeated = false;
        e.ArmedFrame = Time.frameCount;
        e.KnownShooter = null;
        e.FirstOpenRoute = null;
        e.Candidate = 0;
        return false;
    }

    private void TraceEscape(BotComponent b, string result, CoverEscape e)
    {
        if (config?.QuietLogging != false)
            return;
        try
        {
            Logger.LogInfo($"CoverEscape {b.ProfileId} {result} probes={e.Candidate} origin={e.Anchor} destination={e.Destination}");
        }
        catch (Exception ex) { WarnFaultSink(ex); }
    }
}
