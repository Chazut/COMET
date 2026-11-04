using System;
using System.Collections.Generic;
using System.Linq;

namespace Comet.Core;

// Geometry and own mobility only. No live enemy transform or enemy weapon/health lookup.
public sealed class CoverTravel
{
    public bool Complete, DoorBusy, CanSprint;
    public double PathLength, VerticalTravel, StartError, WalkSpeed, SprintSpeed, Stamina;
    public int Corners;
    // Start, 1/3, 2/3, destination. Union of dated known threats; not a hit probability.
    public double[] Exposure = Array.Empty<double>();
}

public sealed class CoverResponseTrace
{
    public string State { get; set; } = "neutral";
    public string Reason { get; set; } = "cover-response-no-route";
    public string Target { get; set; } = "";
    public string CoverId { get; set; } = "";
    public double At { get; set; }
    public double? PathLength { get; set; }
    public double? Eta { get; set; }
    public double? FastEta { get; set; }
    public double? ExposedSeconds { get; set; }
    public double? FastExposedSeconds { get; set; }
    public double? ExposureFraction { get; set; }
    public int KnownThreats { get; set; }
    public string TravelMode { get; set; } = "unknown";
    public double ScoreCap { get; set; }
    public double ShootDelta { get; set; }
    public double CoverDelta { get; set; }
}

public static class CoverResponseModel
{
    private static bool Finite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);
    private static double Clamp(double x) => Math.Max(0, Math.Min(1, x));
    public static CoverResponseTrace Assess(VisibleSituation v, CoverResponseConfig c)
    {
        var s = v.Contact; var p = v.CoverTravel;
        var t = new CoverResponseTrace { Target = s.Target, CoverId = v.CoverId, At = Finite(s.Now) ? s.Now : 0,
            KnownThreats = Math.Max(0, v.ThreatCount), ScoreCap = c.ScoreCap };
        CoverResponseTrace Refuse(string reason) { t.Reason = reason; return t; }
        if (!c.Enabled || c.ScoreCap == 0) return Refuse("cover-response-disabled");
        if (!s.IsAi || !s.IsPmc || !s.IsAuthority || !s.IsActive || s.Ghost || !s.Visible || !s.HasContact
            || s.Target.Length == 0 || !Finite(s.Now)) return Refuse("cover-response-ineligible");
        if (!v.ProbeReady) return Refuse("cover-response-probe-pending");
        if (!v.CoverReachable || p == null || !p.Complete) return Refuse("cover-response-no-route");
        if (p.DoorBusy) return Refuse("cover-response-door-busy");
        if (!v.WeaponReady) return Refuse("cover-response-weapon-busy");
        if (new[] { p.PathLength, p.VerticalTravel, p.StartError, p.WalkSpeed, p.SprintSpeed, p.Stamina }.Any(x => !Finite(x))
            || p.PathLength < 0 || p.PathLength > 200 || p.VerticalTravel < 0 || p.VerticalTravel > p.PathLength
            || p.StartError < 0 || p.StartError > .75 || p.Corners < 2 || p.Corners >= 128
            || p.WalkSpeed <= .1 || p.SprintSpeed < 0 || p.Stamina < 0 || p.Stamina > 1
            || p.Exposure.Length != 4 || p.Exposure.Any(x => !Finite(x) || x < 0 || x > 1))
            return Refuse("cover-response-invalid-route");
        t.PathLength = p.PathLength;
        if (p.PathLength < .25 || s.InCover) return Refuse("cover-response-already-protected");
        // Conservative walk estimate and fastest plausible sprint bound. Never assume SAIN will sprint immediately.
        double walk = Math.Min(3, p.WalkSpeed) * .8;
        double sprint = Math.Max(walk, Math.Min(6, p.SprintSpeed) * .75);
        double sprintDistance = p.CanSprint && p.Stamina >= .4 ? Math.Min(p.PathLength, (p.Stamina - .3) * 20) : 0;
        double delays = .15 + Math.Max(0, p.Corners - 2) * .3 + p.VerticalTravel * .25 + p.StartError / walk;
        t.Eta = p.PathLength / walk + delays;
        t.FastEta = sprintDistance / sprint + (p.PathLength - sprintDistance) / walk + delays;
        // Upper endpoint of each third: a short unmeasured stretch is never treated as guaranteed protection.
        double fraction = (Math.Max(p.Exposure[0], p.Exposure[1]) + Math.Max(p.Exposure[1], p.Exposure[2])
            + Math.Max(p.Exposure[2], p.Exposure[3])) / 3;
        t.ExposureFraction = fraction;
        t.ExposedSeconds = t.Eta * fraction;
        t.FastExposedSeconds = t.FastEta * fraction;
        t.TravelMode = sprintDistance > 0 && sprint > walk ? "walk-to-possible-sprint" : "walk";
        t.State = "assessed"; t.Reason = "cover-response-balanced";
        return t;
    }

    public static void Adjust(List<Candidate> choices, VisibleSituation v, CometConfig config)
    {
        foreach (var candidate in choices) { candidate.ScoreBeforeCoverResponse = candidate.Score; candidate.CoverResponseDelta = 0; candidate.CoverResponseReason = "cover-response-neutral"; }
        if (!config.CoverResponse.Enabled) { v.CoverResponse = null; return; }
        var c = config.CoverResponse; var s = v.Contact;
        var t = v.CoverResponse = Assess(v, c);
        if (t.State != "assessed") return;
        var cover = choices.FirstOrDefault(x => x.Decision == "Cover" && x.Eligible);
        var shoot = choices.FirstOrDefault(x => x.Decision == "Shoot" && x.Eligible);
        if (cover == null) { t.Reason = "cover-response-cover-unavailable"; return; }
        if (v.ThreatsTruncated || v.ThreatCount != 1) { t.Reason = "cover-response-threat-uncertainty"; return; }
        double a = config.Aggression.TryGetValue(s.Personality, out var aggression) ? aggression : .5;
        double coverDelta = 0, shootDelta = 0;
        if (v.CurrentExposure > v.CoverExposure + .15 && t.Eta <= c.LongExposure && t.ExposedSeconds <= c.ShortExposure)
        {
            coverDelta = c.ScoreCap * (1 - .3 * a) * (1 - .4 * Clamp(t.ExposedSeconds!.Value / c.ShortExposure));
            t.State = "quick-cover"; t.Reason = "cover-response-quick-protection";
        }
        else if (shoot == null || s.LowAmmo || s.Hurt || s.Suppressed || s.Fear?.Panic == true)
            t.Reason = "cover-response-defence-priority";
        else if (v.CurrentExposure > .25 && t.FastExposedSeconds > c.ShortExposure)
        {
            double pressure = Clamp((t.FastExposedSeconds!.Value - c.ShortExposure) / (c.LongExposure - c.ShortExposure));
            shootDelta = c.ScoreCap * pressure * (.6 + .4 * a);
            coverDelta = -shootDelta;
            t.State = "return-fire"; t.Reason = "cover-response-exposed-crossing";
        }
        void Apply(Candidate? candidate, double delta)
        {
            if (candidate == null || delta == 0) return;
            double next = Math.Round(Math.Max(0, candidate.Score + Math.Max(-c.ScoreCap, Math.Min(c.ScoreCap, delta))), 2);
            candidate.CoverResponseDelta = next - candidate.Score; candidate.Score = next; candidate.CoverResponseReason = t.Reason;
        }
        Apply(cover, coverDelta); Apply(shoot, shootDelta);
        t.ShootDelta = shoot?.CoverResponseDelta ?? 0; t.CoverDelta = cover.CoverResponseDelta;
    }
}
