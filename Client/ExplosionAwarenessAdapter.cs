using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Comet.Core;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using Newtonsoft.Json;
using SAIN.Classes.Bot.Sense.Hearing;
using SAIN.Components;
using SAIN.Components.PlayerComponentSpace;
using SAIN.Preset.Shared.Enums;
using SAIN.SAINComponent.Classes.EnemyClasses;
using SAIN.SAINComponent.Classes.Mover;
using UnityEngine;

namespace Comet.Client;

internal sealed class BlastProof
{
    internal string Owner = "", Evidence = "", Observer = "";
    internal int OwnerInstance;
    internal float At;
    internal Vector3 Point;
}
internal sealed class BlastThrow
{
    internal string Owner = "", Kind = "special";
    internal int Id;
    internal float At;
    internal readonly Dictionary<BotComponent, BlastProof> Proofs = new();
}
internal sealed class BlastSignal
{
    internal string Owner = "", Kind = "blast", Key = "";
    internal Vector3 Position;
    internal int Id, Cursor;
    internal float At, Range;
    internal BotComponent[] Listeners = Array.Empty<BotComponent>();
    internal BlastThrow? Throw;
}
internal sealed class BlastRuntime
{
    internal readonly ExplosionAwareness Policy = new();
    internal float NextLog, LastApplied;
    internal bool Fault;
}

public sealed partial class Plugin
{
    private readonly List<BlastThrow> blastThrows = new();
    private readonly List<BlastSignal> blastRecent = new();
    private readonly Queue<BlastSignal> blastQueue = new();
    private BlastSignal? blastCall;
    private bool blastWarned;
    private long blastSequence;
    private int blastThrowRayFrame = -1, blastThrowRays;
    private static void PatchExplosionAwareness(Harmony h)
    {
        h.Patch(AccessTools.Method(typeof(GrenadeController), "GrenadeExplosion"),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(BlastBegin)), finalizer: new HarmonyMethod(typeof(Plugin), nameof(BlastFinally)));
        h.Patch(AccessTools.Method(typeof(GrenadeController), "GrenadeThrown"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BlastThrown)));
        h.Patch(AccessTools.Method(typeof(GrenadeController), "RegisterGrenadeExplosionForSAINBots"),
            transpiler: new HarmonyMethod(typeof(Plugin), nameof(BlastReportTranspiler)));
        foreach (var name in new[] { "OnSoundPlayed", "OnSoundPlayedInner" })
            h.Patch(AccessTools.Method(typeof(BotHearingSensor), name), prefix: new HarmonyMethod(typeof(Plugin), nameof(BlastVanillaSound)) { priority = Priority.First });
        h.Patch(AccessTools.Method(typeof(HearingInput), nameof(HearingInput.CheckAddSoundToCache)),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(BlastSoundCache)) { priority = Priority.First });
        h.Patch(AccessTools.Method(typeof(Enemy), nameof(Enemy.EnemyPositionReported)), prefix: new HarmonyMethod(typeof(Plugin), nameof(BlastSharedContact)));
        h.Patch(AccessTools.Method(typeof(SAINSteeringClass), "TickPlayerSteering"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BlastSteer)));
    }
    private bool BlastEnabled => world != null && config?.Enabled == true && config.ExplosionAwareness.Enabled && Authority;
    private bool BlastScope(BotComponent b) => BlastEnabled && b != null && NativeAdapter.IsPmcAi(b)
        && b.BotActive && !b.IsDead && !b.GameEnding && !Ghost(b.ProfileId);
    private static IEnumerable<CodeInstruction> BlastReportTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var method = AccessTools.Method(typeof(SAINEnemyController), nameof(SAINEnemyController.GetEnemy), new[] { typeof(string), typeof(bool) });
        int count = 0;
        foreach (var code in instructions)
        {
            if (code.Calls(method)) { count++; code.opcode = OpCodes.Call; code.operand = AccessTools.Method(typeof(Plugin), nameof(BlastNativeEnemy)); }
            yield return code;
        }
        if (count != 1) throw new InvalidOperationException("G10 native explosion report changed.");
    }
    // Skip only the per-PMC manual report that reads EnemyPosition; original sound/effects and other bots continue.
    private static Enemy? BlastNativeEnemy(SAINEnemyController controller, string id, bool active)
        => Instance?.blastCall != null && Instance.BlastScope(controller.Bot) ? null : controller.GetEnemy(id, active);
    private static bool BlastSharedContact(Enemy __instance) => Instance?.blastCall == null || !Instance.BlastScope(__instance.Bot);
    private static bool BlastVanillaSound(BotHearingSensor __instance, IPlayer player, Vector3 position)
    {
        var p = Instance; if (p?.blastCall == null || !p.BlastMatches(player?.ProfileId, position)) return true;
        var dict = BotManagerComponent.Instance?.BotSpawnController?.BotDictionary;
        return dict == null || !dict.TryGetValue(__instance._botOwner.GetPlayer.ProfileId, out var b) || !p.BlastScope(b);
    }
    private static bool BlastSoundCache(HearingInput __instance, SoundEvent Sound)
    {
        var p = Instance;
        return p?.blastCall == null || !p.BlastScope(__instance.Bot) || !p.BlastMatches(Sound.GetPlayer()?.ProfileId, Sound.Position);
    }
    private bool BlastMatches(string? owner, Vector3 position) => blastCall != null && owner == blastCall.Owner && (position - blastCall.Position).sqrMagnitude < .0001f;
    private static void BlastBegin(GrenadeController __instance, Vector3 explosionPosition, string playerProfileID, bool isSmoke, int throwableId, out BlastSignal? __state)
    {
        var p = Instance; __state = p?.blastCall; if (p == null) return;
        p.blastCall = null;
        if (!p.BlastEnabled) return;
        // Establish nested context even for rejected/duplicate/special events to protect native sound paths.
        var signal = new BlastSignal { Owner = playerProfileID ?? "", Position = explosionPosition, Id = throwableId, At = Time.time, Range = isSmoke ? 50 : 200 };
        p.blastCall = signal;
        try
        {
            if (!ValidPoint(explosionPosition)) return;
            p.PruneBlasts();
            if (p.blastRecent.Any(x => x.Id == throwableId && x.Owner == signal.Owner && (x.Position - explosionPosition).sqrMagnitude < .01f)) return;
            if (p.blastQueue.Count >= 64 || p.blastRecent.Count >= 128) { p.BlastWarning("signal capacity reached; no extra contact created"); return; }
            signal.Throw = p.blastThrows.LastOrDefault(x => x.Id == throwableId && x.Owner == signal.Owner);
            signal.Kind = isSmoke ? "smoke" : signal.Throw?.Kind ?? "blast";
            signal.Key = (++p.blastSequence).ToString();
            signal.Listeners = __instance.Bots.Values.Where(p.BlastScope).Take(512).ToArray();
            p.blastRecent.Add(signal); p.blastQueue.Enqueue(signal);
        }
        catch (Exception ex) { p.BlastWarning(ex.Message); }
    }
    private static Exception? BlastFinally(BlastSignal? __state, Exception? __exception)
    { if (Instance != null) Instance.blastCall = __state; return __exception; }
    private static void BlastThrown(GrenadeController __instance, Grenade grenade)
    {
        var p = Instance; if (p == null || !p.BlastEnabled || grenade == null) return;
        try
        {
            p.PruneBlasts();
            if (p.blastThrows.Any(x => x.Id == grenade.Id && x.Owner == grenade.ProfileId) || p.blastThrows.Count >= 64) return;
            var t = new BlastThrow { Id = grenade.Id, Owner = grenade.ProfileId, At = Time.time };
            if (grenade.WeaponSource is ThrowWeap item)
            {
                string type = item.ThrowType.ToString();
                t.Kind = item.EmitTime > 0 || item.IsDummy || type == "smoke_grenade" || type == "gas_grenade" ? "special"
                    : item.FragmentsCount > 0 && item.MaxExplosionDistance > 0 ? "blast" : "flash";
            }
            foreach (var b in __instance.Bots.Values.Where(p.BlastScope).Take(512))
            {
                if (b.Flashed.IsFlashed) continue;
                if (b.ProfileId == t.Owner) { t.Proofs[b] = new BlastProof { Owner = t.Owner, At = Time.time, Evidence = "own" }; continue; }
                if (b.Squad.Members.TryGetValue(t.Owner, out var ally) && ally != null)
                {
                    if (p.blastThrowRayFrame != Time.frameCount) { p.blastThrowRayFrame = Time.frameCount; p.blastThrowRays = 0; }
                    var delta = ally.Position - b.Position;
                    if (delta.sqrMagnitude < 3600 && Vector3.Dot(b.Player.LookDirection, delta.normalized) > .5f && p.blastThrowRays < 8)
                    {
                        p.blastThrowRays++;
                        if (!Physics.Linecast(b.Transform.HeadData.HeadPosition, ally.Transform.HeadData.HeadPosition, LayersMaskController.HighPolyWithTerrainMask))
                            t.Proofs[b] = new BlastProof { Owner = t.Owner, Observer = b.ProfileId, At = Time.time, Evidence = "known-ally" };
                    }
                    continue;
                }
                // A cached enemy's existence alone proves nothing. Require fresh personal sight at the actual throw.
                var e = b.EnemyController.GetEnemy(t.Owner, true); var seen = e?.KnownPlaces.LastSeenPlace;
                if (e?.IsVisible != true || seen == null || seen.TimeSincePositionUpdated < 0 || seen.TimeSincePositionUpdated > .25f || !ValidPoint(seen.Position)) continue;
                var source = e!.EnemyPlayer; var group = b.BotOwner.BotsGroup;
                bool friendly = b.Squad.Members.ContainsKey(t.Owner) || group.IsAlly(source) || !group.IsPlayerEnemy(source);
                t.Proofs[b] = new BlastProof { Owner = t.Owner, At = Time.time - seen.TimeSincePositionUpdated, Point = seen.Position,
                    Evidence = friendly ? "known-ally" : "seen-throw", Observer = b.ProfileId, OwnerInstance = RuntimeHelpers.GetHashCode(e) };
            }
            p.blastThrows.Add(t);
        }
        catch (Exception ex) { p.BlastWarning(ex.Message); }
    }
    private void PruneBlasts()
    {
        blastThrows.RemoveAll(x => Time.time < x.At || Time.time - x.At > 30);
        blastRecent.RemoveAll(x => Time.time < x.At || Time.time - x.At > 2);
    }
    private void UpdateBlasts()
    {
        if (!BlastEnabled) { blastQueue.Clear(); blastThrows.Clear(); blastRecent.Clear(); return; }
        PruneBlasts(); int checks = 0, rays = 0;
        while (blastQueue.Count > 0 && checks < 64 && rays < 8)
        {
            var signal = blastQueue.Peek();
            if (Time.time < signal.At || Time.time - signal.At > 1 || signal.Cursor >= signal.Listeners.Length) { signal.Listeners = Array.Empty<BotComponent>(); blastQueue.Dequeue(); continue; }
            var b = signal.Listeners[signal.Cursor++]; checks++;
            if (!BlastScope(b)) continue;
            try
            {
                double distance = Vector3.Distance(b.Position, signal.Position);
                if (!ExplosionAwareness.Finite(distance) || distance > signal.Range || distance < 0) continue;
                if (!states.TryGetValue(b, out var s)) { if (states.Count >= 512) continue; s = new BotState(b, config!, session); states.Add(b, s); }
                if (s.Blast.Fault) continue;
                var t = new ExplosionTrace { Signal = signal.Key, Kind = signal.Kind, Distance = distance, X = signal.Position.x, Y = signal.Position.y, Z = signal.Position.z };
                float power = signal.Range * b.Info.Difficulty.HearingDistanceModifier;
                bool ignored = b.Hearing.HearingInput.IsIgnoringSounds(true);
                if (b.Hearing.HearingInput.IsBotDeafened) power *= .3f;
                bool heard = !ignored && ExplosionAwareness.Finite(power) && power > 0 && b.BotOwner.HearingSensor.IsSoundHeard(signal.Position, power, out _);
                if (heard)
                {
                    rays++;
                    bool blocked = Physics.Linecast(b.Transform.HeadData.HeadPosition, signal.Position + Vector3.up * .1f, LayersMaskController.HighPolyWithTerrainMask);
                    if (blocked) power *= Mathf.Clamp01(SAIN.SAINPlugin.LoadedPreset.GlobalSettings.Hearing.GUNSHOT_OCCLUSION_MOD);
                    heard = b.BotOwner.HearingSensor.IsSoundHeard(signal.Position, power, out _);
                }
                t.Audible = heard;
                if (heard && signal.Throw?.Proofs.TryGetValue(b, out var proof) == true)
                {
                    var e = b.EnemyController.GetEnemy(proof.Owner, true);
                    if (proof.Evidence == "own" || proof.Evidence == "known-ally") t.Evidence = proof.Evidence;
                    else if (e != null && RuntimeHelpers.GetHashCode(e) == proof.OwnerInstance && proof.At <= signal.At && signal.At - proof.At <= 30)
                    {
                        t.Evidence = "seen-throw"; t.Target = proof.Owner; t.EvidenceAge = Time.time - proof.At;
                        t.ContactX = proof.Point.x; t.ContactY = proof.Point.y; t.ContactZ = proof.Point.z;
                        // Retain existing dated native vision memory. Do NOT SetHeard: it would refresh the age and broadcast an owner.
                    }
                }
                t.Alternatives = new[] { "Explosion location is not the thrower's location", "Keep existing contact age; do not create an enemy", "Urgent combat and grenade escape take priority" };
                var result = s.Blast.Policy.Receive(t, Time.time, BlastUnsafe(b, s), config!.ExplosionAwareness);
                if (result.State == "look") StopGunfire(s, "gunfire-explosion-priority");
                RecordBlast(s, result, true);
            }
            catch (Exception ex) { BlastError(b, ex); }
        }
    }
    private string BlastUnsafe(BotComponent b, BotState s)
    {
        if (!BlastScope(b) || s.Blast.Fault) return "explosion-out-of-scope";
        if (b.Flashed.IsFlashed || b.Grenade.GrenadeDangerPoint != null || b.Decision.CurrentCombatDecision == ECombatDecision.AvoidGrenade
            || b.Decision.CurrentSelfDecision != ESelfActionType.None || b.Decision.CurrentSquadDecision != ESquadDecision.None
            || !VisibleAdapter.WeaponReady(b) || b.BotOwner.DoorOpener.Interacting || b.BotOwner.WeaponManager.Reload.Reloading) return "explosion-urgent-action";
        if (b.GoalEnemy != null || b.EnemyController.VisibleEnemies.Count > 0 || b.BotOwner.Memory.IsUnderFire || b.Suppression.IsSuppressed
            || b.BotOwner.ShootData.Shooting || b.ManualShoot.Shooting || s.Fear.Current.Panic || s.Sight.Current != null || s.ActiveLabel.Length > 0) return "explosion-combat-priority";
        if (b.Player.IsSprintEnabled || b.Mover.Running || GunfireOwner(b) == "other") return "explosion-steering-unavailable";
        return "";
    }
    private static void BlastSteer(SAINSteeringClass __instance)
    { var p = Instance; if (p != null && p.states.TryGetValue(__instance.Bot, out var s)) p.ApplyBlastLook(__instance.Bot, s, false); }
    private void LateBlasts()
    {
        foreach (var pair in states)
        {
            if (pair.Value.Blast.Policy.Look == null) continue;
            try
            {
                var b = pair.Key; var s = pair.Value;
                var t = s.Blast.Policy.Tick(Time.time, BlastUnsafe(b, s), false);
                if (t?.State == "ended") RecordBlast(s, t, true);
                else if (t != null && !b.SAINLayersActive) ApplyBlastLook(b, s, true);
                else if (t?.Applied == true && Time.time - s.Blast.LastApplied > .5f) StopBlast(s, "explosion-steering-not-applied");
            }
            catch (Exception ex) { BlastError(pair.Key, ex); }
        }
    }
    private void ApplyBlastLook(BotComponent b, BotState s, bool native)
    {
        if (s.Blast.Policy.Look == null) return;
        try
        {
            var t = s.Blast.Policy.Tick(Time.time, BlastUnsafe(b, s), false); if (t == null) return;
            if (t.State == "ended") { RecordBlast(s, t, true); return; }
            var point = new Vector3((float)t.X, (float)t.Y, (float)t.Z);
            if (native) b.BotOwner.Steering.LookToPoint(point); else b.Steering.LookToPoint(point);
            bool first = !t.Applied; s.Blast.LastApplied = Time.time;
            t = s.Blast.Policy.Tick(Time.time, "", true); if (t != null) RecordBlast(s, t, first);
        }
        catch (Exception ex) { BlastError(b, ex); }
    }
    private void StopBlast(BotState s, string reason) { var t = s.Blast.Policy.Stop(reason); if (t != null) RecordBlast(s, t, true); }
    private void ClearBlasts()
    {
        foreach (var s in states.Values) StopBlast(s, "explosion-raid-ended");
        blastQueue.Clear(); blastThrows.Clear(); blastRecent.Clear(); blastCall = null; blastSequence = 0; blastWarned = false; blastThrowRayFrame = -1; blastThrowRays = 0;
    }
    private void BlastWarning(string message) { if (!blastWarned) { blastWarned = true; Logger.LogWarning("G10: " + message); } }
    private void BlastError(BotComponent b, Exception ex)
    { if (states.TryGetValue(b, out var s)) { s.Blast.Fault = true; StopBlast(s, "explosion-adapter-error"); } BlastWarning(ex.GetBaseException().Message); }
    private void RecordBlast(BotState s, ExplosionTrace t, bool force)
    {
        if (!force && Time.time < s.Blast.NextLog) return;
        s.Blast.NextLog = Time.time + .5f;
        var json = Diagnostics.Serialize(new { schema = 1, session, sequence = ++sequence, feature = "G10", stage = "evaluated", decision = "ExplosionAwareness", reason = t.Reason,
            realtime = Time.realtimeSinceStartup, utc = DateTime.UtcNow.ToString("O"), configRevision = config?.Revision ?? 0,
            profileId = s.ProfileId, name = s.Name, botInstance = s.Id, explosionAwareness = t, explosionAwarenessConfig = config?.ExplosionAwareness });
        diagnostics?.Write(json); Api.Decisions.Publish(json, ex => { if (!subscriberWarned) { subscriberWarned = true; Logger.LogWarning(ex.Message); } });
    }
}
