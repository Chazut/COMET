using Comet.Client;
using Comet.Core;
using EFT;
using EFT.Interactive;
using SAIN.Components;
using SAIN.SAINComponent.Classes.Mover;
using UnityEngine;
using UnityEngine.AI;

internal static class OpenLeafCases
{
    internal static void Run()
    {
        int checks = 0;
        void Check(bool ok, string name)
        {
            if (!ok) throw new Exception("open leaves: " + name);
            checks++;
        }
        var world = new GameWorld();
        var p = new Plugin();
        var bot = new BotComponent { Position = new(0, 0, -4) };
        var path = new BotPathDataManual(bot);
        Plugin.Instance = p;
        var controller = BotDoorsController.Scene = new();
        NavMeshDoorLink Leaf(Door? door = null)
        {
            door ??= new Door();
            door.CurrentAngle = 90;
            door.DoorState = EDoorState.Open;
            var link = new NavMeshDoorLink { Door = door };
            controller._navMeshDoorLinks!.Add(link);
            return link;
        }
        void Tick(float time) { p.At(time); p.LeafTick(world); }
        bool Reuse(bool proposed = true) => Plugin.CachedLeafRoute(path, bot, proposed);
        Check(Plugin.LeafHookCount() == 2, "path initialize and reuse hooks");
        foreach (var (master, enabled, authority) in new[] { (false,true,true), (true,false,true), (true,true,false) })
        {
            p.config!.Enabled = master;
            p.config.DoorMotion.CarveOpenLeaves = enabled;
            p.Authority = authority;
            Tick(0);
            Check(!p.LeavesStarted, "disabled master, setting or Fika guest");
        }
        p.config = new CometConfig();
        p.config.DoorMotion.Enabled = false;
        p.Authority = true;
        controller._navMeshDoorLinks = null;
        Tick(0);
        Check(p.LeavesStarted, "Fika host starts carver even with local door recovery off");
        controller._navMeshDoorLinks = new();
        var open = Leaf();
        var reversed = Leaf();
        reversed.Door.CurrentAngle = -90;
        var sliding = Leaf(new SlidingDoor());
        sliding.Door.CurrentAngle = -90;
        var closed = Leaf();
        closed.Door.DoorState = EDoorState.Shut;
        var inactive = Leaf();
        inactive.Door.gameObject.activeInHierarchy = false;
        var initiallyDisabled = Leaf();
        initiallyDisabled.Door.enabled = false;
        var sameDoor = Leaf(open.Door);
        var sharedObstacle = sameDoor.Carver_Breached = open.Carver_Breached;
        Tick(1);
        p.At(1.1f);
        Plugin.StampLeafPath(path);
        var actor = new SAIN.Components.Player { Position = new(1.99f,0,0) };
        world.AllAlivePlayersList.Add(actor);
        Tick(2);
        Check(!open.Carver_Opened.enabled && !initiallyDisabled.Carver_Opened.enabled, "living actor within two metres");
        actor.Position = new(2,0,0);
        Tick(3);
        Check(open.Carver_Opened.enabled && open.Carver_Opened.carving && sharedObstacle.enabled, "both obstacle kinds activated at two metres");
        Check(!closed.Carver_Opened.enabled && !inactive.Carver_Opened.enabled, "closed and inactive door");
        Check(!reversed.Carver_Opened.enabled && sliding.Carver_Opened.enabled && initiallyDisabled.Carver_Opened.enabled,
            "reverse swing, sliding door, and initially disabled component");
        Check(Reuse(), "cut not yet applied in the same frame");
        p.At(3.01f);
        Check(!Reuse(), "old route crosses a new cut");
        Check(!Reuse(false), "native rejection preserved");
        bot.Pmc = false;
        Check(Reuse(), "unsupported bot retains native reuse");
        bot.Pmc = true;
        p.IsGhost = true;
        Check(Reuse(), "ghost retains native reuse");
        p.IsGhost = false;
        bot.Position = new(2.5f,0,-4);
        path.PathCorners = new() { new() { Position = new(2.5f,0,4) } };
        Check(Reuse(), "exact path distance boundary");
        path.PathCorners.Insert(0, new() { Position = new(0,0,0) });
        path.CurrentIndex = 1;
        Check(Reuse(), "passed corners excluded");
        bot.Position = new(0,0,-4);
        path.CurrentIndex = 0;
        Check(!Reuse(), "remaining route passes leaf");
        Plugin.StampLeafPath(path);
        Check(Reuse(), "recycled path initialized after last cut");
        open.Door.DoorState = EDoorState.Interacting;
        Check(!open.Carver_Opened.enabled && !sameDoor.Carver_Opened.enabled && !sharedObstacle.enabled,
            "all links released immediately on transition away from Open");
        open.Door.DoorState = EDoorState.Open;
        open.Door.DoorState = EDoorState.Open;
        reversed.Door.CurrentAngle = 90;
        inactive.Door.gameObject.activeInHierarchy = true;
        Tick(4);
        Check(open.Carver_Opened.enabled && reversed.Carver_Opened.enabled && inactive.Carver_Opened.enabled, "reopened and restored leaves");
        open.Door.enabled = false;
        Tick(5);
        Check(!open.Carver_Opened.enabled, "disabled after raid start without a state event");
        open.Door.enabled = true;
        Tick(6);
        Check(open.Carver_Opened.enabled, "re-enabled while still open");
        Tick(67);
        Check(Reuse(), "cut history expires");
        p.Authority = false;
        Tick(67.1f);
        Check(!open.Carver_Opened.enabled && !sharedObstacle.enabled && !p.LeavesStarted, "loss of AI authority releases obstacles and owner");
        open.Door.DoorState = EDoorState.Shut;
        open.Door.DoorState = EDoorState.Open;
        Check(open.Door.Listeners == 0 && p.Logger.Info.Any(x => x.Contains("stale paths")), "unsubscribed all links and summarized raid");

        controller._navMeshDoorLinks.Clear();
        p.Authority = true;
        for (int i = 0; i < 18; i++)
            Leaf();
        var preexisting = controller._navMeshDoorLinks[0].Carver_Opened;
        preexisting.carving = true;
        Tick(68);
        Check(p.LeavesStarted, "AI authority regained");
        Tick(69);
        Check(controller._navMeshDoorLinks.Count(x => x.Carver_Opened.enabled) == 16, "16 new cuts per tick");
        Tick(69.9f);
        Check(controller._navMeshDoorLinks.Count(x => x.Carver_Opened.enabled) == 16, "once per second");
        Tick(70);
        Check(controller._navMeshDoorLinks.All(x => x.Carver_Opened.enabled), "remainder next tick");
        p.EndLeaves();
        Check(preexisting.carving && !preexisting.enabled, "restore original separate carving/enabled flags on teardown");
        controller._navMeshDoorLinks = null;
        Tick(71);
        Tick(132);
        controller._navMeshDoorLinks = new();
        var late = Leaf();
        controller._navMeshDoorLinks.Clear();
        Tick(137); // EFT has published an empty list.
        controller._navMeshDoorLinks.Add(late);
        var lateDoor = late.Door;
        late.Door = null!;
        Tick(142); // Link exists before its door is assigned.
        late.Door = lateDoor;
        lateDoor.DoorState = EDoorState.Shut;
        var loadedOpen = Leaf();
        Tick(147);
        Tick(148);
        Check(lateDoor.Listeners == 1 && lateDoor.DoorState == EDoorState.Shut && !late.Carver_Opened.enabled,
            "late closed door is observed without opening or carving it");
        Check(loadedOpen.Carver_Opened.enabled, "late scene-open leaf is carved");
        lateDoor.DoorState = EDoorState.Open;
        Tick(149);
        Check(late.Carver_Opened.enabled, "first real opening of late door is carved");
        var partial = Leaf();
        var partialDoor = partial.Door;
        partial.Door = null!;
        Tick(152);
        partial.Door = partialDoor;
        Tick(157);
        Check(partial.Carver_Opened.enabled && lateDoor.Listeners == 1 && partialDoor.Listeners == 1,
            "partially initialized roster picked up without duplicate subscriptions");
        Check(p.Logger.Info.Count(x => x.Contains("still waiting")) == 1, "long load reported only once");
        p.EndLeaves();
        Check(lateDoor.Listeners == 0 && partialDoor.Listeners == 0, "late subscriptions released");
        Console.WriteLine($"Open door leaves: {checks} production adapter checks passed.");
    }
}

public sealed class NavMeshDoorLink
{
    public Door Door = new();
    public Vector3 MidOpen;
    public NavMeshObstacle Carver_Opened = new(), Carver_Breached = new();
}

public sealed class BotDoorsController
{
    public static BotDoorsController Scene = new();
    public List<NavMeshDoorLink>? _navMeshDoorLinks = new();
}

namespace UnityEngine { public class Object { public static T? FindObjectOfType<T>() where T : class => BotDoorsController.Scene as T; } }
namespace EFT { public sealed class IBotGame { } }
namespace UnityEngine { public static class Mathf { public static float Clamp01(float value) => Math.Clamp(value,0,1); } }
namespace UnityEngine.AI { public sealed class NavMeshObstacle { public bool enabled, carving; } }
namespace Comet.Client
{
    public sealed partial class Plugin
    {
        partial void ClearOpenLeaves();
        internal static int LeafHookCount()
        {
            var hooks = new HarmonyLib.Harmony();
            PatchOpenLeafPaths(hooks);
            return hooks.Patches.Count;
        }
        internal bool LeavesStarted => openLeaves != null;
        internal void EndLeaves() => ClearOpenLeaves();
        internal void LeafTick(GameWorld world) => UpdateOpenLeaves(world);
        internal static void StampLeafPath(BotPathDataManual path) => OpenLeafPathInitialized(path);
        internal static bool CachedLeafRoute(BotPathDataManual path, BotComponent bot, bool result)
        {
            OpenLeafPathReused(path, bot, ref result);
            return result;
        }
    }
}
