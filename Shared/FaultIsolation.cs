using System;
using System.Collections.Generic;

namespace Comet.Core;

public sealed class FaultIsolationConfig
{
    public double RetrySeconds { get; set; } = 5;
    public int ErrorLimit { get; set; } = 3;
    public double WindowSeconds { get; set; } = 60;
    public void Validate()
    {
        CometConfig.Range(RetrySeconds, 1, 30, nameof(RetrySeconds));
        CometConfig.Range(ErrorLimit, 2, 10, nameof(ErrorLimit));
        CometConfig.Range(WindowSeconds, 10, 300, nameof(WindowSeconds));
        if (WindowSeconds < RetrySeconds) throw new ArgumentException("Error window must be at least the retry delay.");
    }
}

public sealed class FaultTrace
{
    public string Module { get; set; } = "";
    public string Entry { get; set; } = "";
    public string State { get; set; } = "";
    public string Reason { get; set; } = "";
    public string Error { get; set; } = "";
    public int Count { get; set; }
    public int Total { get; set; }
    public double RetryIn { get; set; }
    public bool CleanupFailed { get; set; }
    public string CleanupError { get; set; } = "";
    public bool First { get; set; }
    public double ValidFor { get; set; } = 2;
}

// Per runtime bot identity, never profile ID. Entries exist only after an error.
// Single AI thread; no timers, tasks, repeated stack traces, or per-frame allocations.
public sealed class FaultIsolation
{
    private sealed class Module
    {
        internal readonly Queue<double> Errors = new();
        internal int Total;
        internal double RetryAt, Last;
        internal bool Disabled, Waiting;
        internal string Entry = "", Error = "";
    }
    private sealed class Bot
    {
        internal readonly Dictionary<string, Module> Modules = new();
        internal bool Disabled;
    }
    private readonly Dictionary<object, Bot> bots = new();
    public bool HasFaults => bots.Count != 0;
    public bool BotDisabled(object bot) => bots.TryGetValue(bot, out var b) && b.Disabled;
    public bool Ready(object bot, string module, double now, out FaultTrace? retry)
    {
        retry = null;
        if (!bots.TryGetValue(bot, out var b)) return true;
        if (b.Disabled) return false;
        if (!b.Modules.TryGetValue(module, out var m)) return true;
        if (m.Disabled) return false;
        if (now < m.Last) { m.RetryAt = now + Math.Max(0, m.RetryAt - m.Last); m.Errors.Clear(); }
        m.Last = now;
        if (now < m.RetryAt) return false;
        if (m.Waiting) { m.Waiting = false; retry = Trace(module, m, "retrying", "fault-retry-fresh-state", now); }
        return true;
    }
    public FaultTrace Fail(object bot, string module, string entry, Exception error, double now, FaultIsolationConfig c, bool cleanupFailed = false)
    {
        if (!bots.TryGetValue(bot, out var b)) bots.Add(bot, b = new());
        if (!b.Modules.TryGetValue(module, out var m)) b.Modules.Add(module, m = new());
        if (now < m.Last) m.Errors.Clear();
        while (m.Errors.Count > 0 && now - m.Errors.Peek() >= c.WindowSeconds) m.Errors.Dequeue();
        m.Errors.Enqueue(now); m.Total++; m.Last = now; m.Entry = entry;
        m.Error = error.GetType().Name + ": " + error.Message;
        if (m.Error.Length > 512) m.Error = m.Error.Substring(0, 512);
        m.RetryAt = now + c.RetrySeconds; m.Waiting = true;
        m.Disabled |= m.Errors.Count >= c.ErrorLimit;
        b.Disabled |= cleanupFailed;
        return Trace(module, m, b.Disabled ? "bot-disabled" : m.Disabled ? "module-disabled" : "suspended",
            b.Disabled ? "fault-cleanup-failed" : m.Disabled ? "fault-error-limit" : "fault-retry-delay", now, cleanupFailed);
    }
    private static FaultTrace Trace(string module, Module m, string state, string reason, double now, bool cleanup = false) => new()
    {
        Module = module, Entry = m.Entry, State = state, Reason = reason, Error = m.Error,
        Count = m.Errors.Count, Total = m.Total, RetryIn = m.Disabled || cleanup ? 0 : Math.Max(0, m.RetryAt - now),
        CleanupFailed = cleanup, First = m.Total == 1
    };
    public void DisableBot(object bot) { if (!bots.TryGetValue(bot, out var b)) bots.Add(bot, b = new()); b.Disabled = true; }
    public void Clear() => bots.Clear();
}
