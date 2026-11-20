using System;
using System.Linq;
using Comet.Core;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using EFT.InventoryLogic;
using HarmonyLib;
using Newtonsoft.Json;
using SAIN.Components;
using SAIN.Models.Enums;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.EnemyClasses;
using SAIN.SAINComponent.Classes.Mover;
using SAIN.SAINComponent.Classes.WeaponFunction;
using SAIN.SAINComponent.SubComponents;
using UnityEngine;
using UnityEngine.AI;

namespace Comet.Client;

internal sealed class DoorClearRuntime
{
    internal readonly DoorRoomClear Policy = new();
    internal BotComponent? Owner;
    internal DoorWork? Door;
    internal Vector3 Stack,
        Destination;
    internal NavMeshPath? Route;
    internal IBotPathData? OwnedPath;
    internal WeakReference<Grenade>? Projectile;
    internal int TargetInstance,
        Candidate;
    internal float OpenedAt,
        ProjectileAt,
        LastCheck,
        NextCheck,
        LastCallback,
        LogAt = -1000;
    internal double BestScore = double.MaxValue;
    internal bool EntryChecked,
        FeintChecked,
        NativeThrowCall,
        ThrowGuardPassed,
        Fault;
    internal string LogKey = "",
        ThrowRefusal = "";

    internal void Drop()
    {
        Owner = null;
        Door = null;
        Route = null;
        OwnedPath = null;
        Projectile = null;
        NativeThrowCall = false;
    }
}

// Only our real D07 projectile is added to SAIN's ordinary danger tracking. Never invent an enemy.
internal sealed class ClearOwnGrenade : IDisposable
{
    private readonly WeakReference<Grenade> grenade;
    private readonly WeakReference<GrenadeReactionClass> reaction;
    private GrenadeTrackerClass? tracker;
    private readonly WeakReference<BotComponent> bot;

    internal ClearOwnGrenade(BotComponent bot, Grenade item, Vector3 point)
    {
        grenade = new(item);
        reaction = new(bot.Grenade.GrenadeReactionClass);
        this.bot = new(bot);
        tracker = new GrenadeTrackerClass(bot, item, point, 0, true);
        bot.Grenade.GrenadeReactionClass.EnemyGrenadesList.Add(item, tracker);
        item.DestroyEvent += Destroyed;
    }

    private void Destroyed(Throwable item) => Dispose();

    internal bool Alive =>
        tracker != null
        && grenade.TryGetTarget(out var g)
        && g != null
        && bot.TryGetTarget(out var b)
        && b != null
        && !b.IsDead;

    public void Dispose()
    {
        if (!grenade.TryGetTarget(out var g))
        {
            tracker = null;
            return;
        }
        // Unity destroyed objects can still carry managed delegates/keys.
        if (!ReferenceEquals(g, null))
        {
            g.DestroyEvent -= Destroyed;
            if (
                reaction.TryGetTarget(out var r)
                && r.EnemyGrenadesList.TryGetValue(g, out var entry)
                && ReferenceEquals(entry, tracker)
            )
                r.EnemyGrenadesList.Remove(g);
        }
        tracker = null;
    }
}

public sealed partial class Plugin
{
    private GlobalEventDispatcher? clearEvents;
    private readonly System.Collections.Generic.List<ClearOwnGrenade> clearOwnGrenades = new();

    private static void PatchDoorRoomClear(Harmony h)
    {
        // Guard the actual SAIN-selected arc immediately before its native throw, without replacing SAIN's cooldown.
        h.Patch(
            AccessTools.Method(typeof(GrenadeThrowDecider), "TryThrowGrenade", Type.EmptyTypes)
                ?? throw new MissingMethodException("D07 TryThrowGrenade"),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(ClearThrowGuard))
            {
                priority = Priority.First,
            }
        );
    }

    private void EnsureClearEvents()
    {
        var current = Singleton<GlobalEventDispatcher>.Instance;
        if (ReferenceEquals(clearEvents, current))
            return;
        ClearRoomEvents();
        clearEvents = current;
        if (clearEvents != null)
        {
            clearEvents.OnGrenadeThrow += ClearProjectileThrown;
            clearEvents.OnGrenadeExplosive += ClearProjectileExplosion;
        }
    }

    private void ClearRoomEvents()
    {
        if (clearEvents != null)
        {
            clearEvents.OnGrenadeThrow -= ClearProjectileThrown;
            clearEvents.OnGrenadeExplosive -= ClearProjectileExplosion;
        }
        clearEvents = null;
        foreach (var link in clearOwnGrenades)
            link.Dispose();
        clearOwnGrenades.Clear();
    }

    private void SweepClearGrenades()
    {
        for (int i = clearOwnGrenades.Count - 1; i >= 0; i--)
            if (!clearOwnGrenades[i].Alive)
            {
                clearOwnGrenades[i].Dispose();
                clearOwnGrenades.RemoveAt(i);
            }
    }

    private void ClearProjectileThrown(Grenade grenade, Vector3 position, Vector3 force, float mass)
    {
        if (grenade == null || world == null)
            return;
        try
        {
            JudgmentProjectile(grenade);
        }
        catch (Exception ex)
        {
            Logger.LogWarning("G01 throw observation failed: " + ex.GetBaseException().Message);
        }
        try
        {
            RelocationProjectile(grenade);
        }
        catch (Exception ex)
        {
            Logger.LogWarning("G09 throw observation failed: " + ex.GetBaseException().Message);
        }
        try
        {
            OwnProjectileThrown(grenade);
        }
        catch (Exception ex)
        {
            Logger.LogWarning("G04 throw observation failed: " + ex.GetBaseException().Message);
        }
        foreach (var pair in states)
        {
            var r = pair.Value.RoomClear;
            var t = r.Policy.Trace;
            if (
                !r.Policy.Active
                || t?.State != "throwing"
                || pair.Key.ProfileId != grenade.ProfileId
                || grenade.WeaponSource?.Id.ToString() != t.GrenadeItem
            )
                continue;
            r.Projectile = new(grenade);
            r.ProjectileAt = Time.time;
            r.Policy.Projectile(t.GrenadeItem, grenade.Id, Time.time);
            RecordDoorClear(pair.Value, true);
            break;
        }
    }

    private void ClearProjectileExplosion(
        Vector3 position,
        string profile,
        bool smoke,
        float radius,
        float life,
        int id
    )
    {
        if (smoke || world == null)
            return;
        RelocationExplosion(profile, id);
        try
        {
            OwnExplosion(profile, id);
        }
        catch (Exception ex)
        {
            Logger.LogWarning("G04 blast observation failed: " + ex.GetBaseException().Message);
        }
        foreach (var pair in states)
        {
            var r = pair.Value.RoomClear;
            if (
                pair.Key.ProfileId != profile
                || !r.Policy.Active
                || r.Policy.Trace?.Projectile != id
            )
                continue;
            r.Policy.Explosion(id, Time.time);
            r.Projectile = null;
            RecordDoorClear(pair.Value, true);
            break;
        }
    }

    private bool TryStartDoorClear(BotComponent b, Enemy e, BotState s, DoorWork door)
    {
        var r = s.RoomClear;
        if (
            config?.DoorRoomClear.Enabled != true
            || r.Fault
            || r.Policy.Active
            || DoorGate(b, e, s).Length > 0
            || e.KnownPlaces.LastKnownPosition is not Vector3 known
        )
            return false;
        if (Vector3.Distance(b.Position, VP(door.Geometry.Center)) > 3)
            return false;
        bool searching =
            config.DoorSearchClear.Enabled
            && b.Decision.CurrentCombatDecision == ECombatDecision.Search;
        var old = r.Policy.Trace;
        // Exactly one variant gets a roll. A declined D07 attempt cannot fall through into D08.
        bool selected = searching
            ? r.Policy.BeginSearch(
                door.Geometry.Id,
                e.EnemyProfileId,
                s.Situation?.ContactSource ?? "sain-last-known",
                b.Info.Personality.ToString(),
                DP(known),
                e.TimeSinceLastKnownUpdated,
                Time.time,
                jumpRandom.NextDouble(),
                config.DoorRoomClear,
                config.DoorSearchClear,
                config.ContactLifetime,
                config.DoorApproach.ContactLifetime
            )
            : r.Policy.Begin(
                door.Geometry.Id,
                e.EnemyProfileId,
                s.Situation?.ContactSource ?? "sain-last-known",
                b.Info.Personality.ToString(),
                DP(known),
                e.TimeSinceLastKnownUpdated,
                Time.time,
                jumpRandom.NextDouble(),
                config.DoorRoomClear
            );
        if (ReferenceEquals(old, r.Policy.Trace))
            return false;
        if (!selected)
        {
            RecordDoorClear(s, true);
            return false;
        }
        r.Owner = b;
        r.Door = door;
        r.Stack = b.Position;
        r.TargetInstance = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(e);
        r.EntryChecked = false;
        r.Route = null;
        r.OwnedPath = null;
        r.Projectile = null;
        r.Candidate = 0;
        r.BestScore = double.MaxValue;
        r.FeintChecked = false;
        r.LastCallback = r.LastCheck = Time.time;
        r.NextCheck = 0;
        r.ThrowGuardPassed = false;
        r.ThrowRefusal = "";
        EnsureClearEvents();
        StopQuiet(b, s, "quiet-room-clear");
        EndSilentSearch(s, "silent-search-room-clear");
        EndAbandonment(s, "search-check-room-clear");
        RecordDoorClear(s, true);
        return true;
    }

    private string ClearResources(BotComponent b)
    {
        if (MedicalPostureGuard(b) || FakeMedicalPriority(b))
            return "clear-real-medical-priority";
        // zzap uses health/ammunition to weight the plan, not to veto an entry already selected.
        return "";
    }

    private string ClearGate(BotComponent b, BotState s)
    {
        var r = s.RoomClear;
        var t = r.Policy.Trace!;
        var e = b.GoalEnemy;
        if (
            config?.Enabled != true
            || !config.DoorRoomClear.Enabled
            || !config.DoorApproach.Enabled
            || !FakeScope(b)
        )
            return "clear-out-of-scope";
        if (!TacticalDoorSearch(b, s))
            return "clear-search-not-owned";
        if (t.Mode == "search")
        {
            if (!config.DoorSearchClear.Enabled)
                return "search-clear-disabled";
            string search = r.Policy.SearchContact(
                e?.TimeSinceLastKnownUpdated ?? -1,
                Time.time,
                e?.EnemyKnown == true,
                config.ContactLifetime,
                config.DoorApproach.ContactLifetime
            );
            if (search.Length > 0)
                return search;
        }
        if (
            e == null
            || !e.EnemyKnown
            || e.EnemyProfileId != t.Target
            || e.KnownPlaces.LastKnownPosition is not Vector3 known
        )
            return "clear-contact-changed";
        t.ContactAge = e.TimeSinceLastKnownUpdated;
        if (e.IsVisible || b.EnemyController.VisibleEnemies.Count > 0)
            return "clear-visible-contact-native-combat";
        if (b.Grenade.GrenadeDangerPoint != null)
            return "clear-grenade-danger-native-escape";
        if (b.BotOwner.Memory.IsUnderFire)
            return "clear-under-fire";
        if (r.Door == null || !TryDoorGeometry(r.Door.Data, out var g))
            return "clear-door-unavailable";
        bool ownOpen = t.State == "opening";
        if (
            !ownOpen
            && (
                b.DoorOpener.Interacting
                || b.BotOwner.DoorOpener.Interacting
                || JumpInteractionState(b)
            )
        )
            return "clear-foreign-door-interaction";
        if (!ownOpen && t.DoorOpenObserved && g.State != "Open")
            return "clear-door-no-longer-open";
        if (!ownOpen && g.State != "Open" && g.State != "Shut")
            return "clear-door-unavailable";
        return "";
    }

    private bool KeepDoorClear(
        BotComponent b,
        BotState s,
        ref ECombatDecision decision,
        ref bool result
    )
    {
        if (!s.RoomClear.Policy.Active)
            return false;
        RefreshDoorClear(b, s);
        if (!s.RoomClear.Policy.Active)
            return false;
        s.Expected = ECombatDecision.Search;
        s.RequestedFrame = Time.frameCount;
        decision = ECombatDecision.Search;
        result = true;
        return true;
    }

    private void RefreshDoorClear(BotComponent b, BotState s)
    {
        if (!s.RoomClear.Policy.Active || s.Entry.Policy.Active)
            return;
        try
        {
            string why = ClearGate(b, s);
            if (why.Length > 0)
                EndDoorClear(s, why);
        }
        catch (Exception ex)
        {
            s.RoomClear.Fault = true;
            EndDoorClear(s, "clear-adapter-error");
            Logger.LogWarning("COMET room entry yielded: " + ex.Message);
        }
    }

    private bool StepDoorClear(BotComponent b, BotState s)
    {
        var r = s.RoomClear;
        var t = r.Policy.Trace!;
        var e = b.GoalEnemy;
        r.LastCallback = Time.time;
        if (s.Entry.Policy.Active)
        {
            RefreshDoorEntry(b, s);
            return !s.Entry.Policy.Active;
        }
        if (t.State == "preparing" && !r.EntryChecked)
        {
            r.EntryChecked = true;
            // A direct entry variant is chosen before opening the door; no grenade was requested.
            if (
                t.Mode == "recent"
                && r.Door != null
                && r.Door.Data.Door.DoorState == EDoorState.Shut
                && ClearGate(b, s).Length == 0
                && TryStartDoorEntry(b, s, r.Door, "prepared", true)
            )
                return false;
        }
        string gate = ClearGate(b, s);
        if (gate.Length > 0)
        {
            EndDoorClear(s, gate);
            return true;
        }
        r.Policy.Tick(Time.time, FakeWeaponReady(b));
        if (!r.Policy.Active)
        {
            FinishDoorClear(s);
            return true;
        }
        RecordDoorClear(s);
        if (t.State == "feint")
        {
            RefreshDoorFake(b, s);
            if (!s.Fake.Policy.Active)
            {
                var f = s.Fake.Policy.Trace;
                r.Policy.FeintEnd(
                    f?.State == "completed" && f.WeaponReady,
                    f?.Reason ?? "feint-not-confirmed",
                    Time.time
                );
                RecordDoorClear(s, true);
            }
            if (!r.Policy.Active)
            {
                FinishDoorClear(s);
                return true;
            }
            return false;
        }
        if (t.State == "opening")
        {
            if (
                r.Door!.Data.Door.DoorState == EDoorState.Open
                && !JumpInteractionState(b)
                && !b.DoorOpener.Interacting
                && !b.BotOwner.DoorOpener.Interacting
            )
            {
                r.Policy.Opened(Time.time);
                RecordDoorClear(s, true);
            }
            return false;
        }
        if (t.State == "throwing")
            return false;
        if (
            t.State == "waiting-blast"
            && (
                r.Projectile == null
                || !r.Projectile.TryGetTarget(out var projectile)
                || projectile == null
            )
        )
        {
            r.Policy.MissingProjectile();
        }
        if (t.State == "entering")
        {
            bool near = Vector3.Distance(b.Position, r.Destination) <= .5f;
            if (!near && !FollowingClearRoute(b, r))
            {
                EndDoorClear(s, "clear-route-replaced");
                return true;
            }
            r.Policy.Movement(
                DP(b.Position),
                DoorClearGeometry.Depth(r.Door!.Geometry, DP(r.Stack), DP(b.Position)) > .4,
                near || r.OwnedPath?.Status == EBotMoveStatus.Complete,
                FakeWeaponReady(b),
                Time.time
            );
            if (!r.Policy.Active)
            {
                FinishDoorClear(s);
                return true;
            }
        }
        if (Time.time < r.NextCheck)
        {
            b.Steering.LookToPoint(VP(new(t.KnownX, t.KnownY, t.KnownZ)) + Vector3.up * 1.2f);
            return false;
        }
        if (!doorBudget.TryBegin(Time.frameCount, b))
        {
            return false;
        }
        doorClock.Restart();
        try
        {
            r.LastCheck = Time.time;
            r.NextCheck = Time.time + .25f;
            if (t.State == "preparing")
            {
                if (r.Door!.Data.Door.DoorState == EDoorState.Shut)
                {
                    if (!RequestJumpDoorOpen(b, new DoorJumpRuntime { Door = r.Door }))
                    {
                        EndDoorClear(s, "clear-native-open-refused");
                        return true;
                    }
                    r.OpenedAt = Time.time;
                    r.Policy.Opening(Time.time);
                }
                else
                    r.Policy.Opened(Time.time);
            }
            else if (t.State == "planning")
            {
                if (r.Candidate < 6)
                {
                    PlanClearCandidate(b, s, r.Candidate++);
                    r.NextCheck = Time.time;
                    return false;
                }
                if (r.Route == null)
                {
                    EndDoorClear(s, "clear-no-interior-route");
                    return true;
                }
                if (!r.FeintChecked)
                {
                    r.FeintChecked = true;
                    if (
                        config!.DoorRoomClear.AllowFeint
                        && (t.Mode != "search" || config.DoorSearchClear.AllowFeint)
                        && TryStartDoorFake(b, e!, s, r.Door!)
                    )
                    {
                        r.Policy.FeintStart(s.Fake.Policy.Trace!.Kind, Time.time);
                        RecordDoorClear(s, true);
                        return false;
                    }
                }
                r.Policy.Ready(Time.time);
            }
            else if (t.State == "ready")
            {
                string route = ValidateClearRoute(b, r, true);
                if (route.Length > 0)
                {
                    EndDoorClear(s, route);
                    return true;
                }
                string direct = DirectClearSafety(b, r);
                string nade = PrepareClearGrenade(b, e!, s);
                r.Policy.Choose(
                    direct.Length == 0,
                    nade.Length == 0,
                    direct,
                    nade,
                    config!.GrenadeJudgment.Enabled ? 0 : jumpRandom.NextDouble(),
                    jumpRandom.NextDouble(),
                    Time.time,
                    config.DoorRoomClear,
                    config.GrenadeJudgment.Enabled
                );
            }
            else if (t.State == "throw-ready")
            {
                string why = PrepareClearGrenade(b, e!, s);
                if (why.Length > 0)
                {
                    t.Variant = "direct";
                    t.State = "entry-ready";
                    t.Reason = "clear-grenade-unavailable-dash-anyway";
                    return false;
                }
                var nade = b.BotOwner.WeaponManager.Grenades.grenade;
                r.Policy.ThrowRequest(
                    nade.Id.ToString(),
                    nade.GetExplDelay,
                    ClearDangerRadius(nade),
                    Time.time
                );
                if (r.Policy.Active)
                {
                    r.NativeThrowCall = true;
                    r.ThrowGuardPassed = false;
                    r.ThrowRefusal = "";
                    try
                    {
                        bool accepted = NativeAdapter.Throw(b, e!, out var reason);
                        r.Policy.ThrowResult(accepted && r.ThrowGuardPassed, Time.time);
                        if (!accepted)
                            t.Alternatives = t
                                .Alternatives.Concat(
                                    new[]
                                    {
                                        "Native throw refused: " + reason + " " + r.ThrowRefusal,
                                    }
                                )
                                .ToArray();
                    }
                    finally
                    {
                        r.NativeThrowCall = false;
                    }
                }
            }
            else if (t.State == "waiting-blast")
            {
                // A projectile necessarily starts at the thrower's hands. Allow only its initial departure,
                // then hand a dangerous own projectile to native avoidance; safe throws need no tracker.
                if (
                    Time.time - r.ProjectileAt >= .35f
                    && r.Projectile != null
                    && r.Projectile.TryGetTarget(out var nade)
                    && nade != null
                    && !ClearBlastProtected(b.Position, nade.transform.position, t.DangerRadius)
                )
                {
                    SweepClearGrenades();
                    if (
                        config?.OwnGrenadeSafety.Enabled != true
                        && !b.Grenade.GrenadeReactionClass.EnemyGrenadesList.ContainsKey(nade)
                    )
                        clearOwnGrenades.Add(new ClearOwnGrenade(b, nade, nade.transform.position));
                    EndDoorClear(s, "clear-own-grenade-threat-native-escape");
                    return true;
                }
            }
            else if (t.State == "entry-ready")
            {
                string why = ValidateClearRoute(b, r, true);
                if (why.Length == 0)
                    why = DirectClearSafety(b, r);
                if (why.Length > 0)
                {
                    EndDoorClear(s, "clear-entry-recheck:" + why);
                    return true;
                }
                r.Policy.MoveRequest(DP(b.Position), Time.time);
                b.Mover.SetTargetPose(1f);
                b.Mover.SetTargetMoveSpeed(1f);
                bool accepted = OrderDoorPoint(b, r.Destination, .5f, true, ref r.OwnedPath);
                r.Policy.MoveResult(accepted);
                if (accepted && !FollowingClearRoute(b, r) && Vector3.Distance(b.Position, r.Destination) > .5f)
                {
                    EndDoorClear(s, "clear-native-path-not-observed");
                    return true;
                }
            }
            if (!r.Policy.Active)
            {
                FinishDoorClear(s);
                return true;
            }
            b.Steering.LookToPoint(VP(new(t.KnownX, t.KnownY, t.KnownZ)) + Vector3.up * 1.2f);
            RecordDoorClear(s);
            return false;
        }
        finally
        {
            doorClock.Stop();
            doorBudget.Charge(doorClock.Elapsed.TotalMilliseconds);
        }
    }

    private void PlanClearCandidate(BotComponent b, BotState s, int index)
    {
        var r = s.RoomClear;
        if (r.Route != null)
            return;
        double depth =
            index < 2 ? 2.5
            : index < 4 ? 2
            : 1.5;
        double side = Math.Sign((DP(r.Stack) - r.Door!.Geometry.Center).Dot(r.Door.Geometry.Axis));
        var raw = DoorClearGeometry.Destination(
            r.Door.Geometry,
            DP(r.Stack),
            depth,
            index % 2 == 0 ? side * .6 : 0
        );
        if (!NavMesh.SamplePosition(VP(raw), out var hit, 1, NavMesh.AllAreas))
            return;
        if (DoorClearGeometry.Depth(r.Door.Geometry, DP(r.Stack), DP(hit.position)) < .5)
            return;
        if (!b.Mover.CanGoToPoint(hit.position, out var path, true) || Length(path.corners) >= 9)
            return;
        r.Destination = hit.position;
        r.Route = path;
        r.Policy.Route(DP(hit.position), false);
    }

    private string ValidateClearRoute(BotComponent b, DoorClearRuntime r, bool refresh)
    {
        if (r.Door?.Data.Door == null)
            return "clear-door-unavailable";
        if (refresh)
        {
            if (
                !b.Mover.CanGoToPoint(r.Destination, out var path, true)
                || Length(path.corners) >= 9
            )
                return "clear-no-current-route";
            r.Route = path;
        }
        return r.Route == null ? "clear-no-current-route" : "";
    }

    private string DirectClearSafety(BotComponent b, DoorClearRuntime r) =>
        b.Grenade.GrenadeDangerPoint != null ? "clear-native-grenade-danger" : "";

    private bool ClearCover(BotComponent b, DoorClearRuntime r)
    {
        int count = 0;
        bool primary = false;
        foreach (var e in b.EnemyController.KnownEnemies)
        {
            if (++count > 8)
                return false;
            if (
                e == null
                || e.TimeSinceLastKnownUpdated > 30
                || e.KnownPlaces.LastKnownPosition is not Vector3 known
            )
                continue;
            if (e.EnemyProfileId == r.Policy.Trace!.Target)
                primary = true;
            if (e.IsVisible || !ClearBlastProtected(b.Position, known, 30))
                return false;
        }
        return primary;
    }

    private static bool ClearBlastProtected(Vector3 point, Vector3 danger, double radius) =>
        Vector3.Distance(point, danger) >= radius
        || Physics.Linecast(
            danger + Vector3.up * .3f,
            point + Vector3.up * 1.2f,
            LayersMaskController.HighPolyWithTerrainMask,
            QueryTriggerInteraction.Ignore
        );

    private static double ClearDangerRadius(ThrowWeap item) =>
        Math.Max(
            6,
            Math.Max(item.MaxExplosionDistance, Math.Max(item.Blindness.y, item.Contusion.y))
        ) + 1;

    private string PrepareClearGrenade(BotComponent b, Enemy e, BotState s)
    {
        if (config!.DoorRoomClear.GrenadePreference <= 0)
            return "clear-real-grenade-disabled";
        string why = NativeAdapter.PrepareRoomGrenade(b, e, s, config!);
        if (why.Length > 0)
            return why;
        var manager = b.BotOwner.WeaponManager.Grenades;
        var item = manager.grenade;
        if (
            item == null
            || item.ThrowType.ToString().IndexOf("smoke", StringComparison.OrdinalIgnoreCase) >= 0
            || item.IsDummy
        )
            return "clear-no-suitable-grenade";
        return ClearThrowSafety(b, s, manager.AIGreanageThrowData, item);
    }

    private string ClearThrowSafety(
        BotComponent b,
        BotState s,
        AIGreanageThrowData data,
        ThrowWeap item
    )
    {
        if (data == null || !data.CanThrow || !data.IsUpToDate())
            return "clear-throw-arc-invalid";
        if (item == null || item.GetExplDelay < 2.5)
            return "clear-fuse-too-short";
        if (!ClearBlastProtected(b.Position, data.Target, 6))
            return "clear-own-blast-exposure";
        return ""; // Native throw validation checks the arc and friendly positions.
    }

    private static bool ClearThrowGuard(GrenadeThrowDecider __instance, ref bool __result)
    {
        var p = Instance;
        var b = __instance.Bot;
        if (p == null || !p.states.TryGetValue(b, out var s) || !s.RoomClear.NativeThrowCall)
            return true;
        var r = s.RoomClear;
        try
        {
            string why = p.ClearGate(b, s);
            if (why.Length == 0)
                why = p.ClearThrowSafety(
                    b,
                    s,
                    b.BotOwner.WeaponManager.Grenades.AIGreanageThrowData,
                    b.BotOwner.WeaponManager.Grenades.grenade
                );
            if (
                why.Length == 0
                && b.BotOwner.WeaponManager.Grenades.grenade.Id.ToString()
                    != r.Policy.Trace!.GrenadeItem
            )
                why = "clear-selected-grenade-changed";
            if (why.Length == 0)
            {
                r.ThrowGuardPassed = true;
                return true;
            }
            r.ThrowRefusal = why;
        }
        catch (Exception)
        {
            r.ThrowRefusal = "clear-throw-validation-failed";
        }
        __result = false;
        return false;
    }

    private static bool FollowingClearRoute(BotComponent bot, DoorClearRuntime r)
    {
        // SAIN can reuse a pre-existing route to this point. Follow it without claiming it for cleanup.
        var leg = QueuedDoorPath(bot) ?? bot.Mover.ActivePath;
        return leg != null && Vector3.Distance(leg.Destination, r.Destination) < .3f;
    }

    private static bool OwnsClearPath(DoorClearRuntime r) =>
        r.Owner != null
        && r.OwnedPath != null
        && CurrentDoorPath(r.Owner, r.OwnedPath)
        && Vector3.Distance(r.OwnedPath.Destination, r.Destination) < .3f;

    private void EndDoorClear(BotState s, string reason)
    {
        var r = s.RoomClear;
        if (!r.Policy.Active)
            return;
        r.Policy.End(reason);
        if (s.Fake.Policy.Active)
            EndDoorFake(s, reason);
        FinishDoorClear(s);
    }

    private void FinishDoorClear(BotState s)
    {
        var r = s.RoomClear;
        if (OwnsClearPath(r))
        {
            r.OwnedPath!.Cancel();
            if (r.Owner!.CurrentAction?.GetType().Name == "SearchAction")
                r.Owner.Search.Reset();
        }
        RecordDoorClear(s, true);
        r.Drop();
    }

    private void RecordDoorClear(BotState s, bool force = false)
    {
        var r = s.RoomClear;
        var t = r.Policy.Trace;
        if (t == null)
            return;
        string key =
            t.State
            + ":"
            + t.Reason
            + ":"
            + t.GrenadeOutcome
            + ":"
            + t.ThresholdCrossed
            + ":"
            + t.MovementObserved
            + ":"
            + t.ThrowAccepted;
        if (!force && key == r.LogKey && (!r.Policy.Active || Time.time - r.LogAt < .5f))
            return;
        r.LogKey = key;
        r.LogAt = Time.time;
        var json = Diagnostics.Serialize(
            new
            {
                schema = 1,
                session,
                sequence = ++sequence,
                feature = t.Mode == "search" ? "D08" : "D07",
                stage = "evaluated",
                decision = t.Mode == "search" ? "DoorSearchClear" : "DoorRoomClear",
                reason = t.Reason,
                realtime = Time.realtimeSinceStartup,
                utc = DateTime.UtcNow.ToString("O"),
                configRevision = config?.Revision ?? 0,
                profileId = s.ProfileId,
                name = s.Name,
                botInstance = s.Id,
                doorRoomClear = t,
                doorRoomClearConfig = config?.DoorRoomClear,
                doorSearchClearConfig = t.Mode == "search" ? config?.DoorSearchClear : null,
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
