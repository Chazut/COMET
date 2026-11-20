using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using Comet.Core;
using Comet.Server;

internal static class OutdoorRelocationTests
{
    public static int Run()
    {
        int n = 0;
        void Check(bool ok, string why)
        {
            if (!ok)
                throw new Exception("M13: " + why);
            n++;
        }
        void Invalid(Action action)
        {
            try
            {
                action();
            }
            catch (ArgumentException)
            {
                n++;
                return;
            }
            throw new Exception("M13 accepted invalid input");
        }
        var c = new OutdoorRelocationConfig();
        c.Validate();
        RelocationFrame F(double now = 10) =>
            new()
            {
                Now = now,
                Target = "known-contact",
                Key = "known-contact:1",
                Source = "personal-vision",
                ContactAge = 2,
                SightAge = 2,
                Position = new(0, 0, 0),
                Known = new(0, 0, 30),
                Outdoor = true,
                InCover = true,
                HandsReady = true,
                NativeMoveStarted = true,
                Route = RelocationRoute.ToSelectedCover,
            };
        RelocationCandidate C() =>
            new()
            {
                Id = "west-cover",
                Position = new(10, 0, 0),
                Distance = 10,
                PathLength = 11,
                Angle = 30,
                Exposure = .1,
                Protection = 3,
                OriginalProtection = 3,
                Safe = true,
            };
        OutdoorRelocation P()
        {
            var p = new OutdoorRelocation();
            p.History.Observe("known-contact:1", true, false, 0);
            return p;
        }
        Check(
            c.Enabled
                && c.Grenade
                && c.Chance == 70
                && c.SightLoss == 1.5
                && c.MinDistance == 5
                && c.MaxDistance == 17
                && c.MaxPath == 26
                && c.PreferredAngle == 25
                && c.MoveTimeout == 12
                && c.SessionTimeout == 30
                && c.RetryDelay == 20,
            "approved defaults"
        );
        int rolls = 0;
        double Low()
        {
            rolls++;
            return .1;
        }
        var p = P();
        Check(
            p.Begin(F(), c, Low) && p.Active,
            "local incoming fire before own sight opens an opportunity"
        );
        Check(
            p.Trace!.Requested && !p.Trace.Accepted && !p.Trace.MovementObserved,
            "selection is not movement"
        );
        Check(p.Select(C(), c), "useful equally protective new angle selected");
        p.NativeAccepted(true);
        Check(p.CanMove(F()), "grenade not required");
        p.MoveRequested(10.1, p.Origin);
        var f = F(10.3);
        f.Position = new(1, 0, 0);
        f.Speed = 3;
        Check(p.Observe(f, c) && p.Trace.MovementObserved, "actual position change recorded");
        f = F(11);
        f.Position = new(9.29, 0, 0);
        Check(p.Observe(f, c) && !p.Trace.Arrived, "outside the source arrival radius");
        f.Position = new(9.31, .1, 0);
        Check(
            !p.Observe(f, c)
                && p.Trace.Arrived
                && p.Trace.State == "arrived"
                && p.Trace.ValidFor == 0,
            "native arrival at matching floor completes"
        );
        p = P();
        f = F();
        f.OtherTactic = true;
        Check(!p.Begin(f, c, Low), "door or squad tactic owns the initial decision");
        f.OtherTactic = false;
        Check(p.Begin(f, c, Low), "can enter once the competing tactic ends");
        f.OtherTactic = true;
        Check(p.Observe(f, c) && p.Active, "a later tactic flag cannot cancel an active relocation");
        p.Select(C(), c);
        p.NativeAccepted(true);
        p.MoveRequested(10.1, new(4, 0, 0));
        f = F(10.2);
        f.Position = new(4.1, 0, 0);
        Check(p.Observe(f,c) && !p.Trace!.MovementObserved && !p.Trace.Accepted, "planning displacement and Freeze acceptance cannot prove SeekCover");
        f.NativeMoveStarted = false;
        f.Position = new(5, 0, 0);
        Check(p.Observe(f,c) && !p.Trace!.MovementObserved, "unrelated motion cannot prove relocation");
        f.NativeMoveStarted = true;
        f.Visible = true;
        f.Distance = 12;
        Check(!p.Observe(f, c) && p.Trace!.MovementObserved && !p.Trace.Arrived,
            "nearby visible contact ends the move and preserves its final displacement");
        var history = p.History;
        Check(
            !history.Eligible("known-contact:1"),
            "elapsed delay alone cannot reroll"
        );
        history.Observe("known-contact:1", true, false, 0);
        Check(
            !history.Eligible("known-contact:1"),
            "source roll is once per enemy"
        );
        Check(
            !history.Eligible("known-contact:1"),
            "fresh fire cannot bypass cooldown"
        );
        p = P();
        Check(p.Begin(F(), c, Low), "new planning");
        p.End("relocate-no-better-cover");
        Check(!p.Begin(F(40), c, Low), "failed search does not spin identical opportunities");
        f = F(40);
        f.Known = new(4, 0, 30);
        Check(!p.Begin(f, c, Low), "moving contact cannot reroll source per-enemy chance");
        p = P();
        int before = rolls;
        Check(
            !p.Begin(F(), c, () => .7) && p.Trace!.Reason == "relocate-chose-to-stay",
            "70% boundary excluded"
        );
        Check(
            !p.Begin(F(50), c, Low) && rolls == before,
            "failed roll remains stable without a new cue"
        );
        foreach (double percent in new[] { 0d, 100d })
        {
            p = P();
            var cfg = new OutdoorRelocationConfig { Chance = percent };
            Check(p.Begin(F(), cfg, () => .999) == (percent == 100), "exact chance endpoint");
        }
        f = F();
        f.Hurt = true;
        Check(Math.Abs(OutdoorRelocation.Chance(f, c) - 91) < .001, "injury multiplier");
        f.LongWeapon = true;
        Check(OutdoorRelocation.Chance(f, c) == 100, "observed long weapon stacks and clamps");
        f = F();
        f.HealthyPusher = true;
        f.MatesEngaging = true;
        Check(
            Math.Abs(OutdoorRelocation.Chance(f, c) - 39.2) < .001,
            "healthy aggression and team support keep zzap adjustments"
        );
        foreach (
            var change in new Action<RelocationFrame>[]
            {
                f => f.Block = "ghost",
                f => f.Block = "remote-client",
                f => f.Block = "scav",
                f => f.Block = "medical",
                f => f.Outdoor = false,
                f => f.InCover = false,
                f => f.Visible = true,
                f => f.SightAge = 1.49,
                f => f.Source = "",
                f => f.Target = "",
                f => f.ContactAge = 30.1,
                f => f.Now = double.NaN,
                f => f.Position = new(double.PositiveInfinity, 0, 0),
                f => f.Known = new(0, 0, double.NaN),
                f => f.Speed = -1,
            }
        )
        {
            p = P();
            f = F();
            change(f);
            before = rolls;
            Check(
                !p.Begin(f, c, Low) && !p.Active && rolls == before,
                "ineligible frames do not consume chance"
            );
        }
        foreach (
            var change in new Action<RelocationCandidate>[]
            {
                v => v.Safe = false,
                v => v.PathLength = 26.1,
                v => v.Angle = double.NaN,
                v => v.PathLength = double.PositiveInfinity,
                v => v.Position = new(0, double.NaN, 0),
                v => v.Refusal = "other-known-threat",
                v => v.Protection = 4,
            }
        )
        {
            var v = C();
            change(v);
            Check(
                OutdoorRelocation.Reject(v, c).Length > 0,
                "unsafe or unhelpful destinations refused"
            );
        }
        var projected = C();
        projected.Distance = 4.9;
        Check(OutdoorRelocation.Reject(projected, c) == "", "a five-metre ring may snap slightly inward");
        var nearAngle = C();
        nearAngle.Angle = 15;
        nearAngle.Exposure = 0;
        Check(
            OutdoorRelocation.Reject(nearAngle, c) == "relocate-bearing-too-small",
            "source requires 25 degrees"
        );
        var pointless = C();
        pointless.Angle = 0;
        Check(
            OutdoorRelocation.Reject(pointless, c) == "relocate-bearing-too-small",
            "do not move just to move"
        );
        p = P();
        p.Begin(F(), c, Low);
        for (int i = 0; i < 80; i++)
            p.Candidate(new() { Id = "bad-" + i, Refusal = "blocked" }, c);
        Check(
            p.Trace!.Candidates == 80 && p.Trace.Rejected == 80 && p.Trace.Alternatives.Length == 8,
            "diagnostics bounded during candidate search"
        );
        p = P();
        p.Begin(F(), c, Low);
        p.Select(C(), c);
        p.Throw(true, 10.1, "native-accepted");
        Check(!p.CanMove(F(11)), "native throw acceptance is not release");
        p.Projectile(10.3);
        Check(!p.CanMove(F(10.49)), "minimum delay after observed release");
        f = F(10.6);
        f.HandsReady = false;
        Check(
            p.CanMove(f),
            "source release delay is sufficient without an additional hands-ready veto"
        );
        f.HandsReady = true;
        Check(
            p.CanMove(f),
            "move after real release and ready hands without waiting for explosion"
        );
        p = P();
        p.Begin(F(), c, Low);
        p.Select(C(), c);
        p.Throw(true, 10.1, "native-accepted");
        Check(
            p.Observe(F(16.2), c) && p.CanMove(F(16.2)),
            "source proceeds after four seconds even without projectile callback"
        );
        p = P();
        p.Begin(F(), c, Low);
        p.Select(C(), c);
        p.Throw(false, 10.1, "native-refused");
        Check(p.CanMove(F(10.2)), "refused grenade falls back to grenade-free movement");
        foreach (
            var scenario in new[]
            {
                "route",
                "contact",
                "visible",
                "clock",
                "stuck",
                "timeout",
                "floor",
                "origin",
                "session",
            }
        )
        {
            p = P();
            p.Begin(F(), c, Low);
            f = F(11);
            if (scenario != "origin" && scenario != "session")
            {
                p.Select(C(), c);
                p.MoveRequested(10.1, p.Origin);
            }
            switch (scenario)
            {
                case "route":
                    f.Route = RelocationRoute.Replaced;
                    break;
                case "contact":
                    f.Known = new(9, 0, 30);
                    break;
                case "visible":
                    f.Visible = true;
                    f.Distance = 30;
                    break;
                case "clock":
                    f.Now = 9;
                    break;
                case "stuck":
                    f.Now = 13.2;
                    break;
                case "timeout":
                    f.Now = 22.2;
                    f.Speed = 1;
                    f.Position = new(2, 0, 0);
                    break;
                case "origin":
                    f.Position = new(2, 0, 0);
                    break;
                case "session":
                    f.Now = 40.1;
                    break;
                case "floor":
                    f.Position = new(10, 2, 0);
                    f.NativeMoveStarted = false;
                    Check(!p.Observe(f, c) && !p.Trace!.Arrived, "horizontal arrival ends the plan without inventing a native move");
                    continue;
            }
            if (scenario is "contact" or "visible" or "stuck" or "origin")
                Check(p.Observe(f, c) && p.Active, "source plan survives: " + scenario);
            else
                Check(
                    !p.Observe(f, c) && !p.Active && p.Trace!.ValidFor == 0,
                    "source interruption: " + scenario
                );
        }
        var h = new RelocationHistory();
        p = P();
        p.Begin(F(), c, Low);
        p.Select(C(), c);
        p.MoveRequested(10, p.Origin);
        var same = p.Goal + new DoorPoint(.3, .1, .2);
        var elsewhere = p.Goal + new DoorPoint(0, 3, 0);
        var waiting = p.ClassifyRoute(null, null, null, false);
        Check(waiting == RelocationRoute.AwaitingPath, "a gap between native paths is recoverable");
        Check(p.TryBeginMoveOrder(10, waiting) && !p.TryBeginMoveOrder(10, waiting),
            "the native action and sampler share the first order");
        f = F(10.4);
        f.Position = new(2, 0, 0);
        f.Route = waiting;
        Check(p.Observe(f, c) && !p.Trace!.MovementObserved, "a path gap cannot prove movement");
        Check(!p.TryBeginMoveOrder(10.599, waiting) && p.TryBeginMoveOrder(10.6, waiting)
            && !p.TryBeginMoveOrder(10.6, waiting), "reissue after 0.6 s only once");
        Check(p.Trace!.MoveOrders == 2 && p.Trace.OrdersAwaitingPath == 1,
            "diagnostics distinguish reissues during path gaps from the initial order");
        Check(p.ClassifyRoute(same, elsewhere, same, true) == waiting,
            "queued replacement to the goal is pending while the outgoing path retires");
        Check(p.ClassifyRoute(same, same, null, false) == waiting,
            "an incomplete mesh is not evidence of travel or a takeover");
        f.Route = p.ClassifyRoute(null, same, null, true);
        Check(f.Route == RelocationRoute.ToSelectedCover && p.Observe(f, c) && p.Trace.MovementObserved,
            "a new native path to the same goal can continue and prove movement without a cover object");
        Check(p.TryBeginMoveOrder(11.3, f.Route) && p.Trace.OrdersAwaitingPath == 1,
            "keep refreshing a live route without counting a recovery");
        var takeover = p.ClassifyRoute(same, same, elsewhere, true);
        Check(takeover == RelocationRoute.Replaced
            && p.ClassifyRoute(elsewhere, same, null, true) == takeover
            && p.ClassifyRoute(null, elsewhere, null, true) == takeover,
            "different queued destination, selected cover or active path all yield, including another floor");
        f.Now = 12;
        f.Route = takeover;
        Check(!p.TryBeginMoveOrder(f.Now, takeover) && !p.Observe(f, c)
            && p.Trace.Reason == "relocate-native-route-replaced" && !p.TryBeginMoveOrder(13, waiting),
            "a real takeover ends the episode before another order can be sent");
        h.Observe("bot-first", false, true, 0);
        h.Observe("bot-first", true, true, .1);
        Check(
            !h.Eligible("bot-first"),
            "own first sight does not trigger the tactic"
        );
        h.Observe("never-seen", true, false, 0);
        Check(h.Eligible("never-seen"), "heard personal fire can precede vision");
        h.Observe("same-moment", true, true, .5);
        h.Observe("late", true, true, .51);
        Check(h.Eligible("same-moment") && !h.Eligible("late"), "source first-contact half-second boundary");
        for (int i = 0; i < 100; i++)
            h.Observe("id-" + i, true, false, 0);
        Check(h.Eligible("id-99"), "a later contact gets its own roll");
        h.Clear();
        Check(h.Count == 0, "raid history cleanup");
        p = P();
        p.Begin(F(), c, Low);
        p.Clear("ghost");
        Check(!p.Active && p.History.Count == 0, "scope release clears history");
        foreach (
            var prop in typeof(OutdoorRelocationConfig)
                .GetProperties()
                .Where(x => x.PropertyType == typeof(double))
        )
        foreach (double value in new[] { double.NaN, double.PositiveInfinity, -1, 1000 })
        {
            var cfg = new OutdoorRelocationConfig();
            prop.SetValue(cfg, value);
            Invalid(cfg.Validate);
        }
        Invalid(() => new OutdoorRelocationConfig { MinDistance = 15, MaxDistance = 5 }.Validate());
        Invalid(() => new OutdoorRelocationConfig { MaxPath = 10 }.Validate());
        Invalid(() =>
            new OutdoorRelocationConfig { MoveTimeout = 20, SessionTimeout = 10 }.Validate()
        );
        Invalid(() => new CometConfig { OutdoorRelocation = null! }.Validate());
        Check(
            JsonSerializer.Deserialize<CometConfig>("{}")!.OutdoorRelocation.Chance == 70,
            "old configs receive defaults"
        );
        var dir = Path.Combine(Path.GetTempPath(), "comet-m13-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "config.json");
        try
        {
            var store = new ConfigStore(file);
            var cfg = store.Read();
            cfg.OutdoorRelocation.Chance = 55;
            store.Save(cfg, cfg.Revision);
            Check(
                new ConfigStore(file).Read().OutdoorRelocation.Chance == 55,
                "server controls persist"
            );
            var previous = File.ReadAllText(file);
            cfg = store.Read();
            cfg.OutdoorRelocation.Chance = 101;
            Invalid(() => store.Save(cfg, cfg.Revision));
            Check(File.ReadAllText(file) == previous, "bad input is atomic");
        }
        finally
        {
            foreach (var path in new[] { file, file + ".bak" })
                if (File.Exists(path))
                    File.Delete(path);
            Directory.Delete(dir);
        }
        using (var pe = new PEReader(File.OpenRead("references/sain-4.5.1/SAIN.dll")))
        {
            var md = pe.GetMetadataReader();
            var types = md.TypeDefinitions.Select(t => md.GetTypeDefinition(t)).ToArray();
            foreach (
                var contract in new[]
                {
                    ("SAINCoverClass", "StopSeekingCover"),
                    ("SAINCoverClass", "FindCoverPoint"),
                    ("SAINMoverClass", "GoToCoverPoint"),
                    ("GrenadeThrowDecider", "TryThrowGrenade"),
                }
            )
                Check(
                    types
                        .Single(t => md.GetString(t.Name) == contract.Item1)
                        .GetMethods()
                        .Select(x => md.GetMethodDefinition(x))
                        .Any(x => md.GetString(x.Name) == contract.Item2),
                    "official native contract " + contract
                );
        }
        using (var pe = new PEReader(File.OpenRead("Client/bin/Release/netstandard2.1/COMET.dll")))
        {
            var md = pe.GetMetadataReader();
            var plugin = md
                .TypeDefinitions.Select(md.GetTypeDefinition)
                .Single(t => md.GetString(t.Name) == "Plugin");
            MethodDefinition Method(string name) =>
                plugin
                    .GetMethods()
                    .Select(md.GetMethodDefinition)
                    .Single(m => md.GetString(m.Name) == name);
            foreach (
                var gate in new[]
                {
                    "IsSupportedAi",
                    "get_Authority",
                    "Ghost",
                    "get_BotActive",
                    "get_GameEnding",
                    "get_SAINLayersActive",
                }
            )
                Check(
                    Calls(pe, md, Method("RelocationScope"), gate) == 1,
                    "compiled PMC authority gate " + gate
                );
            Check(
                Calls(pe, md, Method("RelocationPathInitialized"), "Remove") == 1,
                "native path reuse revokes previous ownership"
            );
            Check(
                Calls(pe, md, Method("RelocationFrame"), "get_PathStatus") == 0
                    && Calls(pe, md, Method("RelocationFrame"), "get_NavMeshPath") > 0,
                "cover mover uses real mesh, not unset status"
            );
            Check(
                Calls(pe, md, Method("RemainingRelocationRoute"), "get_PathPoints") == 0
                    && Calls(pe, md, Method("RemainingRelocationRoute"), "get_PathCorners") > 0,
                "validate actual native steering corners"
            );
            Check(
                Calls(pe, md, Method("RecheckRelocation"), "RemainingRelocationRoute") == 1,
                "actual route is safety checked, not only newly calculated alternatives"
            );
            Check(
                Calls(pe, md, Method("FinishRelocation"), "OwnRelocationRoute") >= 2,
                "only owned queued and active routes are cleaned"
            );
            Check(Calls(pe, md, Method("RelocationFrame"), "ClassifyRoute") == 1
                && Calls(pe, md, Method("RelocationFrame"), "QueuedDoorPath") == 1
                && Calls(pe, md, Method("RelocationFrame"), "RelocationControlsMovement") == 1,
                "real movement, queued replacement and decision owner feed the runtime policy");
            Check(Calls(pe, md, Method("FinishRelocation"), "RelocationControlsMovement") == 1,
                "an action that took over keeps its native movement even when it reused the previous path");
            Check(Calls(pe, md, Method("FindRelocationCover"), "OrderRelocationMove") == 1
                && Calls(pe, md, Method("RefreshRelocation"), "OrderRelocationMove") == 1
                && Calls(pe, md, Method("OrderRelocationMove"), "TryBeginMoveOrder") == 1
                && Calls(pe, md, Method("OrderRelocationMove"), "RunToPoint") == 1
                && Calls(pe, md, Method("OrderRelocationMove"), "WalkToPoint") == 1
                && Calls(pe, md, Method("OrderRelocationMove"), "EndRelocation") == 1,
                "initial selection and maintenance share native point orders and yield on native refusal");
            Check(
                Calls(pe, md, Method("ClearProjectileThrown"), "RelocationProjectile") == 1
                    && Calls(pe, md, Method("ClearProjectileExplosion"), "RelocationExplosion")
                        == 1,
                "actual projectile events integrated"
            );
            foreach (
                var method in new[]
                {
                    "ChooseRelocation",
                    "RefreshRelocation",
                    "RelocationFrame",
                    "EvaluateRelocationPoint",
                    "RelocationRouteSafety",
                }
            )
            foreach (
                var forbidden in new[]
                {
                    "Teleport",
                    "SetEnemyVisible",
                    "get_EnemyPosition",
                    "FindObjectsOfType",
                    "BlockFor",
                    "Shoot",
                }
            )
                Check(
                    Calls(pe, md, Method(method), forbidden) == 0,
                    "no teleport, forced shooting or hidden position: " + method + " " + forbidden
                );
        }
        var events = new List<JsonElement>();
        void Save() =>
            events.Add(
                JsonSerializer.Deserialize<JsonElement>(
                    Newtonsoft.Json.JsonConvert.SerializeObject(
                        p.Trace,
                        Comet.Client.Diagnostics.Json
                    )
                )
            );
        p = P();
        p.Begin(F(), c, Low);
        Save();
        p.Candidate(C(), c);
        p.Select(C(), c);
        p.NativeAccepted(true);
        Save();
        p.Throw(true, 10.1, "native-accepted");
        Save();
        p.Projectile(10.3);
        Save();
        p.MoveRequested(10.6, p.Origin);
        f = F(10.8);
        f.Position = new(1, 0, 0);
        f.Speed = 3;
        p.Observe(f, c);
        Save();
        f = F(12);
        f.Position = new(10, 0, 0);
        p.Observe(f, c);
        Save();
        Directory.CreateDirectory("artifacts");
        File.WriteAllText("artifacts/m13-wire-traces.json", JsonSerializer.Serialize(events));
        var random = new Random(1313);
        for (int i = 0; i < 200; i++)
        {
            var v = C();
            v.Angle = random.NextDouble() * 90;
            v.Exposure = random.NextDouble();
            v.PathLength = 5 + random.NextDouble() * 25;
            string reason = OutdoorRelocation.Reject(v, c);
            if (reason.Length == 0)
                Check(
                    v.Score(c) == -v.Distance && v.PathLength <= 26,
                    "ranked route always satisfies hard limits"
                );
            p = P();
            p.Begin(F(), c, Low);
            p.Select(C(), c);
            p.MoveRequested(10.1, p.Origin);
            f = F(10.2);
            f.Block = "priority-" + i;
            Check(
                !p.Observe(f, c) && p.Trace!.Reason == f.Block,
                "native interrupts release regardless of route ranking"
            );
        }
        Console.WriteLine($"M13: {n} assertions passed.");
        return n;
    }

    private static int Calls(PEReader pe, MetadataReader md, MethodDefinition method, string name)
    {
        var ops = typeof(OpCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(OpCode))
            .Select(f => (OpCode)f.GetValue(null)!)
            .ToDictionary(o => unchecked((ushort)o.Value));
        var il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()!;
        int count = 0;
        for (int i = 0; i < il.Length; )
        {
            ushort value = il[i++];
            if (value == 0xfe)
                value = (ushort)(0xfe00 | il[i++]);
            var op = ops[value];
            if (op == OpCodes.Call || op == OpCodes.Callvirt)
            {
                var h = MetadataTokens.EntityHandle(BitConverter.ToInt32(il, i));
                string called =
                    h.Kind == HandleKind.MethodDefinition
                        ? md.GetString(md.GetMethodDefinition((MethodDefinitionHandle)h).Name)
                    : h.Kind == HandleKind.MemberReference
                        ? md.GetString(md.GetMemberReference((MemberReferenceHandle)h).Name)
                    : "";
                if (called == name)
                    count++;
            }
            i += op.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget
                or OperandType.ShortInlineI
                or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, i),
                _ => 4,
            };
        }
        return count;
    }
}
