using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Comet.Core;

// Enabled only for a performance capture. No Unity references reach the report writer.
internal sealed class RaidSamplingMetrics
{
    private static RaidSamplingMetrics? active;
    private readonly int thread = Environment.CurrentManagedThreadId;
    private readonly Func<int> frameNumber;
    private readonly ConditionalWeakTable<Action<string>, Subscriber> subscriptions = new();
    private readonly List<Subscriber> rows = new();
    private int measurementErrors;

    internal long DoorScans, DoorRoutesCopied, DoorPathCornersCopied;
    internal long GrenadeContactObservations, GrenadeFullEvaluations;
    internal long MedicalLeanGuards, MedicalTacticGuards, MedicalUpdateSamples, MedicalScopeRechecksAvoided;

    internal RaidSamplingMetrics(Func<int> frameNumber) => this.frameNumber = frameNumber;

    internal static RaidSamplingMetrics? Current
    {
        get
        {
            var value = Volatile.Read(ref active);
            return value != null && value.thread == Environment.CurrentManagedThreadId ? value : null;
        }
    }

    internal void Start() => Volatile.Write(ref active, this);
    internal void Stop() => Interlocked.CompareExchange(ref active, null, this);

    private sealed class Subscriber
    {
        internal string Name = "";
        internal long Calls, TotalTicks, MaxTicks, FrameTicks, PreviousTicks, MaxFrameTicks;
        internal int Frame = -1, PreviousFrame = -1, PeakFrame = -1, FrameCalls, PreviousCalls;
    }

    internal void InvokeMeasured(Action<string> callback, string json)
    {
        long start = Stopwatch.GetTimestamp();
        try
        {
            callback(json);
        }
        finally
        {
            long elapsed = Stopwatch.GetTimestamp() - start;
            try
            {
                if (!subscriptions.TryGetValue(callback, out var row))
                {
                    var method = callback.Method;
                    row = new Subscriber
                    {
                        Name = method.Module.Assembly.GetName().Name + "/" + method.DeclaringType?.FullName + "." + method.Name,
                    };
                    subscriptions.Add(callback, row);
                    rows.Add(row);
                }
                int frame = frameNumber();
                if (frame != row.Frame)
                {
                    // FixedUpdate can start this frame before the recorder closes the previous one.
                    row.PreviousFrame = row.Frame;
                    row.PreviousTicks = row.FrameTicks;
                    row.PreviousCalls = row.FrameCalls;
                    row.Frame = frame;
                    row.FrameTicks = 0;
                    row.FrameCalls = 0;
                }
                row.Calls++;
                row.FrameCalls++;
                row.TotalTicks += elapsed;
                row.MaxTicks = Math.Max(row.MaxTicks, elapsed);
                row.FrameTicks += elapsed;
                if (row.PeakFrame < 0 || row.FrameTicks > row.MaxFrameTicks)
                {
                    row.MaxFrameTicks = row.FrameTicks;
                    row.PeakFrame = frame;
                }
            }
            catch (Exception)
            {
                // A profiling failure must never quarantine a working subscriber or mask its exception.
                measurementErrors++;
            }
        }
    }

    internal object? SubscribersInFrame(int frame)
    {
        List<object>? result = null;
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            bool current = row.Frame == frame;
            if (!current && row.PreviousFrame != frame)
                continue;
            (result ??= new()).Add(new
            {
                id = i + 1, handler = row.Name, calls = current ? row.FrameCalls : row.PreviousCalls,
                ms = (current ? row.FrameTicks : row.PreviousTicks) * 1000d / Stopwatch.Frequency,
            });
        }
        return result;
    }

    internal object Snapshot()
    {
        var subscribers = new object[rows.Count];
        double ms = 1000d / Stopwatch.Frequency;
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            subscribers[i] = new
            {
                id = i + 1, handler = row.Name, calls = row.Calls, totalMs = row.TotalTicks * ms,
                maxCallMs = row.MaxTicks * ms, maxFrameMs = row.MaxFrameTicks * ms, peakFrame = row.PeakFrame,
            };
        }
        return new
        {
            DoorScans, DoorRoutesCopied, DoorPathCornersCopied,
            GrenadeContactObservations, GrenadeFullEvaluations,
            MedicalLeanGuards, MedicalTacticGuards, MedicalUpdateSamples, MedicalScopeRechecksAvoided,
            subscriberMeasurementErrors = measurementErrors, subscribers,
        };
    }
}
