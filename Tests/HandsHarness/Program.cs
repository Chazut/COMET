using Comet.Client;
using EFT;
using EFT.InventoryLogic;
using SAIN.Components;
int count=0;void Check(bool ok,string why){count++;if(!ok)throw new Exception(why);}
(Plugin,BotComponent) Setup(){var p=new Plugin();var b=new BotComponent();b.BotOwner.WeaponManager.Reload.Reloading=true;return(p,b);}
void Run(Plugin p,BotComponent b,double start,double end){for(double t=start;t<=end;t+=.5)p.Tick(b,t);}
foreach(var exclude in new Action<Plugin,BotComponent>[] {(p,b)=>p.Settings.Enabled=false,(p,b)=>p.Settings.HandsRecovery.Enabled=false,(p,b)=>p.Authority=false,(p,b)=>p.IsGhost=true,(p,b)=>b.Pmc=false,(p,b)=>b.IsDead=true,(p,b)=>b.GameEnding=true,(p,b)=>b.BotActive=false})
{var(p,b)=Setup();exclude(p,b);Run(p,b,0,20);Check(p.Count==0&&b.BotOwner.WeaponManager.Reload.Reloading,"scope preserves native state");}
{
 var(p,b)=Setup();Run(p,b,0,5.5);Check(b.BotOwner.WeaponManager.Reload.Reloading,"six seconds before stale flag clear");p.Tick(b,6.5);
 Check(!b.BotOwner.WeaponManager.Reload.Reloading&&!p.State(b).HandsRecovery.Policy.Pending,"source clears flag immediately, no acknowledgement lease");
 Check(p.State(b).HandsRecovery.Policy.Trace!.State=="recovered"&&b.BotOwner.WeaponManager.Reload.FillCalls==0,"flag recovery does not refill");
}
{
 var(p,b)=Setup();var gun=(Player.FirearmController)b.Player.HandsController!;gun.Reload=true;Run(p,b,0,60);
 Check(b.BotOwner.WeaponManager.Reload.Reloading,"actual native reload never interrupted");gun.Reload=false;p.Tick(b,60.5);
 Check(!b.BotOwner.WeaponManager.Reload.Reloading,"source timer runs during native reload");
}
foreach(var noise in new Action<Plugin,BotComponent>[] {(p,b)=>b.BotOwner.Medecine.Using=true,(p,b)=>b.Player.InventoryController.IsChangingWeapon=true,(p,b)=>p.State(b).Fake.Policy.Active=true,(p,b)=>p.FeintGuard=true})
{var(p,b)=Setup();p.Tick(b,0);noise(p,b);Run(p,b,0,7);Check(!b.BotOwner.WeaponManager.Reload.Reloading,"no added unrelated recovery veto");}
{
 var(p,b)=Setup();b.Player.HandsController=new Player.GrenadeHandsController();b.BotOwner.WeaponManager.Reload.Reloading=false;Run(p,b,0,60);
 Check(b.BotOwner.WeaponManager.Selector.Calls==0,"no COMET grenade hand restoration");
}
{
 var(p,b)=Setup();for(int attempt=0;attempt<5;attempt++){b.BotOwner.WeaponManager.Reload.Reloading=true;Run(p,b,attempt*8,attempt*8+7);Check(!b.BotOwner.WeaponManager.Reload.Reloading,"no attempt cap");}
 bool result=false;Check(Plugin.SwitchAllowed(b.BotOwner.WeaponManager.Selector,ref result),"no switch lock");Check(Plugin.FillAllowed(b.BotOwner.WeaponManager.Reload),"no refill lock");
}
Console.WriteLine($"W10 production source watchdog: {count} checks passed; no Unity raid.");
