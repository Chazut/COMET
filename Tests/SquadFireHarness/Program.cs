using Comet.Client;
using SAIN.Components;
using UnityEngine;
using Newtonsoft.Json.Linq;
int checks=0;var events=new List<string>();
void Check(bool b,string message){checks++;if(!b)throw new Exception(message);}
Plugin p=null!;BotComponent bot=null!,mate=null!;BotState State()=>p.states[bot];
void Setup(){if(p!=null)events.AddRange(p.diagnostics!.Events);Time.time=Time.realtimeSinceStartup=10;Time.frameCount++;Physics.Fail=false;Physics.Calls=0;Physics.Near.Clear();Physics.Hits.Clear();GameWorldComponent.Instance=new();
 p=new();bot=new();bot.Player.ProfileId=bot.ProfileId;mate=new(){ProfileId="mate",Position=new(0,0,5)};mate.Player.ProfileId=mate.ProfileId;mate.Transform.HeadData.HeadPosition=new(0,1.6f,5);bot.Squad.Members[bot.ProfileId]=bot;bot.Squad.Members[mate.ProfileId]=mate;p.Mates.Add(mate);}
void Advance(float dt=.05f){Time.time+=dt;Time.realtimeSinceStartup=Time.time;Time.frameCount++;}
void MoveMate(float x,float y=0,float z=5){mate.Position=new(x,y,z);mate.Transform.HeadData.HeadPosition=new(x,y+1.6f,z);}
void Body(BotComponent b,float at=5)=>Physics.Hits.Add(new(){collider=new(){Player=b.Player},distance=at});
Setup();Body(mate);Check(!p.Trigger(bot),"prevent new trigger");Check(bot.BotOwner.ShootData.Shots==0,"native Shoot not invoked by veto");Check(State().SquadFire.Policy.Trace is {Kind:"body",StartPrevented:true,Applied:true},"body and prevented start telemetry");
int calls=Physics.Calls;Check(!p.Trigger(bot)&&Physics.Calls>calls,"new trigger always fresh inside interval");Check(State().SquadFire.Hits.All(h=>h.collider==null)&&State().SquadFire.Near.All(c=>c==null)&&State().SquadFire.Surfaces.Held.All(o=>o==null),"buffers clear references");
Setup();bot.BotOwner.ShootData.Shooting=true;p.Update(bot);Check(!bot.BotOwner.ShootData.Shooting&&State().SquadFire.Policy.Trace is {Stopped:true,StopRequested:true},"interrupt actual burst");int stops=bot.BotOwner.ShootData.Stops;p.Update(bot);Check(stops==bot.BotOwner.ShootData.Stops,"no repeated EndShoot during interval");
Advance();MoveMate(4);p.Update(bot);Check(State().SquadFire.Policy.Blocking,"clear debounce starts");Advance();p.Update(bot);Check(State().SquadFire.Policy.Blocking,"continuous clear not yet enough");Advance(.06f);p.Update(bot);Check(!State().SquadFire.Policy.Blocking&&bot.BotOwner.ShootData.Shots==0&&!bot.BotOwner.ShootData.Shooting,"clear does not force firing");Check(p.Trigger(bot),"later native request may fire");
Setup();MoveMate(-1);mate.Player.Velocity=new(8,0,0);Check(!p.Trigger(bot)&&State().SquadFire.Policy.Trace!.Kind=="prediction","swept crossing with both endpoints clear");
Setup();MoveMate(0,3);Check(p.Trigger(bot),"other floor");Setup();MoveMate(0,0,-2);Check(p.Trigger(bot),"behind shooter");Setup();MoveMate(0,0,22);Check(p.Trigger(bot),"past target");
Setup();MoveMate(0);mate.Transform.HeadData.HeadPosition=new(0,.35f,5);bot.Transform.WeaponData.FirePort=new(0,.3f,0);Check(!p.Trigger(bot),"prone squadmate lower barrel");
Setup();MoveMate(0,0,1);Physics.Near.Add(new(){Player=mate.Player});Check(!p.Trigger(bot),"muzzle overlap");
foreach(string kind in new[]{"glass","door","solid"}){Setup();p.Surfaces.Add(new(){Distance=2,Lane="barrel",Kind=kind,Known=true,Level=100,Chance=0});bool fired=p.Trigger(bot);Check(fired==(kind=="solid"),"surface policy "+kind);}
Setup();p.Surfaces.Add(new(){Distance=5.1,Lane="barrel",Known=true,Level=100,Chance=0});Check(!p.Trigger(bot)&&p.GeometryDistance<5,"wall behind ally cannot authorize shot");
Setup();p.Surfaces.Add(new(){Distance=2,Lane="barrel",Known=true,Level=100,Chance=.5});Check(!p.Trigger(bot),"low penetration estimate not enough to risk ally");
Setup();p.GeometryComplete=false;Check(!p.Trigger(bot),"incomplete cover protects known ally");Setup();p.AmmoKnown=false;p.Surfaces.Add(new(){Distance=2,Lane="barrel",Known=true,Level=100,Chance=0});Check(!p.Trigger(bot),"unknown ammo conservative");
Setup();var unrelated=new BotComponent{ProfileId="unrelated",Position=new(0,0,5)};unrelated.Player.ProfileId=unrelated.ProfileId;Body(unrelated);MoveMate(4);Check(p.Trigger(bot),"unrelated nonhostile ignored by S08");
foreach(var block in new Action[]{()=>p.Authority=false,()=>p.config!.Enabled=false,()=>p.config!.SquadFire.Enabled=false,()=>bot.IsSupportedBot=false,()=>bot.IsDead=true,()=>bot.BotActive=false,()=>bot.IsGhost=true,()=>bot.GameEnding=true,()=>p.world=null,()=>bot.Squad.Members.Remove(mate.ProfileId)}){
 Setup();Check(!p.Trigger(bot),"before scope change");block();Check(p.Trigger(bot)&&!State().SquadFire.Policy.Blocking,"release on scope change");}
foreach(var alter in new Action[]{()=>mate.IsDead=true,()=>mate.Player.HealthController.IsAlive=false,()=>mate.IsGhost=true,()=>mate.BotActive=false}){Setup();Body(mate);alter();Check(p.Trigger(bot),"only living active own squad");}
Setup();Check(p.Trigger(bot,false)&&!p.states.ContainsKey(bot)&&Physics.Calls==0,"previous prefix owns request; no mutation");
Setup();p.doorBudget.Allow=false;Check(p.Trigger(bot),"no initial geometry yields native");Setup();Check(!p.Trigger(bot),"initial block");p.doorBudget.Allow=false;Advance(.1f);Check(!p.Trigger(bot),"temporary unavailable protects");Advance(.2f);Check(p.Trigger(bot),"unavailable lease bounded");
Setup();Check(!p.Trigger(bot),"before callback loss");Advance(.3f);p.Sample(bot);Check(!State().SquadFire.Policy.Blocking,"callback expires");
Setup();bot.BotOwner.ShootData.IgnoreStop=true;bot.BotOwner.ShootData.Shooting=true;p.Update(bot);Check(State().SquadFire.Policy.Trace is {StopRequested:true,Stopped:false,ValidFor:0},"unobserved release never map-confirmed");
Setup();Check(!p.Trigger(bot),"before error");Physics.Fail=true;Check(p.Trigger(bot)&&!State().SquadFire.Policy.Blocking,"runtime error yields native");Check(p.Trigger(bot)&&p.Logger.Warnings.Count==1,"fault logged once and does not repatch native");
Setup();MoveMate(4);for(int i=0;i<32;i++)Body(new(){ProfileId="outsider"});Check(p.Trigger(bot),"saturated unknown does not invent blocker");
Setup();bot.ManualShoot.Reason=SAIN.Models.Enums.EShootReason.Suppression;bot.ManualShoot.ShootPosition=new(0,1,3);Check(p.Trigger(bot),"manual target endpoint bounds lane");
Setup();bot.BotOwner.AimingManager.CurrentAiming.LastDist2Target=float.NaN;Check(p.Trigger(bot),"invalid native ray yields");
Setup();bot.BotOwner.AimingManager.CurrentAiming.LastDist2Target=2;Check(!p.ManualTrigger(bot,new(0,1,15)),"new manual target used before SAIN publishes ShootPosition");Check(State().SquadFire.ManualTarget==null,"manual target scope released");
Setup();try{p.ManualTrigger(bot,new(0,1,15),true);}catch(InvalidOperationException){}Check(State().SquadFire.ManualTarget==null,"manual target scope released on native exception without swallowing it");
Setup();p.config!.ShotGeometry.Enabled=false;Check(!p.Trigger(bot),"squad safety independent of W08 toggle");
Setup();Check(!p.Trigger(bot),"before disable teammate geometry");MoveMate(4);mate.Player.Velocity=new(float.NaN,0,0);Advance(.1f);Check(!p.Trigger(bot),"invalid ally geometry not reported as clear");Advance(.2f);Check(p.Trigger(bot),"invalid geometry eventually yields native");
// Production event stream for RR: sustained blockers and a native clear release.
Setup();Time.time=Time.realtimeSinceStartup=22;Body(mate);for(int i=0;i<25;i++){p.Trigger(bot);Advance();}Physics.Hits.Clear();MoveMate(4);for(int i=0;i<5;i++){p.Update(bot);Advance();}p.End(bot);events.AddRange(p.diagnostics!.Events);
foreach(var json in events){var row=JObject.Parse(json);Check((string?)row["feature"]=="S08","feature");var t=row["squadFire"]!;Check((double)t["validFor"]!<=.25,"bounded map lease");if((double)t["validFor"]!>0)Check((bool)t["applied"]!&&((bool)t["stopped"]!||(bool)t["startPrevented"]!),"only confirmed control");}
var output=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../artifacts/s08-adapter-events.jsonl"));Directory.CreateDirectory(Path.GetDirectoryName(output)!);File.WriteAllLines(output,events);
Console.WriteLine($"S08 native adapter: {checks} checks passed; {events.Count} production events. Engine boundaries are doubles; not an in-game raid. {output}");
