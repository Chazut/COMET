using System;
using System.Collections.Generic;

namespace Comet.Core;

public sealed class FloorHearingConfig
{
    public bool Enabled { get; set; } = true;
    public double OneFloorOther { get; set; } = .6;
    public double SeveralFloorsOther { get; set; } = .4;
    public double OneFloorGunfire { get; set; } = 1;
    public double SeveralFloorsGunfire { get; set; } = .9;
    public double MinimumHeight { get; set; } = 2.5;
    public void Validate()
    {
        CometConfig.Range(OneFloorOther, .1, 1, nameof(OneFloorOther));
        CometConfig.Range(SeveralFloorsOther, .1, OneFloorOther, nameof(SeveralFloorsOther));
        CometConfig.Range(OneFloorGunfire, .1, 1, nameof(OneFloorGunfire));
        CometConfig.Range(SeveralFloorsGunfire, .1, OneFloorGunfire, nameof(SeveralFloorsGunfire));
        CometConfig.Range(MinimumHeight, 2, 5, nameof(MinimumHeight));
    }
}

public readonly struct AcousticPoint
{
    public readonly double X, Y, Z;
    public AcousticPoint(double x, double y, double z) { X = x; Y = y; Z = z; }
    public bool Finite => FloorHearing.Finite(X) && FloorHearing.Finite(Y) && FloorHearing.Finite(Z);
    public double DistanceSquared(AcousticPoint p) => (X-p.X)*(X-p.X)+(Y-p.Y)*(Y-p.Y)+(Z-p.Z)*(Z-p.Z);
}
public readonly struct AcousticHit
{
    public readonly double Y, NormalY, Distance;
    public readonly bool Structural;
    public AcousticHit(double y, double normalY, double distance, bool structural)
    { Y = y; NormalY = normalY; Distance = distance; Structural = structural; }
    public bool Horizontal => Structural && FloorHearing.Finite(Y) && FloorHearing.Finite(NormalY) && Math.Abs(NormalY) >= .9;
}
public interface IFloorAcoustics
{
    // Negative count = budget exhausted/uncertain. Full buffer = uncertain, never truncated proof.
    int Cast(AcousticPoint from, AcousticPoint to, AcousticHit[] hits);
}
public readonly struct FloorGeometry
{
    public readonly int Floors;
    public readonly string Reason;
    public FloorGeometry(int floors, string reason) { Floors = floors; Reason = reason; }
}

// Conservative local evidence, not a room graph or a complete acoustic propagation simulation.
// No live actor position, game objects, visibility or pathfinding is required by this policy.
public sealed class FloorGeometryProbe
{
    private readonly AcousticHit[] direct = new AcousticHit[24], a = new AcousticHit[24], b = new AcousticHit[24], route = new AcousticHit[24];
    private static bool Usable(int n, AcousticHit[] hits) => n >= 0 && n < hits.Length;
    public FloorGeometry Probe(IFloorAcoustics world, AcousticPoint listener, AcousticPoint emission)
    {
        FloorGeometry Unknown(string why) => new FloorGeometry(0, why);
        if (!listener.Finite || !emission.Finite) return Unknown("floor-invalid-geometry");
        var source = new AcousticPoint(emission.X, emission.Y + .3, emission.Z);
        int count = world.Cast(listener, source, direct);
        if (!Usable(count, direct)) return Unknown("floor-probe-incomplete");
        if (count == 0) return Unknown("floor-open-direct");
        int closest = 0;
        for (int i = 1; i < count; i++) if (direct[i].Distance < direct[closest].Distance) closest = i;
        // A wall, terrain, moving object, stairs' riser or steep slope does not prove a floor.
        if (!direct[closest].Horizontal) return Unknown("floor-obstacle-uncertain");
        double low = Math.Min(listener.Y, source.Y) + .35, high = Math.Max(listener.Y, source.Y) + .2;
        double dx = source.X-listener.X, dz = source.Z-listener.Z;
        double span = Math.Sqrt(dx*dx+dz*dz);
        // If vertically aligned, use two columns a metre apart to avoid treating a small prop as a slab.
        var columnA = span >= 1 ? listener : new AcousticPoint(listener.X - .6, listener.Y, listener.Z);
        var columnB = span >= 1 ? source : new AcousticPoint(source.X + .6, source.Y, source.Z);
        int na = world.Cast(new AcousticPoint(columnA.X, high, columnA.Z), new AcousticPoint(columnA.X, low, columnA.Z), a);
        int nb = world.Cast(new AcousticPoint(columnB.X, high, columnB.Z), new AcousticPoint(columnB.X, low, columnB.Z), b);
        if (!Usable(na,a) || !Usable(nb,b)) return Unknown("floor-probe-incomplete");
        int floors = 0; double first = double.NaN; bool directMatched = false;
        for (int i = 0; i < na; i++)
        {
            if (!a[i].Horizontal || a[i].Y < low || a[i].Y > high) continue;
            for (int j = 0; j < nb; j++)
            {
                if (!b[j].Horizontal || Math.Abs(a[i].Y-b[j].Y) > .25) continue;
                if (Math.Abs(a[i].Y-direct[closest].Y) <= .35) directMatched = true;
                if (floors == 0) { first = a[i].Y; floors = 1; }
                else if (Math.Abs(first-a[i].Y) >= 1.8) floors = 2;
                break;
            }
        }
        if (floors == 0 || !directMatched) return Unknown("floor-columns-disagree");
        // Two short bent routes can reveal an adjacent open stairwell/atrium. Unknown routes fail open.
        double px = span > .1 ? -dz/span : 1, pz = span > .1 ? dx/span : 0;
        for (int sign = -1; sign <= 1; sign += 2)
        {
            var corner = new AcousticPoint((source.X+listener.X)/2 + sign*2*px, (source.Y+listener.Y)/2, (source.Z+listener.Z)/2 + sign*2*pz);
            int n = world.Cast(listener, corner, route);
            if (!Usable(n,route)) return Unknown("floor-probe-incomplete");
            if (n != 0) continue;
            n = world.Cast(corner, source, route);
            if (!Usable(n,route)) return Unknown("floor-probe-incomplete");
            if (n == 0) return Unknown("floor-open-nearby-route");
        }
        return new FloorGeometry(floors, floors == 1 ? "floor-one-confirmed" : "floor-several-confirmed");
    }
}

public sealed class FloorHearingFrame
{
    public bool Scoped = true, NativeAudible = true, Priority, Supported = true, Gunfire;
    public double Distance, NativeRange, Environment = 1, Occlusion = 1;
    public FloorGeometry Geometry;
}
public sealed class FloorHearingTrace
{
    public string State { get; set; } = "native";
    public string Reason { get; set; } = "floor-native";
    public string Source { get; set; } = "";
    public string Kind { get; set; } = "";
    public int Floors { get; set; }
    public double NativeRange { get; set; }
    public double FinalRange { get; set; }
    public double Distance { get; set; }
    public double Environment { get; set; }
    public double Occlusion { get; set; }
    public double TargetFactor { get; set; } = 1;
    public double AppliedFactor { get; set; } = 1;
    public double EmittedAt { get; set; }
    public bool NativeAudible { get; set; }
    public bool Allowed { get; set; }
    public bool Cached { get; set; }
    public string Geometry { get; set; } = "not-probed";
}
public static class FloorHearing
{
    public static bool Finite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);
    public static FloorHearingTrace Evaluate(FloorHearingConfig c, FloorHearingFrame f)
    {
        var t = new FloorHearingTrace { NativeRange = f.NativeRange, FinalRange = f.NativeRange, Distance = f.Distance,
            Environment = f.Environment, Occlusion = f.Occlusion, NativeAudible = f.NativeAudible, Allowed = f.NativeAudible,
            Floors = f.Geometry.Floors, Geometry = f.Geometry.Reason ?? "not-probed" };
        FloorHearingTrace Native(string reason) { t.Reason = reason; return t; }
        if (!c.Enabled || !f.Scoped) return Native("floor-out-of-scope");
        if (!Finite(f.NativeRange) || f.NativeRange <= 0 || !Finite(f.Distance) || f.Distance < 0
            || !Finite(f.Environment) || f.Environment <= 0 || f.Environment > 1 || !Finite(f.Occlusion) || f.Occlusion <= 0 || f.Occlusion > 1)
            return Native("floor-invalid-signal");
        if (!f.NativeAudible || f.Distance > f.NativeRange) { t.Allowed = false; return Native("floor-native-inaudible"); }
        if (!f.Supported || f.Priority) return Native(f.Priority ? "floor-combat-priority" : "floor-sound-preserved");
        if (f.Geometry.Floors < 1 || f.Geometry.Floors > 2) return Native(f.Geometry.Reason ?? "floor-uncertain");
        t.TargetFactor = f.Gunfire ? (f.Geometry.Floors == 1 ? c.OneFloorGunfire : c.SeveralFloorsGunfire)
            : (f.Geometry.Floors == 1 ? c.OneFloorOther : c.SeveralFloorsOther);
        // Credit native environment (including bunker) and obstruction attenuation together.
        // Never cancel their effects or increase native range. No second blind multiplication.
        double nativeBarrier = f.Environment * f.Occlusion;
        t.AppliedFactor = Math.Min(1, t.TargetFactor / nativeBarrier);
        t.FinalRange = f.NativeRange * t.AppliedFactor;
        t.Allowed = f.Distance <= t.FinalRange;
        t.State = !t.Allowed ? "blocked" : t.AppliedFactor < 1 ? "attenuated" : "native";
        t.Reason = t.AppliedFactor == 1 ? "floor-native-already-attenuated" : !t.Allowed ? "floor-range-cut" : "floor-range-reduced";
        return t;
    }
}

// Bounded, short-lived value-only cache. No Unity object can survive a raid through this cache.
public sealed class FloorGeometryCache
{
    private sealed class Entry { public AcousticPoint Listener, Source; public double At; public FloorGeometry Result; }
    private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>();
    private readonly Queue<string> order = new Queue<string>();
    public int Count => entries.Count;
    public bool TryGet(string key, double now, AcousticPoint listener, AcousticPoint source, out FloorGeometry result)
    {
        result = default;
        if (!entries.TryGetValue(key, out var e) || now < e.At || now-e.At >= .25
            || listener.DistanceSquared(e.Listener) > .0625 || source.DistanceSquared(e.Source) > .0625) return false;
        result = e.Result; return true;
    }
    public void Put(string key, double now, AcousticPoint listener, AcousticPoint source, FloorGeometry result)
    {
        if (result.Reason == "floor-probe-incomplete") return;
        if (!entries.ContainsKey(key)) { if (entries.Count >= 128) entries.Remove(order.Dequeue()); order.Enqueue(key); }
        entries[key] = new Entry { Listener = listener, Source = source, At = now, Result = result };
    }
    public void Clear() { entries.Clear(); order.Clear(); }
}

public sealed class AcousticWorkBudget
{
    private int frame = -1, casts;
    private double spent;
    public void Begin(int currentFrame) { if (frame != currentFrame) { frame = currentFrame; casts = 0; spent = 0; } }
    public bool TryCast(double currentProbeMilliseconds)
    { if (casts >= 32 || !FloorHearing.Finite(currentProbeMilliseconds) || spent + currentProbeMilliseconds >= .5) return false; casts++; return true; }
    public void Charge(double milliseconds) { spent += Math.Max(0, milliseconds); }
    public void Clear() { frame = -1; casts = 0; spent = 0; }
}
