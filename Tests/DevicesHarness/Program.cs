using Comet.Client;
using Comet.Core;
using EFT;
using EFT.InventoryLogic;
using SAIN;
using SAIN.Components;
using SAIN.SAINComponent.Classes.WeaponFunction;
using UnityEngine;
using Newtonsoft.Json.Linq;

Plugin.Patch();int n=0;var events=new List<string>();void C(bool v,string why){n++;if(!v)throw new Exception("L02 adapter: "+why);}
(Plugin p,BotComponent b,BotLightController l,Player.FirearmController h) Setup(int count=2,bool target=true)
{
 Time.time=Time.realtimeSinceStartup=10;var p=new Plugin();p.config!.LightDiscipline.Enabled=false;var b=new BotComponent();b.Player.ProfileId=b.ProfileId;SAINEnableClass.Bots.Clear();SAINEnableClass.Bots[b.ProfileId]=b;
 var h=new Player.FirearmController{_player=b.Player};b.Player.HandsController=h;b.BotOwner.WeaponManager.CurrentWeapon=h.Item;
 for(int i=0;i<count;i++){var d=new LightComponent{IsActive=!target,SelectedMode=i+2};d.Item.ShortName=i%2==0?"test-device":"test-laser";h.Item.Mods.Add(d.Item);}return(p,b,new(b){Want=target},h);
}
void Tick(BotLightController l,float dt=0){Time.time+=dt;Time.realtimeSinceStartup=Time.time;l.Tick();}
void Save(Plugin p)=>events.AddRange(p.diagnostics!.Lines);
foreach(int count in new[]{0,1,2,5,16})foreach(bool target in new[]{false,true})
{
 var(p,b,l,h)=Setup(count,target);int localizations=TestItemLocalization.Calls;Tick(l);C(h.Commands.Count==(count==0?0:1),"one command or none");
 C(h.Item.Mods.All(x=>x.Light!.IsActive==target),"all accessories reached target");
 C(h.Item.Mods.Select(x=>x.Light!.SelectedMode).SequenceEqual(Enumerable.Range(2,count)),"visible/IR modes preserved");
 for(int i=0;i<50;i++)Tick(l,.05f);C(h.Commands.Count==(count==0?0:1),"no repeated commands");C(l.NativeCalls==0&&l.Next==0,"native preference/timer unchanged");
 C(TestItemLocalization.Calls-localizations==count,"names resolved once per cached accessory");
 C(p.states[b].TacticalDevices.Trace!.Accessories.Select(x=>x.Name).SequenceEqual(Enumerable.Range(0,count).Select(i=>i%2==0?"SureFire X400":"AN/PEQ-15")),"localized accessory names in production trace");
 C(p.states[b].TacticalDevices.Trace!.Accessories.Select(x=>x.Id).SequenceEqual(h.Item.Mods.Select(x=>x.Id)),"technical IDs preserved beside names");Save(p);
}
foreach(string key in new[]{"","unknown ShortName","throws"})
{
 var(p,b,l,h)=Setup(1);h.Item.Mods[0].ShortName=key;Tick(l);
 C(h.Item.Mods[0].Light!.IsActive,"missing localization cannot block command");
 C(p.states[b].TacticalDevices.Trace!.Accessories[0].Name=="","missing localization uses display fallback");Save(p);
}
{
 var(p,b,l,h)=Setup(3);h.Item.Mods[0].Light!.IsActive=true;Tick(l);C(h.Commands[0].Length==2,"only mismatched devices in batch");Save(p);
}
{
 var(p,b,l,h)=Setup();h.Ignore.Add(h.Item.Mods[1].Id);Tick(l);Tick(l,.4f);C(h.Commands.Count==1,"wait for pending confirmation");Tick(l,.41f);
 C(h.Commands.Count==2&&h.Commands[1].Length==1,"only failed device retried");Tick(l,.81f);C(h.Commands.Count==2,"pause after second real failure");
 C(p.states[b].TacticalDevices.Trace!.Accessories[1].Rounds==1,"per-device first backoff");
 Tick(l,4.9f);C(h.Commands.Count==2,"initial five seconds pause");Tick(l,.11f);C(h.Commands.Count==3,"retry after initial pause");
 Tick(l,.81f);Tick(l,.81f);C(h.Commands.Count==4&&p.states[b].TacticalDevices.Trace!.Accessories[1].RetryIn>9.9,"second ten second pause");
 h.Ignore.Clear();Tick(l,10.01f);C(h.Item.Mods.All(x=>x.Light!.IsActive),"recovery succeeds");C(p.states[b].TacticalDevices.Trace!.Accessories[1].Rounds==0,"success clears failure clocks");Save(p);
}
foreach(string kind in new[]{"refused","throw","delay"})
{
 var(p,b,l,h)=Setup();h.Refuse=kind=="refused";h.Throw=kind=="throw";h.Delayed=kind=="delay";Tick(l);
 C(!p.states[b].TacticalDevices.Trace!.Observed,"request not mistaken for outcome "+kind);
 if(kind=="delay")foreach(var d in h.Item.Mods)d.Light!.IsActive=true;
 Tick(l,.4f);C(h.Commands.Count==1,"no premature retry "+kind);
 if(kind=="delay")C(p.states[b].TacticalDevices.Trace!.Observed,"late confirmation accepted");
 else{Tick(l,.41f);Tick(l,.81f);C(h.Commands.Count==2,"failure bounded "+kind);}Save(p);
}
foreach(string kind in new[]{"reload","medicine","inventory","processing","remove","spawn","interact","door","not-ready"})
{
 var(p,b,l,h)=Setup();h.Refuse=true;Tick(l);
 switch(kind){case"reload":h.Reload=true;break;case"medicine":b.BotOwner.Medecine.Using=true;break;case"inventory":h.Inventory=true;break;case"processing":h.Processing=true;break;case"remove":h.Remove=true;break;case"spawn":h.Spawn=true;break;case"interact":h.Interact=true;break;case"door":b.DoorOpener.Interacting=true;break;case"not-ready":h.Ready=false;break;}
 for(int i=0;i<30;i++)Tick(l,.1f);C(h.Commands.Count==1,"busy no command "+kind);C(p.states[b].TacticalDevices.Weapons[0].Entries.All(e=>e.Attempt.Failures==0),"busy does not consume failures "+kind);
 h.Reload=h.Inventory=h.Processing=h.Remove=h.Spawn=h.Interact=false;h.Ready=true;b.BotOwner.Medecine.Using=b.DoorOpener.Interacting=false;h.Refuse=false;Tick(l,.1f);
 C(h.Commands.Count==2&&p.states[b].TacticalDevices.Trace!.Observed,"ready resumes immediately "+kind);Save(p);
}
foreach(string kind in new[]{"active","mode"})
{
 var(p,b,l,h)=Setup();Tick(l);if(kind=="active")h.Item.Mods[0].Light!.IsActive=false;else h.Item.Mods[0].Light!.SelectedMode=9;
 for(int i=0;i<100;i++)Tick(l,.1f);C(h.Commands.Count==1,"external change yields "+kind);C(p.states[b].TacticalDevices.Trace!.State=="yielded","external owner logged");
 l.Want=false;Tick(l,.1f);C(h.Item.Mods.All(x=>!x.Light!.IsActive),"new desired state releases old ownership epoch");C(h.Item.Mods[0].Light!.SelectedMode==(kind=="mode"?9:2),"external mode retained");Save(p);
}
{
 var(p,b,l,h)=Setup(2,false);p.config!.LightDiscipline.Enabled=true;l.Want=true;Tick(l);Tick(l,1);
 C(h.Item.Mods.All(x=>!x.Light!.IsActive),"L01 wins before L02 ON");int commands=h.Commands.Count;for(int i=0;i<15;i++)Tick(l,.1f);C(h.Commands.Count==commands,"no L01 L02 command fight");
 h.Item.Mods[1].Light!.IsActive=true;Tick(l,.1f);C(h.Commands.Count==commands,"L01 external yield respected by L02");
 b.GoalEnemy!.IsVisible=true;Tick(l,.1f);Tick(l,.3f);C(h.Item.Mods.All(x=>x.Light!.IsActive),"SAIN normal multi-device ON resumes when ambush ends");Save(p);
}
{
 var(p,b,l,h)=Setup();Tick(l);h.Item.Mods.Add(new LightComponent{IsActive=false}.Item);b.Player.OnItemAddedOrRemoved(h.Item,new(),true);Tick(l,.1f);
 C(h.Commands.Count==2&&h.Commands[1].Length==1,"equipment notification invalidates cache");
 h.Item.Mods.Add(new LightComponent{IsActive=false}.Item);Tick(l,.1f);C(h.Commands.Count==2,"cache avoids continual mod scans");Tick(l,5.1f);C(h.Commands.Count==3,"five second safety refresh catches missed notification");
 var removed=h.Item.Mods[0];h.Item.Mods.RemoveAt(0);b.Player.OnItemAddedOrRemoved(removed,new(),false);Tick(l,.1f);C(!p.states[b].TacticalDevices.Weapons[0].Entries.Any(x=>ReferenceEquals(x.Component,removed.Light)),"removed reference discarded");Save(p);
}
{
 var(p,b,l,h)=Setup();Tick(l);var old=h.Item;for(int i=0;i<7;i++){h.Item=new();h.Item.Mods.Add(new LightComponent{IsActive=false}.Item);b.BotOwner.WeaponManager.CurrentWeapon=h.Item;Tick(l,.1f);}
 C(p.states[b].TacticalDevices.Weapons.Count==4,"weapon cache bounded");C(!p.states[b].TacticalDevices.Weapons.Any(w=>ReferenceEquals(w.Weapon,old)),"old references evicted");
 p.End(b);C(p.states[b].TacticalDevices.Weapons.Count==0,"raid clears references");Save(p);
}
foreach(string excluded in new[]{"scav","human","remote","ghost","disabled","inactive","dead","world","ending"})
{
 var(p,b,l,h)=Setup();switch(excluded){case"scav":b.Pmc=false;break;case"human":b.Player.IsAI=false;break;case"remote":p.Authority=false;break;case"ghost":p.IsGhost=true;break;case"disabled":p.config!.TacticalDevices.Enabled=false;break;case"inactive":b.BotActive=false;break;case"dead":b.IsDead=true;break;case"world":p.world=null;break;case"ending":b.GameEnding=true;break;}
 Tick(l);C(l.NativeCalls==1,"excluded retains native "+excluded);C(h.Commands.Count==1&&h.Commands[0].Length==1,"excluded no multi-device write "+excluded);Save(p);
}
foreach(string gate in new[]{"settings","no-enemy","dark-off","no-owner"})
{
 var(p,b,l,h)=Setup();switch(gate){case"settings":b.BotOwner.BotLight.CanUseBySettings=false;break;case"no-enemy":b.BotOwner.Memory.GoalEnemy=null;break;case"dark-off":l.Want=false;h.Item.Mods.ForEach(x=>x.Light!.IsActive=true);b.BotOwner.BotLight._isInDarkPlace=true;break;case"no-owner":b.SAINLayersActive=false;b.GoalEnemy=null;break;}
 Tick(l);C(h.Commands.Count==(gate=="no-owner"?1:0),"native eligibility gate "+gate);Save(p);
}
foreach(bool refuse in new[]{false,true})
{
 var(p,b,l,h)=Setup(2,false);h.Refuse=refuse;int callbacks=0;h.Drop(1,()=>callbacks++,false);
 C(h.Destroyed&&callbacks==1,"holster never blocked");C(h.Commands.Count==1&&h.Commands[0].All(x=>!x.IsActive),"one native OFF before controller removed");
 C(p.states[b].TacticalDevices.Trace!.Observed==!refuse,"holster actual result tracked");C(p.states[b].TacticalDevices.Trace!.Source=="holster","holster attribution");Save(p);
}
{
 var(p,b,l,h)=Setup();Tick(l);b.Dispose();C(p.states[b].TacticalDevices.Weapons.Count==0,"dispose clears references");Save(p);
}
{
 var(p,b,l,h)=Setup();Tick(l);Time.time+=2;p.Watch(b);C(p.states[b].TacticalDevices.Weapons.Count==0,"lost callback cleanup");Save(p);
}
{
 var(p,b,l,h)=Setup(2,false);p.config!.LightDiscipline.Enabled=true;l.Want=true;Tick(l);Tick(l,1);
 C(h.Item.Mods.All(x=>!x.Light!.IsActive),"ambush hides normal already-on devices");
 b.GoalEnemy!.IsVisible=true;Tick(l,.1f);Tick(l,.3f);
 C(h.Item.Mods.All(x=>x.Light!.IsActive),"L01 OFF is not mistaken for an external override after release");Save(p);
}
foreach(string kind in new[]{"overflow","duplicate"})
{
 var(p,b,l,h)=Setup(kind=="overflow"?17:2);if(kind=="duplicate")h.Item.Mods[1].Id=h.Item.Mods[0].Id;Tick(l);C(h.Commands.Count==0,"unsupported layout sends no partial command "+kind);Save(p);
}
{
 var(p,b,l,h)=Setup();Tick(l);int callbacks=0;h.Drop(1,()=>callbacks++,false);C(h.Item.Mods.All(x=>!x.Light!.IsActive)&&callbacks==1,"holster OFF bypasses ordinary short interval");Save(p);
}
foreach(var json in events){var e=JObject.Parse(json);C(e["feature"]?.ToString() is "L01" or "L02","valid event feature");if(e["feature"]!.ToString()=="L02")C(e["tacticalDevices"]!=null,"typed trace present");}
Directory.CreateDirectory("artifacts");File.WriteAllLines("artifacts/l02-adapter-events.jsonl",events.Where(x=>JObject.Parse(x)["feature"]!.ToString()=="L02"));
Console.WriteLine($"{n} L02 production adapter/Harmony checks passed; {events.Count} events. Controlled native doubles, not a raid.");
