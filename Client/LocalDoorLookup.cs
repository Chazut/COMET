using System;
using System.Collections.Generic;
using EFT.Interactive;

namespace Comet.Client;

// Several navigation links can point at the same door. Two different doors with
// the same ID cannot identify which exterior opening an acoustic portal means.
internal sealed class LocalDoorLookup
{
    private readonly Dictionary<string, Door?> doors = new(StringComparer.Ordinal);
    internal int Revision { get; private set; }

    internal void Add(Door door)
    {
        if (string.IsNullOrEmpty(door.Id))
            return;
        if (doors.TryGetValue(door.Id, out var prior))
        {
            if (prior is null || prior == door)
                return;
            doors[door.Id] = null;
        }
        else
            doors.Add(door.Id, door);
        Revision++;
    }

    internal Door? Find(string id) => doors.TryGetValue(id, out var door) ? door : null;

    internal void Clear()
    {
        doors.Clear();
        Revision++;
    }
}
