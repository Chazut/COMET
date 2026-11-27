using System;
using Comet.Core;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using Newtonsoft.Json;
using SAIN.Components;
using SAIN.Preset.Shared.Enums;
using SAIN.Preset.Shared.GlobalSettings;
using SAIN.SAINComponent.Classes.EnemyClasses;
using SAIN.SAINComponent.Classes.WeaponFunction;
using UnityEngine;

namespace Comet.Client;

internal sealed class DoorFakeRuntime
{
    internal readonly DoorFakeGrenade Policy = new();
    internal DoorWork? Door;
    internal Vector3 Frame;
    internal int TargetInstance,
        Token;
    internal bool CallbackPending,
        CallbackSuccess;
    internal float NextSafety,
        LastSafety,
        LogAt = -1000,
        ErrorAt = -1;
    internal string LogKey = "";
    internal WeakReference<Meds>? MedicalItem;
    internal bool MedicalUseFinished,
        MedicalUseSucceeded,
        MedicalObserving;
    internal float MedicalLastEnd,
        MedicalUseAt,
        MedicalNextBefore;
    internal bool MedicalCooldownChecked;
    internal bool AmbushParent;
    internal bool RoomClearParent;

    internal void Dispose()
    {
        Token++;
        CallbackPending = false;
        MedicalItem = null;
        MedicalObserving = MedicalUseFinished = MedicalUseSucceeded = false;
        Door = null;
        Policy.Clear();
    }
}

public sealed partial class Plugin
{
    private static void PatchDoorFake(Harmony h)
    {
        PatchDoorFakeMedical(h);
        var type = typeof(GrenadeThrowDecider);
        h.Patch(
            AccessTools.Method(
                type,
                "GetDecision",
                new[] { typeof(Enemy), typeof(string).MakeByRefType() }
            ) ?? throw new MissingMethodException("D05 GetDecision"),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(FakeGrenadeDecisionGuard))
            {
                priority = Priority.First,
            }
        );
        h.Patch(
            AccessTools.Method(type, "TryThrowGrenade", Type.EmptyTypes)
                ?? throw new MissingMethodException("D05 TryThrowGrenade"),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(FakeGrenadeThrowGuard))
            {
                priority = Priority.First,
            }
        );
    }

    private static bool FakeGrenadeDecisionGuard(
        GrenadeThrowDecider __instance,
        ref string reason,
        ref bool __result
    )
    {
        if (!FakeGuarded(__instance.Bot))
            return true;
        reason = "comet-fake-grenade-hands-recovery";
        __result = false;
        return false;
    }

    private static bool FakeGrenadeThrowGuard(GrenadeThrowDecider __instance, ref bool __result)
    {
        if (!FakeGuarded(__instance.Bot))
            return true;
        __result = false;
        return false;
    }

    private static bool FakeGuarded(BotComponent b)
    {
        var p = Instance;
        if (
            p == null
            || !p.FakeScope(b)
            || !p.states.TryGetValue(b, out var s)
            || s.Fake.Policy.Trace is not DoorFakeTrace t
            || b.BotOwner.WeaponManager?.Grenades.ThrowindNow == true
        )
            return false;
        if (t.Kind != "grenade")
            return false;
        bool stillOwns =
            b.Player.HandsController is IGrenadeController
            && b.Player.HandsController.Item?.Id.ToString() == t.Grenade;
        // Failed cleanup is not permission to throw the feint grenade. Release as soon as other hands take over.
        return t.Guard || t.RecoveryFailed && stillOwns;
    }

    private bool FakeScope(BotComponent b) =>
        ContactSafety.AllowsGoal(b)
        && world != null
        && b != null
        && NativeAdapter.IsSupportedAi(b)
        && b.BotActive
        && !b.GameEnding
        && Authority
        && !Ghost(b.ProfileId);

    private static bool FakeWeaponReady(BotComponent b) =>
        b.Player.HandsController?.Item is Weapon
        && b.Player.HandsController is not IGrenadeController
        && VisibleAdapter.WeaponReady(b);

    private bool TryStartDoorFake(
        BotComponent b,
        Enemy e,
        BotState s,
        DoorWork door,
        bool ambush = false,
        string? planned = null,
        bool roomClear = false
    )
    {
        var r = s.Fake;
        if (
            config == null
            || (!config.DoorFakeGrenade.Enabled && !config.DoorFakeMedical.Enabled)
            || r.Policy.Active
            || r.Policy.Fault
            || s.Jump.Policy.Active
            || s.RunBy.Policy.Active
            || s.StepPeek.Policy.Active
            || (ambush ? AmbushGate(b, s) : DoorGate(b, e, s)).Length > 0
            || !FakeWeaponReady(b)
            || e.KnownPlaces.LastKnownPosition is not Vector3 known
            || b.BotOwner.WeaponManager?.Grenades.ThrowindNow == true
        )
            return false;
        var c = config.DoorFakeGrenade;
        if (
            !roomClear && (Vector3.Distance(b.Position, VP(door.Geometry.Center)) > c.DoorDistance
            || Vector3.Distance(known, VP(door.Geometry.Center)) > c.ContactDistance)
        )
            return false;
        bool grenade =
            c.Enabled
            && SainPresetAccess.AllowsGrenades()
            && b.Info.FileSettings.Core.CanGrenade
            && b.BotOwner.WeaponManager?.Grenades.HaveGrenade == true;
        string medical = roomClear ? "" : FindFakeMedical(b, out _, out _);
        double styleRoll = roomClear ? 0 : jumpRandom.NextDouble();
        string kind = planned ?? DoorFeintChoice.Pick(
            grenade,
            medical,
            styleRoll,
            config.DoorFakeMedical.Preference
        );
        if (kind.Length == 0 || kind == "grenade" && !grenade || kind != "grenade" && kind != medical)
            return false;
        var previous = r.Policy.Trace;
        bool selected = r.Policy.Begin(
            door.Geometry.Id,
            e.EnemyProfileId,
            s.Situation?.ContactSource ?? "sain-last-known",
            b.Info.Personality.ToString(),
            DP(known),
            e.TimeSinceLastKnownUpdated,
            Time.time,
            jumpRandom.NextDouble(),
            c,
            config.DoorFakeMedical.Enabled,
            planned != null,
            roomClear
        );
        if (ReferenceEquals(previous, r.Policy.Trace))
            return false;
        var trace = r.Policy.Trace!;
        trace.Kind = kind;
        trace.StyleRoll = styleRoll * 100;
        trace.MedicalPreference = config.DoorFakeMedical.Preference;
        trace.GrenadeEligible = grenade;
        trace.MedicalEligible = medical;
        trace.Alternatives = new[]
        {
            grenade ? "Grenade: available" : "Grenade: disabled or unavailable",
            medical.Length > 0
                ? "Medical: " + medical + " available"
                : "Medical: disabled, unsafe or unavailable",
            "Normal SAIN search",
            "Reconnaissance only if no feint selected",
        };
        trace.Medical = kind == "grenade" ? null : new DoorMedicalTrace { Kind = kind };
        r.Token++;
        r.CallbackPending = false;
        r.MedicalItem = null;
        r.MedicalObserving = r.MedicalUseFinished = r.MedicalUseSucceeded = false;
        if (!selected)
        {
            RecordDoorFake(s, true);
            return false;
        }
        r.AmbushParent = ambush;
        r.RoomClearParent = roomClear;
        if (roomClear)
            trace.Alternatives = new[] { "Grenade feint from the room entry plan", "Resume the entry plan if the grenade cannot be drawn" };
        else if (ambush)
            trace.Alternatives = new[]
            {
                "Hold the exit with weapon ready",
                "One eligible feint, subject to existing shared chance and cooldown",
                "Native urgent action takes priority",
            };
        r.Door = door;
        r.Frame = b.Position;
        r.TargetInstance = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(e);
        r.NextSafety = 0;
        r.LastSafety = Time.time;
        r.ErrorAt = -1;
        StopQuiet(b, s, "quiet-door-fake");
        EndSilentSearch(s, "silent-search-door-fake");
        EndAbandonment(s, "search-check-door-fake");
        RecordDoorFake(s, true);
        return true;
    }

    private string FakeGate(BotComponent b, BotState s)
    {
        var r = s.Fake;
        var t = r.Policy.Trace!;
        var e = b.GoalEnemy;
        var c = config?.DoorFakeGrenade;
        if (
            config?.Enabled != true
            || config.DoorApproach.Enabled != true
            || c == null
            || (
                t.Kind == "grenade"
                    ? !c.Enabled
                    : !config.DoorFakeMedical.Enabled
                        || (t.Kind == "stim" && !config.DoorFakeMedical.Stimulators)
            )
            || !FakeScope(b)
        )
            return "fake-out-of-scope";
        if (r.AmbushParent)
        {
            if (!s.Ambush.Policy.Active)
                return "fake-ambush-ended";
            string gate = AmbushGate(b, s);
            if (gate.Length > 0)
                return gate;
        }
        else if (!TacticalDoorSearch(b, s))
            return "fake-search-not-owned";
        if (
            e == null
            || !e.EnemyKnown
            || e.EnemyProfileId != t.Target
            || e.KnownPlaces.LastKnownPosition is not Vector3 known
        )
            return "fake-contact-changed-or-expired";
        t.ContactAge = e.TimeSinceLastKnownUpdated;
        if (e.IsVisible || b.EnemyController.VisibleEnemies.Count > 0)
        {
            t.Reaction = "visual-contact-observed-not-proof-of-bait";
            return "fake-visible-threat";
        }
        if (b.BotOwner.Memory.IsUnderFire)
            return "fake-under-fire";
        if (r.Door?.Data.Door == null)
            return "fake-door-unavailable";
        int count = 0;
        foreach (var other in b.EnemyController.KnownEnemies)
        {
            var place = other?.KnownPlaces.LastHeardPlace;
            if (place == null)
                continue;
            double age = Time.time - silentHeardAt(place);
            if (
                age >= 0
                && age <= 1.2
                && place.SoundType
                    is SAINSoundType.FootStep
                        or SAINSoundType.Sprint
                        or SAINSoundType.Jump
                        or SAINSoundType.Land
                        or SAINSoundType.Door
                        or SAINSoundType.DoorBreach
                && Vector3.Distance(place.Position, VP(r.Door.Geometry.Center)) <= 4
            )
            {
                t.Reaction = "nearby-movement-heard-not-proof-of-bait";
                return "fake-heard-approach";
            }
        }
        return "";
    }

    private bool FakeCover(BotComponent b, BotState s)
    {
        var chest = b.Position + Vector3.up * 1.3f;
        foreach (var e in b.EnemyController.KnownEnemies)
        {
            if (e == null)
                continue;
            if (e.IsVisible)
                return false;
            if (
                e.TimeSinceLastKnownUpdated > 30
                || e.KnownPlaces.LastKnownPosition is not Vector3 pos
            )
                continue;
            if (
                !Physics.Linecast(
                    pos + Vector3.up * 1.5f,
                    chest,
                    LayersMaskController.HighPolyWithTerrainMask,
                    QueryTriggerInteraction.Ignore
                )
            )
                return false;
        }
        if (b.Memory.Location.IsIndoors || b.Player.Environment == EnvironmentType.Indoor)
            return true;
        int blocked = 0;
        for (int k = 0; k < 8; k++)
            if (
                Physics.Raycast(
                    chest,
                    Quaternion.Euler(0, k * 45, 0) * Vector3.forward,
                    4,
                    LayersMaskController.HighPolyWithTerrainMask,
                    QueryTriggerInteraction.Ignore
                )
            )
                blocked++;
        return blocked >= 5;
    }

    private bool KeepDoorFake(
        BotComponent b,
        BotState s,
        ref ECombatDecision decision,
        ref bool result
    )
    {
        if (!s.Fake.Policy.Active || s.Fake.AmbushParent)
            return false;
        RefreshDoorFake(b, s);
        if (!s.Fake.Policy.Active || s.Fake.Policy.Trace!.Interrupted.Length > 0)
            return false;
        s.Expected = ECombatDecision.Search;
        s.RequestedFrame = Time.frameCount;
        decision = ECombatDecision.Search;
        result = true;
        return true;
    }

    private bool StepDoorFake(BotComponent b, BotState s)
    {
        RefreshDoorFake(b, s);
        return !s.Fake.Policy.Active || s.Fake.Policy.Trace!.Interrupted.Length > 0;
    }

    private void RefreshDoorFake(BotComponent b, BotState s)
    {
        try
        {
            TickDoorFake(b, s);
        }
        catch (Exception ex)
        {
            var r = s.Fake;
            if (r.ErrorAt < 0)
            {
                r.ErrorAt = Time.time;
                Logger.LogWarning("COMET doorway feint hands recovery: " + ex.Message);
            }
            if (r.Policy.Trace == null)
                return;
            r.Policy.Abort("fake-adapter-error", Time.time);
            if (Time.time - r.ErrorAt >= DoorFakeGrenade.RestoreWatchdog)
                r.Policy.Fail("fake-recovery-adapter-error");
            RecordDoorFake(s);
        }
    }

    private void TickDoorFake(BotComponent b, BotState s)
    {
        var r = s.Fake;
        var t = r.Policy.Trace;
        if (t == null)
            return;
        if (
            b == null
            || b.IsDead
            || b.Player.HealthController?.IsAlive != true
            || b.GameEnding
            || world == null
        )
        {
            DisposeDoorFake(s, "fake-bot-or-raid-ended");
            return;
        }
        ObserveFakeMedical(r);
        if (FakeScope(b))
            RestoreFakeMedicalCompletionCooldown(b, r);
        if (r.CallbackPending)
        {
            r.CallbackPending = false;
            r.Policy.Callback(r.CallbackSuccess, Time.time);
            RecordDoorFake(s, true);
        }
        if (!r.Policy.Active)
        {
            if (FakeWeaponReady(b))
                r.MedicalObserving = false;
            RefreshFailedFakeGuard(b, s);
            RecordDoorFake(s);
            return;
        }
        if (!FakeScope(b))
        {
            r.Policy.Suspend(Time.time);
            RecordDoorFake(s);
            return;
        }
        r.Policy.Resume(Time.time);
        if (t.Medical != null && FakeMedicalPriority(b))
        {
            r.Policy.YieldToMedical();
            r.MedicalObserving = false;
            r.Door = null;
            RecordDoorFake(s, true);
            return;
        }
        if (t.Interrupted.Length == 0)
        {
            string gate = FakeGate(b, s);
            if (gate.Length > 0)
                r.Policy.Abort(gate, Time.time);
            else if (Time.time >= r.NextSafety)
            {
                if (doorBudget.TryBegin(Time.frameCount, b))
                {
                    doorClock.Restart();
                    bool covered;
                    try
                    {
                        covered = r.RoomClearParent || FakeCover(b, s);
                    }
                    finally
                    {
                        doorClock.Stop();
                        doorBudget.Charge(doorClock.Elapsed.TotalMilliseconds);
                    }
                    r.NextSafety = Time.time + .25f;
                    r.LastSafety = Time.time;
                    if (!covered)
                        r.Policy.Abort("fake-cover-lost-or-unverified", Time.time);
                    else if (t.State == "validating")
                    {
                        if (t.Medical != null)
                            DrawDoorFakeMedical(b, s);
                        else
                            DrawDoorFake(b, s);
                    }
                }
            }
        }
        if (!r.Policy.Active)
        {
            r.Door = null;
            RecordDoorFake(s);
            return;
        }
        var manager = b.BotOwner.WeaponManager;
        var hands = b.Player.HandsController;
        bool owns =
            t.Medical != null
                ? hands is IMedsController && hands.Item?.Id.ToString() == t.Medical.Item
                : hands is IGrenadeController && hands.Item?.Id.ToString() == t.Grenade;
        bool other = hands?.Item != null && !owns && hands.Item is not Weapon;
        bool throwing = manager?.Grenades.ThrowindNow == true;
        r.Policy.Observe(owns, FakeWeaponReady(b), other, throwing, Time.time);
        if (t.WeaponReady || other)
            r.MedicalObserving = false;
        if (r.Policy.CanRestore(owns, manager?.Selector.IsChanging != false, throwing, Time.time))
        {
            bool accepted =
                t.Medical != null
                    ? CancelDoorFakeMedical(b, s)
                    : manager!.Selector.TakePrevWeapon();
            r.Policy.RestoreRequested(accepted, Time.time);
        }
        if (t.Medical != null)
            t.Guard = false;
        if (!r.Policy.Active)
        {
            r.Door = null;
            if (!t.RecoveryFailed && t.Medical == null)
                r.Token++;
            else
                RefreshFailedFakeGuard(b, s);
        }
        RecordDoorFake(s);
    }

    private void RefreshFailedFakeGuard(BotComponent b, BotState s)
    {
        var t = s.Fake.Policy.Trace;
        if (t?.RecoveryFailed != true || t.Kind != "grenade")
            return;
        bool guard =
            FakeScope(b)
            && b.BotOwner.WeaponManager?.Grenades.ThrowindNow != true
            && b.Player.HandsController is IGrenadeController
            && b.Player.HandsController.Item?.Id.ToString() == t.Grenade;
        if (t.Guard != guard)
        {
            t.Guard = guard;
            RecordDoorFake(s, true);
        }
    }

    private void DrawDoorFake(BotComponent b, BotState s)
    {
        var r = s.Fake;
        var manager = b.BotOwner.WeaponManager;
        var grenades = manager?.Grenades;
        if (
            grenades == null
            || !grenades.HaveGrenade
            || grenades.ThrowindNow
            || !FakeWeaponReady(b)
        )
        {
            r.Policy.Abort("fake-item-or-hands-unavailable", Time.time);
            return;
        }
        grenades.CheckGrenade();
        var item = grenades.grenade;
        if (item == null)
        {
            r.Policy.Abort("fake-no-accessible-grenade", Time.time);
            return;
        }
        r.Policy.Draw(item.Id.ToString(), b.Player.HandsController.Item.Id.ToString(), Time.time);
        b.Mover.Stop();
        // Callback holds neither player nor controller and never changes hands itself.
        var weak = new WeakReference<DoorFakeRuntime>(r);
        int token = r.Token;
        Callback<IGrenadeController> callback = value =>
        {
            if (weak.TryGetTarget(out var runtime) && runtime.Token == token)
            {
                runtime.CallbackSuccess = value.Succeed && value.Value != null;
                runtime.CallbackPending = true;
            }
        };
        try
        {
            b.Player.SetInHands(item, callback);
        }
        catch
        {
            r.Policy.Abort("fake-draw-call-failed", Time.time);
            throw;
        }
    }

    private void EndDoorFake(BotState s, string reason)
    {
        var r = s.Fake;
        if (reason.Contains("raid-ended") || reason.Contains("bot-ended"))
        {
            DisposeDoorFake(s, reason);
            return;
        }
        r.Policy.Abort(reason, Time.time);
        r.Door = null;
        RecordDoorFake(s);
    }

    private void DisposeDoorFake(BotState s, string reason)
    {
        try
        {
            s.Fake.Policy.End(reason);
            RecordDoorFake(s);
        }
        finally
        {
            s.Fake.Dispose();
        }
    }

    private void RecordDoorFake(BotState s, bool force = false)
    {
        var r = s.Fake;
        var t = r.Policy.Trace;
        if (t == null)
            return;
        if (RecordingGestures && s.Gestures.Observe(t))
            PublishGesture(s, t.Medical == null ? "D05" : "D06", t.Medical == null ? "FakeGrenade" : "FakeHeal");
        if (!Comet.Core.ReplayCapture.Detailed)
            return;
        string key =
            t.State
            + ":"
            + t.Reason
            + ":"
            + t.HolsterAttempts
            + ":"
            + t.CallbackCompleted
            + ":"
            + t.Medical?.ConsumptionObserved
            + ":"
            + t.Medical?.UseCallbackCompleted
            + ":"
            + t.Medical?.CooldownRestored;
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
                feature = t.Medical == null ? "D05" : "D06",
                stage = "evaluated",
                decision = t.Medical == null ? "DoorFakeGrenade" : "DoorFakeMedical",
                reason = t.Reason,
                realtime = Time.realtimeSinceStartup,
                utc = DateTime.UtcNow.ToString("O"),
                configRevision = config?.Revision ?? 0,
                profileId = s.ProfileId,
                name = s.Name,
                botInstance = s.Id,
                doorFakeGrenade = t.Medical == null ? t : null,
                doorFakeMedical = t.Medical != null ? t : null,
                doorFakeGrenadeConfig = config?.DoorFakeGrenade,
                doorFakeMedicalConfig = t.Medical != null ? config?.DoorFakeMedical : null,
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
