using Comet.Client;
using EFT.Interactive;
using SAIN;
using SAIN.Components;
using SAIN.SAINComponent.Classes.Mover;
using UnityEngine;

Plugin.TestRecovery();

namespace Comet.Client
{
    public sealed partial class Plugin
    {
        public static void TestRecovery()
        {
            int checks = 0;
            void Check(bool ok, string name)
            {
                checks++;
                if (!ok)
                    throw new Exception(name);
            }
            (Plugin p, BotComponent b, BotState s, LootItem loot) Setup()
            {
                var p = new Plugin();
                Instance = p;
                var b = new BotComponent();
                SAINEnableClass.Bot = b;
                var s = new BotState(b);
                p.states.Add(b, s);
                var loot = new LootItem("actual-drop");
                Time.time = 0;
                Physics.Obstacle = null;
                p.Authority = true;
                Sleeping = false;
                Api.Decisions.Events.Clear();
                p.RememberDroppedMagazine(s, loot);
                return (p, b, s, loot);
            }
            void Arrive(BotComponent b)
            {
                RunMagazineRecovery(b.BotOwner, true);
                b.Player.CurrentManagedState.Callback!();
                RunMagazineRecovery(b.BotOwner, true);
            }
            var (p, b, s, loot) = Setup();
            b.Danger = true;
            Check(!MagazineRecoveryActive(b.BotOwner), "no recovery under fire");
            b.Danger = false;
            Check(MagazineRecoveryActive(b.BotOwner), "resume after combat");
            Arrive(b);
            Check(b.Player.InventoryController.Calls == 1, "one native transaction");
            RunMagazineRecovery(b.BotOwner, true);
            Check(
                b.Player.InventoryController.Calls == 1,
                "in-flight pickup must not be duplicated"
            );
            Check(
                s.MagazineRecovery.Pending.Count == 1,
                "do not report completion before native result"
            );
            b.Player.Inventory.Equipment.Items.Add(loot.Item);
            b.Player.InventoryController.Completion.SetResult(new Result(true));
            Check(
                !MagazineRecoveryActive(b.BotOwner) && s.MagazineRecovery.Pending.Count == 0,
                "actual magazine confirmed and queue released"
            );
            Check(
                Api.Decisions.Events.Any(e => e.Contains("\"stage\":\"completed\"")),
                "RR receives confirmed completion"
            );

            (p, b, s, loot) = Setup();
            Arrive(b);
            b.Player.InventoryController.Completion.SetResult(new Result(true));
            MagazineRecoveryActive(b.BotOwner);
            Check(
                Api.Decisions.Events.Any(e => e.Contains("not confirmed")),
                "success callback alone is insufficient"
            );
            (p, b, s, loot) = Setup();
            Arrive(b);
            b.Player.InventoryController.Completion.SetException(
                new InvalidOperationException("inventory rejected")
            );
            MagazineRecoveryActive(b.BotOwner);
            Check(
                s.MagazineRecovery.Pending.Count == 0,
                "faulted transaction is observed and released"
            );
            (p, b, s, loot) = Setup();
            Physics.Obstacle = new Collider();
            RunMagazineRecovery(b.BotOwner, true);
            Check(
                b.Player.CurrentManagedState.Starts == 0 && b.Player.InventoryController.Calls == 0,
                "wall blocks remote pickup"
            );
            (p, b, s, loot) = Setup();
            Physics.Obstacle = new Collider { Parent = loot };
            Arrive(b);
            Check(b.Player.InventoryController.Calls == 1, "own magazine collider is not a wall");
            (p, b, s, loot) = Setup();
            b.Player.InventoryController.HasSpace = false;
            Arrive(b);
            Check(
                b.Player.InventoryController.Calls == 0 && s.MagazineRecovery.Pending.Count == 0,
                "no space uses no transaction"
            );
            (p, b, s, loot) = Setup();
            RunMagazineRecovery(b.BotOwner, true);
            var oldCallback = b.Player.CurrentManagedState.Callback!;
            b.Danger = true;
            Check(
                !MagazineRecoveryActive(b.BotOwner) && b.Player.CurrentManagedState.Stops == 1,
                "combat cancels pickup pose"
            );
            oldCallback();
            Check(
                !s.MagazineRecovery.Ready && s.MagazineRecovery.Pending.Count == 1,
                "late callback cannot reactivate cancelled pickup"
            );
            b.Danger = false;
            Arrive(b);
            Check(b.Player.InventoryController.Calls == 1, "retry original magazine after fight");
            (p, b, s, loot) = Setup();
            loot.transform.position = new(10, 0, 0);
            RunMagazineRecovery(b.BotOwner, true);
            Check(
                SteerMagazineRecovery(b.BotOwner)
                    && b.Steering.RouteLooks == 1
                    && b.Steering.PointLooks == 0,
                "return trip follows native route corners rather than old enemy memory"
            );
            b.Steering.Moving = false;
            Check(
                SteerMagazineRecovery(b.BotOwner) && b.Steering.Point.x == 10,
                "before native movement begins face the actual magazine"
            );
            b.Danger = true;
            int looks = b.Steering.RouteLooks + b.Steering.PointLooks;
            Check(
                !SteerMagazineRecovery(b.BotOwner)
                    && looks == b.Steering.RouteLooks + b.Steering.PointLooks,
                "fresh danger keeps native combat steering"
            );
            b.Danger = false;
            Sleeping = true;
            Check(!SteerMagazineRecovery(b.BotOwner), "ghost recovery never steers a body");
            Sleeping = false;
            p.Authority = false;
            Check(!SteerMagazineRecovery(b.BotOwner), "Fika replica recovery never steers");
            p.Authority = true;
            var owned = (BotPathDataManual)b.Mover.ActivePath!;
            b.Danger = true;
            MagazineRecoveryActive(b.BotOwner);
            Check(
                owned.Stops == 1 && s.MagazineRecovery.Pending.Count == 1,
                "yield owned route and retain item"
            );
            (p, b, s, loot) = Setup();
            loot.transform.position = new(10, 0, 0);
            RunMagazineRecovery(b.BotOwner, true);
            var combatPath = new BotPathDataManual();
            b.Mover.ActivePath = combatPath;
            RunMagazineRecovery(b.BotOwner, false);
            Check(combatPath.Stops == 0, "never stop a newer combat route");
            (p, b, s, loot) = Setup();
            loot.transform.position = new(10, 0, 0);
            RunMagazineRecovery(b.BotOwner, true);
            b.Mover.ActivePath = new BotPathDataManual();
            Check(!SteerMagazineRecovery(b.BotOwner), "new movement owner keeps its steering");
            (p, b, s, loot) = Setup();
            RunMagazineRecovery(b.BotOwner, true);
            Check(
                SteerMagazineRecovery(b.BotOwner) && b.Steering.PointLooks == 1,
                "at pickup range face magazine instead of old target"
            );
            (p, b, s, loot) = Setup();
            loot.gameObject.activeInHierarchy = false;
            Check(
                !MagazineRecoveryActive(b.BotOwner) && s.MagazineRecovery.Pending.Count == 0,
                "someone else took the drop"
            );
            (p, b, s, loot) = Setup();
            Sleeping = true;
            Check(
                !MagazineRecoveryActive(b.BotOwner) && s.MagazineRecovery.Pending.Count == 1,
                "ghost pauses pickup"
            );
            Sleeping = false;
            b.IsDead = true;
            Check(
                !MagazineRecoveryActive(b.BotOwner) && s.MagazineRecovery.Pending.Count == 0,
                "death releases loot reference"
            );
            (p, b, s, loot) = Setup();
            RunMagazineRecovery(b.BotOwner, true);
            Time.time = 9;
            RunMagazineRecovery(b.BotOwner, true);
            Check(
                s.MagazineRecovery.Pending.Count == 0 && b.Player.InventoryController.Calls == 0,
                "missing animation callback terminates cleanly"
            );
            (p, b, s, loot) = Setup();
            Arrive(b);
            Time.time = 20;
            Check(
                !MagazineRecoveryActive(b.BotOwner) && s.MagazineRecovery.Transaction != null,
                "delayed transaction yields movement but retains identity"
            );
            RunMagazineRecovery(b.BotOwner, true);
            Check(b.Player.InventoryController.Calls == 1, "never duplicate a delayed transaction");
            b.Player.Inventory.Equipment.Items.Add(loot.Item);
            b.Player.InventoryController.Completion.SetResult(new Result(true));
            MagazineRecoveryActive(b.BotOwner);
            Check(
                s.MagazineRecovery.Pending.Count == 0,
                "late successful transaction is still confirmed"
            );

            (p, b, s, loot) = Setup();
            var movement = b.Player.MovementContext;
            movement.NativeState = movement.MoveState;
            RunMagazineRecovery(b.BotOwner, true);
            Check(
                !s.MagazineRecovery.Picking && movement.Starts == 0 && b.Player.StopInputs == 1,
                "raid454: moving Pickup is a no-op, arrival does not claim animation started"
            );
            Check(
                Api.Decisions.Events.Any(e => e.Contains("pickup-wait"))
                    && !Api.Decisions.Events.Any(e => e.Contains("pickup-started")),
                "ignored native call is diagnosed accurately"
            );
            int before = Api.Decisions.Events.Count;
            for (int i = 0; i < 100; i++)
                RunMagazineRecovery(b.BotOwner, true);
            Check(
                Api.Decisions.Events.Count == before,
                "unchanged rejected movement state does not spam logs or retry callbacks"
            );
            movement.NativeState = movement.IdleState;
            Time.time = .25f;
            RunMagazineRecovery(b.BotOwner, true);
            Check(
                s.MagazineRecovery.Picking && movement.Starts == 1,
                "transition to native idle retries and actually enters pickup"
            );
            Check(
                b.Player.InventoryController.Calls == 0,
                "entering pickup alone never grants the magazine"
            );
            movement.PickupAction!();
            RunMagazineRecovery(b.BotOwner, true);
            Check(
                b.Player.InventoryController.Calls == 1,
                "native callback unlocks the original item transaction"
            );

            (p, b, s, loot) = Setup();
            movement = b.Player.MovementContext;
            movement.NativeState = movement.MoveState;
            RunMagazineRecovery(b.BotOwner, true);
            Time.time = 9;
            RunMagazineRecovery(b.BotOwner, true);
            Check(
                s.MagazineRecovery.Pending.Count == 0 && movement.Stops == 0,
                "unentered animation expires without cancelling another movement state"
            );
            Check(
                Api.Decisions.Events.Any(e => e.Contains("native pickup state not entered")),
                "unentered state differs from missing callback"
            );

            (p, b, s, loot) = Setup();
            RunMagazineRecovery(b.BotOwner, true);
            movement = b.Player.MovementContext;
            Action other = () => { };
            movement.PickupAction = other;
            b.Danger = true;
            MagazineRecoveryActive(b.BotOwner);
            Check(
                movement.Stops == 0 && movement.PickupAction == other,
                "interruption never cancels a replacement pickup operation"
            );

            (p, b, s, loot) = Setup();
            var second = new LootItem("second-drop");
            second.transform.position = new(9, 2, 3);
            p.RememberDroppedMagazine(s, second);
            Check(
                Api.Decisions.Events.Last().Contains("\"x\":9")
                    && Api.Decisions.Events.Last().Contains("second-drop"),
                "queued telemetry uses that magazine position, not the first pending drop"
            );
            (p, b, s, loot) = Setup();
            RunMagazineRecovery(b.BotOwner, true);
            movement = b.Player.MovementContext;
            var stale = movement.PickupAction;
            movement.OverridenState = null;
            RunMagazineRecovery(b.BotOwner, true);
            stale!();
            Check(
                !s.MagazineRecovery.Ready && movement.Starts == 2,
                "interrupted native animation retries and rejects stale callback"
            );
            p.Authority = false;
            Check(
                !MagazineRecoveryActive(b.BotOwner) && s.MagazineRecovery.Pending.Count == 1,
                "Fika client yields native recovery and retains original drop"
            );
            Check(movement.PickupAction == null, "cancel releases owned native callback");
            Console.WriteLine($"Magazine recovery adapter: {checks} checks passed.");
        }
    }
}
