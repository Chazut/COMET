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
        // zzap LoadoutProfile: scopes take precedence, including a scope plus a red dot.
        bool sniper = weaponClass == "sniperRifle", dmr = weaponClass == "marksmanRifle";
        bool longGun = bolt || sniper;
        p.Profile = sniper || dmr && bolt ? (optic ? "BoltScope" : "BoltIron") : dmr ? (optic ? "DmrScope" : "DmrIron")
            : weaponClass == "pistol" ? "Pistol" : weaponClass == "smg" ? "Smg"
            : weaponClass == "shotgun" ? "Shotgun"
            : optic ? "RifleScope" : dot ? "RifleDot" : "RifleIron";
        p.IdealRange = c.Ranges[p.Profile];
        if (bolt && !sniper && !dmr) p.IdealRange = Math.Max(p.IdealRange, optic ? 60 : 35);
        p.Cqb = bolt ? 0 : weaponClass == "smg" || weaponClass == "shotgun" ? 1 : auto ? (rpm >= 700 ? .9 : .7) : dmr || sniper ? .25 : .45;
        p.Long = longGun && optic ? 1 : dmr && optic ? .8 : longGun || dmr ? .55 : optic ? .5
            : weaponClass == "smg" || weaponClass == "shotgun" || weaponClass == "pistol" ? .05 : .25;
        return p;
    }

    private static double Smooth(double v) { v = Math.Max(0, Math.Min(1, v)); return v * v * (3 - 2 * v); }
    private static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
    private static bool Unit(double v) => Finite(v) && v >= 0 && v <= 1;
}
