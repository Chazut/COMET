using System;
using System.Collections.Generic;

namespace Comet.Core;

// Own equipment only. Never contains references to Unity objects or an opponent's equipment.
public sealed class LoadoutTrace
{
    public string State = "unknown", WeaponId = "", Weapon = "", WeaponClass = "", Profile = "", Optic = "", Ammo = "";
    public double At, ResourceAt, IdealRange, Cqb, Long, ScoreCap, SampleMilliseconds;
    public int LoadedRounds, CompatibleMagazines, LooseRounds, Grenades, MedicalItems, ItemsScanned;
    public bool ResourcesKnown, CanFirstAid, CanSurgery, Ready;
    public string RefreshReason = "", ResourceReason = "";
    public LoadoutTrace Copy() => (LoadoutTrace)MemberwiseClone();
    // Resource refresh scheduling is not a tactical weapon change.
    public string PlanKey => WeaponId + "/" + Profile + "/" + Optic + "/" + Ammo;
    public static LoadoutTrace Pending(LoadoutTrace? cached, string weaponId, double now, string reason)
    {
        var p = cached != null && cached.WeaponId == weaponId ? cached.Copy() : new LoadoutTrace { WeaponId = weaponId };
        p.State = "refresh-pending"; p.At = now; p.RefreshReason = reason;
        p.ResourcesKnown = p.Ready = p.CanFirstAid = p.CanSurgery = false;
        p.LoadedRounds = p.CompatibleMagazines = p.LooseRounds = p.Grenades = p.MedicalItems = 0;
        return p;
    }
}

public sealed class LoadoutRefresh
{
    public string Reason { get; private set; } = "initial";
    private string weapon = "", ammo = "";
    private double next, earliest;
    private bool dirty = true;
    public void Invalidate(string reason) { dirty = true; Reason = reason; }
    public bool NeedsRefresh(double now, string weaponId, string ammoId)
    {
        if (!Finite(now)) return false;
        if (weapon != weaponId || ammo != ammoId) { dirty = true; Reason = "weapon-or-ammo-changed"; }
        return (dirty || now >= next) && now >= earliest;
    }
    public bool IsFresh(double now, string weaponId, string ammoId) => Finite(now) && !dirty && now < next && weapon == weaponId && ammo == ammoId;
    public void Complete(double now, string weaponId, string ammoId, double interval)
    { weapon = weaponId; ammo = ammoId; dirty = false; next = now + interval; earliest = now + .25; Reason = "periodic-check"; }
    public void Failed(double now) { dirty = true; earliest = now + 1; Reason = "retry-after-read-error"; }
    private static bool Finite(double n) => !double.IsNaN(n) && !double.IsInfinity(n);
}

public static class LoadoutModel
{
    public static bool UsableMedicalSupply(double remaining, bool carried, bool hasTreatmentTarget, bool canApply) =>
        Finite(remaining) && remaining > 0 && carried && hasTreatmentTarget && canApply;
    public static bool UsableSpareMagazine(bool fastAccess, bool fitsWeapon, int rounds, bool compatibleAmmo) =>
        fastAccess && fitsWeapon && rounds > 0 && compatibleAmmo;
    public static LoadoutTrace Classify(string weaponClass, bool bolt, bool auto, int rpm, bool optic, bool dot, string ammoKind, LoadoutConfig c)
    {
        var p = new LoadoutTrace { WeaponClass = weaponClass, Optic = optic ? (dot ? "scope-and-dot" : "scope") : dot ? "dot" : "iron", State = "ready" };
        // Do not guess an assault rifle for a missing or custom class.
        bool recognized = weaponClass == "pistol" || weaponClass == "smg" || weaponClass == "shotgun" || weaponClass == "assaultRifle"
            || weaponClass == "assaultCarbine" || weaponClass == "machinegun" || weaponClass == "marksmanRifle" || weaponClass == "sniperRifle";
        if (!recognized) { p.State = "unknown-weapon"; return p; }
        bool longGun = bolt || weaponClass == "sniperRifle", dmr = weaponClass == "marksmanRifle";
        // A dot alongside a scope gives a usable close sight: use the less restrictive tactical profile.
        bool scopeOnly = optic && !dot;
        p.Profile = longGun ? (scopeOnly ? "BoltScope" : "BoltIron") : dmr ? (scopeOnly ? "DmrScope" : "DmrIron")
            : weaponClass == "pistol" ? "Pistol" : weaponClass == "smg" ? "Smg"
            : weaponClass == "shotgun" ? (ammoKind == "slug" ? "ShotgunSlug" : "Shotgun")
            : scopeOnly ? "RifleScope" : dot ? "RifleDot" : "RifleIron";
        p.IdealRange = c.Ranges[p.Profile];
        p.Cqb = longGun ? 0 : weaponClass == "smg" || weaponClass == "shotgun" ? 1 : auto ? (rpm >= 700 ? .9 : .7) : dmr ? .25 : .45;
        p.Long = longGun ? (scopeOnly ? 1 : .55) : dmr ? (scopeOnly ? .8 : .55) : scopeOnly ? .5
            : weaponClass == "smg" || weaponClass == "shotgun" || weaponClass == "pistol" ? .05 : .25;
        if (p.Profile == "ShotgunSlug") { p.Cqb = .65; p.Long = .25; }
        return p;
    }

    public static void Adjust(List<Candidate> choices, Situation s, LoadoutConfig c)
    {
        foreach (var a in choices) { a.ScoreBeforeLoadout = a.Score; a.LoadoutDelta = 0; a.LoadoutReason = "loadout-neutral"; }
        var p = s.Loadout;
        if (!c.Enabled || !s.IsAi || !s.IsPmc || !s.IsAuthority || !s.IsActive || s.Ghost || p == null || p.State != "ready" || !p.Ready
            || !Finite(p.IdealRange) || p.IdealRange <= 0 || !Unit(p.Cqb) || !Unit(p.Long)
            || !Finite(s.KnownDistance) || s.KnownDistance < 0 || !Finite(p.At) || s.Now < p.At || s.Now - p.At > c.RefreshSeconds) return;
        double ratio = s.KnownDistance / p.IdealRange;
        double near = Smooth((.5 - ratio) / .5), far = Smooth((ratio - 1.6) / 1.4), fit = 1 - Math.Max(near, far);
        foreach (var a in choices)
        {
            if (!a.Eligible) { a.LoadoutReason = "loadout-native-refusal-kept"; continue; }
            double factor = 0; string reason = "loadout-neutral";
            switch (a.Decision)
            {
                case "Push": factor = .65 * p.Cqb - .65 * p.Long - .6 * near * p.Long - .35 * far; reason = "loadout-close-weapon-fit"; break;
                case "Shoot": factor = .5 * fit - .35 * far * (1 - p.Long); reason = "loadout-shooting-range"; break;
                case "Hold": factor = .5 * p.Long * fit; reason = "loadout-hold-suitable-range"; break;
                case "Search": factor = -.25 * p.Long; reason = "loadout-long-weapon-caution"; break;
                case "Cover": case "FallBack":
                    factor = .65 * near * p.Long + (a.Decision == "Cover" ? .25 * far : 0);
                    reason = "loadout-protected-distance";
                    if (s.Hurt && p.ResourcesKnown && (p.CanFirstAid || p.CanSurgery)) { factor += .25; reason += "+treatable-injury"; }
                    break;
                case "Grenade":
                    if (p.ResourcesKnown && p.Grenades > 0) { factor = .3 * Math.Min(p.Grenades, c.GrenadeStockCap) / c.GrenadeStockCap; reason = "loadout-usable-grenade-stock"; }
                    break;
            }
            double delta = Math.Max(-c.ScoreCap, Math.Min(c.ScoreCap, factor * c.ScoreCap));
            a.Score = Math.Round(Math.Max(0, a.Score + delta), 2);
            a.LoadoutDelta = Math.Round(a.Score - a.ScoreBeforeLoadout, 2); a.LoadoutReason = reason;
        }
    }
    private static double Smooth(double v) { v = Math.Max(0, Math.Min(1, v)); return v * v * (3 - 2 * v); }
    private static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
    private static bool Unit(double v) => Finite(v) && v >= 0 && v <= 1;
}
