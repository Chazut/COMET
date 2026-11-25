using System;
using System.Collections.Generic;
using System.Linq;

namespace Comet.Core;

public static class CometIdentity
{
    public const string Guid = "com.chazut.comet";
}

public static class CometVersion
{
    // BepInEx 5 uses System.Version; the release label is carried by the DLL and reports.
    public const string Plugin = "1.1.0";
    public const string Release = "1.1.0";
}

// Startup evidence, not a claim of successful live combat or a heartbeat.
public class IntegrationReport
{
    public int Schema { get; set; } = 1;
    public string Run { get; set; } = "";
    public string Outcome { get; set; } = "unknown";
    public string Phase { get; set; } = "";
    public string Message { get; set; } = "";
    public string Role { get; set; } = "unknown";
    public string Version { get; set; } = CometVersion.Release;
    public int Targets { get; set; }
    public int Callbacks { get; set; }
    public string[] Notices { get; set; } = Array.Empty<string>();
}

public sealed class IntegrationBoot
{
    public IntegrationReport Report { get; } = new() { Run = Guid.NewGuid().ToString("N") };
    public bool Ready => Report.Outcome == "ready";

    public void Step(string name, Action action)
    {
        if (Report.Outcome is "ready" or "blocked")
            throw new InvalidOperationException("Startup already finished.");
        Report.Phase = name;
        Report.Outcome = "checking";
        action();
    }

    public void Complete(int targets, int callbacks)
    {
        if (Report.Outcome != "checking" || targets <= 0 || callbacks <= 0)
            throw new InvalidOperationException("Cannot activate without verified hooks.");
        Report.Targets = targets;
        Report.Callbacks = callbacks;
        Report.Outcome = "ready";
        Report.Phase = "complete";
        Report.Message =
            "Official SAIN files and COMET hooks verified. Live raid validation is still required.";
    }

    public void Fail(Exception error, Action rollback)
    {
        var cause = error.GetBaseException();
        Report.Outcome = "blocked";
        Report.Message = Limit(
            error.GetType().Name
                + ": "
                + error.Message
                + (
                    ReferenceEquals(cause, error)
                        ? ""
                        : "; cause: " + cause.GetType().Name + ": " + cause.Message
                )
        );
        try
        {
            rollback();
        }
        catch (Exception cleanup)
        {
            Report.Message = Limit(
                Report.Message
                    + "; rollback failed: "
                    + cleanup.GetType().Name
                    + ": "
                    + cleanup.Message
            );
        }
    }

    public static string Limit(string text) => text.Length <= 1024 ? text : text.Substring(0, 1024);
}

public static class IntegrationContract
{
    // Harmony keys __state by the patch method's declaring type, not by callback name.
    public static void ValidateStateSlots(
        IEnumerable<(
            string Target,
            string Owner,
            string Kind,
            string Callback,
            string StateType,
            bool Writes
        )> hooks
    )
    {
        var types = new Dictionary<(string, string), string>();
        var writers = new Dictionary<(string, string), string>();
        foreach (var hook in hooks)
        {
            var slot = (hook.Target, hook.Owner);
            if (types.TryGetValue(slot, out var type) && type != hook.StateType)
                throw new InvalidOperationException(
                    "Conflicting Harmony __state on "
                        + hook.Target
                        + ": "
                        + type
                        + " / "
                        + hook.StateType
                );
            types[slot] = hook.StateType;
            if (hook.Kind != "prefix" || !hook.Writes)
                continue;
            if (writers.TryGetValue(slot, out var writer) && writer != hook.Callback)
                throw new InvalidOperationException(
                    "Shared Harmony __state producer on "
                        + hook.Target
                        + ": "
                        + writer
                        + " / "
                        + hook.Callback
                );
            writers[slot] = hook.Callback;
        }
    }

    // A callback can legitimately patch several native methods. The pair must be unique.
    public static void ValidateHooks(
        IEnumerable<(string Target, string Kind, string Callback)> installed,
        IEnumerable<string> required
    )
    {
        var pairs = new HashSet<string>(StringComparer.Ordinal);
        var callbacks = new HashSet<string>(StringComparer.Ordinal);
        foreach (var hook in installed)
        {
            string callback = hook.Kind + ":" + hook.Callback;
            if (!pairs.Add(hook.Target + "|" + callback))
                throw new InvalidOperationException(
                    "Duplicate COMET hook: " + callback + " on " + hook.Target
                );
            callbacks.Add(callback);
        }
        var missing = required
            .Where(c => !callbacks.Contains(c))
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToArray();
        if (missing.Length > 0)
            throw new MissingMethodException(
                "COMET hooks not installed: " + string.Join(", ", missing)
            );
    }

    public static bool Accept(
        bool eligible,
        bool hasRequest,
        int requestedFrame,
        int frame,
        int expected,
        int actual,
        bool selfPriority,
        bool squadPriority
    ) =>
        eligible
        && hasRequest
        && requestedFrame == frame
        && expected == actual
        && !selfPriority
        && !squadPriority;
}
