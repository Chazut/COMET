using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Comet.Core;
using HarmonyLib;
using Newtonsoft.Json;
using SAIN.Components;
using SAIN.Helpers;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.Decision;
using SAIN.SAINComponent.Classes.EnemyClasses;
using UnityEngine;

namespace Comet.Client;

internal sealed class ThreatRuntime
{
    internal readonly ThreatSelection Selector;
    internal Enemy? Selected, Nearby;
    internal readonly Dictionary<string, Enemy> References = new Dictionary<string, Enemy>();
    internal ThreatTrace? Pending, LastTrace;
    internal bool Owned;
    internal double Next, FaultUntil;
    internal float LastHit = -1;
    internal int Cursor, Frame = -1;
    internal string LastSignature = "";
    internal double LastLog;
    internal ThreatRuntime(CometConfig config) { Selector = new ThreatSelection(config); }
}

public sealed partial class Plugin
{
    private static AccessTools.FieldRef<DogFightDecisionClass, Enemy> dogFightTarget = null!;
    private static Func<DogFightDecisionClass, Enemy, bool> dogFightEligibility = null!;
    private int threatFrame = -1, threatSamples;

    private static void PatchThreatSelection(Harmony h)
    {
        dogFightTarget = AccessTools.FieldRefAccess<DogFightDecisionClass, Enemy>("_lastDogFightTarget");
        dogFightEligibility = AccessTools.MethodDelegate<Func<DogFightDecisionClass, Enemy, bool>>(
            AccessTools.Method(typeof(DogFightDecisionClass), "ShallDogfightEnemy", new[] { typeof(Enemy) })
            ?? throw new MissingMethodException("SAIN dogfight eligibility"));
        h.Patch(AccessTools.Method(typeof(SAINEnemyController), "SelectEnemy") ?? throw new MissingMethodException("SAIN SelectEnemy"),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(SelectThreat)));
        h.Patch(AccessTools.Method(typeof(SAINEnemyController), "ChooseEnemy") ?? throw new MissingMethodException("SAIN ChooseEnemy"),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(ThreatAccepted)));
        h.Patch(AccessTools.Method(typeof(EnemyList), "AddEnemy", new[] { typeof(Enemy) }) ?? throw new MissingMethodException("SAIN visible enemy list"),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(NoticeVisibleThreat)));
    }

    // A list transition marks work due; it does not scan the scene or change a decision inside SAIN's perception update.
    private static void NoticeVisibleThreat(EnemyList __instance, Enemy enemy)
    {
        var p = Instance;
        if (p?.config == null || !p.config.Enabled || !p.config.ThreatSelection.Enabled || enemy == null) return;
        try
        {
            var bot = enemy.Bot;
            if (!ReferenceEquals(__instance, bot.EnemyController.VisibleEnemies) || !p.states.TryGetValue(bot, out var state)) return;
            var place = enemy.KnownPlaces.LastSeenPlace;
            // SAIN can notify its visible list before creating the first seen place. Queue that
            // transition too; the actual selection still requires a dated place on the next update.
            if (place == null || (place.Position - bot.Position).sqrMagnitude <= p.config.ThreatSelection.CloseDistance * p.config.ThreatSelection.CloseDistance)
                state.Threat.Nearby = enemy;
        }
        catch { /* Normal periodic evaluation remains available. */ }
    }

    private static bool ThreatNativeUrgency(BotComponent bot) => bot.Decision.CurrentSelfDecision != ESelfActionType.None
        || bot.Decision.CurrentSquadDecision != ESquadDecision.None && !TacticalPriority.OrdinarySquad(bot.Decision.CurrentSquadDecision.ToString()) || bot.Decision.CurrentCombatDecision == ECombatDecision.AvoidGrenade
        || !VisibleAdapter.WeaponReady(bot) || bot.BotOwner.WeaponManager.IsMelee;

    private bool ThreatUsable(BotComponent bot, Enemy? e)
    {
        if (e == null || !ContactSafety.Allows(bot,e) || !e.EnemyKnown || !e.CheckValid() || !Enemy.IsEnemyActive(e) || e.IsZombie || Ghost(e.EnemyProfileId)) return false;
        return bot.EnemyController.Enemies.TryGetValue(e.EnemyProfileId, out var member) && ReferenceEquals(e, member);
    }

    private bool CachedThreatUsable(BotComponent bot, Enemy? enemy)
    {
        if (!ThreatUsable(bot, enemy)) return false;
        var places = enemy!.KnownPlaces; var place = places.LastKnownPlace;
        if (place == null || (place.PlaceType != EEnemyPlaceType.Vision && place.PlaceType != EEnemyPlaceType.Hearing)
            || (!ReferenceEquals(place, places.LastSeenPlace) && !ReferenceEquals(place, places.LastHeardPlace)
                && !ReferenceEquals(place, places.LastSquadSeenPlace) && !ReferenceEquals(place, places.LastSquadHeardPlace))) return false;
        var position = place.Position; var age = place.TimeSincePositionUpdated;
        return age >= 0 && age <= config!.ContactLifetime && ThreatSelection.Finite(position.x)
            && ThreatSelection.Finite(position.y) && ThreatSelection.Finite(position.z);
    }

    private ThreatContact CaptureThreat(BotComponent bot, Enemy e)
    {
        var s = new ThreatContact { Id = e.EnemyProfileId, Valid = ThreatUsable(bot, e) };
        if (!s.Valid) return s;
        var places = e.KnownPlaces;
        // Never use LastShotPosition / Injury places or EnemyPosition. SAIN may have a damage-derived
        // location, but that alone is not perception. Refuse it instead of feeding it into native targeting.
        var place = places.LastKnownPlace;
        if (place == null) return s;
        s.Source = ReferenceEquals(place, places.LastSeenPlace) ? "seen"
            : ReferenceEquals(place, places.LastHeardPlace) ? "heard"
            : ReferenceEquals(place, places.LastSquadSeenPlace) ? "shared-seen"
            : ReferenceEquals(place, places.LastSquadHeardPlace) ? "shared-heard" : "unlocated";
        if (place.PlaceType != EEnemyPlaceType.Vision && place.PlaceType != EEnemyPlaceType.Hearing) s.Source = "unlocated";
        if (s.Source == "unlocated") return s;
        Vector3 position = place.Position;
        s.ObservedAt = Time.time - place.TimeSincePositionUpdated;
        s.Distance = Vector3.Distance(bot.Position, position); s.X = position.x; s.Y = position.y; s.Z = position.z;
        s.Visible = e.IsVisible && s.Source == "seen"; s.LookingAtMe = s.Visible && e.EnemyLookingAtMe;
        s.Footsteps = s.Source == "heard" && (place.SoundType == SAINSoundType.FootStep || place.SoundType == SAINSoundType.Sprint);
        s.Gunfire = s.Source == "heard" && place.SoundType.HasValue && place.SoundType.Value.IsGunShot();
        s.HitAt = e.Status.TimeLastShotMe >= 0 ? e.Status.TimeLastShotMe : (double?)null;
        s.ShotAt = e.Status.TimeLastShotAtMe >= 0 ? e.Status.TimeLastShotAtMe : (double?)null;
        return s;
    }

    private static bool SelectThreat(SAINEnemyController __instance, ref Enemy __result)
    {
        var p = Instance;
        if (p?.config == null) return true;
        var bot = __instance.Bot;
        if (!NativeAdapter.IsPmcAi(bot)) return true;
        try
        {
            if (!p.config.Enabled || !p.config.ThreatSelection.Enabled || !p.Authority || !bot.BotActive || p.Ghost(bot.ProfileId)
                || ThreatNativeUrgency(bot))
            {
                if (p.states.TryGetValue(bot, out var prior)) p.ReleaseThreat(bot, prior, "threat-yield-to-sain");
                return true;
            }
            if (!p.states.TryGetValue(bot, out var state)) { state = new BotState(bot, p.config, p.session); p.states.Add(bot, state); }
            var runtime = state.Threat;
            if (Time.time < runtime.FaultUntil) return true;
            bool valid = p.CachedThreatUsable(bot, runtime.Selected);
            bool newHit = bot.Medical.TimeLastShot > runtime.LastHit;
            bool due = !valid || !ReferenceEquals(runtime.Selected, __instance.GoalEnemy) || Time.time >= runtime.Next || newHit || runtime.Nearby != null;
            if (!valid && runtime.Frame == Time.frameCount) { p.ReleaseThreat(bot, state, "threat-current-invalid"); return true; }
            if (due && runtime.Frame != Time.frameCount)
            {
                if (p.threatFrame != Time.frameCount) { p.threatFrame = Time.frameCount; p.threatSamples = 0; }
                if (p.threatSamples >= 4)
                {
                    if (valid) { __result = runtime.Selected!; return false; }
                    p.ReleaseThreat(bot, state, "threat-budget-deferred"); return true;
                }
                p.threatSamples++; runtime.Frame = Time.frameCount;
                var frame = new ThreatFrame { Now = Time.time, Current = __instance.GoalEnemy?.EnemyProfileId ?? "",
                    IsAi = true, IsPmc = true, IsAuthority = true, Active = true,
                    Trigger = newHit ? "new-hit" : runtime.Nearby != null ? "new-close-visible" : !valid ? "contact-changed" : "periodic",
                    Truncated = __instance.KnownEnemies.Count > ThreatSelection.CandidateLimit };
                runtime.References.Clear();
                void Add(Enemy? enemy)
                {
                    if (enemy == null || frame.Contacts.Count >= ThreatSelection.CandidateLimit || runtime.References.ContainsKey(enemy.EnemyProfileId)) return;
                    runtime.References.Add(enemy.EnemyProfileId, enemy); frame.Contacts.Add(p.CaptureThreat(bot, enemy));
                }
                // Always include the current target and urgent contacts; rotate the remaining bounded scan.
                Add(__instance.GoalEnemy); Add(bot.Medical.HitByEnemy.EnemyWhoLastShotMe); Add(runtime.Nearby);
                var known = __instance.KnownEnemies;
                int count = 0;
                while (count < Math.Min(known.Count, ThreatSelection.CandidateLimit) && frame.Contacts.Count < ThreatSelection.CandidateLimit)
                { Add(known[(runtime.Cursor + count) % known.Count]); count++; }
                runtime.Cursor = known.Count == 0 ? 0 : (runtime.Cursor + count) % known.Count;
                var trace = runtime.Selector.Evaluate(frame);
                runtime.Pending = trace; runtime.Next = Time.time + p.config.ThreatSelection.Interval;
                runtime.LastHit = bot.Medical.TimeLastShot; runtime.Nearby = null;
                if (trace.Selected.Length == 0 || !runtime.References.TryGetValue(trace.Selected, out var selected))
                { p.ReleaseThreat(bot, state, trace.Reason, trace); return true; }
                runtime.Selected = selected;
            }
            if (runtime.Selected == null) return true;
            // Returning the cached choice also prevents native SAIN from immediately undoing A07's hold.
            __result = runtime.Selected;
            return false;
        }
        catch (Exception ex)
        {
            if (p.states.TryGetValue(bot, out var state)) { p.ReleaseThreat(bot, state, "threat-adapter-error"); state.Threat.FaultUntil = Time.time + 10; }
            p.Logger.LogWarning("COMET target selection yielded: " + ex.Message); return true;
        }
    }

    private static void ThreatAccepted(SAINEnemyController __instance, Enemy __result)
    {
        var p = Instance;
        if (p?.config == null || !p.states.TryGetValue(__instance.Bot, out var state)) return;
        var runtime = state.Threat;
        if (runtime.Selected == null) return;
        var bot = __instance.Bot;
        try
        {
            if (!ReferenceEquals(__result, runtime.Selected)) { p.ReleaseThreat(bot, state, "threat-native-refused"); return; }
            // Native dogfight remains native behavior, but can only use the accepted A07 target.
            // Its private selector never runs over the full known list or sorts it while A07 owns attention.
            bool dogfight = __result.IsVisible && !ThreatNativeUrgency(bot) && bot.Decision.CurrentCombatDecision != ECombatDecision.RushEnemy
                && !((bot.Decision.CurrentCombatDecision == ECombatDecision.Retreat || bot.Cover.SprintingToCover) && bot.Decision.SelfActionDecisions.LowOnAmmo(.3f))
                && dogFightEligibility(bot.Decision.DogFightDecision, __result);
            dogFightTarget(bot.Decision.DogFightDecision) = dogfight ? __result : null!;
            runtime.Owned = true;
            var trace = runtime.Pending; runtime.Pending = null;
            if (trace == null) return;
            if (trace.Previous != trace.Selected) { p.RefreshDoorInterruption(bot,state,__result.EnemyProfileId); p.Release(state, "threat-target-changed"); } // Does not reset A06 fear.
            runtime.LastTrace = trace;
            p.EmitThreat(state, "accepted", trace);
        }
        catch (Exception ex) { p.ReleaseThreat(bot, state, "threat-adapter-error"); runtime.FaultUntil = Time.time + 10; p.Logger.LogWarning(ex.Message); }
    }

    private void ReleaseThreat(BotComponent bot, BotState state, string reason, ThreatTrace? trace = null)
    {
        var r = state.Threat;
        if (r.Owned && bot != null)
        {
            try { if (bot.Decision != null) dogFightTarget(bot.Decision.DogFightDecision) = null!; }
            catch (Exception ex) { Logger.LogWarning("COMET target cleanup: " + ex.Message); }
        }
        if (r.Owned || trace != null)
        {
            trace ??= new ThreatTrace { At = Time.time, Previous = r.Selected?.EnemyProfileId ?? "", Trigger = "released" };
            trace.Reason = reason; trace.Selected = "";
            EmitThreat(state, "released", trace);
        }
        r.Owned = false; r.Selected = r.Nearby = null; r.Pending = null; r.LastTrace = null;
        r.Selector.Reset(); r.References.Clear();
    }

    private void EmitThreat(BotState state, string stage, ThreatTrace trace)
    {
        var r = state.Threat;
        // Log changes and meaningful rejected challenges; do not repeat periodic scores per frame.
        string signature = stage + ":" + trace.Selected + ":" + trace.Reason + ":" + string.Join(",", trace.Candidates.OrderBy(x => x.Target).Select(x => x.Target + ":" + x.Eligible));
        bool changed = stage == "accepted" && trace.Selected != trace.Previous;
        if (!changed && signature == r.LastSignature) return;
        if (!changed && stage == "accepted" && r.LastSignature.Length > 0 && Time.time - r.LastLog < 1) return;
        r.LastSignature = signature; r.LastLog = Time.time;
        var contact = trace.Candidates.Find(x => x.Target == trace.Selected);
        string json = Diagnostics.Serialize(new {
            schema = 1, session, sequence = ++sequence, feature = "A07", stage, reason = trace.Reason,
            decision = "SelectTarget", realtime = Time.realtimeSinceStartup, utc = DateTime.UtcNow.ToString("O"),
            configRevision = config?.Revision ?? 0, config = config==null?null:Diagnostics.ThreatConfig(config), cometVersion = "0.1.0", profileId = state.ProfileId, name = state.Name, botInstance = state.Id,
            threat = trace, fear = state.Fear.Current,
            contact = contact == null ? null : new { target = contact.Target, source = contact.Source, age = contact.Age, distance = contact.Distance,
                x = contact.X, y = contact.Y, z = contact.Z }
        });
        diagnostics?.Write(json);
        Api.Decisions.Publish(json, ex => { if (!subscriberWarned) { subscriberWarned = true; Logger.LogWarning(ex.Message); } });
        // Deliberately do NOT set GetCurrentDecision: selecting a target is not owning SAIN's action.
    }
}
