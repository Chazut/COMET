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
            int checks=0;
            void Check(bool ok,string name){checks++;if(!ok)throw new Exception(name);}
            (Plugin p,BotComponent b,BotState s,LootItem loot) Setup()
            {
                var p=new Plugin();Instance=p;var b=new BotComponent();SAINEnableClass.Bot=b;
                var s=new BotState(b);p.states.Add(b,s);var loot=new LootItem("actual-drop");
                Time.time=0;Physics.Obstacle=null;Authority=true;Sleeping=false;Api.Decisions.Events.Clear();
                p.RememberDroppedMagazine(s,loot);return(p,b,s,loot);
            }
            void Arrive(BotComponent b){RunMagazineRecovery(b.BotOwner,true);b.Player.CurrentManagedState.Callback!();RunMagazineRecovery(b.BotOwner,true);}
            var (p,b,s,loot)=Setup();
            b.Danger=true;Check(!MagazineRecoveryActive(b.BotOwner),"no recovery under fire");
            b.Danger=false;Check(MagazineRecoveryActive(b.BotOwner),"resume after combat");
            Arrive(b);Check(b.Player.InventoryController.Calls==1,"one native transaction");
            RunMagazineRecovery(b.BotOwner,true);Check(b.Player.InventoryController.Calls==1,"in-flight pickup must not be duplicated");
            Check(s.MagazineRecovery.Pending.Count==1,"do not report completion before native result");
            b.Player.Inventory.Equipment.Items.Add(loot.Item);b.Player.InventoryController.Completion.SetResult(new Result(true));
            Check(!MagazineRecoveryActive(b.BotOwner)&&s.MagazineRecovery.Pending.Count==0,"actual magazine confirmed and queue released");
            Check(Api.Decisions.Events.Any(e=>e.Contains("\"stage\":\"completed\"")),"RR receives confirmed completion");

            (p,b,s,loot)=Setup();Arrive(b);b.Player.InventoryController.Completion.SetResult(new Result(true));
            MagazineRecoveryActive(b.BotOwner);Check(Api.Decisions.Events.Any(e=>e.Contains("not confirmed")),"success callback alone is insufficient");
            (p,b,s,loot)=Setup();Arrive(b);b.Player.InventoryController.Completion.SetException(new InvalidOperationException("inventory rejected"));
            MagazineRecoveryActive(b.BotOwner);Check(s.MagazineRecovery.Pending.Count==0,"faulted transaction is observed and released");
            (p,b,s,loot)=Setup();Physics.Obstacle=new Collider();RunMagazineRecovery(b.BotOwner,true);
            Check(b.Player.CurrentManagedState.Starts==0&&b.Player.InventoryController.Calls==0,"wall blocks remote pickup");
            (p,b,s,loot)=Setup();Physics.Obstacle=new Collider{Parent=loot};Arrive(b);
            Check(b.Player.InventoryController.Calls==1,"own magazine collider is not a wall");
            (p,b,s,loot)=Setup();b.Player.InventoryController.HasSpace=false;Arrive(b);
            Check(b.Player.InventoryController.Calls==0&&s.MagazineRecovery.Pending.Count==0,"no space uses no transaction");
            (p,b,s,loot)=Setup();RunMagazineRecovery(b.BotOwner,true);var oldCallback=b.Player.CurrentManagedState.Callback!;
            b.Danger=true;Check(!MagazineRecoveryActive(b.BotOwner)&&b.Player.CurrentManagedState.Stops==1,"combat cancels pickup pose");
            oldCallback();Check(!s.MagazineRecovery.Ready&&s.MagazineRecovery.Pending.Count==1,"late callback cannot reactivate cancelled pickup");
            b.Danger=false;Arrive(b);Check(b.Player.InventoryController.Calls==1,"retry original magazine after fight");
            (p,b,s,loot)=Setup();loot.transform.position=new(10,0,0);RunMagazineRecovery(b.BotOwner,true);
            var owned=(BotPathDataManual)b.Mover.ActivePath!;b.Danger=true;MagazineRecoveryActive(b.BotOwner);
            Check(owned.Stops==1&&s.MagazineRecovery.Pending.Count==1,"yield owned route and retain item");
            (p,b,s,loot)=Setup();loot.transform.position=new(10,0,0);RunMagazineRecovery(b.BotOwner,true);
            var combatPath=new BotPathDataManual();b.Mover.ActivePath=combatPath;RunMagazineRecovery(b.BotOwner,false);
            Check(combatPath.Stops==0,"never stop a newer combat route");
            (p,b,s,loot)=Setup();loot.gameObject.activeInHierarchy=false;
            Check(!MagazineRecoveryActive(b.BotOwner)&&s.MagazineRecovery.Pending.Count==0,"someone else took the drop");
            (p,b,s,loot)=Setup();Sleeping=true;Check(!MagazineRecoveryActive(b.BotOwner)&&s.MagazineRecovery.Pending.Count==1,"ghost pauses pickup");
            Sleeping=false;b.IsDead=true;Check(!MagazineRecoveryActive(b.BotOwner)&&s.MagazineRecovery.Pending.Count==0,"death releases loot reference");
            (p,b,s,loot)=Setup();RunMagazineRecovery(b.BotOwner,true);Time.time=9;RunMagazineRecovery(b.BotOwner,true);
            Check(s.MagazineRecovery.Pending.Count==0&&b.Player.InventoryController.Calls==0,"missing animation callback terminates cleanly");
            (p,b,s,loot)=Setup();Arrive(b);Time.time=20;
            Check(!MagazineRecoveryActive(b.BotOwner)&&s.MagazineRecovery.Transaction!=null,"delayed transaction yields movement but retains identity");
            RunMagazineRecovery(b.BotOwner,true);
            Check(b.Player.InventoryController.Calls==1,"never duplicate a delayed transaction");
            b.Player.Inventory.Equipment.Items.Add(loot.Item);
            b.Player.InventoryController.Completion.SetResult(new Result(true));
            MagazineRecoveryActive(b.BotOwner);
            Check(s.MagazineRecovery.Pending.Count==0,"late successful transaction is still confirmed");
            Console.WriteLine($"Magazine recovery adapter: {checks} checks passed.");
        }
    }
}
