using Comet.Client;
using Comet.Core;
using EFT;
using EFT.InventoryLogic;
using SAIN;
using SAIN.Components;
using SAIN.SAINComponent.Classes.WeaponFunction;
using UnityEngine;
using Newtonsoft.Json.Linq;

Plugin.Patch();int n=0;var events=new List<string>();void C(bool v,string why){n++;if(!v)throw new Exception("L03 adapter: "+why);}
(Plugin p,BotComponent b,BotLightController l,Player.FirearmController h) Setup(int count=2)
{
 Time.time=Time.realtimeSinceStartup=10;Orbit.Api.OrbitTelemetry.Value=new();var p=new Plugin();var b=new BotComponent{SAINLayersActive=false,GoalEnemy=null};b.BotOwner.Memory.GoalEnemy=null;b.Player.ProfileId=b.ProfileId;SAINEnableClass.Bots.Clear();SAINEnableClass.Bots[b.ProfileId]=b;
 var h=new Player.FirearmController{_player=b.Player};b.Player.HandsController=h;b.BotOwner.WeaponManager.CurrentWeapon=h.Item;
 for(int i=0;i<count;i++){var d=new LightComponent{IsActive=true,SelectedMode=i+2};d.Item.ShortName=i%2==0?"test-device":"test-laser";h.Item.Mods.Add(d.Item);}return(p,b,new(b),h);
}
void Tick(BotLightController l,float dt=0){Time.time+=dt;Time.realtimeSinceStartup=Time.time;l.Tick();}
void Save(Plugin p)=>events.AddRange(p.diagnostics!.Lines.Where(x=>JObject.Parse(x)["feature"]!.Value<string>()=="L03"));
foreach(int count in new[]{0,1,2,5,16})foreach(bool l02 in new[]{false,true})
{
 var(p,b,l,h)=Setup(count);p.config!.TacticalDevices.Enabled=l02;Tick(l);C(h.Commands.Count==0,"first sample waits");Tick(l,1.99f);C(h.Commands.Count==0,"not before two seconds");Tick(l,.26f);
 C(h.Commands.Count==(count==0?0:1),"single batch, independent of L02");C(h.Item.Mods.All(x=>!x.Light!.IsActive),"all devices OFF");C(h.Item.Mods.Select(x=>x.Light!.SelectedMode).SequenceEqual(Enumerable.Range(2,count)),"IR/modes retained");
 for(int i=0;i<100;i++)Tick(l,.3f);C(h.Commands.Count==(count==0?0:1),"no repeat during long rest");C(p.states[b].RestLights.Trace!.ObservedOff==(count>0),"observed outcome");Save(p);
}
foreach(string kind in new[]{"reactivated","mode","added","removed"})
{
 var(p,b,l,h)=Setup();Tick(l);Tick(l,2.01f);
 switch(kind){case"reactivated":h.Item.Mods[0].Light!.IsActive=true;break;case"mode":h.Item.Mods[0].Light!.SelectedMode=9;break;case"added":h.Item.Mods.Add(new LightComponent().Item);break;case"removed":h.Item.Mods.RemoveAt(0);break;}
 for(int i=0;i<100;i++)Tick(l,.3f);C(h.Commands.Count==1,"external owner retains control for whole pause "+kind);C(p.states[b].RestLights.Trace!.State=="yielded","external change logged "+kind);Save(p);
}
foreach(string kind in new[]{"refused","throw","delayed","ignored"})
{
 var(p,b,l,h)=Setup();h.Refuse=kind=="refused";h.Throw=kind=="throw";h.Delayed=kind=="delayed";if(kind=="ignored")h.Ignore.Add(h.Item.Mods[0].Id);Tick(l);Tick(l,2.01f);
 C(!p.states[b].RestLights.Trace!.ObservedOff,"not confused with accepted command "+kind);
 if(kind=="delayed")foreach(var d in h.Item.Mods)d.Light!.IsActive=false;
 Tick(l,.3f);if(kind=="delayed")C(p.states[b].RestLights.Trace!.ObservedOff,"delayed confirmation");
 for(int i=0;i<100;i++)Tick(l,.3f);C(h.Commands.Count==1,"failed OFF never retried "+kind);Save(p);
}
foreach(string excluded in new[]{"scav","human","remote","ghost","disabled","global","inactive","dead","world","ending","SAIN","enemy","known","under-fire","shoot","grenade","flash","sprint","moving","reload","medicine","inventory","door","wrong-weapon","hands","self","owner","looting","extract"})
{
 var(p,b,l,h)=Setup();Tick(l);
 switch(excluded){case"scav":b.Pmc=false;break;case"human":b.Player.IsAI=false;break;case"remote":p.Authority=false;break;case"ghost":p.IsGhost=true;break;case"disabled":p.config!.RestLights.Enabled=false;break;case"global":p.config!.Enabled=false;break;case"inactive":b.BotActive=false;break;case"dead":b.IsDead=true;break;case"world":p.world=null;break;case"ending":b.GameEnding=true;break;
 case"SAIN":b.SAINLayersActive=true;break;case"enemy":b.GoalEnemy=new();break;case"memory":b.BotOwner.Memory.GoalEnemy=new();break;case"known":b.EnemyController.KnownEnemies.Add(new());break;case"under-fire":b.BotOwner.Memory.IsUnderFire=true;break;case"shoot":b.ManualShoot.Shooting=true;break;case"grenade":b.Grenade.GrenadeDangerPoint=new();break;case"flash":b.Flashed.IsFlashed=true;break;case"sprint":b.Player.IsSprintEnabled=true;break;case"moving":b.Player.Velocity=new(.6f,0,0);break;
 case"reload":h.Reload=true;break;case"medicine":b.BotOwner.Medecine.Using=true;break;case"inventory":h.Inventory=true;break;case"door":b.DoorOpener.Interacting=true;break;case"wrong-weapon":b.BotOwner.WeaponManager.CurrentWeapon=new();break;case"hands":b.Player.HandsController=new();break;case"self":b.Decision.CurrentSelfDecision=SAIN.Preset.Shared.Enums.ESelfActionType.FirstAid;break;case"owner":b.BotOwner.Brain.Layer="other";break;case"looting":Orbit.Api.OrbitTelemetry.Value!.Status="Looting";break;case"extract":Orbit.Api.OrbitTelemetry.Value!.Category="Exfil";break;}
 Tick(l,3);C(!h.Commands.Any(a=>a.Any(x=>!x.IsActive)),"no OFF out of scope "+excluded);C(p.states[b].RestLights.Trace==null,"rest ownership released "+excluded);Save(p);
}
{
 var(p,b,l,h)=Setup();Tick(l);Tick(l,2.01f);b.Player.Velocity=new(1,0,0);Tick(l,.3f);h.Item.Mods.ForEach(x=>x.Light!.IsActive=true);b.Player.Velocity=new();Tick(l,.3f);Tick(l,2.1f);C(h.Commands.Count==1,"cooldown across two pauses");Tick(l,8);C(h.Commands.Count==2,"next distinct pause after cooldown");Save(p);
}
{
 var(p,b,l,h)=Setup();Tick(l);Tick(l,2.01f);b.SAINLayersActive=true;b.GoalEnemy=new(){IsVisible=true};b.BotOwner.Memory.GoalEnemy=new();Tick(l,.1f);C(h.Item.Mods.All(x=>x.Light!.IsActive),"combat SAIN and L02 resume ON");C(p.states[b].RestLights.Trace==null,"combat ownership immediate");Save(p);
}
// EFT's old GoalEnemy is not an active combat owner after SAIN has released the bot.
{
 var(p,b,l,h)=Setup();b.BotOwner.Memory.GoalEnemy=new();Tick(l);Tick(l,2.01f);
 C(h.Commands.Count==1,"stale EFT enemy memory does not block genuine rest");Save(p);
}
{
 var(p,b,l,h)=Setup();Tick(l);Tick(l,1.9f);b.Player.Velocity=new(.6f,0,0);Tick(l,.05f);
 C(p.states[b].RestLights.Trace==null,"brief movement resets stability inside evaluation throttle");
 b.Player.Velocity=new(.5f,0,0);Tick(l,.05f);Tick(l,1);C(h.Commands.Count==0,"new stable pause required after short movement");Tick(l,1.01f);C(h.Commands.Count==1,"speed boundary allowed after full new pause");Save(p);
}
{
 var(p,b,l,h)=Setup();Tick(l);Tick(l,2.01f);var old=h.Item;h.Item=new();h.Item.Mods.Add(new LightComponent().Item);b.BotOwner.WeaponManager.CurrentWeapon=h.Item;Tick(l,.1f);
 C(!ReferenceEquals(p.states[b].RestLights.Weapon,old)&&p.states[b].RestLights.Devices.Count==0,"weapon change releases old component references");
 Tick(l,10.1f);C(h.Commands.Count==2&&!h.Item.Mods[0].Light!.IsActive,"new weapon uses new rest episode after cooldown");Save(p);
}
foreach(bool orbit in new[]{true,false})
{
 var(p,b,l,h)=Setup();if(orbit)Orbit.Api.OrbitTelemetry.Value=null;else{Orbit.Api.OrbitTelemetry.Value=null;b.BotOwner.Brain.Layer="PatrolAssault";b.BotOwner.Brain.LastDecision="peaceLook";}
 Tick(l);Tick(l,2.01f);C(h.Commands.Count==(orbit?0:1),"optional ORBIT unavailable; native rest remains supported");Save(p);
}
foreach(string end in new[]{"watchdog","dispose","raid"})
{
 var(p,b,l,h)=Setup();Tick(l);Tick(l,2.01f);if(end=="watchdog"){Time.time+=1;p.Watch(b);}else if(end=="dispose")b.Dispose();else p.End(b);
 C(p.states[b].RestLights.Hand==null&&p.states[b].RestLights.Weapon==null&&p.states[b].RestLights.Devices.Count==0,"cleanup references "+end);C(h.Commands.Count==1,"cleanup never forces ON "+end);Save(p);
}
{
 var(p,b,l,h)=Setup(17);Tick(l);Tick(l,2.01f);C(h.Commands.Count==0,"unsupported device count bounded");Save(p);
}
var dir=Path.GetFullPath("artifacts/l03");Directory.CreateDirectory(dir);File.WriteAllLines(Path.Combine(dir,"rest-lights.jsonl"),events);
var fixture=JObject.Parse(events.First(x=>JObject.Parse(x)["restLights"]!["state"]!.Value<string>()=="dark"&&JObject.Parse(x)["restLights"]!["devices"]!.Value<int>()==2));File.WriteAllText(Path.Combine(dir,"l03-rest-lights.json"),fixture.ToString());
Console.WriteLine($"{n} L03 production adapter checks passed; {events.Count} production events exported");
