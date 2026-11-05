using System;
using System.Collections.Generic;
using System.Linq;
using Comet.Core;
using EFT;
using SAIN.Components;
using SAIN.SAINComponent.Classes.EnemyClasses;
using SAIN.SAINComponent.SubComponents.CoverFinder;
using UnityEngine;
using UnityEngine.AI;

namespace Comet.Client;

internal sealed class KnownThreat
{
    internal string Id = "";
    internal Vector3 Position;
    internal double Age;
    internal bool Visible;
}
internal sealed class ExposureProbe
{
    internal float At;
    internal Vector3 Origin;
    internal CoverPoint? Cover;
    internal List<KnownThreat> Threats = new List<KnownThreat>();
    internal double Current, CoverEnd, CoverRoute, PushRoute;
    internal bool PushComplete;
    internal float PushLength;
    internal int Rays;
    internal double Milliseconds;
    internal int CoverPathSignature, PushPathSignature;
    internal double CoverLength, CoverVertical, CoverStartError;
    internal int CoverCorners;
    internal double[] CoverSamples = Array.Empty<double>();
}

internal static class VisibleAdapter
{
    private static int probeFrame = -1;
    private static int rays;
    private static readonly Vector3[] validationCorners = new Vector3[128];
    internal static bool WeaponReady(BotComponent bot)
    {
        var w = bot.BotOwner.WeaponManager;
        return w != null && w.HaveBullets && !w.Reload.Reloading && !w.Selector.IsChanging
            && !bot.Player.HandsController.IsInInteractionStrictCheck();
    }
    internal static bool ValidCover(BotComponent bot, CoverPoint? p)
    {
        if (p == null || p.CoverData.IsBad || p.Spotted) return false;
        if (ReferenceEquals(p, bot.Cover.CoverInUse) && (p.Position - bot.Position).sqrMagnitude <= 4) return true;
        return (p.PathData.TimeSinceLastUpdated <= 3 || Plugin.RetainedCover(bot, p)) && ValidPath(p.PathToPoint, bot.Position, p.Position);
    }
    internal static bool ValidPath(NavMeshPath? path, Vector3 start, Vector3 end)
    {
        if (path == null || path.status != NavMeshPathStatus.PathComplete) return false;
        int count = path.GetCornersNonAlloc(validationCorners);
        return count >= 2 && count < validationCorners.Length && (validationCorners[0] - start).sqrMagnitude <= 16
            && (validationCorners[count - 1] - end).sqrMagnitude <= 4;
    }
    internal static CoverPoint? SelectCover(BotComponent bot, PlanProgress? progress = null)
    {
        // Respect an existing destination. FindCoverPoint is patched only when COMET owns the choice.
        var owned = bot.Cover.CoverInUse ?? bot.Cover.CoverPoint_MovingTo;
        if (owned != null && progress?.Blocked(ProgressAdapter.CoverKey(owned),Time.time)!=true && ValidCover(bot, owned)) return owned;
        CoverPoint? best = null; float length = float.MaxValue;
        int checkedPoints = 0;
        foreach (var p in bot.Cover.CoverPoints)
        {
            if (++checkedPoints > 24) break;
            if (!ValidCover(bot, p) || progress?.Blocked(ProgressAdapter.CoverKey(p),Time.time)==true || p.PathData.PathLength >= length) continue;
            best = p; length = p.PathData.PathLength;
        }
        return best;
    }
    internal static VisibleSituation Capture(BotComponent bot, Enemy enemy, Situation contact, BotState state, CometConfig config)
    {
        var v = new VisibleSituation { Contact = contact, WeaponReady = WeaponReady(bot),
            CanShoot = enemy.CanShoot && contact.KnownDistance <= bot.Info.WeaponInfo.EffectiveWeaponDistance * 1.25,
            RecentHit = bot.Medical.TimeSinceShot < 1.5f, RecentHitAt = Time.time - bot.Medical.TimeSinceShot };
        if (!contact.HasContact || double.IsNaN(contact.KnownDistance) || double.IsInfinity(contact.KnownDistance)
            || contact.ContactAge < 0 || double.IsNaN(contact.ContactAge) || contact.ContactAge > config.ContactLifetime) return v;
        var threats = new List<KnownThreat>();
        void Add(Enemy e)
        {
            if(!ContactSafety.Allows(bot,e))return;
            var p = e.KnownPlaces.LastKnownPosition;
            float age = e.TimeSinceLastKnownUpdated;
            if (!p.HasValue || !Finite(p.Value) || age < 0 || float.IsNaN(age) || age > config.ContactLifetime) return;
            threats.Add(new KnownThreat { Id = e.EnemyProfileId, Position = p.Value, Age = age, Visible = e.IsVisible });
        }
        Add(enemy);
        int scanned = 0;
        foreach (var e in bot.EnemyController.KnownEnemies)
        {
            if (++scanned > 32) { v.ThreatsTruncated = true; break; }
            if (e == null || e == enemy) continue;
            Add(e);
        }
        v.ThreatCount = threats.Count;
        if (threats.Count == 0) return v;
        v.ThreatsTruncated |= threats.Count > 4;
        var nearest = threats.Skip(1).OrderBy(t => (t.Position - bot.Position).sqrMagnitude).Take(3).ToList();
        if (threats.Count > 0) nearest.Insert(0, threats[0]);
        threats = nearest;
        v.ThreatSignature = string.Join("|", threats.Select(t => t.Id + (t.Visible ? ":visible" : ":known")).OrderBy(t => t)) + "/" + v.ThreatCount;
        var cover = SelectCover(bot,state.ProgressUnavailable?null:state.Progress);
        v.CoverId = cover?.HardData.Id.ToString() ?? "";
        bool push = ValidPath(enemy.Path.PathToEnemy, bot.Position, enemy.KnownPlaces.LastKnownPosition ?? bot.Position);
        v.PushPathLength = Math.Max(0, enemy.Path.PathLength);
        int coverSignature = PathSignature(cover?.PathToPoint), pushSignature = PathSignature(enemy.Path.PathToEnemy);
        var cache = state.Probe;
        bool same = cache != null && Time.time - cache.At <= .75f && (cache.Origin - bot.Position).sqrMagnitude < (config.CoverResponse.Enabled ? .25f : 4)
            && cache.CoverPathSignature == coverSignature && cache.PushPathSignature == pushSignature
            && cache.Cover == cover && cache.PushComplete == push && Math.Abs(cache.PushLength - v.PushPathLength) < 2
            && cache.Threats.Count == threats.Count && cache.Threats.Zip(threats, (a, b) => a.Id == b.Id && a.Visible == b.Visible
                && (a.Position - b.Position).sqrMagnitude < 4).All(x => x);
        if (!same)
        {
            // At most one complete probe globally per frame, at most 28 terrain rays. No scene scan or new path search.
            // A deferred bot yields to SAIN, never ranks using a partially measured route.
            if (probeFrame == Time.frameCount || Time.time < state.NextExposureAttempt) return v;
            state.NextExposureAttempt = Time.time + .05f;
            probeFrame = Time.frameCount;
            long started = System.Diagnostics.Stopwatch.GetTimestamp(); rays = 0;
            cache = new ExposureProbe { At = Time.time, Origin = bot.Position, Cover = cover, Threats = threats, PushComplete = push, PushLength = (float)v.PushPathLength };
            cache.CoverPathSignature = coverSignature; cache.PushPathSignature = pushSignature;
            cache.Current = Exposure(bot.Position, threats, config.ContactLifetime);
            if (cover != null)
            {
                cache.CoverEnd = Exposure(cover.Position, threats, config.ContactLifetime);
                var corners = cover.PathToPoint?.corners;
                cache.CoverRoute = RouteRisk(corners, bot.Position, cover.Position, threats, config.ContactLifetime, cache.CoverEnd,
                    out var samples, config.CoverResponse.Enabled);
                cache.CoverSamples = new[] { cache.Current, samples[0], samples[1], cache.CoverEnd };
                cache.CoverCorners = corners?.Length ?? 0;
                if (corners != null && corners.Length >= 2 && corners.Length < 128)
                {
                    cache.CoverStartError = Vector3.Distance(bot.Position, corners[0]);
                    for (int i = 1; i < corners.Length; i++)
                    { cache.CoverLength += Vector3.Distance(corners[i - 1], corners[i]); cache.CoverVertical += Math.Abs(corners[i].y - corners[i - 1].y); }
                }
            }
            else { cache.CoverEnd = cache.CoverRoute = 1; }
            cache.PushRoute = push ? RouteRisk(EnemyPathCorners.Read(enemy.Path), bot.Position, threats[0].Position, threats, config.ContactLifetime,
                Exposure(threats[0].Position, threats, config.ContactLifetime), out _) : 1;
            cache.Rays = rays; cache.Milliseconds = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000d / System.Diagnostics.Stopwatch.Frequency;
            state.Probe = cache;
        }
        state.SelectedCover = cover;
        v.ProbeReady = true; v.CoverReachable = cover != null; v.PushReachable = push;
        v.CurrentExposure = cache!.Current; v.CoverExposure = cache.CoverEnd; v.CoverRouteRisk = cache.CoverRoute; v.PushRouteRisk = cache.PushRoute;
        if (config.CoverResponse.Enabled && cover != null)
        {
            var movement = bot.Player.MovementContext;
            v.CoverTravel = new CoverTravel { Complete = cache.CoverCorners >= 2 && cache.CoverCorners < 128 && ValidPath(cover.PathToPoint, bot.Position, cover.Position),
                Corners = cache.CoverCorners, PathLength = cache.CoverLength, VerticalTravel = cache.CoverVertical,
                StartError = cache.CoverStartError + Vector3.Distance(bot.Position, cache.Origin), Exposure = cache.CoverSamples,
                DoorBusy = bot.BotOwner.DoorOpener.Interacting, WalkSpeed = movement.MaxSpeed, SprintSpeed = movement.SprintSpeed,
                Stamina = bot.Player.Physical.Stamina.NormalValue,
                CanSprint = movement.CanSprint && bot.BotOwner.CanSprintPlayer && bot.Cover.CoverInUse == null };
        }
        return v;
    }
    private static int PathSignature(NavMeshPath? path)
    {
        if (path == null || path.status != NavMeshPathStatus.PathComplete) return 0;
        int count = path.GetCornersNonAlloc(validationCorners);
        if (count < 2 || count >= validationCorners.Length) return 0;
        unchecked {
            int hash = count;
            for (int i = 0; i < count; i++) hash = hash * 31 + validationCorners[i].GetHashCode();
            return hash;
        }
    }
    private static double Exposure(Vector3 destination, List<KnownThreat> threats, double lifetime)
    {
        double safe = 1;
        foreach (var threat in threats)
        {
            var origin = threat.Position + Vector3.up * 1.5f;
            var delta = destination + Vector3.up - origin;
            if (delta.sqrMagnitude >= 1) rays++;
            bool exposed = delta.sqrMagnitude < 1 || !Physics.Raycast(origin, delta.normalized, delta.magnitude,
                LayersMaskController.HighPolyWithTerrainMask, QueryTriggerInteraction.Ignore);
            // Last-known contacts represent uncertainty, never a current hidden position.
            double confidence = threat.Visible ? 1 : .5 * Math.Max(0, 1 - threat.Age / lifetime);
            if (exposed) safe *= 1 - confidence;
        }
        return 1 - safe;
    }
    private static bool Finite(Vector3 p) => !float.IsNaN(p.x) && !float.IsInfinity(p.x) && !float.IsNaN(p.y)
        && !float.IsInfinity(p.y) && !float.IsNaN(p.z) && !float.IsInfinity(p.z);
    private static double RouteRisk(ReadOnlySpan<Vector3> corners, Vector3 origin, Vector3 destination, List<KnownThreat> threats, double lifetime, double endExposure,
        out double[] samples, bool sampleShortRoute = false)
    {
        samples = new[] { 1d, 1d };
        bool close = (destination - origin).sqrMagnitude < 1;
        if (close && !sampleShortRoute) return 0;
        if (corners.Length < 2 || corners.Length >= 128) return 1;
        float length = 0;
        for (int i = 1; i < corners.Length; i++) length += Vector3.Distance(corners[i - 1], corners[i]);
        double sum = endExposure;
        int sampleIndex = 0;
        foreach (float fraction in new[] { 1f / 3, 2f / 3 })
        {
            float remaining = length * fraction; var sample = destination;
            for (int i = 1; i < corners.Length; i++)
            {
                float leg = Vector3.Distance(corners[i - 1], corners[i]);
                if (remaining <= leg) { sample = Vector3.Lerp(corners[i - 1], corners[i], leg > .001f ? remaining / leg : 0); break; }
                remaining -= leg;
            }
            samples[sampleIndex] = Exposure(sample, threats, lifetime);
            sum += samples[sampleIndex++];
        }
        // Approximate exposed travel time at 3.5 m/s. A short crossing is safer than a long exposed run.
        return close ? 0 : Math.Min(1, sum / 3 * length / 14);
    }
}
