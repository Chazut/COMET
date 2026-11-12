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
    internal bool PushComplete, UnderFire;
    internal double HitAt;
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
            && bot.Player.HandsController != null && !bot.Player.HandsController.IsInInteractionStrictCheck();
    }
    internal static bool ValidCover(BotComponent bot, CoverPoint? p)
    {
        if (p == null || p.CoverData.IsBad || p.Spotted) return false;
        if (Plugin.TryFallbackCover(bot,p,out bool fallbackUsable)) return fallbackUsable;
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
        if (owned != null && progress?.Blocked(ProgressAdapter.CoverKey(owned),Time.time)!=true && ValidCover(bot, owned)
            && Plugin.SelectionCoverUsable(bot, owned, out _)) return owned;
        // Recheck only the cheapest eligible candidate, then the next if refused.
        // Do not perform geometry/path recalculation for every improving entry in
        // an unsorted native list. Keep the same 24-entry bound and no allocations.
        uint rejected = 0;
        for (int attempt = 0; attempt < 24; attempt++)
        {
            CoverPoint? best = null; float length = float.MaxValue;
            int checkedPoints = 0, bestIndex = -1;
            foreach (var p in bot.Cover.CoverPoints)
            {
                int index = checkedPoints++;
                if (index >= 24) break;
                if ((rejected & (1u << index)) != 0 || !ValidCover(bot, p)
                    || progress?.Blocked(ProgressAdapter.CoverKey(p),Time.time)==true || p.PathData.PathLength >= length) continue;
                best = p; length = p.PathData.PathLength; bestIndex = index;
            }
            if (best == null) return Plugin.RequestCoverFallback(bot,progress);
            if (Plugin.SelectionCoverUsable(bot, best, out _)) return best;
            rejected |= 1u << bestIndex;
        }
        return Plugin.RequestCoverFallback(bot,progress);
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
        v.CoverDestination = ProgressAdapter.CoverKey(cover);
        bool push = ValidPath(enemy.Path.PathToEnemy, bot.Position, enemy.KnownPlaces.LastKnownPosition ?? bot.Position);
        v.PushPathLength = Math.Max(0, enemy.Path.PathLength);
        int coverSignature = PathSignature(cover?.PathToPoint), pushSignature = PathSignature(enemy.Path.PathToEnemy);
        var cache = state.Probe;
        // Keep only an action already accepted and executing against the same evidence.
        // No added rays/path searches while the shared probe budget is busy.
        v.ContinuationReason = cache == null ? "no-exposure-cache"
            : Time.time < cache.At || Time.time-cache.At > .75+config.Handoff.Grace ? "exposure-cache-expired"
            : (cache.Origin-bot.Position).sqrMagnitude >= 4 ? "origin-moved"
            : v.ThreatsTruncated ? "threats-truncated"
            : !SameThreats(cache.Threats,threats,4) ? "threat-evidence-changed"
            : state.Feature!="A02" || state.ActiveLabel!="COMET:"+state.Decision ? "not-owned"
            : cache.Threats[0].Id!=contact.Target ? "contact-changed"
            : !bot.SAINLayersActive || bot.Decision.CurrentCombatDecision!=state.Expected ? "native-action-changed"
            : bot.Decision.CurrentSelfDecision.ToString()!="None" || bot.Decision.CurrentSquadDecision.ToString()!="None" ? "native-priority"
            : bot.BotOwner.Medecine?.Using==true || !v.WeaponReady ? "hands-busy"
            : bot.BotOwner.DoorOpener.Interacting || bot.Grenade.GrenadeDangerPoint!=null
                || bot.Decision.DogFightDecision.DogFightActive ? "emergency-or-door"
            : bot.BotOwner.Memory.IsUnderFire&&!cache.UnderFire ? "new-incoming-fire"
            : v.RecentHit && v.RecentHitAt>cache.HitAt+.01 ? "new-hit"
            : state.Decision=="Shoot" ? (v.CanShoot&&bot.CurrentAction?.GetType().Name=="StandAndShootAction"?"":"shot-no-longer-executable")
            : state.Decision!="Cover" ? "action-not-retainable"
            : cover==null || !ReferenceEquals(cover,state.SelectedCover)||!ReferenceEquals(cache.Cover,cover) ? "cover-changed"
            : !ValidCover(bot,cover) || cache.CoverEnd>.5 || cache.CoverRoute>config.VisibleCombat.RiskTolerance ? "cover-no-longer-safe"
            : bot.CurrentAction?.GetType().Name!="SeekCoverAction" ? "cover-action-not-executing"
            : !(cover.BotInThisCover || bot.Mover.ActivePath?.Moving==true
                && (bot.Mover.ActivePath.Destination-cover.Position).sqrMagnitude<=1) ? "cover-route-not-executing"
            : !state.ProgressUnavailable && (state.Progress.Blocked(ProgressAdapter.CoverKey(cover),Time.time)
                ||state.Progress.Current.Reconsider) ? "cover-progress-rejected" : "";
        v.ContinuationSafe = v.ContinuationReason.Length==0;
        bool same = cache != null && Time.time >= cache.At && Time.time - cache.At <= .75f
            && (cache.Origin - bot.Position).sqrMagnitude < (config.CoverResponse.Enabled ? .25f : 4)
            && cache.CoverPathSignature == coverSignature && cache.PushPathSignature == pushSignature
            && cache.Cover == cover && cache.PushComplete == push && Math.Abs(cache.PushLength - v.PushPathLength) < 2
            && SameThreats(cache.Threats,threats,4);
        if (!same)
        {
            // At most one complete probe globally per frame, at most 28 terrain rays. No scene scan or new path search.
            // A deferred bot may continue its verified action briefly; never rank a new route from partial measurements.
            if (probeFrame == Time.frameCount || Time.time < state.NextExposureAttempt)
            { v.ProbePendingReason=probeFrame==Time.frameCount?"shared-frame-budget":"bot-probe-interval";return v; }
            state.NextExposureAttempt = Time.time + .05f;
            probeFrame = Time.frameCount;
            long started = System.Diagnostics.Stopwatch.GetTimestamp(); rays = 0;
            cache = new ExposureProbe { At = Time.time, Origin = bot.Position, Cover = cover, Threats = threats, PushComplete = push, PushLength = (float)v.PushPathLength };
            cache.UnderFire=bot.BotOwner.Memory.IsUnderFire;cache.HitAt=v.RecentHitAt;
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
    private static bool SameThreats(List<KnownThreat> previous,List<KnownThreat> current,float squaredTolerance)
    {
        if(previous.Count!=current.Count||previous.Count==0||previous[0].Id!=current[0].Id)return false;
        // Secondary distance ordering can change without a new contact or changed evidence.
        foreach(var a in previous)
        {
            bool found=false;
            foreach(var b in current)if(a.Id==b.Id&&a.Visible==b.Visible&&(a.Position-b.Position).sqrMagnitude<squaredTolerance){found=true;break;}
            if(!found)return false;
        }
        return true;
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
