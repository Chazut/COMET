using Comet.Client;
using EFT;
using EFT.InventoryLogic;
using SAIN.Components;
using Newtonsoft.Json.Linq;
int count=0;void Check(bool ok,string why){count++;if(!ok)throw new Exception(why);}
(Plugin,BotComponent) Setup(bool grenade=false){var p=new Plugin();var b=new BotComponent();if(grenade)b.Player.HandsController=new Player.GrenadeHandsController();else{var g=(Player.FirearmController)b.Player.HandsController!;((Player.AbstractHandsController)g).Item!.Id=g.Item!.Id;b.BotOwner.WeaponManager.Reload.Reloading=true;}return(p,b);}
void Run(Plugin p,BotComponent b,double start,double end){for(double t=start;t<=end;t+=.5)p.Tick(b,t);}
foreach(var exclude in new Action<Plugin,BotComponent>[] {(p,b)=>p.Settings.Enabled=false,(p,b)=>p.Settings.HandsRecovery.Enabled=false,(p,b)=>p.Authority=false,(p,b)=>p.IsGhost=true,(p,b)=>b.Pmc=false,(p,b)=>b.IsDead=true,(p,b)=>b.GameEnding=true,(p,b)=>b.BotActive=false})
{var(p,b)=Setup();exclude(p,b);Run(p,b,0,20);Check(p.Count==0&&b.BotOwner.WeaponManager.Reload.Reloading,"excluded bots untouched");}
{
 var(p,b)=Setup();Run(p,b,0,5.5);Check(b.BotOwner.WeaponManager.Reload.Reloading,"no premature flag clear");p.Tick(b,6);
 Check(!b.BotOwner.WeaponManager.Reload.Reloading&&p.State(b).HandsRecovery.Policy.Pending,"flag changed, outcome not yet observed");p.Tick(b,6.5);
 Check(p.State(b).HandsRecovery.Policy.Trace!.State=="recovered"&&b.BotOwner.WeaponManager.Reload.FillCalls==0,"confirmed without reload callbacks/refill");
}
foreach(var protect in new Action<Plugin,BotComponent>[] {
 (p,b)=>((Player.FirearmController)b.Player.HandsController!).Reload=true,
 (p,b)=>((Player.FirearmController)b.Player.HandsController!).Interaction=true,
 (p,b)=>((Player.FirearmController)b.Player.HandsController!).CanReload=false,
 (p,b)=>b.BotOwner.Medecine.Using=true,(p,b)=>b.BotOwner.WeaponManager.Selector.IsChanging=true,
 (p,b)=>b.BotOwner.WeaponManager.Grenades.ThrowindNow=true,(p,b)=>b.Decision.CurrentCombatDecision="ThrowGrenade",
 (p,b)=>p.State(b).Fake.Policy.Active=true,(p,b)=>p.State(b).Fake.CallbackPending=true,(p,b)=>p.FeintGuard=true,
 (p,b)=>p.State(b).RoomClear.Policy.Active=true,(p,b)=>p.State(b).WeaponReturn.Policy.Active=true,
 (p,b)=>p.State(b).PistolSwap.Policy.Active=true,(p,b)=>p.State(b).QuickReload.Policy.Active=true,
 (p,b)=>b.Player.HandsController=new Player.AbstractHandsController()})
{var(p,b)=Setup();p.Tick(b,0);protect(p,b);Run(p,b,.5,60);Check(b.BotOwner.WeaponManager.Reload.Reloading&&b.BotOwner.WeaponManager.Selector.Calls==0,"legitimate native/COMET operation protected for 60 s");}
{
 var(p,b)=Setup();var gun=(Player.FirearmController)b.Player.HandsController!;Run(p,b,0,5);gun.Item!.Loaded++;p.Tick(b,5.5);Run(p,b,6,11);
 Check(b.BotOwner.WeaponManager.Reload.Reloading,"ammo progress resets confirmation");p.Tick(b,11.5);Check(!b.BotOwner.WeaponManager.Reload.Reloading,"six continuous idle seconds after progress");
}
foreach(bool accepted in new[]{false,true})
{var(p,b)=Setup(true);b.BotOwner.WeaponManager.Selector.Accept=accepted;Run(p,b,0,120);
 Check(b.BotOwner.WeaponManager.Selector.Calls==2&&p.State(b).HandsRecovery.Policy.Trace!.State=="exhausted","native refused/accepted but stuck bounded");
 Check(b.BotOwner.WeaponManager.Reload.FillCalls==0,"no native ammo refill on either attempt");
 b.BotOwner.WeaponManager.Reload.TryFillMagazines();Check(b.BotOwner.WeaponManager.Reload.FillCalls==1,"ordinary refill outside W10 unchanged");}
foreach(var protect in new Action<Plugin,BotComponent>[] {
 (p,b)=>((Player.GrenadeHandsController)b.Player.HandsController!).CurrentOperation=new Player.GrenadeHandsController.HighThrow(),
 (p,b)=>((Player.GrenadeHandsController)b.Player.HandsController!).WaitingForHighThrow=true,
 (p,b)=>((Player.GrenadeHandsController)b.Player.HandsController!).WaitingForLowThrow=true,
 (p,b)=>p.State(b).Fake.Policy.Active=true,(p,b)=>p.FeintGuard=true,(p,b)=>b.BotOwner.WeaponManager.Grenades.ThrowindNow=true})
{var(p,b)=Setup(true);p.Tick(b,0);protect(p,b);Run(p,b,.5,60);Check(b.BotOwner.WeaponManager.Selector.Calls==0,"committed/intentional grenades protected");}
{
 var(p,b)=Setup(true);Run(p,b,0,2);var r=p.State(b).HandsRecovery;Check(r.Policy.BlocksSwitch&&!r.Policy.Trace!.Ready,"request acceptance not success");
 Check(!b.BotOwner.WeaponManager.Selector.TryChangeToSlot(EquipmentSlot.Holster,true)&&b.BotOwner.WeaponManager.Selector.Calls==1,"W03/other native request cannot compete");
 b.Player.HandsController=new Player.FirearmController();((Player.AbstractHandsController)b.Player.HandsController).Item!.Id="rifle";b.BotOwner.WeaponManager.Selector.IsChanging=true;p.Tick(b,2.5);
 Check(r.Policy.Pending&&r.Policy.Trace!.InHands&&!r.Policy.Trace.Ready,"gun present but draw incomplete");b.BotOwner.WeaponManager.Selector.IsChanging=false;p.Tick(b,3);
 Check(r.Policy.Trace!.Ready&&!r.Policy.BlocksSwitch,"ready observation releases arbiter");
 Check(b.BotOwner.WeaponManager.Selector.TryChangeToSlot(EquipmentSlot.Holster,true),"ordinary native requests resume");
 var ev=p.Events.Select(JObject.Parse).ToArray();Check(ev.Any(e=>(string?)e["handsRecovery"]?["state"]=="recovered")&&ev.All(e=>(string?)e["feature"]=="W10"),"wire includes request and actual outcome");
 var dir=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../artifacts"));File.WriteAllLines(Path.Combine(dir,"w10-adapter-events.jsonl"),p.Events);
}
{
 var(p,b)=Setup(true);b.Player.Equipment.Clear();Run(p,b,0,20);Check(b.BotOwner.WeaponManager.Selector.Calls==0,"no weapon inventory synthesis");
}
{
 var(p,b)=Setup(true);Run(p,b,0,2);p.IsGhost=true;p.Tick(b,2.1);var r=p.State(b).HandsRecovery;
 Check(!r.Policy.Pending&&r.Hands==null&&r.Operation==null&&r.Reload==null,"scope loss immediately releases refs and pending ownership before sampling interval");
}
{
 var(p,b)=Setup(true);b.BotOwner.WeaponManager.Selector.Throw=true;Run(p,b,0,5);
 Check(p.State(b).HandsRecovery.Fault&&!p.State(b).HandsRecovery.Issuing&&!p.State(b).HandsRecovery.Policy.Pending,"exception cannot leak guard/lease");
 b.BotOwner.WeaponManager.Reload.TryFillMagazines();Check(b.BotOwner.WeaponManager.Reload.FillCalls==1,"refill guard restored after throw");
}
{
 var(p,b)=Setup();p.Tick(b,0);p.LogThrows=true;p.Tick(b,.5);Check(p.State(b).HandsRecovery.Fault&&b.BotOwner.WeaponManager.Reload.Reloading,"logging failure yields without mutation");
}
Console.WriteLine($"W10 production adapter: {count} checks passed");
