using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using Comet.Core;
using HarmonyLib;
using Newtonsoft.Json;
using SAIN.Classes.Bot.Sense.Hearing;
using SAIN.Components;
using SAIN.Components.PlayerComponentSpace;
using SAIN.Preset.Shared.Enums;
using UnityEngine;

namespace Comet.Client;

public sealed partial class Plugin
{
    private readonly FloorGeometryProbe floorProbe = new();
    private readonly FloorGeometryCache floorCache = new();
    private readonly Dictionary<string, int> floorCounters = new();
    private readonly FloorPhysics floorPhysics = new();
    private float nextFloorSummary, floorFaultUntil;
    private bool floorWarned;

    private static void PatchFloorHearing(Harmony h)
    {
        h.Patch(AccessTools.Method(typeof(HearingAnalysis), nameof(HearingAnalysis.CheckIfSoundHeard)),
            transpiler: new HarmonyMethod(typeof(Plugin), nameof(FloorRangeTranspiler)));
    }

    // The preview already requires the exact official DLL hash. Fail closed at startup if this
    // verified range-calculation contract changes. Native rolls, equipment and clamps run ONCE.
    private static IEnumerable<CodeInstruction> FloorRangeTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        int env = code.FindIndex(x => x.Calls(AccessTools.Method(typeof(HearingAnalysis), "CalcEnvironmentMod")));
        int occ = code.FindIndex(x => x.Calls(AccessTools.Method(typeof(HearingAnalysis), "CalcOcclusionMod")));
        if (env < 0 || occ < env || code[env+1].opcode != OpCodes.Stloc_0 || code[occ+1].opcode != OpCodes.Stloc_2)
            throw new InvalidOperationException("P04 native hearing modifiers changed.");
        int matches = 0;
        for (int i = 0; i < code.Count; i++)
        {
            var instruction = code[i];
            if (instruction.opcode == OpCodes.Stloc_S && LocalIndex(instruction.operand) == 4)
            {
                if (i < occ || code[i-1].opcode != OpCodes.Mul || code[i+1].opcode != OpCodes.Ldarg_1
                    || !code[i+2].LoadsField(AccessTools.Field(typeof(AISoundData), nameof(AISoundData.PlayerDistance))))
                    throw new InvalidOperationException("P04 native final range changed.");
                matches++;
                var store = new CodeInstruction(instruction.opcode, instruction.operand);
                // Mutate the original entry so its labels/exception boundaries stay on the first injected op.
                instruction.opcode = OpCodes.Ldarg_0; instruction.operand = null;
                yield return instruction; yield return new CodeInstruction(OpCodes.Ldarg_1);
                yield return new CodeInstruction(OpCodes.Ldloc_0); yield return new CodeInstruction(OpCodes.Ldloc_2);
                yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Plugin), nameof(FloorRange)));
                yield return store; continue;
            }
            yield return instruction;
        }
        if (matches != 1) throw new InvalidOperationException("P04 expected one native range calculation.");
    }
    private static int LocalIndex(object value) => value is LocalBuilder local ? local.LocalIndex : Convert.ToInt32(value);
    private bool FloorScoped(BotComponent b) => world != null && config?.Enabled == true && config.FloorHearing.Enabled
        && NativeAdapter.IsPmcAi(b) && b.BotActive && !b.GameEnding && Authority && !Ghost(b.ProfileId);

    private static float FloorRange(float nativeRange, HearingAnalysis analysis, AISoundData sound, float environment, float occlusion)
    {
        var p = Instance;
        if (p == null) return nativeRange;
        try { return p.FilterFloorRange(nativeRange, analysis.Bot, sound, environment, occlusion); }
        catch (Exception ex)
        {
            p.CountFloor("adapter-error"); p.floorFaultUntil = Time.time + 10;
            if (!p.floorWarned) { p.floorWarned = true; p.Logger.LogWarning("COMET P04 keeps native hearing after an error: " + ex.Message); }
            return nativeRange; // Do not turn a geometry/telemetry failure into deaf AI.
        }
    }
    private float FilterFloorRange(float nativeRange, BotComponent bot, AISoundData sound, float environment, float occlusion)
    {
        if (!FloorScoped(bot) || Time.time < floorFaultUntil) return nativeRange;
        CountFloor("range-evaluated");
        if (!FloorHearing.Finite(nativeRange) || nativeRange <= 0 || !FloorHearing.Finite(sound.PlayerDistance)
            || sound.PlayerDistance < 0 || sound.PlayerDistance > nativeRange)
        { CountFloor("native-out-of-range"); return nativeRange; }
        var enemy = sound.Enemy;
        if (enemy == null) return nativeRange;
        bool supported = sound.SoundType is SAINSoundType.Shot or SAINSoundType.SuppressedShot
            or SAINSoundType.FootStep or SAINSoundType.Sprint or SAINSoundType.Prone or SAINSoundType.Jump or SAINSoundType.Land
            or SAINSoundType.TurnSound or SAINSoundType.Bush or SAINSoundType.GearSound or SAINSoundType.Looting
            or SAINSoundType.Reload or SAINSoundType.Heal or SAINSoundType.Surgery or SAINSoundType.Food;
        // Impacts, explosions, door breaches, pain and unknown/modded sound types remain native.
        if (!supported) { CountFloor("sound-preserved"); return nativeRange; }
        var emission = Point(sound.Sound.Position); // Immutable emission, never source.Player.Position.
        var listener = Point(bot.Transform.EyePosition);
        if (!emission.Finite || !listener.Finite || !FloorHearing.Finite(sound.Sound.TimeCreated)
            || sound.Sound.TimeCreated > Time.time || Time.time-sound.Sound.TimeCreated > 2)
        { CountFloor("stale-or-invalid-signal"); return nativeRange; }
        if (Math.Abs(emission.Y-bot.Position.y) < config!.FloorHearing.MinimumHeight)
        { CountFloor("same-level"); return nativeRange; }
        bool priority = enemy.InLineOfSight || enemy.IsVisible || CachedThreatUsable(bot, enemy)
            || bot.BotOwner.Memory.IsUnderFire || bot.Suppression.IsSuppressed
            || GunfireReaction.Recent(Time.time, enemy.Status.TimeLastShotAtMe, 5)
            || GunfireReaction.Recent(Time.time, enemy.Status.TimeLastShotMe, 5);
        bool cached = false;
        var geometry = new FloorGeometry(0, "floor-combat-priority");
        if (!priority)
        {
            string key = bot.ProfileId + ":" + enemy.EnemyProfileId;
            cached = floorCache.TryGet(key, Time.time, listener, emission, out geometry);
            if (!cached)
            {
                floorPhysics.Begin(Time.frameCount);
                try { geometry = floorProbe.Probe(floorPhysics, listener, emission); }
                finally { floorPhysics.End(); }
                floorCache.Put(key, Time.time, listener, emission, geometry);
            }
        }
        var t = FloorHearing.Evaluate(config.FloorHearing, new FloorHearingFrame { NativeRange = nativeRange,
            Distance = sound.PlayerDistance, Environment = environment, Occlusion = occlusion,
            Gunfire = sound.IsGunShot, Geometry = geometry, Priority = priority });
        t.Cached = cached; t.Source = enemy.EnemyProfileId; t.Kind = sound.SoundType.ToString(); t.EmittedAt = sound.Sound.TimeCreated;
        CountFloor(t.Reason); if (cached) CountFloor("cache-hit");
        RecordFloor(bot, t);
        return (float)t.FinalRange;
    }
    private static AcousticPoint Point(Vector3 v) => new(v.x, v.y, v.z);
    private void CountFloor(string key) { floorCounters.TryGetValue(key, out int n); floorCounters[key] = n + 1; }
    private void RecordFloor(BotComponent bot, FloorHearingTrace t)
    {
        if (!states.TryGetValue(bot, out var s))
        { if (states.Count >= 512) return; s = new BotState(bot, config!, session); states.Add(bot, s); }
        if (Time.time < s.FloorNextLog) { s.FloorCoalesced++; return; }
        s.FloorNextLog = Time.time + .5f;
        var json = Diagnostics.Serialize(new { schema = 1, session, sequence = ++sequence, feature = "P04", stage = "evaluated",
            decision = "FloorHearing", reason = t.Reason, realtime = Time.realtimeSinceStartup, utc = DateTime.UtcNow.ToString("O"),
            cometVersion = "0.1.0", configRevision = config?.Revision ?? 0, floorConfig = config?.FloorHearing,
            profileId = s.ProfileId, name = s.Name, botInstance = s.Id, floorHearing = t, coalesced = s.FloorCoalesced });
        s.FloorCoalesced = 0;
        diagnostics?.Write(json); Api.Decisions.Publish(json, ex => { if (!subscriberWarned) { subscriberWarned = true; Logger.LogWarning(ex.Message); } });
        // Range filtering is not action ownership, contact registration or proof of a reaction.
    }
    private void FlushFloorCounters(bool force = false)
    {
        if (!force && Time.time < nextFloorSummary) return;
        nextFloorSummary = Time.time + 10;
        if (floorCounters.Count == 0) return;
        diagnostics?.Write(Diagnostics.Serialize(new { schema = 1, session, feature = "P04", stage = "summary",
            reason = "floor-counters", realtime = Time.realtimeSinceStartup, counters = floorCounters,
            cachedPairs = floorCache.Count }));
        floorCounters.Clear();
    }
    private void ClearFloorHearing()
    {
        FlushFloorCounters(true); floorCache.Clear(); floorPhysics.Clear();
        nextFloorSummary = floorFaultUntil = 0; floorWarned = false;
    }
    private sealed class FloorPhysics : IFloorAcoustics
    {
        private readonly RaycastHit[] hits = new RaycastHit[24];
        private readonly AcousticWorkBudget budget = new();
        private readonly System.Diagnostics.Stopwatch clock = new();
        public void Begin(int frame) { budget.Begin(frame); clock.Restart(); }
        public void End() { clock.Stop(); budget.Charge(clock.Elapsed.TotalMilliseconds); }
        public void Clear() { Array.Clear(hits, 0, hits.Length); budget.Clear(); clock.Reset(); }
        public int Cast(AcousticPoint from, AcousticPoint to, AcousticHit[] output)
        {
            if (!budget.TryCast(clock.Elapsed.TotalMilliseconds)) return -1;
            var origin = new Vector3((float)from.X, (float)from.Y, (float)from.Z);
            var delta = new Vector3((float)(to.X-from.X), (float)(to.Y-from.Y), (float)(to.Z-from.Z));
            if (delta.sqrMagnitude < .001f) return -1;
            int n = Physics.RaycastNonAlloc(origin, delta.normalized, hits, delta.magnitude,
                LayersMaskController.HighPolyWithTerrainMask, QueryTriggerInteraction.Ignore);
            if (n >= hits.Length) { Array.Clear(hits, 0, hits.Length); return -1; }
            for (int i = 0; i < n; i++)
            {
                var h = hits[i]; var collider = h.collider;
                bool structural = collider != null && !(collider is TerrainCollider) && collider.attachedRigidbody == null;
                output[i] = new AcousticHit(h.point.y, h.normal.y, h.distance, structural);
                hits[i] = default; // Do not retain scene objects, including in the reusable buffer.
            }
            return n;
        }
    }
}
