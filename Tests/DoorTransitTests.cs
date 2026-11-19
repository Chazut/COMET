using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Comet.Core;
using Comet.Server;

internal static class DoorTransitTests
{
    internal static int Run()
    {
        int n = 0;
        void Check(bool b, string why)
        {
            n++;
            if (!b)
                throw new Exception("C01: " + why);
        }
        var c = new DoorTransitConfig();
        c.Validate();
        Check(
            c.Enabled
                && c.Anticipation == 3
                && c.SlowDistance == 1.5
                && c.SpeedCap == .25
                && c.BlockedLimit == 4,
            "defaults"
        );
        foreach (
            var edit in new Action<DoorTransitConfig>[]
            {
                c => c.Anticipation = double.NaN,
                c => c.SlowDistance = 4,
                c => c.Anticipation = 1,
                c => c.SpeedCap = 0,
                c => c.BlockedLimit = double.PositiveInfinity,
            }
        )
        {
            var bad = new DoorTransitConfig();
            edit(bad);
            bool rejected = false;
            try
            {
                bad.Validate();
            }
            catch (ArgumentException)
            {
                rejected = true;
            }
            Check(rejected, "validation");
        }
        var migrated = Newtonsoft.Json.JsonConvert.DeserializeObject<CometConfig>(
            "{\"doorTransit\":null}"
        )!;
        migrated.Validate();
        Check(migrated.DoorTransit.BlockedLimit == 4, "old config");
        string dir = Path.Combine(Path.GetTempPath(), "comet-c01-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ConfigStore(dir);
            var v = store.Read();
            v.DoorTransit.SpeedCap = .3;
            store.Save(v, v.Revision);
            Check(store.Read().DoorTransit.SpeedCap == .3, "server persistence");
        }
        finally
        {
            File.Delete(dir);
            File.Delete(dir + ".bak");
        }
        DoorGeometry Door(double x = 0, double y = 0) =>
            new()
            {
                Id = "door",
                Center = new(x, y, 0),
                Axis = new(1, 0, 0),
                Normal = new(0, 0, 1),
                HalfWidth = .5,
            };
        var route = new[] { new DoorPoint(0, 0, -2), new DoorPoint(0, 0, 3) };
        Check(DoorTransit.RouteDistance(Door(), route, 3) == 2, "forward threshold");
        Check(
            double.IsPositiveInfinity(DoorTransit.RouteDistance(Door(2), route, 3)),
            "side door excluded"
        );
        Check(
            double.IsPositiveInfinity(DoorTransit.RouteDistance(Door(0, 3), route, 3)),
            "other floor excluded"
        );
        Check(
            double.IsPositiveInfinity(
                DoorTransit.RouteDistance(
                    Door(),
                    new[] { new DoorPoint(0, 0, 1), new DoorPoint(0, 0, 3) },
                    3
                )
            ),
            "door behind excluded"
        );
        Check(
            double.IsPositiveInfinity(DoorTransit.RouteDistance(Door(), route, 1)),
            "along route limit"
        );
        Check(
            double.IsPositiveInfinity(
                DoorTransit.RouteDistance(
                    Door(),
                    new[]
                    {
                        new DoorPoint(0, 0, -2),
                        new DoorPoint(5, 0, -1),
                        new DoorPoint(0, 0, 2),
                    },
                    3
                )
            ),
            "detour cannot select nearby threshold"
        );
        Check(
            double.IsPositiveInfinity(
                DoorTransit.RouteDistance(
                    Door(),
                    new[] { new DoorPoint(double.NaN, 0, 0), new DoorPoint(0, 0, 1) },
                    3
                )
            ),
            "invalid geometry"
        );
        var m = new DoorTransit();
        DoorTransitTrace Step(
            double now,
            bool block = true,
            bool busy = false,
            bool interacting = false,
            DoorPoint? origin = null,
            string state = "Shut"
        ) =>
            m.Observe(
                "door",
                state,
                "Search",
                origin ?? new(0, 0, -1),
                new(0, 0, 0),
                1,
                now,
                block,
                busy,
                interacting,
                c
            );
        var t = Step(0);
        Check(t.Slow && t.StopSprint && !t.Recovery, "approach");
        for (int i = 1; i <= 8; i++)
            t = Step(i * .1);
        Check(t.Recovery && !t.Requested && !t.Passed, "recovery only after observed jam");
        Step(.9, interacting: true);
        Check(t.Blocked == 0 && !t.Recovery, "native interaction not timeout");
        for (int i = 10; i <= 70; i++)
            Step(i * .1, interacting: true);
        Check(t.State == "interacting" && t.Blocked == 0, "long animation protected");
        Step(7.1, busy: true);
        Check(!t.Slow && !t.StopSprint && t.Blocked == 0, "other interaction respected");
        Step(7.2, block: false, state: "Open");
        Check(
            t.State == "clear" && !t.Slow && !t.StopSprint && !t.Passed,
            "open clear escape unmodified"
        );
        for (int i = 73; i <= 114; i++)
            t = Step(i * .1);
        Check(
            t.State == "blocked" && t.ValidFor == 0 && !t.Recovery && !t.Slow,
            "4s reevaluation bounded"
        );
        m.End("replanned");
        for (int i = 0; i <= 60; i++)
            t = Step(i * .1, origin: new(0, 0, -1 - i * .25));
        Check(t.Blocked == 0, "moving is not jammed");
        m.End("reset");
        Step(0);
        Step(1);
        Check(m.Trace.Blocked == 0, "observation gap not invented blockage");
        Step(.5);
        Check(m.Trace.State == "ended", "clock rewind fails open");
        m.End("passed", true);
        Check(
            m.Trace.Passed && !m.Trace.Slow && m.Trace.ValidFor == 0,
            "explicit crossing observation"
        );
        m.End("reset");
        Step(0, state: "Open");
        for (int i = 1; i <= 60; i++)
        {
            if (i % 8 == 0)
                m.Pause("transit-movement-changed");
            Step(i * .1, state: "Open");
        }
        Check(
            m.Trace.State == "blocked" && !m.Trace.Slow,
            "repeated short replans cannot reset an open-door jam indefinitely"
        );
        m.End("reset");
        Step(0, state: "Open");
        for (int i = 1; i <= 50; i++)
        {
            m.Pause("transit-movement-changed");
            Step(i * .1, state: "Open");
        }
        Check(
            m.Trace.State == "blocked",
            "plan churn on every observed tick still reaches the blocked limit"
        );
        m.Pause("yield");
        m.End("ghost");
        Step(5.1, state: "Open");
        Check(m.Trace.Blocked == 0, "terminal scope exit clears paused incident");
        m.Pause("yield");
        Step(8, state: "Open");
        Check(m.Trace.Blocked == 0, "long observation gaps discard stale jam evidence");
        Step(8.1, state: "Open");
        m.Pause("yield");
        Step(8.2, state: "Open", origin: new(1, 0, -1));
        Check(m.Trace.Blocked == 0, "physical progress clears paused jam");
        var speed = new TransitSpeedLease();
        Check(speed.Limit(.7f, .25) == .25f, "cap");
        Check(speed.Limit(.15f, .25) == .15f, "does not accelerate existing slow move");
        Check(
            speed.Release(.15f, out float restore) && restore == .15f && !speed.Owned,
            "restore latest request"
        );
        speed.Limit(.7f, .25);
        Check(!speed.Release(.8f, out _), "foreign newer speed preserved");
        speed.Limit(.7f, .25);
        speed.Release(float.NaN, out _);
        Check(!speed.Release(.25f, out _), "incoming command invalidates ownership");
        var assembly = Path.Combine(
            AppContext.BaseDirectory,
            "../../../../references/sain-4.5.1/SAIN.dll"
        );
        using (var pe = new PEReader(File.OpenRead(assembly)))
        {
            var md = pe.GetMetadataReader();
            TypeDefinition Type(string name) =>
                md
                    .TypeDefinitions.Select(md.GetTypeDefinition)
                    .Single(t => md.GetString(t.Name) == name);
            var path = Type("BotPathDataManual");
            Check(
                path.GetFields()
                    .Select(md.GetFieldDefinition)
                    .Any(f => md.GetString(f.Name) == "Bot"),
                "native path owner field"
            );
            foreach (
                string name in new[]
                {
                    "TickPath",
                    "RequestStartSprint",
                    "RequestEndSprint",
                    "get_TimeStarted",
                    "get_CancelRequested",
                }
            )
                Check(
                    path.GetMethods()
                        .Select(md.GetMethodDefinition)
                        .Any(x => md.GetString(x.Name) == name),
                    "native path " + name
                );
            var doors = Type("DoorOpener");
            foreach (
                string name in new[]
                {
                    "SelectDoor",
                    "TryInteractWithDoor",
                    "get__interactionDoors",
                }
            )
                Check(
                    doors
                        .GetMethods()
                        .Select(md.GetMethodDefinition)
                        .Any(x => md.GetString(x.Name) == name),
                    "native door " + name
                );
            Check(
                doors
                    .GetFields()
                    .Select(md.GetFieldDefinition)
                    .Any(f => md.GetString(f.Name) == "_interactionDoorIndex"),
                "native recovery index"
            );
            Check(
                Type("PlayerMovementController")
                    .GetMethods()
                    .Select(md.GetMethodDefinition)
                    .Any(x => md.GetString(x.Name) == "SetTargetMoveSpeed"),
                "native direct speed hook"
            );
        }
        Console.WriteLine($"C01 policy and native contracts: {n} checks passed.");
        return n;
    }
}
