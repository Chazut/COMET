using Comet.Client;
using EFT;
using SAIN.Components.PlayerComponentSpace;
using Newtonsoft.Json.Linq;
using UnityEngine;

int checks=0;void Check(bool ok,string why){checks++;if(!ok)throw new Exception(why);}
var events=new List<string>();Comet.Api.Decisions.Published+=events.Add;
var contracts=Plugin.Contracts();Check(contracts.Patches.Count==4,"four native contracts");
foreach(var patch in contracts.Patches){Check(patch.prefix.priority==800,"native guards run first");var method=typeof(Plugin).GetMethod(patch.prefix.Name,System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)!;foreach(var arg in method.GetParameters().Where(x=>!x.Name!.StartsWith("__")))Check(patch.method.GetParameters().Any(x=>x.Name==arg.Name&&x.ParameterType==arg.ParameterType),"named Harmony argument matches native signature");}
var p=new Plugin();var pc=new PlayerComponent();int before=events.Count;
Check(p.Move(pc)&&events.Count==before,"healthy movement unmodified and silent");
pc.Player.HealthController!.Alive=false;Check(!p.Move(pc),"dead movement blocked before native call");
Check((string?)JObject.Parse(events.Last())["validity"]?["reason"]=="owner-not-alive","correct death evidence");
int first=events.Count;for(int i=0;i<1000;i++)Check(!p.Move(pc),"repeat invalid movement blocked");Check(events.Count==first,"blocked calls aggregate instead of logging per frame");
Time.unscaledTimeAsDouble=5;Check(!p.Move(pc),"still blocked");Check(events.Count==first+1&&(int?)JObject.Parse(events.Last())["validity"]?["blocked"]==1002,"five second summary with exact count");
pc.Player.HealthController.Alive=true;pc.Player.Downed=true;pc.Player.HealthController.Throw=true;Check(!p.Move(pc),"downed before health access");Check(events.Last().Contains("owner-downed"),"not generic health failure");
pc.Player.Downed=false;pc.Player.HealthController.Throw=false;Check(p.Move(pc),"valid Fika revive immediately restores native movement");
pc.Player.ThrowDowned=true;Check(!p.Move(pc),"unknown revive state safely rejected");pc.Player.ThrowDowned=false;Check(p.Move(pc),"recovered state not sticky");
pc.BotComponent!.IsDead=true;Check(!p.Move(pc),"native bot dead independent of player health");pc.BotComponent.IsDead=false;
pc.Player.MovementContext=null;Check(!p.Move(pc),"missing movement context");pc.Player.MovementContext=new();
pc.Player.CharacterController=null;Check(!p.Move(pc),"missing controller");pc.Player.CharacterController=new();
pc.Player.Transform!.Original!.Destroyed=true;Check(!p.Move(pc),"Unity destroyed transform");pc.Player.Transform.Original=new();
pc.Player.PlayerBones=null;Check(!p.Move(pc),"missing bones");pc.Player.PlayerBones=new();
pc.Destroyed=true;Check(!p.Move(pc),"destroyed component");pc.Destroyed=false;
pc.Player.Destroyed=true;Check(!p.Move(pc),"known destroyed player");pc.Player.Destroyed=false;
var oldPlayer=pc.Player;pc.Player=new();Check(!p.Move(pc),"reused component cannot inherit prior player identity");pc.Player=oldPlayer;
string id=pc.Player.ProfileId;pc.Player.ProfileId="replacement";Check(!p.Move(pc),"changed owner profile rejected");pc.Player.ProfileId=id;

var data=new OtherPlayersData();var original=new PlayerComponent();Check(p.Add(data,original),"healthy registration");
var replacement=new PlayerComponent();replacement.Player.ProfileId=original.ProfileId;Check(p.Add(data,replacement),"legitimate replacement");
Check(!p.Remove(data,original.ProfileId,original)&&ReferenceEquals(data.DataDictionary[original.ProfileId].OtherPlayerComponent,replacement),"late removal preserves replacement");
Check(!p.Add(data,original),"late re-add cannot resurrect retired component");
replacement.Destroyed=true;Check(p.Remove(data,replacement.ProfileId,replacement)&&data.DataDictionary.Count==0,"current destroyed source still removable");
var failed=new OtherPlayersData();var previous=new PlayerComponent();p.Add(failed,previous);var next=new PlayerComponent();next.Player.ProfileId=previous.ProfileId;p.Add(failed,next,false);Check(p.Add(failed,previous),"failed replacement does not retire original");
var source=new PlayerComponent();source.Player.PlayerBones=null;Check(!p.Add(data,source),"registration requires source bones");source.Player.PlayerBones=new();source.Player.Destroyed=true;Check(!p.Add(data,source),"destroyed source refused");source.Player.Destroyed=false;source.Player.Transform!.Original=null;Check(!p.Add(data,source),"missing source transform refused");
source.Player.Transform.Original=new();source.Player.IsAI=false;Check(p.Add(data,source),"human target registered by receiving PMC");source.Player.HealthController!.Alive=false;Check(p.Add(data,source),"dead intact source not falsely rejected");source.Player=new();Check(!p.Add(data,source),"source Player identity changed in same component");
Check(!p.Add(data,null!),"null registration safe");

var memory=new BotMemory{_owner=new(){GetPlayer=new()}};var shooter=new Player();Check(p.Fire(memory,shooter),"normal under-fire callback");
shooter.HealthController!.Alive=false;Check(p.Fire(memory,shooter),"dead but intact shooter remains valid for bullets/grenades");
shooter.Destroyed=true;Check(!p.Fire(memory,shooter),"destroyed source callback blocked");shooter.Destroyed=false;shooter.Transform!.Original=null;Check(!p.Fire(memory,shooter),"destroyed source transform blocked");
Check(p.Fire(memory,null)&&p.Fire(memory,new OtherPlayer()),"null and other IPlayer retain native semantics");
memory._owner.GetPlayer.Destroyed=true;Check(!p.Fire(memory,shooter),"destroyed receiving player rejected");
// Only notification was patched: neither damage values nor any damage method are accessed.
Check(contracts.Patches.All(x=>!x.method.Name.Contains("Damage")),"damage pipeline untouched");
foreach(var role in new[]{WildSpawnType.assault,WildSpawnType.bossKilla}){var non=new PlayerComponent();non.Player.Profile.Info.Settings.Role=role;non.Player.HealthController!.Alive=false;before=events.Count;Check(p.Move(non),"non-PMC movement left to SAIN");Check(p.Add(new OtherPlayersData{Player=non.Player},new PlayerComponent{Destroyed=true}),"non-PMC receiver registration unchanged");Check(p.Fire(new(){_owner=new(){GetPlayer=non.Player}},shooter)&&events.Count==before,"non-PMC under-fire unchanged");}
var human=new PlayerComponent();human.Player.IsAI=false;human.Player.HealthController!.Alive=false;Check(p.Move(human),"human movement untouched");
pc.Player.HealthController!.Alive=false;before=events.Count;p.Authority=false;Check(p.Move(pc)&&p.Fire(memory,shooter)&&p.Add(data,new(){Destroyed=true})&&p.Remove(data,"missing",original)&&events.Count==before,"Fika replica does not interfere");p.Authority=true;
p.config!.Enabled=false;Check(p.Move(pc),"global COMET disable restores native behavior");p.config.Enabled=true;
// Stable RR instance after C09 retires the state; refreshed after revive/new generation.
var identity=new PlayerComponent();identity.Player.Bot=identity.BotComponent;var state=new BotState();p.states[identity.BotComponent!]=state;identity.Player.HealthController!.Alive=false;p.Move(identity);Check((string?)JObject.Parse(events.Last())["botInstance"]==state.Id,"RR generation matches live COMET state");p.states.Clear();Time.unscaledTimeAsDouble=10;p.Move(identity);Check((string?)JObject.Parse(events.Last())["botInstance"]==state.Id,"retirement does not invent a new RR generation");
state=new();p.states[identity.BotComponent!]=state;Time.unscaledTimeAsDouble=15;p.Move(identity);Check((string?)JObject.Parse(events.Last())["botInstance"]==state.Id,"revive generation refreshed");
Comet.Api.Decisions.Published+=_=>throw new Exception("bad observer");Time.unscaledTimeAsDouble=20;Check(!p.Move(pc)&&p.Warnings>0,"observer failure cannot bypass guard");
p.Move(pc);before=events.Count;p.Reset();Check(events.Count>before&&events.Skip(before).All(x=>(bool?)JObject.Parse(x)["validity"]?["summary"]==true&&(double?)JObject.Parse(x)["validity"]?["validFor"]==0),"raid end flushes unpublished totals without live map badge");
pc.Player=new();Check(p.Move(pc),"new raid clears stale identity");
var huge=new PlayerComponent();huge.Player.ProfileId=new string('x',2048);huge.Player.Destroyed=true;p.Add(new(),huge);Check(((string?)JObject.Parse(events.Last())["validity"]?["source"])?.Length==512,"diagnostic source bounded");
var dir=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../artifacts/c10"));Directory.CreateDirectory(dir);File.WriteAllLines(Path.Combine(dir,"validity.jsonl"),events);File.WriteAllText(Path.Combine(dir,"validity.json"),"["+string.Join(",",events)+"]");
Console.WriteLine($"C10 production native validity guards: {checks} checks, {events.Count} events. Native/Unity calls doubled; no live raid.");
