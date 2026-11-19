using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Comet.Core;

// Unity's thread owns this state. Retired worlds are weak: checking late callbacks
// must not itself keep the old raid alive.
public sealed class RaidLifetime
{
    private WeakReference<object>? retired;
    public long Generation { get; private set; }
    public bool Active { get; private set; }

    public bool CanBegin(object world) =>
        !Active
        && !(retired != null && retired.TryGetTarget(out var old) && ReferenceEquals(old, world));

    public bool Begin(object world)
    {
        if (!CanBegin(world))
            return false;
        Active = true;
        Generation++;
        return true;
    }

    public bool End(object? world)
    {
        if (!Active)
            return false;
        Active = false;
        Generation++;
        if (world != null)
            retired = new(world);
        return true;
    }
}

// RequestHandler is synchronous. Detaching does not pretend to abort HTTP. It drops
// our ownership and observes any late exception without capturing Plugin/GameWorld.
public sealed class RaidConfigLoad<T>
    where T : class
{
    private Task<T>? task;
    private long generation;
    public bool Pending => task != null;

    public void Start(long epoch, Func<T> fetch)
    {
        Discard();
        generation = epoch;
        task = Task.Run(fetch);
    }

    public bool Take(long epoch, out T? value, out Exception? error)
    {
        value = null;
        error = null;
        if (epoch != generation)
        {
            Discard();
            return false;
        }
        var result = task;
        if (result == null || !result.IsCompleted)
            return false;
        task = null;
        if (result.Status == TaskStatus.RanToCompletion)
            value = result.Result;
        else
            error = result.Exception?.GetBaseException() ?? new OperationCanceledException();
        return true;
    }

    public bool Discard()
    {
        var old = task;
        task = null;
        if (old == null)
            return false;
        _ = old.ContinueWith(
            static failed =>
            {
                _ = failed.Exception;
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );
        return true;
    }
}

public sealed class RaidCleanupTrace
{
    public string State { get; set; } = "completed";
    public string Reason { get; set; } = "raid-ended";
    public int Bots { get; set; }
    public int Steps { get; set; }
    public int FailedSteps { get; set; }
    public bool PendingConfigDiscarded { get; set; }
    public int RemainingBots { get; set; }
    public int RemainingScans { get; set; }
    public double DurationMs { get; set; }
    public string[] Failures { get; set; } = Array.Empty<string>();
}

public sealed class RaidCleanupBatch
{
    public RaidCleanupTrace Trace { get; } = new();

    public void Step(string name, Action release)
    {
        Trace.Steps++;
        try
        {
            release();
        }
        catch (Exception ex)
        {
            Failure(name + ": " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    public void Failure(string message)
    {
        Trace.FailedSteps++;
        Trace.State = "completed-with-errors";
        if (Trace.Failures.Length >= 12)
            return;
        var messages = new List<string>(Trace.Failures)
        {
            message.Length > 240 ? message.Substring(0, 240) : message,
        };
        Trace.Failures = messages.ToArray();
    }
}
