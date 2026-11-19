using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Comet.Core;

// Main-thread FIFO of immutable messages. A callback is indivisible, so this is
// a scheduling budget, not a promise that a slow subscriber can be preempted.
public sealed class FrameDispatchQueue<T>
{
    private readonly Queue<T> pending = new();
    private readonly Action<T> deliver;
    private readonly Func<long> clock;
    private readonly long budget;
    private readonly int capacity;
    private int frame = int.MinValue;
    private long spent;
    private bool draining;
    public long Delivered { get; private set; }
    public long ForcedDrains { get; private set; }
    public long OverBudgetCallbacks { get; private set; }
    public int PeakPending { get; private set; }
    public int Pending => pending.Count;
    public long MaxCallbackTicks { get; private set; }

    public FrameDispatchQueue(
        Action<T> deliver,
        Func<long>? clock = null,
        long budgetTicks = 0,
        int capacity = 4096
    )
    {
        this.deliver = deliver;
        this.clock = clock ?? Stopwatch.GetTimestamp;
        budget = budgetTicks > 0 ? budgetTicks : Math.Max(1, Stopwatch.Frequency / 1000);
        this.capacity = Math.Max(1, capacity);
    }

    public void Enqueue(T message, int currentFrame)
    {
        // Backpressure preserves events. This rare drain is counted, not silently dropped.
        if (pending.Count >= capacity && !draining)
        {
            ForcedDrains++;
            Drain(currentFrame, true);
        }
        pending.Enqueue(message);
        PeakPending = Math.Max(PeakPending, pending.Count);
        Drain(currentFrame);
    }

    public void Drain(int currentFrame, bool force = false)
    {
        if (draining)
            return;
        if (currentFrame != frame)
        {
            frame = currentFrame;
            spent = 0;
        }
        draining = true;
        try
        {
            while (pending.Count > 0 && (force || spent < budget))
            {
                var message = pending.Dequeue();
                long start = clock();
                try
                {
                    deliver(message);
                }
                finally
                {
                    long elapsed = Math.Max(0, clock() - start);
                    spent += elapsed;
                    Delivered++;
                    MaxCallbackTicks = Math.Max(MaxCallbackTicks, elapsed);
                    if (elapsed > budget)
                        OverBudgetCallbacks++;
                }
            }
        }
        finally
        {
            draining = false;
        }
    }

    public void ResetCounters()
    {
        Delivered = ForcedDrains = OverBudgetCallbacks = MaxCallbackTicks = 0;
        PeakPending = pending.Count;
        frame = int.MinValue;
        spent = 0;
    }
}
