using Comet.Client;
using EFT;
using EFT.InventoryLogic;
using SAIN.Components;
using Newtonsoft.Json.Linq;
using UnityEngine;

int n=0;void Check(bool v,string why){n++;if(!v)throw new Exception(why);}
var p=new Plugin();Plugin.Instance=p;
BotComponent Bot(string id)
{
 var b=new BotComponent();b.Player.ProfileId=id;b.BotOwner.GetPlayer=b.Player;
 b.BotOwner.WeaponManager.Selector=new(){_owner=b.BotOwner};SAIN.SAINEnableClass.Bots[id]=b;
 var rifle=new Weapon{Id=id+"-rifle"};var pistol=new Weapon{Id=id+"-pistol",ShortName="Pistol"};
 p.Guns[(b,EquipmentSlot.FirstPrimaryWeapon)]=rifle;p.Guns[(b,EquipmentSlot.Holster)]=pistol;b.Player.HandsController=new Player.FirearmController{Item=rifle};return b;
}
void At(float t){Time.time=t;Time.realtimeSinceStartup=t;}
JObject[] Events()=>Comet.Api.Decisions.Events.Select(JObject.Parse).ToArray();
string? WeaponState(JObject x)=>(string?)(x["weaponJournal"] as JObject)?["change"]?["state"];
string? CombatKind(JObject x)=>(string?)(x["combatJournal"] as JObject)?["kind"];
var bot=Bot("pmc");p.Authority=false;p.Track(bot);Check(p.states.Count==0,"Fika replica never enrolls");
p.Authority=true;bot.Pmc=false;p.Track(bot);Check(p.states.Count==0,"non-PMC ignored");
bot.Pmc=true;bot.Player.IsAI=false;p.Track(bot);Check(p.states.Count==0,"human player ignored");
bot.Player.IsAI=true;p.Sleep=true;p.Track(bot);Check(p.states.Count==0,"ghost ignored");p.Sleep=false;
bot.Player.Downed=true;p.Track(bot);Check(p.states.Count==0,"downed PMC ignored");bot.Player.Downed=false;
p.Track(bot);Check(p.states.Count==1,"awake host PMC enrolled even without W06");At(1);p.Sample(bot);
Check(Events().Count(x=>(string?)x["feature"]=="T01")==1,"native action context recorded");
p.states[bot].ActiveLabel="COMET:Hold";At(1.1f);p.Sample(bot);
At(1.5f);p.Sample(bot);Check((string?)Events().Last()["combatJournal"]?["context"]?["feature"]=="A01","current matching native action can be attributed to COMET");
bot.Decision.CurrentSelfDecision="Heal";At(1.8f);p.Sample(bot);
Check((string?)Events().Last()["combatJournal"]?["context"]?["feature"]=="","native medical override does not inherit stale COMET label");bot.Decision.CurrentSelfDecision="None";
bot.SAINLayersActive=false;At(2.1f);p.Sample(bot);Check((string?)Events().Last()["combatJournal"]?["context"]?["action"]=="OtherLayer","inactive SAIN action cannot be attributed to ORBIT or another controller");bot.SAINLayersActive=true;
p.Switch(bot,false);Check(Events().Any(x=>WeaponState(x)=="refused"),"native rejection captured");
p.Switch(bot,true);Check(!Events().Any(x=>WeaponState(x)=="ready"),"acceptance not called ready");
bot.Player.HandsController=new Player.FirearmController{Item=p.Guns[(bot,EquipmentSlot.Holster)]};At(2.5f);p.Sample(bot);
Check(Events().Any(x=>WeaponState(x)=="ready"),"actual observer confirms ready firearm");
var other=Bot("victim");p.Track(other);p.Hit(other.Player,bot.Player,35);
Check(Events().Count(x=>CombatKind(x)=="hit-dealt")==1&&Events().Count(x=>CombatKind(x)=="hit-taken")==1,"damage delta produces one event for each tracked participant");
int before=Events().Length;p.Hit(other.Player,bot.Player,0);Check(Events().Length==before,"no health loss is not a damaging hit");
other.Player.HealthController.IsAlive=false;other.Player.Downed=true;p.Die(other.Player,bot.Player);Check(Events().Length==before,"Fika downed not counted as death");
other.Player.Downed=false;p.Die(other.Player,bot.Player);p.Die(other.Player,bot.Player);
Check(Events().Count(x=>CombatKind(x)=="death")==1&&Events().Count(x=>CombatKind(x)=="kill")==1,"confirmed native death deduplicated");
p.Stop(bot);Check(Events().Any(x=>CombatKind(x)=="pause"),"lifecycle invalidates sampled context");
var faulty=Bot("faulty");p.Track(faulty);faulty.Player.InventoryController.Fail=true;p.Switch(faulty,true);
Check(p.states[faulty].Journal.Fault,"inventory exception disables diagnostic only");
Check(p.states.ContainsKey(faulty)&&p.config!.Enabled,"diagnostic failure keeps gameplay configuration and bot state");
var storm=Bot("storm");p.Track(storm);At(3);for(int i=0;i<100;i++)p.Switch(storm,false);
Check(p.states[storm].Journal.Omitted>0&&Events().Count(x=>(string?)x["profileId"]=="storm")<=12,"request storms rate limited");
p.Stop(storm);Check(p.diagnostics.Events.Any(x=>x.Contains("omitted")),"throttled observations reported");
At(4);p.Switch(storm,false,true);Check(Events().Any(x=>WeaponState(x)=="refused"&&(string?)(x["weaponJournal"] as JObject)?["change"]?["reason"]=="native-request-threw"),"native exception is observed without changing its propagation");
p.End();p.world=null;before=Events().Length;p.Die(bot.Player,null);p.Switch(bot,true);Check(Events().Length==before,"callbacks cannot publish into a closed raid");
Console.WriteLine($"T01/T03 production observer: {n} checks passed (platform doubles, generated boundary).");

// Same native self action can start or stop belonging to COMET without a layer change.
p.world=new();var serviceBot=Bot("service");p.Track(serviceBot);serviceBot.Decision.CurrentSelfDecision="Heal";
foreach(var (service,feature) in new[]{("FirstAid","H01"),("Surgery","H01"),("Stims","H01"),("Reload","W01")})
{
 At(Time.time+1);p.TestOwnedService=service;p.Sample(serviceBot);
 var context=Events().Last()["combatJournal"]?["context"];
 Check((string?)context?["feature"]==feature&&(string?)context?["choice"]==service,"owned service attributed independently of native layer: "+service);
 int count=Events().Length;At(Time.time+.3f);p.Sample(serviceBot);Check(Events().Length==count,"stable service context does not spam events");
 p.TestOwnedService="";At(Time.time+.3f);p.Sample(serviceBot);
 Check(Events().Length==count+1&&(string?)Events().Last()["combatJournal"]?["context"]?["feature"]=="","service ownership end recorded before five-second heartbeat");
}
var retiring=Bot("retiring");p.Track(retiring);At(30);p.Sample(retiring);
p.Guns[(retiring,EquipmentSlot.SecondPrimaryWeapon)]=new Weapon{Id="second"};
p.Switch(retiring,true);
Check(retiring.Player.InventoryController.Scans==1,"one reachable inventory scan for all weapons per snapshot");
Check(p.states[retiring].Journal.Magazines.Count==0&&p.states[retiring].Journal.Ammo.Count==0,"scratch buffers retain no native items");
retiring.BotActive=false;retiring.Info=null!;retiring.Decision=null!;retiring.Player.InventoryController.Fail=true;
At(32);p.Sample(retiring);p.Stop(retiring);
Check(!p.states[retiring].Journal.Fault,"death or Ghost suspension does not read disposed SAIN or inventory");
Check(Events().Any(x=>(string?)x["profileId"]=="retiring"&&(bool?)(x["weaponJournal"] as JObject)?["cachedContext"]==true),"suspended weapon request retains last value-only context");
Check(retiring.Player.InventoryController.Scans==1,"suspension never scans the retired inventory");
p.End();Console.WriteLine($"T01/T03 including service continuity and retirement: {n} checks passed.");
Plugin.TestBeltReload();
