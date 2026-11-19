using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Comet.Core;

// Exact member-set identity, with no refresh delay. The weak key does not retain
// a previous raid's members or bot components.
internal static class SquadMemberIdentity
{
    private sealed class Entry
    {
        internal string[] Members = Array.Empty<string>();
        internal string Identity = "";
    }

    private static readonly ConditionalWeakTable<object, Entry> Entries = new();

    internal static string Get<T>(Dictionary<string, T> members)
    {
        var entry = Entries.GetValue(members, Create);
        bool unchanged = entry.Members.Length == members.Count;
        if (unchanged)
            foreach (var id in entry.Members)
                if (!members.ContainsKey(id))
                {
                    unchanged = false;
                    break;
                }
        if (unchanged)
            return entry.Identity;

        var ids = new string[members.Count];
        members.Keys.CopyTo(ids, 0);
        Array.Sort(ids, StringComparer.Ordinal);
        entry.Members = ids;
        entry.Identity = string.Join("|", ids);
        return entry.Identity;
    }

    private static Entry Create(object _) => new();
}
