using System;
using System.Linq;
using Comet.Core;
using DrakiaXYZ.BigBrain.Brains;
using EFT;
using HarmonyLib;
using Newtonsoft.Json;
using SAIN;
using SAIN.Components;
using SAIN.Models.Enums;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.Decision;
using SAIN.SAINComponent.Classes.Mover;
using UnityEngine;
using UnityEngine.AI;

namespace Comet.Client;

internal enum PostCombatCoverRange { None, Near, Far }

internal sealed class PostCombatRuntime
{
    internal readonly PostCombat Policy = new();
    internal float Next,
        LogAt,
        ContactAt,
        MedicalNext,
        MoveAt,
        NextMoveOrder,
        ProgressAt,
        BestDistance;
    internal Vector3? Threat;
    internal PostCombatCoverRange CoverRange;
    internal bool InspectedThreat;
    internal string Contact = "",
        Key = "",
        RouteStage = "";
    internal IBotPathData? Path;
    internal Vector3 Goal,
        ProtectedAt;
    internal int CoverCandidate,
        MedicalFailures;
    internal bool ProneInOpen,
        ProneRequested,
        Heard,
        Coming;
    internal Vector3 HeardAt;
    internal float NextListen;
    internal bool Protected,
        Layer,
        Fault,
        MedicalOwner,
        MedicalUseful,
        MedicalUrgent,
        MedicalDisabled;
}

public sealed partial class Plugin
{
    partial void ObserveControlRelease(BotState s, string reason);

    private static void PatchPostCombat(Harmony h)
    {
        h.Patch(
            AccessTools.Method(typeof(BotDecisionManager), nameof(BotDecisionManager.ManualUpdate)),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(ObservePostCombat))
        );
        h.Patch(
            AccessTools.Method(typeof(SAIN.Components.BotManagerComponent), "Activate"),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(PostSoundAttach))
        );
        h.Patch(
            AccessTools.Method(typeof(SAIN.Components.BotManagerComponent), "Dispose"),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(PostSoundDetach))
        );
        h.Patch(
            AccessTools.Method(
                typeof(SAIN.Components.PlayerComponentSpace.PlayerComponent),
                "OnMakingShot"
            ),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(PostSoundShot))
        );
        BrainManager.AddCustomLayer(
            typeof(PostCombatLayer),
            new()
            {
                "PMC",
                "PmcUsec",
                "PmcBear",
                "Assault",
                "ExUsec",
                "Knight",
                "BigPipe",
                "BirdEye",
            },
            21
        );
    }

    private static void PostSoundAttach(SAIN.Components.BotManagerComponent __instance) =>
        SourceSoundWatch.Attach(__instance.BotHearing);

    private static void PostSoundDetach(SAIN.Components.BotManagerComponent __instance) =>
        SourceSoundWatch.Detach(__instance.BotHearing);

    private static void PostSoundShot(
        SAIN.Components.PlayerComponentSpace.PlayerComponent __instance
    ) => SourceSoundWatch.OnShot(__instance, __instance.Player.Position);

    private void ListenPostCombat(BotComponent b, PostCombatRuntime r)
    {
        if (Time.time < r.NextListen)
            return;
        r.NextListen = Time.time + .4f;
        r.Heard = SourceSoundWatch.Heard(b, out r.HeardAt, out r.Coming, out var why);
        if (
            r.Heard
            && r.Coming
            && b.BotOwner.Medecine.Using
            && Vector3.Distance(r.HeardAt, b.Position) < 15
        )
            b.Medical.TryCancelHeal();
    }

    private bool PostScope(BotComponent b) =>
        config?.Enabled == true
        && config.PostCombat.Enabled
        && world != null
        && Authority
        && b != null
        && NativeAdapter.IsSupportedAi(b)
        && b.BotActive
        && !b.IsDead
        && !b.GameEnding
        && !Ghost(b.ProfileId);

    private bool PostCombatMedicalScope(BotComponent b) =>
        states.TryGetValue(b, out var s)
        && (
            s.PostCombat.Layer && s.PostCombat.Policy.WantsRecovery(Time.time)
            || s.PostCombat.MedicalOwner
                && (s.Medical.Policy.Active || s.Medical.Interruption.Active)
        );

    private bool PostSurgeryTooSoon(BotComponent b, BotState s) => false;

    private bool PostReachedCover(BotComponent b, BotState s) =>
        s.PostCombat.Layer
        && s.PostCombat.Policy.WantsRecovery(Time.time)
        && s.PostCombat.Protected
        && Vector3.Distance(b.Position, s.PostCombat.ProtectedAt) < 1.5f;

    private static void ObservePostCombat(BotDecisionManager __instance)
    {
        var p = Instance;
        var b = __instance.Bot;
        if (p == null)
            return;
        try
        {
            if (!p.PostScope(b))
            {
                if (p.states.TryGetValue(b, out var old))
                    p.EndPostCombat(b, old, "post-combat-out-of-scope", true);
                return;
            }
            var layer = b.ActiveLayer;
            bool combatLayer = layer is ESAINLayer.Combat or ESAINLayer.AvoidThreat or ESAINLayer.Flashed;
            if (!p.states.TryGetValue(b, out var s))
            {
                if (!combatLayer || p.states.Count >= 512)
                    return;
                s = new BotState(b, p.config!, p.session);
                p.states.Add(b, s);
            }
            if (s.PostCombat.Fault)
                return;
            var e = b.GoalEnemy;
            if (s.PostCombat.Policy.Active && (combatLayer || b.BotOwner.Memory.IsUnderFire
                || b.BotOwner.ShootData.Shooting || e?.IsVisible == true))
                p.EndPostCombat(b, s, "post-combat-fight-resumed", false);
            if (!combatLayer)
            {
                // Squad keeps the previous timestamps while SAIN hands the bot to recovery.
                if (layer != ESAINLayer.Squad)
                    s.PostCombat.Policy.ResetCombatWindow();
                return;
            }
            s.PostCombat.Policy.ObserveFight(Time.time);
            var place = e?.KnownPlaces.LastSeenPlace;
            var heard = e?.KnownPlaces.LastHeardPlace;
            if (heard != null && (place == null || heard.TimeSincePositionUpdated < place.TimeSincePositionUpdated))
                place = heard;
            if (place != null && ValidPoint(place.Position) && place.TimeSincePositionUpdated >= 0)
            {
                s.PostCombat.Threat = place.Position;
                s.PostCombat.Contact = e!.EnemyProfileId;
                s.PostCombat.ContactAt = Time.time - place.TimeSincePositionUpdated;
            }
        }
        catch (Exception ex)
        {
            if (p.states.TryGetValue(b, out var s))
                p.PostError(b, s, ex);
        }
    }

    private bool PostDanger(BotComponent b)
    {
        if (
            b.BotOwner.Memory.IsUnderFire
            || b.BotOwner.ShootData.Shooting
            || b.Grenade.GrenadeDangerPoint != null
            || b.Flashed.IsFlashed
        )
            return true;
        if (b.GoalEnemy?.IsVisible == true)
            return true;
        foreach (var e in b.EnemyController.KnownEnemies)
        {
            if (e?.IsVisible == true)
                return true;
        }
        return false;
    }

    internal static bool PostCombatActive(BotOwner owner)
    {
        var p = Instance;
        if (
            p == null
            || !SAINEnableClass.GetSAIN(owner.ProfileId, out var b)
            || b == null
            || !p.states.TryGetValue(b, out var s)
        )
            return false;
        try
        {
            return p.RefreshPostCombat(b, s);
        }
        catch (Exception ex)
        {
            p.PostError(b, s, ex);
            return false;
        }
    }

    private bool PostCombatWindowOpen(BotComponent b, BotState s)
    {
        var policy = s.PostCombat.Policy;
        if (policy.WantsRecovery(Time.time))
            return true;
        if (policy.Active)
            EndPostCombat(b, s, PostCombat.WindowExpired, false);
        return false;
    }

    private bool RefreshPostCombat(BotComponent b, BotState s)
    {
        var r = s.PostCombat;
        if (r.Fault || !PostScope(b))
        {
            EndPostCombat(b, s, "post-combat-out-of-scope", true);
            return false;
        }
        if (!PostCombatWindowOpen(b, s))
            return false;
        bool danger = PostDanger(b);
        bool priority =
            b.Decision.CurrentCombatDecision != ECombatDecision.None
            || b.Decision.CurrentSquadDecision != ESquadDecision.None
            || b.Decision.CurrentSelfDecision != ESelfActionType.None
            || b.BotOwner.DoorOpener.Interacting;
        if (danger || priority)
        {
            if (r.Policy.Active)
                EndPostCombat(
                    b,
                    s,
                    danger ? "post-combat-threat-returned" : "post-combat-native-priority",
                    false
                );
            return false;
        }
        bool betweenSamples = Time.time < r.Next;
        string? previousStage = r.Policy.Trace?.State;
        if (betweenSamples && previousStage is not ("watch" or "regroup"))
            return r.Policy.Active;
        bool leaderOutOfRange = PostRegroupTarget(b, out var leaderDistance).HasValue;
        if (betweenSamples && PostCombat.CanRegroup(r.Heard, leaderOutOfRange) == (previousStage == "regroup"))
            return r.Policy.Active;
        r.Next = Time.time + (float)config!.PostCombat.Interval;
        double ordinaryDeadline = r.Policy.OrdinaryDeadline(config.PostCombat);
        ListenPostCombat(b, r);
        InspectPostMedical(b, s);
        if (Time.time < ordinaryDeadline)
        {
            // Sample again at the boundary so a new sound or treatment can keep the layer.
            r.Next = Math.Min(r.Next, (float)ordinaryDeadline);
            r.NextListen = Math.Min(r.NextListen, (float)ordinaryDeadline);
        }
        var med = b.BotOwner.Medecine;
        var f = new PostCombatFrame
        {
            Now = Time.time,
            MedicalBusy = PostTreatmentInUse(b),
            NativeMedicalWork = med?.SurgicalKit?.HaveWork == true || med?.FirstAid?.Have2Do == true,
            MedicalUseful = r.MedicalUseful,
            MedicalUrgent = r.MedicalUrgent,
            Reloading = b.BotOwner.WeaponManager.Reload.Reloading,
            Heard = r.Heard,
            Coming = r.Heard && r.Coming,
            Regroup = leaderOutOfRange,
        };
        bool wasActive = r.Policy.Active;
        ETagStatus initialHealth = default;
        if (!wasActive)
        {
            if (r.Policy.QuietWindowExpired(f, config.PostCombat))
                return false;
            initialHealth = b.Memory.Health.HealthStatus;
            f.BadlyInjuredOrDying = initialHealth is ETagStatus.BadlyInjured or ETagStatus.Dying;
            r.NextMoveOrder = 0;
            // A previous episode's reached cover or failed supply scan is not evidence for this fight.
            // Reset before building the frame: otherwise Tick can mark cover complete using stale protection.
            r.ProneInOpen = false;
            r.CoverRange = PostCombatCoverRange.None;
            r.InspectedThreat = false;
            r.Protected = NativeCoverProtects(b) || !SourceOpenGround.IsOpen(b.Position, r.Threat);
            r.ProtectedAt = b.Position;
            r.CoverCandidate = 0;
            r.MedicalFailures = 0;
            r.MedicalDisabled = false;
            r.MedicalNext = 0;
            if (r.Threat is Vector3 contact)
            {
                float distance = Vector3.Distance(b.Position, contact);
                f.CanAdvance = distance > config.PostCombat.AdvanceMin && distance < config.PostCombat.AdvanceMax;
            }
        }
        f.AmmoPercent = TacticalAmmoPercent(b.BotOwner);
        f.Personality = b.Info.Personality.ToString();
        f.NeedsCover = r.Threat.HasValue && !r.Protected;
        bool active = r.Policy.Tick(
            f,
            config.PostCombat,
            jumpRandom.NextDouble(),
            jumpRandom.NextDouble()
        );
        if (!active)
        {
            if (wasActive)
            {
                ObserveControlRelease(s, "post-combat-finished");
                PostStopRoute(b, r);
            }
            r.Layer = false;
        }
        var t = r.Policy.Trace;
        if (t != null)
        {
            if (active && previousStage != t.State && (previousStage == "regroup" || t.State == "regroup"))
                r.MoveAt = Time.time;
            t.LeaderDistance = leaderDistance;
            if (!wasActive && active)
                t.AdvanceHealthStatus = initialHealth.ToString();
            t.Contact = r.Contact;
            t.ContactAge = Math.Max(0, Time.time - r.ContactAt);
            t.X = r.Threat?.x;
            t.Y = r.Threat?.y;
            t.Z = r.Threat?.z;
        }
        r.Policy.Applied(r.Layer);
        RecordPostCombat(s, false);
        return active;
    }

    internal static void RunPostCombat(BotOwner owner, bool active)
    {
        var p = Instance;
        if (
            p == null
            || !SAINEnableClass.GetSAIN(owner.ProfileId, out var b)
            || b == null
            || !p.states.TryGetValue(b, out var s)
        )
            return;
        try
        {
            var r = s.PostCombat;
            if (!active)
            {
                string reason = r.Policy.Active && !r.Policy.WantsRecovery(Time.time)
                    ? PostCombat.WindowExpired : "post-combat-layer-released";
                p.EndPostCombat(b, s, reason, false);
                return;
            }
            if (!p.RefreshPostCombat(b, s))
                return;
            if (!r.Layer)
            {
                r.Layer = true;
                r.Next = 0;
                owner.PatrollingData.Pause();
            }
            r.Policy.Applied(true);
            if (r.RouteStage == "cover" && r.Policy.Trace!.State == "cover"
                && !PostTreatmentInUse(b) && !AtPostCombatCover(b, r.Goal))
                p.RenewPostCoverWatch(r);
            bool movementDue = Time.time >= r.MoveAt
                || r.Policy.Trace!.State == "regroup" && Time.time > r.NextMoveOrder;
            if (movementDue)
                r.MoveAt = Time.time + (float)p.config!.PostCombat.Interval;
            if (movementDue || r.Policy.Trace!.State == "watch")
                p.ExecutePostCombat(b, s);
            if (movementDue)
                p.RecordPostCombat(s, false);
        }
        catch (Exception ex)
        {
            p.PostError(b, s, ex);
        }
    }

    internal static bool SteerPostCombat(BotOwner owner, PostCombatAction action)
    {
        var p = Instance;
        if (
            p == null
            || !SAINEnableClass.GetSAIN(owner.ProfileId, out var b)
            || b == null
            || !p.states.TryGetValue(b, out var s)
            || !s.PostCombat.Layer
            || !s.PostCombat.Policy.Active
        )
            return false;
        try
        {
            if (!p.PostScope(b) || !p.PostCombatWindowOpen(b, s))
                return false;
            var r = s.PostCombat;
            if (Time.time >= r.Next && Time.time >= r.Policy.OrdinaryDeadline(p.config!.PostCombat)
                && !p.RefreshPostCombat(b, s))
                return false;
            var trace = r.Policy.Trace!;
            if (TacticalSteeringFire.TryShoot(b, TacticalSteeringKind.PostCombat))
            {
                trace.LastLook = "engage";
                return true;
            }
            if (p.PostDanger(b))
                return false;
            bool moving = b.Mover.Moving;
            var threat = r.InspectedThreat ? null : r.Threat;
            if (moving && (trace.State == "regroup" || r.CoverRange == PostCombatCoverRange.Far && !r.Heard))
            {
                trace.LastLook = "travel";
                b.Steering.LookToMovingDirection();
            }
            else if (r.Heard || moving && r.Policy.WillAdvance && threat.HasValue)
            {
                trace.LastLook = r.Heard ? "sound" : "advance";
                var target = r.Heard ? r.HeardAt : threat!.Value;
                b.Steering.LookToPoint(SteeringGeometry.ClampPitch(
                    b.Transform.WeaponRoot, target + Vector3.up * 1.3f, b.Transform.LookDirection));
            }
            else if (moving && (r.CoverRange == PostCombatCoverRange.None || !threat.HasValue))
            {
                trace.LastLook = "travel";
                b.Steering.LookToMovingDirection();
            }
            else if (threat.HasValue)
            {
                var offset = action.GetGlanceOffset(b.Position, threat.Value);
                trace.LastLook = offset.sqrMagnitude > 0 ? "watch-side" : "watch-contact";
                b.Steering.LookToPoint(threat.Value + offset + Vector3.up * 1.3f);
            }
            else
            {
                trace.LastLook = "look-around";
                b.Steering.LookToRandomPosition();
            }
            return true;
        }
        catch (Exception ex)
        {
            p.PostError(b, s, ex);
            return false;
        }
    }

    private void EndPostCombat(BotComponent b, BotState s, string why, bool clear)
    {
        var r = s.PostCombat;
        bool active = r.Policy.Active || r.Layer;
        if (active)
            ObserveControlRelease(s, why);
        if (clear)
            r.Policy.Clear(why);
        else if (active)
            r.Policy.End(why, Time.time);
        r.Layer = false;
        if (active)
        {
            try
            {
                PostStopRoute(b, r);
            }
            finally
            {
                r.Path = null;
                RecordPostCombat(s, true);
            }
        }
        if (clear)
        {
            r.Threat = null;
            r.Contact = "";
            r.MedicalOwner = false;
            r.MedicalUseful = false;
        }
    }

    private void PostError(BotComponent b, BotState s, Exception ex)
    {
        if (s.PostCombat.Fault)
            return;
        s.PostCombat.Fault = true;
        EndPostCombat(b, s, "post-combat-adapter-error", true);
        Logger.LogWarning("H04 yielded: " + ex.GetBaseException().Message);
    }

    private void RecordPostCombat(BotState s, bool force)
    {
        var r = s.PostCombat;
        var t = r.Policy.Trace;
        if (t == null)
            return;
        Api.Decisions.SetPostCombat(
            s.ProfileId,
            t.Applied
                ? "COMET:PostCombat" + char.ToUpperInvariant(t.State[0]) + t.State.Substring(1)
                : ""
        );
        string key =
            t.Episode + ":" + t.State + ":" + t.Reason + ":" + t.Execution + ":" + t.Applied;
        if (!force && key == r.Key && (!t.Applied || Time.time < r.LogAt))
            return;
        r.Key = key;
        r.LogAt = Time.time + .5f;
        var json = Diagnostics.Serialize(
            new
            {
                schema = 1,
                session,
                sequence = ++sequence,
                feature = "H04",
                stage = "evaluated",
                decision = "PostCombat",
                reason = t.Reason,
                realtime = Time.realtimeSinceStartup,
                utc = DateTime.UtcNow.ToString("O"),
                configRevision = config?.Revision ?? 0,
                profileId = s.ProfileId,
                name = s.Name,
                botInstance = s.Id,
                postCombat = t,
                postCombatConfig = config?.PostCombat,
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
