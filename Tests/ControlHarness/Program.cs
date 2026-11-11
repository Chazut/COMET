using Comet.Client;
using Comet.Core;
using Comet.Api;
using BepInEx.Bootstrap;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;
using QuestingBots.Controllers;

int checks=0;void Check(bool ok,string why){checks++;if(!ok)throw new Exception(why);}
void Tick(float t){Time.time=Time.realtimeSinceStartup=t;Time.unscaledTimeAsDouble=t;}
foreach(var (layer,owner) in new[]{("SAIN : Combat Layer","combat"),("SAIN : Squad Layer","combat"),("SAIN : Extract","extraction"),("SAIN : Unknown","sain"),("COMET : Post-combat","recovery"),("COMET : Crossfire","combat"),("BotObjectiveLayer","questing"),("BotFollowerLayer","questing"),("BotFollowerRegroupLayer","questing"),("Sleeping","sleep"),("OrbitBrainLayer","orbit"),("Looting","looting"),("","unknown"),("Patrol","other")})Check(ControlHandoff.Classify(layer,true,true,true)==owner,"actual layer "+layer);
Check(ControlHandoff.Classify("Looting",false,false,false)=="other","no inferred plugin without detection");
var model=new ControlHandoff();model.Observe(10,"sain","SAIN : Other");Check(model.Release(11,"Hold","finished")!.Pending,"release not takeover");Check(model.Release(12,"Hold","finished")==null,"duplicate release coalesced");
Check(model.Observe(40,"sain","SAIN : Other")==null,"no early warning");Check(model.Observe(41,"sain","SAIN : Other")!.State=="waiting","one observation of long wait");Check(model.Observe(50,"sain","SAIN : Other")==null,"no warning spam");
var resumed=model.Observe(51,"questing","BotObjectiveLayer")!;Check(!resumed.Pending&&resumed.Elapsed==40,"actual takeover measured separately");Check(model.Observe(53,"combat","SAIN : Combat Layer")!.RapidReturns==1,"quick combat return observed not blocked");
model.Release(54,"Hold","visible");Check(!model.Observe(55,"combat","SAIN : Combat Layer")!.Pending,"ongoing combat interrupts handoff wait");Check(model.Observe(90,"combat","SAIN : Combat Layer")==null,"no false stuck report in combat");
model.Observe(91,"looting","Looting");model.Release(92,"Hold","stop");Check(model.Observe(93,"looting","Looting")!.Reason=="control-takeover-observed","takeover already selected");
foreach(double invalid in new[]{double.NaN,double.PositiveInfinity,-1d})Check(model.Release(invalid,"x","x")==null,"invalid clock");
Check(model.Observe(1,"questing","BotObjectiveLayer")!.RapidReturns==0,"clock reset clears history");
foreach(string mode in new[]{"sain","orbit","qb"}) {
 Chainloader.PluginInfos.Clear();BotRegistrationManager.Asleep.Clear();Tick(100);var p=new Plugin();var harmony=new Harmony();
 if(mode=="orbit")p.isGhost=_=>false;
 if(mode=="qb"){Chainloader.PluginInfos.Add("com.danw.questingbots",new(){Instance=p});Chainloader.PluginInfos.Add("me.skwizzy.lootingbots",new(){Instance=p});}
 p.Connect(harmony);Check(harmony.Hooks==(mode=="qb"?1:0),"optional hook "+mode);
 if(mode=="qb")foreach(string name in new[]{"COMET : Crossfire","COMET : Post-combat","Other","SAIN : Combat Layer"}) {
  object[] callbackArgs={name,name.StartsWith("SAIN")};harmony.Callback!.Invoke(null,callbackArgs);Check((bool)callbackArgs[1]==(name.StartsWith("SAIN")||name.StartsWith("COMET")),"exact optional recognition "+name);
 }
 var s=p.Add();var b=s.Owner;var brain=b.BotOwner.Brain.BaseBrain;p.Sample();Check(brain.Listeners==1,"one subscription");Tick(101);p.Sample();Check(brain.Listeners==1,"idempotent subscription");
 var foreign=p.ForeignRoute;var hands=p.NativeHands;var quest=p.Quest;p.Freed(s,"post-combat-finished");
 Check(JObject.Parse(Decisions.Events.Last())["controlHandoff"]!["pending"]!.Value<bool>(),"released event pending");
 string layer=mode=="qb"?"BotObjectiveLayer":mode=="orbit"?"OrbitBrainLayer":"Patrol";Tick(102);brain.Select(layer);
 Check(p.Releases==1&&s.Handoff.Cleared==1&&p.RecoveryEnds==1,"handoff cleanup once "+mode);Check(ReferenceEquals(foreign,p.ForeignRoute)&&ReferenceEquals(hands,p.NativeHands)&&ReferenceEquals(quest,p.Quest),"foreign work unchanged");
 Tick(103);p.Sample();Check(p.Releases==1,"cleanup not repeated by observation");
 var last=JObject.Parse(Decisions.Events.Last());Check(last["controlHandoff"]!["owner"]!.ToString()==(mode=="qb"?"questing":mode=="orbit"?"orbit":"other"),"actual owner recorded");
 Tick(104);brain.Select("SAIN : Combat Layer");Check(p.Releases==1,"renewed combat keeps priority");
 if(mode=="qb"){
  BotRegistrationManager.Asleep.Add(b.ProfileId);Check(p.Sleep(b.ProfileId),"QB sleep read by profile ID");Tick(105);p.Sample();Check(JObject.Parse(Decisions.Events.Last())["controlHandoff"]!["owner"]!.ToString()=="sleep","sleep observed");
  s.Retired=true;BotRegistrationManager.Asleep.Clear();int count=Decisions.Events.Count;Tick(106);p.Sample();Check(Decisions.Events.Count==count,"retired instance cannot publish awake takeover");
  p.states[b]=new(b);Tick(107);p.Sample();Check(JObject.Parse(Decisions.Events.Last())["botInstance"]!.ToString()==p.states[b].Id,"wake uses fresh instance");
  brain.Select("Looting");Check(JObject.Parse(Decisions.Events.Last())["controlHandoff"]!["owner"]!.ToString()=="looting","LB actual layer recorded");
 }
 int events=Decisions.Events.Count;p.Authority=false;brain.Select("SAIN : Combat Layer");Check(Decisions.Events.Count==events,"Fika client has no authority");p.Authority=true;
 b.BotOwner.Brain.ThrowRead=true;for(int i=0;i<5;i++){Tick(110+i);p.Sample();}Check(p.Logger.Lines.Count(x=>x.Contains("layer sampling unavailable"))==1,"broken optional observation bounded");b.BotOwner.Brain.ThrowRead=false;
 p.Clear();Check(brain.Listeners==0,"raid end detaches callbacks");events=Decisions.Events.Count;brain.Select(layer);Check(Decisions.Events.Count==events,"old world callback cannot publish");
}
Chainloader.PluginInfos.Clear();var scope=new Plugin();scope.Connect(new());var scav=scope.Add();scav.Owner.Pmc=false;var human=scope.Add();human.Owner.Human=true;Tick(200);scope.Sample();Check(scav.Owner.BotOwner.Brain.BaseBrain.Listeners==0&&human.Owner.BotOwner.Brain.BaseBrain.Listeners==0,"non-PMC and human untouched");
Chainloader.PluginInfos.Add("com.danw.questingbots",new(){Instance=new object()});var incompatible=new Plugin();incompatible.Connect(new());Check(incompatible.Sleep("x"),"unknown QB contract suspends safely");
Chainloader.PluginInfos["com.danw.questingbots"].Instance=scope;var broken=new Plugin();broken.Connect(new());BotRegistrationManager.Throw=true;Check(broken.Sleep("x"),"throwing sleep read suspends");BotRegistrationManager.Throw=false;Check(broken.Sleep("x"),"failed sleep contract never silently acts");
var dir=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../artifacts/i02"));Directory.CreateDirectory(dir);File.WriteAllLines(Path.Combine(dir,"control.jsonl"),Decisions.Events);File.WriteAllText(Path.Combine(dir,"control.json"),Decisions.Events.First(x=>x.Contains("control-takeover-observed")));
Console.WriteLine($"I02 production control adapter + model: {checks} checks; {Decisions.Events.Count} events. Native layer/events doubled, no raid.");

// T04 observes the production adapter without changing I02 cleanup.
Chainloader.PluginInfos.Clear();Tick(300);var journal=new Plugin();journal.config!.ControlJournal.Enabled=true;journal.Connect(new());var js=journal.Add();
Decisions.Events.Clear();journal.Sample();Check(Decisions.Events.Any(e=>e.Contains("ControlJournal")),"T04 initial layer observation");
var jbrain=js.Owner.BotOwner.Brain.BaseBrain;Tick(301);jbrain.Select("OrbitBrainLayer");Check(journal.Releases==1,"T04 does not prevent I02 cleanup");
var before=Decisions.Events.Count;Tick(302);journal.Sample();Check(!Decisions.Events.Skip(before).Any(e=>e.Contains("comet-record-remains")),"cleaned intention not falsely reported");
js.ActiveLabel="COMET:Hold";Tick(303);journal.Sample();Tick(304);journal.Sample();Check(Decisions.Events.Any(e=>e.Contains("comet-record-remains")),"sustained residual record is visible");
Check(js.ActiveLabel=="COMET:Hold","T04 does not repair residual records");
Tick(305);js.Owner.BotOwner.Medecine.Using=true;journal.Sample();Check(Decisions.Events.Any(e=>e.Contains("medical-in-progress")),"observed medical context");
js.Owner.BotOwner.Medecine.Using=false;js.Owner.BotOwner.WeaponManager.Reload.Reloading=true;Tick(306);journal.Sample();Check(Decisions.Events.Any(e=>e.Contains("reload-in-progress")),"observed reload context");
Tick(306.1f);int burstStart=Decisions.Events.Count;
for(int i=0;i<32;i++)jbrain.Select(i%2==0?"SAIN : Combat Layer":"OrbitBrainLayer");
Check(Decisions.Events.Count-burstStart>16,"rapid control transitions retained beyond former 16-event cap");
int releasesAfterBurst=journal.Releases;
journal.Downed=true;Tick(307);journal.Sample();Check(Decisions.Events.Any(e=>e.Contains("downed")),"downed observation suspended");journal.Downed=false;
before=Decisions.Events.Count;journal.Authority=false;Tick(308);journal.Sample();Check(Decisions.Events.Count==before,"replica emits no T04");journal.Authority=true;
js.Owner.Info=null!;Tick(309);journal.Sample();Check(journal.Logger.Lines.Any(e=>e.StartsWith("T04 diagnostic unavailable")),"diagnostic fault isolated");Check(journal.Releases==releasesAfterBurst,"T04 fault does not change combat");
journal.Clear();before=Decisions.Events.Count;jbrain.Select("SAIN : Combat Layer");Check(Decisions.Events.Count==before,"no T04 callbacks after cleanup");
Console.WriteLine($"T04 and I02 production adapter: {checks} checks passed.");

var services=new Plugin();services.config!.ControlJournal.Enabled=true;var ss=services.Add();ss.ActiveLabel="";
ss.Owner.Decision.CurrentSelfDecision="Heal";services.Connect(new());Tick(400);services.Sample();
foreach(var (service,feature) in new[]{("FirstAid","H01"),("Surgery","H01"),("Stims","H01"),("Reload","W01")})
{
 services.TestOwnedService=service;Tick(Time.time+1);services.Sample();
 var ev=Decisions.Events.Select(JObject.Parse).Last(x=>x["controlJournal"] is JObject);
 Check((string?)ev["controlJournal"]?["feature"]==feature,"control journal attributes COMET service: "+service);
 services.TestOwnedService="";Tick(Time.time+.3f);services.Sample();
 ev=Decisions.Events.Select(JObject.Parse).Last(x=>x["controlJournal"] is JObject);
 Check((string?)ev["controlJournal"]?["feature"]=="","native service no longer inherits COMET ownership");
}
Check(services.Releases==0,"service attribution never forces a layer release");services.Clear();
Console.WriteLine($"T04/I02 including service continuity: {checks} checks passed.");

// Real adapter reads native posture even without P02 quiet caps or a COMET plan.
var locomotion=new Plugin();locomotion.config!.ControlJournal.Enabled=true;locomotion.Connect(new());
var ls=locomotion.Add();ls.ActiveLabel="";ls.Owner.Player.PoseLevel=.35f;ls.Owner.Player.Velocity=new(){x=.6f,y=4,z=0};
ls.Owner.CurrentAction=new SearchAction();Tick(500);Decisions.Events.Clear();locomotion.Sample();
var locomotionJson=Decisions.Events.Select(JObject.Parse).Last(e=>e["controlJournal"]?["motion"] is JObject);var measured=locomotionJson["controlJournal"]?["motion"];
Check(measured!=null&&measured["speed"]!.Value<double>()==.6&&measured["pose"]!.Value<double>()==.35,"native horizontal crouch movement recorded");
Check((string?)locomotionJson["controlJournal"]?["feature"]=="","native posture is not falsely owned by COMET");
Check(ls.Owner.Player.PoseLevel==.35f&&ls.Owner.Player.Velocity.y==4,"read-only observer leaves gameplay untouched");
int MotionCount()=>Decisions.Events.Select(JObject.Parse).Count(e=>e["controlJournal"]?["motion"] is JObject);
for(int i=1;i<4;i++){Tick(500+i*.25f);locomotion.Sample();}Check(MotionCount()==1,"no repeated per-frame motion record");
Tick(501);locomotion.Sample();Check(MotionCount()==2,"movement heartbeat uses existing diagnostic stream");
Tick(501.1f);ls.Owner.BotOwner.Brain.BaseBrain.Select("OrbitBrainLayer");Tick(501.2f);ls.Owner.BotOwner.Brain.BaseBrain.Select("SAIN : Combat Layer");
Check(MotionCount()==2,"rapid layer cycling cannot multiply motion observations");
Tick(502);ls.Owner.BotOwner.Brain.BaseBrain.Select("OrbitBrainLayer");locomotion.Sample();Check(MotionCount()==2,"no combat-motion sample during ORBIT ownership");
Tick(503);locomotion.isGhost=_=>true;locomotion.Sample();Check(MotionCount()==2,"sleep does not manufacture movement");
locomotion.isGhost=_=>false;locomotion.Authority=false;Tick(504);ls.Owner.BotOwner.Brain.BaseBrain.Select("SAIN : Combat Layer");locomotion.Sample();Check(MotionCount()==2,"Fika replica cannot publish movement");
locomotion.Authority=true;locomotion.config.ControlJournal.Enabled=false;Tick(505);locomotion.Sample();Check(MotionCount()==2,"diagnostic toggle disables movement sampling");
File.WriteAllText(Path.Combine(dir,"movement.json"),locomotionJson.ToString());locomotion.Clear();
Console.WriteLine($"T04/I02 including observed locomotion: {checks} checks passed.");
sealed class SearchAction { }
