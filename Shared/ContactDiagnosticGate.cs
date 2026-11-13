using System.Collections.Generic;

namespace Comet.Core;

// Per-contact transitions: alternating enemies must not bypass the heartbeat throttle.
public sealed class ContactDiagnosticGate
{
    private readonly Dictionary<string, (string State, string Reason, double At)> entries = new();
    public int Count => entries.Count;
    public bool Allow(string contact, string state, string reason, double now, bool force = false)
    {
        if (entries.TryGetValue(contact, out var prior) && !force && now >= prior.At
            && prior.State == state && prior.Reason == reason && now - prior.At < 10) return false;
        if (!entries.ContainsKey(contact) && entries.Count >= 128)
        {
            string? oldest = null; double time = double.MaxValue;
            foreach (var entry in entries) if (entry.Value.At < time) { oldest = entry.Key; time = entry.Value.At; }
            if (oldest != null) entries.Remove(oldest);
        }
        entries[contact] = (state, reason, now);
        return true;
    }
    public void Clear() => entries.Clear();
}
