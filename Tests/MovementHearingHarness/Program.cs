using System.Numerics;

Plugin.Run();
static class Time { public static float time = 4; }
static class Stopwatch
{
    public const long Frequency = 1000;
    public static long Tick;
    public static long GetTimestamp() => Tick;
}
sealed class SoundEvent
{
    public PlayerComponent PlayerComponent = new();
    public float TimeCreated = Time.time, BaseRangeWithVolume = 10;
    public int EnvironmentId;
    public Vector3 Position = Vector3.Zero;
}
sealed class PlayerComponent
{
    public Vector3 Position;
    public readonly AIData AIData = new();
    public readonly Equipment Equipment = new();
    public BotComponent BotComponent = new();
    public sealed class Players { public HashSet<Other> DataHashSet = new(); }
    public Players OtherPlayersData = new();
    public bool Active = true;
}
sealed class Other
{
    public PlayerComponent OtherPlayerComponent = new();
    public bool IsInHearingRadius_Footsteps = true;
    public sealed class Distances { public float Distance; }
    public Distances DistanceData = new();
}
sealed class BotComponent
{
    public readonly Player Player = new();
    public readonly Memory Memory = new();
    public PlayerComponent PlayerComponent = null!;
    public bool Active = true;
    public Vector3 Position;
    public BotComponent Hearing => this;
    public BotComponent HearingInput => this;
    public int Deliveries;
    public Action? OnDelivery;
    public void CheckAddSoundToCache(SoundEvent sound, float distance)
    {
        Deliveries++;
        OnDelivery?.Invoke();
    }
}
partial class Plugin
{
    bool MovementEnabled => true;
    static bool MovementSource(PlayerComponent p) => p.Active;
    static bool MovementListener(BotComponent b) => b.Active;
    void CountMovement(string key) => movementCounts[key] = movementCounts.GetValueOrDefault(key) + 1;
    void FlushMovementSummary() { }
    void MovementError(Exception ex) => throw new Exception("dispatch", ex);
    void ClearMovementHearing() => throw new InvalidOperationException();
    void RemoveMovement(MovementDelivery d, string reason) => movementPending.Remove((d.Bot, d.Signal.Sound));

    public static void Run()
    {
        var p = new Plugin();
        p.CheckGaitOverride();
        var s = new SoundEvent();
        var cache = s.PlayerComponent.OtherPlayersData.DataHashSet;
        var actors = Enumerable.Range(0, 257).Select(_ => new Other()).ToArray();
        foreach (var a in actors) cache.Add(a);
        actors[2].DistanceData.Distance = 100;
        actors[3].IsInHearingRadius_Footsteps = false;
        actors[4].OtherPlayerComponent.BotComponent.Active = false;
        actors[5].OtherPlayerComponent.BotComponent.Position = new Vector3(20, 0, 0);
        actors[0].OtherPlayerComponent.BotComponent.OnDelivery = () => Stopwatch.Tick++;
        p.movementQueue.Enqueue(new MovementSignal { Sound = s });
        p.UpdateMovementHearing();
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
        int Hits(int i) => actors[i].OtherPlayerComponent.BotComponent.Deliveries;
        Check(Hits(0) == 1 && Hits(1) == 0, "pause between recipients at budget");
        cache.Clear(); // The next frame must resume the old snapshot, not enumerate this cache.
        actors[1].OtherPlayerComponent.BotComponent.Active = false;
        var second = new SoundEvent();
        second.PlayerComponent.OtherPlayersData.DataHashSet.Add(actors[1]);
        second.PlayerComponent.OtherPlayersData.DataHashSet.Add(actors[0]);
        p.movementQueue.Enqueue(new MovementSignal { Sound = second });
        for (int i = 0; i < 5; i++) p.UpdateMovementHearing();
        Check(Hits(0) == 2 && actors.Skip(1).Take(5).All(x => x.OtherPlayerComponent.BotComponent.Deliveries == 0),
            "new signal has its own snapshot, revalidate current activity and emission distance");
        Check(actors.Skip(6).Take(250).All(x => x.OtherPlayerComponent.BotComponent.Deliveries == 1)
            && Hits(256) == 0 && p.movementQueue.Count == 0, "stable order, no duplicates and unchanged inspection cap");
        Check(p.movementCounts["recipient-snapshot-truncated"] == 1, "cap counted once");

        p.movementQueue.Enqueue(new MovementSignal { Sound = second });
        p.UpdateMovementHearing();
        Time.time += 2;
        p.movementQueue.Enqueue(new MovementSignal { Sound = new SoundEvent() });
        p.UpdateMovementHearing();
        Check(p.movementQueue.Count == 0 && p.movementRecipients.Count == 0
            && p.movementCounts["source-expired-or-inactive"] == 1, "discard expired head without reusing recipients");
        var delivery = new MovementDelivery { Signal = new MovementSignal { Sound = s }, Bot = actors[0].OtherPlayerComponent.BotComponent };
        p.movementPending.Add((delivery.Bot, s), delivery);
        p.movementLog.Add("old", (-10, 0));
        p.PruneMovementHearing();
        Check(p.movementPending.Count == 0 && p.movementLog.Count == 0, "prune expired entries");

        foreach (var actor in actors.Take(32)) cache.Add(actor);
        p.SnapshotMovementRecipients(s);
        long SnapshotBytes()
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10000; i++) p.SnapshotMovementRecipients(s);
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
        SnapshotBytes();
        long bytes = SnapshotBytes();
        Check(bytes == 0, "snapshot allocates " + bytes);
        Console.WriteLine("Hearing dispatch passed. 10,000 warm 32-actor snapshots: " + bytes + " allocated bytes.");
    }
}
