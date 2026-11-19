using System.Collections.Generic;
using System.Reflection;
using Audio.SpatialSystem;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Comet.Client;

internal sealed class SiegeRoomCatalog
{
    // EFT retains its initialized rooms even when unloading one additive group clears AllCrossGroups.
    private static readonly FieldInfo? StorageField = typeof(SpatialAudioSystem).GetField(
        "_audioRoomStorage", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public
    );

    private readonly List<(List<SpatialAudioRoom>? Rooms, int Count)> sources = new();
    private readonly HashSet<SpatialAudioRoom> rooms = new();
    private int scenes = -1;
    internal int Revision { get; private set; }
    internal bool HasValidatedRooms => sources.Count > 0 && sources[0].Count > 0;

    internal void Clear()
    {
        sources.Clear();
        rooms.Clear();
        scenes = -1;
        Revision++;
    }

    internal bool Refresh()
    {
        var system = SpatialAudioSystem.Instance;
        var storage = system == null ? null : StorageField?.GetValue(system) as AudioRoomStorage;
        var validated = storage?._validSpatialAudioRooms;
        var groups = SpatialAudioCrossSceneGroup.AllCrossGroups;
        int sceneCount = SceneManager.sceneCount;
        bool changed = scenes != sceneCount || sources.Count != groups.Count + 1
            || !Matches(0, validated);
        for (int i = 0; !changed && i < groups.Count; i++)
        {
            var group = groups[i];
            changed = !Matches(i + 1, group == null ? null : group.AudioRooms);
        }
        if (!changed)
            return false;

        sources.Clear();
        rooms.Clear();
        AddSource(validated);
        foreach (var group in groups)
            AddSource(group == null ? null : group.AudioRooms);
        scenes = sceneCount;
        Revision++;
        return true;
    }

    // The same room can be in both EFT registries. Visit its geometry once per lookup.
    private void AddSource(List<SpatialAudioRoom>? source)
    {
        sources.Add((source, source?.Count ?? 0));
        if (source != null)
            foreach (var room in source)
                if (room is not null)
                    rooms.Add(room);
    }

    private bool Matches(int index, List<SpatialAudioRoom>? rooms) =>
        ReferenceEquals(sources[index].Rooms, rooms) && sources[index].Count == (rooms?.Count ?? 0);

    internal SpatialAudioRoom? RoomAt(Vector3 point)
    {
        SpatialAudioRoom? best = null;
        foreach (var room in rooms)
        {
            if (room == null || !room.IsValid || room.IsOutdoor
                || !SiegeBuilding.Contains(room, point) || !room.gameObject.activeInHierarchy)
                continue;
            // Unity's previous scene query returned rooms in instance ID order.
            if (best == null || room.RoomSize < best.RoomSize
                || (room.RoomSize == best.RoomSize && room.GetInstanceID() < best.GetInstanceID()))
                best = room;
        }
        return best;
    }
}
