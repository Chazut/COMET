global using SAIN.Helpers;
using Comet.Core;
using EFT;
using EFT.InventoryLogic;
using SAIN.Components;
using UnityEngine;

namespace Comet.Client;

public sealed partial class Plugin
{
    internal static int TestBeltReload()
    {
        int n=0;void Check(bool ok,string why){n++;if(!ok)throw new Exception("Belt reload: "+why);}
        var p=Instance=new Plugin();
        var b=new BotComponent();b.Player.ProfileId="belt-bot";b.BotOwner.GetPlayer=b.Player;
        SAIN.SAINEnableClass.Bots[b.ProfileId]=b;
        var w=new Weapon{Magazine=new Magazine{Count=0},ChamberAmmoCount=0};
        var reload=new BotReload{_player=b.Player,ShootController=new(){Item=w}};
        var belt=new ContainerCollection{Containers=new(){new Container()}};
        b.Player.Inventory.Equipment.Belt.Item=belt;
        var mags=Enumerable.Range(0,3).Select(i=>new Magazine{Id="gen1-"+i,Root=EquipmentSlot.ArmBand,Count=20}).ToArray();
        belt.Containers[0].Items.AddRange(mags);
        var list=new List<Magazine>();
        b.Player.InventoryController.GetReachableItemsOfTypeNonAlloc(list);
        Check(list.Count==0,"reproduce XHB5: base slots cannot see any of three belt magazines");
        Check(ReloadInventory.ReadMagazines(b.Player.InventoryController,list)&&list.Count==3,"production reader discovers direct belt contents");
        var s=p.JournalState(b.Player)!;
        var f=p.ReloadResources(b,s,w,new(){Now=1,Weapon="m4",Capacity=21},true);
        Check(f.Reserve==20&&f.Resource=="magazine"&&f.ResourceLocation=="ArmBand"&&f.BeltMagazines==3,"W01 finds a loaded magazine, not just spare loose ammo");
        Check(f.ResourcesChecked&&f.MagazineCandidates==3&&f.RejectedMagazines==0,"diagnostic exposes candidate origin and count");
        var policy=new ReloadTimingPolicy();policy.Evaluate(f,new());
        Check(policy.Trace!.ResourceLocation=="ArmBand"&&policy.Trace.BeltMagazines==3,"diagnostic survives serialization model");
        var trace=Newtonsoft.Json.Linq.JObject.Parse(Diagnostics.Serialize(policy.Trace));
        Check((string?)trace["resourceLocation"]=="ArmBand"&&(int?)trace["magazineCandidates"]==3,"new evidence is present in serialized logs");
        s.ReloadTiming.Magazines.Clear();s.ReloadTiming.Ammo.Clear();
        Magazine chosen=null!;ChooseBeltMagazine(reload,w,ref chosen);
        Check(chosen==mags[0],"native reload receives the real loaded belt magazine");
        Check(s.ReloadTiming.BeltMagazines.Count==0,"native scratch inventory cleared");
        var better=new Magazine{Count=30};chosen=better;ChooseBeltMagazine(reload,w,ref chosen);
        Check(chosen==better,"keep better native candidate");
        mags[0].Movable=false;mags[1].Count=0;chosen=null!;ChooseBeltMagazine(reload,w,ref chosen);
        Check(chosen==mags[2],"locked and empty magazines never selected");
        w.MagSlot.Accept=false;chosen=null!;ChooseBeltMagazine(reload,w,ref chosen);
        Check(chosen==null,"incompatible magazines remain refused");
        f=p.ReloadResources(b,s,w,new(){Capacity=21},true);
        Check(f.RejectedMagazines==2&&f.NonImprovingMagazines==1&&f.ResourceRejection.Length>0,"exact refusal class logged");
        s.ReloadTiming.Magazines.Clear();w.MagSlot.Accept=true;mags[0].Movable=true;
        foreach(var m in mags)m.Count=0;
        Time.time=10;RefillBeltMagazines(reload);
        Check(reload.Refilled==3&&mags.All(m=>m.Count==20),"empty belt magazines use existing native refill API");
        RefillBeltMagazines(reload);Check(reload.Refilled==3,"no repeated refill in same interval");
        foreach(var m in mags)m.Count=0;
        b.Player.InventoryController.Reachable.AddRange(mags);Time.time=13;RefillBeltMagazines(reload);
        Check(reload.Refilled==3,"no duplicate refill when another mod already makes belt reachable");
        list.Clear();Check(ReloadInventory.ReadMagazines(b.Player.InventoryController,list)&&list.Count==3,"no duplicate candidates with expanded native slots");
        b.Player.InventoryController.Reachable.Clear();
        var nested=new ContainerCollection{Containers=new(){new Container{Items=new(){new Magazine{Count=100}}}}};
        belt.Containers[0].Items.Add(nested);list.Clear();ReloadInventory.ReadMagazines(b.Player.InventoryController,list);
        Check(list.Count==3,"no traversal of nested bags or cases");
        foreach(var mode in new[]{"human","non-pmc","replica","ghost","downed","disabled","dead"})
        {
            b.Player.IsAI=mode!="human";b.Pmc=mode!="non-pmc";p.Authority=mode!="replica";p.Sleep=mode=="ghost";
            b.Player.Downed=mode=="downed";p.config!.Enabled=mode!="disabled";b.IsDead=mode=="dead";
            mags[0].Count=20;chosen=null!;ChooseBeltMagazine(reload,w,ref chosen);
            Check(chosen==null,"native result unchanged outside scope: "+mode);
            Time.time+=3;int old=reload.Refilled;RefillBeltMagazines(reload);Check(old==reload.Refilled,"no inventory writes outside scope: "+mode);
        }
        b.Player.IsAI=b.Pmc=p.Authority=p.config!.Enabled=true;p.Sleep=b.Player.Downed=b.IsDead=false;
        for(int i=0;i<129;i++)belt.Containers[0].Items.Add(new Magazine());
        list.Clear();Check(!ReloadInventory.ReadMagazines(b.Player.InventoryController,list)&&list.Count<=128,"bounded modded inventory scan reports truncation");
        chosen=null!;ChooseBeltMagazine(reload,w,ref chosen);Check(chosen==null&&s.ReloadTiming.BeltMagazines.Count==0,"partial scan cannot authorize native reload");
        belt.Containers[0].Items.RemoveRange(3,belt.Containers[0].Items.Count-3);
        reload.FailFill=true;Time.time+=3;mags[0].Count=0;RefillBeltMagazines(reload);
        Check(s.ReloadTiming.InventoryFault&&s.ReloadTiming.BeltMagazines.Count==0,"transaction failure safely disables compatibility for this bot");
        Console.WriteLine($"W01 belt resource production adapters: {n} checks passed (platform doubles).");return n;
    }
}
