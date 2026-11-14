using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using EFT;
using HarmonyLib;
using SAIN.Components;
using SAIN.Components.PlayerComponentSpace;
using SAIN.Types.Jobs;
using Unity.Collections;
using UnityEngine;

namespace Comet.Client;

public sealed partial class Plugin
{
    private static Action<PlayerComponent, NativeArray<RaycastHit>, List<Vector3>> addNativeLaserPoints = null!;
    private readonly Dictionary<LightDetectionClass, List<Vector3>> lightBeamPoints = new();
    private readonly Stack<List<Vector3>> lightPointPool = new();
    private readonly Dictionary<string, float> lightDetailAt = new();
    private int lightJobs, lightRays, lightClear, lightBlocked, lightLaserPoints;
    private float lightSummaryAt;
    private bool lightDetectionWarned;

    private static void PatchLightDetection(Harmony harmony)
    {
        // Both official SAIN 4.5.1 and pinned zzap contain this unused helper.
        // Call the source implementation, retaining its 75 m length, 5 m spacing
        // and truncation at the first laser hit.
        addNativeLaserPoints = AccessTools.MethodDelegate<Action<PlayerComponent, NativeArray<RaycastHit>, List<Vector3>>>(
            AccessTools.Method(typeof(FlashlightRaycastJob), "AddLaserPathPoints"));
        harmony.Patch(AccessTools.Method(typeof(FlashlightRaycastJob), "ReadFlashlightJobData"),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(CaptureLightBeamPoints)));
        harmony.Patch(AccessTools.Method(typeof(FlashlightRaycastJob), "CreateLightDetectionJobs"),
            transpiler: new HarmonyMethod(typeof(Plugin), nameof(LightDetectionJobs)));
        harmony.Patch(AccessTools.Method(typeof(FlashlightRaycastJob), "ReadLightDetectionJobData"),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(ObserveLightDetection)));
        harmony.Patch(AccessTools.Method(typeof(FlashlightRaycastJob), "Stop"),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(StopLightDetection)));
    }

    private bool LightDetectionScope(PlayerComponent? player)
    {
        var bot = player?.BotComponent;
        return world != null && config?.Enabled == true && Authority && bot != null
            && NativeAdapter.IsSupportedAi(bot) && bot.BotActive && !bot.GameEnding && !Ghost(bot.ProfileId);
    }

    private void RecycleLightPoints()
    {
        foreach (var points in lightBeamPoints.Values)
        {
            points.Clear();
            lightPointPool.Push(points);
        }
        lightBeamPoints.Clear();
    }

    private static void CaptureLightBeamPoints(int __0, List<RaycastJob> ___RaycastJobs)
    {
        var p = Instance;
        if (p == null) return;
        p.RecycleLightPoints();
        if (p.world == null || p.config?.Enabled != true || !p.Authority) return;
        try
        {
            for (int i = 0; i < __0; i++)
            {
                var job = ___RaycastJobs[i];
                if (!GameWorldComponent.TryGetPlayerComponent(job.Owner, out var source)) continue;
                var light = source.Flashlight;
                if (!light.DeviceActive || (!light.Laser && !light.IRLaser)) continue;
                var points = p.lightPointPool.Count > 0 ? p.lightPointPool.Pop() : new List<Vector3>(64);
                p.lightBeamPoints.Add(light.LightDetection, points);
                points.AddRange(light.LightDetection.LightPoints);
                int surfacePoints = points.Count;
                // Native ReadFlashlightJobData has completed the jobs. Never retain
                // their NativeArrays beyond the callback: SAIN disposes them next.
                addNativeLaserPoints(source, job.Hits, points);
                p.lightLaserPoints += points.Count - surfacePoints;
            }
        }
        catch (Exception ex)
        {
            p.RecycleLightPoints();
            p.LightDetectionError(ex);
        }
    }

    private static IEnumerable<CodeInstruction> LightDetectionJobs(IEnumerable<CodeInstruction> instructions)
    {
        var beamCheck = AccessTools.Method(typeof(LightDetectionClass), nameof(LightDetectionClass.CheckIsBeamVisible));
        var constructor = AccessTools.Constructor(typeof(RaycastJob), new[]
        {
            typeof(List<Vector3>), typeof(Vector3), typeof(LayerMask), typeof(IPlayer), typeof(IPlayer)
        });
        int checks = 0, jobs = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(beamCheck))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(Plugin), nameof(CheckLightBeam));
                checks++;
            }
            else if (instruction.opcode == OpCodes.Newobj && Equals(instruction.operand, constructor))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(Plugin), nameof(CreateLightVisibilityJob));
                jobs++;
            }
            yield return instruction;
        }
        if (checks != 1 || jobs != 1)
            throw new InvalidOperationException($"SAIN light detection changed: {checks} beam checks, {jobs} job constructors.");
    }

    private static bool CheckLightBeam(LightDetectionClass observer, FlashLightClass source)
    {
        var p = Instance;
        if (p == null || !p.LightDetectionScope(observer.PlayerComponent)
            || !p.lightBeamPoints.TryGetValue(source.LightDetection, out var points))
            return observer.CheckIsBeamVisible(source);
        // Same native wavelength gate. Do not grant IR vision to a bot without NVG.
        if (!source.WhiteLight && !source.Laser
            && observer.PlayerComponent.Player.AIData?.BotOwner?.NightVision?.UsingNow == false) return false;
        return points.Count > 0;
    }

    private static RaycastJob CreateLightVisibilityJob(List<Vector3> points, Vector3 origin,
        LayerMask mask, IPlayer owner, IPlayer target)
    {
        var p = Instance;
        if (p == null || !GameWorldComponent.TryGetPlayerComponent(owner, out var observer)
            || !p.LightDetectionScope(observer)) return new RaycastJob(points, origin, mask, owner, target);
        var nativePoints = points;
        if (GameWorldComponent.TryGetPlayerComponent(target, out var source)
            && p.lightBeamPoints.TryGetValue(source.Flashlight.LightDetection, out var beam)) points = beam;
        var job = new RaycastJob(points, origin, mask, owner, target);
        try
        {
            var commands = job.Commands;
            for (int i = 0; i < points.Count; i++)
            {
                Vector3 delta = points[i] - origin;
                // Match SAIN's correct Vector3[] overload. Its List<Vector3>
                // overload passes a non-unit direction and a constant 1 m range.
                commands[i] = new RaycastCommand(origin, delta.normalized,
                    new QueryParameters { layerMask = mask }, delta.magnitude);
            }
            p.lightJobs++;
            p.lightRays += points.Count;
            return job;
        }
        catch (Exception ex)
        {
            job.Dispose();
            p.LightDetectionError(ex);
            return new RaycastJob(nativePoints, origin, mask, owner, target);
        }
    }

    private static void ObserveLightDetection(int __0, List<RaycastJob> ___RaycastJobs)
    {
        var p = Instance;
        if (p == null) return;
        try
        {
            for (int i = 0; i < __0; i++)
            {
                var job = ___RaycastJobs[i];
                if (job.Target == null || !GameWorldComponent.TryGetPlayerComponent(job.Owner, out var observer)
                    || !p.LightDetectionScope(observer)) continue;
                bool clear = false;
                for (int j = 0; j < job.Hits.Length; j++)
                    if (job.Hits[j].collider == null) { clear = true; break; }
                if (clear) p.lightClear++; else p.lightBlocked++;
                // The original reader already called TryToInvestigate for a clear
                // ray. This is an observation, never a second contact injection.
                if (p.config?.CombatJournal.Enabled != true) continue;
                if (p.lightDetailAt.TryGetValue(job.Owner.ProfileId, out float next) && Time.time < next) continue;
                p.lightDetailAt[job.Owner.ProfileId] = Time.time + 5;
                var state = p.JournalState(job.Owner);
                if (state == null) continue;
                var light = GameWorldComponent.TryGetPlayerComponent(job.Target, out var source) ? source.Flashlight : null;
                p.JournalWrite(state, "T01", "light-detection",
                    clear ? "native-light-investigation-requested" : "light-points-occluded", new
                    {
                        kind = "light-detection", target = job.Target.ProfileId, rays = job.Hits.Length,
                        clearRay = clear, whiteLight = light?.WhiteLight, laser = light?.Laser,
                        infraredLight = light?.IRLight, infraredLaser = light?.IRLaser,
                        reaction = clear ? "native-search-point" : "none", grantsVisualContact = false
                    });
            }
        }
        catch (Exception ex) { p.LightDetectionError(ex); }
    }

    private void FlushLightDetection(bool force = false)
    {
        if (!force && Time.time < lightSummaryAt) return;
        lightSummaryAt = Time.time + 10;
        if (lightJobs + lightLaserPoints + lightClear + lightBlocked == 0) return;
        diagnostics?.Write(Diagnostics.Serialize(new
        {
            schema = 1, session, feature = "T01", stage = "summary", reason = "light-detection-counters",
            realtime = Time.realtimeSinceStartup, jobs = lightJobs, rays = lightRays,
            laserPathPoints = lightLaserPoints, investigationRequests = lightClear, occluded = lightBlocked
        }));
        lightJobs = lightRays = lightClear = lightBlocked = lightLaserPoints = 0;
    }

    private void LightDetectionError(Exception ex)
    {
        if (lightDetectionWarned) return;
        lightDetectionWarned = true;
        Logger.LogWarning("COMET light detection adapter: " + ex.Message);
    }

    private static void StopLightDetection() => Instance?.ClearLightDetection();

    private void ClearLightDetection()
    {
        FlushLightDetection(true);
        lightBeamPoints.Clear();
        lightPointPool.Clear();
        lightDetailAt.Clear();
        lightSummaryAt = 0;
        lightDetectionWarned = false;
    }
}
